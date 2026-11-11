#!/bin/sh
set -eu

read_secret() {
    path="$1"
    if [ ! -r "$path" ]; then
        printf '%s\n' "Required protected database credential file is unavailable." >&2
        exit 1
    fi
    value="$(cat "$path")"
    if [ -z "$value" ]; then
        printf '%s\n' "A required protected database credential file is empty." >&2
        exit 1
    fi
    printf '%s' "$value"
}

PGPASSWORD="$(read_secret "${MINICLOUD_POSTGRES_ADMIN_PASSWORD_FILE:-/run/secrets/postgres-admin-password}")"
MINICLOUD_APP_DB_PASSWORD="$(read_secret "${MINICLOUD_APP_DB_PASSWORD_FILE:-/run/secrets/app-db-password}")"
MINICLOUD_KRATOS_DB_PASSWORD="$(read_secret "${MINICLOUD_KRATOS_DB_PASSWORD_FILE:-/run/secrets/kratos-db-password}")"
export PGPASSWORD MINICLOUD_APP_DB_PASSWORD MINICLOUD_KRATOS_DB_PASSWORD

psql -v ON_ERROR_STOP=1 <<'SQL'
\getenv app_password MINICLOUD_APP_DB_PASSWORD
\getenv kratos_password MINICLOUD_KRATOS_DB_PASSWORD
SELECT format('CREATE ROLE minicloud LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'minicloud') \gexec
SELECT format('CREATE ROLE kratos LOGIN PASSWORD %L', :'kratos_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kratos') \gexec
SELECT 'CREATE DATABASE minicloud OWNER minicloud'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'minicloud') \gexec
SELECT 'CREATE DATABASE kratos OWNER kratos'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'kratos') \gexec
DO $$
BEGIN
    IF (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'minicloud') <> 'minicloud' THEN
        RAISE EXCEPTION 'Existing minicloud database has an unexpected owner; refusing to alter it.';
    END IF;
    IF (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'kratos') <> 'kratos' THEN
        RAISE EXCEPTION 'Existing kratos database has an unexpected owner; refusing to alter it.';
    END IF;
END
$$;
SQL
