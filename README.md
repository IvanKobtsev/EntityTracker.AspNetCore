# EntityTracker.AspNetCore

Attribute-driven EF Core change notifications for .NET 10. Clients explicitly subscribe to entity names, and receive notifications only within their host-authorized scope.

Install `EntityTracker.AspNetCore`. The package includes its source generator as a Roslyn analyzer; no separate generator package is required. Domain classes need neither interfaces nor `partial` declarations.

For repository contributors: `src/EntityTracker.Generator` is an internal build project, not a separate package. It compiles the analyzer DLL bundled in `EntityTracker.AspNetCore`; keep this project in the solution and source tree.

```csharp
[Tracked]
public class Note
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}
```

The entity name defaults to `Note`; IDs become invariant strings. An inherited readable `Id` is supported. To override conventions:

```csharp
[Tracked(Name = "Case", IdMember = nameof(GetTrackingId),
    PayloadMember = nameof(GetTrackingPayload))]
public class TestCase
{
    public int ProjectId { get; set; }
    public int Number { get; set; }
    public string GetTrackingId() => $"{ProjectId}:{Number}";
    public CasePayload GetTrackingPayload() => new(Number);
}
public sealed record CasePayload(int Number);
```

`IdMember` and `PayloadMember` accept readable properties or parameterless instance methods accessible from generated adapters. Default IDs support nonnullable string, integer, and GUID values. Custom/composite IDs require a string-returning member. No payload is emitted unless selected. Mark computed payload properties `[NotMapped]` when EF would otherwise map them. Invalid configurations produce compiler error `ET001`.

## Host setup

Tracked entities are discovered automatically in the application and its referenced assemblies.

```csharp
using EntityTracker.AspNetCore;


builder.Services.AddEntityTracker<MyScopeResolver>();
builder.Services.AddDbContext<MyDatabase>((sp, options) =>
    options.UseSqlite(connectionString).UseEntityTracker(sp));

// Configure your application's authentication and authorization as usual.
app.UseAuthentication();
app.UseAuthorization();
// The authorized hub is mapped automatically at /hubs/EntityTracker.
```

Implement `ITrackingScopeResolver.ResolveAsync` to return an authorized scope from the current user or the host's background-job context. Never trust an unvalidated scope supplied by a client. The resolver is scoped and also runs during publication; background jobs must establish their authorized scope. A connection's scope is fixed at connect time; reconnect to change it.

The library registers its own hub. No hub class or endpoint mapping is required from the host. Set `o.HubPath = "/realtime/entities"` inside `AddEntityTracker` to change the route. `MapEntityTracker` remains available for explicit mapping and endpoint conventions; using it suppresses automatic mapping.

### Choose the hub path

To customize the automatically mapped route:

```csharp
builder.Services.AddEntityTracker<MyScopeResolver>(o =>
{
    o.HubPath = "/realtime/entities";
});
```

Or map it explicitly alongside your other endpoints, after `Build`:

```csharp
app.MapEntityTracker("/realtime/entities")
    .RequireAuthorization("EntityUpdates");
```

Explicit mapping replaces the automatic endpoint; the default route is not also exposed. The hub requires authentication in either case. Clients connect to the chosen path.

Referenced entity assemblies are discovered recursively, even before their types have been used. For plugins outside that graph, add `options.Assemblies.Add(typeof(PluginEntity).Assembly)` in the configuration callback. Each generated assembly is registered once; conflicting entity names across assemblies are rejected at startup.

To include notification metadata, declare a DTO and pass its provider alongside the scope resolver:

```csharp
public sealed record TrackingMetadata(string? UserId, string? SessionId);
public sealed class MyTrackingMetadataProvider : ITrackingMetadataProvider<TrackingMetadata>
{
    public TrackingMetadata? GetMetadata() => new("example-user", "example-session");
}

builder.Services.AddEntityTracker<MyScopeResolver, MyTrackingMetadataProvider>();
// A configuration callback is supported by both overloads:
builder.Services.AddEntityTracker<MyScopeResolver, MyTrackingMetadataProvider>(o =>
    o.HubPath = "/realtime/entities");
```

