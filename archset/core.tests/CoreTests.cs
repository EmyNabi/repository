using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArchSet.Core;

namespace ArchSet.Core.Tests;

public static class Repo
{
    public static readonly string LibraryDir = Library.FindRoot(AppContext.BaseDirectory, "library");
    public static readonly string SchemaDir = Library.FindRoot(AppContext.BaseDirectory, "schema");
    public static readonly Library Lib = Library.Load(LibraryDir);
    public static readonly Contract Contract = new(SchemaDir);
}

public class SchemaTests
{
    public static TheoryData<string, string> SeedFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var (dir, schema) in new[]
        {
            ("materials", "material.schema.json"), ("templates", "template.schema.json"),
            ("assemblies", "assembly.schema.json"), ("rules", "rule.schema.json"),
        })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(Repo.LibraryDir, dir), "*.json"))
                data.Add(Path.GetRelativePath(Repo.LibraryDir, f), schema);
        data.Add("pens.json", "pens.schema.json");
        return data;
    }

    [Theory]
    [MemberData(nameof(SeedFiles))]
    public void Seed_file_passes_its_schema(string file, string schema)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.LibraryDir, file)));
        Assert.Empty(Repo.Contract.Schemas.Validate(schema, doc));
    }

    [Fact]
    public void Schema_rejects_a_layer_with_no_thickness()
    {
        var doc = JsonNode.Parse("""{"id":"X","kind":"wall","name":"x","layers":[{"material":"wrb","function":"membrane"}]}""");
        Assert.NotEmpty(Repo.Contract.Schemas.Validate("assembly.schema.json", doc));
    }

    [Fact]
    public void Every_material_file_is_named_by_its_id()
    {
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Repo.LibraryDir, "materials")))
            Assert.Equal(Path.GetFileNameWithoutExtension(f), (string)JsonNode.Parse(File.ReadAllText(f))!["id"]!);
    }

    [Fact]
    public void Known_assemblies_pass_reference_checks_and_carry_no_unsourced_ratings()
    {
        foreach (var a in Repo.Lib.Known)
        {
            Assert.Empty(Repo.Lib.Check(a));
            var r = a.Ratings ?? Ratings.Unsourced;
            if (r.Source is null) Assert.True(r.FireHr is null && r.Stc is null && r.RValue is null, $"{a.Id} has ratings without a source");
        }
    }
}

public class GeneratorTests
{
    static Template Ew => Repo.Lib.Template("EW-WOOD");

    [Fact]
    public void Defaults_produce_a_valid_assembly_with_computed_thickness()
    {
        var a = Generator.Expand(Ew, null, Repo.Lib);
        Assert.Empty(Repo.Lib.Check(a));
        Assert.Equal(a.Layers.Sum(l => l.Thickness), a.Thickness);
        Assert.Equal("EW-WOOD~fc~ply~2x6@16+fg~gx", a.Id);
        Assert.Equal("Exterior wood stud, fiber cement lap siding", a.Name);
    }

    [Fact]
    public void Parameters_change_the_stack_and_the_id()
    {
        var a = Generator.Expand(Ew, new Dictionary<string, object?>
        {
            ["structure"] = "stud-2x8", ["structure.spacing"] = 609.6, ["ci.thickness"] = 50, ["gypsum.count"] = 2,
        }, Repo.Lib);
        Assert.Equal("EW-WOOD~fc~mwb50~ply~2x8@24+fg~gxx2", a.Id);
        Assert.Contains(a.Layers, l => l.Material == "mw-board" && l.Thickness == 50);
        Assert.Equal(2, a.Layers.Count(l => l.Material == "gwb-x-15.9"));
        Assert.Equal(609.6, a.Layers.Single(l => l.Function == "structure").Spacing);
    }

    [Fact]
    public void Zero_thickness_omits_the_layer_and_collapses_equivalent_variants()
    {
        var mw = Generator.Expand(Ew, new Dictionary<string, object?> { ["ci"] = "mw-board", ["ci.thickness"] = 0 }, Repo.Lib);
        var xps = Generator.Expand(Ew, new Dictionary<string, object?> { ["ci"] = "xps", ["ci.thickness"] = 0 }, Repo.Lib);
        Assert.DoesNotContain(mw.Layers, l => l.Function == "insulation");
        Assert.Equal(mw.Id, xps.Id);
    }

    [Fact]
    public void Cavity_fill_none_leaves_the_cavity_empty()
    {
        var a = Generator.Expand(Ew, new Dictionary<string, object?> { ["structure.fill"] = "none" }, Repo.Lib);
        Assert.Null(a.Layers.Single(l => l.Function == "structure").CavityFill);
        Assert.Contains("+0", a.Id);
    }

    [Theory]
    [InlineData("structure", "stud-2x10")]
    [InlineData("structure.spacing", 500.0)]
    [InlineData("nonsense", "x")]
    public void Invalid_parameters_are_refused_with_the_options(string name, object value)
    {
        var e = Assert.Throws<GeneratorException>(() => Generator.Expand(Ew, new Dictionary<string, object?> { [name] = value }, Repo.Lib));
        Assert.Contains(name == "nonsense" ? "Parameters:" : "Options:", e.Message);
    }

