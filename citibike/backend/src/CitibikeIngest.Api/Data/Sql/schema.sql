-- Ingest metadata and the weather table. Applied at backend start-up (idempotent). On the server both
-- schemas are created by deploy/postgres/init/01_roles.sh and owned by the backend's user; the block below
-- only creates them on a plain dev Postgres. (CREATE SCHEMA IF NOT EXISTS is not used: Postgres checks
-- CREATE on the database before it checks existence, so it fails for a non-owner even when the schema is there.)
-- Tables the backend's user creates are readable by citibike_reader through the default privileges in 01_roles.sh.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'citibike_meta') THEN
        EXECUTE 'CREATE SCHEMA citibike_meta';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'citibike') THEN
        EXECUTE 'CREATE SCHEMA citibike';
    END IF;
END
$$;

CREATE TABLE IF NOT EXISTS citibike_meta.ingest_run (
    id            bigserial PRIMARY KEY,
    started_at    timestamptz NOT NULL DEFAULT now(),
    finished_at   timestamptz,
    status        text NOT NULL CHECK (status IN ('running', 'succeeded', 'failed')),
    since         timestamptz NOT NULL,   -- files with LastModified > since were processed
    since_source  text NOT NULL CHECK (since_source IN ('parameter', 'lastrun', 'default')),
    triggered_by  text NOT NULL,          -- manual | cron
    files_found   integer NOT NULL DEFAULT 0,
    files_done    integer NOT NULL DEFAULT 0,
    files_failed  integer NOT NULL DEFAULT 0,
    error         text
);

-- At most one run in progress: two concurrent POSTs cannot both start a run.
CREATE UNIQUE INDEX IF NOT EXISTS ingest_run_one_running_idx
    ON citibike_meta.ingest_run (status) WHERE status = 'running';

CREATE INDEX IF NOT EXISTS ingest_run_status_started_idx
    ON citibike_meta.ingest_run (status, started_at DESC);

CREATE TABLE IF NOT EXISTS citibike_meta.ingest_file (
    id              bigserial PRIMARY KEY,
    run_id          bigint NOT NULL REFERENCES citibike_meta.ingest_run (id) ON DELETE CASCADE,
    file_name       text NOT NULL,
    region          text NOT NULL,
    last_modified   timestamptz NOT NULL,
    size_bytes      bigint NOT NULL,
    status          text NOT NULL CHECK (status IN ('pending', 'downloading', 'converting', 'done', 'failed')),
    months_written  integer NOT NULL DEFAULT 0,
    rows_written    bigint NOT NULL DEFAULT 0,
    parquet_bytes   bigint NOT NULL DEFAULT 0,
    duration_s      double precision,
    error           text,
    processed_at    timestamptz
);

CREATE INDEX IF NOT EXISTS ingest_file_run_id_idx ON citibike_meta.ingest_file (run_id);

-- ---- Weather (NOAA daily summaries, loaded through POST /api/weather/runs) ---------------------------

CREATE TABLE IF NOT EXISTS citibike_meta.weather_run (
    id             bigserial PRIMARY KEY,
    started_at     timestamptz NOT NULL DEFAULT now(),
    finished_at    timestamptz,
    status         text NOT NULL CHECK (status IN ('running', 'succeeded', 'failed')),
    station        text NOT NULL,
    date_from      date NOT NULL,
    date_to        date NOT NULL,
    range_source   text NOT NULL CHECK (range_source IN ('parameter', 'lastday', 'default')),
    triggered_by   text NOT NULL,          -- manual | cron
    days_received  integer NOT NULL DEFAULT 0,
    days_written   integer NOT NULL DEFAULT 0,
    error          text
);

-- At most one load in progress, the same rule as for ingest runs.
CREATE UNIQUE INDEX IF NOT EXISTS weather_run_one_running_idx
    ON citibike_meta.weather_run (status) WHERE status = 'running';

-- One row per station and day; a later load overwrites the values (NOAA corrects recent days).
CREATE TABLE IF NOT EXISTS citibike.weather_daily (
    station        text NOT NULL,
    day            date NOT NULL,
    tmax_c         double precision,
    tmin_c         double precision,
    prcp_mm        double precision,
    snow_mm        double precision,
    snow_depth_mm  double precision,
    wind_ms        double precision,
    run_id         bigint REFERENCES citibike_meta.weather_run (id) ON DELETE SET NULL,
    loaded_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (station, day)
);

COMMENT ON TABLE citibike.weather_daily IS
    'NOAA GHCN-Daily summaries (NCEI daily-summaries service), metric units. Station USW00094728 = New York Central Park. NULL = not reported that day.';
