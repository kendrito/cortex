using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Testy.Core;

namespace Testy.Studio;

/// <summary>How one step appears on the run trace.</summary>
public enum RunTraceStepState
{
    Passed,
    Failed,
    Cancelled,
    /// <summary>Started but not finished yet (live run). Extends to "now" when the builder is given one.</summary>
    Running,
    /// <summary>Pending or skipped. It never executed, so it has no place on the time axis.</summary>
    NotRun,
}

public enum RunTraceGapKind
{
    /// <summary>From the run start to the first step (or the whole run when no step ran).</summary>
    BeforeFirstStep,
    BetweenSteps,
    /// <summary>From the end of the last step to the end of the run.</summary>
    AfterLastStep,
}

/// <summary>One step of a <see cref="RunTrace"/>. Times are milliseconds from the run start.</summary>
public sealed class RunTraceStep
{
    private readonly bool inMilliseconds;

    internal RunTraceStep(int position, StepResult source, RunTraceStepState state, double startMs, double durationMs,
        string? screenshotPath, string description, bool inMilliseconds)
    {
        Position = position;
        Source = source;
        State = state;
        StartMs = startMs;
        DurationMs = durationMs;
        ScreenshotPath = screenshotPath;
        Description = description;
        this.inMilliseconds = inMilliseconds;
    }

    /// <summary>Index in <see cref="RunTrace.Steps"/>, which is the index in <see cref="RunResult.Steps"/>.</summary>
    public int Position { get; }
    /// <summary>1-based number shown to the user ("Step 4").</summary>
    public int Number => Position + 1;
    public StepResult Source { get; }
    public RunTraceStepState State { get; }
    public bool IsOnTimeline => State != RunTraceStepState.NotRun;
    public bool IsProblem => State is RunTraceStepState.Failed or RunTraceStepState.Cancelled;
    /// <summary>Offset of the step start from the run start, clamped to ≥ 0. 0 when not run.</summary>
    public double StartMs { get; }
    /// <summary>Recorded duration, clamped to a finite value ≥ 0. 0 when not run.</summary>
    public double DurationMs { get; }
    /// <summary>End of the step, which is when its screenshot was captured.</summary>
    public double EndMs => StartMs + DurationMs;
    /// <summary>Absolute screenshot path, or null when none was recorded. The file may be missing or unreadable.</summary>
    public string? ScreenshotPath { get; }
    /// <summary>Plain-language action, e.g. "Click Add customer".</summary>
    public string Description { get; }

    public string StateText => State switch
    {
        RunTraceStepState.Passed => "passed",
        RunTraceStepState.Failed => "failed",
        RunTraceStepState.Cancelled => "cancelled",
        RunTraceStepState.Running => "running",
        _ => "not run",
    };

    /// <summary>Duration in the trace's unit, e.g. "0.4 s" (or "180 ms" on a millisecond axis).</summary>
    public string DurationText => RunTrace.FormatTime(DurationMs, inMilliseconds);
    /// <summary>Capture instant, e.g. "5.1 s".</summary>
    public string EndText => RunTrace.FormatTime(EndMs, inMilliseconds);

    /// <summary>e.g. "Step 4, Click Add customer, passed, 0.4 s at 5.1 s".</summary>
    public string AutomationName => State switch
    {
        RunTraceStepState.NotRun => $"Step {Number}, {Description}, not run",
        RunTraceStepState.Running => $"Step {Number}, {Description}, running since {RunTrace.FormatTime(StartMs, inMilliseconds)}",
        _ => $"Step {Number}, {Description}, {StateText}, {DurationText} at {EndText}",
    };

    public override string ToString() => AutomationName;
}

/// <summary>Time between steps (for AI-guided runs: the assistant deciding and finding the next control).</summary>
public readonly record struct RunTraceGap(double StartMs, double DurationMs, RunTraceGapKind Kind, int PreviousStepPosition, int NextStepPosition)
{
    public double EndMs => StartMs + DurationMs;
}

public readonly record struct RunTraceTick(double Ms, string Label);

