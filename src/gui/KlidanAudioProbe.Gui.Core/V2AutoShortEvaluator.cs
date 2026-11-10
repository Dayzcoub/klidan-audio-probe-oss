using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record V2AutoShortCandidate(string Period, long? Period100ns, int? BufferFrames,
    string CaptureSupport, string RenderSupport, string Status, string Reason, JsonElement? Report = null)
{
    public int? RenderBufferFrames { get; init; }
    public long? RenderPeriod100ns { get; init; }
    public string? CaptureSupportSource { get; init; }
    public string? RenderSupportSource { get; init; }
    public string SupportSourceSummary => $"{CaptureSupportSource ?? "UNKNOWN"}/{RenderSupportSource ?? "UNKNOWN"}";
    public string SupportSummary => $"{CaptureSupport}/{RenderSupport} ({SupportSourceSummary})";
    public string PeriodSummary => $"{Period100ns?.ToString() ?? "UNKNOWN"}/{RenderPeriod100ns?.ToString() ?? "UNKNOWN"}";
    public string BufferSummary => $"{BufferFrames?.ToString() ?? "UNKNOWN"}/{RenderBufferFrames?.ToString() ?? "UNKNOWN"}";
}
public sealed record V2AutoShortAssessment(IReadOnlyList<V2AutoShortCandidate> Candidates,
    string Outcome, string? RecommendedPeriod, string Qualification);

