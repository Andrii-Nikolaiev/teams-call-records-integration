# Reproducing the first live test

This guide reproduces the first live test of TCRI (2026-10-25, see [LIVE_TEST_PROTOCOL.md](LIVE_TEST_PROTOCOL.md)): one real one-to-one Teams call is read from Microsoft Graph by the periodic reconciliation, converted into a CDR, stored in SQLite and returned by `GET /api/cdr`.

## 1. Read this first: what is and is not covered

- TCRI has no subscription service. The Host does not create Graph subscriptions. In this guide a call record reaches TCRI only through the periodic reconciliation (every 15 minutes by default).
- The Graph webhook `/graph/notify` has not been exercised by Microsoft. It would need a public HTTPS address, which this guide does not cover.
- Activity notifications to users are not working. Both attempts in the first test returned HTTP 403, the cause was not investigated, and the outbox rows ended in state `failed`. Successful delivery is not confirmed; whether it works in another configuration depends on permissions and the Teams setup.
- The Teams bot is not implemented.
- The first test was one call on one trial tenant. This guide is not production guidance.

## 2. Prerequisites

- .NET 8 SDK and git. The start script is PowerShell.
- A Microsoft 365 tenant with Teams and two users licensed for Teams. The first test used a Microsoft 365 Business Basic trial.
- An administrator who can register applications and grant admin consent (Global Administrator in the first test).
- Two Teams sessions to place a call between the two users, for example two browser profiles on the Teams web client.
- A clone of this repository.

## 3. Register the application in Microsoft Entra ID

