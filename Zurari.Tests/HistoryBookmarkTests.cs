using System.Text.RegularExpressions;
using Xunit;

// 長い行の切り詰めで SGR が落ちていた回帰。
// ブックマーク一覧で、短い項目は選択行が反転するのに、幅を超える長いパスの行だけ
// 反転しない、という症状として出ていた。
public class FitRowToWidthAnsiTests
{
    static readonly Regex Ansi = new Regex("\u001b\\[[0-9;]*m");
    static string Plain(string s) => Ansi.Replace(s, "");

    const string Inverse = "\u001b[7m";
    const string Reset = "\u001b[0m";

    [Fact]
    public void SelectedRowLongerThanWidth_KeepsItsHighlight()
    {
        string longPath = "> D:\\test\\a_very_looooooooooooong_folder_name\\wooooooooaooooooobooooooooocooow";
        string row = Inverse + longPath + Reset;

        string result = Program.FitRowToWidth(row, 40);

        Assert.Contains(Inverse, result);
        Assert.Contains(Reset, result);
        Assert.Equal(40, CharacterWidth.GetStringWidth(Plain(result)));
    }

    [Fact]
    public void TruncatedRow_StillPadsToExactlyInnerWidth()
    {
        string row = Inverse + new string('x', 200) + Reset;
        string result = Program.FitRowToWidth(row, 31);
        Assert.Equal(31, CharacterWidth.GetStringWidth(Plain(result)));
    }

    [Fact]
    public void TruncatedRow_KeepsHeadAndTailLikeSmartTruncate()
    {
        string row = Inverse + "HEAD" + new string('m', 100) + "TAIL" + Reset;
        string plain = Plain(Program.FitRowToWidth(row, 20));

        Assert.StartsWith("HEAD", plain);
        Assert.Contains("…", plain);
        Assert.Contains("TAIL", plain.TrimEnd());
    }

    [Fact]
    public void EscapesInsideTheDroppedMiddleAreKept()
    {
        // 捨てた区間にリセットがある場合、それを落とすと以降の装飾が漏れる。
        string row = Inverse + new string('a', 60) + Reset + new string('b', 60);
        string result = Program.FitRowToWidth(row, 20);

        Assert.Contains(Inverse, result);
        Assert.Contains(Reset, result);
        Assert.Equal(20, CharacterWidth.GetStringWidth(Plain(result)));
    }

    [Fact]
    public void PlainLongRow_StillTruncatesAsBefore()
    {
        string result = Program.FitRowToWidth(new string('z', 100), 12);
        Assert.Equal(12, CharacterWidth.GetStringWidth(result));
        // char 版を使う。string 版はカルチャ比較で ESC を無視し、常に一致してしまう。
        Assert.False(result.Contains('\u001b'));
    }

    [Fact]
    public void WideCharacters_TruncateToExactDisplayWidth()
    {
        string row = Inverse + new string('あ', 60) + Reset;
        string result = Program.FitRowToWidth(row, 21);
        Assert.Equal(21, CharacterWidth.GetStringWidth(Plain(result)));
    }
}

// 改名に追随したパス書き換え。
public class PathRewriteHelperTests
{
    [Theory]
    [InlineData("D:\\test\\old", "D:\\test\\old", "D:\\test\\new", "D:\\test\\new")]
    [InlineData("D:\\test\\old\\sub", "D:\\test\\old", "D:\\test\\new", "D:\\test\\new\\sub")]
    [InlineData("D:\\test\\other", "D:\\test\\old", "D:\\test\\new", "D:\\test\\other")]
    // セパレータ境界でのみ前方一致 — old の改名が oldish を巻き込まないこと
    [InlineData("D:\\test\\oldish", "D:\\test\\old", "D:\\test\\new", "D:\\test\\oldish")]
    // 末尾セパレータの有無に左右されない
    [InlineData("D:\\test\\old\\", "D:\\test\\old", "D:\\test\\new", "D:\\test\\new")]
    public void Rewrite_RepointsOnlyMatchingPaths(string entry, string oldPath, string newPath, string expected)
    {
        Assert.Equal(expected, PathRewriteHelper.Rewrite(entry, oldPath, newPath));
    }

    [Fact]
    public void Rewrite_IsCaseInsensitive()
    {
        Assert.Equal("D:\\test\\new", PathRewriteHelper.Rewrite("d:\\TEST\\Old", "D:\\test\\old", "D:\\test\\new"));
    }
}

[Collection("GlobalStateTests")]
public class HistoryMaintenanceTests
{
    static void SetHistory(params string[] paths)
    {
        Program.ClearHistoryForTest();
        foreach (string p in paths)
            Program.RecordHistoryVisitForTest(p);
    }

