using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public enum AutomaticScanStage
{
    DISCOVERY,
    FORMAT_QUERY,
    PERIOD_DISCOVERY,
    INITIALIZE,
    SHORT_STREAM,
    SHORT_DUPLEX,
    REPEATABILITY,
    SUSTAINED_VALIDATION,
    FINAL_SELECTION,
    SAVE
}

public enum AutomaticScanStageState { PLANNED, COMPLETED, NOT_RUN, FAILED, UNKNOWN, SKIPPED }

public enum AutomaticCandidateState
{
    SUPPORTED_FOR_TEST,
    REPORTED_SUPPORTED,
    INITIALIZE_ACCEPTED,
    RUNTIME_TESTED,
    NOT_SUPPORTED_BY_DEVICE,
    NOT_IMPLEMENTED_BY_PROBE,
    NOT_TESTED,
    UNKNOWN
}

public sealed record AutomaticScanStageResult(AutomaticScanStage Stage, AutomaticScanStageState State, string Message);

public sealed record AutomaticScanCandidate(
    string EndpointId, string Direction, int SampleRateHz, string Encoding, int Channels,
    int? ContainerBits, int? ValidBits, string Period, long? Period100ns, string MmcssMode,
    AutomaticCandidateState State, string Reason, string Provenance);

public sealed record AutomaticScanPlan(
    string DiscoveryFingerprint, string? CaptureEndpointId, string? RenderEndpointId,
    ScanPairIdentity PairIdentity, int MaximumOperationCount, int MaximumDurationSeconds,
    string MmcssMode, IReadOnlyList<AutomaticScanStageResult> Stages,
    IReadOnlyList<AutomaticScanCandidate> Candidates)
{
    public bool IsBoundTo(WindowsEndpointDiscoveryDto discovery) =>
        DiscoveryFingerprint == Fingerprint(discovery) &&
        discovery.Endpoints.Any(e => e.EndpointId == CaptureEndpointId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE") == (CaptureEndpointId is not null) &&
        discovery.Endpoints.Any(e => e.EndpointId == RenderEndpointId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE") == (RenderEndpointId is not null);

    public static string Fingerprint(WindowsEndpointDiscoveryDto discovery) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(discovery))));
}

public static class AutomaticScanPlanner
{
    public static AutomaticScanPlan Create(WindowsEndpointDiscoveryDto discovery, ScanPlan plan, WorkerScanPlan workerPlan) =>
        new(AutomaticScanPlan.Fingerprint(discovery), plan.Target.Capture?.EndpointId, plan.Target.Render?.EndpointId, plan.Target.PairIdentity,
            workerPlan.MaximumOperationCount, workerPlan.MaximumOperationDurationSeconds, plan.Settings.MmcssMode,
            Enum.GetValues<AutomaticScanStage>().Select(stage => new AutomaticScanStageResult(stage, stage switch
            {
                AutomaticScanStage.DISCOVERY or AutomaticScanStage.FORMAT_QUERY or AutomaticScanStage.INITIALIZE or AutomaticScanStage.SAVE => AutomaticScanStageState.PLANNED,
                _ => AutomaticScanStageState.NOT_RUN
            }, stage switch
            {
                AutomaticScanStage.PERIOD_DISCOVERY => "Runs automatically for an exact Capture/Render pair with current custom-period capability.",
                AutomaticScanStage.SHORT_STREAM or AutomaticScanStage.SHORT_DUPLEX or AutomaticScanStage.REPEATABILITY => "Runs automatically only for candidates within the approved bounded scope.",
                AutomaticScanStage.SUSTAINED_VALIDATION => "Runs qualified continuous 60-second screening and bounded Standard/Extended MMCSS validation when exact short evidence qualifies.",
                AutomaticScanStage.FINAL_SELECTION => "Evidence-based sustained primary/fallback selection; AudioHost remains NOT_CERTIFIED.",
                _ => "Bounded stage is included in the immutable scan scope."
            })).ToArray(), []);

