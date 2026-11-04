using System.Security.Cryptography;
using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record OfflineSustainedProfileRecoveryPreview(
    string ScanSessionId,
    string SummaryPath,
    string BaseProfilePath,
    DeviceConfigurationProfile BaseProfile,
    IReadOnlyList<SustainedDuplexEvidence> Evidence,
    DuplexRecommendation Recommendation,
    string SourceFingerprint,
    DateTimeOffset HistoricalMeasurementTimestampUtc,
    string ScansDirectory,
    string ProfilesDirectory,
    string EvidenceDirectory);

public sealed record OfflineSustainedProfileRecoveryResult(
    string ProfilePath,
    DeviceConfigurationProfile Profile,
    DuplexRecommendation Recommendation);

public static class OfflineSustainedProfileRecovery
{
    private static readonly object SaveGate = new();
    private const long MaximumHistoricalFileBytes = 8 * 1024 * 1024;

    public static OfflineSustainedProfileRecoveryPreview Preview(string scanSessionId, string scansDirectory, string profilesDirectory, string evidenceDirectory)
    {
        var sessionId = ParseGuid(scanSessionId, "Scan session identity").ToString("D");
        var scansRoot = ApprovedRoot(scansDirectory, "Scans");
        var profilesRoot = ApprovedRoot(profilesDirectory, "Profiles");
        var evidenceRoot = ApprovedRoot(evidenceDirectory, "Sustained evidence");
        var summaryPath = Path.Combine(scansRoot, $"scan-{sessionId}.json");
        RejectReparsePoint(summaryPath);
        var summaryBytes = ReadHistoricalFile(summaryPath, "scan summary");
        RejectDuplicateProperties(summaryBytes);
        var summary = DeviceConfigurationScanStorage.LoadSummary(summaryBytes);
        ValidateSummary(summary, sessionId);

        var baseProfilePath = ResolveBaseProfile(summary, profilesRoot);
        var baseProfileBytes = ReadHistoricalFile(baseProfilePath, "base profile", DeviceConfigurationProfileJson.MaximumBytes);
        var baseProfile = DeviceConfigurationProfileJson.Parse(baseProfileBytes);
        ValidateBaseProfile(baseProfile, summary);

        var evidence = ReconstructEvidence(summary, evidenceRoot);
        var recommendation = SustainedDuplexSelection.Recommend(evidence);
        var evidenceBytes = summary.ChildOperations.Where(c => c.Stage == "SUSTAINED_VALIDATION")
            .Select(c => ParseGuid(c.OperationId!, "Sustained operation identity"))
            .OrderBy(id => id)
            .Select(id => (id, Bytes: ReadHistoricalFile(Path.Combine(evidenceRoot, $"sustained-duplex-{id:D}.json"), "sustained report")))
            .ToArray();
        var sourceFingerprint = Fingerprint(sessionId, summary, evidence, summaryBytes, baseProfileBytes, evidenceBytes);
        var historicalTimestamp = evidence.Select(e => Timestamp(e.Report)).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        return new(sessionId, summaryPath, baseProfilePath, baseProfile, evidence, recommendation, sourceFingerprint, historicalTimestamp, scansRoot, profilesRoot, evidenceRoot);
    }

