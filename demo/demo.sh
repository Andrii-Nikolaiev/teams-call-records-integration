#!/usr/bin/env bash
# TCRI demo: one synthetic call goes through the whole pipeline over HTTP.
# Requires the Host to be running (dotnet run --project src/TeamsCallRecords.Host) and bash + curl + GNU date (Git Bash is fine).
# Usage: bash demo/demo.sh [base_url]
set -u

BASE="${1:-http://localhost:60049}"
KEY="demo-only-key-change-me"                 # Api:Key from appsettings.json (demo value)
CLIENT_STATE="demo-only-client-state"         # Webhook:ClientState from appsettings.json (demo value)
TENANT="11111111-1111-4111-8111-111111111111" # Graph:TenantId from appsettings.json

iso() { date -u -d "$1" +%Y-%m-%dT%H:%M:%SZ; }
START="$(iso '10 minutes ago')"
END="$(iso '9 minutes ago')"
FROM="$(iso '1 hour ago')"
TO="$(iso '1 hour')"
CALL_ID="demo-$(date -u +%s)"
EP='"@odata.type":"#microsoft.graph.callRecords.participantEndpoint"'

RECORD="{\"id\":\"$CALL_ID\",\"version\":3,\"type\":\"peerToPeer\",\"modalities\":[\"audio\"],\"startDateTime\":\"$START\",\"endDateTime\":\"$END\",\"sessions\":[{\"id\":\"$CALL_ID-s1\",\"modalities\":[\"audio\"],\"startDateTime\":\"$START\",\"endDateTime\":\"$END\",\"caller\":{$EP,\"identity\":{\"user\":{\"id\":\"user-a\"}}},\"callee\":{$EP,\"identity\":{\"user\":{\"id\":\"user-b\"}}},\"segments\":[{\"id\":\"seg-1\",\"startDateTime\":\"$START\",\"endDateTime\":\"$END\"}]}]}"
NOTIFY="{\"value\":[{\"subscriptionId\":\"sub-1\",\"changeType\":\"created\",\"clientState\":\"$CLIENT_STATE\",\"tenantId\":\"$TENANT\",\"resource\":\"communications/callRecords/$CALL_ID\",\"resourceData\":{\"@odata.type\":\"#microsoft.graph.callRecords.callRecord\",\"id\":\"$CALL_ID\"}}]}"
BAD_NOTIFY="${NOTIFY/$CLIENT_STATE/wrong-client-state}"

step() { printf '\n== %s\n' "$1"; }
post() { curl -s -w '\n-> HTTP %{http_code}\n' -H 'Content-Type: application/json' "${@:2}" -d "$1"; }

step "1. Health"
curl -s -w '\n-> HTTP %{http_code}\n' "$BASE/health/ready" || { echo "Host is not running at $BASE"; exit 1; }

step "2. Put a call record into the in-memory Graph stand-in (call $CALL_ID)"
post "$RECORD" -H "X-TCRI-Key: $KEY" "$BASE/demo/call-records"

step "3. Graph change notification with a WRONG clientState (must be rejected, 403)"
post "$BAD_NOTIFY" "$BASE/graph/notify"

step "4. Graph change notification with the right clientState (accepted, 202)"
post "$NOTIFY" "$BASE/graph/notify"

echo; echo "Waiting 4 s for the background workers..."; sleep 4

step "5. CDR via REST"
curl -s -w '\n-> HTTP %{http_code}\n' -H "X-TCRI-Key: $KEY" "$BASE/api/cdr?from=$FROM&to=$TO"

step "6. Activity notifications sent to the (in-memory) Graph"
curl -s -w '\n-> HTTP %{http_code}\n' -H "X-TCRI-Key: $KEY" "$BASE/demo/notifications"

step "7. Status"
curl -s -w '\n-> HTTP %{http_code}\n' -H "X-TCRI-Key: $KEY" "$BASE/api/status"

step "8. REST without the key (must be rejected, 401)"
curl -s -w '\n-> HTTP %{http_code}\n' "$BASE/api/status"
