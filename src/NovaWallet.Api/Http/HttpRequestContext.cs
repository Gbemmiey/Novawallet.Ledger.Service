using NovaWallet.Api.Application.Services;
using NovaWallet.Api.Core.Dto.FintechPartners;
using NovaWallet.Api.Infrastructure.Http;

namespace NovaWallet.Api.Http
{
    internal sealed class HttpRequestContext : IRequestContext
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private const string PartnerContextItemKey = "__PartnerContext";

        public HttpRequestContext(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        private HttpContext HttpContext => _httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No active HttpContext is available.");

        public string TraceId => HttpContext.RetrieveTraceId();

        public string? RetrieveHashedPartnerClientKey => HttpContext.RetrieveHashedPartnerApiKey();

        public FintechPartnerCacheDto? Partner
        {
            get => HttpContext.Items.TryGetValue(PartnerContextItemKey, out var value)
                ? value as FintechPartnerCacheDto
                : null;
            set => HttpContext.Items[PartnerContextItemKey] = value;
        }
    }
}