# Changelog

All notable changes to this project are recorded here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.0] - 2026-10-25

First tagged version: an engineering prototype that has been run against a real Microsoft 365 tenant. It is **not production-ready**; see Known limitations.

### Added

- **Core** (`src/TeamsCallRecords.Core`): conversion of a Microsoft Graph call record into one CDR per session, canonical JSON and SHA-256 hashes, version rules (apply, ignore, repeat, discrepancy). Pure functions, no network, no database.
- **SQLite data layer** (`src/TeamsCallRecords.Infrastructure`): schema migration, write gate, CDR mapping.
- **Processing pipeline**: inbox and outbox queues with leases and retries, call processor, reconciliation (every 15 minutes, 24 hours back by default), source state for suspended calls, error log.
- **Graph client** (`GraphClient`) for a live Microsoft 365 tenant: app-only token (client credentials), call record list and get with paging, subscription calls, activity notification call, retry and timeout policies. `Graph:Mode=Live` switches the Host to it.
- **Subscription management** (ADR-0001): `SubscriptionWorker` and `SubscriptionService` create, renew and recover the `communications/callRecords` subscription when `Graph:NotificationUrl` is set; one secret, `Graph:ClientState`, whose SHA-256 hash is stored in the database; lifecycle notifications (`reauthorizationRequired`, `subscriptionRemoved`, `missed`) are handled.
- **Host** (`src/TeamsCallRecords.Host`): Graph webhook (`/graph/notify`, `/graph/lifecycle`) with validation-token handshake and subscription, `clientState` and tenant checks; REST API protected by `X-TCRI-Key` (`GET /api/cdr`, `POST /api/reprocess`, `GET /api/status`); health endpoints; background workers.
- **Demo mode**: the Host runs without Microsoft 365 against an in-memory Graph stand-in (`demo/demo.sh`).
- **Documentation**: technical design (`docs/DESIGN.md`, TRP v1.5.1), ADR-0001 (Graph subscriptions), live test protocol (`docs/LIVE_TEST_PROTOCOL.md`), live setup guide (`docs/LIVE_SETUP.md`).
- **Tests**: 490 automated tests (`dotnet test TeamsCallRecordsIntegration.sln`: 490 total, 0 failed); none of them uses the network.

### Verified

Two live tests on 2026-10-25 against a Microsoft 365 Business Basic trial tenant, with real Teams calls between two users. Evidence and limits are in [docs/LIVE_TEST_PROTOCOL.md](docs/LIVE_TEST_PROTOCOL.md).

- **Test 1, reconciliation:** app-only authentication with the `CallRecords.Read.All` application permission; the call record list with a `startDateTime` filter is accepted by Graph; a real one-to-one call was read by the periodic reconciliation, converted into a CDR and returned by `GET /api/cdr`.
- **Test 2, push:** `SubscriptionWorker` created the subscription itself (lifetime 4230 minutes, the maximum for `callRecords`); Graph delivered a notification for a new real call to `/graph/notify` through a public tunnel (ngrok) and the Host answered `202 Accepted`; the call entered through an inbox item of kind `callRecord`, and the resulting CDR is returned by `GET /api/cdr`.
- Public surface checked through the tunnel before the call: validation handshake answered 200, `GET /api/status` without the key 401, a forged notification 403.

### Fixed

- `GET /api/status` always reported `"mode": "demo"`; it now reports the mode the Host runs in (`demo` or `live`).

### Known limitations

- The Teams bot is not implemented.
- Activity feed notifications to users: the sender is implemented, but in both live tests the requests (`POST /users/{id}/teamwork/sendActivityNotification`) returned HTTP 403 and the outbox rows ended in state `failed`. The cause was not investigated.
- Subscription renewal, `reauthorizationRequired`, `subscriptionRemoved` and `missed` notifications were not observed on a real tenant; they are covered by automated tests with a fake Graph client only.
- Microsoft Dev Tunnels, the tunnel named in ADR-0001, was not run; the live push test used ngrok. A changed notification address requires removing the old subscription first (described in `docs/LIVE_SETUP.md`, not exercised live).
- Tested on one trial tenant with one-to-one audio calls only. Group calls, video or screen sharing, PSTN, failed or missed calls, paging through `@odata.nextLink` with real data, load and reliability were not tested.
- In the push test, Microsoft Graph sent the notification about 12 minutes after the call ended. The earliest availability of the call record was not measured, and this delay is not guaranteed.
- Through a tunnel the whole Host port is public; `/api/*` is protected only by the API key, and the API key and `Webhook:ClientState` in `appsettings.json` are public demo values (they must be replaced for any real use); in live push mode the secret that matters is `Graph:ClientState`, which is never stored in the repository and is supplied through the environment (`Graph__ClientState`). `/graph/lifecycle` and the health routes were not probed from outside.
- Not production-ready.
