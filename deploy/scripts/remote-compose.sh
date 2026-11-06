#!/usr/bin/env bash
# Runs "docker compose" commands in the deployment folder of the target VM over SSH (used by the pipelines).
#   remote-compose.sh <registry user> "<compose args>" ["<compose args>" ...]
# Environment: DEPLOY_HOST, DEPLOY_USER, DEPLOY_PATH, DEPLOY_HOST_OS (windows | linux), PORTAL_IMAGE, MIGRATOR_IMAGE,
#              REGISTRY (default ghcr.io), SSH_KEY (default ~/.ssh/deploy_key),
#              PERSIST_IMAGES=1 to write PORTAL_IMAGE / MIGRATOR_IMAGE into <DEPLOY_PATH>/.env after success, so a manual
#              "docker compose up -d" on the VM uses the deployed images.
# If the pipeline copied a registry token (GITHUB_TOKEN, ACR token) to <DEPLOY_PATH>/.registry-token, the VM logs in to
# the registry, pulls, logs out and deletes the token file. The VM keeps no registry credentials.
set -euo pipefail

registry_user="$1"; shift
registry="${REGISTRY:-ghcr.io}"
key="${SSH_KEY:-$HOME/.ssh/deploy_key}"
target="${DEPLOY_USER}@${DEPLOY_HOST}"
persist="${PERSIST_IMAGES:-0}"

if [ "${DEPLOY_HOST_OS:-linux}" = "windows" ]; then
  # Windows Server with Docker: a PowerShell script passed with -EncodedCommand (UTF-16LE, base64). Messages use Write-Output and
  # -OutputFormat Text: over SSH Windows PowerShell writes Write-Host and progress records as CLIXML.
  # DOCKER_CONFIG points to a folder without a credential helper: the Windows credential store is not available in SSH sessions.
  script=$(
    cat <<'PS'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
function Set-EnvValue([string] $Name, [string] $Value) {
  $path = Join-Path (Get-Location) '.env'
  if (-not (Test-Path $path)) { return }
  $lines = [System.Collections.Generic.List[string]]::new()
  $lines.AddRange([string[]]@(Get-Content -Path $path))
  $index = -1
  for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match ('^\s*' + [regex]::Escape($Name) + '=')) { $index = $i; break } }
  if ($index -ge 0) { $lines[$index] = "$Name=$Value" } else { $lines.Add("$Name=$Value") }
  [System.IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
}
PS
    echo "Set-Location '${DEPLOY_PATH}'"
    echo "\$env:DOCKER_CONFIG = Join-Path (Get-Location) '.docker-nocreds'"
    echo "New-Item -ItemType Directory -Force \$env:DOCKER_CONFIG | Out-Null"
    echo "\$cfg = Join-Path \$env:DOCKER_CONFIG 'config.json'"
    echo "if (-not (Test-Path \$cfg)) { '{\"auths\":{\"example.invalid\":{}}}' | Set-Content \$cfg -NoNewline }"
    echo "\$env:PORTAL_IMAGE = '${PORTAL_IMAGE}'"
    echo "\$env:MIGRATOR_IMAGE = '${MIGRATOR_IMAGE}'"
    echo "if (Test-Path .registry-token) {"
    echo "  Get-Content .registry-token -Raw | docker login ${registry} -u '${registry_user}' --password-stdin"
    echo "  \$loginExit = \$LASTEXITCODE"
    echo "  Remove-Item .registry-token -Force"
    echo "  if (\$loginExit) { throw 'docker login failed' }"
    echo "}"
    echo "try {"
    for args in "$@"; do
      echo "  Write-Output '> docker compose ${args}'"
      echo "  docker compose ${args}"
      echo "  if (\$LASTEXITCODE) { throw 'docker compose ${args} failed' }"
    done
    if [ "$persist" = "1" ]; then
      echo "  Set-EnvValue 'PORTAL_IMAGE' '${PORTAL_IMAGE}'"
      echo "  Set-EnvValue 'MIGRATOR_IMAGE' '${MIGRATOR_IMAGE}'"
      echo "  Write-Output 'Recorded the deployed images in .env'"
    fi
    echo "}"
    echo "finally {"
    echo "  docker logout ${registry} *> \$null"
    echo "}"
  )
  encoded=$(printf '%s' "$script" | iconv -f UTF-8 -t UTF-16LE | base64 -w0)
  ssh -i "$key" -o BatchMode=yes "$target" "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand ${encoded}"
else
  {
    echo "set -euo pipefail"
    echo "cd '${DEPLOY_PATH}'"
    echo "export PORTAL_IMAGE='${PORTAL_IMAGE}' MIGRATOR_IMAGE='${MIGRATOR_IMAGE}'"
    echo "trap 'docker logout ${registry} >/dev/null 2>&1 || true' EXIT"
    echo "if [ -f .registry-token ]; then docker login ${registry} -u '${registry_user}' --password-stdin < .registry-token; rm -f .registry-token; fi"
    for args in "$@"; do
      echo "echo '> docker compose ${args}'"
      echo "docker compose ${args}"
    done
    if [ "$persist" = "1" ]; then
      cat <<'SH'
set_env() {
  [ -f .env ] || return 0
  if grep -q "^$1=" .env; then sed -i "s|^$1=.*|$1=$2|" .env; else printf '%s=%s\n' "$1" "$2" >> .env; fi
}
SH
      echo "set_env PORTAL_IMAGE '${PORTAL_IMAGE}'"
      echo "set_env MIGRATOR_IMAGE '${MIGRATOR_IMAGE}'"
      echo "echo 'Recorded the deployed images in .env'"
    fi
  } | ssh -i "$key" -o BatchMode=yes "$target" "bash -s"
fi
