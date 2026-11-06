#!/usr/bin/env bash
# Registers the runner on the first start, then runs it. The registration is kept in the container,
# so a restart of the container does not need a new token.
#   RUNNER_URL     https://github.com/<owner>/<repo>  (or https://github.com/<org> for an organization runner)
#   RUNNER_TOKEN   registration token (valid for 1 hour, only needed for the first start)
#   RUNNER_NAME    name shown in GitHub (default: container host name)
#   RUNNER_LABELS  extra labels, comma separated (default: access-review)
#   RUNNER_GROUP   runner group (organization runners only, optional)
set -euo pipefail
cd /home/runner

if [ ! -f .runner ]; then
  : "${RUNNER_URL:?Set RUNNER_URL}"
  : "${RUNNER_TOKEN:?Set RUNNER_TOKEN (registration token)}"
  args=(--unattended --url "$RUNNER_URL" --token "$RUNNER_TOKEN" --name "${RUNNER_NAME:-$(hostname)}"
        --labels "${RUNNER_LABELS:-access-review}" --work _work --replace)
  if [ -n "${RUNNER_GROUP:-}" ]; then args+=(--runnergroup "$RUNNER_GROUP"); fi
  ./config.sh "${args[@]}"
fi

exec ./run.sh
