using System.Net;
using System.Text;
using CupboardTracker.Application.Scans;
using CupboardTracker.Functions.Ingest.Scans;

namespace CupboardTracker.Functions.Ingest.Tests;

public sealed class ScanRequestTests
{
    private static (RecordScanCommand? Command, string? Error) Parse(string json) =>
        ScanRequest.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parses_known_fields_and_keeps_extra_fields()
    {
        var (command, error) = Parse("""
            {"scanId":"0b1c3f1e-6d0e-4c0a-9e0a-3a4d7c1f9a21","barcode":"5000354922831","deviceId":"scanner-in",
             "role":"in","scannedAt":"2026-10-17T13:59:58.123Z","clientSha":"abc1234","source":"simulator","attempt":2}
            """);

        Assert.Null(error);
        Assert.Equal("5000354922831", command!.Barcode);
        Assert.Equal("in", command.Role);
        Assert.Equal("simulator", command.ExtraFields["source"].GetString());
        Assert.Equal(2, command.ExtraFields["attempt"].GetInt32());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("")]
    public void Non_objects_are_rejected(string json) =>
        Assert.Equal("Body must be a JSON object.", Parse(json).Error);

    [Fact]
    public void Known_fields_must_be_strings() =>
        Assert.Equal("barcode must be a string.", Parse("""{"barcode":5000354922831}""").Error);

    [Fact]
    public void Oversized_bodies_are_rejected()
    {
        var big = "{\"source\":\"" + new string('x', ScanRequest.MaxBodyBytes) + "\"}";
        Assert.Contains("too large", Parse(big).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_fields_reach_the_use_case_as_null_for_validation()
    {
        var (command, error) = Parse("{}");
        Assert.Null(error);
        Assert.Null(command!.Barcode);
    }

    [Fact]
    public void A_new_scan_and_a_resend_get_the_same_2xx()
    {
        var id = Guid.NewGuid();
        var (first, _) = ScanRequest.ToResponse(new RecordScanResult.Recorded(id, WasAlreadyRecorded: false));
        var (resend, _) = ScanRequest.ToResponse(new RecordScanResult.Recorded(id, WasAlreadyRecorded: true));
        Assert.Equal(HttpStatusCode.Accepted, first);
        Assert.Equal(first, resend);
    }

    [Fact]
    public void Invalid_scans_are_400() =>
        Assert.Equal(HttpStatusCode.BadRequest, ScanRequest.ToResponse(new RecordScanResult.Invalid(["role must be 'in' or 'out'."])).Status);
}
