using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArchSet.Core;

/// <summary>The project's mutable state, held immutably so every step can be undone by restoring a snapshot.</summary>
/// <summary>
/// The project's mutable state, held immutably so every step can be undone by restoring a snapshot.
/// Volumes and rules are stored; building elements are always derived from them, never stored.
/// </summary>
public sealed record ProjectState
{
    public required ImmutableSortedDictionary<string, Assembly> Assemblies { get; init; }
    public required ImmutableSortedDictionary<string, string> Assignments { get; init; }
    public required ImmutableSortedDictionary<string, Volume> Volumes { get; init; }
    public required RuleSet Rules { get; init; }
    public double Grade { get; init; }
    public ImmutableList<(string A, string B)> Intersections { get; init; } = [];

    public static readonly RuleSet NoRules = new(new VolumeSettings(), [], []);

    public static ProjectState Empty { get; } = new()
    {
        Assemblies = ImmutableSortedDictionary.Create<string, Assembly>(StringComparer.Ordinal),
        Assignments = ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal),
        Volumes = ImmutableSortedDictionary.Create<string, Volume>(StringComparer.Ordinal),
        Rules = NoRules,
    };

    public static ProjectState From(IEnumerable<Assembly> assemblies, RuleSet? rules = null) => Empty with
    {
        Assemblies = assemblies.ToImmutableSortedDictionary(a => a.Id, a => a, StringComparer.Ordinal),
        Rules = rules ?? NoRules,
    };

    public Derivation Derive() => Deriver.Derive(Volumes, Rules, Grade, Intersections);
}

/// <summary>One entry in the session record: what changed, who did it, when, and what they asked for.</summary>
public sealed record Step(int Seq, string Op, string Author, string Summary, ProjectState Before, ProjectState After)
{
    public DateTimeOffset At { get; init; }
    public int Session { get; init; }
    /// <summary>The words that led to this change, verbatim (the person's or Claude's request).</summary>
    public string? Request { get; init; }
    public bool Undone { get; set; }
    public bool Starred { get; set; }
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
    readonly Func<DateTimeOffset> _clock;
    readonly TimeSpan _sessionGap;
    readonly List<Step> _steps = [];
    readonly Stack<Step> _redo = new();
    int _seq;
    string? _request;

    public ProjectState State { get; private set; }
    public IReadOnlyList<Step> Steps => _steps;

    /// <param name="sessionGap">A pause longer than this, or a new calendar day, starts a new session.</param>
    public Engine(Library lib, Contract contract, ProjectState? initial = null, Func<DateTimeOffset>? clock = null, TimeSpan? sessionGap = null)
    {
        _lib = lib;
        _contract = contract;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _sessionGap = sessionGap ?? TimeSpan.FromHours(2);
        State = initial ?? ProjectState.From(lib.Known, lib.VolumeRules);
    }

    /// <param name="request">The words that led to this call, kept verbatim in the session record.</param>
    public OpResult Execute(string op, JsonObject? input, string author, string? request = null)
    {
        _request = request;
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
                "elements.derive" => Ok(DeriveElements((string?)input["kind"])),
                "element.explain" => Ok(Explain((string)input["id"]!)),
                "rule.list" => Ok(ListRules((string)input["set"]!)),
                "session.list" => Ok(Sessions.List(_steps)),
                "session.get" => Ok(Sessions.Get(_steps, (int?)input["session"] ?? LastSession())),
                "change.star" => Star((int)input["seq"]!, (bool?)input["starred"] ?? true),
                "deck.make" => Ok(Sessions.Deck(_steps, (int?)input["session"] ?? LastSession())),
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
            "volume.set" => SetVolume(input),
            "volume.remove" => RemoveVolume((string)input["id"]!),
            "face.tag" => TagFace(input),
            "massing.set" => SetMassing(input),
            "rule.set" => SetRule(input),
            "rule.remove" => RemoveRule((string)input["set"]!, (string)input["id"]!),
            _ => throw new GeneratorException($"'{op}' is in the contract but not implemented in core."),
        };

