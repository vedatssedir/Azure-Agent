using System.Text;
using System.Threading.RateLimiting;
using AzureAgent.Api.Infrastructure;
using AzureAgent.Application.Features.Auth;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Infrastructure;
using AzureAgent.Infrastructure.Persistence;
using AzureAgent.Infrastructure.Security;
using Carter;
using JasperFx;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using TickerQ.DependencyInjection;
using Wolverine;
using Wolverine.FluentValidation;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    var config = builder.Configuration;

    // Logging: structured Serilog with request/tenant/run context, console + optional Seq.
    builder.Host.UseSerilog((context, services, logger) => logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentName()
        .Enrich.WithProperty("Application", "AzureAgent")
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}")
        .WriteTo.Conditional(_ => !string.IsNullOrEmpty(context.Configuration["Seq:Url"]),
            sink => sink.Seq(context.Configuration["Seq:Url"]!, apiKey: context.Configuration["Seq:ApiKey"])));

    builder.Services.AddInfrastructure(config);
    builder.Services.AddCarter();
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
    builder.Services.AddHealthChecks();
    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

    var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };
    });
    builder.Services.AddAuthorization();

    var authPerMinute = config.GetValue("RateLimits:AuthPerMinute", 20);
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(http.ClientIp() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
        o.AddPolicy("refresh", http => RateLimitPartition.GetFixedWindowLimiter(http.ClientIp() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
        o.AddPolicy("hooks", http => RateLimitPartition.GetFixedWindowLimiter(http.Request.Path.Value ?? "",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
    });

    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
        .AllowAnyHeader().AllowAnyMethod()));

    // Observability: OpenTelemetry traces/metrics, exported only when an OTLP endpoint is configured.
    if (!string.IsNullOrEmpty(config["OpenTelemetry:Endpoint"]))
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("azure-agent"))
            .WithTracing(t => t.AddAspNetCoreInstrumentation().AddSource("Wolverine").AddOtlpExporter(o => o.Endpoint = new Uri(config["OpenTelemetry:Endpoint"]!)))
            .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMeter("Wolverine").AddOtlpExporter(o => o.Endpoint = new Uri(config["OpenTelemetry:Endpoint"]!)));
    }

    // Messaging: Wolverine is the mediator (InvokeAsync from endpoints) and the message bus.
    builder.Host.UseWolverine(opts =>
    {
        opts.ApplicationAssembly = typeof(Program).Assembly;
        opts.Discovery.IncludeAssembly(typeof(SetupHandler).Assembly);
        opts.Discovery.IncludeAssembly(typeof(DependencyInjection).Assembly);
        opts.UseFluentValidation();
        // EF Core registers DbContextOptions through a factory; let generated handlers resolve it from the container.
        opts.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;

        var agentWorkers = config.GetValue("Messaging:AgentWorkers", 4);
        if (config.GetConnectionString("RabbitMq") is { Length: > 0 } rabbit)
        {
            opts.UseRabbitMq(new Uri(rabbit)).AutoProvision();
            opts.PublishMessage<ExecuteAgentRun>().ToRabbitQueue("agent-runs");
            opts.PublishMessage<SyncProject>().ToRabbitQueue("azure-sync");
            opts.PublishMessage<SyncWorkItem>().ToRabbitQueue("azure-sync");
            // Nodes with Messaging:AgentWorkers = 0 only serve the API.
            if (agentWorkers > 0) opts.ListenToRabbitQueue("agent-runs").MaximumParallelMessages(agentWorkers);
            opts.ListenToRabbitQueue("azure-sync").MaximumParallelMessages(2);
        }
        else
        {
            opts.LocalQueueFor<ExecuteAgentRun>().MaximumParallelMessages(Math.Max(agentWorkers, 1));
        }
        if (config.GetValue("Messaging:Durable", false))
        {
            opts.PersistMessagesWithPostgresql(config.GetConnectionString("Postgres")!, "wolverine");
            opts.Policies.UseDurableLocalQueues();
        }
    });

    // Background schedules: TickerQ cron functions (see Infrastructure/Jobs).
    builder.Services.AddTickerQ(o => o.ConfigureScheduler(s => s.MaxConcurrency = 4));

    var app = builder.Build();

    app.UseSerilogRequestLogging(o => o.EnrichDiagnosticContext = (diag, http) =>
    {
        diag.Set("UserId", http.User.FindFirst("sub")?.Value);
        diag.Set("ClientIp", http.ClientIp());
    });
    app.UseExceptionHandler();
    app.UseCors();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<TenantResolutionMiddleware>();
    app.MapCarter();
    app.MapHealthChecks("/health");
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");

    if (!config.GetValue("Database:SkipMigrations", false))
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
    }
    if (config.GetValue("Jobs:Enabled", true)) app.UseTickerQ();

    return await app.RunJasperFxCommands(args);
}
catch (Exception e) when (e is not HostAbortedException)
{
    Log.Fatal(e, "AzureAgent terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program;
