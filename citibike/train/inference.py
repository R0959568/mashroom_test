"""The inference script of the Citi Bike models on SageMaker (11): what the scikit-learn serving container runs inside
an endpoint. The container calls four functions: model_fn once when the endpoint starts, then input_fn, predict_fn
and output_fn for every request. It runs next to train.py's model.tar.gz (model.joblib + metrics.json) and the same
requirements.txt, so the pipeline unpickles in the versions that fitted it.

The contract (what citibike/backend sends, through API Gateway and the Lambda of 11):

    request   {"rows": [{<input>: value, ..., "scale": s}, ...]}
              the inputs the model was trained on (metrics.json lists them: lags, calendar, weather, station_key
              as text) and the scale of the row (1 for the day, the days left in the month for the rest of the month)
    response  {"predictions": [departures, ...], "model": {"family", "horizon", "validation_mae"}}
              the model's output times the scale, never below 0, exactly as train.py predicts

Locally the same four functions are tested on a run folder (citibike/tests/test_serving.py).
"""
from __future__ import annotations

import json
import os

import joblib
import numpy as np
import pandas as pd

JSON = "application/json"


def model_fn(model_dir: str) -> dict:
    """The pipeline and what metrics.json says about it; loaded once, when the endpoint starts."""
    pipeline = joblib.load(os.path.join(model_dir, "model.joblib"))
    with open(os.path.join(model_dir, "metrics.json"), encoding="utf-8") as f:
        metrics = json.load(f)
    return {"pipeline": pipeline, "inputs": metrics["inputs"], "family": metrics["family"], "horizon": metrics["horizon"],
            "validation_mae": round(float(metrics["validation"]["MAE"]), 3)}


def input_fn(body, content_type: str = JSON) -> pd.DataFrame:
    """The request body as one DataFrame row per station-day; scale 1 when it is not given."""
    if content_type != JSON:
        raise ValueError(f"unsupported content type {content_type}; send {JSON}")
    payload = json.loads(body if isinstance(body, str) else body.decode("utf-8"))
    rows = payload["rows"] if isinstance(payload, dict) else payload
    if not rows:
        raise ValueError("no rows")
    df = pd.DataFrame(rows)
    if "scale" not in df.columns:
        df["scale"] = 1.0
    return df


def predict_fn(df: pd.DataFrame, model: dict) -> np.ndarray:
    """Departures per row: the pipeline's output times the row's scale, never below 0 (train.predict())."""
    missing = [c for c in model["inputs"] if c not in df.columns]
    if missing:
        raise ValueError(f"missing inputs: {missing}")
    x = df[model["inputs"]].copy()
    if "station_key" in x.columns:
        x["station_key"] = x["station_key"].astype(str)
    return np.clip(model["pipeline"].predict(x) * df["scale"].to_numpy(dtype="float64"), 0, None)


def output_fn(predictions: np.ndarray, accept: str = JSON):
    """The response body; the container sends it with the accept type."""
    return json.dumps({"predictions": [round(float(p), 2) for p in predictions]})


def respond(predictions: np.ndarray, model: dict) -> str:
    """The full response with the model's description, used by the Lambda of 11 (the container's own output_fn has no
    access to the model, so the Lambda adds the description from the endpoint's name)."""
    return json.dumps({"predictions": [round(float(p), 2) for p in predictions],
                       "model": {k: model[k] for k in ("family", "horizon", "validation_mae")}})
