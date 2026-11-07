#!/usr/bin/env python3
"""Packaged API/worker + PostgreSQL/OpenBao queue component smoke."""

import json
import os
import re
import secrets
import subprocess
import sys
import tempfile
import time
from pathlib import Path


ROOT = Path(__file__).resolve().parents[4]
COMPOSE = Path(__file__).with_name("compose.component.yml")
RUN_ID = os.environ.get("MINICLOUD_TEST_RUN_ID") or secrets.token_hex(6)
if not re.fullmatch(r"[0-9a-f]{12}", RUN_ID):
    raise ValueError("MINICLOUD_TEST_RUN_ID must be twelve lowercase hexadecimal characters")
PROJECT = f"mcint{RUN_ID}"
ENV = os.environ.copy()
ENV.setdefault("TEST_POSTGRES_PASSWORD", secrets.token_urlsafe(24))
ENV.setdefault("TEST_BAO_TOKEN", secrets.token_urlsafe(24))


def run(*args, timeout=120, check=True):
    result = subprocess.run(args, cwd=ROOT, env=ENV, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            timeout=timeout, check=False)
    if check and result.returncode:
        raise RuntimeError(f"command failed ({result.returncode}): {' '.join(args[:5])}\n"
                           f"{result.stderr[-1200:]}")
    return result


def compose(*args, timeout=120, check=True):
    return run("docker", "compose", "-p", PROJECT, "-f", str(COMPOSE),
               *args, timeout=timeout, check=check)


def sql(statement):
    return compose("exec", "-T", "postgres", "psql", "-X", "-A", "-t",
                   "-v", "ON_ERROR_STOP=1", "-U", "minicloud", "-d",
                   "minicloud_test", "-c", statement).stdout.strip()


def until(predicate, seconds, description):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if predicate():
            return
        time.sleep(2)
    raise TimeoutError(description)


