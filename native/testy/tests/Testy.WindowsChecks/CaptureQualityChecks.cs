using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testy.Core;
using Testy.Windows;

internal static class CaptureQualityChecks
{
    public static void Run(string reportPath)
    {
        var checks = new List<object>();
        var startedAt = DateTimeOffset.UtcNow;
        bool passed = false; string error = "";
        try
        {
            foreach (var fixture in new[] { (Name: "black-client-framed.png", Reject: true), (Name: "readable-client.png", Reject: false) })
            {
                string resource = Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(n => n.EndsWith(fixture.Name, StringComparison.Ordinal));
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
                var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                var image = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
                var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                bool rejected = false; string reason = "";
                // Both preserved fixture images are real 1000x730 TestLab captures at 100% scale.
                try { WindowCaptureValidator.ValidateClientPixels(pixels, image.PixelWidth, image.PixelHeight, 8, 31, 984, 691); }
                catch (InvalidOperationException ex) { rejected = true; reason = ex.Message; }
                if (rejected != fixture.Reject) throw new Exception($"Capture regression {fixture.Name}: expected rejected={fixture.Reject}, observed {rejected}.");
                checks.Add(new { fixture = fixture.Name, passed = true, rejected, reason });
            }
            passed = true;
        }
        catch (Exception ex) { error = ex.ToString(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        string json = JsonSerializer.Serialize(new { passed, startedAt, finishedAt = DateTimeOffset.UtcNow, error, checks }, TestyJson.Options);
        File.WriteAllText(reportPath, json); Console.WriteLine(json);
        if (!passed) throw new InvalidOperationException("Capture quality regression failed; inspect " + reportPath);
    }
}
