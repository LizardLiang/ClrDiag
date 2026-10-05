using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClrDiag.Core;

/// <summary>
/// 用 Win32 Toolhelp32Snapshot 找出某個行程目前的直接子行程 PID。
///
/// 為什麼不用 ManagedProcessFinder.List 這類「對系統上每一個行程開 Process.Modules 找 coreclr.dll」
/// 的做法：實測在一般開發機上單次呼叫要 5～8 秒（處理序數量多、部分行程的模組列舉又被資安軟體
/// 攔截變慢）。StartUnderDebuggerAsync 需要每 20ms 就問一次「wrapper 生出子行程了嗎」，
/// 這種延遲一輪就把輪詢的意義吃光——子行程早就跑過啟動路徑上的中斷點才被偵測到
/// （實測晚了 7 秒以上，遠遠蓋過 ConfigureServices 之類程式碼真正需要的執行時間）。
///
/// Toolhelp32Snapshot 只讀行程清單本身記錄的 PID／PPID／名稱，不開任何行程控制代碼、
/// 不列舉模組，開銷跟系統行程數量無關，微秒級的呼叫拿來做 20ms 高頻輪詢完全沒問題；
/// 用「是不是 wrapper 的直接子行程」取代「是不是新出現的候選行程」，語意上也更精準——
/// 不需要 CLR 已經載入才能被看見，行程一建立就能偵測到。
/// DescendantsOf 為了核對父子的建立時間，會對有父 PID 連結的行程短暫開啟控制代碼讀建立時間，
/// 只在辨識與清除時呼叫，不在 20ms 的輪詢路徑上。
/// </summary>
public static class ChildProcessFinder
{
    private const uint Th32csSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// 回傳目前所有以 parentPid 為直接父行程的 PID；快照失敗（極少見）回傳空集合，
    /// 呼叫端應該把它當成「這輪沒看到」而不是硬錯誤，下一輪輪詢再試即可。
    /// </summary>
    public static List<int> DirectChildrenOf(int parentPid) =>
        ReadParentMap().Where(pair => pair.Value == parentPid).Select(pair => pair.Key).ToList();

    /// <summary>
    /// 回傳 rootPid 底下所有後代行程的 PID（不含 rootPid 本身）。同一份快照一次走完整棵樹，
    /// 沿著「父 PID」連結往下找。Windows 的父 PID 在父行程結束後不會更新，所以 rootPid 本身
    /// 已結束時，記著它 PID 的直接子行程仍找得到；但中間行程已結束時，它底下的孫行程接不到
    /// rootPid（快照裡沒有那個中間行程），找不到。
    /// PID 會被重複使用，所以父子連結要同時滿足「子行程的建立時間不早於父行程」才算數
    /// （與 .NET Process 判斷父子關係的規則相同）：記著舊父 PID 的無關行程不會被接進樹裡。
    /// 讀不到建立時間的子行程無法核對也無法結束，不列入；父行程（例如已結束的 rootPid）
    /// 讀不到建立時間時，只能憑 PID 連結。
    /// </summary>
    public static HashSet<int> DescendantsOf(int rootPid) => Descendants(rootPid, ReadParentMap(), StartTimeOf);

    /// <summary>後代判斷本體；父 PID 對照與建立時間查詢由參數提供，供測試用假資料驗證。</summary>
    internal static HashSet<int> Descendants(
        int rootPid,
        IReadOnlyDictionary<int, int> parents,
        Func<int, DateTime?> startOf
    )
    {
        var result = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(rootPid);
        while (pending.Count > 0)
        {
            int current = pending.Dequeue();
            DateTime? parentStart = null;
            bool parentStartRead = false;
            foreach (KeyValuePair<int, int> pair in parents)
            {
                // PID 可能被重複使用而形成環，已經走過的不再加入
                if (pair.Value != current || pair.Key == rootPid || result.Contains(pair.Key))
                {
                    continue;
                }

                if (startOf(pair.Key) is not { } childStart)
                {
                    continue;
                }

                if (!parentStartRead)
                {
                    parentStart = startOf(current);
                    parentStartRead = true;
                }

                if (parentStart is { } start && childStart < start)
                {
                    continue;
                }

                result.Add(pair.Key);
                pending.Enqueue(pair.Key);
            }
        }

        return result;
    }

    /// <summary>行程建立時間；行程不存在或沒有權限讀取時回傳 null。</summary>
    internal static DateTime? StartTimeOf(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return SafeStartTime(process);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>已取得的行程物件的建立時間；讀不到（已結束、沒有權限）回傳 null。</summary>
    internal static DateTime? SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>讀取一份行程快照，回傳 PID → 父 PID 對照；快照失敗回傳空表。</summary>
    private static Dictionary<int, int> ReadParentMap()
    {
        var result = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }
}
