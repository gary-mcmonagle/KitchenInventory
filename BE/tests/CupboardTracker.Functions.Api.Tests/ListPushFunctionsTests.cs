using CupboardTracker.Functions.Api.Auth;

namespace CupboardTracker.Functions.Api.Tests;

// The shopping list push (#86).
public sealed class ListPushFunctionsTests
{
    [Fact]
    public void Viewed_needs_a_signed_in_user() =>
        Assert.True(FunctionAuthPolicy.RequiresUser("CupboardTracker.Functions.Api.ShoppingList.ShoppingListViewedFunction.Viewed"));

    [Fact]
    public void The_queue_trigger_isnt_http() =>
        Assert.False(FunctionAuthPolicy.RequiresUser("CupboardTracker.Functions.Api.ShoppingList.ListPushFunction.Run"));
}
