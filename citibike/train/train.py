"""Train one model for one horizon of the Citi Bike station forecast: the same script on a laptop (07, 08) and as the
entry point of a SageMaker training job (10). It knows nothing of the database or of the feature engineering: it reads
the model inputs that 07 §2 writes (modelling.model_inputs()), fits one family, scores it on the validation rows and
saves what 09 and the API need.

Inputs: a train and a validation Parquet file (or the directory that holds it: a SageMaker channel) with the columns
    station_key (text), day (date), y (the target on the model scale), scale (prediction x scale = departures),
    actual (the departures to score), and every other column an input of the model.
Outputs: model.joblib (preprocessing + model in one scikit-learn Pipeline) in --model-dir; metrics.json and
    predictions_valid.parquet in --output-dir (default: the model dir). The validation metrics are also printed as
    `validation:MAE=...` lines, which a SageMaker job reads as its metrics.

Locally:    python train.py --family lightgbm --horizon day --train .../day/train.parquet --valid .../day/valid.parquet
                            --model-dir ../../models/citibike/07_day_lightgbm
SageMaker:  the container runs `python train.py --family lightgbm --n-estimators 1000 ...` with the job's hyperparameters
            as arguments and sets SM_CHANNEL_TRAIN, SM_CHANNEL_VALID, SM_MODEL_DIR and SM_OUTPUT_DATA_DIR: the defaults
            below pick those up, so 10 passes only the hyperparameters.
"""
from __future__ import annotations   # the SageMaker scikit-learn container runs Python 3.8: list[str] | None must stay a string

import argparse
import json
import os
import sys
import time
from pathlib import Path

import joblib
import numpy as np
import pandas as pd
import sklearn
from sklearn.compose import ColumnTransformer
from sklearn.ensemble import ExtraTreesRegressor
from sklearn.model_selection import KFold
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import TargetEncoder

META = ["station_key", "day", "y", "scale", "actual"]   # the non-input columns of a model-inputs file
FAMILIES = ("lightgbm", "extra_trees")
DEFAULT_TREES = {"lightgbm": 1000, "extra_trees": 200}
FAMILY_ARGS = {"lightgbm": ("objective", "learning_rate", "num_leaves", "min_child_samples", "subsample", "colsample_bytree"),
               "extra_trees": ("min_samples_leaf", "max_features")}


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="train.py", description="Train one family for one horizon on the model inputs of 07 §2.")
    p.add_argument("--family", choices=FAMILIES, required=True)
    p.add_argument("--horizon", default="", help="day or rest_of_month; recorded in metrics.json, nothing else")
    p.add_argument("--train", default=os.environ.get("SM_CHANNEL_TRAIN"), help="train.parquet or its directory (a SageMaker channel)")
    p.add_argument("--valid", default=os.environ.get("SM_CHANNEL_VALID"), help="valid.parquet or its directory")
    p.add_argument("--model-dir", default=os.environ.get("SM_MODEL_DIR", "model"), help="where model.joblib goes")
    p.add_argument("--output-dir", default=os.environ.get("SM_OUTPUT_DATA_DIR"), help="metrics.json and predictions; default: the model dir")
    p.add_argument("--station-key", type=int, choices=[0, 1], default=1, help="1: the station key as a target-encoded input; 0: without it (05, decision 3)")
    p.add_argument("--sample-stations", type=float, default=0, help="share of the stations to keep, for a smoke test (0 = all)")
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--n-jobs", type=int, default=-1)
    p.add_argument("--n-estimators", type=int, default=None, help=f"trees; default {DEFAULT_TREES}")
    g = p.add_argument_group("lightgbm")
    g.add_argument("--objective", default="regression", help="regression (L2), regression_l1, poisson, ...")
    g.add_argument("--learning-rate", type=float, default=0.05)
    g.add_argument("--num-leaves", type=int, default=63)
    g.add_argument("--min-child-samples", type=int, default=100)
    g.add_argument("--subsample", type=float, default=0.8, help="share of the rows each tree sees")
    g.add_argument("--colsample-bytree", type=float, default=0.8, help="share of the inputs each tree sees")
    g.add_argument("--curve-every", type=int, default=100, help="validation MAE every this many trees (0 = off)")
    g = p.add_argument_group("extra_trees")
    g.add_argument("--min-samples-leaf", type=int, default=100)
    g.add_argument("--max-features", type=float, default=1.0, help="share of the inputs considered at each split")
    return p


