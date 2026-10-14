from fastapi.testclient import TestClient

from app import WARNING, app, model

client = TestClient(app)

EXAMPLE = {
    "cap-diameter": 5.0,
    "stem-height": 6.0,
    "stem-width": 10.0,
    "spore-print-color": "g",
    "gill-color": "w",
    "habitat": "g",
    "season": "a",
    "ring-type": "p",
    "cap-shape": "x",
    "stem-surface": "y",
}


def test_health_and_feature_catalog() -> None:
    assert client.get("/healthz").json() == {"status": "ok"}
    catalog = client.get("/features").json()
    assert catalog["modelName"] == "Random Forest"
    assert catalog["threshold"] == 0.5
    assert [feature["name"] for feature in catalog["features"]] == model.feature_names
    assert catalog["warning"] == WARNING


def test_prediction_returns_poisonous_probability_and_safety_warning() -> None:
    response = client.post("/predict", json={"features": EXAMPLE})

    assert response.status_code == 200
    result = response.json()
    assert result["predictedClass"] in {"edible", "poisonous"}
    assert 0 <= result["poisonousProbability"] <= 1
    assert result["threshold"] == 0.5
    assert result["modelName"] == "Random Forest"
    assert result["warning"] == WARNING


def test_missing_numeric_values_are_imputed() -> None:
    features = {**EXAMPLE, "cap-diameter": None, "season": None}

    assert client.post("/predict", json={"features": features}).status_code == 200


def test_rejects_missing_or_unknown_features() -> None:
    missing = {**EXAMPLE}
    del missing["cap-diameter"]
    unknown = {**EXAMPLE, "unknown-feature": 1}

    assert client.post("/predict", json={"features": missing}).status_code == 422
    assert client.post("/predict", json={"features": unknown}).status_code == 422


def test_rejects_invalid_numeric_and_categorical_values() -> None:
    invalid_number = {**EXAMPLE, "stem-width": -1}
    invalid_category = {**EXAMPLE, "habitat": 1}

    assert client.post("/predict", json={"features": invalid_number}).status_code == 422
    assert client.post("/predict", json={"features": invalid_category}).status_code == 422
