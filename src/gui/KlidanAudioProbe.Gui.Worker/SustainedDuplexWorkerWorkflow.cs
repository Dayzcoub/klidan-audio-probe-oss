using System.Text.Json;
using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

public sealed record SustainedDuplexResult(string OperationId, string Period, string Mmcss, int DurationSeconds, string Lifecycle, string Classification, string CleanupStatus, DuplexValidationDto? Validation, string? ReportPath, string Message, string PersistenceStatus = "NOT_ATTEMPTED");

public sealed class SustainedDuplexWorkerWorkflow(NativeWorkerClient worker)
{
    public async Task<SustainedDuplexResult> RunAsync(WindowsEndpointDiscoveryDto discovery, WindowsEndpointDto capture, WindowsEndpointDto render, SustainedDuplexCandidate candidate, int durationSeconds, string mmcss, Guid confirmationId, Action<string, string>? progress, CancellationToken cancellationToken)
    {
        if (durationSeconds is not (60 or 300 or 600)) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (mmcss is not ("OFF" or "PRO_AUDIO")) throw new ArgumentOutOfRangeException(nameof(mmcss));
        if (worker.State.State != WorkerConnectionState.ConnectedFake) throw new InvalidOperationException("Worker is not connected.");
        var currentCapture = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == capture.EndpointId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE") ?? throw new InvalidOperationException("Capture endpoint is stale or inactive.");
        var currentRender = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == render.EndpointId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE") ?? throw new InvalidOperationException("Render endpoint is stale or inactive.");
        var plan = new WorkerScanPlan([new(currentCapture.EndpointId!, "CAPTURE"), new(currentRender.EndpointId!, "RENDER")], ["ValidateSustainedDuplex"], [new(48_000, "PCM16", 2, [candidate.Period], [mmcss])], durationSeconds + 30, durationSeconds, 1);
        var operationId = Guid.NewGuid();
        WorkerScanSessionToken? session = null;
        NativeWorkerClient.SharedOperationLease? sharedOperation = null;
        SustainedDuplexResult? result = null;
        try
        {
            sharedOperation = worker.AcquireSharedOperation("sustained Duplex");
            session = await worker.RegisterScanSessionAsync(plan, true, confirmationId, cancellationToken).ConfigureAwait(false);
            progress?.Invoke(operationId.ToString("D"), $"{candidate.Period} · MMCSS {mmcss} · 0/{durationSeconds}s · cancellation {(cancellationToken.IsCancellationRequested ? "requested" : "not requested")}");
            var validation = await worker.ValidateSustainedDuplexInScanAsync(operationId.ToString("D"), currentCapture.EndpointId!, currentRender.EndpointId!, candidate.Period, durationSeconds, mmcss, session, operationId, cancellationToken).ConfigureAwait(false);
            var classification = SustainedDuplexClassification.Classify(validation.Report, durationSeconds);
            var cleanup = validation.Report.TryGetProperty("capture", out var side) && side.TryGetProperty("cleanup", out var cleanupValue) ? cleanupValue.GetString() ?? "UNKNOWN" : "UNKNOWN";
            var lifecycle = classification is "UNKNOWN" or "INCOMPLETE" or "SUSTAINED_FAILED" ? classification == "UNKNOWN" ? "UNKNOWN" : "FAILED" : "COMPLETED";
            result = new(operationId.ToString("D"), candidate.Period, mmcss, durationSeconds, lifecycle, classification, cleanup, validation, null, $"Continuous sustained Duplex · MMCSS {mmcss} · {durationSeconds}/{durationSeconds}s; quality is classified independently from lifecycle.", "NOT_SAVED_SYNTHETIC");
            if (validation.Report.GetProperty("provenance").GetString() == "REAL_WASAPI_SUSTAINED_DUPLEX")
            {
                try { result = result with { ReportPath = SustainedDuplexReportStorage.Save(validation, candidate.Period, lifecycle, classification), PersistenceStatus = "SAVED_AND_RELOADED" }; }
                catch (Exception ex) { result = result with { Lifecycle = "FAILED", Classification = "UNKNOWN", PersistenceStatus = "SAVE_FAILED", Message = result.Message + $" Persistence failed: {ex.GetType().Name}: {ex.Message}" }; }
            }
        }
        catch (OperationCanceledException) when (result is null)
        {
            result = new(operationId.ToString("D"), candidate.Period, mmcss, durationSeconds, "UNKNOWN", "UNKNOWN", "UNKNOWN", null, null, $"Cancellation during {durationSeconds}-second Duplex left completion and cleanup UNKNOWN.");
        }
        catch (Exception ex) when (result is null)
        {
            result = new(operationId.ToString("D"), candidate.Period, mmcss, durationSeconds, "UNKNOWN", "UNKNOWN", "UNKNOWN", null, null, $"Sustained Duplex outcome UNKNOWN: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (session is { } token)
            {
                try
                {
                    using var close = new CancellationTokenSource(TimeSpan.FromSeconds(NativeWorkerClient.SessionCloseTimeoutSeconds));
                    await worker.CloseScanSessionAsync(token, close.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = result is null
                        ? new(operationId.ToString("D"), candidate.Period, mmcss, durationSeconds, "UNKNOWN", "UNKNOWN", "UNKNOWN", null, null, $"Worker session closure UNKNOWN: {ex.GetType().Name}: {ex.Message}")
                        : result with { Lifecycle = "UNKNOWN", Classification = "UNKNOWN", CleanupStatus = "UNKNOWN", Message = result.Message + $" Worker session closure UNKNOWN: {ex.GetType().Name}: {ex.Message}" };
                }
            }
            progress?.Invoke(operationId.ToString("D"), $"{candidate.Period} · MMCSS {mmcss} · {(result?.Lifecycle == "COMPLETED" ? durationSeconds : 0)}/{durationSeconds}s · cancellation {(cancellationToken.IsCancellationRequested ? "requested" : "not requested")}");
            sharedOperation?.Dispose();
        }
        return result ?? throw new InvalidOperationException("Sustained Duplex did not produce a terminal ledger result.");
    }
}

public static class SustainedDuplexReportStorage
{
    public static string Save(DuplexValidationDto validation, string period, string lifecycle, string classification, string? rootDirectory = null)
    {
        if (validation.Report.GetProperty("provenance").GetString() != "REAL_WASAPI_SUSTAINED_DUPLEX") throw new InvalidDataException("Only real sustained evidence may be persisted.");
        rootDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "SustainedDuplex");
        Directory.CreateDirectory(rootDirectory);
        if (!Guid.TryParse(validation.OperationId, out var operationGuid) || operationGuid == Guid.Empty) throw new InvalidDataException("Sustained Duplex operation identity is invalid.");
        var operationId = operationGuid.ToString("D");
        var path = Path.Combine(rootDirectory, $"sustained-duplex-{operationId}.json");
        if (File.Exists(path)) throw new IOException("A sustained Duplex report with this operation identity already exists.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, reportType = "wasapi-sustained-duplex", lifecycle, classification, operationId, period, report = validation.Report }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Sustained Duplex report exceeds the bounded size limit.");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, bytes);
        try { File.Move(temp, path); } catch { if (File.Exists(temp)) File.Delete(temp); throw; }
        ValidateReadBack(path, operationId, period, lifecycle, classification, validation.Report);
        return path;
    }

