using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
namespace EntityTracker.AspNetCore;
public static class EntityTrackerExtensions
{
    /// <summary>Maps a payload specification generated once using SignalR's JSON serialization settings.</summary>
    public static IEndpointConventionBuilder MapEntityTrackerSpec(this IEndpointRouteBuilder endpoints, string pattern = "/entity-tracker/spec.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var registry = endpoints.ServiceProvider.GetRequiredService<TrackingRegistry>();
        var serializer = endpoints.ServiceProvider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;
        var json = PayloadSpecification.Generate(registry, serializer);
        return endpoints.MapGet(pattern, () => Results.Bytes(json, "application/json"));
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static IServiceCollection AddEntityTracker<TScope>(this IServiceCollection services, Action<EntityTrackerOptions>? configure = null)
        where TScope : class, ITrackingScopeResolver
        => AddEntityTrackerCore<TScope>(services, configure, System.Reflection.Assembly.GetCallingAssembly());

    /// <summary>Registers tracking with scoped implementations for audience resolution and notification metadata.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static IServiceCollection AddEntityTracker<TScope, TMetadata>(this IServiceCollection services, Action<EntityTrackerOptions>? configure = null)
        where TScope : class, ITrackingScopeResolver
        where TMetadata : class, ITrackingMetadataProvider
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(
            services, ServiceDescriptor.Scoped<ITrackingMetadataProvider, TMetadata>());
        return AddEntityTrackerCore<TScope>(services, configure, System.Reflection.Assembly.GetCallingAssembly());
    }

    private static IServiceCollection AddEntityTrackerCore<TScope>(IServiceCollection services, Action<EntityTrackerOptions>? configure, System.Reflection.Assembly callingAssembly)
        where TScope : class, ITrackingScopeResolver
    {
        var options = new EntityTrackerOptions(); configure?.Invoke(options);
        services.AddSingleton(sp =>
        {
            var roots = new List<System.Reflection.Assembly> { callingAssembly, typeof(TScope).Assembly };
            var applicationName = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.ApplicationName;
            if (!string.IsNullOrWhiteSpace(applicationName)) roots.Add(System.Reflection.Assembly.Load(applicationName));
            GeneratedTrackingDiscovery.Register(options, roots);
            return new TrackingRegistry(options);
        });
        services.AddSingleton(options.Factory);
        services.AddHttpContextAccessor();
        services.AddScoped<ITrackingScopeResolver, TScope>();
        services.AddScoped<EntityTrackerInterceptor>();
        services.AddSignalR();
        services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, EntityTrackerStartupFilter>();
        return services;
    }
    public static DbContextOptionsBuilder UseEntityTracker(this DbContextOptionsBuilder options, IServiceProvider services)
        => options.AddInterceptors(services.GetRequiredService<EntityTrackerInterceptor>());
    public static HubEndpointConventionBuilder MapEntityTracker(this IEndpointRouteBuilder endpoints, string pattern = "/hubs/EntityTracker")
    {
        var hub = endpoints.ServiceProvider.GetRequiredService<TrackingRegistry>().HubType;
        endpoints.ServiceProvider.GetRequiredService<TrackingRegistry>().EndpointMapped = true;
        var method = typeof(HubEndpointRouteBuilderExtensions).GetMethods().Single(m => m.Name == "MapHub" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);
        return (HubEndpointConventionBuilder)method.MakeGenericMethod(hub).Invoke(null, [endpoints, pattern])!;
    }
}
