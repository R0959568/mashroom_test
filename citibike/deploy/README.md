# Citi Bike ingest: backend, frontend and deployment

Turns the monthly zips in the public `tripdata` S3 bucket into Parquet files that Postgres + pg_duckdb
reads directly. Covers TODO.md §2 (compose), §3 (ingest), §4 (scheduling) and the backend/frontend
half of §6. NetBird, `pg_hba.conf`, TLS on Postgres and DNS/certbot are still manual host steps.

```
S3 bucket ──► GET /api/s3files?since=…     what is new (preview, no side effects)
           ──► POST /api/ingest/runs        background run: download ─► convert_zip.py ─► Parquet ─► delete zip
                                            per-file bookkeeping in citibike_meta.ingest_run / ingest_file
Parquet    ──► /data/citibike/raw/v2/region=nyc/year=YYYY/month=MM/trips.parquet
Postgres   ──► citibike.trips view over read_parquet('…/raw/v2/**')   (postgres/sql/citibike_trips_view.sql)

NOAA NCEI  ──► POST /api/weather/runs       daily weather, Central Park ─► citibike.weather_daily (upsert per day)
                                            load history in citibike_meta.weather_run

holidays   ──► POST /api/calendar/runs      export_holidays.py (pinned package) ─► citibike.holiday (replace per year)
                                            load history in citibike_meta.calendar_run; citibike.calendar_day view
```

## Layout

| Path | What |
| --- | --- |
| `../backend/` | ASP.NET Core 8 Web API (controllers → managers → data), xUnit tests |
| `../frontend/` | Vite + React 18 + TypeScript SPA (Zustand store, facade, one axios instance) |
| `../ingest/convert_zip.py` | one zip → monthly Parquet, called by the backend per file; wraps `citibike_data.py` |
| `../ingest/export_holidays.py` | holidays of a range of years → JSON, called by the backend per calendar load |
| `Dockerfile` | React build → ASP.NET publish → runtime with Python converter; serves SPA and API on 8080 |
| `../../mushroom/inference/` | Private Python inference service and tests for the saved Random Forest pipeline |
| `../../mushroom/artifacts/tuned_mushroom_candidate.joblib` | Versioned model bundle shipped with the inference image |
| `docker-compose.yml` | `postgres`, `backend` (loopback port), and private `mushroom-model` inference service |
| `postgres/init/01_roles.sh` | first-start roles: `citibike_reader`, ingest user, schemas, pg_duckdb settings |
| `postgres/sql/citibike_trips_view.sql` | unified view (TODO §5), run once after the first ingest |
| `postgres/sql/parquet_files_view.sql` | `citibike_meta.parquet_files`: one row per Parquet file (rows, size), footer-only |
| `postgres/sql/mushroom_schema.sql` | `mushroom` schema for project 2: plain tables for the UCI Secondary Mushroom CSVs, load commands in the header |
| `cron/citibike-ingest.sh` | monthly trigger; also the manual trigger for the demo |
| `nginx/citibike.conf.example` | server block for the subdomain |

## How "new" is decided

`GET /api/s3files?since=<date>` lists every bucket object whose S3 `LastModified` is **after** `since`,
restricted to `Ingest__Regions` (default `nyc`) and years ≥ `Ingest__MinYear` (2021: the old column layout
is out of scope, and the 2013–2020 bundles were re-uploaded in 2024 so a date filter alone would pull them in).

Without `since`, the backend looks in `citibike_meta.ingest_run` for the **start time of the last successful
run** and uses that. No run yet → `Ingest__DefaultSince` (2021-01-01), i.e. the full backfill. The start
time (not the end time) is the watermark so a file uploaded during a run is not missed. A failed run does not
move the watermark, so the next run retries the same files; re-converting a month overwrites its Parquet
file, so re-runs never duplicate data.

Try it: `since=2026-09-01` returns only `202608-citibike-tripdata.zip` (uploaded 10 Sept 2026);
`since=2021-01-01` returns the 2021–2023 yearly bundles plus every monthly file from 2024 on.

