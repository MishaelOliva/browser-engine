using System.Runtime.InteropServices;

namespace MishaWeb;

internal readonly record struct SystemMemorySnapshot(
    uint MemoryLoadPercent,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    int LogicalProcessorCount,
    bool IsValid)
{
    private const ulong FourGibibytes = 4UL * 1024 * 1024 * 1024;

    public bool IsTinyMachine => TotalPhysicalBytes is > 0 and <= FourGibibytes;
    public bool IsLowSpecMachine => IsTinyMachine || LogicalProcessorCount is > 0 and <= 2;
}

internal static class SystemResourceInfo
{
    public static SystemMemorySnapshot CaptureMemory()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        var logicalProcessorCount = Environment.ProcessorCount;
        return GlobalMemoryStatusEx(ref status)
            ? new SystemMemorySnapshot(
                status.MemoryLoad,
                status.TotalPhysical,
                status.AvailablePhysical,
                logicalProcessorCount,
                IsValid: true)
            : new SystemMemorySnapshot(
                MemoryLoadPercent: 0,
                TotalPhysicalBytes: 0,
                AvailablePhysicalBytes: 0,
                LogicalProcessorCount: logicalProcessorCount,
                IsValid: false);
    }

    public static bool TrimCurrentProcessWorkingSet()
    {
        try
        {
            return EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            return false;
        }
    }

    public static bool TrimProcessWorkingSet(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            var handle = OpenProcess(ProcessQueryLimitedAndSetQuota, false, processId);
            if (handle == IntPtr.Zero)
            {
                handle = OpenProcess(ProcessQueryAndSetQuota, false, processId);
            }
            if (handle == IntPtr.Zero) return false;
            try
            {
                return EmptyWorkingSet(handle);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return false;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryLimitedAndSetQuota = ProcessQueryLimitedInformation | ProcessSetQuota;
    private const uint ProcessQueryAndSetQuota = ProcessQueryInformation | ProcessSetQuota;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
