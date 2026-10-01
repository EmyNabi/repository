using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchSet.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}

public sealed record CutHatch
{
    public required string Pattern { get; init; }
    public required double Scale { get; init; }
    public double Rotation { get; init; }
    public string Color { get; init; } = "#000000";
    public string? Background { get; init; }
}

public sealed record ElevationHatch
{
    public required string Pattern { get; init; }
    public double? Module { get; init; }
    public string Orientation { get; init; } = "horizontal";
    public string Line { get; init; } = "projection";
}

public sealed record Material
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public required string Category { get; init; }
    public string? Units { get; init; }
    public required double Thickness { get; init; }
    public bool Fill { get; init; }
    public required CutHatch Cut { get; init; }
    public required ElevationHatch Elevation { get; init; }
}

public sealed record Layer
{
    public required string Material { get; init; }
    public required double Thickness { get; init; }
    public required string Function { get; init; }
    public bool Core { get; init; }
    public bool Variable { get; init; }
    public double? Spacing { get; init; }
    public string? CavityFill { get; init; }
}

public sealed record Ratings
{
    public double? FireHr { get; init; }
    public int? Stc { get; init; }
    public double? RValue { get; init; }
    /// <summary>Citation. null means TODO-SOURCE: never show the ratings as verified.</summary>
    public string? Source { get; init; }

    public static readonly Ratings Unsourced = new();
}

public sealed record Coarse
{
    public string? Poche { get; init; }
    public string? Outline { get; init; }
}

public sealed record TemplateRef
{
    public required string Id { get; init; }
    public required ImmutableSortedDictionary<string, JsonElement> Params { get; init; }
}

public sealed record Assembly
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string? Units { get; init; }
    public string? LocationLine { get; init; }
    public required ImmutableList<Layer> Layers { get; init; }
    public Ratings? Ratings { get; init; }
    public Coarse? Coarse { get; init; }
    public ImmutableList<string>? Tags { get; init; }
    public string? Source { get; init; }
    public TemplateRef? Template { get; init; }
    public string? Notes { get; init; }

    /// <summary>Total thickness is always the sum of layers, never stored.</summary>
    [JsonIgnore]
    public double Thickness => Layers.Sum(l => l.Thickness);
}
