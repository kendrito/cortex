using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Testy.Core;

namespace Testy.Windows;

public sealed class UiAutomationDriver : ITargetDriver, IItemLookupDriver, IGuardedTargetDriver, IScreenshotEvidenceSource
{
    private bool disposed;
    private readonly CancellationTokenSource lifetime = new();
    public TargetInfo? Target { get; private set; }
    public ScreenshotEvidence? LastScreenshot => Target is { } target ? Native.LastScreenshot(target) : null;
    public int MaxElements { get; set; } = 2000;
    public int MaxDepth { get; set; } = 30;

    public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default) => Background<IReadOnlyList<TargetInfo>>(cancellationToken =>
    {
        var targets = new List<TargetInfo>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (!Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId || pid == 0) return true;
            var title = new System.Text.StringBuilder(2048); Native.GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                targets.Add(new TargetInfo { ProcessId = (int)pid, WindowHandle = hwnd, Title = title.ToString(), ProcessName = process.ProcessName });
            }
            catch (ArgumentException) { } catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        cancellationToken.ThrowIfCancellationRequested();
        return targets.OrderBy(t => t.Title).ToList();
    }, cancellationToken);

    public async Task AttachAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (processId <= 0 || processId == Environment.ProcessId) throw new ArgumentException("Choose a different running application process.", nameof(processId));
        var targets = await GetTargetsAsync(cancellationToken);
        ObjectDisposedException.ThrowIf(disposed, this); cancellationToken.ThrowIfCancellationRequested();
        var candidates = targets.Where(t => t.ProcessId == processId).ToList();
        if (candidates.Count == 0) throw new InvalidOperationException($"Process {processId} has no accessible visible titled window.");
        // MainWindowHandle avoids accidentally attaching a tiny tool window; area is a fallback.
        using var process = Process.GetProcessById(processId);
        var target = candidates.FirstOrDefault(t => t.WindowHandle == process.MainWindowHandle) ?? candidates.OrderByDescending(t =>
        { Native.GetWindowRect((nint)t.WindowHandle, out var r); return (long)(r.Right - r.Left) * (r.Bottom - r.Top); }).First();
        Native.RememberIdentity(target);
        Native.Validate(target); Target = target;
    }

    public Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => Background(cancellationToken =>
    {
        var target = RequireTarget();
        using var dpi = new Native.DpiScope();
        var nodes = ReadTree(target, cancellationToken, out bool truncated);
        return CreateSnapshot(target, nodes, truncated);
    }, cancellationToken);

    private static UiSnapshot CreateSnapshot(TargetInfo target, List<Node> nodes, bool truncated)
    {
        string focusedSelector = "";
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused?.Current.ProcessId == target.ProcessId)
            {
                var focusedRuntimeId = string.Join('.', focused.GetRuntimeId());
                focusedSelector = nodes.FirstOrDefault(n => n.Info.RuntimeId == focusedRuntimeId)?.Info.Selector ?? "";
            }
        }
        catch (Exception ex) when (UiaErrors.IsElementUnavailable(ex)) { }
        return new UiSnapshot
        {
            Target = TestyJson.Clone(target), Elements = nodes.Select(e => e.Info).ToList(), IsTruncated = truncated, FocusedSelector = focusedSelector,
            ScreenshotBounds = Native.ScreenshotBounds(target)
        };
    }

    public Task<UiSnapshot> SnapshotGuardedAsync(UiExecutionGuard guard, CancellationToken cancellationToken = default) => Background(token =>
    {
        var target = RequireTarget(); using var dpi = new Native.DpiScope();
        var nodes = ReadTree(target, token, out bool truncated);
        var snapshot = CreateSnapshot(target, nodes, truncated);
        ValidateGuard(snapshot, guard);
        return snapshot;
    }, cancellationToken);

    public Task<UiSnapshot> ExecuteGuardedAsync(TestStep resolvedStep, UiExecutionGuard guard, CancellationToken cancellationToken = default) => Background(token =>
    {
        var target = RequireTarget(); using var dpi = new Native.DpiScope();
        var nodes = ReadTree(target, token, out bool truncated);
        var snapshot = CreateSnapshot(target, nodes, truncated);
        var matched = ValidateGuard(snapshot, guard);
        if (resolvedStep.Selector != guard.Selector || !TestValidator.RequiresSelector(resolvedStep.Action) || TestValidator.IsAssertion(resolvedStep.Action))
            throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, "The guarded mutation does not match the validated selector operation.", resolvedStep), snapshot);
        var node = nodes.Single(n => ReferenceEquals(n.Info, matched));
        ExecuteNode(target, node, resolvedStep, token, dispatched =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Native.Validate(target);
                var current = node.Element.Current;
                if (current.ProcessId != guard.ExpectedProcessId || !current.IsEnabled || !Native.IsWindowEnabled(node.Window)
                    || current.IsPassword || string.Join('.', node.Element.GetRuntimeId()) != guard.ExpectedRuntimeId
                    || current.ControlType.ProgrammaticName.Replace("ControlType.", "") != guard.ExpectedControlType
                    || current.AutomationId != guard.ExpectedAutomationId || current.Name != guard.ExpectedName)
                    throw new InvalidOperationException("The live recovery control identity or readiness changed before input.");
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable,
                    "The immediate live selector-recovery check failed; no further input was issued.", resolvedStep,
                    actionOutcome: dispatched ? ActionOutcome.Unknown : ActionOutcome.NotDispatched, observedAt: DateTimeOffset.UtcNow), snapshot, ex);
            }
        });
        return snapshot;
    }, cancellationToken);

    private static UiElementInfo ValidateGuard(UiSnapshot snapshot, UiExecutionGuard guard)
    {
        try { return SelectorRecovery.ValidateGuard(snapshot, guard); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        { throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, "Selector recovery was rejected before input: " + ex.Message, observedAt: snapshot.CapturedAt), snapshot, ex); }
    }

    public Task ExecuteAsync(TestStep step, CancellationToken cancellationToken = default) => Background(cancellationToken =>
    {
        var target = RequireTarget(); cancellationToken.ThrowIfCancellationRequested();
        using var dpi = new Native.DpiScope();
        if (step.Action == StepAction.CoordinateClick) { Native.Click(target, step.X, step.Y, cancellationToken); return true; }
        if (step.Action == StepAction.KeyPress && string.IsNullOrWhiteSpace(step.Selector)) { Native.Press(target, step.Value, cancellationToken: cancellationToken); return true; }
        var nodes = ReadTree(target, cancellationToken, out bool truncated);
        if (truncated) throw new InvalidOperationException("The UI tree was truncated. A unique selector cannot be verified safely.");
        var node = Resolve(nodes, step.Selector);
        ExecuteNode(target, node, step, cancellationToken);
        return true;
    }, cancellationToken);

    private void ExecuteNode(TargetInfo target, Node node, TestStep step, CancellationToken cancellationToken, Action<bool>? identityGuard = null)
    {
        bool dispatched = false;
        void BeforeInput()
        {
            cancellationToken.ThrowIfCancellationRequested();
            identityGuard?.Invoke(dispatched);
            dispatched = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!node.Info.IsEnabled || !Native.IsWindowEnabled(node.Window)) throw new InvalidOperationException($"Control '{step.Selector}' is disabled or its window is blocked by a modal dialog.");
        Native.Validate(target);
        var live = node.Element.Current;
        if (live.ProcessId != target.ProcessId) throw new InvalidOperationException("The selected element is outside the attached process.");
        if (!live.IsEnabled) throw new InvalidOperationException("The selected control became disabled before input.");
        if (step.Action is StepAction.GridEditCell or StepAction.GridCommitRow or StepAction.GridCancelRow)
            throw new NotSupportedException("Transactional grid editing requires the opted-in WPF probe; UI Automation does not expose the GridEdit/GridCommit/GridCancel contract.");
        if (AdvancedSteps.IsMutation(step.Action)) { UiaAdvancedControls.Execute(node.Element, step, target, node.Window, cancellationToken, BeforeInput); return; }
        switch (step.Action)
        {
            case StepAction.Click:
                if (node.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) { BeforeInput(); ((InvokePattern)invoke).Invoke(); }
                else if (node.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var item)) { BeforeInput(); ((SelectionItemPattern)item).Select(); }
                else throw new InvalidOperationException("This control does not expose Invoke or SelectionItem. Use an explicit CoordinateClick for custom-drawn controls.");
                break;
            case StepAction.TypeText:
                if (node.Info.IsPassword) throw new InvalidOperationException("Password fields are not editable through recorded test values.");
                if (!node.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) throw new InvalidOperationException("This control does not expose ValuePattern for text entry.");
                if (((ValuePattern)value).Current.IsReadOnly) throw new InvalidOperationException("The text control is read-only.");
                BeforeInput(); ((ValuePattern)value).SetValue(step.Value);
                break;
            case StepAction.Toggle:
                if (!node.Element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) throw new InvalidOperationException("This control does not expose TogglePattern.");
                var pattern = (TogglePattern)toggle;
                if (string.IsNullOrWhiteSpace(step.Value)) { BeforeInput(); pattern.Toggle(); }
                else
                {
                    bool desired = ParseBoolean(step.Value);
                    var desiredState = desired ? ToggleState.On : ToggleState.Off;
                    for (int i = 0; i < 3 && pattern.Current.ToggleState != desiredState; i++) { BeforeInput(); pattern.Toggle(); }
                    if (pattern.Current.ToggleState != desiredState) throw new InvalidOperationException("The requested toggle state could not be reached.");
                }
                break;
            case StepAction.Select:
                if (node.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection)) { BeforeInput(); ((SelectionItemPattern)selection).Select(); }
                else
                {
                    if (node.Element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand)) { BeforeInput(); ((ExpandCollapsePattern)expand).Expand(); }
                    var choices = FindSelectionItems(node.Element, step.Value, target.ProcessId, cancellationToken);
                    if (choices.Count != 1) throw new InvalidOperationException($"Expected one selectable item named '{step.Value}', found {choices.Count}.");
                    BeforeInput(); choices[0].Select();
                }
                break;
            case StepAction.KeyPress:
                BeforeInput(); Native.Foreground(target, node.Window, cancellationToken);
                BeforeInput(); node.Element.SetFocus();
                BeforeInput(); Native.Press(target, step.Value, node.Window, cancellationToken); break;
            default: throw new NotSupportedException($"{step.Action} is a runner operation, not a native input action.");
        }
    }

    public Task<string> CaptureAsync(string filePath, CancellationToken cancellationToken = default) => Background(token => { token.ThrowIfCancellationRequested(); return Native.Capture(RequireTarget(), filePath, token); }, cancellationToken);

    public Task<ItemLookupResult> LookupItemAsync(string containerSelector, string itemValue, CancellationToken cancellationToken = default) => Background(token =>
    {
        var query = AdvancedSteps.ParseItem(itemValue); var target = RequireTarget();
        var nodes = ReadTree(target, token, out bool truncated);
        if (truncated) return new ItemLookupResult { Status = ItemLookupStatus.Unavailable, Message = "The UI tree was truncated; a unique item container cannot be established." };
        var container = Resolve(nodes, containerSelector); token.ThrowIfCancellationRequested();
        return UiaAdvancedControls.Lookup(container.Element, query, token);
    }, cancellationToken);

    private TargetInfo RequireTarget() { ObjectDisposedException.ThrowIf(disposed, this); var target = Target ?? throw new InvalidOperationException("Attach to a target application first."); Native.Validate(target); return target; }
    private async Task<T> Background<T>(Func<CancellationToken, T> operation, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
        // UIA must run off the WPF UI thread. Native providers cannot themselves be interrupted.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(20));
        return await Task.Run(() => operation(linked.Token), linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
    }
    private static List<SelectionItemPattern> FindSelectionItems(AutomationElement parent, string name, int processId, CancellationToken token)
    {
        var matches = new List<SelectionItemPattern>(); var pending = new Stack<AutomationElement>(); pending.Push(parent);
        int visited = 0; var watch = Stopwatch.StartNew(); var walker = TreeWalker.RawViewWalker;
        while (pending.TryPop(out var element) && visited++ < 2000 && watch.ElapsedMilliseconds < 5000)
        {
            token.ThrowIfCancellationRequested();
            if (element.Current.ProcessId != processId) continue;
            if (element != parent && element.Current.Name == name && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var item)) matches.Add((SelectionItemPattern)item);
            if (matches.Count > 1) return matches;
            var child = walker.GetFirstChild(element);
            int children = 0;
            while (child != null && children++ < 2000 && watch.ElapsedMilliseconds < 5000) { pending.Push(child); child = walker.GetNextSibling(child); }
        }
        if (pending.Count > 0) throw new InvalidOperationException("Selection search exceeded its bounded tree limit. Use the item's unique selector.");
        return matches;
    }
    private sealed record Node(AutomationElement Element, UiElementInfo Info, string Path, nint Window);
    private List<Node> ReadTree(TargetInfo target, CancellationToken token, out bool isTruncated)
    {
        var nodes = new List<Node>(); var walker = TreeWalker.RawViewWalker;
        var snapshotRequest = UiaAdvancedControls.CreateSnapshotRequest();
        var windows = Native.ProcessWindows(target);
        var windowSet = windows.ToHashSet();
        bool truncated = false; int elementLimit = Math.Clamp(MaxElements, 1, 5000), depthLimit = Math.Clamp(MaxDepth, 1, 50);
        var deadline = Stopwatch.StartNew();
        void Visit(AutomationElement element, int depth, string path, nint window)
        {
            token.ThrowIfCancellationRequested();
            if (nodes.Count >= elementLimit || depth > depthLimit || deadline.ElapsedMilliseconds > 10000) { truncated = true; return; }
            try
            {
                // Each node gets one fresh property batch; no cache survives this traversal.
                var cached = element.GetUpdatedCache(snapshotRequest);
                var current = cached.Cached;
                if (current.ProcessId != target.ProcessId) return;
                var nativeWindow = (nint)(uint)current.NativeWindowHandle;
                if (depth > 0 && nativeWindow != 0 && nativeWindow != window && Native.RootWindow(nativeWindow) == nativeWindow)
                {
                    // UIA may expose an owned dialog both beneath its owner and as a desktop root.
                    // Visit each top-level HWND separately, preserving the correct input window.
                    if (!windowSet.Contains(nativeWindow)) truncated = true; // Window opened during traversal; refresh before acting.
                    return;
                }
                bool password = current.IsPassword, valueTruncated = false; string value = ""; string? valueText = null;
                if (password) value = "[REDACTED]";
                else if (UiaAdvancedControls.Supports(cached, AutomationElement.IsValuePatternAvailableProperty))
                {
                    // Value is intentionally excluded from the bulk request: never ask a password
                    // provider for its text, even when a custom provider incorrectly exposes Value.
                    var text = element.GetCurrentPropertyValue(ValuePattern.ValueProperty, true);
                    if (ReferenceEquals(text, AutomationElement.NotSupported)) throw new InvalidOperationException("The UI Automation provider advertised Value but did not supply its value.");
                    if (text is not string content) throw new InvalidOperationException("The UI Automation provider supplied an invalid text value.");
                    value = valueText = content;
                }
                else if (UiaAdvancedControls.Supports(cached, AutomationElement.IsTextPatternAvailableProperty))
                {
                    // One code unit of lookahead distinguishes a complete value from an 8192-unit
                    // prefix. Preserve that proof even if trailing newline normalization removes it.
                    var text = ((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(8193);
                    valueTruncated = text.Length > 8192;
                    value = text.TrimEnd('\r', '\n');
                }
                else if (UiaAdvancedControls.Supports(cached, AutomationElement.IsTogglePatternAvailableProperty))
                {
                    var state = cached.GetCachedPropertyValue(TogglePattern.ToggleStateProperty, true);
                    if (state is not ToggleState toggle) throw new InvalidOperationException("The UI Automation provider advertised Toggle but did not supply its state.");
                    value = toggle.ToString();
                }
                else if (UiaAdvancedControls.Supports(cached, AutomationElement.IsSelectionPatternAvailableProperty)) value = string.Join(", ", ((SelectionPattern)element.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection().Select(e => e.Current.Name));
                else if (current.ControlType == ControlType.Text) value = current.Name;
                valueTruncated |= value.Length > 8192;
                var rect = current.BoundingRectangle;
                nodes.Add(new Node(element, new UiElementInfo
                {
                    AutomationId = current.AutomationId ?? "", Name = current.Name ?? "", ClassName = current.ClassName ?? "", RuntimeId = string.Join('.', cached.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty, true) as int[] ?? element.GetRuntimeId()),
                    ControlType = current.ControlType.ProgrammaticName.Replace("ControlType.", ""), Value = value.Length > 8192 ? value[..8192] : value, IsValueTruncated = valueTruncated,
                    IsEnabled = current.IsEnabled && Native.IsWindowEnabled(window), IsOffscreen = current.IsOffscreen, IsPassword = password, Depth = depth,
                    Bounds = new() { X = double.IsFinite(rect.X) ? rect.X : 0, Y = double.IsFinite(rect.Y) ? rect.Y : 0, Width = double.IsFinite(rect.Width) ? rect.Width : 0, Height = double.IsFinite(rect.Height) ? rect.Height : 0 }
                }, path, window));
                UiaAdvancedControls.ObserveCached(cached, nodes[^1].Info, valueText, token);
                var child = walker.GetFirstChild(element); int index = 0;
                if (depth >= depthLimit) { if (child != null) truncated = true; return; }
                while (child != null && nodes.Count < elementLimit && deadline.ElapsedMilliseconds <= 10000)
                {
                    Visit(child, depth + 1, path + "/" + index++, window); child = walker.GetNextSibling(child);
                }
                if (child != null) truncated = true;
            }
            // A disappearing, virtualized or recycled node must not permit a false absence or unique match. Marking the tree
            // truncated lets callers observe again (the runner re-reads evidence within its deadline and never repeats the action).
            catch (Exception ex) when (UiaErrors.IsElementUnavailable(ex)) { truncated = true; }
        }
        int windowIndex = 0;
        foreach (nint window in windows)
        {
            try { Visit(AutomationElement.FromHandle(window), 0, (windowIndex++).ToString(), window); }
            catch (Exception ex) when (UiaErrors.IsElementUnavailable(ex)) { if (Native.IsWindow(window) && Native.IsWindowVisible(window)) truncated = true; }
        }
        var ids = nodes.Where(n => n.Info.AutomationId.Length > 0).GroupBy(n => n.Info.AutomationId).ToDictionary(g => g.Key, g => g.Count());
        var names = nodes.Where(n => n.Info.Name.Length > 0).GroupBy(n => n.Info.Name).ToDictionary(g => g.Key, g => g.Count());
        foreach (var node in nodes) node.Info.Selector = node.Info.AutomationId.Length > 0 && ids[node.Info.AutomationId] == 1 ? "id:" + node.Info.AutomationId : node.Info.Name.Length > 0 && names[node.Info.Name] == 1 ? "name:" + node.Info.Name : "path:" + node.Path;
        isTruncated = truncated;
        return nodes;
    }
    private static Node Resolve(List<Node> nodes, string selector)
    {
        UiSelectors.Validate(selector);
        IEnumerable<Node> matches = selector.StartsWith("id:", StringComparison.Ordinal) ? nodes.Where(n => n.Info.AutomationId == selector[3..])
            : selector.StartsWith("name:", StringComparison.Ordinal) ? nodes.Where(n => n.Info.Name == selector[5..])
            : selector.StartsWith("path:", StringComparison.Ordinal) ? nodes.Where(n => n.Path == selector[5..])
            : QueryMatches();
        IEnumerable<Node> QueryMatches()
        {
            var selected = UiSelectors.Find(new UiSnapshot { Elements = nodes.Select(n => n.Info).ToList() }, selector).ToHashSet();
            return nodes.Where(n => selected.Contains(n.Info));
        }
        var found = matches.Take(2).ToList();
        return found.Count switch { 1 => found[0], 0 => throw new InvalidOperationException($"No element matches '{selector}'. Refresh the inspector or wait for the UI."), _ => throw new InvalidOperationException($"Selector '{selector}' is ambiguous. Use a unique AutomationId, scoped query, or path.") };
    }
    internal static bool ParseBoolean(string value) => value.ToLowerInvariant() switch { "true" or "on" or "checked" or "1" => true, "false" or "off" or "unchecked" or "0" => false, _ => throw new ArgumentException("Toggle value must be true/false or on/off.") };
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); Target = null; }
}
