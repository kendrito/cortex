using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Testy.Studio;

/// <summary>Fluent theme resource keys the run trace depends on (all verified in .NET 9 PresentationFramework.Fluent, light and dark).</summary>
internal static class RunTraceTheme
{
    public const string NeutralFill = "TextFillColorSecondaryBrush";        // neutral step blocks, "Steps" swatch (softer than primary text in dark mode)
    public const string AccentFill = "AccentFillColorDefaultBrush";         // selected block, playhead, selected thumbnail + badge
    public const string OnAccentText = "TextOnAccentFillColorPrimaryBrush"; // numbers on accent / critical / caution badges
    public const string CriticalFill = "SystemFillColorCriticalBrush";      // failed step
    public const string CautionFill = "SystemFillColorCautionBrush";        // cancelled step
    public const string HatchStroke = "SurfaceStrokeColorDefaultBrush";     // hatched gaps, gap swatch, thumbnail leaders
    public const string TrackFill = "SubtleFillColorSecondaryBrush";        // track, thumbnail placeholders, running step
    public const string TrackStroke = "ControlStrokeColorDefaultBrush";     // track outline
    public const string Gridline = "DividerStrokeColorDefaultBrush";        // gridlines at axis ticks
    public const string TextPrimary = "TextFillColorPrimaryBrush";          // selected thumbnail time, legend values
    public const string TextSecondary = "TextFillColorSecondaryBrush";      // tick labels, gap labels, times, legend labels, "Not run"
    public const string TextTertiary = "TextFillColorTertiaryBrush";        // tick marks, not-run dashes, missing-screenshot glyph
    public const string FrameStroke = "ControlStrokeColorSecondaryBrush";   // thumbnail frames, step-number badges
    public const string FocusStroke = "KeyboardFocusBorderColorBrush";      // keyboard focus ring (as Fluent's DefaultControlFocusVisualStyle)
    public const string SymbolFont = "SymbolThemeFontFamily";               // Segoe Fluent Icons (FontFamily resource)

    public static IReadOnlyList<string> BrushKeys { get; } = new[]
    {
        NeutralFill, AccentFill, OnAccentText, CriticalFill, CautionFill, HatchStroke, TrackFill, TrackStroke, Gridline,
        TextPrimary, TextSecondary, TextTertiary, FrameStroke, FocusStroke,
    }.Distinct().ToArray();
}

/// <summary>
/// Draws the whole trace (axis, track, hatching, blocks, playhead, leaders, thumbnails, labels) in one OnRender,
/// and hosts one <see cref="RunTraceStepElement"/> per step for focus, hit testing and UI Automation.
/// </summary>
internal sealed class RunTraceSurface : Panel
{
    // Vertical metrics, in device-independent pixels.
    internal const double LabelBaseline = 11, TickLength = 4, TrackTop = 20, TrackHeight = 36, BlockInset = 5;
    internal const double ConnectorHeight = 14, ThumbWidth = 60, ThumbHeight = 44, LabelGap = 6, LabelHeight = 16;
    internal const double TrackBottom = TrackTop + TrackHeight;
    internal const double ThumbTop = TrackBottom + ConnectorHeight;
    internal const double LabelTop = ThumbTop + ThumbHeight + LabelGap;
    internal const double FullHeight = LabelTop + LabelHeight + 2;
    internal const double CompactHeight = TrackBottom + 2;
    private const double MinBlockWidth = 3, SlotGap = 12, ChipWidth = 20, ChipGap = 4, ZoneGap = 14;
    private const double BlockRadius = 2, FrameRadius = 4, TrackRadius = 4;
    private const double TickFont = 11, ValueFont = 11, NumberFont = 12, CaptionFont = 12;
    private const string MissingGlyph = "\uE91B"; // Segoe Fluent Icons "Photo"

    private enum Ink { Neutral, Accent, OnAccent, Critical, Caution, Hatch, TrackFill, TrackStroke, Gridline, Text1, Text2, Text3, Frame, Focus }

    private static readonly (string Key, Color Fallback)[] InkKeys =
    [
        (RunTraceTheme.NeutralFill, Color.FromArgb(0xE4, 0, 0, 0)),
        (RunTraceTheme.AccentFill, Color.FromRgb(0x00, 0x5F, 0xB8)),
        (RunTraceTheme.OnAccentText, Colors.White),
        (RunTraceTheme.CriticalFill, Color.FromRgb(0xC4, 0x2B, 0x1C)),
        (RunTraceTheme.CautionFill, Color.FromRgb(0x9D, 0x5D, 0x00)),
        (RunTraceTheme.HatchStroke, Color.FromArgb(0x66, 0x75, 0x75, 0x75)),
        (RunTraceTheme.TrackFill, Color.FromArgb(0x09, 0, 0, 0)),
        (RunTraceTheme.TrackStroke, Color.FromArgb(0x0F, 0, 0, 0)),
        (RunTraceTheme.Gridline, Color.FromArgb(0x0F, 0, 0, 0)),
        (RunTraceTheme.TextPrimary, Color.FromArgb(0xE4, 0, 0, 0)),
        (RunTraceTheme.TextSecondary, Color.FromArgb(0x9E, 0, 0, 0)),
        (RunTraceTheme.TextTertiary, Color.FromArgb(0x72, 0, 0, 0)),
        (RunTraceTheme.FrameStroke, Color.FromArgb(0x29, 0, 0, 0)),
        (RunTraceTheme.FocusStroke, Color.FromArgb(0xBE, 0, 0, 0)),
    ];

