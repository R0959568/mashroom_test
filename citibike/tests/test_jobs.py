"""Tests for train/jobs.py: the SageMaker requests validate against the AWS API model, the hyperparameters survive the
container's decoding, the bundle and the artefacts have the right members. No network, no credentials."""
import io
import json
import sys
import tarfile
from pathlib import Path

import botocore.session
import pandas as pd
import pytest
from botocore.exceptions import ClientError
from botocore.validate import validate_parameters

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "train"))

import jobs  # noqa: E402

REGION, BUCKET, ROLE = "us-east-1", "sagemaker-us-east-1-123456789012", "arn:aws:iam::123456789012:role/LabRole"
SOURCE = f"s3://{BUCKET}/citibike/source/sourcedir.tar.gz"


def shape(operation):
    return botocore.session.get_session().get_service_model("sagemaker").operation_model(operation).input_shape


def day_request(name="citibike-r1-smoke-day"):
    return jobs.training_job_request(name, ROLE, REGION, BUCKET, "day", "lightgbm",
                                     {"n-estimators": 100, "objective": "regression_l1", "learning-rate": 0.05, "sample-stations": 0.05},
                                     SOURCE, max_runtime_s=1800)


def test_training_job_request_validates_against_the_api_model():
    request = day_request()
    validate_parameters(request, shape("CreateTrainingJob"))   # raises ParamValidationError on a wrong field
    assert request["AlgorithmSpecification"]["TrainingImage"] == "683313688378.dkr.ecr.us-east-1.amazonaws.com/sagemaker-scikit-learn:1.2-1-cpu-py3"
    assert [c["ChannelName"] for c in request["InputDataConfig"]] == ["train", "valid"]
    assert request["InputDataConfig"][0]["DataSource"]["S3DataSource"]["S3Uri"] == f"s3://{BUCKET}/citibike/model_inputs/day/train/"
    assert request["ResourceConfig"] == {"InstanceType": "ml.m5.xlarge", "InstanceCount": 1, "VolumeSizeInGB": 30}


def test_hyperparameters_are_json_encoded_as_the_container_expects():
    hp = day_request()["HyperParameters"]
    assert hp["sagemaker_program"] == '"train.py"' and hp["sagemaker_submit_directory"] == json.dumps(SOURCE)
    assert hp["objective"] == '"regression_l1"' and hp["learning-rate"] == "0.05" and hp["n-estimators"] == "100"
    assert jobs.decode(hp["objective"]) == "regression_l1" and jobs.decode(hp["learning-rate"]) == 0.05
    assert jobs.decode("regression") == "regression"          # a tuner's categorical value arrives without quotes
    described = {"HyperParameters": hp}
    assert jobs.hyperparameters_of(described) == {"family": "lightgbm", "horizon": "day", "n-estimators": 100, "objective": "regression_l1",
                                                  "learning-rate": 0.05, "sample-stations": 0.05}


def test_tuning_job_request_validates_and_keeps_the_rest_static():
    base = day_request("citibike-r1-day-tune")
    request = jobs.tuning_job_request(
        "citibike-r1-day-tune", base,
        continuous_ranges=[jobs.continuous("learning-rate", 0.02, 0.2, "Logarithmic"), jobs.continuous("subsample", 0.5, 1.0)],
        integer_ranges=[jobs.integer("num-leaves", 63, 1023, "Logarithmic")],
        categorical_ranges=[jobs.categorical("objective", ["regression", "regression_l1", "poisson"])],
        max_jobs=12, max_parallel=2)
    validate_parameters(request, shape("CreateHyperParameterTuningJob"))
    static = request["TrainingJobDefinition"]["StaticHyperParameters"]
    assert "learning-rate" not in static and "objective" not in static and static["n-estimators"] == "100"
    assert request["HyperParameterTuningJobConfig"]["ResourceLimits"] == {"MaxParallelTrainingJobs": 2, "MaxNumberOfTrainingJobs": 12}
    assert request["HyperParameterTuningJobConfig"]["HyperParameterTuningJobObjective"] == {"Type": "Minimize", "MetricName": "validation:MAE"}


