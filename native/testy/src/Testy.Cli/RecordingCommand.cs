using System.IO;
using System.Diagnostics;
using System.Windows.Automation;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

internal sealed class Demonstration
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TargetInfo Target { get; set; } = new();
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public bool Completed { get; set; }
    public bool RecordingActive { get; set; }
    public bool Cancelled { get; set; }
    public int DroppedEvents { get; set; }
    public string Scope { get; set; } = "Observed Invoke and Value changes in the attached initial window. These events do not prove human intent, complete gesture coverage, or application correctness.";
    public List<DemonstratedAction> Actions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public UiSnapshot InitialSnapshot { get; set; } = new();
    public UiSnapshot FinalSnapshot { get; set; } = new();
    public string InitialScreenshot { get; set; } = "";
    public string FinalScreenshot { get; set; } = "";
}

internal sealed class DemonstratedAction
{
    public int Sequence { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public TestStep Step { get; set; } = new();
    public string EventKind { get; set; } = "";
    public string RuntimeId { get; set; } = "";
    public DateTimeOffset ResolvedAt { get; set; }
}

internal static class RecordingCommand
{
    internal sealed record Identity(string RuntimeId, string AutomationId, string Name, string ControlType);
    private sealed record Notice(long Order, Identity Identity, Identity[] Ancestors, StepAction Action, string Value, DateTimeOffset At);

    internal static bool SameIdentity(Identity observed, UiElementInfo current) => !current.IsPassword &&
        observed == new Identity(current.RuntimeId, current.AutomationId, current.Name, current.ControlType);

    private static Identity ReadIdentity(AutomationElement element, bool ancestor = false)
    {
        var info = element.Current;
        if (info.IsPassword) throw new InvalidOperationException("Password control identity is excluded.");
        string type = info.ControlType.ProgrammaticName.Replace("ControlType.", "");
        string name = !ancestor || type is "DataItem" or "ListItem" or "TreeItem" ? info.Name ?? "" : "";
        var identity = new Identity(string.Join('.', element.GetRuntimeId()), info.AutomationId ?? "", name, type);
        if (identity.RuntimeId.Length > 512 || identity.AutomationId.Length > 1024 || name.Length > 2048)
            throw new InvalidOperationException("Recording identity exceeds the capture limit.");
        return identity;
    }

    private static Identity[] ReadAncestors(AutomationElement element, string rootId)
    {
        var parents = new List<Identity>();
        if (string.Join('.', element.GetRuntimeId()) == rootId) return [];
        for (var parent = TreeWalker.RawViewWalker.GetParent(element); parent is not null && parents.Count < 50; parent = TreeWalker.RawViewWalker.GetParent(parent))
        {
            var identity = ReadIdentity(parent, true); parents.Add(identity);
            if (identity.RuntimeId == rootId) return parents.ToArray();
        }
        throw new InvalidOperationException("The event no longer belongs to the initial recording window.");
    }

    internal static Identity[] SnapshotAncestors(UiSnapshot snapshot, UiElementInfo element)
    {
        var result = new List<Identity>(); int depth = element.Depth;
        for (int index = snapshot.Elements.IndexOf(element) - 1; index >= 0 && depth > 0; index--)
        {
            var parent = snapshot.Elements[index];
            if (parent.Depth >= depth) continue;
            if (parent.Depth != depth - 1 || parent.IsPassword) return [];
            result.Add(new(parent.RuntimeId, parent.AutomationId, parent.ControlType is "DataItem" or "ListItem" or "TreeItem" ? parent.Name : "", parent.ControlType));
            depth = parent.Depth;
        }
        return result.ToArray();
    }

