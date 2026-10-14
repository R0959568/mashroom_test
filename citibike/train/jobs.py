"""SageMaker training and tuning jobs for citibike/train/train.py, through the plain AWS API (boto3): what notebook 10
submits to the AWS Academy Learner Lab and reads back. No SageMaker Python SDK: its current major version pulls in
torch and mlflow, and every field the SDK would hide is one the notebook should show.

The contract with the container (the scikit-learn framework image in script mode): the bundle sourcedir.tar.gz in S3
holds train.py and requirements.txt; the hyperparameters `sagemaker_program` and `sagemaker_submit_directory` tell the
container which script to run from where, and every other hyperparameter becomes a `--name value` argument of the
script; the channels `train` and `valid` are mounted under SM_CHANNEL_TRAIN and SM_CHANNEL_VALID; the model goes to
SM_MODEL_DIR (packed as model.tar.gz), metrics.json and predictions_valid.parquet to SM_OUTPUT_DATA_DIR (output.tar.gz).
The container installs requirements.txt before it runs the script, so the script's own packages travel with it.

Credentials are never passed here: boto3 reads them from the environment (the repo's git-ignored .env, loaded by the
notebook: AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_SESSION_TOKEN from the Learner Lab's "AWS Details" block, new
every session) or from ~/.aws/credentials.
"""
from __future__ import annotations

import io
import json
import tarfile
import time
from datetime import timezone
from pathlib import Path

import pandas as pd
from botocore.exceptions import ClientError

# The scikit-learn framework image per region (the two Learner Lab regions), script mode, CPU.
SKLEARN_IMAGE_ACCOUNTS = {"us-east-1": "683313688378", "us-west-2": "246618743249"}
SKLEARN_VERSION = "1.2-1"
OBJECTIVE_METRIC = "validation:MAE"
# The lines train.py prints, as the job's metrics: `validation:MAE=9.6343 validation:RMSE=... validation:WAPE_%=... validation:bias=...`
METRIC_DEFINITIONS = [{"Name": f"{split}:{name}", "Regex": rf"{split}:{name}=(-?[0-9.]+)"}
                      for split in ("validation", "train") for name in ("MAE", "RMSE", "WAPE_%", "bias")]
SAGEMAKER_KEYS = ("sagemaker_program", "sagemaker_submit_directory", "sagemaker_container_log_level", "sagemaker_region")
EXPIRED = ("ExpiredToken", "ExpiredTokenException", "InvalidClientTokenId", "UnrecognizedClientException", "RequestExpired")


# ---- Who and where ---------------------------------------------------------------------------------------------------
def identity(session) -> dict:
    """Account and caller of the credentials in use (STS), and the LabRole ARN built from the account: IAM is locked
    down in the Learner Lab, so the role is named, not looked up."""
    who = session.client("sts").get_caller_identity()
    account, parts = who["Account"], who["Arn"].split("/")
    caller = parts[1] if len(parts) > 2 else parts[-1]      # the assumed role's name (voclabs in the lab), never the user part
    return {"account": account, "caller": caller, "region": session.region_name,
            "role_arn": f"arn:aws:iam::{account}:role/LabRole"}


def roles_in_use(sm) -> list[str]:
    """The execution roles this account already hands to SageMaker: those of its notebook instances and Studio
    domains. In a lab whose policy forbids passing LabRole, one of these is the role the lab's own SageMaker
    resources run with, and usually the one the policy lets you pass. Anything the policy hides is skipped."""
    roles = []
    try:
        for n in sm.list_notebook_instances().get("NotebookInstances", []):
            roles.append(sm.describe_notebook_instance(NotebookInstanceName=n["NotebookInstanceName"]).get("RoleArn"))
    except ClientError:
        pass
    try:
        for d in sm.list_domains().get("Domains", []):
            roles.append(sm.describe_domain(DomainId=d["DomainId"]).get("DefaultUserSettings", {}).get("ExecutionRole"))
    except ClientError:
        pass
    return sorted({r for r in roles if r})


def masked(account: str) -> str:
    """An account id for a printed line: the last four digits only."""
    return "*" * (len(account) - 4) + account[-4:]


def image_uri(region: str, version: str = SKLEARN_VERSION) -> str:
    return f"{SKLEARN_IMAGE_ACCOUNTS[region]}.dkr.ecr.{region}.amazonaws.com/sagemaker-scikit-learn:{version}-cpu-py3"


# ---- S3: the bucket, the inputs, the source bundle -------------------------------------------------------------------
def ensure_bucket(s3, bucket: str, region: str) -> bool:
    """True when the bucket was created now, False when it was there."""
    try:
        s3.head_bucket(Bucket=bucket)
        return False
    except ClientError as e:
        if e.response["Error"]["Code"] not in ("404", "NoSuchBucket", "NotFound"):
            raise
    if region == "us-east-1":
        s3.create_bucket(Bucket=bucket)
    else:
        s3.create_bucket(Bucket=bucket, CreateBucketConfiguration={"LocationConstraint": region})
    return True


