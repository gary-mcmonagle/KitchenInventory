using System.Security.Claims;
using CupboardTracker.Application.Users;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CupboardTracker.Functions.Api.Auth;

/// <summary>The result of checking a request's bearer token.</summary>
public abstract record AuthOutcome
{
    public sealed record Valid(CurrentUser User) : AuthOutcome;

    /// <summary>401: no token, or not a valid token for this API.</summary>
    public sealed record Unauthorized(string Reason) : AuthOutcome;

    /// <summary>403: a valid token, but from a client that isn't allowed, or without the scope.</summary>
    public sealed record Forbidden(string Reason) : AuthOutcome;
}

public interface ITokenValidator
{
    Task<AuthOutcome> ValidateAsync(string? authorizationHeader, CancellationToken cancellationToken);
}

/// <summary>
/// Validates Entra ID v2 access tokens (spike #35): signature (the tenant's signing keys, fetched and cached),
/// lifetime, issuer, audience (the API's client ID), tenant, the calling client (<c>azp</c>) and the
/// <c>access_as_user</c> scope.
/// </summary>
public sealed class EntraTokenValidator(EntraOptions options, IConfigurationManager<OpenIdConnectConfiguration> metadata)
    : ITokenValidator
{
    private readonly JsonWebTokenHandler handler = new();

    public async Task<AuthOutcome> ValidateAsync(string? authorizationHeader, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return new AuthOutcome.Unauthorized("Sign-in isn't configured on the API yet.");
        }

        if (authorizationHeader is null
            || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || authorizationHeader.Length <= "Bearer ".Length)
        {
            return new AuthOutcome.Unauthorized("Missing bearer token.");
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        var signing = await metadata.GetConfigurationAsync(cancellationToken);
        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = options.ApiClientId,
            IssuerSigningKeys = signing.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        });
        if (!result.IsValid)
        {
            return new AuthOutcome.Unauthorized($"Invalid token ({result.Exception?.GetType().Name ?? "not valid"}).");
        }

        var claims = result.ClaimsIdentity;
        if (!string.Equals(Claim(claims, "tid"), options.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return new AuthOutcome.Unauthorized("Token is from another tenant.");
        }

        if (Claim(claims, "azp") is not { } client || !options.AllowedClientIds.Contains(client))
        {
            return new AuthOutcome.Forbidden("This client isn't allowed to call the API.");
        }

        var scopes = (Claim(claims, "scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains(EntraOptions.Scope, StringComparer.Ordinal))
        {
            return new AuthOutcome.Forbidden($"The token doesn't have the {EntraOptions.Scope} scope.");
        }

        if (!Guid.TryParse(Claim(claims, "oid"), out var objectId))
        {
            return new AuthOutcome.Unauthorized("Token has no user (oid).");
        }

        var name = Claim(claims, "name") ?? Claim(claims, "preferred_username") ?? objectId.ToString("D");
        return new AuthOutcome.Valid(new CurrentUser(objectId, name));
    }

    private static string? Claim(ClaimsIdentity identity, string type) => identity.FindFirst(type)?.Value;

    /// <summary>The tenant's OpenID metadata (signing keys), fetched once and refreshed by the library.</summary>
    public static IConfigurationManager<OpenIdConnectConfiguration> MetadataFor(EntraOptions options) =>
        new ConfigurationManager<OpenIdConnectConfiguration>(
            options.MetadataAddress, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever());
}
