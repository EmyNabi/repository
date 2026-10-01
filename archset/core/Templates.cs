using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArchSet.Core;

public sealed record NumberChoice(ImmutableArray<double> Options, double Default);

public sealed record CountRange(int Min, int Max, int Default);

public sealed record FillChoice(ImmutableArray<string> Choices, string Default);

/// <summary>A template layer: either fixed (Material set) or a slot filled from parameters.</summary>
public sealed record TemplateLayer
{
    public string? Slot { get; init; }
    public string? Label { get; init; }
    public required string Function { get; init; }
    public bool Core { get; init; }
    public string? Material { get; init; }
    public double? FixedThickness { get; init; }
    public ImmutableArray<string> Choices { get; init; } = [];
    public string? Default { get; init; }
    public NumberChoice? Thickness { get; init; }
    public CountRange? Count { get; init; }
    public NumberChoice? Spacing { get; init; }
    public FillChoice? CavityFill { get; init; }
}

/// <summary>One parameter a template exposes, e.g. "structure", "structure.spacing", "gypsum.count".</summary>
public sealed record ParamSpec(string Name, string Label, ImmutableArray<object> Options, object Default);

public sealed record Template
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string Units { get; init; }
    public string? LocationLine { get; init; }
    public ImmutableList<string>? Tags { get; init; }
    public required ImmutableArray<TemplateLayer> Layers { get; init; }

    public static Template Parse(JsonObject o)
    {
        var layers = o["layers"]!.AsArray().Select(n => ParseLayer(n!.AsObject())).ToImmutableArray();
        return new Template
        {
            Id = (string)o["id"]!,
            Kind = (string)o["kind"]!,
            Name = (string)o["name"]!,
            Units = (string)o["units"]!,
            LocationLine = (string?)o["location_line"],
            Tags = o["tags"]?.AsArray().Select(t => (string)t!).ToImmutableList(),
            Layers = layers,
        };
    }

    static TemplateLayer ParseLayer(JsonObject o)
    {
        var choices = o["choices"]?.AsArray().Select(c => (string)c!).ToImmutableArray() ?? [];
        return new TemplateLayer
        {
            Slot = (string?)o["slot"],
            Label = (string?)o["label"],
            Function = (string)o["function"]!,
            Core = (bool?)o["core"] ?? false,
            Material = (string?)o["material"],
            FixedThickness = o["slot"] is null ? (double?)o["thickness"] : null,
            Choices = choices,
            Default = (string?)o["default"] ?? (choices.IsEmpty ? null : choices[0]),
            Thickness = o["slot"] is null ? null : Numbers(o["thickness"]),
            Spacing = Numbers(o["spacing"]),
            Count = o["count"] is JsonObject c
                ? new CountRange((int)c["min"]!, (int)c["max"]!, (int?)c["default"] ?? (int)c["min"]!)
                : null,
            CavityFill = o["cavity_fill"] is JsonObject f
                ? new FillChoice(f["choices"]!.AsArray().Select(x => (string)x!).ToImmutableArray(),
                                 (string?)f["default"] ?? (string)f["choices"]![0]!)
                : null,
        };
    }

    static NumberChoice? Numbers(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        var options = o["options"]!.AsArray().Select(x => (double)x!).ToImmutableArray();
        return new NumberChoice(options, (double?)o["default"] ?? options[0]);
    }

    /// <summary>Every parameter this template takes, in layer order.</summary>
    public ImmutableArray<ParamSpec> Parameters()
    {
        var specs = ImmutableArray.CreateBuilder<ParamSpec>();
        foreach (var l in Layers)
        {
            if (l.Slot is not { } s) continue;
            var label = l.Label ?? s;
            specs.Add(new ParamSpec(s, label, l.Choices.Cast<object>().ToImmutableArray(), l.Default!));
            if (l.Thickness is { } t)
                specs.Add(new ParamSpec($"{s}.thickness", $"{label} thickness", t.Options.Cast<object>().ToImmutableArray(), t.Default));
            if (l.Spacing is { } sp)
                specs.Add(new ParamSpec($"{s}.spacing", $"{label} spacing", sp.Options.Cast<object>().ToImmutableArray(), sp.Default));
            if (l.CavityFill is { } f)
                specs.Add(new ParamSpec($"{s}.fill", $"{label} cavity fill", f.Choices.Cast<object>().ToImmutableArray(), f.Default));
            if (l.Count is { } c)
                specs.Add(new ParamSpec($"{s}.count", $"{label} layers",
                    Enumerable.Range(c.Min, c.Max - c.Min + 1).Select(i => (object)(double)i).ToImmutableArray(), (double)c.Default));
        }
        return specs.ToImmutable();
    }
}

