using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArchSet.Core;

namespace ArchSet.Core.Tests;

/// <summary>
/// A small massing used across tests: a ground-floor room G (10 x 8 x 3 m), an upper room U stacked on it,
/// and a porch P against part of G's east side. Faces are what the Rhino host would report.
/// </summary>
public static class Massing
{
    public static readonly double[] N = [0, 1, 0], S = [0, -1, 0], E = [1, 0, 0], W = [-1, 0, 0], Up = [0, 0, 1], Down = [0, 0, -1];

    public static Face F(string id, double[] n, double area = 30e6, double? width = null, double? height = null,
        double? zMin = null, double? zMax = null, (string v, string f)? adj = null, string? tag = null, params OpeningCut[] openings) => new()
    {
        Id = id, Normal = n, Area = area, Width = width, Height = height, ZMin = zMin, ZMax = zMax, Tag = tag,
        Adjacent = adj is { } a ? new Adjacent(a.v, a.f) : null,
        Openings = openings.Length == 0 ? null : [.. openings],
    };

    public static Volume V(string id, string? type, params Face[] faces) => new() { Id = id, Type = type, Faces = [.. faces] };

    public static Volume G => V("G", "room",
        F("n", N, width: 10000, height: 3000), F("s", S, width: 10000, height: 3000), F("w", W, width: 8000, height: 3000),
        F("e1", E, width: 4000, height: 3000, adj: ("P", "w")), F("e2", E, width: 4000, height: 3000),
        F("bot", Down, 80e6, zMin: 0, zMax: 0), F("top", Up, 80e6, zMin: 3000, zMax: 3000, adj: ("U", "bot")));

    public static Volume U => V("U", "room",
        F("n", N, width: 10000, height: 3000), F("s", S, width: 10000, height: 3000), F("w", W, width: 8000, height: 3000),
        F("e", E, width: 8000, height: 3000), F("top", Up, 80e6), F("bot", Down, 80e6, zMin: 3000, zMax: 3000, adj: ("G", "top")));

    public static Volume P => V("P", "porch",
        F("n", N), F("s", S), F("e", E), F("w", W, adj: ("G", "e1")), F("top", Up, 12e6), F("bot", Down, 12e6, zMin: 0, zMax: 0));

    public static ImmutableSortedDictionary<string, Volume> All(params Volume[] vs) =>
        vs.ToImmutableSortedDictionary(v => v.Id, v => v, StringComparer.Ordinal);

    public static RuleSet Rules => Repo.Lib.VolumeRules!;

    public static Dictionary<string, int> Counts(Derivation d) => d.Elements.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
}

public class DeriverTests
{
    [Theory]
    [InlineData(0, 1, 0, "vertical")]
    [InlineData(0, 0, 1, "top")]
    [InlineData(0, 0, -1, "bottom")]
    [InlineData(0, 0.05, 1, "top")]
    [InlineData(0, 1, 1, "sloped_up")]
    [InlineData(1, 0, -0.5, "sloped_down")]
    public void Orientation_comes_from_the_normal(double x, double y, double z, string expected) =>
        Assert.Equal(expected, Deriver.Orientation([x, y, z], new VolumeSettings()));

    [Fact]
    public void A_stacked_house_with_a_porch_explodes_into_the_right_elements()
    {
        var d = Deriver.Derive(Massing.All(Massing.G, Massing.U, Massing.P), Massing.Rules);
        Assert.Empty(d.Issues);
        Assert.Equal(new Dictionary<string, int>
        {
            ["wall"] = 8, ["interior_wall"] = 1, ["floor"] = 1, ["slab_on_grade"] = 2, ["roof"] = 2,
        }, Massing.Counts(d));
    }

    [Fact]
    public void A_shared_face_becomes_one_element_owned_by_the_smaller_volume_id()
    {
        var d = Deriver.Derive(Massing.All(Massing.G, Massing.U), Massing.Rules);
        var floor = Assert.Single(d.Elements, e => e.Kind == "floor");
        Assert.Equal("G:top|U:bot", floor.Id);
        Assert.Equal("adjacency", floor.Provenance.Level);
    }

