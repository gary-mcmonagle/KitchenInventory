using CupboardTracker.Application.Digest;

namespace CupboardTracker.Application.Tests.Digest;

public sealed class BuildDailyDigestTests
{
    private static readonly DateOnly Friday = new(2026, 10, 16);

    private static DigestData Data(
        DigestLine[]? checkedIn = null, DigestLine[]? checkedOut = null, DigestLine[]? inStock = null, DigestLine[]? unknown = null) =>
        new(Friday, checkedIn ?? [], checkedOut ?? [], inStock ?? [], unknown ?? []);

    [Fact]
    public void Activity_day_subject_counts_units_in_and_out()
    {
        Assert.Equal("Cupboard Tracker – Fri 16 Oct: 3 in",
            BuildDailyDigest.Subject(Data(checkedIn: [new("PG Tips", 2), new("Bisto", 1)])));
        Assert.Equal("Cupboard Tracker – Fri 16 Oct: 3 in, 1 out",
            BuildDailyDigest.Subject(Data(checkedIn: [new("PG Tips", 3)], checkedOut: [new("Bisto", 1)])));
        Assert.Equal("Cupboard Tracker – Fri 16 Oct: 1 out",
            BuildDailyDigest.Subject(Data(checkedOut: [new("Bisto", 1)])));
    }

    [Fact]
    public void A_quiet_day_says_so_and_still_lists_stock()
    {
        var email = BuildDailyDigest.Build(Data(inStock: [new("Bisto", 2)]));

        Assert.Equal("Cupboard Tracker – Fri 16 Oct: no changes today", email.Subject);
        Assert.Contains("No changes today.", email.PlainText, StringComparison.Ordinal);
        Assert.Contains("No changes today.", email.Html, StringComparison.Ordinal);
        Assert.Contains("Bisto ×2", email.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Checked_out_appears_only_when_there_is_data()
    {
        var withoutOut = BuildDailyDigest.Build(Data(checkedIn: [new("PG Tips", 1)]));
        Assert.DoesNotContain("Checked out", withoutOut.PlainText, StringComparison.Ordinal);
        Assert.Contains("Checked in:", withoutOut.PlainText, StringComparison.Ordinal);

        var withOut = BuildDailyDigest.Build(Data(checkedIn: [new("PG Tips", 1)], checkedOut: [new("Bisto", 1)]));
        Assert.Contains("Checked out:\n  Bisto ×1", withOut.PlainText.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Sections_come_in_order_with_totals()
    {
        var text = BuildDailyDigest.Build(Data(
            checkedIn: [new("PG Tips", 2)],
            inStock: [new("Bisto", 1), new("PG Tips", 4)],
            unknown: [new("Unknown item (20055363)", 1)])).PlainText.ReplaceLineEndings("\n");

        var activity = text.IndexOf("Today's activity", StringComparison.Ordinal);
        var stock = text.IndexOf("In stock (6)", StringComparison.Ordinal);
        var unknown = text.IndexOf("Unknown items", StringComparison.Ordinal);
        Assert.True(activity >= 0 && activity < stock && stock < unknown, text);
        Assert.Contains("Unknown item (20055363) ×1", text, StringComparison.Ordinal);
        Assert.Contains("give them a name", text, StringComparison.Ordinal);
        Assert.StartsWith("Friday 16 October\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_stock_and_no_unknowns()
    {
        var email = BuildDailyDigest.Build(Data());

        Assert.Contains("Nothing in stock yet.", email.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown items", email.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_unknown_items_in_stock()
    {
        var text = BuildDailyDigest.Build(Data(unknown: [new("Unknown item (123)", 2)])).PlainText;

        Assert.Contains("In stock (2)", text, StringComparison.Ordinal);
        Assert.Contains("Only unknown items, below.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_are_html_encoded_and_the_plain_text_is_not()
    {
        var email = BuildDailyDigest.Build(Data(inStock: [new("Ben & Jerry's <\"Cookie\">", 1)]));

        Assert.Contains("Ben &amp; Jerry&#39;s &lt;&quot;Cookie&quot;&gt; &#215;1", email.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<\"Cookie\">", email.Html, StringComparison.Ordinal);
        Assert.Contains("Ben & Jerry's <\"Cookie\"> ×1", email.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("<", email.PlainText.Replace("<\"Cookie\">", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Html_uses_inline_styles_and_no_fixed_colours()
    {
        var html = BuildDailyDigest.Build(Data(inStock: [new("Bisto", 1)])).Html;

        Assert.DoesNotContain("<style", html, StringComparison.Ordinal);
        Assert.DoesNotContain("color:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
    }
}
