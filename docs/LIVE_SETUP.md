# Reproducing the live tests

This guide reproduces the two live tests of TCRI done on 2026-10-25 (results in [LIVE_TEST_PROTOCOL.md](LIVE_TEST_PROTOCOL.md)):

- **Part A (sections 2–10), test 1: reconciliation only.** One real one-to-one Teams call is read from Microsoft Graph by the periodic reconciliation, converted into a CDR, stored in SQLite and returned by `GET /api/cdr`. No public address is needed.
- **Part B (sections 11–19), test 2: push through a Graph subscription.** The Host creates the subscription itself, Graph delivers a notification for a real call to `/graph/notify` through a public tunnel, and the resulting CDR is returned by `GET /api/cdr`. Do Part A's sections 2–3 (prerequisites and app registration) first; Part B needs the same tenant, users and application.

## 1. Read this first: what is and is not covered

- The Host manages Graph subscriptions only when `Graph:NotificationUrl` is set (ADR-0001 D4). Without it the Host only polls, and a call record reaches TCRI through the periodic reconciliation (every 15 minutes by default). Part A uses that mode; test 1 itself ran on commit `0d7e901`, before the subscription service existed.
- Part B was tested once, with ngrok (free plan) as the tunnel. Microsoft Dev Tunnels is the tunnel named in ADR-0001 D1; its commands were checked against Microsoft's documentation only, and no test was run with it.
- Subscription renewal, `reauthorizationRequired`, `subscriptionRemoved` and `missed` lifecycle notifications were not observed live; they are covered by automated tests with a fake Graph client only.
- Activity notifications to users are not working. In both tests they returned HTTP 403, the cause was not investigated, and the outbox rows ended in state `failed`. Successful delivery is not confirmed; whether it works in another configuration depends on permissions and the Teams setup.
- The Teams bot is not implemented.
- Each test was one call on one trial tenant. This guide is not production guidance.

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

`GET /api/status` (same header) shows inbox and outbox counters and the time of the last reconciliation. Its `mode` field shows the mode the Host runs in (`demo` or `live`). Before commit `fd57178` it always read `demo`, even in live mode.

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

# Part B: push through a Graph subscription

## 11. What changes compared with Part A

When `Graph:NotificationUrl` is set, the Host's `SubscriptionWorker` creates and renews the `communications/callRecords` subscription (ADR-0001). Graph then sends a notification to `Graph:NotificationUrl` for each new call record, and the reconciliation keeps running in parallel as a safety net.

Settings added to the PowerShell session (the Host reads them as environment variables; `__` stands for `:`):

| Setting | Meaning |
| --- | --- |
| `Graph__NotificationUrl` | Public HTTPS address of the Host ending with `/graph/notify`. Setting it switches subscription management on. The Host refuses to start in live mode if the address is not absolute HTTPS or does not end with `/graph/notify` |
| `Graph__ClientState` | The single secret that Graph returns in every notification, 8..128 characters. Generate a new random value for every run; keep it in the environment only. The database stores only its SHA-256 hash |
| `Storage__DataDirectory` | Use a **new** directory for each run (see section 18) |
| `Api__Key` | Your own random key of at least 16 characters; the default key is public |

Defaults that were not changed in the test: call record subscription lifetime 4230 minutes (the maximum Graph allows for `callRecords`), renewal check every 5 minutes (`Subscriptions:CallRecordsLifetimeMinutes`, `Subscriptions:RenewCheckMinutes`). `Webhook:ClientState` keeps its value from `appsettings.json`; the secret that matters in this mode is `Graph__ClientState` (ADR-0001 D2).

A helper that creates random values in Windows PowerShell:

```
function New-RandomHex { $b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); -join ($b | ForEach-Object { $_.ToString('x2') }) }
```

## 12. Tunnel

Graph must reach the Host from the internet over HTTPS, so a public address that forwards to the local Host is needed.

- **Tested (variant A in section 13):** ngrok (free plan, version 3.35) forwarding to the Host over HTTPS: `ngrok http https://localhost:44396 --host-header="localhost:44396"`, with the Host listening on `https://localhost:44396` and the .NET development certificate trusted. The free plan gives a new random address each time ngrok starts.
- **Not tested (variant B in section 13):** `demo/run-live.ps1` listens on `http://localhost:60049`, so `ngrok http 60049` should be the equivalent; this combination was not run. Microsoft Dev Tunnels (named in ADR-0001 D1) was not run either.
- **Start the tunnel first** and copy its address `https://<host>`: the Host needs it in `Graph__NotificationUrl` before it starts. The tunnel answers 502 until the Host is running; that is expected.
- **The tunnel exposes the whole Host port**, not only `/graph/*`. `/api/*` is protected only by `X-TCRI-Key`. Use a fresh random key, run the test for as short a time as possible and close the tunnel afterwards. Treat the tunnel address as semi-public.

