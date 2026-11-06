#!/usr/bin/env bash
# Smoke test after a deployment: GET <PORTAL_URL>/healthz until it answers "ok" (5 minutes at most).
# For a VM deployment (TARGET=ReservedVM_DockerCompose) the request goes straight to DEPLOY_HOST, but with the host name
# of PORTAL_URL, so the TLS certificate is checked for the real name and internal DNS does not matter.
#   PORTAL_URL   https://accessreview.contoso.com:8088
#   DEPLOY_HOST  name or IP address of the VM (optional)
#   TARGET       ReservedVM_DockerCompose | Azure_AppService
set -uo pipefail

url="${PORTAL_URL%/}"
args=(-fsS --max-time 10)
if [ "${TARGET:-ReservedVM_DockerCompose}" = "ReservedVM_DockerCompose" ] && [ -n "${DEPLOY_HOST:-}" ]; then
  hostport="${url#*://}"; hostport="${hostport%%/*}"
  host="${hostport%%:*}"
  port="${hostport##*:}"; [ "$port" = "$hostport" ] && port=443
  ip="$(getent ahostsv4 "$DEPLOY_HOST" | awk 'NR==1 {print $1}')"
  ip="${ip:-$DEPLOY_HOST}"
  args+=(--resolve "${host}:${port}:${ip}")
  echo "Checking ${url}/healthz on ${ip}"
else
  echo "Checking ${url}/healthz"
fi

for i in $(seq 1 30); do
  if body="$(curl "${args[@]}" "${url}/healthz" 2>/dev/null)" && [ "$body" = "ok" ]; then
    echo "Portal is healthy (attempt $i)."
    exit 0
  fi
  sleep 10
done
echo "The portal did not answer on ${url}/healthz" >&2
curl "${args[@]}" -v "${url}/healthz" || true
exit 1
