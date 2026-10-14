-- One row per Parquet file under raw/v2: the files on disk as a table, so they show up in a SQL client.
-- Reads only the file system entry and the Parquet footer (row count), never the data - cheap to query.
-- Run as the ingest user:
--   docker compose exec -T postgres psql -U $INGEST_DB_USER -d citibike -f - < postgres/sql/parquet_files_view.sql

CREATE OR REPLACE VIEW citibike_meta.parquet_files AS
SELECT
    r['region']::text               AS region,
    r['year']::integer              AS year,
    r['month']::integer             AS month,
    r['num_rows']::bigint           AS row_count,
    r['size']::bigint               AS size_bytes,
    round(r['size']::numeric / 1e6, 1) AS size_mb,
    r['num_row_groups']::bigint     AS row_groups,
    r['last_modified']::timestamptz AS written_at,
    r['filename']::text             AS path
FROM duckdb.query($q$
    SELECT b.filename, b.size, b.last_modified, m.num_rows, m.num_row_groups,
           regexp_extract(b.filename, 'region=([^/]+)', 1)               AS region,
           CAST(regexp_extract(b.filename, 'year=(\d+)', 1) AS INTEGER)  AS year,
           CAST(regexp_extract(b.filename, 'month=(\d+)', 1) AS INTEGER) AS month
    FROM read_blob('/data/citibike/raw/v2/**/*.parquet') b
    JOIN parquet_file_metadata('/data/citibike/raw/v2/**/*.parquet') m ON m.file_name = b.filename
    ORDER BY b.filename
$q$) r;

GRANT SELECT ON citibike_meta.parquet_files TO citibike_reader;
