# Build context is the repository root:  docker compose build  (see docker-compose.yml)
# Stage 1: React SPA ------------------------------------------------------------------------
FROM node:20-alpine AS web
WORKDIR /web
COPY citibike/frontend/package.json citibike/frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY citibike/frontend/ ./
RUN npm run build

# Stage 2: ASP.NET Core API -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
WORKDIR /src
COPY citibike/backend/src/CitibikeIngest.Api/CitibikeIngest.Api.csproj src/CitibikeIngest.Api/
RUN dotnet restore src/CitibikeIngest.Api/CitibikeIngest.Api.csproj
COPY citibike/backend/src/ src/
RUN dotnet publish src/CitibikeIngest.Api/CitibikeIngest.Api.csproj -c Release -o /app/publish --no-restore

# Stage 3: runtime = ASP.NET + Python converter and holiday exporter ------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-pip \
 && rm -rf /var/lib/apt/lists/*
COPY citibike/ingest/requirements.txt /app/ingest/requirements.txt
RUN pip3 install --no-cache-dir --break-system-packages -r /app/ingest/requirements.txt

WORKDIR /app
COPY --from=api /app/publish ./
COPY --from=web /web/dist ./wwwroot/
COPY citibike/citibike_data.py ./citibike_data.py
COPY citibike/ingest/convert_zip.py ./ingest/convert_zip.py
COPY citibike/ingest/export_holidays.py ./ingest/export_holidays.py

ENV ASPNETCORE_URLS=http://+:8080 \
    Ingest__DataDir=/data/citibike \
    Ingest__PythonExecutable=python3 \
    Ingest__ConverterScript=/app/ingest/convert_zip.py \
    Calendar__ExporterScript=/app/ingest/export_holidays.py
EXPOSE 8080
ENTRYPOINT ["dotnet", "CitibikeIngest.Api.dll"]
