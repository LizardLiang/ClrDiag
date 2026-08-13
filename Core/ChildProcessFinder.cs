using System.Runtime.InteropServices;

namespace ClrDiag.Core;

/// <summary>
/// 用 Win32 Toolhelp32Snapshot 找出某個行程目前的直接子行程 PID。
///
/// 為什麼不沿用 ManagedProcessFinder.List／ServerService.SnapshotCandidatePids：那個做法在
/// 沒設定 processNames 時要對「系統上每一個行程」開 Process.Modules 找 coreclr.dll——實測在
/// 一般開發機上單次呼叫要 5～8 秒（處理序數量多、部分行程的模組列舉又被資安軟體攔截變慢）。
/// StartUnderDebuggerAsync 需要每 50ms 就問一次「wrapper 生出子行程了嗎」，這種延遲一輪就把
/// 輪詢的意義吃光——子行程早就跑過啟動路徑上的中斷點才被偵測到（見規劃討論：實測晚了 7 秒
/// 以上，遠遠蓋過 ConfigureServices 之類程式碼真正需要的執行時間）。
///
/// Toolhelp32Snapshot 只讀行程清單本身記錄的 PID／PPID／名稱，不開任何行程控制代碼、
/// 不列舉模組，開銷跟系統行程數量無關，微秒級的呼叫拿來做 50ms 高頻輪詢完全沒問題；
/// 用「是不是 wrapper 的直接子行程」取代「是不是新出現的候選行程」，語意上也更精準——
/// 不需要 CLR 已經載入才能被看見，行程一建立就能偵測到。
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
    public static List<int> DirectChildrenOf(int parentPid)
    {
        var result = new List<int>();
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
                if (entry.th32ParentProcessID == (uint)parentPid)
                {
                    result.Add((int)entry.th32ProcessID);
                }
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }
}
