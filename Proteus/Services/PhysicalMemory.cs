using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Proteus.Services;

/// <summary>
/// What the machine has free, from <c>GlobalMemoryStatusEx</c>: physical memory (what paging is about) and commit
/// (whether an allocation can succeed at all). The lesser is what the decode cache may lean on.
/// </summary>
internal readonly record struct PhysicalMemoryStatus(long TotalPhysBytes, long AvailPhysBytes, long AvailCommitBytes)
{
    /// <summary>The lesser of free physical memory and free commit.</summary>
    public long FreeBytes => Math.Min(AvailPhysBytes, AvailCommitBytes);
}

/// <summary>
/// A read-only OS query, kept out of <c>Interop/</c> on purpose: no game memory, no IPC, no struct offsets into the
/// game — one kernel32 call (the same kind <c>LiveBrushPreview</c> already makes from Services).
/// </summary>
internal static class PhysicalMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>The machine's memory now, or null when it can't be read (then the configured budget stands).</summary>
    public static PhysicalMemoryStatus? Read()
    {
        try
        {
            var s = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref s)) return null;
            return new PhysicalMemoryStatus(Saturate(s.ullTotalPhys), Saturate(s.ullAvailPhys), Saturate(s.ullAvailPageFile));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>An OS byte count as a long that never goes negative.</summary>
    internal static long Saturate(ulong v) => v > long.MaxValue ? long.MaxValue : (long)v;
}

/// <summary>The GC's own view as of its last collection: how many gen2s have run, which GC it was, and what it holds.</summary>
internal readonly record struct GcSnapshot(int Gen2Count, long Index, long CommittedBytes, long HeapSizeBytes);

internal static class GcState
{
    public static GcSnapshot Read()
    {
        var i = GC.GetGCMemoryInfo();
        return new GcSnapshot(GC.CollectionCount(2), i.Index, i.TotalCommittedBytes, i.HeapSizeBytes);
    }
}

/// <summary>
/// The process's footprint for a log line, so a release shows whether memory really went back to the OS. Read only
/// when logging.
/// </summary>
internal static class MemoryFootprint
{
    public static string Describe()
    {
        long gc;
        try { gc = GC.GetGCMemoryInfo().TotalCommittedBytes; } catch { gc = -1; }
        long? priv;
        try { using var p = Process.GetCurrentProcess(); priv = p.PrivateMemorySize64; } catch { priv = null; }
        return Format(gc, priv);
    }

    internal static string Format(long gcCommitted, long? privateBytes)
        => $"GC committed {Mb(gcCommitted)} MB, private {(privateBytes is { } p ? Mb(p) : "?")} MB";

    private static string Mb(long bytes) => bytes < 0 ? "?" : (bytes / (1024 * 1024)).ToString();
}
