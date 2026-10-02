using System.Text.Json.Nodes;

namespace ArchSet.Core;

/// <summary>
/// The session record and decks, read straight from the change log. Nobody writes the record by hand:
/// every change already carries its time, author, request and before/after state.
/// </summary>
public static class Sessions
{
    public static JsonNode List(IReadOnlyList<Step> steps) => new JsonArray(steps
        .GroupBy(s => s.Session)
        .OrderByDescending(g => g.Key)
        .Select(g => (JsonNode)new JsonObject
        {
            ["session"] = g.Key,
            ["date"] = g.First().At.ToString("yyyy-MM-dd"),
            ["start"] = g.First().At.ToString("HH:mm"),
            ["end"] = g.Last().At.ToString("HH:mm"),
            ["changes"] = g.Count(s => !s.Undone),
            ["starred"] = g.Count(s => s.Starred && !s.Undone),
            ["authors"] = new JsonArray(g.Select(s => s.Author).Distinct().Order().Select(a => (JsonNode)a).ToArray()),
        }).ToArray());

    public static JsonNode Get(IReadOnlyList<Step> steps, int session)
    {
        var list = InSession(steps, session);
        return new JsonObject
        {
            ["session"] = session,
            ["date"] = list[0].At.ToString("yyyy-MM-dd"),
            ["changes"] = new JsonArray(list.Select(s => (JsonNode)Entry(s)).ToArray()),
        };
    }

    /// <summary>
    /// One slide per starred change (or every change if none are starred), in the order they happened.
    /// Each slide carries a trace back to its change. Undone changes never appear.
    /// </summary>
    public static JsonNode Deck(IReadOnlyList<Step> steps, int session)
    {
        var live = InSession(steps, session).Where(s => !s.Undone).ToList();
        var picked = live.Any(s => s.Starred) ? live.Where(s => s.Starred).ToList() : live;
        return new JsonObject
        {
            ["session"] = session,
            ["date"] = live.FirstOrDefault()?.At.ToString("yyyy-MM-dd"),
            ["slides"] = new JsonArray(picked.Select((s, i) => (JsonNode)new JsonObject
            {
                ["n"] = i + 1,
                ["title"] = s.Summary,
                ["changed"] = new JsonArray(Changed(s).Select(c => (JsonNode)c).ToArray()),
                ["trace"] = Entry(s),
            }).ToArray()),
        };
    }

    static List<Step> InSession(IReadOnlyList<Step> steps, int session)
    {
        var list = steps.Where(s => s.Session == session).ToList();
        return list.Count > 0 ? list : throw new GeneratorException($"No session {session}.");
    }

    static JsonObject Entry(Step s) => new()
    {
        ["seq"] = s.Seq,
        ["at"] = s.At.ToString("yyyy-MM-dd HH:mm"),
        ["author"] = s.Author,
        ["op"] = s.Op,
        ["summary"] = s.Summary,
        ["request"] = s.Request,
        ["starred"] = s.Starred,
        ["undone"] = s.Undone,
    };

    /// <summary>What a change touched, by ID: assemblies, volumes, rules and assignments that differ before vs after.</summary>
    public static IEnumerable<string> Changed(Step s)
    {
        IEnumerable<string> Diff<T>(IReadOnlyDictionary<string, T> a, IReadOnlyDictionary<string, T> b, string prefix) =>
            a.Keys.Union(b.Keys).Order(StringComparer.Ordinal)
                .Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || !Equals(x, y))
                .Select(k => prefix + k);

        var (b4, af) = (s.Before, s.After);
        return Diff(b4.Assemblies, af.Assemblies, "assembly:")
            .Concat(Diff(b4.Volumes, af.Volumes, "volume:"))
            .Concat(Diff(b4.Assignments, af.Assignments, "object:"))
            .Concat(Diff(b4.Rules.Faces.ToDictionary(r => r.Id), af.Rules.Faces.ToDictionary(r => r.Id), "rule:"))
            .Concat(Diff(b4.Rules.Openings.ToDictionary(r => r.Id), af.Rules.Openings.ToDictionary(r => r.Id), "opening-rule:"));
    }
}
