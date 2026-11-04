using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record SustainedDuplexCandidate(string CaptureEndpointId, string RenderEndpointId, string Period, long? Period100ns, int Frames);
public sealed record SustainedDuplexEvidence(string OperationId, string Period, string Mmcss, int DurationSeconds, string Lifecycle, string Classification, string CleanupStatus, string PersistenceStatus, JsonElement Report, int? CaptureFrames = null, int? RenderFrames = null, string? ReportPath = null);
public sealed record SustainedProfilePersistenceInput(string ScanSessionId, IReadOnlyList<SustainedDuplexEvidence> Evidence, DuplexRecommendation Recommendation, string? EvidenceRootDirectory = null);

public static class SustainedDuplexPlanning
{
    public static IReadOnlyList<SustainedDuplexCandidate> Select(IEnumerable<AutomaticScanCandidate> inventory, IEnumerable<DuplexRepeatabilityRun> runs, int maximum = 3)
    {
        var tested = inventory.Where(c => c.State == AutomaticCandidateState.RUNTIME_TESTED && c.SampleRateHz == 48_000 && c.Encoding == "PCM16" && c.Channels == 2).ToArray();
        var periods = tested.Where(c => c.Direction == "CAPTURE")
            .Select(c => (capture: c, render: tested.FirstOrDefault(r => r.Direction == "RENDER" && r.Period == c.Period && r.Period100ns == c.Period100ns)))
            .Where(x => x.render is not null)
            .Select(x => (pair: x, evidence: runs.Where(r => r.Period.Equals(x.capture.Period, StringComparison.OrdinalIgnoreCase) && Matches(x.capture, x.render!, r.Validation?.Report)).ToArray()))
            .Where(x => x.evidence.Any(r => r.Lifecycle == ScanOperationLifecycle.Completed && IsUsable(r.Validation?.Report)) &&
                        !x.evidence.Any(r => r.Lifecycle is ScanOperationLifecycle.Unknown or ScanOperationLifecycle.Failed))
            .Select(x => new
            {
                Candidate = new SustainedDuplexCandidate(x.pair.capture.EndpointId, x.pair.render!.EndpointId, x.pair.capture.Period, x.pair.capture.Period100ns, Frames(x.pair.capture)),
                Clean = x.evidence.Count(r => r.Lifecycle == ScanOperationLifecycle.Completed && Status(r.Validation?.Report) == "DUPLEX_CLEAN"),
                Warnings = x.evidence.Count(r => r.Lifecycle == ScanOperationLifecycle.Completed && Status(r.Validation?.Report) == "DUPLEX_WARNINGS"),
                Evidence = x.evidence.Length
            })
            .Where(x => x.Evidence > 0)
            .OrderByDescending(x => x.Clean == x.Evidence)
            .ThenByDescending(x => x.Clean)
            .ThenBy(x => x.Warnings)
            .ThenBy(x => x.Candidate.Frames)
            .Select(x => x.Candidate)
            .DistinctBy(c => c.Period, StringComparer.OrdinalIgnoreCase).ToArray();
        if (periods.Length <= maximum) return periods;
        var selected = new List<SustainedDuplexCandidate> { periods[0] };
        var conservative = periods.FirstOrDefault(c => c.Period.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase));
        if (conservative is not null) selected.Add(conservative);
        foreach (var candidate in periods.Skip(1).OrderBy(c => Math.Abs(c.Frames - periods[periods.Length / 2].Frames)))
        {
            if (selected.Any(existing => existing.Period.Equals(candidate.Period, StringComparison.OrdinalIgnoreCase))) continue;
            selected.Add(candidate);
            if (selected.Count == maximum) break;
        }
        return selected.Take(maximum).ToArray();

