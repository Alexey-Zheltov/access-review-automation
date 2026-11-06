# Self-hosted GitHub Actions runner for the Access Review portal

This guide sets up a self-hosted runner for the sample workflow `.github/workflows/deploy-access-review.yml`.
The runner builds and tests the portal, pushes the container images to GitHub Container Registry (GHCR) and deploys them to your Docker host over SSH.

The workflow and the runner image are a sample. Adapt them to your network, security and change-management rules.

## How it works

```
GitHub (repository, Actions, GHCR)
        ^   outbound HTTPS only
        |
Runner container  --(Docker socket)-->  Docker engine of the runner host   (docker build / docker push)
        |
        +--(SSH, port 22)-->  Target VM with Docker  (docker compose pull / run migrate / up)
```

- The runner opens only **outbound** HTTPS connections to GitHub. You do not open inbound ports for it.
- The target VM pulls the images with a token that the run copies to it (GHCR: the short-lived `GITHUB_TOKEN`). After the pull it logs out and deletes the token file, so no registry password stays on the VM.
- The runner and the target VM can be the same machine or two machines.

## What you need

| Item | Details |
|---|---|
| Runner host | Linux with Docker Engine, or Windows Server with Docker (Linux containers). 2 vCPU, 4 GB RAM, 30 GB disk are enough. |
| Outbound HTTPS from the runner | `github.com`, `api.github.com`, `*.actions.githubusercontent.com`, `ghcr.io`, `pkg-containers.githubusercontent.com`, `*.blob.core.windows.net`, `mcr.microsoft.com`, `api.nuget.org`, `builds.dotnet.microsoft.com` |
| SSH from the runner to the target VM | Port 22. A deploy user with a key pair (see step 1). |
| Target VM | Docker with the Compose plugin, the deployment folder with `.env` and `certs/` (see INSTALL-GUIDE.md). |
| GitHub | A private repository with this code. You need the **Admin** role on the repository (or organization owner for an organization runner). |

## Step 1. Create the deploy key and allow it on the target VM

Create a key pair on your admin workstation. The private key goes to GitHub (step 2), the public key to the target VM.

```bash
ssh-keygen -t ed25519 -N "" -C "access-review-pipeline-deploy" -f ./deploy_key
```

The key must have **no passphrase** (the pipeline cannot type one). Take care in PowerShell: in PowerShell 7 use `-N ''`; in Windows PowerShell 5.1 use `-N '""'`. With the wrong form the key gets a passphrase of two quote characters. Check the key with `ssh-keygen -y -P '' -f ./deploy_key`: it must print the public key without an error.

**Linux target VM.** Create a deploy user that may run Docker, and allow the key:

```bash
sudo useradd -m -s /bin/bash deploy
sudo usermod -aG docker deploy
sudo mkdir -p /home/deploy/.ssh
cat deploy_key.pub | sudo tee -a /home/deploy/.ssh/authorized_keys
sudo chown -R deploy:deploy /home/deploy/.ssh && sudo chmod 700 /home/deploy/.ssh && sudo chmod 600 /home/deploy/.ssh/authorized_keys
sudo chown -R deploy:deploy /opt/access-review
```

**Windows Server target VM** (OpenSSH Server). For a member of the local Administrators group, Windows OpenSSH reads `administrators_authorized_keys`:

```powershell
$key = Get-Content .\deploy_key.pub -Raw
Add-Content -Path C:\ProgramData\ssh\administrators_authorized_keys -Value $key.Trim()
icacls C:\ProgramData\ssh\administrators_authorized_keys /inheritance:r /grant "Administrators:F" /grant "SYSTEM:F"
```

Test the key from the runner host:

```bash
ssh -i ./deploy_key deploy@<target-vm> "docker compose version"
```

## Step 2. Repository variables and secret

Open the repository, then **Settings > Secrets and variables > Actions**.