    [Fact]
    public void RemoveHistoryEntry_DropsAMissingPath()
    {
        SetHistory("D:\\a", "D:\\b", "D:\\c");

        Program.RemoveHistoryEntry("D:\\b");

        Assert.Equal(new[] { "D:\\a", "D:\\c" }, Program.HistoryForTest);
    }

    [Fact]
    public void RemoveHistoryEntry_IsCaseInsensitive()
    {
        SetHistory("D:\\Test");
        Program.RemoveHistoryEntry("d:\\test");
        Assert.Empty(Program.HistoryForTest);
    }

    [Fact]
    public void RemoveHistoryEntry_UnknownPath_ChangesNothing()
    {
        SetHistory("D:\\a");
        Program.RemoveHistoryEntry("D:\\zzz");
        Assert.Single(Program.HistoryForTest);
    }

    [Fact]
    public void RewriteHistoryPath_RepointsTheRenamedDirectoryAndItsChildren()
    {
        SetHistory("D:\\test\\old", "D:\\test\\old\\sub", "D:\\other");

        Program.RewriteHistoryPath("D:\\test\\old", "D:\\test\\new");

        Assert.Equal(
            new[] { "D:\\test\\new", "D:\\test\\new\\sub", "D:\\other" },
            Program.HistoryForTest);
    }

    [Fact]
    public void RewriteHistoryPath_CollapsesDuplicatesItCreates()
    {
        // old と new の両方を訪問済みだった場合、改名後は同じパスになるので1件に畳む。
        SetHistory("D:\\test\\new", "D:\\test\\old");

        Program.RewriteHistoryPath("D:\\test\\old", "D:\\test\\new");

        Assert.Single(Program.HistoryForTest);
        Assert.Equal("D:\\test\\new", Program.HistoryForTest[0]);
    }

    [Fact]
    public void RewriteHistoryPath_UnrelatedRename_LeavesHistoryAlone()
    {
        SetHistory("D:\\a", "D:\\b");
        Program.RewriteHistoryPath("D:\\zzz", "D:\\yyy");
        Assert.Equal(new[] { "D:\\a", "D:\\b" }, Program.HistoryForTest);
    }
}

