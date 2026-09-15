using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class MemoryGuard
{
    // Test infrastructure only. A private Windows Job captures descendants before their first instruction.
    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length < 5)
            throw new ArgumentException("guard output-directory limit-MiB reserve-MiB executable [arguments...]");
        string output = Path.GetFullPath(args[0]);
        ulong limit = checked(ulong.Parse(args[1]) * 1048576), reserve = checked(ulong.Parse(args[2]) * 1048576);
        if (limit == 0 || limit > 8192UL * 1048576 || reserve < 512UL * 1048576)
            throw new ArgumentException("Guard limit cannot exceed 8 GiB; reserve must be at least 512 MiB.");
        Directory.CreateDirectory(output);
        using StreamWriter log = new(new FileStream(Path.Combine(output, "guard.jsonl"), FileMode.CreateNew)) { AutoFlush = true };
        void Log(object value) { string json = JsonSerializer.Serialize(value); log.WriteLine(json); Console.WriteLine(json); }
        MEMORYSTATUSEX memory = GetMemory();
        Log(new { kind = "guard-start", limitBytes = limit, reserveBytes = reserve, availableBytes = memory.AvailPhys,
            command = args[3..], sampleMilliseconds = 100, hardJobPrivateCommitLimit = true });
        if (memory.AvailPhys < reserve + 256UL * 1048576)
        { Log(new { kind = "not-started", reason = "insufficient-available-memory" }); return 125; }
        nint job = CreateJobObject(nint.Zero, null);
        if (job == 0) throw new Win32Exception();
        PROCESS_INFORMATION processInfo = default;
        nint stdout = 0, stderr = 0;
        try
        {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new();
            limits.BasicLimitInformation.LimitFlags = 0x2000 | 0x200; // Kill-on-close; job commit hard ceiling.
            limits.JobMemoryLimit = (nuint)limit;
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw new Win32Exception();
            STARTUPINFO startup = new() { cb = (uint)Marshal.SizeOf<STARTUPINFO>() };
            SECURITY_ATTRIBUTES attributes = new() { Length = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(), InheritHandle = 1 };
            stdout = CreateFile(Path.Combine(output, "child.stdout.log"), 0x40000000, 0x1 | 0x2, ref attributes, 1, 0x80, 0);
            if (stdout == -1) throw new Win32Exception();
            stderr = CreateFile(Path.Combine(output, "child.stderr.log"), 0x40000000, 0x1 | 0x2, ref attributes, 1, 0x80, 0);
            if (stderr == -1) throw new Win32Exception();
            startup.dwFlags = 0x100; startup.hStdOutput = stdout; startup.hStdError = stderr;
            string command = string.Join(" ", args[3..].Select(Quote));
            if (!CreateProcess(args[3], new StringBuilder(command), nint.Zero, nint.Zero, true, 0x4 | 0x08000000,
                    nint.Zero, Environment.CurrentDirectory, ref startup, out processInfo)) throw new Win32Exception();
            if (!AssignProcessToJobObject(job, processInfo.hProcess))
            { TerminateProcess(processInfo.hProcess, 125); throw new Win32Exception(); }
            Log(new { kind = "child-assigned-suspended", processInfo.dwProcessId });
            if (ResumeThread(processInfo.hThread) == uint.MaxValue) throw new Win32Exception();
            ulong peakPrivate = 0, peakWs = 0; int samples = 0;
            string? stopReason = null;
            while (true)
            {
                uint[] ids = GetJobProcessIds(job);
                ulong privateBytes = 0, workingSet = 0;
                foreach (uint id in ids)
                {
                    try
                    {
                        using Process p = Process.GetProcessById(checked((int)id));
                        p.Refresh(); privateBytes += (ulong)p.PrivateMemorySize64; workingSet += (ulong)p.WorkingSet64;
                    }
                    catch (ArgumentException) { } // Process exited after the owned-job snapshot.
                    catch (InvalidOperationException) { }
                }
                memory = GetMemory(); samples++;
                peakPrivate = Math.Max(peakPrivate, privateBytes); peakWs = Math.Max(peakWs, workingSet);
                log.WriteLine(JsonSerializer.Serialize(new { kind = "sample", utc = DateTimeOffset.UtcNow, processCount = ids.Length,
                    privateBytes, workingSetBytes = workingSet, availableBytes = memory.AvailPhys }));
                if (privateBytes >= limit) stopReason = "process-tree-private-limit";
                else if (workingSet >= limit) stopReason = "process-tree-working-set-limit";
                else if (memory.AvailPhys < reserve) stopReason = "system-available-reserve";
                if (stopReason is not null)
                {
                    Log(new { kind = "guard-stop", reason = stopReason, privateBytes, workingSet, availableBytes = memory.AvailPhys,
                        ownedProcessIds = ids });
                    if (!TerminateJobObject(job, 124)) throw new Win32Exception();
                    WaitForSingleObject(processInfo.hProcess, 5000); break;
                }
                if (ids.Length == 0) break;
                Thread.Sleep(100);
            }
            if (!GetExitCodeProcess(processInfo.hProcess, out uint code)) throw new Win32Exception();
            if (code == 259) { TerminateJobObject(job, 125); code = 125; }
            var summary = new { kind = "guard-complete", exitCode = code, stopReason, samples,
                peakTreePrivateBytes = peakPrivate, peakTreeWorkingSetBytes = peakWs, limitBytes = limit, reserveBytes = reserve };
            Log(summary);
            File.WriteAllText(Path.Combine(output, "guard-result.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            return stopReason is null ? unchecked((int)code) : 124;
        }
        finally
        {
            // Closing our job kills only processes assigned to this private job; never enumerate/kill user processes.
            CloseHandle(job);
            if (processInfo.hThread != 0) CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != 0) CloseHandle(processInfo.hProcess);
            if (stdout != 0 && stdout != -1) CloseHandle(stdout);
            if (stderr != 0 && stderr != -1) CloseHandle(stderr);
        }
    }
    private static string Quote(string value) => "\"" + System.Text.RegularExpressions.Regex.Replace(value, "(\\\\*)\"", "$1$1\\\"")
        .TrimEnd('\\') + new string('\\', value.Length - value.TrimEnd('\\').Length) + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
    private static MEMORYSTATUSEX GetMemory()
    {
        MEMORYSTATUSEX memory = new() { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(); return memory;
    }
    private static uint[] GetJobProcessIds(nint job)
    {
        nint buffer = Marshal.AllocHGlobal(8 + 1024 * IntPtr.Size);
        try
        {
            if (!QueryInformationJobObject(job, 3, buffer, (uint)(8 + 1024 * IntPtr.Size), out _)) throw new Win32Exception();
            int count = Marshal.ReadInt32(buffer, 4); uint[] ids = new uint[count];
            for (int i = 0; i < count; i++) ids[i] = checked((uint)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size));
            return ids;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] private struct MEMORYSTATUSEX
    { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
    { public uint cb; public string? lpReserved, lpDesktop, lpTitle; public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public ushort wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION
    { public nint hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES
    { public uint Length; public nint SecurityDescriptor; public int InheritHandle; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint CreateFile(string path, uint access, uint sharing, ref SECURITY_ATTRIBUTES attributes, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint CreateJobObject(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int infoClass, nint info, uint length, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inheritHandles, uint flags, nint environment, string currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
