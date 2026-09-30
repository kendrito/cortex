using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Testy.Core;

/// <summary>Where an app candidate comes from. A running instance is listed per process; the other kinds are programs that can be started.</summary>
public enum AppCandidateKind { Running, Installed, Recent, Sample }

/// <summary>
/// One application that a name can refer to: a running process with a visible window, an installed desktop program (Start menu shortcut,
/// App Paths registration, packaged app), a program an earlier test used, or one of Testy's sample apps. Discovery fills it; scoring is pure.
/// </summary>
public sealed class AppCandidate
{
    public AppCandidateKind Kind { get; set; }
    /// <summary>The friendly name shown for the app.</summary>
    public string Name { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    /// <summary>Titles of the process's other visible windows (matched like the main title).</summary>
    public List<string> OtherTitles { get; set; } = [];
    public int? ProcessId { get; set; }
    public long WindowHandle { get; set; }
    public string ProcessName { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string FileDescription { get; set; } = "";
    /// <summary>Start menu shortcut names (and App Paths names) that start this program.</summary>
    public List<string> ShortcutNames { get; set; } = [];
    /// <summary>Other names this program is known by: the display name an earlier test stored, a sample app's name.</summary>
    public List<string> OtherNames { get; set; } = [];
    /// <summary>The AppUserModelID of a packaged (Store/MSIX) app, else empty.</summary>
    public string AppId { get; set; } = "";
    public bool Packaged { get; set; }
    /// <summary>The package's install folder, when known (a packaged app's program is read from its manifest there).</summary>
    public string PackageInstallPath { get; set; } = "";
    /// <summary>Every source that described this program (a running instance of a sample app is Running with Sample among its sources).</summary>
    public List<AppCandidateKind> Sources { get; set; } = [];
    public bool IsRunning => Kind == AppCandidateKind.Running && ProcessId is not null;
    /// <summary>What starts this program: its AppUserModelID for a packaged app, else its exe path; empty when it cannot be started.</summary>
    public string LaunchKey => AppId.Length > 0 ? "appid:" + AppId.ToUpperInvariant() : ExePath.Length > 0 ? "exe:" + NormalizePath(ExePath) : "";
    public static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path.Trim()).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path.Trim().ToUpperInvariant(); }
    }
    public AppCandidate Clone()
    {
        var copy = (AppCandidate)MemberwiseClone();
        copy.OtherTitles = [.. OtherTitles]; copy.ShortcutNames = [.. ShortcutNames]; copy.OtherNames = [.. OtherNames]; copy.Sources = [.. Sources];
        return copy;
    }
}

public enum AppMatchLevel { None, Fuzzy, Word, Prefix, Exact }
public enum AppResolutionStatus { Unique, Ambiguous, NotFound }
/// <summary>Attach: one running instance is wanted, so two instances of one program are two answers. Launch: a program to start, so instances of one program count once.</summary>
public enum AppResolvePurpose { Attach, Launch }

