set -euo pipefail
printf '127.0.0.1 %s\n' "$(hostname)" >> /etc/hosts
mkdir -p /opt/minicloud/apps/demo /opt/minicloud/caddy/apps /opt/minicloud/locks /var/lib/minicloud-agent
# systemd and Docker discovery are fixture adapters. Agent, installer, sudo,
# Caddy, certificate trust, HTTP transport and routing helper are real.
groupadd -f docker
cat > /usr/local/bin/systemctl <<'SH'
#!/bin/sh
case "$1" in
 reload) exec caddy reload --config /etc/caddy/Caddyfile ;;
 is-active) echo active ;;
 daemon-reload|enable|restart|stop) exit 0 ;;
 *) exit 1 ;;
esac
SH
cat > /usr/local/bin/docker <<'SH'
#!/bin/sh
case "$1" in
 ps) echo demo-api ;;
 inspect) echo '[{"Id":"demo-api","Name":"demo-api-1","State":{"Status":"running","Health":{"Status":"healthy"}},"Config":{"Image":"fixture","Labels":{"com.docker.compose.service":"api"}},"RestartCount":0}]' ;;
 logs) exit 0 ;;
 *) exit 1 ;;
esac
SH
printf '#!/bin/sh\nexit 0\n' > /usr/local/bin/journalctl
chmod 755 /usr/local/bin/{systemctl,docker,journalctl}
cat > /opt/minicloud/apps/demo/app.compose.json <<'JSON'
{"services":{"api":{"ports":["127.0.0.1:8080:8080"]}}}
JSON
cat > /opt/minicloud/caddy/apps/demo.caddy <<'CADDY'
demo.app.muni.dev {
 reverse_proxy 127.0.0.1:8080
}
CADDY
cat > /etc/caddy/Caddyfile <<'CADDY'
{
 local_certs
}
import /opt/minicloud/caddy/apps/*.caddy
CADDY
python3 -m http.server 8080 --bind 127.0.0.1 >/tmp/backend.log 2>&1 &
caddy start --config /etc/caddy/Caddyfile >/tmp/caddy.log 2>&1
curl --retry 5 --retry-all-errors --retry-delay 1 -kfsS --resolve demo.app.muni.dev:443:127.0.0.1 https://demo.app.muni.dev/ >/dev/null
install -m 0644 /root/.local/share/caddy/pki/authorities/local/root.crt /usr/local/share/ca-certificates/fixture-caddy.crt
update-ca-certificates >/dev/null
cat > /bundle/payload.json <<'JSON'
{"runtime_agent":{"config":{"serverId":"server_demo","organizationId":"org_demo","appId":"app_demo","apiBaseUrl":"http://127.0.0.1:9080","composeProject":"minicloud-demo","composeFile":"/opt/minicloud/apps/demo/app.compose.json","tokenPath":"/opt/minicloud/agent/runtime-token","statePath":"/var/lib/minicloud-agent/state.json"},"token":"fixture-token"}}
JSON
# Execute the exact installation function shipped with the worker runtime.
python3 - <<'PY'
from pathlib import Path
source=Path('/remote-deploy.sh').read_text()
Path('/install-function.sh').write_text('install_runtime_agent() {' + source.split('install_runtime_agent() {',1)[1].split('\nmain() {',1)[0])
PY
phase() { :; }
worker_lease_valid() { return 0; }
PAYLOAD_FILE=/bundle/payload.json
source /install-function.sh
install_runtime_agent
[ "$(stat -c %a /opt/minicloud/agent/runtime-token)" = 600 ]
[ "$(stat -c %U /opt/minicloud/agent/config.json)" = root ]
[ "$(stat -c %a /opt/minicloud/agent/config.json)" = 640 ]
[ "$(stat -c %U /opt/minicloud/agent/apply-minicloud-routing)" = root ]
visudo -cf /etc/sudoers.d/minicloud-agent-routing
cat > /tmp/control.py <<'PY'
from http.server import BaseHTTPRequestHandler,HTTPServer
import json
from pathlib import Path
route={'bindingId':'dom_demo','hostname':'demo.app.muni.dev','pathPrefix':'/','serviceName':'api','servicePort':8080}
Path('/tmp/routes.json').write_text(json.dumps([route]))
class Handler(BaseHTTPRequestHandler):
 def do_GET(self):
  assert self.headers['Authorization']=='Bearer fixture-token'
  data={'version':'2026-10-23T12:00:00Z','appId':'app_demo','composeProject':'minicloud-demo','routes':json.loads(Path('/tmp/routes.json').read_text())}
  self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(json.dumps(data).encode())
 def do_POST(self):
  assert self.headers['Authorization']=='Bearer fixture-token'
  if self.headers.get('Transfer-Encoding')=='chunked':
   chunks=[]
   while True:
    size=int(self.rfile.readline().split(b';')[0],16)
    if not size:
     self.rfile.readline();break
    chunks.append(self.rfile.read(size));self.rfile.read(2)
   raw=b''.join(chunks)
  else:raw=self.rfile.read(int(self.headers['Content-Length']))
  data=json.loads(raw)
  with open('/tmp/events.jsonl','a') as out:out.write(json.dumps({'path':self.path,'body':data})+'\n')
  self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(b'{}')
 def log_message(self,*args):pass
HTTPServer(('127.0.0.1',9080),Handler).serve_forever()
PY
python3 /tmp/control.py &
sudo -u minicloud-agent /opt/minicloud/agent/minicloud-agent /opt/minicloud/agent/config.json >/tmp/agent.log 2>&1 &
agent_pid=$!
trap 'kill "$agent_pid" 2>/dev/null || true' EXIT
python3 - <<'PY'
import json,time
from pathlib import Path
for _ in range(60):
 events=[json.loads(line) for line in Path('/tmp/events.jsonl').read_text().splitlines()] if Path('/tmp/events.jsonl').exists() else []
 heartbeats=[e for e in events if e['path'].endswith('/heartbeat')]
 statuses=[e for e in events if e['path'].endswith('/ingest/status')]
 applied=[e for e in events if e['path'].endswith('/apply-results') and e['body']['results'][0]['applyStatus']=='applied' and e['body']['results'][0]['sslStatus']=='ready']
 if heartbeats and statuses and applied:
  assert statuses[-1]['body']['services'][0]['health']=='healthy'
  print('PASS: packaged worker agent installed as restricted user; heartbeat, runtime status, real Caddy routing and trusted TLS apply result received.')
  break
 time.sleep(1)
else:
 print(Path('/tmp/agent.log').read_text());raise SystemExit('Agent did not report ready routing within 60 seconds')
PY
