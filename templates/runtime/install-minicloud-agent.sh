#!/usr/bin/env bash
set -euo pipefail

install -d -o root -g root -m 0755 /opt/minicloud/agent
install -d -m 0750 /var/lib/minicloud-agent

if ! id minicloud-agent >/dev/null 2>&1; then
  useradd --system --home /var/lib/minicloud-agent --shell /usr/sbin/nologin minicloud-agent
fi

for group in docker systemd-journal adm; do
  if getent group "${group}" >/dev/null 2>&1; then
    usermod -aG "${group}" minicloud-agent || true
  fi
done
chown -R minicloud-agent:minicloud-agent /var/lib/minicloud-agent
chown root:minicloud-agent /opt/minicloud/agent/config.json
chown minicloud-agent:minicloud-agent /opt/minicloud/agent/runtime-token
chmod 0640 /opt/minicloud/agent/config.json
chmod 0600 /opt/minicloud/agent/runtime-token
chown root:root /opt/minicloud/agent/minicloud-agent /opt/minicloud/agent/apply-minicloud-routing
chmod 0755 /opt/minicloud/agent/minicloud-agent /opt/minicloud/agent/apply-minicloud-routing

install -m 0644 /opt/minicloud/agent/minicloud-agent.service /etc/systemd/system/minicloud-agent.service
command -v python3 >/dev/null
install -d -o root -g root -m 0755 /etc/sudoers.d
printf '%s\n' 'minicloud-agent ALL=(root) NOPASSWD: /opt/minicloud/agent/apply-minicloud-routing ""' > /etc/sudoers.d/minicloud-agent-routing
chmod 0440 /etc/sudoers.d/minicloud-agent-routing
visudo -cf /etc/sudoers.d/minicloud-agent-routing
systemctl daemon-reload
systemctl enable minicloud-agent.service
systemctl restart minicloud-agent.service
