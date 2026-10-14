-- Unified view over the v2 Parquet files (TODO.md §5). Run once as the ingest user after the first
-- month has been ingested:
--   docker compose exec -T postgres psql -U $INGEST_DB_USER -d citibike -f - < postgres/sql/citibike_trips_view.sql
--
-- The glob picks up new monthly files automatically. Cleaning rules (document these for the team):
--   * rows without start or end time are dropped
--   * durations under 60 s (false starts / re-docks) and over 24 h (unreturned bikes) are dropped
--
-- pg_duckdb 0.3.x syntax: columns of read_parquet() are addressed as r['name'] on the alias r and
-- cast to Postgres types so the view has a fixed, typed column list (verified 2026-09-27 on 17-v0.3.1).

CREATE OR REPLACE VIEW citibike.trips AS
SELECT
    r['ride_id']::text                      AS ride_id,
    r['started_at']::timestamp              AS started_at,
    r['ended_at']::timestamp                AS ended_at,
    r['duration_s']::integer                AS duration_s,
    r['start_station_id']::text             AS start_station_id,
    r['start_station_name']::text           AS start_station_name,
    r['start_lat']::double precision        AS start_lat,
    r['start_lng']::double precision        AS start_lng,
    r['end_station_id']::text               AS end_station_id,
    r['end_station_name']::text             AS end_station_name,
    r['end_lat']::double precision          AS end_lat,
    r['end_lng']::double precision          AS end_lng,
    r['rideable_type']::text                AS rideable_type,
    r['member_casual']::text                AS member_casual,
    r['region']::text                       AS region,
    'v2'::text                              AS schema_version
FROM read_parquet('/data/citibike/raw/v2/region=*/year=*/month=*/trips.parquet',
                  union_by_name := true, hive_partitioning := true) r
WHERE r['started_at'] IS NOT NULL
  AND r['ended_at'] IS NOT NULL
  AND r['duration_s'] BETWEEN 60 AND 24 * 3600;

GRANT SELECT ON citibike.trips TO citibike_reader;
