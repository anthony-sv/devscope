using System.Data.SqlTypes;
using Devscope.Core.Database;

namespace Devscope.Core.Tests.Database;

public sealed class DbValuesTests
{
    [Theory]
    [InlineData("a", "a  ", 0)] // trailing spaces are padding, as in SQL Server
    [InlineData("a", "a\t", 1)] // 'a' pads to 'a ', and a space sorts after a tab
    [InlineData("a", "B", 1)] // binary order: lowercase after uppercase
    [InlineData("ab", "abc", -1)]
    public void CompareKeys_Text_LikeABinaryCollation(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(DbValues.CompareKeys(a, b)));

    [Theory]
    [InlineData(new byte[] { 1 }, new byte[] { 1, 0 }, 0)] // shorter value padded with zero bytes, as in SQL Server
    [InlineData(new byte[] { 1 }, new byte[] { 1, 1 }, -1)]
    [InlineData(new byte[0], new byte[] { 0 }, 0)]
    [InlineData(new byte[] { 2 }, new byte[] { 1, 9 }, 1)]
    public void CompareKeys_Binary_PadsWithZeros(byte[] a, byte[] b, int expected)
    {
        Assert.Equal(expected, Math.Sign(DbValues.CompareKeys(a, b)));
        Assert.Equal(expected == 0, DbValues.CanonicalKey(a) == DbValues.CanonicalKey(b));
    }

    [Fact]
    public void CompareKeys_NullFirst_AndNumbersAcrossWidths()
    {
        Assert.True(DbValues.CompareKeys(null, 0) < 0);
        Assert.Equal(0, DbValues.CompareKeys(5, 5L));
        Assert.True(DbValues.CompareKeys(2, 10m) < 0);
        Assert.Equal(0, DbValues.CompareKeys(new SqlDecimal(7), 7));
    }

    [Fact]
    public void CompareKeys_Guids_InSqlServerOrder()
    {
        // SQL Server orders uniqueidentifier by its last six bytes first.
        var low = Guid.Parse("ffffffff-ffff-ffff-ffff-000000000001");
        var high = Guid.Parse("00000000-0000-0000-0000-000000000002");

        Assert.True(DbValues.CompareKeys(low, high) < 0);
    }

    [Fact]
    public void AreEqual_ExactTextButNumbersByValue()
    {
        Assert.False(DbValues.AreEqual("abc", "ABC"));
        Assert.False(DbValues.AreEqual("abc", "abc "));
        Assert.True(DbValues.AreEqual(5, 5.00m));
        Assert.True(DbValues.AreEqual(2.5d, 2.5m));
        Assert.True(DbValues.AreEqual(new byte[] { 1, 2 }, new byte[] { 1, 2 }));
        Assert.False(DbValues.AreEqual(null, string.Empty));
        Assert.False(DbValues.AreEqual(
            new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(1)),
            new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Canonical_EqualNumbersHaveTheSameText()
    {
        Assert.Equal(DbValues.Canonical(5), DbValues.Canonical(5.000m));
        Assert.Equal(DbValues.Canonical(5), DbValues.Canonical(5.0d));
        Assert.Equal(DbValues.Canonical(0.5m), DbValues.Canonical(0.5d));
        Assert.NotEqual(DbValues.Canonical(5), DbValues.Canonical("5"));
        Assert.Equal(DbValues.CanonicalKey("a  "), DbValues.CanonicalKey("a"));
    }

    [Fact]
    public void ToSqlLiteral_PerType()
    {
        var at = new DateTime(2026, 1, 31, 10, 5, 0, 123);

        Assert.Equal("N'it''s'", DbValues.ToSqlLiteral("it's"));
        Assert.Equal("NULL", DbValues.ToSqlLiteral(null));
        Assert.Equal("1", DbValues.ToSqlLiteral(true));
        Assert.Equal("12.50", DbValues.ToSqlLiteral(12.50m));
        Assert.Equal("0x0A0B", DbValues.ToSqlLiteral(new byte[] { 10, 11 }));
        Assert.Equal("'2026-01-31T10:05:00.123'", DbValues.ToSqlLiteral(at, "datetime"));
        Assert.Equal("'2026-01-31T10:05:00.1230000'", DbValues.ToSqlLiteral(at, "datetime2"));
        Assert.Equal("'2026-01-31'", DbValues.ToSqlLiteral(at.Date, "date"));
    }

    [Fact]
    public void Format_ForDisplay()
    {
        Assert.Equal("'line1\\nline2'", DbValues.Format("line1\nline2"));
        Assert.Equal("NULL", DbValues.Format(null));
        Assert.Equal("2026-01-31", DbValues.Format(new DateTime(2026, 1, 31)));
        Assert.Equal("'abc…", DbValues.Format("abcdef", maxLength: 4));
    }
}
