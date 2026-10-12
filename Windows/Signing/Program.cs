using PassphraseMemorizer.Crypto;
using PassphraseMemorizer.WindowsSigning;

try
{
    if (args.Length == 0) throw new ArgumentException("Commands: generate --key-directory <path> --public-directory <path>; sign --bundle <path> --key-directory <path>; verify --bundle <path>; sign-file --target <path> --key-directory <path>; verify-file --target <path>; selftest [--reference-library <path>] [--skein-vectors <path>].");
    Dictionary<string, string> options = new(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException("Invalid or repeated command option.");
    }
    string Require(string name) => options.TryGetValue(name, out var value) ? value : throw new ArgumentException("Missing --" + name);
    void Allow(params string[] names) { if (options.Keys.Any(k => !names.Contains(k, StringComparer.Ordinal))) throw new ArgumentException("Unsupported option for this command."); }
    switch (args[0])
    {
        case "generate": Allow("key-directory", "public-directory"); WindowsKeyStore.Generate(Require("key-directory"), Require("public-directory")); break;
        case "sign": Allow("bundle", "key-directory"); BundleSigner.Sign(Require("bundle"), Require("key-directory")); break;
        case "verify": Allow("bundle"); using (var lease = BundleVerifier.OpenAndVerify(Require("bundle"), ReleaseTrust.LoadEmbedded())) Console.WriteLine("Both signatures, complete inventory and three hashes verified."); break;
        case "sign-file": Allow("target", "key-directory"); BundleSigner.SignFile(Require("target"), Require("key-directory")); break;
        case "verify-file": Allow("target"); BundleSigner.VerifyFile(Require("target")); break;
        case "selftest": Allow("reference-library", "skein-vectors"); SelfTests.Run(options.GetValueOrDefault("reference-library"), options.GetValueOrDefault("skein-vectors")); break;
        default: throw new ArgumentException("Unknown signer command.");
    }
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or PlatformNotSupportedException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
