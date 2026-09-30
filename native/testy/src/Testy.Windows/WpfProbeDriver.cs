using System.IO.Pipes;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Testy.Core;
using Microsoft.Win32.SafeHandles;

namespace Testy.Windows;

/// <summary>Connects only to an explicitly started Testy.WpfProbe within the selected process.</summary>
public sealed class WpfProbeDriver : ITargetDriver, IItemLookupDriver, IGuardedTargetDriver, IScreenshotEvidenceSource
{
    private readonly UiAutomationDriver windows = new();
    public TargetInfo? Target => windows.Target;
    public ScreenshotEvidence? LastScreenshot => windows.LastScreenshot;
    public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default) => windows.GetTargetsAsync(cancellationToken);
    public async Task AttachAsync(int processId, CancellationToken cancellationToken = default)
    {
        await windows.AttachAsync(processId, cancellationToken);
        await SnapshotAsync(cancellationToken); // Fail early if the target did not explicitly enable the bridge.
    }
    public async Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => (await RequestAsync("snapshot", null, cancellationToken)).Snapshot ?? throw new InvalidOperationException("The probe did not return a snapshot.");
    public async Task<UiSnapshot> SnapshotGuardedAsync(UiExecutionGuard guard, CancellationToken cancellationToken = default) =>
        (await RequestAsync("snapshotGuarded", null, cancellationToken, guard)).Snapshot ?? throw new InvalidOperationException("The probe did not return guarded snapshot evidence.");
    public async Task<UiSnapshot> ExecuteGuardedAsync(TestStep resolvedStep, UiExecutionGuard guard, CancellationToken cancellationToken = default)
    {
        // Visual identities cannot be reused by the separate UIA keyboard-focus path.
        if (resolvedStep.Action is StepAction.KeyPress or StepAction.CoordinateClick)
            throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.CapabilityUnavailable, "Probe selector recovery cannot use the separate UIA physical-input path.", resolvedStep));
        return (await RequestAsync("executeGuarded", resolvedStep, cancellationToken, guard)).Snapshot ?? throw new InvalidOperationException("The probe did not return the guarded pre-input snapshot.");
    }
    public async Task<ItemLookupResult> LookupItemAsync(string containerSelector, string itemValue, CancellationToken cancellationToken = default)
    {
        UiSelectors.Validate(containerSelector); AdvancedSteps.ParseItem(itemValue);
        var response = await RequestAsync("lookupItem", new TestStep { Selector = containerSelector, Value = itemValue }, cancellationToken);
        return response.ItemLookup ?? new ItemLookupResult { Status = ItemLookupStatus.Unavailable, Message = "The probe did not return item lookup evidence." };
    }
    public async Task ExecuteAsync(TestStep step, CancellationToken cancellationToken = default)
    {
        if (step.Action == StepAction.KeyPress && (step.Selector.StartsWith("path:", StringComparison.Ordinal) || step.Selector.StartsWith("query:", StringComparison.Ordinal))) throw new InvalidOperationException("Use a unique id: or name: selector for KeyPress in probe mode; visual-tree paths and scoped queries cannot be reused for UIA focus because the trees can differ.");
        if (step.Action is StepAction.CoordinateClick or StepAction.KeyPress) { await windows.ExecuteAsync(step, cancellationToken); return; }
        await RequestAsync("execute", step, cancellationToken);
    }
    public Task<string> CaptureAsync(string filePath, CancellationToken cancellationToken = default) => windows.CaptureAsync(filePath, cancellationToken);
    private static JsonSerializerOptions WireOptions { get; } = new(TestyJson.Options) { WriteIndented = false };
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    private async Task<ProbeResponse> RequestAsync(string command, TestStep? step, CancellationToken token, UiExecutionGuard? guard = null)
    {
        var target = Target ?? throw new InvalidOperationException("Attach to an application with the WPF probe enabled first."); Native.Validate(target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await using var pipe = new NamedPipeClientStream(".", $"testy-wpf-{target.ProcessId}", PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("WPF probe unavailable. Add `ProbeServer.Start()` to the target's startup, or use Windows UI Automation mode."); }
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != target.ProcessId) throw new InvalidOperationException("The named-pipe server is not the attached process. No request was sent.");
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { Command = command, WindowHandle = target.WindowHandle, Step = step, Guard = guard }, WireOptions).AsMemory(), timeout.Token);
        var responseText = new StringBuilder(); char[] buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), timeout.Token) != 0)
        {
            if (buffer[0] == '\n') break;
            if (responseText.Length >= 16 * 1024 * 1024) throw new IOException("WPF probe response exceeds the 16 MB limit.");
            responseText.Append(buffer[0]);
        }
        var response = JsonSerializer.Deserialize<ProbeResponse>(responseText.ToString(), TestyJson.Options) ?? throw new IOException("Empty probe response.");
        // Capture is performed by this out-of-process driver, so its physical union bounds
        // (including titleless HWNDs) are authoritative over the probe's Window-only bounds.
        if (response.Snapshot is not null) response.Snapshot.ScreenshotBounds = Native.ScreenshotBounds(target);
        if (response.Diagnostic is not null) throw new StepDiagnosticException(response.Diagnostic, response.Observation);
        if (!string.IsNullOrEmpty(response.Error)) throw new InvalidOperationException(response.Error);
        return response;
    }
    public void Dispose() => windows.Dispose();
    private sealed class ProbeResponse { public bool Success { get; set; } public string Error { get; set; } = ""; public UiSnapshot? Snapshot { get; set; } public ItemLookupResult? ItemLookup { get; set; } public FailureDiagnostic? Diagnostic { get; set; } public UiSnapshot? Observation { get; set; } }
}
