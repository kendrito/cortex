namespace Testy.Windows;

/// <summary>Rejects blank client content independently of a successfully painted system frame.</summary>
internal static class WindowCaptureValidator
{
    internal static void ValidateClientPixels(byte[] pixels, int width, int height, int clientX, int clientY, int clientWidth, int clientHeight)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4)) throw new ArgumentException("Capture pixel dimensions are invalid.");
        int left = Math.Clamp(clientX, 0, width), top = Math.Clamp(clientY, 0, height);
        int right = (int)Math.Clamp((long)clientX + clientWidth, 0, width), bottom = (int)Math.Clamp((long)clientY + clientHeight, 0, height);
        if (right - left < 4 || bottom - top < 4) throw new InvalidOperationException("Target capture has no usable client area.");
        // Exclude a few border pixels. Windows may paint those even when the app client is black.
        int inset = Math.Min(3, Math.Min(right - left, bottom - top) / 10);
        left += inset; right -= inset; top += inset; bottom -= inset;
        int ColorAt(int x, int y)
        {
            int i = (y * width + x) * 4; return pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16;
        }
        // A representative background prevents a stray corner pixel from disguising an otherwise blank surface.
        var samples = new Dictionary<int, int>();
        for (int row = 1; row <= 3; row++)
            for (int column = 1; column <= 3; column++)
            {
                int color = ColorAt(left + (right - left - 1) * column / 4, top + (bottom - top - 1) * row / 4);
                samples[color] = samples.GetValueOrDefault(color) + 1;
            }
        int background = samples.MaxBy(pair => pair.Value).Key;
        int blue = background & 255, green = (background >> 8) & 255, red = (background >> 16) & 255;
        long area = (long)(right - left) * (bottom - top);
        int requiredVariation = (int)Math.Clamp(area / 10000, 16, 256), variation = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int i = (y * width + x) * 4;
                if (Math.Abs(pixels[i] - blue) >= 8 || Math.Abs(pixels[i + 1] - green) >= 8 || Math.Abs(pixels[i + 2] - red) >= 8)
                    if (++variation >= requiredVariation) return;
            }
        throw new InvalidOperationException("Target capture returned a blank or nearly uniform CLIENT AREA. A visible title bar is not valid application evidence.");
    }
}
