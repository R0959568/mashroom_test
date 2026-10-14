"""Download, unpack and assemble the NYC Citi Bike trip data.

Source: https://citibikenyc.com/system-data, which points to the public S3 bucket
https://s3.amazonaws.com/tripdata/ (September 2026: 176 files, 32 GB zipped).

The bucket mixes four packaging styles and two column layouts, so nothing here is
downloaded or unpacked by hand:

    2013-2019  YYYY-citibike-tripdata.zip         yearly bundle with the monthly CSVs inside
                                                   (some months are in there twice: split in
                                                   parts and unsplit; the rows are the same)
    2020-2023  YYYY-citibike-tripdata.zip         yearly bundle with one nested zip per month
    2024-      YYYYMM-citibike-tripdata.zip       monthly zip, CSV split in parts of ~200 MB
    2015-09-   JC-YYYYMM-citibike-tripdata.csv.zip  Jersey City / Hoboken, one small CSV per month

Column layouts:

    old   tripduration, starttime, stoptime, start/end station id, name, latitude, longitude,
          bikeid, usertype, birth year, gender
          (NYC bundles 2013-2019, Jersey City files until January 2021)
    new   ride_id, rideable_type, started_at, ended_at, start/end station name, id, lat, lng,
          member_casual
          (NYC bundles from 2020, monthly files, Jersey City files from February 2021)

Both layouts are mapped onto one schema (SCHEMA below) and written as one Parquet file per
month, Hive-partitioned so a subset can be loaded without touching the rest:

    data/citibike/trips/region=nyc/year=2026/month=08/trips.parquet

CSVs are streamed from the zip straight into Parquet, so memory use stays flat and the raw
CSVs never touch the disk. Zips are deleted after conversion unless keep_zip=True.

Command line:

    python citibike/citibike_data.py --region nyc --start 2025-09 --end 2026-08
    python citibike/citibike_data.py --list          # what is published
    python citibike/citibike_data.py --summary       # what is assembled locally

Notebook:

    from citibike_data import build_dataset, load_trips, dataset_summary
    build_dataset("nyc", "2025-09", "2026-08")
    trips = load_trips("nyc", "2026-06", "2026-08", columns=["started_at", "member_casual"])
"""
from __future__ import annotations

import argparse
import csv
import logging
import re
import shutil
import sys
import time
import zipfile
from dataclasses import dataclass
from itertools import groupby
from pathlib import Path
from typing import IO, Callable, Iterable, Iterator
from xml.etree import ElementTree

import pandas as pd
import pyarrow as pa
import pyarrow.compute as pc
import pyarrow.csv as pacsv
import pyarrow.dataset as ds
import pyarrow.parquet as pq
import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry

try:
    from tqdm.auto import tqdm
except ImportError:  # pragma: no cover
    tqdm = None

try:
    # Use the operating system's certificate store instead of certifi. Needed on managed laptops
    # behind a TLS-inspecting proxy; harmless everywhere else.
    import truststore

    truststore.inject_into_ssl()
except ImportError:  # pragma: no cover
    pass

__all__ = [
    "SCHEMA", "RemoteFile", "list_remote_files", "remote_files_table", "available_months",
    "plan_downloads", "download", "build_dataset", "dataset_summary", "load_trips",
    "month_path", "configure_logging",
]

log = logging.getLogger("citibike")

BUCKET_URL = "https://s3.amazonaws.com/tripdata/"
S3_NS = {"s3": "http://s3.amazonaws.com/doc/2006-03-01/"}
REPO_DIR = Path(__file__).resolve().parents[1]
DATA_DIR = REPO_DIR / "data" / "citibike"
RAW_DIR = DATA_DIR / "raw"
TRIPS_DIR = DATA_DIR / "trips"

Month = tuple[int, int]
Opener = Callable[[], IO[bytes]]

# The service started in June 2013 (NYC) and September 2015 (Jersey City); the yearly bundles
# would otherwise suggest that January 2013 exists.
FIRST_MONTH: dict[str, Month] = {"nyc": (2013, 6), "jc": (2015, 9)}

