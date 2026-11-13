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
            await WriteNewAtomicAsync(path, bytes, ct).ConfigureAwait(false);
        };
        var manifestPath = Path.Combine(rootDirectory, "run-manifest.json");
        ValidateRequiredComposition(rootDirectory, readAllBytes);
        var entries = Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
            .Select(path => new { path = Path.GetRelativePath(rootDirectory, path), sha256 = Convert.ToHexStringLower(SHA256.HashData(readAllBytes(path))) })
            .OrderBy(entry => entry.path, StringComparer.Ordinal).ToArray();
        using var start = JsonDocument.Parse(readAllBytes(Path.Combine(rootDirectory, "run-start.json")));
        var captureId = start.RootElement.GetProperty("captureEndpointId").GetString() ?? throw new InvalidDataException("Capture endpoint identity is missing.");
        var renderId = start.RootElement.GetProperty("renderEndpointId").GetString() ?? throw new InvalidDataException("Render endpoint identity is missing.");
        var workflowType = start.RootElement.GetProperty("workflowType").GetString() ?? throw new InvalidDataException("Run workflow type is missing.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, runId, workflowType, captureEndpointId = captureId, renderEndpointId = renderId, files = entries }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
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
        if (!json.TryGetProperty("workflowType", out var manifestWorkflow) || manifestWorkflow.GetString() != startJson.GetProperty("workflowType").GetString())
            throw new InvalidDataException("Run manifest workflow type mismatch.");
        ValidateRequiredComposition(rootDirectory, readAllBytes);
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

    private static void ValidateRequiredComposition(string rootDirectory, Func<string, byte[]> readAllBytes)
    {
        var root = Path.GetFullPath(rootDirectory) + Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => !string.Equals(path, Path.Combine(rootDirectory, "run-manifest.json"), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var startPath = Path.Combine(rootDirectory, "run-start.json");
        if (!File.Exists(startPath)) throw new InvalidDataException("Required run-start.json is missing.");
        using var start = JsonDocument.Parse(readAllBytes(startPath));
        var workflowType = start.RootElement.GetProperty("workflowType").GetString();
        if (workflowType is not ("V4_DIAGNOSTIC" or "V4_SEARCH" or "V4_SCAN_QUALIFICATION"))
            throw new InvalidDataException("Run workflow type is missing or unsupported.");
        if (workflowType is "V4_DIAGNOSTIC" or "V4_SEARCH")
        {
            var resultFiles = files.Where(path => !Path.GetFileName(path).Equals("run-start.json", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(path).Equals("run-failure.json", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(path).Equals("run-manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            var startId = start.RootElement.GetProperty("runId").GetGuid();
            var captureId = start.RootElement.GetProperty("captureEndpointId").GetString();
            var renderId = start.RootElement.GetProperty("renderEndpointId").GetString();
            var hasResult = resultFiles.Any(path =>
            {
                try
                {
                    using var result = JsonDocument.Parse(readAllBytes(path));
                    var json = result.RootElement;
                    return json.TryGetProperty("runId", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var actualId) && actualId == startId &&
                        json.TryGetProperty("captureEndpointId", out var capture) && capture.GetString() == captureId &&
                        json.TryGetProperty("renderEndpointId", out var render) && render.GetString() == renderId;
                }
                catch (JsonException) { return false; }
            });
            if (!hasResult) throw new InvalidDataException($"Required {workflowType} result with this Run ID and endpoint pair is missing.");
            return;
        }
        var coordinatorPaths = files.Where(path => Path.GetFileName(path).StartsWith("scan-qualification-", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (coordinatorPaths.Length != 1) throw new InvalidDataException("A qualification run requires exactly one terminal coordinator result.");
        using var coordinator = JsonDocument.Parse(readAllBytes(coordinatorPaths[0]));
        var result = coordinator.RootElement;
        var status = result.GetProperty("status").GetString();
        RequireReferencedPath(result, "evidencePath");
        foreach (var attempt in result.GetProperty("attempts").EnumerateArray())
        {
            RequireReferencedPath(attempt, "scanEvidencePath", allowEmpty: true);
            RequireReferencedPath(attempt, "qualificationEvidencePath", allowEmpty: true);
        }
        if (status is not ("LOAD_CLEAN" or "TEST_ONLY")) return;
        RequireFile("audiohost-result-c*.json");
        RequireQualificationSource("short", "short-search-summary.json");
        RequireQualificationSource("repeat", "repeatability-summary.json");
        if (status == "LOAD_CLEAN") RequireFile("pending-profile-v5.json");
        var combined = result.GetProperty("evidencePath").GetString()!;
        var combinedPath = Path.GetFullPath(Path.IsPathRooted(combined) ? combined : Path.Combine(rootDirectory, combined));
        if (!files.Contains(combinedPath, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Coordinator result evidence is not present in the terminal chain.");

        void RequireFile(string pattern)
        {
            if (!files.Any(path => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path), ignoreCase: true)))
                throw new InvalidDataException($"Required {workflowType} evidence '{pattern}' is missing.");
        }

        void RequireQualificationSource(string side, string fileName)
        {
            var found = files.Any(path =>
            {
                var parts = Path.GetRelativePath(rootDirectory, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 4 && parts[0] == "qualification-source" && parts[2] == side && parts[3] == fileName;
            });
            if (!found) throw new InvalidDataException($"Required qualification-source/{side}/{fileName} is missing.");
        }

        void RequireReferencedPath(JsonElement owner, string propertyName, bool allowEmpty = false)
        {
            var value = owner.GetProperty(propertyName).GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                if (allowEmpty) return;
                throw new InvalidDataException($"Coordinator evidence reference '{propertyName}' is missing.");
            }
            var fullPath = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(rootDirectory, value));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !files.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Coordinator evidence reference '{propertyName}' is outside or missing from the run chain.");
        }
    }

    private static async Task WriteNewAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Evidence destination has no directory.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
