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

        HashSet<int> result = ChildProcessFinder.Descendants(100, parents, starts, rootStart: T0);

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

    [Fact]
    public void 根行程已結束時比它舊的孤兒行程與孤兒的子行程都不算後代()
    {
        // 根 100 已結束（讀不到建立時間，持有的控制代碼讀得到 T0+10）；200 是更早建立、記著死亡父 PID 100 的孤兒，201 是孤兒之後建立的子行程
        var parents = new Dictionary<int, int> { [200] = 100, [201] = 200, [300] = 100 };
        Func<int, DateTime?> starts = Starts((200, 5), (201, 20), (300, 11));

        HashSet<int> result = ChildProcessFinder.Descendants(100, parents, starts, rootStart: T0.AddSeconds(10));

        Assert.Equal(new[] { 300 }, result.OrderBy(pid => pid));
    }

    [Fact]
    public void 沒有提供根建立時間且讀不到時根底下的連結都不採用()
    {
        var parents = new Dictionary<int, int> { [101] = 100, [102] = 101 };
        Func<int, DateTime?> starts = Starts((101, 1), (102, 2));

        HashSet<int> result = ChildProcessFinder.Descendants(100, parents, starts);

        Assert.Empty(result);
    }

    [Fact]
    public void 中間行程讀不到建立時間時它底下的連結不採用()
    {
        // 101 讀不到建立時間（子行程列入的條件不成立），所以 102 不會被接到 101 底下；103 的父 PID 是根，不受影響
        var parents = new Dictionary<int, int> { [101] = 100, [102] = 101, [103] = 100 };
        Func<int, DateTime?> starts = Starts((102, 2), (103, 3));

        HashSet<int> result = ChildProcessFinder.Descendants(100, parents, starts, rootStart: T0);

        Assert.Equal(new[] { 103 }, result);
    }

    [Fact]
    public void 子行程建立時間等於父行程時算後代_早一刻則不算()
    {
        var parents = new Dictionary<int, int> { [101] = 100, [102] = 100 };
        var starts = new Dictionary<int, DateTime?>
        {
            [101] = T0.AddSeconds(10),
            [102] = T0.AddSeconds(10).AddTicks(-1),
        };

        HashSet<int> result = ChildProcessFinder.Descendants(100, parents, pid => starts.GetValueOrDefault(pid), T0.AddSeconds(10));

        Assert.Equal(new[] { 101 }, result);
    }
}