def upload_if_changed(s3, path: Path, bucket: str, key: str) -> str:
    """Uploads path to s3://bucket/key unless an object of the same size is there. Returns what happened."""
    size = Path(path).stat().st_size
    try:
        if s3.head_object(Bucket=bucket, Key=key)["ContentLength"] == size:
            return "already there"
    except ClientError as e:
        if e.response["Error"]["Code"] not in ("404", "NoSuchKey", "NotFound"):
            raise
    s3.upload_file(str(path), bucket, key)
    return "uploaded"


def channel_uri(bucket: str, horizon: str, split: str, prefix: str = "citibike/model_inputs") -> str:
    """The S3 prefix of one channel: a directory with one Parquet file, what train.py's read_inputs() reads."""
    return f"s3://{bucket}/{prefix}/{horizon}/{split}/"


def upload_inputs(s3, bucket: str, inputs_dir: Path, horizons, prefix: str = "citibike/model_inputs") -> pd.DataFrame:
    """train.parquet and valid.parquet per horizon (07 §2) to their channel prefixes; the test file stays home."""
    rows = []
    for horizon in horizons:
        for split in ("train", "valid"):
            path = Path(inputs_dir) / horizon / f"{split}.parquet"
            key = f"{prefix}/{horizon}/{split}/{split}.parquet"
            rows.append({"horizon": horizon, "split": split, "MB": round(path.stat().st_size / 1e6, 1),
                         "s3": f"s3://{bucket}/{key}", "status": upload_if_changed(s3, path, bucket, key)})
    return pd.DataFrame(rows)


def bundle_source(train_dir: Path, files=("train.py", "requirements.txt")) -> bytes:
    """sourcedir.tar.gz in memory: the script and its requirements, nothing else."""
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as tar:
        for name in files:
            tar.add(Path(train_dir) / name, arcname=name)
    return buffer.getvalue()


def upload_source(s3, bucket: str, train_dir: Path, key: str = "citibike/source/sourcedir.tar.gz") -> str:
    """The bundle to S3 (always: it is small and the script may have changed); returns its URI."""
    s3.put_object(Bucket=bucket, Key=key, Body=bundle_source(train_dir))
    return f"s3://{bucket}/{key}"


# ---- The requests ------------------------------------------------------------------------------------------------------
def encode(value) -> str:
    """A hyperparameter value as the container expects it: JSON in a string (the SDK's convention; the container
    decodes it, so 0.05 arrives as a number and "regression_l1" as a string)."""
    return json.dumps(value)


def decode(value: str):
    try:
        return json.loads(value)
    except (ValueError, TypeError):
        return value


def hyperparameters_of(described: dict) -> dict:
    """The script's hyperparameters of a described training job, decoded, without the container's own keys."""
    return {k: decode(v) for k, v in described.get("HyperParameters", {}).items()
            if k not in SAGEMAKER_KEYS and not k.startswith("_")}   # _tuning_objective_metric is the tuner's, not the script's


def channel(name: str, uri: str) -> dict:
    return {"ChannelName": name, "ContentType": "application/x-parquet", "InputMode": "File",
            "DataSource": {"S3DataSource": {"S3DataType": "S3Prefix", "S3Uri": uri, "S3DataDistributionType": "FullyReplicated"}}}


def training_job_request(name: str, role_arn: str, region: str, bucket: str, horizon: str, family: str, hyperparameters: dict,
                         source_uri: str, instance_type: str = "ml.m5.xlarge", max_runtime_s: int = 3600,
                         volume_gb: int = 30, output_prefix: str = "citibike/jobs") -> dict:
    """The CreateTrainingJob request for one run of train.py: the image, the bundle, the channels, the output, the
    machine and the time limit. `hyperparameters` are the script's own arguments (names as on the command line)."""
    hp = {"sagemaker_program": "train.py", "sagemaker_submit_directory": source_uri, "sagemaker_container_log_level": 20,
          "sagemaker_region": region, "family": family, "horizon": horizon, **hyperparameters}
    return {"TrainingJobName": name, "RoleArn": role_arn,
            "AlgorithmSpecification": {"TrainingImage": image_uri(region), "TrainingInputMode": "File",
                                       "MetricDefinitions": METRIC_DEFINITIONS},
            "HyperParameters": {k: encode(v) for k, v in hp.items()},
            "InputDataConfig": [channel("train", channel_uri(bucket, horizon, "train")), channel("valid", channel_uri(bucket, horizon, "valid"))],
            "OutputDataConfig": {"S3OutputPath": f"s3://{bucket}/{output_prefix}/"},
            "ResourceConfig": {"InstanceType": instance_type, "InstanceCount": 1, "VolumeSizeInGB": volume_gb},
            "StoppingCondition": {"MaxRuntimeInSeconds": max_runtime_s}}