    [Fact]
    public void Porch_sides_build_nothing_by_volume_type_rule()
    {
        var d = Deriver.Derive(Massing.All(Massing.P), Massing.Rules);
        Assert.DoesNotContain(d.Elements, e => e.Kind == "wall");
        Assert.Contains(d.Elements, e => e.Kind == "roof");
    }

    [Fact]
    public void Roof_carries_the_overhang_parameter_from_its_rule()
    {
        var d = Deriver.Derive(Massing.All(Massing.U), Massing.Rules);
        Assert.Equal(450, d.Elements.Single(e => e.Kind == "roof").Params!["overhang"]);
    }

    [Fact]
    public void A_face_tag_beats_every_other_rule_and_is_not_assumed()
    {
        var g = Massing.G with { Faces = Massing.G.Faces.Replace(Massing.G.Faces[1], Massing.G.Faces[1] with { Tag = "storefront" }) };
        var e = Deriver.Derive(Massing.All(g), Massing.Rules).Elements.Single(x => x.Id == "G:s");
        Assert.Equal("storefront", e.Kind);
        Assert.False(e.Provenance.Assumed);
        Assert.Equal("face_tag", e.Provenance.Level);
        Assert.Contains("orient.vertical", e.Provenance.Overruled);
    }

    [Fact]
    public void Two_units_side_by_side_share_a_party_wall()
    {
        var a = Massing.V("A", "unit", Massing.F("e", Massing.E, adj: ("B", "w")));
        var b = Massing.V("B", "unit", Massing.F("w", Massing.W, adj: ("A", "e")));
        var e = Assert.Single(Deriver.Derive(Massing.All(a, b), Massing.Rules).Elements);
        Assert.Equal("party_wall", e.Kind);
        Assert.Equal("volume_type", e.Provenance.Level);
    }

    [Fact]
    public void An_exposed_underside_above_grade_is_a_soffit()
    {
        var v = Massing.V("C", "room", Massing.F("bot", Massing.Down, zMin: 3000, zMax: 3000));
        Assert.Equal("soffit", Assert.Single(Deriver.Derive(Massing.All(v), Massing.Rules).Elements).Kind);
    }

    [Theory]
    [InlineData(900, 2100, 0, "door")]
    [InlineData(1200, 1500, 900, "window")]
    [InlineData(3000, 2400, 0, "storefront")]
    [InlineData(8000, 2500, 200, "curtain_wall")]
    public void Openings_are_assumed_from_proportion_and_location(double w, double h, double sill, string kind)
    {
        var v = Massing.V("G", "room", Massing.F("n", Massing.N, width: 10000, height: 3000,
            openings: new OpeningCut { Id = "o1", Width = w, Height = h, Sill = sill }));
        var o = Deriver.Derive(Massing.All(v), Massing.Rules).Elements.Single(e => e.Source.Opening == "o1");
        Assert.Equal(kind, o.Kind);
        Assert.Equal("G:n", o.Host);
        Assert.True(o.Provenance.Assumed);
    }

    [Fact]
    public void An_explicit_opening_kind_skips_the_rules()
    {
        var v = Massing.V("G", "room", Massing.F("n", Massing.N, width: 10000, height: 3000,
            openings: new OpeningCut { Id = "o1", Width = 900, Height = 2100, Sill = 0, Kind = "window" }));
        var o = Deriver.Derive(Massing.All(v), Massing.Rules).Elements.Single(e => e.Source.Opening == "o1");
        Assert.Equal("window", o.Kind);
        Assert.False(o.Provenance.Assumed);
    }

    [Fact]
    public void Problems_are_reported_as_issues_not_errors()
    {
        var lonely = Massing.V("X", "room",
            Massing.F("e", Massing.E, adj: ("GHOST", "w")),
            Massing.F("top", Massing.Up, openings: new OpeningCut { Id = "sky", Width = 900, Height = 900, Sill = 0 }));
        var d = Deriver.Derive(Massing.All(lonely), Massing.Rules, intersections: [("X", "Y")]);
        Assert.Equal(["intersection", "missing_neighbor", "orphan_opening"], d.Issues.Select(i => i.Kind).Order());
        Assert.Contains(d.Elements, e => e.Id == "X:e" && e.Kind == "wall");
    }

