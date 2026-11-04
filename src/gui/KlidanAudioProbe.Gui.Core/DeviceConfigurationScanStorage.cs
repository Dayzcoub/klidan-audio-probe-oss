using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record ScanPersistenceResult(string Status, string? SummaryPath, IReadOnlyList<string> ProfilePaths, string? Error = null);
public sealed record DeviceConfigurationScanSummary(int SchemaVersion, string SessionId, DateTimeOffset TimestampUtc,
    string Lifecycle, string CaptureEndpointId, string? RenderEndpointId,
    IReadOnlyList<ScanSummaryOperation> Operations, IReadOnlyList<DuplexCandidateAssessment> DuplexAssessments,
    DuplexRecommendation Recommendation, IReadOnlyList<string> ProfilePaths,
    string PhysicalRtl, string AudioHostEligibility)
{
    public IReadOnlyList<AutomaticScanStageResult> AutomaticStages { get; init; } = [];
    public IReadOnlyList<AutomaticScanSummaryCandidate> CandidateInventory { get; init; } = [];
    public IReadOnlyList<AutomaticScanChildOperation> ChildOperations { get; init; } = [];
    public AutomaticScanBudget? AutomaticBudget { get; init; }
}

public sealed record AutomaticScanSummaryCandidate(string EndpointId, string Direction, int SampleRateHz, string Encoding, int Channels,
    int? ContainerBits, int? ValidBits, string Period, long? Period100ns, string MmcssMode, string State, string Reason, string Provenance);
public sealed record AutomaticScanBudget(int MaximumOperations, int MaximumActiveSeconds, int MaximumWallClockSeconds);
public sealed record ScanSummaryOperation(string Stage, string Lifecycle, string OperationId, string? EndpointId,
    string? Direction, string CleanupStatus, string Message, IReadOnlyList<string> ProfilePaths);

