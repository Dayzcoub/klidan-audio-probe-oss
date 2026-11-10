using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public enum ScanStage { Discovery, Query, Initialize, Stream, Duplex, PeriodDiscovery, ShortCustomDuplex, Repeatability, SustainedValidation, Evaluate, Save, Completed }
public enum ScanOperationLifecycle { Pending, Running, Completed, Skipped, Failed, Unknown }
public enum ScanRunLifecycle { Completed, Incomplete, Cancelled, Failed, Unknown }
public enum ScanPairIdentity { Unresolved, Ambiguous, OperatorSelectedPair }
public enum ScanCapturePath { Unknown, Analogue, DriverLoopback }
public enum ScanExecutionMode { DeterministicFake, RealWorker }

public sealed record ScanTarget(
    WindowsEndpointDto? Capture,
    WindowsEndpointDto? Render,
    ScanPairIdentity PairIdentity = ScanPairIdentity.Unresolved,
    ScanCapturePath CapturePath = ScanCapturePath.Unknown)
{
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{Capture?.EndpointId ?? "-"}\n{Render?.EndpointId ?? "-"}\n{PairIdentity}\n{CapturePath}")));
}

public sealed record ScanCapabilities(bool Discovery, bool Query, bool Initialize, bool Stream, bool Duplex);
public sealed record ScanSettings(int DurationSeconds = 5, int MaximumInitializeOperations = 6, int MaximumStreamOperations = 4, int MaximumDuplexOperations = 2, int OperationTimeoutSeconds = 60, int CleanupTimeoutSeconds = 5, string MmcssMode = "OFF");

public sealed record ScanPlan(ScanTarget Target, ScanCapabilities Capabilities, ScanSettings Settings, string Fingerprint)
{
    public static ScanPlan Build(ScanTarget target, ScanCapabilities capabilities, ScanSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capabilities);
        settings ??= new();
        ValidateTarget(target);
        if (settings.DurationSeconds is < 1 or > 10 || settings.MaximumInitializeOperations is < 1 or > 8 || settings.MaximumStreamOperations is < 0 or > 8 || settings.MaximumDuplexOperations is < 0 or > 5 || settings.OperationTimeoutSeconds is < 1 or > 120 || settings.CleanupTimeoutSeconds is < 1 or > 15 || settings.MmcssMode is not ("OFF" or "PRO_AUDIO"))
            throw new ArgumentOutOfRangeException(nameof(settings), "Scan bounds are outside the supported limits.");
        var ids = $"{target.Capture?.EndpointId ?? "-"}\n{target.Render?.EndpointId ?? "-"}\n{target.PairIdentity}\n{target.CapturePath}\n{capabilities}\n{settings}";
        return new(target, capabilities, settings, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ids))));
    }

    private static void ValidateTarget(ScanTarget target)
    {
        if (target.Capture is null && target.Render is null) throw new InvalidOperationException("Select an exact Capture or Render endpoint.");
        ValidateEndpoint(target.Capture, "CAPTURE");
        ValidateEndpoint(target.Render, "RENDER");
        if (target.Capture is not null && target.Render is not null && target.Capture.EndpointId == target.Render.EndpointId)
            throw new InvalidOperationException("Capture and Render endpoint IDs must be distinct.");
        if ((target.Capture is null || target.Render is null) && target.PairIdentity == ScanPairIdentity.OperatorSelectedPair)
            throw new InvalidOperationException("An explicit pair requires both endpoint directions.");
    }

    private static void ValidateEndpoint(WindowsEndpointDto? endpoint, string direction)
    {
        if (endpoint is null) return;
        if (string.IsNullOrWhiteSpace(endpoint.EndpointId) || endpoint.DeviceState != "ACTIVE" || endpoint.Direction != direction)
            throw new InvalidOperationException($"Scan target must contain the exact active {direction} endpoint.");
    }
}

