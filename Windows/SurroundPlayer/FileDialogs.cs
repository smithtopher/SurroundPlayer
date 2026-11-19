// Windows' own open dialog (IFileOpenDialog) for files and folders. WinUI's pickers misbehave
// in this unpackaged app: FolderPicker throws E_FAIL after a folder is chosen. The shell dialog
// also lets the user choose several folders at once.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SurroundPlayer;

internal static class FileDialogs
{
    /// Shows the dialog modal to `owner`. Folders: choose folders instead of files. Returns the
    /// chosen paths, or an empty list when cancelled.
    public static List<string> Open(IntPtr owner, string title, bool folders, bool multiple,
                                    (string Name, string Pattern)[]? filters = null)
    {
        var result = new List<string>();
        var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
        try
        {
            dialog.GetOptions(out var opts);
            opts |= FOS.FORCEFILESYSTEM | FOS.PATHMUSTEXIST | FOS.NOCHANGEDIR;
            if (folders) opts |= FOS.PICKFOLDERS;
            else opts |= FOS.FILEMUSTEXIST;
            if (multiple) opts |= FOS.ALLOWMULTISELECT;
            dialog.SetOptions(opts);
            dialog.SetTitle(title);
            if (!folders && filters is { Length: > 0 })
            {
                var specs = new COMDLG_FILTERSPEC[filters.Length];
                for (int i = 0; i < filters.Length; i++) specs[i] = new COMDLG_FILTERSPEC { pszName = filters[i].Name, pszSpec = filters[i].Pattern };
                dialog.SetFileTypes((uint)specs.Length, specs);
            }
            int hr = dialog.Show(owner);
            if (hr == unchecked((int)0x800704C7)) return result;   // cancelled
            Marshal.ThrowExceptionForHR(hr);
            dialog.GetResults(out var items);
            items.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out var item);
                item.GetDisplayName(SIGDN.FILESYSPATH, out var p);
                if (p != IntPtr.Zero)
                {
                    var path = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(path)) result.Add(path);
                }
                Marshal.ReleaseComObject(item);
            }
            Marshal.ReleaseComObject(items);
        }
        finally { Marshal.ReleaseComObject(dialog); }
        return result;
    }

    // ------------------------------------------------------------------ COM declarations

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW { }

    [Flags]
    private enum FOS : uint
    {
        NOCHANGEDIR = 0x8, PICKFOLDERS = 0x20, FORCEFILESYSTEM = 0x40, ALLOWMULTISELECT = 0x200,
        PATHMUSTEXIST = 0x800, FILEMUSTEXIST = 0x1000,
    }

    private enum SIGDN : uint { FILESYSPATH = 0x80058000 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);
        // IFileDialog
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FOS fos);
        void GetOptions(out FOS pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        // IFileOpenDialog
        void GetResults(out IShellItemArray ppenum);
        void GetSelectedItems(out IShellItemArray ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }
}