Choose one registration above. The provider is scoped and may inject request/job services. It implements `ITrackingMetadataProvider<TrackingMetadata>` and returns the declared DTO or null. The DTO type is inferred from the provider?s single `ITrackingMetadataProvider<TMetadata>` interface, including inherited implementations, without constructing the provider. Register tracking once with one provider; compose multiple contributions inside that provider. Metadata does not affect scope or subscriptions. The scope-only overload remains available.

## Scope resolver examples

The same resolver runs when a connection is established and when EF saves changes. Both must produce exactly the same scope string for a notification to reach a subscriber. A resolver determines the audience for changes, so choose a scope that matches who may see the entity IDs and payloads.

### Tenant scope from authenticated claims

Use a tenant claim issued by a trusted identity provider after membership validation. Validate JWT issuer, signature, and audience through the host's authentication configuration. Reading a claim does not itself validate membership.

```csharp
using System.Security.Claims;
using EntityTracker.AspNetCore;

public sealed class TenantScopeResolver : ITrackingScopeResolver
{
    public ValueTask<string> ResolveAsync(
        ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (user?.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Authentication is required.");

        var tenantId = user.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new UnauthorizedAccessException("A validated tenant_id is required.");

        return ValueTask.FromResult($"tenant:{tenantId}");
    }
}

// Inside host setup:
builder.Services.AddEntityTracker<TenantScopeResolver>();
```

All users with the same authorized tenant scope receive the same entity notifications when subscribed. If the application lets users select tenants, use the application's authorization service to check that selection before returning its scope; do not return a raw header/query value. On tenant switches, reconnect and restore subscriptions. A single save must contain changes belonging to the resolved tenant; split cross-tenant jobs into separate scoped saves.

### Per-user scope

Use this when updates should be visible only to the same user who made the change, across their connections/devices:

```csharp
public sealed class UserScopeResolver : ITrackingScopeResolver
{
    public ValueTask<string> ResolveAsync(
        ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (user?.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("Authentication is required.");

        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(id))
            throw new UnauthorizedAccessException("A user ID is required.");

        return ValueTask.FromResult($"user:{id}");
    }
}
```

This scope targets the submitting user; it does not infer the owner of an entity. Use a tenant or another shared scope when collaborators should receive the same changes.

### Tenant-scoped background jobs

There is no HTTP principal during a background save. Supply an explicit job context scoped to the same DI scope as the DbContext, and have the resolver fall back to it only when no authenticated principal is available:

```csharp
public sealed class TrackingJobContext
{
    // Populate only after the worker has authorized the job's tenant.
    public string? AuthorizedTenantId { get; set; }
}

public sealed class TenantOrJobScopeResolver(TrackingJobContext job)
    : ITrackingScopeResolver
{
    public ValueTask<string> ResolveAsync(
        ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenantId = user?.Identity?.IsAuthenticated == true
            ? user.FindFirst("tenant_id")?.Value
            : job.AuthorizedTenantId;

        if (string.IsNullOrWhiteSpace(tenantId))
            throw new UnauthorizedAccessException("No authorized tenant scope.");

        return ValueTask.FromResult($"tenant:{tenantId}");
    }
}

builder.Services.AddScoped<TrackingJobContext>();
builder.Services.AddEntityTracker<TenantOrJobScopeResolver>();

// Inside a worker, after authorizing the job's tenant:
using var scope = scopeFactory.CreateScope();
scope.ServiceProvider.GetRequiredService<TrackingJobContext>()
    .AuthorizedTenantId = authorizedTenantId;
var db = scope.ServiceProvider.GetRequiredService<MyDatabase>();
// Apply this tenant's changes, then:
await db.SaveChangesAsync(cancellationToken);
```

