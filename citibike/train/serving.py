"""The model as an API on AWS, through the plain API (boto3): a SageMaker endpoint per horizon (serverless where the
lab allows it), one Lambda in front that routes on the horizon and checks a shared key, and an HTTP API of API
Gateway that gives the Lambda a public URL. What notebook 11 builds, and what citibike/backend calls.

Why the detour over API Gateway and Lambda: invoking a SageMaker endpoint needs AWS credentials on every call, and
the Learner Lab's credentials expire with every session. The Lambda runs under LabRole, which does not expire, and
the HTTP API needs no AWS credentials, only the shared key. So the server on Hetzner holds a URL and a key, never
an AWS key.

    Hetzner backend ──HTTPS, x-api-key──► API Gateway ──► Lambda (LabRole) ──► SageMaker endpoint ──► model.tar.gz
"""
from __future__ import annotations

import io
import json
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

from botocore.exceptions import ClientError

from jobs import bundle_source, image_uri

SERVING_KEY = "citibike/source/serving.tar.gz"
LAMBDA_HANDLER = "lambda_function.handler"
LAMBDA_RUNTIME = "python3.12"

# The function behind the URL. One for both horizons: the request names the horizon, the environment names the
# endpoint per horizon. The key check is the only authentication; the Lambda itself has no AWS key, it has LabRole.
LAMBDA_CODE = '''
import base64
import json
import os

import boto3

runtime = boto3.client("sagemaker-runtime")


def reply(status, payload):
    return {"statusCode": status, "headers": {"content-type": "application/json"}, "body": json.dumps(payload)}


def handler(event, context):
    headers = {k.lower(): v for k, v in (event.get("headers") or {}).items()}
    if headers.get("x-api-key") != os.environ["API_KEY"]:
        return reply(401, {"error": "bad or missing x-api-key"})
    body = event.get("body") or ""
    if event.get("isBase64Encoded"):
        body = base64.b64decode(body).decode("utf-8")
    try:
        payload = json.loads(body or "{}")
        horizon = payload.get("horizon", "day")
        endpoint = os.environ.get("ENDPOINT_" + horizon.upper())
        if not endpoint:
            return reply(400, {"error": f"no endpoint for horizon {horizon}"})
        response = runtime.invoke_endpoint(EndpointName=endpoint, ContentType="application/json", Accept="application/json",
                                           Body=json.dumps({"rows": payload["rows"]}))
        answer = json.loads(response["Body"].read().decode("utf-8"))
        answer["model"] = json.loads(os.environ.get("MODEL_" + horizon.upper(), "{}"))
        return reply(200, answer)
    except KeyError as e:
        return reply(400, {"error": f"missing field {e}"})
    except Exception as e:   # the endpoint's own error text is the useful part
        return reply(502, {"error": str(e)[:500]})
'''


# ---- The endpoint ------------------------------------------------------------------------------------------------------
def upload_serving_bundle(s3, bucket: str, train_dir: Path, key: str = SERVING_KEY) -> str:
    """inference.py and the training requirements (the versions that fitted the model) to S3; returns the URI."""
    s3.put_object(Bucket=bucket, Key=key, Body=bundle_source(train_dir, files=("inference.py", "requirements.txt")))
    return f"s3://{bucket}/{key}"


def model_request(name: str, role_arn: str, region: str, model_data_url: str, serving_uri: str) -> dict:
    """CreateModel: the scikit-learn serving image, the job's model.tar.gz, and the serving bundle as the code it runs."""
    return {"ModelName": name, "ExecutionRoleArn": role_arn,
            "PrimaryContainer": {"Image": image_uri(region), "ModelDataUrl": model_data_url,
                                 "Environment": {"SAGEMAKER_PROGRAM": "inference.py", "SAGEMAKER_SUBMIT_DIRECTORY": serving_uri,
                                                 "SAGEMAKER_CONTAINER_LOG_LEVEL": "20", "SAGEMAKER_REGION": region}}}


