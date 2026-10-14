# Mushroom Streamlit app

- `backend/main.py` – FastAPI API (`/healthz`, `/features`, `/predict`)
- `frontend/app.py` – Streamlit UI. Uses the API when `MUSHROOM_API_URL` is set, otherwise loads the model in-process.
- `model/` – the tuned model (`tuned_mushroom_candidate.joblib`)

## Run locally

```bash
cd mushroom/streamlit
pip install -r requirements.txt

# standalone
streamlit run frontend/app.py

# with the backend
uvicorn backend.main:app --port 8000 &
MUSHROOM_API_URL=http://localhost:8000 streamlit run frontend/app.py
```

Or `docker compose up --build` (UI on :8501, API on :8000).

## Deploy online (Streamlit Community Cloud, free)

1. Push this repo to GitHub.
2. Go to https://share.streamlit.io → **New app**.
3. Repository: `R0959568/mashroom_test`, branch `main`, main file path `mushroom/streamlit/frontend/app.py`.
4. Advanced settings → Python 3.12. Requirements are picked up from `mushroom/streamlit/requirements.txt`.
5. Deploy. You get a public URL.

To also host the API, deploy the `Dockerfile` (Render, Railway, Fly.io, ...) and set `MUSHROOM_API_URL` in the app's secrets/env.