## 13. Start the Host

Run everything below in one PowerShell session, from the repository root, after the tunnel is up. The API key is copied to the clipboard instead of being printed; paste it where the requests need it and do not leave it in a screenshot or a chat.

**Variant A: the tested configuration** (tunnel to `https://localhost:44396`). The test used a private start script outside the repository with the same variables and the same `dotnet run` line; the block below is that setup assembled from it and from `run-live.ps1`, and was not run as one block:

```
$env:Storage__DataDirectory = 'D:\tcri-live-push-data'
$env:Api__Key = New-RandomHex
$env:Graph__ClientState = New-RandomHex
$env:Graph__NotificationUrl = 'https://YOUR_TUNNEL_HOST/graph/notify'
$env:Graph__Mode = 'Live'
$env:Graph__TenantId = 'YOUR_TENANT_GUID'
$env:Graph__ClientId = 'YOUR_APPLICATION_GUID'
$env:Demo__Enabled = 'false'
$sec = Read-Host -Prompt 'Client secret' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
try { $env:Graph__ClientSecret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
Remove-Variable sec, bstr
Set-Clipboard $env:Api__Key
dotnet run --project src/TeamsCallRecords.Host --no-launch-profile --urls https://localhost:44396
```

**Variant B: not tested with a tunnel** (tunnel to `http://localhost:60049`, started with `ngrok http 60049`). It uses the script from the repository, which asks for the client secret, sets `Graph__Mode=Live` and `Demo__Enabled=false`, and does not clear the variables set before it:

```
$env:Storage__DataDirectory = 'D:\tcri-live-push-data'
$env:Api__Key = New-RandomHex
$env:Graph__ClientState = New-RandomHex
$env:Graph__NotificationUrl = 'https://YOUR_TUNNEL_HOST/graph/notify'
Set-Clipboard $env:Api__Key
.\demo\run-live.ps1 -TenantId 'YOUR_TENANT_GUID' -ClientId 'YOUR_APPLICATION_GUID'
```

The Host starts listening first and only then does `SubscriptionWorker` create the subscription (ADR-0001 D5), so the validation request from Graph finds the Host ready. In the test the subscription was created within about a second of the first reconciliation. The worker repeats its check every 5 minutes; the line `Subscriptions are in order` was seen in the Host log after a successful check.

## 14. Check the public surface before the call

Replace `YOUR_TUNNEL_HOST` and run in a second PowerShell window (`curl.exe`, not the `curl` alias):

```
$u = 'https://YOUR_TUNNEL_HOST'
curl.exe -i -X POST "$u/graph/notify?validationToken=abc123"
curl.exe -i "$u/api/status"
'{"value":[{"subscriptionId":"00000000-0000-0000-0000-000000000000","tenantId":"x","clientState":"x","resource":"communications/callRecords/1","changeType":"created"}]}' | Set-Content -Encoding ascii $env:TEMP\fake.json
curl.exe -i -X POST "$u/graph/notify" -H "Content-Type: application/json" --data-binary "@$env:TEMP\fake.json"
```

Expected (as observed in the test):

| Request | Result |
| --- | --- |
| Validation request | HTTP 200, `text/plain`, body `abc123` |
| `GET /api/status` without the key | HTTP 401 `unauthorized` |
| Forged notification | HTTP 403 `clientState or tenant mismatch` |

## 15. Check that Graph holds the subscription

This asks Graph itself, not TCRI. In the second window. The token request and the list request are the calls that were run in the test (there the secret was typed in plain text); the hidden prompt, the function wrapper and the cleanup are new and were not run as written:

```
function Get-GraphToken {
    param([string]$TenantId, [string]$ClientId)
    $sec = Read-Host 'Client secret' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try {
        $secret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -Body @{ client_id = $ClientId; client_secret = $secret; scope = 'https://graph.microsoft.com/.default'; grant_type = 'client_credentials' }).access_token
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        Remove-Variable secret, sec -ErrorAction SilentlyContinue
    }
}
$token = Get-GraphToken -TenantId 'YOUR_TENANT_GUID' -ClientId 'YOUR_APPLICATION_GUID'
(Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/subscriptions' -Headers @{ Authorization = "Bearer $token" }).value | Select-Object id,resource,notificationUrl,expirationDateTime,changeType | Format-List
```

