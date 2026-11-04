using System.Globalization;
using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record NormalizedDuplexConfiguration(
    string CaptureEndpointId, string RenderEndpointId, string Provenance,
    int? SampleRateHz, string? Encoding, int? Channels, string? WasapiMode,
    string? RequestedPeriod, long? RequestedPeriod100ns, int? RequestedFrames,
    string? Mmcss, int? DurationSeconds)
{
    public string ExactKey => string.Join("|", CaptureEndpointId, RenderEndpointId, Provenance, SampleRateHz, Encoding, Channels, WasapiMode, RequestedPeriod, RequestedPeriod100ns, RequestedFrames, Mmcss, DurationSeconds);
    public string ExplorationKey => string.Join("|", CaptureEndpointId, RenderEndpointId, Provenance, SampleRateHz, Encoding, Channels, WasapiMode, Mmcss, DurationSeconds);
}

public sealed record NormalizedDuplexSide(
    int? ActualBufferFrames, double? FrameCoveragePercent, long? FramesProcessed, long? ExpectedFrames,
    double? EventIntervalMedianMilliseconds, double? EventIntervalP95Milliseconds, double? EventIntervalP99Milliseconds,
    long? Discontinuities, long? TimestampErrors, long? ServiceFaults, long? LongEventIntervals,
    long? IntermediateWaitTimeouts, bool? WatchdogTimedOut, bool? DeviceInvalidated, string? Cleanup, string? Quality);

public sealed record NormalizedDuplexObservation(
    string SourcePath, string? ContainingSeriesIdentity, string? ReportId, string? OperationId,
    DateTimeOffset? TimestampUtc, string Provenance, NormalizedDuplexConfiguration Configuration,
    string Lifecycle, string? DuplexStatus, NormalizedDuplexSide Capture, NormalizedDuplexSide Render,
    double? StartDifferenceMilliseconds, double? SimultaneousRuntimeMilliseconds,
    string? CleanupEvidence, string? PhysicalRtlEvidence, bool IsValid, string ValidationStatus);

public sealed record NormalizedDuplexSeries(string SourcePath, string SessionId, string Lifecycle, string Provenance, int PlannedRuns, int AttemptedRuns, int CompletedRuns, int FailedRuns, int UnknownRuns, int SkippedRuns);

public enum DuplexComparisonMode { ExactConfigurationRepeatability, CrossPeriodExploration }

public sealed record NormalizedDuplexComparison(
    DuplexComparisonMode Mode, string ConfigurationKey, IReadOnlyList<NormalizedDuplexObservation> Observations,
    int TotalObservations, int Clean, int Warnings, int Failed, int Unknown, int Completed, int Skipped,
    double? CaptureCoverageMin, double? CaptureCoverageMedian, double? CaptureCoverageMax,
    double? RenderCoverageMin, double? RenderCoverageMedian, double? RenderCoverageMax,
    double? CaptureP99Min, double? CaptureP99Median, double? CaptureP99Max,
    double? RenderP99Min, double? RenderP99Median, double? RenderP99Max,
    long TotalDiscontinuities, long TotalServiceFaults, long TotalLongEventIntervals, string Summary);

