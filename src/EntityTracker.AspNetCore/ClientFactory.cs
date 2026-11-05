using Microsoft.AspNetCore.SignalR;
namespace EntityTracker.AspNetCore;
public interface IEntityTrackerClientFactory
{
    IEntityTrackerClient Group(string group);
}
internal sealed class EntityTrackerClientFactory<THub>(IHubContext<THub> context) : IEntityTrackerClientFactory where THub : Hub
{
    public IEntityTrackerClient Group(string group) => new Client(context.Clients.Group(group));
    private sealed class Client(IClientProxy proxy) : IEntityTrackerClient
    {
        public Task EntitiesChanged(EntityChangeEvent change) => proxy.SendCoreAsync(nameof(EntitiesChanged), [change]);
        public Task Subscribe(string[] entityNames) => throw new NotSupportedException("Subscribe is a client-to-server method.");
        public Task Unsubscribe(string[] entityNames) => throw new NotSupportedException("Unsubscribe is a client-to-server method.");
    }
}
