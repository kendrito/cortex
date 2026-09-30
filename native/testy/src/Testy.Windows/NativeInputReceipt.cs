using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using Testy.Core;

namespace Testy.Windows;

/// <summary>One bounded UIA synchronized-input subscription. No unsupported provider is guessed to have delivered input.</summary>
internal sealed class NativeInputReceipt : IAsyncDisposable
{
    private readonly TaskCompletionSource<bool> delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AutomationElement? element;
    private readonly SynchronizedInputPattern? pattern;
    private readonly AutomationEventHandler handler;
    private readonly List<AutomationEvent> subscriptions = [];
    public NativeActionReceipt Receipt { get; }
    private NativeInputReceipt(TargetInfo target, AutomationElement? element, List<string> ids, SynchronizedInputType? kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        this.element = element;
        Receipt = new NativeActionReceipt { ProcessId = target.ProcessId, CapturedAt = DateTimeOffset.UtcNow, HitRuntimeIds = ids };
        handler = (_, e) => delivered.TrySetResult(e.EventId == SynchronizedInputPattern.InputReachedTargetEvent);
        if (element is null || kind is null || !element.TryGetCurrentPattern(SynchronizedInputPattern.Pattern, out var candidate)) return;
        pattern = (SynchronizedInputPattern)candidate;
        try
        {
            foreach (var id in new[] { SynchronizedInputPattern.InputReachedTargetEvent, SynchronizedInputPattern.InputReachedOtherElementEvent, SynchronizedInputPattern.InputDiscardedEvent })
            {
                ct.ThrowIfCancellationRequested();
                Automation.AddAutomationEventHandler(id, element, TreeScope.Element, handler); subscriptions.Add(id);
            }
            ct.ThrowIfCancellationRequested();
            pattern.StartListening(kind.Value);
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            Cleanup(); throw;
        }
    }
    public static Task<NativeInputReceipt> ForMouseAsync(TargetInfo target, nint window, int x, int y, string button, CancellationToken ct) => PrepareAsync(token =>
    {
        using var dpi = new Native.DpiScope(); Native.Validate(target);
        var point = Native.InputPoint(target, window, x, y);
        var hit = AutomationElement.FromPoint(new Point(point.X, point.Y));
        var ids = new List<string>(); AutomationElement? actionable = null; var watch = Stopwatch.StartNew();
        for (int depth = 0; hit != null && depth < 32; depth++)
        {
            token.ThrowIfCancellationRequested();
            if (watch.ElapsedMilliseconds > 2000) throw new TimeoutException("UIA hit ancestry exceeded its deadline.");
            var info = hit.Current;
            if (info.ProcessId != target.ProcessId) break;
            ids.Add(string.Join('.', hit.GetRuntimeId()));
            // Passive text/template descendants are retained; an independently actionable child
            // is a hard boundary and can never credit its clickable parent.
            if (IsActionable(hit, info)) { actionable = hit; break; }
            hit = TreeWalker.RawViewWalker.GetParent(hit);
        }
        SynchronizedInputType? kind = button switch { "left" => SynchronizedInputType.MouseLeftButtonDown, "right" => SynchronizedInputType.MouseRightButtonDown, _ => null };
        return new NativeInputReceipt(target, actionable, ids, kind, token);
    }, ct);
    public static Task<NativeInputReceipt> ForKeyboardAsync(TargetInfo target, CancellationToken ct) => PrepareAsync(token =>
    {
        Native.Validate(target); var focused = AutomationElement.FocusedElement;
        if (focused == null || focused.Current.ProcessId != target.ProcessId) throw new InvalidOperationException("The focused control does not belong to the attached process.");
        if (focused.Current.IsPassword) throw new InvalidOperationException("Native keyboard input into password controls is not supported.");
        return new NativeInputReceipt(target, focused, [string.Join('.', focused.GetRuntimeId())], SynchronizedInputType.KeyDown, token);
    }, ct);
    private static async Task<NativeInputReceipt> PrepareAsync(Func<CancellationToken, NativeInputReceipt> prepare, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var preparationToken = deadline.Token;
        return await Task.Run(() =>
        {
            NativeInputReceipt? lease = null;
            try { preparationToken.ThrowIfCancellationRequested(); lease = prepare(preparationToken); preparationToken.ThrowIfCancellationRequested(); return lease; }
            catch { lease?.Cleanup(); throw; }
        }, preparationToken).WaitAsync(preparationToken).ConfigureAwait(false);
    }
    private static bool IsActionable(AutomationElement element, AutomationElement.AutomationElementInformation info)
    {
        if (info.IsKeyboardFocusable) return true;
        foreach (var supported in new[] { InvokePattern.Pattern, TogglePattern.Pattern, SelectionItemPattern.Pattern, ValuePattern.Pattern, RangeValuePattern.Pattern, ExpandCollapsePattern.Pattern })
            if (element.TryGetCurrentPattern(supported, out _)) return true;
        return false;
    }
    public async Task ConfirmAsync(CancellationToken ct)
    {
        if (pattern is null) return;
        try { Receipt.InputDelivered = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
        catch (TimeoutException) { Receipt.InputDelivered = false; }
    }
    public async Task VerifyFocusedAsync(TargetInfo target, CancellationToken ct)
    {
        await Task.Run(() =>
        {
        var focused = AutomationElement.FocusedElement;
        if (focused == null || focused.Current.ProcessId != target.ProcessId || Receipt.HitRuntimeIds.Count != 1 || string.Join('.', focused.GetRuntimeId()) != Receipt.HitRuntimeIds[0])
            throw new InvalidOperationException("Keyboard focus changed after the input receipt was prepared. No input was sent.");
        }, ct).WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }
    private void Cleanup()
    {
        if (element is null) return;
        try { pattern?.Cancel(); } catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        foreach (var id in subscriptions)
            try { Automation.RemoveAutomationEventHandler(id, element, handler); } catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        subscriptions.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        try { await Task.Run(Cleanup).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { /* Provider cleanup may finish later; no further input is dispatched. */ }
    }
}
