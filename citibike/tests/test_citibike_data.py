"""Tests for citibike_data on small synthetic zips. No network, no real downloads.

Run from the repo root:  python -m pytest citibike/tests -q
"""
import io
import zipfile
from pathlib import Path

import pandas as pd
import pyarrow as pa
import pytest

import citibike_data as cb

# ---- sample CSVs in the layouts found in the bucket ------------------------------------------

OLD_2013_QUOTED = (
    '"tripduration","starttime","stoptime","start station id","start station name",'
    '"start station latitude","start station longitude","end station id","end station name",'
    '"end station latitude","end station longitude","bikeid","usertype","birth year","gender"\n'
    '"1010","2013-09-01 00:00:02","2013-09-01 00:16:52","254","W 11 St & 6 Ave","40.73532427",'
    '"-73.99800419","147","Greenwich St & Warren St","40.71542197","-74.01121978","15014","Subscriber","1974","1"\n'
    '"1443","2013-09-01 00:00:09","2013-09-01 00:24:12","151","Cleveland Pl & Spring St","40.7218158",'
    '"-73.99720307","497","E 17 St & Broadway","40.73704984","-73.99009296","19393","Customer",\\N,"0"\n'
)

OLD_2015_SLASH_DATES = (
    "tripduration,starttime,stoptime,start station id,start station name,start station latitude,"
    "start station longitude,end station id,end station name,end station latitude,end station longitude,"
    "bikeid,usertype,birth year,gender\n"
    "241,4/1/2015 00:00:23,4/1/2015 00:04:25,494,W 26 St & 8 Ave,40.74734825,-73.99723551,489,"
    "10 Ave & W 28 St,40.75066386,-74.00176802,15510,Subscriber,1992.0,2\n"
    "578,4/1/2015 00:00:52,4/1/2015 00:10:31,82,St James Pl & Pearl St,40.71117416,-74.00016545,2008,"
    "Little West St & 1 Pl,40.70569254,-74.01677685,15014,Subscriber,,1\n"
)

OLD_2017_CAPITALISED = (
    "Trip Duration,Start Time,Stop Time,Start Station ID,Start Station Name,Start Station Latitude,"
    "Start Station Longitude,End Station ID,End Station Name,End Station Latitude,End Station Longitude,"
    "Bike ID,User Type,Birth Year,Gender\n"
    "195,2017-01-01 00:00:58,2017-01-01 00:04:14,217,Old Fulton St,40.70277159,-73.99383605,430,"
    "York St & Jay St,40.7014851,-73.98656928,25454,Subscriber,1966.0,1\n"
)

OLD_2020_JC_FRACTIONS = (
    '"tripduration","starttime","stoptime","start station id","start station name","start station latitude",'
    '"start station longitude","end station id","end station name","end station latitude",'
    '"end station longitude","bikeid","usertype","birth year","gender"\n'
    '226,"2020-01-01 00:04:50.1920","2020-01-01 00:08:37.0370",3186,"Grove St PATH",40.71958611647166,'
    '-74.04311746358871,3211,"Newark Ave",40.72152515,-74.046304543,29444,"Subscriber",1984,2\n'
)

NEW_2024 = (
    "ride_id,rideable_type,started_at,ended_at,start_station_name,start_station_id,end_station_name,"
    "end_station_id,start_lat,start_lng,end_lat,end_lng,member_casual\n"
    "62EF1AC5BE598131,classic_bike,2024-01-24 09:03:33.533,2024-01-24 09:06:53.535,E 102 St & 1 Ave,7407.13,"
    "E 103 St & Lexington Ave,7463.09,40.7869946,-73.94164802,40.7903051,-73.94755757,member\n"
    "8464E543DAB27DBF,electric_bike,2024-01-30 08:21:29.510,2024-01-30 08:29:03.304,E 102 St & 1 Ave,7407.13,"
    "E 91 St & 2 Ave,7286.01,40.7869946,-73.94164802,40.78115276427915,-73.94963040947914,casual\n"
)

NEW_2025_JC_QUOTED_EMPTY_END = (
    '"ride_id","rideable_type","started_at","ended_at","start_station_name","start_station_id",'
    '"end_station_name","end_station_id","start_lat","start_lng","end_lat","end_lng","member_casual"\n'
    '"929852F0DC04F49C","electric_bike","2025-10-08 06:59:53.767","2025-10-08 07:04:08.012","Warren St","JC006",'
    '"Essex Light Rail",,40.7211236,-74.03805095,,,"member"\n'
)


def make_zip(path: Path, members: dict[str, bytes | str]) -> Path:
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as zf:
        for name, content in members.items():
            zf.writestr(name, content.encode() if isinstance(content, str) else content)
    return path


def zip_bytes(members: dict[str, str]) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for name, content in members.items():
            zf.writestr(name, content)
    return buf.getvalue()