    private static readonly DependencyProperty[] InkProperties = InkKeys.Select((k, i) => DependencyProperty.Register(
        "Ink" + (Ink)i, typeof(Brush), typeof(RunTraceSurface),
        new FrameworkPropertyMetadata(Frozen(new SolidColorBrush(k.Fallback)), FrameworkPropertyMetadataOptions.AffectsRender))).ToArray();

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(RunTraceSurface),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SymbolFontProperty = DependencyProperty.Register(
        "SymbolFont", typeof(FontFamily), typeof(RunTraceSurface),
        new FrameworkPropertyMetadata(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly FontFamily DefaultMono = new("Cascadia Mono, Consolas");

    private readonly Brush?[] inkCache = new Brush?[InkKeys.Length];
    private readonly List<RunTraceStepElement> elements = [];
    private readonly Dictionary<string, ThumbState> thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> pendingLoads = [];
    private RunTraceView? view;
    private RunTrace trace = RunTrace.Empty;
    private Layout? layout;
    private int selected = -1, hover = -1, focusCue = -1;
    private double ppd = 1;

    public RunTraceSurface()
    {
        for (var i = 0; i < InkKeys.Length; i++) SetResourceReference(InkProperties[i], InkKeys[i].Key);
        SetResourceReference(SymbolFontProperty, RunTraceTheme.SymbolFont);
        Background = Brushes.Transparent; // hit-testable, so thumbnails can be clicked
        FlowDirection = FlowDirection.LeftToRight; // time runs left to right
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Auto);
    }

    public FontFamily FontFamily => (FontFamily)GetValue(FontFamilyProperty);
    internal RunTraceView? View => view;
    internal IReadOnlyList<RunTraceStepElement> StepElements => elements;
    internal int SelectedPosition => selected;

    internal void Attach(RunTraceView owner) => view = owner;

    internal RunTraceStepElement? ElementAt(int position) => position >= 0 && position < elements.Count ? elements[position] : null;

    internal void SetTrace(RunTrace value)
    {
        trace = value;
        hover = -1;
        // Keep decoded thumbnails that are still used (no placeholder flash on live updates), but revalidate them.
        var paths = value.Steps.Select(s => s.ScreenshotPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in thumbs.Keys.Where(k => !paths.Contains(k)).ToList()) thumbs.Remove(stale);
        foreach (var state in thumbs.Values) state.Fresh = false;
        // Reuse elements by position so keyboard focus survives live updates of a running run.
        for (var i = 0; i < value.Steps.Count; i++)
        {
            if (i < elements.Count) elements[i].Update(value.Steps[i]);
            else
            {
                var element = new RunTraceStepElement(this, value.Steps[i]);
                elements.Add(element);
                Children.Add(element);
            }
        }
        while (elements.Count > value.Steps.Count)
        {
            var last = elements[^1];
            elements.RemoveAt(elements.Count - 1);
            Children.Remove(last);
        }
        if (focusCue >= elements.Count) focusCue = -1;
        UpdateTabStop();
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    internal void SetSelected(int position)
    {
        if (selected == position) return;
        var old = ElementAt(selected);
        selected = position;
        old?.InvalidateVisual();
        ElementAt(position)?.InvalidateVisual();
        UpdateTabStop();
        InvalidateArrange(); // the thumbnail subset always includes the selected step
        InvalidateVisual();
    }

    /// <summary>Roving tab stop: Tab lands on the selected step (or the first), arrows move from there.</summary>
    private void UpdateTabStop()
    {
        var active = selected >= 0 && selected < elements.Count ? selected : 0;
        for (var i = 0; i < elements.Count; i++) KeyboardNavigation.SetIsTabStop(elements[i], i == active);
    }

    internal void SetFocusCue(int position)
    {
        if (focusCue == position) return;
        var old = ElementAt(focusCue);
        focusCue = position;
        old?.InvalidateVisual();
        ElementAt(position)?.InvalidateVisual();
    }

    internal bool ShowsFocusCue(RunTraceStepElement element) => focusCue >= 0 && focusCue == element.Step.Position;

    internal Task WhenThumbnailsLoadedAsync()
    {
        UpdateLayout();
        return Task.WhenAll(pendingLoads.ToArray());
    }

    internal void SelectFromUser(int position, bool focus)
    {
        view?.SelectFromUser(position);
        if (!focus || ElementAt(position) is not { } element) return;
        var viaKeyboard = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        if (!element.IsKeyboardFocused) element.Focus();
        SetFocusCue(viaKeyboard ? position : -1);
    }

    internal void OnStepGotFocus(RunTraceStepElement element, bool viaKeyboard) => SetFocusCue(viaKeyboard ? element.Step.Position : -1);

    internal void OnStepLostFocus(RunTraceStepElement element)
    {
        if (focusCue == element.Step.Position) SetFocusCue(-1);
    }


    // ------------------------------------------------------------------ layout

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var element in elements) element.Measure(new Size(0, 0));
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        return new Size(width, trace.Steps.Count > 0 ? FullHeight : CompactHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        layout = ComputeLayout(finalSize.Width);
        for (var i = 0; i < elements.Count && i < layout.Hits.Length; i++)
        {
            var hit = layout.Hits[i];
            var visual = trace.Steps[i].IsOnTimeline ? layout.Blocks[i] : layout.Chips[i];
            elements[i].VisualRect = new Rect(visual.X - hit.X, visual.Y - hit.Y, visual.Width, visual.Height);
            elements[i].Arrange(hit);
        }
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        thumbs.Clear(); // re-decode for the new pixel size
        InvalidateMeasure();
        InvalidateVisual();
    }

    private sealed class Layout(int count)
    {
        public double Width, TrackLeft, TrackRight, Scale, ZoneLeft;
        public Rect[] Blocks = new Rect[count];
        public Rect[] Chips = new Rect[count];
        public Rect[] Hits = new Rect[count];
        public Rect NotRunCaption = Rect.Empty;
        public string NotRunText = "", NotRunRange = "";
        public bool NotRunAsBar;
        public readonly List<Thumb> Thumbs = [];
        public IReadOnlyList<RunTraceTick> Ticks = [];
        public double X(double ms) => TrackLeft + ms * Scale;
    }

    private sealed record Thumb(int Position, Rect Frame, Rect Label, double AnchorX);

    private sealed class ThumbState
    {
        public BitmapSource? Image;
        /// <summary>Loaded: a load finished (Image null = missing or unreadable). Fresh: requested for the current trace.</summary>
        public bool Loaded, Fresh;
    }

    private Layout ComputeLayout(double width)
    {
        var t = trace;
        var n = t.Steps.Count;
        var L = new Layout(n) { Width = width };
        var px = Px;

        // Steps that never ran sit after the end of the track: one dashed chip each, or, when there are too
        // many for numbered chips, one dashed bar split into slices (still one element per step).
        var trackRight = width;
        if (t.NotRunCount > 0)
        {
            var k = t.NotRunCount;
            var chipsZone = k * ChipWidth + (k - 1) * ChipGap;
            L.NotRunAsBar = chipsZone > Math.Max(ChipWidth + 2, Math.Floor(width * 0.3));
            double zone, slice, gap;
            if (L.NotRunAsBar)
            {
                zone = Math.Floor(Math.Clamp(k * 8.0, 64, Math.Max(64, width * 0.22)));
                slice = zone / k;
                gap = 0;
            }
            else
            {
                zone = chipsZone;
                slice = ChipWidth;
                gap = ChipGap;
            }
            var x = Snap(width - zone);
            L.ZoneLeft = x;
            foreach (var s in t.Steps)
            {
                if (s.IsOnTimeline) continue;
                L.Chips[s.Position] = new Rect(x, TrackTop + BlockInset, slice, TrackHeight - 2 * BlockInset);
                x += slice + gap;
            }
            var notRun = t.Steps.Where(s => !s.IsOnTimeline).Select(s => s.Number).ToList();
            L.NotRunRange = notRun[^1] - notRun[0] + 1 == notRun.Count
                ? notRun.Count == 1 ? notRun[0].ToString(CultureInfo.CurrentCulture) : string.Format(CultureInfo.CurrentCulture, "{0}–{1}", notRun[0], notRun[^1])
                : string.Format(CultureInfo.CurrentCulture, "{0} steps", notRun.Count);
            var selectedNotRun = selected >= 0 && selected < n && !t.Steps[selected].IsOnTimeline;
            L.NotRunText = L.NotRunAsBar && selectedNotRun
                ? string.Format(CultureInfo.CurrentCulture, "Step {0} not run", selected + 1)
                : t.IsInProgress ? "Not run yet" : "Not run";
            var caption = Text(L.NotRunText, Ui(FontWeights.Normal), CaptionFont, Brushes.Black);
            var captionWidth = caption.WidthIncludingTrailingWhitespace;
            var captionLeft = Math.Min(width - captionWidth, L.ZoneLeft + zone / 2 - captionWidth / 2);
            L.NotRunCaption = new Rect(Snap(captionLeft), LabelTop, captionWidth, LabelHeight);
            trackRight = L.ZoneLeft - ZoneGap;
        }
        L.TrackLeft = 0;
        L.TrackRight = Math.Max(24, Snap(trackRight));
        var trackWidth = L.TrackRight - L.TrackLeft;
        L.Scale = t.TotalMs > 0 ? trackWidth / t.TotalMs : 0;
        L.Ticks = FitTicks(t, L.Scale);

        // Blocks at their real start and duration (at least MinBlockWidth so instant steps stay visible).
        var timed = new List<int>();
        foreach (var s in t.Steps)
        {
            if (!s.IsOnTimeline) continue;
            double x0 = Snap(L.X(s.StartMs)), x1 = Snap(L.X(s.EndMs));
            if (x1 - x0 < MinBlockWidth)
            {
                x1 = x0 + MinBlockWidth;
                if (x1 > L.TrackRight) { x1 = L.TrackRight; x0 = x1 - MinBlockWidth; }
            }
            L.Blocks[s.Position] = new Rect(x0, TrackTop + BlockInset, x1 - x0, TrackHeight - 2 * BlockInset);
            timed.Add(s.Position);
        }
        // Keep a hairline between blocks that touch, so back-to-back steps stay distinguishable.
        var byX = timed.OrderBy(p => L.Blocks[p].Left).ThenBy(p => p).ToList();
        for (var i = 0; i + 1 < byX.Count; i++)
        {
            Rect a = L.Blocks[byX[i]], b = L.Blocks[byX[i + 1]];
            if (a.Right > b.Left - px && a.Right <= b.Left + 2 && a.Width > 2 * px)
                L.Blocks[byX[i]] = new Rect(a.Left, a.Top, Math.Max(px, b.Left - px - a.Left), a.Height);
        }

        // Hit targets: the full track height, at least 12 px wide.
        for (var p = 0; p < n; p++)
        {
            var v = t.Steps[p].IsOnTimeline ? L.Blocks[p] : L.Chips[p];
            var w = Math.Max(v.Width, 12);
            var left = Math.Clamp(v.Left + v.Width / 2 - w / 2, 0, Math.Max(0, width - w));
            L.Hits[p] = new Rect(left, TrackTop, w, TrackHeight);
        }

        // Thumbnails: as many as fit without overlapping (always the selected and the first failure),
        // placed as close as possible to their capture instant.
        if (timed.Count > 0)
        {
            var ordered = timed.OrderBy(p => Anchor(L, p)).ThenBy(p => p).ToList();
            var labelWidths = ordered.Select(p => LabelWidth(t.Steps[p])).ToArray();
            var slot = Math.Max(ThumbWidth, labelWidths.Max());
            var rowRight = L.NotRunCaption.IsEmpty ? width : Math.Min(L.NotRunCaption.Left, L.ZoneLeft) - SlotGap;
            var capacity = Math.Max(1, (int)Math.Floor((rowRight + SlotGap) / (slot + SlotGap)));
            var problem = t.FirstProblem is { IsOnTimeline: true } fp ? ordered.IndexOf(fp.Position) : -1;
            var anchors = ordered.Select(p => Anchor(L, p)).ToArray();
            var chosen = RunTraceLayoutMath.ChooseThumbnails(anchors, ordered.IndexOf(selected), problem, capacity);
            var centers = RunTraceLayoutMath.Pack(chosen.Select(i => anchors[i]).ToArray(), slot, SlotGap, 0, rowRight);
            for (var k = 0; k < chosen.Length; k++)
            {
                var p = ordered[chosen[k]];
                var c = centers[k];
                var lw = labelWidths[chosen[k]];
                L.Thumbs.Add(new Thumb(p,
                    new Rect(Snap(c - ThumbWidth / 2), ThumbTop, ThumbWidth, ThumbHeight),
                    new Rect(Snap(c - lw / 2), LabelTop, lw, LabelHeight),
                    anchors[chosen[k]]));
                EnsureThumbnail(t.Steps[p]);
            }
        }
        return L;
    }

    /// <summary>The capture instant (end of the step): the last pixel column of its block.</summary>
    private double Anchor(Layout L, int position) => L.Blocks[position].Right - Px;

    private IReadOnlyList<RunTraceTick> FitTicks(RunTrace t, double scale)
    {
        var axis = t.Axis;
        if (axis.IntervalMs <= 0 || scale <= 0 || axis.Ticks.Count == 0) return [];
        if (Fits(axis)) return axis.Ticks;
        for (var max = Math.Min(axis.Ticks.Count - 1, 9); max >= 2; max--)
        {
            var coarser = RunTraceAxis.Create(t.TotalMs, 2, max);
            if (Fits(coarser)) return coarser.Ticks;
        }
        return [axis.Ticks[0]];

        bool Fits(RunTraceAxis a) =>
            a.IntervalMs * scale >= a.Ticks.Max(k => Text(k.Label, Mono(FontWeights.Normal), TickFont, Brushes.Black).WidthIncludingTrailingWhitespace) + 16;
    }

    private double LabelWidth(RunTraceStep s)
    {
        var face = Mono(FontWeights.SemiBold);
        var number = Text(s.Number.ToString(CultureInfo.CurrentCulture), face, ValueFont, Brushes.Black);
        var badge = Math.Max(16, Math.Ceiling(number.WidthIncludingTrailingWhitespace) + 8);
        var time = Text(ThumbCaption(s), face, ValueFont, Brushes.Black);
        return badge + 5 + Math.Ceiling(time.WidthIncludingTrailingWhitespace);
    }

    private static string ThumbCaption(RunTraceStep s) => s.State == RunTraceStepState.Running ? "running" : s.EndText;

    // ------------------------------------------------------------------ thumbnails

    private void EnsureThumbnail(RunTraceStep step)
    {
        var path = step.ScreenshotPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!thumbs.TryGetValue(path, out var state)) thumbs[path] = state = new ThumbState();
        if (state.Fresh) return;
        state.Fresh = true;
        pendingLoads.RemoveAll(task => task.IsCompleted);
        var w = (int)Math.Ceiling(ThumbWidth * ppd);
        var h = (int)Math.Ceiling(ThumbHeight * ppd);
        pendingLoads.Add(LoadThumbnailAsync(path, w, h, state));
    }

