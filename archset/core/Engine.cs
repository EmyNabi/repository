using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArchSet.Core;

/// <summary>The project's mutable state, held immutably so every step can be undone by restoring a snapshot.</summary>
public sealed record ProjectState(
    ImmutableSortedDictionary<string, Assembly> Assemblies,
    ImmutableSortedDictionary<string, string> Assignments)
{
    public static ProjectState Empty { get; } = new(
        ImmutableSortedDictionary.Create<string, Assembly>(StringComparer.Ordinal),
        ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal));

    public static ProjectState From(IEnumerable<Assembly> assemblies) =>
        Empty with { Assemblies = assemblies.ToImmutableSortedDictionary(a => a.Id, a => a, StringComparer.Ordinal) };
}

public sealed record Step(int Seq, string Op, string Author, string Summary, ProjectState Before, ProjectState After)
{
    public bool Undone { get; set; }
}

public sealed record OpResult(bool Ok, JsonNode? Output = null, string? Error = null, int? Seq = null)
{
    public static OpResult Fail(string error) => new(false, Error: error);
}

/// <summary>
/// Runs the operations in schema/operations.json. The panel and Claude (over MCP) both call Execute;
/// the only difference between them is the author string recorded in the log.
/// </summary>
public sealed class Engine
{
    public const string Person = "person";
    public const string Claude = "claude";

    readonly Library _lib;
    readonly Contract _contract;
    readonly List<Step> _steps = [];
    readonly Stack<Step> _redo = new();
    int _seq;

    public ProjectState State { get; private set; }
    public IReadOnlyList<Step> Steps => _steps;

    public Engine(Library lib, Contract contract, ProjectState? initial = null)
    {
        _lib = lib;
        _contract = contract;
        State = initial ?? ProjectState.From(lib.Known);
    }

    public OpResult Execute(string op, JsonObject? input, string author)
    {
        if (!_contract.Operations.TryGetValue(op, out var spec)) return OpResult.Fail($"Unknown operation '{op}'.");
        if (spec.Host != "core") return OpResult.Fail($"'{op}' needs the Rhino host.");
        input ??= [];
        var invalid = _contract.ValidateInput(op, input);
        if (invalid.Count > 0) return OpResult.Fail($"Invalid input for {op}: {string.Join("; ", invalid)}");

        try
        {
            return op switch
            {
                "library.list" => Ok(ListLibrary((string?)input["kind"])),
                "assembly.get" => Ok(Describe(Get((string)input["id"]!))),
                "template.list" => Ok(ListTemplates()),
                "template.preview" => Ok(Describe(Generator.Expand(_lib.Template((string)input["template"]!), Params(input), _lib))),
                "history.undo" => Undo(),
                "history.redo" => Redo(),
                "history.log" => Ok(Log((int?)input["limit"])),
                _ => Mutate(op, input, author),
            };
        }
        catch (GeneratorException e) { return OpResult.Fail(e.Message); }
    }

    OpResult Mutate(string op, JsonObject input, string author)
    {
        var before = State;
        var (after, summary, output) = op switch
        {
            "assembly.create" => Create(input),
            "assembly.rename" => Update(input, a => a with { Name = (string)input["name"]! }, a => $"Renamed {a.Id}"),
            "assembly.set_layer" => SetLayer(input),
            "assembly.insert_layer" => Update(input, a => a with
            {
                Layers = a.Layers.Insert(Index(input, "index", a.Layers.Count + 1), Deserialize<Layer>(input["layer"]!))
            }, a => $"Inserted a layer in {a.Id}"),
            "assembly.remove_layer" => Update(input, a => a.Layers.Count == 1
                ? throw new GeneratorException($"{a.Id} must keep at least one layer.")
                : a with { Layers = a.Layers.RemoveAt(Index(input, "index", a.Layers.Count)) }, a => $"Removed a layer from {a.Id}"),
            "assembly.move_layer" => Update(input, a =>
            {
                var from = Index(input, "from", a.Layers.Count);
                var to = Index(input, "to", a.Layers.Count);
                var layer = a.Layers[from];
                return a with { Layers = a.Layers.RemoveAt(from).Insert(to, layer) };
            }, a => $"Moved a layer in {a.Id}"),
            "assembly.delete" => Delete((string)input["id"]!),
            "template.generate" => Generate(input),
            "object.assign" => Assign(input),
            "object.clear" => Clear(input),
            _ => throw new GeneratorException($"'{op}' is in the contract but not implemented in core."),
        };

        var step = new Step(++_seq, op, author, summary, before, after);
        _steps.Add(step);
        _redo.Clear();
        State = after;
        return new OpResult(true, output, Seq: step.Seq);
    }

    (ProjectState, string, JsonNode?) Create(JsonObject input)
    {
        var a = Deserialize<Assembly>(input["assembly"]!);
        if (State.Assemblies.ContainsKey(a.Id)) throw new GeneratorException($"Assembly {a.Id} already exists.");
        Checked(a);
        return (State with { Assemblies = State.Assemblies.Add(a.Id, a) }, $"Created {a.Id}", Describe(a));
    }

    (ProjectState, string, JsonNode?) Update(JsonObject input, Func<Assembly, Assembly> change, Func<Assembly, string> summary)
    {
        var a = change(Get((string)input["id"]!));
        Checked(a);
        return (State with { Assemblies = State.Assemblies.SetItem(a.Id, a) }, summary(a), Describe(a));
    }

