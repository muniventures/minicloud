#!/bin/sh
set -eu
umask 077

BAO_ADDR=${BAO_ADDR:-https://openbao:8200}
BAO_CACERT=${BAO_CACERT:-/run/secrets/openbao-ca.crt}
ROOT_TOKEN_FILE=${OPENBAO_ROOT_TOKEN_FILE:-/run/secrets/openbao-root-token}
ROLE_ID_FILE=${OPENBAO_ROLE_ID_FILE:-/run/openbao-bootstrap/minicloud-approle-role-id}
SECRET_ID_FILE=${OPENBAO_SECRET_ID_FILE:-/run/openbao-bootstrap/minicloud-approle-secret-id}
POLICY_FILE=${OPENBAO_POLICY_FILE:-}
MOUNT_PATH=minicloud-apps
APPROLE_PATH=approle
ROLE_NAME=minicloud

if [ -z "$POLICY_FILE" ] || [ ! -r "$POLICY_FILE" ]; then
  echo "OPENBAO_POLICY_FILE must name the readable Minicloud policy file." >&2
  exit 1
fi
if [ ! -f "$ROOT_TOKEN_FILE" ] || [ ! -r "$ROOT_TOKEN_FILE" ]; then
  echo "OPENBAO_ROOT_TOKEN_FILE must name a readable protected token file." >&2
  exit 1
fi

unset BAO_TOKEN
BAO_TOKEN=$(cat -- "$ROOT_TOKEN_FILE")
if [ -z "$BAO_TOKEN" ]; then
  echo "OpenBao bootstrap token file is empty." >&2
  exit 1
fi
export BAO_ADDR BAO_CACERT BAO_TOKEN

# Fail before changing configuration unless the server is initialized and unsealed.
bao status >/dev/null

mounts=$(bao secrets list -format=json)
if printf '%s' "$mounts" | grep -Fq "\"$MOUNT_PATH/\""; then
  mount_info=$(bao read -format=json "sys/mounts/$MOUNT_PATH")
  printf '%s' "$mount_info" | grep -Eq '"type"[[:space:]]*:[[:space:]]*"kv"' &&
    printf '%s' "$mount_info" | grep -Eq '"version"[[:space:]]*:[[:space:]]*"2"' || {
      echo "The existing Minicloud secrets mount is not KV v2." >&2
      exit 1
    }
else
  bao secrets enable -path="$MOUNT_PATH" -version=2 kv >/dev/null
fi

auth_mounts=$(bao auth list -format=json)
if printf '%s' "$auth_mounts" | grep -Fq "\"$APPROLE_PATH/\""; then
  auth_info=$(bao read -format=json "sys/auth/$APPROLE_PATH")
  printf '%s' "$auth_info" | grep -Eq '"type"[[:space:]]*:[[:space:]]*"approle"' || {
    echo "The existing AppRole mount has an unexpected auth type." >&2
    exit 1
  }
else
  bao auth enable "$APPROLE_PATH" >/dev/null
fi

bao policy write minicloud "$POLICY_FILE" >/dev/null
bao write "auth/$APPROLE_PATH/role/$ROLE_NAME" \
  token_policies=minicloud token_type=service token_period=24h \
  secret_id_ttl=0 secret_id_num_uses=0 >/dev/null

write_protected_file() {
  destination=$1
  content=$2
  parent=$(dirname -- "$destination")
  mkdir -p -- "$parent"
  temporary="${destination}.new.$$"
  trap 'rm -f -- "$temporary"' EXIT HUP INT TERM
  if [ -e "$temporary" ]; then
    echo "Refusing to overwrite an existing credential staging file." >&2
    exit 1
  fi
  printf '%s\n' "$content" > "$temporary"
  chmod 600 "$temporary"
  mv -f -- "$temporary" "$destination"
  trap - EXIT HUP INT TERM
}

role_id=$(bao read -field=role_id "auth/$APPROLE_PATH/role/$ROLE_NAME/role-id")
if [ -s "$ROLE_ID_FILE" ]; then
  existing_role_id=$(cat -- "$ROLE_ID_FILE")
  if [ "$existing_role_id" != "$role_id" ]; then
    echo "Existing protected AppRole ID does not match the configured role." >&2
    exit 1
  fi
else
  write_protected_file "$ROLE_ID_FILE" "$role_id"
fi

if [ ! -s "$SECRET_ID_FILE" ]; then
  secret_id=$(bao write -field=secret_id -f "auth/$APPROLE_PATH/role/$ROLE_NAME/secret-id")
  if [ -z "$secret_id" ]; then
    echo "OpenBao did not return an AppRole secret ID." >&2
    exit 1
  fi
  write_protected_file "$SECRET_ID_FILE" "$secret_id"
fi

unset BAO_TOKEN role_id secret_id existing_role_id
printf '%s\n' "OpenBao Minicloud policy, KV v2 mount, and AppRole are ready."
