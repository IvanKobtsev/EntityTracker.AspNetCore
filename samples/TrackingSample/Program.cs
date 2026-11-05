using System.Security.Claims;
using EntityTracker.AspNetCore;
using EntityTracker.Generated.TrackingSample;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthorization();
builder.Services.AddAuthentication("Bearer").AddJwtBearer();
builder.Services.AddEntityTracker<SampleScope>(o => o.AddGeneratedTracking());
builder.Services.AddDbContext<SampleDatabase>((sp, o) => o.UseSqlite("Data Source=tracking.db").UseEntityTracker(sp));
var app = builder.Build();
using (var scope = app.Services.CreateScope()) scope.ServiceProvider.GetRequiredService<SampleDatabase>().Database.EnsureCreated();
app.UseAuthentication(); app.UseAuthorization();
app.MapEntityTrackerSpec().RequireAuthorization();
app.MapPost("/notes", async (string title, SampleDatabase db) =>
{
    var note = new Note { Title = title }; db.Add(note); await db.SaveChangesAsync(); return Results.Ok(note.Id);
}).RequireAuthorization();
app.Run();

[Tracked(PayloadMember = nameof(TrackingPayload))]
public class Note
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public NotePayload TrackingPayload => new(Title);
}
public sealed record NotePayload(string Title);
public sealed class SampleDatabase(DbContextOptions<SampleDatabase> options) : DbContext(options)
{
    public DbSet<Note> Notes => Set<Note>();
}
public sealed class SampleScope : ITrackingScopeResolver
{
    public ValueTask<string> ResolveAsync(ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (user?.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Authentication is required.");
        var tenantId = user.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new UnauthorizedAccessException("A validated tenant_id claim is required.");
        return ValueTask.FromResult($"tenant:{tenantId}");
    }
}
