using System.Text.Json;
using System.Text.Json.Serialization;
using EntityTracker.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EntityTracker.Tests;

[Tracked(Name = "Renamed", PayloadMember = nameof(Payload))]
public class SpecEntity
{
    public int Id { get; set; }
    public SpecPayload? Payload => throw new InvalidOperationException("Spec generation must not invoke getters.");
}
public class SpecPayload
{
    [JsonPropertyName("display_name")]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<SpecChild> Children { get; set; } = [];
    public Dictionary<string, int> Counts { get; set; } = [];
    public SpecPayload? Next { get; set; }
    public SpecState State { get; set; }
    [JsonIgnore] public string Secret => "not in schema";
}
public record SpecChild(int Value);
[JsonConverter(typeof(JsonStringEnumConverter<SpecState>))]
public enum SpecState { Open, Closed }

public class PayloadSpecTests
{
    [Fact]
    public async Task Default_path_supports_empty_registry_and_endpoint_conventions()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddAuthorization(o => o.AddPolicy("SpecReaders", policy => policy.RequireAssertion(_ => true)));
        builder.Services.AddAuthentication();
        builder.Services.AddEntityTracker<HubTests.Scope>(_ => { });
        await using var app = builder.Build(); app.UseAuthorization();
        app.MapEntityTrackerSpec().RequireAuthorization("SpecReaders");
        await app.StartAsync();
        using var document = JsonDocument.Parse(await app.GetTestClient().GetStringAsync("/entity-tracker/spec.json"));
        Assert.Empty(document.RootElement.GetProperty("entities").EnumerateObject());
        Assert.Empty(document.RootElement.GetProperty("payloads").EnumerateObject());
        var endpoint = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints)
            .Single(x => x.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()?.Policy == "SpecReaders");
        Assert.NotNull(endpoint);
    }
    [Fact]
    public async Task Spec_describes_generated_payloads_without_invoking_getters()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddAuthorization(); builder.Services.AddAuthentication();
        builder.Services.AddEntityTracker<HubTests.Scope>(o =>
        {
            EntityTracker.Generated.TrackingSample.GeneratedTrackingRegistration.AddGeneratedTracking(o);
            EntityTracker.Generated.EntityTracker_Tests.GeneratedTrackingRegistration.AddGeneratedTracking(o);
            o.Add(new(typeof(PayloadSpecTests), "NoPayload", _ => "1", _ => null));
        });
        await using var app = builder.Build();
        app.UseAuthorization(); app.MapEntityTrackerSpec("/payloads.json");
        await app.StartAsync();
        using var response = await app.GetTestClient().GetAsync("/payloads.json");
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var entities = root.GetProperty("entities");
        Assert.False(entities.TryGetProperty("SpecEntity", out _));
        var renamed = entities.GetProperty("Renamed");
        Assert.True(renamed.GetProperty("payloadNullable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entities.GetProperty("NoPayload").GetProperty("payloadType").ValueKind);
        var schemas = root.GetProperty("payloads");
        var schema = schemas.GetProperty(renamed.GetProperty("payloadType").GetString()!);
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("display_name", out _));
        Assert.False(properties.TryGetProperty("secret", out _));
        Assert.Contains("null", properties.GetProperty("description").GetProperty("type").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("array", properties.GetProperty("children").GetProperty("type").GetString());
        Assert.True(properties.GetProperty("children").GetProperty("items").GetProperty("properties").TryGetProperty("value", out _));
        Assert.True(properties.GetProperty("counts").TryGetProperty("additionalProperties", out _));
        Assert.Contains("Open", properties.GetProperty("state").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("\"$ref\"", properties.GetProperty("next").GetRawText());
        Assert.StartsWith("urn:entitytracker:payload:", schema.GetProperty("$id").GetString());
        var note = entities.GetProperty("Note");
        Assert.False(note.GetProperty("payloadNullable").GetBoolean());
        Assert.True(schemas.GetProperty(note.GetProperty("payloadType").GetString()!).GetProperty("properties").TryGetProperty("title", out _));
        Assert.Equal(text, await app.GetTestClient().GetStringAsync("/payloads.json"));
    }
}