`ZeroFreeBSTR` and `Remove-Variable` release the unmanaged copy and drop the references; they do not erase the .NET string from memory, so close the window when you are done. The token is valid for a limited time (typically about an hour).

Expected: one subscription with `resource` `communications/callRecords`, `changeType` `created`, your tunnel address in `notificationUrl`, and `expirationDateTime` 4230 minutes after creation (about 70.5 hours). The tunnel's request list should also show the validation request from Graph (`POST /graph/notify`, 200) a moment after the Host started.

## 16. Place the call and watch the notification

Place a call as in section 6 (about two minutes). Graph needs time to create the call record: in the test it was 12 minutes after the end of the call, and the notification reached the Host 0.24 seconds after that. The delay is Graph's and is not guaranteed. While waiting, the ngrok inspector (`http://127.0.0.1:4040`) lists the requests; the notification is a `POST /graph/notify` answered `202 Accepted`. The request body contains `clientState`, so do not publish screenshots of the raw body.

Then read the CDR as in section 7 (`GET /api/cdr` with your key) and compare `call_id` with the call record id in the notification.

## 17. Tell push from reconciliation

The `inbox` table records how each call entered. Open the database read-only as in section 8 and do not select the `raw` column, because it may hold the `clientState`:

```
sqlite3 -readonly -header -column "D:\tcri-live-push-data\tcri.db" "select id, kind, substr(call_id,1,8) as call, state, attempts, received_utc, done_utc from inbox order by id;"
```

- `kind = callRecord`, `state = done`: the call came in through the notification.
- `kind = reconcile`: the call came in through the reconciliation. The first reconciliation after start-up fetches calls of the previous 24 hours, so earlier calls appear this way.

In the test, the new call had one `callRecord` row received 0.09 s after the request reached the tunnel and done after 1.1 s, and no `reconcile` row.

## 18. Finish safely, manage the subscription and rotate secrets

1. Stop the Host (Ctrl+C), then stop the tunnel. Without a tunnel Graph cannot deliver; the subscription is left in place.
2. **The subscription is not removed by stopping the Host.** It expires by itself at the time shown in section 15 (at most 4230 minutes after creation). To remove it earlier, get a **fresh** token with `Get-GraphToken` from section 15 (the token from the check may have expired by now; if the function is not defined in this window, define it again) and send the standard Graph call (it was not run in the test):

```
$token = Get-GraphToken -TenantId 'YOUR_TENANT_GUID' -ClientId 'YOUR_APPLICATION_GUID'
Invoke-RestMethod -Method Delete -Uri 'https://graph.microsoft.com/v1.0/subscriptions/SUBSCRIPTION_ID' -Headers @{ Authorization = "Bearer $token" }
```

   Take `SUBSCRIPTION_ID` from the list in section 15. An empty response means success; repeat the list request to confirm the subscription is gone.
3. **Before a new run with a different tunnel address or a new `Graph__ClientState`, delete the old subscription first (or wait until it expires) and use a new data directory.** Graph keeps the `clientState` that was set when the subscription was created, and a renewal in TCRI does not change it. A changed address with an old data directory makes the Host log `subscription notification URL mismatch` to `error_log` and do nothing (ADR-0001 DV-1). A second subscription for the same resource and change type is rejected by Graph with HTTP 409 (ADR-0001). These cases were not exercised live.
4. **A `clientState` that appeared in a screenshot, a log, a chat or a notification body is exposed.** Do not reuse it: delete the subscription, then start with a new data directory and a new `Graph__ClientState`.
5. Remove the secrets from the PowerShell sessions:

```
Remove-Item Env:\Graph__ClientSecret, Env:\Graph__ClientState, Env:\Api__Key -ErrorAction SilentlyContinue
```

6. Delete the client secret in Entra when you are done. If you used a trial tenant, cancel the subscription before the trial ends.

## 19. Troubleshooting (Part B)

- The tunnel answers 502: the Host is not running, or the tunnel points to the wrong port or scheme.
- No subscription in section 15: read `error_log` (`SELECT id, ts, component, message FROM error_log ORDER BY id;`). Graph validates the address while creating the subscription, so the tunnel and the Host must both be up; the worker retries every 5 minutes.
- `Graph__NotificationUrl` is rejected at start-up: it must be absolute HTTPS and end with `/graph/notify`; `Graph__ClientState` must be 8..128 characters.
- A notification answered 403: the `clientState` or the tenant does not match the subscription. A forged request in section 14 is expected to be answered this way.
- Notification received but no CDR: check `inbox` (`state`, `attempts`, `last_error`) and `GET /api/status`.
- Failed outbox rows with HTTP 403: the open item from section 1; it does not affect the subscription or the CDR.