public sealed class AppMatch
{
    public required AppCandidate Candidate { get; init; }
    /// <summary>0–1. Exact 1.0 (0.98 on a secondary field), prefix 0.85, whole words 0.75, fuzzy token overlap at most 0.65.</summary>
    public double Confidence { get; init; }
    public AppMatchLevel Level { get; init; }
    /// <summary>Which name matched: name, product name, window title, shortcut name, exe name, …</summary>
    public string Field { get; init; } = "";
    public string MatchedText { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed class AppResolution
{
    public string Query { get; init; } = "";
    public AppResolutionStatus Status { get; init; }
    /// <summary>Every candidate that matched at all, best first (running instances first among equals).</summary>
    public List<AppMatch> Matches { get; init; } = [];
    /// <summary>The one clear winner when Status is Unique.</summary>
    public AppMatch? Best { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>A leading clause that names the app a description is about ("In Customer Desk: …"), and the description without it.</summary>
public sealed record AppMention(string App, string Remainder);

/// <summary>
/// Resolves an application named in words to the candidates discovery found. Scoring is deterministic and pure:
/// exact name (display name, product name, window title, shortcut name or exe name) &gt; prefix &gt; whole-word contains &gt; fuzzy token overlap.
/// A name is unambiguous when exactly one candidate reaches <see cref="MinimumConfidence"/> and no other scores within
/// <see cref="AmbiguityMargin"/> of it; among equal best scores a single running instance wins. Nothing weaker is ever launched or attached.
/// </summary>
public static partial class AppResolver
{
    /// <summary>The least confidence at which a name selects an app: a whole-word match of the name or better.</summary>
    public const double MinimumConfidence = 0.7;
    /// <summary>Another candidate within this distance of the best one makes the name ambiguous.</summary>
    public const double AmbiguityMargin = 0.1;
    private const double Exact = 1.0, Prefix = 0.85, PartialPrefix = 0.8, Word = 0.75, FuzzyBase = 0.3, SecondaryPenalty = 0.02;
    private static readonly string[] Fillers = ["the", "a", "an", "app", "application", "program", "window", "desktop"];

    /// <summary>The best way <paramref name="query"/> names <paramref name="candidate"/>, or null when it does not name it at all.</summary>
    public static AppMatch? Score(string query, AppCandidate candidate)
    {
        List<string> q = QueryTokens(query, splitCase: true), qPlain = QueryTokens(query, splitCase: false);
        if (q.Count == 0) return null;
        AppMatch? best = null;
        foreach (var (field, text, secondary) in Names(candidate))
        {
            // Words are compared both with camel case split ("TestLab" = "test lab") and as written ("testlab" = "TestLab").
            List<string> n = Tokens(text, splitCase: true), nPlain = Tokens(text, splitCase: false);
            var (level, confidence) = new[] { Compare(q, n), Compare(qPlain, nPlain), Compare(qPlain, n), Compare(q, nPlain) }.MaxBy(r => r.Confidence);
            if (level == AppMatchLevel.None) continue;
            if (secondary) confidence -= SecondaryPenalty;
            confidence = Math.Round(confidence, 4);
            if (best is not null && best.Confidence >= confidence) continue;
            best = new AppMatch { Candidate = candidate, Confidence = confidence, Level = level, Field = field, MatchedText = text, Reason = Describe(level, field, text) };
        }
        return best;
    }

    /// <summary>Ranks the candidates for a name and decides whether one of them is the clear answer.</summary>
    public static AppResolution Resolve(string query, IEnumerable<AppCandidate> candidates, AppResolvePurpose purpose = AppResolvePurpose.Attach)
    {
        query = (query ?? "").Trim();
        var scored = candidates.Select(c => Score(query, c)).OfType<AppMatch>().ToList();
        if (purpose == AppResolvePurpose.Launch)
            // One program is one answer however many of its instances run: keep the best-scoring entry per program (running first among equals).
            scored = scored.GroupBy(m => m.Candidate.LaunchKey.Length > 0 ? m.Candidate.LaunchKey : "pid:" + m.Candidate.ProcessId + ":" + m.Candidate.Name, StringComparer.Ordinal)
                .Select(g => Rank(g).First()).ToList();
        var ranked = Rank(scored).ToList();
        if (ranked.Count == 0 || ranked[0].Confidence < MinimumConfidence)
            return new AppResolution
            {
                Query = query, Status = AppResolutionStatus.NotFound, Matches = ranked,
                Message = ranked.Count == 0 ? $"No running or installed app is called “{query}”." : $"No app clearly matches “{query}”: the closest is “{ranked[0].Candidate.Name}” ({ranked[0].Reason}, confidence {ranked[0].Confidence:0.00}, below {MinimumConfidence:0.00})."
            };
        var top = ranked[0].Confidence;
        var contenders = ranked.Where(m => m.Confidence >= top - AmbiguityMargin - 1e-9).ToList();
        AppMatch? winner = contenders.Count == 1 ? contenders[0] : null;
        if (winner is null)
        {
            // Among equal best scores a single running instance wins: it is the app the person is looking at. That holds only when the
            // running instance is named as a program (its name, product, shortcut or exe name), or when every contender is the same program:
            // a window whose title merely reads like another program's name (a folder, a browser or terminal tab) is a different app.
            var tied = contenders.Where(m => Math.Abs(m.Confidence - top) < 1e-9).ToList();
            var running = tied.Where(m => m.Candidate.IsRunning).ToList();
            if (running.Count == 1 && contenders.Count(m => m.Candidate.IsRunning) == 1
                && (NamedAsProgram(query, running[0].Candidate, top) || SameProgram(running[0].Candidate, contenders))) winner = running[0];
        }
        if (winner is not null)
            return new AppResolution { Query = query, Status = AppResolutionStatus.Unique, Matches = ranked, Best = winner, Message = $"“{query}” is {Label(winner)} ({winner.Reason})." };
        return new AppResolution
        {
            Query = query, Status = AppResolutionStatus.Ambiguous, Matches = ranked,
            Message = $"“{query}” matches {contenders.Count} apps about equally well: {string.Join("; ", contenders.Take(6).Select(Label))}. Name one of them more precisely" +
                (contenders.Any(m => m.Candidate.IsRunning) ? " or pass its pid." : ".")
        };
    }

    /// <summary>
    /// True when <paramref name="query"/> reaches <paramref name="score"/> on the program's own names alone: its display name (unless that is
    /// just its window title), other names, product name, shortcut names, exe name or file description, not its window titles.
    /// </summary>
    private static bool NamedAsProgram(string query, AppCandidate candidate, double score)
    {
        var program = candidate.Clone();
        if (TitleDerived(candidate)) program.Name = "";
        program.WindowTitle = ""; program.OtherTitles = [];
        return Score(query, program) is { } match && match.Confidence >= score - 1e-9;
    }
    /// <summary>A running window's display name that was taken from its title (no file description said more), not from the program.</summary>
    private static bool TitleDerived(AppCandidate candidate)
    {
        if (candidate.Name.Length == 0 || candidate.WindowTitle.Length == 0) return false;
        if (string.Equals(candidate.Name, candidate.FileDescription, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate.Name, candidate.ProductName, StringComparison.OrdinalIgnoreCase)) return false;
        var title = candidate.WindowTitle.Trim();
        return string.Equals(candidate.Name, title, StringComparison.OrdinalIgnoreCase)
            || TitleSeparator().Split(title).Any(part => string.Equals(part.Trim(), candidate.Name, StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>Every contender starts the same program as the running one (the same AppUserModelID or exe path).</summary>
    private static bool SameProgram(AppCandidate running, IEnumerable<AppMatch> contenders) =>
        running.LaunchKey.Length > 0 && contenders.All(m => m.Candidate.LaunchKey == running.LaunchKey);

    /// <summary>Best first; among equal scores running instances first, then earlier tests' apps, samples and installed programs; then by name.</summary>
    public static IEnumerable<AppMatch> Rank(IEnumerable<AppMatch> matches) => matches
        .OrderByDescending(m => m.Confidence).ThenBy(m => m.Candidate.IsRunning ? 0 : 1)
        .ThenBy(m => m.Candidate.Kind switch { AppCandidateKind.Running => 0, AppCandidateKind.Recent => 1, AppCandidateKind.Sample => 2, _ => 3 })
        .ThenBy(m => m.Candidate.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Candidate.ProcessId ?? 0);

    public static string Label(AppMatch match)
    {
        var c = match.Candidate;
        var kind = c.IsRunning ? $"running, pid {c.ProcessId}" : c.Kind.ToString().ToLowerInvariant();
        var where = c.Packaged ? c.AppId : c.ExePath;
        return $"“{c.Name}” ({kind}{(where.Length > 0 ? ", " + where : "")})";
    }

    /// <summary>
    /// Folds what several sources say about one program into one candidate. Running instances stay one per process and take on the names of
    /// the installed, recent and sample entries for the same program (those entries are then dropped); other entries merge by program.
    /// </summary>
    public static List<AppCandidate> Merge(IEnumerable<AppCandidate> candidates)
    {
        var all = candidates.ToList();
        var running = all.Where(c => c.Kind == AppCandidateKind.Running).GroupBy(c => c.ProcessId ?? -1).Select(g =>
        {
            var first = g.First().Clone();
            foreach (var other in g.Skip(1))
            {
                if (other.WindowTitle.Length > 0 && !first.OtherTitles.Contains(other.WindowTitle) && other.WindowTitle != first.WindowTitle) first.OtherTitles.Add(other.WindowTitle);
                first.OtherTitles.AddRange(other.OtherTitles.Where(t => !first.OtherTitles.Contains(t)));
            }
            if (!first.Sources.Contains(AppCandidateKind.Running)) first.Sources.Insert(0, AppCandidateKind.Running);
            return first;
        }).ToList();
        var programs = new Dictionary<string, AppCandidate>(StringComparer.Ordinal);
        var order = new List<AppCandidate>();
        foreach (var entry in all.Where(c => c.Kind != AppCandidateKind.Running))
        {
            var key = entry.LaunchKey;
            if (key.Length == 0) { var alone = entry.Clone(); if (!alone.Sources.Contains(alone.Kind)) alone.Sources.Add(alone.Kind); order.Add(alone); continue; }
            if (!programs.TryGetValue(key, out var merged))
            {
                merged = entry.Clone();
                if (!merged.Sources.Contains(merged.Kind)) merged.Sources.Add(merged.Kind);
                programs[key] = merged; order.Add(merged);
                continue;
            }
            Absorb(merged, entry);
            // The more specific kind names the entry: an earlier test's app, then a sample, then an installed program.
            if (Priority(entry.Kind) < Priority(merged.Kind)) { merged.Kind = entry.Kind; if (entry.Name.Length > 0) { if (merged.Name.Length > 0 && !merged.OtherNames.Contains(merged.Name)) merged.OtherNames.Add(merged.Name); merged.Name = entry.Name; } }
        }
        // A program file inside a packaged app's install folder (an App Paths entry for a Store app) is that packaged app when the names agree,
        // or when it is the only packaged app installed there.
        foreach (var file in order.Where(p => !p.Packaged && p.ExePath.Length > 0).ToList())
        {
            var owners = order.Where(p => p.Packaged && p.PackageInstallPath.Length > 0 && Inside(p.PackageInstallPath, file.ExePath)).ToList();
            var owner = owners.Count == 1 ? owners[0] : owners.FirstOrDefault(p => SameName(p, file));
            if (owner is null) continue;
            Absorb(owner, file);
            owner.ExePath = owner.ExePath.Length > 0 ? owner.ExePath : file.ExePath;
            order.Remove(file);
            programs.Remove(file.LaunchKey);
        }
        var absorbed = new HashSet<AppCandidate>();
        foreach (var instance in running)
        {
            // The same program: the same AppUserModelID or exe path (a packaged app's running process also carries its exe path).
            var matches = order.Where(p => p.LaunchKey.Length > 0 && (p.LaunchKey == instance.LaunchKey
                || p.ExePath.Length > 0 && instance.ExePath.Length > 0 && AppCandidate.NormalizePath(p.ExePath) == AppCandidate.NormalizePath(instance.ExePath))).ToList();
            foreach (var program in matches)
            {
                Absorb(instance, program);
                if (instance.Name.Length == 0) instance.Name = program.Name;
                absorbed.Add(program);
            }
        }
        return running.Concat(order.Where(p => !absorbed.Contains(p))).ToList();

        static bool Inside(string folder, string path)
        {
            var root = AppCandidate.NormalizePath(folder).TrimEnd('\\', '/') + "\\";
            return AppCandidate.NormalizePath(path).StartsWith(root, StringComparison.Ordinal);
        }
        static bool SameName(AppCandidate a, AppCandidate b) =>
            new[] { a.Name }.Concat(a.OtherNames).Concat(a.ShortcutNames).Any(n => n.Length > 0 && new[] { b.Name, b.FileDescription }.Concat(b.OtherNames).Concat(b.ShortcutNames).Contains(n, StringComparer.OrdinalIgnoreCase));

        static int Priority(AppCandidateKind kind) => kind switch { AppCandidateKind.Recent => 0, AppCandidateKind.Sample => 1, _ => 2 };
        static void Absorb(AppCandidate into, AppCandidate from)
        {
            foreach (var kind in from.Sources.Append(from.Kind)) if (!into.Sources.Contains(kind)) into.Sources.Add(kind);
            foreach (var name in from.ShortcutNames) if (!into.ShortcutNames.Contains(name, StringComparer.OrdinalIgnoreCase)) into.ShortcutNames.Add(name);
            foreach (var name in from.OtherNames.Append(from.Name).Where(n => n.Length > 0))
                if (!string.Equals(name, into.Name, StringComparison.OrdinalIgnoreCase) && !into.OtherNames.Contains(name, StringComparer.OrdinalIgnoreCase)) into.OtherNames.Add(name);
            if (into.ProductName.Length == 0) into.ProductName = from.ProductName;
            if (into.FileDescription.Length == 0) into.FileDescription = from.FileDescription;
            if (into.ExePath.Length == 0) into.ExePath = from.ExePath;
            if (into.AppId.Length == 0) { into.AppId = from.AppId; into.Packaged |= from.Packaged; }
            if (into.PackageInstallPath.Length == 0) into.PackageInstallPath = from.PackageInstallPath;
        }
    }

    /// <summary>
    /// Finds a leading clause that names the app a description is about: "In Customer Desk: …", "Using Order Desk, …", "Launch Customer Desk and …",
    /// or a first line "App: Customer Desk". Returns null when the text does not start that way. The name is not checked against any app here;
    /// "open …" and "start …" are not read as app names, because they usually describe something inside the app ("Open Settings and …").
    /// </summary>
    public static AppMention? FindMention(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var index = Array.FindIndex(lines, l => l.Trim().Length > 0);
        var first = lines[index].Trim();
        var rest = string.Join("\n", lines.Skip(index + 1)).Trim();
        foreach (var pattern in new[] { LeadingIn(), LeadingOpen(), AppLine() })
        {
            var match = pattern.Match(first);
            if (!match.Success) continue;
            var app = CleanAppPhrase(match.Groups["app"].Value);
            if (app is null) continue;
            var inline = match.Groups["rest"].Success ? match.Groups["rest"].Value.Trim() : "";
            var remainder = inline.Length == 0 ? rest : rest.Length == 0 ? inline : inline + "\n" + rest;
            return new AppMention(app, remainder);
        }
        return null;
    }
    private static string? CleanAppPhrase(string phrase)
    {
        var app = phrase.Trim().Trim('"', '“', '”', '\'', '‘', '’').Trim();
        app = TrailingNoun().Replace(app, "").Trim();
        if (app.Length is 0 or > 80) return null;
        var words = app.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 8) return null;
        return app;
    }
    [GeneratedRegex(@"^(?:in|on|using|with|inside|within)\s+(?:the\s+)?(?<app>[^:,\n]{1,120}?)\s*(?::|,|\s[—–-]\s)\s*(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingIn();
    [GeneratedRegex(@"^launch\s+(?:the\s+)?(?<app>[^:,\n]{1,120}?)\s*(?::|,|\s+and\s+(?:then\s+)?|\s+then\s+)\s*(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingOpen();
    [GeneratedRegex(@"^app(?:lication)?\s*:\s*(?<app>.{1,120})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppLine();
    [GeneratedRegex(@"\s+(?:app|application|program|window)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNoun();

    // ═══════════════ Scoring internals ═══════════════
    private static IEnumerable<(string Field, string Text, bool Secondary)> Names(AppCandidate c)
    {
        if (c.Name.Length > 0) yield return ("name", c.Name, false);
        foreach (var name in c.OtherNames) yield return ("name", name, false);
        if (c.ProductName.Length > 0) yield return ("product name", c.ProductName, false);
        foreach (var shortcut in c.ShortcutNames) yield return ("shortcut name", shortcut, false);
        if (c.ExePath.Length > 0) yield return ("exe name", Path.GetFileNameWithoutExtension(c.ExePath), false);
        foreach (var title in new[] { c.WindowTitle }.Concat(c.OtherTitles).Where(t => t.Length > 0))
        {
            yield return ("window title", title, false);
            // "Document1 - Word", "Testy TestLab — Customer Desk": each part of a title can be the app's name.
            var parts = TitleSeparator().Split(title).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count > 1) foreach (var part in parts) yield return ("window title", part, true);
        }
        if (c.FileDescription.Length > 0) yield return ("file description", c.FileDescription, true);
        if (c.ProcessName.Length > 0) yield return ("process name", c.ProcessName, true);
    }
    [GeneratedRegex(@"\s[—–\-|·]\s")]
    private static partial Regex TitleSeparator();

    private static (AppMatchLevel Level, double Confidence) Compare(List<string> q, List<string> n)
    {
        if (n.Count == 0) return (AppMatchLevel.None, 0);
        string qJoined = string.Concat(q), nJoined = string.Concat(n);
        if (q.SequenceEqual(n) || qJoined == nJoined) return (AppMatchLevel.Exact, Exact);
        if (q.Count < n.Count && n.Take(q.Count).SequenceEqual(q)) return (AppMatchLevel.Prefix, Prefix);
        // The start of a word ("Calc" for "Calculator") counts a little less than whole words.
        if (qJoined.Length >= 3 && IsTokenPrefix(q, n)) return (AppMatchLevel.Prefix, PartialPrefix);
        for (var start = 1; start + q.Count <= n.Count; start++)
            if (n.Skip(start).Take(q.Count).SequenceEqual(q)) return (AppMatchLevel.Word, Word);
        // Fuzzy: the share of the query's words found among the name's words (one typo allowed in words of five letters or more).
        var meaningful = q.Where(t => t.Length >= 2).ToList();
        if (meaningful.Count == 0) return (AppMatchLevel.None, 0);
        var matched = meaningful.Count(t => n.Any(w => w == t || (t.Length >= 5 && w.Length >= 5 && Distance(t, w) <= 1)));
        if (matched == 0 || !meaningful.Any(t => t.Length >= 3 && n.Any(w => w == t || (t.Length >= 5 && w.Length >= 5 && Distance(t, w) <= 1)))) return (AppMatchLevel.None, 0);
        var overlap = (double)matched / meaningful.Count;
        if (overlap < 0.5) return (AppMatchLevel.None, 0);
        var coverage = (double)matched / n.Count;
        return (AppMatchLevel.Fuzzy, FuzzyBase + 0.2 * overlap + 0.15 * Math.Min(1, coverage));
    }
    /// <summary>"cust desk" is a prefix of "customer desk": every query word begins the name word at the same position.</summary>
    private static bool IsTokenPrefix(List<string> q, List<string> n)
    {
        if (q.Count > n.Count) return false;
        for (var i = 0; i < q.Count; i++)
        {
            var last = i == q.Count - 1;
            if (last ? !n[i].StartsWith(q[i], StringComparison.Ordinal) : n[i] != q[i]) return false;
        }
        return true;
    }
    private static string Describe(AppMatchLevel level, string field, string text) => level switch
    {
        AppMatchLevel.Exact => $"exact {field} “{text}”",
        AppMatchLevel.Prefix => $"{field} “{text}” starts with the name",
        AppMatchLevel.Word => $"{field} “{text}” contains the name as whole words",
        _ => $"some words of the name appear in the {field} “{text}”"
    };

    /// <summary>
    /// Words of a name: lower case, letters and digits only, accents dropped, ".exe" dropped; with <paramref name="splitCase"/> camel case and
    /// letter/digit steps start new words ("TestLab" → "test", "lab").
    /// </summary>
    public static List<string> Tokens(string text, bool splitCase = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var value = text.Trim();
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        var builder = new StringBuilder(value.Length + 8);
        char previous = ' ';
        foreach (var raw in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(raw))
            {
                if (splitCase && char.IsLetterOrDigit(previous) && (char.IsUpper(raw) && char.IsLower(previous) || char.IsDigit(raw) != char.IsDigit(previous))) builder.Append(' ');
                builder.Append(char.ToLowerInvariant(raw));
            }
            else builder.Append(raw == '&' ? " and " : " ");
            previous = raw;
        }
        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
    /// <summary>The query's words without leading and trailing filler ("the", "app", "window", …), as long as a word remains.</summary>
    private static List<string> QueryTokens(string query, bool splitCase)
    {
        var trimmed = Tokens(query, splitCase);
        while (trimmed.Count > 1 && Fillers.Contains(trimmed[0])) trimmed.RemoveAt(0);
        while (trimmed.Count > 1 && Fillers.Contains(trimmed[^1])) trimmed.RemoveAt(trimmed.Count - 1);
        return trimmed;
    }
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return 2;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