# Bucket keys: 2013-citibike-tripdata.zip, 202401-citibike-tripdata.zip,
# JC-202001-citibike-tripdata.csv.zip and the irregular "JC-201708 citibike-tripdata.csv.zip",
# "JC-202207-citbike-tripdata.csv.zip", "JC-202510-citibike-tripdata.zip".
FILE_RE = re.compile(
    r"^(?P<jc>JC-)?(?P<year>\d{4})(?P<month>\d{2})?[ -]cit(?:i)?bike-tripdata(?:\.csv)?\.zip$"
)
# CSV names inside the zips: 201309-citibike-tripdata.csv, 201309-citibike-tripdata_1.csv,
# 201704-citibike-tripdata.csv_1.csv, JC-202001-citibike-tripdata.csv and, from April 2026,
# 202604-citibike-tripdata-part1.csv.
CSV_RE = re.compile(
    r"^(?:JC-)?(?P<year>\d{4})(?P<month>\d{2})[ -]cit(?:i)?bike-tripdata(?:\.csv)?"
    r"(?:[_-](?:part)?(?P<part>\d+))?\.csv$"
)

SCHEMA = pa.schema([
    ("ride_id", pa.string()),            # new layout only
    ("rideable_type", pa.string()),      # new layout only: classic_bike, electric_bike, docked_bike
    ("started_at", pa.timestamp("us")),  # local New York time, no time zone attached
    ("ended_at", pa.timestamp("us")),
    ("duration_s", pa.int32()),          # ended_at - started_at in seconds, computed for both layouts
    ("start_station_id", pa.string()),   # old layout: "254"; new layout: "7407.13", "JC006"
    ("start_station_name", pa.string()),
    ("start_lat", pa.float64()),
    ("start_lng", pa.float64()),
    ("end_station_id", pa.string()),
    ("end_station_name", pa.string()),
    ("end_lat", pa.float64()),
    ("end_lng", pa.float64()),
    ("member_casual", pa.string()),      # member (old: Subscriber) or casual (old: Customer)
    ("bike_id", pa.int32()),             # old layout only
    ("birth_year", pa.int16()),          # old layout only
    ("gender", pa.int8()),               # old layout only: 0 unknown, 1 male, 2 female
])

# Raw CSV header -> canonical column. Keys are the header lower-cased with everything but letters
# removed, so "start station id", "Start Station ID" and "start_station_id" all match.
COLUMN_ALIASES = {
    "tripduration": "trip_duration",  # old layout; dropped, duration_s is recomputed
    "starttime": "started_at", "stoptime": "ended_at",
    "startedat": "started_at", "endedat": "ended_at",
    "startstationid": "start_station_id", "startstationname": "start_station_name",
    "startstationlatitude": "start_lat", "startstationlongitude": "start_lng",
    "startlat": "start_lat", "startlng": "start_lng",
    "endstationid": "end_station_id", "endstationname": "end_station_name",
    "endstationlatitude": "end_lat", "endstationlongitude": "end_lng",
    "endlat": "end_lat", "endlng": "end_lng",
    "bikeid": "bike_id", "usertype": "member_casual", "membercasual": "member_casual",
    "birthyear": "birth_year", "gender": "gender",
    "rideid": "ride_id", "rideabletype": "rideable_type",
}
# Types the CSV reader uses per canonical column. Every column gets an explicit type: the
# streaming reader infers types from the first block only, which breaks on a column that is
# empty at the top of a file. Numbers that appear as "1992.0" in some months are read as float.
CSV_TYPES = {
    "trip_duration": pa.string(), "started_at": pa.string(), "ended_at": pa.string(),
    "start_station_id": pa.string(), "start_station_name": pa.string(),
    "start_lat": pa.float64(), "start_lng": pa.float64(),
    "end_station_id": pa.string(), "end_station_name": pa.string(),
    "end_lat": pa.float64(), "end_lng": pa.float64(),
    "bike_id": pa.float64(), "member_casual": pa.string(),
    "birth_year": pa.float64(), "gender": pa.float64(),
    "ride_id": pa.string(), "rideable_type": pa.string(),
}
NULL_VALUES = ["", "NULL", "null", "\\N", "NaN", "nan", "N/A"]  # "\N" is how 2013 writes a missing birth year
MEMBER_CASUAL = {"Subscriber": "member", "Customer": "casual", "member": "member", "casual": "casual"}
# Timestamp formats seen in the data, tried in this order on the first rows of each file:
# "2013-09-01 00:00:02", "2018-09-01 00:00:05.2690", "2024-01-24 09:03:33.533" (all ISO 8601),
# "4/1/2015 00:00:23" and "1/1/2015 0:01".
TIMESTAMP_FORMATS = ["ISO8601", "%m/%d/%Y %H:%M:%S", "%m/%d/%Y %H:%M"]


