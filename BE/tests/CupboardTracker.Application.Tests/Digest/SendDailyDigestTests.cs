using CupboardTracker.Application.Digest;
using CupboardTracker.Application.Notifications;
using CupboardTracker.Application.Tests.Hubs;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Digest;

internal sealed class FakeDigestQueries : IDigestQueries
{
    public List<DigestUnit> InStock { get; } = [];

    public List<DigestUnit> CheckedIn { get; } = [];

    public List<DigestUnit> CheckedOut { get; } = [];

    public List<DigestWindow> Windows { get; } = [];

    public Task<IReadOnlyList<DigestUnit>> GetInStockAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DigestUnit>>(InStock);

    public Task<IReadOnlyList<DigestUnit>> GetCheckedInAsync(DigestWindow window, CancellationToken cancellationToken)
    {
        Windows.Add(window);
        return Task.FromResult<IReadOnlyList<DigestUnit>>(CheckedIn);
    }

    public Task<IReadOnlyList<DigestUnit>> GetCheckedOutAsync(DigestWindow window, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DigestUnit>>(CheckedOut);
}

internal sealed class FakeSentDigests : ISentDigestRepository
{
    public HashSet<DateOnly> Claimed { get; } = [];

    public Task<bool> TryClaimAsync(DigestWindow window, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult(Claimed.Add(window.Date));

    public Task ReleaseAsync(DateOnly day, CancellationToken cancellationToken)
    {
        Claimed.Remove(day);
        return Task.CompletedTask;
    }
}

public sealed class SendDailyDigestTests
{
    // Saturday 17 October 2026, 20:00 UTC = 21:00 BST.
    private static readonly DateTimeOffset NinePm = new(2026, 10, 17, 20, 0, 5, TimeSpan.Zero);

    private readonly FakeDigestQueries queries = new();
    private readonly FakeProducts products = new();
    private readonly FakeSentDigests sent = new();
    private readonly FakeNotifier notifier = new();
    private readonly FixedClock clock = new(NinePm);
    private readonly ListLogger<SendDailyDigest> logger = new();

    private static readonly Guid Bisto = Guid.NewGuid();
    private static readonly Guid Tea = Guid.NewGuid();
    private static readonly Guid Naan = Guid.NewGuid();

    public SendDailyDigestTests()
    {
        var at = NinePm.AddDays(-3);
        products.Products[Bisto] = Product.Found(Bisto, "5000354922831", new("Bisto Gravy", null, null, null, []), at, 1);
        products.Products[Tea] = Product.Found(Tea, "5000208000000", new("pg tips", null, null, null, []), at, 1);
        products.Products[Naan] = Product.NotFound(Naan, "20055363", at, 1);
    }

    private SendDailyDigest UseCase() => new(
        queries, products, sent, notifier,
        new DigestSettings(TimeZoneInfo.FindSystemTimeZoneById("Europe/London")), TestSettings.Stock(), clock, logger);

    [Fact]
    public async Task Groups_counts_sorts_and_separates_unknown_items()
    {
        queries.InStock.AddRange([new(Tea, "5000208000000"), new(Bisto, "5000354922831"), new(Tea, "5000208000000"),
            new(Naan, "20055363")]);
        queries.CheckedIn.AddRange([new(Tea, "5000208000000"), new(Naan, "20055363")]);

        var data = await UseCase().GatherAsync(DigestWindow.EndingOn(new DateOnly(2026, 10, 17), TimeZoneInfo.Utc), CancellationToken.None);

        Assert.Equal([new DigestLine("Bisto Gravy", 1), new DigestLine("pg tips", 2)], data.InStock); // case-insensitive order
        Assert.Equal([new DigestLine("Unknown item (20055363)", 1)], data.UnknownInStock);
        Assert.Equal([new DigestLine("pg tips", 1), new DigestLine("Unknown item (20055363)", 1)], data.CheckedIn);
        Assert.Empty(data.CheckedOut);
    }

    [Fact]
    public async Task Use_first_lists_named_products_due_within_three_days_soonest_first_with_their_badge()
    {
        var today = new DateOnly(2026, 10, 17);
        queries.InStock.AddRange([
            new(Tea, "5000208000000", today.AddDays(3)),
            new(Tea, "5000208000000", today.AddDays(40)),   // the soonest unit decides
            new(Bisto, "5000354922831", today.AddDays(-2)),
            new(Naan, "20055363", today),                    // unknown items aren't in Use first
        ]);

        var data = await UseCase().GatherAsync(DigestWindow.EndingOn(today, TimeZoneInfo.Utc), CancellationToken.None);

        Assert.Equal(
            [new DigestUseFirstLine("Bisto Gravy", 1, "Est. expired"), new DigestUseFirstLine("pg tips", 2, "Est. 3 days")],
            data.UseFirstLines);
    }

    [Fact]
    public async Task Nothing_due_soon_means_no_use_first()
    {
        var today = new DateOnly(2026, 10, 17);
        queries.InStock.AddRange([new(Tea, "5000208000000", today.AddDays(4)), new(Bisto, "5000354922831")]);

        var data = await UseCase().GatherAsync(DigestWindow.EndingOn(today, TimeZoneInfo.Utc), CancellationToken.None);

        Assert.Empty(data.UseFirstLines);
    }

    [Fact]
    public async Task The_9pm_run_sends_one_digest_for_todays_window()
    {
        queries.InStock.Add(new(Bisto, "5000354922831"));

        await UseCase().ExecuteAsync(CancellationToken.None);

        var email = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationKind.DailyDigest, email.Kind);
        Assert.Equal("Cupboard Tracker – Sat 17 Oct: no changes today", email.Content.Subject);
        var window = Assert.Single(queries.Windows);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 20, 0, 0, TimeSpan.Zero), window.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 20, 0, 0, TimeSpan.Zero), window.End);
    }

    [Fact]
    public async Task The_other_run_exits_quietly()
    {
        clock.Now = NinePm.AddHours(1); // 21:00 UTC = 22:00 BST

        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Empty(notifier.Sent);
        Assert.Empty(sent.Claimed);
        Assert.Contains(logger.Entries, e => e.Message.Contains("Not the digest hour", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_run_the_same_day_sends_nothing()
    {
        await UseCase().ExecuteAsync(CancellationToken.None);
        clock.Now = NinePm.AddMinutes(2);

        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Single(notifier.Sent);
        Assert.Contains(logger.Entries, e => e.Message.Contains("already sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_send_logs_an_error_releases_the_day_and_throws_for_the_retry()
    {
        notifier.FailWith = new InvalidOperationException("ACS said no");

        await Assert.ThrowsAsync<InvalidOperationException>(() => UseCase().ExecuteAsync(CancellationToken.None));

        Assert.Empty(sent.Claimed);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);

        notifier.FailWith = null;
        clock.Now = NinePm.AddMinutes(1); // the retry, still in the 21:00 hour
        await UseCase().ExecuteAsync(CancellationToken.None);
        Assert.Single(notifier.Sent);
    }
}
