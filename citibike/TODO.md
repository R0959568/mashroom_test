# Citi Bike AI project — infrastructure TODO

Context file for a Claude Code session. Read fully before starting any task.

## Context

- School AI project, team of 4. Deliverable end of October 2026.
- **Milestone 1 (Fri 2 Oct 2026):** teammates can query the Citi Bike data with zero setup on their side (only the NetBird client + DB credentials).
- Source: NYC Citi Bike open data, public S3 bucket `tripdata` (monthly zip files containing CSVs).
- Scope: data from **2021 onwards only** (new schema). Older schema is out of scope for now; architecture must allow adding it later.
- Host: existing Hetzner webserver, Ubuntu, Docker + Docker Compose, nginx reverse proxy already running for existing apps (Postgres + backend + frontend).
- Disk: ~23 GB free (measured 27 Sep 2026). Do not load raw data into Postgres tables; raw data lives as Parquet on disk.
- Security rule: Postgres port **never** exposed publicly. Access only via the NetBird VPN (Tailscale until 10 Oct 2026, then removed).

## Target architecture

```
S3 (zips) --> ingest worker (cron, monthly) --> /data/citibike/raw/<schema_version>/*.parquet
                                                        |
                                    Postgres + pg_duckdb reads Parquet directly
                                                        |
                                   unified cleaned VIEW (one target schema)
                                                        |
                         data science (team, via NetBird)   --> aggregates/features/model output
                                                        |
                                     regular Postgres tables --> backend API --> frontend (subdomain via nginx)
```

Layers:
1. **raw** — Parquet per schema version, untouched, exactly as source delivers.
2. **unified view** — column renames, type casts, derived fields, union across schema versions.
3. **curated** (optional, later) — unified view materialised to Parquet if the view is too slow.
4. **app tables** — aggregates, features, model output in plain Postgres tables.

## Tasks

### 1. VPN on the host: NetBird (Tailscale until 10 Oct 2026)
- [x] Tailscale installed, tagged and ACL'd on 27 Sep 2026; replaced by NetBird in October (same model: a 100.x address, Postgres bound to it). On 10 Oct 2026 the Tailscale client, its ACL file and `TAILSCALE_IP` were removed from the host, the compose file and the repo.
- [x] NetBird on the host: `NETBIRD_IP` in the server `.env`; Postgres binds to it and nowhere else (verified 10 Oct 2026: `ss -ltn` shows the NetBird address alone; backend healthy after the recreate).
- [ ] NetBird dashboard: the teammates in the group whose policy allows TCP 5432 to this server, nothing else.
- [ ] Verify: no new inbound ports opened in the Hetzner firewall / ufw. NetBird needs outbound traffic only.

### 2. Postgres + pg_duckdb container
Implementation: `deploy/docker-compose.yml`, `deploy/postgres/init/01_roles.sh` (see `deploy/README.md`).
- [x] New compose project (separate from existing apps), own internal network, own volume.
- [x] Use an image that ships **pg_duckdb** (`pgduckdb/pgduckdb:17-v0.3.1`; verify the tag on the server). Standard Postgres cannot read Parquet.
- [x] Bind port 5432 **only** to the NetBird IP (`${NETBIRD_IP}:5432:5432`), never `0.0.0.0`.
- [ ] `pg_hba.conf`: allow only the NetBird range (`100.88.0.0/16` on this network) plus the internal docker network for the backend.
- [ ] Enable TLS on Postgres.
- [x] Mount the Parquet directory read-only into the container (`${DATA_DIR}` -> `/data/citibike:ro`).
- [x] Create roles: `citibike_reader` (NOLOGIN) and the ingest user are created on first start; teammate logins still by hand (`CREATE ROLE ... IN ROLE citibike_reader`).
- [x] Secrets via `.env` (not committed, `.env.example` documents the keys). Never print credentials in logs or chat.