public static class NormalizedDuplexEvidence
{
    public static void ValidateReportContract(JsonElement report)
    {
        Need(report.ValueKind == JsonValueKind.Object, "Duplex report root is not an object.");
        var schema = Number(report, "schemaVersion");
        Need(schema is 1 or 2 && String(report, "reportType") == "wasapi-duplex-validation", "Unsupported Duplex report schema.");
        Need(Guid.TryParse(String(report, "reportId"), out _) && Guid.TryParse(String(report, "operationId"), out _), "Duplex report identity is invalid.");
        Need(Date(report, "timestampUtc") is not null, "Duplex report timestamp is invalid.");
        var provenance = String(report, "provenance");
        Need(provenance is "REAL_WASAPI_DUPLEX" or "TEST_FAKE_WASAPI_DUPLEX", "Duplex provenance is invalid.");
        var requested = report.GetProperty("requested");
        Need(!string.IsNullOrWhiteSpace(String(requested, "captureEndpointId")) && !string.IsNullOrWhiteSpace(String(requested, "renderEndpointId")) &&
            Number(requested, "sampleRateHz") == 48_000 && String(requested, "encoding") == "PCM16" && Number(requested, "channels") == 2 &&
            (String(requested, "period") is "MINIMUM" or "DEFAULT" || CustomDuplexPeriod.IsValidAuthorization(String(requested, "period"))), "Duplex requested configuration is invalid.");
        Need(Number(requested, "durationSeconds") is >= 1 and <= 10 && String(requested, "mmcss") is "OFF" or "PRO_AUDIO", "Duplex requested bounds are invalid.");
        var capture = report.GetProperty("capture"); var render = report.GetProperty("render"); var duplex = report.GetProperty("duplex");
        ValidateSide(capture, "CAPTURE", String(requested, "captureEndpointId")!, schema == 2);
        ValidateSide(render, "RENDER", String(requested, "renderEndpointId")!, schema == 2);
        Need(String(duplex, "status") is "DUPLEX_CLEAN" or "DUPLEX_WARNINGS" or "FAILED" or "INCOMPLETE" or "CANCELLED", "Duplex status is invalid.");
    }

    private static void ValidateSide(JsonElement side, string direction, string endpoint, bool schemaV2)
    {
        Need(String(side, "direction") == direction && String(side, "endpointId") == endpoint, "Duplex side identity is invalid.");
        foreach (var field in new[] { "framesProcessed", "expectedFrames", "events", "packets", "discontinuities", "timestampErrors", "serviceFaults", "eventIntervalsObserved" })
            Need(Long(side, field) is >= 0, $"Duplex counter {field} is invalid.");
        if (schemaV2)
            foreach (var field in new[] { "longEventIntervals", "intermediateWaitTimeouts" }) Need(Long(side, field) is >= 0, $"Duplex counter {field} is invalid.");
        Need(String(side, "quality") is "CLEAN" or "ANOMALIES_PRESENT" or "SEVERE_ANOMALIES" or "UNKNOWN", "Duplex quality is invalid.");
        Need(String(side, "cleanup") is "NOT_ATTEMPTED" or "RELEASE_ATTEMPTED" or "UNKNOWN" or "FAILED", "Duplex cleanup evidence is invalid.");
    }

