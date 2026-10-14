"""Retrain, gate, promote: the continuous-training step of the pipeline (.github/workflows/retrain.yml), also runnable
from the laptop. Per horizon it runs train.py as a SageMaker training job with the hyperparameters of retrain.json on the
model inputs already in S3 (10 §1 put them there), compares the job's validation MAE with that of the model the endpoint
serves now, and only when the new model is not worse (within tolerance_pct) points the endpoint at it, checks it with a
probe and rolls back when the probe fails. The Lambda's model description follows the endpoint.

What it does not do: refresh the model inputs (they come from Postgres over the VPN: 07 §2 on the laptop, then 10 §1),
tune hyperparameters (10), or create endpoints, the Lambda or the API (11). A missing endpoint is an error, not a create.

    python retrain.py                         # both horizons: train, gate, promote
    python retrain.py --horizons day          # one
    python retrain.py --dry-run               # train and gate, promote nothing

Credentials as in 10 and 11: AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY and AWS_SESSION_TOKEN from the environment (GitHub
secrets in the pipeline, the repo's .env on the laptop), new every Learner Lab session. Exit code 1 when anything failed
or the gate kept a candidate out: the pipeline goes red, and the step summary says why.
"""
from __future__ import annotations

try:                    # the laptop's TLS-inspecting proxy (see 10), before boto3; a GitHub runner has neither the proxy nor the package
    import truststore
    truststore.inject_into_ssl()
except ImportError:
    pass

import argparse
import io
import json
import os
import sys
import tarfile
import tempfile
import time
from pathlib import Path

import boto3
import pandas as pd
from botocore.config import Config
from botocore.exceptions import ClientError, NoCredentialsError

import jobs
import serving

TRAIN_DIR = Path(__file__).resolve().parent
CONFIG_FILE = TRAIN_DIR / "retrain.json"
MAX_CONCURRENCY = 2                                   # as 11
PROBE_ROWS, PROBE_TOLERANCE = 5, 0.05                 # as 11: the endpoint must answer what the job itself predicted
IN_ACTIONS = os.environ.get("GITHUB_ACTIONS") == "true"


def log(message: str) -> None:
    print(message, flush=True)


def error(message: str) -> None:
    """An error line; in GitHub Actions an annotation, so it shows on the run's page and not only in the log."""
    log(f"::error::{message}" if IN_ACTIONS else f"error: {message}")


def load_config(path: Path = CONFIG_FILE) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8"))


# ---- Names -------------------------------------------------------------------------------------------------------------
def run_name(env=os.environ) -> str:
    """Part of every job, model and config name: unique per pipeline run (names are unique per account and never
    reused), and readable back to the run that made it."""
    if env.get("GITHUB_RUN_ID"):
        return f"ci{env['GITHUB_RUN_ID']}-{env.get('GITHUB_RUN_ATTEMPT', '1')}"
    return "local" + time.strftime("%Y%m%d%H%M")


def job_name(run: str, horizon: str) -> str:
    """The training job, and the model and endpoint config made from it (separate namespaces, so one name for all three)."""
    return f"citibike-{run}-{horizon.replace('_', '-')}"


def inputs_key(horizon: str, split: str) -> str:
    """Where 10 §1 put a channel's file (jobs.upload_inputs)."""
    return f"citibike/model_inputs/{horizon}/{split}/{split}.parquet"


def in_s3(s3, bucket: str, key: str) -> bool:
    try:
        s3.head_object(Bucket=bucket, Key=key)
        return True
    except ClientError as e:
        if e.response["Error"]["Code"] in ("404", "NoSuchKey", "NotFound"):
            return False
        raise


def job_of_model_data(url: str) -> str:
    """The training job behind a model.tar.gz: jobs.training_job_request writes to s3://<bucket>/citibike/jobs/<job>/output/."""
    parts = url.rstrip("/").split("/")
    assert parts[-2:] == ["output", "model.tar.gz"], f"not a training job's model.tar.gz: {url}"
    return parts[-3]


# ---- What is live ------------------------------------------------------------------------------------------------------
def validation_mae(described_job: dict) -> float | None:
    return {m["MetricName"]: m["Value"] for m in described_job.get("FinalMetricDataList", [])}.get(jobs.OBJECTIVE_METRIC)