# --------------------------------------------------------------------------------------------
# Listing the bucket
# --------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class RemoteFile:
    """One zip in the bucket. month is None for a yearly bundle."""

    region: str  # "nyc" or "jc"
    year: int
    month: int | None
    name: str
    size: int

    @property
    def url(self) -> str:
        return BUCKET_URL + requests.utils.quote(self.name)

    @property
    def label(self) -> str:
        return f"{self.year}-{self.month:02d}" if self.month else f"{self.year} (yearly bundle)"

    def covers(self, ym: Month) -> bool:
        return ym[0] == self.year and (self.month is None or ym[1] == self.month)


def parse_remote_name(name: str, size: int = 0) -> RemoteFile | None:
    m = FILE_RE.match(name)
    if not m:
        return None
    return RemoteFile(
        region="jc" if m["jc"] else "nyc",
        year=int(m["year"]),
        month=int(m["month"]) if m["month"] else None,
        name=name,
        size=size,
    )


def _file_key(f: RemoteFile):
    return (f.region, f.year, f.month or 0)


def make_session() -> requests.Session:
    session = requests.Session()
    retry = Retry(total=5, backoff_factor=1.0, status_forcelist=[429, 500, 502, 503, 504],
                  allowed_methods=["GET", "HEAD"])
    session.mount("https://", HTTPAdapter(max_retries=retry))
    session.headers["User-Agent"] = "cloud-ai-avenue-5 citibike downloader"
    return session


def list_remote_files(session: requests.Session | None = None) -> list[RemoteFile]:
    """Every trip-data zip in the bucket, sorted by region, year, month."""
    session = session or make_session()
    files: list[RemoteFile] = []
    token = None
    while True:
        params = {"list-type": "2", "max-keys": "1000"}
        if token:
            params["continuation-token"] = token
        response = session.get(BUCKET_URL, params=params, timeout=60)
        response.raise_for_status()
        root = ElementTree.fromstring(response.content)
        for item in root.findall("s3:Contents", S3_NS):
            key = item.findtext("s3:Key", namespaces=S3_NS) or ""
            size = int(item.findtext("s3:Size", namespaces=S3_NS) or 0)
            remote = parse_remote_name(key, size)
            if remote:
                files.append(remote)
            elif key != "index.html":
                log.warning("Unrecognised file in bucket, ignored: %s", key)
        if root.findtext("s3:IsTruncated", namespaces=S3_NS) != "true":
            break
        token = root.findtext("s3:NextContinuationToken", namespaces=S3_NS)
    return sorted(files, key=_file_key)


def remote_files_table(files: Iterable[RemoteFile]) -> pd.DataFrame:
    """The listing as a DataFrame, handy in a notebook."""
    return pd.DataFrame(
        [{"region": f.region, "year": f.year, "month": f.month, "name": f.name,
          "size_mb": round(f.size / 1e6, 1)} for f in files],
        columns=["region", "year", "month", "name", "size_mb"],
    )


# --------------------------------------------------------------------------------------------
# Months and planning
# --------------------------------------------------------------------------------------------

def parse_month(value: str | Month | None) -> Month | None:
    """'2026-08' or (2026, 8) -> (2026, 8); None stays None."""
    if value is None:
        return None
    if isinstance(value, tuple):
        return int(value[0]), int(value[1])
    m = re.fullmatch(r"(\d{4})-(\d{1,2})", str(value).strip())
    if not m:
        raise ValueError(f"Month must look like YYYY-MM, got {value!r}")
    year, month = int(m[1]), int(m[2])
    if not 1 <= month <= 12:
        raise ValueError(f"Month must be 1-12, got {value!r}")
    return year, month


def fmt_month(ym: Month) -> str:
    return f"{ym[0]}-{ym[1]:02d}"


def month_range(start: Month, end: Month) -> list[Month]:
    if start > end:
        raise ValueError(f"start {fmt_month(start)} is after end {fmt_month(end)}")
    out, (y, m) = [], start
    while (y, m) <= end:
        out.append((y, m))
        y, m = (y + 1, 1) if m == 12 else (y, m + 1)
    return out