    public static async Task<Demonstration> RecordAsync(int processId, string output, int durationSeconds, CancellationToken ct)
    {
        if (durationSeconds is < 1 or > 600) throw new ArgumentException("Recording duration must be 1–600 seconds.");
        output = Path.GetFullPath(output);
        if (File.Exists(output)) throw new IOException("The recording output already exists; choose a new file.");
        string evidence = Path.Combine(Path.GetDirectoryName(output)!, "demonstration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        using var driver = new UiAutomationDriver();
        await driver.AttachAsync(processId, ct);
        var demonstration = new Demonstration { Target = TestyJson.Clone(driver.Target!) };
        // Reserve the output without overwriting a file created concurrently after the earlier check.
        MaintenanceCommand.WriteNew(output, demonstration);
        var capture = new RecordingCaptureGate<Notice>(256);
        int unresolved = 0;
        string rootId = "";
        AutomationElement? root = null;
        AutomationEventHandler? invoked = null;
        AutomationPropertyChangedEventHandler? changed = null;
        bool invokeRegistered = false, valueRegistered = false;
        void Save() => WorkspaceStore.WriteAtomic(output, demonstration);
        void Observe(object sender, StepAction action)
        {
            if (sender is not AutomationElement element || !capture.TryBegin(out long order)) return;
            Notice? notice = null; bool failed = false; var at = DateTimeOffset.UtcNow;
            try
            {
                if (element.Current.ProcessId != processId || element.Current.IsPassword) return;
                var identity = ReadIdentity(element);
                var ancestors = ReadAncestors(element, rootId);
                // Read the current safe pattern, never trust an event payload containing another control's data.
                string value = "";
                if (action == StepAction.TypeText)
                {
                    if (element.Current.ControlType != ControlType.Edit || !element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) return;
                    var current = ((ValuePattern)pattern).Current;
                    if (current.IsReadOnly) return;
                    value = current.Value;
                    if (value.Length > 8192) { failed = true; return; }
                }
                if (ReadIdentity(element) != identity || !ancestors.SequenceEqual(ReadAncestors(element, rootId))) { failed = true; return; }
                notice = new(order, identity, ancestors, action, value, at);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            { failed = true; }
            finally { capture.Complete(notice, failed); }
        }
        async Task DrainAsync(CancellationToken token)
        {
            var batch = new List<Notice>();
            foreach (var notice in capture.Drain().OrderBy(n => n.Order))
            {
                if (notice.Action == StepAction.TypeText && batch.LastOrDefault() is { Action: StepAction.TypeText } previous && previous.Identity == notice.Identity && previous.Ancestors.SequenceEqual(notice.Ancestors))
                    batch[^1] = notice;
                else batch.Add(notice);
            }
            if (batch.Count == 0) return;
            var snapshot = await driver.SnapshotAsync(token);
            if (snapshot.IsTruncated) { unresolved += batch.Count; return; }
            foreach (var notice in batch)
            {
                var matches = snapshot.Elements.Where(e => SameIdentity(notice.Identity, e)).ToArray();
                if (matches.Length != 1 || demonstration.Actions.Count >= 200 || !notice.Ancestors.SequenceEqual(SnapshotAncestors(snapshot, matches[0]))) { unresolved++; continue; }
                var element = matches[0];
                string selector = element.Selector;
                if (element.AutomationId.Length > 0 && snapshot.Elements.Count(e => e.AutomationId == element.AutomationId) == 1) selector = "id:" + element.AutomationId;
                if (string.IsNullOrWhiteSpace(selector) || UiSelectors.Find(snapshot, selector).Count != 1) { unresolved++; continue; }
                var action = new DemonstratedAction
                {
                    Sequence = demonstration.Actions.Count, ObservedAt = notice.At, ResolvedAt = snapshot.CapturedAt,
                    RuntimeId = notice.Identity.RuntimeId, EventKind = notice.Action == StepAction.Click ? "UIA Invoke" : "UIA Value changed",
                    Step = new TestStep { Title = notice.Action + " " + selector, Action = notice.Action, Selector = selector, Value = notice.Value }
                };
                // Coalescing is confined to the identity-checked batch above. A runtime ID may
                // be recycled between drains, so separate observations remain separate actions.
                demonstration.Actions.Add(action);
            }
            demonstration.DroppedEvents = capture.Dropped + unresolved;
            Save();
        }
        async Task StopAsync()
        {
            capture.StopAccepting();
            try
            {
                if (invokeRegistered) { invokeRegistered = false; Automation.RemoveAutomationEventHandler(InvokePattern.InvokedEvent, root!, invoked!); }
            }
            finally
            {
                try { if (valueRegistered) { valueRegistered = false; Automation.RemoveAutomationPropertyChangedEventHandler(root!, changed!); } }
                finally
                {
                    await capture.FinishAsync(TimeSpan.FromSeconds(2));
                    demonstration.RecordingActive = false;
                }
            }
        }
        try
        {
            demonstration.InitialSnapshot = await driver.SnapshotAsync(ct);
            demonstration.InitialScreenshot = await driver.CaptureAsync(Path.Combine(evidence, "initial.png"), ct);
            root = AutomationElement.FromHandle((nint)demonstration.Target.WindowHandle);
            rootId = string.Join('.', root.GetRuntimeId());
            invoked = (sender, _) => Observe(sender, StepAction.Click);
            changed = (sender, _) => Observe(sender, StepAction.TypeText);
            Automation.AddAutomationEventHandler(InvokePattern.InvokedEvent, root, TreeScope.Subtree, invoked); invokeRegistered = true;
            Automation.AddAutomationPropertyChangedEventHandler(root, TreeScope.Subtree, changed, ValuePattern.ValueProperty); valueRegistered = true;
            demonstration.RecordingActive = true;
            Save();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(durationSeconds))
            {
                ct.ThrowIfCancellationRequested();
                await DrainAsync(ct);
                await Task.Delay(100, ct);
            }
            await StopAsync();
            await DrainAsync(ct);
            demonstration.FinalSnapshot = await driver.SnapshotAsync(ct);
            demonstration.FinalScreenshot = await driver.CaptureAsync(Path.Combine(evidence, "final.png"), ct);
            demonstration.DroppedEvents = capture.Dropped + unresolved;
            demonstration.Completed = demonstration.DroppedEvents == 0 && !demonstration.InitialSnapshot.IsTruncated && !demonstration.FinalSnapshot.IsTruncated;
            if (demonstration.DroppedEvents > 0) demonstration.Warnings.Add("Some events could not be resolved without ambiguity or exceeded capture limits. This recording is incomplete.");
            demonstration.Warnings.Add("Review event order and selectors. Assertions, keyboard shortcuts, selections, drag operations, and separately opened windows are not inferred by this recorder.");
            return demonstration;
        }
        catch (OperationCanceledException) { demonstration.Cancelled = true; throw; }
        catch (Exception ex) { demonstration.Warnings.Add(ex.Message); throw; }
        finally
        {
            try { await StopAsync(); }
            finally
            {
                demonstration.DroppedEvents = capture.Dropped + unresolved;
                demonstration.FinishedAt = DateTimeOffset.UtcNow; Save();
            }
        }
    }
}

// A stopped capture waits for callbacks that already entered. Sealing rejects late publications.
internal sealed class RecordingCaptureGate<T>(int capacity) where T : class
{
    private readonly object sync = new();
    private readonly Queue<T> queue = new();
    private readonly TaskCompletionSource idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool accepting = true, sealedCapture;
    private int active, dropped;
    private long nextOrder;
    public int Dropped { get { lock (sync) return dropped; } }
    public bool TryBegin(out long order)
    {
        lock (sync) { order = nextOrder++; if (!accepting) return false; active++; return true; }
    }
    public void Complete(T? value, bool failed = false)
    {
        lock (sync)
        {
            if (!sealedCapture)
            {
                if (failed || value is not null && queue.Count >= capacity) dropped++;
                else if (value is not null) queue.Enqueue(value);
            }
            active--;
            if (!accepting && active == 0) idle.TrySetResult();
        }
    }
    public void StopAccepting() { lock (sync) { accepting = false; if (active == 0) idle.TrySetResult(); } }
    public async Task FinishAsync(TimeSpan timeout)
    {
        StopAccepting();
        try { await idle.Task.WaitAsync(timeout); } catch (TimeoutException) { }
        lock (sync) { if (!sealedCapture && active > 0) dropped += active; sealedCapture = true; }
    }
    public T[] Drain()
    {
        lock (sync)
        {
            if (active > 0 && !sealedCapture) return [];
            var values = queue.ToArray(); queue.Clear(); return values;
        }
    }
}
