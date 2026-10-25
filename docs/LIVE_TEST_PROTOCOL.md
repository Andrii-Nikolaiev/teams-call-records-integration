# TCRI live test protocol: first real Teams call through Microsoft Graph

| | |
| --- | --- |
| Date | 2026-10-25 (times below are UTC; Kyiv was UTC+2) |
| Tester | Andrii Nikolaiev |
| Software | TCRI, repository commit `0d7e901` (`main`), run locally from source |
| Result | **Passed for the scope below.** A real one-to-one Teams call produced one CDR in `GET /api/cdr` |
| Second test | Sections 1–7 describe test 1 (reconciliation, no subscription). Test 2, the push path through a Graph subscription, is in section 8 |

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

Observed in the database after the test (SQLite file read in read-only mode):
- The record reached TCRI through the periodic reconciliation, not through a notification: the `inbox` row of the call has `kind=reconcile` (received 10:29:21Z, CDR created 10:29:22Z), and the `subscription` table is empty.
- `cdr_seq` is 2 because the database file was shared with earlier demo runs: `cdr_seq=1` is a synthetic demo CDR (tenant `11111111…`, created 2026-10-23). The API filters by tenant and does not return it. `sqlite_sequence` for `cdr` is 2 with two rows, so there are no gaps.

Not measured or not tested:
- **Delay between the end of a call and its availability in Graph.** The call ended at 10:17:47Z and the record was fetched by the reconciliation run at 10:29:21Z, so it was available less than 12 minutes after the end of the call. This is an upper bound; the lower bound is not known. The tester first queried the API at about 10:55Z.
- **Push notifications (in test 1):** no Graph subscription was created, no public HTTPS endpoint was used, `/graph/notify` was not exercised by Microsoft. The push path was tested separately in test 2 (section 8).
- Teams bot and activity feed notifications to users: sending was attempted, but successful delivery was not achieved or confirmed. Two activity notifications (one per user) were attempted at 10:29Z: `POST /v1.0/users/{id}/teamwork/sendActivityNotification` returned HTTP 403 for both, and both outbox items ended in state `failed` after one attempt. The cause was not investigated; the test setup had no permission or Teams app configured for activity notifications.
- Group calls, calls with video or screen sharing, PSTN calls, failed or missed calls.
- The callee identifier was not cross-checked against Entra.
- A single call on a single tenant: this is a functional check, not a load or reliability test.

## 7. Reproducing

1. Create a tenant with two Teams-licensed users and register a single-tenant Entra app with the application permission `CallRecords.Read.All` (admin consent) and a client secret.
2. From the repository root in PowerShell, run `demo/run-live.ps1 -TenantId <tenant id> -ClientId <application id>` and enter the client secret at the prompt.
3. Place a Teams call between the two users and end it.
4. After the call record has appeared in Graph (the delay is not specified here), query `GET http://localhost:60049/api/cdr?from=<UTC start>&to=<UTC end>` with the header `X-TCRI-Key`. Restarting the Host triggers an immediate reconciliation.

## 8. Test 2: push through a Graph subscription (ngrok)

| | |
| --- | --- |
| Date | 2026-10-25 (UTC; Kyiv was UTC+2), later the same day |
| Software | TCRI, repository commit `b73f968` (`main`), 488 automated tests passing, run locally from source |
| Result | **Passed for the scope below.** The Host created the subscription itself, Graph delivered a notification for a real call, the Host answered 202, stored it as an inbox item of kind `callRecord`, and the resulting CDR is returned by `GET /api/cdr` |

### 8.1 Environment

Same tenant, users and app registration as test 1, with these differences:

| Item | Value |
| --- | --- |
| Mode | `Graph:Mode=Live`, demo endpoints disabled, fresh data directory, random `Api:Key` and `Graph:ClientState` generated for every start and supplied through environment variables |
| Subscription | Created by `SubscriptionWorker` (ADR-0001), not by hand: resource `communications/callRecords`, change type `created` |
| Reachability | `https://<random>.ngrok-free.app` (ngrok free plan) forwarding to `https://localhost:44396`; `Graph:NotificationUrl` = that address + `/graph/notify` |
| Tunnel choice | ADR-0001 D1 names Microsoft Dev Tunnels. ngrok was used for this test because it was already installed. The decision in ADR-0001 is unchanged; the tunnel is a test-time tool |

No secret, `clientState` value, raw notification body or unredacted screenshot is stored in the repository.

### 8.2 Public surface check, before the call

| Request through the public address | Expected | Observed |
| --- | --- | --- |
| `POST /graph/notify?validationToken=abc123` | 200, token echoed | 200 `text/plain`, body `abc123` |
| `GET /api/status` without the API key | 401 | 401 `unauthorized` |
| `POST /graph/notify` with a forged notification (zero subscription id, wrong `clientState` and tenant) | 403 | 403 `clientState or tenant mismatch` |

The whole Host port was reachable through the tunnel during the test; `/api/*` was protected only by the API key.

### 8.3 Timeline (UTC)