/// <summary>A "nice" time axis: ticks every 1, 2 or 5 × 10^n, labelled in seconds (milliseconds for runs under 1 s).</summary>
public sealed class RunTraceAxis
{
    private RunTraceAxis(double totalMs, double intervalMs, bool inMilliseconds, IReadOnlyList<RunTraceTick> ticks)
    {
        TotalMs = totalMs;
        IntervalMs = intervalMs;
        InMilliseconds = inMilliseconds;
        Ticks = ticks;
    }

    public double TotalMs { get; }
    /// <summary>Tick spacing in milliseconds; 0 when the run has no duration.</summary>
    public double IntervalMs { get; }
    public bool InMilliseconds { get; }
    public string UnitName => InMilliseconds ? "milliseconds" : "seconds";
    /// <summary>Ticks from 0 up to and including the total, when a tick lands on it.</summary>
    public IReadOnlyList<RunTraceTick> Ticks { get; }

    /// <summary>Runs shorter than a second are labelled in milliseconds.</summary>
    public static bool UsesMilliseconds(double totalMs) => totalMs > 0 && totalMs < 1000;

    public static RunTraceAxis Create(double totalMs, int minTicks = 6, int maxTicks = 10)
    {
        if (!(totalMs > 0) || double.IsInfinity(totalMs))
            return new RunTraceAxis(0, 0, false, [new RunTraceTick(0, "0")]);
        minTicks = Math.Max(2, minTicks);
        maxTicks = Math.Max(minTicks, maxTicks);
        var inMs = UsesMilliseconds(totalMs);

        double best = 0, bestPenalty = double.MaxValue, bestCount = 0;
        var target = totalMs / Math.Max(1, (minTicks + maxTicks) / 2.0 - 1);
        var exponent = (int)Math.Floor(Math.Log10(target));
        for (var e = exponent - 2; e <= exponent + 2; e++)
        {
            foreach (var m in (ReadOnlySpan<double>)[1, 2, 5])
            {
                var interval = m * Math.Pow(10, e);
                if (!(interval > 0)) continue;
                var count = Math.Floor(totalMs / interval + 1e-9) + 1;
                var penalty = count < minTicks ? minTicks - count : count > maxTicks ? count - maxTicks : 0;
                // Equal penalty: prefer fewer ticks (calmer, and safer for label room).
                if (penalty < bestPenalty || penalty == bestPenalty && count < bestCount)
                {
                    best = interval; bestPenalty = penalty; bestCount = count;
                }
            }
        }

        var unit = inMs ? 1.0 : 1000.0;
        var decimals = Math.Max(0, -(int)Math.Floor(Math.Log10(best / unit) + 1e-9));
        var format = "F" + decimals.ToString(CultureInfo.InvariantCulture);
        var ticks = new List<RunTraceTick>();
        for (var i = 0; i < bestCount; i++)
        {
            var ms = i * best;
            if (ms > totalMs * (1 + 1e-9)) break;
            ticks.Add(new RunTraceTick(ms, (ms / unit).ToString(format, CultureInfo.CurrentCulture)));
        }
        return new RunTraceAxis(totalMs, best, inMs, ticks);
    }
}

/// <summary>
/// Immutable model of a run for the run trace: where each step sits on the time axis, the time between steps,
/// totals and axis ticks. Build it with <see cref="From"/>, a pure function of the run (and an optional "now").
/// </summary>
public sealed class RunTrace
{
    public const string AiGapLabel = "Assistant finding the next control";
    public const string ReplayGapLabel = "Between steps";
    private const double GapEpsilonMs = 0.001;

    private RunTrace(RunResult? run, RunStatus status, bool inProgress, bool aiGuided, double totalMs, double stepMs,
        IReadOnlyList<RunTraceStep> steps, IReadOnlyList<RunTraceGap> gaps)
    {
        Run = run;
        Status = status;
        IsInProgress = inProgress;
        IsAiGuided = aiGuided;
        TotalMs = totalMs;
        StepMs = Math.Min(stepMs, totalMs);
        Steps = steps;
        Gaps = gaps;
        Axis = RunTraceAxis.Create(totalMs);
        TimedStepCount = steps.Count(s => s.IsOnTimeline);
        NotRunCount = steps.Count - TimedStepCount;
        FirstProblem = steps.FirstOrDefault(s => s.IsProblem);
        (StepTimeText, BetweenTimeText, TotalTimeText) = LegendTexts(StepMs, BetweenMs, TotalMs, Axis.InMilliseconds);
    }

