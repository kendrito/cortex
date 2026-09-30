using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Testy.WpfLab;

public partial class LabWindow : Window
{
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    private readonly List<Order> rows = Enumerable.Range(1, 2000).Select(i => new Order(i)).ToList();
    private readonly QuantityModel model = new();
    private ICollectionView gridView = null!;
    private int selections, viewports, trees, actions;
    private bool ready;
    public LabWindow()
    {
        InitializeComponent();
        DataContext = model;
        gridView = new ListCollectionView(rows);
        OrdersGrid.ItemsSource = gridView;
        OrdersList.ItemsSource = rows;
        for (var i = 1; i <= 60; i++)
        {
            var line = new TextBlock { Text = $"Document line {i:00}", Margin = new Thickness(2, 8, 2, 8) };
            AutomationProperties.SetAutomationId(line, $"DocumentLine-{i:00}");
            DocumentContent.Children.Add(line);
        }
        // Optional owned fixture for the UIA TextPattern boundary. Normal stress runs
        // retain the same control tree and performance workload.
        if (Environment.GetCommandLineArgs().Contains("--text-boundary", StringComparer.Ordinal))
        {
            var richText = new RichTextBox
            {
                IsReadOnly = true, Height = 100,
                Document = new FlowDocument(new Paragraph(new Run(new string('R', 8193))))
            };
            AutomationProperties.SetAutomationId(richText, "RichTextBoundary");
            AutomationProperties.SetName(richText, "Long read-only document");
            DocumentContent.Children.Add(richText);
        }
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (ready && (e.VerticalChange != 0 || e.HorizontalChange != 0)) { viewports++; RefreshState(); }
        }));
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowWindow(new WindowInteropHelper(this).Handle, 5);
        Activate();
        ValidatedQuantity.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => { ready = true; RefreshState(); });
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) { if (ready) selections++; RefreshState(); }
    private void Tree_Changed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && ready) trees++;
        RefreshState();
    }
    private void RefreshState()
    {
        if (FixtureState is null || SelectionState is null) return;
        FixtureState.Text = $"selections={selections}; viewports={viewports}; trees={trees}; actions={actions}";
        SelectionState.Text = $"Grid selection: {(OrdersGrid.SelectedItem as Order)?.Id ?? "none"}; List selection: {(OrdersList.SelectedItem as Order)?.Id ?? "none"}";
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        ready = false;
        gridView.Filter = null; gridView.SortDescriptions.Clear();
        OrdersGrid.SelectedItem = null; OrdersList.SelectedItem = null;
        OrdersGrid.ScrollIntoView(rows[0]); OrdersList.ScrollIntoView(rows[0]);
        TreeRoot.IsExpanded = false; TreeBranch.IsExpanded = false;
        LongDocument.ScrollToTop(); OrdersGrid.Columns[0].DisplayIndex = 0;
        ValidatedQuantity.Text = "0";
        FixtureStatus.Text = "Reset complete";
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => { selections = viewports = trees = actions = 0; ready = true; RefreshState(); });
    }
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        gridView.SortDescriptions.Clear(); gridView.SortDescriptions.Add(new SortDescription(nameof(Order.Number), ListSortDirection.Descending));
        actions++; FixtureStatus.Text = "Sorted descending"; RefreshState();
    }
    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        gridView.Filter = value => ((Order)value).Number >= 1500;
        actions++; FixtureStatus.Text = "Filtered to 1500+"; RefreshState();
    }
    private void ClearFilter_Click(object sender, RoutedEventArgs e) { gridView.Filter = null; actions++; FixtureStatus.Text = "Filter cleared"; RefreshState(); }
    private void Reorder_Click(object sender, RoutedEventArgs e) { OrdersGrid.Columns[0].DisplayIndex = 2; actions++; FixtureStatus.Text = "Key column moved last"; RefreshState(); }
    private void Template_Click(object sender, RoutedEventArgs e) { actions++; FixtureStatus.Text = "Templated action invoked"; RefreshState(); }
    private void Modal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Title = "WPF Lab modal", Owner = this, Width = 380, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Owner controls must be blocked.", Margin = new Thickness(0, 0, 0, 18) });
        var close = new Button { Content = "Close modal" }; AutomationProperties.SetAutomationId(close, "CloseModal");
        close.Click += (_, _) => dialog.Close(); panel.Children.Add(close); dialog.Content = panel;
        actions++; RefreshState(); dialog.ShowDialog();
    }
}

public sealed class Order(int number)
{
    public int Number { get; } = number;
    public string Id => $"Order-{Number:0000}";
    public string ListId => $"ListOrder-{Number:0000}";
    public string Label => Number is 200 or 1200 ? "Duplicate entry" : $"Order {Number:0000}";
    public decimal Amount => Number * 1.25m;
    public override string ToString() => Label;
}
public sealed class QuantityModel { public string Quantity { get; set; } = "0"; }
public sealed class PositiveNumberRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo) => int.TryParse(value?.ToString(), out var number) && number > 0
        ? ValidationResult.ValidResult : new ValidationResult(false, "Enter a positive whole number.");
}