def live_model(sm, endpoint: str) -> dict | None:
    """What the endpoint serves now: its config, its model, the training job behind the model and that job's validation
    MAE. Read from AWS, never from a file in the repo, so the gate compares with what is really deployed. None when the
    endpoint does not exist."""
    if not serving.exists(sm.describe_endpoint, EndpointName=endpoint):
        return None
    config = sm.describe_endpoint(EndpointName=endpoint)["EndpointConfigName"]
    model = sm.describe_endpoint_config(EndpointConfigName=config)["ProductionVariants"][0]["ModelName"]
    job = job_of_model_data(sm.describe_model(ModelName=model)["PrimaryContainer"]["ModelDataUrl"])
    return {"endpoint": endpoint, "config": config, "model": model, "job": job,
            "validation_mae": validation_mae(sm.describe_training_job(TrainingJobName=job))}


def gate(candidate_mae: float | None, live_mae: float | None, tolerance_pct: float) -> tuple[bool, str]:
    """Promote when the candidate is not worse than the live model by more than tolerance_pct: 09's tie rule (within 1 %
    is equal), so a retrain on the same inputs passes and a change that costs accuracy does not."""
    if candidate_mae is None:
        return False, "the job reported no validation MAE"
    if live_mae is None:
        return False, "the live model's validation MAE is unknown"
    limit = live_mae * (1 + tolerance_pct / 100)
    sign = "<=" if candidate_mae <= limit else ">"
    return candidate_mae <= limit, f"MAE {candidate_mae:.4f} {sign} {limit:.4f} (live {live_mae:.4f} + {tolerance_pct:g} %)"


# ---- Promotion ---------------------------------------------------------------------------------------------------------
def probe_rows(s3, bucket: str, horizon: str, described_job: dict, n: int = PROBE_ROWS) -> tuple[list[dict], list[float]]:
    """n validation rows as the backend sends them (the inputs and the scale, the station key as text), and what the job
    itself predicted for them: predictions_valid.parquet in its output.tar.gz, in the order of the validation file."""
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp) / "valid.parquet"
        s3.download_file(bucket, inputs_key(horizon, "valid"), str(path))
        inputs = pd.read_parquet(path).head(n)
    output_bucket, key = described_job["ModelArtifacts"]["S3ModelArtifacts"][len("s3://"):].split("/", 1)
    body = s3.get_object(Bucket=output_bucket, Key=key.replace("model.tar.gz", "output.tar.gz"))["Body"].read()
    with tarfile.open(fileobj=io.BytesIO(body)) as tar:
        member = next(m for m in tar.getmembers() if m.name.endswith("predictions_valid.parquet"))
        saved = pd.read_parquet(io.BytesIO(tar.extractfile(member).read())).head(n)
    assert (inputs.station_key.astype(str).to_numpy() == saved.station_key.astype(str).to_numpy()).all(), \
        "the job's predictions are not in the order of the validation file"
    rows = inputs.drop(columns=["day", "y", "actual"]).assign(station_key=lambda d: d.station_key.astype(str))
    return json.loads(rows.to_json(orient="records")), saved.prediction.round(2).tolist()


def promote(sm, runtime, name: str, spec: dict, role_arn: str, region: str, model_data: str, serving_uri: str,
            previous_config: str, rows: list[dict], expected: list[float]) -> dict:
    """A model and an endpoint config of the job, the endpoint pointed at it (same name: the backend and the Lambda
    change nothing), then the probe. A failed update leaves SageMaker on the old config by itself; a failed probe
    points the endpoint back here."""
    endpoint = spec["endpoint"]
    serving.create_model(sm, serving.model_request(name, role_arn, region, model_data, serving_uri))
    serving.create_endpoint_config(sm, serving.endpoint_config_request(name, name, serverless_memory_mb=spec["serverless_mb"],
                                                                       max_concurrency=MAX_CONCURRENCY))
    serving.create_or_update_endpoint(sm, endpoint, name)
    described = serving.wait_endpoint(sm, endpoint)
    if described["EndpointStatus"] != "InService" or described["EndpointConfigName"] != name:
        raise RuntimeError(f"the update of {endpoint} failed and it stays on {described['EndpointConfigName']}: "
                           f"{described.get('FailureReason', '?')}")
    try:
        answer, seconds = serving.invoke(runtime, endpoint, rows)
        gap = max(abs(a - b) for a, b in zip(answer["predictions"], expected))
        assert gap < PROBE_TOLERANCE, f"the endpoint answers up to {gap:.3f} away from the job's own predictions"
    except Exception as e:
        log(f"probe of {endpoint} failed ({e}); pointing it back at {previous_config}")
        serving.create_or_update_endpoint(sm, endpoint, previous_config)
        serving.wait_endpoint(sm, endpoint)
        raise
    log(f"{endpoint}: probe of {len(rows)} rows answered in {seconds:.1f} s, largest difference {gap}")
    return {"probe_s": round(seconds, 1), "probe_gap": gap}


