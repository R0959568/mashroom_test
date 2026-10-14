"""Export the holidays of a range of years from the `holidays` package as JSON.

The backend calls this once per calendar load (POST /api/calendar/runs). It can also be run by hand:

    python citibike/ingest/export_holidays.py --from-year 2021 --to-year 2028 --country US --subdiv NY \
        --categories public,unofficial

The result goes to stdout as one JSON document, errors go to stderr with exit code 1:

    {"source": "holidays", "source_version": "0.105", "country": "US", "subdiv": "NY",
     "holidays": [{"day": "2022-12-25", "name": "Christmas Day", "category": "public", "observed": false},
                  {"day": "2022-12-26", "name": "Christmas Day (observed)", "category": "public", "observed": true}]}

The rows are the package's output unchanged: no mapping to day types happens here. A holiday that falls on a
weekend yields two rows, the actual date and the observed day off; `observed` tells them apart by comparing
with the package's own observed=False output, not by parsing the name. The package computes everything from
rules in its source code and makes no network calls.
"""
from __future__ import annotations

import argparse
import json
import sys

import holidays


def export(country: str, subdiv: str | None, from_year: int, to_year: int, categories: list[str]) -> dict:
    """One dict per (day, name, category), sorted by day; see the module docstring for the shape."""
    supported = holidays.country_holidays(country, subdiv=subdiv).supported_categories
    unknown = sorted(set(categories) - set(supported))
    if unknown:
        raise ValueError(f"categories {unknown} not supported for {country}; supported: {sorted(supported)}")
    years = range(from_year, to_year + 1)
    rows = []
    for category in categories:
        with_observed = holidays.country_holidays(country, subdiv=subdiv, years=years, categories=(category,))
        actual = holidays.country_holidays(country, subdiv=subdiv, years=years, categories=(category,), observed=False)
        for day in sorted(with_observed):
            for name in with_observed.get_list(day):
                rows.append({
                    "day": day.isoformat(),
                    "name": name,
                    "category": category,
                    "observed": name not in actual.get_list(day),
                })
    rows.sort(key=lambda r: (r["day"], r["category"], r["name"]))
    return {
        "source": "holidays",
        "source_version": holidays.__version__,
        "country": country,
        "subdiv": subdiv,
        "holidays": rows,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Export holidays from the holidays package as JSON.")
    parser.add_argument("--from-year", required=True, type=int)
    parser.add_argument("--to-year", required=True, type=int)
    parser.add_argument("--country", default="US")
    parser.add_argument("--subdiv", default="NY", help="state or region code; empty = country-wide only")
    parser.add_argument("--categories", default="public", help="comma-separated, e.g. public,unofficial")
    args = parser.parse_args(argv)
    categories = [c.strip() for c in args.categories.split(",") if c.strip()]
    try:
        if args.from_year > args.to_year:
            raise ValueError("--from-year must be on or before --to-year")
        if not categories:
            raise ValueError("--categories must name at least one category")
        result = export(args.country, args.subdiv or None, args.from_year, args.to_year, categories)
    except Exception as exc:  # noqa: BLE001 - the backend reports the message, the exit code says it failed
        print(f"export_holidays: {exc}", file=sys.stderr)
        return 1
    json.dump(result, sys.stdout)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
