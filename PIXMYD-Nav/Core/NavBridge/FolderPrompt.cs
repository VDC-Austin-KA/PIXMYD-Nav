using System;
using System.Runtime.InteropServices;

namespace PIXMYD_Nav.Core.NavBridge
{
    /// <summary>
    /// Ask for a folder with the Explorer window, not the tree.
    ///
    /// `System.Windows.Forms.FolderBrowserDialog` on .NET Framework is always
    /// the old `SHBrowseForFolder` control: a narrow tree with no file list, no
    /// address bar, no search, and no way to type or paste a path. That is a
    /// bad instrument for the job it is used for here, which is finding the
    /// folder holding `capture.json` among several dated arrivals -- the one
    /// question the user cannot answer without seeing what is inside.
    ///
    /// Windows has had the answer since Vista: `IFileOpenDialog` with the
    /// `FOS_PICKFOLDERS` option is the ordinary file dialog, showing files,
    /// still returning a folder. .NET Core wired `FolderBrowserDialog` to it;
    /// .NET Framework never did, and this plugin is `net48`.
    ///
    /// So this is the COM declaration for that one call. It is more code than a
    /// NuGet wrapper would be, and it is the right trade inside a Navisworks
    /// add-in: a dependency here has to load into Navisworks' own process
    /// alongside whatever else is already there, and the whole feature is one
    /// dialog.
    ///
    /// Falls back to the old dialog if any of it fails, so a future Windows
    /// that moves the interface leaves the button working rather than dead.
    ///
    /// Navisworks-only in the sense that nothing else uses it; the code itself
    /// is plain Win32 and has no Navisworks in scope.
    /// </summary>
    public static class FolderPrompt
    {
        /// <summary>
        /// Show the picker. Returns the chosen folder, or null if dismissed.
        /// </summary>
        /// <param name="title">Shown in the dialog's title bar.</param>
        /// <param name="startAt">Folder to open at, or null.</param>
        public static string Pick(string title, string startAt)
        {
            try
            {
                string picked = PickWithFileDialog(title, startAt);
                // An empty string is a dismissal, not a failure, so it must not
                // fall through to showing a second dialog.
                return picked;
            }
            catch (Exception)
            {
                return PickWithLegacyDialog(title, startAt);
            }
        }

        private static string PickWithFileDialog(string title, string startAt)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogRcw();
            try
            {
                uint options;
                dialog.GetOptions(out options);
                // PICKFOLDERS is what turns the file dialog into a folder
                // dialog while keeping the file list visible. FORCEFILESYSTEM
                // keeps the result a real path rather than a shell library or a
                // virtual location that has no directory behind it.
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
                if (!string.IsNullOrWhiteSpace(title)) dialog.SetTitle(title);

                if (!string.IsNullOrWhiteSpace(startAt))
                {
                    try
                    {
                        object item;
                        int hr = SHCreateItemFromParsingName(
                            startAt, IntPtr.Zero, typeof(IShellItem).GUID, out item);
                        if (hr == 0 && item != null) dialog.SetFolder((IShellItem)item);
                    }
                    catch (Exception)
                    {
                        // A start folder that no longer exists is not a reason
                        // to refuse to open the dialog at all.
                    }
                }

                int shown = dialog.Show(OwnerWindow());
                if (shown != 0) return null;   // cancelled, or closed

                IShellItem result;
                dialog.GetResult(out result);
                if (result == null) return null;

                string path;
                result.GetDisplayName(SIGDN_FILESYSPATH, out path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        private static string PickWithLegacyDialog(string title, string startAt)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = title;
                if (!string.IsNullOrWhiteSpace(startAt)) dialog.SelectedPath = startAt;
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    ? dialog.SelectedPath
                    : null;
            }
        }

        /// <summary>
        /// The window the dialog is modal to.
        ///
        /// Navisworks' main window, so the dialog cannot end up behind it --
        /// which is what an unowned modal dialog does when the host application
        /// is what has focus, and looks to the user like the button did nothing.
        /// </summary>
        private static IntPtr OwnerWindow()
        {
            try
            {
                System.Windows.Window active = System.Windows.Application.Current != null
                    ? System.Windows.Application.Current.MainWindow
                    : null;
                if (active != null)
                {
                    IntPtr handle = new System.Windows.Interop.WindowInteropHelper(active).Handle;
                    if (handle != IntPtr.Zero) return handle;
                }
            }
            catch (Exception)
            {
            }
            return GetActiveWindow();
        }

        // MARK: - Win32

        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            string path, IntPtr bindingContext,
            [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw
        {
        }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // The vtable order is the contract: every method of IFileDialog has
            // to be declared, in order, even the ones never called, or the slots
            // below it point at the wrong function.
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName(string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem place, int order);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close([MarshalAs(UnmanagedType.Error)] int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindingContext, ref Guid handler, ref Guid interfaceId,
                               out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
