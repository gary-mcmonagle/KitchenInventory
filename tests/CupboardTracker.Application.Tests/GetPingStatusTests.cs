using CupboardTracker.Application.Ping;

namespace CupboardTracker.Application.Tests;

public sealed class GetPingStatusTests
{
    [Fact]
    public void Reports_ok_and_the_deployed_commit()
    {
        var status = new GetPingStatus(new FakeDeploymentInfo("e1130b1")).Execute();

        Assert.Equal(new PingStatus("ok", "e1130b1"), status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reports_local_when_no_commit_is_set(string? commit)
    {
        var status = new GetPingStatus(new FakeDeploymentInfo(commit)).Execute();

        Assert.Equal(GetPingStatus.LocalCommit, status.Commit);
    }

    [Fact]
    public void Trims_the_commit()
    {
        var status = new GetPingStatus(new FakeDeploymentInfo(" abc123\n")).Execute();

        Assert.Equal("abc123", status.Commit);
    }

    private sealed class FakeDeploymentInfo(string? commitSha) : IDeploymentInfo
    {
        public string? CommitSha { get; } = commitSha;
    }
}
