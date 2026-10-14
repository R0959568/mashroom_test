"""Shared pieces of the model notebooks (05 - 10): the split in time, the cold-start rule, the feature table
and the metrics. Every notebook imports these, so the scores in 09 compare like with like.

Usage in a notebook under citibike/notebooks/:

    import sys
    sys.path.insert(0, "..")          # citibike/ -> modelling.py (and connection.py, which it uses)
    from modelling import load_features, scorable, score_table, TARGETS

    df = load_features()                                   # Parquet cache under data/citibike/, else the server
    valid = df[(df.split == "valid") & scorable(df, "day")]
    score_table(valid, {"seasonal naive": valid.dep_lag_7}, target=TARGETS["day"])

The feature table (citibike_prep.station_day_features, built in 05) has one row per station and target day t:
the forecast for t is made on the evening of t - 1, so every feature is known by then. Targets: y_day (departures
on t) and y_rest_of_month (departures from t to the last day of t's month).
"""
import json
from pathlib import Path

import joblib
import numpy as np
import pandas as pd

from connection import PREP_SCHEMA, copy_out, fmt

# The split in time (03 §6.3): never train on the future. The test period is scored once, in 09.
TRAIN_UNTIL = "2024-12-31"   # train: 2021 - 2024
VALID_UNTIL = "2025-12-31"   # validate: 2025, for model choice and tuning (06 - 08)
#                              test: 2026-01-01 onwards (January - August 2026 in the current data)
MIN_HISTORY_DAYS = 28        # cold start: no forecast before a station has 28 days in its active period (every lag exists)
TARGETS = {"day": "y_day", "rest_of_month": "y_rest_of_month"}

REPO = Path(__file__).resolve().parents[1]
FEATURES_PARQUET = REPO / "data" / "citibike" / "station_day_features.parquet"   # git-ignored cache of the server table
MODELS_DIR = REPO / "models" / "citibike"                                         # git-ignored; the notebooks recreate them
FEATURE_TABLE = f"{PREP_SCHEMA}.station_day_features"


def split_of(day: pd.Series) -> pd.Series:
    """'train', 'valid' or 'test' for a Series of dates."""
    return pd.Series(np.select([day <= TRAIN_UNTIL, day <= VALID_UNTIL], ["train", "valid"], default="test"),
                     index=day.index)


def load_features(refresh: bool = False) -> pd.DataFrame:
    """The feature table with a `split` column: from the Parquet cache, or from the server when there is no cache
    or refresh=True (after a rebuild of the table in 05). The station key is kept as text: 7386.10 is not 7386.1."""
    if FEATURES_PARQUET.exists() and not refresh:
        df = pd.read_parquet(FEATURES_PARQUET)
        print(f"{fmt(len(df))} rows from {FEATURES_PARQUET.relative_to(REPO)}")
    else:
        df = copy_out(f"SELECT * FROM {FEATURE_TABLE} ORDER BY station_key, day",
                      parse_dates=["day"], dtype={"station_key": "string"})
        FEATURES_PARQUET.parent.mkdir(parents=True, exist_ok=True)
        df.to_parquet(FEATURES_PARQUET, index=False)
        print(f"cached as {FEATURES_PARQUET.relative_to(REPO)} ({fmt(FEATURES_PARQUET.stat().st_size / 1e6, '.0f')} MB)")
    df["split"] = split_of(df.day)
    return df


def scorable(df: pd.DataFrame, horizon: str) -> pd.Series:
    """Rows a forecast is expected for: enough history for every lag, and an observed target."""
    return (df.history_days >= MIN_HISTORY_DAYS) & df[TARGETS[horizon]].notna()


def metrics(actual: pd.Series, predicted: pd.Series) -> dict:
    """MAE and RMSE in departures, WAPE in % of the actual volume, bias (mean of predicted - actual), n."""
    err = predicted - actual
    return {"MAE": err.abs().mean(), "RMSE": float(np.sqrt((err ** 2).mean())),
            "WAPE_%": err.abs().sum() / actual.sum() * 100, "bias": err.mean(), "n": int(len(actual))}


def score_table(df: pd.DataFrame, predictions: dict[str, pd.Series], target: str,
                by: str | None = None) -> pd.DataFrame:
    """One row per method (and per value of `by`), every method scored on the same rows.
    Each prediction Series must be aligned with df's index and complete: a NaN would silently drop rows."""
    groups = [(None, df.index)] if by is None else list(df.groupby(by, observed=True).groups.items())
    rows = []
    for method, pred in predictions.items():
        pred = pred.loc[df.index]
        assert not pred.isna().any(), f"{method}: {int(pred.isna().sum())} rows without a prediction"
        for group, idx in groups:
            rows.append({**({} if by is None else {by: group}), "method": method, **metrics(df.loc[idx, target], pred.loc[idx])})
    return pd.DataFrame(rows).set_index(["method"] if by is None else [by, "method"])

