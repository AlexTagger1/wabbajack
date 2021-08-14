using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;


namespace Wabbajack.Server.CDN
{

    public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
    {
        public const string DefaultScheme = "API Key";
        public string Scheme => DefaultScheme;
        public string AuthenticationType = DefaultScheme;
    }

    public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
    {
        private AppSettings _settings;
        private const string ProblemDetailsContentType = "application/problem+json";
        private const string ApiKeyHeaderName = "X-Api-Key";

        private static Dictionary<string, string> _knownKeys { get; set; } = new();
        
        public ApiKeyAuthenticationHandler(
            IOptionsMonitor<ApiKeyAuthenticationOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            AppSettings settings,
            ISystemClock clock) : base(options, logger, encoder, clock)
        {
            _settings = settings;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {

            var authorKey = Request.Headers[ApiKeyHeaderName].FirstOrDefault();

            if (authorKey == null)
            {
                return AuthenticateResult.NoResult();
            }


            var authedKeys = await File.ReadAllLinesAsync(_settings.AuthFile);
            var indexed = authedKeys.Select(k => k.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToDictionary(k => k[0], v => v[1]);
            
            if (!indexed.TryGetValue(authorKey, out var author))
                return AuthenticateResult.Fail("Key invalid");

            var claims = new List<Claim> {new(ClaimTypes.Name, author)};

            claims.Add(new Claim(ClaimTypes.Role, "Author"));
            claims.Add(new Claim(ClaimTypes.Role, "User"));

            var identity = new ClaimsIdentity(claims, Options.AuthenticationType);
            var identities = new List<ClaimsIdentity> {identity};
            var principal = new ClaimsPrincipal(identities);
            var ticket = new AuthenticationTicket(principal, Options.Scheme);
            return AuthenticateResult.Success(ticket);
        }

        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = 401;
            Response.ContentType = ProblemDetailsContentType;
            await Response.WriteAsync("Unauthorized");
        }

        protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = 403;
            Response.ContentType = ProblemDetailsContentType;
            await Response.WriteAsync("forbidden");
        }
    }

    public static class ApiKeyAuthorizationHandlerExtensions
    {
        public static AuthenticationBuilder AddApiKeySupport(this AuthenticationBuilder authenticationBuilder, Action<ApiKeyAuthenticationOptions> options)
        {
            return authenticationBuilder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.DefaultScheme, options);
        }

    }
}
