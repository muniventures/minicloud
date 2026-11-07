set -euo pipefail
cat > /opt/minicloud/agent/config.json <<'JSON'
{"appId":"app_demo","composeProject":"minicloud-demo"}
JSON
cat > /opt/minicloud/apps/demo/app.compose.json <<'JSON'
{"services":{"api":{"ports":["127.0.0.1:8080:8080"]},"worker":{}}}
JSON
printf '%s\n' 'minicloud-agent ALL=(root) NOPASSWD: /opt/minicloud/agent/apply-minicloud-routing ""' > /etc/sudoers.d/minicloud-agent-routing
chmod 440 /etc/sudoers.d/minicloud-agent-routing
visudo -cf /etc/sudoers.d/minicloud-agent-routing
cat > /usr/local/bin/systemctl <<'SH'
#!/bin/sh
case "$1" in
 reload) exec caddy reload --config /etc/caddy/Caddyfile ;;
 is-active) exec curl -fsS http://127.0.0.1:2019/config/ ;;
 *) exit 1 ;;
esac
SH
chmod 755 /usr/local/bin/systemctl
cat > /etc/caddy/Caddyfile <<'CADDY'
{
 local_certs
}
import /opt/minicloud/caddy/apps/*.caddy
CADDY
python3 -m http.server 8080 --bind 127.0.0.1 >/tmp/backend.log 2>&1 &
caddy start --config /etc/caddy/Caddyfile >/tmp/caddy.log 2>&1
cat > /var/lib/minicloud-agent/routing-request.json <<'JSON'
{"appId":"app_demo","composeProject":"minicloud-demo","routes":[{"hostname":"demo.app.muni.dev","pathPrefix":"/","serviceName":"api","servicePort":8080}]}
JSON
chown minicloud-agent /var/lib/minicloud-agent/routing-request.json
sudo -u minicloud-agent sudo -n /opt/minicloud/agent/apply-minicloud-routing
curl --retry 5 --retry-all-errors --retry-delay 1 -fsS --cacert /root/.local/share/caddy/pki/authorities/local/root.crt --resolve demo.app.muni.dev:443:127.0.0.1 https://demo.app.muni.dev/ >/dev/null
sudo -u minicloud-agent sudo -n /opt/minicloud/agent/apply-minicloud-routing
if sudo -u minicloud-agent sudo -n /opt/minicloud/agent/apply-minicloud-routing /etc/passwd 2>/dev/null; then exit 1; fi
python3 - <<'PY'
import json
p='/var/lib/minicloud-agent/routing-request.json'
r=json.load(open(p)); r['routes'][0]['serviceName']='worker'; open(p,'w').write(json.dumps(r))
PY
if sudo -u minicloud-agent sudo -n /opt/minicloud/agent/apply-minicloud-routing; then exit 1; fi
curl -fsS --cacert /root/.local/share/caddy/pki/authorities/local/root.crt --resolve demo.app.muni.dev:443:127.0.0.1 https://demo.app.muni.dev/ >/dev/null
printf 'PASS: restricted user, real Caddy validate/reload, unchanged routes, argument rejection, private target preserves traffic.\n'