    [Fact]
    public void Generated_variants_never_carry_ratings_unless_they_match_a_known_assembly()
    {
        foreach (var t in Repo.Lib.Templates.Values)
            foreach (var a in Generator.Enumerate(t, Repo.Lib).Take(500))
            {
                var known = Repo.Lib.Known.FirstOrDefault(k => Generator.Signature(k) == Generator.Signature(a));
                Assert.Equal(known?.Ratings ?? Ratings.Unsourced, a.Ratings);
            }
    }

    [Fact]
    public void A_variant_matching_a_known_assembly_copies_its_cited_ratings()
    {
        var cited = new Ratings { FireHr = 1, Source = "test-fixture" };
        var known = Generator.Expand(Ew, null, Repo.Lib) with { Id = "EW-KNOWN", Ratings = cited, Template = null };
        var lib = new Library { Materials = Repo.Lib.Materials, Templates = Repo.Lib.Templates, Known = [known] };
        var a = Generator.Expand(Ew, null, lib);
        Assert.Equal(cited, a.Ratings);
        Assert.Contains("EW-KNOWN", a.Notes);
    }

    [Fact]
    public void Seed_example_EW_1_is_the_EW_WOOD_default()
    {
        var ew1 = Repo.Lib.Known.Single(k => k.Id == "EW-1");
        Assert.Equal(Generator.Signature(ew1), Generator.Signature(Generator.Expand(Ew, null, Repo.Lib)));
    }

    [Fact]
    public void Every_template_expands_every_variant_to_a_valid_assembly()
    {
        foreach (var t in Repo.Lib.Templates.Values)
        {
            var variants = Generator.Enumerate(t, Repo.Lib).ToList();
            Assert.NotEmpty(variants);
            Assert.Equal(variants.Count, variants.Select(v => v.Id).Distinct().Count());
            foreach (var a in variants)
            {
                Assert.Empty(Repo.Lib.Check(a));
                var doc = JsonSerializer.SerializeToNode(a, Json.Options);
                Assert.Empty(Repo.Contract.Schemas.Validate("assembly.schema.json", doc));
            }
        }
    }

    [Fact]
    public void Metric_template_uses_only_metric_or_unit_neutral_materials()
    {
        var a = Generator.Expand(Repo.Lib.Template("EW-WOOD-M"), null, Repo.Lib);
        Assert.All(a.Layers, l => Assert.NotEqual("imperial", Repo.Lib.Material(l.Material).Units));
        Assert.Contains("@600", a.Id);
    }
}

public class EngineTests
{
    static Engine New() => new(Repo.Lib, Repo.Contract);