COMMENT ON COLUMN citibike.weather_daily.tmax_c IS 'Maximum temperature, degrees C (TMAX)';
COMMENT ON COLUMN citibike.weather_daily.tmin_c IS 'Minimum temperature, degrees C (TMIN)';
COMMENT ON COLUMN citibike.weather_daily.prcp_mm IS 'Precipitation, mm, day total (PRCP)';
COMMENT ON COLUMN citibike.weather_daily.snow_mm IS 'Snowfall, mm (SNOW)';
COMMENT ON COLUMN citibike.weather_daily.snow_depth_mm IS 'Snow depth, mm (SNWD)';
COMMENT ON COLUMN citibike.weather_daily.wind_ms IS 'Average daily wind speed, m/s (AWND)';

-- ---- Holiday calendar (holidays package, loaded through POST /api/calendar/runs) ---------------------

CREATE TABLE IF NOT EXISTS citibike_meta.calendar_run (
    id                 bigserial PRIMARY KEY,
    started_at         timestamptz NOT NULL DEFAULT now(),
    finished_at        timestamptz,
    status             text NOT NULL CHECK (status IN ('running', 'succeeded', 'failed')),
    country            text NOT NULL,
    subdivision        text NOT NULL,      -- '' = country-wide only
    categories         text NOT NULL,      -- comma-separated package categories, e.g. public,unofficial
    year_from          integer NOT NULL,
    year_to            integer NOT NULL,
    range_source       text NOT NULL CHECK (range_source IN ('parameter', 'default')),
    triggered_by       text NOT NULL,      -- manual | cron
    source             text,               -- package that produced the rows: holidays
    source_version     text,               -- its version, e.g. 0.105
    holidays_received  integer NOT NULL DEFAULT 0,
    holidays_written   integer NOT NULL DEFAULT 0,
    error              text
);

-- At most one load in progress, the same rule as for ingest runs and weather loads.
CREATE UNIQUE INDEX IF NOT EXISTS calendar_run_one_running_idx
    ON citibike_meta.calendar_run (status) WHERE status = 'running';

-- The package output unchanged, one row per holiday name and day. A load replaces the whole years it covers
-- (delete + insert in one transaction), so a holiday a newer package version renames or drops does not linger.
CREATE TABLE IF NOT EXISTS citibike.holiday (
    country         text NOT NULL,
    subdivision     text NOT NULL,
    day             date NOT NULL,
    name            text NOT NULL,
    category        text NOT NULL,
    is_observed     boolean NOT NULL,
    source          text NOT NULL,
    source_version  text NOT NULL,
    run_id          bigint REFERENCES citibike_meta.calendar_run (id) ON DELETE SET NULL,
    loaded_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (country, subdivision, day, name, category)
);

COMMENT ON TABLE citibike.holiday IS
    'Holidays from the Python holidays package (US, subdivision NY), unchanged. A holiday on a weekend has two rows: the actual date and the observed day off. Join through citibike.calendar_day.';
COMMENT ON COLUMN citibike.holiday.category IS
    'Package category: public = federal + New York State holidays; unofficial = observances without a day off (Easter Sunday, Good Friday, Christmas Eve, New Year''s Eve, Halloween, ...)';
COMMENT ON COLUMN citibike.holiday.is_observed IS
    'true = the day off for a holiday on a weekend (name ends in "(observed)"); false = the actual date';
COMMENT ON COLUMN citibike.holiday.source_version IS 'Version of the holidays package that produced the row';

-- One row per date over the loaded years, holidays or not, so a join never drops a day. Facts only: mapping
-- to day types (workday, day off, bridge day) is a later, separate step. Fixed to US / NY, the Citi Bike region.
CREATE OR REPLACE VIEW citibike.calendar_day AS
WITH ny AS (
    SELECT * FROM citibike.holiday WHERE country = 'US' AND subdivision = 'NY'
), bounds AS (
    SELECT make_date(extract(year FROM min(day))::int, 1, 1) AS first_day,
           make_date(extract(year FROM max(day))::int, 12, 31) AS last_day
      FROM ny
)
SELECT d.day,
       extract(isodow FROM d.day)::int                                   AS iso_dow,      -- 1 = Monday, 7 = Sunday
       extract(isodow FROM d.day) >= 6                                   AS is_weekend,
       coalesce(bool_or(h.category = 'public' AND NOT h.is_observed), false) AS has_public_holiday,
       coalesce(bool_or(h.category = 'public' AND h.is_observed), false)     AS is_observed_day_off,
       string_agg(h.name, '; ' ORDER BY h.name) FILTER (WHERE h.category = 'public')  AS public_holidays,
       string_agg(h.name, '; ' ORDER BY h.name) FILTER (WHERE h.category <> 'public') AS other_holidays
  FROM bounds
 CROSS JOIN LATERAL generate_series(bounds.first_day, bounds.last_day, interval '1 day') AS g (ts)
 CROSS JOIN LATERAL (SELECT g.ts::date AS day) AS d
  LEFT JOIN ny AS h ON h.day = d.day
 GROUP BY d.day;

COMMENT ON VIEW citibike.calendar_day IS
    'One row per date over the years in citibike.holiday (US/NY). has_public_holiday = the actual date of a public holiday; is_observed_day_off = the weekday off for one that fell on a weekend.';
