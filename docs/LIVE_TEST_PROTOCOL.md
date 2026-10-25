# TCRI live test protocol: first real Teams call through Microsoft Graph

| | |
| --- | --- |
| Date | 2026-10-25 (times below are UTC; Kyiv was UTC+2) |
| Tester | Andrii Nikolaiev |
| Software | TCRI, repository commit `0d7e901` (`main`), run locally from source |
| Result | **Passed for the scope below.** A real one-to-one Teams call produced one CDR in `GET /api/cdr` |

## 1. Purpose

Show that TCRI works against a real Microsoft 365 tenant, not only against the in-memory demo stand-in: a call made in Microsoft Teams is read from Microsoft Graph, converted into a CDR, stored, and returned by the REST API.

## 2. Environment

| Item | Value |
| --- | --- |
| Tenant | New Microsoft 365 Business Basic trial tenant created for this test |
| Users | Two licensed users: the tenant administrator and a second test user (`TCRI Test`) |
| Entra app registration | `TCRI`, single tenant |
| Graph permission | `CallRecords.Read.All`, **application** permission, administrator consent granted |
| Authentication | Client credentials (client secret), `ClientSecretCredential`, scope `https://graph.microsoft.com/.default` |
| TCRI mode | `Graph:Mode=Live`, demo endpoints disabled, listening on `http://localhost:60049` |
| Reachability | Local machine only. No public HTTPS address, **no Graph subscription** |
| Teams clients | Two Chrome sessions on the Teams web client (one normal, one InPrivate), one per user |
| Automated tests at this commit | 356 passed, 0 failed (`dotnet test`), none of them uses the network |

The client secret is supplied at start-up through an environment variable and is not stored in the repository. Identifiers in this document are truncated on purpose (see Data policy in `README.md`).

## 3. Procedure

1. Start the Host in live mode (`demo/run-live.ps1`). At start-up the reconciler asks Graph for call records of the previous 24 hours.
2. Place a call in Teams from the administrator user to `TCRI Test`, answer it, talk about one minute, hang up.
3. Wait. The Host keeps running and polls Graph every 15 minutes (`Reconcile:IntervalMinutes`).
4. Query `GET /api/cdr` for the day with the API key.

## 4. Results

| # | Check | Expected | Observed | Result |
| --- | --- | --- | --- | --- |
| 1 | Token acquisition | Access token for Graph is obtained | Request to Graph was authorized | Pass |
| 2 | List request | `GET /communications/callRecords?$filter=startDateTime ge … and startDateTime lt …` is accepted | HTTP 200 at 09:44:18Z, headers after 517 ms; 0 call records at that time (no calls had been made) | Pass |
| 3 | Real call | Call is established and ends normally | Teams call log: outgoing, 1 min 17 s, started 10:16Z | Pass |
| 4 | CDR is available through the API | One CDR for the call | `GET /api/cdr` returned exactly one item (see section 5) | Pass |
| 5 | Duration | 77 s | `duration_seconds: 77`; `start_utc` 10:16:29.7840111Z, `end_utc` 10:17:47.0839918Z, difference 77.30 s, rounded to the nearest second | Pass |
| 6 | Caller identity | Object id of the calling user | `caller.id` equals the user's object id shown in Microsoft Entra | Pass |
| 7 | Call classification | One-to-one audio call | `call_type: oneToOne`, `modalities: [audio]`, `status: completed`, `segment_count: 1`, `aggregation_status: complete` | Pass |

## 5. Evidence: API response (identifiers truncated)

```json
{
  "items": [
    {
      "cdr_seq": 2,
      "tenant_id": "fa7fb546…",
      "call_id": "95ab92e1…",
      "session_id": "95ab92e1…",
      "source_version": 1,
      "call_type": "oneToOne",
      "caller": { "type": "user", "id": "a3778ad8…" },
      "callee": { "type": "user", "id": "24aaf2ef…" },
      "start_utc": "2026-10-25T10:16:29.7840111Z",
      "end_utc": "2026-10-25T10:17:47.0839918Z",
      "duration_seconds": 77,
      "modalities": ["audio"],
      "status": "completed",
      "termination_reason": null,
      "segment_count": 1,
      "aggregation_status": "complete",
      "source_hash": "fac6c09e…"
    }
  ],
  "next": null
}
```

## 6. What this does and does not show

Shown:
- The live Graph client (token, request, response parsing) works against a real tenant. Paging through `@odata.nextLink` was not observed with real data.
- `CallRecords.Read.All` as an application permission is sufficient to read the record of a call between two users of the tenant.
- A real call is converted into a CDR by the existing pipeline without changes to the Core.
- The `$filter` on `startDateTime` for the call record list, which the design marked as unverified (`[P-proposed]`), is accepted by Graph (HTTP 200).

Inference, not directly observed:
- Because no subscription existed, the record must have reached TCRI through the periodic reconciliation, not through a notification. The log line of that reconciliation run was not captured.

Not measured or not tested:
- **Delay between the end of a call and its availability in Graph.** The CDR was seen at about 10:55Z, roughly 37 minutes after the call ended; the actual delay is not known.
- **Push notifications:** no Graph subscription was created, no public HTTPS endpoint was used, `/graph/notify` was not exercised by Microsoft.
- Teams bot and activity feed notifications to users.
- Group calls, calls with video or screen sharing, PSTN calls, failed or missed calls.
- The callee identifier was not cross-checked against Entra.
- `cdr_seq` is 2 for the first and only CDR; the reason was not investigated.
- A single call on a single tenant: this is a functional check, not a load or reliability test.

## 7. Reproducing

1. Create a tenant with two Teams-licensed users and register a single-tenant Entra app with the application permission `CallRecords.Read.All` (admin consent) and a client secret.
2. From the repository root in PowerShell, run `demo/run-live.ps1 -TenantId <tenant id> -ClientId <application id>` and enter the client secret at the prompt.
3. Place a Teams call between the two users and end it.
4. After the call record has appeared in Graph (the delay is not specified here), query `GET http://localhost:60049/api/cdr?from=<UTC start>&to=<UTC end>` with the header `X-TCRI-Key`. Restarting the Host triggers an immediate reconciliation.
