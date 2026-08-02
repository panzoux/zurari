using System.IO;
using Xunit;

namespace Zurari.Tests;

public class PathsOverlapTests
{
    private static string Root => Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\";

    [Fact]
    public void SamePath_Overlaps()
    {
        string p = Path.Combine(Root, "a");
        Assert.True(PathOverlapHelper.PathsOverlap(p, p));
    }

    [Fact]
    public void ChildPath_Overlaps()
    {
        string parent = Path.Combine(Root, "a");
        string child = Path.Combine(Root, "a", "b");
        Assert.True(PathOverlapHelper.PathsOverlap(parent, child));
        Assert.True(PathOverlapHelper.PathsOverlap(child, parent));
    }

    [Fact]
    public void SiblingWithSharedPrefix_DoesNotOverlap()
    {
        // C:\a と C:\ab は前方一致に見えるが、セパレータ境界がないため非重複でなければならない
        string a = Path.Combine(Root, "a");
        string ab = Path.Combine(Root, "ab");
        Assert.False(PathOverlapHelper.PathsOverlap(a, ab));
        Assert.False(PathOverlapHelper.PathsOverlap(ab, a));
    }

    [Fact]
    public void DeepChildPath_Overlaps()
    {
        string parent = Path.Combine(Root, "a");
        string deepChild = Path.Combine(Root, "a", "b", "c", "d.txt");
        Assert.True(PathOverlapHelper.PathsOverlap(parent, deepChild));
    }

    [Fact]
    public void UnrelatedPaths_DoNotOverlap()
    {
        string a = Path.Combine(Root, "a");
        string b = Path.Combine(Root, "b");
        Assert.False(PathOverlapHelper.PathsOverlap(a, b));
    }

    [Fact]
    public void CaseInsensitive_Overlaps()
    {
        string lower = Path.Combine(Root, "a", "b");
        string upperParent = Path.Combine(Root, "A");
        Assert.True(PathOverlapHelper.PathsOverlap(lower, upperParent));
    }

    [Fact]
    public void TrailingSeparator_SamePath_Overlaps()
    {
        string p1 = Path.Combine(Root, "a");
        string p2 = Path.Combine(Root, "a") + Path.DirectorySeparatorChar;
        Assert.True(PathOverlapHelper.PathsOverlap(p1, p2));
    }
}