def lambda_variables(variables: dict, models: dict[str, dict]) -> dict:
    """The function's variables with MODEL_<HORIZON> replaced for the promoted horizons; everything else (the key, the
    endpoint names) kept exactly as 11 set it."""
    return {**variables, **{f"MODEL_{horizon.upper()}": json.dumps(model) for horizon, model in models.items()}}


def describe_in_lambda(lam, function: str, models: dict[str, dict]) -> None:
    """What the API reports as the model (validation MAE, family) follows the endpoint. The variables hold the API key:
    read and written back, never printed."""
    current = lam.get_function_configuration(FunctionName=function).get("Environment", {}).get("Variables", {})
    lam.update_function_configuration(FunctionName=function, Environment={"Variables": lambda_variables(current, models)})
    lam.get_waiter("function_updated_v2").wait(FunctionName=function)
    log(f"function {function}: model description updated for {', '.join(models)}")


# ---- Report ------------------------------------------------------------------------------------------------------------
def report(results: list[dict], run: str, dry_run: bool) -> str:
    """The run as a Markdown table: the job log's last lines and, in GitHub Actions, the run's summary page."""
    def number(value, digits=4):
        return "–" if value is None else f"{value:.{digits}f}"
    lines = [f"### Retrain `{run}`{' (dry run)' if dry_run else ''}", "",
             "| horizon | job | candidate MAE | live MAE | live job | billed (s) | decision |", "| --- | --- | --- | --- | --- | --- | --- |"]
    lines += [f"| {r['horizon']} | `{r['job']}` | {number(r['candidate_mae'])} | {number(r['live_mae'])} | `{r['live_job']}` | "
              f"{r['billed_s'] if r['billed_s'] is not None else '–'} | {r['decision']} |" for r in results]
    text = "\n".join(lines) + "\n"
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
            summary.write(text)
    return text


# ---- The run -----------------------------------------------------------------------------------------------------------
def connect(region: str):
    """The session and who it is; an expired or missing lab session ends the run here, with what to do about it."""
    session = boto3.Session(region_name=region)
    try:
        who = jobs.identity(session)
    except NoCredentialsError:
        error("no AWS credentials: set AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY and AWS_SESSION_TOKEN (Learner Lab, AWS Details)")
        return None, None
    except ClientError as e:
        if e.response["Error"]["Code"] in jobs.EXPIRED:
            error("the Learner Lab session expired: start the lab, copy AWS Details -> AWS CLI into the repository secrets "
                  "AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY and AWS_SESSION_TOKEN, and run the workflow again")
            return None, None
        raise
    if IN_ACTIONS:
        log(f"::add-mask::{who['account']}")      # the account id is in the bucket and every S3 URI: hidden in the public log
    return session, who


def build_parser(config: dict) -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="retrain.py", description="Retrain on SageMaker, gate on validation MAE, promote to the endpoint.")
    p.add_argument("--horizons", nargs="+", choices=list(config["horizons"]), default=list(config["horizons"]))
    p.add_argument("--dry-run", action="store_true", help="train and gate, promote nothing")
    p.add_argument("--tolerance-pct", type=float, default=config["tolerance_pct"], help="how much worse than the live MAE still passes")
    return p