| Kind | Name | Example | Meaning |
|---|---|---|---|
| Variable | `DEPLOY_HOST` | `10.10.20.15` | Name or IP of the target VM |
| Variable | `DEPLOY_USER` | `deploy` | SSH user on the target VM |
| Variable | `DEPLOY_PATH` | `/opt/access-review` or `C:/access-review` | Deployment folder (with `.env` and `certs/`) |
| Variable | `DEPLOY_HOST_OS` | `linux` or `windows` | Shell on the target VM |
| Variable | `PORTAL_URL` | `https://accessreview.contoso.com:8088` | Used by the health check |
| Variable | `DEPLOY_KNOWN_HOSTS` | output of `ssh-keyscan -t ed25519 <target-vm>` | Pins the SSH host key (recommended) |
| Secret | `DEPLOY_SSH_KEY` | content of `deploy_key` | Private key of the deploy user |

Create an environment called `production` (**Settings > Environments > New environment**). The deploy jobs use it. You can add required reviewers there, so that every deployment waits for an approval.

The same with GitHub CLI:

```bash
REPO=<owner>/<repo>
gh variable set DEPLOY_HOST --repo $REPO --body "10.10.20.15"
gh variable set DEPLOY_USER --repo $REPO --body "deploy"
gh variable set DEPLOY_PATH --repo $REPO --body "/opt/access-review"
gh variable set DEPLOY_HOST_OS --repo $REPO --body "linux"
gh variable set PORTAL_URL --repo $REPO --body "https://accessreview.contoso.com:8088"
gh variable set DEPLOY_KNOWN_HOSTS --repo $REPO --body "$(ssh-keyscan -t ed25519 10.10.20.15 2>/dev/null)"
gh secret set DEPLOY_SSH_KEY --repo $REPO < ./deploy_key
gh api -X PUT repos/$REPO/environments/production
```

After that, delete the local copy of `deploy_key` or keep it in your password vault.

### Container registry

By default the workflow pushes the images to GitHub Container Registry (`ghcr.io/<owner>`) with the `GITHUB_TOKEN` of the run. Nothing else is needed.

To use another registry, for example Azure Container Registry, set:

| Kind | Name | Example |
|---|---|---|
| Variable | `REGISTRY` | `myregistry.azurecr.io` |
| Variable | `REGISTRY_USERNAME` | `gh-access-review` |
| Secret | `REGISTRY_PASSWORD` | password of the registry user or token |

For Azure Container Registry, a repository-scoped token keeps the rights small (push and pull only for the two repositories):

```bash
az acr token create --name gh-access-review --registry myregistry \
  --repository access-review-portal content/write content/read metadata/read \
  --repository access-review-migrator content/write content/read metadata/read \
  --expiration-in-days 90 --query "credentials.passwords[0].value" -o tsv | gh secret set REGISTRY_PASSWORD --repo $REPO
gh variable set REGISTRY --repo $REPO --body "myregistry.azurecr.io"
gh variable set REGISTRY_USERNAME --repo $REPO --body "gh-access-review"
```

If the registry has only a private endpoint, the runner host and the target VM must resolve its name to the private IP address.

## Step 3. Get a registration token

In the repository open **Settings > Actions > Runners > New self-hosted runner**. Copy the token from the `./config.sh --url ... --token XXXXX` line.

Or with GitHub CLI:

```bash
gh api -X POST repos/<owner>/<repo>/actions/runners/registration-token --jq .token
```

The token is valid for one hour and only for the registration. After the registration the runner uses its own credentials.

For an organization runner (shared by several repositories), use **Organization settings > Actions > Runners** and `gh api -X POST orgs/<org>/actions/runners/registration-token`. Put the runner in a runner group that only the needed repositories may use.

## Step 4. Build the runner image

The folder `deploy/github-runner` contains a small image based on the official runner image `ghcr.io/actions/actions-runner`. It adds `ssh`, `scp`, `curl` and `jq`. Copy the folder to the runner host and build it:

```bash
cd deploy/github-runner
docker build -t access-review-runner:1 .
```

## Step 5. Start the runner