    /// <summary>No run at all.</summary>
    public static RunTrace Empty { get; } = new(null, RunStatus.Pending, false, false, 0, 0, [], []);

    public RunResult? Run { get; }
    public bool HasRun => Run is not null;
    public string TestName => Run?.TestName ?? "";
    public RunStatus Status { get; }
    /// <summary>The run has no FinishedAt and is still Running/Pending.</summary>
    public bool IsInProgress { get; }
    /// <summary>Gaps are the assistant deciding; otherwise they are runner overhead between replayed steps.</summary>
    public bool IsAiGuided { get; }
    public double TotalMs { get; }
    /// <summary>Time covered by steps. Overlapping steps are counted once, so StepMs + BetweenMs = TotalMs.</summary>
    public double StepMs { get; }
    public double BetweenMs => Math.Max(0, TotalMs - StepMs);
    /// <summary>All steps in <see cref="RunResult.Steps"/> order (not-run steps included).</summary>
    public IReadOnlyList<RunTraceStep> Steps { get; }
    /// <summary>Gaps in time order: before the first step, between steps, and after the last step.</summary>
    public IReadOnlyList<RunTraceGap> Gaps { get; }
    public RunTraceAxis Axis { get; }
    public int TimedStepCount { get; }
    public int NotRunCount { get; }
    /// <summary>First failed or cancelled step, if any.</summary>
    public RunTraceStep? FirstProblem { get; }
    public string DefaultGapLabel => IsAiGuided ? AiGapLabel : ReplayGapLabel;
    /// <summary>Legend values. Rounded together so that "Steps" + "between" adds up to "Total" when all use tenths of a second.</summary>
    public string StepTimeText { get; }
    public string BetweenTimeText { get; }
    public string TotalTimeText { get; }
    public string Summary => Describe(DefaultGapLabel);

    /// <summary>One-sentence summary for screen readers, e.g. "Run trace, passed, 5 steps, total 8.2 s. Steps 2.0 s, assistant finding the next control 6.2 s."</summary>
    public string Describe(string? gapLabel)
    {
        if (Run is null) return "Run trace, no run yet.";
        gapLabel = string.IsNullOrWhiteSpace(gapLabel) ? DefaultGapLabel : gapLabel.Trim();
        var sb = new StringBuilder("Run trace, ");
        sb.Append(IsInProgress ? "running" : Status switch
        {
            RunStatus.Passed => "passed",
            RunStatus.Failed => "failed",
            RunStatus.Cancelled => "cancelled",
            RunStatus.Skipped => "skipped",
            RunStatus.Running => "running",
            _ => "pending",
        });
        if (FirstProblem is { } problem) sb.Append(" at step ").Append(problem.Number.ToString(CultureInfo.CurrentCulture));
        sb.Append(", ");
        if (Steps.Count == 0) sb.Append(IsInProgress ? "no steps yet" : "no steps ran");
        else if (IsInProgress) sb.Append(TimedStepCount == 1 ? "1 step so far" : string.Format(CultureInfo.CurrentCulture, "{0} steps so far", TimedStepCount));
        else if (NotRunCount > 0) sb.Append(CultureInfo.CurrentCulture, $"{TimedStepCount} of {Steps.Count} steps ran, {NotRunCount} not run");
        else sb.Append(Steps.Count == 1 ? "1 step" : string.Format(CultureInfo.CurrentCulture, "{0} steps", Steps.Count));
        sb.Append(", total ").Append(TotalTimeText).Append('.');
        if (Steps.Count > 0)
            sb.Append(" Steps ").Append(StepTimeText).Append(", ").Append(LowerFirst(gapLabel)).Append(' ').Append(BetweenTimeText).Append('.');
        return sb.ToString();
    }