### 3. Ingest worker
Implementation: `backend/` (ASP.NET Core API, runs the work as a background job) + `ingest/convert_zip.py` (conversion, wraps `citibike_data.py`).
Decision (27 Sep 2026): the conversion runs as a Python subprocess **inside** the backend container instead of a separate worker; `IZipConverter` is the seam if that changes.
- [x] List the bucket index, determine which files (2021+) are new: `GET /api/s3files?since=<date>` filters on S3 `LastModified`; without `since` the start of the last successful run in `citibike_meta.ingest_run` is used.
- [x] State record: `citibike_meta.ingest_run` / `ingest_file` in Postgres. Idempotent: a month's Parquet file is overwritten, never appended.
- [x] Download zip, stream-extract, convert CSV -> Parquet, delete the zip and any temp CSV afterwards.
- [x] Known source quirks handled in `citibike_data.py` (nested zips, duplicate months, header/timestamp variants, macOS junk).
- [x] Output layout: `/data/citibike/raw/v2/region=nyc/year=YYYY/month=MM/trips.parquet` (Hive partitions, decided 27 Sep 2026). `v1/` reserved for pre-2021.
- [x] Log per file: rows written, bytes, duration (backend log + `ingest_file` row).
- [ ] Backfill run once for 2021 -> now. First month measured locally (27 Sep 2026): 2026-08 = 1.02 GB zip -> 193 MB Parquet, 5.2M rows, 176 s; the zip-based estimate (≈ 5 GB) was wrong; per row it is ≈ 38 B of Parquet, so ≈ 8 GB for 2021–2026 (2022 bundle: 29.8M rows -> 1,160 MB).

### 4. Scheduling
- [ ] Monthly cron on the 5th: **deliberately not installed** (decision 27 Sep 2026). `deploy/cron/citibike-ingest.sh` without a date is ready and uses the last successful run as watermark; it is presented to the lecturers as the continuity design.
- [x] Host cron calling the backend on its loopback port; no scheduler in the container.
- [x] Manual trigger documented: `./citibike-ingest.sh 2026-09-01` or the Ingest page in the frontend.

### 5. Unified view in Postgres
Implementation: `deploy/postgres/sql/citibike_trips_view.sql`, verified locally on `pgduckdb/pgduckdb:17-v0.3.1` (27 Sep 2026).
- [x] `CREATE VIEW citibike.trips` over `read_parquet('/data/citibike/raw/v2/region=*/year=*/month=*/trips.parquet', union_by_name := true, hive_partitioning := true) r` via pg_duckdb; columns addressed as `r['name']` and cast to Postgres types.
- [x] Target schema: `ride_id, started_at, ended_at, duration_s (derived), start_station_id, start_station_name, start_lat, start_lng, end_station_id, end_station_name, end_lat, end_lng, rideable_type, member_casual, region, schema_version`.
- [x] Basic cleaning in the view: drop rows with null start/end time, drop durations < 60 s or > 24 h (documented in the SQL file).
- [x] Glob pattern so new monthly Parquet files appear automatically without changing the view.
- [ ] Later (when v1 is added): a second read_parquet + column mapping + UNION ALL; station ID mapping between v1 and v2 needed then.
- [ ] Check query latency on the full dataset; if too slow, materialise to `/data/citibike/curated/` as Parquet.

### 6. Backend / frontend / subdomain
- [x] DNS A record `deep-learning.henskens-tech.be` -> server IP (added 10 Oct 2026, resolving within the hour).
- [x] nginx blocks for the subdomain in the host's nginx.conf (`deploy/nginx/citibike.conf.example`; the backend serves SPA and `/api` on one port, reached over the nginx overlay network, no public port); Let's Encrypt certificate via the host's certbot (webroot), 10 Oct 2026.
- [x] Backend does not read the raw view; today it only serves the ingest metadata. App tables for aggregates/model output come later.
- [x] `POST /api/ingest/runs` is protected with `X-Admin-Token` because the subdomain is public.
- [x] Forecast page around one date ("today is", default the last day of the data; the forecast is for the day after) with a map of every active station (Leaflet, OpenStreetMap), sized by mean departures of the 28 days up to today, hover = forecast button, click = pick, colour = mean departures; `GET /api/forecast/map`; `./debug.sh` for a local run; loading skeleton; the AI portal's robot in the sidebar; the app named for the whole track (image `citibike-ingest:20261012b`, live 12 Oct 2026).

