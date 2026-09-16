namespace NovaWallet.Api.Core.Dto.Login
{
    public class MockLoginRequest
    {
        public Guid UserId { get; set; }
    }

    /// <summary>
    /// Represents the response returned upon a successful user login.
    /// </summary>
    public class MockLoginResponse
    {
        /// <summary>
        /// Gets or sets the access token used to authenticate the user's requests.
        /// </summary>
        public string AccessToken { get; set; } = string.Empty;
    }
}