## API

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/api/s3files?since=2026-09-01` | preview; `since` optional (ISO date or timestamp) |
| POST | `/api/ingest/runs` | body `{"since": "2026-09-01", "trigger": "manual"}` (both optional); header `X-Admin-Token`; returns 202 + run, 409 if a run is in progress |
| GET | `/api/ingest/runs?limit=20` | history |
| GET | `/api/ingest/runs/{id}` | run + per-file status, rows written, Parquet bytes, duration, error |
| POST | `/api/weather/runs` | body `{"from": "2026-08-01", "to": "2026-08-31", "trigger": "manual"}` (all optional); header `X-Admin-Token`; fetches NOAA and stores the days within the request; returns 201 + load, 409 if a load is in progress, 502 if NOAA fails |
| GET | `/api/weather/runs?limit=20` | load history |
| GET | `/api/weather/runs/{id}` | one load: range, where the start date came from, days received / written, error |
| GET | `/api/weather/days?from=2026-08-01&to=2026-08-31` | stored days (both dates required) |
| POST | `/api/calendar/runs` | body `{"fromYear": 2021, "toYear": 2028, "trigger": "manual"}` (all optional); header `X-Admin-Token`; exports the holidays and replaces those years within the request; returns 201 + load, 409 if a load is in progress, 502 if the export fails |
| GET | `/api/calendar/runs?limit=20` | load history, with the package version of every load |
| GET | `/api/calendar/runs/{id}` | one load: years, categories, package version, holidays received / written, error |
| GET | `/api/calendar/holidays?from=2022-12-01&to=2022-12-31` | stored holidays (both dates required) |
| GET | `/api/forecast/stations?q=central&limit=20` | stations to pick from (name contains, or exact key), busiest first |
| GET | `/api/forecast/range` | the days a forecast can be asked for: the data's first and last day, and `tomorrow` (the day after the data) |
| GET | `/api/forecast/map?today=2026-08-31` | the map of the forecast page: every station with trips in the 28 days up to `today` (a day of the data), with coordinates, mean departures per active day and the number of active days, busiest first; 400 for a day outside the data. About 2,300 stations, 330 kB, 0,1 s (index `station_day_day_idx`, see below) |
| GET | `/api/forecast?station=6140.05&day=2026-09-01` | the forecast of one station-day: departures of the day and of the rest of its month from the models behind the forecast API (notebook 11), the actuals when the day is in the data, and what the model saw; 400 for a day outside the range or a station without 28 days of history, 502 when the forecast API fails, 503 when `FORECAST_API_URL` is not set. The first call after a quiet period takes up to about 40 s (both serverless endpoints waking up, the rest-of-month model is 1 GB; 36 s measured on 10 Oct 2026), a warm call about 1 s |
| GET | `/api/mushroom/features` | model version, threshold, raw feature names, and the categorical code choices used to build the Mushroom form |
| POST | `/api/mushroom/predict` | body `{"features":{"cap-diameter":5,"stem-height":6,"stem-width":10,"spore-print-color":"g","gill-color":"w","habitat":"g","season":"a","ring-type":"p","cap-shape":"x","stem-surface":"y"}}`; returns the dataset class and poisonous probability. Educational use only; never use to decide whether a real mushroom is safe to eat |
| GET | `/healthz` | readiness |

Errors are plain text with the proper status code (400 validation, 401 token, 404, 409, 500, 502 upstream, 503 not configured).

## Forecast

The forecast page (the first page of the SPA) asks for a station and a day, and shows what the models expect for that
day and for the rest of its month, next to the actuals for a day inside the data. The models run on AWS: notebook 11
put them behind a SageMaker endpoint per horizon, a Lambda and an HTTP API of API Gateway; the backend holds only the
URL and the key (`FORECAST_API_URL`, `FORECAST_API_KEY` in `.env`), no AWS credential. For a day inside the data the
backend reads the row of `citibike_prep.station_day_features` (the feature table of notebook 05); for the day after the
data it computes the same features from `station_day`, `calendar_day` and the last observed weather (the window
definitions of 05 §4 for one station-day, `Data/ForecastRepository.cs`). `Managers/ForecastManager.cs` turns a row into
the model inputs exactly as the notebooks' `design()` does, with the cold-start rule of 28 days.

The page is built around one date, "today is": the data is not live, so the user chooses the last observed day
(default: the last day of the data) and everything forecasts the day after it, tomorrow. A map (Leaflet on
OpenStreetMap tiles, `frontend/src/components/StationMap.tsx`) shows every station active in the 28 days up to
today, sized by its mean departures; hovering a station opens its forecast button, clicking picks it. The map's
query groups `citibike_prep.station_day` over the window; it needs the index `station_day_day_idx` on `(day)`,
created on the server on 12 Oct 2026 (`CREATE INDEX CONCURRENTLY IF NOT EXISTS station_day_day_idx ON
citibike_prep.station_day (day)`; 0,6 s without it, 0,1 s with it). A notebook that drops and recreates the
table drops the index with it: recreate it afterwards.

## Weather

`POST /api/weather/runs` reads the **NOAA GHCN-Daily** summaries of station `USW00094728` (New York Central
Park) from the public NCEI Access Data Service (`daily-summaries`, CSV, metric units; no account or key) and
upserts them into `citibike.weather_daily`, one row per station and day:

| Column | Unit | NOAA |
| --- | --- | --- |
| `tmax_c`, `tmin_c` | °C | TMAX, TMIN |
| `prcp_mm` | mm, day total | PRCP |
| `snow_mm`, `snow_depth_mm` | mm | SNOW, SNWD |
| `wind_ms` | m/s, daily average | AWND |

NULL means the station did not report that variable that day. The table is created by the backend at start-up
(`Data/Sql/schema.sql`), owned by the backend's user and readable by `citibike_reader`; pg_duckdb joins it with
the `citibike.trips` view (checked with `EXPLAIN`: the join runs as one DuckDB scan).

**Which days a load fetches.** Explicit `from` / `to` win. Without `from`, the load starts `Weather__RefetchDays`
(30) days before the last stored day, because NOAA publishes with a delay of a few days and corrects recent
values; the upsert overwrites them, so re-loading never duplicates. With nothing stored yet it starts at
`Weather__DefaultFrom` (2021-01-01). Without `to` it runs to today; days NOAA has not published yet are simply
absent (`daysReceived` is then smaller than the range).

**Why synchronous, not a background run like the trip ingest.** A full load from 2021 is ~2,100 rows in one HTTP
request: 1.6 s measured. The background-job pattern exists for work beyond the ~60 s request budget; a
weather load is far below it, so the POST returns the finished load (201) and a NOAA failure is an immediate
502 with the reason, while the failed load is still recorded in `citibike_meta.weather_run`.

```
curl -X POST http://127.0.0.1:8081/api/weather/runs -H "X-Admin-Token: $INGEST_ADMIN_TOKEN"     # first load: 2021 -> today
curl "http://127.0.0.1:8081/api/weather/days?from=2026-08-01&to=2026-08-07"
```

The monthly cron (`cron/citibike-ingest.sh`) also triggers a weather load after starting the trip ingest.

## Holiday calendar

`POST /api/calendar/runs` runs `ingest/export_holidays.py`, which takes the holidays from the open-source
[`holidays`](https://pypi.org/project/holidays/) package (MIT, pinned to `0.105` in `ingest/requirements.txt`)
and stores them unchanged in `citibike.holiday`, one row per day and holiday name:

| Column | Meaning |
| --- | --- |
| `country`, `subdivision` | `US`, `NY`: federal plus New York State holidays (`Calendar__Country`, `Calendar__Subdivision`) |
| `day`, `name` | as the package reports them |
| `category` | `public` (holidays with a day off) or `unofficial` (Easter Sunday, Good Friday, Christmas Eve, New Year's Eve, Halloween, ...); `Calendar__Categories` |
| `is_observed` | `true` = the weekday off for a holiday on a weekend (`Christmas Day (observed)` on Monday 26 Dec 2022), `false` = the actual date |
| `source`, `source_version` | `holidays`, `0.105`: which package version produced the row |

Why a package and not a hand-written seed: the dates come from rules in versioned, open-source code, so every
row can be traced to a package version. Why not the pandas `USFederalHolidayCalendar` used in 03_hypothesis:
it only knows the observed day, so Christmas 2022 (a Sunday) was missing from the holidays. The package runs
offline, it makes no network calls.

**Which years a load covers.** Explicit `fromYear` / `toYear` win; without them `Calendar__DefaultFromYear`
(2021) up to the current year plus `Calendar__YearsAhead` (2), so a forecast can join future dates. A load
**replaces** the years it covers (delete + insert in one transaction) instead of upserting: after a version
bump a holiday that the package renamed or dropped does not linger. An export with no rows, a duplicate row
or a day outside the years fails the load (502) and leaves the stored holidays as they were.

**`citibike.calendar_day`** is a view with one row per date over the loaded years, holidays or not, so a join
never drops a day: `iso_dow`, `is_weekend`, `has_public_holiday` (actual date), `is_observed_day_off`,
`public_holidays`, `other_holidays`. It holds facts only; mapping them to day types (workday, day off,
bridge day) is a later step. Join on the trip date, never store holiday flags per trip:

```sql
SELECT t.started_at::date AS day, count(*) AS trips, c.is_weekend, c.public_holidays, c.other_holidays
  FROM citibike.trips t
  JOIN citibike.calendar_day c ON c.day = t.started_at::date
 GROUP BY 1, 3, 4, 5;
