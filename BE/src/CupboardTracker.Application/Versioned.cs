namespace CupboardTracker.Application;

/// <summary>A stored value plus its version (Cosmos ETag), for optimistic-concurrency updates.</summary>
public sealed record Versioned<T>(T Value, string ETag);
