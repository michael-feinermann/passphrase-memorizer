using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PassphraseMemorizer.Core;
using PassphraseMemorizer.Crypto;

namespace PassphraseMemorizer.Runtime;

public sealed class RunnerClient
{
    private static readonly SemaphoreSlim SingleRunner = new(1, 1);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int FrameLimit = 65536;
    public Task<string> GenerateAsync(string modelPath, string prompt, string[] words, CancellationToken cancellationToken,
        Action<GpuExecutionReport>? gpuReport = null, Action? gpuReady = null)
    {
        ArgumentNullException.ThrowIfNull(prompt); ArgumentNullException.ThrowIfNull(words);
        if (words.Length is < 1 or > 128 || words.Any(word => word.Length is < 1 or > 64 || !word.All(char.IsAsciiLetter)))
            throw new ArgumentException("Invalid marked words.");
        string markers = string.Join(" ", words.Select(word => "[" + word + "]"));
        string lastLine = prompt.TrimEnd(' ', '\t', '\r', '\n').Split('\n')[^1].Trim(' ', '\t', '\r');
        if (lastLine != markers || Utf8.GetByteCount(prompt) is < 1 or > FrameLimit)
            throw new ArgumentException("The prompt's last line must contain exactly the marked words.");
        return InvokeAsync(modelPath, prompt, false, cancellationToken, gpuReport, gpuReady);
    }
    // Public fixture only: no secret is transmitted and no model is loaded.
    public Task<string> SelfTestAsync(string modelPath, CancellationToken cancellationToken = default)
        => InvokeAsync(modelPath, null, true, cancellationToken);

    private static async Task<string> InvokeAsync(string modelPath, string? prompt, bool selfTest, CancellationToken caller,
        Action<GpuExecutionReport>? gpuReport = null, Action? gpuReady = null)
    {
        await SingleRunner.WaitAsync(caller).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            return await Task.Run(() => Invoke(modelPath, prompt, selfTest, deadline.Token, gpuReport, gpuReady), deadline.Token).ConfigureAwait(false);
        }
        finally { SingleRunner.Release(); }
    }
    private static string Invoke(string modelPath, string? prompt, bool selfTest, CancellationToken cancellationToken,
        Action<GpuExecutionReport>? gpuReport, Action? gpuReady)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var serialization = new UserRunnerMutex(cancellationToken);
        string root = AppContext.BaseDirectory;
        string native = Path.Combine(root, "native", "WindowsMnemonicRunner.exe");
        using var release = BundleVerifier.OpenAndVerifyRunningExecutable(root);
        release.RequireVerifiedPayload(native);
        string model = ModelFile.Validate(modelPath);
        using var modelLease = SafeFiles.OpenReadLease(model);
        // Persist and read back the exact worker's dedicated-memory policy
        // before its CUDA context is created. No global NVIDIA policy changes.
        NvidiaMemoryPolicy.EnsureForExecutable(native);
        using var profile = new AppContainerProfile();
        nint sid = profile.Sid;
        string probeDirectory = Path.Combine(Path.GetTempPath(), "PassphraseMemorizer.PublicProbe." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDirectory);
        string readProbe = Path.Combine(probeDirectory, "public-read-canary.txt");
        string writeProbe = Path.Combine(probeDirectory, "forbidden-write-canary.txt");
        File.WriteAllText(readProbe, "Public isolation probe. Contains no credentials.");
        try
        {
            using var access = new ReadAccessLease(sid);
            access.Grant(native, true); access.Grant(model, false);
            // CUDA dependencies are part of the same dual-signed inventory.
            foreach (string dependency in Directory.EnumerateFiles(Path.GetDirectoryName(native)!, "*.dll"))
            {
                release.RequireVerifiedPayload(dependency);
                access.Grant(dependency, true);
            }
            // Bundle leases prevent replacement; explicit applicationName and a
            // checked suspended image bind verification to the actual child.
            using var child = SandboxProcess.Start(native, [selfTest ? "--self-test" : "--gpu", model, readProbe, writeProbe, profile.WriteProbe, profile.StorageProbe, profile.PrivateRegistryProbe], sid);
            using var cancellation = cancellationToken.Register(child.Cancel);
            byte[]? request = null;
            byte[]? response = null;
            try
            {
                Span<byte> ready = stackalloc byte[4]; child.Output.ReadExactly(ready);
                if (!ready.SequenceEqual("MSAI"u8)) throw new IOException("The runner did not verify its isolation.");
                if (!selfTest) gpuReady?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (!selfTest)
                {
                    // This is the first point at which any secret byte enters
                    // the child. Neither arguments nor environment contain it.
                    request = Utf8.GetBytes(prompt!);
                    Span<byte> header = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)request.Length);
                    child.Input.Write(header); child.Input.Write(request); child.Input.Flush();
                    CryptographicOperations.ZeroMemory(request); request = null;
                }
                child.Input.Dispose();
                if (!selfTest)
                {
                    Span<byte> proofHeader = stackalloc byte[4]; child.Output.ReadExactly(proofHeader);
                    uint proofLength = BinaryPrimitives.ReadUInt32LittleEndian(proofHeader);
                    if (proofLength is 0 or > 4096) throw new IOException("Invalid GPU execution proof frame.");
                    byte[] proofBytes = new byte[proofLength]; child.Output.ReadExactly(proofBytes);
                    var proof = GpuExecutionReport.Parse(proofBytes);
                    gpuReport?.Invoke(proof);
                }
                Span<byte> outputHeader = stackalloc byte[4]; child.Output.ReadExactly(outputHeader);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(outputHeader);
                if (length is 0 or > FrameLimit) throw new IOException("Invalid isolated response frame.");
                response = new byte[length]; child.Output.ReadExactly(response);
                if (child.Output.ReadByte() != -1 || child.Error.ReadByte() != -1 || child.Wait() != 0)
                    throw new IOException("The isolated runner failed.");
                cancellationToken.ThrowIfCancellationRequested();
                string story = Utf8.GetString(response);
                if (story.IndexOf('\0') >= 0) throw new IOException("Invalid isolated response text.");
                return story;
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
            finally
            {
                if (request != null) CryptographicOperations.ZeroMemory(request);
                if (response != null) CryptographicOperations.ZeroMemory(response);
            }
        }
        finally
        {
            // Only this known public canary directory is removed. ACL grants
            // are removed after the job terminates, including cancellation.
            File.Delete(readProbe); File.Delete(writeProbe); Directory.Delete(probeDirectory);
        }
    }
}
