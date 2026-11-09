using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace EntityTracker.AspNetCore;

internal static class PayloadSpecification
{
    public static byte[] Generate(TrackingRegistry registry, JsonSerializerOptions serializer)
    {
        var options = new JsonSerializerOptions(serializer);
        options.MakeReadOnly(populateMissingResolver: true);
        var payloads = new JsonObject();
        var names = new Dictionary<string, Type>(StringComparer.Ordinal);
        string Describe(Type type)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null) return Describe(nullable) + " | null | undefined";
            var info = options.GetTypeInfo(type);
            if (info.Kind == JsonTypeInfoKind.Object)
            {
                var name = type.Name;
                if (names.TryGetValue(name, out var previous))
                {
                    if (previous != type) throw new InvalidOperationException($"Payload type name collision: {name}.");
                    return name;
                }
                names.Add(name, type);
                var properties = new JsonArray();
                payloads[name] = new JsonObject { ["properties"] = properties };
                foreach (var property in info.Properties.Where(p => p.Get != null).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    var expression = Describe(property.PropertyType);
                    if (property.IsGetNullable && Nullable.GetUnderlyingType(property.PropertyType) == null) expression += " | null | undefined";
                    properties.Add(new JsonObject { ["name"] = property.Name, ["type"] = expression });
                }
                return name;
            }
            if (info.Kind == JsonTypeInfoKind.Dictionary)
            {
                var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(t => t.IsGenericType &&
                    (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
                return "{ [key: string]: " + (dictionary == null ? "any" : Describe(dictionary.GetGenericArguments()[1])) + " }";
            }
            if (info.Kind == JsonTypeInfoKind.Enumerable)
            {
                var element = type.IsArray ? type.GetElementType() : type.GetInterfaces().Append(type)
                    .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
                var expression = element == null ? "any" : Describe(element);
                return (expression.Contains('|') ? "(" + expression + ")" : expression) + "[]";
            }
            var schema = options.GetJsonSchemaAsNode(type);
            if (schema is not JsonObject obj) return "any";
            if (obj["enum"] is JsonArray values) return string.Join(" | ", values.Select(v => v?.ToJsonString() ?? "null"));
            var primitive = obj["type"] is JsonArray alternatives ? alternatives.FirstOrDefault(n => n?.ToString() != "null")?.ToString() : obj["type"]?.ToString();
            return primitive switch
            {
                "string" => "string", "integer" or "number" => "number", "boolean" => "boolean", "null" => "null", _ => "any"
            };
        }
        var entities = new JsonObject();
        var types = new JsonArray();
        foreach (var descriptor in registry.Descriptors.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            types.Add(descriptor.Name);
            entities[descriptor.Name] = new JsonObject
            {
                ["payloadType"] = descriptor.PayloadType is { } type ? Describe(type) : null,
                ["payloadNullable"] = descriptor.PayloadType == null || descriptor.PayloadNullable
            };
        }
        JsonNode? metadata = registry.MetadataType is { } metadataType
            ? new JsonObject { ["type"] = Describe(metadataType), ["nullable"] = true } : null;
        var sorted = new JsonObject();
        foreach (var entry in payloads.OrderBy(p => p.Key, StringComparer.Ordinal)) sorted[entry.Key] = entry.Value?.DeepClone();
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["version"] = 3,
            ["metadata"] = new JsonObject { ["description"] = "This is an extension data used by the automated client code generator for entity change tracking." },
            ["types"] = types, ["payloads"] = sorted, ["entities"] = entities, ["eventMetadata"] = metadata
        });
    }
}


