using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Json.Schema;

namespace ArchSet.Core;

public sealed record OperationSpec(string Name, string Host, bool Mutates, string Description, JsonObject Input);

/// <summary>schema/operations.json: the one operation set shared by the panel (HTTP) and Claude (MCP).</summary>
public sealed class Contract
{
    readonly Schemas _schemas;
    readonly Dictionary<string, JsonSchema> _inputs = new();

    public ImmutableSortedDictionary<string, OperationSpec> Operations { get; }

    public Contract(string schemaDir)
    {
        _schemas = new Schemas(schemaDir);
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(schemaDir, "operations.json")))!;
        Operations = doc["operations"]!.AsArray()
            .Select(o => new OperationSpec(
                (string)o!["name"]!, (string)o["host"]!, (bool)o["mutates"]!, (string)o["description"]!, o["input"]!.AsObject()))
            .ToImmutableSortedDictionary(o => o.Name, o => o, StringComparer.Ordinal);
        foreach (var op in Operations.Values) _inputs[op.Name] = _schemas.Compile(op.Name, op.Input);
    }

    public Schemas Schemas => _schemas;

    public IReadOnlyList<string> ValidateInput(string op, JsonNode input) => _schemas.Validate(_inputs[op], input);

    /// <summary>The MCP tools/list payload: one tool per operation, input schema inlined as written.</summary>
    public JsonArray McpTools() => new(Operations.Values.Select(o => (JsonNode)new JsonObject
    {
        ["name"] = o.Name.Replace('.', '_'),
        ["description"] = o.Description + (o.Host == "rhino" ? " Requires Rhino." : ""),
        ["inputSchema"] = o.Input.DeepClone(),
    }).ToArray());
}
