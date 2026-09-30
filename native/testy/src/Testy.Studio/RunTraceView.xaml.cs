using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using Testy.Core;

namespace Testy.Studio;

/// <summary>
/// "Run trace" timeline: one block per step at its real start time and duration, hatched time between steps,
/// a time axis, a playhead at the selected step and screenshot thumbnails linked to their steps.
/// Set <see cref="Run"/> (or a prebuilt <see cref="Trace"/>) and bind <see cref="SelectedStepIndex"/>.
/// </summary>
public partial class RunTraceView : UserControl
{
    public static readonly DependencyProperty TraceProperty = DependencyProperty.Register(
        nameof(Trace), typeof(RunTrace), typeof(RunTraceView), new FrameworkPropertyMetadata(null, (d, _) => ((RunTraceView)d).OnTraceChanged()));

    public static readonly DependencyProperty RunProperty = DependencyProperty.Register(
        nameof(Run), typeof(RunResult), typeof(RunTraceView), new FrameworkPropertyMetadata(null, (d, e) => ((RunTraceView)d).OnRunChanged((RunResult?)e.NewValue)));

    public static readonly DependencyProperty SelectedStepIndexProperty = DependencyProperty.Register(
        nameof(SelectedStepIndex), typeof(int), typeof(RunTraceView),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((RunTraceView)d).OnSelectedStepIndexChanged((int)e.OldValue, (int)e.NewValue),
            (d, v) => ((RunTraceView)d).CoerceSelectedStepIndex((int)v)));

    private static readonly DependencyPropertyKey SelectedStepPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(SelectedStep), typeof(RunTraceStep), typeof(RunTraceView), new PropertyMetadata(null));
    public static readonly DependencyProperty SelectedStepProperty = SelectedStepPropertyKey.DependencyProperty;

    public static readonly DependencyProperty GapLabelProperty = DependencyProperty.Register(
        nameof(GapLabel), typeof(string), typeof(RunTraceView), new FrameworkPropertyMetadata(null, (d, _) => ((RunTraceView)d).ApplyHeader()));

    public static readonly DependencyProperty MonoFontFamilyProperty = DependencyProperty.Register(
        nameof(MonoFontFamily), typeof(FontFamily), typeof(RunTraceView),
        new FrameworkPropertyMetadata(new FontFamily("Cascadia Mono, Consolas"), (d, _) => ((RunTraceView)d).OnMonoFontChanged()));

    public static readonly RoutedEvent StepSelectedEvent = EventManager.RegisterRoutedEvent(
        nameof(StepSelected), RoutingStrategy.Bubble, typeof(EventHandler<RunTraceStepSelectedEventArgs>), typeof(RunTraceView));

    private bool userSelecting;
    private string automationName = "";

    public RunTraceView()
    {
        InitializeComponent();
        Surface.Attach(this);
        OnMonoFontChanged();
        OnTraceChanged();
    }

    /// <summary>The trace to show. Set this or <see cref="Run"/>, not both. Null shows an empty trace.</summary>
    public RunTrace? Trace { get => (RunTrace?)GetValue(TraceProperty); set => SetValue(TraceProperty, value); }

    /// <summary>Convenience input: builds <see cref="Trace"/> with <see cref="RunTrace.From"/>
    /// (a run still in progress is measured up to now). Re-set it on each progress report of a live run.</summary>
    public RunResult? Run { get => (RunResult?)GetValue(RunProperty); set => SetValue(RunProperty, value); }

    /// <summary>Index into <c>RunResult.Steps</c> of the selected step, or -1. Two-way by default.</summary>
    public int SelectedStepIndex { get => (int)GetValue(SelectedStepIndexProperty); set => SetValue(SelectedStepIndexProperty, value); }

    public RunTraceStep? SelectedStep => (RunTraceStep?)GetValue(SelectedStepProperty);

    /// <summary>Legend text for the hatched time. Null uses "Assistant finding the next control" for AI-guided runs, else "Between steps".</summary>
    public string? GapLabel { get => (string?)GetValue(GapLabelProperty); set => SetValue(GapLabelProperty, value); }

    /// <summary>Font for values (times, tick labels). Defaults to "Cascadia Mono, Consolas".</summary>
    public FontFamily MonoFontFamily { get => (FontFamily)GetValue(MonoFontFamilyProperty); set => SetValue(MonoFontFamilyProperty, value); }

    /// <summary>Raised when the selected step changes to a step, and when the user re-selects the selected step.</summary>
    public event EventHandler<RunTraceStepSelectedEventArgs> StepSelected
    {
        add => AddHandler(StepSelectedEvent, value);
        remove => RemoveHandler(StepSelectedEvent, value);
    }

    internal RunTrace EffectiveTrace => Trace ?? RunTrace.Empty;
    internal string EffectiveGapLabel => string.IsNullOrWhiteSpace(GapLabel) ? EffectiveTrace.DefaultGapLabel : GapLabel!.Trim();
    internal string AutomationSummary => automationName;

    /// <summary>Waits until the thumbnails currently laid out have loaded (or failed). For tests and snapshots.</summary>
    internal Task WhenThumbnailsLoadedAsync() => Surface.WhenThumbnailsLoadedAsync();

    /// <summary>Draws the keyboard focus cue on a step without real keyboard focus (headless snapshots only).</summary>
    internal void ShowFocusCueForSnapshot(int position) => Surface.SetFocusCue(position);

    internal void SelectFromUser(int position)
    {
        if (position < 0 || position >= EffectiveTrace.Steps.Count) return;
        userSelecting = true;
        try
        {
            if (position == SelectedStepIndex) RaiseStepSelected(position, true);
            else SetCurrentValue(SelectedStepIndexProperty, position);
        }
        finally { userSelecting = false; }
    }

    internal void ClearSelectionFromUser()
    {
        userSelecting = true;
        try { SetCurrentValue(SelectedStepIndexProperty, -1); }
        finally { userSelecting = false; }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RunTraceViewAutomationPeer(this);

    private void OnRunChanged(RunResult? run) =>
        SetCurrentValue(TraceProperty, run is null ? null : RunTrace.From(run, run.FinishedAt is null ? DateTimeOffset.UtcNow : null));

    private void OnTraceChanged()
    {
        var trace = EffectiveTrace;
        Surface.SetTrace(trace);
        CoerceValue(SelectedStepIndexProperty);
        Surface.SetSelected(SelectedStepIndex);
        SetValue(SelectedStepPropertyKey, StepAt(SelectedStepIndex));
        ApplyHeader();
        if (UIElementAutomationPeer.FromElement(this) is RunTraceViewAutomationPeer peer) peer.ResetChildrenCache();
    }

    private void ApplyHeader()
    {
        var trace = EffectiveTrace;
        UnitRun.Text = trace.HasRun ? trace.Axis.UnitName : "";
        StepsValue.Text = trace.StepTimeText;
        GapLabelRun.Text = EffectiveGapLabel;
        GapValue.Text = trace.BetweenTimeText;
        TotalValue.Text = trace.TotalTimeText;
        Legend.Visibility = trace.HasRun ? Visibility.Visible : Visibility.Collapsed;

        var old = automationName;
        automationName = trace.Describe(EffectiveGapLabel);
        if (old != automationName && UIElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, automationName);
    }

    private void OnMonoFontChanged()
    {
        foreach (var run in new[] { StepsValue, GapValue, TotalValue }) run.FontFamily = MonoFontFamily;
        Surface.InvalidateMeasure();
        Surface.InvalidateVisual();
    }

    private int CoerceSelectedStepIndex(int value) => value >= 0 && value < EffectiveTrace.Steps.Count ? value : -1;

    private RunTraceStep? StepAt(int index) => index >= 0 && index < EffectiveTrace.Steps.Count ? EffectiveTrace.Steps[index] : null;

    private void OnSelectedStepIndexChanged(int oldIndex, int newIndex)
    {
        SetValue(SelectedStepPropertyKey, StepAt(newIndex));
        Surface.SetSelected(newIndex);
        RaiseSelectionAutomationEvents(oldIndex, newIndex);
        if (newIndex >= 0) RaiseStepSelected(newIndex, userSelecting);
    }

    private void RaiseStepSelected(int position, bool user)
    {
        var step = StepAt(position);
        if (step is not null) RaiseEvent(new RunTraceStepSelectedEventArgs(StepSelectedEvent, this, step, user));
    }

    private void RaiseSelectionAutomationEvents(int oldIndex, int newIndex)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged) && !AutomationPeer.ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected))
            return;
        if (PeerAt(oldIndex) is { } oldPeer)
            oldPeer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, true, false);
        if (PeerAt(newIndex) is { } newPeer)
        {
            newPeer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, false, true);
            newPeer.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
        }
    }

    private AutomationPeer? PeerAt(int index) =>
        Surface.ElementAt(index) is { } element ? UIElementAutomationPeer.CreatePeerForElement(element) : null;
}

