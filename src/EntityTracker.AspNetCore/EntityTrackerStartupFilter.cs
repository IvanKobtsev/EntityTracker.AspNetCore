using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
namespace EntityTracker.AspNetCore;

internal sealed class EntityTrackerStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseRouting();
        next(app);
        var registry = app.ApplicationServices.GetRequiredService<TrackingRegistry>();
        if (!registry.EndpointMapped)
            app.UseEndpoints(endpoints => endpoints.MapEntityTracker(registry.HubPath));
    };
}
