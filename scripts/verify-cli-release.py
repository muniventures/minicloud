#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Smoke-test a packaged CLI binary against a minimal authenticated API."""

import json
import os
import subprocess
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse


TOKEN = "release-smoke-token"


class Handler(BaseHTTPRequestHandler):
    requests = []

    def do_GET(self):
        parsed = urlparse(self.path)
        Handler.requests.append((parsed.path, parsed.query, self.headers.get("Authorization")))
        if parsed.path == "/v1/instance":
            self.respond({"product": "minicloud", "protocolVersion": 1, "appDomain": "apps.example.invalid"})
            return
        if self.headers.get("Authorization") != f"Bearer {TOKEN}":
            self.send_response(401)
            self.end_headers()
            return

        if parsed.path == "/v1/me":
            self.respond({
                "id": "release-smoke-user",
                "email": "release-smoke@example.invalid",
                "displayName": "Release Smoke",
                "organizations": [{
                    "id": "release-smoke-org",
                    "name": "Release Smoke",
                    "slug": "release-smoke",
                    "role": "owner",
                }],
            })
            return

        if parsed.path == "/v1/apps" and parsed.query == "organizationId=release-smoke-org":
            self.respond([{
                "id": "release-smoke-app",
                "organizationId": "release-smoke-org",
                "name": "Release Smoke",
                "slug": "release-smoke",
                "parentAppId": None,
                "branchName": None,
                "plan": "p0",
                "database": "none",
                "domains": [],
                "branches": [],
                "latestDeployment": None,
                "deletionStatus": None,
                "deletionError": None,
            }])
            return

        self.send_response(404)
        self.end_headers()

    def respond(self, body):
        payload = json.dumps(body).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, format, *args):
        return


def run(executable, *args, env=None):
    result = subprocess.run(
        [str(executable), *args],
        env=env,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=30,
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"{' '.join(args)} exited {result.returncode}:\n{result.stdout}\n{result.stderr}"
        )
    return result.stdout


def main():
    if len(sys.argv) != 3:
        raise SystemExit("usage: verify-cli-release.py <executable> <version>")

    executable = Path(sys.argv[1]).resolve()
    expected_version = sys.argv[2]
    if not executable.is_file():
        raise FileNotFoundError(executable)

    if expected_version not in run(executable, "--version"):
        raise AssertionError(f"--version did not report {expected_version}")
    if "minicloud" not in run(executable, "--help").lower():
        raise AssertionError("--help did not identify Minicloud")

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with tempfile.TemporaryDirectory(prefix="minicloud-release-smoke-") as config_home:
            environment = os.environ.copy()
            environment["MINICLOUD_API_URL"] = f"http://127.0.0.1:{server.server_port}"
            environment.pop("MINICLOUD_APP_DOMAIN", None)
            environment["MINICLOUD_TOKEN"] = TOKEN
            environment["XDG_CONFIG_HOME"] = config_home
            output = run(executable, "apps", "list", env=environment)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)

    if "release-smoke\tnone" not in output:
        raise AssertionError(f"apps list did not render the mocked app: {output}")
    expected_requests = [
        ("/v1/instance", "", None),
        ("/v1/me", "", f"Bearer {TOKEN}"),
        ("/v1/apps", "organizationId=release-smoke-org", f"Bearer {TOKEN}"),
    ]
    if Handler.requests != expected_requests:
        raise AssertionError(f"unexpected API requests: {Handler.requests}")

    print(f"release smoke passed for {executable.name} {expected_version}")


if __name__ == "__main__":
    main()
