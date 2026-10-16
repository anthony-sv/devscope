using System.Text;
using Devscope.Core.Ear;

namespace Devscope.Core.Tests.Ear;

public sealed class ContentDiffTests
{
    [Fact]
    public void Build_TextFiles_ProducesAlignedSideBySideLines()
    {
        var left = Encoding.UTF8.GetBytes("a=1\nb=2\nc=3\n");
        var right = Encoding.UTF8.GetBytes("a=1\nb=20\nc=3\nd=4\n");

        var result = ContentDiff.Build("config.properties", left, right);

        Assert.True(result.IsText);
        Assert.Contains(result.Lines, l => l.LeftText == "b=2" && l.RightText == "b=20");
        Assert.Contains(result.Lines, l => l.RightText == "d=4" && l.RightKind == DiffLineKind.Inserted);
        Assert.Contains(result.Lines, l => l.LeftText == "a=1" && !l.IsChange);
    }

    [Fact]
    public void Build_FileOnlyOnRight_ShowsAllLinesInserted()
    {
        var result = ContentDiff.Build("new.xml", null, Encoding.UTF8.GetBytes("<a/>\n<b/>"));

        Assert.True(result.IsText);
        Assert.All(result.Lines, l => Assert.Equal(DiffLineKind.Inserted, l.RightKind));
    }

    [Fact]
    public void Build_ClassFiles_SummarizesJavaVersion()
    {
        // CAFEBABE, minor 0, major 61 (Java 17)
        byte[] java17 = [0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 61, 0, 1];
        byte[] java21 = [0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 65, 0, 1];

        var result = ContentDiff.Build("com/acme/A.class", java17, java21);

        Assert.False(result.IsText);
        Assert.Contains("Java 17", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Java 21", result.Summary, StringComparison.Ordinal);
        Assert.Contains("differs", result.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("web.xml", true)]
    [InlineData("README", true)]
    [InlineData("image.bin", false)]
    public void IsText_UsesExtensionThenNulBytes(string path, bool expected)
    {
        var content = path == "image.bin" ? new byte[] { 1, 0, 2 } : Encoding.UTF8.GetBytes("plain text");

        Assert.Equal(expected, ContentDiff.IsText(path, content));
    }
}
