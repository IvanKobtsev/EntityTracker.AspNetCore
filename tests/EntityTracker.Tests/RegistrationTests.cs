using EntityTracker.AspNetCore;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace EntityTracker.Tests;

public class RegistrationTests
{
    public sealed class Metadata : ITrackingMetadataProvider
    {
        public IReadOnlyDictionary<string, string>? GetMetadata() => new Dictionary<string, string> { ["session"] = "example" };
    }

    [Fact]
    public void Metadata_overload_registers_scoped_provider_and_preserves_discovery_and_options()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddScoped<ITrackingMetadataProvider, Metadata>();
        services.AddEntityTracker<HubTests.Scope, Metadata>(o => o.HubPath = "/changes");
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope(); using var second = provider.CreateScope();
        var metadata = Assert.IsType<Metadata>(Assert.Single(first.ServiceProvider.GetServices<ITrackingMetadataProvider>()));
        Assert.Same(metadata, first.ServiceProvider.GetRequiredService<ITrackingMetadataProvider>());
        Assert.NotSame(metadata, second.ServiceProvider.GetRequiredService<ITrackingMetadataProvider>());
        Assert.IsType<HubTests.Scope>(first.ServiceProvider.GetRequiredService<ITrackingScopeResolver>());
        var registry = provider.GetRequiredService<TrackingRegistry>();
        Assert.Equal("/changes", registry.HubPath);
        Assert.NotNull(registry.Find(typeof(SpecEntity)));
        Assert.NotNull(registry.Find(typeof(Note)));
    }
    [Fact]
    public void Generated_registration_selects_hub_and_entity_adapter()
    {
        var options = new EntityTrackerOptions();
        EntityTracker.Generated.TrackingSample.GeneratedTrackingRegistration.AddGeneratedTracking(options);
        var registry = new TrackingRegistry(options);
        Assert.Equal(typeof(EntityTrackerHub), registry.HubType);
        var descriptor = Assert.IsType<TrackingDescriptor>(registry.Find(typeof(Note)));
        var note = new Note { Id = 42, Title = "Example" };
        Assert.Equal("Note", descriptor.Name);
        Assert.Equal("42", descriptor.GetId(note));
        Assert.Equal(new NotePayload("Example"), descriptor.GetPayload(note));
        Assert.Equal(typeof(NotePayload), descriptor.PayloadType);
        Assert.False(descriptor.PayloadNullable);
    }
    [Fact]
    public void Discovers_application_and_referenced_entities_without_generated_calls()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEntityTracker<HubTests.Scope>();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<TrackingRegistry>();
        Assert.NotNull(registry.Find(typeof(SpecEntity)));
        Assert.NotNull(registry.Find(typeof(Note)));
        Assert.Equal(typeof(NotePayload), registry.Find(typeof(Note))!.PayloadType);
        Assert.Equal(typeof(EntityTrackerHub), registry.HubType);
    }
    [Fact]
    public void Explicit_assemblies_and_legacy_calls_do_not_duplicate_discovery()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddEntityTracker<HubTests.Scope>(o =>
        {
            o.Assemblies.Add(typeof(Note).Assembly);
            o.Assemblies.Add(typeof(Note).Assembly);
            EntityTracker.Generated.TrackingSample.GeneratedTrackingRegistration.AddGeneratedTracking(o);
        });
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<TrackingRegistry>();
        Assert.Single(registry.Descriptors, x => x.EntityType == typeof(Note));
    }
}
