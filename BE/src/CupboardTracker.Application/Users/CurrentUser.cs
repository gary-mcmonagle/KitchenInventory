namespace CupboardTracker.Application.Users;

/// <summary>The signed-in household user calling the API (#36): their Entra object ID (<c>oid</c>) and display name. No
/// token types reach Application or Domain.</summary>
public sealed record CurrentUser(Guid ObjectId, string DisplayName);