def continuous(name: str, low: float, high: float, scaling: str = "Linear") -> dict:
    return {"Name": name, "MinValue": str(low), "MaxValue": str(high), "ScalingType": scaling}


def integer(name: str, low: int, high: int, scaling: str = "Linear") -> dict:
    return {"Name": name, "MinValue": str(low), "MaxValue": str(high), "ScalingType": scaling}


def categorical(name: str, values) -> dict:
    return {"Name": name, "Values": [str(v) for v in values]}


def tuning_job_request(name: str, training_request: dict, continuous_ranges=(), integer_ranges=(), categorical_ranges=(),
                       strategy: str = "Bayesian", max_jobs: int | None = 12, max_parallel: int = 2) -> dict:
    """The CreateHyperParameterTuningJob request around a training request: the ranges replace the hyperparameters
    of the same names, the rest stays static; the objective is the validation MAE, minimised. Grid search takes
    categorical ranges only and runs every combination, so it gets no MaxNumberOfTrainingJobs."""
    tuned = {r["Name"] for r in (*continuous_ranges, *integer_ranges, *categorical_ranges)}
    static = {k: v for k, v in training_request["HyperParameters"].items() if k not in tuned}
    limits = {"MaxParallelTrainingJobs": max_parallel}
    if strategy != "Grid":
        assert max_jobs, "max_jobs is required unless the strategy is Grid"
        limits["MaxNumberOfTrainingJobs"] = max_jobs
    return {"HyperParameterTuningJobName": name,
            "HyperParameterTuningJobConfig": {
                "Strategy": strategy,
                "HyperParameterTuningJobObjective": {"Type": "Minimize", "MetricName": OBJECTIVE_METRIC},
                "ResourceLimits": limits,
                "ParameterRanges": {"ContinuousParameterRanges": list(continuous_ranges), "IntegerParameterRanges": list(integer_ranges),
                                    "CategoricalParameterRanges": list(categorical_ranges)},
                "TrainingJobEarlyStoppingType": "Off"},
            "TrainingJobDefinition": {
                "StaticHyperParameters": static,
                "AlgorithmSpecification": training_request["AlgorithmSpecification"],
                "RoleArn": training_request["RoleArn"],
                "InputDataConfig": training_request["InputDataConfig"],
                "OutputDataConfig": training_request["OutputDataConfig"],
                "ResourceConfig": training_request["ResourceConfig"],
                "StoppingCondition": training_request["StoppingCondition"]}}


# ---- Submitting, waiting, reading back -------------------------------------------------------------------------------
def submit(client, request: dict) -> str:
    """Creates the job of the request (a training job or a tuning job, told apart by the request's name field); a job
    of that name that already exists is left alone, so a cell can be run again after a kernel or session died."""
    tuning = "HyperParameterTuningJobName" in request
    name = request["HyperParameterTuningJobName" if tuning else "TrainingJobName"]
    try:
        if tuning:
            client.create_hyper_parameter_tuning_job(**request)
        else:
            client.create_training_job(**request)
        print(f"submitted {'tuning' if tuning else 'training'} job {name}")
    except ClientError as e:
        if e.response["Error"]["Code"] != "ResourceInUse":
            raise
        print(f"{name} exists already: read back, not resubmitted")
    return name


def describe(client, name: str, tuning: bool = False) -> dict:
    return client.describe_hyper_parameter_tuning_job(HyperParameterTuningJobName=name) if tuning \
        else client.describe_training_job(TrainingJobName=name)


def status_of(described: dict) -> tuple[str, str]:
    if "HyperParameterTuningJobStatus" in described:
        counters = described.get("TrainingJobStatusCounters", {})
        return described["HyperParameterTuningJobStatus"], ", ".join(f"{k.lower()} {v}" for k, v in counters.items() if v)
    return described["TrainingJobStatus"], described.get("SecondaryStatus", "")


def wait(client, name: str, tuning: bool = False, poll_s: int = 60, max_wait_s: int = 4 * 3600) -> dict:
    """Polls a job until it is Completed, Failed or Stopped, printing each change of status. An expired lab session
    stops the wait with the steps to resume; the job itself keeps running in AWS."""
    last, t0 = None, time.time()
    while True:
        try:
            d = describe(client, name, tuning)
        except ClientError as e:
            if e.response["Error"]["Code"] in EXPIRED:
                raise RuntimeError("the lab session's credentials expired: start the lab again, paste the new credentials "
                                   "into ~/.aws/credentials, run the setup cell and then this cell again; the job keeps "
                                   "running in AWS and is read back by name") from e
            raise
        status, detail = status_of(d)
        if (status, detail) != last:
            print(f"{time.strftime('%H:%M:%S')} {name}: {status} {detail}".rstrip())
            last = (status, detail)
        if status in ("Completed", "Failed", "Stopped"):
            if status == "Failed":
                print(f"failure reason: {d.get('FailureReason', '?')}")
            return d
        if time.time() - t0 > max_wait_s:
            raise TimeoutError(f"{name} still {status} after {max_wait_s} s")
        time.sleep(poll_s)


