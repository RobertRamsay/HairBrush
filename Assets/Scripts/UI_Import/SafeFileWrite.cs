using System;
using System.IO;
using System.Text;

// Crash-safe file writes.
//
// File.WriteAllText truncates the target first and then writes into it, so a crash, a power cut
// or a full disk partway through leaves a half-written file where the user's only copy used to
// be. Every save in HairBrush goes through here instead:
//
//   1. the whole payload is written to "<path>.tmp" beside the target and flushed to disk;
//   2. only then is the tmp swapped over the target in one step (File.Replace), optionally
//      keeping the previous version as "<path>.bak".
//
// If anything fails, the original file is untouched and the exception reaches the caller, who
// is expected to tell the user. The tmp is cleaned up on the way out.
public static class SafeFileWrite
{
    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static void WriteAllText(string path, string contents, bool keepBackup = false, Encoding encoding = null)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("No file path given.", nameof(path));

        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        // Same folder as the target, so the final swap is a rename on one volume, never a copy.
        string tmp = full + ".tmp";
        try
        {
            using (FileStream stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, encoding ?? Utf8NoBom))
            {
                writer.Write(contents ?? string.Empty);
                writer.Flush();
                stream.Flush(true);
            }

            if (!File.Exists(full))
            {
                File.Move(tmp, full);
                return;
            }

            string backup = keepBackup ? full + ".bak" : null;
            try
            {
                File.Replace(tmp, full, backup, true);
            }
            catch (Exception replaceFailure) when (replaceFailure is PlatformNotSupportedException || replaceFailure is IOException)
            {
                // Some file systems (FAT, some network shares) do not support Replace. The tmp is
                // already complete on disk, so fall back to copying it over: not atomic, but the
                // window is a local copy rather than the whole serialize-and-write.
                if (backup != null) File.Copy(full, backup, true);
                File.Copy(tmp, full, true);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort - a stray .tmp is harmless */ }
        }
    }
}
