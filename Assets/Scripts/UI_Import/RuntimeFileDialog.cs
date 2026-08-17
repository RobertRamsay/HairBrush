using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

// Minimal native file picker for standalone Windows builds. Keeps HairBrush free of a
// third-party file-browser package while using the normal Windows Explorer dialogs.
public static class RuntimeFileDialog
{
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    const int OFN_OVERWRITEPROMPT = 0x00000002;
    const int OFN_HIDEREADONLY = 0x00000004;
    const int OFN_NOCHANGEDIR = 0x00000008;
    const int OFN_PATHMUSTEXIST = 0x00000800;
    const int OFN_FILEMUSTEXIST = 0x00001000;
    const int OFN_EXPLORER = 0x00080000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    class OpenFileName
    {
        public int lStructSize = Marshal.SizeOf(typeof(OpenFileName));
        public IntPtr hwndOwner = IntPtr.Zero;
        public IntPtr hInstance = IntPtr.Zero;
        public string lpstrFilter = null;
        public string lpstrCustomFilter = null;
        public int nMaxCustFilter = 0;
        public int nFilterIndex = 1;
        public StringBuilder lpstrFile = new StringBuilder(4096);
        public int nMaxFile = 4096;
        public StringBuilder lpstrFileTitle = new StringBuilder(512);
        public int nMaxFileTitle = 512;
        public string lpstrInitialDir = null;
        public string lpstrTitle = null;
        public int Flags = 0;
        public short nFileOffset = 0;
        public short nFileExtension = 0;
        public string lpstrDefExt = null;
        public IntPtr lCustData = IntPtr.Zero;
        public IntPtr lpfnHook = IntPtr.Zero;
        public string lpTemplateName = null;
        public IntPtr pvReserved = IntPtr.Zero;
        public int dwReserved = 0;
        public int FlagsEx = 0;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetSaveFileName([In, Out] OpenFileName ofn);
#endif

    public static string OpenFile(string title, string filter, string defaultExtension = null)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        OpenFileName dialog = NewDialog(title, filter, defaultExtension);
        dialog.Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR;
        return GetOpenFileName(dialog) ? dialog.lpstrFile.ToString() : string.Empty;
#else
        Debug.LogWarning("RuntimeFileDialog currently supports standalone Windows builds only.");
        return string.Empty;
#endif
    }

    public static string SaveFile(string title, string filter, string defaultFileName, string defaultExtension)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        OpenFileName dialog = NewDialog(title, filter, defaultExtension);
        dialog.lpstrFile.Append(defaultFileName ?? string.Empty);
        dialog.Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR;
        return GetSaveFileName(dialog) ? dialog.lpstrFile.ToString() : string.Empty;
#else
        Debug.LogWarning("RuntimeFileDialog currently supports standalone Windows builds only.");
        return string.Empty;
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    static OpenFileName NewDialog(string title, string filter, string defaultExtension)
    {
        return new OpenFileName
        {
            lpstrTitle = title,
            lpstrFilter = EnsureDoubleNull(filter),
            lpstrDefExt = defaultExtension,
            lpstrInitialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
    }

    static string EnsureDoubleNull(string filter)
    {
        if (string.IsNullOrEmpty(filter)) return "All Files\0*.*\0\0";
        return filter.EndsWith("\0\0", StringComparison.Ordinal) ? filter : filter.TrimEnd('\0') + "\0\0";
    }
#endif
}
