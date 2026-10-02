using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArchSet.Core;

// ---- Input: volumes as the host describes them -------------------------------------------

public sealed record Adjacent(string Volume, string Face);

public sealed record OpeningCut
{
    public required string Id { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required double Sill { get; init; }
    public string? Kind { get; init; }
}

public sealed record Face
{
    public required string Id { get; init; }
    public required double[] Normal { get; init; }
    public required double Area { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public double? ZMin { get; init; }
    public double? ZMax { get; init; }
    public Adjacent? Adjacent { get; init; }
    public string? Tag { get; init; }
    public ImmutableList<OpeningCut>? Openings { get; init; }
}

/// <summary>A closed volume. The host supplies faces; the core decides what each becomes.</summary>
public sealed record Volume
{
    public required string Id { get; init; }
    public string? Type { get; init; }
    public string? Name { get; init; }
    public required ImmutableList<Face> Faces { get; init; }
}

// ---- Rules as data ------------------------------------------------------------------------

public sealed record FaceWhen
{
    public ImmutableArray<string>? Orientation { get; init; }
    public ImmutableArray<string>? Adjacency { get; init; }
    public ImmutableArray<string>? VolumeType { get; init; }
    public ImmutableArray<string>? OtherVolumeType { get; init; }
    public ImmutableArray<string>? FaceTag { get; init; }
}

public sealed record FaceThen
{
    public required string Element { get; init; }
    public string? Side { get; init; }
    public ImmutableSortedDictionary<string, double>? Params { get; init; }
}

public sealed record FaceRule
{
    public required string Id { get; init; }
    public required FaceWhen When { get; init; }
    public required FaceThen Then { get; init; }
    public string? Why { get; init; }

    /// <summary>Precedence level from the most specific condition: face_tag 4, volume type 3, adjacency 2, orientation 1.</summary>
    public int Level => When.FaceTag is not null ? 4
        : When.VolumeType is not null || When.OtherVolumeType is not null ? 3
        : When.Adjacency is not null ? 2
        : 1;

    public int Conditions => new object?[] { When.Orientation, When.Adjacency, When.VolumeType, When.OtherVolumeType, When.FaceTag }.Count(c => c is not null);

    public static string LevelName(int level) => level switch { 4 => "face_tag", 3 => "volume_type", 2 => "adjacency", _ => "orientation" };
}

public sealed record OpeningWhen
{
    public ImmutableArray<string>? Host { get; init; }
    public double? SillMax { get; init; }
    public double? SillMin { get; init; }
    public double? HeightMin { get; init; }
    public double? WidthMin { get; init; }
    public double? WidthMax { get; init; }
    public double? WidthRatioMin { get; init; }
    public double? HeightRatioMin { get; init; }
}

public sealed record OpeningRule
{
    public required string Id { get; init; }
    public required OpeningWhen When { get; init; }
    public required FaceThen Then { get; init; }
    public string? Why { get; init; }
}

public sealed record VolumeSettings
{
    public double FlatToleranceDeg { get; init; } = 5;
    public double GradeTolerance { get; init; } = 50;
}

public sealed record RuleSet(VolumeSettings Settings, ImmutableList<FaceRule> Faces, ImmutableList<OpeningRule> Openings)
{
    public static RuleSet Load(string dir)
    {
        var faces = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "faces.json")))!;
        var openings = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "openings.json")))!;
        return new RuleSet(
            faces["settings"]?.Deserialize<VolumeSettings>(Json.Options) ?? new VolumeSettings(),
            faces["rules"]!.Deserialize<ImmutableList<FaceRule>>(Json.Options)!,
            openings["rules"]!.Deserialize<ImmutableList<OpeningRule>>(Json.Options)!);
    }
}

// ---- Output: derived elements with provenance -----------------------------------------------

public sealed record ElementSource(string Volume, string Face, string? Opening = null, Adjacent? Other = null);

public sealed record Provenance
{
    public required string Rule { get; init; }
    public required string Level { get; init; }
    public string? Why { get; init; }
    /// <summary>True when the result came from a default rule rather than a face tag or explicit kind.</summary>
    public required bool Assumed { get; init; }
    /// <summary>Other rules that matched but lost on precedence, most specific first.</summary>
    public ImmutableList<string> Overruled { get; init; } = [];
}

public sealed record Element
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string? Side { get; init; }
    public required ElementSource Source { get; init; }
    public double Area { get; init; }
    public string? Host { get; init; }
    public ImmutableSortedDictionary<string, double>? Params { get; init; }
    public required Provenance Provenance { get; init; }
}

public sealed record Issue(string Kind, string Message, ImmutableList<string> Refs);

public sealed record Derivation(ImmutableList<Element> Elements, ImmutableList<Issue> Issues);

/// <summary>
/// Explodes volumes into building elements. Pure: the same volumes and rules always give the same
/// elements, so elements are never stored, only derived. Edit a volume or a rule and everything rebuilds.
/// </summary>
public static class Deriver
{
    static readonly HashSet<string> Hosts = ["wall", "interior_wall", "party_wall"];

    public static string Orientation(double[] n, VolumeSettings s)
    {
        var len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        var nz = len == 0 ? 0 : n[2] / len;
        var tol = s.FlatToleranceDeg * Math.PI / 180;
        if (nz >= Math.Cos(tol)) return "top";
        if (nz <= -Math.Cos(tol)) return "bottom";
        if (Math.Abs(nz) <= Math.Sin(tol)) return "vertical";
        return nz > 0 ? "sloped_up" : "sloped_down";
    }

