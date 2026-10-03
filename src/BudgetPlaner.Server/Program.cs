using System.IO.Compression;
using System.Threading.RateLimiting;
using BudgetPlaner.Application;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Infrastructure;
using BudgetPlaner.Infrastructure.Persistence;
using BudgetPlaner.Server.Auth;
using BudgetPlaner.Server.Endpoints;
using BudgetPlaner.Server.Hosting;
using BudgetPlaner.Server.Realtime;
using BudgetPlaner.Shared;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var dataDirectory = Path.GetFullPath(config["App:DataDirectory"] ?? "App_Data", builder.Environment.ContentRootPath);

builder.Services.Configure<AppOptions>(config.GetSection("App"));
// Dev-Postfach nur in Entwicklung/Test: in Produktion würden sonst Anmeldecodes über /dev/postfach abrufbar.
var devMailboxAllowed = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
builder.Services.PostConfigure<AppOptions>(o =>
{
    o.DataDirectory = dataDirectory;
    if (!devMailboxAllowed) o.DevMailbox = false;
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPublicUrlProvider, PublicUrlProvider>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(config, dataDirectory);
builder.Services.AddBudgetAuth();

builder.Services.AddSignalR().AddJsonProtocol(o => JsonDefaults.Apply(o.PayloadSerializerOptions));
builder.Services.AddSingleton<HubGroups>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();
builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1", o => o.AddDocumentTransformer<ApiDocumentTransformer>());

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var authLimit = config.GetValue("RateLimits:AuthPerMinute", 20);
    var apiLimit = config.GetValue("RateLimits:ApiPerMinute", 1200);
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authLimit, Window = TimeSpan.FromMinutes(1) }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetTokenBucketLimiter(http.User.UserId()?.ToString() ?? http.Connection.RemoteIpAddress?.ToString() ?? "local",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = apiLimit, TokensPerPeriod = apiLimit, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0,
            }));
});

builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json", "application/problem+json"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

if (config.GetValue("App:BackgroundJobs", true))
{
    builder.Services.AddHostedService<EmailWorker>();
    builder.Services.AddHostedService<NotificationWorker>();
    builder.Services.AddHostedService<MaintenanceWorker>();
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

if (!devMailboxAllowed && config.GetValue("App:DevMailbox", false))
    app.Logger.LogWarning("App:DevMailbox ist in der Umgebung {Environment} nicht erlaubt und wurde deaktiviert.", app.Environment.EnvironmentName);

// Hinter einem TLS-Proxy (nginx, Caddy, Tunnel) Schema/IP aus X-Forwarded-* übernehmen – nur von lokalen Proxys (Standard: Loopback).
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });

app.UseAppErrors();
app.UseSecurityHeaders();
if (config.GetValue("App:RequireHttps", false))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseResponseCompression();
// Client-Dateien (inkl. _framework, vorkomprimiert und mit Fingerprint) kommen über die Static-Assets-Endpunkte.
app.MapStaticAssets();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi("/openapi/{documentName}.json");
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi/v1.json", "BudgetPlaner API v1");
    o.RoutePrefix = "api/docs";
    o.DocumentTitle = "BudgetPlaner API";
});

app.MapAccountEndpoints();
app.MapDataEndpoints();
app.MapAnalysisEndpoints();
app.MapMiscEndpoints(app.Environment);
app.MapAdminEndpoints(app.Environment);
app.MapHub<SyncHub>("/hubs/sync");
app.Map("/api/{**rest}", () => Results.Problem(statusCode: 404, title: "Not found")).ExcludeFromDescription();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
