using CupboardTracker.Application.Ping;
using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure;

/// <summary>Reads the deployed commit from the <c>COMMIT_SHA</c> app setting.</summary>
public sealed class ConfigurationDeploymentInfo(IConfiguration configuration) : IDeploymentInfo
{
    public const string CommitShaSetting = "COMMIT_SHA";

    public string? CommitSha => configuration[CommitShaSetting];
}
