"""Tests for train/inference.py and train/serving.py: the inference functions on a real run folder, the SageMaker,
Lambda and API Gateway requests against the AWS API models, the Lambda handler against fake events. No network."""
import json
import sys
import zipfile
import io
from pathlib import Path

import botocore.session
import numpy as np
import pandas as pd
import pytest
from botocore.validate import validate_parameters

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "train"))

import inference  # noqa: E402
import serving  # noqa: E402
import train  # noqa: E402

REGION, ROLE, ACCOUNT = "us-east-1", "arn:aws:iam::123456789012:role/LabRole", "123456789012"


def shape(service, operation):
    return botocore.session.get_session().get_service_model(service).operation_model(operation).input_shape


@pytest.fixture(scope="module")
def run_folder(tmp_path_factory):
    """A tiny LightGBM run of train.py on synthetic inputs: model.joblib and metrics.json, as a job's model.tar.gz holds them."""
    rng = np.random.default_rng(0)
    n = 400
    frame = pd.DataFrame({"station_key": rng.choice(["a", "b", "c", "d"], n).astype(str), "day": pd.Timestamp("2024-01-01") + pd.to_timedelta(rng.integers(0, 300, n), "D"),
                          "dep_lag_1": rng.poisson(20, n).astype(float), "dep_mean_7": rng.poisson(20, n).astype(float), "iso_dow": rng.integers(1, 8, n)})
    frame["y"] = frame.dep_lag_1 * 0.6 + frame.dep_mean_7 * 0.4 + rng.normal(0, 2, n)
    frame["scale"], frame["actual"] = 1.0, frame.y.round().astype("int64")
    folder = tmp_path_factory.mktemp("run")
    frame.iloc[:300].to_parquet(folder / "train.parquet", index=False)
    frame.iloc[300:].to_parquet(folder / "valid.parquet", index=False)
    train.main(["--family", "lightgbm", "--horizon", "day", "--n-estimators", "20", "--curve-every", "0", "--min-child-samples", "5",
                "--model-dir", str(folder / "model"), "--train", str(folder / "train.parquet"), "--valid", str(folder / "valid.parquet")])
    return folder


def test_inference_functions_reproduce_the_run_on_its_validation_rows(run_folder):
    model = inference.model_fn(str(run_folder / "model"))
    assert model["family"] == "lightgbm" and model["horizon"] == "day" and "station_key" in model["inputs"]
    valid = pd.read_parquet(run_folder / "valid.parquet")
    saved = pd.read_parquet(run_folder / "model" / "predictions_valid.parquet")
    rows = json.loads(valid.head(5).assign(day=lambda d: d.day.astype(str)).to_json(orient="records"))
    df = inference.input_fn(json.dumps({"rows": rows}))
    predictions = inference.predict_fn(df, model)
    assert np.allclose(predictions, saved.prediction.head(5).to_numpy(), atol=1e-6)
    body = json.loads(inference.output_fn(predictions))
    assert len(body["predictions"]) == 5 and all(p >= 0 for p in body["predictions"])
    full = json.loads(inference.respond(predictions, model))
    assert full["model"] == {"family": "lightgbm", "horizon": "day", "validation_mae": model["validation_mae"]}


def test_input_fn_defaults_the_scale_and_rejects_other_content_types(run_folder):
    df = inference.input_fn(b'[{"dep_lag_1": 3, "dep_mean_7": 4, "iso_dow": 2, "station_key": "a"}]')
    assert list(df.scale) == [1.0]
    with pytest.raises(ValueError):
        inference.input_fn("{}", content_type="text/csv")
    with pytest.raises(ValueError):
        inference.input_fn('{"rows": []}')


def test_predict_fn_names_the_missing_inputs(run_folder):
    model = inference.model_fn(str(run_folder / "model"))
    with pytest.raises(ValueError, match="dep_mean_7"):
        inference.predict_fn(pd.DataFrame([{"dep_lag_1": 3, "iso_dow": 2, "station_key": "a", "scale": 1.0}]), model)


def test_sagemaker_requests_validate_against_the_api_model():
    model = serving.model_request("citibike-day-r1", ROLE, REGION, "s3://b/citibike/jobs/j/output/model.tar.gz", "s3://b/citibike/source/serving.tar.gz")
    validate_parameters(model, shape("sagemaker", "CreateModel"))
    assert model["PrimaryContainer"]["Environment"]["SAGEMAKER_PROGRAM"] == "inference.py"
    serverless = serving.endpoint_config_request("citibike-day-r1", "citibike-day-r1", serverless_memory_mb=2048, max_concurrency=2)
    validate_parameters(serverless, shape("sagemaker", "CreateEndpointConfig"))
    assert serverless["ProductionVariants"][0]["ServerlessConfig"] == {"MemorySizeInMB": 2048, "MaxConcurrency": 2}
    instance = serving.endpoint_config_request("citibike-day-r1", "citibike-day-r1", serverless_memory_mb=None)
    validate_parameters(instance, shape("sagemaker", "CreateEndpointConfig"))
    assert instance["ProductionVariants"][0]["InstanceType"] == "ml.t3.medium" and "ServerlessConfig" not in instance["ProductionVariants"][0]


