from __future__ import annotations

import math
import os
from pathlib import Path
from typing import Any

import numpy as np
import pandas as pd
from fastapi import FastAPI, HTTPException
from joblib import load
from pydantic import BaseModel

WARNING = (
    "This educational model is not reliable enough to identify real mushrooms. "
    "Never eat a mushroom based on this prediction."
)
MISSING_CATEGORY = "__missing__"
NUMERIC_FEATURES = {"cap-diameter", "stem-height", "stem-width"}
MODEL_PATH = Path(
    os.environ.get(
        "MUSHROOM_MODEL_PATH",
        Path(__file__).resolve().parents[1] / "model" / "tuned_mushroom_candidate.joblib",
    )
)


class PredictionRequest(BaseModel):
    features: dict[str, Any]


class MushroomInference:
    def __init__(self, model_path: Path = MODEL_PATH) -> None:
        bundle = load(model_path)
        self.pipeline = bundle["pipeline"]
        self.threshold = float(bundle["threshold"])
        self.model_name = str(bundle["selected_model"])
        self.feature_names = list(self.pipeline.feature_names_in_)
        if set(self.feature_names) != NUMERIC_FEATURES | {
            "spore-print-color",
            "gill-color",
            "habitat",
            "season",
            "ring-type",
            "cap-shape",
            "stem-surface",
        }:
            raise RuntimeError("The saved model's feature schema does not match the Mushroom API")
        if 1 not in self.pipeline.classes_:
            raise RuntimeError("The saved model does not include poisonous class 1")
        self.poisonous_class_index = list(self.pipeline.classes_).index(1)
        self.features = self._feature_catalog()

    def _feature_catalog(self) -> list[dict[str, Any]]:
        definitions: dict[str, dict[str, Any]] = {}
        preprocessor = self.pipeline.named_steps["preprocessor"]
        for name, transformer, columns in preprocessor.transformers_:
            if name == "numeric":
                definitions.update(
                    {column: {"name": column, "kind": "numeric", "options": []} for column in columns}
                )
            elif name == "categorical":
                encoder = transformer.named_steps["onehot"]
                for column, categories in zip(columns, encoder.categories_, strict=True):
                    options = [str(value) for value in categories if str(value) != MISSING_CATEGORY]
                    definitions[column] = {
                        "name": column,
                        "kind": "categorical",
                        "options": options,
                    }
        if set(definitions) != set(self.feature_names):
            raise RuntimeError("The saved model's preprocessor and input feature names differ")
        return [definitions[name] for name in self.feature_names]

    def predict(self, supplied: dict[str, Any]) -> dict[str, Any]:
        expected = set(self.feature_names)
        actual = set(supplied)
        if missing := expected - actual:
            raise ValueError(f"Missing feature(s): {', '.join(sorted(missing))}")
        if extra := actual - expected:
            raise ValueError(f"Unknown feature(s): {', '.join(sorted(extra))}")

        row: dict[str, Any] = {}
        for name in self.feature_names:
            value = supplied[name]
            if name in NUMERIC_FEATURES:
                if value is None:
                    row[name] = np.nan
                    continue
                if isinstance(value, bool) or not isinstance(value, (int, float)):
                    raise ValueError(f"{name} must be a number or null")
                if not math.isfinite(value) or value < 0:
                    raise ValueError(f"{name} must be finite and non-negative")
                row[name] = float(value)
            else:
                if value is None or value == "":
                    row[name] = MISSING_CATEGORY
                elif isinstance(value, str):
                    row[name] = value
                else:
                    raise ValueError(f"{name} must be a string or null")

        frame = pd.DataFrame([row], columns=self.feature_names)
        probabilities = self.pipeline.predict_proba(frame)[0]
        poisonous_probability = float(probabilities[self.poisonous_class_index])
        predicted_class = (
            "poisonous" if poisonous_probability > self.threshold else "edible"
        )
        return {
            "predictedClass": predicted_class,
            "poisonousProbability": poisonous_probability,
            "threshold": self.threshold,
            "modelName": self.model_name,
            "warning": WARNING,
        }


model = MushroomInference()
app = FastAPI(title="Mushroom inference")


@app.get("/healthz")
def healthz() -> dict[str, str]:
    return {"status": "ok"}


@app.get("/features")
def get_features() -> dict[str, Any]:
    return {
        "modelName": model.model_name,
        "threshold": model.threshold,
        "features": model.features,
        "warning": WARNING,
    }


@app.post("/predict")
def predict(request: PredictionRequest) -> dict[str, Any]:
    try:
        return model.predict(request.features)
    except ValueError as error:
        raise HTTPException(status_code=422, detail=str(error)) from error