    /// <summary>
    /// Builds the trace. Pure: reads only <paramref name="run"/> and the optional <paramref name="now"/>.
    /// </summary>
    /// <param name="now">For a run in progress, extends the running step and the total to this instant.</param>
    /// <param name="aiGuided">Whether gaps are the assistant deciding. Null infers it from the AI runner's
    /// artifact-folder naming ("…-ai-…"); pass the known mode when the host has it.</param>
    public static RunTrace From(RunResult? run, DateTimeOffset? now = null, bool? aiGuided = null)
    {
        if (run is null) return Empty;
        var source = run.Steps ?? [];
        var origin = run.StartedAt;
        var finished = run.FinishedAt.HasValue;
        var inProgress = !finished && run.Status is RunStatus.Running or RunStatus.Pending;
        var finishedAt = finished ? Offset(run.FinishedAt!.Value, origin) : double.NaN;
        var nowAt = inProgress && now.HasValue ? Offset(now.Value, origin) : double.NaN;

        var n = source.Count;
        var states = new RunTraceStepState[n];
        var starts = new double[n];
        var durations = new double[n];
        double lastEnd = 0;
        for (var i = 0; i < n; i++)
        {
            var step = source[i];
            var state = step is null ? RunTraceStepState.NotRun : Map(step.Status);
            states[i] = state;
            if (state == RunTraceStepState.NotRun) continue;
            var start = Clean(Offset(step!.StartedAt, origin)); // clamps negatives (clock skew) to the run start
            var duration = Clean(step.DurationMs);
            if (state == RunTraceStepState.Running && duration == 0 && !double.IsNaN(nowAt)) duration = Math.Max(0, nowAt - start);
            // Synthetic zero-length results appended after the run finished (e.g. "Verify saved acceptance
            // criteria") belong at the end of the run, not wherever their construction clock happened to be.
            if (finished && finishedAt >= 0 && duration == 0 && start > finishedAt) start = finishedAt;
            starts[i] = start;
            durations[i] = duration;
            lastEnd = Math.Max(lastEnd, start + duration);
        }

        // A missing FinishedAt falls back to the end of the last step (or "now" for a live run).
        var total = lastEnd;
        if (finished && finishedAt > total) total = finishedAt;
        if (!double.IsNaN(nowAt) && nowAt > total) total = nowAt;

        // Gaps are the complement of the union of step intervals; overlapping steps merge.
        var order = Enumerable.Range(0, n).Where(i => states[i] != RunTraceStepState.NotRun)
            .OrderBy(i => starts[i]).ThenBy(i => starts[i] + durations[i]).ThenBy(i => i).ToList();
        var gaps = new List<RunTraceGap>();
        double cursor = 0, covered = 0;
        var previous = -1;
        foreach (var i in order)
        {
            double s = starts[i], e = s + durations[i];
            if (s > cursor + GapEpsilonMs)
            {
                gaps.Add(new RunTraceGap(cursor, s - cursor, previous < 0 ? RunTraceGapKind.BeforeFirstStep : RunTraceGapKind.BetweenSteps, previous, i));
                cursor = s;
            }
            if (e >= cursor)
            {
                covered += e - cursor;
                cursor = e;
                previous = i;
            }
        }
        if (total > cursor + GapEpsilonMs)
            gaps.Add(new RunTraceGap(cursor, total - cursor, previous < 0 ? RunTraceGapKind.BeforeFirstStep : RunTraceGapKind.AfterLastStep, previous, -1));

        var inMs = RunTraceAxis.UsesMilliseconds(total);
        var steps = new RunTraceStep[n];
        for (var i = 0; i < n; i++)
        {
            var result = source[i] ?? new StepResult { Index = i, Status = RunStatus.Pending };
            var timed = states[i] != RunTraceStepState.NotRun;
            steps[i] = new RunTraceStep(i, result, states[i], timed ? starts[i] : 0, timed ? durations[i] : 0,
                ResolveScreenshot(result, run.ArtifactDirectory), StepDescriber.Describe(result), inMs);
        }

        return new RunTrace(run, run.Status, inProgress, aiGuided ?? InferAiGuided(run), total, covered, steps, gaps);
    }

