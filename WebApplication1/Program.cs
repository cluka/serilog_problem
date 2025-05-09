using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.SemanticKernel;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.AspNetCore;
using Serilog.Events;
using WebApplication1;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
    {
        builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        builder.SetIsOriginAllowed(_ => true);
    });
});

builder.Services.Configure<JsonOptions>(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
});
// Removes the built-in logging providers
builder.Logging.ClearProviders();

// Including the writeToProviders=true parameter allows the OpenTelemetry logger to still be written to
builder.Services.AddSerilog((_, loggerConfiguration) =>
{
    // Configure Serilog as desired here for every project (or use IConfiguration for configuration variations between projects)
    loggerConfiguration
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext();
    //.WriteTo.Console();
}, writeToProviders: true);

builder.Services.ConfigureHttpClientDefaults(http =>
{
    // Turn on resilience by default
    http.AddStandardResilienceHandler();

    // Turn on service discovery by default
    http.AddServiceDiscovery();
});

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation();
    })
    .WithTracing(tracing =>
    {
        tracing.AddSource(builder.Environment.ApplicationName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation(opt =>
            {
                opt.EnrichWithIDbCommand = (activity, command) =>
                {
                    var stateDisplayName = $"{command.CommandType} main";
                    activity.DisplayName = stateDisplayName;
                    activity.SetTag("db.name", stateDisplayName);
                };
            })
            .AddSqlClientInstrumentation(opt => opt.SetDbStatementForText = true)
            .AddMassTransitInstrumentation();
    });

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

builder.Services
    .AddProblemDetails(options =>
    {
        options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Instance =
                $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

            context.ProblemDetails.Extensions.TryAdd("requestId", context.HttpContext.TraceIdentifier);

            var activity = context.HttpContext.Features.Get<IHttpActivityFeature>()?.Activity;
            context.ProblemDetails.Extensions.TryAdd("traceId", activity?.Id);
        };
    })
    .AddExceptionHandler<GlobalExceptionHandler>();

AddOpenTelemetryExporters(builder);

static TBuilder AddOpenTelemetryExporters<TBuilder>(TBuilder builder) where TBuilder : IHostApplicationBuilder
{
    var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

    if (useOtlpExporter) builder.Services.AddOpenTelemetry().UseOtlpExporter();

    return builder;
}

builder.Services.AddOpenApi();

builder.Services.AddSingleton<Kernel>(sp =>
{
    var kernelBuilder = Kernel.CreateBuilder();
#pragma warning disable SKEXP0070
    kernelBuilder.AddOllamaChatCompletion("tinyllama:latest", new Uri("http://localhost:11434"));
#pragma warning restore SKEXP0070

    return kernelBuilder.Build();
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseCors("AllowAll");

app.UseSerilogPlusRequestLogging(options =>
{
    options.GetLevel = new Func<HttpContext, double, Exception?, LogEventLevel>((httpContext, elapsed, ex) =>
    {
        if (ex != null || httpContext.Response.StatusCode >= 500)
            return LogEventLevel.Error;
        else if (httpContext.Response.StatusCode >= 400)
            return LogEventLevel.Warning;
        else
            return LogEventLevel.Debug;
    });

    options.LogMode = LogMode.LogAll;
    options.RequestBodyLogMode = LogMode.LogFailures;
    options.RequestHeaderLogMode = LogMode.LogFailures;
    options.IncludeQueryInRequestPath = true;
    options.ResponseBodyLogMode = LogMode.LogFailures;
});

app.UseExceptionHandler();
app.UseStatusCodePages();

var summaries = new List<string>
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

var testAsyncEnumerable = summaries.ToAsyncEnumerable();

app.MapPost("test", async (HttpContext http) =>
{
    http.Response.ContentType = "text/plain"; // or "application/x-ndjson"
    http.Response.Headers.Append("X-Accel-Buffering", "no"); // sometimes for reverse proxies
    http.Response.Headers.Append("Pragma", "no-cache");
    http.Response.Headers.Append("Cache-Control", "no-cache"); // avoid caching
    http.Request.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    await foreach (var asn in GetData())
    {
        await http.Response.WriteAsync(asn + "\n");
        await http.Response.Body.FlushAsync();
        await Task.Delay(1000); // simulate work
    }
});


async IAsyncEnumerable<string> GetData()
{
    await foreach (var asn in testAsyncEnumerable) yield return asn;
}


app.Run();

internal record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}