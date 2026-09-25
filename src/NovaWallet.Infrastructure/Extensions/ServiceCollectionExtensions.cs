using Microsoft.Extensions.DependencyInjection;
using RestSharp;
using RestSharp.Serializers.Json;
using System.Text.Json;

namespace NovaWallet.Infrastructure.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection RegisterSingletonRestsharp(this IServiceCollection services)
        {
            services.AddSingleton<RestClient>(_ => new RestClient(
                new RestClientOptions
                {
                    ThrowOnDeserializationError = false,
                },
                configureSerialization: s => s.UseSystemTextJson(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                })
            ));
            return services;
        }
    }
}