```

```
curl -X POST http://127.0.0.1:8081/api/calendar/runs -H "X-Admin-Token: $INGEST_ADMIN_TOKEN"   # 2021 -> current year + 2
curl "http://127.0.0.1:8081/api/calendar/holidays?from=2022-12-24&to=2022-12-31"
```

**Upgrading the package.** Bump the pin in `ingest/requirements.txt` (and the repo-root `requirements.txt`),
rebuild the image, then reload: `source_version` shows which rows come from which version. The monthly cron
reloads the default years after the weather load, so the years ahead keep moving forward.

## Local development

Prerequisites: .NET 8 SDK, Node 20+, Python 3.12 with `pip install -r citibike/ingest/requirements.txt`, and a
Postgres you can reach (any Postgres works for the metadata tables; pg_duckdb is only needed for the view).

The quick way, from `citibike/`: `./debug.sh` starts the backend (dotnet, `http://127.0.0.1:8080`) and the Vite dev
server (`http://localhost:5173`, hot reload, `/api` proxied) against the project database over NetBird and the
forecast API of notebook 11, with the notebooks' credentials from the repo root `.env`. That login is read-only:
the forecast page works, starting a run does not (the backend logs a warning instead of refusing to start). The
script's header explains the overrides, including an ssh tunnel when NetBird is not connected. By hand:

```
# backend (from citibike/backend) - Development settings point the converter at ../ingest and data at ../../data
set ConnectionStrings__Postgres=Host=127.0.0.1;Port=5432;Database=citibike;Username=citibike_ingest;Password=...
dotnet run --project src/CitibikeIngest.Api          # http://127.0.0.1:8080, Swagger on /swagger
dotnet test                                          # manager tests with in-memory fakes

# frontend (from citibike/frontend)
npm install
npm run dev                                          # http://localhost:5173, /api proxied to 8080
npm run build                                        # tsc -b && vite build - the type check is the gate

# python
python -m pytest citibike/tests -q
```

Without `Ingest__AdminToken` the POST is unprotected; the backend logs a warning. Set it in production.

### Full stack locally with Docker Desktop

Keep `.env` for the server and use a second, git-ignored file for the laptop (`NETBIRD_IP=127.0.0.1`, a
local `DATA_DIR` such as `C:/…/cloud-ai-avenue-5/data/citibike`, generated passwords):

```
cd citibike/deploy
cp .env.example .env.local && edit
docker compose --env-file .env.local up -d --build
curl "http://127.0.0.1:8081/api/s3files?since=2026-09-01"
docker compose --env-file .env.local exec -T postgres psql -U citibike_ingest -d citibike -f - < postgres/sql/citibike_trips_view.sql
```

The build context is the repository root; `.dockerignore` keeps build outputs, notebook data, and virtual
environments out of it. The selected 10 MB model bundle is the explicit exception and is copied into the
inference image.

**Laptop behind a TLS-inspecting proxy** (symptom: `GET /api/s3files` → 500, log says `UntrustedRoot`): the
container has to trust the proxy's CA. Export the chain the proxy presents (any tool that shows the
certificate chain for `s3.amazonaws.com`, minus the leaf), append it to the container's own bundle
(`docker cp citibike-backend-1:/etc/ssl/certs/ca-certificates.crt …`) into `data/certs/ca-bundle.pem`, set
`LOCAL_CA_BUNDLE=<absolute path>` in `.env.local` and add `-f docker-compose.yml -f docker-compose.local.yml`
to every compose command. `data/` is git-ignored; never commit the bundle.

