using System.Text.Json.Nodes;
using Json.Schema;

namespace ArchSet.Core;

/// <summary>Loads schema/*.schema.json (the source of truth) and validates documents against them.</summary>
public sealed class Schemas
{
    public const string BaseUri = "https://archset.dev/schema/";

    readonly EvaluationOptions _options;
    readonly Dictionary<string, JsonSchema> _byFile = new();

    public Schemas(string schemaDir)
    {
        _options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        foreach (var file in Directory.EnumerateFiles(schemaDir, "*.schema.json"))
        {
            var schema = JsonSchema.FromText(File.ReadAllText(file));
            _options.SchemaRegistry.Register(schema);
            _byFile[Path.GetFileName(file)] = schema;
        }
    }

    public JsonSchema this[string file] => _byFile[file];

    /// <summary>Compile an inline schema (e.g. an operation's input) whose $refs resolve against schema/.</summary>
    public JsonSchema Compile(string name, JsonNode schema)
    {
        var copy = schema.DeepClone().AsObject();
        copy["$id"] = $"{BaseUri}op.{name}.json";
        var compiled = JsonSchema.FromText(copy.ToJsonString());
        _options.SchemaRegistry.Register(compiled);
        return compiled;
    }

    public IReadOnlyList<string> Validate(JsonSchema schema, JsonNode? doc)
    {
        var result = schema.Evaluate(doc, _options);
        if (result.IsValid) return [];
        return result.Details
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{(d.InstanceLocation.ToString() is "" ? "/" : d.InstanceLocation.ToString())}: {e.Value}"))
            .Distinct()
            .ToList();
    }

    public IReadOnlyList<string> Validate(string file, JsonNode? doc) => Validate(this[file], doc);
}