        var now = _clock();
        var last = _steps.LastOrDefault();
        var session = last is null ? 1
            : now - last.At > _sessionGap || now.Date != last.At.Date ? last.Session + 1
            : last.Session;
        var step = new Step(++_seq, op, author, summary, before, after) { At = now, Session = session, Request = _request };
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

    // ---- Volumes and rules ----

    (ProjectState, string, JsonNode?) SetVolume(JsonObject input)
    {
        var v = Deserialize<Volume>(input["volume"]!);
        var dup = v.Faces.GroupBy(f => f.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new GeneratorException($"Volume {v.Id} has two faces named {dup.Key}.");
        var verb = State.Volumes.ContainsKey(v.Id) ? "Updated" : "Added";
        var after = State with { Volumes = State.Volumes.SetItem(v.Id, v) };
        return (after, $"{verb} volume {v.Id}", Counts(after.Derive(), v.Id));
    }

    (ProjectState, string, JsonNode?) RemoveVolume(string id)
    {
        if (!State.Volumes.ContainsKey(id)) throw new GeneratorException($"No volume '{id}'.");
        return (State with { Volumes = State.Volumes.Remove(id) }, $"Removed volume {id}", null);
    }

    (ProjectState, string, JsonNode?) TagFace(JsonObject input)
    {
        var vid = (string)input["volume"]!;
        var fid = (string)input["face"]!;
        var tag = (string?)input["tag"];
        if (!State.Volumes.TryGetValue(vid, out var v)) throw new GeneratorException($"No volume '{vid}'.");
        var i = v.Faces.FindIndex(f => f.Id == fid);
        if (i < 0) throw new GeneratorException($"Volume {vid} has no face '{fid}'.");
        var nv = v with { Faces = v.Faces.SetItem(i, v.Faces[i] with { Tag = tag }) };
        var after = State with { Volumes = State.Volumes.SetItem(vid, nv) };
        var element = after.Derive().Elements.FirstOrDefault(e => e.Source.Volume == vid && e.Source.Face == fid && e.Source.Opening is null);
        return (after, tag is null ? $"Cleared the tag on {vid}:{fid}" : $"Tagged {vid}:{fid} {tag}",
            element is null ? new JsonObject { ["element"] = "none" } : JsonSerializer.SerializeToNode(element, Json.Options));
    }

    (ProjectState, string, JsonNode?) SetMassing(JsonObject input)
    {
        var after = State;
        if (input["grade"] is JsonNode g) after = after with { Grade = (double)g };
        if (input["intersections"] is JsonArray pairs)
            after = after with { Intersections = pairs.Select(p => ((string)p![0]!, (string)p[1]!)).ToImmutableList() };
        return (after, "Updated massing settings", null);
    }

    (ProjectState, string, JsonNode?) SetRule(JsonObject input)
    {
        var set = (string)input["set"]!;
        var node = input["rule"]!;
        var errors = _contract.ValidateRule(set, node);
        if (errors.Count > 0) throw new GeneratorException($"Invalid {set} rule: {string.Join("; ", errors)}");
        var r = State.Rules;
        var before = (string?)input["before"];
        RuleSet next;
        string id;
        if (set == "faces")
        {
            var rule = Deserialize<FaceRule>(node);
            id = rule.Id;
            next = r with { Faces = Place(r.Faces, rule, x => x.Id, before) };
        }
        else
        {
            var rule = Deserialize<OpeningRule>(node);
            id = rule.Id;
            next = r with { Openings = Place(r.Openings, rule, x => x.Id, before) };
        }
        var verb = (set == "faces" ? r.Faces.Any(x => x.Id == id) : r.Openings.Any(x => x.Id == id)) ? "Changed" : "Added";
        var after = State with { Rules = next };
        return (after, $"{verb} {set} rule {id}", Counts(after.Derive(), null));
    }

    static ImmutableList<T> Place<T>(ImmutableList<T> list, T item, Func<T, string> id, string? before)
    {
        var at = list.FindIndex(x => id(x) == id(item));
        if (at >= 0 && before is null) return list.SetItem(at, item);
        if (at >= 0) list = list.RemoveAt(at);
        var b = before is null ? -1 : list.FindIndex(x => id(x) == before);
        if (before is not null && b < 0) throw new GeneratorException($"No rule '{before}' to place before.");
        return b < 0 ? list.Add(item) : list.Insert(b, item);
    }

    (ProjectState, string, JsonNode?) RemoveRule(string set, string id)
    {
        var r = State.Rules;
        var next = set == "faces"
            ? r with { Faces = r.Faces.RemoveAll(x => x.Id == id) }
            : r with { Openings = r.Openings.RemoveAll(x => x.Id == id) };
        if (next.Faces.Count == r.Faces.Count && next.Openings.Count == r.Openings.Count)
            throw new GeneratorException($"No {set} rule '{id}'.");
        return (State with { Rules = next }, $"Removed {set} rule {id}", null);
    }

    JsonNode DeriveElements(string? kind)
    {
        var d = State.Derive();
        var els = d.Elements.Where(e => kind is null || e.Kind == kind).ToList();
        return new JsonObject
        {
            ["counts"] = Counts(d, null),
            ["elements"] = JsonSerializer.SerializeToNode(els, Json.Options),
            ["issues"] = JsonSerializer.SerializeToNode(d.Issues, Json.Options),
        };
    }

    JsonNode Explain(string id)
    {
        var e = State.Derive().Elements.FirstOrDefault(x => x.Id == id)
            ?? throw new GeneratorException($"No element '{id}'. Element IDs look like volume:face, or volume:face#opening.");
        var rule = State.Rules.Faces.FirstOrDefault(r => r.Id == e.Provenance.Rule);
        var node = JsonSerializer.SerializeToNode(e, Json.Options)!.AsObject();
        node["sentence"] = $"{e.Id} is a {e.Kind.Replace('_', ' ')} because of rule {e.Provenance.Rule} ({e.Provenance.Level}): {e.Provenance.Why}"
            + (e.Provenance.Overruled.IsEmpty ? "" : $" It overrode {string.Join(", ", e.Provenance.Overruled)}.")
            + (e.Provenance.Assumed ? " This is a default; tag the face to override it." : "");
        return node;
    }

    JsonNode ListRules(string set) => set == "faces"
        ? new JsonObject
        {
            ["settings"] = JsonSerializer.SerializeToNode(State.Rules.Settings, Json.Options),
            ["rules"] = new JsonArray(State.Rules.Faces.Select(r =>
            {
                var n = JsonSerializer.SerializeToNode(r, Json.Options)!.AsObject();
                n["level"] = FaceRule.LevelName(r.Level);
                return (JsonNode)n;
            }).ToArray()),
        }
        : new JsonObject { ["rules"] = JsonSerializer.SerializeToNode(State.Rules.Openings, Json.Options) };

    static JsonObject Counts(Derivation d, string? volume) => new(d.Elements
        .Where(e => volume is null || e.Source.Volume == volume || e.Source.Other?.Volume == volume)
        .GroupBy(e => e.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count())));

    // ---- Session record ----

    int LastSession() => _steps.Count == 0 ? throw new GeneratorException("No changes recorded yet.") : _steps[^1].Session;

    OpResult Star(int seq, bool starred)
    {
        var s = _steps.FirstOrDefault(x => x.Seq == seq) ?? throw new GeneratorException($"No change {seq}.");
        s.Starred = starred;
        return Ok(new JsonObject { ["seq"] = seq, ["starred"] = starred });
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
