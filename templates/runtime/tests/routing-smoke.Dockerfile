FROM ubuntu:24.04
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq python3 caddy sudo curl >/dev/null && rm -rf /var/lib/apt/lists/*
COPY templates/runtime/apply-minicloud-routing.py /opt/minicloud/agent/apply-minicloud-routing
RUN chmod 755 /opt/minicloud/agent/apply-minicloud-routing && useradd --system minicloud-agent && mkdir -p /opt/minicloud/apps/demo /opt/minicloud/caddy/apps /opt/minicloud/locks /var/lib/minicloud-agent && chown minicloud-agent /var/lib/minicloud-agent
COPY templates/runtime/tests/routing-smoke.sh /smoke.sh
CMD ["bash", "/smoke.sh"]
