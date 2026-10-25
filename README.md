# Teams Call Records Integration (TCRI)

Open-source integration that reads Microsoft Teams call records through Microsoft Graph (Entra ID, app-only), converts every call session into a CDR, stores the result in SQLite and exposes it through a REST API. Notifications to Teams users (activity feed, Teams bot) are part of the design; see the status below. C# / .NET 8.

> **Status: engineering prototype. Not production-ready.**
> The system architecture and detailed technical design are documented. Implementation and live verification are progressing incrementally. The core, data layer, processing pipeline and HTTP interface are implemented. They run in demo mode with an in-memory Graph stand-in, or against a live Microsoft 365 tenant (`Graph:Mode=Live`). One real Teams call has been verified end to end, picked up by periodic reconciliation; see the [live test protocol](docs/LIVE_TEST_PROTOCOL.md). Push notifications (Graph subscriptions), activity feed delivery and the Teams bot are not verified or not implemented, see below.

## What works now

### Implemented

The solution passed 356 automated tests on October 25, 2026; none of them uses the network. GitHub Actions builds and tests the full solution on every push and pull request.

| Part | State |
| --- | --- |
| **F6 core** (`src/TeamsCallRecords.Core`): call record → CDR conversion, canonical JSON and SHA-256 hashes, version rules | Implemented. Pure functions, no network, no database |
| **SQLite data layer** (`src/TeamsCallRecords.Infrastructure`): schema migration, DbContext, write gate, PRAGMA settings, CDR mapping | Implemented |
| Infrastructure queues (inbox/outbox), reconciliation, processing, activity notification sender | Implemented |
| Host (`src/TeamsCallRecords.Host`): Graph webhook endpoint, REST API (`/api/cdr`, `/api/reprocess`, `/api/status`), health, background workers | Implemented. Runs against an in-memory stand-in for Microsoft Graph (default) or live Microsoft Graph (`Graph:Mode=Live`) |
| Graph client for a live Microsoft 365 tenant (`GraphClient`) | Implemented: token, call record list and get with paging, subscription calls, activity notification call. Tested against an HTTP stub. The service that creates and renews subscriptions is not implemented |

### Verified on a real tenant

One test on 2026-10-25, see the [live test protocol](docs/LIVE_TEST_PROTOCOL.md) for evidence and limits:

- App-only authentication with the `CallRecords.Read.All` application permission.
- Call record list with a `startDateTime` filter: accepted by Graph (HTTP 200).
- One real one-to-one Teams call between two users of the tenant was read through Graph by the periodic reconciliation, converted into one CDR, stored in SQLite and returned by `GET /api/cdr`.

### Not verified or not implemented

- Graph subscriptions and push delivery to `/graph/notify`: the subscription service is not implemented, and the live test used no subscription and no public HTTPS address.
- Activity feed delivery: the sender is implemented, but in the live test both notifications failed with HTTP 403; the cause was not investigated.
- Teams bot: designed, not implemented.
- Group calls, video or screen sharing, PSTN, failed or missed calls; load and reliability; paging through `@odata.nextLink` with real data.

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

The script puts a synthetic call record into the stand-in, sends a Graph-style change notification (first with a wrong `clientState`, which is rejected, then a correct one), and shows the resulting CDR (`GET /api/cdr`), the activity notification that was "sent" (`GET /demo/notifications`) and the queue status (`GET /api/status`).

What the demo is not:

- No live Microsoft Graph and no Microsoft 365 tenant. Call records are synthetic, built from the public Graph resource structure.
- No Teams bot. Activity feed notifications go to an in-memory list, not to real users.
- No Graph subscription management (create/renew); a single fixed `clientState` is used.
- The API key and `clientState` in `appsettings.json` are demo values. Replace them before any real use.

## Live mode

`Graph:Mode=Live` runs the Host against a real Microsoft 365 tenant: it requires `Demo:Enabled=false` and the client secret in the `Graph__ClientSecret` environment variable. `demo/run-live.ps1` starts it and asks for the secret; the setup is described in section 7 of the [live test protocol](docs/LIVE_TEST_PROTOCOL.md).

Demo and live runs use the same SQLite file unless `Storage:DataDirectory` is changed, so synthetic and real records can end up in one database. Use a separate data directory for a live run.

## Build and test

Requires the .NET 8 SDK.

```
dotnet test
```

## Design principles

- Document first: requirements, engineering proposal and technical design precede code.
- Independent of any other product; no changes to third-party systems.
- Honest status: a result is reported only when it has actually been produced.

## Data policy

No secrets, tokens or full real identifiers in this repository. The live test protocol contains only truncated identifiers and the timing of one test call between two test users of the author's own tenant. Test data are synthetic, built from the public Microsoft Graph resource structure. Third-party documentation and vendor sample CDRs are not used.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).