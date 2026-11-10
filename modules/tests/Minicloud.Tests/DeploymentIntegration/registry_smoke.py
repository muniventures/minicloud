#!/usr/bin/env python3
"""Real Distribution + packaged API interoperability, using the existing component stack."""
import base64
import concurrent.futures
import hashlib
import json
import os
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

import component_smoke as component

REGISTRY_IMAGE = "registry:3.1.2@sha256:01cd312e67026f06f5d89c3e133a4a9adc7cb9eeb9b1194e0b2954dfd770efd2"


def main():
    fixture_user = f"{os.getuid()}:{os.getgid()}"
    with tempfile.TemporaryDirectory(prefix="minicloud-registry-smoke-") as temp:
        directory = Path(temp)
        (directory / "auth").mkdir()
        (directory / "data").mkdir()
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=minicloud-registry-test", "-days", "2",
                        "-keyout", str(directory / "auth/key.pem"), "-out", str(directory / "auth/cert.pem")], check=True, capture_output=True)
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        base = f"http://127.0.0.1:{port}"
        compose_file = directory / "compose.registry.yml"
        compose_file.write_text(f"""services:
  registry:
    image: {REGISTRY_IMAGE}
    user: '{fixture_user}'
    mem_limit: 512m
    cpus: 1
    pids_limit: 128
    environment:
      REGISTRY_LOG_LEVEL: warn
      REGISTRY_HTTP_ADDR: 0.0.0.0:5000
      REGISTRY_HTTP_RELATIVEURLS: 'true'
      REGISTRY_HTTP_SECRET: {secrets.token_hex(32)}
      REGISTRY_STORAGE_DELETE_ENABLED: 'true'
      REGISTRY_AUTH: token
      REGISTRY_AUTH_TOKEN_REALM: {base}/api/registry/token
      REGISTRY_AUTH_TOKEN_SERVICE: minicloud-registry
      REGISTRY_AUTH_TOKEN_ISSUER: minicloud-registry
      REGISTRY_AUTH_TOKEN_ROOTCERTBUNDLE: /auth/cert.pem
      OTEL_TRACES_EXPORTER: none
    volumes:
      - {temp}/data:/var/lib/registry
      - {temp}/auth/cert.pem:/auth/cert.pem:ro
  api:
    ports: ['127.0.0.1:{port}:8080']
    environment:
      Registry__Enabled: 'true'
      Registry__AllowInsecureLoopback: 'true'
      Registry__PublicUrl: {base}
      Registry__BackendUrl: http://registry:5000
      Registry__CertificatePath: /auth/cert.pem
      Registry__KeyPath: /auth/key.pem
      Registry__StoragePath: /registry-data
      Registry__PlannedPlatforms__Hetzner_cx23: linux/amd64
    volumes:
      - {temp}/auth:/auth:ro
      - {temp}/data:/registry-data:ro
networks:
  default:
    internal: false
""")

        def compose(*args, timeout=120, check=True):
            return component.run("docker", "compose", "-p", component.PROJECT, "-f", str(component.COMPOSE), "-f", str(compose_file),
                                 *args, timeout=timeout, check=check)

        extras = []

        def sql(statement):
            return compose("exec", "-T", "postgres", "psql", "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-U", "minicloud", "-d", "minicloud_test", "-c", statement).stdout.strip()

        def request(path, method="GET", body=None, token=None, headers=None):
            values = dict(headers or {})
            if token:
                values["Authorization"] = "Bearer " + token
            if body is not None:
                values["Content-Type"] = "application/json"
            req = urllib.request.Request(base + path, data=json.dumps(body).encode() if body is not None else None, headers=values, method=method)
            try:
                with urllib.request.urlopen(req, timeout=120) as response:
                    payload = response.read()
                    return response.status, json.loads(payload) if payload else None
            except urllib.error.HTTPError as error:
                payload = error.read()
                return error.code, json.loads(payload) if payload else None

        def seed(suffix):
            organization, app = "org_" + suffix, "app_" + suffix
            secret = "mc_live_" + suffix + "_" + secrets.token_hex(32)
            prefix = "mc_live_" + suffix
            salt = secrets.token_bytes(16)
            hashed = "pbkdf2_sha256$210000$" + base64.b64encode(salt).decode() + "$" + base64.b64encode(hashlib.pbkdf2_hmac("sha256", secret.encode(), salt, 210000)).decode()
            sql(f"INSERT INTO organizations(id,name,slug,created_at,updated_at) VALUES('{organization}','Registry test','{organization}',now(),now()); "
                f"INSERT INTO applications(id,organization_id,name,slug,normalized_slug,default_environment_id,created_at,updated_at) VALUES('application_{app}','{organization}','Registry test','{app}','{app}','{app}',now(),now()); "
                f"INSERT INTO apps(id,organization_id,application_id,name,slug,plan,database,environment_name,normalized_environment_name,environment_kind,created_at,updated_at) VALUES('{app}','{organization}','application_{app}','Registry test','{app}','P0','None','production','production','persistent',now(),now()); "
                f"INSERT INTO api_keys(id,organization_id,name,key_prefix,key_hash,scopes_json,created_at,updated_at) VALUES('key_{suffix}','{organization}','Registry test','{prefix}','{hashed}','[\"apps:read\",\"deployments:create\",\"deployments:read\"]',now(),now());")
            return app, secret

        try:
            compose("build", "api", timeout=1200)
            compose("up", "-d", "registry", "api", timeout=180)
            component.until(lambda: subprocess.run(["curl", "--silent", "--fail", base + "/health"], capture_output=True).returncode == 0, 120, "API startup")
            print("Registry fixture storage (production reserve enforced):\n" +
                  compose("exec", "-T", "api", "df", "-k", "/registry-data").stdout, file=sys.stderr)
            app, token = seed(component.RUN_ID)
            foreign_app, foreign_token = seed("f" + component.RUN_ID)
            status, target = request(f"/api/environments/{app}/image-build-target", token=token)
            assert status == 200, (status, target)
            assert request(f"/api/environments/{app}/image-build-target", token=foreign_token)[0] == 403
            # Real PostgreSQL app-row locking serializes competing CLI reservations.
            preparation_app, preparation_token = seed("p" + component.RUN_ID)
            starts = [{"appId": preparation_app, "idempotencyKey": str(__import__("uuid").uuid4()), "serviceNames": ["api"]} for _ in range(2)]
            with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
                reservations = list(pool.map(lambda body: request("/api/deployments/preparations", "POST", body, preparation_token), starts))
            assert sorted(item[0] for item in reservations) == [201, 409], reservations
            winner = next(index for index, item in enumerate(reservations) if item[0] == 201)
            reserved = reservations[winner][1]
            assert reserved["status"] == "waiting_for_build"
            assert request("/api/deployments/preparations", "POST", starts[winner], preparation_token)[1]["id"] == reserved["id"]
            assert request("/api/deployments/preparations/" + reserved["id"], "POST", {"appId": preparation_app, "status": "heartbeat"}, preparation_token)[0] == 204
            assert request("/api/deployments/preparations/" + reserved["id"], "POST", {"appId": preparation_app, "status": "failed"}, foreign_token)[0] == 403
            admission = {"appId": preparation_app, "database": "none", "preparationId": reserved["id"],
                         "services": [{"name": "api", "image": "busybox:1.37", "port": 8080, "public": True, "path": "/", "healthPath": "/"}]}
            status, prepared = request("/api/deployments", "POST", admission, preparation_token)
            assert status == 201 and prepared["id"] == reserved["id"], (status, prepared)
            assert request("/api/deployments/preparations/" + reserved["id"], "POST", {"appId": preparation_app, "status": "failed"}, preparation_token)[0] == 409
            assert sql(f"SELECT count(*) FROM deployments WHERE app_id='{preparation_app}'") == "1"
            original_config = Path(os.environ.get("DOCKER_CONFIG", Path.home() / ".docker"))
            docker_config = directory / "docker"
            docker_config.mkdir()
            if (original_config / "contexts").exists():
                shutil.copytree(original_config / "contexts", docker_config / "contexts")
            config = json.loads((original_config / "config.json").read_text()) if (original_config / "config.json").exists() else {}
            config.pop("credsStore", None)
            config.setdefault("cliPluginsExtraDirs", []).append(str(original_config / "cli-plugins"))
            build_env = component.ENV.copy()
            build_env["DOCKER_CONFIG"] = str(docker_config)
            build_env["BUILDX_CONFIG"] = str(original_config / "buildx")
            source = directory / "service"
            source.mkdir()
            (source / "Dockerfile").write_text('FROM busybox:1.37\nWORKDIR /www\nCOPY index.html .\nEXPOSE 8080\nCMD ["httpd","-f","-p","8080"]\n')
            (source / "index.html").write_text("registry smoke\n")
            evidence = []
            for number in range(3):
                if number == 2:
                    (source / "index.html").write_text("expired orphan smoke\n")
                status, publication = request(f"/api/environments/{app}/image-publications", "POST", {
                    "serviceName": "api", "inputSha256": hashlib.sha256(b"registry-smoke").hexdigest(), "target": target, "idempotencyKey": __import__("uuid").uuid4().__str__()}, token)
                assert status == 201, (status, publication)
                identity = publication["id"]
                status, credentials = request(f"/api/environments/{app}/image-publications/{identity}/credentials", "POST", token=token)
                assert status == 200, (status, credentials)
                config.setdefault("auths", {})[credentials["registry"]] = {"auth": base64.b64encode((credentials["username"] + ":" + credentials["password"]).encode()).decode()}
                config.setdefault("credHelpers", {}).pop(credentials["registry"], None)
                (docker_config / "config.json").write_text(json.dumps(config))
                metadata = directory / f"metadata-{number}.json"
                image = credentials["registry"] + "/" + publication["repository"] + ":" + publication["candidateTag"]
                started = time.monotonic()
                result = subprocess.run(["docker", "buildx", "build", "--platform", target["platform"], "--provenance=false", "--sbom=false", "--push", "--progress", "plain", "--metadata-file", str(metadata), "--tag", image, str(source)], cwd=component.ROOT,
                                        env=build_env, text=True, capture_output=True, timeout=300)
                assert result.returncode == 0, result.stderr[-6000:].replace(credentials["password"], "[redacted]")
                digest = json.loads(metadata.read_text())["containerimage.digest"]
                if number == 2:
                    scope = urllib.parse.urlencode({"service": "minicloud-registry", "scope": "repository:" + publication["repository"] + ":pull,push"})
                    status, bearer = request("/api/registry/token?" + scope, headers={"Authorization": "Basic " + base64.b64encode((credentials["username"] + ":" + credentials["password"]).encode()).decode()})
                    assert status == 200
                    status, orphan_manifest = request("/v2/" + publication["repository"] + "/manifests/" + digest, token=bearer["token"], headers={"Accept": "application/vnd.oci.image.manifest.v1+json"})
                    assert status == 200, (status, orphan_manifest)
                    orphan_digest = digest
                    orphan_layer = orphan_manifest["layers"][-1]["digest"]
                status, ready = request(f"/api/environments/{app}/image-publications/{identity}/complete", "POST", {"manifestDigest": digest}, token)
                assert status == 200, (status, ready)
                assert request(f"/api/environments/{app}/image-publications/{identity}", token=foreign_token)[0] == 403
                charged = int(sql(f"SELECT COALESCE(sum(charged_bytes),0) FROM registry_sessions WHERE image_publication_id='{identity}'"))
                evidence.append({"digest": digest, "sizeBytes": ready["sizeBytes"], "uploadedBytes": charged, "seconds": round(time.monotonic() - started, 2)})
                if number == 1:
                    retained_publication, retained_identity = publication, identity
                if number == 2:
                    sql(f"UPDATE image_publications SET expires_at=now()-interval '1 hour' WHERE id='{identity}'")
            publication, identity = retained_publication, retained_identity
            (source / "index.html").write_text("registry smoke\n")
            assert evidence[1]["uploadedBytes"] < evidence[0]["uploadedBytes"], evidence
            assert sql("SELECT count(*) FROM deployment_artifacts") == "0"
            status, queued = request("/api/deployments", "POST", {"appId": app, "database": "none", "services": [{"name": "api", "imagePublicationId": identity, "port": 8080, "public": True, "path": "/", "healthPath": "/"}]}, token)
            assert status == 201, (status, queued)
            assert sql(f"SELECT image FROM deployment_services WHERE deployment_id='{queued['id']}'").endswith("@" + evidence[1]["digest"])
            assert request(f"/api/deployments/{queued['id']}/dispatch", "POST", token=token)[0] == 200
            sql(f"UPDATE deployments SET status='Canceled',completed_at=now() WHERE id='{queued['id']}'")
            # Keep the real build visible long enough to observe waiting before queue admission.
            with (source / "Dockerfile").open("a") as build_file:
                build_file.write("RUN sleep 3\n")
            component.run("dotnet", "build", "modules/cli/Minicloud.Cli.csproj", "--no-restore", "-v", "quiet", "-m:1", "-nr:false", timeout=180)
            customer_config = directory / "minicloud.yml"
            customer_config.write_text(f"""app: {app}
appId: application_{app}
database: none
buildJobs: 3
services:
""" + "".join(f"""  {name}:
    sourcePath: {source}
    buildMode: local
    port: 8080
    public: {str(name == "api").lower()}
    path: /
    healthPath: /
""" for name in ("api", "worker", "admin")))
            cli_env = component.ENV.copy()
            cli_env["MINICLOUD_API_URL"] = base
            cli_env["MINICLOUD_TOKEN"] = token
            cli = subprocess.Popen(["dotnet", "modules/cli/bin/Debug/net10.0/Minicloud.Cli.dll", "deploy", "all", "--config", str(customer_config)],
                                   cwd=component.ROOT, env=cli_env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 120
                waiting_id = None
                while time.monotonic() < deadline and cli.poll() is None:
                    waiting = sql(f"SELECT id FROM deployments WHERE app_id='{app}' AND status='WaitingForBuild'")
                    if waiting:
                        waiting_id = waiting
                    if int(sql(f"SELECT count(*) FROM deployment_services WHERE deployment_id IN (SELECT id FROM deployments WHERE app_id='{app}' AND status='Queued')")) == 3:
                        break
                    time.sleep(0.2)
                else:
                    raise AssertionError("CLI did not publish and enqueue its managed image: " + (cli.communicate(timeout=5)[0] if cli.poll() is not None else "timeout"))
                assert waiting_id, "Local CLI build never exposed waiting_for_build"
                assert sql(f"SELECT status FROM deployments WHERE id='{waiting_id}'") == "Queued"
                assert sql(f"SELECT count(*) FROM deployments WHERE app_id='{app}'") == "2"
                assert sql(f"SELECT count(*) FROM image_publications p JOIN deployment_services s ON s.image_publication_id=p.id JOIN deployments d ON d.id=s.deployment_id WHERE d.id='{waiting_id}' AND p.created_at >= d.created_at") == "3"
                cli.send_signal(signal.SIGINT)
                output = cli.communicate(timeout=20)[0]
                assert sql(f"SELECT status FROM deployments WHERE id='{waiting_id}'") == "Queued"
                assert "Images: 3/3 ready" in output and "Build logs:" in output, output
                build_starts = [output.index(f"[{name}] Building image") for name in ("api", "worker", "admin")]
                build_ends = [output.index(f"[{name}] Image built") for name in ("api", "worker", "admin")]
                upload_starts = [output.index(f"[{name}] Uploading image") for name in ("api", "worker", "admin")]
                assert max(build_starts) < min(build_ends) and max(build_ends) < min(upload_starts), output
                assert sql("SELECT count(*) FROM deployment_artifacts") == "0"
                assert sql(f"SELECT count(*) FROM deployment_services WHERE deployment_id IN (SELECT id FROM deployments WHERE app_id='{app}') AND image_publication_id IS NOT NULL") == "4"
            finally:
                if cli.poll() is None:
                    cli.kill()
                    cli.communicate()

            # Simulate only the worker's durable credential issuance; exercise real Docker pull/run.
            pull_password = secrets.token_hex(32)
            pull_session = "rs_" + component.RUN_ID
            sql(f"INSERT INTO server_instances(id,organization_id,app_id,provider,provider_server_id,region,status,bootstrap_status,created_at,updated_at) VALUES('srv_{component.RUN_ID}','org_{component.RUN_ID}','{app}','Hetzner','fixture','nbg1','running','ready',now(),now()); "
                f"UPDATE deployments SET status='Deploying', worker_id='registry-smoke', ownership_token='registry-smoke-owner', started_at=now() WHERE id='{queued['id']}'; "
                f"INSERT INTO registry_sessions(id,organization_id,app_id,image_publication_id,deployment_id,subject_id,subject_kind,kind,credential_hash,reserved_bytes,charged_bytes,revision,created_at,expires_at,ownership_token,server_id) "
                f"VALUES('{pull_session}','org_{component.RUN_ID}','{app}','{identity}','{queued['id']}','registry-smoke','worker','Pull','{hashlib.sha256(pull_password.encode()).hexdigest()}',0,0,0,now(),now()+interval '30 minutes','registry-smoke-owner','srv_{component.RUN_ID}');")
            config["auths"][f"127.0.0.1:{port}"] = {"auth": base64.b64encode((pull_session + ":" + pull_password).encode()).decode()}
            (docker_config / "config.json").write_text(json.dumps(config))
            canonical = f"127.0.0.1:{port}/" + publication["repository"] + "@" + evidence[1]["digest"]
            payload = directory / "managed.json"
            payload.write_text(json.dumps({"managed_images": [{"name": "api", "image": canonical, "platform": target["platform"], "registry": f"127.0.0.1:{port}", "username": pull_session, "password": pull_password}]}))
            runtime_source = (component.ROOT / "scripts/github-actions/minicloud-deploy/remote-deploy.sh").read_text()
            prepare = runtime_source[runtime_source.index("prepare_managed_images() ("):runtime_source.index("\nwait_for_rollout_gate()", runtime_source.index("prepare_managed_images() ("))]
            fixture = directory / "prepare.sh"
            fixture.write_text('set -Eeuo pipefail\nphase() { :; }\nworker_lease_valid() { return 0; }\nuname() { echo x86_64; }\ntimeout() { shift; "$@"; }\n' + prepare + '\nprepare_managed_images\n')
            runtime_env = build_env.copy()
            runtime_env.update({"PAYLOAD_FILE": str(payload), "APP_DIR": str(directory)})
            pulled = subprocess.run(["bash", str(fixture)], env=runtime_env, text=True, capture_output=True, timeout=120)
            assert pulled.returncode == 0, pulled.stderr.replace(pull_password, "[redacted]")
            assert not list(directory.glob("registry-auth.*"))
            image_container = component.PROJECT + "-image"
            extras.append(image_container)
            component.run("docker", "run", "-d", "--rm", "--platform", target["platform"], "--name", image_container, "-p", "127.0.0.1::8080", canonical)
            image_port = component.run("docker", "port", image_container, "8080").stdout.strip().split(":")[-1]
            component.until(lambda: subprocess.run(["curl", "-fsS", f"http://127.0.0.1:{image_port}/"], capture_output=True).returncode == 0, 30, "image HTTP startup")
            assert subprocess.check_output(["curl", "-fsS", f"http://127.0.0.1:{image_port}/"]).decode() == "registry smoke\n"

            scope = urllib.parse.urlencode({"service": "minicloud-registry", "scope": "repository:" + publication["repository"] + ":pull"})
            basic = "Basic " + base64.b64encode((pull_session + ":" + pull_password).encode()).decode()
            status, pull_token = request("/api/registry/token?" + scope, headers={"Authorization": basic})
            assert status == 200
            assert request("/v2/" + publication["repository"] + "/manifests/" + orphan_digest, token=pull_token["token"])[0] == 403
            assert request("/v2/org/org_foreign/app/app_foreign/service/api/manifests/" + evidence[1]["digest"], token=pull_token["token"])[0] == 403
            sql(f"UPDATE deployments SET ownership_token='changed-owner' WHERE id='{queued['id']}'")
            assert request("/api/registry/token?" + scope, headers={"Authorization": basic})[0] == 401
            sql(f"UPDATE deployments SET ownership_token='registry-smoke-owner' WHERE id='{queued['id']}'; UPDATE registry_sessions SET server_id=NULL WHERE id='{pull_session}'")
            assert request("/api/registry/token?" + scope, headers={"Authorization": basic})[0] == 401
            sql(f"UPDATE registry_sessions SET server_id='srv_{component.RUN_ID}' WHERE id='{pull_session}'")

            assert request(f"/api/deployments/{queued['id']}/dispatch", "POST", token=token)[0] == 409

            operator = hashlib.sha256((directory / "auth/key.pem").read_bytes()).hexdigest()
            assert request("/internal/registry/maintenance", "POST", {"action": "begin"}, token)[0] == 401
            status, state = request("/internal/registry/maintenance", "POST", {"action": "begin"}, headers={"X-Minicloud-Registry-Operator": operator})
            assert status == 200, (status, state)
            assert request(f"/api/environments/{app}/image-publications", "POST", {"serviceName": "api", "inputSha256": "b"*64, "target": target, "idempotencyKey": str(__import__("uuid").uuid4())}, token)[0] == 503
            # Snapshot immutable registry bytes and a transactionally consistent control-plane dump.
            snapshot = directory / "restore-data"
            shutil.copytree(directory / "data", snapshot)
            dump = compose("exec", "-T", "postgres", "pg_dump", "--no-owner", "--no-acl", "-U", "minicloud", "-d", "minicloud_test").stdout
            compose("exec", "-T", "postgres", "createdb", "-U", "minicloud", "registry_restore")
            restored = subprocess.run(["docker", "compose", "-p", component.PROJECT, "-f", str(component.COMPOSE), "-f", str(compose_file),
                                       "exec", "-T", "postgres", "psql", "-v", "ON_ERROR_STOP=1", "-U", "minicloud", "-d", "registry_restore"],
                                      input=dump, env=component.ENV, cwd=component.ROOT, text=True, capture_output=True, timeout=120)
            assert restored.returncode == 0, "Isolated metadata restore failed: " + restored.stderr[-1000:]
            with socket.socket() as sock:
                sock.bind(("127.0.0.1", 0))
                restore_port = sock.getsockname()[1]
            restore_registry = component.PROJECT + "-restore-registry"
            restore_api = component.PROJECT + "-restore-api"
            extras.extend([restore_registry, restore_api])
            component.run("docker", "run", "-d", "--rm", "--name", restore_registry, "--network", component.PROJECT + "_default",
                          "--user", fixture_user,
                          "-v", str(snapshot) + ":/var/lib/registry", "-v", str(directory / "auth/cert.pem") + ":/auth/cert.pem:ro",
                          "-e", "REGISTRY_AUTH=token", "-e", f"REGISTRY_AUTH_TOKEN_REALM=http://127.0.0.1:{restore_port}/api/registry/token",
                          "-e", "REGISTRY_AUTH_TOKEN_SERVICE=minicloud-registry", "-e", "REGISTRY_AUTH_TOKEN_ISSUER=minicloud-registry", "-e", "REGISTRY_AUTH_TOKEN_ROOTCERTBUNDLE=/auth/cert.pem",
                          "-e", "OTEL_TRACES_EXPORTER=none", REGISTRY_IMAGE)
            api_container = compose("ps", "-q", "api").stdout.strip()
            api_image = component.run("docker", "inspect", api_container, "--format", "{{.Config.Image}}").stdout.strip()
            restore_env = {"ASPNETCORE_ENVIRONMENT": "Production", "Database__Provider": "Postgres", "Database__RunMigrationsOnStartup": "false",
                           "Firebase__ProjectId": "minicloud-test",
                           "ConnectionStrings__Minicloud": "Host=postgres;Database=registry_restore;Username=minicloud;Password=" + component.ENV["TEST_POSTGRES_PASSWORD"],
                           "Registry__Enabled": "true", "Registry__AllowInsecureLoopback": "true", "Registry__PublicUrl": f"http://127.0.0.1:{restore_port}",
                           "Registry__BackendUrl": f"http://{restore_registry}:5000", "Registry__CertificatePath": "/auth/cert.pem", "Registry__KeyPath": "/auth/key.pem", "Registry__StoragePath": "/registry-data"}
            env_file = directory / "restore.env"
            env_file.write_text("\n".join(key + "=" + value for key, value in restore_env.items()))
            component.run("docker", "run", "-d", "--rm", "--name", restore_api, "--network", component.PROJECT + "_default", "--env-file", str(env_file),
                          "-p", f"127.0.0.1:{restore_port}:8080", "-v", str(directory / "auth") + ":/auth:ro", "-v", str(snapshot) + ":/registry-data:ro", api_image)
            component.until(lambda: subprocess.run(["curl", "-fsS", f"http://127.0.0.1:{restore_port}/health"], capture_output=True).returncode == 0, 60, "restored API startup")
            query = urllib.parse.urlencode({"service": "minicloud-registry", "scope": "repository:" + publication["repository"] + ":pull"})
            basic = base64.b64encode((pull_session + ":" + pull_password).encode()).decode()
            auth_request = urllib.request.Request(f"http://127.0.0.1:{restore_port}/api/registry/token?" + query, headers={"Authorization": "Basic " + basic})
            with urllib.request.urlopen(auth_request, timeout=30) as response:
                restored_token = json.load(response)["token"]
            manifest_request = urllib.request.Request(f"http://127.0.0.1:{restore_port}/v2/" + publication["repository"] + "/manifests/" + evidence[1]["digest"], headers={"Authorization": "Bearer " + restored_token, "Accept": "application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json"})
            with urllib.request.urlopen(manifest_request, timeout=30) as response:
                manifest_bytes = response.read()
            assert "sha256:" + hashlib.sha256(manifest_bytes).hexdigest() == evidence[1]["digest"]
            manifest = json.loads(manifest_bytes)
            for blob in manifest["layers"] + [manifest["config"]]:
                blob_request = urllib.request.Request(f"http://127.0.0.1:{restore_port}/v2/" + publication["repository"] + "/blobs/" + blob["digest"], headers={"Authorization": "Bearer " + restored_token})
                digest = hashlib.sha256()
                with urllib.request.urlopen(blob_request, timeout=30) as response:
                    while chunk := response.read(65536):
                        digest.update(chunk)
                assert "sha256:" + digest.hexdigest() == blob["digest"]
            assert request("/internal/registry/maintenance", "POST", {"action": "sweep", "ownerToken": state["ownerToken"]}, headers={"X-Minicloud-Registry-Operator": operator})[0] == 200
            registry_container = compose("ps", "-q", "registry").stdout.strip()
            compose("stop", "registry")
            gc = component.run("docker", "run", "--rm", "--network", "none", "--user", fixture_user, "--volumes-from", registry_container, "--entrypoint", "/bin/registry", REGISTRY_IMAGE,
                               "garbage-collect", "/etc/distribution/config.yml", timeout=120)
            compose("start", "registry")
            component.until(lambda: request("/internal/registry/maintenance", "POST", {"action": "finish", "ownerToken": state["ownerToken"]}, headers={"X-Minicloud-Registry-Operator": operator})[0] == 200, 30, "registry GC recovery")
            assert sql("SELECT count(*) FROM image_publications WHERE retired_at IS NOT NULL") == "1"
            # An authorized push session can check repository blobs after GC; the retired graph cannot.
            sql(f"UPDATE server_instances SET build_platform='linux/amd64', build_platform_observed_at=now() WHERE id='srv_{component.RUN_ID}'")
            status, target = request(f"/api/environments/{app}/image-build-target", token=token)
            assert status == 200
            status, candidate = request(f"/api/environments/{app}/image-publications", "POST", {"serviceName": "api", "inputSha256": "c"*64, "target": target, "idempotencyKey": str(__import__("uuid").uuid4())}, token)
            assert status == 201
            status, probe = request(f"/api/environments/{app}/image-publications/{candidate['id']}/credentials", "POST", token=token)
            assert status == 200
            status, probe_token = request("/api/registry/token?" + scope, headers={"Authorization": "Basic " + base64.b64encode((probe["username"] + ":" + probe["password"]).encode()).decode()})
            assert status == 200
            assert request("/v2/" + publication["repository"] + "/blobs/" + orphan_layer, "HEAD", token=probe_token["token"])[0] == 404
            assert request("/v2/" + publication["repository"] + "/manifests/" + evidence[1]["digest"], token=probe_token["token"], headers={"Accept": "application/vnd.oci.image.manifest.v1+json"})[0] == 200
            assert request(f"/api/environments/{app}/image-publications/{candidate['id']}/credentials", "POST", token=token)[0] == 200
            assert request(f"/api/environments/{app}/image-publications/{candidate['id']}/credentials", "POST", token=token)[0] == 429
            stats = component.run("docker", "stats", "--no-stream", "--format", "{{.MemUsage}}", registry_container).stdout.strip()
            print(json.dumps({"registryImage": REGISTRY_IMAGE, "status": "passed", "pushes": evidence, "sourceZipCount": 0, "deploymentAdmission": "digest-pinned", "cliLocalPublication": "passed; monitoring canceled after queue admission", "localBuildPreparation": "visible_before_build_same_record_admission_and_concurrent_exclusion", "dockerPullAndHttp": "passed", "runtimeImagePreparation": "passed; fixture lease and host architecture", "tenantIsolation": "passed", "isolatedMetadataAndBlobRestore": "passed", "fencedGc": "passed", "expiredOrphanCollected": "passed", "workerAndServerScope": "passed", "uploadSessionQuota": "passed", "managedRedispatch": "passed", "registryMemory": stats}))
        except Exception:
            for name in extras:
                logs = component.run("docker", "logs", "--tail", "25", name, check=False)
                print(logs.stdout[-3000:] + logs.stderr[-3000:], file=__import__("sys").stderr)
            print(compose("logs", "--tail", "80", "registry", "api", check=False).stdout[-10000:], file=__import__("sys").stderr)
            raise
        finally:
            for name in reversed(extras):
                component.run("docker", "rm", "-f", name, check=False)
            cleanup = compose("down", "--volumes", "--remove-orphans", "--rmi", "local", timeout=180, check=False)
            images_gone = all(component.run("docker", "image", "inspect", f"{component.PROJECT}-{service}:latest",
                                           check=False).returncode != 0 for service in ("api", "worker"))
            if cleanup.returncode != 0 or not images_gone:
                raise RuntimeError("isolated registry Docker cleanup failed")


if __name__ == "__main__":
    main()