    [Fact]
    public void Default_rule_files_pass_their_schemas()
    {
        var dir = Path.Combine(Repo.LibraryDir, "volume-rules");
        Assert.Empty(Repo.Contract.Schemas.Validate("face-rules.schema.json", JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "faces.json")))));
        Assert.Empty(Repo.Contract.Schemas.Validate("opening-rules.schema.json", JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "openings.json")))));
    }

    [Fact]
    public void Volume_input_round_trips_through_its_schema()
    {
        foreach (var v in new[] { Massing.G, Massing.U, Massing.P })
            Assert.Empty(Repo.Contract.Schemas.Validate("volume.schema.json", JsonSerializer.SerializeToNode(v, Json.Options)));
    }
}

public class VolumeEngineTests
{
    static JsonObject Vol(Volume v) => new() { ["volume"] = JsonSerializer.SerializeToNode(v, Json.Options) };

    static JsonObject In(string json) => JsonNode.Parse(json)!.AsObject();

    static Engine Built()
    {
        var e = new Engine(Repo.Lib, Repo.Contract);
        foreach (var v in new[] { Massing.G, Massing.U, Massing.P })
            Assert.True(e.Execute("volume.set", Vol(v), Engine.Person).Ok);
        return e;
    }

    [Fact]
    public void Claude_rewrites_a_rule_in_plain_data_and_the_model_rebuilds_then_undo_restores_it()
    {
        var e = Built();
        Assert.Equal("interior_wall", Kind(e, "G:e1|P:w"));

        var rule = """{"set":"faces","rule":{"id":"adj.porch.exterior","when":{"adjacency":["shared"],"other_volume_type":["porch"],"orientation":["vertical"]},"then":{"element":"wall","side":"exterior"},"why":"The wall between a room and a porch is exterior."}}""";
        var r = e.Execute("rule.set", In(rule), Engine.Claude, "the wall to the porch is an outside wall");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("wall", Kind(e, "G:e1|P:w"));
        Assert.Contains("adj.porch.exterior", (string)e.Execute("element.explain", In("""{"id":"G:e1|P:w"}"""), Engine.Person).Output!["sentence"]!);

        e.Execute("history.undo", null, Engine.Person);
        Assert.Equal("interior_wall", Kind(e, "G:e1|P:w"));
    }

    [Fact]
    public void Tagging_a_face_returns_the_new_element_and_is_one_undo_step()
    {
        var e = Built();
        var r = e.Execute("face.tag", In("""{"volume":"U","face":"s","tag":"curtain_wall"}"""), Engine.Person);
        Assert.Equal("curtain_wall", (string)r.Output!["kind"]!);
        e.Execute("history.undo", null, Engine.Person);
        Assert.Equal("wall", Kind(e, "U:s"));
    }

    [Fact]
    public void Removing_a_volume_rebuilds_its_neighbors()
    {
        var e = Built();
        e.Execute("volume.remove", In("""{"id":"U"}"""), Engine.Person);
        var d = e.Execute("elements.derive", null, Engine.Person).Output!;
        Assert.Equal("missing_neighbor", (string)d["issues"]![0]!["kind"]!);
        Assert.Null(d["counts"]!["floor"]);
    }

    [Theory]
    [InlineData("""{"set":"faces","rule":{"id":"bad","when":{},"then":{"element":"wall"}}}""")]
    [InlineData("""{"set":"faces","rule":{"id":"bad","when":{"orientation":["sideways"]},"then":{"element":"wall"}}}""")]
    [InlineData("""{"set":"openings","rule":{"id":"bad","when":{"width_ratio_min":2},"then":{"element":"door"}}}""")]
    public void Malformed_rules_are_refused(string input)
    {
        var e = Built();
        var r = e.Execute("rule.set", In(input), Engine.Claude);
        Assert.False(r.Ok);
        Assert.Contains("Invalid", r.Error);
    }

    [Fact]
    public void A_rule_can_be_placed_before_another_and_removed()
    {
        var e = Built();
        e.Execute("rule.set", In("""{"set":"openings","rule":{"id":"open.slot","when":{"height_min":2800},"then":{"element":"slot_window"}},"before":"open.door"}"""), Engine.Person);
        var ids = e.Execute("rule.list", In("""{"set":"openings"}"""), Engine.Person).Output!["rules"]!.AsArray().Select(r => (string)r!["id"]!).ToList();
        Assert.Equal(ids.IndexOf("open.door") - 1, ids.IndexOf("open.slot"));
        Assert.True(e.Execute("rule.remove", In("""{"set":"openings","id":"open.slot"}"""), Engine.Person).Ok);
        Assert.False(e.Execute("rule.remove", In("""{"set":"openings","id":"open.slot"}"""), Engine.Person).Ok);
    }