        static int Frames(AutomaticScanCandidate candidate) => candidate.Period.StartsWith("CUSTOM:", StringComparison.OrdinalIgnoreCase) && int.TryParse(candidate.Period.Split(':') is [_, _, var f] ? f : null, out var custom) ? custom : candidate.Period100ns is { } p ? ExclusivePeriodDiscoveryPlanning.Frames(p, candidate.SampleRateHz) : 0;
    }

    private static bool Matches(AutomaticScanCandidate candidate, AutomaticScanCandidate render, JsonElement? report)
    {
        if (report is not JsonElement value || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("requested", out var requested)) return false;
        return requested.TryGetProperty("captureEndpointId", out var capture) && capture.GetString() == candidate.EndpointId &&
            requested.TryGetProperty("renderEndpointId", out var renderId) && renderId.GetString() == render.EndpointId &&
            requested.TryGetProperty("sampleRateHz", out var rate) && rate.GetInt32() == candidate.SampleRateHz &&
            requested.TryGetProperty("encoding", out var encoding) && encoding.GetString() == candidate.Encoding &&
            requested.TryGetProperty("channels", out var channels) && channels.GetInt32() == candidate.Channels &&
            requested.TryGetProperty("period", out var period) && period.GetString() == candidate.Period &&
            requested.TryGetProperty("mmcss", out var mmcss) && mmcss.GetString() == candidate.MmcssMode;
    }

    private static string? Status(JsonElement? report) => report is JsonElement value && value.TryGetProperty("duplex", out var duplex) && duplex.TryGetProperty("status", out var status) ? status.GetString() : null;

    private static bool IsUsable(JsonElement? report) => Status(report) is "DUPLEX_CLEAN" or "DUPLEX_WARNINGS";
}

public static class SustainedDuplexClassification
{
    public static string Classify(JsonElement report, int requestedDuration)
    {
        if (report.ValueKind != JsonValueKind.Object) return "UNKNOWN";
        if (!report.TryGetProperty("provenance", out var provenance) || provenance.GetString() is not ("REAL_WASAPI_SUSTAINED_DUPLEX" or "TEST_FAKE_WASAPI_SUSTAINED_DUPLEX")) return "UNKNOWN";
        if (!report.TryGetProperty("requested", out var requested) || !requested.TryGetProperty("durationSeconds", out var duration) || duration.GetInt32() != requestedDuration) return "INCOMPLETE";
        if (!report.TryGetProperty("duplex", out var duplex) || !duplex.TryGetProperty("status", out var status)) return "UNKNOWN";
        var value = status.GetString();
        if (value is "FAILED" or "CANCELLED") return "SUSTAINED_FAILED";
        if (value == "INCOMPLETE") return "INCOMPLETE";
        if (value is not ("DUPLEX_CLEAN" or "DUPLEX_WARNINGS")) return "UNKNOWN";
        if (HasTrue(report, "capture", "deviceInvalidated") || HasTrue(report, "render", "deviceInvalidated")) return "UNKNOWN";
        if (BadCleanup(report, "capture") || BadCleanup(report, "render")) return "UNKNOWN";
        var captureReached = report.TryGetProperty("capture", out var capture) && capture.TryGetProperty("durationReached", out var captureDuration) && captureDuration.GetBoolean();
        var renderReached = report.TryGetProperty("render", out var render) && render.TryGetProperty("durationReached", out var renderDuration) && renderDuration.GetBoolean();
        if (!captureReached || !renderReached) return "INCOMPLETE";
        return value == "DUPLEX_CLEAN" ? "SUSTAINED_CLEAN" : "SUSTAINED_WARNINGS";

        static bool HasTrue(JsonElement root, string sideName, string property) => root.TryGetProperty(sideName, out var side) && side.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
        static bool BadCleanup(JsonElement root, string sideName) => root.TryGetProperty(sideName, out var side) && side.TryGetProperty("cleanup", out var value) && (value.GetString() is "UNKNOWN" or "FAILED" || value.GetString()?.Contains("FAILED", StringComparison.Ordinal) == true);
    }
}

