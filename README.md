# Teams Call Records Integration (TCRI)

Open-source integration that reads Microsoft Teams call records through Microsoft Graph (Entra ID, app-only), converts every call session into a CDR, stores the result in SQLite and reports it through REST, a Teams bot and the activity feed. C# / .NET 8.

> **Status: engineering prototype. Not production-ready.**
> The design is complete (requirements, engineering proposal, detailed technical design). The core, data layer, processing pipeline and HTTP interface are implemented and run in a demo mode with an in-memory Graph stand-in; a live Microsoft 365 tenant is not used yet.

## What works now

| Part | State |
| --- | --- |
| **F6 core** (`src/TeamsCallRecords.Core`): call record → CDR conversion, canonical JSON and SHA-256 hashes, version rules | Implemented. Pure functions, no network, no database |
| Unit tests for the core (`tests/TeamsCallRecords.Core.Tests`) | 42 tests, run by the GitHub Actions job `ci` on every push to `main` |
| **SQLite data layer** (`src/TeamsCallRecords.Infrastructure`): schema migration, DbContext, write gate, PRAGMA settings, CDR mapping | Implemented, with tests in `tests/TeamsCallRecords.Infrastructure.Tests`. Live Microsoft 365 verification is not done |
| Infrastructure queues (inbox/outbox), reconciliation, processing | Implemented, tested (230 tests for the whole Infrastructure project) |
| Host (`src/TeamsCallRecords.Host`): Graph webhook, REST API (`/api/cdr`, `/api/reprocess`, `/api/status`), health, background workers | Implemented, tested end to end (30 tests in `tests/TeamsCallRecords.Host.Tests`). Runs against an in-memory stand-in for Microsoft Graph |
| Graph client for a live Microsoft 365 tenant | Not implemented. The interface exists; the Host uses the in-memory stand-in |
| Teams bot, activity feed notifications to real users | Designed, require a live Microsoft 365 tenant |
| Live Graph / Teams verification | Not done |

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

No secrets, tokens, real identifiers or real call data in this repository. Test data are synthetic, built from the public Microsoft Graph resource structure. Third-party documentation and vendor sample CDRs are not used.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).