    static string Kind(Engine e, string id) =>
        (string)e.Execute("elements.derive", null, Engine.Person).Output!["elements"]!.AsArray().Single(x => (string)x!["id"]! == id)!["kind"]!;
}

public class SessionTests
{
    sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Get() => Now;
    }

    static JsonObject In(string json) => JsonNode.Parse(json)!.AsObject();

    static (Engine, Clock) New()
    {
        var c = new Clock();
        return (new Engine(Repo.Lib, Repo.Contract, clock: c.Get), c);
    }

    static void Rename(Engine e, string name, string author = Engine.Person, string? request = null) =>
        Assert.True(e.Execute("assembly.rename", In($$"""{"id":"EW-1","name":"{{name}}"}"""), author, request).Ok);

    [Fact]
    public void Sessions_split_on_a_long_pause_or_a_new_day()
    {
        var (e, c) = New();
        Rename(e, "a");
        c.Now = c.Now.AddMinutes(30); Rename(e, "b");
        c.Now = c.Now.AddHours(3); Rename(e, "c");
        c.Now = c.Now.AddDays(1).AddHours(-5); Rename(e, "d");
        Assert.Equal([1, 1, 2, 3], e.Steps.Select(s => s.Session));

        var list = e.Execute("session.list", null, Engine.Person).Output!.AsArray();
        Assert.Equal([3, 2, 1], list.Select(s => (int)s!["session"]!));
        Assert.Equal(2, (int)list[2]!["changes"]!);
        Assert.Equal("09:00", (string)list[2]!["start"]!);
    }

    [Fact]
    public void A_deck_has_one_slide_per_starred_change_each_traced_to_its_request()
    {
        var (e, c) = New();
        Rename(e, "a", Engine.Person, "call it a");
        c.Now = c.Now.AddMinutes(5);
        var gen = e.Execute("template.generate", In("""{"template":"IW-STEEL"}"""), Engine.Claude, "corridor partition please");
        c.Now = c.Now.AddMinutes(5);
        Rename(e, "b");
        e.Execute("change.star", In($$"""{"seq":{{gen.Seq}}}"""), Engine.Person);

        var deck = e.Execute("deck.make", null, Engine.Person).Output!;
        var slide = Assert.Single(deck["slides"]!.AsArray())!;
        Assert.Equal("claude", (string)slide["trace"]!["author"]!);
        Assert.Equal("corridor partition please", (string)slide["trace"]!["request"]!);
        Assert.Equal("2026-10-02 09:05", (string)slide["trace"]!["at"]!);
        Assert.Contains("assembly:IW-STEEL~gx~ms362@24+mw~gx", slide["changed"]!.AsArray().Select(x => (string)x!));
    }

    [Fact]
    public void Undone_changes_never_reach_a_deck_and_no_stars_means_every_change()
    {
        var (e, _) = New();
        Rename(e, "a");
        Rename(e, "b");
        e.Execute("history.undo", null, Engine.Person);
        var slides = e.Execute("deck.make", null, Engine.Person).Output!["slides"]!.AsArray();
        Assert.Equal(["Renamed EW-1"], slides.Select(s => (string)s!["title"]!));
        Assert.Single(slides);
    }

    [Fact]
    public void Volume_and_rule_changes_show_up_in_the_record()
    {
        var (e, _) = New();
        e.Execute("volume.set", new JsonObject { ["volume"] = JsonSerializer.SerializeToNode(Massing.G, Json.Options) }, Engine.Person);
        e.Execute("rule.remove", In("""{"set":"faces","id":"tag.open"}"""), Engine.Claude);
        var changed = e.Steps.Select(Sessions.Changed).ToList();
        Assert.Equal(["volume:G"], changed[0]);
        Assert.Equal(["rule:tag.open"], changed[1]);
    }
}