def available_months(region: str, files: Iterable[RemoteFile]) -> list[Month]:
    """Months for which the bucket has a file (a yearly bundle counts for all its months)."""
    months: set[Month] = set()
    for f in files:
        if f.region != region:
            continue
        if f.month:
            months.add((f.year, f.month))
        else:
            months.update((f.year, m) for m in range(1, 13))
    first = FIRST_MONTH.get(region, (1900, 1))
    return sorted(ym for ym in months if ym >= first)


def plan_downloads(region: str, months: Iterable[Month], files: Iterable[RemoteFile]) -> list[RemoteFile]:
    """The smallest set of files that covers the months: monthly file if there is one, else the yearly bundle."""
    files = list(files)
    monthly = {(f.year, f.month): f for f in files if f.region == region and f.month}
    yearly = {f.year: f for f in files if f.region == region and f.month is None}
    chosen: dict[str, RemoteFile] = {}
    missing: list[Month] = []
    for ym in sorted(set(months)):
        remote = monthly.get(ym) or yearly.get(ym[0])
        if remote is None:
            missing.append(ym)
        else:
            chosen.setdefault(remote.name, remote)
    if missing:
        raise ValueError(f"No {region} data published for " + ", ".join(map(fmt_month, missing)))
    return sorted(chosen.values(), key=_file_key)


def month_path(trips_dir: Path, region: str, ym: Month) -> Path:
    return trips_dir / f"region={region}" / f"year={ym[0]}" / f"month={ym[1]:02d}" / "trips.parquet"


# --------------------------------------------------------------------------------------------
# Downloading
# --------------------------------------------------------------------------------------------

def download(remote: RemoteFile, raw_dir: Path = RAW_DIR, session: requests.Session | None = None,
             progress: bool = True, attempts: int = 5) -> Path:
    """Download one zip into raw_dir. Skips a complete file, resumes a partial one, retries on errors."""
    raw_dir.mkdir(parents=True, exist_ok=True)
    dest = raw_dir / remote.name
    if dest.exists() and dest.stat().st_size == remote.size:
        log.info("Already downloaded: %s", remote.name)
        return dest
    session = session or make_session()
    part = dest.with_name(dest.name + ".part")
    log.info("Downloading %s (%.0f MB)", remote.name, remote.size / 1e6)
    for attempt in range(1, attempts + 1):
        try:
            _download_to(session, remote, part, progress)
            break
        except (requests.RequestException, OSError) as exc:
            if attempt == attempts:
                raise
            wait = 5 * attempt
            log.warning("Download of %s failed (%s); retrying in %d s", remote.name, exc, wait)
            time.sleep(wait)
    size = part.stat().st_size
    if size != remote.size:
        part.unlink()
        raise OSError(f"{remote.name}: downloaded {size} bytes, expected {remote.size}")
    part.replace(dest)
    return dest


def _download_to(session: requests.Session, remote: RemoteFile, part: Path, progress: bool) -> None:
    done = part.stat().st_size if part.exists() else 0
    if done > remote.size:
        part.unlink()
        done = 0
    if done == remote.size:
        return
    headers = {"Range": f"bytes={done}-"} if done else {}
    with session.get(remote.url, headers=headers, stream=True, timeout=(30, 300)) as response:
        response.raise_for_status()
        if done and response.status_code != 206:
            done = 0  # the server ignored the Range header: start over
        bar = None
        if progress and tqdm is not None:
            bar = tqdm(total=remote.size, initial=done, unit="B", unit_scale=True,
                       unit_divisor=1024, desc=remote.name, leave=False)
        with open(part, "ab" if done else "wb") as f:
            for chunk in response.iter_content(chunk_size=8 << 20):
                f.write(chunk)
                if bar:
                    bar.update(len(chunk))
        if bar:
            bar.close()


# --------------------------------------------------------------------------------------------
# Looking inside the zips
# --------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class CsvEntry:
    month: Month
    part: int  # 0 for an unsplit file
    info: zipfile.ZipInfo


def _is_junk(name: str) -> bool:
    base = name.rsplit("/", 1)[-1]
    return name.endswith("/") or "__MACOSX" in name or base.startswith("._") or base == ".DS_Store"