def test_grid_strategy_takes_no_job_count():
    base = jobs.training_job_request("citibike-r1-rest-tune", ROLE, REGION, BUCKET, "rest_of_month", "extra_trees",
                                     {"n-estimators": 100}, SOURCE)
    request = jobs.tuning_job_request("citibike-r1-rest-tune", base, strategy="Grid", max_jobs=None, max_parallel=2,
                                      categorical_ranges=[jobs.categorical("min-samples-leaf", [25, 50, 100]), jobs.categorical("max-features", [0.7, 1.0])])
    validate_parameters(request, shape("CreateHyperParameterTuningJob"))
    assert request["HyperParameterTuningJobConfig"]["ResourceLimits"] == {"MaxParallelTrainingJobs": 2}
    assert request["HyperParameterTuningJobConfig"]["ParameterRanges"]["CategoricalParameterRanges"][0]["Values"] == ["25", "50", "100"]


def test_metric_regex_reads_the_script_line():
    import re
    line = "validation:MAE=9.6343 validation:RMSE=19.0011 validation:WAPE_%=16.4479 validation:bias=-1.0675"
    found = {m["Name"]: re.search(m["Regex"], line) for m in jobs.METRIC_DEFINITIONS if m["Name"].startswith("validation")}
    assert {k: float(v.group(1)) for k, v in found.items()} == {"validation:MAE": 9.6343, "validation:RMSE": 19.0011,
                                                                "validation:WAPE_%": 16.4479, "validation:bias": -1.0675}


def test_bundle_holds_the_script_and_its_requirements(tmp_path):
    (tmp_path / "train.py").write_text("print('hi')\n")
    (tmp_path / "requirements.txt").write_text("lightgbm>=4.0\n")
    with tarfile.open(fileobj=io.BytesIO(jobs.bundle_source(tmp_path)), mode="r:gz") as tar:
        assert sorted(tar.getnames()) == ["requirements.txt", "train.py"]


class FakeS3:
    """download_file copies prepared archives; head/create/put record what was asked."""
    def __init__(self, archives: dict, existing: dict | None = None):
        self.archives, self.existing, self.calls = archives, existing or {}, []

    def download_file(self, bucket, key, target):
        Path(target).write_bytes(self.archives[key])

    def head_object(self, Bucket, Key):
        if Key in self.existing:
            return {"ContentLength": self.existing[Key]}
        raise ClientError({"Error": {"Code": "404", "Message": "not found"}}, "HeadObject")

    def upload_file(self, path, bucket, key):
        self.calls.append(("upload", key))


def targz(members: dict) -> bytes:
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as tar:
        for name, data in members.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            tar.addfile(info, io.BytesIO(data))
    return buffer.getvalue()


def test_fetch_artefacts_unpacks_both_archives_and_records_the_job(tmp_path):
    described = {"TrainingJobName": "citibike-r1-smoke-day", "TrainingJobStatus": "Completed",
                 "HyperParameters": day_request()["HyperParameters"],
                 "ResourceConfig": {"InstanceType": "ml.m5.xlarge"}, "AlgorithmSpecification": {"TrainingImage": jobs.image_uri(REGION)},
                 "FinalMetricDataList": [{"MetricName": "validation:MAE", "Value": 9.63}],
                 "ModelArtifacts": {"S3ModelArtifacts": f"s3://{BUCKET}/citibike/jobs/citibike-r1-smoke-day/output/model.tar.gz"},
                 "TrainingStartTime": pd.Timestamp("2026-10-08 10:00:00"), "TrainingEndTime": pd.Timestamp("2026-10-08 10:05:00"),
                 "BillableTimeInSeconds": 300}
    s3 = FakeS3({"citibike/jobs/citibike-r1-smoke-day/output/model.tar.gz": targz({"model.joblib": b"model"}),
                 "citibike/jobs/citibike-r1-smoke-day/output/output.tar.gz": targz({"metrics.json": b"{}", "predictions_valid.parquet": b"p"})})
    record = jobs.fetch_artefacts(s3, described, tmp_path / "run")
    assert sorted(p.name for p in (tmp_path / "run").iterdir()) == ["job.json", "metrics.json", "model.joblib", "predictions_valid.parquet"]
    assert record["hyperparameters"]["objective"] == "regression_l1" and record["metrics"] == {"validation:MAE": 9.63}
    assert record["training_seconds"] == 300 and json.loads((tmp_path / "run" / "job.json").read_text())["job"] == "citibike-r1-smoke-day"