### 7. Handover to the team (before 2 Oct)
- [x] Connection instructions for teammates sent by message (no CONNECT.md in the repo, decision 27 Sep 2026). Originally: install the VPN client, accept the invite, connect with host = `<vpn-ip>`, port 5432, their user, SSL required; example query against `citibike.trips`.
- [ ] Verify from a teammate's machine (or a second device) that the connection works and that nothing else on the server is reachable.
- [ ] Prepare a walkthrough of what was built and why (data-architecture explanation for the team).

## Deployment status (27 Sep 2026)
- Stack running on the host from `/opt/cloud-ai-avenue-5/citibike/deploy` (compose project `citibike`, image `citibike-ingest:20260927` loaded with `docker save | docker load`, not built on the host: 3.7 GB RAM).
- Postgres listens on the VPN IP only, backend on 127.0.0.1:8081 only. Public 5432 gives no Postgres handshake; via the VPN it does.
- Run 1 (`since=2026-09-01`): 2026-08, 5,246,236 rows, 193 MB Parquet, 58 s. `citibike.trips` queried from a laptop over the VPN: 5,244,973 rows, `count(*)` 0.45 s.
- Backfill done: 68 monthly files 2021-01 -> 2026-08, 212.3M rows, 7.88 GB Parquet; `count(*)` on `citibike.trips` 26 s.
- Teammate logins (read-only, `citibike_reader`, 3 connections, 768 MB DuckDB memory, 10 min timeout) created; `mushroom` schema loaded for project 2.
- Still open: `pg_hba.conf`, teammates in the NetBird group.
- 10 Oct 2026: Tailscale removed from the host (client purged), from compose and `.env` (NetBird bind only) and from the repo (`deploy/tailscale/`, `TAILSCALE_IP`); Postgres verified on the NetBird address alone, backend healthy.
- 10 Oct 2026: forecast feature live on `https://deep-learning.henskens-tech.be` (image `citibike-ingest:20261011` since 11 Oct 2026: the first image served every asset as index.html, a white page, because Program.cs had no explicit `UseRouting()` after the static files): the SPA's forecast page, `GET /api/forecast/stations|range|?station&day`; the backend calls the forecast API of notebook 11 (API Gateway -> Lambda -> two serverless SageMaker endpoints) with the URL and key from the server `.env`, no AWS credential on the host. Measured through the public URL: a day inside the data 1 s, tomorrow with both endpoints cold 36 s, warm 1 s. Two faults fixed on the way: the backend joined the nginx overlay network and `postgres` resolved to another stack's database (alias `citibike-postgres`); the computed-row query was read by position and one index was off (now by column name).

## Definition of done for milestone 1
- NetBird up, 3 teammates invited, the access policy restricts them to 5432.
- Postgres + pg_duckdb running; port not reachable from the public internet (test with an external port scan).
- All 2021 -> latest monthly files ingested as Parquet; state manifest complete; no zips left on disk.
- `SELECT count(*) FROM citibike.trips` returns and the row count is plausible (~45–50M rows per recent year).
- Teammates can connect and run queries with a read-only user.

## Open questions / decisions still pending
- ~~Subdomain name for the project.~~ `deep-learning.henskens-tech.be` (10 Oct 2026).
- Whether to add pre-2021 data (v1 schema) later.
- Aggregation level and features: decided by the team during data science, not now.
- Disk: upgrade the Hetzner volume if the backfill exceeds ~15 GB of Parquet (expected ≈ 8 GB at ≈ 38 B per row).