def endpoint_config_request(name: str, model_name: str, serverless_memory_mb: int | None = 2048, max_concurrency: int = 2,
                            instance_type: str = "ml.t3.medium") -> dict:
    """CreateEndpointConfig: serverless (pay per call, nothing while idle) or, when serverless_memory_mb is None, one
    small instance that costs per hour."""
    variant = {"VariantName": "all", "ModelName": model_name}
    if serverless_memory_mb:
        variant["ServerlessConfig"] = {"MemorySizeInMB": serverless_memory_mb, "MaxConcurrency": max_concurrency}
    else:
        variant.update({"InstanceType": instance_type, "InitialInstanceCount": 1})
    return {"EndpointConfigName": name, "ProductionVariants": [variant]}


def exists(call, **kwargs) -> bool:
    try:
        call(**kwargs)
        return True
    except ClientError as e:
        if e.response["Error"]["Code"] in ("ValidationException", "ResourceNotFoundException", "ResourceNotFound") \
                or "Could not find" in e.response["Error"].get("Message", ""):
            return False
        raise


def create_model(sm, request: dict) -> str:
    """Creates the model, or leaves one of that name alone."""
    if exists(sm.describe_model, ModelName=request["ModelName"]):
        print(f"model {request['ModelName']} exists already")
    else:
        sm.create_model(**request)
        print(f"created model {request['ModelName']}")
    return request["ModelName"]


def create_endpoint_config(sm, request: dict) -> str:
    if exists(sm.describe_endpoint_config, EndpointConfigName=request["EndpointConfigName"]):
        print(f"endpoint config {request['EndpointConfigName']} exists already")
    else:
        sm.create_endpoint_config(**request)
        print(f"created endpoint config {request['EndpointConfigName']}")
    return request["EndpointConfigName"]


def create_or_update_endpoint(sm, name: str, config_name: str) -> str:
    """Creates the endpoint, or points an existing one at the config (a new config for the same name replaces the model
    without a second endpoint)."""
    if exists(sm.describe_endpoint, EndpointName=name):
        current = sm.describe_endpoint(EndpointName=name)
        if current["EndpointConfigName"] == config_name:
            print(f"endpoint {name} already on {config_name} ({current['EndpointStatus']})")
        else:
            sm.update_endpoint(EndpointName=name, EndpointConfigName=config_name)
            print(f"updating endpoint {name} to {config_name}")
    else:
        sm.create_endpoint(EndpointName=name, EndpointConfigName=config_name)
        print(f"creating endpoint {name} on {config_name}")
    return name


def wait_endpoint(sm, name: str, poll_s: int = 20, max_wait_s: int = 1800) -> dict:
    """Polls until the endpoint is InService or Failed, printing each change of status."""
    last, t0 = None, time.time()
    while True:
        d = sm.describe_endpoint(EndpointName=name)
        status = d["EndpointStatus"]
        if status != last:
            print(f"{time.strftime('%H:%M:%S')} endpoint {name}: {status}")
            last = status
        if status in ("InService", "Failed", "OutOfService"):
            if status != "InService":
                print(f"failure reason: {d.get('FailureReason', '?')}")
            return d
        if time.time() - t0 > max_wait_s:
            raise TimeoutError(f"endpoint {name} still {status} after {max_wait_s} s")
        time.sleep(poll_s)


def invoke(runtime, endpoint_name: str, rows: list[dict]) -> tuple[dict, float]:
    """One request straight to the endpoint (needs AWS credentials: the check from the laptop), with its time."""
    t0 = time.time()
    response = runtime.invoke_endpoint(EndpointName=endpoint_name, ContentType="application/json", Accept="application/json",
                                       Body=json.dumps({"rows": rows}))
    return json.loads(response["Body"].read().decode("utf-8")), time.time() - t0


# ---- The Lambda and the HTTP API ---------------------------------------------------------------------------------------
def lambda_zip(code: str = LAMBDA_CODE) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("lambda_function.py", code)
    return buffer.getvalue()