    public static void ValidateReadBack(string path, string? expectedOperationId = null, string? expectedPeriod = null, string? expectedLifecycle = null, string? expectedClassification = null, JsonElement? expectedReport = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("Sustained Duplex report is missing or exceeds the bounded size limit.");
        using var readback = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        var root = readback.RootElement;
        var operationId = root.GetProperty("operationId").GetString();
        var report = root.GetProperty("report");
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("reportType").GetString() != "wasapi-sustained-duplex" || !Guid.TryParse(operationId, out var operationGuid) || operationGuid == Guid.Empty || expectedOperationId is not null && (!Guid.TryParse(expectedOperationId, out var expectedGuid) || expectedGuid != operationGuid) || expectedPeriod is not null && root.GetProperty("period").GetString() != expectedPeriod || expectedLifecycle is not null && root.GetProperty("lifecycle").GetString() != expectedLifecycle || expectedClassification is not null && root.GetProperty("classification").GetString() != expectedClassification)
            throw new InvalidDataException("Sustained Duplex evidence failed persisted readback envelope validation.");
        var requested = report.GetProperty("requested");
        var sourceReport = expectedReport ?? report;
        var source = new SustainedDuplexEvidence(operationId!, requested.GetProperty("period").GetString()!, requested.GetProperty("mmcss").GetString()!, requested.GetProperty("durationSeconds").GetInt32(), root.GetProperty("lifecycle").GetString()!, root.GetProperty("classification").GetString()!, report.GetProperty("capture").GetProperty("cleanup").GetString()!, "SAVED_AND_RELOADED", sourceReport, report.GetProperty("capture").GetProperty("actualBufferFrames").GetInt32(), report.GetProperty("render").GetProperty("actualBufferFrames").GetInt32(), Path.GetFullPath(path));
        SustainedDuplexEvidenceResolver.Resolve([source], requested.GetProperty("captureEndpointId").GetString()!, requested.GetProperty("renderEndpointId").GetString()!, Path.GetDirectoryName(Path.GetFullPath(path)));
    }
}
