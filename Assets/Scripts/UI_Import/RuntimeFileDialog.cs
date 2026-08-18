using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

// Native Windows file picker for standalone builds.
// Uses the Win32 OPENFILENAMEW structure exactly as documented by Microsoft.
public static class RuntimeFileDialog
{
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    const int OFN_OVERWRITEPROMPT = 0x00000002;
    const int OFN_HIDEREADONLY = 0x00000004;
    const int OFN_NOCHANGEDIR = 0x00000008;
    const int OFN_PATHMUSTEXIST = 0x00000800;
    const int OFN_FILEMUSTEXIST = 0x00001000;
    const int OFN_EXPLORER = 0x00080000;
    const uint MB_YESNO = 0x00000004;
    const uint MB_ICONQUESTION = 0x00000020;
    const int IDYES = 6;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetOpenFileName(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetSaveFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSaveFileName(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", EntryPoint = "CommDlgExtendedError")]
    static extern uint CommDlgExtendedError();

    [DllImport("user32.dll")]
    static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
#endif

    public static string OpenFile(string title, string filter, string defaultExtension = null)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        return ShowDialog(false, title, filter, string.Empty, defaultExtension);
#else
        Debug.LogWarning("RuntimeFileDialog currently supports standalone Windows builds only.");
        return string.Empty;
#endif
    }

    public static string SaveFile(string title, string filter, string defaultFileName, string defaultExtension)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        return ShowDialog(true, title, filter, defaultFileName ?? string.Empty, defaultExtension);
#else
        Debug.LogWarning("RuntimeFileDialog currently supports standalone Windows builds only.");
        return string.Empty;
#endif
    }

    public static bool ConfirmOptionalAlbedo()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        int result = MessageBox(
            GetActiveWindow(),
            "Would you like to add an albedo texture to this head?\n\nYes = Choose Albedo\nNo = Skip (use HairBrush grey)",
            "HairBrush - Optional Albedo",
            MB_YESNO | MB_ICONQUESTION);
        return result == IDYES;
#else
        return false;
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    static string ShowDialog(bool save, string title, string filter, string initialFile, string defaultExtension)
    {
        const int maxChars = 32768;
        IntPtr fileBuffer = IntPtr.Zero;
        IntPtr titleBuffer = IntPtr.Zero;

        try
        {
            fileBuffer = Marshal.AllocHGlobal(maxChars * sizeof(char));
            titleBuffer = Marshal.AllocHGlobal(1024 * sizeof(char));

            // OPENFILENAME requires the first filename character to be NUL when no initial name is supplied.
            for (int i = 0; i < maxChars; i++) Marshal.WriteInt16(fileBuffer, i * sizeof(char), 0);
            for (int i = 0; i < 1024; i++) Marshal.WriteInt16(titleBuffer, i * sizeof(char), 0);

            if (!string.IsNullOrEmpty(initialFile))
            {
                byte[] bytes = Encoding.Unicode.GetBytes(initialFile + "\0");
                Marshal.Copy(bytes, 0, fileBuffer, Math.Min(bytes.Length, maxChars * sizeof(char)));
            }

            OpenFileName dialog = new OpenFileName
            {
                lStructSize = Marshal.SizeOf(typeof(OpenFileName)),
                hwndOwner = GetActiveWindow(),
                hInstance = IntPtr.Zero,
                lpstrFilter = EnsureDoubleNull(filter),
                lpstrCustomFilter = IntPtr.Zero,
                nMaxCustFilter = 0,
                nFilterIndex = 1,
                lpstrFile = fileBuffer,
                nMaxFile = maxChars,
                lpstrFileTitle = titleBuffer,
                nMaxFileTitle = 1024,
                lpstrInitialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                lpstrTitle = title,
                Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR |
                        (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST | OFN_HIDEREADONLY),
                nFileOffset = 0,
                nFileExtension = 0,
                lpstrDefExt = defaultExtension,
                lCustData = IntPtr.Zero,
                lpfnHook = IntPtr.Zero,
                lpTemplateName = IntPtr.Zero,
                pvReserved = IntPtr.Zero,
                dwReserved = 0,
                FlagsEx = 0
            };

            bool ok = save ? GetSaveFileName(ref dialog) : GetOpenFileName(ref dialog);
            if (ok)
                return Marshal.PtrToStringUni(fileBuffer) ?? string.Empty;

            uint error = CommDlgExtendedError();
            // Zero means the user cancelled. Any non-zero value is a real native dialog failure.
            if (error != 0)
                Debug.LogError("HairBrush Windows file dialog failed. CommDlgExtendedError=0x" + error.ToString("X"));

            return string.Empty;
        }
        catch (Exception ex)
        {
            Debug.LogError("HairBrush Windows file dialog exception: " + ex);
            return string.Empty;
        }
        finally
        {
            if (fileBuffer != IntPtr.Zero) Marshal.FreeHGlobal(fileBuffer);
            if (titleBuffer != IntPtr.Zero) Marshal.FreeHGlobal(titleBuffer);
        }
    }

    static string EnsureDoubleNull(string filter)
    {
        if (string.IsNullOrEmpty(filter)) return "All Files\0*.*\0\0";
        return filter.EndsWith("\0\0", StringComparison.Ordinal)
            ? filter
            : filter.TrimEnd('\0') + "\0\0";
    }
#endif
}
