using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
namespace Testy.TestLab;
public partial class MainWindow : Window
{
    private readonly List<Customer> customers = [];
    private int operationGeneration;
    public MainWindow() { InitializeComponent(); RefreshCustomers(); }
    private void AddCustomer_Click(object sender, RoutedEventArgs e)
    {
        string name = CustomerName.Text.Trim(), email = CustomerEmail.Text.Trim();
        if (name.Length == 0) { StatusMessage.Text = "Enter a customer name."; return; }
        if (!System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email || !email.Contains('.') || email.EndsWith('.')) { StatusMessage.Text = "Enter a valid email address."; return; }
        customers.Add(new Customer(name, email)); RefreshCustomers();
        StatusMessage.Text = DefectToggle.IsChecked == true ? "Customer added: [incorrect name]" : $"Customer added: {name}";
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsInitialized && CustomerList != null) RefreshCustomers(); }
    private void RefreshCustomers()
    {
        string search = SearchBox.Text.Trim();
        var filtered = customers.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Email.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        CustomerList.ItemsSource = filtered; ResultCount.Text = filtered.Count == 1 ? "1 customer" : $"{filtered.Count} customers";
    }
    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        operationGeneration++; customers.Clear(); CustomerName.Clear(); CustomerEmail.Clear(); SearchBox.Clear(); DefectToggle.IsChecked = false; DelayedButton.IsEnabled = true;
        RefreshCustomers(); StatusMessage.Text = "Ready for a new customer.";
    }
    private async void DelayedButton_Click(object sender, RoutedEventArgs e)
    {
        int generation = operationGeneration; DelayedButton.IsEnabled = false; StatusMessage.Text = "Background check running…";
        await Task.Delay(700);
        if (generation != operationGeneration) return;
        DelayedButton.IsEnabled = true; StatusMessage.Text = "Background check complete.";
    }
    private void OpenReview_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Margin = new Thickness(28) };
        var heading = new TextBlock { Text = "Directory review", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) };
        var summary = new TextBlock { Text = $"{customers.Count} customer records in this session.", FontSize = 14, Margin = new Thickness(0, 0, 0, 28) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(summary, "ReviewSummary");
        var close = new Button { Content = "Close review", HorizontalAlignment = HorizontalAlignment.Left, IsDefault = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(close, "CloseReview");
        content.Children.Add(heading); content.Children.Add(summary); content.Children.Add(close);
        var dialog = new Window { Title = "Customer review", Width = 440, Height = 280, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, Content = content };
        System.Windows.Automation.AutomationProperties.SetAutomationId(dialog, "ReviewDialog");
        close.Click += (_, _) => dialog.Close(); dialog.ShowDialog();
    }
    public sealed record Customer(string Name, string Email) { public override string ToString() => $"{Name} — {Email}"; }
}