## Server

### 1. NetBird on the host (not in a container)

```
curl -fsSL https://pkgs.netbird.io/install.sh | sh
sudo netbird up                      # join the project network: the login link it prints, or a setup key from the dashboard
netbird status | grep "NetBird IP"   # -> NETBIRD_IP in .env (the host also needs sysctl net.ipv4.ip_nonlocal_bind=1)
```

Then in the NetBird dashboard: a group for the server and a group for the people who may query Postgres, and
one access policy from that group to the server on TCP 5432 only. Verify from the host that nothing new is open
to the internet: `sudo ufw status` / the Hetzner firewall must show no 5432 or 8081 rule — NetBird needs
outbound traffic only. (Tailscale played this role until 10 October 2026; it was removed from the host, the
compose file and this repo on that day.)

### 2. The subdomain

`https://deep-learning.henskens-tech.be` (live since 10 Oct 2026) is an A record to the host. The host's nginx (a
swarm service with one config file, see `nginx/citibike.conf.example` for the two blocks and the order of the steps)
reaches the backend by its alias `citibike-backend` on the overlay network the compose file joins (`PROXY_NETWORK`); the
certificate comes from the host's certbot (webroot `/var/www/certbot`, renewed by its timer). Joining that network has
one consequence inside the stack: the backend now sees two networks, and a bare service name such as `postgres` can
resolve to another stack's database on the shared one (it did: the first start failed with a password error from the
wrong Postgres). The compose file therefore gives this stack's Postgres the alias `citibike-postgres` on the internal
network and the connection string uses that name.

### 3. Stack

The host has 3.7 GB RAM next to the other apps, so the image is built on a laptop and streamed over
instead of being built on the host:

```
docker tag citibike-ingest:local citibike-ingest:<yyyymmdd>                    # immutable tag, never latest
docker tag mushroom-inference:local mushroom-inference:<yyyymmdd>
docker save citibike-ingest:<yyyymmdd> mushroom-inference:<yyyymmdd> | gzip -1 | ssh root@<host> 'gunzip | docker load'
# then IMAGE_TAG=<yyyymmdd> and MUSHROOM_IMAGE_TAG=<yyyymmdd> in the server .env, followed by docker compose up -d --no-build
```

`/data/citibike` must be `755` (not 700): the backend writes as root, but Postgres reads the Parquet
files as the `postgres` user and gets "No files found" if it cannot enter the folder.

```
sudo mkdir -p /data/citibike && sudo chmod 755 /data/citibike
cd citibike/deploy && cp .env.example .env && nano .env
docker compose up -d --build
docker compose logs -f backend                      # "Metadata schema ready" = tables created
curl "http://127.0.0.1:8081/api/s3files?since=2026-09-01"
./cron/citibike-ingest.sh 2026-09-01                # first real run: one month, measure disk
docker compose exec postgres psql -U $INGEST_DB_USER -d citibike -f - < postgres/sql/citibike_trips_view.sql
curl -X POST http://127.0.0.1:8081/api/weather/runs -H "X-Admin-Token: $INGEST_ADMIN_TOKEN"   # weather 2021 -> today
```

Backfill: `./cron/citibike-ingest.sh 2021-01-01` after the one-month test. Extrapolate disk use from the
first month before launching it (TODO §3). Then install the crontab line from `cron/citibike-ingest.sh`.

Restart behaviour: a run interrupted by a container restart is marked `failed` at the next start; zips in
`landing/` are deleted before the next attempt and the month is converted again.

### 4. Pipeline (GitHub Actions)

No pull requests: a push to `main` is the trigger. The CitiBike AWS forecasting workflow remains separate; the
Mushroom classifier runs privately on the Hetzner host and does not use AWS.

