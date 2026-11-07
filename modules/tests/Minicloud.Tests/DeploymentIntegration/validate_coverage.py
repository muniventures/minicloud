#!/usr/bin/env python3
"""Fail closed when matrix cases, variants, tiers, or verified run evidence are absent."""

import argparse
import json
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent


def required_keys():
    manifest = json.loads((HERE / "cases.json").read_text())
    matrix = (ROOT / manifest["source"]).read_text()
    rows = re.findall(r"^\| ([A-Z]{2}\d{2}) \| ([CRAS,]+) \|", matrix, re.M)
    actual = {case["id"]: case for case in manifest["cases"]}
    if len(actual) != len(manifest["cases"]) or len(rows) != 47:
        raise ValueError("scenario matrix must contain 47 unique cases")
    for case_id, tiers in rows:
        case = actual.get(case_id)
        if not case or case["tiers"] != tiers.split(",") or not case["variants"]:
            raise ValueError(f"manifest missing or mismatched: {case_id}")
        if len(case["variants"]) != len(set(case["variants"])):
            raise ValueError(f"duplicate variants: {case_id}")
    if set(actual) != {case_id for case_id, _ in rows}:
        raise ValueError("manifest contains cases not present in matrix")
    return {(case["id"], tier, variant)
            for case in actual.values()
            for tier in case["tiers"]
            for variant in case["variants"]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", type=Path, help="JSON array of verified case results")
    args = parser.parse_args()
    required = required_keys()
    print(f"Manifest valid: 47 scenarios, {len(required)} required group/variant results")
    if args.evidence is None:
        return 0
    results = json.loads(args.evidence.read_text())
    passed = set()
    for item in results:
        key = (item.get("id"), item.get("tier"), item.get("variant"))
        if key not in required:
            raise ValueError(f"unrecognized result: {key}")
        if item.get("status") != "passed":
            continue
        if not all(item.get(field) for field in
                   ("revision", "imageDigests", "runId", "evidence", "cleanup")):
            raise ValueError(f"passed result has missing evidence: {key}")
        if item["cleanup"] != "passed":
            raise ValueError(f"passed result has incomplete cleanup: {key}")
        if key in passed:
            raise ValueError(f"duplicate passing result: {key}")
        passed.add(key)
    missing = sorted(required - passed)
    if missing:
        print(f"Coverage blocked: {len(missing)} required results missing", file=sys.stderr)
        for key in missing[:20]:
            print("/".join(key), file=sys.stderr)
        return 1
    print("All required results have verified evidence and cleanup")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, KeyError, json.JSONDecodeError) as error:
        print(f"coverage validation failed: {error}", file=sys.stderr)
        sys.exit(2)
