ARG WORKER_IMAGE=minicloud-worker:domain-agent-check
FROM --platform=$BUILDPLATFORM ${WORKER_IMAGE} AS worker
FROM ubuntu:24.04
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq python3 jq caddy sudo curl ca-certificates libicu74 >/dev/null && rm -rf /var/lib/apt/lists/*
COPY --from=worker /app/runtime-agent /bundle
COPY scripts/github-actions/minicloud-deploy/remote-deploy.sh /remote-deploy.sh
COPY templates/runtime/tests/worker-agent-smoke.sh /smoke.sh
CMD ["bash", "/smoke.sh"]