def opener_for(text: str) -> cb.Opener:
    return lambda: io.BytesIO(text.encode())


# ---- names ----------------------------------------------------------------------------------------

@pytest.mark.parametrize("name, region, year, month", [
    ("2013-citibike-tripdata.zip", "nyc", 2013, None),
    ("202401-citibike-tripdata.zip", "nyc", 2024, 1),
    ("JC-202001-citibike-tripdata.csv.zip", "jc", 2020, 1),
    ("JC-201708 citibike-tripdata.csv.zip", "jc", 2017, 8),   # space instead of dash
    ("JC-202207-citbike-tripdata.csv.zip", "jc", 2022, 7),    # typo in the bucket
    ("JC-202510-citibike-tripdata.zip", "jc", 2025, 10),      # no .csv in the name
])
def test_parse_remote_name(name, region, year, month):
    remote = cb.parse_remote_name(name, 123)
    assert (remote.region, remote.year, remote.month, remote.size) == (region, year, month, 123)
    assert remote.url.startswith(cb.BUCKET_URL) and " " not in remote.url


def test_parse_remote_name_rejects_other_files():
    assert cb.parse_remote_name("index.html") is None
    assert cb.parse_remote_name("stations.csv") is None


@pytest.mark.parametrize("name, month, part", [
    ("201309-citibike-tripdata.csv", (2013, 9), 0),
    ("201309-citibike-tripdata_2.csv", (2013, 9), 2),
    ("201704-citibike-tripdata.csv_1.csv", (2017, 4), 1),      # 2017 bundle style
    ("JC-202001-citibike-tripdata.csv", (2020, 1), 0),
    ("202608-citibike-tripdata_6.csv", (2026, 8), 6),
    ("202604-citibike-tripdata-part1.csv", (2026, 4), 1),     # April 2026 style
    ("202604-citibike-tripdata_part12.csv", (2026, 4), 12),
])
def test_csv_name_pattern(name, month, part):
    m = cb.CSV_RE.match(name)
    assert (int(m["year"]), int(m["month"])) == month
    assert (int(m["part"]) if m["part"] else 0) == part


# ---- months and planning -------------------------------------------------------------------------

def test_parse_month_and_range():
    assert cb.parse_month("2026-08") == (2026, 8)
    assert cb.parse_month((2026, 8)) == (2026, 8)
    assert cb.parse_month(None) is None
    assert cb.month_range((2025, 11), (2026, 2)) == [(2025, 11), (2025, 12), (2026, 1), (2026, 2)]
    with pytest.raises(ValueError):
        cb.parse_month("202608")
    with pytest.raises(ValueError):
        cb.month_range((2026, 2), (2026, 1))


FILES = [
    cb.RemoteFile("nyc", 2013, None, "2013-citibike-tripdata.zip", 10),
    cb.RemoteFile("nyc", 2023, None, "2023-citibike-tripdata.zip", 10),
    cb.RemoteFile("nyc", 2024, 1, "202401-citibike-tripdata.zip", 10),
    cb.RemoteFile("nyc", 2024, 2, "202402-citibike-tripdata.zip", 10),
    cb.RemoteFile("jc", 2024, 1, "JC-202401-citibike-tripdata.csv.zip", 1),
]


def test_available_months_skips_before_service_start():
    months = cb.available_months("nyc", FILES)
    assert months[0] == (2013, 6)          # yearly bundle, but the service started in June
    assert (2023, 12) in months and (2024, 2) in months
    assert cb.available_months("jc", FILES) == [(2024, 1)]


def test_plan_downloads_prefers_monthly_file_and_dedupes_bundle():
    plan = cb.plan_downloads("nyc", [(2023, 11), (2023, 12), (2024, 1)], FILES)
    assert [f.name for f in plan] == ["2023-citibike-tripdata.zip", "202401-citibike-tripdata.zip"]


def test_plan_downloads_reports_missing_months():
    with pytest.raises(ValueError, match="2024-03"):
        cb.plan_downloads("nyc", [(2024, 3)], FILES)


# ---- reading CSVs ---------------------------------------------------------------------------------

def read_all(text: str) -> pd.DataFrame:
    tables = list(cb.read_trip_csv(opener_for(text)))
    assert all(t.schema.equals(cb.SCHEMA) for t in tables)
    return pa.concat_tables(tables).to_pandas()


def test_old_layout_quoted_with_mysql_nulls():
    df = read_all(OLD_2013_QUOTED)
    assert len(df) == 2
    assert df.duration_s.tolist() == [1010, 1443]                 # recomputed, equals tripduration
    assert df.member_casual.tolist() == ["member", "casual"]
    assert df.start_station_id.tolist() == ["254", "151"]        # ids stay strings
    assert df.bike_id.tolist() == [15014, 19393]
    assert df.birth_year.tolist()[0] == 1974 and pd.isna(df.birth_year[1])   # "\N" -> null
    assert df.gender.tolist() == [1, 0]
    assert df.ride_id.isna().all() and df.rideable_type.isna().all()
    assert str(df.started_at[0]) == "2013-09-01 00:00:02"


