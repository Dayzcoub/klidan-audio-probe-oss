using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record V2CandidateMatrixEntry(WorkerScanV2Candidate Candidate, string CaptureInitializeDisposition,
    string RenderInitializeDisposition, string DuplexDisposition, string CaptureCleanup, string RenderCleanup,
    string[] ReasonCodes, bool DuplexNotRun, string? SkipReason, string LowerPeriodCoverage,
    string RepeatabilityStatus, string Provenance)
{
    public int? ActualCaptureFrames { get; init; }
    public long? ActualCapturePeriod100ns { get; init; }
    public int? ActualRenderFrames { get; init; }
    public long? ActualRenderPeriod100ns { get; init; }
}
public sealed record V2CandidateMatrixEvidence(int SchemaVersion, string PlanFingerprint, string RouteFingerprint,
    string Lifecycle, WorkerScanV2PeriodBounds CaptureBounds, WorkerScanV2PeriodBounds RenderBounds, IReadOnlyList<WorkerScanV2Candidate> CandidateDomain,
    IReadOnlyList<V2CandidateMatrixEntry> Candidates, string SelectionState, string Provenance)
{
    public int EvidenceVersion { get; init; } = 1;
    public ulong DiscoveryGeneration { get; init; }
    public int? SelectedCandidateIndex { get; init; }
    public bool GlobalMinimumProven { get; init; }
    public int UsedOperations { get; init; }
    public int MaximumOperations { get; init; }
    public int UsedActiveDuplexSeconds { get; init; }
    public int MaximumActiveDuplexSeconds { get; init; }
    public string StopReason { get; init; } = "PLAN_ONLY";
    public bool RepeatabilityPending { get; init; }
}

public static class V2CandidateMatrixEvidenceJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string SerializeRoundTrip(V2CandidateMatrixEvidence evidence)
    {
        Validate(evidence);
        var json = JsonSerializer.Serialize(evidence, Options);
        var readBack = JsonSerializer.Deserialize<V2CandidateMatrixEvidence>(json, Options) ?? throw new InvalidDataException("Candidate evidence could not be read back.");
        Validate(readBack);
        if (!JsonDocument.Parse(json).RootElement.GetProperty("schemaVersion").TryGetInt32(out var version) || version != 1 || readBack.Candidates.Count != evidence.Candidates.Count || readBack.PlanFingerprint != evidence.PlanFingerprint)
            throw new InvalidDataException("Candidate matrix exact read-back verification failed.");
        return json;
    }

    public static void Validate(V2CandidateMatrixEvidence value)
    {
        static bool Hash(string? s, int length) => s is { } value && value.Length == length && value.All(Uri.IsHexDigit);
        static bool State(string s) => s is "NOT_TESTED" or "SKIPPED" or "REJECTED" or "FAILED" or "UNSUPPORTED" or "UNKNOWN" or "CLEAN";
        if (value.SchemaVersion != 1 || value.EvidenceVersion is not (1 or 2) || !Hash(value.PlanFingerprint, 64) || !Hash(value.RouteFingerprint, 16) || value.Lifecycle is not ("PLANNED" or "COMPLETED" or "UNKNOWN" or "CANCELLED") || value.CandidateDomain.Count is < 1 or > 8 || value.Candidates.Count != value.CandidateDomain.Count || string.IsNullOrWhiteSpace(value.Provenance))
            throw new InvalidDataException("Candidate matrix envelope is invalid.");
        if (value.CaptureBounds.MinimumPeriod100ns <= 0 || value.CaptureBounds.DefaultPeriod100ns < value.CaptureBounds.MinimumPeriod100ns || value.RenderBounds.MinimumPeriod100ns <= 0 || value.RenderBounds.DefaultPeriod100ns < value.RenderBounds.MinimumPeriod100ns ||
            value.EvidenceVersion == 1 && (value.CaptureBounds.AlignmentFrames is <= 0 || value.RenderBounds.AlignmentFrames is <= 0) ||
            value.EvidenceVersion == 2 && (value.CaptureBounds.AlignmentFrames is < 0 || value.RenderBounds.AlignmentFrames is < 0))
            throw new InvalidDataException("Candidate matrix device bounds or alignment state is invalid.");
        for (var i = 0; i < value.Candidates.Count; i++)
        {
            var item = value.Candidates[i];
            if (item.Candidate != value.CandidateDomain[i] || item.Candidate.Index != i || item.CaptureInitializeDisposition is not ("NOT_TESTED" or "SKIPPED" or "REJECTED" or "FAILED" or "UNSUPPORTED" or "INITIALIZED" or "UNKNOWN") || item.RenderInitializeDisposition is not ("NOT_TESTED" or "SKIPPED" or "REJECTED" or "FAILED" or "UNSUPPORTED" or "INITIALIZED" or "UNKNOWN") || !State(item.DuplexDisposition) || item.CleanupOutcomeInvalid() || item.ReasonCodes.Any(string.IsNullOrWhiteSpace) || item.DuplexNotRun != (item.DuplexDisposition is "NOT_TESTED" or "SKIPPED" or "REJECTED" or "UNSUPPORTED") || item.DuplexNotRun && string.IsNullOrWhiteSpace(item.SkipReason) || item.LowerPeriodCoverage is not ("COMPLETE" or "INCOMPLETE" or "UNKNOWN") || item.RepeatabilityStatus is not ("NOT_RUN" or "CONFIRMED" or "FAILED" or "UNKNOWN") || string.IsNullOrWhiteSpace(item.Provenance) || (item.ActualCaptureFrames is null) != (item.ActualCapturePeriod100ns is null) || (item.ActualRenderFrames is null) != (item.ActualRenderPeriod100ns is null) || item.ActualCaptureFrames is <= 0 || item.ActualCapturePeriod100ns is <= 0 || item.ActualRenderFrames is <= 0 || item.ActualRenderPeriod100ns is <= 0)
                throw new InvalidDataException($"Candidate matrix row {i} is invalid.");
        }
        if (value.SelectionState is not ("MINIMUM_SHORT_CLEAN_FOUND" or "REPEATABILITY_CONFIRMED" or "REPEATABILITY_FAILED" or "MINIMUM_NOT_PROVEN" or "UNKNOWN")) throw new InvalidDataException("Candidate matrix selection state is invalid.");
        if (value.EvidenceVersion == 2 && (value.Lifecycle == "PLANNED" || value.DiscoveryGeneration == 0 || value.MaximumOperations is < 1 or > 29 || value.UsedOperations < 0 || value.UsedOperations > value.MaximumOperations || value.MaximumActiveDuplexSeconds is < 0 or > 65 || value.UsedActiveDuplexSeconds < 0 || value.UsedActiveDuplexSeconds > value.MaximumActiveDuplexSeconds || string.IsNullOrWhiteSpace(value.StopReason) || value.SelectedCandidateIndex is { } selected && (selected < 0 || selected >= value.CandidateDomain.Count) || value.RepeatabilityPending != (value.SelectionState == "MINIMUM_SHORT_CLEAN_FOUND")))
            throw new InvalidDataException("V4 completed candidate matrix budget, generation, or selection evidence is invalid.");
    }

    private static bool CleanupOutcomeInvalid(this V2CandidateMatrixEntry e) => e.CaptureCleanup is not ("NOT_ATTEMPTED" or "RELEASE_ATTEMPTED" or "UNKNOWN" or "FAILED") || e.RenderCleanup is not ("NOT_ATTEMPTED" or "RELEASE_ATTEMPTED" or "UNKNOWN" or "FAILED");

    public static string SaveNoOverwriteAndVerify(V2CandidateMatrixEvidence evidence, string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(SerializeRoundTrip(evidence));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path);
            var readback = File.ReadAllBytes(path);
            var parsed = JsonSerializer.Deserialize<V2CandidateMatrixEvidence>(readback, Options) ?? throw new InvalidDataException("Saved candidate matrix is unreadable.");
            Validate(parsed);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Security.Cryptography.SHA256.HashData(bytes), System.Security.Cryptography.SHA256.HashData(readback))) throw new InvalidDataException("Candidate matrix read-back differs from the atomic write.");
            return path;
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }
}