    /// <summary>A time or duration in this trace's unit: milliseconds when the whole run is under a second, else <see cref="FormatDuration"/>.</summary>
    public string FormatTime(double ms) => FormatTime(ms, Axis.InMilliseconds);

    internal static string FormatTime(double ms, bool inMilliseconds) => inMilliseconds ? FormatMilliseconds(ms) : FormatDuration(ms);

    /// <summary>"0 ms", "0.4 ms", "3.2 ms", "12 ms", "640 ms".</summary>
    public static string FormatMilliseconds(double ms)
    {
        var c = CultureInfo.CurrentCulture;
        if (double.IsNaN(ms) || ms <= 0) return "0 ms";
        return ms < 9.95 ? string.Format(c, "{0:0.0} ms", ms) : string.Format(c, "{0:0} ms", ms);
    }

    /// <summary>"0 ms", "&lt;1 ms", "12 ms", "0.4 s", "8.2 s", "12.5 s", "125 s".</summary>
    public static string FormatDuration(double ms)
    {
        var c = CultureInfo.CurrentCulture;
        if (double.IsNaN(ms) || ms <= 0) return "0 ms";
        if (double.IsPositiveInfinity(ms)) return "∞";
        if (ms < 1) return "<1 ms";
        if (ms < 99.5) return string.Format(c, "{0:0} ms", ms);
        if (ms < 99_950) return string.Format(c, "{0:0.0} s", ms / 1000);
        return string.Format(c, "{0:0} s", ms / 1000);
    }

    internal static RunTraceStepState Map(RunStatus status) => status switch
    {
        RunStatus.Passed => RunTraceStepState.Passed,
        RunStatus.Failed => RunTraceStepState.Failed,
        RunStatus.Cancelled => RunTraceStepState.Cancelled,
        RunStatus.Running => RunTraceStepState.Running,
        _ => RunTraceStepState.NotRun, // Pending, Skipped
    };

    private static bool InferAiGuided(RunResult run)
    {
        // AiTestRunner names its artifact folder "yyyyMMdd-HHmmss-ai-<id>"; TestRunner uses "yyyyMMdd-HHmmss-<id>".
        var dir = run.ArtifactDirectory;
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var name = dir.TrimEnd('\\', '/');
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) name = name[(slash + 1)..];
        return name.Contains("-ai-", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveScreenshot(StepResult step, string? artifactDirectory)
    {
        var path = !string.IsNullOrWhiteSpace(step.ScreenshotPath) ? step.ScreenshotPath : step.ScreenshotEvidence?.Path;
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();
        try
        {
            if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(artifactDirectory)) path = Path.Combine(artifactDirectory, path);
            return path;
        }
        catch (ArgumentException) { return null; }
    }

    private static (string Steps, string Between, string Total) LegendTexts(double stepMs, double betweenMs, double totalMs, bool inMilliseconds)
    {
        if (inMilliseconds)
        {
            // Whole milliseconds (tenths below 10 ms), rounded together so the parts add up to the total.
            var decimals = totalMs < 9.95 ? 1 : 0;
            var factor = decimals == 1 ? 10.0 : 1.0;
            var t = (long)Math.Round(totalMs * factor, MidpointRounding.AwayFromZero);
            var s = Math.Min(t, (long)Math.Round(stepMs * factor, MidpointRounding.AwayFromZero));
            return (Ms(s), Ms(t - s), Ms(t));
            string Ms(long units) => units == 0 ? "0 ms" : string.Format(CultureInfo.CurrentCulture, decimals == 1 ? "{0:0.0} ms" : "{0:0} ms", units / factor);
        }
        static bool Tenths(double v) => v <= 0 || v >= 99.5 && v < 99_950;
        if (totalMs >= 99.5 && Tenths(totalMs) && Tenths(stepMs) && Tenths(betweenMs))
        {
            var t = (long)Math.Round(totalMs / 100, MidpointRounding.AwayFromZero);
            var s = Math.Min(t, (long)Math.Round(stepMs / 100, MidpointRounding.AwayFromZero));
            return (Tenth(s), Tenth(t - s), Tenth(t));
        }
        return (FormatDuration(stepMs), FormatDuration(betweenMs), FormatDuration(totalMs));
        static string Tenth(long tenths) => string.Format(CultureInfo.CurrentCulture, "{0:0.0} s", tenths / 10.0);
    }

