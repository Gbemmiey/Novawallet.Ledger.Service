using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Api.Core.Configuration;
using System.Text;

namespace NovaWallet.Api.Infrastructure.Extensions
{
    public static class AuthenticationExtensions
    {
        private const string JwtSecretKey = "JWT__SECRET_KEY";
        private const string JwtIssuerKey = "JWT__ISSUER";
        private const string JwtAudienceKey = "JWT__AUDIENCE";

        public static IServiceCollection AddJwtAuthenticationAndAuthorization(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services
                .AddOptions<JwtSettings>()
                .Bind(configuration.GetSection("Jwt"))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer();

            services
                .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<IOptions<JwtSettings>>((options, jwtOptions) =>
                {
                    var jwtSettings = jwtOptions.Value;

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = jwtSettings.Issuer,
                        ValidAudience = jwtSettings.Audience,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
                        ClockSkew = TimeSpan.Zero
                    };

                    options.Events = new JwtBearerEvents
                    {
                        OnAuthenticationFailed = context =>
                        {
                            if (context.Exception is SecurityTokenExpiredException)
                            {
                                context.Response.Headers["Token-Expired"] = "true";
                                context.Fail("Token has expired");
                            }

                            return Task.CompletedTask;
                        }
                    };
                });

            services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();

                // Backs the /api/v1/admin route group. RequireRole checks the JWT's role claim
                // (ClaimTypes.Role, written by MockUserAuthHelper) against nameof(UserRole.Admin).
                options.AddPolicy(NovaWalletConstants.AuthorizationPolicyConstants.AdminOnly, policy =>
                    policy.RequireRole(nameof(Core.Enums.UserRole.Admin)));
            });

            return services;
        }
    }
}