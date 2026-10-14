"""Tests for ingest/convert_zip.py: the subprocess contract the C# backend relies on."""
import json
import sys
from pathlib import Path

import pyarrow.parquet as pq

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ingest"))

import convert_zip  # noqa: E402
from test_citibike_data import NEW_2024, OLD_2017_CAPITALISED, make_zip, zip_bytes  # noqa: E402


def test_convert_zip_writes_hive_partitions_and_skips_old_years(tmp_path):
    # A yearly bundle with one month before min-year and one after, in nested monthly zips.
    path = make_zip(tmp_path / "2021-citibike-tripdata.zip", {
        "2021-citibike-tripdata/202012-citibike-tripdata.zip": zip_bytes({"202012-citibike-tripdata_1.csv": OLD_2017_CAPITALISED}),
        "2021-citibike-tripdata/202101-citibike-tripdata.zip": zip_bytes({
            "202101-citibike-tripdata_1.csv": NEW_2024, "202101-citibike-tripdata_2.csv": NEW_2024,
            "__MACOSX/._202101-citibike-tripdata_1.csv": "junk",
        }),
    })
    out = tmp_path / "raw" / "v2"
    months = convert_zip.convert_zip(path, out, "nyc", min_year=2021, tmp_dir=tmp_path / "tmp")

    assert [(m["year"], m["month"], m["rows"]) for m in months] == [(2021, 1, 4)]
    parquet = out / "region=nyc" / "year=2021" / "month=01" / "trips.parquet"
    assert parquet.exists() and months[0]["parquet_bytes"] == parquet.stat().st_size
    assert pq.read_metadata(parquet).num_rows == 4
    assert not (out / "region=nyc" / "year=2020").exists()      # before min-year
    assert list((tmp_path / "tmp").glob("*.zip")) == []          # nested zips cleaned up
    assert path.exists()                                         # deleting the zip is the caller's job


def test_main_prints_json_on_stdout_only(tmp_path, capsys):
    path = make_zip(tmp_path / "202401-citibike-tripdata.zip", {"202401-citibike-tripdata_1.csv": NEW_2024})
    code = convert_zip.main(["--zip", str(path), "--out", str(tmp_path / "out"), "--min-year", "2021"])
    captured = capsys.readouterr()
    assert code == 0
    assert json.loads(captured.out) == {"months": [{
        "region": "nyc", "year": 2024, "month": 1, "rows": 2,
        "parquet_bytes": (tmp_path / "out/region=nyc/year=2024/month=01/trips.parquet").stat().st_size,
        "seconds": json.loads(captured.out)["months"][0]["seconds"],
    }]}


def test_new_part_naming_is_converted(tmp_path):
    path = make_zip(tmp_path / "202604-citibike-tripdata.zip", {
        "202604-citibike-tripdata-part1.csv": NEW_2024, "202604-citibike-tripdata-part2.csv": NEW_2024,
    })
    months = convert_zip.convert_zip(path, tmp_path / "out", "nyc", min_year=2021)
    assert [(m["year"], m["month"], m["rows"]) for m in months] == [(2026, 4, 4)]


def test_in_scope_zip_without_recognised_csvs_fails(tmp_path, capsys):
    path = make_zip(tmp_path / "202604-citibike-tripdata.zip", {"trips-april-2026.csv": NEW_2024})
    assert convert_zip.main(["--zip", str(path), "--out", str(tmp_path / "out"), "--min-year", "2021"]) == 1
    assert capsys.readouterr().out == ""                         # no JSON -> backend marks the file failed


def test_out_of_scope_zip_without_months_is_not_an_error(tmp_path):
    path = make_zip(tmp_path / "2019-citibike-tripdata.zip", {"201901-citibike-tripdata.csv": OLD_2017_CAPITALISED})
    assert convert_zip.convert_zip(path, tmp_path / "out", "nyc", min_year=2021) == []


def test_main_fails_with_exit_code_on_bad_zip(tmp_path, capsys):
    bad = tmp_path / "202401-citibike-tripdata.zip"
    bad.write_bytes(b"not a zip")
    assert convert_zip.main(["--zip", str(bad), "--out", str(tmp_path / "out")]) == 1
    assert capsys.readouterr().out == ""