    private static void Need(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    public static IReadOnlyList<NormalizedDuplexObservation> FromCatalog(EvidenceLibrarySnapshot snapshot) => FromEntries(snapshot.ValidEntries);

    public static IReadOnlyList<NormalizedDuplexObservation> FromEntries(IEnumerable<EvidenceLibraryEntry> entries)
    {
        var observations = new List<NormalizedDuplexObservation>();
        foreach (var entry in entries.Where(e => e.IsValid && e.RecordType is EvidenceLibraryRecordType.DuplexValidation or EvidenceLibraryRecordType.CustomPeriodDuplex or EvidenceLibraryRecordType.DuplexRepeatability))
        {
            if (entry.RecordType == EvidenceLibraryRecordType.DuplexRepeatability)
            {
                if (entry.Payload.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
                    foreach (var run in runs.EnumerateArray())
                        if (run.TryGetProperty("validation", out var validation) && validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("report", out var report) && report.ValueKind == JsonValueKind.Object)
                            observations.Add(Parse(entry, report, entry.SessionId, run.TryGetProperty("operationId", out var id) ? id.GetString() : null, Lifecycle(run)));
                continue;
            }
            var standaloneReport = entry.RecordType == EvidenceLibraryRecordType.CustomPeriodDuplex && entry.Payload.TryGetProperty("report", out var nested) ? nested : entry.Payload;
            observations.Add(Parse(entry, standaloneReport, entry.SeriesIdentity, entry.OperationId, null));
        }
        return observations.Where(o => o.IsValid)
            .GroupBy(o => o.ReportId is not null ? $"report:{o.ReportId}" : $"operation:{o.OperationId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(o => o.SourcePath, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(o => o.TimestampUtc).ThenBy(o => o.SourcePath, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.OperationId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<NormalizedDuplexSeries> Series(EvidenceLibrarySnapshot snapshot) => snapshot.ValidEntries
        .Where(e => e.RecordType == EvidenceLibraryRecordType.DuplexRepeatability && e.SessionId is not null)
        .Select(e => new NormalizedDuplexSeries(e.Path, e.SessionId!, String(e.Payload, "lifecycle") ?? "UNKNOWN", e.Provenance,
            Number(e.Payload, "plannedRuns") ?? 0, Number(e.Payload, "attemptedRuns") ?? 0, Number(e.Payload, "completedRuns") ?? 0,
            Number(e.Payload, "failedRuns") ?? 0, Number(e.Payload, "unknownRuns") ?? 0, Number(e.Payload, "skippedRuns") ?? 0))
        .OrderBy(e => e.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<NormalizedDuplexComparison> Compare(IEnumerable<NormalizedDuplexObservation> source, DuplexComparisonMode mode)
    {
        var rows = source.Where(o => o.IsValid).GroupBy(o => mode == DuplexComparisonMode.ExactConfigurationRepeatability ? o.Configuration.ExactKey : o.Configuration.ExplorationKey, StringComparer.Ordinal);
        return rows.Select(g => Build(mode, g.Key, g.OrderBy(o => o.TimestampUtc).ToArray())).OrderBy(x => x.ConfigurationKey, StringComparer.Ordinal).ToArray();
    }

    private static NormalizedDuplexComparison Build(DuplexComparisonMode mode, string key, IReadOnlyList<NormalizedDuplexObservation> rows)
    {
        var clean = rows.Count(o => o.DuplexStatus == "DUPLEX_CLEAN");
        var failed = rows.Count(o => o.DuplexStatus == "FAILED" || o.Lifecycle == "FAILED");
        var unknown = rows.Count(o => o.DuplexStatus is null or "UNKNOWN" or "INCOMPLETE" or "CANCELLED" || o.Lifecycle == "UNKNOWN");
        var warnings = rows.Count - clean - failed - unknown;
        var completed = rows.Count(o => o.Lifecycle == "COMPLETED");
        var skipped = rows.Count(o => o.Lifecycle == "SKIPPED");
        static (double?, double?, double?) Stats(IEnumerable<double?> values)
        {
            var a = values.Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).OrderBy(v => v).ToArray();
            return a.Length == 0 ? (null, null, null) : (a[0], a[(a.Length - 1) / 2], a[^1]);
        }
        var cc = Stats(rows.Select(o => o.Capture.FrameCoveragePercent)); var rc = Stats(rows.Select(o => o.Render.FrameCoveragePercent));
        var cp = Stats(rows.Select(o => o.Capture.EventIntervalP99Milliseconds)); var rp = Stats(rows.Select(o => o.Render.EventIntervalP99Milliseconds));
        var summary = $"{mode}: {rows.Count} observations; CLEAN={clean}, WARNINGS={warnings}, FAILED={failed}, UNKNOWN={unknown}, completed={completed}, skipped={skipped}.";
        return new(mode, key, rows, rows.Count, clean, warnings, failed, unknown, completed, skipped, cc.Item1, cc.Item2, cc.Item3, rc.Item1, rc.Item2, rc.Item3, cp.Item1, cp.Item2, cp.Item3, rp.Item1, rp.Item2, rp.Item3,
            Sum(rows, o => o.Capture.Discontinuities, o => o.Render.Discontinuities), Sum(rows, o => o.Capture.ServiceFaults, o => o.Render.ServiceFaults), Sum(rows, o => o.Capture.LongEventIntervals, o => o.Render.LongEventIntervals), summary);
    }

    private static long Sum(IEnumerable<NormalizedDuplexObservation> rows, Func<NormalizedDuplexObservation, long?> left, Func<NormalizedDuplexObservation, long?> right) => rows.Select(o => (left(o) ?? 0) + (right(o) ?? 0)).Sum();

    private static NormalizedDuplexObservation Parse(EvidenceLibraryEntry entry, JsonElement report, string? series, string? operationOverride, string? lifecycleOverride)
    {
        try
        {
            var requested = report.GetProperty("requested"); var capture = report.GetProperty("capture"); var render = report.GetProperty("render"); var duplex = report.GetProperty("duplex");
            var provenance = Canonical(String(report, "provenance"));
            var configuration = new NormalizedDuplexConfiguration(String(requested, "captureEndpointId")!, String(requested, "renderEndpointId")!, provenance, Number(requested, "sampleRateHz"), String(requested, "encoding"), Number(requested, "channels"), "EXCLUSIVE", String(requested, "period"), Period100ns(String(requested, "period")), Frames(String(requested, "period")), String(requested, "mmcss"), Number(requested, "durationSeconds"));
            return new(entry.Path, series, String(report, "reportId"), operationOverride ?? String(report, "operationId"), Date(report, "timestampUtc"), provenance, configuration, lifecycleOverride ?? String(entry.Payload, "lifecycle") ?? "COMPLETED", String(duplex, "status"), Side(capture), Side(render), NumberDouble(duplex, "startDifferenceMilliseconds"), NumberDouble(duplex, "simultaneousRuntimeMilliseconds"), $"{String(capture, "cleanup")}|{String(render, "cleanup")}", String(report, "physicalRtl") ?? "NOT_MEASURED", true, "VALID");
        }
        catch (Exception ex)
        {
            return new(entry.Path, series, String(report, "reportId"), operationOverride ?? String(report, "operationId"), Date(report, "timestampUtc"), "UNKNOWN", new("", "", "UNKNOWN", null, null, null, null, null, null, null, null, null), "UNKNOWN", "UNKNOWN", EmptySide(), EmptySide(), null, null, null, null, false, ex.GetType().Name);
        }
    }

    private static NormalizedDuplexSide EmptySide() => new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
    private static NormalizedDuplexSide Side(JsonElement e) => new(Number(e, "actualBufferFrames"), NumberDouble(e, "frameCoveragePercent"), Long(e, "framesProcessed"), Long(e, "expectedFrames"), NumberDouble(e, "eventIntervalMedianMilliseconds"), NumberDouble(e, "eventIntervalP95Milliseconds"), NumberDouble(e, "eventIntervalP99Milliseconds"), Long(e, "discontinuities"), Long(e, "timestampErrors"), Long(e, "serviceFaults"), Long(e, "longEventIntervals"), Long(e, "intermediateWaitTimeouts"), Bool(e, "watchdogTimedOut"), Bool(e, "deviceInvalidated"), String(e, "cleanup"), String(e, "quality"));
    private static string Canonical(string? value) => value?.StartsWith("REAL_", StringComparison.Ordinal) == true ? "REAL" : value?.StartsWith("TEST_", StringComparison.Ordinal) == true ? "SYNTHETIC" : value ?? "UNKNOWN";
    private static long? Period100ns(string? period) => period?.StartsWith("CUSTOM:", StringComparison.Ordinal) == true && long.TryParse(period.Split(':')[1], out var value) ? value : null;
    private static int? Frames(string? period) => period?.StartsWith("CUSTOM:", StringComparison.Ordinal) == true && int.TryParse(period.Split(':')[2], out var value) ? value : null;
    private static DateTimeOffset? Date(JsonElement e, string name) => DateTimeOffset.TryParse(String(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.ToUniversalTime() : null;
    private static string? String(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
    private static int? Number(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var v) ? v : null;
    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var v) ? v : null;
    private static double? NumberDouble(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && double.IsFinite(x.GetDouble()) ? x.GetDouble() : null;
    private static bool? Bool(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind is JsonValueKind.True or JsonValueKind.False ? x.GetBoolean() : null;
    private static string? Lifecycle(JsonElement e)
    {
        if (!e.TryGetProperty("lifecycle", out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString()?.ToUpperInvariant();
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number switch { 2 => "COMPLETED", 3 => "SKIPPED", 4 => "FAILED", 5 => "UNKNOWN", _ => "UNKNOWN" } : "UNKNOWN";
    }
}