def test_old_layout_slash_dates_and_float_birth_year():
    df = read_all(OLD_2015_SLASH_DATES)
    assert df.started_at.dt.strftime("%Y-%m-%d %H:%M:%S").tolist() == ["2015-04-01 00:00:23", "2015-04-01 00:00:52"]
    assert df.birth_year.tolist()[0] == 1992 and pd.isna(df.birth_year[1])
    assert df.duration_s.tolist() == [242, 579]                  # stop - start; the file's own tripduration is 1 s off


def test_old_layout_capitalised_headers():
    df = read_all(OLD_2017_CAPITALISED)
    assert df.start_station_name[0] == "Old Fulton St" and df.bike_id[0] == 25454 and df.birth_year[0] == 1966


def test_old_layout_four_digit_fractions():
    df = read_all(OLD_2020_JC_FRACTIONS)
    assert df.started_at[0] == pd.Timestamp("2020-01-01 00:04:50.192")
    assert df.duration_s[0] == 227


def test_new_layout():
    df = read_all(NEW_2024)
    assert df.ride_id.tolist() == ["62EF1AC5BE598131", "8464E543DAB27DBF"]
    assert df.rideable_type.tolist() == ["classic_bike", "electric_bike"]
    assert df.start_station_id.tolist() == ["7407.13", "7407.13"]
    assert df.member_casual.tolist() == ["member", "casual"]
    assert df.duration_s.tolist() == [200, 454]
    assert df.bike_id.isna().all() and df.birth_year.isna().all() and df.gender.isna().all()


def test_new_layout_quoted_with_empty_end_station():
    df = read_all(NEW_2025_JC_QUOTED_EMPTY_END)
    assert df.start_station_id[0] == "JC006"
    assert pd.isna(df.end_station_id[0]) and pd.isna(df.end_lat[0])
    assert df.end_station_name[0] == "Essex Light Rail"


def test_unknown_column_is_ignored_and_missing_timestamp_fails():
    df = read_all(NEW_2024.replace("member_casual", "member_casual,extra") .replace(",member\n", ",member,x\n").replace(",casual\n", ",casual,y\n"))
    assert len(df) == 2
    with pytest.raises(ValueError, match="ended_at"):
        list(cb.read_trip_csv(opener_for("ride_id,started_at\nA,2024-01-01 00:00:00\n")))


def test_malformed_rows_are_skipped():
    text = NEW_2024 + "this,row,is,broken\n"
    assert len(read_all(text)) == 2


def test_small_blocks_keep_timestamp_format_per_block():
    text = OLD_2015_SLASH_DATES + "".join(
        f"100,4/{d}/2015 10:00:00,4/{d}/2015 10:01:40,1,A,40.0,-74.0,2,B,40.1,-74.1,1,Customer,1990,1\n"
        for d in range(2, 30)
    )
    tables = list(cb.read_trip_csv(opener_for(text), block_size=600))
    assert len(tables) > 1
    df = pa.concat_tables(tables).to_pandas()
    assert len(df) == 30 and df.started_at.notna().all()


# ---- zips: junk, duplicates, nesting -------------------------------------------------------------

def test_scan_zip_drops_junk_and_prefers_split_parts(tmp_path):
    path = make_zip(tmp_path / "2018-citibike-tripdata.zip", {
        "2018-citibike-tripdata/": "",
        "__MACOSX/2018-citibike-tripdata/._201804-citibike-tripdata.csv": "junk",
        "2018-citibike-tripdata/.DS_Store": "junk",
        "2018-citibike-tripdata/201804-citibike-tripdata.csv": OLD_2017_CAPITALISED,          # unsplit copy
        "2018-citibike-tripdata/201804-citibike-tripdata_1.csv": OLD_2017_CAPITALISED,        # parts at the root
        "2018-citibike-tripdata/201804-citibike-tripdata_2.csv": OLD_2017_CAPITALISED,
        "2018-citibike-tripdata/4_April/201804-citibike-tripdata_1.csv": OLD_2017_CAPITALISED,  # same parts again
        "2018-citibike-tripdata/4_April/201804-citibike-tripdata_2.csv": OLD_2017_CAPITALISED,
        "2018-citibike-tripdata/201805-citibike-tripdata.csv": OLD_2017_CAPITALISED,
    })
    with zipfile.ZipFile(path) as zf:
        csvs, nested = cb.scan_zip(zf)
    assert nested == []
    assert [(e.month, e.part) for e in csvs] == [((2018, 4), 1), ((2018, 4), 2), ((2018, 5), 0)]


