"""Tests for train/retrain.py: names, the gate, the Lambda variables, and main() end to end against fake SageMaker, S3,
runtime and Lambda clients (promote, keep out, roll back). No network, no credentials."""
import io
import json
import re
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
import retrain  # noqa: E402

ACCOUNT = "123456789012"
BUCKET = f"sagemaker-us-east-1-{ACCOUNT}"
WHO = {"account": ACCOUNT, "caller": "voclabs", "region": "us-east-1", "role_arn": f"arn:aws:iam::{ACCOUNT}:role/LabRole"}
LIVE_MAE = {"day": 9.6343, "rest_of_month": 108.4714}
NAME = re.compile(r"^[a-zA-Z0-9](-*[a-zA-Z0-9]){0,62}$")     # SageMaker's pattern for job, model and config names


def not_found(what):
    return ClientError({"Error": {"Code": "ValidationException", "Message": f"Could not find {what}"}}, "Describe")


# ---- Pure functions ----------------------------------------------------------------------------------------------------
def test_names_are_valid_sagemaker_names_and_unique_per_run():
    run = retrain.run_name({"GITHUB_RUN_ID": "18234567890", "GITHUB_RUN_ATTEMPT": "2"})
    assert run == "ci18234567890-2"
    for horizon in ("day", "rest_of_month"):
        assert NAME.match(retrain.job_name(run, horizon))
    assert retrain.run_name({}).startswith("local")


def test_job_of_model_data():
    assert retrain.job_of_model_data(f"s3://{BUCKET}/citibike/jobs/citibike-s1-day-train/output/model.tar.gz") == "citibike-s1-day-train"
    with pytest.raises(AssertionError):
        retrain.job_of_model_data(f"s3://{BUCKET}/somewhere/model.joblib")


@pytest.mark.parametrize("candidate, live, passed", [(9.60, 9.6343, True),     # better
                                                     (9.70, 9.6343, True),     # worse, within 1 %
                                                     (9.80, 9.6343, False),    # worse by 1.7 %
                                                     (None, 9.6343, False),
                                                     (9.60, None, False)])
def test_gate(candidate, live, passed):
    assert retrain.gate(candidate, live, 1.0)[0] is passed


def test_lambda_variables_replace_only_the_model_descriptions():
    current = {"API_KEY": "k", "ENDPOINT_DAY": "citibike-day-s1", "MODEL_DAY": "{}", "MODEL_REST_OF_MONTH": '{"old": 1}'}
    new = retrain.lambda_variables(current, {"day": {"validation_mae": 9.5}})
    assert new["API_KEY"] == "k" and new["ENDPOINT_DAY"] == "citibike-day-s1" and new["MODEL_REST_OF_MONTH"] == '{"old": 1}'
    assert json.loads(new["MODEL_DAY"]) == {"validation_mae": 9.5}


def test_config_builds_valid_training_requests():
    config = retrain.load_config()
    shape = botocore.session.get_session().get_service_model("sagemaker").operation_model("CreateTrainingJob").input_shape
    for horizon, spec in config["horizons"].items():
        request = jobs.training_job_request(retrain.job_name("ci1-1", horizon), WHO["role_arn"], config["region"], BUCKET, horizon,
                                            spec["family"], spec["hyperparameters"], f"s3://{BUCKET}/citibike/source/ci1-1/sourcedir.tar.gz",
                                            instance_type=config["instance_type"])
        validate_parameters(request, shape)
        assert spec["family"] in ("lightgbm", "extra_trees")


# ---- main() against fakes ----------------------------------------------------------------------------------------------
class Waiter:
    def wait(self, **kwargs):
        pass


