using System.Reflection;

namespace EntityTracker.AspNetCore;

/// <summary>Assembly marker emitted by the bundled generator.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class GeneratedTrackingRegistrationAttribute(Type providerType) : Attribute
{
    public Type ProviderType { get; } = providerType;
}

/// <summary>Registration contract implemented by generated assembly providers.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public interface IGeneratedTrackingProvider
{
    void Register(EntityTrackerOptions options);
}

internal static class GeneratedTrackingDiscovery
{
    public static void Register(EntityTrackerOptions options, IEnumerable<Assembly> roots)
    {
        var visited = new HashSet<Assembly>();
        var pending = new Queue<Assembly>(roots.Concat(options.Assemblies));
        while (pending.TryDequeue(out var assembly))
        {
            if (!visited.Add(assembly)) continue;
            if (assembly.GetCustomAttribute<GeneratedTrackingRegistrationAttribute>() is { } marker)
            {
                var provider = Activator.CreateInstance(marker.ProviderType) as IGeneratedTrackingProvider
                    ?? throw new InvalidOperationException($"Invalid generated tracking provider in {assembly.FullName}.");
                provider.Register(options);
            }
            foreach (var reference in assembly.GetReferencedAssemblies().OrderBy(x => x.FullName, StringComparer.Ordinal))
            {
                // Optional references may not be deployed when the host never uses their types.
                try { pending.Enqueue(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly)!.LoadFromAssemblyName(reference)); }
                catch (FileNotFoundException) { }
            }
        }
    }
}
