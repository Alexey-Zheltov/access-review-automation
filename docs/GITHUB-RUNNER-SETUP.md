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
| Runner host | A Linux VM with Docker Engine, only for the runner. The steps below use Ubuntu Server 24.04 LTS. 2 vCPU, 4 GB RAM, 40 GB disk. Windows Server with Docker (Linux containers) also works. See [Prepare the runner host](#prepare-the-runner-host-ubuntu-server). |
| Outbound HTTPS from the runner | `github.com`, `api.github.com`, `codeload.github.com`, `*.actions.githubusercontent.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`, `ghcr.io`, `pkg-containers.githubusercontent.com`, `*.blob.core.windows.net`, `mcr.microsoft.com`, `api.nuget.org`, `builds.dotnet.microsoft.com`. To install Docker and to build the image with Azure CLI: also `download.docker.com`, `packages.microsoft.com`, `aka.ms`. |
| SSH from the runner to the target VM | Port 22. A deploy user with a key pair (see step 1). |
| Target VM | Docker with the Compose plugin, the deployment folder with `.env` and `certs/` (see INSTALL-GUIDE.md). |
| GitHub | A private repository with this code. You need the **Admin** role on the repository (or organization owner for an organization runner). |

## Prepare the runner host (Ubuntu Server)

The commands are for a new Ubuntu Server 24.04 LTS VM. Run them as an admin user with `sudo`.

**Updates and time.** GitHub and Microsoft Entra ID refuse tokens when the clock is wrong.

```bash
sudo apt-get update && sudo apt-get -y upgrade
[ -f /var/run/reboot-required ] && echo "restart needed" || echo "no restart needed"
timedatectl | grep -E "synchronized|NTP service"     # System clock synchronized: yes
systemctl is-active unattended-upgrades              # active: daily security updates
```

**Firewall.** The runner needs no inbound connection. Allow SSH only from your admin network (example `10.10.0.0/24`). Add the SSH rule before you enable the firewall.

```bash
sudo ufw allow from 10.10.0.0/24 to any port 22 proto tcp
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw --force enable
sudo ufw status verbose
```

Docker opens published container ports past `ufw`. The runner container publishes no port: do not add `-p` to its `docker run` command.

**Outbound check.** Every line must show a number (`401` or `404` also mean that the address answers). `000` means no connection.

```bash
APP=<app-name>   # only for the App Service workflow
for u in https://github.com https://api.github.com https://codeload.github.com \
  https://pipelines.actions.githubusercontent.com https://results-receiver.actions.githubusercontent.com \
  https://objects.githubusercontent.com https://release-assets.githubusercontent.com \
  https://ghcr.io/v2/ https://pkg-containers.githubusercontent.com \
  https://download.docker.com https://packages.microsoft.com https://aka.ms \
  https://builds.dotnet.microsoft.com https://api.nuget.org/v3/index.json \
  https://login.microsoftonline.com https://management.azure.com \
  https://$APP.scm.azurewebsites.net https://$APP.azurewebsites.net; do
  printf '%-58s %s\n' "$u" "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "$u")"
done
```

With a proxy server, pass `https_proxy`, `http_proxy` and `no_proxy` to the runner container with `-e`, and configure the proxy of the Docker daemon. See [Using a proxy server with self-hosted runners](https://docs.github.com/en/actions/how-tos/manage-runners/use-proxy-servers).

**Docker Engine** from the Docker repository, as in [Install Docker Engine on Ubuntu](https://docs.docker.com/engine/install/ubuntu/):

```bash
sudo apt-get remove -y $(dpkg --get-selections docker.io docker-compose docker-compose-v2 docker-doc docker-buildx podman-docker containerd runc | cut -f1)
sudo apt-get install -y ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
sudo tee /etc/apt/sources.list.d/docker.sources <<EOF
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: $(. /etc/os-release && echo "${UBUNTU_CODENAME:-$VERSION_CODENAME}")
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
EOF
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

Limit the size of the container logs:

```bash
sudo tee /etc/docker/daemon.json <<'EOF'
{
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" }
}
EOF
sudo systemctl restart docker
```

The commands below run Docker with `sudo`. Members of the `docker` group can run it without `sudo`, but this gives them the same rights as root.

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

The folder `deploy/github-runner` contains a small image based on the official runner image `ghcr.io/actions/actions-runner`. It adds `ssh`, `scp`, `curl`, `jq` and `zip`. Copy the folder to the runner host, check the line endings and build it:

```bash
scp -r deploy/github-runner <admin>@<runner-host>:~/      # on your workstation
cd ~/github-runner && file start.sh Dockerfile            # on the runner host: no "with CRLF line terminators"
sudo docker build -t access-review-runner:1 .
```

A checkout on Windows can give `start.sh` Windows line endings. Fix them with `sed -i 's/\r$//' start.sh Dockerfile`.

For the App Service workflow (`.github/workflows/deploy-appservice.yml`), add Azure CLI to the image: `sudo docker build --build-arg AZURE_CLI=true -t access-review-runner:1 .`. That workflow does not use Docker, so you can leave out the Docker socket in step 5. Setup of the workflow: [DEPLOY-AZURE-APP-SERVICE.md](DEPLOY-AZURE-APP-SERVICE.md#deploy-with-github-actions-optional).

## Step 5. Start the runner

The first command asks for the token without showing it. `-e RUNNER_TOKEN` without a value passes the variable to the container, so the token is not in the command line or in the shell history.

```bash
read -rsp 'Registration token: ' RUNNER_TOKEN; echo; export RUNNER_TOKEN
sudo --preserve-env=RUNNER_TOKEN docker run -d --name access-review-runner --restart unless-stopped \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e RUNNER_URL=https://github.com/<owner>/<repo> \
  -e RUNNER_TOKEN \
  -e RUNNER_NAME=access-review-runner-01 \
  -e RUNNER_LABELS=access-review \
  access-review-runner:1
unset RUNNER_TOKEN
```

On Windows Server with Docker Desktop, run the command in PowerShell without `sudo`. Put it on one line, or replace `\` with a backtick (`` ` ``), and pass the token as `-e RUNNER_TOKEN=<registration token>`.

- `RUNNER_LABELS=access-review` must match the `runs-on: [self-hosted, linux, access-review]` lines of the workflow. GitHub adds `self-hosted`, `Linux` and `X64` itself.
- The Docker socket lets the runner build images with the Docker engine of its host. Anyone who can change workflows in the repository can control this engine. Use a dedicated runner host, and use the runner only for private repositories.
- The registration is stored in the container. A restart of the container or of the host does not need a new token. If you delete the container, register again with a new token (see "Keep the runner host up to date").

## Step 6. Check that the runner is online

In **Settings > Actions > Runners** the runner shows as **Idle**. Or:

```bash
gh api repos/<owner>/<repo>/actions/runners --jq '.runners[] | "\(.name) \(.status) \(.labels | map(.name) | join(","))"'
```

Expected output:

```
access-review-runner-01 online self-hosted,Linux,X64,access-review
```

The container log shows `Listening for Jobs`. The image also writes detailed trace lines that start with `[RUNNER` or `[WORKER`; the first `grep` hides them:

```bash
sudo docker logs access-review-runner 2>&1 | grep -vE "^\[(RUNNER|WORKER)" | grep -E "Connected to GitHub|Runner successfully added|Settings Saved|Listening for Jobs"
```

Test a restart of the host: after `sudo reboot` the container starts again by itself (`--restart unless-stopped`).

## Step 7. Run the workflow

Start the workflow by hand, with a choice of the target. To start it with every push to `main`, add the `push` trigger at the top of the workflow file (see the comment there).

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
| `permission denied while trying to connect to the docker API at unix:///var/run/docker.sock` | Your account may not use Docker. Run the command with `sudo`. |
| Runner log: "A session for this runner already exists", `Runner connect error: Error: Conflict` | An old container with the same runner name is still connected, or it was not stopped cleanly. Stop and remove the old container. The new runner connects when the old session ends (about 3 minutes in a test). |
| `/usr/bin/env: 'bash\r': No such file or directory` | `start.sh` has Windows line endings. Fix it (step 4), build the image again and start a new container. |

## Keep the runner host up to date

| When | Task |
|---|---|
| Every day, automatic | Ubuntu security updates (`unattended-upgrades`). They do not include Docker Engine, which comes from the Docker repository. |
| Automatic | The runner program updates itself when GitHub releases a new version. If you turn this off (`--disableupdate`), update the runner within 30 days of a new release; else GitHub stops sending jobs to it. |
| Once a month | `sudo apt-get update && sudo apt-get -y upgrade` (also Docker Engine). Restart the host if `/var/run/reboot-required` exists. |
| Once a month | Build the image again with the newest base image and replace the container (below). |
| When the disk is full | `df -h /` and `sudo docker system df`. Remove unused images and build cache: `sudo docker image prune -f` and `sudo docker builder prune -f`. |

Replace the container with a new image. A new container needs a new registration token (step 3). Stop the old container first:

```bash
cd ~/github-runner
sudo docker build --pull -t access-review-runner:1 .      # add --build-arg AZURE_CLI=true for the App Service workflow
sudo docker stop access-review-runner && sudo docker rm access-review-runner
read -rsp 'Registration token: ' RUNNER_TOKEN; echo; export RUNNER_TOKEN
# then the same docker run command as in step 5, and: unset RUNNER_TOKEN
```

The log then shows "A runner exists with the same name" and "Successfully replaced the runner".

With the Docker socket, every run of `deploy-access-review.yml` builds two new images on the runner host. Remove old images once a week, for example with a cron job (it keeps the images of running containers):

```bash
echo '0 3 * * 0 root docker image prune -af --filter "until=168h" > /dev/null' | sudo tee /etc/cron.d/docker-image-prune
```

## Remove the runner

```bash
sudo docker rm -f access-review-runner
gh api -X DELETE repos/<owner>/<repo>/actions/runners/<runner id>
```

Get the runner id with the command of step 6 (`.runners[].id`).
