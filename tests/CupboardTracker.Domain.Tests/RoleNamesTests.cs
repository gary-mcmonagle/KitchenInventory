using CupboardTracker.Domain;

namespace CupboardTracker.Domain.Tests;

public sealed class RoleNamesTests
{
    [Theory]
    [InlineData("in", Role.In)]
    [InlineData("out", Role.Out)]
    public void TryParse_accepts_the_wire_names(string value, Role expected)
    {
        Assert.True(RoleNames.TryParse(value, out var role));
        Assert.Equal(expected, role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("IN")]
    [InlineData("checkin")]
    public void TryParse_rejects_anything_else(string? value)
    {
        Assert.False(RoleNames.TryParse(value, out _));
    }

    [Theory]
    [InlineData(Role.In, "in")]
    [InlineData(Role.Out, "out")]
    public void ToWireName_round_trips(Role role, string expected)
    {
        Assert.Equal(expected, role.ToWireName());
    }
}