def scan_zip(zf: zipfile.ZipFile) -> tuple[list[CsvEntry], list[zipfile.ZipInfo]]:
    """Trip CSVs (junk removed, duplicate months resolved, sorted) and nested zips in a Citi Bike zip."""
    by_month: dict[Month, dict[str, CsvEntry]] = {}
    nested: list[zipfile.ZipInfo] = []
    for zi in zf.infolist():
        if _is_junk(zi.filename):
            continue
        base = zi.filename.rsplit("/", 1)[-1]
        if base.lower().endswith(".zip"):
            nested.append(zi)
            continue
        m = CSV_RE.match(base)
        if not m:
            log.warning("Skipping unexpected member %s", zi.filename)
            continue
        month = (int(m["year"]), int(m["month"]))
        part = int(m["part"]) if m["part"] else 0
        # The same file can sit in two folders (2018 bundle): keep the first one.
        by_month.setdefault(month, {}).setdefault(base, CsvEntry(month, part, zi))
    csvs: list[CsvEntry] = []
    for month, entries in by_month.items():
        parts = [e for e in entries.values() if e.part]
        # Some bundles (2013, 2018) hold a month both as one CSV and split in parts, with the same
        # rows (the unsplit copy is quoted, the parts are not). Use the parts, like the 2024+ files.
        chosen = parts or list(entries.values())
        if parts and len(parts) != len(entries):
            log.info("%s: using the %d split parts, ignoring the unsplit copy", fmt_month(month), len(parts))
        numbers = sorted(e.part for e in chosen)
        if parts and numbers != list(range(1, len(numbers) + 1)):
            log.warning("%s: parts are not contiguous: %s", fmt_month(month), numbers)
        csvs.extend(chosen)
    csvs.sort(key=lambda e: (e.month, e.part))
    nested.sort(key=lambda zi: zi.filename)
    return csvs, nested


def iter_trip_csvs(zip_path: Path, tmp_dir: Path, months: set[Month] | None = None
                   ) -> Iterator[tuple[Month, int, Opener]]:
    """Yield (month, part, opener) for every trip CSV in a downloaded zip, in month order.

    Nested monthly zips (2020-2023 bundles) are unpacked to tmp_dir one at a time and removed
    again. The opener returns a fresh binary stream for the CSV, so a CSV can be read twice.
    """
    with zipfile.ZipFile(zip_path) as zf:
        yield from _iter_zip(zf, tmp_dir, months, depth=0)


def _iter_zip(zf: zipfile.ZipFile, tmp_dir: Path, months: set[Month] | None, depth: int
              ) -> Iterator[tuple[Month, int, Opener]]:
    csvs, nested = scan_zip(zf)
    for entry in csvs:
        if months is None or entry.month in months:
            yield entry.month, entry.part, (lambda zi=entry.info: zf.open(zi))
    for zi in nested:
        if depth >= 1:
            raise RuntimeError(f"Unexpected zip inside a nested zip: {zi.filename}")
        inner_name = zi.filename.rsplit("/", 1)[-1]
        inner_remote = parse_remote_name(inner_name)
        if months is not None and inner_remote and inner_remote.month:
            if (inner_remote.year, inner_remote.month) not in months:
                continue
        tmp_dir.mkdir(parents=True, exist_ok=True)
        inner_path = tmp_dir / inner_name
        log.info("Unpacking nested %s (%.0f MB)", inner_name, zi.file_size / 1e6)
        with zf.open(zi) as src, open(inner_path, "wb") as dst:
            shutil.copyfileobj(src, dst, 8 << 20)
        try:
            with zipfile.ZipFile(inner_path) as inner:
                yield from _iter_zip(inner, tmp_dir, months, depth + 1)
        finally:
            inner_path.unlink(missing_ok=True)


# --------------------------------------------------------------------------------------------
# CSV -> normalised Arrow batches
# --------------------------------------------------------------------------------------------

def _header_key(name: str) -> str:
    return re.sub(r"[^a-z]", "", name.lower())


def _read_header(stream: IO[bytes]) -> list[str]:
    line = stream.readline().decode("utf-8-sig")  # utf-8-sig drops a byte order mark
    return next(csv.reader([line.strip()]))


