using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace EntityTracker.AspNetCore;

internal static class PayloadSpecification
{
    public static byte[] Generate(TrackingRegistry registry, JsonSerializerOptions serializer)
    {
        // Clone instead of freezing/mutating the application's SignalR options.
        var options = new JsonSerializerOptions(serializer);
        options.MakeReadOnly(populateMissingResolver: true);
        var entities = new JsonObject();
        var payloads = new JsonObject();
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var descriptor in registry.Descriptors.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            string? key = null;
            if (descriptor.PayloadType is { } type)
            {
                key = type.FullName ?? type.Name;
                if (types.TryGetValue(key, out var previous) && previous != type)
                    throw new InvalidOperationException($"Payload type name collision: {key}.");
                if (!types.ContainsKey(key))
                {
                    types.Add(key, type);
                    var schema = options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
                    // Each payload is its own schema resource so recursive '#' references remain valid.
                    if (schema is JsonObject obj)
                    {
                        obj["$schema"] = "https://json-schema.org/draft/2020-12/schema";
                        obj["$id"] = "urn:entitytracker:payload:" + Uri.EscapeDataString(key);
                    }
                    payloads[key] = schema;
                }
            }
            entities[descriptor.Name] = new JsonObject
            {
                ["payloadType"] = key,
                ["payloadNullable"] = descriptor.PayloadType == null || descriptor.PayloadNullable
            };
        }
        JsonNode? metadata = null;
        if (registry.MetadataType is { } metadataType)
        {
            var key = metadataType.FullName ?? metadataType.Name;
            var schema = options.GetJsonSchemaAsNode(metadataType, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
            if (schema is JsonObject obj)
            {
                obj["$schema"] = "https://json-schema.org/draft/2020-12/schema";
                obj["$id"] = "urn:entitytracker:metadata:" + Uri.EscapeDataString(key);
            }
            metadata = new JsonObject { ["type"] = key, ["nullable"] = true, ["schema"] = schema };
        }
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["version"] = 2,
            ["metadata"] = metadata,
            ["entities"] = entities,
            ["payloads"] = payloads
        });
    }
}