# ---- Model inputs, as defined in 05 §7 (05 keeps its own copy; from 06 on they are imported from here) -------------
WEATHER = ["tmax_c", "tmin_c", "prcp_mm", "snow_mm", "snow_depth_mm", "wind_ms"]
CALENDAR = ["iso_dow", "month"]                       # categories: a weekday or a month is not a number
FLAGS = ["holiday", "day_off", "holiday_lag_7"]
HISTORY = ["dep_lag_1", "dep_lag_7", "dep_lag_14", "dep_lag_28", "dep_mean_7", "dep_mean_4_weeks",
           "arr_lag_1", "arr_mean_7", "member_share_28", "ebike_share_28", "history_days"]
RATES = ["days_in_horizon", "day_of_month", "mtd_rate", "last_year_rate", "has_last_year"]
FEATURES_DAY = HISTORY + FLAGS + WEATHER + CALENDAR   # the day horizon knows the weather of the day (a forecast in the API)
FEATURES_REST = HISTORY + FLAGS + RATES + CALENDAR    # the rest-of-month horizon does not: no forecast weeks ahead


def design(d: pd.DataFrame) -> pd.DataFrame:
    """The model inputs for the rows of d: flags as 0/1, shares of a station without departures as 0, the
    rest-of-month rates with their fallbacks and the fallback flag. Identical to 05 §7."""
    x = d[HISTORY + FLAGS + WEATHER + CALENDAR + ["days_in_horizon", "day_of_month"]].copy()
    x[FLAGS] = x[FLAGS].astype(int)
    x[["member_share_28", "ebike_share_28"]] = x[["member_share_28", "ebike_share_28"]].fillna(0)
    x["mtd_rate"] = d.dep_mtd.fillna(d.dep_mean_7)
    last_year_rate = (d.dep_rest_of_month_ly * (d.dep_sum_28 / d.dep_sum_28_ly) / d.days_in_horizon).where(lambda s: np.isfinite(s))
    x["has_last_year"] = last_year_rate.notna().astype(int)
    x["last_year_rate"] = last_year_rate.fillna(d.dep_mean_7)
    assert not x.isna().any().any(), x.isna().sum()[lambda s: s > 0]
    return x


def rule_predictions(d: pd.DataFrame, horizon: str) -> dict[str, pd.Series]:
    """The rule baselines of 05 §6 for the rows of d, every rule defined on every scorable row."""
    if horizon == "day":
        return {"naive (yesterday)": d.dep_lag_1,
                "seasonal naive (same weekday last week)": d.dep_lag_7,
                "mean of the same weekday, 4 weeks": d.dep_mean_4_weeks}
    pace_week = d.dep_mean_7 * d.days_in_horizon
    pace_mtd = d.dep_mtd.fillna(d.dep_mean_7) * d.days_in_horizon
    last_year = (d.dep_rest_of_month_ly * (d.dep_sum_28 / d.dep_sum_28_ly)).where(lambda s: np.isfinite(s))
    return {"pace, month to date x days left": pace_mtd,
            "pace, last 7 days x days left": pace_week,
            "last year, same period x growth": last_year.fillna(pace_week)}


# ---- Model inputs: the contract between the feature table and citibike/train/train.py (07 §2) ----------------------
MODEL_INPUTS_DIR = REPO / "data" / "citibike" / "model_inputs"   # git-ignored; one folder per horizon, what 10 copies to S3
FEATURES = {"day": FEATURES_DAY, "rest_of_month": FEATURES_REST}
INPUT_META = ["split", "station_key", "day", "y", "scale", "actual"]   # the non-input columns of model_inputs()


def model_inputs(df: pd.DataFrame, horizon: str) -> pd.DataFrame:
    """The model-ready rows of df for one horizon, the layout train.py reads: split, station_key (text), day, y (the
    target on the model scale: departures for the day, departures per remaining day for the rest of the month), scale
    (prediction x scale = departures), actual (the departures to score), then the inputs of design() for the horizon.
    Scorable rows only (28 days of history and an observed target), in the order of df."""
    d = df[scorable(df, horizon)]
    x = design(d)[FEATURES[horizon]]
    if horizon == "day":
        scale = pd.Series(1.0, index=d.index)
        y, actual = d.y_day.astype("float64"), d.y_day
    else:
        scale = d.days_in_horizon.astype("float64")
        y, actual = d.y_rest_of_month / scale, d.y_rest_of_month
    head = pd.DataFrame({"split": d.split, "station_key": d.station_key.astype(str), "day": d.day,
                         "y": y, "scale": scale, "actual": actual.astype("int64")}, index=d.index)
    return pd.concat([head, x], axis=1)


