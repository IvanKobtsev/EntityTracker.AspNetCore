using EntityTracker.AspNetCore;
using Xunit;

namespace EntityTracker.Tests;

public class RegistrationTests
{
    [Fact]
    public void Generated_registration_selects_hub_and_entity_adapter()
    {
        var options = new EntityTrackerOptions();
        EntityTracker.Generated.TrackingSample.GeneratedTrackingRegistration.AddGeneratedTracking(options);
        var registry = new TrackingRegistry(options);
        Assert.Equal(typeof(EntityTracker.Generated.TrackingSample.GeneratedEntityTrackerHub), registry.HubType);
        var descriptor = Assert.IsType<TrackingDescriptor>(registry.Find(typeof(Note)));
        var note = new Note { Id = 42, Title = "Example" };
        Assert.Equal("Note", descriptor.Name);
        Assert.Equal("42", descriptor.GetId(note));
        Assert.Equal(new NotePayload("Example"), descriptor.GetPayload(note));
        Assert.Equal(typeof(NotePayload), descriptor.PayloadType);
        Assert.False(descriptor.PayloadNullable);
    }
}
