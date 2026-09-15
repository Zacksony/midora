using System.ComponentModel;
using System.Runtime.InteropServices;

// Read-only classification of this process only; no process enumeration or memory modification.
internal sealed record ProcessMemoryMap(long PrivateCommittedBytes, long MappedCommittedBytes,
    long ImageCommittedBytes, long ReservedBytes, int Regions)
{
    public static ProcessMemoryMap Capture()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("The WPF probe requires x64.");
        ulong address = 0;
        long privateBytes = 0, mappedBytes = 0, imageBytes = 0, reservedBytes = 0;
        int regions = 0;
        while (VirtualQuery((nint)address, out MemoryBasicInformation info,
                   (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            regions++;
            long bytes = checked((long)info.RegionSize);
            if (info.State == 0x1000)
            {
                switch (info.Type)
                {
                    case 0x20000: privateBytes = checked(privateBytes + bytes); break;
                    case 0x40000: mappedBytes = checked(mappedBytes + bytes); break;
                    case 0x1000000: imageBytes = checked(imageBytes + bytes); break;
                }
            }
            else if (info.State == 0x2000) reservedBytes = checked(reservedBytes + bytes);
            ulong next = checked((ulong)info.BaseAddress + (ulong)info.RegionSize);
            if (next <= address) throw new InvalidDataException("VirtualQuery did not advance.");
            address = next;
        }
        // ERROR_INVALID_PARAMETER marks the end of the user virtual address range.
        int error = Marshal.GetLastWin32Error();
        if (error != 87) throw new Win32Exception(error);
        return new(privateBytes, mappedBytes, imageBytes, reservedBytes, regions);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);
}
