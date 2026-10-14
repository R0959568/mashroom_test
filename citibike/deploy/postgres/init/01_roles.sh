#!/bin/bash
# Runs once, when the data volume is empty (docker-entrypoint-initdb.d). Creates:
#   - pg_duckdb extension (reads Parquet straight from /data/citibike)
#   - citibike_reader: NOLOGIN role that teammates' personal logins inherit (read-only)
#   - $INGEST_DB_USER: login for the backend; owns the citibike and citibike_meta schemas
# Teammate logins are created afterwards by hand:  CREATE ROLE <name> LOGIN PASSWORD '...' IN ROLE citibike_reader;
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
     -v ingest_user="$INGEST_DB_USER" -v ingest_password="$INGEST_DB_PASSWORD" <<-'EOSQL'
    CREATE EXTENSION IF NOT EXISTS pg_duckdb;

    CREATE ROLE citibike_reader NOLOGIN;
    CREATE ROLE :"ingest_user" LOGIN PASSWORD :'ingest_password';

    CREATE SCHEMA citibike      AUTHORIZATION :"ingest_user";
    CREATE SCHEMA citibike_meta AUTHORIZATION :"ingest_user";

    GRANT USAGE ON SCHEMA citibike, citibike_meta TO citibike_reader;
    ALTER DEFAULT PRIVILEGES FOR ROLE :"ingest_user" IN SCHEMA citibike, citibike_meta
        GRANT SELECT ON TABLES TO citibike_reader;

    -- pg_duckdb only executes for members of duckdb.postgres_role and blocks the local file system for
    -- non-superusers by default. The data volume is mounted read-only, so allowing it is acceptable here.
    ALTER SYSTEM SET duckdb.postgres_role = 'citibike_reader';
    ALTER SYSTEM SET duckdb.disabled_filesystems = '';
    GRANT citibike_reader TO :"ingest_user";
EOSQL
