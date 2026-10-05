using System.Runtime.InteropServices;

namespace ClrDiag.Core;

/// <summary>
/// 查出哪個行程擁有某個連接埠的 TCP 監聽（Win32 GetExtendedTcpTable，同時查 IPv4 與 IPv6）。
/// 是 ServerLocator 辨識伺服器行程的依據：只認監聽設定連接埠的行程，
/// 不去猜系統上任何一個載入 CLR 的行程。
/// </summary>
public static class PortOwnerFinder
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;

    // TCP_TABLE_OWNER_PID_LISTENER：只列出監聽中的端點並附上擁有者 PID
    private const int TcpTableOwnerPidListener = 3;

    private const int ErrorInsufficientBuffer = 122;

    // MIB_TCPROW_OWNER_PID：6 個 DWORD，本機連接埠在第 3 個（網路位元組順序），PID 在第 6 個
    private const int Row4Size = 24;
    private const int Row4PortOffset = 8;
    private const int Row4PidOffset = 20;

    // MIB_TCP6ROW_OWNER_PID：本機連接埠在位移 20，PID 在位移 52
    private const int Row6Size = 56;
    private const int Row6PortOffset = 20;
    private const int Row6PidOffset = 52;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        int reserved
    );

    /// <summary>
    /// 回傳監聽該連接埠的所有行程 PID（IPv4 與 IPv6 合併、去除重複）；沒有人監聽回傳空清單，
    /// 兩個位址族的查詢都失敗（API 不存在、呼叫失敗）回傳 null——呼叫端要分辨「沒人監聽」與「查不到」。
    /// </summary>
    public static IReadOnlyList<int>? FindListenerPids(int port)
    {
        List<int>? v4 = ReadListenerPids(port, AfInet, Row4Size, Row4PortOffset, Row4PidOffset);
        List<int>? v6 = ReadListenerPids(port, AfInet6, Row6Size, Row6PortOffset, Row6PidOffset);
        if (v4 is null && v6 is null)
        {
            return null;
        }

        return (v4 ?? new List<int>()).Concat(v6 ?? new List<int>()).Distinct().ToList();
    }

    /// <summary>
    /// 回傳監聽該連接埠的行程 PID；沒有人監聽、查詢失敗，或 IPv4 / IPv6 由不同行程監聽（無法判斷
    /// 哪一個才是目標）都回傳 null。
    /// </summary>
    public static int? FindListenerPid(int port)
    {
        IReadOnlyList<int>? pids = FindListenerPids(port);
        return pids is { Count: 1 } ? pids[0] : null;
    }

    /// <summary>是否有人在該連接埠監聽；查表失敗時退回 IPGlobalProperties 的結果。</summary>
    public static bool IsListening(int port)
    {
        IReadOnlyList<int>? pids = FindListenerPids(port);
        if (pids is not null)
        {
            return pids.Count > 0;
        }

        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    private static List<int>? ReadListenerPids(
        int port,
        int addressFamily,
        int rowSize,
        int portOffset,
        int pidOffset
    )
    {
        int size = 0;
        int result;
        try
        {
            result = GetExtendedTcpTable(
                IntPtr.Zero,
                ref size,
                false,
                addressFamily,
                TcpTableOwnerPidListener,
                0
            );
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer || size <= 0)
        {
            return null;
        }

        // 查表與配置之間表可能變大，多留一些空間並在不足時重試
        for (int attempt = 0; attempt < 3; attempt++)
        {
            size += 4096;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(
                    buffer,
                    ref size,
                    false,
                    addressFamily,
                    TcpTableOwnerPidListener,
                    0
                );
                if (result == ErrorInsufficientBuffer)
                {
                    continue;
                }

                if (result != 0)
                {
                    return null;
                }

                int count = Marshal.ReadInt32(buffer);
                var pids = new List<int>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = buffer + 4 + (i * rowSize);

                    // 連接埠存在 DWORD 的低 16 位元，為網路位元組順序
                    int raw = Marshal.ReadInt32(row, portOffset) & 0xFFFF;
                    int localPort = ((raw & 0xFF) << 8) | (raw >> 8);
                    if (localPort == port)
                    {
                        pids.Add(Marshal.ReadInt32(row, pidOffset));
                    }
                }

                return pids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }
}
