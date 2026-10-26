# Teams Call Records Integration (TCRI)

Open-source integration that reads Microsoft Teams call records through Microsoft Graph (Entra ID, app-only), converts every call session into a CDR, stores the result in SQLite and exposes it through a REST API. Calls reach the system by two routes: Graph change notifications (push) and periodic reconciliation. A Teams bot answers two text commands (`help`, `calls today`); activity feed notifications to users are implemented but did not work on the tested tenant. See the status below. C# / .NET 8.

> **Status: engineering prototype (v0.1.0 plus the Teams bot on `main`, not yet released). Not production-ready.**
> The core, data layer, processing pipeline, Graph client, subscription management and HTTP interface are implemented. They run in demo mode with an in-memory Graph stand-in, or against a live Microsoft 365 tenant (`Graph:Mode=Live`). Both ingestion routes have been verified end to end with real Teams calls on a trial tenant: periodic reconciliation (test 1) and Graph push notifications through a subscription created by the Host itself (test 2). The Teams bot (`help`, `calls today`, added after v0.1.0) was then verified in the same tenant with a real call and real Teams messages (test 3). See the [live test protocol](docs/LIVE_TEST_PROTOCOL.md). Activity feed delivery failed in every test; see the limitations below.

## What works now

### Implemented

The solution has 507 automated tests (`dotnet test`: 507 total, 0 failed); none of them uses the network. GitHub Actions builds and tests the full solution on every push and pull request.