def test_upload_if_changed_skips_an_object_of_the_same_size(tmp_path):
    path = tmp_path / "train.parquet"
    path.write_bytes(b"x" * 10)
    s3 = FakeS3({}, existing={"same": 10, "other": 3})
    assert jobs.upload_if_changed(s3, path, BUCKET, "same") == "already there"
    assert jobs.upload_if_changed(s3, path, BUCKET, "other") == "uploaded"
    assert jobs.upload_if_changed(s3, path, BUCKET, "new") == "uploaded"
    assert s3.calls == [("upload", "other"), ("upload", "new")]


class FakePaginator:
    def __init__(self, pages):
        self.pages = pages

    def paginate(self, **kwargs):
        return iter(self.pages)


class FakeSageMaker:
    def __init__(self, pages):
        self.pages = pages

    def get_paginator(self, name):
        assert name == "list_training_jobs_for_hyper_parameter_tuning_job"
        return FakePaginator(self.pages)


def test_trials_table_sorts_on_the_objective_and_decodes_the_values():
    pages = [{"TrainingJobSummaries": [
        {"TrainingJobName": "t-001", "TrainingJobStatus": "Completed", "TunedHyperParameters": {"num-leaves": "255", "objective": "poisson"},
         "FinalHyperParameterTuningJobObjectiveMetric": {"Value": 9.70},
         "TrainingStartTime": pd.Timestamp("2026-10-08 10:00:00"), "TrainingEndTime": pd.Timestamp("2026-10-08 10:06:00")},
        {"TrainingJobName": "t-002", "TrainingJobStatus": "Completed", "TunedHyperParameters": {"num-leaves": "511", "objective": "regression_l1"},
         "FinalHyperParameterTuningJobObjectiveMetric": {"Value": 9.60}},
        {"TrainingJobName": "t-003", "TrainingJobStatus": "Failed", "TunedHyperParameters": {"num-leaves": "1023", "objective": "regression"},
         "FailureReason": "MemoryError"}]}]
    table = jobs.trials(FakeSageMaker(pages), "citibike-r1-day-tune")
    assert list(table.index) == ["t-002", "t-001", "t-003"]
    assert table.loc["t-001", "num-leaves"] == 255 and table.loc["t-002", "objective"] == "regression_l1"
    assert table.loc["t-001", "training (s)"] == 360 and table.loc["t-003", "failure"] == "MemoryError"


def test_status_of_reads_both_kinds_of_job():
    assert jobs.status_of({"TrainingJobStatus": "InProgress", "SecondaryStatus": "Training"}) == ("InProgress", "Training")
    assert jobs.status_of({"HyperParameterTuningJobStatus": "InProgress",
                           "TrainingJobStatusCounters": {"Completed": 3, "InProgress": 2, "RetryableError": 0, "NonRetryableError": 0, "Stopped": 0}}) \
        == ("InProgress", "completed 3, inprogress 2")


def test_masked_account_shows_four_digits_only():
    assert jobs.masked("123456789012") == "********9012"


class FakeSageMakerRoles:
    def list_notebook_instances(self):
        return {"NotebookInstances": [{"NotebookInstanceName": "lab-notebook"}]}

    def describe_notebook_instance(self, NotebookInstanceName):
        return {"RoleArn": "arn:aws:iam::123456789012:role/service-role/c1234-SageMakerRole"}

    def list_domains(self):
        raise ClientError({"Error": {"Code": "AccessDeniedException", "Message": "no"}}, "ListDomains")


def test_roles_in_use_lists_what_it_may_see_and_skips_what_it_may_not():
    assert jobs.roles_in_use(FakeSageMakerRoles()) == ["arn:aws:iam::123456789012:role/service-role/c1234-SageMakerRole"]