def log_tail(session, job_name: str, lines: int = 20) -> list[str]:
    """The last lines of a training job's CloudWatch log (the container's output: the package install, the script's
    prints, a traceback when it failed); an empty list when the log is not there yet."""
    logs = session.client("logs")
    try:
        streams = logs.describe_log_streams(logGroupName="/aws/sagemaker/TrainingJobs", logStreamNamePrefix=job_name)["logStreams"]
    except ClientError as e:
        if e.response["Error"]["Code"] == "ResourceNotFoundException":
            return []
        raise
    if not streams:
        return []
    events = logs.get_log_events(logGroupName="/aws/sagemaker/TrainingJobs", logStreamName=streams[0]["logStreamName"],
                                 limit=lines, startFromHead=False)["events"]
    return [e["message"].rstrip() for e in events]


def _seconds(d: dict, start: str, end: str) -> float | None:
    return (d[end] - d[start]).total_seconds() if d.get(start) and d.get(end) else None


def trials(client, tuning_job_name: str) -> pd.DataFrame:
    """One row per training job of a tuning job: its tuned hyperparameters, its objective metric, its time."""
    rows = []
    for page in client.get_paginator("list_training_jobs_for_hyper_parameter_tuning_job").paginate(HyperParameterTuningJobName=tuning_job_name):
        for j in page["TrainingJobSummaries"]:
            metric = j.get("FinalHyperParameterTuningJobObjectiveMetric", {})
            rows.append({"job": j["TrainingJobName"], "status": j["TrainingJobStatus"],
                         **{k: decode(v) for k, v in j.get("TunedHyperParameters", {}).items()},
                         "MAE": metric.get("Value"), "training (s)": _seconds(j, "TrainingStartTime", "TrainingEndTime"),
                         "failure": j.get("FailureReason", "")})
    table = pd.DataFrame(rows)
    return table.sort_values("MAE", na_position="last").set_index("job") if len(table) else table


def training_jobs_table(client, names) -> pd.DataFrame:
    """The same shape for plain training jobs (the fallback when tuning jobs are not allowed): one row per name."""
    rows = []
    for name in names:
        d = describe(client, name)
        metrics = {m["MetricName"]: m["Value"] for m in d.get("FinalMetricDataList", [])}
        rows.append({"job": name, "status": d["TrainingJobStatus"], **hyperparameters_of(d),
                     "MAE": metrics.get(OBJECTIVE_METRIC), "training (s)": _seconds(d, "TrainingStartTime", "TrainingEndTime"),
                     "failure": d.get("FailureReason", "")})
    table = pd.DataFrame(rows)
    return table.sort_values("MAE", na_position="last").set_index("job") if len(table) else table


def fetch_artefacts(s3, described: dict, folder: Path) -> dict:
    """model.tar.gz (model.joblib) and output.tar.gz (metrics.json, predictions_valid.parquet) of a completed
    training job, unpacked into folder, next to a job.json that records the job. Returns that record."""
    folder = Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    model_uri = described["ModelArtifacts"]["S3ModelArtifacts"]
    bucket, key = model_uri[len("s3://"):].split("/", 1)
    for archive, s3_key in (("model.tar.gz", key), ("output.tar.gz", key.replace("model.tar.gz", "output.tar.gz"))):
        target = folder / archive
        s3.download_file(bucket, s3_key, str(target))
        with tarfile.open(target) as tar:
            tar.extractall(folder, filter="data")
        target.unlink()
    record = {"job": described["TrainingJobName"], "status": described["TrainingJobStatus"],
              "hyperparameters": hyperparameters_of(described),
              "instance": described["ResourceConfig"]["InstanceType"], "image": described["AlgorithmSpecification"]["TrainingImage"],
              "metrics": {m["MetricName"]: m["Value"] for m in described.get("FinalMetricDataList", [])},
              "training_seconds": _seconds(described, "TrainingStartTime", "TrainingEndTime"),
              "billable_seconds": described.get("BillableTimeInSeconds"),
              "model_uri": model_uri,
              "fetched": pd.Timestamp.now(tz=timezone.utc).isoformat(timespec="seconds")}
    (folder / "job.json").write_text(json.dumps(record, indent=1, default=str), encoding="utf-8")
    return record
