using Xunit;
using SkiaSharp;

public class ImagePreviewTests
{
    [Fact]
    public void RenderPixelLines_2x2Image_ProducesCorrectAnsiEscapes()
    {
        // Arrange: 2x2 opaque image — upper row red, lower row blue
        using var image = new SKBitmap(2, 2);
        image.SetPixel(0, 0, SKColors.Red);
        image.SetPixel(1, 0, SKColors.Red);
        image.SetPixel(0, 1, SKColors.Blue);
        image.SetPixel(1, 1, SKColors.Blue);

        // Act: tgtW=2, tgtH=2 → 1 terminal row
        string[] lines = RasterPreview.RenderPixelLines(image);

        // Assert
        Assert.Single(lines);
        string expected =
            "\x1b[48;2;255;0;0m\x1b[38;2;0;0;255m▄" +
            "\x1b[48;2;255;0;0m\x1b[38;2;0;0;255m▄" +
            "\x1b[0m";
        Assert.Equal(expected, lines[0]);
    }

    [Fact]
    public void RenderPixelLines_AlphaBlend_MultipliesAgainstBlack()
    {
        // Arrange: 1x2 image — upper pixel 50% transparent red → blends to (127,0,0)
        using var image = new SKBitmap(1, 2);
        image.SetPixel(0, 0, new SKColor(255, 0, 0, 127));
        image.SetPixel(0, 1, SKColors.Lime);

        string[] lines = RasterPreview.RenderPixelLines(image);

        Assert.Single(lines);
        // upR = 255 * 127 / 255 = 127; loG = 255 * 255 / 255 = 255
        Assert.Contains("\x1b[48;2;127;0;0m", lines[0]); // background (upper pixel blended)
        Assert.Contains("\x1b[38;2;0;255;0m", lines[0]); // foreground (lower pixel opaque)
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "PNG Image")]
    [InlineData(SKEncodedImageFormat.Jpeg, "JPEG Image")]
    [InlineData(SKEncodedImageFormat.Webp, "WebP Image")]
    public async Task LoadImagePreviewAsync_RasterImage_RendersPixels(SKEncodedImageFormat format, string label)
    {
        string path = Path.Combine(Path.GetTempPath(), $"zrr-preview-{Guid.NewGuid():N}.{format}");
        try
        {
            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.Red);
            using var encoded = bitmap.Encode(format, 100);
            File.WriteAllBytes(path, encoded.ToArray());

            var type = new FileType(FileCategory.Image, label);
            var preview = await PreviewLoader.LoadImagePreviewAsync(path, type, "", 20, 10, CancellationToken.None);

            Assert.NotNull(preview.PixelLines);
            Assert.NotEmpty(preview.PixelLines);
            Assert.True(preview.PixelWidth > 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadImagePreviewAsync_SvgLabel_ReturnsMetaWithoutPixelLines()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "test_img_preview.svg");
        File.WriteAllText(tempPath, "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'/>");
        try
        {
            var fileType = new FileType(FileCategory.Image, "SVG Image", "image/svg+xml");
            var result = await PreviewLoader.LoadImagePreviewAsync(
                tempPath, fileType, "2024-01-01 00:00", 80, 20, CancellationToken.None);

            Assert.Null(result.PixelLines);
            Assert.Equal(0, result.PixelWidth);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public async Task LoadImagePreviewAsync_IconLabel_ReturnsMetaWithoutPixelLines()
    {
        // ICO files are excluded from pixel rendering (same guard as SVG)
        string tempPath = Path.Combine(Path.GetTempPath(), "test_img_preview.ico");
        // Minimal fake ICO — LoadImageMeta will fail gracefully; we only need the type guard
        File.WriteAllBytes(tempPath, [0x00, 0x00, 0x01, 0x00, 0x00, 0x00]);
        try
        {
            var fileType = new FileType(FileCategory.Image, "Icon", "image/x-icon");
            var result = await PreviewLoader.LoadImagePreviewAsync(
                tempPath, fileType, "2024-01-01 00:00", 80, 20, CancellationToken.None);

            Assert.Null(result.PixelLines);
            Assert.Equal(0, result.PixelWidth);
        }
        finally { File.Delete(tempPath); }
    }
}
