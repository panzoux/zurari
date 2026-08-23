using Xunit;

// 回帰: 転送先が対象自身(= 対象の親ディレクトリ)だと、ディレクトリ移動が
// 「中身を自分自身へ移す → 空になったので元を削除」となり、ディレクトリごと消えていた。
public class SelfMoveGuardTests
{
    sealed class NullProgress : IFileOpProgress
    {
        public void Report(FileOpProgressInfo info) { }
        public void ReportItemResult(OpLogEntry entry) { }
    }

    static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zurari-selfmove-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static Func<string, Task<ConflictDecision?>> NeverAsked()
        => _ => throw new InvalidOperationException("onConflict should not be called");

    [Fact]
    public async Task MoveAsync_EmptyDirectoryIntoItsOwnParent_DoesNotDeleteIt()
    {
        string root = NewTempDir();
        try
        {
            string victim = Path.Combine(root, "1fol2");
            Directory.CreateDirectory(victim);

            var result = await FileOpEngine.MoveAsync(
                new[] { victim }, root, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.True(Directory.Exists(victim), "the directory must survive a move onto itself");
            Assert.Equal(0, result.Succeeded);
            Assert.Equal(1, result.Failed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MoveAsync_NonEmptyDirectoryIntoItsOwnParent_KeepsContents()
    {
        string root = NewTempDir();
        try
        {
            string victim = Path.Combine(root, "docs");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "keep.txt"), "payload");

            var result = await FileOpEngine.MoveAsync(
                new[] { victim }, root, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.True(Directory.Exists(victim));
            Assert.Equal("payload", File.ReadAllText(Path.Combine(victim, "keep.txt")));
            Assert.Equal(1, result.Failed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MoveAsync_DirectoryIntoItsOwnSubdirectory_IsRefused()
    {
        string root = NewTempDir();
        try
        {
            string outer = Path.Combine(root, "outer");
            string inner = Path.Combine(outer, "inner");
            Directory.CreateDirectory(inner);
            File.WriteAllText(Path.Combine(outer, "a.txt"), "x");

            var result = await FileOpEngine.MoveAsync(
                new[] { outer }, inner, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(outer, "a.txt")));
            Assert.Equal(1, result.Failed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MoveAsync_FileIntoItsOwnDirectory_IsRefused()
    {
        string root = NewTempDir();
        try
        {
            string file = Path.Combine(root, "a.txt");
            File.WriteAllText(file, "payload");

            var result = await FileOpEngine.MoveAsync(
                new[] { file }, root, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.Equal("payload", File.ReadAllText(file));
            Assert.Equal(1, result.Failed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MoveAsync_IntoASiblingDirectory_StillWorks()
    {
        // ガードが正当な移動まで巻き込んでいないこと。
        string root = NewTempDir();
        try
        {
            string src = Path.Combine(root, "1fol2");
            string destParent = Path.Combine(root, "1fol");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(destParent);
            File.WriteAllText(Path.Combine(src, "a.txt"), "payload");

            var result = await FileOpEngine.MoveAsync(
                new[] { src }, destParent, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.Equal(1, result.Succeeded);
            Assert.Equal(0, result.Failed);
            Assert.False(Directory.Exists(src));
            Assert.Equal("payload", File.ReadAllText(Path.Combine(destParent, "1fol2", "a.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CopyAsync_IntoItsOwnDirectory_IsRefusedRatherThanTruncating()
    {
        string root = NewTempDir();
        try
        {
            string file = Path.Combine(root, "a.txt");
            File.WriteAllText(file, "payload");

            var result = await FileOpEngine.CopyAsync(
                new[] { file }, root, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.Equal("payload", File.ReadAllText(file));
            Assert.Equal(1, result.Failed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    // 対象そのもの / 対象の親 / 対象の配下 → 重なりとして弾く
    [InlineData("C:\\test\\1fol2", "C:\\test\\1fol2", true)]
    [InlineData("C:\\test\\1fol2", "C:\\test", true)]
    [InlineData("C:\\test", "C:\\test\\1fol", true)]
    // 兄弟ディレクトリ / 別ドライブ → 正当な転送先
    [InlineData("C:\\test\\1fol2", "C:\\test\\1fol", false)]
    [InlineData("C:\\test\\1fol2", "D:\\other", false)]
    // 前方一致の誤判定を起こさないこと
    [InlineData("C:\\test\\1fol", "C:\\test\\1fol2", false)]
    public void PathsOverlapSafe_ClassifiesDestinations(string target, string destDir, bool expected)
    {
        Assert.Equal(expected, PathOverlapHelper.PathsOverlapSafe(target, destDir));
    }

    [Fact]
    public void PathsOverlapSafe_InvalidPath_FailsClosed()
    {
        // 判定不能なら「重なっている」= 転送を止める側に倒す。
        Assert.True(PathOverlapHelper.PathsOverlapSafe("C:\\test", "\0invalid"));
    }
}
