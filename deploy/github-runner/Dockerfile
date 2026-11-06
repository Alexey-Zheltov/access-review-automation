# Self-hosted GitHub Actions runner in a container (sample).
# Based on the official runner image. Adds the tools the deployment workflow needs (ssh/scp, curl, jq).
# The container uses the Docker engine of its host through the mounted Docker socket (docker build / push).
FROM ghcr.io/actions/actions-runner:latest

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-client curl ca-certificates jq \
    && rm -rf /var/lib/apt/lists/*

COPY start.sh /home/runner/start.sh
RUN chmod +x /home/runner/start.sh

# The runner process runs as root so that it can use the mounted Docker socket.
ENV RUNNER_ALLOW_RUNASROOT=1
WORKDIR /home/runner
ENTRYPOINT ["/home/runner/start.sh"]
