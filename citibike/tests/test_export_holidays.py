"""Tests for ingest/export_holidays.py: the subprocess contract the C# backend relies on."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ingest"))

import export_holidays  # noqa: E402


def rows_on(result, day):
    return [(h["name"], h["category"], h["observed"]) for h in result["holidays"] if h["day"] == day]


def test_a_weekend_holiday_yields_the_actual_date_and_the_observed_day_off():
    # Christmas 2022 fell on a Sunday: the pandas calendar in 03_hypothesis only knew Monday 26 December.
    result = export_holidays.export("US", "NY", 2022, 2022, ["public"])
    assert rows_on(result, "2022-12-25") == [("Christmas Day", "public", False)]
    assert rows_on(result, "2022-12-26") == [("Christmas Day (observed)", "public", True)]


def test_unofficial_category_brings_easter_and_christmas_eve():
    result = export_holidays.export("US", "NY", 2022, 2022, ["public", "unofficial"])
    assert rows_on(result, "2022-04-17") == [("Easter Sunday", "unofficial", False)]
    assert rows_on(result, "2022-12-24") == [("Christmas Eve", "unofficial", False)]


def test_new_york_subdivision_adds_state_holidays():
    ny = {h["name"] for h in export_holidays.export("US", "NY", 2022, 2022, ["public"])["holidays"]}
    federal = {h["name"] for h in export_holidays.export("US", None, 2022, 2022, ["public"])["holidays"]}
    assert "Election Day" in ny - federal


def test_rows_are_sorted_and_carry_the_package_version():
    result = export_holidays.export("US", "NY", 2021, 2023, ["public", "unofficial"])
    days = [h["day"] for h in result["holidays"]]
    assert days == sorted(days)
    assert result["source"] == "holidays" and result["source_version"]
    assert {d[:4] for d in days} == {"2021", "2022", "2023"}


def test_main_prints_json_on_stdout_only(capsys):
    assert export_holidays.main(["--from-year", "2024", "--to-year", "2024"]) == 0
    captured = capsys.readouterr()
    result = json.loads(captured.out)
    assert (result["country"], result["subdiv"]) == ("US", "NY")
    assert {h["category"] for h in result["holidays"]} == {"public"}
    assert captured.err == ""


def test_main_fails_with_exit_code_on_unknown_category(capsys):
    assert export_holidays.main(["--from-year", "2024", "--to-year", "2024", "--categories", "nope"]) == 1
    captured = capsys.readouterr()
    assert captured.out == ""                                    # no JSON -> backend marks the load failed
    assert "nope" in captured.err


def test_main_fails_when_the_range_is_reversed(capsys):
    assert export_holidays.main(["--from-year", "2025", "--to-year", "2024"]) == 1
    assert capsys.readouterr().out == ""