def write_model_inputs(df: pd.DataFrame, horizon: str, folder: Path | None = None) -> pd.DataFrame:
    """model_inputs() as one Parquet file per split (train, valid, test) in folder, default MODEL_INPUTS_DIR/<horizon>.
    Returns rows, stations, columns and file size per split."""
    folder = Path(folder or MODEL_INPUTS_DIR / horizon)
    folder.mkdir(parents=True, exist_ok=True)
    inputs = model_inputs(df, horizon)
    rows = {}
    for split in ["train", "valid", "test"]:
        part = inputs[inputs.split == split].drop(columns="split")
        part.to_parquet(folder / f"{split}.parquet", index=False)
        rows[split] = {"rows": len(part), "stations": part.station_key.nunique(), "columns": part.shape[1],
                       "MB": round((folder / f"{split}.parquet").stat().st_size / 1e6, 1)}
    return pd.DataFrame(rows).T


# ---- Reading a run back (07 keeps its own copies; 08 and 09 import these) -------------------------------------------
def _on_rows(values, station_key: pd.Series, day: pd.Series, rows: pd.DataFrame, what) -> pd.Series:
    """values, keyed by (station_key, day), on the index of rows (feature-table rows), matched on station key and day.
    Every row must get one value: a missing prediction would otherwise drop a row from the score silently."""
    keyed = pd.Series(np.asarray(values), index=pd.MultiIndex.from_arrays([station_key.astype(str), day.astype("datetime64[ns]")]))
    wanted = pd.MultiIndex.from_arrays([rows.station_key.astype(str), rows.day.astype("datetime64[ns]")])
    aligned = keyed.reindex(wanted)
    assert not aligned.isna().any(), f"{int(aligned.isna().sum())} rows without a prediction in {what}"
    return pd.Series(aligned.to_numpy(), index=rows.index)


def run_predictions(run_dir: Path, rows: pd.DataFrame) -> pd.Series:
    """The validation predictions a train.py run saved (predictions_valid.parquet), on the index of rows: validation
    rows of the feature table, matched on station key and day. Every row must have one."""
    saved = pd.read_parquet(Path(run_dir) / "predictions_valid.parquet")
    return _on_rows(saved.prediction, saved.station_key, saved.day, rows, run_dir)


def model_predictions(run_dir: Path, rows: pd.DataFrame, inputs: pd.DataFrame) -> pd.Series:
    """The saved pipeline of a train.py run (model.joblib) applied to model inputs of 07 §2 (a file written by
    write_model_inputs(), for example test.parquet), as departures: the model's output times the row's scale, never
    below 0, as train.py predicts. On the index of rows, the feature-table rows the inputs were built from, matched
    on station key and day. The inputs the pipeline reads are the ones its metrics.json records. 09 scores the test
    period through this; the pipeline is loaded, used and dropped, so the 1 GB extra trees model stays in memory
    only while it predicts."""
    run_dir = Path(run_dir)
    features = json.loads((run_dir / "metrics.json").read_text(encoding="utf-8"))["inputs"]
    pipeline = joblib.load(run_dir / "model.joblib")
    departures = np.clip(pipeline.predict(inputs[features]) * inputs["scale"].to_numpy(), 0, None)
    del pipeline
    return _on_rows(departures, inputs.station_key, inputs.day, rows, run_dir)


def ridge_predictions(horizon: str, rows: pd.DataFrame) -> pd.Series | None:
    """The ridge floor of 05 on rows (its saved pipeline, departures after the rate x days left for the rest of the
    month), or None when 05 §7.1 has not been run on this machine."""
    path = MODELS_DIR / f"05_ridge_{horizon}.joblib"
    if not path.exists():
        print(f"no {path.name}: run 05 §7.1 to get the ridge floor into the tables")
        return None
    scale = 1 if horizon == "day" else rows.days_in_horizon.to_numpy()
    return pd.Series(joblib.load(path).predict(design(rows)[FEATURES[horizon]]) * scale, index=rows.index).clip(lower=0)
