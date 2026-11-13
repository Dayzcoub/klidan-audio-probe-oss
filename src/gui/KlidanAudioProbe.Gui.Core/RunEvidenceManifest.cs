using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading;

namespace KlidanAudioProbe.Gui.Core;

public static class RunEvidenceManifest
{
    public static async Task WriteAsync(string rootDirectory, Guid runId, CancellationToken cancellationToken = default, Func<string, byte[]>? readAllBytes = null,
        Func<string, byte[], CancellationToken, Task>? writeNew = null)
    {
        readAllBytes ??= File.ReadAllBytes;
        writeNew ??= async (path, bytes, ct) =>
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false); stream.Flush(true);
        };
        var manifestPath = Path.Combine(rootDirectory, "run-manifest.json");
        var entries = Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
            .Select(path => new { path = Path.GetRelativePath(rootDirectory, path), sha256 = Convert.ToHexStringLower(SHA256.HashData(readAllBytes(path))) })
            .OrderBy(entry => entry.path, StringComparer.Ordinal).ToArray();
        using var start = JsonDocument.Parse(readAllBytes(Path.Combine(rootDirectory, "run-start.json")));
        var captureId = start.RootElement.GetProperty("captureEndpointId").GetString() ?? throw new InvalidDataException("Capture endpoint identity is missing.");
        var renderId = start.RootElement.GetProperty("renderEndpointId").GetString() ?? throw new InvalidDataException("Render endpoint identity is missing.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, runId, captureEndpointId = captureId, renderEndpointId = renderId, files = entries }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await writeNew(manifestPath, bytes, cancellationToken).ConfigureAwait(false);
        Verify(rootDirectory, manifestPath, runId, readAllBytes);
    }

    public static void Verify(string rootDirectory, string manifestPath, Guid runId, Func<string, byte[]>? readAllBytes = null)
    {
        readAllBytes ??= File.ReadAllBytes;
        var root = Path.GetFullPath(rootDirectory) + Path.DirectorySeparatorChar;
        using var document = JsonDocument.Parse(readAllBytes(manifestPath));
        var json = document.RootElement;
        if (json.GetProperty("runId").GetGuid() != runId) throw new InvalidDataException("Run manifest ID mismatch.");
        var startPath = Path.Combine(rootDirectory, "run-start.json");
        using var start = JsonDocument.Parse(readAllBytes(startPath));
        var startJson = start.RootElement;
        if (startJson.GetProperty("runId").GetGuid() != runId) throw new InvalidDataException("Run start ID mismatch.");
        var captureId = startJson.GetProperty("captureEndpointId").GetString() ?? throw new InvalidDataException("Capture endpoint identity is missing.");
        var renderId = startJson.GetProperty("renderEndpointId").GetString() ?? throw new InvalidDataException("Render endpoint identity is missing.");
        if (json.GetProperty("captureEndpointId").GetString() != captureId || json.GetProperty("renderEndpointId").GetString() != renderId)
            throw new InvalidDataException("Run manifest endpoint pair mismatch.");
        var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        foreach (var item in json.GetProperty("files").EnumerateArray())
        {
            var relative = item.GetProperty("path").GetString() ?? throw new InvalidDataException("Run manifest path is missing.");
            if (Path.IsPathRooted(relative) || !indexed.Add(relative) || previousPath is not null && StringComparer.Ordinal.Compare(previousPath, relative) >= 0)
                throw new InvalidDataException("Run manifest path is rooted, duplicated, or not canonically ordered.");
            previousPath = relative;
            var fullPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Run manifest path escapes its run directory.");
            var fileBytes = readAllBytes(fullPath);
            if (Path.GetFileName(fullPath).EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var evidence = JsonDocument.Parse(fileBytes);
                ValidateIdentity(evidence.RootElement);
            }
            var actualSha = Convert.ToHexStringLower(SHA256.HashData(fileBytes));
            if (!string.Equals(actualSha, item.GetProperty("sha256").GetString(), StringComparison.Ordinal))
                throw new InvalidDataException($"Run manifest SHA-256 mismatch for {relative}.");
        }
        var actualFiles = Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(rootDirectory, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualFiles.SetEquals(indexed)) throw new InvalidDataException("Run manifest does not index exactly the run evidence files.");

        void ValidateIdentity(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var isAudioHostQualification = element.TryGetProperty("qualificationConfigSha256", out _) || element.TryGetProperty("audioHostBuildSha", out _);
                foreach (var property in element.EnumerateObject())
                {
                    if (!isAudioHostQualification && property.Name.Equals("runId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String &&
                        Guid.TryParse(property.Value.GetString(), out var evidenceRunId) && evidenceRunId != runId)
                        throw new InvalidDataException("Run evidence ID mismatch.");
                    if (property.Name.Equals("captureEndpointId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() != captureId ||
                        property.Name.Equals("renderEndpointId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() != renderId)
                        throw new InvalidDataException("Run evidence endpoint pair mismatch.");
                    ValidateIdentity(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) ValidateIdentity(item);
        }
    }
}
