using NovaWallet.Application.Abstractions;
using NovaWallet.Infrastructure.Http;

namespace NovaWallet.Api.Http
{
    internal sealed class HttpRequestContext : IRequestContext
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public HttpRequestContext(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        private HttpContext HttpContext => _httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No active HttpContext is available.");

        public Guid? UserId => HttpContext.RetrieveUserId();

        public string? RetrieveIdempotencyKey => HttpContext.RetrieveIdempotencyKey();
    }
}