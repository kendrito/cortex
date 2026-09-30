using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Testy.WpfLab;

/// <summary>Owned multi-HWND screenshot/physical-input fixture; all state is displayed.</summary>
public sealed class CaptureLabWindow : Window
{
    private Window? auxiliary;
    private int count;
    private readonly TextBlock result = new() { Text = "0", FontSize = 24 };
    public CaptureLabWindow()
    {
        Title = "Testy capture surfaces lab"; Width = 640; Height = 480; Left = 120; Top = 140; WindowStartupLocation = WindowStartupLocation.Manual; Background = Brushes.WhiteSmoke;
        AutomationProperties.SetAutomationId(this, "CaptureLabWindow");
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Owned windows and popup capture", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var choices = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 12), SelectedIndex = 0 };
        AutomationProperties.SetAutomationId(choices, "CaptureChoice");
        foreach (string name in new[] { "Violet choice", "Orange choice", "Azure choice" })
        {
            var option = new ComboBoxItem { Content = name, Background = new SolidColorBrush(Color.FromRgb(217, 41, 190)), Foreground = Brushes.White, Padding = new Thickness(10), Height = 45 };
            AutomationProperties.SetName(option, name); AutomationProperties.SetAutomationId(option, "CaptureOption" + name.Split(' ')[0]); choices.Items.Add(option);
        }
        panel.Children.Add(choices);
        var input = new TextBox { Text = "", Height = 32, Margin = new Thickness(0, 6, 0, 6) }; AutomationProperties.SetAutomationId(input, "NativeText"); panel.Children.Add(input);
        Button(panel, "NativeIncrement", "Increment using physical input", () => result.Text = (++count).ToString()); AutomationProperties.SetAutomationId(result, "NativeCount"); panel.Children.Add(result);
        Button(panel, "OpenAuxiliary", "Open separate owned window", () =>
        {
            if (auxiliary is not null) { auxiliary.Activate(); return; }
            auxiliary = new Window { Owner = this, Title = "Owned capture auxiliary", Width = 270, Height = 220, Left = Left + Width + 60, Top = Top + 30, WindowStartupLocation = WindowStartupLocation.Manual, Background = Brushes.LightSkyBlue };
            AutomationProperties.SetAutomationId(auxiliary, "CaptureAuxiliary");
            var body = new StackPanel { Margin = new Thickness(20) }; body.Children.Add(new TextBlock { Text = "Owned auxiliary content", FontSize = 17 });
            Button(body, "AuxiliaryIncrement", "Increment owned counter", () => result.Text = (++count).ToString());
            Button(body, "CloseAuxiliary", "Close this window", () => auxiliary.Close());
            auxiliary.Content = body; auxiliary.Closed += (_, _) => auxiliary = null; auxiliary.Show();
        });
        Button(panel, "MoveAuxiliary", "Move owned window", () => { if (auxiliary is not null) auxiliary.Left += 25; });
        Loaded += (_, _) => Activate();
    }
    private static void Button(Panel panel, string id, string label, Action action)
    { var button = new Button { Content = label, Margin = new Thickness(0, 5, 0, 5), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left }; AutomationProperties.SetAutomationId(button, id); button.Click += (_, _) => action(); panel.Children.Add(button); }
}
