using System.Runtime.InteropServices;

namespace Glance;

/// <summary>Whole-machine memory and CPU figures from two cheap kernel calls.</summary>
internal sealed class SystemStats
{
    private long _idle, _kernel, _user;
    private bool _primed;

    public double CpuPercent { get; private set; }
    public double MemPercent { get; private set; }
    public ulong MemTotal { get; private set; }
    public ulong MemUsed { get; private set; }
    public int Cores { get; } = Environment.ProcessorCount;

    public void Sample()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref m))
        {
            MemTotal = m.ullTotalPhys;
            MemUsed = m.ullTotalPhys - m.ullAvailPhys;
            MemPercent = MemTotal == 0 ? 0 : 100.0 * MemUsed / MemTotal;
        }

        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            if (_primed)
            {
                long total = (kernel - _kernel) + (user - _user);
                long busy = total - (idle - _idle);
                CpuPercent = total <= 0 ? 0 : Math.Clamp(100.0 * busy / total, 0, 100);
            }
            _idle = idle; _kernel = kernel; _user = user; _primed = true;
        }
    }
}

internal static class Fmt
{
    public static string Bytes(long b)
    {
        double mb = b / 1048576.0;
        if (mb >= 1000) return $"{mb / 1024:0.0} GB";
        if (mb >= 1) return $"{mb:0} MB";
        return $"{b / 1024.0:0} KB";
    }

    public static string Gb(ulong b) => $"{b / 1073741824.0:0.0}";
    public static string Gb0(ulong b) => $"{b / 1073741824.0:0}";
}
