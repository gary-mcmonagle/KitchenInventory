using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Scanners;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Scanners;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class ScannersFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset At = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData("CupboardTracker.Functions.Api.Scanners.ScannersFunctions.Get")]
    [InlineData("CupboardTracker.Functions.Api.Scanners.ScannersFunctions.Dismiss")]
    public void Both_need_a_signed_in_user(string entryPoint) => Assert.True(FunctionAuthPolicy.RequiresUser(entryPoint));

    [Fact]
    public void Json_matches_the_contract()
    {
        var view = new ScannersView(At,
            new ScannerHubView("cupboard-pi", At.AddMinutes(-2), TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(20), true),
            [new ScannerDeviceView("scanner-out", Role.Out, false, null, At.AddMinutes(-30), null)],
            [new NoMatchCheckInView(Id, "5010012345674", NoMatchKind.NoMatch, At, Id, 1),
                new NoMatchCheckInView(null, "2012345678906", NoMatchKind.StoreLabel, At, null, 0, Id)],
            [new UnmatchedCheckOutView(Id, "20055363", null, null, At, StoreLabel: true)],
            [new RecentCheckOutView(Id, Id, "Beef Gravy", At, "scanner")]);

        var expected = """
            {"generatedAt":"2026-10-18T08:00:00.000Z",
            "hub":{"hubId":"cupboard-pi","lastSeenAt":"2026-10-18T07:58:00.000Z","heartbeatIntervalSeconds":120,"offlineThresholdMinutes":20,"online":true},
            "devices":[{"deviceId":"scanner-out","role":"out","present":false,"presentSince":null,"absentSince":"2026-10-18T07:30:00.000Z","lastScanAt":null}],
            "noMatchCheckIns":[{"productId":"11111111-1111-1111-1111-111111111111","barcode":"5010012345674","status":"noMatch","scannedAt":"2026-10-18T08:00:00.000Z","unitId":"11111111-1111-1111-1111-111111111111","inStockCount":1,"scanId":null},
            {"productId":null,"barcode":"2012345678906","status":"storeLabel","scannedAt":"2026-10-18T08:00:00.000Z","unitId":null,"inStockCount":0,"scanId":"11111111-1111-1111-1111-111111111111"}],
            "unmatchedCheckOuts":[{"scanId":"11111111-1111-1111-1111-111111111111","barcode":"20055363","productId":null,"displayName":null,"scannedAt":"2026-10-18T08:00:00.000Z","storeLabel":true}],
            "recentlyCheckedOut":[{"unitId":"11111111-1111-1111-1111-111111111111","productId":"11111111-1111-1111-1111-111111111111","displayName":"Beef Gravy","checkedOutAt":"2026-10-18T08:00:00.000Z","source":"scanner"}]}
            """.ReplaceLineEndings("");

        Assert.Equal(expected, JsonSerializer.Serialize(ScannersFunctions.ToJson(view), WebJson));
    }

    [Fact]
    public void Dismiss_responses()
    {
        Assert.Equal(HttpStatusCode.OK, ScannersFunctions.ToResponse(new DismissUnmatchedCheckOutResult.Dismissed(Id)).Status);
        Assert.Equal(HttpStatusCode.NotFound, ScannersFunctions.ToResponse(new DismissUnmatchedCheckOutResult.NotFound(Id)).Status);
    }
}
