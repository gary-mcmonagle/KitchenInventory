using System.Globalization;
using System.Net;
using System.Text;
using CupboardTracker.Application.Notifications;

namespace CupboardTracker.Application.Digest;

/// <summary>
/// Renders the daily digest (pure: data in, email out). One column, inline styles, no images and no fixed colours, so
/// it reads well on a phone in light and dark mode; a separate plain-text version. Every name is HTML-encoded (Open
/// Food Facts data is untrusted). Each section is one method in <see cref="Sections"/>: add a new one there.
/// </summary>
public static class BuildDailyDigest
{
    private static readonly CultureInfo UkCulture = CultureInfo.GetCultureInfo("en-GB");

    public static EmailContent Build(DigestData data)
    {
        var html = new StringBuilder();
        var text = new StringBuilder();
        var title = data.Date.ToString("dddd d MMMM", UkCulture);
        html.Append(
            "<div style=\"font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Arial,sans-serif;"
            + "font-size:16px;line-height:1.5;max-width:560px\">");
        html.Append(CultureInfo.InvariantCulture, $"<p style=\"margin:0 0 16px;font-size:20px\"><strong>{E(title)}</strong></p>");
        text.AppendLine(title).AppendLine();

        foreach (var section in Sections(data))
        {
            if (section is null)
            {
                continue;
            }

            html.Append(CultureInfo.InvariantCulture, $"<p style=\"margin:20px 0 6px;font-size:18px\"><strong>{E(section.Heading)}</strong></p>");
            text.AppendLine(section.Heading);
            foreach (var part in section.Parts)
            {
                part.Render(html, text);
            }

            text.AppendLine();
        }

        html.Append("</div>");
        return new EmailContent(Subject(data), html.ToString(), text.ToString().TrimEnd() + Environment.NewLine);
    }

    public static string Subject(DigestData data)
    {
        var day = data.Date.ToString("ddd d MMM", UkCulture);
        if (data.IsQuiet)
        {
            return $"Cupboard Tracker – {day}: no changes today";
        }

        var counts = new List<string>();
        if (data.CheckedInCount > 0)
        {
            counts.Add($"{data.CheckedInCount} in");
        }

        if (data.CheckedOutCount > 0)
        {
            counts.Add($"{data.CheckedOutCount} out");
        }

        return $"Cupboard Tracker – {day}: {string.Join(", ", counts)}";
    }

    /// <summary>The sections, in order. Null means "leave this section out".</summary>
    private static IEnumerable<Section?> Sections(DigestData data)
    {
        yield return Activity(data);
        yield return InStock(data);
        yield return UnknownItems(data);
    }

    private static Section Activity(DigestData data)
    {
        if (data.IsQuiet)
        {
            return new Section("Today's activity", [new Note("No changes today.")]);
        }

        var parts = new List<Part>();
        if (data.CheckedIn.Count > 0)
        {
            parts.Add(new Label("Checked in"));
            parts.Add(new Lines(data.CheckedIn));
        }

        if (data.CheckedOut.Count > 0)
        {
            parts.Add(new Label("Checked out"));
            parts.Add(new Lines(data.CheckedOut));
        }

        return new Section("Today's activity", parts);
    }

    private static Section InStock(DigestData data)
    {
        if (data.InStock.Count == 0 && data.UnknownInStock.Count == 0)
        {
            return new Section("In stock", [new Note("Nothing in stock yet.")]);
        }

        var total = data.InStock.Sum(l => l.Count) + data.UnknownInStock.Sum(l => l.Count);
        return data.InStock.Count == 0
            ? new Section($"In stock ({total})", [new Note("Only unknown items, below.")])
            : new Section($"In stock ({total})", [new Lines(data.InStock)]);
    }

    private static Section? UnknownItems(DigestData data) =>
        data.UnknownInStock.Count == 0
            ? null
            : new Section("Unknown items", [
                new Lines(data.UnknownInStock),
                new Note("Open Food Facts doesn't know these barcodes: give them a name in Cupboard Tracker."),
            ]);

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static string Line(DigestLine line) => $"{line.Name} ×{line.Count}";

    private sealed record Section(string Heading, IReadOnlyList<Part> Parts);

    private abstract record Part
    {
        public abstract void Render(StringBuilder html, StringBuilder text);
    }

    private sealed record Label(string Text) : Part
    {
        public override void Render(StringBuilder html, StringBuilder text)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p style=\"margin:8px 0 2px;opacity:0.75\">{E(Text)}</p>");
            text.AppendLine(Text + ":");
        }
    }

    private sealed record Lines(IReadOnlyList<DigestLine> Items) : Part
    {
        public override void Render(StringBuilder html, StringBuilder text)
        {
            html.Append("<ul style=\"margin:0;padding-left:20px\">");
            foreach (var item in Items)
            {
                html.Append(CultureInfo.InvariantCulture, $"<li style=\"margin:2px 0\">{E(Line(item))}</li>");
                text.AppendLine("  " + Line(item));
            }

            html.Append("</ul>");
        }
    }

    private sealed record Note(string Text) : Part
    {
        public override void Render(StringBuilder html, StringBuilder text)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p style=\"margin:4px 0\">{E(Text)}</p>");
            text.AppendLine(Text);
        }
    }
}
