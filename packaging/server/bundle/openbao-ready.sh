#!/bin/sh
set -eu

BAO_ADDR=${BAO_ADDR:-https://openbao:8200}
BAO_CACERT=${BAO_CACERT:-/run/secrets/openbao-ca.crt}
TOKEN_FILE=${OPENBAO_TOKEN_FILE:-/run/minicloud-openbao/token}

unset BAO_TOKEN
[ -r "$TOKEN_FILE" ] && [ -s "$TOKEN_FILE" ] || exit 1
BAO_TOKEN=$(cat -- "$TOKEN_FILE" 2>/dev/null) || exit 1
[ -n "$BAO_TOKEN" ] || exit 1
export BAO_ADDR BAO_CACERT BAO_TOKEN

# A retained sink can outlive an Agent restart while consumers hold the volume.
# Verify the credential against the TLS server, not just the presence of a file.
bao token lookup -format=json >/dev/null 2>&1
