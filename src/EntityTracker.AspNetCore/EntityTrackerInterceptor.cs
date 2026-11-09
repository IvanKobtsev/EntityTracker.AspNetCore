using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
namespace EntityTracker.AspNetCore;

/// <summary>Tracks SaveChanges and relational transaction commits. Do not use with pooled contexts.</summary>
public sealed class EntityTrackerInterceptor(
    TrackingRegistry registry, ITrackingScopeResolver scopes,
    IEntityTrackerClientFactory hub,
    ILogger<EntityTrackerInterceptor> logger,
    IServiceProvider services,
    IHttpContextAccessor? http = null) : DbTransactionInterceptor, ISaveChangesInterceptor
{
    private sealed class State
    {
        public List<(object Entity, TrackingDescriptor Descriptor)> Saving = [];
        public Dictionary<(string Scope, string Name, string Id), EntityChange> Pending = [];
        public object? Metadata;
        public Guid? TransactionId;
    }
    private readonly ConditionalWeakTable<DbContext, State> states = new();
    private State Get(DbContext context) => states.GetOrCreateValue(context);
    private void Capture(DbContext context)
    {
        if (System.Transactions.Transaction.Current != null)
            throw new NotSupportedException("Ambient transactions are not supported by EntityTracker.");
        var state = Get(context);
        if (state.TransactionId != context.Database.CurrentTransaction?.TransactionId)
        {
            state.Pending.Clear();
            state.TransactionId = context.Database.CurrentTransaction?.TransactionId;
        }
        state.Saving.Clear();
        context.ChangeTracker.DetectChanges();
        var entries = context.ChangeTracker.Entries().ToArray();
        var selected = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries.Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var owner = entry;
            while (owner.Metadata.FindOwnership() is { } ownership)
            {
                var values = ownership.Properties.Select(p => owner.Property(p.Name).CurrentValue).ToArray();
                var principal = entries.FirstOrDefault(e => ownership.PrincipalEntityType.IsAssignableFrom(e.Metadata)
                    && ownership.PrincipalKey.Properties.Select(p => e.Property(p.Name).CurrentValue).SequenceEqual(values));
                if (principal == null) break;
                owner = principal;
            }
            if (registry.Find(owner.Metadata.ClrType) is { } descriptor && selected.Add(owner.Entity))
                state.Saving.Add((owner.Entity, descriptor));
        }
    }
    private async Task Saved(DbContext context, CancellationToken cancellationToken)
    {
        var state = Get(context);
        try
        {
            if (state.Saving.Count == 0) return;
            var scope = await scopes.ResolveAsync(http?.HttpContext?.User, cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(scope);
            foreach (var (entity, descriptor) in state.Saving)
            {
                var id = descriptor.GetId(entity);
                if (id == null) throw new InvalidOperationException("Tracking IDs cannot be null.");
                state.Pending[(scope, descriptor.Name, id)] = new(id, descriptor.GetPayload(entity));
            }
            state.Metadata = (services.GetService(typeof(ITrackingMetadataAccessor)) as ITrackingMetadataAccessor)?.GetMetadata();
            if (context.Database.CurrentTransaction == null) await Publish(context);
        }
        catch (Exception exception)
        {
            state.Pending.Clear();
            logger.LogError(exception, "Unable to prepare entity notifications after saving.");
        }
        finally { state.Saving.Clear(); }
    }
    private async Task Publish(DbContext context)
    {
        var state = Get(context);
        var pending = state.Pending.ToArray();
        state.Pending.Clear();
        foreach (var group in pending.GroupBy(x => (x.Key.Scope, x.Key.Name)))
            try
            {
                await hub.Group(EntityTrackerHub.Group(group.Key.Scope, group.Key.Name))
                    .EntitiesChanged(new(group.Key.Name, group.Select(x => x.Value).ToArray(), state.Metadata));
            }
            catch (Exception exception) { logger.LogError(exception, "Unable to deliver entity notifications."); }
    }
    private void Clear(DbContext? context) { if (context != null) states.Remove(context); }
    public InterceptionResult<int> SavingChanges(DbContextEventData e, InterceptionResult<int> result) { Capture(e.Context!); return result; }
    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Capture(e.Context!); return ValueTask.FromResult(result); }
    public int SavedChanges(SaveChangesCompletedEventData e, int result) { Saved(e.Context!, default).GetAwaiter().GetResult(); return result; }
    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData e, int result, CancellationToken cancellationToken = default)
    { await Saved(e.Context!, cancellationToken); return result; }
    public void SaveChangesFailed(DbContextErrorEventData e) => Clear(e.Context);
    public Task SaveChangesFailedAsync(DbContextErrorEventData e, CancellationToken cancellationToken = default) { Clear(e.Context); return Task.CompletedTask; }
    public void SaveChangesCanceled(DbContextEventData e) => Clear(e.Context);
    public Task SaveChangesCanceledAsync(DbContextEventData e, CancellationToken cancellationToken = default) { Clear(e.Context); return Task.CompletedTask; }
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData e) { if (e.Context != null) Publish(e.Context).GetAwaiter().GetResult(); }
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData e, CancellationToken cancellationToken = default)
        => e.Context == null ? Task.CompletedTask : Publish(e.Context);
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData e) => Clear(e.Context);
    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData e, CancellationToken cancellationToken = default) { Clear(e.Context); return Task.CompletedTask; }
    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData e) => Clear(e.Context);
    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData e, CancellationToken cancellationToken = default) { Clear(e.Context); return Task.CompletedTask; }
}
