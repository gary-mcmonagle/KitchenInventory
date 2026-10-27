using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CupboardTracker.Application.AiModels;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Settings;

namespace CupboardTracker.Functions.Api.Tests;

// The Foundry model settings endpoints (#120): who may call them, what they accept and what they return.
public sealed class AiModelFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly AiModelDefaults Defaults = new("gpt-4o", "gpt-4o", "gpt-4o");
    private static readonly Guid Gary = Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67");

    [Theory]
    [InlineData("CupboardTracker.Functions.Api.Settings.AiModelFunctions.Get")]
    [InlineData("CupboardTracker.Functions.Api.Settings.AiModelFunctions.Put")]
    [InlineData("CupboardTracker.Functions.Api.Settings.AiModelFunctions.Delete")]
    [InlineData("CupboardTracker.Functions.Api.Settings.AiModelFunctions.SetActive")]
    [InlineData("CupboardTracker.Functions.Api.Settings.AiModelFunctions.Revert")]
    public void All_need_a_signed_in_user(string entryPoint) => Assert.True(FunctionAuthPolicy.RequiresUser(entryPoint));

    [Fact]
    public void A_model_body_needs_a_label_and_a_deployment()
    {
        Assert.Equal(("GPT-6 Luna", "gpt-6-luna", (string?)null), AiModelFunctions.ParseModel(Body("""{"label":"GPT-6 Luna","deployment":"gpt-6-luna"}""")));
        foreach (var bad in new[] { "", "[]", "{}", """{"label":"x"}""", """{"deployment":"x"}""", """{"label":1,"deployment":"x"}""", """{"label":"x","deployment":null}""" })
        {
            Assert.NotNull(AiModelFunctions.ParseModel(Body(bad)).Error);
        }

        Assert.Equal("Body must be a JSON object.", AiModelFunctions.ParseModel(Body("{nope")).Error);
        Assert.NotNull(AiModelFunctions.ParseModel(new byte[AiModelFunctions.MaxBodyBytes + 1]).Error);
    }

    [Fact]
    public void An_active_body_needs_a_model_id()
    {
        Assert.Equal(("luna", (string?)null), AiModelFunctions.ParseActive(Body("""{"modelId":"luna"}""")));
        foreach (var bad in new[] { "", "{}", """{"modelId":3}""", "[]" })
        {
            Assert.NotNull(AiModelFunctions.ParseActive(Body(bad)).Error);
        }

        Assert.Equal("Body must be a JSON object.", AiModelFunctions.ParseActive(Body("{nope")).Error);
    }

    [Fact]
    public void Results_map_to_status_codes()
    {
        Assert.Equal(HttpStatusCode.OK, AiModelFunctions.ToResponse(new AiModelsResult.Done(AiModelSettings.Empty), Defaults).Status);
        Assert.Equal(HttpStatusCode.BadRequest, AiModelFunctions.ToResponse(new AiModelsResult.Invalid("x"), Defaults).Status);
        Assert.Equal(HttpStatusCode.NotFound, AiModelFunctions.ToResponse(new AiModelsResult.NotFound("x"), Defaults).Status);
        var (status, body) = AiModelFunctions.ToResponse(new AiModelsResult.Conflict("Luna hasn't passed the tests for recipes."), Defaults);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("""{"error":"Luna hasn't passed the tests for recipes."}""", JsonSerializer.Serialize(body, WebJson).Replace("\\u0027", "'", StringComparison.Ordinal));
    }

    [Fact]
    public void No_settings_shows_no_models_every_job_on_its_default()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(AiModelFunctions.ToJson(AiModelSettings.Empty, new AiModelDefaults("r", "p", "g")), WebJson))!;

        Assert.Empty(json["models"]!.AsArray());
        Assert.All(AiJobs.All.Select(AiJobs.Id), job => Assert.Null(json["active"]![job]));
        Assert.Equal(("r", "p", "g"), (json["defaults"]!["recipes"]!.GetValue<string>(), json["defaults"]!["photos"]!.GetValue<string>(), json["defaults"]!["genericNames"]!.GetValue<string>()));
    }

    [Fact]
    public void The_contract_shows_models_with_their_tests_and_the_active_choice()
    {
        var added = new DateTimeOffset(2026, 10, 27, 9, 0, 0, 123, TimeSpan.Zero);
        var settings = new AiModelSettings(
            [new AiModel("luna", "GPT-6 Luna", "gpt-6-luna", TemperatureMode.Default, [AiJob.Recipes], Gary, "Gary", added, new JsonObject { ["passed"] = true })],
            new Dictionary<AiJob, ActiveSelection> { [AiJob.Recipes] = new("luna", Gary, "Gary", added.AddMinutes(5), "gpt-4o") });

        var json = JsonSerializer.Serialize(AiModelFunctions.ToJson(settings, Defaults), WebJson);

        const string Models =
            "\"models\":[{\"id\":\"luna\",\"label\":\"GPT-6 Luna\",\"deployment\":\"gpt-6-luna\",\"temperatureMode\":\"default\",\"jobs\":[\"recipes\"],"
            + "\"addedBy\":\"caf87c93-9030-44e8-a25d-fed63261ea67\",\"addedByName\":\"Gary\",\"addedAt\":\"2026-10-27T09:00:00.123Z\",\"tests\":{\"passed\":true}}]";
        const string Active =
            "\"active\":{\"recipes\":{\"modelId\":\"luna\",\"changedBy\":\"caf87c93-9030-44e8-a25d-fed63261ea67\",\"changedByName\":\"Gary\","
            + "\"changedAt\":\"2026-10-27T09:05:00.123Z\",\"previousModelId\":\"gpt-4o\"},\"photos\":null,\"genericNames\":null}";
        const string DefaultsJson = "\"defaults\":{\"recipes\":\"gpt-4o\",\"photos\":\"gpt-4o\",\"genericNames\":\"gpt-4o\"}";
        Assert.Equal("{" + Models + "," + Active + "," + DefaultsJson + "}", json);
    }

    private static byte[] Body(string text) => Encoding.UTF8.GetBytes(text);
}