Create a new DI scope per job/tenant; do not store the active tenant in a singleton. Scope-resolution failures during publication are logged after persistence and do not undo saved changes.

## Payload specification

Map the payload spec independently of the hub, with an optional custom path and access policy:

```csharp
app.MapEntityTrackerSpec("/entity-tracker/spec.json")
    .RequireAuthorization();
```

The path above is also the default when no argument is supplied. This endpoint is opt-in; authorization conventions are applied by the host. It describes registered entities and their declared payload types, without constructing entities or invoking payload getters.

Version 3 returns Tokyo's former x-domain-types contents directly, without a wrapper:

```json
{
  "version": 3,
  "metadata": { "description": "This is an extension data used by the automated client code generator for entity change tracking." },
  "types": ["Note"],
  "payloads": { "NotePayload": { "properties": [{ "name": "title", "type": "string" }] } },
  "entities": { "Note": { "payloadType": "NotePayload", "payloadNullable": false } },
  "eventMetadata": null
}
```

Migration from version 2: consume TypeScript property expressions instead of JSON Schema.
Payload keys are short DTO names; conflicting names fail at endpoint setup. Nested DTOs are included
and recursive references use their names. Arrays, dictionaries, enum literals and nullable members
are supported. Nullable members use ` | null | undefined`. Names and ignored members follow SignalR
JSON settings. Generation does not invoke getters or construct providers.

Notification metadata moves from `metadata` to `eventMetadata`: null without a provider,
otherwise `{ "type": "TrackingMetadata", "nullable": true }`, with its DTO included in payloads.
The types array and entities mapping use configured tracking names. Entities without payloads
retain a null payloadType. The endpoint path and authorization conventions are unchanged.

## Subscription contract

- `Subscribe(string[] entityNames)` adds the connection to each entity group within its scope.
- `Unsubscribe(string[] entityNames)` removes those memberships.
- `EntitiesChanged(EntityChangeEvent change)` contains one `EntityType`, its list of string `Id`/optional `Payload` changes, and optional metadata.

Names are case-sensitive. Unknown names reject the whole request before any group operation. Repeated subscriptions and unsubscriptions are harmless. Connections start with no subscriptions, including after reconnect. SignalR removes memberships on disconnect. Each event contains changes for exactly one entity name; subscribing to one entity never exposes other entities in the same transaction.

## Persistence behavior

Only registered attributed entities are tracked. Owned-value changes invalidate their tracked owner, resolving ownership through EF metadata. Notifications read generated IDs after successful saves. Explicit relational transactions accumulate changes until commit and deduplicate by scope, entity name, and ID, retaining the latest payload. Rollback, failed/canceled saves, and abandoned transactions discard pending state. Synchronous and asynchronous saves/commits are supported.

Notifications are invalidations: deleted IDs are included, but there is no operation-kind field or automatic serialization of entity instances. Payload getters must remain callable after deletion. Metadata is captured on the latest save. Notification preparation/delivery failures are logged and do not turn successful persistence into a reported failure.

Use ordinary `AddDbContext`, not context pooling. Ambient/distributed transactions, manual savepoint rollback, bulk SQL updates/deletes that bypass EF tracking, and durable/outbox delivery are outside this version. Configure SignalR's normal scale-out support when running multiple application instances.

## Sample and validation

`samples/TrackingSample` demonstrates SQLite, JWT authorization, a validated `tenant_id` claim, custom payloads, and an authenticated write endpoint. Configure `Authentication:Schemes:Bearer:Authority` and `Audience` for your issuer before running it. No frontend changes are included.

```sh
dotnet restore EntityTracker.AspNetCore.slnx
dotnet test EntityTracker.AspNetCore.slnx -c Release
dotnet pack src/EntityTracker.AspNetCore -c Release -o artifacts
```

Tests cover generator diagnostics/output, relational transaction behavior, generated IDs, owned changes, failure/cancellation cleanup, WebSocket subscriptions, scope isolation, reconnect behavior, and payload specifications.