public sealed class GeneratorException(string message) : Exception(message);

/// <summary>
/// Expands templates into assemblies. Generated variants never inherit ratings: they get ratings
/// only by matching a known assembly layer-for-layer, otherwise ratings stay null (TODO-SOURCE).
/// </summary>
public static class Generator
{
    public const string NoFill = "none";

    /// <summary>Resolve user-supplied params against the template: defaults filled, unknown names and values refused.</summary>
    public static ImmutableSortedDictionary<string, object> Resolve(Template t, IReadOnlyDictionary<string, object?>? given)
    {
        var specs = t.Parameters();
        var byName = specs.ToDictionary(p => p.Name);
        var result = ImmutableSortedDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        foreach (var p in specs) result[p.Name] = p.Default;
        foreach (var (name, raw) in given ?? new Dictionary<string, object?>())
        {
            if (!byName.TryGetValue(name, out var spec))
                throw new GeneratorException($"Template {t.Id} has no parameter '{name}'. Parameters: {string.Join(", ", byName.Keys)}.");
            var value = Normalize(raw);
            var match = spec.Options.FirstOrDefault(o => Same(o, value))
                ?? throw new GeneratorException($"'{Format(value)}' is not an option for {name}. Options: {string.Join(", ", spec.Options.Select(Format))}.");
            result[name] = match;
        }
        return result.ToImmutable();
    }

