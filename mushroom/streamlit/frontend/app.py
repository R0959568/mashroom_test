"""Streamlit frontend for the mushroom model.

If MUSHROOM_API_URL is set, predictions go through the FastAPI backend.
Otherwise the model is loaded in-process (used for Streamlit Community Cloud).
"""
import os
import sys
from pathlib import Path

import requests
import streamlit as st

API_URL = os.environ.get("MUSHROOM_API_URL", "").rstrip("/")
ROOT = Path(__file__).resolve().parents[1]

st.set_page_config(page_title="Mushroom classifier", page_icon="🍄")

COLORS = {
    "n": "Brown", "b": "Buff (beige)", "g": "Gray", "r": "Green", "p": "Pink",
    "u": "Purple", "e": "Red", "w": "White", "y": "Yellow", "l": "Blue",
    "o": "Orange", "k": "Black", "f": "None",
}
LABELS = {
    "cap-diameter": ("Cap diameter (cm)", "Width of the mushroom cap."),
    "stem-height": ("Stem height (cm)", "How tall the stem is."),
    "stem-width": ("Stem width (mm)", "How thick the stem is."),
    "cap-shape": ("Cap shape", "The overall shape of the cap."),
    "stem-surface": ("Stem surface", "What the surface of the stem feels or looks like."),
    "gill-color": ("Gill color", "Color of the gills (the ridges under the cap)."),
    "spore-print-color": ("Spore print color", "Color of the spore powder the cap leaves on paper."),
    "ring-type": ("Ring type", "The ring (skirt) around the stem."),
    "habitat": ("Habitat", "Where the mushroom grows."),
    "season": ("Season found", "Season in which it was found."),
}
VALUES = {
    "cap-shape": {"b": "Bell", "c": "Conical (pointed)", "x": "Convex (dome)", "f": "Flat",
                  "s": "Sunken", "p": "Spherical (round)", "o": "Other"},
    "stem-surface": {"i": "Fibrous", "g": "Grooved", "y": "Scaly", "s": "Smooth", "h": "Shiny",
                     "l": "Leathery", "k": "Silky", "t": "Sticky", "w": "Wrinkled",
                     "e": "Fleshy", "f": "None"},
    "gill-color": COLORS,
    "spore-print-color": COLORS,
    "ring-type": {"c": "Cobwebby", "e": "Fading (evanescent)", "r": "Flaring", "g": "Grooved",
                  "l": "Large", "p": "Pendant (hanging)", "s": "Sheathing", "z": "Zone",
                  "y": "Scaly", "m": "Movable", "f": "None"},
    "habitat": {"g": "Grass", "l": "Leaf litter", "m": "Meadow", "p": "Path", "h": "Heath",
                "u": "Urban", "w": "Waste ground", "d": "Woods"},
    "season": {"s": "Spring", "u": "Summer", "a": "Autumn", "w": "Winter"},
}


@st.cache_resource
def _local_model():
    sys.path.insert(0, str(ROOT))
    from backend.main import model

    return model


def get_features() -> dict:
    if API_URL:
        response = requests.get(f"{API_URL}/features", timeout=30)
        response.raise_for_status()
        return response.json()
    model = _local_model()
    from backend.main import WARNING

    return {
        "modelName": model.model_name,
        "threshold": model.threshold,
        "features": model.features,
        "warning": WARNING,
    }


def predict(features: dict) -> dict:
    if API_URL:
        response = requests.post(f"{API_URL}/predict", json={"features": features}, timeout=30)
        if response.status_code == 422:
            raise ValueError(response.json().get("detail", "Invalid input"))
        response.raise_for_status()
        return response.json()
    return _local_model().predict(features)


st.title("🍄 Mushroom classifier")

try:
    catalog = get_features()
except Exception as error:
    st.error(f"Could not load the model: {error}")
    st.stop()

st.warning(catalog["warning"])
st.caption(f"Model: {catalog['modelName']} · decision threshold: {catalog['threshold']:.3f}")

values: dict = {}
with st.form("mushroom-form"):
    columns = st.columns(2)
    for index, feature in enumerate(catalog["features"]):
        name = feature["name"]
        label, help_text = LABELS.get(name, (name.replace("-", " ").capitalize(), None))
        names = VALUES.get(name, {})
        with columns[index % 2]:
            if feature["kind"] == "numeric":
                unknown = st.checkbox(f"{label}: I don't know", key=f"{name}-unknown")
                number = st.number_input(label, min_value=0.0, value=5.0, step=0.5, key=name, help=help_text)
                values[name] = None if unknown else number
            else:
                choice = st.selectbox(
                    label,
                    ["(I don't know)"] + feature["options"],
                    format_func=lambda code, n=names: n.get(code, code),
                    key=name,
                    help=help_text,
                )
                values[name] = None if choice == "(I don't know)" else choice
    submitted = st.form_submit_button("Predict", type="primary")

if submitted:
    try:
        result = predict(values)
    except ValueError as error:
        st.error(str(error))
    except Exception as error:
        st.error(f"Prediction failed: {error}")
    else:
        probability = result["poisonousProbability"]
        if result["predictedClass"] == "poisonous":
            st.error(f"☠️ Predicted **poisonous** ({probability:.1%} probability)")
        else:
            st.success(f"✅ Predicted **edible** ({probability:.1%} poisonous probability)")
        st.progress(min(max(probability, 0.0), 1.0))
        st.caption(result["warning"])