    private static double Offset(DateTimeOffset at, DateTimeOffset origin) => (at - origin).TotalMilliseconds;
    private static double Clean(double ms) => double.IsFinite(ms) && ms > 0 ? ms : 0;

    private static string LowerFirst(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLower(text[0], CultureInfo.CurrentCulture) + text[1..] : text;
}

public enum StepTargetKind
{
    /// <summary>The rectangle of the control the step clicked, typed into or checked.</summary>
    Control,
    /// <summary>A coordinate click: <see cref="StepTargetRegion.Bounds"/> is a zero-size point.</summary>
    Point,
}

/// <summary>Where a step's target sits inside that step's screenshot.</summary>
public sealed class StepTargetRegion
{
    public StepTargetKind Kind { get; init; }
    /// <summary>Screenshot-relative rectangle in screenshot pixels (the coordinate space of
    /// <see cref="UiSnapshot.ScreenshotBounds"/>), clipped to the screenshot.</summary>
    public Rect Bounds { get; init; }
    /// <summary>Size of the screenshot coordinate space. Normally equals the PNG's pixel size.</summary>
    public Size ImageSize { get; init; }
    /// <summary>True when the control extends beyond the screenshot and <see cref="Bounds"/> was clipped.</summary>
    public bool IsClipped { get; init; }
    public UiElementInfo? Element { get; init; }
    public string Selector { get; init; } = "";

    /// <summary><see cref="Bounds"/> as fractions (0–1) of the screenshot size.</summary>
    public Rect Normalized => ImageSize.Width > 0 && ImageSize.Height > 0
        ? new Rect(Bounds.X / ImageSize.Width, Bounds.Y / ImageSize.Height, Bounds.Width / ImageSize.Width, Bounds.Height / ImageSize.Height)
        : Rect.Empty;

    /// <summary>Maps <see cref="Bounds"/> onto the screenshot drawn at <paramref name="displayed"/> (e.g. an Image's ActualWidth/ActualHeight with Stretch=Uniform content box).</summary>
    public Rect MapTo(Rect displayed)
    {
        var n = Normalized;
        return n.IsEmpty ? Rect.Empty : new Rect(displayed.X + n.X * displayed.Width, displayed.Y + n.Y * displayed.Height, n.Width * displayed.Width, n.Height * displayed.Height);
    }
}

/// <summary>Finds the control a step acted on inside the step's screenshot (for outlining it on the big screenshot).</summary>
public static class StepTargetLocator
{
    /// <summary>The selector the step actually used: an explicitly chosen selector alternative, else the saved selector.</summary>
    public static string TargetSelector(StepResult step)
    {
        var resolved = step.SelectorRecovery?.ResolvedSelector;
        return !string.IsNullOrWhiteSpace(resolved) ? resolved : step.Step?.Selector ?? "";
    }

