using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Testy.Cli.Mcp;

internal sealed class McpImage
{
    public byte[] Png { get; init; } = [];
    public int Width { get; init; }
    public int Height { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    /// <summary>Returned pixels per source pixel; divide image coordinates by this to obtain coordinateClick x/y.</summary>
    public double Scale => SourceWidth == 0 ? 1 : (double)Width / SourceWidth;
}

/// <summary>PNG evidence for tool results: the original file, scaled down proportionally when wider than the requested maximum.</summary>
internal static class McpImages
{
    public static McpImage Load(string path, int maxWidth)
    {
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        if (width <= maxWidth) return new McpImage { Png = bytes, Width = width, Height = height, SourceWidth = width, SourceHeight = height };
        var scale = (double)maxWidth / width;
        var scaled = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var output = new MemoryStream();
        encoder.Save(output);
        return new McpImage { Png = output.ToArray(), Width = scaled.PixelWidth, Height = scaled.PixelHeight, SourceWidth = width, SourceHeight = height };
    }
}
