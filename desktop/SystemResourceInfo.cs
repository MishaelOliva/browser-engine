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
