using Hangfire;
using Hangfire.SqlServer;
using LLMQuorum.Core.Configuration;
using LLMQuorum.Core.Engine;
using LLMQuorum.Core.Models;
using LLMQuorum.Core.Providers;
using LLMQuorum.Core.Storage;
using LLMQuorum.Web.Endpoints;
using LLMQuorum.Web.Jobs;

// -----------------------------------------------------------------------------
// LLMQuorum portal.
//
// Hangfire owns execution because a sweep is long-running, must survive an
// app-pool recycle, and needs a visible queue. Its dashboard at /jobs gives job
// monitoring without building any of it.
//
// Worker count is pinned to ONE. Sweeps are rate-limited by the tightest free
// tier on the ladder, not by CPU, and a second concurrent sweep would double the
// request rate against providers whose ceiling is measured in calls per day.
// -----------------------------------------------------------------------------

const int HANGFIRE_WORKER_COUNT = 1;

var builder = WebApplication.CreateBuilder( args );

var quorumConfig = QuorumConfig.Load(
    Path.Combine( builder.Environment.ContentRootPath, "llmquorum.json" ) );

// Property names are emitted verbatim. The read endpoints project SQL rows into
// dictionaries whose keys are the actual column names, and System.Text.Json does
// not apply a naming policy to dictionary keys. Leaving the default web policy in
// place would camelCase the typed responses while the row-shaped ones stayed
// PascalCase, so the portal would see two different casings from one API.
builder.Services.ConfigureHttpJsonOptions( options =>
    options.SerializerOptions.PropertyNamingPolicy = null );

builder.Services.AddSingleton( quorumConfig );
builder.Services.AddSingleton( new QuorumRepository( quorumConfig.ConnectionString ) );
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ProviderFactory>();
builder.Services.AddSingleton<QuorumRunner>();
builder.Services.AddScoped<SweepJob>();
builder.Services.AddScoped<ModelSweepJob>();
builder.Services.AddScoped<PlanRunnerJob>();
builder.Services.AddHostedService<KeepAwakeService>();

builder.Services.AddHangfire( configuration => configuration
    .SetDataCompatibilityLevel( CompatibilityLevel.Version_180 )
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage( quorumConfig.ConnectionString, new SqlServerStorageOptions
    {
        PrepareSchemaIfNecessary = true,
        QueuePollInterval = TimeSpan.FromSeconds( 5 )
    } ) );

builder.Services.AddHangfireServer( options => options.WorkerCount = HANGFIRE_WORKER_COUNT );

var app = builder.Build();

app.UseStaticFiles();
app.UseHangfireDashboard( "/jobs" );

// Slow and steady: every 15 minutes the plan runner takes at most one question per provider group,
// in order, and waits out daily caps. It does nothing when the plan has no eligible item.
app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<PlanRunnerJob>(
    "plan-public-v2", job => job.RunAsync( "public-v2", CancellationToken.None ), "*/15 * * * *",
    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc } );

// Public set v3 (2026-09-24): 54 questions whose answers all changed within the last year. Offset by
// seven minutes so the two plans take their turns rather than competing for the same daily allowances.
app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<PlanRunnerJob>(
    "plan-public-v3", job => job.RunAsync( "public-v3", CancellationToken.None ), "7,22,37,52 * * * *",
    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc } );

// The paid lane runs as its own plan. MEASURED 2026-09-24: a pass over all eight groups takes about 35
// minutes, of which the paid sweep is 8, so behind the free groups the paid set would take a day and a
// half. On its own schedule it takes hours, and the free groups simply carry on between its turns.
app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<PlanRunnerJob>(
    "plan-v3-paid", job => job.RunAsync( "v3-paid", CancellationToken.None ), "*/5 * * * *",
    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc } );

app.MapQuorumEndpoints();
app.MapFallbackToFile( "index.html" );

app.Run();
