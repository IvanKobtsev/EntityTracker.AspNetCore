using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
namespace EntityTracker.AspNetCore;
internal interface ITrackingMetadataAccessor { object? GetMetadata(); }
internal sealed class TrackingMetadataAccessor<TMetadata>(ITrackingMetadataProvider<TMetadata> provider,
    IOptions<JsonHubProtocolOptions> options) : ITrackingMetadataAccessor where TMetadata : class
{
    public object? GetMetadata()
    {
        var value = provider.GetMetadata();
        return value is null ? null : JsonSerializer.SerializeToElement(value, typeof(TMetadata), options.Value.PayloadSerializerOptions);
    }
}