    private async Task LoadThumbnailAsync(string path, int width, int height, ThumbState state)
    {
        var image = await RunTraceThumbnails.LoadAsync(path, width, height).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            if (!thumbs.TryGetValue(path, out var current) || !ReferenceEquals(current, state)) return; // superseded
            state.Image = image;
            state.Loaded = true;
            InvalidateVisual();
        });
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(HitTestItem(e.GetPosition(this)));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!e.Handled && TrySelectAt(e.GetPosition(this))) e.Handled = true;
    }

    /// <summary>Selects (and focuses) the step whose thumbnail or label is at <paramref name="point"/>.</summary>
    internal bool TrySelectAt(Point point)
    {
        var position = HitTestThumbnail(point);
        if (position < 0) return false;
        SelectFromUser(position, focus: true);
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.OriginalSource is not RunTraceStepElement focused || Keyboard.Modifiers != ModifierKeys.None || elements.Count == 0) return;
        var current = focused.Step.Position;
        var target = e.Key switch
        {
            Key.Left => current - 1,
            Key.Right => current + 1,
            Key.Home => 0,
            Key.End => elements.Count - 1,
            Key.Space or Key.Enter => current,
            _ => int.MinValue,
        };
        if (target == int.MinValue) return;
        target = Math.Clamp(target, 0, elements.Count - 1);
        SelectFromUser(target, focus: true);
        SetFocusCue(target);
        e.Handled = true;
    }

    private void SetHover(int position)
    {
        if (hover == position) return;
        hover = position;
        InvalidateVisual();
    }

    private int HitTestThumbnail(Point point)
    {
        if (layout is null) return -1;
        foreach (var th in layout.Thumbs)
        {
            var area = Rect.Union(th.Frame, th.Label);
            area.Inflate(2, 2);
            if (area.Contains(point)) return th.Position;
        }
        return -1;
    }

    private int HitTestItem(Point point)
    {
        var thumb = HitTestThumbnail(point);
        if (thumb >= 0 || layout is null) return thumb;
        for (var i = layout.Hits.Length - 1; i >= 0; i--)
            if (layout.Hits[i].Contains(point)) return i;
        return -1;
    }

    // ------------------------------------------------------------------ rendering

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var L = layout;
        if (L is null) return;
        ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Array.Clear(inkCache); // pick up the current theme colours
        DrawAxis(dc, L);
        DrawTrack(dc, L);
        DrawBlocks(dc, L);
        DrawNotRun(dc, L);
        DrawLeaders(dc, L);
        DrawThumbnails(dc, L);
    }

    private void DrawAxis(DrawingContext dc, Layout L)
    {
        if (L.Ticks.Count == 0) return;
        var px = Px;
        var tickBrush = Brush(Ink.Text3);
        var labelBrush = Brush(Ink.Text2);
        var face = Mono(FontWeights.Normal);
        var lastRight = double.NegativeInfinity;
        foreach (var tick in L.Ticks)
        {
            var x = Math.Min(Snap(L.X(tick.Ms)), L.TrackRight - px);
            dc.DrawRectangle(tickBrush, null, new Rect(x, TrackTop - TickLength, px, TickLength));
            var ft = Text(tick.Label, face, TickFont, labelBrush);
            var w = ft.WidthIncludingTrailingWhitespace;
            // First label starts at its tick, last label ends at its tick, the rest are centred.
            var left = tick.Ms <= 0 ? x : x + w / 2 > L.TrackRight ? x + px - w : x - w / 2;
            left = Snap(Math.Clamp(left, 0, Math.Max(0, L.Width - w)));
            if (left < lastRight + 6) continue;
            dc.DrawText(ft, new Point(left, Snap(LabelBaseline) - ft.Baseline));
            lastRight = left + w;
        }
    }

    private void DrawTrack(DrawingContext dc, Layout L)
    {
        var px = Px;
        var track = new Rect(L.TrackLeft, TrackTop, L.TrackRight - L.TrackLeft, TrackHeight);
        dc.DrawRoundedRectangle(Brush(Ink.TrackFill), null, track, TrackRadius, TrackRadius);

        // Labels first: gridlines and hatching get a clean cut-out around them (no opaque patch, so any host
        // background shows through).
        var band = new Rect(track.Left, TrackTop + BlockInset, track.Width, TrackHeight - 2 * BlockInset);
        var areas = new GeometryGroup { FillRule = FillRule.Nonzero };
        var cutouts = new GeometryGroup { FillRule = FillRule.Nonzero };
        var labels = new List<(FormattedText Text, Point At)>();
        var face = Mono(FontWeights.Normal);
        var labelBrush = Brush(Ink.Text2);
        var baseline = Snap(TrackTop + TrackHeight / 2 + DigitHeight(ValueFont) / 2);
        foreach (var gap in trace.Gaps)
        {
            double x0 = Snap(L.X(gap.StartMs)), x1 = Snap(L.X(gap.EndMs));
            if (x1 - x0 < px) continue;
            areas.Children.Add(new RectangleGeometry(new Rect(x0, band.Top, x1 - x0, band.Height)));
            if (trace.Steps.Count == 0) continue; // the whole run is one gap; the message below says why
            var ft = Text(trace.FormatTime(gap.DurationMs), face, ValueFont, labelBrush);
            var w = ft.WidthIncludingTrailingWhitespace;
            if (x1 - x0 < w + 10) continue;
            var at = new Point(Snap((x0 + x1 - w) / 2), baseline - ft.Baseline);
            labels.Add((ft, at));
            cutouts.Children.Add(new RectangleGeometry(new Rect(at.X - 4, baseline - DigitHeight(ValueFont) - 4, w + 8, DigitHeight(ValueFont) + 8), 2, 2));
        }
        if (trace.Steps.Count == 0)
        {
            var text = !trace.HasRun ? "No run yet" : trace.IsInProgress ? "No steps yet" : "No steps ran";
            var message = Text(text, Ui(FontWeights.Normal), CaptionFont, Brush(Ink.Text2));
            var w = message.WidthIncludingTrailingWhitespace;
            var at = new Point(Snap(track.Left + (track.Width - w) / 2), Snap(TrackTop + TrackHeight / 2 + DigitHeight(CaptionFont) / 2) - message.Baseline);
            labels.Add((message, at));
            cutouts.Children.Add(new RectangleGeometry(new Rect(at.X - 8, band.Top, w + 16, band.Height), 2, 2));
        }

        // Gridlines only where the track is empty: not through labels, and not peeking above or below blocks.
        var hidden = new GeometryGroup { FillRule = FillRule.Nonzero };
        hidden.Children.Add(cutouts);
        foreach (var s in trace.Steps)
            if (s.IsOnTimeline) hidden.Children.Add(new RectangleGeometry(new Rect(L.Blocks[s.Position].Left, track.Top, L.Blocks[s.Position].Width, track.Height)));
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(track), hidden));
        var grid = Brush(Ink.Gridline);
        foreach (var tick in L.Ticks)
        {
            var x = Snap(L.X(tick.Ms));
            if (tick.Ms <= 0 || x >= L.TrackRight - 2) continue;
            dc.DrawRectangle(grid, null, new Rect(x, TrackTop + px, px, TrackHeight - 2 * px));
        }
        dc.Pop();
        if (areas.Children.Count > 0)
        {
            Geometry clip = cutouts.Children.Count > 0 ? new CombinedGeometry(GeometryCombineMode.Exclude, areas, cutouts) : areas;
            dc.PushClip(clip);
            dc.DrawGeometry(null, Frozen(new Pen(Brush(Ink.Hatch), 1)), HatchLines(band, 6));
            dc.Pop();
        }
        foreach (var (ft, at) in labels) dc.DrawText(ft, at);
        dc.DrawRoundedRectangle(null, Frozen(new Pen(Brush(Ink.TrackStroke), px)), Deflate(track, px / 2), TrackRadius, TrackRadius);
    }

    private void DrawBlocks(DrawingContext dc, Layout L)
    {
        var face = Ui(FontWeights.SemiBold);
        var baseline = Snap(TrackTop + TrackHeight / 2 + DigitHeight(NumberFont) / 2);
        foreach (var s in trace.Steps.Where(s => s.IsOnTimeline).OrderBy(s => s.StartMs).ThenBy(s => s.Position))
        {
            var rect = L.Blocks[s.Position];
            var isSelected = s.Position == selected;
            var isHover = s.Position == hover;
            if (isHover) dc.PushOpacity(0.82);
            if (s.State == RunTraceStepState.Running)
            {
                var stroke = Brush(isSelected ? Ink.Accent : Ink.Text2);
                dc.DrawRoundedRectangle(Brush(Ink.TrackFill), Frozen(new Pen(stroke, 1.5)), Deflate(rect, 0.75), BlockRadius, BlockRadius);
                var ft = Text(s.Number.ToString(CultureInfo.CurrentCulture), face, NumberFont, stroke);
                if (rect.Width >= ft.WidthIncludingTrailingWhitespace + 8)
                    dc.DrawText(ft, new Point(Snap(rect.Left + (rect.Width - ft.WidthIncludingTrailingWhitespace) / 2), baseline - ft.Baseline));
            }
            else
            {
                var fill = s.State switch
                {
                    RunTraceStepState.Failed => Brush(Ink.Critical),
                    RunTraceStepState.Cancelled => Brush(Ink.Caution),
                    _ => Brush(isSelected ? Ink.Accent : Ink.Neutral),
                };
                Geometry shape = new RectangleGeometry(rect, BlockRadius, BlockRadius);
                // The step number is knocked out of the block, so the track shows through in any theme.
                if (rect.Width >= 12)
                {
                    var ft = Text(s.Number.ToString(CultureInfo.CurrentCulture), face, NumberFont, fill);
                    var w = ft.WidthIncludingTrailingWhitespace;
                    if (rect.Width >= w + 6)
                        shape = new CombinedGeometry(GeometryCombineMode.Exclude, shape,
                            ft.BuildGeometry(new Point(Snap(rect.Left + (rect.Width - w) / 2), baseline - ft.Baseline)));
                }
                dc.DrawGeometry(fill, null, shape);
            }
            if (isHover) dc.Pop();
        }
    }

    private void DrawNotRun(DrawingContext dc, Layout L)
    {
        if (trace.NotRunCount == 0) return;
        var px = Px;
        // Dashes use the tertiary colour; text uses the secondary one, which keeps AA contrast on light cards.
        var dashed = new Pen(Brush(Ink.Text3), px) { DashStyle = new DashStyle([2, 2], 0) };
        dashed.Freeze();
        var textBrush = Brush(Ink.Text2);
        var face = Ui(FontWeights.Normal);
        var baseline = Snap(TrackTop + TrackHeight / 2 + DigitHeight(ValueFont) / 2);
        if (L.NotRunAsBar)
        {
            // One dashed bar with the step range; the selected (or hovered) slice is outlined.
            var bar = new Rect(L.ZoneLeft, TrackTop + BlockInset, L.Width - L.ZoneLeft, TrackHeight - 2 * BlockInset);
            dc.DrawRoundedRectangle(null, dashed, Deflate(bar, px / 2), BlockRadius, BlockRadius);
            var range = Text(L.NotRunRange, face, ValueFont, textBrush);
            var rw = range.WidthIncludingTrailingWhitespace;
            var marked = selected >= 0 && selected < trace.Steps.Count && !trace.Steps[selected].IsOnTimeline ? selected
                : hover >= 0 && hover < trace.Steps.Count && !trace.Steps[hover].IsOnTimeline ? hover : -1;
            if (bar.Width >= rw + 12 && marked < 0)
                dc.DrawText(range, new Point(Snap(bar.Left + (bar.Width - rw) / 2), baseline - range.Baseline));
            if (marked >= 0)
            {
                var slice = L.Chips[marked];
                var r = new Rect(Snap(slice.Left), slice.Top, Math.Max(2 * px, Snap(slice.Right) - Snap(slice.Left)), slice.Height);
                var pen = Frozen(new Pen(Brush(marked == selected ? Ink.Accent : Ink.Text2), (marked == selected ? 2 : 1) * px));
                dc.DrawRoundedRectangle(null, pen, Deflate(r, pen.Thickness / 2), 1, 1);
            }
        }
        else
        {
            foreach (var s in trace.Steps)
            {
                if (s.IsOnTimeline) continue;
                var r = L.Chips[s.Position];
                var isSelected = s.Position == selected;
                var pen = isSelected ? Frozen(new Pen(Brush(Ink.Accent), 2 * px)) : dashed;
                if (s.Position == hover && !isSelected) pen = Frozen(new Pen(Brush(Ink.Text2), px) { DashStyle = dashed.DashStyle });
                dc.DrawRoundedRectangle(null, pen, Deflate(r, pen.Thickness / 2), BlockRadius, BlockRadius);
                var ft = Text(s.Number.ToString(CultureInfo.CurrentCulture), face, ValueFont, isSelected ? Brush(Ink.Accent) : textBrush);
                var w = ft.WidthIncludingTrailingWhitespace;
                if (r.Width >= w + 4)
                    dc.DrawText(ft, new Point(Snap(r.Left + (r.Width - w) / 2), baseline - ft.Baseline));
            }
        }
        var caption = Text(L.NotRunText, face, CaptionFont, textBrush);
        dc.DrawText(caption, new Point(L.NotRunCaption.X, Snap(LabelTop + 12) - caption.Baseline));
    }

    private void DrawLeaders(DrawingContext dc, Layout L)
    {
        var px = Px;
        foreach (var th in L.Thumbs)
        {
            if (th.Position == selected) continue;
            var brush = Brush(th.Position == hover ? Ink.Text2 : Ink.Hatch);
            // Leaders leave from the capture instant; when the thumbnail sits under its own block anyway,
            // drop straight down instead of drawing a tiny jog.
            var centre = Snap(th.Frame.Left + ThumbWidth / 2);
            var block = L.Blocks[th.Position];
            var from = centre >= block.Left + px && centre <= block.Right - px ? centre : th.AnchorX;
            Elbow(dc, brush, from, block.Bottom, centre, ThumbTop, px);
        }
        if (selected < 0 || selected >= trace.Steps.Count || !trace.Steps[selected].IsOnTimeline) return;

        // Playhead: a 1 px accent line at the capture instant of the selected step, continuing to its thumbnail.
        var accent = Brush(Ink.Accent);
        var x = Snap(L.Blocks[selected].Right - px);
        var top = Snap(LabelBaseline + 4);
        dc.DrawRectangle(accent, null, new Rect(x, top, px, TrackBottom - top));
        var thumb = L.Thumbs.FirstOrDefault(th => th.Position == selected);
        if (thumb is not null) Elbow(dc, accent, x, TrackBottom, thumb.Frame.Left + ThumbWidth / 2, ThumbTop - 2, px);
    }

    private void Elbow(DrawingContext dc, Brush brush, double ax, double y0, double tx, double y1, double px)
    {
        ax = Snap(ax);
        tx = Snap(tx);
        if (Math.Abs(ax - tx) < px / 2)
        {
            dc.DrawRectangle(brush, null, new Rect(ax, y0, px, Math.Max(0, y1 - y0)));
            return;
        }
        var ym = Snap(y0 + (y1 - y0) / 2);
        dc.DrawRectangle(brush, null, new Rect(ax, y0, px, ym - y0));
        dc.DrawRectangle(brush, null, new Rect(Math.Min(ax, tx), ym, Math.Abs(tx - ax) + px, px));
        dc.DrawRectangle(brush, null, new Rect(tx, ym, px, Math.Max(0, y1 - ym)));
    }

    private void DrawThumbnails(DrawingContext dc, Layout L)
    {
        var px = Px;
        foreach (var th in L.Thumbs)
        {
            var s = trace.Steps[th.Position];
            var isSelected = th.Position == selected;
            var frame = th.Frame;
            var shape = new RectangleGeometry(frame, FrameRadius, FrameRadius);
            var state = s.ScreenshotPath is { } path ? thumbs.GetValueOrDefault(path) : null;
            // A running step captures its screenshot when it ends; otherwise no path, or a load that produced
            // nothing (missing or unreadable file), shows the dashed "no screenshot" placeholder.
            var pending = s.State == RunTraceStepState.Running && s.ScreenshotPath is null;
            var missing = !pending && (s.ScreenshotPath is null || state is { Loaded: true, Image: null });
            if (state?.Image is { } image)
            {
                dc.PushClip(shape);
                dc.DrawImage(image, Cover(frame, image.PixelWidth, image.PixelHeight));
                dc.Pop();
            }
            else
            {
                dc.DrawGeometry(Brush(Ink.TrackFill), null, shape);
                if (missing)
                {
                    var glyph = Text(MissingGlyph, new Typeface((FontFamily)GetValue(SymbolFontProperty), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 16, Brush(Ink.Text3));
                    dc.DrawText(glyph, new Point(Snap(frame.Left + (frame.Width - glyph.Width) / 2), Snap(frame.Top + (frame.Height - glyph.Height) / 2)));
                }
            }

            if (isSelected)
            {
                var ring = frame;
                ring.Inflate(1, 1);
                dc.DrawRoundedRectangle(null, Frozen(new Pen(Brush(Ink.Accent), 2)), ring, FrameRadius + 1, FrameRadius + 1);
            }
            else
            {
                var ink = s.State == RunTraceStepState.Failed ? Ink.Critical
                    : s.State == RunTraceStepState.Cancelled ? Ink.Caution
                    : th.Position == hover ? Ink.Text2 : Ink.Frame;
                var pen = new Pen(Brush(ink), px);
                if (missing) pen.DashStyle = new DashStyle([2, 2], 0);
                dc.DrawRoundedRectangle(null, Frozen(pen), Deflate(frame, px / 2), FrameRadius, FrameRadius);
            }
            DrawThumbLabel(dc, th, s, isSelected, px);
        }
    }

    private void DrawThumbLabel(DrawingContext dc, Thumb th, RunTraceStep s, bool isSelected, double px)
    {
        var face = Mono(isSelected ? FontWeights.SemiBold : FontWeights.Normal);
        // The badge mirrors its block: status colour for problems, accent when selected, outline otherwise.
        Ink? filled = s.State switch
        {
            RunTraceStepState.Failed => Ink.Critical,
            RunTraceStepState.Cancelled => Ink.Caution,
            _ => isSelected ? Ink.Accent : null,
        };
        var number = Text(s.Number.ToString(CultureInfo.CurrentCulture), face, ValueFont, Brush(filled is null ? Ink.Text2 : Ink.OnAccent));
        var badge = new Rect(th.Label.Left, th.Label.Top, Math.Max(16, Math.Ceiling(number.WidthIncludingTrailingWhitespace) + 8), 16);
        if (filled is { } ink) dc.DrawRoundedRectangle(Brush(ink), null, badge, 3, 3);
        else dc.DrawRoundedRectangle(null, Frozen(new Pen(Brush(Ink.Hatch), px)), Deflate(badge, px / 2), 3, 3);
        var baseline = Snap(badge.Top + 12);
        dc.DrawText(number, new Point(Snap(badge.Left + (badge.Width - number.WidthIncludingTrailingWhitespace) / 2), baseline - number.Baseline));
        var time = Text(ThumbCaption(s), face, ValueFont, Brush(isSelected ? Ink.Text1 : Ink.Text2));
        dc.DrawText(time, new Point(badge.Right + 5, baseline - time.Baseline));
    }

    /// <summary>Fluent focus visual: 2 px ring, 3 px outside the element, corner radius 4 (DefaultControlFocusVisualStyle).</summary>
    internal void DrawFocusRing(DrawingContext dc, Rect visual)
    {
        var ring = visual;
        ring.Inflate(3, 3);
        dc.DrawRoundedRectangle(null, Frozen(new Pen(Brush(Ink.Focus), 2)), Deflate(ring, 1), 4, 4);
    }

    // ------------------------------------------------------------------ helpers

    internal static StreamGeometry HatchLines(Rect area, double spacing)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var h = area.Height;
            // Anchored to x = 0 so the pattern lines up across separate gaps.
            for (var x = Math.Floor((area.Left - h) / spacing) * spacing; x <= area.Right; x += spacing)
            {
                ctx.BeginFigure(new Point(x, area.Bottom), false, false);
                ctx.LineTo(new Point(x + h, area.Top), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>UniformToFill, centred horizontally and aligned to the top (keeps the window's title bar).</summary>
    private static Rect Cover(Rect box, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return box;
        var scale = Math.Max(box.Width / pixelWidth, box.Height / pixelHeight);
        double w = pixelWidth * scale, h = pixelHeight * scale;
        return new Rect(box.Left + (box.Width - w) / 2, box.Top, w, h);
    }

    /// <summary>Theme brush for this frame. Fluent brushes whose colour is itself a DynamicResource (e.g. the accent)
    /// cannot be frozen, so a frozen snapshot is taken once per frame; the DP still tracks theme changes.</summary>
    private Brush Brush(Ink ink)
    {
        var i = (int)ink;
        if (inkCache[i] is { } cached) return cached;
        var brush = (Brush)GetValue(InkProperties[i]);
        if (!brush.IsFrozen)
        {
            brush = brush.CloneCurrentValue();
            if (brush.CanFreeze) brush.Freeze();
        }
        return inkCache[i] = brush;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property.OwnerType == typeof(RunTraceSurface) && e.Property.Name.StartsWith("Ink", StringComparison.Ordinal)) Array.Clear(inkCache);
    }
    private double Px => Math.Max(1, Math.Round(ppd)) / ppd;
    private double Snap(double value) => Math.Round(value * ppd) / ppd;
    private static double DigitHeight(double fontSize) => Math.Round(fontSize * 0.7);
    private static Rect Deflate(Rect r, double by) => r.Width > 2 * by && r.Height > 2 * by ? new Rect(r.X + by, r.Y + by, r.Width - 2 * by, r.Height - 2 * by) : r;
    private Typeface Ui(FontWeight weight) => new(FontFamily, FontStyles.Normal, weight, FontStretches.Normal);
    private Typeface Mono(FontWeight weight) => new(view?.MonoFontFamily ?? DefaultMono, FontStyles.Normal, weight, FontStretches.Normal);

    private FormattedText Text(string text, Typeface face, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, ppd);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>One focusable, selectable element per step (UI Automation ListItem "RunTraceStep{n}"). Draws only its focus ring.</summary>
internal sealed class RunTraceStepElement : FrameworkElement
{
    public RunTraceStepElement(RunTraceSurface surface, RunTraceStep step)
    {
        Surface = surface;
        Step = step;
        Focusable = true;
        FocusVisualStyle = null; // the Fluent-style ring is drawn in OnRender, for keyboard focus only
        Update(step);
    }

    public RunTraceSurface Surface { get; }
    public RunTraceStep Step { get; private set; }
    internal Rect VisualRect { get; set; }
    public bool IsSelected => Surface.SelectedPosition == Step.Position;

    internal string HelpText
    {
        get
        {
            if (Step.State is RunTraceStepState.Passed or RunTraceStepState.NotRun) return "";
            var message = string.Join(' ', (Step.Source.Message ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return message.Length <= 300 ? message : message[..299] + "…";
        }
    }

    public void Update(RunTraceStep step)
    {
        Step = step;
        AutomationProperties.SetAutomationId(this, "RunTraceStep" + step.Number.ToString(CultureInfo.InvariantCulture));
        AutomationProperties.SetName(this, step.AutomationName);
        var help = HelpText;
        ToolTip = help.Length > 0 ? step.AutomationName + Environment.NewLine + help : step.AutomationName;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => default;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable
        if (Surface.ShowsFocusCue(this)) Surface.DrawFocusRing(dc, VisualRect);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Surface.OnStepGotFocus(this, InputManager.Current.MostRecentInputDevice is KeyboardDevice);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Surface.OnStepLostFocus(this);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Surface.SelectFromUser(Step.Position, focus: true);
        e.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RunTraceStepAutomationPeer(this);
}

/// <summary>Pure layout helpers (unit-tested by the lab's --selftest).</summary>
internal static class RunTraceLayoutMath
{
    /// <summary>
    /// Picks at most <paramref name="capacity"/> of the items at <paramref name="positions"/> (ascending), spread as evenly
    /// as possible from the first to the last, and always including <paramref name="mustA"/> and <paramref name="mustB"/>
    /// (the selected step and the first failure), which replace their nearest neighbour. Returns ascending indices.
    /// </summary>
    public static int[] ChooseThumbnails(IReadOnlyList<double> positions, int mustA, int mustB, int capacity)
    {
        var n = positions.Count;
        if (capacity <= 0 || n == 0) return [];
        if (n <= capacity) return Enumerable.Range(0, n).ToArray();
        var musts = new[] { mustA, mustB }.Where(m => m >= 0 && m < n).Distinct().Take(capacity).ToList();
        var chosen = new List<int>();
        if (capacity == 1) chosen.Add(musts.Count > 0 ? musts[0] : n - 1);
        else
        {
            // Evenly spaced ideal positions between the first and last item; take the nearest free item for each.
            double first = positions[0], span = positions[n - 1] - positions[0];
            for (var j = 0; j < capacity; j++)
            {
                var ideal = first + span * j / (capacity - 1);
                int best = -1;
                var bestDistance = double.MaxValue;
                for (var i = 0; i < n; i++)
                {
                    var d = Math.Abs(positions[i] - ideal);
                    if (!chosen.Contains(i) && d < bestDistance) { bestDistance = d; best = i; }
                }
                if (best >= 0) chosen.Add(best);
            }
        }
        foreach (var must in musts)
        {
            if (chosen.Contains(must)) continue;
            var replace = chosen.Where(c => !musts.Contains(c)).OrderBy(c => Math.Abs(positions[c] - positions[must])).ThenBy(c => c).DefaultIfEmpty(-1).First();
            if (replace >= 0) chosen[chosen.IndexOf(replace)] = must;
            else if (chosen.Count < capacity) chosen.Add(must);
        }
        chosen.Sort();
        return chosen.ToArray();
    }

    /// <summary>
    /// 1-D placement: returns centres at least <paramref name="slot"/> + <paramref name="gap"/> apart, keeping order,
    /// inside [<paramref name="lo"/>, <paramref name="hi"/>] when they fit, with the least squared displacement from
    /// <paramref name="desired"/> (ascending).
    /// </summary>
    public static double[] Pack(IReadOnlyList<double> desired, double slot, double gap, double lo, double hi)
    {
        var n = desired.Count;
        var result = new double[n];
        if (n == 0) return result;
        var step = slot + gap;
        var min = lo + slot / 2;
        var max = Math.Max(min, hi - slot / 2);
        var first = new List<int>();
        var count = new List<int>();
        var start = new List<double>();
        for (var i = 0; i < n; i++)
        {
            first.Add(i);
            count.Add(1);
            start.Add(Place(1, desired[i]));
            while (start.Count > 1)
            {
                int b = start.Count - 1, a = b - 1;
                if (start[a] + count[a] * step <= start[b] + 1e-9) break;
                int f = first[a], c = count[a] + count[b];
                var sum = 0.0;
                for (var k = 0; k < c; k++) sum += desired[f + k] - k * step;
                first.RemoveAt(b);
                count.RemoveAt(b);
                start.RemoveAt(b);
                count[a] = c;
                start[a] = Place(c, sum / c);
            }
        }
        for (var j = 0; j < first.Count; j++)
            for (var k = 0; k < count[j]; k++)
                result[first[j] + k] = start[j] + k * step;
        return result;

        double Place(int c, double s) => Math.Max(min, Math.Min(s, max - (c - 1) * step));
    }
}

/// <summary>
/// Thumbnail decoding: BitmapImage with DecodePixelWidth, CacheOption.OnLoad and Freeze, from a FileStream opened
/// with FileShare.ReadWrite | FileShare.Delete and closed straight away, so screenshot files are never locked.
/// Decodes off the UI thread, at most three at a time, with a small LRU cache.
/// </summary>
internal static class RunTraceThumbnails
{
    private const int Capacity = 160;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (BitmapSource? Image, DateTime Stamp, long Length)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Recent = new();
    private static readonly SemaphoreSlim Throttle = new(3);

    /// <summary>Never throws; null when the file is missing, empty or not a readable image.</summary>
    public static async Task<BitmapSource?> LoadAsync(string path, int boxWidthPx, int boxHeightPx)
    {
        await Throttle.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Load(path, boxWidthPx, boxHeightPx)).ConfigureAwait(false); }
        finally { Throttle.Release(); }
    }

    internal static BitmapSource? Load(string path, int boxWidthPx, int boxHeightPx)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return null;
            var key = string.Create(CultureInfo.InvariantCulture, $"{info.FullName}|{boxWidthPx}x{boxHeightPx}");
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var hit) && hit.Stamp == info.LastWriteTimeUtc && hit.Length == info.Length)
                {
                    Recent.Remove(key);
                    Recent.AddFirst(key);
                    return hit.Image;
                }
            }
            var image = Decode(info.FullName, boxWidthPx, boxHeightPx);
            lock (Gate)
            {
                Cache[key] = (image, info.LastWriteTimeUtc, info.Length);
                Recent.Remove(key);
                Recent.AddFirst(key);
                while (Recent.Count > Capacity)
                {
                    Cache.Remove(Recent.Last!.Value);
                    Recent.RemoveLast();
                }
            }
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
            or InvalidOperationException or OverflowException or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static BitmapImage Decode(string path, int boxWidthPx, int boxHeightPx)
    {
        int pixelWidth, pixelHeight;
        using (var header = Open(path))
        {
            var frame = BitmapDecoder.Create(header, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            pixelWidth = frame.PixelWidth;
            pixelHeight = frame.PixelHeight;
        }
        if (pixelWidth <= 0 || pixelHeight <= 0) throw new NotSupportedException("Empty image.");
        // Decode straight to the size that covers the thumbnail box (UniformToFill), never upscaling.
        var cover = Math.Max(boxWidthPx / (double)pixelWidth, boxHeightPx / (double)pixelHeight);
        var decodeWidth = Math.Clamp((int)Math.Ceiling(pixelWidth * cover), 1, pixelWidth);
        using var stream = Open(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // fully decoded in EndInit; the stream can close right after
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = decodeWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
}
