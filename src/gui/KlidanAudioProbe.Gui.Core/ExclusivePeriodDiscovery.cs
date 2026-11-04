using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record PeriodDiscoveryCandidate(int Frames, long Period100ns)
{
    public string AuthorizationValue => $"CUSTOM:{Period100ns}:{Frames}";
    public string Display => $"{Frames} frames · {Period100ns}×100 ns";
}

public sealed record PeriodDiscoveryPreparation(WindowsEndpointDiscoveryDto Discovery, WindowsEndpointDto Capture, WindowsEndpointDto Render,
    IReadOnlyList<PeriodDiscoveryCandidate> Candidates, WorkerScanPlan WorkerPlan, string DiscoveryFingerprint, string PlanText);

public sealed record PeriodDiscoveryOperationDiagnostic(int OperationIndex, string OperationId, string EndpointId,
    string Direction, string Outcome, long DurationMilliseconds, int TimeoutSeconds, string? ExceptionType = null,
    string? WorkerErrorCode = null);

public sealed record PeriodDiscoveryTerminationDiagnostics(string TerminationReason, string? ExceptionType,
    string? WorkerErrorCode, int? OperationIndex, string? LastOperationId, string SessionCloseStatus,
    int OperationTimeoutSeconds, IReadOnlyList<PeriodDiscoveryOperationDiagnostic> Operations)
{
    public string Summary =>
        $"Причина: {TerminationReason}; операция: {OperationIndex?.ToString() ?? "—"}; " +
        $"последняя: {LastOperationId ?? "—"}; close: {SessionCloseStatus}; " +
        $"timeout: {OperationTimeoutSeconds} с" +
        (string.IsNullOrWhiteSpace(WorkerErrorCode) ? string.Empty : $"; Worker: {WorkerErrorCode}");
}

public sealed record PeriodDiscoverySeriesResult(string Lifecycle, IReadOnlyList<ExclusivePeriodDiscoveryReportDto> Reports,
    IReadOnlyList<string> Progress, string MatrixText, string? ReportPath, string PersistenceStatus,
    PeriodDiscoveryTerminationDiagnostics? Diagnostics = null);

public interface IExclusivePeriodDiscoveryWorkflow
{
    Task<PeriodDiscoveryPreparation> PrepareAsync(WindowsEndpointDiscoveryDto discovery, WindowsEndpointDto capture, WindowsEndpointDto render, CancellationToken cancellationToken = default);
    Task<PeriodDiscoverySeriesResult> RunApprovedAsync(PeriodDiscoveryPreparation preparation, Guid confirmationId, Action<string>? progress = null, CancellationToken cancellationToken = default);
}

public static class ExclusivePeriodDiscoveryPlanning
{
    public const int MaximumCandidates = 8;
    public static IReadOnlyList<PeriodDiscoveryCandidate> Generate(long minimum100ns, long default100ns, int rate = 48_000)
    {
        if (rate <= 0 || minimum100ns <= 0 || default100ns <= minimum100ns) return [];
        var minimum = Frames(minimum100ns, rate); var maximum = Frames(default100ns, rate);
        if (maximum <= minimum) return [];
        var selected = new SortedSet<int>();
        foreach (var frames in new[] { 192, 240, 288, 336, 384, 432 }) if (frames > minimum && frames < maximum) selected.Add(frames);
        foreach (var frames in new[] { minimum + (maximum - minimum) / 4, minimum + (maximum - minimum) / 2, minimum + (maximum - minimum) * 3 / 4 })
            if (frames > minimum && frames < maximum && selected.Count < MaximumCandidates) selected.Add(frames);
        return selected.Take(MaximumCandidates).Select(frames => new PeriodDiscoveryCandidate(frames, Duration(frames, rate))).ToArray();
    }
    public static int Frames(long duration100ns, int rate) => checked((int)((duration100ns * rate + 5_000_000) / 10_000_000));
    public static long Duration(int frames, int rate) => (frames * 10_000_000L + rate / 2) / rate;
    public static string Fingerprint(WindowsEndpointDiscoveryDto discovery) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(discovery))));
}

public static class ExclusivePeriodDiscoverySeriesJson
{
    private const int MaximumReports = 16;
    private const int MaximumBytes = 1_048_576;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string SaveNoOverwrite(string lifecycle, IReadOnlyList<ExclusivePeriodDiscoveryReportDto> reports, string matrix, string directory,
        PeriodDiscoveryTerminationDiagnostics? diagnostics = null)
    {
        Validate(lifecycle, reports, matrix);
        var envelope = new SeriesEnvelope(1, "wasapi-exclusive-period-discovery-series", lifecycle, reports, matrix,
            reports.Count > 0 && reports.All(r => r.Provenance == "REAL_WASAPI_INITIALIZE_ONLY") ? "REAL_WASAPI_INITIALIZE_ONLY" : "MIXED_OR_TEST",
            reports.Where(r => r.Direction == "CAPTURE").Select(r => r.EndpointId).Distinct(StringComparer.Ordinal).SingleOrDefault(),
            reports.Where(r => r.Direction == "RENDER").Select(r => r.EndpointId).Distinct(StringComparer.Ordinal).SingleOrDefault(),
            diagnostics?.TerminationReason, diagnostics?.ExceptionType, diagnostics?.WorkerErrorCode, diagnostics?.OperationIndex,
            diagnostics?.LastOperationId, diagnostics?.SessionCloseStatus, diagnostics?.OperationTimeoutSeconds, diagnostics?.Operations);
        Validate(envelope);
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("D"); var destination = Path.Combine(directory, $"period-discovery-{id}.json"); var temporary = Path.Combine(directory, $".{id}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Period discovery series exceeds the bounded report size.");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, destination, false);
            var readbackBytes = File.ReadAllBytes(destination);
            if (readbackBytes.Length > MaximumBytes) throw new InvalidDataException("Period discovery series read-back exceeds the bounded report size.");
            ValidateReadBack(readbackBytes);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void ValidateReadBack(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Period discovery series read-back exceeds the bounded report size.");
        ValidateReadBack(bytes);
    }