def log(message: str) -> None:
    print(message, flush=True)   # flushed, so the SageMaker job log shows progress


def read_inputs(path: str) -> pd.DataFrame:
    path = Path(path)
    if path.is_dir():
        files = sorted(path.glob("*.parquet"))
        assert files, f"no .parquet file in {path}"
        return pd.concat([pd.read_parquet(f) for f in files], ignore_index=True)
    return pd.read_parquet(path)


def metrics(actual, predicted) -> dict:
    """The same four numbers as modelling.metrics(): MAE and RMSE in departures, WAPE in % of the actual volume, bias."""
    actual, predicted = np.asarray(actual, dtype="float64"), np.asarray(predicted, dtype="float64")
    err = predicted - actual
    return {"MAE": float(np.abs(err).mean()), "RMSE": float(np.sqrt((err ** 2).mean())),
            "WAPE_%": float(np.abs(err).sum() / actual.sum() * 100), "bias": float(err.mean()), "n": int(len(actual))}


def target_encoder(seed: int) -> TargetEncoder:
    """Cross-fitted in 5 shuffled folds with a fixed seed. scikit-learn 1.9 takes the seed through a `cv` splitter and
    deprecates `random_state`; older versions (a SageMaker container may have one) take only the integer form."""
    if tuple(int(x) for x in sklearn.__version__.split(".")[:2]) >= (1, 9):
        return TargetEncoder(target_type="continuous", cv=KFold(5, shuffle=True, random_state=seed))
    return TargetEncoder(target_type="continuous", cv=5, random_state=seed)


def make_preprocessing(features: list[str], seed: int) -> ColumnTransformer:
    """The station key target-encoded (the station's mean target on the training rows, cross-fitted so that no row sees
    its own target, as PyCaret did in 06), every other input passed through as the number it is."""
    transformers = []
    if "station_key" in features:
        transformers.append(("station", target_encoder(seed), ["station_key"]))
    transformers.append(("numeric", "passthrough", [f for f in features if f != "station_key"]))
    return ColumnTransformer(transformers, verbose_feature_names_out=False).set_output(transform="pandas")


def make_model(args: argparse.Namespace):
    if args.family == "lightgbm":
        from lightgbm import LGBMRegressor
        return LGBMRegressor(objective=args.objective, n_estimators=args.n_estimators, learning_rate=args.learning_rate,
                             num_leaves=args.num_leaves, min_child_samples=args.min_child_samples,
                             subsample=args.subsample, subsample_freq=1, colsample_bytree=args.colsample_bytree,
                             random_state=args.seed, n_jobs=args.n_jobs, verbose=-1)
    return ExtraTreesRegressor(n_estimators=args.n_estimators, min_samples_leaf=args.min_samples_leaf,
                               max_features=args.max_features, n_jobs=args.n_jobs, random_state=args.seed)


def predict(pipeline: Pipeline, d: pd.DataFrame, features: list[str]) -> np.ndarray:
    """Departures: the model's output times the scale of the row, never below 0."""
    return np.clip(pipeline.predict(d[features]) * d["scale"].to_numpy(), 0, None)


def importance(model, names: list[str]) -> dict:
    """Share of the split gain (LightGBM) or of the impurity decrease (extra trees) per input, largest first."""
    values = model.booster_.feature_importance(importance_type="gain") if hasattr(model, "booster_") else model.feature_importances_
    values = np.asarray(values, dtype="float64")
    share = values / values.sum() if values.sum() > 0 else values
    return dict(sorted(zip(names, share.round(4).tolist()), key=lambda kv: -kv[1]))