def lambda_environment(api_key: str, endpoints: dict[str, str], models: dict[str, dict]) -> dict:
    """The function's settings: the key, the endpoint per horizon, the model description per horizon."""
    env = {"API_KEY": api_key}
    for horizon, endpoint in endpoints.items():
        env[f"ENDPOINT_{horizon.upper()}"] = endpoint
        env[f"MODEL_{horizon.upper()}"] = json.dumps(models.get(horizon, {}))
    return {"Variables": env}


def create_or_update_lambda(lam, name: str, role_arn: str, environment: dict, timeout_s: int = 60) -> str:
    """Creates the function with LabRole, or updates its code and settings; waits until it is active. Returns its ARN."""
    if exists(lam.get_function, FunctionName=name):
        lam.update_function_code(FunctionName=name, ZipFile=lambda_zip())
        lam.get_waiter("function_updated_v2").wait(FunctionName=name)
        lam.update_function_configuration(FunctionName=name, Environment=environment, Timeout=timeout_s)
        lam.get_waiter("function_updated_v2").wait(FunctionName=name)
        print(f"updated function {name}")
    else:
        lam.create_function(FunctionName=name, Runtime=LAMBDA_RUNTIME, Role=role_arn, Handler=LAMBDA_HANDLER,
                            Code={"ZipFile": lambda_zip()}, Timeout=timeout_s, MemorySize=256, Environment=environment,
                            Description="citibike forecast: checks the key and invokes the SageMaker endpoint of the horizon")
        lam.get_waiter("function_active_v2").wait(FunctionName=name)
        print(f"created function {name}")
    return lam.get_function(FunctionName=name)["Configuration"]["FunctionArn"]


def ensure_routes(apigw, api_id: str, function_arn: str) -> bool:
    """Quick create is meant to leave the API with a Lambda integration, a catch-all route and an auto-deployed
    $default stage; in the Learner Lab it left an API that answered 404 "Not Found" to everything (10 October). This
    looks at the three and creates what is missing. Returns True when something was created."""
    created = False
    integrations = apigw.get_integrations(ApiId=api_id).get("Items", [])
    if not integrations:
        integrations = [apigw.create_integration(ApiId=api_id, IntegrationType="AWS_PROXY", IntegrationUri=function_arn,
                                                 IntegrationMethod="POST", PayloadFormatVersion="2.0")]
        print("created the Lambda integration")
        created = True
    integration_id = integrations[0]["IntegrationId"]
    routes = apigw.get_routes(ApiId=api_id).get("Items", [])
    if not any(r.get("RouteKey") == "$default" for r in routes):
        apigw.create_route(ApiId=api_id, RouteKey="$default", Target=f"integrations/{integration_id}")
        print("created the $default route")
        created = True
    stages = apigw.get_stages(ApiId=api_id).get("Items", [])
    default_stage = next((s for s in stages if s.get("StageName") == "$default"), None)
    if default_stage is None:
        apigw.create_stage(ApiId=api_id, StageName="$default", AutoDeploy=True)
        print("created the $default stage with auto-deploy")
        created = True
    elif not default_stage.get("AutoDeploy"):
        apigw.create_deployment(ApiId=api_id, StageName="$default")
        print("deployed the $default stage")
        created = True
    print(f"HTTP API {api_id}: {len(integrations)} integration(s), routes {[r.get('RouteKey') for r in routes] or ['$default']}, "
          f"stages {[s.get('StageName') for s in stages] or ['$default']}")
    return created


