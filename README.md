# Avenue 5 — CloudAI project

Group project for the **CloudAI** course at Thomas More, academic year 2026–2027.
Theme: *Going green*.

## Team

| Name | GitHub |
| --- | --- |
| Peter Henskens | _TODO_ |
| Vince Bruyndoncx | _TODO_ |
| Shamim Shariar Sakil | _TODO_ |
| Hamid Iqbal | _TODO_ |

Work is split up, but every team member can explain every notebook in this repo, regardless of who wrote it.

## What we are building

We investigate two datasets, from raw data all the way to a deployed model.

1. **Secondary Mushroom** ([UCI dataset 848](https://archive.ics.uci.edu/dataset/848/secondary+mushroom+dataset)). 61,069 hypothetical mushrooms based on 173 species, labelled edible or poisonous. Small and clean, and a good fit for a clean inference interface.
2. **NYC Citi Bike system data** ([citibikenyc.com](https://citibikenyc.com/system-data)). All Citi Bike trips in New York. Too large for the repo, so the data is downloaded, unpacked and assembled by code, never by hand.

For each dataset we deliver:

- EDA with cleaning, graphs and statistical evidence (and a testable hypothesis for Citi Bike)
- one data-preparation notebook that goes from raw to prepared data, without graphs
- a quick baseline model, an AutoML comparison (PyCaret) and several tuned models
- at least one model trained and tuned on AWS
- a comparison notebook with all metrics and our conclusions

The best model is deployed behind an API with a custom frontend. Two GitHub Actions pipelines run on a push to `main` ([.github/workflows/](.github/workflows/), details in [citibike/deploy/README.md](citibike/deploy/README.md#4-pipeline-github-actions)): `app` tests, builds and rolls out the web app, and `model` retrains the model on SageMaker, compares it with the live model, and redeploys it only when it is not worse.

## Repository structure

One folder per dataset. Notebooks are numbered so they run in order: load → clean and explore → prepare → model → compare.
Every notebook starts with who worked on it and what they did, and explains each code block in a markdown cell above it.

```
.
├── README.md
├── genai-log.md          # running log of how we used AI tools (see below)
├── requirements.txt
├── mushrooms/            # dataset 1: Secondary Mushroom
│   ├── 01_load.ipynb
│   ├── 02_eda.ipynb
│   ├── 03_prepare.ipynb  # raw → prepared data, no graphs
│   ├── 04_model_baseline.ipynb
│   ├── 05_model_pycaret.ipynb
│   ├── 06_model_<name>.ipynb   # one notebook per model, incl. the AWS one
│   └── 09_compare.ipynb
├── citibike/             # dataset 2: NYC Citi Bike
│   ├── citibike_data.py  # download, unpack and assemble the monthly files (used by the notebooks and the ingest)
│   ├── ingest/           # convert_zip.py: one zip -> monthly Parquet, called by the backend
│   ├── backend/          # ASP.NET Core API: lists new S3 files, runs the ingest, keeps run metadata in Postgres
│   ├── frontend/         # React SPA: preview new files, start a run, follow progress
│   ├── deploy/           # docker compose (Postgres + pg_duckdb, backend), cron, nginx - see deploy/README.md
│   ├── 01_download.ipynb # download, unpack and assemble the monthly files
│   ├── 02_eda.ipynb
│   ├── 03_hypothesis.ipynb
│   ├── 04_prepare.ipynb
│   ├── 05_model_baseline.ipynb
│   ├── 06_model_<name>.ipynb
│   └── 09_compare.ipynb
└── deploy/
    ├── backend/          # API that loads the model and serves predictions
    └── frontend/         # custom web page that calls the API
```

Raw data and trained models (`.pkl`) are not committed. The notebooks recreate them.

## Getting started

```bash
git clone https://github.com/henskens-tech/cloud-ai-avenue-5.git
cd cloud-ai-avenue-5
python -m venv venv
venv\Scripts\activate        # Windows
# source venv/bin/activate   # macOS / Linux
pip install -r requirements.txt
```

Then open the notebooks in a dataset folder and run them in numerical order.

## Deadlines

| Milestone | Date |
| --- | --- |
| Final upload (repo link on Canvas) | Thursday 15 October 2026 |
| Presentation and oral exam (25 min per team) | Friday 23 October 2026 |

## GenAI disclosure

We use generative AI tools during this project and log that use in [genai-log.md](genai-log.md) while we work: what was generated, what we rewrote, which mistakes we caught and which proposals we rejected. The final GenAI disclosure is written from that log, following the course GenAI policy.