def main(argv: list[str] | None = None) -> dict:
    args = build_parser().parse_args(argv)
    assert args.train and args.valid, "--train and --valid (or SM_CHANNEL_TRAIN / SM_CHANNEL_VALID) are required"
    if args.n_estimators is None:
        args.n_estimators = DEFAULT_TREES[args.family]
    model_dir, out_dir = Path(args.model_dir), Path(args.output_dir or args.model_dir)
    t0 = time.time()

    train, valid = read_inputs(args.train), read_inputs(args.valid)
    if args.sample_stations:
        keys = np.sort(train["station_key"].unique())
        keep = np.random.default_rng(args.seed).choice(keys, size=max(1, int(len(keys) * args.sample_stations)), replace=False)
        train, valid = train[train["station_key"].isin(keep)], valid[valid["station_key"].isin(keep)]
    features = [c for c in train.columns if c not in META] + (["station_key"] if args.station_key else [])
    missing = [c for c in META + features if c not in valid.columns]
    assert not missing, f"the validation file lacks {missing}"
    assert not train[features].isna().any().any() and not valid[features].isna().any().any(), "an input is empty"
    log(f"{args.family} {args.horizon}: {len(train):,} training rows, {len(valid):,} validation rows, {len(features)} inputs, "
        f"{args.n_estimators} trees, read in {time.time() - t0:.0f} s")

    prep, model = make_preprocessing(features, args.seed), make_model(args)
    t1 = time.time()
    model.fit(prep.fit_transform(train[features], train["y"]), train["y"])
    fit_seconds = time.time() - t1
    pipeline = Pipeline([("prep", prep), ("model", model)])
    log(f"fitted in {fit_seconds:.0f} s")

    pred_train, pred_valid = predict(pipeline, train, features), predict(pipeline, valid, features)
    result = {
        "family": args.family, "horizon": args.horizon, "inputs": features, "rows": {"train": len(train), "valid": len(valid)},
        "hyperparameters": {k: vars(args)[k] for k in ("n_estimators", *FAMILY_ARGS[args.family], "station_key", "sample_stations", "seed", "n_jobs")},
        "fit_seconds": round(fit_seconds, 1),
        "train": metrics(train["actual"], pred_train), "validation": metrics(valid["actual"], pred_valid),
        "importance": importance(model, list(prep.get_feature_names_out())),
        "versions": {"python": sys.version.split()[0], "scikit-learn": sklearn.__version__,
                     "pandas": pd.__version__, "numpy": np.__version__},
    }
    if args.family == "lightgbm":
        import lightgbm
        result["versions"]["lightgbm"] = lightgbm.__version__
        if args.curve_every:   # the validation MAE after k trees: does the model still gain at the last tree?
            x_valid, scale = prep.transform(valid[features]), valid["scale"].to_numpy()
            result["curve"] = {k: metrics(valid["actual"], np.clip(model.predict(x_valid, num_iteration=k) * scale, 0, None))["MAE"]
                               for k in range(args.curve_every, args.n_estimators + 1, args.curve_every)}
    for name, m in [("train", result["train"]), ("validation", result["validation"])]:
        log(" ".join(f"{name}:{k}={v:.4f}" for k, v in m.items() if k != "n"))

    model_dir.mkdir(parents=True, exist_ok=True)
    out_dir.mkdir(parents=True, exist_ok=True)
    joblib.dump(pipeline, model_dir / "model.joblib")
    valid[["station_key", "day", "actual"]].assign(prediction=pred_valid).to_parquet(out_dir / "predictions_valid.parquet", index=False)
    for folder in {model_dir, out_dir}:
        (folder / "metrics.json").write_text(json.dumps(result, indent=1, default=str), encoding="utf-8")
    log(f"saved model.joblib in {model_dir}, metrics.json and predictions_valid.parquet in {out_dir}; {time.time() - t0:.0f} s in total")
    return result


if __name__ == "__main__":
    main()
