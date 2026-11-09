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
    public sealed class SpecMetadataProvider : ITrackingMetadataProvider<SpecPayload>
    {
        public SpecMetadataProvider() => throw new InvalidOperationException("Must not construct provider for spec.");
        public SpecPayload? GetMetadata() => throw new InvalidOperationException();
    }
    [Fact]
    public async Task Metadata_schema_uses_declared_type_without_resolving_provider()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddEntityTracker<HubTests.Scope, SpecMetadataProvider>();
        builder.Services.Configure<Microsoft.AspNetCore.SignalR.JsonHubProtocolOptions>(o =>
            o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        await using var app = builder.Build();
        app.MapEntityTrackerSpec(); await app.StartAsync();
        using var doc = JsonDocument.Parse(await app.GetTestClient().GetStringAsync("/entity-tracker/spec.json"));
        var metadata = doc.RootElement.GetProperty("eventMetadata");
        Assert.Equal("SpecPayload", metadata.GetProperty("type").GetString());
        Assert.True(metadata.GetProperty("nullable").GetBoolean());
        AssertPayload(doc.RootElement);
    }

    [Fact]
    public async Task Default_path_supports_discovery_and_endpoint_conventions()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddAuthorization(o => o.AddPolicy("SpecReaders", policy => policy.RequireAssertion(_ => true)));
        builder.Services.AddAuthentication();
        builder.Services.AddEntityTracker<HubTests.Scope>();
        await using var app = builder.Build(); app.UseAuthorization();
        app.MapEntityTrackerSpec().RequireAuthorization("SpecReaders");
        await app.StartAsync();
        using var document = JsonDocument.Parse(await app.GetTestClient().GetStringAsync("/entity-tracker/spec.json"));
        Assert.True(document.RootElement.GetProperty("entities").TryGetProperty("Note", out _));
        Assert.NotEmpty(document.RootElement.GetProperty("payloads").EnumerateObject());
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
        Assert.Equal(3, root.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("eventMetadata").ValueKind);
        var entities = root.GetProperty("entities");
        Assert.False(entities.TryGetProperty("SpecEntity", out _));
        var renamed = entities.GetProperty("Renamed");
        Assert.True(renamed.GetProperty("payloadNullable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entities.GetProperty("NoPayload").GetProperty("payloadType").ValueKind);
        Assert.Equal("SpecPayload", renamed.GetProperty("payloadType").GetString());
        Assert.Contains("Renamed", root.GetProperty("types").EnumerateArray().Select(x => x.GetString()));
        AssertPayload(root);
        Assert.Equal(text, await app.GetTestClient().GetStringAsync("/payloads.json"));
    }
    private static void AssertPayload(JsonElement root)
    {
        Assert.Contains("automated client", root.GetProperty("metadata").GetProperty("description").GetString());
        var payloads = root.GetProperty("payloads");
        var properties = payloads.GetProperty("SpecPayload").GetProperty("properties").EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("type").GetString());
        Assert.Equal("string", properties["display_name"]);
        Assert.False(properties.ContainsKey("secret"));
        Assert.Equal("string | null | undefined", properties["description"]);
        Assert.Equal("SpecChild[]", properties["children"]);
        Assert.Equal("{ [key: string]: number }", properties["counts"]);
        Assert.Equal("SpecPayload | null | undefined", properties["next"]);
        Assert.Contains("Open", properties["state"]);
        Assert.Equal("value", payloads.GetProperty("SpecChild").GetProperty("properties")[0].GetProperty("name").GetString());
    }
}