public static class DeviceConfigurationScanStorage
{
    private const int MaximumSummaryBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ScanPersistenceResult Save(string sessionId, ScanRunLifecycle lifecycle, ScanTarget target,
        IReadOnlyList<ScanOperationResult> operations, IReadOnlyList<DuplexCandidateAssessment> assessments,
        DuplexRecommendation recommendation, string? rootDirectory = null,
        IReadOnlyList<AutomaticScanCandidate>? candidateInventory = null,
        IReadOnlyList<AutomaticScanStageResult>? stageResults = null,
        IReadOnlyList<AutomaticScanChildOperation>? childOperations = null,
        AutomaticScanBudget? budget = null,
        SustainedProfilePersistenceInput? sustainedProfile = null)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty) throw new InvalidDataException("Scan session identity is invalid.");
        ArgumentNullException.ThrowIfNull(target);
        var customRoot = rootDirectory is not null;
        rootDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "Scans");
        var summaryPath = Path.Combine(rootDirectory, $"scan-{sessionGuid:D}.json");
        if (File.Exists(summaryPath)) throw new IOException("A scan summary with this session identity already exists; existing evidence will not be overwritten.");
        foreach (var operation in operations)
        {
            if (operation.Evidence?.FormatMatrix is { } matrix && matrix.Provenance is not ("REAL_WASAPI_QUERY" or "REAL_WASAPI_INITIALIZE_ONLY") ||
                operation.Evidence?.Stream is { } stream && (!stream.Report.TryGetProperty("provenance", out var streamProvenance) || streamProvenance.GetString() != "REAL_WASAPI_STREAM_VALIDATION") ||
                operation.Evidence?.Duplex is { } duplexEvidence && (!duplexEvidence.Report.TryGetProperty("provenance", out var duplexProvenance) || duplexProvenance.GetString() != "REAL_WASAPI_DUPLEX"))
                throw new InvalidDataException("Synthetic or unknown provenance cannot enter a persisted real scan report.");
        }
        var profileDirectory = customRoot ? Path.Combine(rootDirectory, "Profiles") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "Profiles");
        Directory.CreateDirectory(profileDirectory);
        var profilePaths = new List<string>();
        var endpointProfilePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var endpointProfiles = new Dictionary<string, DeviceConfigurationProfile>(StringComparer.Ordinal);
        string? profileError = null;
        string? pairProfilePath = null;

        try
        {
            foreach (var endpoint in new[] { target.Capture, target.Render }.Where(e => e is not null).Cast<WindowsEndpointDto>())
            {
                var matrix = operations.Where(o => o.Operation.EndpointId == endpoint.EndpointId && o.Evidence?.FormatMatrix is not null)
                    .Select(o => o.Evidence!.FormatMatrix!).ToArray();
                var query = matrix.FirstOrDefault(m => m.Provenance == "REAL_WASAPI_QUERY" && m.ReportLevel == "PASSIVE_QUERY");
                var initializations = matrix.Where(m => m.Provenance == "REAL_WASAPI_INITIALIZE_ONLY" && m.ReportLevel == "INITIALIZE_ONLY").ToArray();
                var streams = operations.Where(o => o.Operation.EndpointId == endpoint.EndpointId && o.Evidence?.Stream is { } s &&
                        s.Report.TryGetProperty("provenance", out var p) && p.GetString() == "REAL_WASAPI_STREAM_VALIDATION")
                    .Select(o => o.Evidence!.Stream!).ToArray();
                if (query is null && initializations.Length == 0) continue;
                var profile = DeviceConfigurationProfileFactory.CreateAggregated(endpoint, query, initializations,
                    streamValidations: streams);
                endpointProfiles.Add(endpoint.EndpointId!, profile);
            }

            var duplex = operations.Where(o => o.Evidence?.Duplex is { } d &&
                    d.Report.TryGetProperty("provenance", out var p) && p.GetString() == "REAL_WASAPI_DUPLEX")
                .Select(o => o.Evidence!.Duplex!).ToArray();
            if (duplex.Length > 0)
            {
                if (target.Capture is null || target.Render is null || target.PairIdentity != ScanPairIdentity.OperatorSelectedPair)
                    throw new InvalidDataException("Real Duplex evidence cannot be persisted without its exact operator-selected endpoint pair.");
                var pairProfile = DeviceConfigurationProfileFactory.CreateDuplex(target.Capture, target.Render, duplex);
                var captureProfile = endpointProfiles.GetValueOrDefault(target.Capture.EndpointId!);
                var renderProfile = endpointProfiles.GetValueOrDefault(target.Render.EndpointId!);
                pairProfile = pairProfile with
                {
                    Evidence = pairProfile.Evidence.Concat(endpointProfiles.Values.SelectMany(p => p.Evidence)).ToArray(),
                    CapabilityEvidence = new(captureProfile?.CapabilityEvidence.Capture ?? [], renderProfile?.CapabilityEvidence.Render ?? [], pairProfile.CapabilityEvidence.Duplex),
                    StreamObservations = endpointProfiles.Values.SelectMany(p => p.StreamObservations).ToArray()
                };
                pairProfile = DeviceConfigurationProfileJson.Validate(pairProfile);
                if (sustainedProfile is not null)
                {
                    if (!string.Equals(sustainedProfile.ScanSessionId, sessionGuid.ToString("D"), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Sustained profile evidence belongs to a different scan session.");
                    var resolvedSustained = SustainedDuplexEvidenceResolver.Resolve(sustainedProfile.Evidence, target.Capture.EndpointId!, target.Render.EndpointId!, sustainedProfile.EvidenceRootDirectory);
                    pairProfile = DeviceConfigurationProfileFactory.CreateSustained(pairProfile, sessionGuid.ToString("D"), resolvedSustained, sustainedProfile.Recommendation);
                }
                pairProfilePath = DeviceConfigurationProfileStorage.SaveLocal(pairProfile, profileDirectory);
                _ = DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(pairProfilePath));
                profilePaths.Add(pairProfilePath);
                if (captureProfile is not null) endpointProfilePaths[target.Capture.EndpointId!] = pairProfilePath;
                if (renderProfile is not null) endpointProfilePaths[target.Render.EndpointId!] = pairProfilePath;
            }
            else
            {
                if (sustainedProfile is not null) throw new InvalidDataException("Sustained profile evidence requires a persisted exact pair profile.");
                foreach (var (endpointId, profile) in endpointProfiles)
                {
                    var path = DeviceConfigurationProfileStorage.SaveLocal(profile, profileDirectory);
                    _ = DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(path));
                    profilePaths.Add(path);
                    endpointProfilePaths[endpointId] = path;
                }
            }
        }
        catch (Exception ex)
        {
            profileError = $"{ex.GetType().Name}: {ex.Message}";
            profilePaths.Clear();
            endpointProfilePaths.Clear();
            pairProfilePath = null;
        }

        if (string.IsNullOrWhiteSpace(target.Capture?.EndpointId) && string.IsNullOrWhiteSpace(target.Render?.EndpointId))
            throw new InvalidDataException("A saved scan requires at least one exact local endpoint identity.");
        var summaryOperations = operations.Select(o =>
        {
            IReadOnlyList<string> paths = [];
            if (o.Evidence?.Duplex is not null && pairProfilePath is not null) paths = [pairProfilePath];
            else if (o.Operation.EndpointId is not null && endpointProfilePaths.TryGetValue(o.Operation.EndpointId, out var profilePath)) paths = [profilePath];
            return new ScanSummaryOperation(o.Operation.Stage.ToString().ToUpperInvariant(), o.Lifecycle.ToString().ToUpperInvariant(),
                o.Operation.OperationId, o.Operation.EndpointId, o.Operation.Direction, o.CleanupStatus, o.Message, paths);
        }).ToArray();
        var summary = new DeviceConfigurationScanSummary(1, sessionGuid.ToString("D"), DateTimeOffset.UtcNow,
            lifecycle.ToString().ToUpperInvariant(), target.Capture?.EndpointId ?? "", target.Render?.EndpointId,
            summaryOperations, assessments.ToArray(), recommendation, profilePaths.ToArray(), "NOT_MEASURED", "NOT_CERTIFIED");
        summary = summary with
        {
            AutomaticStages = stageResults?.Select(stage => stage.Stage == AutomaticScanStage.SAVE
                ? stage with { State = profileError is null ? AutomaticScanStageState.COMPLETED : AutomaticScanStageState.FAILED, Message = profileError ?? "SAVED_AND_RELOADED" }
                : stage).ToArray() ?? [],
            CandidateInventory = candidateInventory?.Select(c => new AutomaticScanSummaryCandidate(c.EndpointId, c.Direction, c.SampleRateHz, c.Encoding, c.Channels, c.ContainerBits, c.ValidBits, c.Period, c.Period100ns, c.MmcssMode, c.State.ToString(), c.Reason, c.Provenance)).ToArray() ?? [],
            ChildOperations = childOperations?.ToArray() ?? operations.Select(ToChildOperation).ToArray(),
            AutomaticBudget = budget
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(summary, JsonOptions);
        if (bytes.Length is 0 or > MaximumSummaryBytes) throw new InvalidDataException("Scan summary exceeds the bounded local report size.");
        Directory.CreateDirectory(rootDirectory);
        WriteNewAtomically(summaryPath, bytes);
        var reloaded = LoadSummary(summaryPath);
        if (reloaded.SessionId != summary.SessionId || reloaded.ProfilePaths.Count != profilePaths.Count || reloaded.PhysicalRtl != "NOT_MEASURED" || reloaded.AudioHostEligibility != "NOT_CERTIFIED")
            throw new InvalidDataException("Saved scan summary failed its read-back validation.");
        return profileError is null
            ? new("SAVED_AND_RELOADED", summaryPath, profilePaths)
            : new("SUMMARY_SAVED_PROFILE_FAILED", summaryPath, [], profileError);
    }

    public static DeviceConfigurationScanSummary LoadSummary(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumSummaryBytes) throw new InvalidDataException("Scan summary is missing or outside the size limit.");
        return LoadSummary(File.ReadAllBytes(path));
    }

    public static DeviceConfigurationScanSummary LoadSummary(byte[] bytes)
    {
        if (bytes is null || bytes.Length is 0 or > MaximumSummaryBytes) throw new InvalidDataException("Scan summary is missing or outside the size limit.");
        var summary = JsonSerializer.Deserialize<DeviceConfigurationScanSummary>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Scan summary JSON is invalid.");
        if (summary.SchemaVersion != 1 || !Guid.TryParse(summary.SessionId, out _) || summary.PhysicalRtl != "NOT_MEASURED" || summary.AudioHostEligibility != "NOT_CERTIFIED" || summary.Operations.Count > 64 || summary.ChildOperations.Count > 256 || summary.ProfilePaths.Count > 3 || summary.AutomaticStages.Count > Enum.GetValues<AutomaticScanStage>().Length || summary.AutomaticStages.Select(s => s.Stage).Distinct().Count() != summary.AutomaticStages.Count || summary.CandidateInventory.Count > 4096 || summary.CandidateInventory.Any(c => string.IsNullOrWhiteSpace(c.EndpointId) || c.Direction is not ("CAPTURE" or "RENDER") || c.SampleRateHz <= 0 || string.IsNullOrWhiteSpace(c.Encoding) || c.Channels <= 0 || string.IsNullOrWhiteSpace(c.Period) || string.IsNullOrWhiteSpace(c.State) || string.IsNullOrWhiteSpace(c.Provenance)) || summary.ChildOperations.Any(c => string.IsNullOrWhiteSpace(c.Stage) || c.Lifecycle is not ("COMPLETED" or "FAILED" or "UNKNOWN" or "SKIPPED") || string.IsNullOrWhiteSpace(c.Provenance) || c.ReportPath is { Length: > 4096 }) || summary.AutomaticBudget is { MaximumOperations: <= 0 } or { MaximumActiveSeconds: < 0 } or { MaximumWallClockSeconds: < 0 })
            throw new InvalidDataException("Scan summary contract validation failed.");
        return summary;
    }

    private static void WriteNewAtomically(string destination, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static AutomaticScanChildOperation ToChildOperation(ScanOperationResult operation)
    {
        var evidence = operation.Evidence;
        var report = evidence?.Stream?.Report ?? evidence?.Duplex?.Report ?? default;
        var reportId = report.ValueKind == JsonValueKind.Object && report.TryGetProperty("reportId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        var provenance = evidence?.FormatMatrix?.Provenance ??
            (report.ValueKind == JsonValueKind.Object && report.TryGetProperty("provenance", out var p) ? p.GetString() : null) ?? "NOT_OBSERVED";
        return new(operation.Operation.Stage.ToString().ToUpperInvariant(), operation.Operation.OperationId, reportId,
            operation.Lifecycle.ToString().ToUpperInvariant(), provenance, operation.CleanupStatus, operation.Message, null,
            operation.Operation.Period, operation.Operation.EndpointId, operation.Operation.Direction);
    }
}
