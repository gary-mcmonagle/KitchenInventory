using System.Security.Cryptography;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class EntraTokenValidatorTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Api = "22222222-2222-2222-2222-222222222222";
    private const string Spa = "33333333-3333-3333-3333-333333333333";
    private const string AzureCli = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    private static readonly Guid UserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly RsaSecurityKey TenantKey = NewKey("tenant-key");
    private static readonly RsaSecurityKey OtherKey = NewKey("other-key");

    private static readonly EntraOptions Options = new(Tenant, Api, new HashSet<string> { Spa, AzureCli });

    private static RsaSecurityKey NewKey(string id) => new(RSA.Create(2048)) { KeyId = id };

    private static EntraTokenValidator Validator(EntraOptions? options = null)
    {
        var metadata = new OpenIdConnectConfiguration();
        metadata.SigningKeys.Add(TenantKey);
        return new EntraTokenValidator(options ?? Options, new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
    }

    private static string Token(
        string issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
        string audience = Api,
        string tid = Tenant,
        string azp = Spa,
        string? scp = "access_as_user",
        DateTime? expires = null,
        RsaSecurityKey? key = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["tid"] = tid, ["azp"] = azp, ["oid"] = UserId.ToString(), ["name"] = "Gary McMonagle",
        };
        if (scp is not null)
        {
            claims["scp"] = scp;
        }

        var now = DateTime.UtcNow;
        var exp = expires ?? now.AddHours(1);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = exp.AddHours(-1),
            NotBefore = exp.AddHours(-1),
            Expires = exp,
            SigningCredentials = new SigningCredentials(key ?? TenantKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private static Task<AuthOutcome> Validate(string? header, EntraOptions? options = null) =>
        Validator(options).ValidateAsync(header, CancellationToken.None);

    [Fact]
    public async Task A_valid_token_gives_the_current_user()
    {
        var outcome = Assert.IsType<AuthOutcome.Valid>(await Validate("Bearer " + Token()));
        Assert.Equal(UserId, outcome.User.ObjectId);
        Assert.Equal("Gary McMonagle", outcome.User.DisplayName);
    }

    [Fact]
    public async Task The_azure_cli_is_allowed_for_verification() =>
        Assert.IsType<AuthOutcome.Valid>(await Validate("Bearer " + Token(azp: AzureCli)));

    [Fact]
    public async Task Extra_scopes_are_fine() =>
        Assert.IsType<AuthOutcome.Valid>(await Validate("Bearer " + Token(scp: "openid access_as_user")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer ")]
    [InlineData("Basic abc")]
    [InlineData("Bearer not-a-jwt")]
    public async Task Missing_or_malformed_tokens_are_401(string? header) =>
        Assert.IsType<AuthOutcome.Unauthorized>(await Validate(header));

    public static TheoryData<string, string> BadTokens => new()
    {
        { "wrong audience", Token(audience: "55555555-5555-5555-5555-555555555555") },
        { "wrong issuer", Token(issuer: "https://login.microsoftonline.com/99999999-9999-9999-9999-999999999999/v2.0") },
        { "wrong tenant claim", Token(tid: "99999999-9999-9999-9999-999999999999") },
        { "expired", Token(expires: DateTime.UtcNow.AddMinutes(-10)) },
        { "signed by another key", Token(key: OtherKey) },
    };

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task Invalid_tokens_are_401(string why, string token) =>
        Assert.True(await Validate("Bearer " + token) is AuthOutcome.Unauthorized, why);

    [Theory]
    [InlineData("66666666-6666-6666-6666-666666666666", "access_as_user")] // another client
    [InlineData(Spa, null)] // no scope
    [InlineData(Spa, "User.Read")] // another scope
    public async Task Wrong_client_or_scope_is_403(string azp, string? scp) =>
        Assert.IsType<AuthOutcome.Forbidden>(await Validate("Bearer " + Token(azp: azp, scp: scp)));

    [Fact]
    public async Task Until_it_is_configured_every_request_is_refused() =>
        Assert.IsType<AuthOutcome.Unauthorized>(
            await Validate("Bearer " + Token(), new EntraOptions(Tenant, "", new HashSet<string>())));
}
