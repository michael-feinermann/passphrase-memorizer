using System.Text.Json;

namespace PassphraseMemorizer.Core;

/// <summary>The complete persistence allowlist. Secrets and rendered text have no persisted fields.</summary>
public sealed class Preferences
{
    public string Language { get; set; } = "en";
    public double WindowWidth { get; set; } = 940;
    public double WindowHeight { get; set; } = 880;
    public string ModelPath { get; set; } = "";
    public static string StoragePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Passphrase Memorizer", "preferences.json");

    public static Preferences Load() => LoadFrom(StoragePath);
    public void Save() => SaveTo(StoragePath);

    public static Preferences LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            ModelFile.CheckLocalPath(Path.GetFullPath(path));
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 32768) return new();
            using var json = JsonDocument.Parse(file);
            var root = json.RootElement;
            var result = new Preferences();
            if (root.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String && language.GetString() is "en" or "de")
                result.Language = language.GetString()!;
            if (root.TryGetProperty("windowWidth", out var width) && width.TryGetDouble(out double w) && double.IsFinite(w))
                result.WindowWidth = Math.Clamp(w, 640, 3840);
            if (root.TryGetProperty("windowHeight", out var height) && height.TryGetDouble(out double h) && double.IsFinite(h))
                result.WindowHeight = Math.Clamp(h, 600, 2160);
            if (root.TryGetProperty("modelPath", out var model) && model.ValueKind == JsonValueKind.String && model.GetString() is string m && m.Length <= 32768)
                result.ModelPath = m;
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return new(); }
    }

    public void SaveTo(string path)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        ModelFile.CheckLocalPath(directory);
        if (File.Exists(full)) ModelFile.CheckLocalPath(full);
        // Explicit anonymous shape discards unknown fields introduced by frameworks or older builds.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            language = Language is "de" ? "de" : "en",
            windowWidth = double.IsFinite(WindowWidth) ? Math.Clamp(WindowWidth, 640, 3840) : 940,
            windowHeight = double.IsFinite(WindowHeight) ? Math.Clamp(WindowHeight, 600, 2160) : 880,
            modelPath = ModelPath.Length <= 32768 ? ModelPath : ""
        });
        string temporary = Path.Combine(directory, "preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
