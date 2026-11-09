namespace EntityTracker.AspNetCore;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TrackedAttribute : Attribute
{
    public string? Name { get; set; }
    public string? IdMember { get; set; }
    public string? PayloadMember { get; set; }
}
public sealed record EntityChange(string Id, object? Payload);
public sealed record EntityChangeEvent(string EntityType, IReadOnlyList<EntityChange> Changes, object? Metadata);
/// <summary>Resolve an authorized scope, stable for the connection lifetime.</summary>
public interface ITrackingScopeResolver
{
    ValueTask<string> ResolveAsync(System.Security.Claims.ClaimsPrincipal? user, CancellationToken cancellationToken);
}
public interface ITrackingMetadataProvider<TMetadata> where TMetadata : class
{
    TMetadata? GetMetadata();
}
public sealed record TrackingDescriptor(Type EntityType, string Name, Func<object, string> GetId, Func<object, object?> GetPayload,
    Type? PayloadType = null, bool PayloadNullable = true);
public sealed class EntityTrackerOptions
{
    /// <summary>Additional assemblies outside the application's reference graph, such as plugins.</summary>
    public ICollection<System.Reflection.Assembly> Assemblies { get; } = new List<System.Reflection.Assembly>();
    private readonly HashSet<System.Reflection.Assembly> generatedAssemblies = [];
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public bool RegisterGeneratedAssembly(System.Reflection.Assembly assembly) => generatedAssemblies.Add(assembly);
    internal Type HubType { get; private set; } = typeof(EntityTrackerHub);
    public string HubPath { get; set; } = "/hubs/EntityTracker";
    internal Func<IServiceProvider, IEntityTrackerClientFactory> Factory { get; private set; } = sp => new EntityTrackerClientFactory<EntityTrackerHub>(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<EntityTrackerHub>>(sp));
    public void UseHub<THub>() where THub : Microsoft.AspNetCore.SignalR.Hub
    {
        HubType = typeof(THub);
        Factory = sp => new EntityTrackerClientFactory<THub>(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<THub>>(sp));
    }
    internal List<TrackingDescriptor> Descriptors { get; } = [];
    public EntityTrackerOptions Add(TrackingDescriptor descriptor) { Descriptors.Add(descriptor); return this; }
}
public sealed class TrackingRegistry
{
    public Type? MetadataType { get; internal set; }
    public Type HubType { get; }
    public string HubPath { get; }
    internal bool EndpointMapped { get; set; }
    private readonly Dictionary<Type, TrackingDescriptor> descriptors;
    public IReadOnlyCollection<TrackingDescriptor> Descriptors => descriptors.Values.ToArray();
    public TrackingRegistry(EntityTrackerOptions options)
    {
        HubType = options.HubType;
        HubPath = options.HubPath;
        if (string.IsNullOrWhiteSpace(HubPath) || !HubPath.StartsWith('/')) throw new ArgumentException("HubPath must start with '/'.");
        var duplicate = options.Descriptors.GroupBy(x => x.Name, StringComparer.Ordinal).FirstOrDefault(x => x.Count() > 1);
        if (duplicate != null) throw new InvalidOperationException($"Duplicate tracked entity name: {duplicate.Key}");
        if (options.Descriptors.Any(x => string.IsNullOrWhiteSpace(x.Name))) throw new InvalidOperationException("Entity names cannot be empty.");
        descriptors = options.Descriptors.ToDictionary(x => x.EntityType);
    }
    public bool ContainsName(string name) => descriptors.Values.Any(x => x.Name == name);
    public TrackingDescriptor? Find(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (descriptors.TryGetValue(current, out var descriptor)) return descriptor;
        return null;
    }
}
