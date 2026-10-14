using System.Diagnostics;
using System.Text.Json;
using PassphraseMemorizer.Core;
using PassphraseMemorizer.Runtime;

namespace PassphraseMemorizer.App;

internal static class PublicFixtureTests
{
    // Deliberately public dictionary fixtures; no user phrase is read or persisted.
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--smoke-test"))
        {
            var window = new MainWindow(new Preferences { Language = "en" }, true);
            var checks = window.RunPublicSmokeTests();
            var renderIndex = Array.IndexOf(args, "--render-public-ui");
            if (renderIndex >= 0)
            {
                if (renderIndex + 1 >= args.Length) throw new ArgumentException("A public fixture image path is required.");
                window.RenderPublicFixture(args[renderIndex + 1]);
            }
            window.ClearForShutdown();
            Console.WriteLine($"PUBLIC_FIXTURE_RESULT PASS ui_checks={checks}");
            return 0;
        }

        var modelIndex = Array.IndexOf(args, "--model");
        var modelPath = modelIndex >= 0 && modelIndex + 1 < args.Length ? args[modelIndex + 1] : MainWindow.DefaultModelPath;
        modelPath = ModelFile.Validate(modelPath);
        var clock = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        if (args.Contains("--sandbox-test"))
        {
            try
            {
                var sandboxResult = await new RunnerClient().SelfTestAsync(modelPath, cancellation.Token);
                using var report = JsonDocument.Parse(sandboxResult);
                var root = report.RootElement;
                bool passed = root.GetProperty("sandbox").GetString() == "passed" && root.GetProperty("backend").GetString() == "CUDA" &&
                    root.GetProperty("ipv4").GetString() == "denied" && root.GetProperty("ipv6").GetString() == "denied" &&
                    root.GetProperty("writes").GetString() == "denied" && root.GetProperty("unrelated_reads").GetString() == "denied" &&
                    root.GetProperty("model_writes").GetString() == "denied" &&
                    root.GetProperty("loopback_ipv4").GetString() == "denied" && root.GetProperty("loopback_ipv6").GetString() == "denied" &&
                    root.GetProperty("profile_writes").GetString() == "denied" && root.GetProperty("storage_writes").GetString() == "denied" &&
                    root.GetProperty("private_registry_reads").GetString() == "denied" && root.GetProperty("child_processes").GetString() == "denied" &&
                    root.GetProperty("capabilities").GetInt32() == 1 && root.GetProperty("registryReadOnly").GetBoolean() &&
                    root.GetProperty("zeroNetworkCapabilities").GetBoolean();
                if (!passed) { Console.WriteLine("PUBLIC_FIXTURE_RESULT FAIL sandbox_response"); return 1; }
                Console.WriteLine("PUBLIC_FIXTURE_RESULT PASS sandbox_network_read_write_denial");
                return 0;
            }
            catch { Console.WriteLine("PUBLIC_FIXTURE_RESULT FAIL sandbox"); return 1; }
        }
        bool gpuReadyObserved = false;
        var cancelClock = new Stopwatch();
        var lexicon = new PhraseLexicon();
        using var phrase = lexicon.Parse("abandon ability able", PhraseFormat.Bip39);
        // A short public fixture exercises real inference and ordered markers
        // without requesting the application's full 1600-word output budget.
        var prompt = phrase.BuildPrompt(LiteraryStyle.ShortStory, "en")
            .Replace("Keep the text under 1600 words.", "Keep the text under 60 words.", StringComparison.Ordinal);
        string result = "";
        GpuExecutionReport? placement = null;
        try
        {
            result = await new RunnerClient().GenerateAsync(modelPath, prompt, phrase.Words, cancellation.Token, proof => placement = proof,
                () => { gpuReadyObserved = true; if (args.Contains("--cancel-test")) { cancelClock.Start(); cancellation.CancelAfter(TimeSpan.FromMilliseconds(200)); } });
            if (args.Contains("--cancel-test"))
            {
                Console.WriteLine("PUBLIC_FIXTURE_RESULT FAIL cancellation_not_observed");
                return 1;
            }
            if (!phrase.VerifyStory(result))
            {
                Console.WriteLine($"PUBLIC_FIXTURE_RESULT FAIL story_validation elapsed_seconds={clock.Elapsed.TotalSeconds:F1}");
                return 1;
            }
            if (placement is null) { Console.WriteLine("PUBLIC_FIXTURE_RESULT FAIL gpu_proof"); return 1; }
            Console.WriteLine("PUBLIC_GPU_PLACEMENT " + JsonSerializer.Serialize(placement));
            Console.WriteLine($"PUBLIC_FIXTURE_RESULT PASS native_generation=True marked_words=3 elapsed_seconds={clock.Elapsed.TotalSeconds:F1}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            if (args.Contains("--cancel-test") && gpuReadyObserved && cancelClock.Elapsed < TimeSpan.FromSeconds(5))
            {
                Console.WriteLine($"PUBLIC_FIXTURE_RESULT PASS cancelled gpu_ready=True cancellation_seconds={cancelClock.Elapsed.TotalSeconds:F1} elapsed_seconds={clock.Elapsed.TotalSeconds:F1}");
                return 0;
            }
            Console.WriteLine("PUBLIC_FIXTURE_RESULT FAIL cancelled_or_deadline");
            return 1;
        }
        catch
        {
            Console.WriteLine("PUBLIC_FIXTURE_RESULT FAIL isolated_runtime");
            return 1;
        }
        finally { prompt = ""; result = ""; }
    }
}