    public static Assembly Expand(Template t, IReadOnlyDictionary<string, object?>? given, Library lib)
    {
        var p = Resolve(t, given);
        var layers = ImmutableList.CreateBuilder<Layer>();
        var names = new Dictionary<string, string>();
        foreach (var l in t.Layers)
        {
            if (l.Slot is null)
            {
                var m = lib.Material(l.Material!);
                layers.Add(new Layer { Material = m.Id, Thickness = l.FixedThickness ?? m.Thickness, Function = l.Function, Core = l.Core });
                continue;
            }
            var s = l.Slot;
            var mat = lib.Material((string)p[s]);
            names[s] = mat.Name;
            var thickness = l.Thickness is null ? mat.Thickness : (double)p[$"{s}.thickness"];
            if (thickness == 0) continue; // a zero option omits the layer
            var fill = l.CavityFill is null ? null : (string)p[$"{s}.fill"];
            var layer = new Layer
            {
                Material = mat.Id,
                Thickness = thickness,
                Function = l.Function,
                Core = l.Core,
                Spacing = l.Spacing is null ? null : (double)p[$"{s}.spacing"],
                CavityFill = fill is null or NoFill ? null : lib.Material(fill).Id,
            };
            var count = l.Count is null ? 1 : (int)(double)p[$"{s}.count"];
            for (var i = 0; i < count; i++) layers.Add(layer);
        }

        var name = t.Name;
        foreach (var (slot, matName) in names) name = name.Replace("{" + slot + "}", matName.ToLowerInvariant());

        var assembly = new Assembly
        {
            Id = VariantId(t, p, lib),
            Kind = t.Kind,
            Name = name,
            Units = t.Units,
            LocationLine = t.LocationLine,
            Layers = layers.ToImmutable(),
            Tags = t.Tags,
            Source = $"generated:{t.Id}",
            Template = new TemplateRef
            {
                Id = t.Id,
                Params = p.ToImmutableSortedDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value), StringComparer.Ordinal),
            },
            Ratings = Ratings.Unsourced,
        };

        var known = lib.Known.FirstOrDefault(k => Signature(k) == Signature(assembly));
        return known is null
            ? assembly
            : assembly with { Ratings = known.Ratings, Notes = $"Layers match known assembly {known.Id}; ratings copied from it." };
    }

    /// <summary>
    /// Readable, deterministic ID: template, then one segment per varying slot.
    /// EW-WOOD~fc~mwb50~ply~2x6@16+fg~gx2. Identical layer stacks get identical IDs.
    /// </summary>
    public static string VariantId(Template t, IReadOnlyDictionary<string, object> p, Library lib)
    {
        var segs = new List<string> { t.Id };
        foreach (var l in t.Layers)
        {
            if (l.Slot is not { } s) continue;
            var varies = l.Choices.Length > 1 || l.Thickness is not null || l.Spacing is not null || l.CavityFill is not null || l.Count is not null;
            if (!varies) continue;
            var seg = lib.Material((string)p[s]).Code;
            if (l.Thickness is not null)
            {
                var th = (double)p[$"{s}.thickness"];
                if (th == 0) continue;
                seg += Num(th);
            }
            if (l.Spacing is not null)
            {
                var sp = (double)p[$"{s}.spacing"];
                seg += "@" + (t.Units == "imperial" ? Num(Math.Round(sp / 25.4)) : Num(sp));
            }
            if (l.CavityFill is not null)
            {
                var f = (string)p[$"{s}.fill"];
                seg += f == NoFill ? "+0" : "+" + lib.Material(f).Code;
            }
            if (l.Count is not null && (int)(double)p[$"{s}.count"] > 1) seg += "x" + (int)(double)p[$"{s}.count"];
            segs.Add(seg);
        }
        return string.Join("~", segs);
    }

    /// <summary>Every distinct variant a template can produce (identical stacks counted once).</summary>
    public static IEnumerable<Assembly> Enumerate(Template t, Library lib)
    {
        var specs = t.Parameters();
        var seen = new HashSet<string>();
        foreach (var combo in Cartesian(specs))
        {
            var a = Expand(t, combo, lib);
            if (seen.Add(a.Id)) yield return a;
        }
    }

    static IEnumerable<Dictionary<string, object?>> Cartesian(ImmutableArray<ParamSpec> specs, int i = 0)
    {
        if (i == specs.Length) { yield return new Dictionary<string, object?>(); yield break; }
        foreach (var rest in Cartesian(specs, i + 1))
            foreach (var o in specs[i].Options)
                yield return new Dictionary<string, object?>(rest) { [specs[i].Name] = o };
    }

    /// <summary>Layer-for-layer identity used to match generated variants to known, rated assemblies.</summary>
    public static string Signature(Assembly a) => a.Kind + "|" + string.Join("|", a.Layers.Select(l =>
        $"{l.Material}:{Math.Round(l.Thickness, 1).ToString(CultureInfo.InvariantCulture)}" +
        (l.Spacing is { } s ? "@" + Math.Round(s).ToString(CultureInfo.InvariantCulture) : "") +
        (l.CavityFill is { } f ? "+" + f : "")));

    static object? Normalize(object? v) => v switch
    {
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        JsonValue jv when jv.TryGetValue<string>(out var s) => s,
        JsonValue jv when jv.TryGetValue<double>(out var d) => d,
        int i => (double)i,
        long l => (double)l,
        float f => (double)f,
        _ => v,
    };

    static bool Same(object option, object? value) => (option, value) switch
    {
        (double a, double b) => Math.Abs(a - b) < 0.01,
        (string a, string b) => a == b,
        _ => false,
    };

    static string Format(object? v) => v is double d ? Num(d) : v?.ToString() ?? "null";

    static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