1. Sign in to the Microsoft Entra admin center (https://entra.microsoft.com) as the administrator.
2. Open **App registrations**, choose **New registration**, enter a name (the first test used `TCRI`), keep **single tenant**, leave the redirect URI empty, and register.
3. On the **Overview** page copy the **Application (client) ID** and the **Directory (tenant) ID**.
4. Open **API permissions**, choose **Add a permission**, then **Microsoft Graph**, then **Application permissions**, select `CallRecords.Read.All` and add it. Choose **Grant admin consent** and check that the status shows **Granted**. This is the only permission the tested path needs.
5. Open **Certificates & secrets**, choose **New client secret**, and copy the secret **Value** immediately; it is shown only once.

## 4. Configure

The Host reads `appsettings.json` and environment variables (`__` stands for `:`). `demo/run-live.ps1` sets `Graph__Mode=Live`, `Graph__TenantId`, `Graph__ClientId`, `Graph__ClientSecret` and `Demo__Enabled=false` for you. Live mode refuses to start while `Demo:Enabled` is true.

The client secret is typed at the script's prompt and exists only in that PowerShell session. Never put it in `appsettings.json`, in a script or in a commit. If a secret is ever exposed, delete it in Entra and create a new one.

Two optional settings are recommended. Set them in the same PowerShell session before starting the Host (run PowerShell from the repository root):

```
$env:Storage__DataDirectory = 'D:\tcri-live-data'
$env:Api__Key = 'YOUR_API_KEY_AT_LEAST_16_CHARACTERS'
```

- `Storage__DataDirectory`: by default demo and live runs share one SQLite file. In the first test it held a synthetic demo CDR (`cdr_seq=1`) next to the real one. A separate directory keeps live data apart; it is created if missing and nothing is deleted. Without this setting the relative default `./data` is resolved against the Host working directory (in the first test: `src\TeamsCallRecords.Host\data\tcri.db`).
- `Api__Key`: the default `demo-only-key-change-me` is public. If you do not set your own key, use the default value in the examples below.

## 5. Start the Host

From the repository root in PowerShell:

```
.\demo\run-live.ps1 -TenantId 'YOUR_TENANT_GUID' -ClientId 'YOUR_APPLICATION_GUID'
```

Replace both values with the Directory (tenant) ID and the Application (client) ID from section 3. If the execution policy blocks the script, run it as it was run in the first test:

```
powershell -ExecutionPolicy Bypass -File .\demo\run-live.ps1 -TenantId 'YOUR_TENANT_GUID' -ClientId 'YOUR_APPLICATION_GUID'
```

The Host listens on `http://localhost:60049`. `GET /health/ready` returns `ready` when start-up has finished. At start-up the reconciler requests the call records of the previous 24 hours. In the Host log you should see a `GET https://graph.microsoft.com/v1.0/communications/callRecords?$filter=...` request answered with HTTP 200 and the line `Reconciliation: N call(s) enqueued`. HTTP 401 or 403 here points to the token, the permission or the admin consent.

## 6. Place the control call

Sign in as the two users in two separate Teams sessions. Call the second user from the first, answer, talk for about a minute, hang up, and note the time. Keep the Host running.

## 7. Check the result through the API

Graph needs time to publish a call record, and the reconciliation runs every 15 minutes (`Reconcile:IntervalMinutes`). In the first test the record was fetched less than 12 minutes after the call ended; this is an upper bound, the real delay is not known. Restarting the Host runs a reconciliation immediately.

```
Invoke-RestMethod -Uri "http://localhost:60049/api/cdr?from=2026-10-25T00:00:00Z&to=2026-10-26T00:00:00Z" -Headers @{ "X-TCRI-Key" = "demo-only-key-change-me" } | ConvertTo-Json -Depth 6
```

The dates above belong to the first test; replace them with the date of your call in UTC. Replace the key with your own `Api__Key` if you set one. `from` and `to` must end with `Z` or carry an offset, and the range must not exceed 90 days. Only CDRs of the tenant in `Graph:TenantId` are returned.

Expected in the first test: one item with `call_type` `oneToOne`, `status` `completed`, `aggregation_status` `complete`, `segment_count` 1, `caller.id` equal to the calling user's object ID in Entra, and `duration_seconds` equal to the call duration rounded to the nearest second.

`GET /api/status` (same header) shows inbox and outbox counters and the time of the last reconciliation. Its `Mode` field always reads `demo` in the current code, even in live mode; do not rely on it.

## 8. Check the database

The file is `tcri.db` in the data directory (SQLite in WAL mode, with `tcri.db-wal` and `tcri.db-shm` beside it). Open it in place and read-only with any SQLite client, for example `sqlite3 -readonly "D:\tcri-live-data\tcri.db"` (use your own path). While the Host runs, do not copy `tcri.db` alone: recent data may still be in the `-wal` file.

```
SELECT id, kind, call_id, state, received_utc, done_utc FROM inbox ORDER BY id;
SELECT cdr_seq, tenant_id, call_id, session_id, source_version, start_utc, end_utc, duration_s, status, created_utc FROM cdr ORDER BY cdr_seq;
SELECT id, kind, recipient, state, attempts, last_error FROM outbox ORDER BY id;
SELECT id, ts, component, message FROM error_log ORDER BY id;
SELECT * FROM subscription;
```

Expected in the first test:

- `inbox`: a row with `kind` = `reconcile` and `state` = `done` for the call, which shows that the record arrived through the reconciliation.
- `subscription`: empty.
- `cdr`: one row for the call. `cdr_seq` depends on the history of the file (AUTOINCREMENT), so a shared demo file gives 2, not 1.
- `outbox` and `error_log`: two failed activity notifications, `last_error` `POST /v1.0/users/{id}/teamwork/sendActivityNotification returned 403`.

## 9. Troubleshooting

- HTTP 401 from `/api`: the `X-TCRI-Key` value does not match `Api:Key`.
- HTTP 400 `range_too_large`: the range is longer than 90 days.
- Empty `items`: wait for the next reconciliation or restart the Host; check the log for Graph HTTP 401 or 403; check that `Graph__TenantId` is the tenant where the call was made.
- Failed outbox rows with HTTP 403: observed in the first test, see section 1.

## 10. Clean up

Stop the Host with Ctrl+C. `run-live.ps1` does not remove the client secret from the PowerShell session afterwards, so remove it:

```
Remove-Item Env:\Graph__ClientSecret -ErrorAction SilentlyContinue
```

Delete the client secret in Entra when you are done, and delete the data directory if you no longer need it. If you used a trial tenant, cancel the subscription before the trial ends.