| Workflow | Runs on | Does |
| --- | --- | --- |
| [`app.yml`](../../.github/workflows/app.yml) | every push to `main` (not docs, notebooks or CitiBike `train/`), or by hand | CitiBike and Mushroom Python tests, backend tests → app and model images → `docker save \| ssh \| docker load` → Compose manifest update → [`ci/rollout.sh`](ci/rollout.sh) → public `/healthz` |
| [`retrain.yml`](../../.github/workflows/retrain.yml) | a push changing `train.py`, `inference.py`, `train/requirements.txt`, `retrain.py` or `retrain.json`, or by hand (optionally a dry run) | the train tests → [`train/retrain.py`](../train/retrain.py): a SageMaker training job per horizon → gate → endpoint update → probe |

`rollout.sh` backs up `.env`, changes both image tags, recreates the app and Mushroom model services (Postgres
untouched), waits for `/healthz` on the loopback port, and restores the previous images if the app fails to start.
On the first Mushroom deployment, when there is no previous model image, rollback restores the old app without
starting the new model dependency. It keeps the three newest images for each service.

The Mushroom page calls `GET /api/mushroom/features` and `POST /api/mushroom/predict`. The ASP.NET API validates the
10 raw features and calls the model service on the internal Docker network; the model service is not exposed through
a host port. The pinned scikit-learn runtime matches the version used to serialize the selected candidate. This
experimental classifier is for dataset examples only and must never be used to decide whether a real mushroom is safe
to eat.

`retrain.py` trains with the hyperparameters of `train/retrain.json` (09's choice) on the model inputs that 10 §1 put in
S3, and reads the live model's validation MAE from AWS (endpoint → config → model → training job). A candidate within 1 %
of the live MAE (09's tie rule) gets its own model and endpoint config, the endpoint is pointed at it (same name, so the
Lambda and the backend change nothing), and five validation rows must come back as the job itself predicted them, else
the endpoint is pointed back. The Lambda's model description follows. What it does not do: refresh the inputs (Postgres
is reachable over the VPN only: 07 §2 and 10 §1 on the laptop), tune (10), or create endpoints (11).

**One-time setup.** Repository → Settings → Secrets and variables → Actions:

| Secret | Value |
| --- | --- |
| `HETZNER_SSH_TARGET` | `root@<host>` |
| `HETZNER_SSH_KEY` | private key of a pair made for the pipeline only: `ssh-keygen -t ed25519 -f gha_deploy -C github-actions-citibike -N ""`, the `.pub` appended to the host's `~/.ssh/authorized_keys` |
| `HETZNER_KNOWN_HOSTS` | `ssh-keyscan -t ed25519 <host>`: the runner trusts this host key and no other |
| `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN` | Learner Lab → AWS Details → AWS CLI; new every session, refresh before a model run |
| `SAGEMAKER_ROLE_ARN` | optional, only when the lab does not let you pass LabRole (10 part 1) |

A model run without a live lab session fails at its first step with what to refresh, and leaves the endpoints as they
were.

## Decisions

- **Converter as a Python subprocess inside the backend container** instead of a separate worker (TODO §3
  said "not inside the API backend"). Reason: `citibike_data.py` already handles every source quirk and is
  tested; re-implementing that in C# is not feasible before 2 Oct, and a second container would need its own
  S3 listing and metadata access. Cost: a larger image and the conversion sharing CPU with the API, which is
  internal-only. Reversible: `IZipConverter` is the seam.
- **Hive-partitioned Parquet** (`region=…/year=…/month=…/trips.parquet`) instead of flat `YYYY-MM.parquet`:
  it is what the Python module already writes, pg_duckdb globs it, and Jersey City can be added by config.
- **Holidays through the backend, like the weather**, although a one-off script via `connection.py` would
  be shorter: one pattern for every source (endpoint, load history, admin token, cron), and the notebooks only
  read. The `holidays` package is Python, so it runs as a subprocess like the zip converter
  (`IHolidayExporter` is the seam).
- **Weather as a Postgres table, not Parquet:** ~2,100 small rows that change (NOAA corrections), so an upsert
  into a heap table is simpler than rewriting files, and pg_duckdb still joins it with the Parquet trips. Kept
  in the `citibike` schema next to the trips view so analysts find both in one place.
- **Watermark = start of last successful run**, stored in Postgres, not a per-file manifest: a re-uploaded
  month is picked up again automatically (LastModified moves), and idempotency comes from per-month files
  being overwritten.