class FakeSageMaker:
    """Endpoints, configs, models and training jobs in dicts; every job completes at once with the MAE asked for."""

    def __init__(self, candidate_mae, fail_update=()):
        self.candidate_mae, self.fail_update = candidate_mae, set(fail_update)
        self.endpoints, self.configs, self.models, self.jobs = {}, {}, {}, {}
        for horizon, endpoint in (("day", "citibike-day-s1"), ("rest_of_month", "citibike-rest-of-month-s1")):
            job = f"citibike-s1-{horizon}-train"
            self.jobs[job] = self._job(job, LIVE_MAE[horizon])
            self.models[job] = f"s3://{BUCKET}/citibike/jobs/{job}/output/model.tar.gz"
            self.configs[job] = job
            self.endpoints[endpoint] = job

    @staticmethod
    def _job(name, mae):
        return {"TrainingJobName": name, "TrainingJobStatus": "Completed", "BillableTimeInSeconds": 300,
                "FinalMetricDataList": [{"MetricName": "validation:MAE", "Value": mae}],
                "ModelArtifacts": {"S3ModelArtifacts": f"s3://{BUCKET}/citibike/jobs/{name}/output/model.tar.gz"}}

    def create_training_job(self, **request):
        horizon = json.loads(request["HyperParameters"]["horizon"])
        self.jobs[request["TrainingJobName"]] = self._job(request["TrainingJobName"], self.candidate_mae[horizon])

    def describe_training_job(self, TrainingJobName):
        return self.jobs[TrainingJobName]

    def describe_endpoint(self, EndpointName):
        if EndpointName not in self.endpoints:
            raise not_found(f"endpoint {EndpointName}")
        return {"EndpointName": EndpointName, "EndpointConfigName": self.endpoints[EndpointName], "EndpointStatus": "InService"}

    def update_endpoint(self, EndpointName, EndpointConfigName):
        if EndpointName not in self.fail_update:       # a failed update: SageMaker stays on the old config
            self.endpoints[EndpointName] = EndpointConfigName

    def describe_endpoint_config(self, EndpointConfigName):
        if EndpointConfigName not in self.configs:
            raise not_found(f"endpoint config {EndpointConfigName}")
        return {"ProductionVariants": [{"ModelName": self.configs[EndpointConfigName]}]}

    def create_endpoint_config(self, EndpointConfigName, ProductionVariants):
        self.configs[EndpointConfigName] = ProductionVariants[0]["ModelName"]

    def describe_model(self, ModelName):
        if ModelName not in self.models:
            raise not_found(f"model {ModelName}")
        return {"PrimaryContainer": {"ModelDataUrl": self.models[ModelName]}}

    def create_model(self, ModelName, PrimaryContainer, **kwargs):
        self.models[ModelName] = PrimaryContainer["ModelDataUrl"]


VALID = pd.DataFrame({"station_key": [101, 102, 103, 104, 105, 106], "day": pd.to_datetime(["2026-07-01"] * 6),
                      "dep_lag_1": [10.0, 20, 30, 40, 50, 60], "y": 1.0, "scale": 1.0, "actual": [11, 19, 33, 38, 52, 61]})
PREDICTIONS = VALID[["station_key", "day", "actual"]].assign(station_key=lambda d: d.station_key.astype(str), prediction=[10.5, 20.1, 31, 39, 50, 60])


class FakeS3:
    def __init__(self):
        self.put = {}

    def head_object(self, Bucket, Key):
        if not Key.startswith("citibike/model_inputs/"):
            raise ClientError({"Error": {"Code": "404", "Message": "Not Found"}}, "HeadObject")
        return {"ContentLength": 1}

    def put_object(self, Bucket, Key, Body):
        self.put[Key] = Body

    def download_file(self, bucket, key, path):
        VALID.to_parquet(path, index=False)

    def get_object(self, Bucket, Key):
        assert Key.endswith("output.tar.gz")
        data, buffer = io.BytesIO(), io.BytesIO()
        PREDICTIONS.to_parquet(data, index=False)
        with tarfile.open(fileobj=buffer, mode="w:gz") as tar:
            info = tarfile.TarInfo("predictions_valid.parquet")
            info.size = len(data.getvalue())
            tar.addfile(info, io.BytesIO(data.getvalue()))
        return {"Body": io.BytesIO(buffer.getvalue())}


class FakeRuntime:
    def __init__(self, offset=0.0):
        self.offset = offset

    def invoke_endpoint(self, EndpointName, Body, **kwargs):
        n = len(json.loads(Body)["rows"])
        answer = {"predictions": [p + self.offset for p in PREDICTIONS.prediction.round(2).head(n)]}
        return {"Body": io.BytesIO(json.dumps(answer).encode())}


