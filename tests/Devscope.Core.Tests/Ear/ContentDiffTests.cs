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
    public void Build_ModifiedAndInsertedLines_CountsLikeGit()
    {
        // b=2 → b=20 counts as one deletion and one addition; d=4 is one more addition.
        var left = Encoding.UTF8.GetBytes("a=1\nb=2\nc=3\n");
        var right = Encoding.UTF8.GetBytes("a=1\nb=20\nc=3\nd=4\n");

        var result = ContentDiff.Build("config.properties", left, right);

        Assert.Equal(new DiffStats(Additions: 2, Deletions: 1), result.Stats);
        Assert.Equal("2 additions, 1 deletion", result.Summary);
        Assert.Equal([DiffStatus.Added, DiffStatus.Added, DiffStatus.Added, DiffStatus.Removed, DiffStatus.Removed], result.Stats.Blocks);
    }

    [Fact]
    public void BuildText_ModifiedLine_HighlightsOnlyTheChangedWords()
    {
        var result = ContentDiff.BuildText("    private int retries = 3;\n", "    private int retries = 5;\n", summaryPrefix: null);

        var line = Assert.Single(result.Lines, l => l.LeftKind == DiffLineKind.Modified);
        Assert.Equal(line.LeftText, string.Concat(line.LeftSpans!.Select(s => s.Text)));
        Assert.Equal(line.RightText, string.Concat(line.RightSpans!.Select(s => s.Text)));
        Assert.Equal(["3"], line.LeftSpans!.Where(s => s.IsChanged).Select(s => s.Text.Trim(';')));
        Assert.Equal(["5"], line.RightSpans!.Where(s => s.IsChanged).Select(s => s.Text.Trim(';')));
    }

    [Fact]
    public void BuildText_InsertedAndUnchangedLines_HaveNoWordSpans()
    {
        var result = ContentDiff.BuildText("a\n", "a\nb\n", summaryPrefix: null);

        Assert.All(result.Lines, l => Assert.Null(l.LeftSpans));
        Assert.All(result.Lines, l => Assert.Null(l.RightSpans));
    }

    [Theory]
    [InlineData(10, 0, 5, 0)]
    [InlineData(0, 3, 0, 5)]
    [InlineData(200, 1, 4, 1)]
    [InlineData(1, 200, 1, 4)]
    [InlineData(0, 0, 0, 0)]
    public void Blocks_AlwaysFiveSquaresAndEachKindThatOccursIsVisible(int additions, int deletions, int green, int red)
    {
        var blocks = new DiffStats(additions, deletions).Blocks;

        Assert.Equal(DiffStats.BlockCount, blocks.Count);
        Assert.Equal(green, blocks.Count(b => b == DiffStatus.Added));
        Assert.Equal(red, blocks.Count(b => b == DiffStatus.Removed));
    }

    [Fact]
    public void Build_IdenticalText_HasNoChanges()
    {
        var text = Encoding.UTF8.GetBytes("a=1\n");

        var result = ContentDiff.Build("a.properties", text, text);

        Assert.False(result.Stats.HasChanges);
        Assert.Equal("Identical text", result.Summary);
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
