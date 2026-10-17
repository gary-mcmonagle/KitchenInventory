using CupboardTracker.Functions.Api.Auth;
using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class FunctionAuthPolicyTests
{
    [Theory]
    [InlineData("CupboardTracker.Functions.Api.Auth.MeFunction.Run", true)] // anonymous at the platform: needs a user
    [InlineData("CupboardTracker.Functions.Api.PingFunction.Run", false)] // marked [PublicEndpoint]
    [InlineData("CupboardTracker.Functions.Api.DiagnosticsFunction.Run", false)] // function key: the platform checks it
    [InlineData("CupboardTracker.Functions.Api.Hubs.OfflineCheckTimer.Run", false)] // not HTTP
    [InlineData("CupboardTracker.Functions.Api.Nope.Run", true)] // unknown: fail closed
    public void Only_anonymous_http_functions_not_marked_public_need_a_user(string entryPoint, bool requiresUser) =>
        Assert.Equal(requiresUser, FunctionAuthPolicy.RequiresUser(entryPoint));

    [Fact]
    public void Entra_options_read_the_allowed_clients()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ENTRA_TENANT_ID"] = "t", ["ENTRA_API_CLIENT_ID"] = "a", ["ENTRA_ALLOWED_CLIENT_IDS"] = "spa, cli ,",
            }).Build();

        var options = EntraOptions.FromConfiguration(config);

        Assert.True(options.IsConfigured);
        Assert.Equal(["cli", "spa"], options.AllowedClientIds.Order());
        Assert.Equal("https://login.microsoftonline.com/t/v2.0", options.Issuer);
        Assert.False(EntraOptions.FromConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).IsConfigured);
    }
}
