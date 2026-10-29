using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Glance;

/// <summary>Keeps a "Glance" entry in the Start menu that opens this copy, so it's easy to start (or restart) from Start.</summary>
internal static class Shortcut
{
    private static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Glance.lnk");

    public static void Ensure()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            if (File.Exists(LinkPath) && string.Equals(Target(LinkPath), exe, StringComparison.OrdinalIgnoreCase)) return;

            var link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(exe);
                link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
                link.SetDescription("Memory and CPU by the clock. Open it again to restart it.");
                link.SetIconLocation(exe, 0);
                ((IPersistFile)link).Save(LinkPath, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    public static void Remove()
    {
        try { if (File.Exists(LinkPath)) File.Delete(LinkPath); }
        catch (Exception ex) { Log.Write(ex); }
    }

    private static string? Target(string lnk)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
