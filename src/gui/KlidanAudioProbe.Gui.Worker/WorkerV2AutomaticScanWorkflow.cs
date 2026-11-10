using System.Security.Cryptography;
using System.Text.Json;
using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

public sealed record V2AutomaticScanOperation(string Stage, string OperationId, string? EndpointId, string? Direction,
    string State, string? Period, int? DurationSeconds, string Cleanup, string? Provenance, JsonElement? Evidence);
public sealed record V2AutomaticScanResult(string SessionId, string Lifecycle, string CaptureEndpointId, string RenderEndpointId,
    string RouteFingerprint, WorkerScanV2Route Route, string Period, IReadOnlyList<V2AutomaticScanOperation> Operations,
    IReadOnlyList<AutomaticScanStageResult> Stages, string? EvidencePath, string? Error);

/// <summary>Executes one caller-selected immutable V2 route. Format query is passive; Initialize and audio operations use the same V2 authorization.</summary>
public sealed class WorkerV2AutomaticScanWorkflow(NativeWorkerClient worker)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(8);\r\n\r\n    // shortcut: one caller-selected route per consent; add bounded alternate-route iteration only with explicit per-route authorization budgeting.

    public async Task<V2AutomaticScanResult> RunAsync(string captureEndpointId, string renderEndpointId,
        WorkerScanV2Route route, bool operatorAccepted, Guid confirmationId, string? evidenceRoot = null,
        CancellationToken cancellationToken = default, Func<string, int, string?>? testScenarioForOperation = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!operatorAccepted || confirmationId == Guid.Empty) throw new ArgumentException("Explicit scan acceptance is required.");
        using var operationLease = worker.AcquireSharedOperation("V2 Automatic Scan");
        var discovery = await worker.ListWindowsEndpointsAsync(cancellationToken).ConfigureAwait(false);
        var capture = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == captureEndpointId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE")
            ?? throw new InvalidOperationException("The exact active Capture endpoint is missing from current discovery.");
        var render = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == renderEndpointId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE")
            ?? throw new InvalidOperationException("The exact active Render endpoint is missing from current discovery.");
        var routeFingerprint = WorkerScanPlanV2Fingerprint.Route(captureEndpointId, renderEndpointId, route);
        var operations = new List<V2AutomaticScanOperation>();
        var stages = Enum.GetValues<AutomaticScanStage>().Select(s => new AutomaticScanStageResult(s, AutomaticScanStageState.NOT_RUN, "Not reached.")).ToArray();
        void Stage(AutomaticScanStage stage, AutomaticScanStageState state, string message) => stages = stages.Select(s => s.Stage == stage ? s with { State = state, Message = message } : s).ToArray();

        var captureCounts = QueryChannelCounts(capture.SharedModeFormat.ReportedChannelCount);
        var renderCounts = QueryChannelCounts(render.SharedModeFormat.ReportedChannelCount);
        var captureQuery = await worker.QueryFormatMatrixAsync(captureEndpointId, "CAPTURE", captureCounts, cancellationToken).ConfigureAwait(false);
        var renderQuery = await worker.QueryFormatMatrixAsync(renderEndpointId, "RENDER", renderCounts, cancellationToken).ConfigureAwait(false);
        Stage(AutomaticScanStage.DISCOVERY, AutomaticScanStageState.COMPLETED, "Exact active endpoints and current discovery generation verified.");
        Stage(AutomaticScanStage.FORMAT_QUERY, AutomaticScanStageState.COMPLETED, "Bounded passive query completed for common and endpoint-reported channel counts.");

        bool QuerySupports(string endpointId, WorkerScanV2Side side, string period, RealFormatMatrixDto matrix) =>
            matrix.EndpointId == endpointId && matrix.Candidates.Any(c => c.SampleRate == side.SampleRateHz && c.Channels == side.Channels && c.Encoding == side.Encoding && c.Period == period.ToLowerInvariant() && c.Status == "SUPPORTED_REPORTED" && c.SupportStatus == "SUPPORTED_REPORTED");
        var period = QuerySupports(captureEndpointId, route.Capture, "DEFAULT", captureQuery) && QuerySupports(renderEndpointId, route.Render, "DEFAULT", renderQuery)
            ? "DEFAULT"
            : QuerySupports(captureEndpointId, route.Capture, "MINIMUM", captureQuery) && QuerySupports(renderEndpointId, route.Render, "MINIMUM", renderQuery) ? "MINIMUM" : null;
        if (period is null)
        {
            var captureState = QueryState(route.Capture, captureQuery);
            var renderState = QueryState(route.Render, renderQuery);
            var reason = $"ROUTE_NOT_CONFIRMED_BY_QUERY; CAPTURE={captureState}; RENDER={renderState}";
            Stage(AutomaticScanStage.FORMAT_QUERY, AutomaticScanStageState.SKIPPED, reason);
            var skipped = new V2AutomaticScanResult(Guid.NewGuid().ToString("D"), "INCOMPLETE", captureEndpointId, renderEndpointId, routeFingerprint, route, "", operations, stages, null, reason);
            try
            {
                Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.COMPLETED, "Query evidence and skip reason saved and read back.");
                skipped = skipped with { Stages = stages };
                var path = V2AutomaticScanEvidenceStorage.Save(skipped, captureQuery, renderQuery, null, null, evidenceRoot);
                return skipped with { EvidencePath = path, Stages = stages };
            }
            catch (Exception ex) { Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.FAILED, ex.Message); return skipped with { Stages = stages, Error = $"{reason}; SAVE_FAILED: {ex.Message}" }; }
        }
        Stage(AutomaticScanStage.FORMAT_QUERY, AutomaticScanStageState.COMPLETED, $"Exact route confirmed for {period}; physical mapping remains UNKNOWN.");

        var plan = new WorkerScanPlanV2(route, [period], ["OFF"], 1800, 600, 12, 1030);
        WorkerScanSessionToken? session = null;
        var registrationAttempted = false;
        string? activeOperation = null;
        var sessionId = Guid.NewGuid().ToString("D");
        var lifecycle = "INCOMPLETE";
        string? error = null;
        var duplexSequence = 0;
        try
        {
            registrationAttempted = true;
            session = await worker.RegisterScanSessionV2Async(captureEndpointId, renderEndpointId, plan, true, confirmationId, cancellationToken).ConfigureAwait(false);
            sessionId = session.ScanSessionId.ToString("D");

            async Task<RealFormatMatrixDto> Initialize(string endpointId, string direction, WorkerScanV2Side side)
            {
                var id = Guid.NewGuid(); activeOperation = id.ToString("D");
                try
                {
                    var result = await worker.ValidateFormatMatrixInScanAsync(session, id, endpointId, direction, side.SampleRateHz, side.Channels, side.Encoding, period, cancellationToken).ConfigureAwait(false);
                    var candidate = result.Candidates.Single();
                    var initialized = candidate.InitializeAttempted && candidate.InitializeStatus is "INITIALIZED" or "READY";
                    var cleanupClean = candidate.CleanupStatus == "RELEASE_ATTEMPTED";
                    var clean = initialized && cleanupClean;
                    operations.Add(new("INITIALIZE", activeOperation, endpointId, direction, clean ? "COMPLETED" : !cleanupClean ? "UNKNOWN" : "FAILED", period, null, candidate.CleanupStatus, result.Provenance, JsonSerializer.SerializeToElement(result)));
                    if (!clean) throw new InvalidDataException($"Initialize did not confirm the exact {direction} stream; state={candidate.InitializeStatus}, cleanup={candidate.CleanupStatus}.");
                    return result;
                }
                catch (Exception ex) when (!operations.Any(o => o.OperationId == activeOperation))
                {
                    operations.Add(new("INITIALIZE", activeOperation, endpointId, direction, "UNKNOWN", period, null, "UNKNOWN", null, null));
                    throw new IOException($"Initialize result is unresolved: {ex.GetType().Name}.", ex);
                }
                finally { activeOperation = null; }
            }
            await Initialize(captureEndpointId, "CAPTURE", route.Capture).ConfigureAwait(false);
            await Initialize(renderEndpointId, "RENDER", route.Render).ConfigureAwait(false);
            Stage(AutomaticScanStage.INITIALIZE, AutomaticScanStageState.COMPLETED, "Both exact stream formats initialized and cleaned up.");

            async Task<DuplexValidationDto> Duplex(int duration, bool sustained, string stage)
            {
                var id = Guid.NewGuid(); activeOperation = id.ToString("D");
                try
                {
                    var scenario = testScenarioForOperation?.Invoke(stage, ++duplexSequence);
                    var result = await worker.ValidateDuplexV2InScanAsync(activeOperation, session, id, route, captureEndpointId, renderEndpointId, period, duration, "OFF", sustained, cancellationToken, scenario).ConfigureAwait(false);
                    var report = result.Report;
                    var quality = report.GetProperty("duplex").GetProperty("status").GetString();
                    var cleanup = new[] { report.GetProperty("capture").GetProperty("cleanup").GetString(), report.GetProperty("render").GetProperty("cleanup").GetString() };
                    var cleanupClean = cleanup.All(c => c == "RELEASE_ATTEMPTED");
                    var clean = quality == "DUPLEX_CLEAN" && cleanupClean;
                    operations.Add(new(stage, activeOperation, null, null, clean ? "COMPLETED" : quality == "UNKNOWN" || !cleanupClean ? "UNKNOWN" : "FAILED", period, duration, string.Join("/", cleanup), report.GetProperty("provenance").GetString(), report.Clone()));
                    return result;
                }
                catch (OperationCanceledException)
                {
                    operations.Add(new(stage, activeOperation, null, null, "UNKNOWN", period, duration, "UNKNOWN", null, null));
                    using var cancelTimeout = new CancellationTokenSource(CleanupTimeout);
                    try { await worker.CancelDuplexValidationAsync(activeOperation, cancelTimeout.Token).ConfigureAwait(false); }
                    catch { operationLease.Keep(); }
                    throw;
                }
                catch (Exception ex)
                {
                    operations.Add(new(stage, activeOperation, null, null, "UNKNOWN", period, duration, "UNKNOWN", null, null));
                    throw new IOException($"{stage} result is unresolved: {ex.GetType().Name}.", ex);
                }
                finally { activeOperation = null; }
            }

            var shortResult = await Duplex(5, false, "SHORT_DUPLEX").ConfigureAwait(false);
            var shortClean = IsClean(shortResult.Report);
            Stage(AutomaticScanStage.SHORT_DUPLEX, shortClean ? AutomaticScanStageState.COMPLETED : AutomaticScanStageState.FAILED, shortClean ? "Five-second exact-route Duplex was CLEAN." : "Short Duplex did not qualify; later audio operations stopped.");
            if (!shortClean) throw new InvalidDataException("SHORT_DUPLEX_NOT_CLEAN");

            var repeatClean = true;
            for (var i = 0; i < 6; i++)
            {
                var repeat = await Duplex(5, false, "REPEATABILITY").ConfigureAwait(false);
                if (!IsClean(repeat.Report)) { repeatClean = false; break; }
            }
            Stage(AutomaticScanStage.REPEATABILITY, repeatClean ? AutomaticScanStageState.COMPLETED : AutomaticScanStageState.FAILED, repeatClean ? "Six sequential exact-route Duplex operations were CLEAN." : "Repeatability failed; Sustained was not started.");
            if (!repeatClean) throw new InvalidDataException("REPEATABILITY_NOT_CLEAN");

            var sustainedClean = true;
            foreach (var duration in new[] { 60, 300, 600 })
            {
                var sustained = await Duplex(duration, true, "SUSTAINED_VALIDATION").ConfigureAwait(false);
                if (!IsClean(sustained.Report)) { sustainedClean = false; break; }
            }
            Stage(AutomaticScanStage.SUSTAINED_VALIDATION, sustainedClean ? AutomaticScanStageState.COMPLETED : AutomaticScanStageState.FAILED, sustainedClean ? "60/300/600-second ladder was CLEAN in order." : "Sustained ladder stopped on non-CLEAN evidence.");
            if (!sustainedClean) throw new InvalidDataException("SUSTAINED_NOT_CLEAN");
            Stage(AutomaticScanStage.FINAL_SELECTION, AutomaticScanStageState.COMPLETED, "Selected this single fully qualified route; stability evidence does not certify physical RTL or AudioHost.");
            lifecycle = "COMPLETED";
        }
        catch (OperationCanceledException) { lifecycle = "UNKNOWN"; error = "CANCELLED_OUTCOME_REQUIRES_WORKER_CLEANUP"; }
        catch (Exception ex)
        {
            lifecycle = operations.Any(o => o.State == "UNKNOWN") || session is null && registrationAttempted && ex is not WorkerCommandException ? "UNKNOWN" : "INCOMPLETE";
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (session is { } token)
            {
                using var cleanup = new CancellationTokenSource(CleanupTimeout);
                try
                {
                    if (lifecycle == "UNKNOWN") await worker.CancelScanSessionAsync(token, cleanup.Token).ConfigureAwait(false);
                    else await worker.CloseScanSessionAsync(token, cleanup.Token).ConfigureAwait(false);
                }
                catch { lifecycle = "UNKNOWN"; operationLease.Keep(); error = "SESSION_CLEANUP_UNKNOWN"; }
            }
            else if (registrationAttempted && lifecycle == "UNKNOWN") operationLease.Keep();
        }

        var resultValue = new V2AutomaticScanResult(sessionId, lifecycle, captureEndpointId, renderEndpointId, routeFingerprint, route, period, operations, stages, null, error);
        if (operations.Any(o => o.Stage == "INITIALIZE" && o.State == "FAILED")) Stage(AutomaticScanStage.INITIALIZE, AutomaticScanStageState.FAILED, "Initialize failed; later audio operations were stopped.");
        if (operations.Any(o => o.State == "UNKNOWN") || lifecycle == "UNKNOWN")
        {
            var failedStage = operations.LastOrDefault(o => o.State == "UNKNOWN")?.Stage;
            var stage = failedStage switch { "INITIALIZE" => AutomaticScanStage.INITIALIZE, "SHORT_DUPLEX" => AutomaticScanStage.SHORT_DUPLEX, "REPEATABILITY" => AutomaticScanStage.REPEATABILITY, "SUSTAINED_VALIDATION" => AutomaticScanStage.SUSTAINED_VALIDATION, _ => AutomaticScanStage.SAVE };
            if (stage != AutomaticScanStage.SAVE) Stage(stage, AutomaticScanStageState.UNKNOWN, "Operation result or cleanup is unresolved; all subsequent audio operations were stopped.");
        }
        Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.COMPLETED, "V2 evidence saved and read back.");
        resultValue = resultValue with { Stages = stages };
        if (session is not null || registrationAttempted)
        {
            try
            {
                var path = V2AutomaticScanEvidenceStorage.Save(resultValue, captureQuery, renderQuery, captureInitOrNull(), renderInitOrNull(), evidenceRoot);
                resultValue = resultValue with { EvidencePath = path, Stages = stages };
            }
            catch (Exception ex) { Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.FAILED, ex.Message); resultValue = resultValue with { Lifecycle = "INCOMPLETE", Stages = stages, Error = ex.Message }; }
        }
        return resultValue;

        RealFormatMatrixDto? captureInitOrNull() => operations.FirstOrDefault(o => o.Stage == "INITIALIZE" && o.Direction == "CAPTURE")?.Evidence is { } j ? j.Deserialize<RealFormatMatrixDto>() : null;
        RealFormatMatrixDto? renderInitOrNull() => operations.FirstOrDefault(o => o.Stage == "INITIALIZE" && o.Direction == "RENDER")?.Evidence is { } j ? j.Deserialize<RealFormatMatrixDto>() : null;
    }

    public static IReadOnlyList<int> QueryChannelCounts(int? endpointChannels) =>
        new[] { 1, 2, 8, 16, 32, endpointChannels.GetValueOrDefault() }.Where(c => c is >= 1 and <= 32).Distinct().ToArray();

    private static bool IsClean(JsonElement report) => report.ValueKind == JsonValueKind.Object &&
        report.GetProperty("duplex").GetProperty("status").GetString() == "DUPLEX_CLEAN" &&
        new[] { report.GetProperty("capture").GetProperty("cleanup").GetString(), report.GetProperty("render").GetProperty("cleanup").GetString() }.All(c => c == "RELEASE_ATTEMPTED");

    private static string QueryState(WorkerScanV2Side side, RealFormatMatrixDto matrix)
    {
        var candidates = matrix.Candidates.Where(c => c.SampleRate == side.SampleRateHz && c.Channels == side.Channels && c.Encoding == side.Encoding).ToArray();
        if (candidates.Length == 0) return "NOT_TESTED";
        if (candidates.Any(c => c.SupportStatus is "UNSUPPORTED" or "UNSUPPORTED_BY_DEVICE" || c.Status is "UNSUPPORTED" or "UNSUPPORTED_BY_DEVICE")) return "UNSUPPORTED_BY_DEVICE";
        if (candidates.Any(c => c.SupportStatus == "UNSUPPORTED_BY_PROBE" || c.Status == "UNSUPPORTED_BY_PROBE")) return "UNSUPPORTED_BY_PROBE";
        if (candidates.Any(c => c.SupportStatus == "NOT_TESTED" || c.Status == "NOT_TESTED")) return "NOT_TESTED";
        return "UNKNOWN";
    }
}

