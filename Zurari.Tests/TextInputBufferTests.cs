using Xunit;

public class TextInputBufferTests
{
    [Fact]
    public void InsertChar_AtEnd_AppendsAndAdvancesCursor()
    {
        var (buf, pos) = TextInputBuffer.Insert("abc", 3, 'd');
        Assert.Equal("abcd", buf);
        Assert.Equal(4, pos);
    }

    [Fact]
    public void InsertChar_AtMiddle_SplicesAndAdvancesCursor()
    {
        var (buf, pos) = TextInputBuffer.Insert("ac", 1, 'b');
        Assert.Equal("abc", buf);
        Assert.Equal(2, pos);
    }

    [Fact]
    public void InsertChar_CJK_CountsAsSingleCodeUnitForCursor()
    {
        // '漢' は1 UTF-16コードユニット、表示幅は2だがカーソル位置(文字インデックス)は1つ分進む
        var (buf, pos) = TextInputBuffer.Insert("A", 1, '漢');
        Assert.Equal("A漢", buf);
        Assert.Equal(2, pos);
    }

    [Fact]
    public void Backspace_AtEnd_RemovesLastChar()
    {
        var (buf, pos) = TextInputBuffer.Backspace("abc", 3);
        Assert.Equal("ab", buf);
        Assert.Equal(2, pos);
    }

    [Fact]
    public void Backspace_AtStart_NoOp()
    {
        var (buf, pos) = TextInputBuffer.Backspace("abc", 0);
        Assert.Equal("abc", buf);
        Assert.Equal(0, pos);
    }

    [Fact]
    public void Backspace_AtMiddle_RemovesCharBeforeCursor()
    {
        var (buf, pos) = TextInputBuffer.Backspace("漢字test", 2);
        Assert.Equal("漢test", buf);
        Assert.Equal(1, pos);
    }

    [Fact]
    public void Delete_AtMiddle_RemovesCharAtCursor()
    {
        var (buf, pos) = TextInputBuffer.Delete("abc", 1);
        Assert.Equal("ac", buf);
        Assert.Equal(1, pos); // カーソル位置自体は動かない
    }

    [Fact]
    public void Delete_AtEnd_NoOp()
    {
        var (buf, pos) = TextInputBuffer.Delete("abc", 3);
        Assert.Equal("abc", buf);
        Assert.Equal(3, pos);
    }

    [Fact]
    public void MoveLeft_DecrementsCursor()
    {
        Assert.Equal(2, TextInputBuffer.MoveLeft("abc", 3));
    }

    [Fact]
    public void MoveLeft_AtStart_ClampsToZero()
    {
        Assert.Equal(0, TextInputBuffer.MoveLeft("abc", 0));
    }

    [Fact]
    public void MoveRight_IncrementsCursor()
    {
        Assert.Equal(2, TextInputBuffer.MoveRight("abc", 1));
    }

    [Fact]
    public void MoveRight_AtEnd_ClampsToLength()
    {
        Assert.Equal(3, TextInputBuffer.MoveRight("abc", 3));
    }

    [Fact]
    public void Home_ReturnsZero()
    {
        Assert.Equal(0, TextInputBuffer.Home("abc", 2));
    }

    [Fact]
    public void End_ReturnsLength()
    {
        Assert.Equal(3, TextInputBuffer.End("abc", 1));
    }

    // ── CJK混在時の表示幅計算(カーソルの「画面上の列位置」算出) ──────────

    [Fact]
    public void DisplayColumnAtCursor_AsciiOnly_EqualsCursorPos()
    {
        Assert.Equal(3, TextInputBuffer.DisplayColumnAtCursor("abc", 3));
    }

    [Fact]
    public void DisplayColumnAtCursor_CJKBeforeCursor_CountsDoubleWidth()
    {
        // "漢字abc" でカーソルが 'a' の直前(index=2)にあるとき、表示列は 2*2=4
        Assert.Equal(4, TextInputBuffer.DisplayColumnAtCursor("漢字abc", 2));
    }

    [Fact]
    public void DisplayColumnAtCursor_MixedContent_CountsPerCharWidth()
    {
        // "a漢b" でカーソルが末尾(index=3)のとき、表示列は 1(a) + 2(漢) + 1(b) = 4
        Assert.Equal(4, TextInputBuffer.DisplayColumnAtCursor("a漢b", 3));
    }

    [Fact]
    public void DisplayColumnAtCursor_CursorAtStart_ReturnsZero()
    {
        Assert.Equal(0, TextInputBuffer.DisplayColumnAtCursor("漢字", 0));
    }
}