    public static IReadOnlyList<AutomaticScanCandidate> FromQueries(IEnumerable<RealFormatMatrixDto> queries, string mmcssMode = "OFF")
    {
        var result = new List<AutomaticScanCandidate>();
        foreach (var matrix in queries.OrderBy(q => q.EndpointId, StringComparer.Ordinal).ThenBy(q => q.Direction, StringComparer.Ordinal))
        foreach (var candidate in matrix.Candidates.OrderBy(c => c.SampleRate).ThenBy(c => c.Encoding, StringComparer.Ordinal).ThenBy(c => c.Channels).ThenBy(c => c.Period, StringComparer.Ordinal))
        {
            var state = candidate.Status switch
            {
                "SUPPORTED_REPORTED" when candidate.SupportStatus == "SUPPORTED_REPORTED" && candidate.SampleRate == 48_000 && candidate.Encoding == "PCM16" && candidate.Channels is >= 1 and <= 2
                    => AutomaticCandidateState.SUPPORTED_FOR_TEST,
                "SUPPORTED_REPORTED" => AutomaticCandidateState.REPORTED_SUPPORTED,
                "UNSUPPORTED" => AutomaticCandidateState.NOT_SUPPORTED_BY_DEVICE,
                "NOT_TESTED" => AutomaticCandidateState.NOT_TESTED,
                _ => AutomaticCandidateState.UNKNOWN
            };
            var reason = state == AutomaticCandidateState.SUPPORTED_FOR_TEST
                ? "Query reports a bounded 48 kHz PCM16 candidate within the current probe scope."
                : state == AutomaticCandidateState.NOT_SUPPORTED_BY_DEVICE
                    ? "The device reported this candidate as unsupported."
                    : state == AutomaticCandidateState.NOT_TESTED
                        ? "The candidate was not tested by the device query."
                        : "The query does not establish a usable test authorization.";
            result.Add(new(matrix.EndpointId, matrix.Direction, candidate.SampleRate, candidate.Encoding, candidate.Channels,
                candidate.ContainerBits, candidate.ValidBits, candidate.Period.ToUpperInvariant(), candidate.Period == "minimum" ? candidate.MinimumPeriod100ns : candidate.DefaultPeriod100ns,
                mmcssMode, state, reason, matrix.Provenance));
        }
        foreach (var matrix in queries.Where(q => q.Direction is "CAPTURE" or "RENDER"))
        {
            var stereo = matrix.Candidates.FirstOrDefault(c => c.SampleRate == 48_000 && c.Encoding == "PCM16" && c.Channels == 2 && c.Status == "SUPPORTED_REPORTED");
            if (stereo is null || stereo.MinimumPeriod100ns is not > 0 || stereo.DefaultPeriod100ns is not > 0) continue;
            foreach (var custom in ExclusivePeriodDiscoveryPlanning.Generate(stereo.MinimumPeriod100ns.Value, stereo.DefaultPeriod100ns.Value))
                result.Add(new(matrix.EndpointId, matrix.Direction, 48_000, "PCM16", 2, stereo.ContainerBits, stereo.ValidBits, custom.AuthorizationValue, custom.Period100ns, mmcssMode,
                    AutomaticCandidateState.NOT_TESTED, "C4.2A custom-period discovery is pending; this candidate is not authorized or executed by S1.", matrix.Provenance));
        }
        return result;
    }

    public static IReadOnlyList<AutomaticScanCandidate> WithInitializeResults(IReadOnlyList<AutomaticScanCandidate> candidates, IEnumerable<ScanOperationResult> operations)
    {
        var accepted = operations.Where(o => o.Operation.Stage == ScanStage.Initialize && o.Lifecycle == ScanOperationLifecycle.Completed && o.Evidence?.FormatMatrix is { } matrix && matrix.Candidates.Count == 1 && matrix.Candidates[0].InitializeStatus is "INITIALIZED" or "READY")
            .Select(o => (o.Operation.EndpointId, o.Operation.Direction, o.Operation.SampleRateHz, o.Operation.Encoding, o.Operation.Channels, Period: o.Operation.Period?.ToUpperInvariant())).ToHashSet();
        return candidates.Select(c => accepted.Contains((c.EndpointId, c.Direction, c.SampleRateHz, c.Encoding, c.Channels, c.Period)) ? c with { State = AutomaticCandidateState.INITIALIZE_ACCEPTED, Reason = "Initialize-only accepted for the exact endpoint and requested geometry." } : c).ToArray();
    }

