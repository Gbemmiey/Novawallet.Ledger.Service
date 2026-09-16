using FluentValidation;
using Microsoft.FeatureManagement;
using NovaWallet.Api.Application.Services;
using NovaWallet.Api.Endpoints;
using NovaWallet.Api.Extensions;
using NovaWallet.Api.Http;
using NovaWallet.Api.Infrastructure.Extensions;
using NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry;
using NovaWallet.Api.Infrastructure.Providers;
using NovaWallet.Api.Middlewares;
using Scalar.AspNetCore;
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
        .AddScoped<IRequestContext, HttpRequestContext>()
        .AddScoped<IPartnerCacheService, PartnerCacheService>()
        .AddAppHybridCache(builder.Configuration)
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

    builder.Services.RegisterOpenApiSpecifications();
    builder.Services.AddEndpointsApiExplorer();

    builder.Services.RegisterPartnerAuthorization();
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

    // D. Partner Authentication / Context Population (Populates IRequestContext.Partner)
    app.UseMiddleware<PartnerAuthenticationMiddleware>();

    app.UseAuthorization();

    // E. Rate Limiting (Evaluates PerPartnerPolicy using populated Partner context)
    app.UseRateLimiter();

    // =========================================================================
    // 4. API Documentation & Scalar UI
    // =========================================================================

    if (bool.TryParse(builder.Configuration["Swagger:DisplaySwagger"], out var displaySwagger) && displaySwagger)
    {
        app.MapOpenApi();
        app.MapScalarApiReference(options =>
        {
            options.WithTitle("Enterprise Card API")
                   .WithTheme(ScalarTheme.Moon)
                   .WithOpenApiRoutePattern("/openapi/{documentName}.json");
        });
    }

    // =========================================================================
    // 5. Endpoint Routing & Policy Binding
    // =========================================================================

    app.MapGroup("/api/v1/partners")
       .MapPartnerCardEndpoints();

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