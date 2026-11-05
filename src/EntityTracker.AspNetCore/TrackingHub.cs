using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
namespace EntityTracker.AspNetCore;
public interface IEntityTrackerClient
{
    Task Subscribe(string[] entityNames);
    Task Unsubscribe(string[] entityNames);
    Task EntitiesChanged(EntityChangeEvent change);
}
[Authorize]
public class EntityTrackerHub(TrackingRegistry registry, ITrackingScopeResolver scopes) : EntityTrackerHub<IEntityTrackerClient>(registry, scopes);

[Authorize]
public class EntityTrackerHub<TClient>(TrackingRegistry registry, ITrackingScopeResolver scopes) : Hub<TClient> where TClient : class
{
    private const string ScopeKey = "EntityTracker.Scope";
    public override async Task OnConnectedAsync()
    {
        var scope = await scopes.ResolveAsync(Context.User, Context.ConnectionAborted);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        Context.Items[ScopeKey] = scope;
        await base.OnConnectedAsync();
    }
    public Task Subscribe(string[] entityNames) => Update(entityNames, true);
    public Task Unsubscribe(string[] entityNames) => Update(entityNames, false);
    private async Task Update(string[] names, bool subscribe)
    {
        if (names == null || names.Any(x => x == null || !registry.ContainsName(x))) throw new HubException("Unknown entity name.");
        if (Context.Items[ScopeKey] is not string scope) throw new HubException("Tracking scope is unavailable.");
        foreach (var name in names.Distinct(StringComparer.Ordinal))
            if (subscribe) await Groups.AddToGroupAsync(Context.ConnectionId, Group(scope, name), Context.ConnectionAborted);
            else await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(scope, name), Context.ConnectionAborted);
    }
    internal static string Group(string scope, string name) => $"EntityTracker:{scope.Length}:{scope}:{name}";
}
