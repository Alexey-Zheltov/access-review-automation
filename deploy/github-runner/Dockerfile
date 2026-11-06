# Self-hosted GitHub Actions runner in a container (sample).
# Based on the official runner image. Adds the tools the deployment workflows need (ssh/scp, curl, jq, zip).
# deploy-access-review.yml uses the Docker engine of the host through the mounted Docker socket (docker build / push).
# deploy-appservice.yml needs Azure CLI: build with --build-arg AZURE_CLI=true.
FROM ghcr.io/actions/actions-runner:latest

ARG AZURE_CLI=false
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-client curl ca-certificates jq zip \
    && if [ "$AZURE_CLI" = "true" ]; then curl -sL https://aka.ms/InstallAzureCLIDeb | bash; fi \
    && rm -rf /var/lib/apt/lists/*

COPY start.sh /home/runner/start.sh
RUN chmod +x /home/runner/start.sh

# The runner process runs as root so that it can use the mounted Docker socket.
ENV RUNNER_ALLOW_RUNASROOT=1
WORKDIR /home/runner
ENTRYPOINT ["/home/runner/start.sh"]
