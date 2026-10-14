-- Secondary Mushroom dataset (UCI 848, CC BY 4.0) for the second project, as plain Postgres tables.
-- The data is small (61k rows), so it lives in regular tables, not Parquet.
--
-- 1. As the postgres superuser (the ingest user cannot create schemas):
--      docker compose exec -T postgres psql -U postgres -d citibike -f - < postgres/sql/mushroom_schema.sql
-- 2. Load as the ingest user, from the folder with the CSVs (';'-separated, empty field = missing):
--      psql ... -c "\copy mushroom.secondary_data (<columns>) FROM STDIN WITH (FORMAT csv, DELIMITER ';', HEADER true)" < secondary_data.csv
--      psql ... -c "\copy mushroom.primary_data (<columns>) FROM STDIN WITH (FORMAT csv, DELIMITER ';', HEADER true)" < primary_data.csv
--
-- Columns keep the source names with '-' replaced by '_' and the source codes as-is (see the *_meta.txt
-- files of the dataset for what each letter means). Any recoding is modelling work for the notebooks.

CREATE SCHEMA IF NOT EXISTS mushroom AUTHORIZATION citibike_ingest;
GRANT USAGE ON SCHEMA mushroom TO citibike_reader;
ALTER DEFAULT PRIVILEGES FOR ROLE citibike_ingest IN SCHEMA mushroom GRANT SELECT ON TABLES TO citibike_reader;

SET ROLE citibike_ingest;   -- tables owned by the ingest user, like everything else it loads

-- 61,069 simulated mushrooms; class e = edible, p = poisonous. Measurements in cm (cap) and cm/mm (stem).
CREATE TABLE IF NOT EXISTS mushroom.secondary_data (
    id                    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    class                 text NOT NULL CHECK (class IN ('e', 'p')),
    cap_diameter          double precision,
    cap_shape             text,
    cap_surface           text,
    cap_color             text,
    does_bruise_or_bleed  text,
    gill_attachment       text,
    gill_spacing          text,
    gill_color            text,
    stem_height           double precision,
    stem_width            double precision,
    stem_root             text,
    stem_surface          text,
    stem_color            text,
    veil_type             text,
    veil_color            text,
    has_ring              text,
    ring_type             text,
    spore_print_color     text,
    habitat               text,
    season                text
);

-- 173 species the secondary data was simulated from. Values are ranges/lists as text, e.g. "[10, 20]", "[x, f]".
CREATE TABLE IF NOT EXISTS mushroom.primary_data (
    id                    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    family                text,
    name                  text,
    class                 text NOT NULL CHECK (class IN ('e', 'p')),
    cap_diameter          text,
    cap_shape             text,
    cap_surface           text,
    cap_color             text,
    does_bruise_or_bleed  text,
    gill_attachment       text,
    gill_spacing          text,
    gill_color            text,
    stem_height           text,
    stem_width            text,
    stem_root             text,
    stem_surface          text,
    stem_color            text,
    veil_type             text,
    veil_color            text,
    has_ring              text,
    ring_type             text,
    spore_print_color     text,
    habitat               text,
    season                text
);

RESET ROLE;