public class BookmarkRewriteTests
{
    static string NewBookmarkFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zrr-bm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "bookmarks.txt");
    }

    [Fact]
    public void Rewrite_RepointsBookmarksInsteadOfDeletingThem()
    {
        string file = NewBookmarkFile();
        Bookmarks.ResetForTest(file);
        try
        {
            Bookmarks.Add("D:\\test\\old");
            Bookmarks.Add("D:\\test\\old\\sub");
            Bookmarks.Add("D:\\keep");

            int changed = Bookmarks.Rewrite("D:\\test\\old", "D:\\test\\new");

            Assert.Equal(2, changed);
            Assert.Equal(
                new[] { "D:\\test\\new", "D:\\test\\new\\sub", "D:\\keep" },
                Bookmarks.Load());
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public void Rewrite_UnrelatedRename_TouchesNothing()
    {
        string file = NewBookmarkFile();
        Bookmarks.ResetForTest(file);
        try
        {
            Bookmarks.Add("D:\\a");
            Assert.Equal(0, Bookmarks.Rewrite("D:\\zzz", "D:\\yyy"));
            Assert.Equal(new[] { "D:\\a" }, Bookmarks.Load());
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public void Rewrite_CollapsesDuplicatesItCreates()
    {
        string file = NewBookmarkFile();
        Bookmarks.ResetForTest(file);
        try
        {
            Bookmarks.Add("D:\\test\\new");
            Bookmarks.Add("D:\\test\\old");

            Bookmarks.Rewrite("D:\\test\\old", "D:\\test\\new");

            Assert.Equal(new[] { "D:\\test\\new" }, Bookmarks.Load());
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }
}

// ペインが「今いる場所」として指すディレクトリ。Shift+B のブックマーク登録と
// コピー/移動の転送先が共有する。GetSelectedDirectory は実在確認をするので、
// 実際のディレクトリを temp に作って検証する(固定パスだと環境依存になる)。
public class PaneDirectoryTests
{
    static Pane MakePane(string columnPath, params string[] entries)
    {
        var pane = new Pane();
        var col = new Column(columnPath);
        col.Entries.AddRange(entries);
        col.DirectoriesCount = entries.Count(e => e.EndsWith("/"));
        pane.Columns.Add(col);
        pane.ActiveColumn = 0;
        return pane;
    }

    [Fact]
    public void CursorOnADirectory_ResolvesToThatDirectory()
    {
        // 回帰: カーソルをディレクトリに置いて Shift+B を押しても、
        // アクティブカラムのパス(1つ上の階層)が登録されていた。
        string root = AsyncColumnLoadTests.MakeTempDir("a.txt");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "childdir"));
            Pane pane = MakePane(root, "childdir/", "a.txt");
            pane.Columns[0].Selected = 0;

            Assert.Equal(Path.Combine(root, "childdir"), Program.ResolvePaneDirectory(pane));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CursorOnAFile_FallsBackToTheContainingDirectory()
    {
        string root = AsyncColumnLoadTests.MakeTempDir("a.txt");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "childdir"));
            Pane pane = MakePane(root, "childdir/", "a.txt");
            pane.Columns[0].Selected = 1;

            Assert.Equal(root, Program.ResolvePaneDirectory(pane));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CursorOnADirectoryThatNoLongerExists_FallsBackToTheColumnPath()
    {
        string root = AsyncColumnLoadTests.MakeTempDir();
        try
        {
            Pane pane = MakePane(root, "deleted_elsewhere/");
            pane.Columns[0].Selected = 0;

            Assert.Equal(root, Program.ResolvePaneDirectory(pane));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EmptyColumn_FallsBackToTheColumnPath()
    {
        Pane pane = MakePane("D:\test");
        Assert.Equal("D:\test", Program.ResolvePaneDirectory(pane));
    }

    [Fact]
    public void PaneWithNoColumns_ReturnsEmpty()
    {
        Assert.Equal("", Program.ResolvePaneDirectory(new Pane()));
    }
}

// bug3: 「いつ履歴に載るのか分からない」——←/→ での出入りが記録されていなかった。
[Collection("GlobalStateTests")]
public class HistoryRecordingTests
{
    [Fact]
    public async Task ArrowRightIntoADirectory_RecordsIt()
    {
        string root = AsyncColumnLoadTests.MakeTempDir();
        Directory.CreateDirectory(Path.Combine(root, "child"));
        try
        {
            Program.ClearHistoryForTest();

            Column rootCol = await Program.CreateColumnAsync(root);
            await Program.CompleteColumnLoadAsync(rootCol);

            var pane = new Pane();
            pane.Columns.Add(rootCol);
            pane.ActiveColumn = 0;
            rootCol.Selected = rootCol.Entries.IndexOf("child/");
            await Program.RebuildRightSideAsync(pane, 0);

            await Program.MoveRightForTestAsync(pane);

            Assert.Contains(Path.Combine(root, "child"), Program.HistoryForTest);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ArrowLeftBackOut_RecordsTheParent()
    {
        string root = AsyncColumnLoadTests.MakeTempDir();
        Directory.CreateDirectory(Path.Combine(root, "child"));
        try
        {
            Column rootCol = await Program.CreateColumnAsync(root);
            await Program.CompleteColumnLoadAsync(rootCol);

            var pane = new Pane();
            pane.Columns.Add(rootCol);
            pane.ActiveColumn = 0;
            rootCol.Selected = rootCol.Entries.IndexOf("child/");
            await Program.RebuildRightSideAsync(pane, 0);
            await Program.MoveRightForTestAsync(pane);

            Program.ClearHistoryForTest();
            Program.MoveLeftForTest(pane);

            Assert.Contains(root, Program.HistoryForTest);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CursorMovementWithinAColumn_RecordsNothing()
    {
        // 上下移動では記録しない —— 覗いただけのディレクトリで履歴が埋まらないように。
        Program.ClearHistoryForTest();

        var pane = new Pane();
        var col = new Column("D:\test");
        col.Entries.AddRange(new[] { "a/", "b/", "c/" });
        col.DirectoriesCount = 3;
        pane.Columns.Add(col);
        pane.ActiveColumn = 0;

        Program.MoveDownForTest(pane);
        Program.MoveDownForTest(pane);
        Program.MoveUpForTest(pane);

        Assert.Empty(Program.HistoryForTest);
    }

    [Fact]
    public void DriveListIsNeverRecorded()
    {
        // ::DRIVES:: は実パスではなくジャンプもできないので、履歴に入れても失敗するだけ。
        Program.ClearHistoryForTest();
        Program.RecordHistoryVisitForTest("::DRIVES::");
        Assert.Empty(Program.HistoryForTest);
    }

    [Fact]
    public void RevisitingAPath_MovesItToTheFrontWithoutDuplicating()
    {
        Program.ClearHistoryForTest();
        Program.RecordHistoryVisitForTest("D:\a");
        Program.RecordHistoryVisitForTest("D:\b");
        Program.RecordHistoryVisitForTest("D:\a");

        Assert.Equal(new[] { "D:\b", "D:\a" }, Program.HistoryForTest);
    }
}
