using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using EntityTracker.AspNetCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace EntityTracker.Tests;
public class TrackingTests
{
    public sealed class Item { public int Id { get; set; } public string Name { get; set; } = "initial"; public Detail Detail {get;set;} = new(); }
    public sealed class Detail { public string Value {get;set;} = "a"; }
    private sealed class Database(DbContextOptions<Database> options) : DbContext(options)
    {
        public DbSet<Item> Items => Set<Item>();
        protected override void OnModelCreating(ModelBuilder model) => model.Entity<Item>().OwnsOne(x=>x.Detail);
    }
    private sealed class Scope : ITrackingScopeResolver
    { public ValueTask<string> ResolveAsync(ClaimsPrincipal? user, CancellationToken cancellationToken) => ValueTask.FromResult("tenant"); }
    private sealed class Sink : IEntityTrackerClientFactory, IEntityTrackerClient
    {
        public List<(string Group, EntityChangeEvent Event)> Events {get;} = [];
        private string current = "";
        public IEntityTrackerClient Group(string group) { current = group; return this; }
        public Task Subscribe(string[] names) => Task.CompletedTask;
        public Task Unsubscribe(string[] names) => Task.CompletedTask;
        public Task EntitiesChanged(EntityChangeEvent change) { Events.Add((current, change)); return Task.CompletedTask; }
    }
    private static (SqliteConnection Connection, Database Db, Sink Sink) Open(IServiceProvider? services = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        var options = new EntityTrackerOptions().Add(new(typeof(Item), "Item", x => ((Item)x).Id.ToString(), x => ((Item)x).Name));
        var sink = new Sink();
        var interceptor = new EntityTrackerInterceptor(new(options), new Scope(), sink, NullLogger<EntityTrackerInterceptor>.Instance, services ?? new ServiceCollection().BuildServiceProvider());
        var db = new Database(new DbContextOptionsBuilder<Database>().UseSqlite(connection).AddInterceptors(interceptor).Options);
        db.Database.EnsureCreated();
        return (connection, db, sink);
    }
    public sealed record EventMetadata([property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string Session);
    public sealed class EventMetadataProvider : ITrackingMetadataProvider<EventMetadata>
    {
        public EventMetadata? Value { get; set; } = new("initial");
        public EventMetadata? GetMetadata() => Value;
    }
    [Fact]
    public async Task Typed_metadata_captures_latest_save_and_allows_null()
    {
        var registrations = new ServiceCollection(); registrations.AddLogging();
        registrations.AddEntityTracker<Scope, EventMetadataProvider>();
        using var provider = registrations.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var metadata = (EventMetadataProvider)scope.ServiceProvider.GetRequiredService<ITrackingMetadataProvider<EventMetadata>>();
        var (connection, db, sink) = Open(scope.ServiceProvider); using var c = connection; await using var d = db;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var item = new Item(); db.Add(item); await db.SaveChangesAsync();
            metadata.Value = new("latest"); item.Name = "changed"; await db.SaveChangesAsync();
            Assert.Empty(sink.Events); await transaction.CommitAsync();
        }
        var json = Assert.IsType<System.Text.Json.JsonElement>(Assert.Single(sink.Events).Event.Metadata);
        Assert.Equal("latest", json.GetProperty("session_id").GetString());
        metadata.Value = null; db.Add(new Item()); await db.SaveChangesAsync();
        Assert.Null(sink.Events[1].Event.Metadata);
    }
    [Fact]
    public async Task Generated_ids_and_owned_changes_are_published()
    {
        var (connection, db, sink) = Open(); using var c = connection; await using var d = db;
        var item = new Item(); db.Add(item); await db.SaveChangesAsync();
        Assert.Equal(item.Id.ToString(), Assert.Single(Assert.Single(sink.Events).Event.Changes).Id);
        Assert.NotEqual(0, item.Id);
        item.Detail.Value = "b"; await db.SaveChangesAsync();
        Assert.Equal(2, sink.Events.Count);
        db.Remove(item); await db.SaveChangesAsync(); Assert.Equal(3, sink.Events.Count);
    }
    [Fact]
    public async Task Commit_deduplicates_and_retains_latest_payload()
    {
        var (connection, db, sink) = Open(); using var c = connection; await using var d = db;
        await using var transaction = await db.Database.BeginTransactionAsync();
        var item = new Item(); db.Add(item); await db.SaveChangesAsync();
        item.Name = "latest"; await db.SaveChangesAsync();
        Assert.Empty(sink.Events); await transaction.CommitAsync();
        Assert.Equal("latest", Assert.Single(Assert.Single(sink.Events).Event.Changes).Payload);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Rollback_or_disposal_does_not_leak_changes(bool rollback)
    {
        var (connection, db, sink) = Open(); using var c = connection; await using var d = db;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Add(new Item()); await db.SaveChangesAsync();
            if (rollback) await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear(); db.Add(new Item { Name = "committed" }); await db.SaveChangesAsync();
        Assert.Equal("committed", Assert.Single(Assert.Single(sink.Events).Event.Changes).Payload);
    }
    [Fact]
    public void Duplicate_names_are_rejected()
    {
        var options = new EntityTrackerOptions().Add(new(typeof(Item), "Same", _=>"1", _=>null)).Add(new(typeof(Detail), "Same", _=>"2", _=>null));
        Assert.Throws<InvalidOperationException>(()=>new TrackingRegistry(options));
    }
    [Fact]
    public async Task Failed_save_clears_pending_changes()
    {
        var (connection, db, sink) = Open(); using var c = connection; await using var d = db;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Add(new Item()); await db.SaveChangesAsync();
            db.Add(new Item { Name = null! });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear(); db.Add(new Item { Name = "good" }); await db.SaveChangesAsync();
        Assert.Equal("good", Assert.Single(Assert.Single(sink.Events).Event.Changes).Payload);
    }
    [Fact]
    public async Task Cancellation_does_not_leak_pending_changes()
    {
        var (connection, db, sink) = Open(); using var c = connection; await using var d = db;
        db.Add(new Item { Name = "canceled" });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(new CancellationToken(true)));
        Assert.Empty(sink.Events);
        db.ChangeTracker.Clear(); db.Add(new Item { Name = "good" }); db.SaveChanges();
        Assert.Equal("good", Assert.Single(Assert.Single(sink.Events).Event.Changes).Payload);
    }
    [Fact]
    public async Task Shared_interceptor_keeps_context_state_isolated()
    {
        var options = new EntityTrackerOptions().Add(new(typeof(Item), "Item", x => ((Item)x).Id.ToString(), x => ((Item)x).Name));
        var sink = new Sink();
        var interceptor = new EntityTrackerInterceptor(new(options), new Scope(), sink, NullLogger<EntityTrackerInterceptor>.Instance, new ServiceCollection().BuildServiceProvider());
        using var firstConnection = new SqliteConnection("Data Source=:memory:"); firstConnection.Open();
        using var secondConnection = new SqliteConnection("Data Source=:memory:"); secondConnection.Open();
        await using var first = new Database(new DbContextOptionsBuilder<Database>().UseSqlite(firstConnection).AddInterceptors(interceptor).Options);
        await using var second = new Database(new DbContextOptionsBuilder<Database>().UseSqlite(secondConnection).AddInterceptors(interceptor).Options);
        first.Database.EnsureCreated(); second.Database.EnsureCreated();
        await using var transaction = await first.Database.BeginTransactionAsync();
        first.Add(new Item { Name = "pending" }); await first.SaveChangesAsync();
        second.Add(new Item { Name = "independent" }); await second.SaveChangesAsync();
        Assert.Equal("independent", Assert.Single(Assert.Single(sink.Events).Event.Changes).Payload);
        await transaction.RollbackAsync(); Assert.Single(sink.Events);
    }
}
