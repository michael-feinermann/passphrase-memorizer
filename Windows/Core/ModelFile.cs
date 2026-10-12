namespace PassphraseMemorizer.Core;

public static class ModelFile
{
    public const string DefaultGemmaPath = @"D:\KI-Modelle\Ollama\blobs\sha256-0374ce7b0124db9ba96fc649e835c531223ee224a497ce88a374baaea10932ec";
    public static string Validate(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Choose a fully local model file.");
        string full = Path.GetFullPath(path);
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The isolated model must be on a local NTFS drive.");
        foreach (string name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string? cloud = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(cloud) && IsWithin(full, Path.GetFullPath(cloud)))
                throw new IOException("Cloud model locations are unavailable.");
        }
        string[] components = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (components.Any(part => part.Equals("Dropbox", StringComparison.OrdinalIgnoreCase)
            || part.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) || part.Equals("Google Drive", StringComparison.OrdinalIgnoreCase)
            || part.Equals("iCloudDrive", StringComparison.OrdinalIgnoreCase))) throw new IOException("Cloud model locations are unavailable.");
        CheckLocalPath(full);
        using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[4];
        input.ReadExactly(magic);
        if (!magic.SequenceEqual("GGUF"u8) || input.Length < 4096) throw new IOException("Choose a valid GGUF model.");
        return full;
    }

    public static void CheckLocalPath(string full)
    {
        const FileAttributes rejected = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & rejected) != 0) throw new IOException("Linked or non-local paths are unavailable.");
            if (Path.GetPathRoot(current) == current) break;
        }
    }

    internal static bool IsWithin(string path, string root) => path.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
