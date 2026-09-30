using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Testy.Core;

namespace Testy.WpfProbe;

/// <summary>Opt-in, same-user inspection bridge for an application you build and own.</summary>
public sealed class ProbeServer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetLastActivePopup(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint value);
    private readonly CancellationTokenSource stop = new();
    private readonly Application application;
    private readonly Task listener;
    private readonly ProbeDiagnostics diagnostics;
    private readonly ConditionalWeakTable<DependencyObject, VisualIdentity> visualIdentities = new();
    private readonly string identityScope = "wpf:" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + Guid.NewGuid().ToString("N") + ":";
    private long nextVisualIdentity;
    private sealed record VisualIdentity(string Value);
    private string RuntimeIdentity(DependencyObject visual) => visualIdentities.GetValue(visual,
        _ => new VisualIdentity(identityScope + Interlocked.Increment(ref nextVisualIdentity).ToString(CultureInfo.InvariantCulture))).Value;
    public static string PipeName(int processId) => $"testy-wpf-{processId}";
    private ProbeServer(Application application) { this.application = application; diagnostics = new ProbeDiagnostics(RuntimeIdentity); listener = Task.Run(ListenAsync); }
    public static ProbeServer Start()
    {
        var app = Application.Current ?? throw new InvalidOperationException("Start the probe after the WPF Application has been created.");
        app.Dispatcher.VerifyAccess();
        return new ProbeServer(app);
    }
    private async Task ListenAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName(Environment.ProcessId), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                string json = await ReadBoundedAsync(reader, timeout.Token);
                ProbeResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<ProbeRequest>(json, TestyJson.Options) ?? throw new InvalidOperationException("Empty probe request.");
                    response = await application.Dispatcher.InvokeAsync(() => Handle(request, timeout.Token), System.Windows.Threading.DispatcherPriority.Background, timeout.Token).Task;
                }
                catch (StepDiagnosticException ex) { response = new ProbeResponse { Error = ex.Message, Diagnostic = ex.Diagnostic, Observation = ex.Observation }; }
                catch (Exception ex) { response = new ProbeResponse { Error = ex.Message }; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, WireOptions).AsMemory(), timeout.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { /* A client disconnected or supplied an invalid request. */ }
        }
    }
    private static JsonSerializerOptions WireOptions { get; } = new(TestyJson.Options) { WriteIndented = false };
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); char[] buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == '\n') return text.ToString();
            if (text.Length >= 64 * 1024) throw new IOException("Probe request exceeds 64 KB.");
            text.Append(buffer[0]);
        }
        throw new IOException("The probe client disconnected before completing its request.");
    }
    private ProbeResponse Handle(ProbeRequest request, CancellationToken ct)
    {
        Window? window = application.Windows.Cast<Window>().FirstOrDefault(w => new WindowInteropHelper(w).Handle.ToInt64() == request.WindowHandle);
        if (window == null) throw new InvalidOperationException("The requested window does not belong to this WPF application.");
        var windows = new List<Window> { window };
        windows.AddRange(application.Windows.Cast<Window>().Where(w => w != window && w.IsVisible));
        foreach (var observedWindow in windows) diagnostics.Hook(observedWindow);
        ct.ThrowIfCancellationRequested();
        var nodes = ReadTree(windows, ct, out bool truncated);
        nint active = GetAncestor(GetForegroundWindow(), 2); GetWindowThreadProcessId(active, out uint activePid);
        if (activePid != Environment.ProcessId || !IsWindowVisible(active) || !IsWindowEnabled(active)) active = GetAncestor(GetLastActivePopup((nint)request.WindowHandle), 2);
        GetWindowThreadProcessId(active, out activePid);
        if (activePid != Environment.ProcessId || !IsWindowVisible(active)) active = (nint)request.WindowHandle;
        RECT rect; nint dpi = SetThreadDpiAwarenessContext((nint)(-4));
        try { if (!GetWindowRect(active, out rect)) throw new InvalidOperationException("Cannot identify the screenshot window bounds."); }
        finally { if (dpi != 0) SetThreadDpiAwarenessContext(dpi); }
        var focused = System.Windows.Input.Keyboard.FocusedElement;
        var focusedSelector = nodes.FirstOrDefault(n => ReferenceEquals(n.Element, focused))?.Info.Selector ?? "";
        var context = diagnostics.Read(nodes.Select(n => n.Info));
        var snapshot = new UiSnapshot
        {
            Source = "Opt-in WPF visual tree", Target = new TargetInfo { ProcessId = Environment.ProcessId, WindowHandle = request.WindowHandle, Title = window.Title, ProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName }, Elements = nodes.Select(n => n.Info).ToList(), IsTruncated = truncated,
            ScreenshotBounds = new() { X = rect.Left, Y = rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top }, FocusedSelector = focusedSelector,
            ApplicationDiagnostics = context.Entries, DiagnosticsTruncated = context.Truncated
        };
        UiElementInfo? guarded = null;
        if (request.Command is "snapshotGuarded" or "executeGuarded")
        {
            try { guarded = SelectorRecovery.ValidateGuard(snapshot, request.Guard ?? throw new InvalidOperationException("Missing execution guard.")); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
            { throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, "Selector recovery was rejected before input: " + ex.Message, observedAt: snapshot.CapturedAt), snapshot, ex); }
        }
        if (request.Command is "snapshot" or "snapshotGuarded") return new ProbeResponse { Snapshot = snapshot };
        if (request.Command is not ("execute" or "executeGuarded" or "lookupItem") || request.Step == null) throw new InvalidOperationException("Unknown probe command.");
        if (truncated) throw new InvalidOperationException("The WPF tree was truncated. A unique selector cannot be verified safely.");
        var step = request.Step;
        if (request.Command == "executeGuarded" && (step.Selector != request.Guard!.Selector || !TestValidator.RequiresSelector(step.Action) || TestValidator.IsAssertion(step.Action)))
            throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, "The guarded mutation does not match the validated selector operation.", step), snapshot);
        UiSelectors.Validate(step.Selector);
        var matches = step.Selector.StartsWith("path:", StringComparison.Ordinal)
            ? nodes.Where(n => n.Path == step.Selector[5..]).Select(n => n.Info).ToHashSet()
            : UiSelectors.Find(new UiSnapshot { Elements = nodes.Select(n => n.Info).ToList(), IsTruncated = truncated }, step.Selector).ToHashSet();
        var found = nodes.Where(n => guarded is not null ? ReferenceEquals(n.Info, guarded) : matches.Contains(n.Info)).Take(2).ToList();
        if (found.Count != 1) throw new InvalidOperationException(found.Count == 0 ? $"No WPF element matches '{step.Selector}'." : $"Selector '{step.Selector}' is ambiguous.");
        var node = found[0];
        if (request.Command == "lookupItem")
        {
            if (node.Element is not FrameworkElement container) throw new InvalidOperationException("The selected visual is not an item container.");
            return new ProbeResponse { ItemLookup = ProbeAdvancedControls.Lookup(container, AdvancedSteps.ParseItem(step.Value), ct) };
        }
        if (!node.Info.IsEnabled) throw new InvalidOperationException("The WPF control is disabled.");
        if (node.Element is not FrameworkElement element) throw new InvalidOperationException("The selected visual does not support input.");
        var containingWindow = element as Window ?? Window.GetWindow(element);
        if (!IsWindowEnabled(node.WindowHandle) || containingWindow is not null && !IsWindowEnabled(new WindowInteropHelper(containingWindow).Handle))
            throw new InvalidOperationException("The WPF control's window is disabled or blocked by a modal dialog.");
        void Guard()
        {
            ct.ThrowIfCancellationRequested();
            GetWindowThreadProcessId(node.WindowHandle, out var inputPid);
            if (!element.IsEnabled || inputPid != Environment.ProcessId || !IsWindowVisible(node.WindowHandle) || !IsWindowEnabled(node.WindowHandle) || containingWindow is not null && !IsWindowEnabled(new WindowInteropHelper(containingWindow).Handle))
                throw new InvalidOperationException("The WPF control is disabled or its window is blocked by a modal dialog.");
        }
        Guard();
        if (step.Action is StepAction.GridEditCell or StepAction.GridCommitRow or StepAction.GridCancelRow)
        {
            bool completed = false;
            try { GridAutomation.Execute(element, step, Guard, ct); completed = true; }
            finally { diagnostics.Operation(element, step.Action, completed); }
            return new ProbeResponse { Success = true, Snapshot = guarded is null ? null : snapshot };
        }
        if (AdvancedSteps.IsMutation(step.Action))
        {
            ProbeAdvancedControls.Execute(element, step, Guard, ct);
            return new ProbeResponse { Success = true, Snapshot = guarded is null ? null : snapshot };
        }
        switch (step.Action)
        {
            case StepAction.Click:
                if (ProbeAdvancedControls.PatternFor(element, PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
                else if (ProbeAdvancedControls.PatternFor(element, PatternInterface.SelectionItem) is ISelectionItemProvider selection) selection.Select();
                else throw new InvalidOperationException("This visual does not expose a supported Invoke or SelectionItem action.");
                break;
            case StepAction.TypeText:
                if (element is not TextBox textbox || textbox.IsReadOnly) throw new InvalidOperationException("Typing requires an editable WPF TextBox. PasswordBox is intentionally unsupported.");
                textbox.SetCurrentValue(TextBox.TextProperty, step.Value); break;
            case StepAction.Toggle:
                if (element is not ToggleButton toggle) throw new InvalidOperationException("Toggle requires a WPF ToggleButton or CheckBox.");
                bool? desired = string.IsNullOrWhiteSpace(step.Value) ? toggle.IsChecked != true : step.Value.ToLowerInvariant() switch { "true" or "on" or "checked" or "1" => true, "false" or "off" or "unchecked" or "0" => false, _ => throw new ArgumentException("Toggle value must be true/false or on/off.") };
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, desired); break;
            case StepAction.Select:
                if (ProbeAdvancedControls.PatternFor(element, PatternInterface.SelectionItem) is ISelectionItemProvider selectItem) { Guard(); selectItem.Select(); break; }
                if (element is not Selector selector) throw new InvalidOperationException("Select requires a WPF ListBox or ComboBox.");
                var items = selector.Items.Cast<object>().Where(i => ItemText(i) == step.Value).Take(2).ToList();
                if (items.Count != 1) throw new InvalidOperationException($"Expected one item named '{step.Value}'.");
                selector.SetCurrentValue(Selector.SelectedItemProperty, items[0]); break;
            default: throw new NotSupportedException($"Action '{step.Action}' is not a WPF probe operation.");
        }
        return new ProbeResponse { Success = true, Snapshot = guarded is null ? null : snapshot };
    }
    private sealed record Node(DependencyObject Element, UiElementInfo Info, string Path, nint WindowHandle);
    private static string ItemText(object item) => item is ContentControl control ? control.Content?.ToString() ?? "" : item.ToString() ?? "";
    private List<Node> ReadTree(List<Window> windows, CancellationToken ct, out bool isTruncated)
    {
        var nodes = new List<Node>();
        bool truncated = false; int visited = 0;
        var observedVisuals = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        void Visit(DependencyObject visual, int depth, string path, nint windowHandle)
        {
            ct.ThrowIfCancellationRequested();
            if (!observedVisuals.Add(visual)) return;
            if (++visited > 5000 || nodes.Count >= 2000 || depth > 30) { truncated = true; return; }
            if (visual is FrameworkElement element)
            {
                string name = AutomationProperties.GetName(element);
                if (string.IsNullOrEmpty(name)) name = element switch { TextBlock b => b.Text, ContentControl c when c.Content is string s => s, Window w => w.Title, _ => element.Name };
                bool password = element is PasswordBox;
                var containingWindow = element as Window ?? Window.GetWindow(element);
                bool windowEnabled = IsWindowEnabled(windowHandle) && (containingWindow is null || IsWindowEnabled(new WindowInteropHelper(containingWindow).Handle));
                string value = element switch { PasswordBox => "[REDACTED]", TextBox t => t.Text, TextBlock b => b.Text, ToggleButton t => t.IsChecked == true ? "On" : "Off", Selector s => s.SelectedItem is { } i ? ItemText(i) : "", _ => "" };
                bool valueTruncated = value.Length > 8192;
                var bounds = new ElementBounds();
                if (element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0)
                {
                    var top = element.PointToScreen(new Point()); var bottom = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
                    bounds = new() { X = top.X, Y = top.Y, Width = bottom.X - top.X, Height = bottom.Y - top.Y };
                }
                if (element is Window topWindow && GetWindowRect(new WindowInteropHelper(topWindow).Handle, out var outer))
                    bounds = new() { X = outer.Left, Y = outer.Top, Width = outer.Right - outer.Left, Height = outer.Bottom - outer.Top };
                nodes.Add(new(visual, new UiElementInfo { RuntimeId = RuntimeIdentity(visual), AutomationId = AutomationProperties.GetAutomationId(element), Name = name, ControlType = element switch { TextBox => "Edit", TextBlock => "Text", CheckBox => "CheckBox", Button => "Button", ListBox => "List", ComboBox => "ComboBox", Window => "Window", _ => element.GetType().Name }, ClassName = element.GetType().FullName ?? "", Value = valueTruncated ? value[..8192] : value, IsValueTruncated = valueTruncated, IsEnabled = element.IsEnabled && windowEnabled, IsOffscreen = !element.IsVisible, IsPassword = password, Depth = depth, Bounds = bounds }, path, windowHandle));
                ProbeAdvancedControls.Observe(element, nodes[^1].Info, ct);
                if (element is DataGrid grid) GridAutomation.Observe(grid, nodes[^1].Info);
                diagnostics.Observe(element, nodes[^1].Info);
            }
            int count = VisualTreeHelper.GetChildrenCount(visual);
            for (int i = 0; i < count; i++)
            {
                if (nodes.Count >= 2000 || visited >= 5000) { truncated = true; break; }
                Visit(VisualTreeHelper.GetChild(visual, i), depth + 1, path + "/" + i, windowHandle);
            }
        }
        for (int i = 0; i < windows.Count; i++) Visit(windows[i], 0, i.ToString(), new WindowInteropHelper(windows[i]).Handle);
        int rootIndex = windows.Count;
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>().ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (!source.Dispatcher.CheckAccess()) { truncated = true; continue; }
            nint handle = source.Handle; GetWindowThreadProcessId(handle, out var pid);
            if (pid != Environment.ProcessId || !IsWindowVisible(handle) || GetAncestor(handle, 2) != handle || source.RootVisual is not { } visual || observedVisuals.Contains(visual)) continue;
            if (rootIndex >= 16) { truncated = true; break; }
            Visit(visual, 0, (rootIndex++).ToString(CultureInfo.InvariantCulture), handle);
        }
        var ids = nodes.Where(n => !string.IsNullOrEmpty(n.Info.AutomationId)).GroupBy(n => n.Info.AutomationId).ToDictionary(g => g.Key, g => g.Count());
        var names = nodes.Where(n => !string.IsNullOrEmpty(n.Info.Name)).GroupBy(n => n.Info.Name).ToDictionary(g => g.Key, g => g.Count());
        foreach (var n in nodes) n.Info.Selector = !string.IsNullOrEmpty(n.Info.AutomationId) && ids[n.Info.AutomationId] == 1 ? "id:" + n.Info.AutomationId : !string.IsNullOrEmpty(n.Info.Name) && names[n.Info.Name] == 1 ? "name:" + n.Info.Name : "path:" + n.Path;
        isTruncated = truncated;
        return nodes;
    }
    public void Dispose()
    {
        stop.Cancel(); // Never wait for an in-flight dispatcher request.
        if (application.Dispatcher.CheckAccess()) diagnostics.Dispose();
        else if (!application.Dispatcher.HasShutdownStarted) application.Dispatcher.BeginInvoke(new Action(diagnostics.Dispose));
    }
    private sealed class ProbeRequest { public string Command { get; set; } = ""; public long WindowHandle { get; set; } public TestStep? Step { get; set; } public UiExecutionGuard? Guard { get; set; } }
    private sealed class ProbeResponse { public bool Success { get; set; } public string Error { get; set; } = ""; public UiSnapshot? Snapshot { get; set; } public ItemLookupResult? ItemLookup { get; set; } public FailureDiagnostic? Diagnostic { get; set; } public UiSnapshot? Observation { get; set; } }
}
