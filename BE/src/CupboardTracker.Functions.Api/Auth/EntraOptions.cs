using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Functions.Api.Auth;

/// <summary>Who may call the API with a user token (#36, spike #35). Not secrets: app settings from Bicep.</summary>
public sealed record EntraOptions(string TenantId, string ApiClientId, IReadOnlySet<string> AllowedClientIds)
{
    public const string Scope = "access_as_user";

    /// <summary>False until the registrations exist (deploy/entra.sh, then deploy/infra.sh): every user request is refused.</summary>
    public bool IsConfigured => TenantId.Length > 0 && ApiClientId.Length > 0 && AllowedClientIds.Count > 0;

    public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    public string MetadataAddress => $"{Issuer}/.well-known/openid-configuration";

    public static EntraOptions FromConfiguration(IConfiguration config) => new(
        (config["ENTRA_TENANT_ID"] ?? "").Trim(),
        (config["ENTRA_API_CLIENT_ID"] ?? "").Trim(),
        (config["ENTRA_ALLOWED_CLIENT_IDS"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));
}
