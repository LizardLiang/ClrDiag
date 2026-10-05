using System.Runtime.InteropServices;

namespace ClrDiag.Core;

/// <summary>
/// 查出哪個行程擁有某個連接埠的 TCP 監聽（Win32 GetExtendedTcpTable，同時查 IPv4 與 IPv6）。
/// 用於沒有設定 processNames 時決定要接管哪個行程：只接管監聽設定連接埠的那一個，
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

    /// <summary>回傳監聽該連接埠的行程 PID；沒有人監聽或查詢失敗回傳 null。</summary>
    public static int? FindListenerPid(int port)
    {
        return ReadListenerPid(port, AfInet, Row4Size, Row4PortOffset, Row4PidOffset)
            ?? ReadListenerPid(port, AfInet6, Row6Size, Row6PortOffset, Row6PidOffset);
    }

    private static int? ReadListenerPid(
        int port,
        int addressFamily,
        int rowSize,
        int portOffset,
        int pidOffset
    )
    {
        int size = 0;
        int result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            false,
            addressFamily,
            TcpTableOwnerPidListener,
            0
        );
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
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = buffer + 4 + (i * rowSize);

                    // 連接埠存在 DWORD 的低 16 位元，為網路位元組順序
                    int raw = Marshal.ReadInt32(row, portOffset) & 0xFFFF;
                    int localPort = ((raw & 0xFF) << 8) | (raw >> 8);
                    if (localPort == port)
                    {
                        return Marshal.ReadInt32(row, pidOffset);
                    }
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }
}
