FROM python:3.12-slim

WORKDIR /app
COPY mushroom/inference/requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

COPY mushroom/inference/app.py ./app.py
COPY mushroom/artifacts/tuned_mushroom_candidate.joblib ./artifacts/tuned_mushroom_candidate.joblib

ENV MUSHROOM_MODEL_PATH=/app/artifacts/tuned_mushroom_candidate.joblib
EXPOSE 8000
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8000"]
