"""Convert one downloaded Citi Bike zip into monthly Parquet files.

The ingest backend calls this once per zip (one subprocess per file). It can also be run by hand:

    python citibike/ingest/convert_zip.py --zip /data/citibike/landing/202608-citibike-tripdata.zip \
        --out /data/citibike/raw/v2 --region nyc --min-year 2021

Progress goes to stderr as log lines. The result goes to stdout as one JSON document:

    {"months": [{"region": "nyc", "year": 2026, "month": 8, "rows": 4012345,
                 "parquet_bytes": 98765432, "seconds": 41.2}]}

All quirk handling (nested zips, duplicate months, junk entries, both column layouts) lives in
citibike_data; this script only wires it to a command line the backend can call. Months before
--min-year are skipped, so a re-uploaded yearly bundle never writes old-layout data into the
v2 folder. Output is Hive-partitioned: <out>/region=nyc/year=2026/month=08/trips.parquet.
"""
from __future__ import annotations

import argparse
import json
import logging
import sys
from itertools import groupby
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # citibike/ -> citibike_data

import citibike_data as cb  # noqa: E402

log = logging.getLogger("citibike.convert")


def convert_zip(zip_path: Path, out_dir: Path, region: str, min_year: int,
                tmp_dir: Path | None = None) -> list[dict]:
    """Stream every month in the zip into <out_dir>; returns one summary dict per month written."""
    if region not in cb.FIRST_MONTH:
        raise ValueError(f"region must be one of {sorted(cb.FIRST_MONTH)}, got {region!r}")
    tmp_dir = tmp_dir or zip_path.parent / "tmp"
    results: list[dict] = []
    entries = cb.iter_trip_csvs(zip_path, tmp_dir)
    for ym, group in groupby(entries, key=lambda entry: entry[0]):
        if ym[0] < min_year:
            log.info("%s %s: before %d, skipped", region, cb.fmt_month(ym), min_year)
            continue
        summary = cb.convert_month(region, ym, (opener for _, _, opener in group), out_dir)
        results.append({
            "region": region, "year": ym[0], "month": ym[1], "rows": summary["rows"],
            "parquet_bytes": cb.month_path(out_dir, region, ym).stat().st_size,
            "seconds": summary["seconds"],
        })
    if not results:
        # A zip whose own name puts it in scope must produce data. Returning an empty result would mark the
        # file done and move the run's watermark past it, so the next run would never retry it (seen with
        # the April 2026 zip, whose CSV names were not recognised).
        remote = cb.parse_remote_name(zip_path.name)
        if remote is None or remote.year >= min_year:
            raise ValueError(f"{zip_path.name}: no trip CSVs recognised on or after {min_year}; "
                             "check the 'Skipping unexpected member' warnings above")
        log.warning("%s: no months on or after %d in this zip", zip_path.name, min_year)
    return results


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Convert one Citi Bike zip into monthly Parquet files.")
    parser.add_argument("--zip", required=True, type=Path, help="downloaded zip")
    parser.add_argument("--out", required=True, type=Path, help="Parquet root, e.g. /data/citibike/raw/v2")
    parser.add_argument("--region", default="nyc", choices=sorted(cb.FIRST_MONTH))
    parser.add_argument("--min-year", type=int, default=2021, help="skip months before this year")
    parser.add_argument("--tmp", type=Path, help="scratch folder for nested zips (default: next to the zip)")
    args = parser.parse_args(argv)
    cb.configure_logging()  # logging.basicConfig -> stderr, so stdout stays clean for the JSON
    try:
        months = convert_zip(args.zip, args.out, args.region, args.min_year, args.tmp)
    except Exception as exc:  # noqa: BLE001 - the backend reports the message, the exit code says it failed
        log.error("%s: %s", args.zip.name, exc)
        return 1
    json.dump({"months": months}, sys.stdout)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