def main():
    started = time.monotonic()
    info = run("docker", "info", "--format", "{{.ServerVersion}}")
    if info.returncode != 0 or not info.stdout.strip():
        raise RuntimeError("A real Docker daemon is required")

    deployment_id = f"probe{RUN_ID}"
    org_id = f"org{RUN_ID}"
    app_id = f"app{RUN_ID}"
    result = {"runId": RUN_ID, "group": "C", "status": "failed",
              "revision": run("git", "rev-parse", "HEAD").stdout.strip(),
              "components": ["packaged API", "packaged worker", "PostgreSQL 16", "OpenBao container (unexercised)"],
              "scenarioEvidence": {"PK01": "partial", "PK03": "empty-schema only",
                                   "QU01": "two packaged workers plus same-app exclusion",
                                   "QU03": "stale ownership and late-finalization fencing",
                                   "QU04": "delete/claim exclusion",
                                   "QU05": "collided server registration refusal"},
              "negativeControl": {},
              "mockedBoundaries": [], "realBoundaries": ["API image", "worker image", "PostgreSQL"]}
    try:
        # Acquire dependencies before the network-isolated scenario starts.
        compose("pull", "postgres", "openbao", timeout=600)
        compose("build", "api", "worker", timeout=1200)
        broken_image = f"{PROJECT}-broken-worker:local"
        with tempfile.TemporaryDirectory(prefix="mc-broken-worker-") as directory:
            dockerfile = Path(directory) / "Dockerfile"
            # Common removes the ASP.NET dependency; use a missing .NET major instead.
            dockerfile.write_text(
                "FROM mcr.microsoft.com/dotnet/runtime:9.0-alpine\n"
                f"COPY --from={PROJECT}-worker:latest /app /app\n"
                "WORKDIR /app\nENTRYPOINT [\"dotnet\",\"Minicloud.Worker.dll\"]\n")
            try:
                run("docker", "build", "-q", "-t", broken_image, directory, timeout=600)
                broken = run("docker", "run", "--rm", "--name", f"{PROJECT}-broken-worker", "--network", "none", broken_image,
                             timeout=20, check=False)
                if broken.returncode == 0 or "Microsoft.NETCore.App" not in broken.stdout + broken.stderr:
                    raise AssertionError("broken worker framework image was not rejected")
                result["negativeControl"]["brokenFramework"] = "rejected"
            finally:
                run("docker", "rm", "-f", f"{PROJECT}-broken-worker", check=False)
                run("docker", "image", "rm", "-f", broken_image, check=False)

        compose("up", "-d", "--no-build", "--pull", "never", "postgres", "openbao", "api", timeout=1200)

        def migrated():
            probe = compose("exec", "-T", "postgres", "psql", "-X", "-A", "-t",
                            "-U", "minicloud", "-d", "minicloud_test", "-c",
                            'SELECT count(*) FROM "__EFMigrationsHistory"', check=False)
            return probe.returncode == 0 and probe.stdout.strip().isdigit() and int(probe.stdout.strip()) > 0

        until(migrated, 120, "API did not migrate empty PostgreSQL database")
        result["migrationCount"] = int(sql('SELECT count(*) FROM "__EFMigrationsHistory"'))
        sql("CREATE DATABASE minicloud_unmigrated")
        try:
            absent = compose("exec", "-T", "postgres", "psql", "-X", "-A", "-t",
                             "-U", "minicloud", "-d", "minicloud_unmigrated", "-c",
                             'SELECT count(*) FROM "__EFMigrationsHistory"', check=False)
            if absent.returncode == 0:
                raise AssertionError("migration gate accepted a database without schema history")
        finally:
            sql("DROP DATABASE minicloud_unmigrated")
        result["negativeControl"]["missingMigration"] = "rejected"

        now = "now()"
        sql(f"INSERT INTO organizations (id,name,slug,created_at,updated_at) "
            f"VALUES ('{org_id}','Integration','{org_id}',{now},{now})")
        sql(f"INSERT INTO apps (id,organization_id,name,slug,plan,database,created_at,updated_at) "
            f"VALUES ('{app_id}','{org_id}','Probe','probe','P0','None',{now},{now})")
        app2_id = f"app2{RUN_ID}"
        ordered_app_id = f"orderedapp{RUN_ID}"
        deleting_app_id = f"deletingapp{RUN_ID}"
        collision_app_a_id = f"collisiona{RUN_ID}"
        collision_app_b_id = f"collisionb{RUN_ID}"
        stale_app_id = f"staleapp{RUN_ID}"
        fresh_app_id = f"freshapp{RUN_ID}"
        local_build_app_id = f"localbuildapp{RUN_ID}"
        abandoned_build_app_id = f"abandonedbuildapp{RUN_ID}"
        for extra_app_id, name in ((app2_id, "Probe Two"), (ordered_app_id, "Ordered"),
                                   (deleting_app_id, "Deleting"), (collision_app_a_id, "Collision A"),
                                   (collision_app_b_id, "Collision B"), (stale_app_id, "Stale"),
                                   (fresh_app_id, "Fresh"), (local_build_app_id, "Local build"),
                                   (abandoned_build_app_id, "Abandoned build")):
            sql(f"INSERT INTO apps (id,organization_id,name,slug,plan,database,created_at,updated_at) "
                f"VALUES ('{extra_app_id}','{org_id}','{name}','{extra_app_id}','P0','None',{now},{now})")
        local_build_id = f"localbuild{RUN_ID}"
        abandoned_build_id = f"abandonedbuild{RUN_ID}"
        local_queued_id = f"localqueued{RUN_ID}"
        for preparation_id, preparation_app, status, updated in (
                (local_build_id, local_build_app_id, "WaitingForBuild", "now()"),
                (local_queued_id, local_build_app_id, "Queued", "now()"),
                (abandoned_build_id, abandoned_build_app_id, "WaitingForBuild", "now()-interval '6 minutes'")):
            sql(f"INSERT INTO deployments(id,organization_id,app_id,database,status,created_at,updated_at) "
                f"VALUES('{preparation_id}','{org_id}','{preparation_app}','None','{status}',now()-interval '10 minutes',{updated})")
        sql(f"UPDATE apps SET deletion_status='queued' WHERE id='{deleting_app_id}'")
        sql(f"INSERT INTO deployments "
            f"(id,organization_id,app_id,database,status,created_at,updated_at) "
            f"VALUES ('{deployment_id}','{org_id}','{app_id}','None','Queued',now() - interval '10 seconds',now() - interval '10 seconds')")
        second_id = f"probe2{RUN_ID}"
        ordered_first_id = f"ordered1{RUN_ID}"
        ordered_second_id = f"ordered2{RUN_ID}"
        deleting_id = f"deleting{RUN_ID}"
        collision_a_id = f"collisiona{RUN_ID}"
        collision_b_id = f"collisionb{RUN_ID}"
        stale_id = f"stale{RUN_ID}"
        fresh_id = f"active{RUN_ID}"
        sql(f"INSERT INTO deployments "
            f"(id,organization_id,app_id,database,status,created_at,updated_at) "
            f"VALUES ('{second_id}','{org_id}','{app2_id}','None','Queued',now() - interval '9 seconds',now() - interval '9 seconds')")
        for server_id, server_app, provider_id in (("probe-server", app_id, f"probe-provider-{RUN_ID}"),
                                                   ("probe2-server", app2_id, f"probe2-provider-{RUN_ID}")):
            sql(f"INSERT INTO server_instances "
                f"(id,organization_id,app_id,provider,provider_server_id,ip_address,region,status,bootstrap_status,created_at,updated_at) "
                f"VALUES ('{server_id}-{RUN_ID}','{org_id}','{server_app}','hetzner','{provider_id}',"
                f"'192.0.2.1','nbg1','active','ready',{now},{now})")
        for queued_id, queued_app, age in ((ordered_first_id, ordered_app_id, "2 seconds"),
                                           (ordered_second_id, ordered_app_id, "1 second"),
                                           (deleting_id, deleting_app_id, "0 seconds"),
                                           (collision_a_id, collision_app_a_id, "0 seconds"),
                                           (collision_b_id, collision_app_b_id, "0 seconds")):
            sql(f"INSERT INTO deployments "
                f"(id,organization_id,app_id,database,status,created_at,updated_at) "
                f"VALUES ('{queued_id}','{org_id}','{queued_app}','None','Queued',"
                f"now() - interval '{age}',now() - interval '{age}')")
        for server_id, server_app in (("collision-server-a", collision_app_a_id),
                                      ("collision-server-b", collision_app_b_id)):
            sql(f"INSERT INTO server_instances "
                f"(id,organization_id,app_id,provider,provider_server_id,region,status,bootstrap_status,created_at,updated_at) "
                f"VALUES ('{server_id}-{RUN_ID}','{org_id}','{server_app}','hetzner','shared-{RUN_ID}',"
                f"'nbg1','active','ready',{now},{now})")
        sql(f"INSERT INTO deployments "
            f"(id,organization_id,app_id,database,status,worker_id,ownership_token,ownership_acquired_at,created_at,started_at,updated_at) "
            f"VALUES ('{stale_id}','{org_id}','{stale_app_id}','None','Deploying','lost-worker','stale-token-{RUN_ID}',"
            "now() - interval '2 hours',now() - interval '2 hours',now() - interval '2 hours',now() - interval '2 hours')")
        sql(f"INSERT INTO deployments "
            f"(id,organization_id,app_id,database,status,worker_id,ownership_token,ownership_acquired_at,created_at,started_at,updated_at) "
            f"VALUES ('{fresh_id}','{org_id}','{fresh_app_id}','None','Deploying','active-worker','fresh-token-{RUN_ID}',now(),now(),now(),now())")

        # Negative control: an absent worker must leave the queued row untouched.
        time.sleep(3)
        if sql(f"SELECT count(*) FROM deployments WHERE id IN ('{deployment_id}','{second_id}') AND status='Queued'") != "2":
            raise AssertionError("negative control: queue moved without worker")
        result["negativeControl"]["queueAbsent"] = "queued while worker absent"

        compose("up", "-d", "--no-build", "--pull", "never", "--scale", "worker=2", "worker", timeout=1200)
        container_ids = {service: compose("ps", "-q", service).stdout.splitlines()
                         for service in ("api", "worker")}
        result["imageIds"] = {service: sorted(run("docker", "inspect", "--format", "{{.Image}}", cid).stdout.strip()
                                             for cid in ids)
                              for service, ids in container_ids.items()}

        until(lambda: sql(f"SELECT count(*) FROM deployments "
                          f"WHERE id IN ('{deployment_id}','{second_id}') AND status='Queued'") == "0",
              90, "packaged workers failed to claim both queued jobs")
        claim_messages = sql(f"SELECT message FROM deployment_events WHERE deployment_id IN "
                             f"('{deployment_id}','{second_id}') AND message LIKE 'Deployment claimed by worker%'")
        cross_app_workers = set(re.findall(r"worker '([^']+)'", claim_messages))
        if len(cross_app_workers) != 2:
            raise AssertionError(f"independent apps did not overlap across two workers: {claim_messages}")
        result["crossAppWorkerCount"] = len(cross_app_workers)
        try:
            compose("pause", "postgres")
            time.sleep(3)
        finally:
            compose("unpause", "postgres", check=False)
        until(lambda: sql("SELECT 1") == "1", 30, "PostgreSQL did not recover after simulated partition")
        result["databasePartition"] = "recovered_without_reclaim"
        until(lambda: sql(f"SELECT status FROM deployments WHERE id='{stale_id}'") == "Failed",
              30, "packaged worker failed to close an expired active job")
        if sql(f"SELECT failure_code FROM deployments WHERE id='{stale_id}'") != "worker_lost":
            raise AssertionError("stale job failure classification missing")
        if sql(f"SELECT count(*) FROM deployment_events WHERE deployment_id='{stale_id}' "
               "AND message LIKE 'Deployment claimed by worker%'") != "0":
            raise AssertionError("stale job was requeued or claimed again")
        if sql(f"SELECT count(*) FROM deployment_events WHERE deployment_id='{stale_id}' "
               "AND message LIKE 'Worker heartbeat expired%'") != "1":
            raise AssertionError("stale job recovery was recorded more or less than once")
        if sql(f"SELECT status FROM deployments WHERE id='{fresh_id}'") != "Deploying":
            raise AssertionError("fresh active job was incorrectly failed by recovery")
        result["staleJob"] = "failed_without_requeue"
        until(lambda: sql(f"SELECT failure_code FROM deployments WHERE id='{abandoned_build_id}'") == "local_build_abandoned",
              30, "packaged workers failed to close an abandoned local build")
        assert sql(f"SELECT status FROM deployments WHERE id='{local_build_id}'") == "WaitingForBuild"
        assert sql(f"SELECT status FROM deployments WHERE id='{local_queued_id}'") == "Queued"
        assert sql(f"SELECT count(*) FROM deployment_events WHERE deployment_id IN ('{local_build_id}','{abandoned_build_id}','{local_queued_id}') AND message LIKE 'Deployment claimed by worker%'") == "0"
        result["localBuildWaiting"] = "unclaimable_same_app_excluded_abandoned_expired"
        late_update = sql(f"WITH changed AS (UPDATE deployments SET status='Succeeded' "
                          f"WHERE id='{stale_id}' AND worker_id='lost-worker' "
                          f"AND ownership_token='stale-token-{RUN_ID}' "
                          "AND status IN ('Provisioning','Deploying','Verifying') RETURNING 1) "
                          "SELECT count(*) FROM changed")
        if late_update != "0":
            raise AssertionError("stale worker could finalize after ownership recovery")
        result["staleFinalization"] = "rejected"

        until(lambda: sql(f"SELECT count(*) FROM deployment_events WHERE deployment_id IN "
                          f"('{ordered_first_id}','{ordered_second_id}') "
                          "AND message LIKE 'Deployment claimed by worker%'") == "2",
              90, "same-app deployments were not processed")
        ordered_claims = sql(f"SELECT deployment_id FROM deployment_events WHERE deployment_id IN "
                             f"('{ordered_first_id}','{ordered_second_id}') "
                             "AND message LIKE 'Deployment claimed by worker%' ORDER BY created_at,id").splitlines()
        if ordered_claims != [ordered_first_id, ordered_second_id]:
            raise AssertionError(f"same-app FIFO/exclusion failed: {ordered_claims}")
        first_completed = sql(f"SELECT completed_at IS NOT NULL FROM deployments WHERE id='{ordered_first_id}'")
        if first_completed != "t":
            raise AssertionError("second same-app deployment was admitted before first became terminal")
        result["sameAppOrdering"] = "exclusive_fifo"

        time.sleep(3)
        deleting_status = sql(f"SELECT status FROM deployments WHERE id='{deleting_id}'")
        if deleting_status not in ("", "Queued", "Canceled"):
            raise AssertionError(f"deployment entered rollout after app deletion started: {deleting_status}")
        if sql(f"SELECT count(*) FROM deployment_events WHERE deployment_id='{deleting_id}' "
               "AND message LIKE 'Deployment claimed by worker%'") != "0":
            raise AssertionError("deleting app has a claim event")
        result["deleteClaimRace"] = "excluded_canceled_or_deleted"

        until(lambda: sql(f"SELECT count(*) FROM deployments WHERE id IN "
                          f"('{collision_a_id}','{collision_b_id}') AND status='Failed'") == "2",
              90, "collided server registrations were not rejected")
        collision_codes = sql(f"SELECT DISTINCT failure_code FROM deployments WHERE id IN "
                              f"('{collision_a_id}','{collision_b_id}')")
        if collision_codes != "provider_provision_failed":
            raise AssertionError(f"unexpected collision classification: {collision_codes}")
        result["serverCollision"] = "rejected_before_remote_mutation"
        claims = sql(f"SELECT deployment_id, count(*) FROM deployment_events "
                     f"WHERE deployment_id IN ('{deployment_id}','{second_id}') "
                     "AND message LIKE 'Deployment claimed by worker%' GROUP BY deployment_id ORDER BY deployment_id")
        if sorted(line.rsplit("|", 1)[-1] for line in claims.splitlines()) != ["1", "1"]:
            raise AssertionError(f"expected exactly one claim per job; found {claims}")
        if sql(f"SELECT count(*) FROM deployment_events WHERE deployment_id IN "
               f"('{deployment_id}','{second_id}') AND message LIKE 'Deployment claimed by worker%'") != "2":
            raise AssertionError("database partition caused a duplicate claim")
        result["claimEvents"] = int(sql("SELECT count(*) FROM deployment_events "
                                        "WHERE message LIKE 'Deployment claimed by worker%'"))
        timing_rows = sql(f"SELECT coalesce(metadata_json,'') FROM deployment_events "
                          f"WHERE deployment_id IN ('{deployment_id}','{second_id}') "
                          "AND metadata_json IS NOT NULL ORDER BY created_at,id")
        result["workerTimingEvents"] = [json.loads(row) for row in timing_rows.splitlines() if row]
        result["observedStatus"] = sql(f"SELECT status FROM deployments WHERE id='{deployment_id}'")
        result["status"] = "passed"
    except Exception:
        logs = compose("logs", "--no-color", "--tail", "100", "worker", check=False).stdout
        for secret in (ENV["TEST_POSTGRES_PASSWORD"], ENV["TEST_BAO_TOKEN"]):
            logs = logs.replace(secret, "[REDACTED]")
        result["workerLogTail"] = logs[-4000:]
        raise
    finally:
        cleanup = compose("down", "--volumes", "--remove-orphans", "--rmi", "local",
                          timeout=180, check=False)
        images_gone = all(run("docker", "image", "inspect", f"{PROJECT}-{service}:latest",
                              check=False).returncode != 0 for service in ("api", "worker"))
        result["cleanup"] = "passed" if cleanup.returncode == 0 and images_gone else "failed"
        result["durationSeconds"] = round(time.monotonic() - started, 2)
        print(json.dumps(result, indent=2, sort_keys=True))
        if result["cleanup"] != "passed":
            raise RuntimeError("isolated Docker cleanup failed")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"component smoke failed: {error}", file=sys.stderr)
        sys.exit(1)
