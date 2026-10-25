# ADR-0001: Graph subscriptions (SubscriptionService)

Status: Accepted (revision 3), 2026-10-25. Amends TRP v1.5.1 sections 3.15.3 and 3.23.3 (DV-1, DV-2, DV-3).
Revision 2: DV-1 replaced (create-before-delete would fail with HTTP 409); lifecycle routes clarified; registry location follows the TRP; three tests added.
Revision 3: written down as implemented. DV-1 made precise (the URL check runs before any cleanup, renewal or creation, for rows of any status); DV-3 added (lifecycle notifications carry no resource, resource matching by prefix, lifecycle handled in the background); D4 gained two validation rules.

## Context

At the time of the first live test (see LIVE_TEST_PROTOCOL.md) call records were received only through the periodic reconciliation. The push path (Graph subscription, webhook, inbox, CDR) was designed in TRP 3.15 and 3.23 but not implemented: there was no SubscriptionService, subscription store, subscription worker or pending-subscription registry, and the webhook compared clientState with one fixed demo value (`Webhook:ClientState`).

## Decisions

D1. Tunnel for the live test: Microsoft Dev Tunnels, a persistent tunnel (`devtunnel create`) with time-limited anonymous access (1 hour to 30 days). Dev Tunnels is in preview and meant for development, not production. Nothing is opened in this stage.
D2. One secret: `Graph:ClientState` (environment variable). The database stores only its SHA-256 hash (`subscription.client_state_hash`). In live mode the webhook validates subscriptionId, tenant, resource and the clientState hash as in TRP 3.23.3, and `Webhook:ClientState` is not used. Demo mode keeps the fixed-value check, so `demo/demo.sh` and the existing demo tests do not change.
D3. Routes. `Graph:NotificationUrl` is the full HTTPS route, for example `https://<tunnel host>/graph/notify`, not just the host name. `lifecycleNotificationUrl` equals `notificationUrl` (allowed by Microsoft and by the TRP). `POST /graph/lifecycle` from TRP 3.23.1 stays. `POST /graph/notify` distinguishes elements: an element with `lifecycleEvent` goes to `HandleLifecycleAsync` (`reauthorizationRequired`, `subscriptionRemoved`, `missed`), an element with `changeType` goes to the inbox. Microsoft documents `subscriptionRemoved` and `missed` only for other resources and does not name `communications/callRecords`; these paths are implemented and tested synthetically, not promised for real callRecords subscriptions.
D4. Subscription management is enabled only when `Graph:NotificationUrl` is set (live mode). `GraphOptions.ValidateLive` then requires `NotificationUrl` (absolute HTTPS address ending with `/graph/notify`) and `ClientState` (8..128 characters, the Graph limit is 128). Without a `NotificationUrl` the polling-only live mode works unchanged and does not touch subscription management.
D5. Start order: configuration, SQLite and migration, readiness, HTTP server listening, and only then the first `SubscriptionService.EnsureAsync` (after `ApplicationStarted`; Graph validates the public URL while the subscription is created). Then `EnsureAsync` every `Subscriptions:RenewCheckMinutes` (5).
D6. Reconciliation stays independent: `ReconcileWorker` is unchanged and shares no state with subscriptions. A Graph error handled by `SubscriptionService` is written to `error_log` (component Subscriptions); an unexpected exception (for example a database failure) is caught by `SubscriptionWorker` and written to the application log only. Neither stops reconciliation.

## Deviations from the TRP

DV-1 (TRP 3.15.3, EnsureAsync). Changing the notification URL is not automated in this increment. Graph rejects a second subscription with the same changeType and resource (HTTP 409), so a seamless "create new, delete old" switch is not possible. PATCH can change `notificationUrl` but not `lifecycleNotificationUrl`. Therefore:
- "best" requires `notification_url` to equal the current `Graph:NotificationUrl`.
- If a row of the kind and resource that has not expired has another URL, whatever its status (including `failed`), the service does not POST, does not renew, does not delete anything, writes `error_log` ("subscription notification URL mismatch") and returns false. This check runs before any cleanup, so it also holds when a correct subscription exists next to the mismatched one: that one is not renewed either until the owner resolves the URL. Reconciliation keeps working.
- An expired row is replaced as in the TRP.
- Moving to a new URL is a separate, manual procedure (delete the old subscription, then create), documented later; reconciliation covers the gap.
- Where the TRP test `Ensure_ForeignNotificationUrl_IsNotAdopted` expects a new subscription to be created, Graph may answer 409; the service then logs the error and returns false.