    public static IReadOnlyList<AutomaticScanCandidate> WithRuntimeResults(IReadOnlyList<AutomaticScanCandidate> candidates,
        PeriodDiscoverySeriesResult? periodDiscovery, IEnumerable<string> repeatedPeriods,
        IEnumerable<DuplexRepeatabilityRun>? repeatedRuns = null)
    {
        var exact = new HashSet<(string EndpointId, string Direction, int Frames, long Period100ns)>();
        if (periodDiscovery?.Lifecycle == "COMPLETED")
        {
            foreach (var group in periodDiscovery.Reports
                .Where(r => (r.Provenance is "REAL_WASAPI_INITIALIZE_ONLY" or "TEST_FAKE_PERIOD_BACKEND") && r.Candidates.Count == 1)
                .GroupBy(r => (r.EndpointId, r.Direction, r.Candidates[0].RequestedFrames, r.Candidates[0].RequestedPeriod100ns)))
            {
                if (group.Select(r => r.Direction).Distinct(StringComparer.Ordinal).Count() == 1 &&
                    group.All(r => r.Candidates[0].Classification == "ACCEPTED" && r.Candidates[0].CleanupStatus == "RELEASE_ATTEMPTED"))
                    exact.Add((group.Key.EndpointId, group.Key.Direction, checked((int)group.Key.RequestedFrames), group.Key.RequestedPeriod100ns));
            }
        }
        var repeated = repeatedPeriods.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verifiedRuns = repeatedRuns?.Where(r => r.Lifecycle == ScanOperationLifecycle.Completed && r.Validation is not null)
            .Where(r => IsVerifiedRuntimeEvidence(r.Validation!.Report, r.Period)).ToArray() ?? [];
        return candidates.Select(candidate =>
        {
            if (candidate.Period.StartsWith("CUSTOM:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(candidate.Period.Split(':') is [_, _, var frameText] ? frameText : null, out var frames) &&
                candidate.Period100ns is { } period && exact.Contains((candidate.EndpointId, candidate.Direction, frames, period)))
                return verifiedRuns.Any(r => Matches(candidate, r.Validation!.Report, r.Period))
                    ? candidate with { State = AutomaticCandidateState.RUNTIME_TESTED, Reason = "Exact custom period was initialized and exercised by verified bounded repeatability evidence." }
                    : candidate with { State = AutomaticCandidateState.INITIALIZE_ACCEPTED, Reason = "Exact custom period was accepted by current paired period discovery." };
            return verifiedRuns.Any(r => Matches(candidate, r.Validation!.Report, r.Period))
                ? candidate with { State = AutomaticCandidateState.RUNTIME_TESTED, Reason = "Exact candidate was exercised by verified bounded repeatability evidence." }
                : repeated.Contains(candidate.Period) && repeatedRuns is null
                    ? candidate with { State = AutomaticCandidateState.RUNTIME_TESTED, Reason = "Exact candidate was exercised by bounded repeatability." }
                    : candidate;
        }).ToArray();

        static bool Matches(AutomaticScanCandidate candidate, JsonElement report, string period)
        {
            if (!report.TryGetProperty("requested", out var requested) || requested.GetProperty("period").GetString() != period ||
                requested.GetProperty("sampleRateHz").GetInt32() != candidate.SampleRateHz || requested.GetProperty("encoding").GetString() != candidate.Encoding ||
                requested.GetProperty("channels").GetInt32() != candidate.Channels || requested.GetProperty("mmcss").GetString() != candidate.MmcssMode ||
                report.GetProperty("provenance").GetString() is not ("REAL_WASAPI_DUPLEX" or "TEST_FAKE_WASAPI_DUPLEX")) return false;
            var endpoint = candidate.Direction == "CAPTURE" ? requested.GetProperty("captureEndpointId").GetString() : requested.GetProperty("renderEndpointId").GetString();
            if (endpoint != candidate.EndpointId) return false;
            if (!ProvenanceMatches(candidate.Provenance, report.GetProperty("provenance").GetString())) return false;
            var side = report.GetProperty(candidate.Direction == "CAPTURE" ? "capture" : "render");
            var expectedPeriod = candidate.Period100ns ?? (period.StartsWith("CUSTOM:", StringComparison.OrdinalIgnoreCase) ? null : period == "MINIMUM" ? 30_000 : 100_000);
            var expectedFrames = period.StartsWith("CUSTOM:", StringComparison.OrdinalIgnoreCase) && int.TryParse(period.Split(':') is [_, _, var f] ? f : null, out var customFrames)
                ? customFrames : expectedPeriod is { } p ? ExclusivePeriodDiscoveryPlanning.Frames(p, candidate.SampleRateHz) : 0;
            return side.TryGetProperty("actualBufferFrames", out var frames) && frames.ValueKind == JsonValueKind.Number && frames.GetInt32() == expectedFrames;
        }

        static bool IsVerifiedRuntimeEvidence(JsonElement report, string period) =>
            report.ValueKind == JsonValueKind.Object && report.TryGetProperty("duplex", out var duplex) &&
            duplex.TryGetProperty("status", out var status) && status.GetString() is "DUPLEX_CLEAN" or "DUPLEX_WARNINGS" &&
            report.TryGetProperty("requested", out var requested) && requested.TryGetProperty("period", out var requestedPeriod) && requestedPeriod.GetString() == period;

        static bool ProvenanceMatches(string candidate, string? runtime) =>
            candidate.StartsWith("REAL_", StringComparison.Ordinal) == (runtime?.StartsWith("REAL_", StringComparison.Ordinal) == true);
    }

    public static IReadOnlyList<AutomaticScanStageResult> CompleteFromOperations(IEnumerable<ScanOperationResult> operations)
    {
        var byStage = operations.GroupBy(o => o.Operation.Stage).ToDictionary(g => g.Key, g => g.ToArray());
        return Enum.GetValues<AutomaticScanStage>().Select(stage =>
        {
            if (stage == AutomaticScanStage.FORMAT_QUERY) return State(stage, byStage.GetValueOrDefault(ScanStage.Query));
            if (stage == AutomaticScanStage.INITIALIZE) return State(stage, byStage.GetValueOrDefault(ScanStage.Initialize));
            if (stage == AutomaticScanStage.SHORT_STREAM) return State(stage, byStage.GetValueOrDefault(ScanStage.Stream));
            if (stage == AutomaticScanStage.SHORT_DUPLEX) return State(stage, byStage.GetValueOrDefault(ScanStage.Duplex));
            if (stage == AutomaticScanStage.SAVE) return State(stage, byStage.GetValueOrDefault(ScanStage.Save));
            if (stage == AutomaticScanStage.DISCOVERY) return State(stage, byStage.GetValueOrDefault(ScanStage.Discovery));
            return stage == AutomaticScanStage.FINAL_SELECTION
                ? new(stage, AutomaticScanStageState.NOT_RUN, "Final selection awaits sustained evidence.")
                : new(stage, AutomaticScanStageState.NOT_RUN, "No operation-backed automatic stage was scheduled.");
        }).ToArray();

        static AutomaticScanStageResult State(AutomaticScanStage stage, IReadOnlyList<ScanOperationResult>? rows)
        {
            if (rows is null || rows.Count == 0) return new(stage, AutomaticScanStageState.NOT_RUN, "No operation was scheduled.");
            var state = rows.Any(r => r.Lifecycle == ScanOperationLifecycle.Unknown) ? AutomaticScanStageState.UNKNOWN :
                rows.Any(r => r.Lifecycle == ScanOperationLifecycle.Failed) ? AutomaticScanStageState.FAILED :
                rows.All(r => r.Lifecycle == ScanOperationLifecycle.Skipped) ? AutomaticScanStageState.SKIPPED :
                rows.All(r => r.Lifecycle is ScanOperationLifecycle.Completed or ScanOperationLifecycle.Skipped) ? AutomaticScanStageState.COMPLETED : AutomaticScanStageState.NOT_RUN;
            return new(stage, state, string.Join(" · ", rows.Select(r => r.Message).Distinct(StringComparer.Ordinal).Take(2)));
        }
    }
}
