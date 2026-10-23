# Teams Call Records Integration (TCRI)

Open-source integration that reads Microsoft Teams call records through Microsoft Graph (Entra ID, app-only), converts every call session into a CDR, stores the result in SQLite and reports it through REST, a Teams bot and the activity feed. C# / .NET 8.

> **Status: engineering prototype. Not production-ready.**
> The design is complete (requirements, engineering proposal, detailed technical design). Implementation has just started and is being published step by step.

## What works now

| Part | State |
| --- | --- |
| **F6 core** (`src/TeamsCallRecords.Core`): call record → CDR conversion, canonical JSON and SHA-256 hashes, version rules | Implemented. Pure functions, no network, no database |
| Unit tests for the core (`tests/TeamsCallRecords.Core.Tests`) | Written. The first run on the real xunit runner is the GitHub Actions job of this repository: see the **Actions** tab. Until it is green, no test result is claimed |
| Infrastructure (EF Core + SQLite, inbox/outbox, reconciliation, Graph client) | Designed, not implemented yet |
| Host (webhook, REST API, health) | Designed, not implemented yet |
| Teams bot, activity feed notifications | Designed, require a live Microsoft 365 tenant |
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

This project is licensed under the MIT License. See [LICENSE.txt](LICENSE.txt).