/// <summary>Arguments of <see cref="RunTraceView.StepSelected"/>.</summary>
public sealed class RunTraceStepSelectedEventArgs : RoutedEventArgs
{
    public RunTraceStepSelectedEventArgs(RoutedEvent routedEvent, object source, RunTraceStep step, bool isUserInitiated)
        : base(routedEvent, source)
    {
        Step = step;
        IsUserInitiated = isUserInitiated;
    }

    public RunTraceStep Step { get; }
    /// <summary>Index into <c>RunResult.Steps</c>.</summary>
    public int StepIndex => Step.Position;
    /// <summary>True for a click, a key press or a UI Automation Select; false when the host set <see cref="RunTraceView.SelectedStepIndex"/>.</summary>
    public bool IsUserInitiated { get; }

    /// <summary>Where the step's control sits in its screenshot, if determinable (see <see cref="StepTargetLocator.Locate"/>).</summary>
    public StepTargetRegion? LocateTarget() => StepTargetLocator.Locate(Step.Source);

    protected override void InvokeEventHandler(Delegate genericHandler, object genericTarget) =>
        ((EventHandler<RunTraceStepSelectedEventArgs>)genericHandler)(genericTarget, this);
}

/// <summary>The trace is exposed as a List of ListItems (one per step), with the Selection pattern.</summary>
internal sealed class RunTraceViewAutomationPeer(RunTraceView owner) : UserControlAutomationPeer(owner), ISelectionProvider
{
    private RunTraceView View => (RunTraceView)Owner;

