using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record CustomDuplexPeriod(int Frames, long Period100ns, string EvidenceReportId)
{
    public string AuthorizationValue => $"CUSTOM:{Period100ns}:{Frames}";
    public string Display => $"{Frames} frames · {Period100ns}×100 ns";

    public static bool IsValidAuthorization(string? value)
    {
        var parts = value?.Split(':');
        if (parts is not ["CUSTOM", var periodText, var framesText] || !long.TryParse(periodText, out var period) || !int.TryParse(framesText, out var frames) || period <= 0 || frames <= 0 || frames > 48_000)
            return false;
        var expected = ((long)frames * 10_000_000 + 24_000) / 48_000;
        return Math.Abs(period - expected) <= 1;
    }
}

public sealed record CustomPeriodDuplexPreparation(WindowsEndpointDiscoveryDto Discovery, WindowsEndpointDto Capture,
    WindowsEndpointDto Render, CustomDuplexPeriod Period, WorkerScanPlan WorkerPlan, string DiscoveryFingerprint, string PlanText);

public sealed record CustomPeriodDuplexResult(string Lifecycle, string OperationId, string Period, string? ReportPath,
    string PersistenceStatus, JsonElement? Report, string? Error = null);

public interface ICustomPeriodDuplexWorkflow
{
    CustomPeriodDuplexPreparation Prepare(WindowsEndpointDiscoveryDto discovery, WindowsEndpointDto capture, WindowsEndpointDto render,
        PeriodDiscoverySeriesResult series, int frames);
    Task<CustomPeriodDuplexResult> RunApprovedAsync(CustomPeriodDuplexPreparation preparation, Guid confirmationId,
        CancellationToken cancellationToken = default);
}

public static class CustomPeriodDuplexEvidence
{
    public static CustomDuplexPeriod Select(PeriodDiscoverySeriesResult series, string captureId, string renderId, int frames, bool allowSynthetic = false)
    {
        var provenanceAllowed = allowSynthetic
            ? series.Reports.All(r => r.Provenance is "REAL_WASAPI_INITIALIZE_ONLY" or "TEST_FAKE_PERIOD_BACKEND")
            : series.Reports.All(r => r.Provenance == "REAL_WASAPI_INITIALIZE_ONLY");
        if (series.Lifecycle != "COMPLETED" || series.Reports.Count == 0 || !provenanceAllowed || series.Reports.SelectMany(r => r.Candidates).Any(c => c.Classification == "UNKNOWN"))
            throw new InvalidDataException("Custom Duplex requires a completed REAL C4.2A period-discovery series.");
        var reports = series.Reports.Where(r => r.EndpointId is not null && (r.EndpointId == captureId || r.EndpointId == renderId) && r.Candidates.Count == 1 && r.Candidates[0].RequestedFrames == frames).ToArray();
        var capture = reports.SingleOrDefault(r => r.EndpointId == captureId && r.Direction == "CAPTURE");
        var render = reports.SingleOrDefault(r => r.EndpointId == renderId && r.Direction == "RENDER");
        if (capture is null || render is null || capture.Candidates[0].Classification != "ACCEPTED" || render.Candidates[0].Classification != "ACCEPTED" ||
            capture.Candidates[0].ActualBufferFrames != frames || render.Candidates[0].ActualBufferFrames != frames ||
            capture.Candidates[0].CleanupStatus is "UNKNOWN" or "FAILED" || render.Candidates[0].CleanupStatus is "UNKNOWN" or "FAILED")
            throw new InvalidDataException("Selected custom period is not BOTH_EXACT for the current Capture/Render endpoints.");
        if (capture.Candidates[0].ActualPeriod100ns != capture.Candidates[0].RequestedEquivalentPeriod100ns || render.Candidates[0].ActualPeriod100ns != render.Candidates[0].RequestedEquivalentPeriod100ns)
            throw new InvalidDataException("Selected custom period actual/requested geometry does not match.");
        return new(frames, capture.Candidates[0].RequestedPeriod100ns, capture.ReportId);
    }
}

public static class CustomPeriodDuplexReportStorage
{
    public static void ValidateReadBack(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("Custom-period Duplex report is missing or outside the bounded size limit.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("reportType").GetString() != "wasapi-custom-period-duplex" || root.GetProperty("lifecycle").GetString() != "COMPLETED" || root.GetProperty("report").GetProperty("provenance").GetString() != "REAL_WASAPI_DUPLEX" || !Guid.TryParse(root.GetProperty("operationId").GetString(), out _))
            throw new InvalidDataException("Custom-period Duplex report contract validation failed.");
    }

    public static string Save(CustomPeriodDuplexResult result, string? rootDirectory = null)
    {
        if (result.Report is not { } report || result.Lifecycle != "COMPLETED" || report.GetProperty("provenance").GetString() != "REAL_WASAPI_DUPLEX")
            throw new InvalidDataException("Only completed REAL custom-period Duplex evidence may be persisted.");
        rootDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "CustomDuplex");
        Directory.CreateDirectory(rootDirectory);
        var path = Path.Combine(rootDirectory, $"custom-duplex-{result.OperationId}.json");
        if (File.Exists(path)) throw new IOException("A custom-period Duplex report with this operation identity already exists.");
        var envelope = new { schemaVersion = 1, reportType = "wasapi-custom-period-duplex", lifecycle = result.Lifecycle, operationId = result.OperationId, period = result.Period, report };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Custom-period Duplex report exceeds the bounded size limit.");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, bytes);
        try { File.Move(temp, path); } catch { if (File.Exists(temp)) File.Delete(temp); throw; }
        using var persisted = JsonDocument.Parse(File.ReadAllBytes(path));
        if (persisted.RootElement.GetProperty("report").GetProperty("provenance").GetString() != "REAL_WASAPI_DUPLEX") throw new InvalidDataException("Persisted custom-period Duplex evidence failed its provenance check.");
        return path;
    }
}
