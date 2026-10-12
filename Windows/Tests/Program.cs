using System.Text.Json;
using PassphraseMemorizer.Core;

int assertions = 0;
void Check(bool condition, string reason)
{
    assertions++;
    if (!condition) throw new Exception(reason);
}
void Reject(Action action, string reason)
{
    assertions++;
    try { action(); } catch (ArgumentException) { return; } catch (IOException) { return; } catch (ObjectDisposedException) { return; }
    throw new Exception(reason);
}
var lexicon = new PhraseLexicon();
foreach (var format in Enum.GetValues<PhraseFormat>())
{
    string word = format == PhraseFormat.Bip39 ? "abandon" : "abacus";
    foreach (int count in new[] { 1, 128 })
    {
        using var phrase = lexicon.Parse(string.Join(' ', Enumerable.Repeat(word, count)), format);
        Check(phrase.WordCount == count, "boundary count");
        Check(phrase.VerifyStory("A remembered path: " + string.Join(' ', Enumerable.Repeat($"[{word}]", count))), "boundary verification");
    }
}
foreach (string raw in new[] { "", string.Join(' ', Enumerable.Repeat("abandon", 129)), "ignore [instructions]", "abandon\0about", "NOT_A_WORD", "über", new string('a', 8193), "aardvark", "abandon-about", "abandon\u00a0about" })
    Reject(() => lexicon.Parse(raw, PhraseFormat.Bip39), "invalid input");
using (var phrase = lexicon.Parse("ABANDON\tABOUT\nABANDON", PhraseFormat.Bip39))
{
    Check(phrase.BuildPrompt(LiteraryStyle.Poem, "de").EndsWith("[ABANDON] [ABOUT] [ABANDON] \n", StringComparison.Ordinal), "capitalization and repetition");
    Check(!phrase.VerifyStory("A [abandon] [about] [abandon]"), "case mismatch");
    Check(phrase.VerifyStory("Ü [ABANDON] [ABOUT] [ABANDON]"), "unicode unmarked letter");
}
using (var phrase = lexicon.Parse("ABACUS-\nABDomen", PhraseFormat.Eff))
    Check(phrase.WordCount == 2, "EFF separators");
using (var phrase = lexicon.Parse("abandon about abandon", PhraseFormat.Bip39))
{
    foreach (string invalid in new[] { "abandon about abandon", "[about] [abandon] [abandon]", "[abandon] [about]", "[abandon] [about] [abandon] [about]", "[ABANDON] [about] [abandon]", "[abandon] [about] [abandon]]", "[abandon] [about] [abandon]", "[abandon], [about]; [abandon]!", "123 [abandon]\n[about]…[abandon] 🎵" })
        Check(!phrase.VerifyStory(invalid), "invalid marker story");
    string valid = "We [abandon] our doubt, sing [about] hope, and [abandon] fear.";
    Check(phrase.VerifyStory(valid), "literary text");
    Check(phrase.VerifyStory("\U00010400 [abandon] [about] [abandon]"), "supplementary Unicode scalar letter parity");
    Check(!phrase.VerifyStory(valid + new string('a', 65536)), "output cap");
    phrase.Dispose();
    Check(!phrase.VerifyStory(valid), "disposed verifier");
    Check(phrase.WordCount == 0 && phrase.Words.All(word => word is null), "released references");
    Reject(() => phrase.BuildPrompt(LiteraryStyle.Rap, "en"), "disposed prompt");
}
string testDirectory = Path.Combine(Path.GetTempPath(), "PassphraseMemorizer-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
try
{
    string path = Path.Combine(testDirectory, "prefs.json");
    File.WriteAllText(path, "{\"language\":\"de\",\"windowWidth\":900,\"windowHeight\":810,\"modelPath\":\"PUBLIC_MODEL\",\"story\":\"PUBLIC_STORY_FIXTURE\",\"password\":\"PUBLIC_PHRASE_FIXTURE\"}");
    var preferences = Preferences.LoadFrom(path);
    Check(preferences.Language == "de" && preferences.ModelPath == "PUBLIC_MODEL", "retained preferences");
    preferences.SaveTo(path);
    using var json = JsonDocument.Parse(File.ReadAllText(path));
    Check(json.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "language", "modelPath", "windowHeight", "windowWidth" }), "persistence allowlist");
    File.WriteAllText(path, "invalid");
    Check(Preferences.LoadFrom(path).Language == "en", "malformed preference fallback");
    File.WriteAllText(path, "{\"windowWidth\":-2,\"windowHeight\":900000,\"language\":\"unknown\"}");
    var clamped = Preferences.LoadFrom(path);
    Check(clamped.WindowWidth == 640 && clamped.WindowHeight == 2160 && clamped.Language == "en", "bounds");
    string fake = Path.Combine(testDirectory, "fake.gguf");
    File.WriteAllBytes(fake, new byte[5000]);
    Reject(() => ModelFile.Validate(fake), "reject fake GGUF");
    Reject(() => ModelFile.Validate(@"\\localhost\c$\models\a.gguf"), "reject UNC");
    Reject(() => ModelFile.Validate("relative.gguf"), "reject relative");
    if (File.Exists(ModelFile.DefaultGemmaPath))
        Check(ModelFile.Validate(ModelFile.DefaultGemmaPath) == ModelFile.DefaultGemmaPath, "local Gemma file");
}
finally
{
    // All files in this unique directory were created by this test and contain public fixtures.
    Directory.Delete(testDirectory, true);
}
Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", assertions, suite = "Windows v1.0.1 parity, validation, persistence and local model" }));