    public static OfflineSustainedProfileRecoveryResult Save(OfflineSustainedProfileRecoveryPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        lock (SaveGate)
        {
            var current = Preview(preview.ScanSessionId, preview.ScansDirectory, preview.ProfilesDirectory, preview.EvidenceDirectory);
            Need(current.SourceFingerprint == preview.SourceFingerprint, "Historical sources changed after preview; create a new preview before saving.");
            if (current.Recommendation.Preferred is null)
                throw new InvalidDataException("Offline recovery found no verified sustained clean primary.");
            if (current.Evidence.Select(e => e.OperationId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != current.Evidence.Count)
                throw new InvalidDataException("Offline recovery evidence contains duplicate operation identities.");
            RejectExistingRecovery(current);

            var profile = DeviceConfigurationProfileFactory.CreateSustained(
                current.BaseProfile with { ProfileId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow },
                current.ScanSessionId, current.Evidence, current.Recommendation);
            var path = DeviceConfigurationProfileStorage.SaveLocal(profile, current.ProfilesDirectory);
            var reloaded = DeviceConfigurationProfileJson.Parse(ReadHistoricalFile(path, "recovered profile"));
            if (reloaded.ProfileId != profile.ProfileId || reloaded.CreatedAtUtc != profile.CreatedAtUtc || reloaded.SustainedSelection?.SourceScanSessionId != current.ScanSessionId)
                throw new InvalidDataException("Recovered v4 profile failed read-back validation.");
            return new(path, reloaded, current.Recommendation);
        }
    }

    private static void ValidateSummary(DeviceConfigurationScanSummary summary, string sessionId)
    {
        Need(string.Equals(summary.SessionId, sessionId, StringComparison.OrdinalIgnoreCase), "Historical summary session identity differs from the requested session.");
        Need(summary.Lifecycle == "COMPLETED", "Historical scan is not completed.");
        Need(!string.IsNullOrWhiteSpace(summary.CaptureEndpointId) && !string.IsNullOrWhiteSpace(summary.RenderEndpointId) && summary.CaptureEndpointId != summary.RenderEndpointId, "Historical scan does not contain one exact Capture/Render pair.");
        Need(summary.ProfilePaths.Count == 1, "Historical scan does not contain exactly one local pair profile.");
        Need(summary.CandidateInventory.Count > 0, "Historical scan candidate inventory is missing.");
        Need(summary.ChildOperations.Count > 0, "Historical scan child-operation ledger is missing.");
        var sustained = summary.ChildOperations.Where(c => c.Stage == "SUSTAINED_VALIDATION").ToArray();
        Need(sustained.Length > 0 && sustained.All(c => c.Lifecycle == "COMPLETED" && c.Provenance == "REAL_WASAPI_SUSTAINED_DUPLEX" && c.CleanupStatus == "RELEASE_ATTEMPTED" && Guid.TryParse(c.OperationId, out var id) && id != Guid.Empty && Guid.TryParse(c.ReportId, out var report) && report != Guid.Empty), "Historical sustained child-operation ledger is incomplete or unsafe.");
        Need(sustained.Select(c => c.OperationId!).Distinct(StringComparer.OrdinalIgnoreCase).Count() == sustained.Length, "Historical sustained child-operation ledger contains duplicate operation identities.");
        var stage = summary.AutomaticStages.SingleOrDefault(s => s.Stage == AutomaticScanStage.SUSTAINED_VALIDATION);
        Need(stage?.State == AutomaticScanStageState.COMPLETED, "Historical sustained validation stage is not completed.");
    }

    private static string ResolveBaseProfile(DeviceConfigurationScanSummary summary, string profilesRoot)
    {
        var path = Path.GetFullPath(summary.ProfilePaths.Single());
        Need(IsWithin(path, profilesRoot) && Path.GetFileName(path).StartsWith("profile-", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase), "Historical profile reference is outside the approved Profiles directory.");
        RejectReparsePoint(path);
        return path;
    }

    private static void ValidateBaseProfile(DeviceConfigurationProfile profile, DeviceConfigurationScanSummary summary)
    {
        Need(profile.SchemaVersion == 3 && profile.Privacy == "LOCAL_MACHINE" && profile.SustainedSelection is null, "Historical base profile must be a local v3 profile without sustained selection.");
        Need(profile.EndpointBindings.Capture?.EndpointId == summary.CaptureEndpointId && profile.EndpointBindings.Render?.EndpointId == summary.RenderEndpointId, "Historical v3 profile does not bind the exact scan pair.");
    }

    private static IReadOnlyList<SustainedDuplexEvidence> ReconstructEvidence(DeviceConfigurationScanSummary summary, string evidenceRoot)
    {
        var result = new List<SustainedDuplexEvidence>();
        foreach (var child in summary.ChildOperations.Where(c => c.Stage == "SUSTAINED_VALIDATION"))
        {
            var operationId = ParseGuid(child.OperationId!, "Sustained operation identity");
            var path = Path.Combine(evidenceRoot, $"sustained-duplex-{operationId:D}.json");
            RejectReparsePoint(path);
            var envelope = ParseObject(ReadHistoricalFile(path, "sustained report"));
            var report = envelope.GetProperty("report");
            var requested = report.GetProperty("requested");
            var period = RequiredString(requested, "period");
            var mmcss = RequiredString(requested, "mmcss");
            var duration = requested.GetProperty("durationSeconds").GetInt32();
            Need(Guid.Parse(RequiredString(report, "reportId")) == ParseGuid(child.ReportId!, "Sustained report identity"), "Historical child-operation report identity differs from persisted report.");
            Need(ParseGuid(RequiredString(report, "operationId"), "Persisted sustained operation identity") == operationId, "Persisted sustained report operation identity differs from the child ledger.");
            Need(RequiredString(requested, "captureEndpointId") == summary.CaptureEndpointId && RequiredString(requested, "renderEndpointId") == summary.RenderEndpointId, "Persisted sustained report uses a different endpoint pair.");
            Need(child.Period is null || child.Period == $"{period}/{mmcss}/{duration}s", "Historical child-operation settings contradict the persisted report.");
            var classification = SustainedDuplexClassification.Classify(report, duration);
            Need(classification is "SUSTAINED_CLEAN" or "SUSTAINED_WARNINGS", "Persisted sustained report is incomplete, synthetic, or unsafe.");
            // Candidate inventory establishes period geometry; sustained reports establish the runtime MMCSS mode.
            var captureFrames = ExpectedFrames(summary.CandidateInventory, summary.CaptureEndpointId, "CAPTURE", period);
            var renderFrames = ExpectedFrames(summary.CandidateInventory, summary.RenderEndpointId!, "RENDER", period);
            var source = new SustainedDuplexEvidence(operationId.ToString("D"), period, mmcss, duration, child.Lifecycle, classification, child.CleanupStatus, "SAVED_AND_RELOADED", report, captureFrames, renderFrames);
            result.Add(source);
        }
        var resolved = SustainedDuplexEvidenceResolver.Resolve(result, summary.CaptureEndpointId, summary.RenderEndpointId!, evidenceRoot);
        Need(resolved.Count == result.Count, "Historical sustained evidence resolution was incomplete.");
        return resolved;
    }

    private static int ExpectedFrames(IReadOnlyList<AutomaticScanSummaryCandidate> inventory, string endpointId, string direction, string period)
    {
        var candidates = inventory.Where(c => c.EndpointId == endpointId && c.Direction == direction && c.SampleRateHz == 48_000 && c.Encoding == "PCM16" && c.Channels == 2 && c.Period == period && c.State == nameof(AutomaticCandidateState.RUNTIME_TESTED) && c.Provenance.StartsWith("REAL_", StringComparison.Ordinal)).ToArray();
        Need(candidates.Length > 0, "Historical candidate inventory does not contain one exact runtime-tested candidate per sustained side.");
        var period100nsValues = candidates.Select(c => c.Period100ns).Distinct().ToArray();
        Need(period100nsValues.Length == 1 && period100nsValues[0] is > 0, "Historical candidate inventory contains contradictory period geometry.");
        var period100ns = period100nsValues[0]!.Value;
        int expected;
        if (period.StartsWith("CUSTOM:", StringComparison.Ordinal))
        {
            var parts = period.Split(':');
            if (parts.Length != 3 || !long.TryParse(parts[1], out var customPeriod100ns) || !int.TryParse(parts[2], out var customFrames) || customPeriod100ns <= 0 || customFrames <= 0)
                throw new InvalidDataException("Historical custom candidate period geometry is invalid.");
            Need(period100ns == customPeriod100ns, "Historical custom candidate Period100ns contradicts the authorized period token.");
            expected = ExclusivePeriodDiscoveryPlanning.Frames(customPeriod100ns, candidates[0].SampleRateHz);
            Need(customFrames == expected, "Historical custom token frame count contradicts independently calculated geometry.");
        }
        else
        {
            expected = ExclusivePeriodDiscoveryPlanning.Frames(period100ns, candidates[0].SampleRateHz);
        }
        Need(expected > 0, "Historical candidate inventory cannot independently reconstruct buffer geometry.");
        return expected;
    }

    private static void RejectExistingRecovery(OfflineSustainedProfileRecoveryPreview preview)
    {
        foreach (var path in Directory.EnumerateFiles(preview.ProfilesDirectory, "profile-*.json", SearchOption.TopDirectoryOnly))
        {
            RejectReparsePoint(path);
            var profile = DeviceConfigurationProfileJson.Parse(ReadHistoricalFile(path, "profile"));
            if (profile.SchemaVersion == 4 && string.Equals(profile.SustainedSelection?.SourceScanSessionId, preview.ScanSessionId, StringComparison.OrdinalIgnoreCase))
                throw new IOException("This historical scan already has a recovered v4 profile.");
        }
    }

    private static string Fingerprint(string sessionId, DeviceConfigurationScanSummary summary, IReadOnlyList<SustainedDuplexEvidence> evidence, byte[] summaryBytes, byte[] baseProfileBytes, IReadOnlyList<(Guid Id, byte[] Bytes)> evidenceBytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddText($"session\n{sessionId}\n{summary.CaptureEndpointId}\n{summary.RenderEndpointId}\n");
        AddBytes(summaryBytes); AddBytes(baseProfileBytes);
        foreach (var item in evidence.OrderBy(e => ParseGuid(e.OperationId, "Sustained operation identity")))
            AddText($"evidence\n{item.OperationId.ToLowerInvariant()}\n{item.Report.GetProperty("reportId").GetString()!.ToLowerInvariant()}\n{item.Period}\n{item.Mmcss}\n{item.DurationSeconds}\n{item.CaptureFrames}\n{item.RenderFrames}\n{item.Classification}\n");
        foreach (var item in evidenceBytes.OrderBy(e => e.Id)) { AddText(item.Id.ToString("D")); AddBytes(item.Bytes); }
        return Convert.ToHexString(hash.GetHashAndReset());
        void AddText(string value) => AddBytes(System.Text.Encoding.UTF8.GetBytes(value));
        void AddBytes(byte[] bytes) { hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }
    }

    private static byte[] ReadHistoricalFile(string path, string name, long maximumBytes = MaximumHistoricalFileBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 || info.Length > maximumBytes) throw new InvalidDataException($"Historical {name} is missing or exceeds the size limit.");
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidDataException($"Historical {name} is missing or unreadable.", ex); }
    }

    private static JsonElement ParseObject(byte[] bytes)
    {
        RejectDuplicateProperties(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        Need(document.RootElement.ValueKind == JsonValueKind.Object, "Persisted sustained report is not an object.");
        return document.RootElement.Clone();
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
            Walk(document.RootElement);
        }
        catch (JsonException ex) { throw new InvalidDataException("Historical JSON is invalid.", ex); }
        static void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) { Need(names.Add(property.Name), "Historical JSON contains duplicate properties."); Walk(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Walk(item);
        }
    }

    private static DateTimeOffset Timestamp(JsonElement report) => report.TryGetProperty("timestampUtc", out var value) && DateTimeOffset.TryParse(value.GetString(), out var timestamp) ? timestamp.ToUniversalTime() : DateTimeOffset.MinValue;
    private static string RequiredString(JsonElement objectElement, string name) => objectElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidDataException($"Historical JSON field '{name}' is missing.");
    private static Guid ParseGuid(string value, string name) => Guid.TryParse(value, out var guid) && guid != Guid.Empty ? guid : throw new InvalidDataException($"{name} is invalid.");
    private static string ApprovedRoot(string path, string name) { var root = Path.GetFullPath(path); Need(Directory.Exists(root), $"Approved {name} directory is missing."); return root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }
    private static bool IsWithin(string path, string root) => path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    private static void RejectReparsePoint(string path) { if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Historical path must not be a reparse point."); }
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
