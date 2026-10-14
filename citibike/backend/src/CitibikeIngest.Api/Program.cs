using System.Globalization;
using CitibikeIngest.Api.Data;
using CitibikeIngest.Api.Infrastructure;
using CitibikeIngest.Api.Managers;
using CitibikeIngest.Api.Managers.Interfaces;
using CitibikeIngest.Api.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Configuration: Ingest__*, Weather__*, Calendar__*, Forecast__* and ConnectionStrings__Postgres come from the environment (compose .env), never from git.
builder.Services.AddOptions<IngestOptions>()
    .Bind(builder.Configuration.GetSection(IngestOptions.Section))
    .Validate(o => o.RegionSet.Count > 0, "Ingest:Regions must name at least one region (nyc, jc)")
    .Validate(o => DateTimeOffset.TryParse(o.DefaultSince, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _),
        "Ingest:DefaultSince must be an ISO date such as 2021-01-01")
    .ValidateOnStart();

builder.Services.AddOptions<WeatherOptions>()
    .Bind(builder.Configuration.GetSection(WeatherOptions.Section))
    .Validate(o => DateOnly.TryParseExact(o.DefaultFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "Weather:DefaultFrom must be an ISO date such as 2021-01-01")
    .Validate(o => o.RefetchDays >= 0 && o.MaxDaysPerRequest > 0, "Weather:RefetchDays must be >= 0 and Weather:MaxDaysPerRequest > 0")
    .ValidateOnStart();

builder.Services.AddOptions<CalendarOptions>()
    .Bind(builder.Configuration.GetSection(CalendarOptions.Section))
    .Validate(o => o.CategoryList.Count > 0, "Calendar:Categories must name at least one category (public, unofficial, ...)")
    .Validate(o => o.YearsAhead >= 0 && o.MaxYearsPerRequest > 0, "Calendar:YearsAhead must be >= 0 and Calendar:MaxYearsPerRequest > 0")
    .ValidateOnStart();

builder.Services.AddOptions<ForecastOptions>()
    .Bind(builder.Configuration.GetSection(ForecastOptions.Section))
    .Validate(o => o.TimeoutSeconds is > 0 and <= 300 && o.MinHistoryDays >= 0, "Forecast:TimeoutSeconds must be 1 - 300 and Forecast:MinHistoryDays >= 0")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings__Postgres is not configured");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString)); // pooled; one connection per operation

// Data
builder.Services.AddHttpClient<IS3BucketClient, S3BucketClient>(client =>
{
    client.Timeout = TimeSpan.FromHours(2); // a monthly zip is ~1 GB
    client.DefaultRequestHeaders.UserAgent.ParseAdd("cloud-ai-avenue-5-citibike-ingest/1.0");
});
builder.Services.AddHttpClient<INoaaClient, NoaaClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60); // a full 2021-now load is ~2,000 rows and takes a few seconds
    client.DefaultRequestHeaders.UserAgent.ParseAdd("cloud-ai-avenue-5-citibike-ingest/1.0");
});
builder.Services.AddScoped<IIngestRepository, IngestRepository>();
builder.Services.AddScoped<IWeatherRepository, WeatherRepository>();
builder.Services.AddScoped<ICalendarRepository, CalendarRepository>();
builder.Services.AddScoped<IForecastRepository, ForecastRepository>();
builder.Services.AddHttpClient<IForecastClient, ForecastClient>((services, client) =>
{
    // A serverless endpoint that slept answers the first call in about 15 s; the gateway's own limit is about 30 s.
    client.Timeout = TimeSpan.FromSeconds(services.GetRequiredService<IOptions<ForecastOptions>>().Value.TimeoutSeconds);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("cloud-ai-avenue-5-citibike-ingest/1.0");
});
var mushroomUrl = builder.Configuration["Mushroom:Url"] ?? "http://mushroom-model:8000";
builder.Services.AddHttpClient<IMushroomClient, MushroomClient>(client =>
{
    client.BaseAddress = new Uri(mushroomUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Managers
builder.Services.AddScoped<IS3FilesManager, S3FilesManager>();
builder.Services.AddScoped<IIngestManager, IngestManager>();
builder.Services.AddScoped<IWeatherManager, WeatherManager>();
builder.Services.AddScoped<ICalendarManager, CalendarManager>();
builder.Services.AddScoped<IForecastManager, ForecastManager>();
builder.Services.AddScoped<IMushroomManager, MushroomManager>();

// Infrastructure: job queue + worker, converter and holiday-export subprocesses, schema/warm-up at startup
builder.Services.AddSingleton<IZipConverter, PythonZipConverter>();
builder.Services.AddSingleton<IHolidayExporter, PythonHolidayExporter>();
builder.Services.AddSingleton<IIngestQueue, IngestQueue>();
builder.Services.AddHostedService<StartupInitializer>();
builder.Services.AddHostedService<IngestWorker>();

builder.Services.AddControllers();
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    // Model-binding errors follow the house contract too: status code + plain text, not ProblemDetails JSON.
    options.InvalidModelStateResponseFactory = context => new ContentResult
    {
        StatusCode = StatusCodes.Status400BadRequest,
        ContentType = "text/plain; charset=utf-8",
        Content = string.Join("; ", context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .Select(e => $"{e.Key}: {string.Join(", ", e.Value!.Errors.Select(err => err.ErrorMessage))}")),
    };
});

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

var app = builder.Build();

if (string.IsNullOrEmpty(app.Configuration[$"{ForecastOptions.Section}:Url"]))
{
    app.Logger.LogWarning("Forecast:Url is not set; GET /api/forecast answers 503 until FORECAST_API_URL and FORECAST_API_KEY are configured (notebook 11)");
}

if (string.IsNullOrEmpty(app.Configuration[$"{IngestOptions.Section}:AdminToken"]))
{
    app.Logger.LogWarning("Ingest:AdminToken is not set; POST /api/ingest/runs, /api/weather/runs and /api/calendar/runs are unprotected (acceptable for local dev only)");
}

app.UseMiddleware<ExceptionMiddleware>();
app.UseMiddleware<AdminTokenMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// The React build lands in wwwroot/ (see deploy/Dockerfile); deep links fall back to index.html, /api never does.
// UseRouting() must be called explicitly AFTER the static files: without it WebApplication inserts routing at the very
// start of the pipeline, the fallback endpoint then matches /assets/*.js before UseStaticFiles() runs, and the static
// file middleware skips any request that already has an endpoint, so every asset came back as index.html (a white
// page in the browser, found 2026-10-11 on the public URL).
app.UseDefaultFiles();
// The static file middleware serves known extensions only; the robot's glTF binary (.glb) is not one of them, so
// without this mapping the SPA fallback answered /models/robot.glb with index.html (found 2026-10-12 on the host).
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".glb"] = "model/gltf-binary";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });
app.UseRouting();
app.MapGet("/healthz", () => Results.Text("ok"));
app.MapControllers();
app.MapFallbackToFile("{*path:regex(^(?!api/).*$)}", "index.html");

app.Run();