    protected override string GetClassNameCore() => nameof(RunTraceView);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override string GetNameCore()
    {
        var name = base.GetNameCore();
        return string.IsNullOrEmpty(name) ? View.AutomationSummary : name;
    }

    // Only the steps: the header's text is summarised in the Name, and ticks/labels are drawn, not elements.
    protected override List<AutomationPeer> GetChildrenCore()
    {
        var peers = new List<AutomationPeer>();
        foreach (var element in View.Surface.StepElements)
            if (UIElementAutomationPeer.CreatePeerForElement(element) is { } peer) peers.Add(peer);
        return peers;
    }

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);

    IRawElementProviderSimple[] ISelectionProvider.GetSelection()
    {
        var element = View.Surface.ElementAt(View.SelectedStepIndex);
        var peer = element is null ? null : UIElementAutomationPeer.CreatePeerForElement(element);
        return peer is null ? [] : [ProviderFromPeer(peer)];
    }

    bool ISelectionProvider.CanSelectMultiple => false;
    bool ISelectionProvider.IsSelectionRequired => false;
}

internal sealed class RunTraceStepAutomationPeer(RunTraceStepElement owner) : FrameworkElementAutomationPeer(owner), ISelectionItemProvider
{
    private RunTraceStepElement Element => (RunTraceStepElement)Owner;

    protected override string GetClassNameCore() => "RunTraceStep";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetNameCore()
    {
        var name = base.GetNameCore();
        return string.IsNullOrEmpty(name) ? Element.Step.AutomationName : name;
    }

    protected override string GetHelpTextCore()
    {
        var help = base.GetHelpTextCore();
        return string.IsNullOrEmpty(help) ? Element.HelpText : help;
    }

    protected override string GetItemStatusCore() => Element.Step.StateText;
    protected override int GetPositionInSetCore() => Element.Step.Number;
    protected override int GetSizeOfSetCore() => Element.Surface.StepElements.Count;

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.SelectionItem ? this : base.GetPattern(patternInterface);

    bool ISelectionItemProvider.IsSelected => Element.IsSelected;

    IRawElementProviderSimple? ISelectionItemProvider.SelectionContainer =>
        Element.Surface.View is { } view && UIElementAutomationPeer.CreatePeerForElement(view) is { } peer ? ProviderFromPeer(peer) : null;

    void ISelectionItemProvider.Select()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        Element.Surface.View?.SelectFromUser(Element.Step.Position);
    }

    void ISelectionItemProvider.AddToSelection()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        var view = Element.Surface.View;
        if (view is null) return;
        if (view.SelectedStepIndex >= 0 && view.SelectedStepIndex != Element.Step.Position)
            throw new InvalidOperationException("The run trace selects one step at a time.");
        view.SelectFromUser(Element.Step.Position);
    }

    void ISelectionItemProvider.RemoveFromSelection()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        if (Element.IsSelected) Element.Surface.View?.ClearSelectionFromUser();
    }
}

/// <summary>Legend swatch: a solid step block or a hatched gap, in the same theme colours as the trace.</summary>
internal sealed class RunTraceLegendSwatch : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(RunTraceSwatchKind), typeof(RunTraceLegendSwatch),
        new FrameworkPropertyMetadata(RunTraceSwatchKind.Step, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        "SwatchFill", typeof(Brush), typeof(RunTraceLegendSwatch),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty HatchProperty = DependencyProperty.Register(
        "SwatchHatch", typeof(Brush), typeof(RunTraceLegendSwatch),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public RunTraceLegendSwatch()
    {
        SetResourceReference(FillProperty, RunTraceTheme.NeutralFill);
        SetResourceReference(HatchProperty, RunTraceTheme.HatchStroke);
        SnapsToDevicePixels = true;
    }

    public RunTraceSwatchKind Kind { get => (RunTraceSwatchKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(14, 10);

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, 14, 10);
        if (Kind == RunTraceSwatchKind.Step)
        {
            dc.DrawRoundedRectangle((Brush)GetValue(FillProperty), null, rect, 2, 2);
            return;
        }
        var hatch = ((Brush)GetValue(HatchProperty)).CloneCurrentValue();
        var pen = new Pen(hatch, 1);
        if (pen.CanFreeze) pen.Freeze();
        var clip = new RectangleGeometry(rect, 2, 2);
        dc.PushClip(clip);
        dc.DrawGeometry(null, pen, RunTraceSurface.HatchLines(rect, 4));
        dc.Pop();
        dc.DrawRoundedRectangle(null, pen, new Rect(0.5, 0.5, 13, 9), 2, 2);
    }
}

internal enum RunTraceSwatchKind { Step, Gap }