public static class V2AutomaticScanEvidenceStorage
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Save(V2AutomaticScanResult result, RealFormatMatrixDto captureQuery, RealFormatMatrixDto renderQuery,
        RealFormatMatrixDto? captureInitialize, RealFormatMatrixDto? renderInitialize, string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Guid.TryParseExact(result.SessionId, "D", out var sessionId) || sessionId == Guid.Empty) throw new InvalidDataException("V2 scan session identity is invalid.");
        rootDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "Scans", "V2");
        Directory.CreateDirectory(rootDirectory);
        var path = Path.Combine(rootDirectory, $"scan-{sessionId:D}.json");
        var envelope = new { schemaVersion = 1, result, captureQuery, renderQuery, captureInitialize, renderInitialize, physicalMapping = "UNKNOWN", physicalRtl = "NOT_MEASURED", audioHost = "NOT_CERTIFIED" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        var temporary = Path.Combine(rootDirectory, $".{sessionId:D}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path);
            var persisted = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(persisted);
            if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1 || document.RootElement.GetProperty("result").GetProperty("routeFingerprint").GetString() != result.RouteFingerprint || document.RootElement.GetProperty("result").GetProperty("sessionId").GetString() != result.SessionId || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(persisted))) throw new InvalidDataException("V2 Automatic Scan evidence did not survive exact read-back verification.");
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
        return path;
    }
}
