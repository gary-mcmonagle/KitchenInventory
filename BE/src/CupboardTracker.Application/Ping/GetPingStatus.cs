namespace CupboardTracker.Application.Ping;

public sealed record PingStatus(string Status, string Commit);

/// <summary>Reports that the app is up, and which commit is deployed.</summary>
public sealed class GetPingStatus(IDeploymentInfo deploymentInfo)
{
    public const string LocalCommit = "local";

    public PingStatus Execute()
    {
        var commit = deploymentInfo.CommitSha;
        return new PingStatus("ok", string.IsNullOrWhiteSpace(commit) ? LocalCommit : commit.Trim());
    }
}
