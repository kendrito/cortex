using System.Text.Json;
using System.Windows.Automation;
using Testy.Core;

namespace Testy.Windows;

/// <summary>Dispatches OpenAI computer actions only into the process and window of the last target screenshot.</summary>
public sealed class NativeComputerActionExecutor(ITargetDriver driver) : IComputerActionExecutor, INativeActionReceiptSource
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public NativeActionReceipt? LastReceipt { get; private set; }
    public async Task ExecuteAsync(JsonElement action, CancellationToken cancellationToken = default)
    {
        LastReceipt = null;
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("A native computer action is already in progress.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20)); cancellationToken = timeout.Token;
        try
        {
            if (action.ValueKind != JsonValueKind.Object) throw new ArgumentException("A computer action must be a JSON object.");
            string type = String(action, "type");
            var target = driver.Target ?? throw new InvalidOperationException("Attach a target before executing computer actions.");
            Native.Validate(target); cancellationToken.ThrowIfCancellationRequested();
            if (type == "screenshot") return; // The agent loop captures new evidence after every action.
            if (type == "wait")
            {
                int delay = action.TryGetProperty("milliseconds", out var ms) ? Number(ms, "milliseconds") : 1000;
                if (delay is < 0 or > 5000) throw new ArgumentOutOfRangeException(nameof(action), "Wait must be between 0 and 5000 ms.");
                await Task.Delay(delay, cancellationToken); return;
            }
            if (type is not ("click" or "double_click" or "move" or "scroll" or "drag" or "type" or "keypress")) throw new NotSupportedException($"Unsupported computer action '{type}'.");
            // Pin coordinates to the last captured HWND. A newly opened modal or resized surface requires new evidence.
            nint window = Native.CapturedWindow(target);
            if (type is "click" or "double_click" or "move" or "scroll")
                Native.InputPoint(target, window, Integer(action, "x"), Integer(action, "y"));
            Native.Foreground(target, window, cancellationToken);
            void Ensure()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(driver.Target, target)) throw new InvalidOperationException("The driver attachment changed during the action.");
                Native.RequireFocus(target, window, true);
            }
            switch (type)
            {
                case "click":
                case "double_click":
                    string button = action.TryGetProperty("button", out var buttonJson) ? buttonJson.GetString() ?? "left" : "left";
                    if (button is not ("left" or "right" or "middle" or "wheel")) throw new ArgumentException($"Unsupported mouse button '{button}'.");
                    int clickX = Integer(action, "x"), clickY = Integer(action, "y");
                    Ensure(); Native.Move(target, window, clickX, clickY);
                    await using (var receipt = await NativeInputReceipt.ForMouseAsync(target, window, clickX, clickY, button, cancellationToken))
                    {
                        Ensure(); Native.VerifyPointer(target, window, clickX, clickY);
                        Native.MouseButton(target, window, button, type == "double_click");
                        await receipt.ConfirmAsync(cancellationToken); LastReceipt = receipt.Receipt;
                    }
                    break;
                case "move":
                    Ensure(); Native.Move(target, window, Integer(action, "x"), Integer(action, "y")); break;
                case "scroll":
                    int sx = Integer(action, "scroll_x"), sy = Integer(action, "scroll_y");
                    if (Math.Abs((long)sx) > 10000 || Math.Abs((long)sy) > 10000) throw new ArgumentOutOfRangeException(nameof(action), "Scroll magnitude exceeds 10000 pixels.");
                    Ensure(); Native.Move(target, window, Integer(action, "x"), Integer(action, "y")); Ensure();
                    // Windows wheel events are not exact pixel scrolling. Positive protocol Y means down.
                    var wheel = new List<Native.INPUT>();
                    if (sy != 0) wheel.Add(Native.MouseInput(0x0800, -Wheel(sy)));
                    if (sx != 0) wheel.Add(Native.MouseInput(0x1000, Wheel(sx)));
                    if (wheel.Count > 0) Native.Send(wheel.ToArray()); break;
                case "drag":
                    if (!action.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array || path.GetArrayLength() is < 2 or > 128) throw new ArgumentException("Drag requires 2–128 points.");
                    var points = path.EnumerateArray().Select(p => (X: Integer(p, "x"), Y: Integer(p, "y"))).ToArray();
                    foreach (var point in points) Native.InputPoint(target, window, point.X, point.Y);
                    double distance = points.Zip(points.Skip(1), (a, b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2))).Sum();
                    if (distance > 16000) throw new ArgumentException("Drag path exceeds the 16000 pixel travel limit.");
                    bool held = false;
                    try
                    {
                        Ensure(); Native.Move(target, window, points[0].X, points[0].Y); Native.Send([Native.MouseInput(0x0002)]); held = true;
                        for (int i = 1; i < points.Length; i++)
                        {
                            var a = points[i - 1]; var b = points[i]; int count = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)) / 16.0));
                            for (int j = 1; j <= count; j++)
                            {
                                Ensure(); Native.Move(target, window, a.X + (int)Math.Round((b.X - a.X) * j / (double)count), a.Y + (int)Math.Round((b.Y - a.Y) * j / (double)count));
                                await Task.Delay(5, cancellationToken);
                            }
                        }
                    }
                    finally { if (held) Native.Send([Native.MouseInput(0x0004)]); }
                    break;
                case "type":
                    string text = String(action, "text");
                    if (text.Length > 8192) throw new ArgumentException("Typed text exceeds 8192 UTF-16 code units.");
                    await using (var receipt = await NativeInputReceipt.ForKeyboardAsync(target, cancellationToken))
                    {
                    for (int i = 0; i < text.Length; i += 64)
                    {
                        Ensure(); await receipt.VerifyFocusedAsync(target, cancellationToken); Ensure();
                        // Unicode packet events avoid clipboard access and preserve non-ASCII input.
                        var inputs = text.Skip(i).Take(64).SelectMany(c => new[]
                        {
                            new Native.INPUT { Type = 1, Data = new() { Keyboard = new() { Scan = c, Flags = 0x0004 } } },
                            new Native.INPUT { Type = 1, Data = new() { Keyboard = new() { Scan = c, Flags = 0x0006 } } }
                        }).ToArray();
                        Native.Send(inputs); await Task.Delay(5, cancellationToken);
                    }
                    await receipt.ConfirmAsync(cancellationToken); LastReceipt = receipt.Receipt;
                    }
                    break;
                case "keypress":
                    if (!action.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() is < 1 or > 4) throw new ArgumentException("keypress requires 1–4 key names.");
                    string[] names = keys.EnumerateArray().Select(k => k.ValueKind == JsonValueKind.String ? k.GetString()! : throw new ArgumentException("Each key must be a string.")).ToArray();
                    if (names.Any(k => k.Contains('+'))) throw new ArgumentException("Supply chord keys as separate array entries.");
                    await using (var receipt = await NativeInputReceipt.ForKeyboardAsync(target, cancellationToken))
                    {
                        Ensure(); await receipt.VerifyFocusedAsync(target, cancellationToken); Native.Press(target, string.Join('+', names), window, cancellationToken, true);
                        await receipt.ConfirmAsync(cancellationToken); LastReceipt = receipt.Receipt;
                    }
                    break;
            }
        }
        finally { gate.Release(); }
    }
    private static int Wheel(int pixels) => Math.Sign(pixels) * (int)Math.Ceiling(Math.Abs(pixels) / 100.0) * 120;
    private static string String(JsonElement action, string field) => action.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : throw new ArgumentException($"Computer action requires string '{field}'.");
    private static int Integer(JsonElement action, string field) => action.TryGetProperty(field, out var property) ? Number(property, field) : throw new ArgumentException($"Computer action requires integer '{field}'.");
    private static int Number(JsonElement value, string field) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : throw new ArgumentException($"Computer action requires integer '{field}'.");
}