def create_http_api(apigw, lam, name: str, function_arn: str, region: str, account: str) -> str:
    """An HTTP API whose every route goes to the function, and the permission for API Gateway to call it. Returns the
    URL. An API of that name is reused; its route, integration and stage are checked and completed every time."""
    apis = [a for a in apigw.get_apis(MaxResults="100").get("Items", []) if a["Name"] == name]
    if apis:
        api = apis[0]
        print(f"HTTP API {name} exists already")
    else:
        api = apigw.create_api(Name=name, ProtocolType="HTTP", Target=function_arn,
                               Description="citibike forecast: the public URL in front of the SageMaker endpoints")
        print(f"created HTTP API {name}")
    if ensure_routes(apigw, api["ApiId"], function_arn):
        time.sleep(5)       # a new route or stage takes a few seconds to answer
    try:
        lam.add_permission(FunctionName=function_arn, StatementId=f"apigw-{api['ApiId']}", Action="lambda:InvokeFunction",
                           Principal="apigateway.amazonaws.com", SourceArn=f"arn:aws:execute-api:{region}:{account}:{api['ApiId']}/*")
    except ClientError as e:
        if e.response["Error"]["Code"] != "ResourceConflictException":
            raise
    return api["ApiEndpoint"]


def call_url(url: str, api_key: str, horizon: str, rows: list[dict], timeout_s: int = 60) -> tuple[int, dict, float]:
    """The call the Hetzner backend makes: HTTPS, the key in a header, no AWS credentials. Returns status, body, seconds."""
    data = json.dumps({"horizon": horizon, "rows": rows}).encode("utf-8")
    request = urllib.request.Request(url, data=data, method="POST",
                                     headers={"content-type": "application/json", "x-api-key": api_key})
    t0 = time.time()
    try:
        with urllib.request.urlopen(request, timeout=timeout_s) as response:
            return response.status, json.loads(response.read().decode("utf-8")), time.time() - t0
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", errors="replace")
        try:
            body = json.loads(body)
        except ValueError:
            body = {"error": body[:500]}
        return e.code, body, time.time() - t0


# ---- Tearing down (the budget rule) --------------------------------------------------------------------------------------
def teardown(sm, lam=None, apigw=None, endpoints=(), configs=(), models=(), function: str | None = None, api_name: str | None = None) -> None:
    """Deletes what costs or clutters: endpoints first (a serverless endpoint costs nothing idle, an instance one does),
    then their configs and models, the function and the API. Missing things are skipped."""
    for name in endpoints:
        if exists(sm.describe_endpoint, EndpointName=name):
            sm.delete_endpoint(EndpointName=name)
            print(f"deleted endpoint {name}")
    for name in configs:
        if exists(sm.describe_endpoint_config, EndpointConfigName=name):
            sm.delete_endpoint_config(EndpointConfigName=name)
            print(f"deleted endpoint config {name}")
    for name in models:
        if exists(sm.describe_model, ModelName=name):
            sm.delete_model(ModelName=name)
            print(f"deleted model {name}")
    if lam and function and exists(lam.get_function, FunctionName=function):
        lam.delete_function(FunctionName=function)
        print(f"deleted function {function}")
    if apigw and api_name:
        for api in [a for a in apigw.get_apis(MaxResults="100").get("Items", []) if a["Name"] == api_name]:
            apigw.delete_api(ApiId=api["ApiId"])
            print(f"deleted HTTP API {api_name}")


# ---- What the server gets ----------------------------------------------------------------------------------------------
def write_env(path: Path, values: dict[str, str]) -> list[str]:
    """Sets the given keys in a .env file (replacing lines of the same key, keeping everything else); creates the file
    when missing. Returns the keys written. The values are never printed: the file is the only place they go."""
    path = Path(path)
    lines = path.read_text(encoding="utf-8").split("\n") if path.exists() else []
    done = set()
    for i, line in enumerate(lines):
        key = line.split("=", 1)[0].strip()
        if key in values:
            lines[i] = f"{key}={values[key]}"
            done.add(key)
    for key, value in values.items():
        if key not in done:
            if lines and lines[-1].strip():
                lines.append("")
            lines.append(f"{key}={value}")
    path.write_text("\n".join(lines).rstrip("\n") + "\n", encoding="utf-8")
    return list(values)
