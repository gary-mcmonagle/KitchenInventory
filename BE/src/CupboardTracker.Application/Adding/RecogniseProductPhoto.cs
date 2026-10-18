using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Adding;

/// <summary>A recognised item and the existing product it matches, if any.</summary>
public sealed record RecognisedItemView(RecognisedItem Item, Guid? MatchProductId, string? MatchDisplayName);

public abstract record RecogniseProductPhotoResult
{
    public sealed record Recognised(bool Readable, IReadOnlyList<RecognisedItemView> Items) : RecogniseProductPhotoResult;

    public sealed record Invalid(string Error) : RecogniseProductPhotoResult;

    public sealed record Unavailable : RecogniseProductPhotoResult;
}

/// <summary>
/// <c>POST /api/photos/recognise</c> (#58): what's in the photo, each item matched to an existing product by name.
/// Nothing is stored; the app shows a checklist to confirm.
/// </summary>
public sealed partial class RecogniseProductPhoto(
    IProductPhotoRecogniser recogniser, ProductMatcher matcher, ILogger<RecogniseProductPhoto> logger)
{
    public const int MaxBytes = 1024 * 1024;

    public async Task<RecogniseProductPhotoResult> ExecuteAsync(ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken)
    {
        if (jpeg.Length == 0 || jpeg.Length > MaxBytes)
        {
            return new RecogniseProductPhotoResult.Invalid($"Send one JPEG photo of at most {MaxBytes / 1024} KB.");
        }

        if (jpeg.Length < 3 || jpeg.Span[0] != 0xFF || jpeg.Span[1] != 0xD8 || jpeg.Span[2] != 0xFF)
        {
            return new RecogniseProductPhotoResult.Invalid("The photo must be a JPEG.");
        }

        var result = await recogniser.RecogniseAsync(jpeg, cancellationToken);
        if (result is not PhotoRecognitionResult.Recognised { Recognition: var recognition })
        {
            LogUnavailable(((PhotoRecognitionResult.Unavailable)result).Reason);
            return new RecogniseProductPhotoResult.Unavailable();
        }

        var items = new List<RecognisedItemView>();
        foreach (var item in recognition.Items)
        {
            var match = await matcher.FindAsync(item.Name, cancellationToken);
            items.Add(new RecognisedItemView(item, match?.ProductId, match?.DisplayName));
        }

        LogRecognised(items.Count, items.Count(i => i.MatchProductId is not null));
        return new RecogniseProductPhotoResult.Recognised(recognition.Readable, items);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Photo recognised: {Items} items, {Matched} matched to existing products.")]
    private partial void LogRecognised(int items, int matched);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Photo recognition unavailable ({Reason}); the app falls back to typing.")]
    private partial void LogUnavailable(string reason);
}
