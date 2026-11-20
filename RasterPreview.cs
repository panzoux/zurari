using SkiaSharp;
using System.Runtime.CompilerServices;
using System.Text;

// Keep the decoder dependency here so previews can fall back to metadata if it cannot load.
internal static class RasterPreview
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static (string[] Lines, int Width) LoadAndRender(
        string filePath, int pixelCols, int pixelRows, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var codec = SKCodec.Create(filePath);
        if (codec == null || codec.EncodedFormat is not (
            SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Gif or
            SKEncodedImageFormat.Bmp or SKEncodedImageFormat.Webp))
            throw new InvalidDataException("Unsupported raster image.");

        var sourceInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var image = SKBitmap.Decode(codec, sourceInfo)
            ?? throw new InvalidDataException("Could not decode image.");
        ct.ThrowIfCancellationRequested();

        double scaleX = pixelCols > 0 ? (double)pixelCols / image.Width : 1.0;
        double scaleY = pixelRows > 0 ? (double)pixelRows / image.Height : 1.0;
        double scale = Math.Min(scaleX, scaleY);
        int tgtW = Math.Max(1, (int)(image.Width * scale));
        int tgtH = Math.Max(2, (int)(image.Height * scale));
        if (tgtH % 2 != 0) tgtH--;

        var targetInfo = new SKImageInfo(tgtW, tgtH, sourceInfo.ColorType, sourceInfo.AlphaType);
        using var resized = image.Resize(targetInfo, new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidDataException("Could not resize image.");
        ct.ThrowIfCancellationRequested();
        return (RenderPixelLines(resized), tgtW);
    }

    internal static string[] RenderPixelLines(SKBitmap image)
    {
        int termRows = image.Height / 2;
        string[] lines = new string[termRows];
        var sb = new StringBuilder(image.Width * 42);

        for (int row = 0; row < termRows; row++)
        {
            sb.Clear();
            int upperY = row * 2;
            int lowerY = upperY + 1;

            for (int x = 0; x < image.Width; x++)
            {
                SKColor up = image.GetPixel(x, upperY);
                SKColor lo = image.GetPixel(x, lowerY);

                byte upR = (byte)(up.Red * up.Alpha / 255);
                byte upG = (byte)(up.Green * up.Alpha / 255);
                byte upB = (byte)(up.Blue * up.Alpha / 255);
                byte loR = (byte)(lo.Red * lo.Alpha / 255);
                byte loG = (byte)(lo.Green * lo.Alpha / 255);
                byte loB = (byte)(lo.Blue * lo.Alpha / 255);

                sb.Append("\x1b[48;2;");
                sb.Append(upR); sb.Append(';');
                sb.Append(upG); sb.Append(';');
                sb.Append(upB); sb.Append('m');
                sb.Append("\x1b[38;2;");
                sb.Append(loR); sb.Append(';');
                sb.Append(loG); sb.Append(';');
                sb.Append(loB); sb.Append('m');
                sb.Append('▄');
            }

            sb.Append("\x1b[0m");
            lines[row] = sb.ToString();
        }

        return lines;
    }
}