def test_iter_trip_csvs_unpacks_nested_monthly_zips(tmp_path):
    inner_jan = zip_bytes({"202001-citibike-tripdata_1.csv": NEW_2024, "202001-citibike-tripdata_2.csv": NEW_2024})
    inner_feb = zip_bytes({"202002-citibike-tripdata_1.csv": NEW_2024, "__MACOSX/._x.csv": "junk"})
    path = make_zip(tmp_path / "2020-citibike-tripdata.zip", {
        "2020-citibike-tripdata/202002-citibike-tripdata.zip": inner_feb,
        "2020-citibike-tripdata/202001-citibike-tripdata.zip": inner_jan,
    })
    tmp = tmp_path / "tmp"
    seen = [(m, p, sum(t.num_rows for t in cb.read_trip_csv(op))) for m, p, op in cb.iter_trip_csvs(path, tmp)]
    assert seen == [((2020, 1), 1, 2), ((2020, 1), 2, 2), ((2020, 2), 1, 2)]
    assert list(tmp.glob("*.zip")) == []                          # nested zips cleaned up

    only_feb = [m for m, _, _ in cb.iter_trip_csvs(path, tmp, months={(2020, 2)})]
    assert only_feb == [(2020, 2)]


# ---- end to end on local files ---------------------------------------------------------------------

def test_build_dataset_converts_loads_and_is_idempotent(tmp_path, monkeypatch):
    raw, trips = tmp_path / "raw", tmp_path / "trips"
    files = [
        cb.RemoteFile("nyc", 2013, None, "2013-citibike-tripdata.zip", 0),
        cb.RemoteFile("nyc", 2024, 1, "202401-citibike-tripdata.zip", 0),
    ]
    zips = {
        "2013-citibike-tripdata.zip": {
            "2013-citibike-tripdata/201309-citibike-tripdata.csv": OLD_2013_QUOTED,
            "2013-citibike-tripdata/201310-citibike-tripdata.csv": OLD_2013_QUOTED,
        },
        "202401-citibike-tripdata.zip": {
            "202401-citibike-tripdata_1.csv": NEW_2024,
            "202401-citibike-tripdata_2.csv": NEW_2024,
        },
    }
    downloads = []

    def fake_download(remote, raw_dir, session=None, progress=True, attempts=5):
        downloads.append(remote.name)
        raw_dir.mkdir(parents=True, exist_ok=True)
        return make_zip(raw_dir / remote.name, zips[remote.name])

    monkeypatch.setattr(cb, "download", fake_download)

    # A range with a gap in the bucket is refused before anything is downloaded.
    with pytest.raises(ValueError, match="2014-01"):
        cb.build_dataset("nyc", "2013-10", "2024-01", raw_dir=raw, trips_dir=trips, files=files)
    assert downloads == []

    # Yearly bundle: only the requested months are converted; months without data are just warned about.
    result = cb.build_dataset("nyc", "2013-10", "2013-12", raw_dir=raw, trips_dir=trips, files=files)
    assert result[["year", "month", "rows"]].values.tolist() == [[2013, 10, 2]]
    assert cb.month_path(trips, "nyc", (2013, 10)).exists()
    assert not cb.month_path(trips, "nyc", (2013, 9)).exists()      # not requested

    # Monthly file with two parts, deleted after conversion.
    result = cb.build_dataset("nyc", "2024-01", "2024-01", raw_dir=raw, trips_dir=trips, files=files)
    assert result[["year", "month", "rows"]].values.tolist() == [[2024, 1, 4]]
    assert downloads == ["2013-citibike-tripdata.zip", "202401-citibike-tripdata.zip"]
    assert not (raw / "202401-citibike-tripdata.zip").exists()

    summary = cb.dataset_summary(trips)
    assert summary.rows.tolist() == [2, 4]

    # Second run: nothing to do, nothing downloaded.
    again = cb.build_dataset("nyc", "2024-01", "2024-01", raw_dir=raw, trips_dir=trips, files=files)
    assert again.empty and len(downloads) == 2

    # Loading with month filter and column selection; partition columns come back as well.
    df = cb.load_trips("nyc", "2024-01", "2024-01", columns=["started_at", "member_casual"], trips_dir=trips)
    assert len(df) == 4 and set(df.columns) == {"started_at", "member_casual"}
    everything = cb.load_trips("nyc", trips_dir=trips)
    assert len(everything) == 6 and set(everything.year) == {2013, 2024} and set(everything.region) == {"nyc"}
    assert everything.started_at.dtype == "datetime64[us]"
    arrow = cb.load_trips("nyc", trips_dir=trips, arrow_dtypes=True, columns=["ride_id"])
    assert isinstance(arrow.ride_id.dtype, pd.ArrowDtype)