    private static void ValidateReadBack(byte[] bytes) =>
        Validate(JsonSerializer.Deserialize<SeriesEnvelope>(bytes, Options) ?? throw new InvalidDataException("Period discovery series read-back was empty."));

    private sealed record SeriesEnvelope(int SchemaVersion, string ReportType, string Lifecycle, IReadOnlyList<ExclusivePeriodDiscoveryReportDto> Reports,
        string Eligibility, string Provenance, string? CaptureEndpointId, string? RenderEndpointId,
        string? TerminationReason = null, string? ExceptionType = null, string? WorkerErrorCode = null,
        int? OperationIndex = null, string? LastOperationId = null, string? SessionCloseStatus = null,
        int? OperationTimeoutSeconds = null, IReadOnlyList<PeriodDiscoveryOperationDiagnostic>? OperationDiagnostics = null);

    private static void Validate(string lifecycle, IReadOnlyList<ExclusivePeriodDiscoveryReportDto> reports, string matrix) =>
        Validate(new SeriesEnvelope(1, "wasapi-exclusive-period-discovery-series", lifecycle, reports, matrix,
            reports.Count > 0 && reports.All(r => r.Provenance == "REAL_WASAPI_INITIALIZE_ONLY") ? "REAL_WASAPI_INITIALIZE_ONLY" : "MIXED_OR_TEST",
            reports.Where(r => r.Direction == "CAPTURE").Select(r => r.EndpointId).Distinct(StringComparer.Ordinal).SingleOrDefault(),
            reports.Where(r => r.Direction == "RENDER").Select(r => r.EndpointId).Distinct(StringComparer.Ordinal).SingleOrDefault()));

    private static void Validate(SeriesEnvelope envelope)
    {
        if (envelope.SchemaVersion != 1 || envelope.ReportType != "wasapi-exclusive-period-discovery-series" || envelope.Lifecycle is not ("COMPLETED" or "UNKNOWN") || envelope.Reports is null || envelope.Reports.Count > MaximumReports || envelope.Eligibility is null || envelope.Provenance is not ("REAL_WASAPI_INITIALIZE_ONLY" or "MIXED_OR_TEST") || envelope.OperationDiagnostics is { Count: > MaximumReports })
            throw new InvalidDataException("Period discovery series envelope is invalid.");
        var reports = envelope.Reports;
        if (reports.Count == 0 && envelope.Provenance == "REAL_WASAPI_INITIALIZE_ONLY") throw new InvalidDataException("Empty period discovery evidence cannot be REAL.");
        if (envelope.Provenance == "REAL_WASAPI_INITIALIZE_ONLY" && reports.Any(r => r.Provenance != "REAL_WASAPI_INITIALIZE_ONLY")) throw new InvalidDataException("Mixed evidence cannot be labeled REAL.");
        if (reports.Select(r => r.ReportId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != reports.Count || reports.Select(r => r.OperationId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != reports.Count)
            throw new InvalidDataException("Period discovery report and operation identities must be unique.");
        foreach (var report in reports)
        {
            _ = ExclusivePeriodDiscoveryReportJson.Serialize(report);
            if (report.Candidates.Count != 1) throw new InvalidDataException("A series operation must contain exactly one candidate.");
            var candidate = report.Candidates[0];
            if (Math.Abs(candidate.RequestedEquivalentPeriod100ns - candidate.RequestedPeriod100ns) > 1) throw new InvalidDataException("Requested period and frame conversion disagree.");
            if (candidate.Classification == "ACCEPTED" && (candidate.ActualBufferFrames != candidate.RequestedFrames || candidate.ActualPeriod100ns != candidate.RequestedEquivalentPeriod100ns || candidate.AlignmentRequiresExplicitConfirmation || candidate.CleanupStatus != "RELEASE_ATTEMPTED"))
                throw new InvalidDataException("Exact acceptance lacks matching actual configuration or release-attempt evidence.");
        }
        var captures = reports.Where(r => r.Direction == "CAPTURE").Select(r => r.EndpointId).Distinct(StringComparer.Ordinal).ToArray();
        var renders = reports.Where(r => r.Direction == "RENDER").Select(r => r.EndpointId).Distinct(StringComparer.Ordinal).ToArray();
        if (captures.Length > 1 || renders.Length > 1 || envelope.CaptureEndpointId != captures.SingleOrDefault() || envelope.RenderEndpointId != renders.SingleOrDefault())
            throw new InvalidDataException("Series endpoint identity is inconsistent.");
    }
}
