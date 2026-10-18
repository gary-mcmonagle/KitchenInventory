namespace CupboardTracker.Application.Adding;

public enum ItemKind
{
    Packaged,
    Loose,
    StoreLabelled,
}

/// <summary>Something seen in a photo (#58, spike #41). Brand and size only when legible on that item.</summary>
public sealed record RecognisedItem(string Name, string? Brand, string? Size, ItemKind Kind, int Quantity);

public sealed record PhotoRecognition(bool Readable, IReadOnlyList<RecognisedItem> Items);

public abstract record PhotoRecognitionResult
{
    public sealed record Recognised(PhotoRecognition Recognition) : PhotoRecognitionResult;

    /// <summary>The model couldn't be reached or refused the request: the app offers typing and the pick list.</summary>
    public sealed record Unavailable(string Reason) : PhotoRecognitionResult;
}

/// <summary>Says what food is in a photo, several items at once (gpt-4o on Foundry in Infrastructure). The photo is
/// never stored or logged.</summary>
public interface IProductPhotoRecogniser
{
    Task<PhotoRecognitionResult> RecogniseAsync(ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken);
}
