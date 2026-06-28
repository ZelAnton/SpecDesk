using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class ImageInsertAdapterTests
{
    private readonly List<string> _dirs = [];

    private string TempRepo()
    {
        string dir = Path.Combine(Path.GetTempPath(), "specdesk-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    [TearDown]
    public void TearDown()
    {
        foreach (string dir in _dirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        _dirs.Clear();
    }

    private static byte[] OnePixelPng()
    {
        using SKBitmap bitmap = new(1, 1);
        bitmap.SetPixel(0, 0, SKColors.Teal);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Test]
    public void Insert_WithAValidImage_ReturnsMarkdownAndWritesAFileInTheRepo()
    {
        string repoRoot = TempRepo();
        string docPath = Path.Combine(repoRoot, "doc.md");
        File.WriteAllText(docPath, "# Doc");
        ImageInsertAdapter adapter = new(NullLogger.Instance);

        string? markdown = adapter.Insert(repoRoot, docPath, OnePixelPng(), "diagram.png", "image/png");

        Assert.That(markdown, Is.Not.Null);
        Assert.That(markdown, Does.StartWith("![").And.Contain("]("));
        Assert.That(
            Directory.EnumerateFiles(repoRoot, "*.*", SearchOption.AllDirectories)
                .Any(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)),
            Is.True,
            "the engine should have written the processed image somewhere under the repo");
    }

    [Test]
    public void Insert_WithBytesThatAreNotAnImage_ReturnsNullWithoutThrowing()
    {
        string repoRoot = TempRepo();
        string docPath = Path.Combine(repoRoot, "doc.md");
        File.WriteAllText(docPath, "# Doc");
        ImageInsertAdapter adapter = new(NullLogger.Instance);

        string? markdown = "unset";
        Assert.DoesNotThrow(() =>
            markdown = adapter.Insert(repoRoot, docPath, [1, 2, 3], "note.bin", "application/octet-stream"));
        Assert.That(markdown, Is.Null);
    }
}
