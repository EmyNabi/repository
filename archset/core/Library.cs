using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArchSet.Core;

/// <summary>The office library on disk: materials, templates, known assemblies, pens, rules.</summary>
public sealed class Library
{
    public required ImmutableSortedDictionary<string, Material> Materials { get; init; }
    public required ImmutableSortedDictionary<string, Template> Templates { get; init; }
    /// <summary>Known assemblies (office standard, Excel import). Generated variants match against these for ratings.</summary>
    public required ImmutableList<Assembly> Known { get; init; }
    /// <summary>Default volume rules (faces and openings); copied into each project, where they can be edited.</summary>
    public RuleSet? VolumeRules { get; init; }

    public Material Material(string id) =>
        Materials.TryGetValue(id, out var m) ? m : throw new GeneratorException($"Unknown material '{id}'.");

    public Template Template(string id) =>
        Templates.TryGetValue(id, out var t) ? t : throw new GeneratorException($"Unknown template '{id}'.");

    public static Library Load(string root)
    {
        static IEnumerable<string> Files(string dir) =>
            Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal) : [];

        return new Library
        {
            Materials = Files(Path.Combine(root, "materials"))
                .Select(f => JsonSerializer.Deserialize<Material>(File.ReadAllText(f), Json.Options)!)
                .ToImmutableSortedDictionary(m => m.Id, m => m, StringComparer.Ordinal),
            Templates = Files(Path.Combine(root, "templates"))
                .Select(f => Core.Template.Parse(JsonNode.Parse(File.ReadAllText(f))!.AsObject()))
                .ToImmutableSortedDictionary(t => t.Id, t => t, StringComparer.Ordinal),
            Known = Files(Path.Combine(root, "assemblies"))
                .Select(f => JsonSerializer.Deserialize<Assembly>(File.ReadAllText(f), Json.Options)!)
                .ToImmutableList(),
            VolumeRules = Directory.Exists(Path.Combine(root, "volume-rules")) ? RuleSet.Load(Path.Combine(root, "volume-rules")) : null,
        };
    }

    /// <summary>Find the repo's library/ folder by walking up from a directory.</summary>
    public static string FindRoot(string start, string name = "library")
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, name);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException($"No '{name}' folder above {start}.");
    }

    /// <summary>Checks the schema cannot express: references resolve and units agree.</summary>
    public IReadOnlyList<string> Check(Assembly a)
    {
        var errors = new List<string>();
        if (a.Layers.IsEmpty) errors.Add($"{a.Id}: an assembly needs at least one layer.");
        for (var i = 0; i < a.Layers.Count; i++)
        {
            var l = a.Layers[i];
            if (!Materials.TryGetValue(l.Material, out var m)) { errors.Add($"{a.Id} layer {i}: unknown material '{l.Material}'."); continue; }
            if (m.Fill) errors.Add($"{a.Id} layer {i}: '{m.Id}' is a cavity fill; set it as cavity_fill on a framing layer.");
            if (m.Units is { } u && a.Units is { } au && u != au) errors.Add($"{a.Id} layer {i}: {u} material '{m.Id}' in a {au} assembly.");
            if (l.CavityFill is { } f)
            {
                if (!Materials.TryGetValue(f, out var fm)) errors.Add($"{a.Id} layer {i}: unknown cavity fill '{f}'.");
                else if (!fm.Fill) errors.Add($"{a.Id} layer {i}: '{f}' is not a cavity fill material.");
            }
            if ((l.Spacing is not null || l.CavityFill is not null) && m.Category != "framing")
                errors.Add($"{a.Id} layer {i}: spacing and cavity_fill apply only to framing.");
        }
        return errors;
    }
}