class FakeLambda:
    def __init__(self):
        self.variables = {"API_KEY": "secret", "ENDPOINT_DAY": "citibike-day-s1", "MODEL_DAY": "{}"}

    def get_function(self, FunctionName):
        return {"Configuration": {"FunctionName": FunctionName}}

    def get_function_configuration(self, FunctionName):
        return {"Environment": {"Variables": dict(self.variables)}}

    def update_function_configuration(self, FunctionName, Environment):
        self.variables = Environment["Variables"]

    def get_waiter(self, name):
        return Waiter()


class FakeSession:
    def __init__(self, sm, runtime):
        self.clients = {"sagemaker": sm, "s3": FakeS3(), "lambda": FakeLambda(), "sagemaker-runtime": runtime}

    def client(self, name, config=None):
        return self.clients[name]


@pytest.fixture
def fake(monkeypatch):
    def make(candidate_mae, fail_update=(), offset=0.0):
        session = FakeSession(FakeSageMaker(candidate_mae, fail_update), FakeRuntime(offset))
        monkeypatch.setattr(retrain, "connect", lambda region: (session, WHO))
        monkeypatch.setenv("GITHUB_RUN_ID", "7")
        monkeypatch.setenv("GITHUB_RUN_ATTEMPT", "1")
        monkeypatch.delenv("GITHUB_STEP_SUMMARY", raising=False)
        return session.clients
    return make


def test_main_promotes_a_candidate_that_passes_the_gate(fake):
    clients = fake({"day": 9.60, "rest_of_month": 108.0})
    assert retrain.main([]) == 0
    sm = clients["sagemaker"]
    assert sm.endpoints == {"citibike-day-s1": "citibike-ci7-1-day", "citibike-rest-of-month-s1": "citibike-ci7-1-rest-of-month"}
    assert "citibike/source/ci7-1/serving.tar.gz" in clients["s3"].put          # the live bundle's key is never overwritten
    variables = clients["lambda"].variables
    assert variables["API_KEY"] == "secret"
    assert json.loads(variables["MODEL_DAY"]) == {"family": "lightgbm", "horizon": "day", "validation_mae": 9.6, "test_mae": None, "job": "citibike-ci7-1-day"}


def test_main_keeps_the_live_model_when_the_candidate_is_worse(fake):
    clients = fake({"day": 9.90, "rest_of_month": 108.0})
    assert retrain.main([]) == 1
    assert clients["sagemaker"].endpoints["citibike-day-s1"] == "citibike-s1-day-train"
    assert clients["sagemaker"].endpoints["citibike-rest-of-month-s1"] == "citibike-ci7-1-rest-of-month"
    assert "MODEL_REST_OF_MONTH" in clients["lambda"].variables and clients["lambda"].variables["MODEL_DAY"] == "{}"


def test_main_dry_run_promotes_nothing(fake):
    clients = fake({"day": 9.60, "rest_of_month": 108.0})
    assert retrain.main(["--dry-run", "--horizons", "day"]) == 0
    assert clients["sagemaker"].endpoints["citibike-day-s1"] == "citibike-s1-day-train"
    assert "citibike-ci7-1-day" in clients["sagemaker"].jobs and "citibike-ci7-1-rest-of-month" not in clients["sagemaker"].jobs


def test_main_rolls_back_when_the_probe_fails(fake):
    clients = fake({"day": 9.60, "rest_of_month": 108.0}, offset=1.0)   # the endpoint answers 1 departure off
    assert retrain.main(["--horizons", "day"]) == 1
    assert clients["sagemaker"].endpoints["citibike-day-s1"] == "citibike-s1-day-train"


def test_main_reports_a_failed_endpoint_update(fake):
    clients = fake({"day": 9.60, "rest_of_month": 108.0}, fail_update=["citibike-day-s1"])
    assert retrain.main(["--horizons", "day"]) == 1
    assert clients["sagemaker"].endpoints["citibike-day-s1"] == "citibike-s1-day-train"


def test_main_stops_before_training_without_an_endpoint(fake):
    clients = fake({"day": 9.60, "rest_of_month": 108.0})
    del clients["sagemaker"].endpoints["citibike-day-s1"]
    assert retrain.main([]) == 1
    assert not any(name.startswith("citibike-ci7") for name in clients["sagemaker"].jobs)
