using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PassphraseMemorizer.Crypto;

public static class SafeFiles
{
    public static string CanonicalPath(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    public static void RejectReparsePath(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        for (string? current = full; current != null; current = System.IO.Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolic links, junctions and reparse paths are forbidden for release or signing files.");
        }
    }

    public static FileStream OpenReadLease(string path)
    {
        RejectReparsePath(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new IOException("A release file must be a regular file.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var finalName = new StringBuilder(32768);
                uint count = GetFinalPathNameByHandle(stream.SafeFileHandle, finalName, (uint)finalName.Capacity, 0);
                if (count == 0 || count >= finalName.Capacity) throw new IOException("The opened release file path could not be established.");
                string actual = finalName.ToString();
                if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
                else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
                if (!CanonicalPath(actual).Equals(CanonicalPath(path), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The opened release file resolved through an unexpected path.");
            }
            RejectReparsePath(path);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public static string[] EnumeratePayload(string root)
    {
        root = CanonicalPath(root);
        RejectReparsePath(root);
        List<string> files = [];
        void Visit(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparsePath(path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) Visit(path);
                else
                {
                    string relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                    BundleFormat.ValidateRelativePath(relative);
                    if (!BundleFormat.IsMetadata(relative)) files.Add(relative);
                }
            }
        }
        Visit(root);
        files.Sort(StringComparer.Ordinal);
        if (files.Count != files.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidDataException("Release paths collide on Windows.");
        return files.ToArray();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint capacity, uint flags);
}
