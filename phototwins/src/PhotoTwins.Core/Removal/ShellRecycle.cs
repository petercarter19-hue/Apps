using System.Runtime.InteropServices;
using PhotoTwins.Core.Native;

namespace PhotoTwins.Core.Removal;

/// <summary>What Windows did with one photo we asked it to recycle.</summary>
internal sealed record RecycleOutcome(bool Recycled, string? RecycledPath, string? CancelledBecause);

/// <summary>An item sitting in the Recycle Bin.</summary>
internal sealed record BinItem(string BinPath, string OriginalPath, dynamic Handle);

/// <summary>
/// The only code that talks to Windows' Recycle Bin. Used by <see cref="FileMutations"/> and nothing else.
/// Recycling goes through the shell's file operation with a guard attached: if Windows says a delete would be PERMANENT
/// (not "recycle if possible"), the guard cancels it before anything is touched.
/// </summary>
internal static class ShellRecycle
{
    private const uint FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;
    private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;
    private const int E_ABORT = unchecked((int)0x80004004);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IFileOperationProgressSink pfops);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetProperties(IntPtr pproparray);
        void SetOwnerWindow(IntPtr hwndOwner);
        void ApplyPropertiesToItem(IShellItem psiItem);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IFileOperationProgressSink pfopsItem);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IFileOperationProgressSink pfopsItem);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName, IFileOperationProgressSink pfopsItem);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IFileOperationProgressSink pfopsItem);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        uint NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, IFileOperationProgressSink pfopsItem);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }

    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        void StartOperations();
        void FinishOperations(uint hrResult);
        void PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, uint hrRename, IShellItem psiNewlyCreated);
        void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, uint hrMove, IShellItem psiNewlyCreated);
        void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, uint hrCopy, IShellItem psiNewlyCreated);
        [PreserveSig] int PreDeleteItem(uint dwFlags, IShellItem psiItem);
        void PostDeleteItem(uint dwFlags, IShellItem psiItem, uint hrDelete, IShellItem psiNewlyCreated);
        void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, uint dwFileAttributes, uint hrNew, IShellItem psiNewItem);
        void UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
        void ResetTimer();
        void PauseTimer();
        void ResumeTimer();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);

    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>The guard: lets a delete go ahead only if Windows says it will be recycled, and notes where the photo ended up.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DeleteGuard : IFileOperationProgressSink
    {
        public bool WouldDeleteForGood;
        public string? RecycledPath;

        public int PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
            if ((dwFlags & TSF_DELETE_RECYCLE_IF_POSSIBLE) == 0)
            {
                WouldDeleteForGood = true;
                return E_ABORT;   // cancel: this delete would not be recycled
            }
            return 0;
        }

        public void PostDeleteItem(uint dwFlags, IShellItem psiItem, uint hrDelete, IShellItem psiNewlyCreated)
        {
            if (psiNewlyCreated == null) return;
            try
            {
                psiNewlyCreated.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out var p);
                if (p != IntPtr.Zero) { RecycledPath = Marshal.PtrToStringUni(p); CoTaskMemFree(p); }
            }
            catch { /* the recycled path is only a convenience for Undo */ }
        }

        public void StartOperations() { }
        public void FinishOperations(uint hrResult) { }
        public void PreRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName) { }
        public void PostRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName, uint hrRename, IShellItem psiNewlyCreated) { }
        public void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) { }
        public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, uint hrMove, IShellItem psiNewlyCreated) { }
        public void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) { }
        public void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, uint hrCopy, IShellItem psiNewlyCreated) { }
        public void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName) { }
        public void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName, string pszTemplateName, uint dwFileAttributes, uint hrNew, IShellItem psiNewItem) { }
        public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar) { }
        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }
    }

    /// <summary>
    /// Sends one file to the Recycle Bin. If Windows would delete it for good instead, the guard cancels it and nothing happens
    /// (<see cref="RecycleOutcome.CancelledBecause"/> is set). Throws if Windows reports any other failure.
    /// </summary>
    public static RecycleOutcome Recycle(string path) => StaRunner.Run(() =>
    {
        var op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), true)!)!;
        var guard = new DeleteGuard();
        IShellItem? item = null;
        try
        {
            op.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT);
            var iid = IidShellItem;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            op.DeleteItem(item, guard);
            try { op.PerformOperations(); }
            catch (COMException) when (guard.WouldDeleteForGood) { /* cancelled by the guard, as intended */ }
            if (guard.WouldDeleteForGood)
                return new RecycleOutcome(false, null, "Windows would have deleted it for good instead of recycling it, so PhotoTwins stopped it.");
            if (op.GetAnyOperationsAborted())
                throw new IOException("Windows stopped the move to the Recycle Bin.");
            return new RecycleOutcome(true, guard.RecycledPath, null);
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(op);
        }
    });

    // ---------------------------------------------------------------- looking inside the Recycle Bin (for Undo)

    /// <summary>How many times THIS thread has read the whole Recycle Bin (for tests: proves one action reads it once).</summary>
    [ThreadStatic] internal static int BinReads;

    /// <summary>Reads the whole Recycle Bin once.</summary>
    public static BinSnapshot Snapshot() { BinReads++; return ReadBin(); }

    private static BinSnapshot ReadBin() => StaRunner.Run(() =>
    {
        var byOriginal = new Dictionary<string, List<BinItem>>(StringComparer.OrdinalIgnoreCase);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!)!;
        dynamic bin = shell.NameSpace(10);   // the Recycle Bin
        foreach (dynamic item in bin.Items())
        {
            string from = (string)bin.GetDetailsOf(item, 1);        // column 1: original location
            string binPath = (string)item.Path;
            string shown = (string)item.Name;
            string ext = Path.GetExtension(binPath);
            if (ext.Length > 0 && !shown.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) shown += ext;   // Explorer may hide extensions
            var original = Path.Combine(from.TrimEnd('\\'), shown);
            if (!byOriginal.TryGetValue(original, out var list)) byOriginal[original] = list = new List<BinItem>();
            list.Add(new BinItem(binPath, original, item));
        }
        return new BinSnapshot(byOriginal);
    });

    /// <summary>Finds the Recycle Bin items that came from <paramref name="originalPath"/>.</summary>
    public static List<BinItem> FindInBin(string originalPath) => Snapshot().Find(originalPath);

    /// <summary>Asks Windows to restore an item exactly as Explorer's "Restore" does.</summary>
    public static void Restore(BinItem item) => StaRunner.Run(() => { item.Handle.InvokeVerb("undelete"); });
}

/// <summary>A look at everything in the Recycle Bin at one moment, read once and used for a whole action (Undo or recovery).</summary>
public sealed class BinSnapshot
{
    private readonly Dictionary<string, List<BinItem>> _byOriginal;
    internal BinSnapshot(Dictionary<string, List<BinItem>> byOriginal) { _byOriginal = byOriginal; }
    public int Count => _byOriginal.Values.Sum(l => l.Count);
    internal List<BinItem> Find(string originalPath) =>
        _byOriginal.TryGetValue(originalPath, out var l) ? l.ToList() : new List<BinItem>();
}
