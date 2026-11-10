using System.Security.Cryptography;
using System.Text.Json;
using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

/// <summary>Executes one caller-selected immutable V2 route. Format query is passive; Initialize and audio operations use the same V2 authorization.</summary>
public sealed class WorkerV2AutomaticScanWorkflow(NativeWorkerClient worker)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(8);

    // shortcut: one caller-selected route per consent; add bounded alternate-route iteration only with explicit per-route authorization budgeting.

    public async Task<V2AutomaticScanResult> RunAsync(string captureEndpointId, string renderEndpointId,
        WorkerScanV2Route route, bool operatorAccepted, Guid confirmationId, string? evidenceRoot = null,
        CancellationToken cancellationToken = default, Func<string, int, string?>? testScenarioForOperation = null,
        string period = "MINIMUM", Action<V2AutomaticScanProgress>? progress = null, string qaMode = "DISCOVERY")
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!operatorAccepted || confirmationId == Guid.Empty) throw new ArgumentException("Explicit scan acceptance is required.");
        if (period is not ("MINIMUM" or "DEFAULT")) throw new ArgumentOutOfRangeException(nameof(period));
        if (qaMode is not ("DISCOVERY" or "SHORT" or "AUTO_SHORT" or "FULL")) throw new ArgumentOutOfRangeException(nameof(qaMode));
        using var operationLease = worker.AcquireSharedOperation("V2 Automatic Scan");
        var discovery = await worker.ListWindowsEndpointsAsync(cancellationToken).ConfigureAwait(false);
        var capture = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == captureEndpointId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE")
            ?? throw new InvalidOperationException("The exact active Capture endpoint is missing from current discovery.");
        var render = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == renderEndpointId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE")
            ?? throw new InvalidOperationException("The exact active Render endpoint is missing from current discovery.");
        var routeFingerprint = WorkerScanPlanV2Fingerprint.Route(captureEndpointId, renderEndpointId, route);
        var operations = new List<V2AutomaticScanOperation>();
        var stages = Enum.GetValues<AutomaticScanStage>().Select(s => new AutomaticScanStageResult(s, AutomaticScanStageState.NOT_RUN, "NOT_REQUESTED")).ToArray();
        void Stage(AutomaticScanStage stage, AutomaticScanStageState state, string message)
        {
            stages = stages.Select(s => s.Stage == stage ? s with { State = state, Message = message } : s).ToArray();
            progress?.Invoke(new(stage.ToString(), state.ToString(), message));
        }
        IReadOnlyDictionary<string, string> StageStatuses() => stages.ToDictionary(s => s.Stage.ToString(), s => s.Message == "V2_QUALIFICATION_NOT_REQUESTED" ? "NOT_REQUESTED" : s.State switch
        {
            AutomaticScanStageState.COMPLETED => "COMPLETED", AutomaticScanStageState.FAILED => "FAILED",
            AutomaticScanStageState.UNKNOWN => "UNKNOWN", AutomaticScanStageState.SKIPPED => "SKIPPED", AutomaticScanStageState.NOT_RUN => "NOT_RUN", _ => "NOT_RUN"
        }, StringComparer.Ordinal);

        var captureCounts = QueryChannelCounts(capture.SharedModeFormat.ReportedChannelCount);
        var renderCounts = QueryChannelCounts(render.SharedModeFormat.ReportedChannelCount);
        var captureQuery = await worker.QueryFormatMatrixAsync(captureEndpointId, "CAPTURE", captureCounts, cancellationToken).ConfigureAwait(false);
        var renderQuery = await worker.QueryFormatMatrixAsync(renderEndpointId, "RENDER", renderCounts, cancellationToken).ConfigureAwait(false);
        Stage(AutomaticScanStage.DISCOVERY, AutomaticScanStageState.COMPLETED, "V2_DISCOVERY_VERIFIED");
        Stage(AutomaticScanStage.FORMAT_QUERY, AutomaticScanStageState.COMPLETED, "V2_FORMAT_QUERY_COMPLETED");

        bool QuerySupports(string endpointId, WorkerScanV2Side side, string period, RealFormatMatrixDto matrix) =>
            matrix.EndpointId == endpointId && matrix.Candidates.Any(c => c.SampleRate == side.SampleRateHz && c.Channels == side.Channels && c.Encoding == side.Encoding && c.Period == period.ToLowerInvariant() && c.Status == "SUPPORTED_REPORTED" && c.SupportStatus == "SUPPORTED_REPORTED");
        if (qaMode == "AUTO_SHORT")
        {
            Stage(AutomaticScanStage.DISCOVERY, AutomaticScanStageState.COMPLETED, "V2_DISCOVERY_VERIFIED");
            Stage(AutomaticScanStage.FORMAT_QUERY, AutomaticScanStageState.COMPLETED, "V2_DEFAULT_MINIMUM_QUERY_COMPLETED");
            var candidatePeriods = new[] { "DEFAULT", "MINIMUM" };
            RealFormatCandidateDto? FindCandidate(RealFormatMatrixDto matrix, WorkerScanV2Side side, string p) => matrix.Candidates.FirstOrDefault(c => c.SampleRate == side.SampleRateHz && c.Channels == side.Channels && c.Encoding == side.Encoding && c.Period == p.ToLowerInvariant());
            long? QueryPeriod(RealFormatCandidateDto? c, string p) => p == "DEFAULT" ? c?.DefaultPeriod100ns : c?.MinimumPeriod100ns;
            int? PeriodFrames(long? hns, WorkerScanV2Side side) => hns is > 0 ? checked((int)(hns.Value * side.SampleRateHz / 10_000_000L)) : null;
            var inventory = candidatePeriods.Select(p =>
            {
                var cap = FindCandidate(captureQuery, route.Capture, p); var ren = FindCandidate(renderQuery, route.Render, p);
                var captureSupport = QuerySupports(captureEndpointId, route.Capture, p, captureQuery) ? "SUPPORTED_REPORTED" : QueryState(route.Capture, captureQuery);
                var renderSupport = QuerySupports(renderEndpointId, route.Render, p, renderQuery) ? "SUPPORTED_REPORTED" : QueryState(route.Render, renderQuery);
                var supportedBoth = captureSupport == "SUPPORTED_REPORTED" && renderSupport == "SUPPORTED_REPORTED";
                var capPeriod = QueryPeriod(cap, p); var renPeriod = QueryPeriod(ren, p);
                return new V2AutoShortCandidate(p, capPeriod, PeriodFrames(capPeriod, route.Capture), captureSupport, renderSupport,
                    supportedBoth ? "QUERY_SUPPORTED" : "UNSUPPORTED", supportedBoth ? "BOTH_ENDPOINTS_CONFIRM_SUPPORT" : $"CAPTURE={captureSupport};RENDER={renderSupport}")
                    { RenderPeriod100ns = renPeriod, RenderBufferFrames = PeriodFrames(renPeriod, route.Render), CaptureSupportSource = captureQuery.Provenance, RenderSupportSource = renderQuery.Provenance };
            }).ToArray();
            var supported = inventory.Where(c => c.Status == "QUERY_SUPPORTED").ToArray();
            if (supported.Length == 0)
            {
                var noCandidates = V2AutoShortEvaluator.Evaluate(inventory);
                Stage(AutomaticScanStage.INITIALIZE, AutomaticScanStageState.SKIPPED, "NO_SUPPORTED_PERIOD_CANDIDATES");
                Stage(AutomaticScanStage.SHORT_DUPLEX, AutomaticScanStageState.SKIPPED, "NO_SUPPORTED_PERIOD_CANDIDATES");
                Stage(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
                Stage(AutomaticScanStage.SUSTAINED_VALIDATION, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
                Stage(AutomaticScanStage.FINAL_SELECTION, AutomaticScanStageState.SKIPPED, "NO_SUPPORTED_PERIOD_CANDIDATES");
                var emptyResult = new V2AutomaticScanResult(Guid.NewGuid().ToString("D"), "COMPLETED", captureEndpointId, renderEndpointId, routeFingerprint, route, "", [], stages, null, null)
                    { CaptureFriendlyName = capture.FriendlyNameDisplay, RenderFriendlyName = render.FriendlyNameDisplay, QaMode = qaMode, AutoShortCandidates = noCandidates.Candidates, Qualification = noCandidates.Qualification, QaStageStatuses = StageStatuses(), RepeatabilityStatus = "NOT_REQUESTED", SustainedStatus = "NOT_REQUESTED" };
                try { Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.COMPLETED, "V2_EVIDENCE_SAVED_READBACK"); emptyResult = emptyResult with { Stages = stages, QaStageStatuses = StageStatuses() }; var path = V2AutomaticScanEvidenceStorage.Save(emptyResult, captureQuery, renderQuery, null, null, evidenceRoot); return emptyResult with { EvidencePath = path, Stages = stages, QaStageStatuses = StageStatuses() }; }
                catch (Exception ex) { Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.FAILED, "V2_EVIDENCE_SAVE_FAILED"); return emptyResult with { Lifecycle = "INCOMPLETE", Stages = stages, QaStageStatuses = StageStatuses(), Error = ex.Message }; }
            }
            var autoPeriods = supported.Select(c => c.Period).ToArray();
            var autoPlan = new WorkerScanPlanV2(route, autoPeriods, ["OFF"], 180, 5, 6, 10, "AUTO_SHORT");
            WorkerScanSessionToken? autoSession = null; var autoOperations = new List<V2AutomaticScanOperation>();
            string autoLifecycle = "INCOMPLETE", autoError = "", autoCleanupOutcome = "NOT_ATTEMPTED", autoCancellationOutcome = "NOT_ATTEMPTED", activeAutoOperation = "", activeAutoStage = "INITIALIZE", activeAutoPeriod = "";
            V2WorkerErrorDiagnostics? autoWorkerError = null; var assessed = new List<V2AutoShortCandidate>(inventory);
            try
            {
                autoSession = await worker.RegisterScanSessionV2Async(captureEndpointId, renderEndpointId, autoPlan, true, confirmationId, cancellationToken).ConfigureAwait(false);
                foreach (var candidate in supported)
                {
                    if (autoSession.RouteFingerprint != routeFingerprint ||
                        WorkerScanPlanV2Fingerprint.Route(captureEndpointId, renderEndpointId, route) != routeFingerprint)
                        throw new IOException("AUTO_SHORT_ENDPOINT_OR_ROUTE_CHANGED");
                    foreach (var (endpointId, direction, side) in new[] { (captureEndpointId, "CAPTURE", route.Capture), (renderEndpointId, "RENDER", route.Render) })
                    {
                        var id = Guid.NewGuid();
                        activeAutoStage = "INITIALIZE"; activeAutoPeriod = candidate.Period;
                        activeAutoOperation = id.ToString("D");
                        var init = await worker.ValidateFormatMatrixInScanAsync(autoSession, id, endpointId, direction, side.SampleRateHz, side.Channels, side.Encoding, candidate.Period, cancellationToken).ConfigureAwait(false);
                        var item = init.Candidates.Single();
                        var ready = item.InitializeAttempted && item.InitializeStatus is "INITIALIZED" or "READY" && item.CleanupStatus == "RELEASE_ATTEMPTED";
                        autoOperations.Add(new("INITIALIZE", id.ToString("D"), endpointId, direction, ready ? "COMPLETED" : item.CleanupStatus == "RELEASE_ATTEMPTED" ? "FAILED" : "UNKNOWN", candidate.Period, null, item.CleanupStatus, init.Provenance, JsonSerializer.SerializeToElement(init)));
                        activeAutoOperation = "";
                        if (!ready) throw new IOException(item.CleanupStatus == "RELEASE_ATTEMPTED" ? "AUTO_SHORT_INITIALIZE_FAILED" : "AUTO_SHORT_INITIALIZE_UNKNOWN");
                    }
                    Stage(AutomaticScanStage.INITIALIZE, AutomaticScanStageState.COMPLETED, $"INITIALIZE_READY_{candidate.Period}");
                    assessed.RemoveAll(c => c.Period == candidate.Period); assessed.Add(candidate with { Status = "INITIALIZE_READY", Reason = "BOTH_INITIALIZE_AND_CLEANUP_CONFIRMED" });
                    var operationId = Guid.NewGuid();
                    activeAutoStage = "SHORT_DUPLEX"; activeAutoPeriod = candidate.Period;
                    activeAutoOperation = operationId.ToString("D");
                    var scenario = testScenarioForOperation?.Invoke("SHORT_DUPLEX", autoOperations.Count(o => o.Stage == "SHORT_DUPLEX") + 1);
                    var duplex = await worker.ValidateDuplexV2InScanAsync(operationId.ToString("D"), autoSession, operationId, route, captureEndpointId, renderEndpointId, candidate.Period, 5, "OFF", false, cancellationToken, scenario).ConfigureAwait(false);
                    var report = duplex.Report;
                    var periodInfo = FindCandidate(captureQuery, route.Capture, candidate.Period);
                    var buffer = report.GetProperty("capture").GetProperty("actualBufferFrames").GetInt32();
                    var classified = V2AutoShortEvaluator.ClassifyReport(candidate.Period, candidate.Period == "DEFAULT" ? periodInfo?.DefaultPeriod100ns : periodInfo?.MinimumPeriod100ns, buffer, operationId.ToString("D"), captureEndpointId, renderEndpointId, route, routeFingerprint, report)
                        with { RenderBufferFrames = report.GetProperty("render").GetProperty("actualBufferFrames").GetInt32(), RenderPeriod100ns = candidate.RenderPeriod100ns, CaptureSupportSource = candidate.CaptureSupportSource, RenderSupportSource = candidate.RenderSupportSource };
                    autoOperations.Add(new("SHORT_DUPLEX", operationId.ToString("D"), null, null, classified.Status == "SHORT_CLEAN" ? "COMPLETED" : classified.Status == "SHORT_FAILED" ? "FAILED" : "UNKNOWN", candidate.Period, 5, "REPORT_CLEANUP", report.GetProperty("provenance").GetString(), report.Clone()));
                    activeAutoOperation = "";
                    assessed.RemoveAll(c => c.Period == candidate.Period); assessed.Add(classified);
                    Stage(AutomaticScanStage.SHORT_DUPLEX, classified.Status switch { "SHORT_CLEAN" => AutomaticScanStageState.COMPLETED, "SHORT_FAILED" => AutomaticScanStageState.FAILED, _ => AutomaticScanStageState.UNKNOWN }, $"{candidate.Period}_{classified.Status}");
                    if (classified.Status == "UNKNOWN") { autoLifecycle = "UNKNOWN"; autoError = "AUTO_SHORT_OUTCOME_UNKNOWN"; break; }
                }
                if (autoLifecycle != "UNKNOWN") autoLifecycle = "AUTO_SHORT_COMPLETED";
            }
            catch (OperationCanceledException ex)
            {
                if (activeAutoOperation.Length > 0)
                {
                    autoOperations.Add(new(activeAutoStage, activeAutoOperation, null, null, "UNKNOWN", activeAutoPeriod, activeAutoStage == "SHORT_DUPLEX" ? 5 : null, "UNKNOWN", null, null));
                    if (activeAutoStage == "SHORT_DUPLEX")
                    {
                        using var cancel = new CancellationTokenSource(CleanupTimeout);
                        try { await worker.CancelDuplexValidationAsync(activeAutoOperation, cancel.Token).ConfigureAwait(false); autoCancellationOutcome = "CANCEL_REQUEST_ACCEPTED;FINAL_REPORT_UNCONFIRMED"; }
                        catch { autoCancellationOutcome = "UNKNOWN"; operationLease.Keep(); }
                    }
                    else { autoCancellationOutcome = "OPERATION_OUTCOME_UNKNOWN"; operationLease.Keep(); }
                }
                else autoCancellationOutcome = "CANCELLED_BEFORE_OPERATION";
                autoLifecycle = "UNKNOWN"; autoError = "AUTO_SHORT_CANCELLED_OUTCOME_UNKNOWN";
                autoWorkerError = new(null, ex.Message, ex.GetType().Name, activeAutoStage, autoSession?.ScanSessionId.ToString("D"), activeAutoOperation, "CANCELLED", worker.State.State.ToString(), autoCancellationOutcome, "PENDING");
            }
            catch (Exception ex) { if (activeAutoOperation.Length > 0) autoOperations.Add(new(activeAutoStage, activeAutoOperation, null, null, "UNKNOWN", activeAutoPeriod, activeAutoStage == "SHORT_DUPLEX" ? 5 : null, "UNKNOWN", null, null)); autoLifecycle = autoOperations.Any(o => o.State == "UNKNOWN") || worker.State.State == WorkerConnectionState.Disconnected ? "UNKNOWN" : "INCOMPLETE"; autoError = ex.Message; autoWorkerError = new((ex as WorkerCommandException)?.ErrorCode, ex.Message, ex.GetType().Name, activeAutoStage, autoSession?.ScanSessionId.ToString("D"), activeAutoOperation, ex is WorkerCommandException ? "RESPONSE_ERROR" : ex is TimeoutException ? "RESPONSE_TIMEOUT" : "UNKNOWN", worker.State.State.ToString(), "NOT_ATTEMPTED", "PENDING"); }
            finally
            {
                if (autoSession is { } token)
                {
                    using var close = new CancellationTokenSource(CleanupTimeout);
                    try { if (autoLifecycle == "UNKNOWN") { await worker.CancelScanSessionAsync(token, close.Token).ConfigureAwait(false); autoCleanupOutcome = "CANCELLED"; } else { await worker.CloseScanSessionAsync(token, close.Token).ConfigureAwait(false); autoCleanupOutcome = "CLOSED"; } }
                    catch (Exception ex) { autoLifecycle = "UNKNOWN"; autoError = "AUTO_SHORT_SESSION_CLEANUP_UNKNOWN"; autoCleanupOutcome = "UNKNOWN"; if (autoWorkerError is not null) autoWorkerError = autoWorkerError with { SessionCleanupOutcome = "UNKNOWN", ErrorMessage = $"{autoWorkerError.ErrorMessage}; {ex.GetType().Name}: {ex.Message}" }; operationLease.Keep(); }
                }
            }
            var assessment = V2AutoShortEvaluator.Evaluate(assessed);
            Stage(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
            Stage(AutomaticScanStage.SUSTAINED_VALIDATION, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
            Stage(AutomaticScanStage.FINAL_SELECTION, assessment.RecommendedPeriod is not null ? AutomaticScanStageState.COMPLETED : autoLifecycle == "UNKNOWN" ? AutomaticScanStageState.UNKNOWN : AutomaticScanStageState.FAILED,
                assessment.RecommendedPeriod is not null ? $"PRELIMINARY_RECOMMENDATION_{assessment.RecommendedPeriod}_NOT_CERTIFIED" : assessment.Outcome);
            Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.COMPLETED, "V2_EVIDENCE_SAVED_READBACK");
            var autoResult = new V2AutomaticScanResult(autoSession?.ScanSessionId.ToString("D") ?? Guid.NewGuid().ToString("D"), autoLifecycle, captureEndpointId, renderEndpointId, routeFingerprint, route, "", autoOperations, stages, null, string.IsNullOrEmpty(autoError) ? null : autoError)
                { CaptureFriendlyName = capture.FriendlyNameDisplay, RenderFriendlyName = render.FriendlyNameDisplay, QaMode = qaMode, QaStageStatuses = StageStatuses(), AutoShortCandidates = assessment.Candidates, RecommendedPeriod = assessment.RecommendedPeriod, Qualification = assessment.RecommendedPeriod is null ? assessment.Outcome : "SHORT_CLEAN", SourceSha = V2AutomaticScanEvidenceStorage.SourceSha, SessionCleanupOutcome = autoCleanupOutcome, WorkerError = autoWorkerError, RepeatabilityStatus = "NOT_REQUESTED", SustainedStatus = "NOT_REQUESTED" };
            try { var path = V2AutomaticScanEvidenceStorage.Save(autoResult, captureQuery, renderQuery, autoOperations.FirstOrDefault(o => o.Direction == "CAPTURE")?.Evidence?.Deserialize<RealFormatMatrixDto>(), autoOperations.FirstOrDefault(o => o.Direction == "RENDER")?.Evidence?.Deserialize<RealFormatMatrixDto>(), evidenceRoot); return autoResult with { EvidencePath = path }; }
            catch (Exception ex) { Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.FAILED, "V2_EVIDENCE_SAVE_FAILED"); return autoResult with { Lifecycle = autoResult.Lifecycle == "UNKNOWN" ? "UNKNOWN" : "INCOMPLETE", RecommendedPeriod = null, Qualification = "UNKNOWN", Stages = stages, QaStageStatuses = StageStatuses(), Error = $"EVIDENCE_SAVE_FAILED: {ex.Message}" }; }
        }
        var periodConfirmed = QuerySupports(captureEndpointId, route.Capture, period, captureQuery) && QuerySupports(renderEndpointId, route.Render, period, renderQuery);
        if (!periodConfirmed)
        {
            var captureState = QueryState(route.Capture, captureQuery);
            var renderState = QueryState(route.Render, renderQuery);
            var reason = $"ROUTE_NOT_CONFIRMED_BY_QUERY; PERIOD={period}_NOT_SUPPORTED_ON_BOTH_SIDES; CAPTURE={captureState}; RENDER={renderState}";
            Stage(AutomaticScanStage.FORMAT_QUERY, AutomaticScanStageState.SKIPPED, "V2_FORMAT_NOT_CONFIRMED");
            var skipped = new V2AutomaticScanResult(Guid.NewGuid().ToString("D"), "INCOMPLETE", captureEndpointId, renderEndpointId, routeFingerprint, route, "", operations, stages, null, reason)
                { CaptureFriendlyName = capture.FriendlyNameDisplay, RenderFriendlyName = render.FriendlyNameDisplay, QaMode = qaMode, QaStageStatuses = StageStatuses(), RepeatabilityStatus = "NOT_REQUESTED", SustainedStatus = "NOT_REQUESTED" };
            try
            {
                Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.COMPLETED, "V2_EVIDENCE_SAVED_READBACK");
                skipped = skipped with { Stages = stages };
                var path = V2AutomaticScanEvidenceStorage.Save(skipped, captureQuery, renderQuery, null, null, evidenceRoot);
                return skipped with { EvidencePath = path, Stages = stages, QaStageStatuses = StageStatuses() };
            }
            catch (Exception ex) { Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.FAILED, "V2_EVIDENCE_SAVE_FAILED"); return skipped with { Stages = stages, QaStageStatuses = StageStatuses(), Error = $"{reason}; SAVE_FAILED: {ex.Message}" }; }
        }
        Stage(AutomaticScanStage.FORMAT_QUERY, AutomaticScanStageState.COMPLETED, qaMode == "DISCOVERY" ? "V2_DISCOVERY_COMPLETE" : "V2_ROUTE_PERIOD_CONFIRMED");

        if (qaMode == "DISCOVERY")
        {
            Stage(AutomaticScanStage.INITIALIZE, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
            Stage(AutomaticScanStage.SHORT_DUPLEX, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
            Stage(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
            Stage(AutomaticScanStage.SUSTAINED_VALIDATION, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
            Stage(AutomaticScanStage.FINAL_SELECTION, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
            var discoveryResult = new V2AutomaticScanResult(Guid.NewGuid().ToString("D"), "COMPLETED", captureEndpointId, renderEndpointId, routeFingerprint, route, period, operations, stages, null, null)
            { CaptureFriendlyName = capture.FriendlyNameDisplay, RenderFriendlyName = render.FriendlyNameDisplay, QaMode = "DISCOVERY", QaStageStatuses = StageStatuses(), RepeatabilityStatus = "NOT_REQUESTED", SustainedStatus = "NOT_REQUESTED" };
            Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.COMPLETED, "V2_EVIDENCE_SAVED_READBACK");
            discoveryResult = discoveryResult with { Stages = stages };
            try { var path = V2AutomaticScanEvidenceStorage.Save(discoveryResult, captureQuery, renderQuery, null, null, evidenceRoot); return discoveryResult with { EvidencePath = path, Stages = stages, QaStageStatuses = StageStatuses() }; }
            catch (Exception ex) { Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.FAILED, "V2_EVIDENCE_SAVE_FAILED"); return discoveryResult with { Lifecycle = "INCOMPLETE", Stages = stages, QaStageStatuses = StageStatuses(), Error = ex.Message }; }
        }

        var plan = qaMode == "SHORT" ? new WorkerScanPlanV2(route, [period], ["OFF"], 120, 5, 3, 5, "SHORT") : new WorkerScanPlanV2(route, [period], ["OFF"], 1200, 600, 12, 995, "FULL");
        WorkerScanSessionToken? session = null;
        var registrationAttempted = false;
        string? activeOperation = null;
        var sessionId = Guid.NewGuid().ToString("D");
        var lifecycle = "INCOMPLETE";
        string? error = null;
        V2WorkerErrorDiagnostics? workerError = null;
        string? cleanupOutcome = null;
        var currentStage = "SCAN_SESSION";
        var duplexSequence = 0;
        try
        {
            registrationAttempted = true;
            session = await worker.RegisterScanSessionV2Async(captureEndpointId, renderEndpointId, plan, true, confirmationId, cancellationToken).ConfigureAwait(false);
            sessionId = session.ScanSessionId.ToString("D");

            async Task<RealFormatMatrixDto> Initialize(string endpointId, string direction, WorkerScanV2Side side)
            {
                currentStage = "INITIALIZE";
                var id = Guid.NewGuid(); activeOperation = id.ToString("D");
                progress?.Invoke(new("INITIALIZE", "RUNNING", direction, activeOperation));
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
                    workerError = CreateWorkerError(ex, ex.GetType().Name, "INITIALIZE", sessionId, activeOperation,
                        ex is WorkerCommandException ? "RESPONSE_ERROR" : ex is TimeoutException ? "RESPONSE_TIMEOUT" : worker.State.State == WorkerConnectionState.ConnectedFake ? "UNKNOWN" : "TRANSPORT_LOST", "NOT_ATTEMPTED", cleanupOutcome);
                    throw new IOException($"Initialize result is unresolved: {ex.GetType().Name}.", ex);
                }
                finally { activeOperation = null; }
            }
            await Initialize(captureEndpointId, "CAPTURE", route.Capture).ConfigureAwait(false);
            await Initialize(renderEndpointId, "RENDER", route.Render).ConfigureAwait(false);
            Stage(AutomaticScanStage.INITIALIZE, AutomaticScanStageState.COMPLETED, "V2_INITIALIZE_CLEAN");

            async Task<DuplexValidationDto> Duplex(int duration, bool sustained, string stage)
            {
                currentStage = stage;
                var id = Guid.NewGuid(); activeOperation = id.ToString("D");
                progress?.Invoke(new(stage, "RUNNING", duration.ToString(System.Globalization.CultureInfo.InvariantCulture), activeOperation, duration));
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
                    var cancellationOutcome = "UNKNOWN";
                    try { await worker.CancelDuplexValidationAsync(activeOperation, cancelTimeout.Token).ConfigureAwait(false); cancellationOutcome = "CONFIRMED"; }
                    catch { operationLease.Keep(); }
                    workerError = CreateWorkerError(null, "OperationCanceledException", stage, sessionId, activeOperation, "CANCELLED", cancellationOutcome, cleanupOutcome);
                    throw;
                }
                catch (Exception ex)
                {
                    operations.Add(new(stage, activeOperation, null, null, "UNKNOWN", period, duration, "UNKNOWN", null, null));
                    workerError = CreateWorkerError(ex, ex.GetType().Name, stage, sessionId, activeOperation,
                        ex is WorkerCommandException ? "RESPONSE_ERROR" : ex is TimeoutException ? "RESPONSE_TIMEOUT" : worker.State.State == WorkerConnectionState.ConnectedFake ? "UNKNOWN" : "TRANSPORT_LOST", "NOT_CANCELLED", cleanupOutcome);
                    throw new IOException($"{stage} result is unresolved: {ex.GetType().Name}.", ex);
                }
                finally { activeOperation = null; }
            }

            var shortResult = await Duplex(5, false, "SHORT_DUPLEX").ConfigureAwait(false);
            var shortClean = IsClean(shortResult.Report);
            Stage(AutomaticScanStage.SHORT_DUPLEX, shortClean ? AutomaticScanStageState.COMPLETED : AutomaticScanStageState.FAILED, shortClean ? "V2_SHORT_DUPLEX_CLEAN" : "V2_SHORT_DUPLEX_NOT_QUALIFIED");
            if (!shortClean) throw new InvalidDataException("SHORT_DUPLEX_NOT_CLEAN");
            if (qaMode == "SHORT") Stage(AutomaticScanStage.SHORT_DUPLEX, AutomaticScanStageState.COMPLETED, "V2_SHORT_QA_COMPLETE");

            var repeatClean = true;
            if (qaMode == "SHORT")
            {
                Stage(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
                Stage(AutomaticScanStage.SUSTAINED_VALIDATION, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
                Stage(AutomaticScanStage.FINAL_SELECTION, AutomaticScanStageState.SKIPPED, "V2_QUALIFICATION_NOT_REQUESTED");
                lifecycle = "SHORT_COMPLETED";
            }
            else
            {
                for (var i = 0; i < 6; i++)
                {
                    var repeat = await Duplex(5, false, "REPEATABILITY").ConfigureAwait(false);
                    if (!IsClean(repeat.Report)) { repeatClean = false; break; }
                }
                Stage(AutomaticScanStage.REPEATABILITY, repeatClean ? AutomaticScanStageState.COMPLETED : AutomaticScanStageState.FAILED, repeatClean ? "V2_REPEATABILITY_CLEAN" : "V2_REPEATABILITY_FAILED");
                if (!repeatClean) throw new InvalidDataException("REPEATABILITY_NOT_CLEAN");

                var sustainedClean = true;
                foreach (var duration in new[] { 60, 300, 600 })
                {
                    var sustained = await Duplex(duration, true, "SUSTAINED_VALIDATION").ConfigureAwait(false);
                    if (!IsClean(sustained.Report)) { sustainedClean = false; break; }
                }
                Stage(AutomaticScanStage.SUSTAINED_VALIDATION, sustainedClean ? AutomaticScanStageState.COMPLETED : AutomaticScanStageState.FAILED, sustainedClean ? "V2_SUSTAINED_CLEAN" : "V2_SUSTAINED_NOT_QUALIFIED");
                if (!sustainedClean) throw new InvalidDataException("SUSTAINED_NOT_CLEAN");
                Stage(AutomaticScanStage.FINAL_SELECTION, AutomaticScanStageState.COMPLETED, "V2_FINAL_SELECTION_COMPLETE");
                lifecycle = "COMPLETED";
            }
        }
        catch (OperationCanceledException) { lifecycle = "UNKNOWN"; error = "CANCELLED_OUTCOME_REQUIRES_WORKER_CLEANUP"; }
        catch (Exception ex)
        {
            lifecycle = operations.Any(o => o.State == "UNKNOWN") || session is null && registrationAttempted && ex is not WorkerCommandException ? "UNKNOWN" : "INCOMPLETE";
            error = $"{ex.GetType().Name}: {ex.Message}";
            workerError ??= CreateWorkerError(ex, ex.GetType().Name, currentStage, session?.ScanSessionId.ToString("D"), activeOperation,
                ex is WorkerCommandException ? "RESPONSE_ERROR" : ex is TimeoutException ? "RESPONSE_TIMEOUT" : worker.State.State == WorkerConnectionState.ConnectedFake ? "UNKNOWN" : "TRANSPORT_LOST", "NOT_ATTEMPTED", cleanupOutcome);
        }
        finally
        {
            if (session is { } token)
            {
                using var cleanup = new CancellationTokenSource(CleanupTimeout);
                try
                {
                    if (lifecycle == "UNKNOWN") { await worker.CancelScanSessionAsync(token, cleanup.Token).ConfigureAwait(false); cleanupOutcome = "CANCELLED"; }
                    else { await worker.CloseScanSessionAsync(token, cleanup.Token).ConfigureAwait(false); cleanupOutcome = "CLOSED"; }
                }
                catch (Exception ex) { lifecycle = "UNKNOWN"; cleanupOutcome = "UNKNOWN"; operationLease.Keep(); error = "SESSION_CLEANUP_UNKNOWN"; if (workerError is not null) workerError = workerError with { SessionCleanupOutcome = "UNKNOWN", ErrorMessage = $"{workerError.ErrorMessage}; session cleanup {ex.GetType().Name}: {ex.Message}" }; }
            }
            else if (registrationAttempted && lifecycle == "UNKNOWN") operationLease.Keep();
            if (workerError is not null) workerError = workerError with { SessionCleanupOutcome = cleanupOutcome ?? "NOT_ATTEMPTED", WorkerState = worker.State.State.ToString() };
        }

        var resultValue = new V2AutomaticScanResult(sessionId, lifecycle, captureEndpointId, renderEndpointId, routeFingerprint, route, period, operations, stages, null, error)
            { CaptureFriendlyName = capture.FriendlyNameDisplay, RenderFriendlyName = render.FriendlyNameDisplay, WorkerError = workerError, QaMode = qaMode, QaStageStatuses = StageStatuses(),
                RepeatabilityStatus = qaMode == "SHORT" ? "NOT_REQUESTED" : lifecycle == "COMPLETED" ? "COMPLETED" : operations.Any(o => o.Stage == "REPEATABILITY" && o.State == "UNKNOWN") ? "UNKNOWN" : operations.Any(o => o.Stage == "REPEATABILITY" && o.State == "FAILED") ? "FAILED" : "NOT_RUN",
                SustainedStatus = qaMode == "SHORT" ? "NOT_REQUESTED" : lifecycle == "COMPLETED" ? "COMPLETED" : operations.Any(o => o.Stage == "SUSTAINED_VALIDATION" && o.State == "UNKNOWN") ? "UNKNOWN" : operations.Any(o => o.Stage == "SUSTAINED_VALIDATION" && o.State == "FAILED") ? "FAILED" : "NOT_RUN" };
        if (operations.Any(o => o.Stage == "INITIALIZE" && o.State == "FAILED")) Stage(AutomaticScanStage.INITIALIZE, AutomaticScanStageState.FAILED, "V2_INITIALIZE_FAILED");
        if (operations.Any(o => o.State == "UNKNOWN") || lifecycle == "UNKNOWN")
        {
            var failedStage = operations.LastOrDefault(o => o.State == "UNKNOWN")?.Stage;
            var stage = failedStage switch { "INITIALIZE" => AutomaticScanStage.INITIALIZE, "SHORT_DUPLEX" => AutomaticScanStage.SHORT_DUPLEX, "REPEATABILITY" => AutomaticScanStage.REPEATABILITY, "SUSTAINED_VALIDATION" => AutomaticScanStage.SUSTAINED_VALIDATION, _ => AutomaticScanStage.SAVE };
            if (stage != AutomaticScanStage.SAVE) Stage(stage, AutomaticScanStageState.UNKNOWN, "V2_OPERATION_UNKNOWN");
        }
        Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.COMPLETED, "V2_EVIDENCE_SAVED_READBACK");
        resultValue = resultValue with { Stages = stages };
        resultValue = resultValue with { QaStageStatuses = StageStatuses() };
        if (session is not null || registrationAttempted)
        {
            try
            {
                var path = V2AutomaticScanEvidenceStorage.Save(resultValue, captureQuery, renderQuery, captureInitOrNull(), renderInitOrNull(), evidenceRoot);
                resultValue = resultValue with { EvidencePath = path, Stages = stages };
            }
            catch (Exception ex) { Stage(AutomaticScanStage.SAVE, AutomaticScanStageState.FAILED, "V2_EVIDENCE_SAVE_FAILED"); resultValue = resultValue with { Lifecycle = resultValue.Lifecycle == "UNKNOWN" ? "UNKNOWN" : "INCOMPLETE", Stages = stages, Error = ex.Message }; }
        }
        return resultValue;

        RealFormatMatrixDto? captureInitOrNull() => operations.FirstOrDefault(o => o.Stage == "INITIALIZE" && o.Direction == "CAPTURE")?.Evidence is { } j ? j.Deserialize<RealFormatMatrixDto>() : null;
        RealFormatMatrixDto? renderInitOrNull() => operations.FirstOrDefault(o => o.Stage == "INITIALIZE" && o.Direction == "RENDER")?.Evidence is { } j ? j.Deserialize<RealFormatMatrixDto>() : null;

        V2WorkerErrorDiagnostics CreateWorkerError(Exception? exception, string exceptionType, string failedStage, string? failedSession, string? failedOperation,
            string ipcState, string cancellationOutcome, string? cleanup) => new(
                (exception as WorkerCommandException)?.ErrorCode,
                exception?.Message,
                exceptionType,
                failedStage,
                failedSession,
                failedOperation,
                ipcState,
                worker.State.State.ToString(),
                cancellationOutcome,
                cleanup ?? "PENDING");
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
    public static string SourceSha
    {
        get
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            var match = System.Text.RegularExpressions.Regex.Match(version ?? "", "[0-9a-fA-F]{40}");
            return match.Success ? match.Value.ToLowerInvariant() : "UNKNOWN";
        }
    }
    public static string Save(V2AutomaticScanResult result, RealFormatMatrixDto captureQuery, RealFormatMatrixDto renderQuery,
        RealFormatMatrixDto? captureInitialize, RealFormatMatrixDto? renderInitialize, string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Guid.TryParseExact(result.SessionId, "D", out var sessionId) || sessionId == Guid.Empty) throw new InvalidDataException("V2 scan session identity is invalid.");
        rootDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "Scans", "V2");
        Directory.CreateDirectory(rootDirectory);
        var path = Path.Combine(rootDirectory, $"scan-{sessionId:D}.json");
        if (File.Exists(path)) throw new IOException("V2 Automatic Scan evidence already exists; refusing to overwrite it.");
        var persistedResult = result with { EvidencePath = path };
        var envelope = new { schemaVersion = 1, result = persistedResult, captureQuery, renderQuery, captureInitialize, renderInitialize, physicalMapping = "UNKNOWN", physicalRtl = "NOT_MEASURED", audioHost = "NOT_CERTIFIED" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        var temporary = Path.Combine(rootDirectory, $".{sessionId:D}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path);
            var persisted = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(persisted);
            if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1 || document.RootElement.GetProperty("result").GetProperty("routeFingerprint").GetString() != result.RouteFingerprint || document.RootElement.GetProperty("result").GetProperty("sessionId").GetString() != result.SessionId || document.RootElement.GetProperty("result").GetProperty("evidencePath").GetString() != path || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(persisted))) throw new InvalidDataException("V2 Automatic Scan evidence did not survive exact read-back verification.");
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
        return path;
    }
}
