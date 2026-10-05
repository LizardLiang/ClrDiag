using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>行程樹的後代判斷：父 PID 連結要搭配建立時間，PID 被重複使用不會把無關行程接進樹裡。</summary>
public sealed class ChildProcessFinderTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static Func<int, DateTime?> Starts(params (int Pid, int Seconds)[] entries)
    {
        var map = entries.ToDictionary(e => e.Pid, e => (DateTime?)T0.AddSeconds(e.Seconds));
        return pid => map.GetValueOrDefault(pid);
    }

    [Fact]
    public void 後代行程依父PID連結往下找且包含孫行程()
    {
        var parents = new Dictionary<int, int> { [101] = 100, [102] = 101, [200] = 1 };
        Func<int, DateTime?> starts = Starts((100, 0), (101, 1), (102, 2), (200, 0));

        HashSet<int> result = ChildProcessFinder.Descendants(100, parents, starts);

        Assert.Equal(new[] { 101, 102 }, result.OrderBy(pid => pid));
    }

    [Fact]
    public void 子行程建立時間早於父行程時不接進樹裡()
    {
        // 101 的建立時間早於根 100：它記的父 PID 是 PID 被重複使用前的舊行程
        var parents = new Dictionary<int, int> { [101] = 100, [102] = 101, [103] = 100 };
        Func<int, DateTime?> starts = Starts((100, 10), (101, 5), (102, 20), (103, 11));

        HashSet<int> result = ChildProcessFinder.Descendants(100, parents, starts);

        Assert.Equal(new[] { 103 }, result.OrderBy(pid => pid));
    }

    [Fact]
    public void 根行程已結束時仍找得到記著它PID的子行程()
    {
        var parents = new Dictionary<int, int> { [101] = 100 };
        Func<int, DateTime?> starts = Starts((101, 1));

        HashSet<int> result = ChildProcessFinder.Descendants(100, parents, starts);

        Assert.Equal(new[] { 101 }, result);
    }

    [Fact]
    public void 讀不到建立時間的子行程不接進樹裡()
    {
        var parents = new Dictionary<int, int> { [101] = 100 };
        Func<int, DateTime?> starts = Starts((100, 0));

        HashSet<int> result = ChildProcessFinder.Descendants(100, parents, starts);

        Assert.Empty(result);
    }
}
