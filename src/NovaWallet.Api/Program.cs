using FluentValidation;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;
using NovaWallet.Api.Features.Admin;
using NovaWallet.Api.Features.Auth;
using NovaWallet.Api.Features.Deposits;
using NovaWallet.Api.Features.Transfers;
using NovaWallet.Api.Features.Wallets;
using NovaWallet.Api.Http;
using NovaWallet.Api.Middlewares;
using NovaWallet.Application;
using NovaWallet.Application.Abstractions;
using NovaWallet.Infrastructure.Extensions;
using NovaWallet.Infrastructure.Observability;
using NovaWallet.Infrastructure.Options;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Ensure Serilog/Observability captures startup failures
builder.AddObservability();

try
{
    // =========================================================================
    // 1. Dependency Injection / Service Registration
    // =========================================================================

    builder.Services
        .RegisterApplicationDatabase(builder.Configuration)
        .RegisterApplicationOptions(builder.Configuration)
        .RegisterApplicationServices()
        .RegisterInfrastructureServices()
        .RegisterPayloadValidation()
        .AddHttpContextAccessor()
        .AddAppHybridCache(builder.Configuration)
        .AddScoped<IRequestContext, HttpRequestContext>()
        .RegisterSingletonRestsharp()
        .AddCustomCors(builder.Configuration.GetAllowedOrigins())
        .AddFeatureManagement();

    builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
    {
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    // 2. For MVC Controllers
    builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    builder.Services.AddSwaggerAndApiVersioning();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddJwtAuthenticationAndAuthorization(builder.Configuration);

    // Register named rate limiting policies (LoginPolicy, UserPolicy, TransferPolicy,
    // CreditPolicy, InternalAdminPolicy); limits come from the "RateLimiting" config section.
    builder.Services.RegisterRateLimitingPolicies(builder.Configuration);

    // Forwarded-headers handling is OFF by default. Enable it (ForwardedHeaders:Enabled=true)
    // ONLY when the API sits behind a trusted reverse proxy / load balancer: otherwise every
    // client appears to share the proxy's IP (one IP-keyed bucket for all). When enabled the
    // known proxy/network allow-lists are cleared, so a client that can reach the API directly
    // could spoof X-Forwarded-For - never enable it on a directly exposed API.
    var forwardedHeadersEnabled = builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");
    if (forwardedHeadersEnabled)
    {
        builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }

    var app = builder.Build();

    // =========================================================================
    // 2. Application Startup Inspections & Migrations
    // =========================================================================

    // One-shot migration mode: `dotnet NovaWallet.Api.dll --migrate-only` applies
    // pending EF Core migrations and exits immediately - no Kestrel host, no
    // validator/options checks below. This is what docker-compose.yml's dedicated
    // `migrator` service runs, so schema changes happen as an isolated, observable
    // step that completes (and the container exits 0) before `api` ever starts,
    // rather than being folded into the API process's own boot sequence.
    if (args.Contains("--migrate-only", StringComparer.OrdinalIgnoreCase))
    {
        await app.ApplyDatabaseMigrationsAsync();
        return;
    }

    // Fail fast if any validator's constructor can't be resolved
    using (var scope = app.Services.CreateScope())
    {
        scope.ServiceProvider.GetRequiredService<IEnumerable<IValidator>>();
    }

    // Whether `api` itself also applies migrations on boot is now an explicit,
    // environment-agnostic config flag (StartupOptions.ApplyMigrationsOnStartup,
    // default false) rather than being tied to ASPNETCORE_ENVIRONMENT=Development.
    // In the Docker Compose stack this stays off - the `migrator` service above
    // already guarantees the schema is current before `api` is started - but the
    // flag remains available for non-Compose scenarios (e.g. `dotnet run` against
    // a fresh local database) where running the one-shot mode separately isn't
    // convenient.
    var startupOptions = app.Services.GetRequiredService<IOptions<StartupOptions>>().Value;

    if (startupOptions.ApplyMigrationsOnStartup)
    {
        await app.ApplyDatabaseMigrationsAsync();
    }

    // =========================================================================
    // 3. HTTP Request Pipeline Order
    // =========================================================================

    // Must run first so every later component (rate limiting, logging) sees the real client IP.
    if (forwardedHeadersEnabled)
    {
        app.UseForwardedHeaders();
    }

    // A. Outermost Exception & Observability Middlewares (Catches everything below)
    app.UseMiddleware<GlobalExceptionMiddleware>();
    app.UseObservabilityMiddleware();

    // B. Security Headers, CORS & Redirection
    app.AddCustomSecurityHeaders();
    app.UseCustomCors();
    app.UseHttpsRedirection();

    // C. Routing Context (Must precede RateLimiting so route metadata is available)
    app.UseRouting();

    // =========================================================================
    // 4. API Documentation & Scalar UI
    // =========================================================================

    if (bool.TryParse(builder.Configuration["Swagger:DisplaySwagger"], out var displaySwagger) && displaySwagger)
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // D. Authentication / Context Population (Populates IRequestContext.UserId)

    app.UseAuthentication();
    app.UseAuthorization();

    // E. Rate Limiting (per-user policies key on the authenticated user; login/credit key on client IP)
    app.UseRateLimiter();

    // =========================================================================
    // 5. Endpoint Routing & Policy Binding
    // =========================================================================

    app.MapGroup("/api/v1/auth")
       .MapAuthenticationEndpoints();

    app.MapGroup("/api/v1/wallets")
        .MapWalletEndpoints()
        .MapDepositEndpoints()
        .MapTransferEndpoints();

    app.MapGroup("/api/v1/admin")
       .MapAdminEndpoints();

    app.Run();
}
catch (Exception ex)
{
    // Log fatal startup error (or let Serilog capture it if registered in builder.AddObservability())
    Console.WriteLine($"Application startup failed: {ex.Message}");
    throw;
}
finally
{
    // Flush trace exporters / logging buffers on shutdown
}

// Exposes the entry point to WebApplicationFactory<Program> in tests/NovaWallet.Api.IntegrationTests.
public partial class Program
{ }