/// <summary>Classifies bounded AUTO_SHORT evidence without UI or device dependencies.</summary>
public static class V2AutoShortEvaluator
{
    public static V2AutoShortAssessment Evaluate(IEnumerable<V2AutoShortCandidate> candidates)
    {
        var ordered = candidates.OrderBy(c => c.Period == "DEFAULT" ? 0 : 1).ToArray();
        if (ordered.Any(c => c.Period is not ("DEFAULT" or "MINIMUM")) || ordered.Select(c => c.Period).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("AUTO_SHORT period candidates must be unique DEFAULT/MINIMUM entries.", nameof(candidates));
        var unknown = ordered.Any(c => c.Status == "UNKNOWN");
        var clean = ordered.Where(c => c.Status == "SHORT_CLEAN").ToArray();
        var recommended = clean.OrderBy(c => c.Period == "MINIMUM" ? 0 : 1).FirstOrDefault();
        return new(ordered, unknown ? "UNKNOWN" : clean.Length == 0 ? "NO_RECOMMENDATION" : "SHORT_CLEAN",
            unknown ? null : recommended?.Period, "NOT_CERTIFIED");
    }

    public static V2AutoShortCandidate ClassifyReport(string period, long? period100ns, int? frames,
        string operationId, string captureEndpointId, string renderEndpointId, WorkerScanV2Route expectedRoute, string routeFingerprint, JsonElement report)
    {
        try
        {
            var requested = report.GetProperty("requested");
            var duplex = report.GetProperty("duplex");
            var capture = report.GetProperty("capture"); var render = report.GetProperty("render");
            var route = report.GetProperty("route");
            var captureRoute = route.GetProperty("capture"); var renderRoute = route.GetProperty("render");
            bool SideMatches(JsonElement actual, WorkerScanV2Side expected) =>
                actual.GetProperty("sampleRateHz").GetInt32() == expected.SampleRateHz && actual.GetProperty("encoding").GetString() == expected.Encoding &&
                actual.GetProperty("channels").GetInt32() == expected.Channels && actual.GetProperty("channelMask").GetUInt32() == expected.ChannelMask &&
                actual.GetProperty("selectedIndicesZeroBased").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(expected.SelectedIndicesZeroBased);
            var identity = report.GetProperty("schemaVersion").GetInt32() == 3 && report.GetProperty("reportType").GetString() == "wasapi-duplex-validation-v2" &&
                Guid.TryParse(report.GetProperty("reportId").GetString(), out _) && Guid.TryParse(report.GetProperty("operationId").GetString(), out var reportOperation) && reportOperation == Guid.Parse(operationId) &&
                report.GetProperty("provenance").GetString() is "REAL_WASAPI_DUPLEX_V2" or "TEST_FAKE_WASAPI_DUPLEX_V2" &&
                requested.GetProperty("captureEndpointId").GetString() == captureEndpointId &&
                requested.GetProperty("renderEndpointId").GetString() == renderEndpointId &&
                requested.GetProperty("period").GetString() == period &&
                requested.GetProperty("durationSeconds").GetInt32() == 5 && requested.GetProperty("mmcss").GetString() == "OFF" &&
                requested.GetProperty("sampleRateHz").GetInt32() == expectedRoute.Capture.SampleRateHz && requested.GetProperty("channels").GetInt32() == expectedRoute.Capture.Channels && requested.GetProperty("encoding").GetString() == expectedRoute.Capture.Encoding &&
                route.GetProperty("fingerprint").GetString() == routeFingerprint && route.GetProperty("mode").GetString() == expectedRoute.Mode &&
                captureRoute.GetProperty("endpointId").GetString() == captureEndpointId && renderRoute.GetProperty("endpointId").GetString() == renderEndpointId &&
                SideMatches(captureRoute, expectedRoute.Capture) && SideMatches(renderRoute, expectedRoute.Render);
            if (!identity || capture.GetProperty("endpointId").GetString() != captureEndpointId || render.GetProperty("endpointId").GetString() != renderEndpointId)
                return new(period, period100ns, frames, "SUPPORTED_REPORTED", "SUPPORTED_REPORTED", "UNKNOWN", "REPORT_IDENTITY_MISMATCH", report.Clone());
            var clean = duplex.GetProperty("status").GetString() == "DUPLEX_CLEAN" &&
                new[] { capture, render }.All(s => s.GetProperty("quality").GetString() == "CLEAN" &&
                    s.GetProperty("cleanup").GetString() == "RELEASE_ATTEMPTED" &&
                    s.GetProperty("startSucceeded").GetBoolean() && s.GetProperty("stopSucceeded").GetBoolean() &&
                    s.GetProperty("durationReached").GetBoolean() &&
                    s.GetProperty("framesProcessed").GetInt64() > 0 && s.GetProperty("expectedFrames").GetInt64() > 0 && s.GetProperty("actualBufferFrames").GetInt32() > 0 &&
                    s.GetProperty("frameCoveragePercent").GetDouble() >= 99.0 &&
                    s.GetProperty("discontinuities").GetInt64() == 0 &&
                    s.GetProperty("timestampErrors").GetInt64() == 0 &&
                    s.GetProperty("serviceFaults").GetInt64() == 0 && s.GetProperty("intermediateWaitTimeouts").GetInt64() == 0 &&
                    s.GetProperty("packetDrainLimitHits").GetInt64() == 0 && s.GetProperty("longEventIntervals").GetInt64() == 0 &&
                    (!s.TryGetProperty("deviceInvalidated", out var invalid) || !invalid.GetBoolean()) &&
                    (!s.TryGetProperty("watchdogTimedOut", out var watchdog) || !watchdog.GetBoolean()));
            var safeCleanup = new[] { capture, render }.All(s => s.TryGetProperty("cleanup", out var cleanup) && cleanup.GetString() == "RELEASE_ATTEMPTED");
            var unsafeSignal = new[] { capture, render }.Any(s => new[] { "deviceInvalidated", "watchdogTimedOut", "cancelled" }.Any(n => s.TryGetProperty(n, out var flag) && flag.ValueKind == JsonValueKind.True));
            var outcome = clean ? "SHORT_CLEAN" : safeCleanup && !unsafeSignal && duplex.GetProperty("status").GetString() is "DUPLEX_WARNINGS" or "FAILED" ? "SHORT_FAILED" : "UNKNOWN";
            return new(period, period100ns, frames, "SUPPORTED_REPORTED", "SUPPORTED_REPORTED", outcome,
                clean ? "DUPLEX_CLEAN" : outcome == "SHORT_FAILED" ? "CONFIRMED_NON_CLEAN" : "INCOMPLETE_OR_UNSAFE_REPORT", report.Clone());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { return new(period, period100ns, frames, "SUPPORTED_REPORTED", "SUPPORTED_REPORTED", "UNKNOWN", "REPORT_INCOMPLETE", report.Clone()); }
    }
}