    /// <summary>The unique element matching <paramref name="selector"/>: exact <see cref="UiElementInfo.Selector"/> match first,
    /// then Testy.Core's selector semantics (id:/name:/query:). Null when missing or ambiguous.</summary>
    public static UiElementInfo? FindElement(UiSnapshot? snapshot, string? selector)
    {
        if (snapshot?.Elements is not { Count: > 0 } elements || string.IsNullOrWhiteSpace(selector)) return null;
        UiElementInfo? exact = null;
        var count = 0;
        foreach (var element in elements)
            if (element is not null && string.Equals(element.Selector, selector, StringComparison.Ordinal)) { exact = element; count++; }
        if (count == 1) return exact;
        if (count > 1 || elements.Any(e => e is null)) return null; // ambiguous or malformed: not determinable
        try
        {
            var found = UiSelectors.Find(snapshot, selector);
            return found.Count == 1 ? found[0] : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
        {
            return null; // malformed selector, or a truncated tree where uniqueness cannot be verified
        }
    }

    /// <summary>
    /// The clicked/typed/checked control's rectangle within the step's screenshot, if determinable: the step's selector
    /// is matched to <c>Snapshot.Elements[i].Selector</c> and its screen <c>Bounds</c> are made relative to
    /// <c>Snapshot.ScreenshotBounds</c> (falling back to <c>ScreenshotEvidence.Bounds</c>). Null when there is no
    /// snapshot, no unique match, the control is off screen, or it lies outside the screenshot.
    /// </summary>
    public static StepTargetRegion? Locate(StepResult? step)
    {
        if (step?.Step is null || step.Snapshot is null) return null;
        var frame = ValidBounds(step.Snapshot.ScreenshotBounds) ?? ValidBounds(step.ScreenshotEvidence?.Bounds);
        if (frame is null) return null;
        var size = new Size(frame.Width, frame.Height);
        var action = step.Step.Action;
        if (action == StepAction.CoordinateClick)
        {
            // Saved coordinates are relative to the target window, which is the screenshot origin.
            if (step.Step.X < 0 || step.Step.Y < 0 || step.Step.X > size.Width || step.Step.Y > size.Height) return null;
            return new StepTargetRegion { Kind = StepTargetKind.Point, Bounds = new Rect(step.Step.X, step.Step.Y, 0, 0), ImageSize = size };
        }
        if (action is StepAction.Wait or StepAction.Screenshot or StepAction.KeyPress) return null;

        var selector = TargetSelector(step);
        var element = FindElement(step.Snapshot, selector);
        if (element is null || element.IsOffscreen) return null;
        var b = ValidBounds(element.Bounds);
        if (b is null) return null;
        var raw = new Rect(b.X - frame.X, b.Y - frame.Y, b.Width, b.Height);
        var visible = Rect.Intersect(raw, new Rect(size));
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return null;
        return new StepTargetRegion
        {
            Kind = StepTargetKind.Control, Bounds = visible, ImageSize = size, IsClipped = visible != raw, Element = element, Selector = selector,
        };
    }

    private static ElementBounds? ValidBounds(ElementBounds? b) =>
        b is not null && double.IsFinite(b.X) && double.IsFinite(b.Y) && double.IsFinite(b.Width) && double.IsFinite(b.Height)
        && b.Width > 0 && b.Height > 0 ? b : null;
}

/// <summary>Short plain-language descriptions of steps ("Click Add customer") for names and tooltips.</summary>
internal static class StepDescriber
{
    private const int MaxValue = 40, MaxName = 48;

    public static string Describe(StepResult result)
    {
        var step = result.Step ?? new TestStep();
        var selector = StepTargetLocator.TargetSelector(result);
        var element = StepTargetLocator.FindElement(result.Snapshot, selector);
        var title = MeaningfulTitle(step.Title);
        var target = TargetName(element, selector) ?? title ?? "the control";
        var secret = element?.IsPassword == true || LooksSecret(selector) || LooksSecret(target);
        var value = secret ? "••••••" : Clip(OneLine(step.Value), MaxValue);
        var quoted = value.Length > 0 ? $"“{value}”" : "";

        var phrase = step.Action switch
        {
            StepAction.Click => $"Click {target}",
            StepAction.TypeText => value.Length > 0 ? $"Type {quoted} into {target}" : $"Clear {target}",
            StepAction.Select => value.Length > 0 ? $"Select {quoted} in {target}" : $"Select {target}",
            StepAction.Toggle => $"Toggle {target}",
            StepAction.AssertText => $"Check {target} says {quoted}".TrimEnd(),
            StepAction.AssertExists => $"Check {target} is present",
            StepAction.AssertNotExists => $"Check {target} is absent",
            StepAction.AssertEnabled => $"Check {target} is enabled",
            StepAction.AssertProperty => value.Length > 0 ? $"Check {target} {value}" : $"Check {target}",
            StepAction.AssertItemExists => $"Check {target} has {quoted}".TrimEnd(),
            StepAction.AssertItemAbsent => $"Check {target} has no {quoted}".TrimEnd(),
            StepAction.KeyPress => title ?? (value.Length > 0 ? $"Press {value}" : "Press a key"),
            StepAction.CoordinateClick => title ?? $"Click at {step.X}, {step.Y}",
            StepAction.Expand => $"Expand {target}",
            StepAction.Collapse => $"Collapse {target}",
            StepAction.RealizeItem => $"Find {quoted} in {target}",
            StepAction.ScrollIntoView => $"Scroll {target} into view",
            StepAction.ScrollPercent => value.Length > 0 ? $"Scroll {target} to {value}%" : $"Scroll {target}",
            StepAction.GridEditCell => $"Edit a cell in {target}",
            StepAction.GridCommitRow => $"Commit the row in {target}",
            StepAction.GridCancelRow => $"Cancel the row edit in {target}",
            StepAction.Wait => title ?? "Wait",
            StepAction.Screenshot => title ?? "Take a screenshot",
            _ => title ?? "Step",
        };
        return phrase;
    }

