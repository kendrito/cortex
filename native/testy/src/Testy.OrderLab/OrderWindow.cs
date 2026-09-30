using System.Globalization;
using System.Runtime.InteropServices;

namespace Testy.OrderLab;

/// <summary>An independent real WinForms application for owned integration tests.</summary>
public sealed class OrderWindow : Form
{
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    private readonly TextBox shippingName = Input("Recipient", "Recipient");
    private readonly TextBox billingName = Input("Recipient", "Recipient");
    private readonly TextBox quantity = Input("Quantity", "Quantity");
    private readonly TextBox status = Readout("OrderStatus", "Ready for an order.");
    private readonly TextBox shippingStatus = Readout("ShippingStatus", "Shipping not applied");
    private readonly TextBox billingStatus = Readout("BillingStatus", "Billing not applied");
    private readonly TextBox mutationCount = Readout("MutationCount", "0");
    private readonly TextBox priorityStatus = Readout("PriorityStatus", "Priority: none");
    private readonly TextBox generationStatus = Readout("GenerationStatus", "Generation 0");
    private readonly CheckBox defect = new() { Name = "DefectToggle", AccessibleName = "Simulate wrong total", Text = "Simulate wrong total", AutoSize = true };
    private readonly Button submit = Action("SubmitOrder", "Submit order");
    private readonly Button inventory = Action("CheckInventory", "Check inventory");
    private readonly FlowLayoutPanel priorities = new() { Name = "Priorities", AccessibleName = "Priorities", AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
    private bool shippingApplied;
    private bool billingApplied;
    private int mutations;
    private int generation;
    private int operationGeneration;

    public OrderWindow()
    {
        Name = "OrderWindow"; AccessibleName = "Testy OrderLab";
        Text = "Testy OrderLab — Independent WinForms fixture";
        ClientSize = new Size(1060, 780); MinimumSize = new Size(1000, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(243, 246, 249); ForeColor = Color.FromArgb(25, 43, 61);
        Font = new Font("Segoe UI", 10);
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 7 };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 186));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new Label { Text = "Order desk", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        heading.Font = new Font("Segoe UI", 22, FontStyle.Bold);
        page.Controls.Add(heading, 0, 0); page.SetColumnSpan(heading, 2);
        page.Controls.Add(AddressSection("Shipping", shippingName, shippingStatus, () =>
        { shippingApplied = shippingName.Text.Trim().Length > 0; shippingStatus.Text = shippingApplied ? "Shipping: " + shippingName.Text.Trim() : "Shipping name required"; Mutated(); UpdateSubmit(); }), 0, 1);
        page.Controls.Add(AddressSection("Billing", billingName, billingStatus, () =>
        { billingApplied = billingName.Text.Trim().Length > 0; billingStatus.Text = billingApplied ? "Billing: " + billingName.Text.Trim() : "Billing name required"; Mutated(); UpdateSubmit(); }), 1, 1);

        var amount = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 16, 0, 8) };
        amount.Controls.Add(new Label { Text = "Quantity ($25 each)", AutoSize = true, Padding = new Padding(0, 6, 16, 0) });
        quantity.Width = 100; amount.Controls.Add(quantity);
        amount.Controls.Add(defect); page.Controls.Add(amount, 0, 2); page.SetColumnSpan(amount, 2);
        quantity.TextChanged += (_, _) => UpdateSubmit();

        var dynamicGroup = new GroupBox { Name = "DynamicSection", AccessibleName = "Dynamic priorities", Text = "Dynamic priorities — recreated and reordered", Dock = DockStyle.Fill, Padding = new Padding(12) };
        dynamicGroup.Controls.Add(priorities); page.Controls.Add(dynamicGroup, 0, 3);
        var dynamicControls = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 0, 0), FlowDirection = FlowDirection.TopDown };
        var reorder = Action("ReorderControls", "Recreate and reorder"); reorder.Click += (_, _) => { generation++; RebuildPriorities(); Mutated(); };
        dynamicControls.Controls.Add(reorder); dynamicControls.Controls.Add(generationStatus); dynamicControls.Controls.Add(priorityStatus); page.Controls.Add(dynamicControls, 1, 3);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 18, 0, 0) };
        submit.Click += (_, _) => BeginInvoke(new System.Action(ShowConfirmation));
        inventory.Click += async (_, _) =>
        {
            var current = operationGeneration; inventory.Enabled = false; status.Text = "Inventory check running";
            await Task.Delay(650);
            if (current != operationGeneration || IsDisposed) return;
            status.Text = "Inventory ready"; inventory.Enabled = true;
        };
        var disabled = Action("DisabledAction", "Unavailable action"); disabled.Enabled = false;
        disabled.Click += (_, _) => { status.Text = "ERROR: disabled action executed"; Mutated(); };
        var reset = Action("ResetOrders", "Reset fixture"); reset.Click += (_, _) => Reset();
        actions.Controls.AddRange([submit, inventory, disabled, reset]); page.Controls.Add(actions, 0, 4); page.SetColumnSpan(actions, 2);
        var results = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 12, 0, 0) };
        status.Width = 940; status.Font = new Font(Font, FontStyle.Bold);
        results.Controls.Add(status);
        var counters = new FlowLayoutPanel { AutoSize = true };
        counters.Controls.Add(new Label { Text = "Dispatched mutations:", AutoSize = true, Padding = new Padding(0, 4, 8, 0) });
        mutationCount.Width = 80; counters.Controls.Add(mutationCount); results.Controls.Add(counters);
        page.Controls.Add(results, 0, 5); page.SetColumnSpan(results, 2);
        var note = new Label { Text = "Owned test fixture. Duplicate Apply/Recipient controls require a scoped selector. No external data or services.", AutoSize = true, ForeColor = Color.FromArgb(80, 103, 122) };
        page.Controls.Add(note, 0, 6); page.SetColumnSpan(note, 2);
        Controls.Add(page); Reset();
        // Verifier helpers launch hidden. This fixture explicitly opts into a visible
        // owned test surface after that first startup show command is consumed.
        Shown += (_, _) => BeginInvoke(new System.Action(() => { ShowWindow(Handle, 5); Activate(); }));
    }

    private GroupBox AddressSection(string section, TextBox input, TextBox result, System.Action apply)
    {
        var group = new GroupBox { Name = section + "Section", AccessibleName = section, Text = section, Dock = DockStyle.Fill, Padding = new Padding(16), Margin = new Padding(0, 0, 12, 8) };
        var contents = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        contents.Controls.Add(new Label { Text = "Recipient", AutoSize = true }); input.Width = 400; contents.Controls.Add(input);
        var button = Action("ApplyAction", "Apply"); button.Click += (_, _) => apply(); contents.Controls.Add(button);
        result.Width = 400; contents.Controls.Add(result); group.Controls.Add(contents); return group;
    }
    private void RebuildPriorities()
    {
        foreach (Control control in priorities.Controls.Cast<Control>().ToArray()) control.Dispose();
        var names = generation % 2 == 0 ? new[] { "Standard", "Express" } : new[] { "Express", "Standard" };
        foreach (string choice in names)
        {
            var button = Action("Priority" + choice, choice);
            button.Click += (_, _) => { priorityStatus.Text = "Priority: " + choice; Mutated(); };
            priorities.Controls.Add(button);
        }
        generationStatus.Text = "Generation " + generation;
    }
    private void ShowConfirmation()
    {
        if (!submit.Enabled || !int.TryParse(quantity.Text, out int amount)) return;
        using var dialog = new Form { Name = "ConfirmOrderDialog", AccessibleName = "Order confirmation", Text = "Confirm order", ClientSize = new Size(480, 220), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, Font = Font, BackColor = BackColor };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), FlowDirection = FlowDirection.TopDown };
        var summary = Readout("ConfirmationText", $"Confirm {amount} units for {shippingName.Text.Trim()}"); summary.Width = 420;
        var confirm = Action("ConfirmAction", "Confirm order"); var cancel = Action("CancelConfirmation", "Cancel");
        confirm.Click += (_, _) =>
        {
            var total = amount * 25 + (defect.Checked ? 1 : 0);
            status.Text = $"Order confirmed: {amount} units; total ${total.ToString("F2", CultureInfo.InvariantCulture)}.";
            Mutated(); dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        layout.Controls.AddRange([summary, confirm, cancel]); dialog.Controls.Add(layout); dialog.ShowDialog(this);
    }
    private void Mutated() { mutations++; mutationCount.Text = mutations.ToString(CultureInfo.InvariantCulture); }
    private void UpdateSubmit() => submit.Enabled = shippingApplied && billingApplied && int.TryParse(quantity.Text, out int amount) && amount is >= 1 and <= 100;
    private void Reset()
    {
        operationGeneration++; shippingApplied = false; billingApplied = false; shippingName.Clear(); billingName.Clear(); quantity.Text = "1";
        shippingStatus.Text = "Shipping not applied"; billingStatus.Text = "Billing not applied"; defect.Checked = false;
        mutations = 0; mutationCount.Text = "0"; generation = 0; RebuildPriorities(); priorityStatus.Text = "Priority: none";
        status.Text = "Ready for an order."; inventory.Enabled = true; UpdateSubmit();
    }
    private static TextBox Input(string id, string label) => new() { Name = id, AccessibleName = label, Width = 280, Margin = new Padding(0, 4, 0, 8) };
    private static TextBox Readout(string id, string value) => new() { Name = id, AccessibleName = id, Text = value, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(243, 246, 249), Width = 350, TabStop = false, Margin = new Padding(0, 5, 0, 3) };
    private static Button Action(string id, string text) => new() { Name = id, AccessibleName = text, Text = text, AutoSize = true, MinimumSize = new Size(120, 34), Margin = new Padding(0, 2, 10, 6) };
}