def read_trip_csv(opener: Opener, block_size: int = 32 << 20) -> Iterator[pa.Table]:
    """Stream a trip CSV (either layout) as Arrow tables in SCHEMA, one per block of block_size bytes."""
    with opener() as stream:
        header = _read_header(stream)
    canonical = [COLUMN_ALIASES.get(_header_key(h)) or f"unknown_{i}" for i, h in enumerate(header)]
    unknown = [h for h, c in zip(header, canonical) if c.startswith("unknown_")]
    if unknown:
        log.warning("Unknown CSV columns ignored: %s", unknown)
    for required in ("started_at", "ended_at"):
        if required not in canonical:
            raise ValueError(f"CSV has no {required} column; header was {header}")

    skipped = 0

    def skip_invalid_row(_row) -> str:
        nonlocal skipped
        skipped += 1
        return "skip"

    read_options = pacsv.ReadOptions(column_names=canonical, skip_rows=1, block_size=block_size)
    parse_options = pacsv.ParseOptions(invalid_row_handler=skip_invalid_row)
    convert_options = pacsv.ConvertOptions(
        column_types={c: CSV_TYPES.get(c, pa.string()) for c in canonical},
        null_values=NULL_VALUES, strings_can_be_null=True,
    )
    timestamp_formats: dict[str, str] = {}
    with opener() as stream:
        reader = pacsv.open_csv(stream, read_options=read_options, parse_options=parse_options,
                                convert_options=convert_options)
        for batch in reader:
            yield normalize(pa.Table.from_batches([batch]), timestamp_formats)
    if skipped:
        log.warning("Skipped %d malformed rows", skipped)


def normalize(table: pa.Table, timestamp_formats: dict[str, str] | None = None) -> pa.Table:
    """Map a table with canonical column names (either layout) onto SCHEMA."""
    timestamp_formats = {} if timestamp_formats is None else timestamp_formats
    n = table.num_rows
    cols: dict[str, pa.Array] = {}
    for field in SCHEMA:
        if field.name == "duration_s":
            continue
        if field.name in table.column_names:
            cols[field.name] = _convert(field.name, table[field.name], field.type, timestamp_formats)
        else:
            cols[field.name] = pa.nulls(n, field.type)
    diff = pc.subtract(cols["ended_at"], cols["started_at"])  # duration[us]
    seconds = pc.round(pc.divide(pc.cast(pc.cast(diff, pa.int64()), pa.float64()), 1e6))
    cols["duration_s"] = _to_int(seconds, pa.int32())
    return pa.table([cols[f.name] for f in SCHEMA], schema=SCHEMA)


def _convert(name: str, arr, typ: pa.DataType, timestamp_formats: dict[str, str]) -> pa.Array:
    if isinstance(arr, pa.ChunkedArray):
        arr = arr.combine_chunks()
    if pa.types.is_timestamp(typ):
        return _to_timestamp(arr, name, timestamp_formats)
    if name == "member_casual":
        return _map_values(arr, MEMBER_CASUAL)
    if pa.types.is_integer(typ):
        return _to_int(arr, typ)
    return pc.cast(arr, typ)


def _map_values(arr: pa.Array, mapping: dict[str, str]) -> pa.Array:
    """Replace values through a dict; anything not in the dict becomes null."""
    index = pc.index_in(arr, value_set=pa.array(list(mapping), pa.string()))
    return pc.take(pa.array(list(mapping.values()), pa.string()), index)


_INT_BOUNDS = {pa.int8(): (-128, 127), pa.int16(): (-32768, 32767), pa.int32(): (-2**31, 2**31 - 1)}


def _to_int(arr: pa.Array, typ: pa.DataType) -> pa.Array:
    """Round to integer; values outside the type's range become null instead of failing the file."""
    if pa.types.is_string(arr.type):
        arr = pc.cast(arr, pa.float64())
    if pa.types.is_floating(arr.type):
        arr = pc.round(arr)
    lo, hi = _INT_BOUNDS[typ]
    in_range = pc.and_kleene(pc.greater_equal(arr, lo), pc.less_equal(arr, hi))
    arr = pc.if_else(in_range, arr, pa.scalar(None, arr.type))
    return pc.cast(arr, typ)


def _detect_timestamp_format(sample: pd.Series) -> str:
    for fmt in TIMESTAMP_FORMATS:
        try:
            pd.to_datetime(sample, format=fmt)
            return fmt
        except (ValueError, TypeError):
            continue
    return "mixed"


