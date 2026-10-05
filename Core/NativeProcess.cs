using System.Runtime.InteropServices;

namespace ClrDiag.Core;

/// <summary>
/// 讀取其他行程的命令列與位元數（Win32 / NT API，只需要 PROCESS_QUERY_LIMITED_INFORMATION）。
/// 用於 HTTP.sys 站台（IIS Express、w3wp）：連接埠的監聽記在系統行程 PID 4 名下，
/// 只能從候選行程的命令列（例如 iisexpress 的 /port:58649）判斷哪一個服務該連接埠。
/// </summary>
public static class NativeProcess
{
    private const int ProcessQueryLimitedInformation = 0x1000;

    // PROCESSINFOCLASS.ProcessCommandLine（Windows 8.1 起）
    private const int ProcessCommandLineClass = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool isWow64);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process,
        int informationClass,
        IntPtr buffer,
        int length,
        out int returnLength
    );

    /// <summary>取得行程的命令列；行程不存在、沒有權限或系統不支援時回傳 null。</summary>
    public static string? CommandLine(int pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineClass, IntPtr.Zero, 0, out int needed);
            if (needed <= 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineClass, buffer, needed, out _) < 0)
                {
                    return null;
                }

                // UNICODE_STRING：Length（位元組）在位移 0，字串指標在位移 8（x64），內容緊接在同一塊緩衝區
                int length = Marshal.ReadInt16(buffer, 0);
                IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return length == 0 || text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(text, length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>行程是否為 32 位元（WOW64）；查不到回傳 false。本工具是 64 位元，無法診斷 32 位元行程。</summary>
    public static bool IsWow64(int pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return IsWow64Process(handle, out bool wow64) && wow64;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