| Time | Event |
| --- | --- |
| 14:55:16 | Host started; the first reconciliation ran |
| 14:55:17 | `SubscriptionWorker` created the subscription |
| 14:55:18 | Graph requested validation at `/graph/notify`; answered 200 |
| 15:05:15 – 15:07:12 | Real one-to-one Teams call, 1 min 57 s |
| 15:19:23.92 | Graph set `lastModifiedDateTime` of the call record (record available) |
| 15:19:24.16 | `POST /graph/notify` received through the tunnel, answered **202 Accepted** (141.5 ms as measured by ngrok) |
| 15:19:24.25 | Inbox row 2 created, `kind=callRecord`, call `2cc02d8c…` |
| 15:19:25.35 | Inbox row 2 `done` (1 attempt) |
| 15:40 | `GET /api/status`: inbox pending 0, failed 0 |

Graph `GET /subscriptions` returned one subscription with the tunnel notification URL. Its expiry, `2026-10-28T13:25:17Z`, equals creation plus 4230 minutes, the maximum lifetime for `callRecords`.

### 8.4 Evidence

- Notification body (read in the ngrok inspector): subscription id equal to the id returned by Graph, `resource` `communications/callRecords/2cc02d8c…`, `changeType` `created`, tenant id equal to the test tenant. The sender address differed from the tester's address.
- `GET /api/cdr?from=2026-10-25T00:00:00Z&to=2026-10-26T00:00:00Z` returned two CDRs: the call of test 1 (`cdr_seq` 1, because the data directory was new and the first reconciliation fetched it) and the new call:

```json
{
  "cdr_seq": 2,
  "tenant_id": "fa7fb546…",
  "call_id": "2cc02d8c…",
  "session_id": "2cc02d8c…",
  "source_version": 1,
  "call_type": "oneToOne",
  "caller": { "type": "user", "id": "24aaf2ef…" },
  "callee": { "type": "user", "id": "a3778ad8…" },
  "start_utc": "2026-10-25T15:05:15.0409599Z",
  "end_utc": "2026-10-25T15:07:12.5399806Z",
  "duration_seconds": 117,
  "modalities": ["audio"],
  "status": "completed",
  "segment_count": 1,
  "aggregation_status": "complete"
}
```

- The `inbox` table (SQLite opened read-only with `sqlite3 -readonly`; the `raw` column deliberately not read, because it may contain the `clientState`) holds exactly two rows:

| id | kind | call | state | attempts | received_utc | done_utc |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `reconcile` | `95ab92e1…` (the call of test 1) | `done` | 1 | 2026-10-25T14:55:18.487Z | 2026-10-25T14:55:19.900Z |
| 2 | `callRecord` | `2cc02d8c…` (the new call) | `done` | 1 | 2026-10-25T15:19:24.248Z | 2026-10-25T15:19:25.349Z |

  The new call entered through the notification (`kind=callRecord`, received 0.09 s after the request reached the tunnel) and was processed in 1.1 s on the first attempt. No `reconcile` row exists for it, so the CDR was not produced by a reconciliation run.
- `call_id` equals the call record id in the notification. Duration 117 s agrees with the difference of the timestamps (117.50 s, rounded). The caller is the test user and the callee the administrator, as in the Teams call log.

### 8.5 Observations

- Graph needed about 12 minutes after the end of the call to create the record (15:07:12 to 15:19:23). The notification reached the Host 0.24 s after that. The Host's own part was a fraction of a second.
- The record did not exist in Graph before 15:19:23, so no reconciliation run before that time could have produced it.
- The outbox `failed` counter grew by 2 for the new call: `POST /users/{id}/teamwork/sendActivityNotification` returns 403. This is the same open item as in test 1 and is unrelated to the subscription.

### 8.6 Not shown by this test

- Subscription renewal, `reauthorizationRequired`, `subscriptionRemoved` and `missed` lifecycle notifications were not observed live; they are covered by automated tests with a fake Graph client only.
- `/graph/lifecycle` and the health routes were not probed from outside.
- `/api/status` reported `"mode":"demo"` in live mode during this test (a hard-coded value, fixed in commit `fd57178`; the corrected value has not been observed in a live run yet).
- One call on one tenant, through a temporary tunnel: a functional check, not a reliability test.

### 8.7 Reproducing

1. Prepare the tenant, app registration and permission as in section 7.
2. Start the tunnel and note its HTTPS address.
3. Start the Host with `Graph:Mode=Live`, demo disabled, a new data directory, new random `Api:Key` and `Graph:ClientState`, and `Graph:NotificationUrl` set to the tunnel address + `/graph/notify`. Reusing an old data directory with a new `clientState` is not supported by this procedure.
4. Check the public surface as in 8.2, then place a Teams call between the two users and end it.
5. Wait for the notification (about 12 minutes in this test), then query `GET /api/cdr` and read the inbox row of the call with `sqlite3 -readonly` (columns other than `raw`): `kind=callRecord` shows that the notification, not a reconciliation run, brought the call in.
6. Close the tunnel. The subscription expires by itself.