DV-3 (TRP 3.15.3 HandleLifecycle, 3.23.3). Details found while implementing:
- Microsoft's lifecycle notification contains `subscriptionId`, `subscriptionExpirationDateTime`, `tenantId`, `clientState` and `lifecycleEvent`, and no `resource`. For lifecycle elements the resource is therefore optional; when present it is still compared. Change notifications require it.
- A notification resource (`communications/callRecords/{id}`) matches a subscription of `communications/callRecords` by prefix (`ResourceMatch.Covers`), in the stored row and in the pending marker.
- In managed mode `tenantId` is required. Demo mode keeps its old rules.
- `missed` is handled before the 202 answer (one inbox row). `reauthorizationRequired` and `subscriptionRemoved` run in the background, because renewal and re-creation call Graph and Graph expects the answer within about 3 seconds; the periodic check repairs the subscription if such an attempt is lost.
- After a failed renewal, `Create` may adopt the same subscription again (it still exists in Graph); the old row is then kept and not deleted.
- Operations that change subscriptions are serialized by one lock, so the periodic check and a lifecycle notification cannot both create a subscription (Graph answers 409).
- A new interface `ISubscriptionService` lets the webhook and the worker be tested with a stand-in.

DV-2 (TRP 3.23.3). `PendingSubscriptionRegistry` stays in `Host/Web` as in the TRP. Because the service in Infrastructure must register a marker after a successful create, Infrastructure defines a small interface `IPendingSubscriptionMarker` (`Mark(id, tenantId, resource, clientStateHash)`), and the registry implements it. The dependency points from Host to Infrastructure only.

## Files

New in `src/TeamsCallRecords.Infrastructure/Subscriptions/`: `SubscriptionOptions.cs`, `ISubscriptionStore.cs`, `SubscriptionStore.cs` (with `HashClientState` and `Matches`), `SubscriptionService.cs`, `IPendingSubscriptionMarker.cs`.
New in `src/TeamsCallRecords.Host/`: `Web/PendingSubscriptionRegistry.cs` (in memory, TTL `Webhook:SubscriptionCreationGraceSeconds`=120, allowed 1..300), `Web/GraphNotificationValidator.cs` (live classification: valid, temporarilyUnknownSubscription, permanentlyInvalid), `Web/ResourceMatch.cs`, `Workers/SubscriptionWorker.cs`. In Infrastructure also `Subscriptions/ISubscriptionService.cs`.
Changed: `Infrastructure/InfrastructureRegistration.cs`, `Infrastructure/Config/GraphOptions.cs` (`ValidateLive`, D4), `Host/Web/GraphWebhookEndpoints.cs` (live validator, lifecycle routing, 503 for a pending subscription), `Host/Program.cs` (registrations; the worker is added in Graph:Mode=Live and does nothing unless `NotificationUrl` is set).
Not changed: `Core`, `Reconciler`, `ReconcileWorker`, `CdrApplier`, `GraphClient`. Splitting out the TRP's `GraphNotificationParser` is not part of this increment.

## Tests

Implemented in four increments (storage and registry; service; webhook and validator; worker and settings validation). Test classes: `SubscriptionStoreTests`, `SubscriptionOptionsTests`, `SubscriptionServiceTests` (the 17 tests of TRP 3.20.15 plus URL change for rows of any status, 409, adoption after a failed renewal, hash kept on renewal, disabled kind untouched), `PendingSubscriptionRegistryTests` (TTL, removal, prefix match), `ManagedWebhookTests` (per-subscription clientState, mixed batches, creation race with 503 within the TTL and 403 after it, lifecycle on both routes, `missed` failure gives 503), `SubscriptionWorkerTests` (start order, period, failures, a subscription failure does not stop reconciliation), `GraphSubscriptionOptionsTests`. The TRP names `WebhookEndpointTests`, `WebhookSecurityTests` and `LifecycleEndpointTests` are covered by `HttpTests` and `ManagedWebhookTests` in this increment.
Polling-only: without `Graph:NotificationUrl` TCRI starts and works as before and makes no subscription call (`SubscriptionWorker` returns at once; demo-mode webhook tests keep the fixed-value check).
Regression: the earlier 356 tests stay green.

## Out of scope

Opening the tunnel, creating a real subscription, activity notifications and the 403, the Teams bot, channel messages, automatic URL migration.

## Before the live step (separate approval)

Demo endpoints off, an own `Api__Key` (the tunnel exposes the whole Host port and `/api` is protected only by the key), a separate data directory, a short anonymous-access window, and a synthetic check of the validation handshake.

## Risks

A crash between a successful create and saving the row leaves the subscription unknown until the next discovery pass (TRP known limitation). The marker is in memory, so a crash in that window may cause a 403 on a retry (TRP risk DR-19). Dev Tunnels accepting Graph's anonymous requests is untested and will show at subscription creation.