def _to_timestamp(arr: pa.Array, name: str, timestamp_formats: dict[str, str]) -> pa.Array:
    if pa.types.is_timestamp(arr.type):
        return pc.cast(arr, pa.timestamp("us"))
    series = arr.to_pandas()
    fmt = timestamp_formats.get(name) or _detect_timestamp_format(series.dropna().head(50))
    timestamp_formats[name] = fmt
    try:
        parsed = pd.to_datetime(series, format=fmt)
    except (ValueError, TypeError):
        parsed = pd.to_datetime(series, format="mixed", errors="coerce")
        bad = int(parsed.isna().sum() - series.isna().sum())
        if bad:
            log.warning("%s: %d values could not be parsed as a timestamp and became null", name, bad)
        timestamp_formats.pop(name, None)  # detect again on the next block
    return pa.Array.from_pandas(parsed).cast(pa.timestamp("us"), safe=False)


# --------------------------------------------------------------------------------------------
# Assembling the Parquet dataset
# --------------------------------------------------------------------------------------------

def convert_month(region: str, ym: Month, openers: Iterable[Opener], trips_dir: Path = TRIPS_DIR) -> dict:
    """Stream all CSV parts of one month into one Parquet file. Returns a summary row."""
    final = month_path(trips_dir, region, ym)
    final.parent.mkdir(parents=True, exist_ok=True)
    tmp = final.with_name("_" + final.name + ".part")  # "_" prefix: ignored by pyarrow.dataset
    rows, t0 = 0, time.time()
    writer = pq.ParquetWriter(str(tmp), SCHEMA, compression="zstd")
    try:
        for part_no, opener in enumerate(openers, 1):
            part_rows = 0
            for table in read_trip_csv(opener):
                writer.write_table(table)
                part_rows += table.num_rows
            rows += part_rows
            log.info("%s %s: part %d converted, %s rows", region, fmt_month(ym), part_no, f"{part_rows:,}")
    finally:
        writer.close()
    tmp.replace(final)
    seconds = round(time.time() - t0, 1)
    log.info("%s %s: %s rows -> %s (%.1f MB, %.0f s)", region, fmt_month(ym), f"{rows:,}",
             final.relative_to(trips_dir), final.stat().st_size / 1e6, seconds)
    return {"region": region, "year": ym[0], "month": ym[1], "rows": rows,
            "parquet_mb": round(final.stat().st_size / 1e6, 1), "seconds": seconds}


def build_dataset(region: str = "nyc", start: str | Month | None = None, end: str | Month | None = None, *,
                  raw_dir: Path = RAW_DIR, trips_dir: Path = TRIPS_DIR, keep_zip: bool = False,
                  force: bool = False, progress: bool = True, files: list[RemoteFile] | None = None
                  ) -> pd.DataFrame:
    """Download, unpack and convert every month in [start, end] that is not assembled yet.

    region    "nyc" (New York City) or "jc" (Jersey City / Hoboken)
    start/end "YYYY-MM"; default: first and last published month of the region
    keep_zip  keep the downloaded zips in raw_dir (default: delete after a successful conversion)
    force     convert again even if the month's Parquet file exists

    Returns one row per month converted in this call (rows, size, seconds).
    """
    region = region.lower()
    if region not in FIRST_MONTH:
        raise ValueError(f"region must be one of {sorted(FIRST_MONTH)}, got {region!r}")
    files = list_remote_files() if files is None else files
    available = available_months(region, files)
    if not available:
        raise ValueError(f"The bucket has no files for region {region!r}")
    first = parse_month(start) or available[0]
    last = parse_month(end) or available[-1]
    wanted = month_range(first, last)
    todo = {ym for ym in wanted if force or not month_path(trips_dir, region, ym).exists()}
    log.info("%s %s..%s: %d months, %d already assembled, %d to do", region, fmt_month(first),
             fmt_month(last), len(wanted), len(wanted) - len(todo), len(todo))
    results: list[dict] = []
    if todo:
        remotes = plan_downloads(region, todo, files)
        log.info("%d file(s) to download, %.0f MB in total", len(remotes), sum(r.size for r in remotes) / 1e6)
        session = make_session()
        for remote in remotes:
            zip_path = download(remote, raw_dir, session, progress)
            months_in_zip = {ym for ym in todo if remote.covers(ym)}
            done_here: set[Month] = set()
            entries = iter_trip_csvs(zip_path, raw_dir / "tmp", months_in_zip)
            for ym, group in groupby(entries, key=lambda entry: entry[0]):
                results.append(convert_month(region, ym, (opener for _, _, opener in group), trips_dir))
                done_here.add(ym)
            for ym in sorted(months_in_zip - done_here):
                log.warning("%s has no data for %s", remote.name, fmt_month(ym))
            if not keep_zip:
                zip_path.unlink()
                log.info("Deleted %s", remote.name)
    columns = ["region", "year", "month", "rows", "parquet_mb", "seconds"]
    return pd.DataFrame(results, columns=columns)


