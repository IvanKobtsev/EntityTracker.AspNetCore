using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using EntityTracker.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
namespace EntityTracker.Tests;
public class HubTests
{
    public sealed class Scope : ITrackingScopeResolver
    {
        public ValueTask<string> ResolveAsync(ClaimsPrincipal? user, CancellationToken cancellationToken)
            => ValueTask.FromResult(user!.FindFirst("scope")!.Value);
    }
    private sealed class Client(WebSocket socket) : IDisposable
    {
        private readonly Queue<JsonElement> frames = new();
        private readonly StringBuilder buffered = new();
        public List<JsonElement> Events {get;} = [];
        private async Task Send(object frame) => await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame) + "\u001e"), WebSocketMessageType.Text, true, CancellationToken.None);
        private async Task<JsonElement> Receive()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (frames.Count == 0)
            {
                var buffer = new byte[65536];
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
                buffered.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                var text = buffered.ToString(); var split = text.Split('\u001e');
                foreach (var value in split.SkipLast(1)) if (value.Length > 0) frames.Enqueue(JsonDocument.Parse(value).RootElement.Clone());
                buffered.Clear(); buffered.Append(split[^1]);
            }
            return frames.Dequeue();
        }
        public async Task Handshake() { await Send(new {protocol="json",version=1}); await Receive(); }
        public async Task<string?> Invoke(string method, params string[] names)
        {
            await Send(new {type=1, invocationId="1", target=method, arguments=new object[]{names}});
            while (true)
            {
                var frame = await Receive();
                if (frame.GetProperty("type").GetInt32() == 1) Events.Add(frame);
                if (frame.GetProperty("type").GetInt32() == 3)
                    return frame.TryGetProperty("error", out var error) ? error.GetString() : null;
            }
        }
        public void Dispose() => socket.Dispose();
    }
    [Theory]
    [InlineData("/hubs/EntityTracker", false)]
    [InlineData("/custom/entities", false)]
    [InlineData("/manual/entities", true)]
    public async Task Subscriptions_filter_entity_and_scope_and_reconnect_starts_empty(string path, bool manualMapping)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthorization(); builder.Services.AddAuthentication();
        builder.Services.AddEntityTracker<Scope>(o => { o.HubPath = path; o.Add(new(typeof(HubTests), "A", _=>"1", _=>null)).Add(new(typeof(Scope), "B", _=>"2", _=>null)); });
        await using var app = builder.Build();
        app.Use(async (context, next) => { context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", context.Request.Query["scope"].ToString())], "test")); await next(); });
        app.UseAuthorization();
        if (manualMapping) app.MapEntityTracker(path);
        await app.StartAsync();
        async Task<Client> Connect(string scope)
        {
            var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost"+path+"?scope="+scope), CancellationToken.None);
            var client = new Client(socket); await client.Handshake(); return client;
        }
        using var a = await Connect("one"); using var b = await Connect("two"); using var unsubscribed = await Connect("one");
        Assert.Null(await a.Invoke("Subscribe", "A", "A")); Assert.Null(await b.Invoke("Subscribe", "A"));
        Assert.NotNull(await unsubscribed.Invoke("Subscribe", "A", "Unknown"));
        var publisher = app.Services.GetRequiredService<IEntityTrackerClientFactory>();
        await publisher.Group("EntityTracker:3:one:A").EntitiesChanged(new("A", [new("1", null)], null));
        await publisher.Group("EntityTracker:3:one:B").EntitiesChanged(new("B", [new("2", null)], null));
        await a.Invoke("Subscribe"); await b.Invoke("Subscribe"); await unsubscribed.Invoke("Subscribe");
        Assert.Single(a.Events); Assert.Empty(b.Events); Assert.Empty(unsubscribed.Events);
        await a.Invoke("Unsubscribe", "A"); await a.Invoke("Unsubscribe", "A");
        await publisher.Group("EntityTracker:3:one:A").EntitiesChanged(new("A", [], null));
        await a.Invoke("Subscribe"); Assert.Single(a.Events);
        a.Dispose(); using var reconnected = await Connect("one");
        await publisher.Group("EntityTracker:3:one:A").EntitiesChanged(new("A", [], null));
        await reconnected.Invoke("Subscribe"); Assert.Empty(reconnected.Events);
    }
}
