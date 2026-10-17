namespace CupboardTracker.Application.Ping;

/// <summary>Facts about the running deployment.</summary>
public interface IDeploymentInfo
{
    /// <summary>The deployed commit SHA, or null when not set (for example, running locally).</summary>
    string? CommitSha { get; }
}