    static JsonObject In(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Claude_and_a_person_make_the_same_change_the_same_way()
    {
        var byPerson = New();
        var byClaude = New();
        var input = """{"template":"EW-WOOD","params":{"structure":"stud-2x8"}}""";
        Assert.True(byPerson.Execute("template.generate", In(input), Engine.Person).Ok);
        Assert.True(byClaude.Execute("template.generate", In(input), Engine.Claude).Ok);
        Assert.Equal(byPerson.State.Assemblies.Keys, byClaude.State.Assemblies.Keys);
        Assert.Equal(Engine.Claude, byClaude.Steps.Single().Author);
    }

    [Fact]
    public void Claude_creates_and_assigns_and_the_person_undoes_it_step_by_step()
    {
        var e = New();
        var start = e.State;
        var gen = e.Execute("template.generate", In("""{"template":"IW-STEEL"}"""), Engine.Claude);
        Assert.True(gen.Ok, gen.Error);
        var id = (string)gen.Output!["id"]!;
        Assert.True(e.Execute("object.assign", In($$"""{"objects":["wall-1","wall-2"],"assembly":"{{id}}"}"""), Engine.Claude).Ok);
        Assert.Equal(2, e.State.Assignments.Count);

        Assert.True(e.Execute("history.undo", null, Engine.Person).Ok);
        Assert.Empty(e.State.Assignments);
        Assert.True(e.Execute("history.undo", null, Engine.Person).Ok);
        Assert.Same(start, e.State);

        Assert.True(e.Execute("history.redo", null, Engine.Person).Ok);
        Assert.Contains(id, e.State.Assemblies.Keys);

        var log = e.Execute("history.log", null, Engine.Person).Output!.AsArray();
        Assert.Equal(["claude", "claude"], log.Select(s => (string)s!["author"]!));
        Assert.Equal([false, true], log.Select(s => (bool)s!["undone"]!));
    }

    [Fact]
    public void A_new_change_clears_redo()
    {
        var e = New();
        e.Execute("assembly.rename", In("""{"id":"EW-1","name":"A"}"""), Engine.Person);
        e.Execute("history.undo", null, Engine.Person);
        e.Execute("assembly.rename", In("""{"id":"EW-1","name":"B"}"""), Engine.Claude);
        Assert.False(e.Execute("history.redo", null, Engine.Person).Ok);
        Assert.Equal("B", e.State.Assemblies["EW-1"].Name);
    }

    [Fact]
    public void Layer_edits_reorder_insert_and_remove()
    {
        var e = New();
        var before = e.State.Assemblies["EW-1"];
        Assert.True(e.Execute("assembly.move_layer", In("""{"id":"EW-1","from":0,"to":5}"""), Engine.Person).Ok);
        Assert.Equal(before.Layers[0], e.State.Assemblies["EW-1"].Layers[5]);
        Assert.True(e.Execute("assembly.insert_layer", In("""{"id":"EW-1","index":6,"layer":{"material":"gwb-x-15.9","thickness":15.875,"function":"finish_int"}}"""), Engine.Person).Ok);
        Assert.Equal(7, e.State.Assemblies["EW-1"].Layers.Count);
        Assert.True(e.Execute("assembly.remove_layer", In("""{"id":"EW-1","index":6}"""), Engine.Person).Ok);
        Assert.True(e.Execute("assembly.set_layer", In("""{"id":"EW-1","index":3,"thickness":184.15,"material":"stud-2x8"}"""), Engine.Person).Ok);
        Assert.Equal(before.Thickness - 139.7 + 184.15, e.State.Assemblies["EW-1"].Thickness, 3);
    }

    [Theory]
    [InlineData("assembly.set_layer", """{"id":"EW-1","index":0,"material":"no-such"}""", "unknown material")]
    [InlineData("assembly.set_layer", """{"id":"EW-1","index":0,"cavity_fill":"batt-fiberglass"}""", "framing")]
    [InlineData("assembly.set_layer", """{"id":"EW-1","index":99}""", "out of range")]
    [InlineData("assembly.set_layer", """{"id":"EW-1","index":0,"thickness":-5}""", "Invalid input")]
    [InlineData("assembly.get", """{"id":"NOPE"}""", "No assembly")]
    [InlineData("template.generate", """{"template":"EW-WOOD","params":{"structure":"stud-38x140"}}""", "not an option")]
    [InlineData("view.color_by", """{"attribute":"fire_hr"}""", "Rhino host")]
    [InlineData("nope.nope", "{}", "Unknown operation")]
    public void Bad_requests_are_refused_and_change_nothing(string op, string input, string error)
    {
        var e = New();
        var before = e.State;
        var r = e.Execute(op, In(input), Engine.Claude);
        Assert.False(r.Ok);
        Assert.Contains(error, r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Same(before, e.State);
        Assert.Empty(e.Steps);
    }

    [Fact]
    public void An_assembly_in_use_cannot_be_deleted()
    {
        var e = New();
        e.Execute("object.assign", In("""{"objects":["w"],"assembly":"EW-1"}"""), Engine.Person);
        Assert.Contains("clear them first", e.Execute("assembly.delete", In("""{"id":"EW-1"}"""), Engine.Claude).Error);
        e.Execute("object.clear", In("""{"objects":["w"]}"""), Engine.Person);
        Assert.True(e.Execute("assembly.delete", In("""{"id":"EW-1"}"""), Engine.Claude).Ok);
    }

    [Fact]
    public void Editing_a_layer_drops_cited_ratings()
    {
        var cited = Repo.Lib.Known[0] with { Ratings = new Ratings { FireHr = 1, Source = "UL test" } };
        var e = new Engine(Repo.Lib, Repo.Contract, ProjectState.From([cited]));
        e.Execute("assembly.set_layer", In($$"""{"id":"{{cited.Id}}","index":5,"material":"gwb-12.7","thickness":12.7}"""), Engine.Claude);
        Assert.Equal(Ratings.Unsourced, e.State.Assemblies[cited.Id].Ratings);
    }

    [Fact]
    public void Template_list_reports_parameters_and_variant_counts()
    {
        var list = New().Execute("template.list", null, Engine.Claude).Output!.AsArray();
        var ew = list.Single(t => (string)t!["id"]! == "EW-WOOD")!;
        Assert.True((int)ew["variants"]! > 100);
        Assert.Contains(ew["parameters"]!.AsArray(), p => (string)p!["name"]! == "structure.spacing");
    }
}

public class ContractTests
{
    [Fact]
    public void Every_core_operation_is_implemented()
    {
        var e = new Engine(Repo.Lib, Repo.Contract);
        foreach (var op in Repo.Contract.Operations.Values.Where(o => o.Host == "core"))
        {
            var r = e.Execute(op.Name, [], Engine.Person);
            Assert.DoesNotContain("not implemented", r.Error ?? "");
        }
    }

    [Fact]
    public void Mcp_tool_list_has_one_tool_per_operation_with_valid_names()
    {
        var tools = Repo.Contract.McpTools();
        Assert.Equal(Repo.Contract.Operations.Count, tools.Count);
        Assert.All(tools, t => Assert.Matches("^[a-z_]+$", (string)t!["name"]!));
    }
}