    private static string? TargetName(UiElementInfo? element, string selector)
    {
        if (element is not null)
        {
            // Text elements are named by their content ("Customer added: Ada"), so prefer their id there.
            var textLike = element.ControlType is "Text" or "Document";
            var name = OneLine(element.Name);
            if (!textLike && name.Length is > 0 and <= MaxName) return name;
            if (!string.IsNullOrWhiteSpace(element.AutomationId)) return Humanize(element.AutomationId);
            if (name.Length > 0) return Clip(name, MaxName);
        }
        if (string.IsNullOrWhiteSpace(selector)) return null;
        if (selector.StartsWith("id:", StringComparison.Ordinal)) return Humanize(selector[3..]);
        if (selector.StartsWith("name:", StringComparison.Ordinal)) return Clip(OneLine(selector[5..]), MaxName);
        if (selector.StartsWith("path:", StringComparison.Ordinal))
        {
            var path = selector[5..].TrimEnd('/', '>', ' ');
            var cut = path.LastIndexOfAny(['/', '>']);
            return Clip(OneLine(cut >= 0 ? path[(cut + 1)..] : path), MaxName);
        }
        if (selector.StartsWith("query:", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(selector[6..]);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String) return Clip(OneLine(label.GetString()), MaxName);
                    if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) return Humanize(id.GetString() ?? "");
                    if (doc.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String) return (type.GetString() ?? "").ToLowerInvariant();
                }
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static string? MeaningfulTitle(string? title)
    {
        var t = OneLine(title);
        return t.Length == 0 || t == "New step" ? null : Clip(t, 60);
    }

    /// <summary>"AddCustomer" → "Add customer", "OKButton" → "OK button", "status_message" → "Status message".</summary>
    internal static string Humanize(string id)
    {
        id = id.Trim();
        if (id.Length == 0) return id;
        var words = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (c is '_' or '-' or ' ' or '.')
            {
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
                continue;
            }
            var boundary = current.Length > 0 && char.IsUpper(c) &&
                (!char.IsUpper(id[i - 1]) || i + 1 < id.Length && char.IsLower(id[i + 1]));
            if (boundary || current.Length > 0 && char.IsDigit(c) != char.IsDigit(id[i - 1]))
            {
                words.Add(current.ToString());
                current.Clear();
            }
            current.Append(c);
        }
        if (current.Length > 0) words.Add(current.ToString());
        for (var w = 0; w < words.Count; w++)
        {
            var word = words[w];
            var acronym = word.Length > 1 && word.All(ch => !char.IsLetter(ch) || char.IsUpper(ch));
            if (acronym) continue;
            words[w] = w == 0
                ? char.ToUpperInvariant(word[0]) + word[1..]
                : char.ToLowerInvariant(word[0]) + word[1..];
        }
        return string.Join(' ', words);
    }

    private static bool LooksSecret(string? text) =>
        !string.IsNullOrEmpty(text) && (text.Contains("password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("passwd", StringComparison.OrdinalIgnoreCase) || text.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || text.Contains("token", StringComparison.OrdinalIgnoreCase) || text.Contains("apikey", StringComparison.OrdinalIgnoreCase));

    private static string OneLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