    public static Derivation Derive(IReadOnlyDictionary<string, Volume> volumes, RuleSet rules, double grade = 0,
        IEnumerable<(string A, string B)>? intersections = null)
    {
        var elements = ImmutableList.CreateBuilder<Element>();
        var issues = ImmutableList.CreateBuilder<Issue>();

        foreach (var (a, b) in intersections ?? [])
            issues.Add(new Issue("intersection", $"Volumes {a} and {b} intersect. Merge them, or say which one cuts the other.", [a, b]));

        foreach (var v in volumes.Values.OrderBy(v => v.Id, StringComparer.Ordinal))
            foreach (var f in v.Faces)
            {
                Volume? other = null;
                var adjacency = "exposed";
                var orientation = Orientation(f.Normal, rules.Settings);
                if (f.Adjacent is { } adj)
                {
                    if (volumes.TryGetValue(adj.Volume, out other))
                    {
                        adjacency = "shared";
                        // A shared face is one element; the volume with the smaller ID owns it.
                        if (string.CompareOrdinal(v.Id, other.Id) > 0) continue;
                    }
                    else
                        issues.Add(new Issue("missing_neighbor", $"{v.Id}:{f.Id} says it touches {adj.Volume}, which is not in the model; treated as exposed.", [v.Id, adj.Volume]));
                }
                else if (orientation == "bottom" && (f.ZMax ?? f.ZMin ?? double.MaxValue) <= grade + rules.Settings.GradeTolerance)
                    adjacency = "ground";

                var matches = rules.Faces
                    .Select((r, i) => (r, i))
                    .Where(x => Matches(x.r.When, orientation, adjacency, v.Type, other?.Type, f.Tag))
                    .OrderByDescending(x => x.r.Level).ThenByDescending(x => x.r.Conditions).ThenBy(x => x.i)
                    .Select(x => x.r)
                    .ToList();
                if (matches.Count == 0)
                {
                    issues.Add(new Issue("no_rule", $"No rule covers {v.Id}:{f.Id} ({orientation}, {adjacency}).", [v.Id]));
                    continue;
                }

                var rule = matches[0];
                if (rule.Then.Element == "none") continue;

                var id = other is null ? $"{v.Id}:{f.Id}" : $"{v.Id}:{f.Id}|{other.Id}:{f.Adjacent!.Face}";
                var element = new Element
                {
                    Id = id,
                    Kind = rule.Then.Element,
                    Side = rule.Then.Side,
                    Source = new ElementSource(v.Id, f.Id, Other: other is null ? null : f.Adjacent),
                    Area = f.Area,
                    Params = rule.Then.Params,
                    Provenance = new Provenance
                    {
                        Rule = rule.Id, Level = FaceRule.LevelName(rule.Level), Why = rule.Why,
                        Assumed = rule.Level < 4, Overruled = matches.Skip(1).Select(m => m.Id).ToImmutableList(),
                    },
                };
                elements.Add(element);

                foreach (var o in f.Openings ?? [])
                {
                    if (!Hosts.Contains(element.Kind))
                    {
                        issues.Add(new Issue("orphan_opening", $"Opening {o.Id} is on {id}, a {element.Kind}, which cannot host openings.", [id]));
                        continue;
                    }
                    if (f.Width is { } w && o.Width > w + 1)
                        issues.Add(new Issue("opening_too_wide", $"Opening {o.Id} is wider than its face {id}.", [id]));
                    elements.Add(Opening(o, f, element, rules));
                }
            }

        return new Derivation(elements.ToImmutable(), issues.ToImmutable());
    }

    static Element Opening(OpeningCut o, Face f, Element host, RuleSet rules)
    {
        var src = host.Source with { Opening = o.Id };
        var id = $"{host.Id}#{o.Id}";
        if (o.Kind is { } k)
            return new Element
            {
                Id = id, Kind = k, Source = src, Area = o.Width * o.Height, Host = host.Id,
                Provenance = new Provenance { Rule = "explicit", Level = "face_tag", Why = "Kind set on the opening.", Assumed = false },
            };
        var matches = rules.Openings.Where(r => Matches(r.When, o, f, host.Kind)).ToList();
        var rule = matches.FirstOrDefault()
            ?? new OpeningRule { Id = "fallback", When = new OpeningWhen(), Then = new FaceThen { Element = "window" }, Why = "No opening rule matched." };
        return new Element
        {
            Id = id, Kind = rule.Then.Element, Source = src, Area = o.Width * o.Height, Host = host.Id,
            Provenance = new Provenance
            {
                Rule = rule.Id, Level = "proportion", Why = rule.Why, Assumed = true,
                Overruled = matches.Skip(1).Select(m => m.Id).ToImmutableList(),
            },
        };
    }

    static bool Matches(FaceWhen w, string orientation, string adjacency, string? type, string? otherType, string? tag) =>
        In(w.Orientation, orientation) && In(w.Adjacency, adjacency) && In(w.VolumeType, type)
        && In(w.OtherVolumeType, otherType) && In(w.FaceTag, tag);

    static bool Matches(OpeningWhen w, OpeningCut o, Face f, string hostKind) =>
        In(w.Host, hostKind)
        && (w.SillMax is null || o.Sill <= w.SillMax)
        && (w.SillMin is null || o.Sill >= w.SillMin)
        && (w.HeightMin is null || o.Height >= w.HeightMin)
        && (w.WidthMin is null || o.Width >= w.WidthMin)
        && (w.WidthMax is null || o.Width <= w.WidthMax)
        && (w.WidthRatioMin is null || (f.Width is > 0 && o.Width / f.Width >= w.WidthRatioMin))
        && (w.HeightRatioMin is null || (f.Height is > 0 && o.Height / f.Height >= w.HeightRatioMin));

    static bool In(ImmutableArray<string>? allowed, string? value) => allowed is null || (value is not null && allowed.Value.Contains(value));
}