def main(argv: list[str] | None = None, config: dict | None = None) -> int:
    config = config or load_config()
    args = build_parser(config).parse_args(argv)
    region, run = config["region"], run_name()
    session, who = connect(region)
    if session is None:
        return 1
    sm, s3, lam = session.client("sagemaker"), session.client("s3"), session.client("lambda")
    runtime = session.client("sagemaker-runtime", config=Config(read_timeout=180, retries={"max_attempts": 2}))   # a cold 1 GB model
    bucket = f"sagemaker-{region}-{who['account']}"
    role_arn = os.environ.get("SAGEMAKER_ROLE_ARN") or who["role_arn"]
    log(f"run {run}: account {jobs.masked(who['account'])} as {who['caller']}, role {role_arn.split('/')[-1]}, "
        f"horizons {', '.join(args.horizons)}, tolerance {args.tolerance_pct:g} %{', dry run' if args.dry_run else ''}")

    # Everything that can stop the run is checked before the first job costs anything.
    missing = [inputs_key(h, split) for h in args.horizons for split in ("train", "valid")
               if not in_s3(s3, bucket, inputs_key(h, split))]
    if missing:
        error(f"model inputs missing in S3: {', '.join(missing)}; upload them with 10 §1 (jobs.upload_inputs)")
        return 1
    live = {h: live_model(sm, config["horizons"][h]["endpoint"]) for h in args.horizons}
    absent = [config["horizons"][h]["endpoint"] for h in args.horizons if live[h] is None]
    if absent and not args.dry_run:
        error(f"no endpoint {', '.join(absent)}: retrain.py promotes to an existing endpoint, deploy it with 11 first")
        return 1
    for h in args.horizons:
        if live[h]:
            log(f"{h}: live on {live[h]['endpoint']} is {live[h]['job']}, validation MAE {live[h]['validation_mae']}")

    # The bundles under the run's own prefix: a serverless endpoint fetches its serving bundle at every cold start, so
    # overwriting the key a live model points at would change the live code before any gate.
    source_uri = jobs.upload_source(s3, bucket, TRAIN_DIR, key=f"citibike/source/{run}/sourcedir.tar.gz")
    serving_uri = serving.upload_serving_bundle(s3, bucket, TRAIN_DIR, key=f"citibike/source/{run}/serving.tar.gz")
    names = {h: job_name(run, h) for h in args.horizons}
    for h in args.horizons:
        spec = config["horizons"][h]
        jobs.submit(sm, jobs.training_job_request(names[h], role_arn, region, bucket, h, spec["family"], spec["hyperparameters"],
                                                  source_uri, instance_type=config["instance_type"], max_runtime_s=7200))

    results, promoted, ok = [], {}, True
    for h in args.horizons:                          # the jobs run side by side; reading them back one after the other
        spec, described = config["horizons"][h], jobs.wait(sm, names[h])
        row = {"horizon": h, "job": names[h], "candidate_mae": validation_mae(described), "billed_s": described.get("BillableTimeInSeconds"),
               "live_mae": live[h]["validation_mae"] if live[h] else None, "live_job": live[h]["job"] if live[h] else "–"}
        if described["TrainingJobStatus"] != "Completed":
            log("\n".join("  | " + line for line in jobs.log_tail(session, names[h], 60)))
            row["decision"], ok = f"training {described['TrainingJobStatus'].lower()}: {described.get('FailureReason', '?')[:200]}", False
            results.append(row)
            continue
        passed, reason = gate(row["candidate_mae"], row["live_mae"], args.tolerance_pct)
        if not passed:
            row["decision"], ok = f"kept the live model: {reason}", False
        elif args.dry_run:
            row["decision"] = f"would promote (dry run): {reason}"
        else:
            try:
                rows, expected = probe_rows(s3, bucket, h, described)
                check = promote(sm, runtime, names[h], spec, role_arn, region, described["ModelArtifacts"]["S3ModelArtifacts"],
                                serving_uri, live[h]["config"], rows, expected)
                promoted[h] = {"family": spec["family"], "horizon": h, "validation_mae": round(row["candidate_mae"], 3),
                               "test_mae": None, "job": names[h]}   # the test MAE is 09's, measured on the laptop: unknown for a retrained model
                row["decision"] = f"promoted: {reason}; probe {check['probe_s']} s"
            except Exception as e:
                row["decision"], ok = f"promotion failed, live model kept: {str(e)[:200]}", False
        log(f"{h}: {row['decision']}")
        results.append(row)

    if promoted:
        if serving.exists(lam.get_function, FunctionName=config["function"]):
            try:
                describe_in_lambda(lam, config["function"], promoted)
            except ClientError as e:
                error(f"the endpoints are promoted, the function's model description is not: {e}")
                ok = False
        else:
            log(f"no function {config['function']}: nothing to describe")
    log("\n" + report(results, run, args.dry_run))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
