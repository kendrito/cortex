using System.Text.Json;

namespace Testy.Core;

/// <summary>Exact selectors over a complete preorder UI tree. No fuzzy matching or implicit healing.</summary>
public static class UiSelectors
{
    public const int MaximumQueryLevels = 8;
    private sealed record Query(string? Id, string? Type, string? Label, Query? Ancestor)
    {
        public bool Matches(UiElementInfo element) => (Id is null || element.AutomationId == Id)
            && (Type is null || element.ControlType == Type) && (Label is null || element.Name == Label);
    }

    public static void Validate(string selector) => Parse(selector);

    public static IReadOnlyList<UiElementInfo> Find(UiSnapshot snapshot, string selector)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var query = Parse(selector);
        if (snapshot.IsTruncated) throw new InvalidOperationException("The application UI tree was truncated. Selector uniqueness and absence cannot be verified.");
        if (query is null) return snapshot.Elements.Where(e => LegacyMatches(e, selector)).ToList();
        return FindQuery(snapshot, query);
    }
    private static IReadOnlyList<UiElementInfo> FindQuery(UiSnapshot snapshot, Query query)
    {
        var results = new List<UiElementInfo>();
        var ancestry = new List<UiElementInfo>();
        foreach (var element in snapshot.Elements)
        {
            if (element.Depth < 0 || (ancestry.Count == 0 && element.Depth != 0))
                throw new InvalidOperationException("A scoped selector requires a valid preorder UI tree with depth-zero window roots.");
            while (ancestry.Count > 0 && ancestry[^1].Depth >= element.Depth) ancestry.RemoveAt(ancestry.Count - 1);
            if (query.Matches(element) && AncestorsMatch(query.Ancestor, ancestry, ancestry.Count - 1)) results.Add(element);
            ancestry.Add(element);
        }
        return results;
    }

    /// <summary>Absence needs complete logical coverage, globally or within a unique stable-ID ancestor scope.</summary>
    public static bool HasCompleteAbsenceCoverage(UiSnapshot snapshot, string selector)
    {
        if (snapshot.IsTruncated || Find(snapshot, selector).Count != 0) return false;
        if (snapshot.Elements.All(e => e.ChildCoverage == ChildCoverage.Complete)) return true;
        var query = Parse(selector);
        if (query?.Ancestor?.Id is null) return false;
        var scopes = FindQuery(snapshot, query.Ancestor);
        if (scopes.Count != 1) return false;
        var scope = scopes[0]; var start = snapshot.Elements.IndexOf(scope);
        // A scope inside an incompletely observed collection is not a stable absence boundary.
        var depth = scope.Depth;
        for (var index = start - 1; index >= 0 && depth > 0; index--)
            if (snapshot.Elements[index].Depth < depth)
            {
                if (snapshot.Elements[index].ChildCoverage != ChildCoverage.Complete) return false;
                depth = snapshot.Elements[index].Depth;
            }
        if (scope.ChildCoverage != ChildCoverage.Complete) return false;
        for (var index = start + 1; index < snapshot.Elements.Count && snapshot.Elements[index].Depth > scope.Depth; index++)
            if (snapshot.Elements[index].ChildCoverage != ChildCoverage.Complete) return false;
        return true;
    }

    /// <summary>Use the snapshot overload for queries containing an ancestor constraint.</summary>
    public static bool Matches(UiElementInfo element, string selector)
    {
        var query = Parse(selector);
        if (query?.Ancestor is not null) throw new InvalidOperationException("Matching an ancestor selector requires its UI snapshot.");
        return query?.Matches(element) ?? LegacyMatches(element, selector);
    }

    public static bool Matches(UiSnapshot snapshot, UiElementInfo element, string selector) =>
        Find(snapshot, selector).Any(candidate => ReferenceEquals(candidate, element));

    private static bool AncestorsMatch(Query? query, IReadOnlyList<UiElementInfo> ancestors, int upper)
    {
        // The nearest matching ancestor leaves the largest possible outer ancestry.
        // Greedy matching is sufficient for this strict ancestor chain and avoids combinatorial backtracking.
        for (int index = upper; query is not null && index >= 0; index--)
            if (query.Matches(ancestors[index])) query = query.Ancestor;
        return query is null;
    }

    private static bool LegacyMatches(UiElementInfo element, string selector)
    {
        if (string.Equals(element.Selector, selector, StringComparison.Ordinal)) return true;
        if (selector.StartsWith("id:", StringComparison.Ordinal)) return element.AutomationId == selector[3..];
        if (selector.StartsWith("name:", StringComparison.Ordinal)) return element.Name == selector[5..];
        return false;
    }

    private static Query? Parse(string selector)
    {
        if (selector is null || selector.Length is 0 or > 2048) throw new InvalidDataException("Selectors must contain 1–2048 characters.");
        foreach (string prefix in new[] { "id:", "name:", "path:" })
            if (selector.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(selector[prefix.Length..])) throw new InvalidDataException($"Selector '{prefix}' requires a value.");
                return null;
            }
        if (!selector.StartsWith("query:", StringComparison.Ordinal)) throw new InvalidDataException("Use an explicit id:, name:, path:, or query: selector.");
        try
        {
            using var document = JsonDocument.Parse(selector[6..], new JsonDocumentOptions { MaxDepth = MaximumQueryLevels + 1 });
            return ParseQuery(document.RootElement, 1);
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid query selector JSON: " + ex.Message, ex); }
    }

    private static Query ParseQuery(JsonElement value, int level)
    {
        if (level > MaximumQueryLevels) throw new InvalidDataException($"A query selector supports at most {MaximumQueryLevels} levels.");
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Every query selector level must be a JSON object.");
        string? id = null, type = null, label = null; Query? ancestor = null;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.Add(property.Name)) throw new InvalidDataException($"Duplicate query selector field '{property.Name}'.");
            if (property.Name == "ancestor") { ancestor = ParseQuery(property.Value, level + 1); continue; }
            if (property.Name is not ("id" or "type" or "label")) throw new InvalidDataException($"Unknown query selector field '{property.Name}'.");
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                throw new InvalidDataException($"Query selector '{property.Name}' must be a nonempty string.");
            switch (property.Name)
            {
                case "id": id = property.Value.GetString(); break;
                case "type": type = property.Value.GetString(); break;
                case "label": label = property.Value.GetString(); break;
            }
        }
        if (id is null && type is null && label is null) throw new InvalidDataException("Every query selector level requires at least one id, type, or label field.");
        return new(id, type, label, ancestor);
    }
}