def dataset_summary(trips_dir: Path = TRIPS_DIR, region: str | None = None) -> pd.DataFrame:
    """One row per assembled month with its row count and file size (read from Parquet metadata only)."""
    rows = []
    for path in sorted(trips_dir.glob("region=*/year=*/month=*/trips.parquet")):
        reg, year, month = (p.split("=", 1)[1] for p in path.parts[-4:-1])
        if region and reg != region:
            continue
        rows.append({"region": reg, "year": int(year), "month": int(month),
                     "rows": pq.read_metadata(path).num_rows,
                     "parquet_mb": round(path.stat().st_size / 1e6, 1)})
    return pd.DataFrame(rows, columns=["region", "year", "month", "rows", "parquet_mb"])


def load_trips(region: str = "nyc", start: str | Month | None = None, end: str | Month | None = None,
               columns: list[str] | None = None, *, trips_dir: Path = TRIPS_DIR,
               filter: ds.Expression | None = None, arrow_dtypes: bool = False) -> pd.DataFrame:
    """Load assembled trips as a DataFrame.

    Only the requested months and columns are read from disk. Pass columns=[...] for big ranges:
    a full NYC month is a few million rows. arrow_dtypes=True keeps strings as Arrow-backed
    columns, which uses a fraction of the memory of classic object columns.
    """
    if not trips_dir.exists():
        raise FileNotFoundError(f"{trips_dir} does not exist; run build_dataset() first")
    dataset = ds.dataset(str(trips_dir), format="parquet", partitioning="hive")
    expr = ds.field("region") == region
    ym = ds.field("year") * 100 + ds.field("month")
    first, last = parse_month(start), parse_month(end)
    if first:
        expr = expr & (ym >= first[0] * 100 + first[1])
    if last:
        expr = expr & (ym <= last[0] * 100 + last[1])
    if filter is not None:
        expr = expr & filter
    table = dataset.to_table(columns=columns, filter=expr)
    if arrow_dtypes:
        return table.to_pandas(types_mapper=pd.ArrowDtype)
    return table.to_pandas(self_destruct=True, split_blocks=True)


# --------------------------------------------------------------------------------------------
# Command line
# --------------------------------------------------------------------------------------------

def configure_logging(level: int = logging.INFO) -> None:
    logging.basicConfig(level=level, format="%(asctime)s %(levelname)s %(message)s", datefmt="%H:%M:%S")
    logging.getLogger("citibike").setLevel(level)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Download, unpack and assemble the Citi Bike trip data into Parquet files per month.",
        epilog=f"Data goes to {DATA_DIR}",
    )
    parser.add_argument("--region", choices=sorted(FIRST_MONTH), default="nyc",
                        help="nyc (New York City, default) or jc (Jersey City / Hoboken)")
    parser.add_argument("--start", help="first month as YYYY-MM (default: first published month)")
    parser.add_argument("--end", help="last month as YYYY-MM (default: last published month)")
    parser.add_argument("--keep-zip", action="store_true", help="keep the downloaded zips")
    parser.add_argument("--force", action="store_true", help="convert months again even if assembled")
    parser.add_argument("--list", action="store_true", help="list the published files and exit")
    parser.add_argument("--summary", action="store_true", help="show what is assembled locally and exit")
    args = parser.parse_args(argv)
    configure_logging()
    pd.set_option("display.width", 160)
    pd.set_option("display.max_rows", 500)
    if args.list:
        table = remote_files_table(list_remote_files())
        print(table.to_string(index=False))
        print(f"\n{len(table)} files, {table.size_mb.sum() / 1000:.1f} GB")
        return 0
    if args.summary:
        summary = dataset_summary(region=args.region)
        print(summary.to_string(index=False) if len(summary) else "Nothing assembled yet.")
        return 0
    result = build_dataset(args.region, args.start, args.end, keep_zip=args.keep_zip, force=args.force)
    if len(result):
        print(result.to_string(index=False))
    summary = dataset_summary(region=args.region)
    print(f"\nAssembled: {len(summary)} months, {summary.rows.sum():,} rows, {summary.parquet_mb.sum():.0f} MB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