    (ProjectState, string, JsonNode?) SetLayer(JsonObject input) => Update(input, a =>
    {
        var i = Index(input, "index", a.Layers.Count);
        var l = a.Layers[i];
        l = l with
        {
            Material = (string?)input["material"] ?? l.Material,
            Thickness = (double?)input["thickness"] ?? l.Thickness,
            Function = (string?)input["function"] ?? l.Function,
            Core = (bool?)input["core"] ?? l.Core,
            Spacing = (double?)input["spacing"] ?? l.Spacing,
            CavityFill = input.ContainsKey("cavity_fill") ? (string?)input["cavity_fill"] : l.CavityFill,
        };
        // A hand edit means the stack no longer matches whatever its ratings were cited for.
        var ratings = a.Ratings?.Source is null ? a.Ratings : Ratings.Unsourced;
        return a with { Layers = a.Layers.SetItem(i, l), Ratings = ratings };
    }, a => $"Edited layer {(int)input["index"]!} of {a.Id}");

    (ProjectState, string, JsonNode?) Delete(string id)
    {
        Get(id);
        var users = State.Assignments.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList();
        if (users.Count > 0) throw new GeneratorException($"{id} is assigned to {users.Count} object(s); clear them first.");
        return (State with { Assemblies = State.Assemblies.Remove(id) }, $"Deleted {id}", null);
    }

    (ProjectState, string, JsonNode?) Generate(JsonObject input)
    {
        var a = Generator.Expand(_lib.Template((string)input["template"]!), Params(input), _lib);
        Checked(a);
        if (State.Assemblies.TryGetValue(a.Id, out var existing))
            return (State, $"{a.Id} already in library", Describe(existing));
        return (State with { Assemblies = State.Assemblies.Add(a.Id, a) }, $"Generated {a.Id}", Describe(a));
    }

    (ProjectState, string, JsonNode?) Assign(JsonObject input)
    {
        var id = (string)input["assembly"]!;
        Get(id);
        var objects = input["objects"]!.AsArray().Select(o => (string)o!).ToList();
        var map = State.Assignments;
        foreach (var o in objects) map = map.SetItem(o, id);
        return (State with { Assignments = map }, $"Assigned {id} to {objects.Count} object(s)", null);
    }

    (ProjectState, string, JsonNode?) Clear(JsonObject input)
    {
        var objects = input["objects"]!.AsArray().Select(o => (string)o!).ToList();
        return (State with { Assignments = State.Assignments.RemoveRange(objects) }, $"Cleared {objects.Count} object(s)", null);
    }

    OpResult Undo()
    {
        var step = _steps.LastOrDefault(s => !s.Undone);
        if (step is null) return OpResult.Fail("Nothing to undo.");
        step.Undone = true;
        State = step.Before;
        _redo.Push(step);
        return new OpResult(true, new JsonObject { ["undid"] = step.Seq, ["summary"] = step.Summary, ["author"] = step.Author });
    }

    OpResult Redo()
    {
        if (!_redo.TryPop(out var step)) return OpResult.Fail("Nothing to redo.");
        step.Undone = false;
        State = step.After;
        return new OpResult(true, new JsonObject { ["redid"] = step.Seq, ["summary"] = step.Summary, ["author"] = step.Author });
    }

    JsonNode Log(int? limit) => new JsonArray(_steps
        .TakeLast(limit ?? int.MaxValue)
        .Select(s => (JsonNode)new JsonObject
        {
            ["seq"] = s.Seq, ["op"] = s.Op, ["author"] = s.Author, ["summary"] = s.Summary, ["undone"] = s.Undone,
        }).ToArray());

    JsonNode ListLibrary(string? kind) => new JsonArray(State.Assemblies.Values
        .Where(a => kind is null || a.Kind == kind)
        .Select(a => (JsonNode)new JsonObject
        {
            ["id"] = a.Id, ["kind"] = a.Kind, ["name"] = a.Name,
            ["thickness"] = Math.Round(a.Thickness, 2), ["layers"] = a.Layers.Count,
            ["template"] = a.Template?.Id,
        }).ToArray());

    JsonNode ListTemplates() => new JsonArray(_lib.Templates.Values.Select(t => (JsonNode)new JsonObject
    {
        ["id"] = t.Id, ["kind"] = t.Kind, ["name"] = t.Name, ["units"] = t.Units,
        ["variants"] = Generator.Enumerate(t, _lib).Count(),
        ["parameters"] = new JsonArray(t.Parameters().Select(p => (JsonNode)new JsonObject
        {
            ["name"] = p.Name, ["label"] = p.Label,
            ["options"] = JsonSerializer.SerializeToNode(p.Options), ["default"] = JsonSerializer.SerializeToNode(p.Default),
        }).ToArray()),
    }).ToArray());

    static JsonNode Describe(Assembly a)
    {
        var node = JsonSerializer.SerializeToNode(a, Json.Options)!.AsObject();
        node["thickness"] = Math.Round(a.Thickness, 2);
        return node;
    }

    Assembly Get(string id) =>
        State.Assemblies.TryGetValue(id, out var a) ? a : throw new GeneratorException($"No assembly '{id}'.");

    void Checked(Assembly a)
    {
        var errors = _lib.Check(a);
        if (errors.Count > 0) throw new GeneratorException(string.Join(" ", errors));
    }

    static int Index(JsonObject input, string key, int count)
    {
        var i = (int)input[key]!;
        return i < count ? i : throw new GeneratorException($"{key} {i} is out of range (0..{count - 1}).");
    }

    static Dictionary<string, object?>? Params(JsonObject input) =>
        input["params"]?.AsObject().ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    static T Deserialize<T>(JsonNode node) => node.Deserialize<T>(Json.Options)!;

    static OpResult Ok(JsonNode? output) => new(true, output);
}
