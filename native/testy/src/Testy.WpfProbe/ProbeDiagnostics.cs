using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Testy.Core;

namespace Testy.WpfProbe;

/// <summary>Bounded classifications only: never copies binding paths, error content, parameters or application data.</summary>
internal sealed class ProbeDiagnostics : IDisposable
{
    private sealed class State { internal bool? Validation; internal BindingStatus? Binding; }
    private sealed record IdentityContext(string AutomationId, string[] AncestorIds, WeakReference<object>? Item, bool Complete);
    private sealed record Entry(UiApplicationDiagnostic Diagnostic, WeakReference<DependencyObject> Source, IdentityContext Identity);
    private readonly ConditionalWeakTable<FrameworkElement, State> states = new();
    private readonly List<WeakReference<Window>> windows = [];
    private readonly Queue<Entry> entries = new();
    private readonly Func<DependencyObject, string> identity;
    private bool dropped;
    internal ProbeDiagnostics(Func<DependencyObject, string> identity) => this.identity = identity;

    internal void Hook(Window window)
    {
        if (windows.Any(w => w.TryGetTarget(out var found) && ReferenceEquals(found, window))) return;
        windows.Add(new(window));
        window.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(Click), true);
        window.AddHandler(CommandManager.ExecutedEvent, new ExecutedRoutedEventHandler(Executed), true);
        window.AddHandler(Validation.ErrorEvent, new EventHandler<ValidationErrorEventArgs>(ValidationChanged), true);
    }
    private void Click(object sender, RoutedEventArgs e) => Record(e.OriginalSource as DependencyObject, "command", "WPF ButtonBase.Click", "A Click event was observed; application outcome is not established.");
    private void Executed(object sender, ExecutedRoutedEventArgs e) => Record(e.OriginalSource as DependencyObject, "command", "WPF CommandManager.Executed", "A routed-command execution event was observed; persistence is not established.");
    private void ValidationChanged(object? sender, ValidationErrorEventArgs e) => Record(e.OriginalSource as DependencyObject, "validation", "WPF Validation.Error", e.Action == ValidationErrorEventAction.Added ? "A validation error was added; its content is withheld." : "A validation error was removed; its content is withheld.");

    internal void Operation(FrameworkElement element, StepAction action, bool completed) => Record(element, "gridOperation", "Opt-in WPF grid command", $"{action}: {(completed ? "UI operation returned successfully; persistence requires an assertion." : "UI operation failed or stopped; inspect fresh state before any further mutation.")}");

    internal void Observe(FrameworkElement element, UiElementInfo info)
    {
        if (Protected(element))
        {
            info.Properties["wpf.bindingStatus"] = new() { Status = UiPropertyStatus.Redacted, Source = "Password subtree" };
            return;
        }
        var prior = states.GetOrCreateValue(element);
        bool error = Validation.GetHasError(element);
        if (error && prior.Validation is not true || !error && prior.Validation is true)
            Record(element, "validation", "WPF Validation.GetHasError", error ? "Validation errors are present; error contents are withheld." : "Previously observed validation errors are no longer present.");
        prior.Validation = error;

        var property = element switch { TextBox => TextBox.TextProperty, TextBlock => TextBlock.TextProperty, _ => null };
        if (property is not null)
        {
            var expression = BindingOperations.GetBindingExpression(element, property);
            if (expression is not null)
            {
                var status = expression.Status;
                info.Properties["wpf.bindingStatus"] = Known(status.ToString(), "WPF BindingExpression.Status");
                if (prior.Binding != status && (status != BindingStatus.Active || prior.Binding.HasValue))
                    Record(element, "binding", "WPF BindingExpression.Status", $"Binding status: {status}. Binding paths, data and error details are withheld.");
                prior.Binding = status;
            }
        }
        if (element is ButtonBase button && button.Command is not null)
        {
            if (button.Command is RoutedCommand command)
            {
                try { info.Properties["wpf.commandCanExecute"] = Known(command.CanExecute(button.CommandParameter, button.CommandTarget ?? button), "WPF RoutedCommand.CanExecute"); }
                catch { info.Properties["wpf.commandCanExecute"] = new() { Status = UiPropertyStatus.Unavailable, Source = "WPF command availability query failed; exception content withheld" }; }
            }
            else info.Properties["wpf.commandCanExecute"] = new() { Status = UiPropertyStatus.Unavailable, Source = "Custom ICommand availability is not queried by this bridge" };
        }
    }
    private static UiPropertyObservation Known(object value, string source) => new() { Status = UiPropertyStatus.Known, Source = source, Value = JsonSerializer.SerializeToElement(value) };

    private void Record(DependencyObject? source, string kind, string origin, string message)
    {
        if (source is null) return;
        bool redacted = Protected(source);
        var observed = redacted ? new IdentityContext("", [], null, false) : ReadIdentity(source);
        if (entries.Count == 64) { entries.Dequeue(); dropped = true; }
        entries.Enqueue(new(new UiApplicationDiagnostic
        {
            ObservedAt = DateTimeOffset.UtcNow, Kind = kind, Source = origin,
            Status = redacted ? UiPropertyStatus.Redacted : UiPropertyStatus.Known,
            Message = redacted ? "An event from a password subtree was withheld." : message,
            ObservedRuntimeId = redacted ? "" : identity(source), ObservedAutomationId = observed.AutomationId,
            IdentityStatus = redacted ? UiPropertyStatus.Redacted : observed.Complete ? UiPropertyStatus.Known : UiPropertyStatus.Truncated
        }, new(source), observed));
    }
    internal (List<UiApplicationDiagnostic> Entries, bool Truncated) Read(IEnumerable<UiElementInfo> elements)
    {
        var unique = elements.GroupBy(e => e.RuntimeId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        return (entries.Select(e =>
        {
            var result = TestyJson.Clone(e.Diagnostic);
            // A visual RuntimeId survives container recycling. Never attach an old event to
            // the new business item merely because that visual is still in the current tree.
            bool matches = result.IdentityStatus == UiPropertyStatus.Known
                && unique.TryGetValue(result.ObservedRuntimeId, out var current)
                && current.AutomationId == result.ObservedAutomationId
                && e.Source.TryGetTarget(out var source)
                && SameIdentity(e.Identity, ReadIdentity(source));
            result.Selector = matches ? unique[result.ObservedRuntimeId].Selector : "";
            if (!matches && result.IdentityStatus == UiPropertyStatus.Known) result.IdentityStatus = UiPropertyStatus.Unavailable;
            return result;
        }).ToList(), dropped);
    }
    private static bool SameIdentity(IdentityContext observed, IdentityContext current)
    {
        if (!observed.Complete || !current.Complete || observed.AutomationId != current.AutomationId || !observed.AncestorIds.SequenceEqual(current.AncestorIds)) return false;
        if (observed.Item is null || current.Item is null) return observed.Item is null && current.Item is null;
        return observed.Item.TryGetTarget(out var before) && current.Item.TryGetTarget(out var now) && ReferenceEquals(before, now);
    }
    private static IdentityContext ReadIdentity(DependencyObject source)
    {
        bool complete = true;
        string BoundId(DependencyObject element)
        {
            string id = System.Windows.Automation.AutomationProperties.GetAutomationId(element);
            if (id.Length <= 256) return id;
            complete = false; return id[..256];
        }
        string automationId = BoundId(source);
        var ancestors = new List<string>(); WeakReference<object>? item = null;
        DependencyObject? current = source;
        for (int depth = 0; current is not null && depth < 40; depth++)
        {
            // Public WPF item properties are used only for reference continuity. Their values
            // are never stringified or serialized, and weak references do not retain old rows.
            object? candidate = current switch { DataGridRow row => row.Item, ListBoxItem row => row.Content, TreeViewItem row => row.Header, _ => null };
            if (item is null && candidate is not null) item = new(candidate);
            if (!ReferenceEquals(current, source))
            {
                string id = BoundId(current);
                if (id.Length > 0) { if (ancestors.Count == 8) { complete = false; break; } ancestors.Add(id); }
            }
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        if (current is not null) complete = false;
        return new(automationId, ancestors.ToArray(), item, complete);
    }
    private static bool Protected(DependencyObject source)
    {
        DependencyObject? current = source;
        for (int depth = 0; current is not null && depth < 40; depth++)
        {
            if (current is PasswordBox) return true;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
    public void Dispose()
    {
        foreach (var reference in windows)
        {
            if (!reference.TryGetTarget(out var window)) continue;
            window.RemoveHandler(ButtonBase.ClickEvent, new RoutedEventHandler(Click));
            window.RemoveHandler(CommandManager.ExecutedEvent, new ExecutedRoutedEventHandler(Executed));
            window.RemoveHandler(Validation.ErrorEvent, new EventHandler<ValidationErrorEventArgs>(ValidationChanged));
        }
        windows.Clear(); entries.Clear();
    }
}