| Part | State |
| --- | --- |
| **F6 core** (`src/TeamsCallRecords.Core`): call record → CDR conversion, canonical JSON and SHA-256 hashes, version rules | Implemented. Pure functions, no network, no database |
| **SQLite data layer** (`src/TeamsCallRecords.Infrastructure`): schema migration, DbContext, write gate, PRAGMA settings, CDR mapping | Implemented |
| Infrastructure queues (inbox/outbox), reconciliation, processing, activity notification sender | Implemented |
| **Subscription management** ([ADR-0001](docs/ADR-0001-subscription-service.md)): `SubscriptionWorker` / `SubscriptionService` create, renew and recover the `communications/callRecords` subscription; lifecycle notifications are handled | Implemented. Active when `Graph:NotificationUrl` is set. Creation verified live; renewal and lifecycle handling are covered by automated tests only |
| Host (`src/TeamsCallRecords.Host`): Graph webhook (`/graph/notify`, `/graph/lifecycle`), REST API (`/api/cdr`, `/api/reprocess`, `/api/status`), health, background workers | Implemented. Runs against an in-memory stand-in for Microsoft Graph (default) or live Microsoft Graph (`Graph:Mode=Live`) |
| Graph client for a live Microsoft 365 tenant (`GraphClient`) | Implemented: token, call record list and get with paging, subscription calls, activity notification call, retry and timeout policies |
| **Teams bot** (`src/TeamsCallRecords.Host/Bot`, Teams SDK 2.1.1): `POST /api/messages`, commands `help` and `calls today` | Implemented after v0.1.0, not yet in a release. Verified live in a personal chat (test 3); see [Teams bot](#teams-bot) |

### Verified on a real tenant

Three tests against a Microsoft 365 Business Basic trial tenant, with real Teams calls between two users (tests 1 and 2 on 2026-10-25, test 3 on 2026-10-26). Evidence and limits are in the [live test protocol](docs/LIVE_TEST_PROTOCOL.md).

- **Test 1, reconciliation.** App-only authentication with the `CallRecords.Read.All` application permission; the call record list with a `startDateTime` filter is accepted by Graph; one real one-to-one call was read by the periodic reconciliation, converted into one CDR, stored in SQLite and returned by `GET /api/cdr`.
- **Test 2, push.** The Host created the subscription itself; Graph delivered a notification for a new real call to `/graph/notify` through a public tunnel (ngrok) and the Host answered `202 Accepted`; the call entered through an inbox item of kind `callRecord` (not `reconcile`), and the resulting CDR is returned by `GET /api/cdr`.
- **Public surface.** Checked through the tunnel before the call: validation handshake 200, `GET /api/status` without the key 401, a forged notification 403.
- **Test 3, Teams bot.** Real Teams messages reached `/api/messages` through ngrok, were accepted by the SDK authentication, and the bot replied through Bot Framework. `help`, `calls today` (no calls, and one call), a differently capitalised command and an unknown command were answered as specified. After a real one-to-one call of 2 min 59 s, delivered by a Graph push notification, the bot answered `Today (Europe/Kyiv): 1 call, total session duration 00:02:59.`, equal to the Teams call log. The database also held two calls of the previous day, and the bot counted only the call of the current day.

### Known limitations

- **Teams bot:** tested in one personal chat with one user (the tenant administrator) on one trial tenant, through ngrok. Group chats, channels, other users and RSC were not tested. Automated tests cover three unauthenticated requests to `/api/messages` (all answered 401); signed-token cases (bad signature, wrong audience, expired, valid) are not covered by automated tests. Answers are in English only. The bot reads the local database, so `calls today` shows a call only after it has been ingested (about 12 to 21 minutes after the end of a call in the live tests).
- **Activity feed notifications to users:** the sender is implemented, but in all three live tests the requests returned HTTP 403 and the outbox rows ended in state `failed`. The cause was not investigated.
- **Not observed on a real tenant:** subscription renewal, `reauthorizationRequired`, `subscriptionRemoved` and `missed` notifications (automated tests with a fake Graph client only).
- **Tunnel:** Microsoft Dev Tunnels, named in ADR-0001, was not run; the push test used ngrok. A changed notification address requires removing the old subscription first (see [live setup](docs/LIVE_SETUP.md)).
- **Scope of testing:** one trial tenant, one-to-one audio calls only. Group calls, video or screen sharing, PSTN, failed or missed calls, paging through `@odata.nextLink` with real data, load and reliability were not tested.
- **Latency:** In the push tests, Microsoft Graph sent the notification about 12 minutes (test 2) and about 21 minutes (test 3) after the call ended. The earliest availability of the call record was not measured, and this delay is not guaranteed.
- **Exposure:** through a tunnel the whole Host port is public, and `/api/*` is protected only by the `X-TCRI-Key` header. `/graph/lifecycle` and the health routes were not probed from outside.

## Documentation

| Document | Content |
| --- | --- |
| [docs/DESIGN.md](docs/DESIGN.md) | Technical design |
| [docs/ADR-0001-subscription-service.md](docs/ADR-0001-subscription-service.md) | Decision record: Graph subscription management |
| [docs/LIVE_TEST_PROTOCOL.md](docs/LIVE_TEST_PROTOCOL.md) | The three live tests: environment, evidence, limits |
| [docs/LIVE_SETUP.md](docs/LIVE_SETUP.md) | How to reproduce the live runs (polling-only, push and the Teams bot) |
| [CHANGELOG.md](CHANGELOG.md) | Release notes |

## F6 core

Pure functions with no network and no database.

- One call record gives one CDR per session. Segments are counted, not split out, and may overlap.
- Duration is the difference between session end and start in whole seconds, rounded to nearest, a half second rounds up. Timestamps keep source precision, UTC.
- `failed` only when the source carries `failureInfo`; otherwise `completed`.
- Incomplete source data (no sessions, missing session dates) produces no placeholder CDR; the result is flagged as not fit for replacing stored data.
- Invalid sessions go to a rejection list with a code and never raise an exception.
- `source_hash` is the SHA-256 of the canonical JSON of the session; transport annotations (`@odata.context`, `@odata.nextLink`, `@odata.count`) are excluded, array order is kept.
- `VersionRules.Decide`: a higher version applies, a lower one is ignored, the same version with the same content is a repeat, the same version with different content is a discrepancy.

## Demo

The Host runs without Microsoft 365: Microsoft Graph is replaced by an in-memory stand-in, so one synthetic call can go through the whole pipeline.

```
dotnet run --project src/TeamsCallRecords.Host
```

In a second terminal (Git Bash; needs `bash`, `curl`, GNU `date`):

```
bash demo/demo.sh
```

The script puts a synthetic call record into the stand-in, sends a Graph-style change notification (first with a wrong `clientState`, which is rejected, then a correct one), and shows the resulting CDR (`GET /api/cdr`), the activity notification that was "sent" (`GET /demo/notifications`) and the queue status (`GET /api/status`, `"mode": "demo"`).

What the demo is not:

- No live Microsoft Graph and no Microsoft 365 tenant. Call records are synthetic, built from the public Graph resource structure.
- No Teams connection. The bot endpoint `/api/messages` is part of the Host but answers 401 without Azure AD settings, so the bot is not reachable. Activity feed notifications go to an in-memory list, not to real users.
- No Graph subscription management; a single fixed `Webhook:ClientState` is used.
- The API key and `Webhook:ClientState` in `appsettings.json` are demo values. Replace them before any real use.

## Live mode

`Graph:Mode=Live` runs the Host against a real Microsoft 365 tenant. It requires `Demo:Enabled=false` and the client secret in the `Graph__ClientSecret` environment variable. `GET /api/status` reports `"mode": "live"`.

- **Polling only:** reconciliation every 15 minutes (24 hours back by default). No public address is needed.
- **Push:** set `Graph:NotificationUrl` to an absolute HTTPS address ending in `/graph/notify`. The Host then creates and renews the subscription. The secret `Graph:ClientState` (8..128 characters) is supplied through the environment only (`Graph__ClientState`); the database stores only its SHA-256 hash.

Step-by-step instructions for both variants, including the tunnel, checks and safe shutdown, are in [docs/LIVE_SETUP.md](docs/LIVE_SETUP.md). Demo and live runs use the same SQLite file unless `Storage:DataDirectory` is changed, so use a separate data directory for a live run.

## Teams bot

The Host contains a Teams bot built on the Teams SDK (`Microsoft.Teams.Apps` 2.1.1). Teams sends messages to `POST /api/messages`; the SDK validates the Bot Framework token and answers 401 to a request without a valid one. The handler passes the text of the message to `BotCommands` and sends the answer back.

| Command | Answer |
| --- | --- |
| `help` | `Commands: help; calls today.` |
| `calls today` | `Today (Europe/Kyiv): 1 call, total session duration 00:02:59.` or `Today (Europe/Kyiv): no calls recorded.` |
| anything else | `Unknown command. Type help.` |

- Commands are trimmed and case-insensitive. Answers are in English.
- "Today" is the local day in the time zone `Bot:TimeZoneId` (default `Europe/Kyiv`); a day around a daylight saving change is 23 or 25 hours long. If the system does not know the zone id `Europe/Kyiv` (for example some Windows installations), `Europe/Kiev` and `FLE Standard Time` are tried; the label in the answer is always the configured id.
- The answer counts the CDRs (sessions) whose start falls in that day: `calls` is the number of distinct call ids among them, and the duration is the sum of the durations of those sessions. It is built from the local database, so a call appears only after it has been ingested.
- Settings (environment variables, `__` stands for `:`): `AzureAd__TenantId`, `AzureAd__ClientId` (the id of the bot), `AzureAd__ClientCredentials__0__SourceType=ClientSecret`, `AzureAd__ClientCredentials__0__ClientSecret`, optionally `Bot__TimeZoneId`. The secret must be supplied under `ClientCredentials:0:ClientSecret`; the key `AzureAd:ClientSecret` is not read.

Registering the bot and installing the Teams app are described in [docs/LIVE_SETUP.md](docs/LIVE_SETUP.md), Part C.

## Build and test

Requires the .NET 8 SDK.

```
dotnet test TeamsCallRecordsIntegration.sln
```

## Design principles

- Document first: requirements, engineering proposal and technical design precede code.
- Independent of any other product; no changes to third-party systems.
- Honest status: a result is reported only when it has actually been produced.

## Data policy

No secrets, tokens or full real identifiers in this repository. The live test protocol contains only truncated identifiers and the timing of test calls between two test users of the author's own tenant. Test data are synthetic, built from the public Microsoft Graph resource structure. Third-party documentation and vendor sample CDRs are not used.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