```bash
docker run -d --name access-review-runner --restart unless-stopped \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e RUNNER_URL=https://github.com/<owner>/<repo> \
  -e RUNNER_TOKEN=<registration token from step 3> \
  -e RUNNER_NAME=access-review-runner-01 \
  -e RUNNER_LABELS=access-review \
  access-review-runner:1
```

On Windows Server with Docker Desktop, run the same command in PowerShell. Put it on one line, or replace `\` with a backtick (`` ` ``).

- `RUNNER_LABELS=access-review` must match the `runs-on: [self-hosted, linux, access-review]` lines of the workflow. GitHub adds `self-hosted`, `Linux` and `X64` itself.
- The Docker socket lets the runner build images with the Docker engine of its host. Anyone who can change workflows in the repository can control this engine. Use a dedicated runner host, and use the runner only for private repositories.
- The registration is stored in the container. A restart does not need a new token. If you delete the container, remove the runner in GitHub and register again with a new token.

## Step 6. Check that the runner is online

In **Settings > Actions > Runners** the runner shows as **Idle**. Or:

```bash
gh api repos/<owner>/<repo>/actions/runners --jq '.runners[] | "\(.name) \(.status) \(.labels | map(.name) | join(","))"'
```

Expected output:

```
access-review-runner-01 online self-hosted,Linux,X64,access-review
```

The container log shows `Listening for Jobs`:

```bash
docker logs access-review-runner --tail 5
```

## Step 7. Run the workflow

A push to `main` starts the workflow. To start it by hand, with a choice of the target:

```bash
gh workflow run deploy-access-review.yml --repo <owner>/<repo> -f target=ReservedVM_DockerCompose -f database=ContainerOnVM
gh run watch --repo <owner>/<repo>
```

What the jobs do:

| Job | Steps |
|---|---|
| `build` | .NET 10 restore and unit tests, EF Core migration bundle (`efbundle`), images `access-review-portal` and `access-review-migrator` pushed to `ghcr.io/<owner>` with the commit tag and `latest` |
| `deploy-db` | copies `docker-compose.yml` and `deploy/postgres/postgresql.conf` to the VM, starts PostgreSQL, runs the migration image (`docker compose run --rm migrate`). The migration only applies what is missing, so it is safe to run it every time. |
| `deploy-app` | pulls the new portal image on the VM, recreates only the portal container (`docker compose up -d --no-deps web`), then calls `PORTAL_URL/healthz` |

## Troubleshooting

| Message | Cause and fix |
|---|---|
| Job stays "Waiting for a runner" | No online runner has all labels of `runs-on`. Check the labels in step 6. |
| `Permission denied (publickey)` | The public key is not in the right file on the VM (Windows administrators: `C:\ProgramData\ssh\administrators_authorized_keys`), `DEPLOY_USER` is wrong, or the private key has a passphrase (check with `ssh-keygen -y -P '' -f deploy_key`). |
| `Host key verification failed` | `DEPLOY_KNOWN_HOSTS` does not match the VM. Create it again with `ssh-keyscan`. |
| `error getting credentials` / `A specified logon session does not exist` on Windows | Docker on Windows tries to use the Windows credential store, which SSH sessions cannot use. The deploy script sets `DOCKER_CONFIG` to `<DEPLOY_PATH>/.docker-nocreds` to avoid it. |
| `denied` or `denied: permission_denied` on push to GHCR | The workflow needs `packages: write` (already set). Enterprise managed users (EMU) cannot publish to their personal namespace: keep the repository in an organization. The organization may also restrict package creation (**Organization settings > Packages**). Or use another registry (see "Container registry"). |
| `Artifact storage quota has been hit` | The account has no artifact storage left. The upload step is optional and does not stop the deployment. |
| Health check fails | Look at `docker compose logs web` on the VM. The portal needs `.env` and the certificate in `certs/`. |

## Remove the runner

```bash
docker rm -f access-review-runner
gh api -X DELETE repos/<owner>/<repo>/actions/runners/<runner id>
```

Get the runner id with the command of step 6 (`.runners[].id`).
