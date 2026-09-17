using FluentValidation;
using Microsoft.FeatureManagement;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Extensions;
using NovaWallet.Api.Features.Auth;
using NovaWallet.Api.Features.Deposits;
using NovaWallet.Api.Features.Wallets;
using NovaWallet.Api.Http;
using NovaWallet.Api.Infrastructure.Extensions;
using NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry;
using NovaWallet.Api.Middlewares;
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

    // Register customized partner rate limiting policy extension
    builder.Services.RegisterCardIssuanceLimiting();

    var app = builder.Build();

    // =========================================================================
    // 2. Application Startup Inspections & Migrations
    // =========================================================================

    // Fail fast if any validator's constructor can't be resolved
    using (var scope = app.Services.CreateScope())
    {
        scope.ServiceProvider.GetRequiredService<IEnumerable<IValidator>>();
    }

    if (app.Environment.IsDevelopment())
    {
        await app.ApplyDatabaseMigrationsAsync();
    }

    // =========================================================================
    // 3. HTTP Request Pipeline Order
    // =========================================================================

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

    // D. Partner Authentication / Context Population (Populates IRequestContext.Partner)

    app.UseAuthentication();
    app.UseAuthorization();

    // E. Rate Limiting (Evaluates PerPartnerPolicy using populated Partner context)
    app.UseRateLimiter();

    // =========================================================================
    // 5. Endpoint Routing & Policy Binding
    // =========================================================================

    app.MapGroup("/api/v1/auth")
       .MapAuthenticationEndpoints();

    app.MapGroup("/api/v1/wallets")
        .MapWalletEndpoints()
        .MapDepositEndpoints();

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