public sealed record ScanSessionAuthorization(Guid ConfirmationId, bool Accepted, string PlanFingerprint,
    IReadOnlySet<ScanStage> AllowedStages, int MaximumDurationSeconds, int MaximumOperationCount,
    bool AllowsPhysicalRtl = false, bool AllowsAudioHostActivation = false)
{
    public static string Fingerprint(ScanPlan plan, IEnumerable<ScanStage> allowedStages, int maximumDurationSeconds,
        int maximumOperationCount, bool allowsPhysicalRtl = false, bool allowsAudioHostActivation = false)
    {
        var scope = string.Join(',', allowedStages.OrderBy(stage => stage));
        var value = $"{plan.Fingerprint}\n{scope}\n{maximumDurationSeconds}\n{maximumOperationCount}\n{allowsPhysicalRtl}\n{allowsAudioHostActivation}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed record ScanOperation(ScanStage Stage, string? EndpointId = null, string? Direction = null,
    string? Encoding = null, int? SampleRateHz = null, int? Channels = null, string? Period = null,
    int DurationSeconds = 0, string OperationId = "", string MmcssMode = "OFF",
    string? CaptureEndpointId = null, string? RenderEndpointId = null);

public sealed record ScanEvidence(RealFormatMatrixDto? FormatMatrix = null,
    RealStreamValidationDto? Stream = null, DuplexValidationDto? Duplex = null);

public sealed record ScanAdapterResult(ScanOperationLifecycle Lifecycle, ScanEvidence? Evidence = null,
    string Message = "", string CleanupStatus = "NOT_APPLICABLE", bool IdentityCurrent = true,
    bool DevicePresent = true, bool WorkerConnected = true, bool CanSafelyContinue = true);

public sealed record ScanOperationResult(ScanOperation Operation, ScanOperationLifecycle Lifecycle,
    string Message, string CleanupStatus, ScanEvidence? Evidence);

public sealed record AutomaticScanChildOperation(string Stage, string? OperationId, string? ReportId,
    string Lifecycle, string Provenance, string CleanupStatus, string Message, string? ReportPath,
    string? Period, string? EndpointId, string? Direction);

public sealed record ScanProgressEvent(long Sequence, DateTimeOffset TimestampUtc, ScanStage Stage,
    ScanOperationLifecycle Lifecycle, string Action, string? EndpointId, string? OperationId);

public sealed record ScanRunResult(string SessionId, ScanRunLifecycle Lifecycle, ScanTarget Target,
    IReadOnlyList<ScanOperationResult> Operations, IReadOnlyList<ScanProgressEvent> Events,
    IReadOnlyList<DuplexCandidateAssessment> DuplexAssessments, DuplexRecommendation Recommendation,
    string Summary, bool PhysicalRtlMeasured, string AudioHostEligibility, ScanPersistenceResult? Persistence = null,
    PeriodDiscoveryTerminationDiagnostics? PeriodDiscoveryDiagnostics = null)
{
    public IReadOnlyList<ScanEvidence> Evidence => Operations.Where(o => o.Evidence is not null).Select(o => o.Evidence!).ToArray();
    public IReadOnlyList<AutomaticScanCandidate> CandidateInventory { get; init; } = [];
    public IReadOnlyList<AutomaticScanStageResult> StageResults { get; init; } = [];
    public IReadOnlyList<AutomaticScanChildOperation> ChildOperations { get; init; } = [];
}

public interface IScanExecutionAdapter
{
    ScanExecutionMode Mode { get; }
    ScanCapabilities Capabilities { get; }
    Task<ScanAdapterResult> ExecuteAsync(ScanOperation operation, ScanPlan plan, ScanSessionAuthorization authorization, CancellationToken cancellationToken);
    Task<ScanAdapterResult> CancelAsync(ScanOperation operation, ScanPlan plan, CancellationToken cancellationToken);
}

public interface IWorkerAuthorizedScanAdapter : IScanExecutionAdapter
{
    bool IsBoundTo(ScanPlan plan, ScanSessionAuthorization authorization);
    Task FinishSessionAsync(bool cancel, CancellationToken cancellationToken);
}

public sealed record AutomaticScanSelection(string? EndpointId, bool ExplicitPair, string? CaptureEndpointId, string? RenderEndpointId,
    string? PhysicalGroupId = null, bool RequireUnambiguousPair = false, bool AllowUnconfirmedPair = false, bool ExtendedSustainedValidation = false);
public sealed record AutomaticScanPreparation(WindowsEndpointDiscoveryDto Discovery, ScanPlan Plan, WorkerScanPlan WorkerPlan, int MaximumActiveSeconds,
    bool PairPhysicalIdentityUnconfirmed = false)
{
    public AutomaticScanPlan? StagedPlan { get; init; }
    public int MaximumTotalOperations { get; init; } = 24;
    public int MaximumWallClockSeconds { get; init; } = 30;
    public int SustainedDurationSeconds { get; init; } = 300;
}
public sealed record AutomaticScanProgressRow(string OperationId, string Stage, string State, string Action);

public sealed record V2AutomaticScanOperation(string Stage, string OperationId, string? EndpointId, string? Direction,
    string State, string? Period, int? DurationSeconds, string Cleanup, string? Provenance, System.Text.Json.JsonElement? Evidence);
public sealed record V2WorkerErrorDiagnostics(string? ErrorCode, string? ErrorMessage, string ExceptionType, string Stage,
    string? SessionId, string? OperationId, string IpcState, string WorkerState, string CancellationOutcome, string SessionCleanupOutcome);
public sealed record V2AutomaticScanProgress(string Stage, string State, string Message, string? OperationId = null, int? DurationSeconds = null);
public sealed record V2AutomaticScanResult(string SessionId, string Lifecycle, string CaptureEndpointId, string RenderEndpointId,
    string RouteFingerprint, WorkerScanV2Route Route, string Period, IReadOnlyList<V2AutomaticScanOperation> Operations,
    IReadOnlyList<AutomaticScanStageResult> Stages, string? EvidencePath, string? Error)
{
    public string CaptureFriendlyName { get; init; } = string.Empty;
    public string RenderFriendlyName { get; init; } = string.Empty;
    public V2WorkerErrorDiagnostics? WorkerError { get; init; }
}

public interface IAutomaticScanWorkflow
{
    Task<WindowsEndpointDiscoveryDto> RefreshEndpointsAsync(CancellationToken cancellationToken);
    AutomaticScanPreparation Prepare(WindowsEndpointDiscoveryDto discovery, AutomaticScanSelection selection);
    Task<ScanRunResult> RunApprovedAsync(AutomaticScanPreparation preparation, Guid confirmationId,
        Action<ScanProgressEvent>? progress, CancellationToken cancellationToken);
    Task<V2AutomaticScanResult> RunV2Async(string captureEndpointId, string renderEndpointId, WorkerScanV2Route route,
        string period, Guid confirmationId, Action<V2AutomaticScanProgress>? progress, CancellationToken cancellationToken) =>
        Task.FromException<V2AutomaticScanResult>(new NotSupportedException("V2 Automatic Scan is unavailable in this workflow."));
}

public sealed class ScanCoordinator(IScanExecutionAdapter adapter)
{
    private const int MaximumOperations = 24;
    private int _running;
    private int _workerMayBeActive;

    public async Task<ScanRunResult> RunAsync(ScanPlan plan, ScanSessionAuthorization authorization,
        Action<ScanProgressEvent>? progress = null, CancellationToken cancellationToken = default,
        bool persist = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(authorization);
        if (adapter.Mode == ScanExecutionMode.RealWorker && (adapter is not IWorkerAuthorizedScanAdapter realAdapter || !realAdapter.IsBoundTo(plan, authorization)))
            throw new InvalidOperationException("A real scan requires an already-approved Worker session bound to this exact plan and discovery snapshot.");
        if (Volatile.Read(ref _workerMayBeActive) != 0) throw new InvalidOperationException("The previous native operation may still be active; this coordinator cannot safely start another scan.");
        var allowedStages = authorization.AllowedStages.ToHashSet();
        if (!authorization.Accepted || authorization.ConfirmationId == Guid.Empty || authorization.PlanFingerprint != ScanSessionAuthorization.Fingerprint(plan, allowedStages, authorization.MaximumDurationSeconds, authorization.MaximumOperationCount, authorization.AllowsPhysicalRtl, authorization.AllowsAudioHostActivation) || authorization.MaximumDurationSeconds < plan.Settings.DurationSeconds || authorization.MaximumDurationSeconds > 30 || authorization.MaximumOperationCount is < 1 or > MaximumOperations || authorization.AllowsPhysicalRtl || authorization.AllowsAudioHostActivation)
            throw new InvalidOperationException("Scan session authorization is missing, stale, over-broad, or outside its safety bounds.");

        var operations = new List<ScanOperationResult>();
        var events = new List<ScanProgressEvent>();
        var queries = new Dictionary<string, RealFormatMatrixDto>(StringComparer.Ordinal);
        var initialized = new List<(string EndpointId, string Direction, int Channels, string Period)>();
        var duplexReports = new List<DuplexValidationDto>();
        long sequence = 0;
        var lifecycle = ScanRunLifecycle.Completed;
        var sessionId = Guid.NewGuid().ToString("D");

        var progressFailed = false;
        void Emit(ScanOperation op, ScanOperationLifecycle state, string message)
        {
            var item = new ScanProgressEvent(++sequence, DateTimeOffset.UtcNow, op.Stage, state, message, op.EndpointId, op.OperationId);
            events.Add(item);
            if (!progressFailed && progress is not null) try { progress(item); } catch { progressFailed = true; }
        }

        async Task<ScanAdapterResult?> Execute(ScanOperation op)
        {
            Emit(op, ScanOperationLifecycle.Pending, $"Запланировано: {op.Stage}.");
            if (operations.Count >= Math.Min(MaximumOperations, authorization.MaximumOperationCount))
            {
                operations.Add(new(op, ScanOperationLifecycle.Failed, "Scan session operation limit reached; remaining stages were not started.", "NOT_ATTEMPTED", null));
                Emit(op, ScanOperationLifecycle.Failed, operations[^1].Message);
                lifecycle = ScanRunLifecycle.Incomplete;
                return new(ScanOperationLifecycle.Failed, Message: operations[^1].Message, CleanupStatus: "NOT_ATTEMPTED", CanSafelyContinue: false);
            }
            Emit(op, ScanOperationLifecycle.Running, op.Stage.ToString());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(plan.Settings.OperationTimeoutSeconds));
            Task<ScanAdapterResult>? execution = null;
            try
            {
                execution = adapter.ExecuteAsync(op, plan, authorization, timeout.Token);
                var result = await execution.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (!result.IdentityCurrent || !result.DevicePresent || !result.WorkerConnected)
                {
                    var state = !result.WorkerConnected || !result.IdentityCurrent || !result.DevicePresent || CleanupHasUnknown(result.CleanupStatus) ? ScanOperationLifecycle.Unknown : ScanOperationLifecycle.Failed;
                    Interlocked.Exchange(ref _workerMayBeActive, 1);
                    operations.Add(new(op, state, result.Message, result.CleanupStatus, result.Evidence));
                    Emit(op, state, result.Message);
                    lifecycle = state == ScanOperationLifecycle.Unknown ? ScanRunLifecycle.Unknown : ScanRunLifecycle.Incomplete;
                    return result with { CanSafelyContinue = false };
                }
                if (op.Stage == ScanStage.Discovery && result.Lifecycle != ScanOperationLifecycle.Completed)
                {
                    operations.Add(new(op, result.Lifecycle, result.Message, result.CleanupStatus, result.Evidence));
                    Emit(op, result.Lifecycle, result.Message);
                    lifecycle = result.Lifecycle == ScanOperationLifecycle.Unknown || CleanupHasUnknown(result.CleanupStatus) ? ScanRunLifecycle.Unknown : ScanRunLifecycle.Incomplete;
                    return result with { CanSafelyContinue = false };
                }
                if (result.Lifecycle == ScanOperationLifecycle.Completed && op.Stage is (ScanStage.Query or ScanStage.Initialize or ScanStage.Stream or ScanStage.Duplex) && result.Evidence is null)
                    throw new InvalidDataException("Completed hardware-shaped operation returned no evidence.");
                ValidateEvidence(op, result.Evidence, plan.Target, adapter.Mode);
                var cleanupUnsafe = CleanupIsUnsafe(result.CleanupStatus);
                var cleanupUnknown = CleanupHasUnknown(result.CleanupStatus);
                var operationState = cleanupUnsafe
                    ? cleanupUnknown ? ScanOperationLifecycle.Unknown : ScanOperationLifecycle.Failed
                    : result.Lifecycle;
                operations.Add(new(op, operationState, result.Message, result.CleanupStatus, result.Evidence));
                Emit(op, operationState, result.Message);
                if (operationState is ScanOperationLifecycle.Failed or ScanOperationLifecycle.Unknown)
                    lifecycle = operationState == ScanOperationLifecycle.Unknown || cleanupUnknown ? ScanRunLifecycle.Unknown : ScanRunLifecycle.Incomplete;
                if (!result.CanSafelyContinue || cleanupUnsafe)
                {
                    Interlocked.Exchange(ref _workerMayBeActive, 1);
                    if (!cleanupUnsafe) lifecycle = ScanRunLifecycle.Incomplete;
                    return result with { CanSafelyContinue = false };
                }
                if (result.Evidence?.FormatMatrix is { } matrix && op.Stage == ScanStage.Query) queries[op.EndpointId!] = matrix;
                if (result.Evidence?.FormatMatrix is { } initializedMatrix && op.Stage == ScanStage.Initialize && IsInitializeSuccessful(initializedMatrix))
                    initialized.Add((op.EndpointId!, op.Direction!, op.Channels!.Value, op.Period!));
                if (result.Evidence?.Duplex is { } duplex) duplexReports.Add(duplex);
                return result;
            }
            catch (OperationCanceledException)
            {
                var userCancelled = cancellationToken.IsCancellationRequested;
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(plan.Settings.CleanupTimeoutSeconds));
                try
                {
                    var stopped = await adapter.CancelAsync(op, plan, cleanupTimeout.Token).WaitAsync(cleanupTimeout.Token).ConfigureAwait(false);
                    var settled = execution is { IsCompleted: true };
                    ScanAdapterResult? lateResult = null;
                    if (execution is { IsCompleted: false })
                    {
                        try { lateResult = await execution.WaitAsync(cleanupTimeout.Token).ConfigureAwait(false); settled = true; }
                        catch { settled = execution.IsCompleted; }
                    }
                    else if (settled) lateResult = await execution!.ConfigureAwait(false);
                    var evidence = lateResult?.Evidence ?? stopped.Evidence;
                    var cleanup = stopped.CleanupStatus;
                    var safe = settled && stopped.CanSafelyContinue && !CleanupIsUnsafe(cleanup) && lateResult is not { CanSafelyContinue: false } && lateResult is not { IdentityCurrent: false } && lateResult is not { DevicePresent: false } && lateResult is not { WorkerConnected: false };
                    if (!settled) Interlocked.Exchange(ref _workerMayBeActive, 1);
                    var state = safe ? ScanOperationLifecycle.Skipped : ScanOperationLifecycle.Unknown;
                    if (!safe) Interlocked.Exchange(ref _workerMayBeActive, 1);
                    operations.Add(new(op, state, !settled ? "Operation did not stop within the bounded cleanup deadline; worker state may still be active." : userCancelled ? "Cancelled after bounded stop; operation evidence retained." : "Operation deadline expired; operation evidence retained.", cleanup, evidence));
                    Emit(op, state, operations[^1].Message);
                    lifecycle = state == ScanOperationLifecycle.Unknown ? ScanRunLifecycle.Unknown : userCancelled ? ScanRunLifecycle.Cancelled : ScanRunLifecycle.Incomplete;
                }
                catch
                {
                    Interlocked.Exchange(ref _workerMayBeActive, 1);
                    operations.Add(new(op, ScanOperationLifecycle.Unknown, "Cancellation/timeout cleanup was not confirmed.", "UNKNOWN", null));
                    Emit(op, ScanOperationLifecycle.Unknown, operations[^1].Message);
                    lifecycle = ScanRunLifecycle.Unknown;
                }
                    return new(ScanOperationLifecycle.Unknown, Message: operations[^1].Message, CleanupStatus: operations[^1].CleanupStatus, CanSafelyContinue: false);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _workerMayBeActive, 1);
                operations.Add(new(op, ScanOperationLifecycle.Unknown, $"{ex.GetType().Name}: {ex.Message}", "UNKNOWN", null));
                Emit(op, ScanOperationLifecycle.Unknown, operations[^1].Message);
                lifecycle = ScanRunLifecycle.Unknown;
                return new(ScanOperationLifecycle.Unknown, Message: operations[^1].Message, CleanupStatus: "UNKNOWN", CanSafelyContinue: false);
            }
        }

        async Task RunIfAllowed(ScanOperation op)
        {
            if (!allowedStages.Contains(op.Stage))
            {
                operations.Add(new(op, ScanOperationLifecycle.Skipped, "Stage was not authorized for this scan session.", "NOT_ATTEMPTED", null));
                Emit(op, ScanOperationLifecycle.Skipped, operations[^1].Message);
                return;
            }
            var outcome = await Execute(op).ConfigureAwait(false);
            if (outcome is { CanSafelyContinue: false }) throw new StopScanException();
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("A scan is already active on this coordinator.");
        try
        {
            if (!plan.Capabilities.Discovery || !adapter.Capabilities.Discovery)
            {
                RecordSkipped(ScanStage.Discovery, null, "Fresh discovery capability is unavailable; scan stopped before endpoint operations.");
                lifecycle = ScanRunLifecycle.Incomplete;
                throw new StopScanException();
            }
            await RunIfAllowed(New(ScanStage.Discovery)).ConfigureAwait(false);
            if (lifecycle is ScanRunLifecycle.Unknown or ScanRunLifecycle.Cancelled) throw new StopScanException();
            foreach (var endpoint in Endpoints(plan.Target))
            {
                if (!plan.Capabilities.Query || !adapter.Capabilities.Query)
                {
                    RecordSkipped(ScanStage.Query, endpoint, "Query capability is unavailable.");
                    continue;
                }
                await RunIfAllowed(New(ScanStage.Query, endpoint.EndpointId, endpoint.Direction)).ConfigureAwait(false);
            }
            if (lifecycle is ScanRunLifecycle.Unknown or ScanRunLifecycle.Cancelled) throw new StopScanException();

            var candidates = SupportedCandidates(plan.Target, queries).ToArray();
            if (candidates.Length == 0) RecordSkipped(ScanStage.Initialize, null, "No supported 48 kHz PCM16 candidates.");
            foreach (var candidate in candidates.Take(plan.Settings.MaximumInitializeOperations))
            {
                if (!plan.Capabilities.Initialize || !adapter.Capabilities.Initialize)
                {
                    RecordSkipped(ScanStage.Initialize, EndpointFor(plan.Target, candidate.EndpointId), "Initialize capability is unavailable.");
                    continue;
                }
                await RunIfAllowed(New(ScanStage.Initialize, candidate.EndpointId, candidate.Direction, "PCM16", 48000, candidate.Channels, candidate.Period.ToUpperInvariant(), plan.Settings.DurationSeconds)).ConfigureAwait(false);
            }
            if (lifecycle is ScanRunLifecycle.Unknown or ScanRunLifecycle.Cancelled) throw new StopScanException();

            var streamCandidates = initialized.Where(x => x.Channels == 2).Take(plan.Settings.MaximumStreamOperations).ToArray();
            if (streamCandidates.Length == 0) RecordSkipped(ScanStage.Stream, null, "No successfully initialized stereo candidate is eligible for stream validation.");
            foreach (var candidate in streamCandidates)
            {
                if (!plan.Capabilities.Stream || !adapter.Capabilities.Stream)
                {
                    RecordSkipped(ScanStage.Stream, EndpointFor(plan.Target, candidate.EndpointId), "Stream capability is unavailable.");
                    continue;
                }
                await RunIfAllowed(New(ScanStage.Stream, candidate.EndpointId, candidate.Direction, "PCM16", 48000, 2, candidate.Period, plan.Settings.DurationSeconds)).ConfigureAwait(false);
            }
            if (lifecycle is ScanRunLifecycle.Unknown or ScanRunLifecycle.Cancelled) throw new StopScanException();

            var pairEligible = plan.Target.Capture is not null && plan.Target.Render is not null && plan.Target.PairIdentity is ScanPairIdentity.OperatorSelectedPair;
            var duplexPeriods = new[] { "MINIMUM", "DEFAULT" }.Where(p => initialized.Any(x => x.Direction == "CAPTURE" && x.Channels == 2 && x.Period == p) && initialized.Any(x => x.Direction == "RENDER" && x.Channels == 2 && x.Period == p)).Take(plan.Settings.MaximumDuplexOperations).ToArray();
            if (!pairEligible || duplexPeriods.Length == 0)
                RecordSkipped(ScanStage.Duplex, null, !pairEligible ? $"Pair identity is {plan.Target.PairIdentity}; Duplex requires an explicitly selected exact pair. No name-based grouping is performed." : "Both endpoints lack a successful matching stereo Initialize candidate.");
            foreach (var period in duplexPeriods)
            {
                if (!plan.Capabilities.Duplex || !adapter.Capabilities.Duplex)
                {
                    RecordSkipped(ScanStage.Duplex, null, "Duplex capability is unavailable.");
                    continue;
                }
                await RunIfAllowed(New(ScanStage.Duplex, null, "PAIR", "PCM16", 48000, 2, period, plan.Settings.DurationSeconds,
                    plan.Target.Capture!.EndpointId, plan.Target.Render!.EndpointId)).ConfigureAwait(false);
            }

            string? closeError = null;
            if (adapter is IWorkerAuthorizedScanAdapter workerAdapter)
            {
                try { await workerAdapter.FinishSessionAsync(cancel: false, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    lifecycle = ScanRunLifecycle.Unknown;
                    Interlocked.Exchange(ref _workerMayBeActive, 1);
                    closeError = ex.Message;
                    try { await workerAdapter.FinishSessionAsync(cancel: true, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception cancelError) { closeError += $"; cancellation failed: {cancelError.Message}"; }
                }
            }
            RecordLocal(ScanStage.Evaluate, (adapter.Mode == ScanExecutionMode.RealWorker
                ? "Only validated REAL_WASAPI evidence was evaluated; AudioHost remains NOT_CERTIFIED."
                : "Only received test evidence was evaluated; synthetic provenance cannot produce a real recommendation.") + (closeError is null ? "" : $" Worker session close failed: {closeError}"));
            var assessments = EvaluateAccepted();
            var recommendation = DeviceConfigurationEvaluator.Recommend(assessments);
            ScanPersistenceResult? persistence = null;
            if (persist && adapter.Mode == ScanExecutionMode.RealWorker)
            {
                var saveOperation = New(ScanStage.Save);
                Emit(saveOperation, ScanOperationLifecycle.Running, "Saving validated local scan evidence.");
                try
                {
                    persistence = DeviceConfigurationScanStorage.Save(sessionId, lifecycle, plan.Target, operations, assessments, recommendation,
                        candidateInventory: AutomaticScanPlanner.WithInitializeResults(AutomaticScanPlanner.FromQueries(queries.Values, plan.Settings.MmcssMode), operations), stageResults: AutomaticScanPlanner.CompleteFromOperations(operations));
                    if (persistence.Status == "SAVED_AND_RELOADED")
                    {
                        operations.Add(new(saveOperation, ScanOperationLifecycle.Completed, persistence.SummaryPath!, "ATOMIC_SAVE_AND_RELOAD_VERIFIED", null));
                        Emit(saveOperation, ScanOperationLifecycle.Completed, "Validated profile and scan summary saved and reloaded.");
                    }
                    else
                    {
                        operations.Add(new(saveOperation, ScanOperationLifecycle.Failed, persistence.Error ?? persistence.Status, persistence.Status, null));
                        Emit(saveOperation, ScanOperationLifecycle.Failed, $"{persistence.Status} · {persistence.Error ?? "profile was not saved"}");
                    }
                }
                catch (Exception ex)
                {
                    persistence = new("SAVE_FAILED", null, [], $"{ex.GetType().Name}: {ex.Message}");
                    operations.Add(new(saveOperation, ScanOperationLifecycle.Failed, persistence.Error!, "SAVE_FAILED", null));
                    Emit(saveOperation, ScanOperationLifecycle.Failed, persistence.Error!);
                    lifecycle = ScanRunLifecycle.Failed;
                }
            }
            else if (persist || adapter.Mode == ScanExecutionMode.DeterministicFake) RecordSkipped(ScanStage.Save, null, "Automatic persistence is disabled for deterministic/synthetic evidence.");
            var terminal = New(ScanStage.Completed);
            var terminalState = TerminalState(lifecycle);
            operations.Add(new(terminal, terminalState, lifecycle.ToString(), "NOT_APPLICABLE", null));
            Emit(terminal, terminalState, lifecycle.ToString());
            return Result(lifecycle, assessments, recommendation, persistence);
        }
        catch (StopScanException)
        {
            SkipUnstarted(operations, events, plan, queries, initialized);
            var assessments = EvaluateAccepted();
            var recommendation = DeviceConfigurationEvaluator.Recommend(assessments);
            if (adapter is IWorkerAuthorizedScanAdapter workerAdapter)
            {
                try { await workerAdapter.FinishSessionAsync(cancel: true, CancellationToken.None).ConfigureAwait(false); }
                catch { lifecycle = ScanRunLifecycle.Unknown; Interlocked.Exchange(ref _workerMayBeActive, 1); }
            }
            RecordLocal(ScanStage.Evaluate, "Aggregated only evidence accepted before interruption.");
            ScanPersistenceResult? persistence = null;
            if (persist && adapter.Mode == ScanExecutionMode.RealWorker)
            {
                try
                {
                    persistence = DeviceConfigurationScanStorage.Save(sessionId, lifecycle, plan.Target, operations, assessments, recommendation,
                        candidateInventory: AutomaticScanPlanner.WithInitializeResults(AutomaticScanPlanner.FromQueries(queries.Values, plan.Settings.MmcssMode), operations), stageResults: AutomaticScanPlanner.CompleteFromOperations(operations));
                    if (persistence.Status == "SAVED_AND_RELOADED") RecordLocal(ScanStage.Save, persistence.SummaryPath ?? "Validated scan summary saved and reloaded.");
                    else RecordSaveFailure($"{persistence.Status} · {persistence.Error ?? "profile was not saved"}", persistence.Status);
                }
                catch (Exception ex) { persistence = new("SAVE_FAILED", null, [], $"{ex.GetType().Name}: {ex.Message}"); if (lifecycle != ScanRunLifecycle.Unknown) lifecycle = ScanRunLifecycle.Failed; RecordSaveFailure(persistence.Error!); }
            }
            else if (persist || adapter.Mode == ScanExecutionMode.DeterministicFake) RecordSkipped(ScanStage.Save, null, "Interrupted scan evidence was not saved because no real worker authorization remained valid.");
            var terminal = New(ScanStage.Completed);
            var terminalState = TerminalState(lifecycle);
            operations.Add(new(terminal, terminalState, lifecycle.ToString(), "NOT_APPLICABLE", null));
            Emit(terminal, terminalState, lifecycle.ToString());
            return Result(lifecycle, assessments, recommendation, persistence);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _workerMayBeActive, 1);
            if (adapter is IWorkerAuthorizedScanAdapter workerAdapter)
                try { await workerAdapter.FinishSessionAsync(cancel: true, CancellationToken.None).ConfigureAwait(false); } catch { }
            var evaluationFailure = New(ScanStage.Evaluate);
            operations.Add(new(evaluationFailure, ScanOperationLifecycle.Unknown, $"Unexpected coordinator failure: {ex.GetType().Name}: {ex.Message}", "UNKNOWN", null));
            Emit(evaluationFailure, ScanOperationLifecycle.Unknown, operations[^1].Message);
            var assessments = Array.Empty<DuplexCandidateAssessment>();
            try { assessments = EvaluateAccepted(); } catch { }
            var recommendation = DeviceConfigurationEvaluator.Recommend(assessments);
            ScanPersistenceResult? persistence = null;
            if (persist && adapter.Mode == ScanExecutionMode.RealWorker)
                try
                {
                    persistence = DeviceConfigurationScanStorage.Save(sessionId, ScanRunLifecycle.Unknown, plan.Target, operations, assessments, recommendation,
                        candidateInventory: AutomaticScanPlanner.WithInitializeResults(AutomaticScanPlanner.FromQueries(queries.Values, plan.Settings.MmcssMode), operations), stageResults: AutomaticScanPlanner.CompleteFromOperations(operations));
                    if (persistence.Status != "SAVED_AND_RELOADED") RecordSaveFailure($"{persistence.Status} · {persistence.Error ?? "profile was not saved"}", persistence.Status);
                }
                catch (Exception saveError) { persistence = new("SAVE_FAILED", null, [], $"{saveError.GetType().Name}: {saveError.Message}"); RecordSaveFailure(persistence.Error!); }
            var terminal = New(ScanStage.Completed);
            operations.Add(new(terminal, ScanOperationLifecycle.Unknown, $"Unexpected coordinator failure: {ex.GetType().Name}: {ex.Message}", "UNKNOWN", null));
            Emit(terminal, ScanOperationLifecycle.Unknown, operations[^1].Message);
            return Result(ScanRunLifecycle.Unknown, assessments, recommendation, persistence);
        }
        finally { Volatile.Write(ref _running, 0); }

        ScanRunResult Result(ScanRunLifecycle state, IReadOnlyList<DuplexCandidateAssessment> assessments, DuplexRecommendation recommendation, ScanPersistenceResult? persistence = null) =>
            new(sessionId, state, plan.Target, operations.ToArray(), events.ToArray(), assessments, recommendation, Summary(state, operations), false, "NOT_CERTIFIED", persistence)
            {
                CandidateInventory = AutomaticScanPlanner.WithInitializeResults(AutomaticScanPlanner.FromQueries(queries.Values, plan.Settings.MmcssMode), operations),
                StageResults = AutomaticScanPlanner.CompleteFromOperations(operations)
            };

        DuplexCandidateAssessment[] EvaluateAccepted() => duplexReports.Select(d => DeviceConfigurationEvaluator.Evaluate(
            d.Report.GetProperty("requested").GetProperty("period").GetString()!, d.Report,
            d.CaptureEndpointId, d.RenderEndpointId, d.Report.GetProperty("requested").GetProperty("mmcss").GetString()!)).ToArray();

        void RecordSkipped(ScanStage stage, WindowsEndpointDto? endpoint, string message)
        {
            var op = New(stage, endpoint?.EndpointId, endpoint?.Direction);
            operations.Add(new(op, ScanOperationLifecycle.Skipped, message, "NOT_ATTEMPTED", null));
            Emit(op, ScanOperationLifecycle.Skipped, message);
        }

        void RecordLocal(ScanStage stage, string message)
        {
            var op = New(stage);
            operations.Add(new(op, ScanOperationLifecycle.Completed, message, "NOT_APPLICABLE", null));
            Emit(op, ScanOperationLifecycle.Completed, message);
        }

        void RecordSaveFailure(string message, string code = "SAVE_FAILED")
        {
            var op = New(ScanStage.Save);
            operations.Add(new(op, ScanOperationLifecycle.Failed, message, code, null));
            Emit(op, ScanOperationLifecycle.Failed, message);
        }

        static ScanOperationLifecycle TerminalState(ScanRunLifecycle state) => state switch
        {
            ScanRunLifecycle.Completed => ScanOperationLifecycle.Completed,
            ScanRunLifecycle.Failed => ScanOperationLifecycle.Failed,
            ScanRunLifecycle.Unknown => ScanOperationLifecycle.Unknown,
            ScanRunLifecycle.Cancelled => ScanOperationLifecycle.Skipped,
            ScanRunLifecycle.Incomplete => ScanOperationLifecycle.Failed,
            _ => ScanOperationLifecycle.Failed
        };

        ScanOperation New(ScanStage stage, string? endpointId = null, string? direction = null,
            string? encoding = null, int? sampleRateHz = null, int? channels = null, string? period = null,
            int durationSeconds = 0, string? captureId = null, string? renderId = null) =>
            new(stage, endpointId ?? captureId, direction ?? (stage == ScanStage.Duplex ? "PAIR" : null), encoding, sampleRateHz, channels, period, durationSeconds, Guid.NewGuid().ToString("D"), plan.Settings.MmcssMode, captureId, renderId);
    }

    private static IEnumerable<WindowsEndpointDto> Endpoints(ScanTarget target)
    {
        if (target.Capture is not null) yield return target.Capture;
        if (target.Render is not null) yield return target.Render;
    }

    private static WindowsEndpointDto? EndpointFor(ScanTarget target, string id) => target.Capture?.EndpointId == id ? target.Capture : target.Render;

    private static IEnumerable<(string EndpointId, string Direction, int Channels, string Period)> SupportedCandidates(ScanTarget target, IReadOnlyDictionary<string, RealFormatMatrixDto> queries)
    {
        foreach (var endpoint in Endpoints(target))
        {
            if (!queries.TryGetValue(endpoint.EndpointId!, out var query)) continue;
            var channels = endpoint.Direction == "CAPTURE" ? new[] { 1, 2 } : new[] { 2 };
            foreach (var row in RealFormatMatrixValidation.BuildUniqueRows(query, channels)
                .Where(r => !r.IsConflict && r.SampleRate == 48000 && r.Format == "PCM16" && r.Channels is 1 or 2 && channels.Contains(r.Channels)))
            {
                foreach (var period in new[] { "minimum", "default" })
                {
                    var candidate = period == "minimum" ? row.MinimumCandidate : row.DefaultCandidate;
                    if (candidate is { Status: "SUPPORTED_REPORTED", SupportStatus: "SUPPORTED_REPORTED" } && DeviceConfigurationProfileJson.IsSuccessfulHresult(candidate.SupportHr))
                        yield return (endpoint.EndpointId!, endpoint.Direction, row.Channels, period);
                }
            }
        }
    }

    private static bool IsInitializeSuccessful(RealFormatMatrixDto matrix) => matrix.Candidates.Count == 1 && matrix.Candidates[0] is { InitializeAttempted: true, InitializeStatus: "INITIALIZED" or "READY" } c && DeviceConfigurationProfileJson.IsSuccessfulHresult(c.FinalInitializeHr);

    private static bool CleanupIsUnsafe(string status) => status.Split(';').Any(stage =>
        stage.Trim() is "UNKNOWN" or "FAILED" || stage.Trim().EndsWith("_FAILED", StringComparison.Ordinal) || stage.Trim().Contains("CLEANUP_UNKNOWN", StringComparison.Ordinal));

    private static bool CleanupHasUnknown(string status) => status.Split(';').Any(stage => stage.Trim().Contains("UNKNOWN", StringComparison.Ordinal));

    private static void ValidateEvidence(ScanOperation op, ScanEvidence? evidence, ScanTarget target, ScanExecutionMode mode)
    {
        if (evidence is null) return;
        if (op.Stage is ScanStage.Query or ScanStage.Initialize)
        {
            var matrix = evidence.FormatMatrix ?? throw new InvalidDataException("Format operation returned no matrix evidence.");
            var expectedProvenance = mode == ScanExecutionMode.RealWorker
                ? op.Stage == ScanStage.Query ? "REAL_WASAPI_QUERY" : "REAL_WASAPI_INITIALIZE_ONLY"
                : evidence.FormatMatrix.Provenance.StartsWith("TEST_FAKE_", StringComparison.Ordinal) ? evidence.FormatMatrix.Provenance : "TEST_FAKE_INVALID";
            if (matrix.EndpointId != op.EndpointId || matrix.Direction != op.Direction || matrix.Provenance != expectedProvenance)
                throw new InvalidDataException("Format evidence identity or provenance does not match the scan operation.");
            if (op.Stage == ScanStage.Query && matrix.ReportLevel != "PASSIVE_QUERY" || op.Stage == ScanStage.Initialize && (matrix.ReportLevel != "INITIALIZE_ONLY" || matrix.Candidates.Count != 1 || matrix.Candidates[0].Encoding != op.Encoding || matrix.Candidates[0].SampleRate != op.SampleRateHz || matrix.Candidates[0].Channels != op.Channels || !string.Equals(matrix.Candidates[0].Period, op.Period, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Format evidence does not match the requested operation.");
        }
        if (op.Stage == ScanStage.Stream)
        {
            var stream = evidence.Stream ?? throw new InvalidDataException("Stream operation returned no stream evidence.");
            var report = stream.Report;
            static void Require(bool valid, string field)
            {
                if (!valid) throw new InvalidDataException($"Stream evidence contract mismatch at {field}.");
            }
            static bool HasString(JsonElement value, string name, string expected) =>
                value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) &&
                member.ValueKind == JsonValueKind.String && string.Equals(member.GetString(), expected, StringComparison.Ordinal);
            static bool HasInt(JsonElement value, string name, int expected) =>
                value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) &&
                member.ValueKind == JsonValueKind.Number && member.TryGetInt32(out var actual) && actual == expected;
            static bool HasUuid(JsonElement value, string name) =>
                value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) &&
                member.ValueKind == JsonValueKind.String && Guid.TryParse(member.GetString(), out var id) && id != Guid.Empty;

            Require(Guid.TryParse(op.OperationId, out var expectedStreamId) && expectedStreamId != Guid.Empty, "operation.operationId");
            Require(Guid.TryParse(stream.OperationId, out var streamId) && streamId != Guid.Empty && streamId == expectedStreamId, "ipc.operationId");
            Require(stream.EndpointId == op.EndpointId, "ipc.endpointId");
            Require(stream.Direction == op.Direction, "ipc.direction");
            Require(report.ValueKind == JsonValueKind.Object, "report");
            Require(HasInt(report, "schemaVersion", 1), "schemaVersion");
            Require(HasString(report, "reportType", "wasapi-stream-validation"), "reportType");
            Require(HasUuid(report, "reportId"), "reportId");
            Require(HasString(report, "provenance", mode == ScanExecutionMode.RealWorker ? "REAL_WASAPI_STREAM_VALIDATION" : "TEST_FAKE_STREAM_BACKEND"), "provenance");
            Require(HasString(report, "direction", op.Direction!), "direction");
            Require(HasString(report, "period", op.Period!), "period");
            Require(HasInt(report, "durationSeconds", op.DurationSeconds), "durationSeconds");
            var format = report.TryGetProperty("format", out var reportFormat) ? reportFormat : default;
            Require(HasInt(format, "sampleRateHz", op.SampleRateHz!.Value), "format.sampleRateHz");
            Require(HasString(format, "encoding", op.Encoding!), "format.encoding");
            Require(HasInt(format, "channels", op.Channels!.Value), "format.channels");
            Require(HasString(report, "physicalPlayback", "NOT_MEASURED"), "physicalPlayback");
            Require(HasString(report, "physicalRouting", "UNKNOWN"), "physicalRouting");
        }
        if (op.Stage == ScanStage.Duplex)
        {
            var duplex = evidence.Duplex ?? throw new InvalidDataException("Duplex operation returned no Duplex evidence.");
            var report = duplex.Report;
            var requested = report.GetProperty("requested");
            if (!Guid.TryParse(duplex.OperationId, out var duplexId) || !Guid.TryParse(op.OperationId, out var expectedDuplexId) || duplexId != expectedDuplexId ||
                report.GetProperty("schemaVersion").GetInt32() != 2 || report.GetProperty("reportType").GetString() != "wasapi-duplex-validation" ||
                !Guid.TryParse(report.GetProperty("operationId").GetString(), out var reportOperationId) || reportOperationId != expectedDuplexId ||
                duplex.CaptureEndpointId != target.Capture?.EndpointId || duplex.RenderEndpointId != target.Render?.EndpointId ||
                requested.GetProperty("captureEndpointId").GetString() != target.Capture?.EndpointId || requested.GetProperty("renderEndpointId").GetString() != target.Render?.EndpointId ||
                requested.GetProperty("period").GetString() != op.Period || requested.GetProperty("mmcss").GetString() != op.MmcssMode ||
                requested.GetProperty("durationSeconds").GetInt32() != op.DurationSeconds || requested.GetProperty("sampleRateHz").GetInt32() != op.SampleRateHz ||
                requested.GetProperty("encoding").GetString() != op.Encoding || requested.GetProperty("channels").GetInt32() != op.Channels ||
                report.GetProperty("provenance").GetString() != (mode == ScanExecutionMode.RealWorker ? "REAL_WASAPI_DUPLEX" : "TEST_FAKE_WASAPI_DUPLEX") ||
                report.GetProperty("capture").GetProperty("endpointId").GetString() != duplex.CaptureEndpointId || report.GetProperty("capture").GetProperty("direction").GetString() != "CAPTURE" ||
                report.GetProperty("render").GetProperty("endpointId").GetString() != duplex.RenderEndpointId || report.GetProperty("render").GetProperty("direction").GetString() != "RENDER" ||
                report.GetProperty("physicalRtl").GetString() != "NOT_MEASURED" || report.GetProperty("audioHostEligibility").GetString() != "NOT_CERTIFIED")
                throw new InvalidDataException("Duplex evidence identity, settings, or synthetic provenance does not match the scan operation.");
        }

    }

    private static string Summary(ScanRunLifecycle lifecycle, IReadOnlyList<ScanOperationResult> operations) =>
        $"{lifecycle} · завершено {operations.Count(o => o.Lifecycle == ScanOperationLifecycle.Completed)} · пропущено {operations.Count(o => o.Lifecycle == ScanOperationLifecycle.Skipped)} · ошибки {operations.Count(o => o.Lifecycle is ScanOperationLifecycle.Failed or ScanOperationLifecycle.Unknown)}. Physical RTL=NOT_MEASURED · AudioHost=NOT_CERTIFIED.";

    private static void SkipUnstarted(List<ScanOperationResult> operations, List<ScanProgressEvent> events, ScanPlan plan,
        IReadOnlyDictionary<string, RealFormatMatrixDto> queries, IReadOnlyList<(string EndpointId, string Direction, int Channels, string Period)> initialized)
    {
        bool Contains(ScanStage stage, string? endpointId = null, int? channels = null, string? period = null) => operations.Any(o =>
            o.Operation.Stage == stage && o.Operation.EndpointId == endpointId && o.Operation.Channels == channels && o.Operation.Period == period);
        void Add(ScanOperation operation)
        {
            operations.Add(new(operation, ScanOperationLifecycle.Skipped, "Not run after cancellation, device loss, worker disconnect, or unsafe cleanup.", "NOT_ATTEMPTED", null));
            events.Add(new(events.Count + 1, DateTimeOffset.UtcNow, operation.Stage, ScanOperationLifecycle.Skipped, operations[^1].Message, operation.EndpointId, operation.OperationId));
        }
        if (!Contains(ScanStage.Discovery)) Add(new(ScanStage.Discovery, OperationId: Guid.NewGuid().ToString("D")));
        foreach (var endpoint in Endpoints(plan.Target))
            if (!Contains(ScanStage.Query, endpoint.EndpointId)) Add(new(ScanStage.Query, endpoint.EndpointId, endpoint.Direction, OperationId: Guid.NewGuid().ToString("D")));
        foreach (var candidate in SupportedCandidates(plan.Target, queries).Take(plan.Settings.MaximumInitializeOperations))
        {
            var period = candidate.Period.ToUpperInvariant();
            if (!Contains(ScanStage.Initialize, candidate.EndpointId, candidate.Channels, period))
                Add(new(ScanStage.Initialize, candidate.EndpointId, candidate.Direction, "PCM16", 48000, candidate.Channels, period, plan.Settings.DurationSeconds, Guid.NewGuid().ToString("D")));
        }
        if (!operations.Any(o => o.Operation.Stage == ScanStage.Initialize)) Add(new(ScanStage.Initialize, OperationId: Guid.NewGuid().ToString("D")));
        foreach (var candidate in initialized.Where(x => x.Channels == 2).Take(plan.Settings.MaximumStreamOperations))
            if (!Contains(ScanStage.Stream, candidate.EndpointId, 2, candidate.Period))
                Add(new(ScanStage.Stream, candidate.EndpointId, candidate.Direction, "PCM16", 48000, 2, candidate.Period, plan.Settings.DurationSeconds, Guid.NewGuid().ToString("D")));
        if (!operations.Any(o => o.Operation.Stage == ScanStage.Stream)) Add(new(ScanStage.Stream, OperationId: Guid.NewGuid().ToString("D")));
        var pairEligible = plan.Target.Capture is not null && plan.Target.Render is not null && plan.Target.PairIdentity == ScanPairIdentity.OperatorSelectedPair;
        foreach (var period in new[] { "MINIMUM", "DEFAULT" }.Where(p => initialized.Any(x => x.Direction == "CAPTURE" && x.Channels == 2 && x.Period == p) && initialized.Any(x => x.Direction == "RENDER" && x.Channels == 2 && x.Period == p)).Take(plan.Settings.MaximumDuplexOperations))
            if (pairEligible && !Contains(ScanStage.Duplex, plan.Target.Capture!.EndpointId, 2, period))
                Add(new(ScanStage.Duplex, plan.Target.Capture.EndpointId, "PAIR", "PCM16", 48000, 2, period, plan.Settings.DurationSeconds, Guid.NewGuid().ToString("D"), plan.Settings.MmcssMode, plan.Target.Capture.EndpointId, plan.Target.Render!.EndpointId));
        if (!operations.Any(o => o.Operation.Stage == ScanStage.Duplex)) Add(new(ScanStage.Duplex, OperationId: Guid.NewGuid().ToString("D")));
    }

    private sealed class StopScanException : Exception { }
}