public static class SustainedDuplexSelection
{
    public static DuplexRecommendation Recommend(IEnumerable<SustainedDuplexEvidence> evidence)
    {
        var groups = evidence.Where(IsExactEvidence)
            .GroupBy(e => new
            {
                Capture = Requested(e.Report, "captureEndpointId"),
                Render = Requested(e.Report, "renderEndpointId"),
                Rate = RequestedInt(e.Report, "sampleRateHz"),
                Encoding = Requested(e.Report, "encoding"),
                Channels = RequestedInt(e.Report, "channels"),
                e.Period,
                e.Mmcss
            })
            .Select(g => new { g.Key, Evidence = g.ToArray(), Long = g.Where(e => e.DurationSeconds >= 300).ToArray() })
            .Where(g => g.Long.Length > 0 && g.Long.All(e => e.Classification == "SUSTAINED_CLEAN" && e.Lifecycle == "COMPLETED" && IsCleanMeasurement(e.Report)) && !evidence.Any(e => e.DurationSeconds >= 300 && e.Period == g.Key.Period && e.Mmcss == g.Key.Mmcss && Requested(e.Report, "captureEndpointId") == g.Key.Capture && Requested(e.Report, "renderEndpointId") == g.Key.Render && (e.Classification != "SUSTAINED_CLEAN" || !IsCleanMeasurement(e.Report))))
            .Select(g =>
            {
                var item = g.Long.OrderByDescending(e => e.DurationSeconds).First();
                var capture = item.Report.GetProperty("capture");
                var render = item.Report.GetProperty("render");
                var confidence = g.Long.Length > 1 ? "VERIFIED_SUSTAINED_CLEAN" : "PROVISIONAL_SINGLE_SUSTAINED_RUN";
                return new DuplexCandidateAssessment(g.Key.Capture!, g.Key.Render!, g.Key.Rate, g.Key.Encoding!, g.Key.Channels, item.Period, item.Mmcss, item.DurationSeconds,
                    "VERIFIED_SUSTAINED_CLEAN", confidence, capture.GetProperty("actualBufferFrames").GetInt32().ToString(), render.GetProperty("actualBufferFrames").GetInt32().ToString(),
                    Coverage(capture), Coverage(render), $"Exact persisted sustained report operationId={item.Report.GetProperty("operationId").GetString()} reportId={item.Report.GetProperty("reportId").GetString()}; physical RTL remains NOT_MEASURED.", item.Report);
            })
            .OrderBy(a => BufferFrames(a.Report, "capture"))
            .ThenBy(a => a.Mmcss, StringComparer.Ordinal)
            .ToArray();

        if (groups.Length == 0)
            return new("NONE", "NOT_CERTIFIED", null, null, ["NO_VERIFIED_STABLE_CONFIGURATION: no exact real sustained configuration reached the required clean 300-second-or-longer evidence threshold."]);

        var preferred = groups[0];
        var preferredFrames = BufferFrames(preferred.Report, "capture");
        var fallback = groups.FirstOrDefault(a => BufferFrames(a.Report, "capture") > preferredFrames);
        var reasons = new List<string>
        {
            $"Preferred {preferred.Period}/{preferred.Mmcss} is the lowest exact configuration with verified sustained clean evidence.",
            $"Preferred evidence operationId={preferred.Report.GetProperty("operationId").GetString()} reportId={preferred.Report.GetProperty("reportId").GetString()} duration={preferred.DurationSeconds}s.",
            fallback is null ? "Fallback = NONE: no separately validated larger-period sustained configuration exists." : $"Fallback {fallback.Period}/{fallback.Mmcss} has its own verified sustained clean evidence."
        };
        return new("SUSTAINED_VALIDATED", "NOT_CERTIFIED", preferred, fallback, reasons);

        static string? Requested(JsonElement report, string key) => report.TryGetProperty("requested", out var request) && request.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static int RequestedInt(JsonElement report, string key) => report.TryGetProperty("requested", out var request) && request.TryGetProperty(key, out var value) && value.TryGetInt32(out var number) ? number : 0;

        static bool IsExactEvidence(SustainedDuplexEvidence item)
        {
            try
            {
                if (item.Lifecycle != "COMPLETED" || item.CleanupStatus is "UNKNOWN" or "FAILED" || item.PersistenceStatus != "SAVED_AND_RELOADED" || item.Report.ValueKind != JsonValueKind.Object || item.Report.GetProperty("schemaVersion").GetInt32() != 1 || item.Report.GetProperty("reportType").GetString() != "wasapi-sustained-duplex-validation" || item.Report.GetProperty("provenance").GetString() != "REAL_WASAPI_SUSTAINED_DUPLEX" || item.Report.GetProperty("physicalRtl").GetString() != "NOT_MEASURED" || item.Report.GetProperty("duplex").GetProperty("status").GetString() != "DUPLEX_CLEAN") return false;
                if (item.Classification is not ("SUSTAINED_CLEAN" or "SUSTAINED_WARNINGS")) return false;
                var reportOperation = item.Report.GetProperty("operationId").GetString();
                if (!MatchesOperationIdentity(reportOperation, item.OperationId) || string.IsNullOrWhiteSpace(item.Report.GetProperty("reportId").GetString())) return false;
                var requested = item.Report.GetProperty("requested");
                if (requested.GetProperty("period").GetString() != item.Period || requested.GetProperty("mmcss").GetString() != item.Mmcss || requested.GetProperty("durationSeconds").GetInt32() != item.DurationSeconds || requested.GetProperty("sampleRateHz").GetInt32() != 48000 || requested.GetProperty("encoding").GetString() != "PCM16" || requested.GetProperty("channels").GetInt32() != 2) return false;
                foreach (var sideName in new[] { "capture", "render" })
                {
                    var side = item.Report.GetProperty(sideName);
                    var requestedEndpoint = requested.GetProperty(sideName == "capture" ? "captureEndpointId" : "renderEndpointId").GetString();
                    var expectedFrames = sideName == "capture" ? item.CaptureFrames : item.RenderFrames;
                    if (expectedFrames is null || expectedFrames <= 0) return false;
                    if (side.GetProperty("endpointId").GetString() != requestedEndpoint || side.GetProperty("direction").GetString() != sideName.ToUpperInvariant() || side.GetProperty("durationReached").ValueKind != JsonValueKind.True || side.GetProperty("actualBufferFrames").ValueKind != JsonValueKind.Number || side.GetProperty("actualBufferFrames").GetInt32() != expectedFrames || side.GetProperty("stopSucceeded").ValueKind != JsonValueKind.True || side.GetProperty("cleanup").GetString() != "RELEASE_ATTEMPTED") return false;
                }
                return true;
            }
            catch (InvalidOperationException) { return false; }
            catch (KeyNotFoundException) { return false; }

            static bool MatchesOperationIdentity(string? reportOperation, string? operationId)
            {
                if (string.IsNullOrWhiteSpace(reportOperation) || string.IsNullOrWhiteSpace(operationId)) return false;
                return Guid.TryParse(reportOperation, out var reportGuid) && reportGuid != Guid.Empty &&
                    Guid.TryParse(operationId, out var operationGuid) && operationGuid != Guid.Empty && reportGuid == operationGuid;
            }
        }

        static string Coverage(JsonElement side) => side.TryGetProperty("frameCoveragePercent", out var coverage) && coverage.ValueKind == JsonValueKind.Number ? $"{coverage.GetDouble():0.00}%" : "UNKNOWN";
        static int BufferFrames(JsonElement report, string side) => report.GetProperty(side).GetProperty("actualBufferFrames").GetInt32();
        static bool IsCleanMeasurement(JsonElement report)
        {
            try
            {
                foreach (var sideName in new[] { "capture", "render" })
                {
                    var side = report.GetProperty(sideName);
                    if (side.GetProperty("quality").GetString() != "CLEAN" || side.GetProperty("discontinuities").GetInt64() != 0 || side.GetProperty("longEventIntervals").GetInt64() != 0 || side.GetProperty("serviceFaults").GetInt64() != 0 || side.GetProperty("packetDrainLimitHits").GetInt64() != 0 || side.GetProperty("timestampErrors").GetInt64() != 0 || side.GetProperty("watchdogTimedOut").ValueKind != JsonValueKind.False || side.GetProperty("deviceInvalidated").ValueKind != JsonValueKind.False || side.GetProperty("frameCoveragePercent").GetDouble() < 99.0) return false;
                }
                return true;
            }
            catch (InvalidOperationException) { return false; }
            catch (KeyNotFoundException) { return false; }
        }
    }
}