def test_lambda_request_validates_and_the_zip_holds_the_handler():
    env = serving.lambda_environment("secret", {"day": "citibike-day", "rest_of_month": "citibike-rest"}, {"day": {"family": "lightgbm"}})
    request = {"FunctionName": "citibike-forecast", "Runtime": serving.LAMBDA_RUNTIME, "Role": ROLE, "Handler": serving.LAMBDA_HANDLER,
               "Code": {"ZipFile": serving.lambda_zip()}, "Timeout": 60, "MemorySize": 256, "Environment": env}
    validate_parameters(request, shape("lambda", "CreateFunction"))
    assert env["Variables"]["ENDPOINT_REST_OF_MONTH"] == "citibike-rest" and json.loads(env["Variables"]["MODEL_DAY"]) == {"family": "lightgbm"}
    with zipfile.ZipFile(io.BytesIO(serving.lambda_zip())) as z:
        assert z.namelist() == ["lambda_function.py"] and b"def handler" in z.read("lambda_function.py")


def test_http_api_request_validates():
    validate_parameters({"Name": "citibike-forecast", "ProtocolType": "HTTP", "Target": "arn:aws:lambda:us-east-1:123456789012:function:f"},
                        shape("apigatewayv2", "CreateApi"))


class FakeRuntimeBody:
    def __init__(self, payload):
        self.payload = json.dumps(payload).encode("utf-8")

    def read(self):
        return self.payload


class FakeRuntime:
    def __init__(self):
        self.calls = []

    def invoke_endpoint(self, **kwargs):
        self.calls.append(kwargs)
        return {"Body": FakeRuntimeBody({"predictions": [12.3, 4.5]})}


def lambda_module(fake_runtime, api_key="secret", endpoints=None):
    """The Lambda code executed as a module with a fake SageMaker runtime and the environment of the notebook."""
    import os
    import types
    env = serving.lambda_environment(api_key, endpoints or {"day": "citibike-day"}, {"day": {"family": "lightgbm", "validation_mae": 9.5}})["Variables"]
    os.environ.update(env)
    module = types.ModuleType("lambda_function")
    import boto3
    original = boto3.client
    boto3.client = lambda *a, **k: fake_runtime
    try:
        exec(serving.LAMBDA_CODE, module.__dict__)
    finally:
        boto3.client = original
    return module


def test_lambda_handler_checks_the_key_routes_on_the_horizon_and_adds_the_model():
    runtime = FakeRuntime()
    module = lambda_module(runtime)
    denied = module.handler({"headers": {"x-api-key": "wrong"}, "body": "{}"}, None)
    assert denied["statusCode"] == 401
    ok = module.handler({"headers": {"X-Api-Key": "secret"}, "body": json.dumps({"horizon": "day", "rows": [{"a": 1}, {"a": 2}]})}, None)
    assert ok["statusCode"] == 200
    body = json.loads(ok["body"])
    assert body["predictions"] == [12.3, 4.5] and body["model"] == {"family": "lightgbm", "validation_mae": 9.5}
    assert runtime.calls[0]["EndpointName"] == "citibike-day" and json.loads(runtime.calls[0]["Body"]) == {"rows": [{"a": 1}, {"a": 2}]}
    unknown = module.handler({"headers": {"x-api-key": "secret"}, "body": json.dumps({"horizon": "week", "rows": []})}, None)
    assert unknown["statusCode"] == 400
    no_rows = module.handler({"headers": {"x-api-key": "secret"}, "body": json.dumps({"horizon": "day"})}, None)
    assert no_rows["statusCode"] == 400


def test_write_env_replaces_and_appends_without_touching_other_lines(tmp_path):
    env = tmp_path / ".env"
    env.write_text("CITIBIKE_DB_USER=u\nFORECAST_API_URL=https://old\n", encoding="utf-8")
    serving.write_env(env, {"FORECAST_API_URL": "https://new", "FORECAST_API_KEY": "k"})
    assert env.read_text(encoding="utf-8") == "CITIBIKE_DB_USER=u\nFORECAST_API_URL=https://new\n\nFORECAST_API_KEY=k\n"
    serving.write_env(tmp_path / "fresh.env", {"A": "1"})
    assert (tmp_path / "fresh.env").read_text(encoding="utf-8") == "A=1\n"



class RecordingApiGw:
    """Records the requests of ensure_routes(); starts empty, so everything must be created."""
    def __init__(self):
        self.calls = []

    def get_integrations(self, ApiId):
        return {"Items": []}

    def create_integration(self, **r):
        self.calls.append(("create_integration", r))
        return {"IntegrationId": "int1"}

    def get_routes(self, ApiId):
        return {"Items": []}

    def create_route(self, **r):
        self.calls.append(("create_route", r))
        return {"RouteId": "r1"}

    def get_stages(self, ApiId):
        return {"Items": []}

    def create_stage(self, **r):
        self.calls.append(("create_stage", r))
        return {}


def test_ensure_routes_creates_integration_route_and_stage_with_valid_requests():
    gw = RecordingApiGw()
    assert serving.ensure_routes(gw, "api1", "arn:aws:lambda:us-east-1:123456789012:function:f") is True
    names = [c[0] for c in gw.calls]
    assert names == ["create_integration", "create_route", "create_stage"]
    for operation, request in gw.calls:
        validate_parameters(request, shape("apigatewayv2", {"create_integration": "CreateIntegration", "create_route": "CreateRoute", "create_stage": "CreateStage"}[operation]))
    assert gw.calls[1][1]["Target"] == "integrations/int1" and gw.calls[1][1]["RouteKey"] == "$default"
    assert gw.calls[2][1]["AutoDeploy"] is True


class CompleteApiGw:
    def get_integrations(self, ApiId):
        return {"Items": [{"IntegrationId": "int1"}]}

    def get_routes(self, ApiId):
        return {"Items": [{"RouteKey": "$default"}]}

    def get_stages(self, ApiId):
        return {"Items": [{"StageName": "$default", "AutoDeploy": True}]}


def test_ensure_routes_leaves_a_complete_api_alone():
    assert serving.ensure_routes(CompleteApiGw(), "api1", "arn") is False
