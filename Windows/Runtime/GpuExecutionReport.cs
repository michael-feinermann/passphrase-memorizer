using System.Text.Json;
using System.Text.Json.Serialization;

namespace PassphraseMemorizer.Runtime;

// Public placement metadata only. No prompt, generated text or token values.
public sealed record GpuExecutionReport(string Backend, string Device, ulong GpuModelBytes, ulong CpuModelBytes,
    ulong GpuKvBytes, ulong CpuKvBytes, ulong GpuComputeBytes, ulong CpuComputeBufferBytes,
    ulong ContextTokens, ulong FreeVramBeforeBytes, ulong TotalVramBytes,
    ulong GpuOperationCount, ulong CpuModelOperationCount)
{
    internal static GpuExecutionReport Parse(ReadOnlySpan<byte> bytes)
    {
        var report = JsonSerializer.Deserialize<GpuExecutionReport>(bytes, new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            RespectRequiredConstructorParameters = true,
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 2
        }) ?? throw new IOException("The GPU execution proof is missing.");
        if (report.Backend != "CUDA" || string.IsNullOrWhiteSpace(report.Device) || report.Device.Length > 255 ||
            report.Device.Any(c => c < 32 || c > 126) || report.GpuModelBytes == 0 || report.CpuModelBytes != 0 ||
            report.GpuKvBytes == 0 || report.CpuKvBytes != 0 || report.GpuComputeBytes == 0 ||
            report.CpuComputeBufferBytes > 16UL * 1024 * 1024 || report.ContextTokens is < 4096 or > 8192 ||
            report.TotalVramBytes is 0 or > (1UL << 40) || report.FreeVramBeforeBytes == 0 || report.FreeVramBeforeBytes > report.TotalVramBytes ||
            report.GpuModelBytes > report.TotalVramBytes || report.GpuKvBytes > report.TotalVramBytes ||
            report.GpuComputeBytes > report.TotalVramBytes || report.GpuOperationCount == 0 || report.CpuModelOperationCount != 0)
            throw new IOException("The required GPU-only execution was not verified.");
        if (report.GpuModelBytes + report.GpuKvBytes + report.GpuComputeBytes > report.FreeVramBeforeBytes)
            throw new IOException("The GPU allocations exceeded the available dedicated memory.");
        return report;
    }
}
