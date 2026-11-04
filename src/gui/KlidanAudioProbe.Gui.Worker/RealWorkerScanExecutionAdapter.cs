using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

public sealed class RealWorkerScanExecutionAdapter : IWorkerAuthorizedScanAdapter
{
    private readonly NativeWorkerClient _worker;
    private readonly WindowsEndpointDiscoveryDto _discovery;
    private readonly WorkerScanSessionToken _session;
    private readonly WorkerScanPlan _workerPlan;
    private readonly string _planFingerprint;
    private readonly SemaphoreSlim _sessionEndGate = new(1, 1);
    private int _sessionEndState;

    public RealWorkerScanExecutionAdapter(NativeWorkerClient worker, WindowsEndpointDiscoveryDto discovery,
        WorkerScanSessionToken session, WorkerScanPlan workerPlan, ScanPlan plan)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _session = session;
        _workerPlan = workerPlan ?? throw new ArgumentNullException(nameof(workerPlan));
        _planFingerprint = plan?.Fingerprint ?? throw new ArgumentNullException(nameof(plan));
        var testWorker = worker.Capabilities.Any(c => c.StartsWith("TestInjected", StringComparison.Ordinal));
        WindowsEndpointDiscoveryValidation.Validate(discovery, testWorker);
        if (session.ScanSessionId == Guid.Empty)
            throw new ArgumentException("A valid already-registered Worker scan session is required.", nameof(session));
        if (!IsBoundToCore(plan, null)) throw new ArgumentException("Discovery, Worker authorization and coordinator plan must bind the same exact endpoints and limits.", nameof(workerPlan));
    }

    public ScanExecutionMode Mode => _worker.Capabilities.Any(c => c.StartsWith("TestInjected", StringComparison.Ordinal))
        ? ScanExecutionMode.DeterministicFake : ScanExecutionMode.RealWorker;

    public ScanCapabilities Capabilities => new(
        _worker.Capabilities.Contains("ListWindowsEndpoints", StringComparer.Ordinal),
        _worker.Capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal),
        _worker.Capabilities.Contains("RealExclusiveFormatInitialize", StringComparer.Ordinal),
        _worker.Capabilities.Contains("RealExclusiveStreamValidation", StringComparer.Ordinal),
        _worker.Capabilities.Contains("RealExclusiveDuplexValidation", StringComparer.Ordinal));

    public static WorkerScanPlan CreateWorkerPlan(ScanPlan plan, IReadOnlyList<string> workerCapabilities)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(workerCapabilities);
        var endpoints = Endpoints(plan.Target).Select(e => new WorkerScanEndpoint(e.EndpointId!, e.Direction)).ToArray();
        var operations = new List<string>();
        if (plan.Capabilities.Query && workerCapabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal)) operations.Add("QueryFormatMatrix");
        if (plan.Capabilities.Initialize && workerCapabilities.Contains("RealExclusiveFormatInitialize", StringComparer.Ordinal)) operations.Add("ValidateFormatMatrix");
        if (plan.Capabilities.Stream && workerCapabilities.Contains("RealExclusiveStreamValidation", StringComparer.Ordinal)) operations.Add("ValidateStream");
        if (plan.Capabilities.Duplex && workerCapabilities.Contains("RealExclusiveDuplexValidation", StringComparer.Ordinal)) operations.Add("ValidateDuplex");
        if (operations.Count == 0) throw new InvalidOperationException("The selected Worker has no real scan operation capability in the approved plan.");
        var formats = (from rate in new[] { 44100, 48000 }
                       from encoding in new[] { "PCM16", "PCM24_IN_32", "FLOAT32" }
                       from channels in new[] { 1, 2 }
                       select new WorkerScanFormat(rate, encoding, channels, ["MINIMUM", "DEFAULT"], [plan.Settings.MmcssMode])).ToArray();
        var initializeCount = Math.Min(plan.Settings.MaximumInitializeOperations, (plan.Target.Capture is null ? 0 : 4) + (plan.Target.Render is null ? 0 : 2));
        var streamCount = plan.Capabilities.Stream ? Math.Min(plan.Settings.MaximumStreamOperations, endpoints.Length * 2) : 0;
        var duplexCount = plan.Target.PairIdentity == ScanPairIdentity.OperatorSelectedPair && plan.Target.Capture is not null && plan.Target.Render is not null && plan.Capabilities.Duplex
            ? Math.Min(plan.Settings.MaximumDuplexOperations, 2) : 0;
        var queryCount = plan.Capabilities.Query ? endpoints.Length : 0;
        var workerPlan = new WorkerScanPlan(endpoints, operations.Distinct(StringComparer.Ordinal).ToArray(), formats,
            600, Math.Min(10, plan.Settings.DurationSeconds), 24);
        var workerOperationCount = queryCount + initializeCount + streamCount + duplexCount;
        var worstCaseSeconds = queryCount * 30 + initializeCount * 30 + streamCount * 45 + duplexCount * 51 + workerOperationCount * 5 + 8;
        if (worstCaseSeconds > workerPlan.LifetimeSeconds)
            throw new InvalidOperationException("The bounded scan plan exceeds Worker scan-session lifetime.");
        return workerPlan;
    }

    public bool IsBoundTo(ScanPlan plan, ScanSessionAuthorization authorization) => IsBoundToCore(plan, authorization);

    private bool IsBoundToCore(ScanPlan plan, ScanSessionAuthorization? authorization)
    {
        if (plan.Fingerprint != _planFingerprint || _session.ScanSessionId == Guid.Empty || Volatile.Read(ref _sessionEndState) != 0 || _worker.State.State != WorkerConnectionState.ConnectedFake)
            return false;
        var expectedDiscoveryProvenance = Mode == ScanExecutionMode.RealWorker ? "REAL_WINDOWS_METADATA" : "TEST_FAKE_FORMAT_BACKEND";
        if (_discovery.Provenance != expectedDiscoveryProvenance ||
            !_discovery.Endpoints.Any(e => e.EndpointId == plan.Target.Capture?.EndpointId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE") && plan.Target.Capture is not null ||
            !_discovery.Endpoints.Any(e => e.EndpointId == plan.Target.Render?.EndpointId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE") && plan.Target.Render is not null)
            return false;
        var endpointSet = _workerPlan.Endpoints.ToHashSet();
        var expected = CreateWorkerPlan(plan, _worker.Capabilities);
        return WorkerScanPlanFingerprint.Compute(expected) == WorkerScanPlanFingerprint.Compute(_workerPlan) &&
            Endpoints(plan.Target).All(e => endpointSet.Contains(new(e.EndpointId!, e.Direction))) &&
            _workerPlan.LifetimeSeconds is >= 1 and <= 600 && _workerPlan.MaximumOperationCount is >= 1 and <= 24 &&
            _workerPlan.MaximumOperationDurationSeconds >= plan.Settings.DurationSeconds &&
            (authorization is null || _workerPlan.MaximumOperationCount <= authorization.MaximumOperationCount &&
                authorization.MaximumDurationSeconds >= PlannedAudioSeconds(plan));
    }

    public async Task<ScanAdapterResult> ExecuteAsync(ScanOperation operation, ScanPlan plan,
        ScanSessionAuthorization authorization, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsBoundToCore(plan, authorization) || Volatile.Read(ref _sessionEndState) != 0)
            return new(ScanOperationLifecycle.Unknown, Message: "Worker session, discovery snapshot, or exact scan plan is stale.", CleanupStatus: "UNKNOWN", IdentityCurrent: false, WorkerConnected: _worker.State.State == WorkerConnectionState.ConnectedFake, CanSafelyContinue: false);
        if (operation.Stage == ScanStage.Discovery)
            return new(ScanOperationLifecycle.Completed, Message: "Exact active endpoint identities match the pre-registration discovery snapshot; no second enumeration was performed.", CleanupStatus: "NOT_APPLICABLE");

        var operationId = Guid.Parse(operation.OperationId);
        switch (operation.Stage)
        {
            case ScanStage.Query:
            {
                var matrix = await _worker.QueryFormatMatrixInScanAsync(_session, operationId, operation.EndpointId!, operation.Direction!, [1, 2], CancellationToken.None).ConfigureAwait(false);
                var cleanup = string.Join(';', matrix.Candidates.Select(c => c.QueryCleanupStatus).Distinct(StringComparer.Ordinal));
                return Outcome(new(FormatMatrix: matrix), cleanup, "Passive Exclusive format query completed.");
            }
            case ScanStage.Initialize:
            {
                var matrix = await _worker.ValidateFormatMatrixInScanAsync(_session, operationId, operation.EndpointId!, operation.Direction!, operation.SampleRateHz!.Value,
                    operation.Channels!.Value, operation.Encoding!, operation.Period!, CancellationToken.None).ConfigureAwait(false);
                return Outcome(new(FormatMatrix: matrix), matrix.Candidates[0].CleanupStatus, "Initialize-only evidence received; no Start or PCM operation was performed.");
            }
            case ScanStage.Stream:
            {
                var stream = await _worker.ValidateStreamInScanAsync(operation.OperationId, operation.EndpointId!, operation.Direction!, operation.Period!, operation.DurationSeconds,
                    operation.MmcssMode, _session, operationId, CancellationToken.None).ConfigureAwait(false);
                var cleanup = stream.Report.GetProperty("cleanup").GetString() ?? "UNKNOWN";
                return Outcome(new(Stream: stream), cleanup, "Real bounded Stream report received.");
            }
            case ScanStage.Duplex:
            {
                var duplex = await _worker.ValidateDuplexInScanAsync(operation.OperationId, operation.CaptureEndpointId!, operation.RenderEndpointId!, operation.Period!, operation.DurationSeconds,
                    operation.MmcssMode, _session, operationId, CancellationToken.None).ConfigureAwait(false);
                var report = duplex.Report;
                var cleanup = $"CAPTURE:{report.GetProperty("capture").GetProperty("cleanup").GetString()};RENDER:{report.GetProperty("render").GetProperty("cleanup").GetString()}";
                return Outcome(new(Duplex: duplex), cleanup, "Real coordinated Duplex report received.");
            }
            default: return new(ScanOperationLifecycle.Completed, Message: "Local coordinator stage.", CleanupStatus: "NOT_APPLICABLE");
        }

        ScanAdapterResult Outcome(ScanEvidence evidence, string cleanup, string message)
        {
            var unsafeCleanup = cleanup.Split(';').Any(s => s.Contains("UNKNOWN", StringComparison.Ordinal) || s.Contains("FAILED", StringComparison.Ordinal));
            return new(unsafeCleanup ? ScanOperationLifecycle.Unknown : ScanOperationLifecycle.Completed, evidence, message, cleanup,
                CanSafelyContinue: !unsafeCleanup);
        }
    }

    public async Task<ScanAdapterResult> CancelAsync(ScanOperation operation, ScanPlan plan, CancellationToken cancellationToken)
    {
        await _sessionEndGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionEndState == 0)
            {
                await _worker.CancelScanSessionAsync(_session, cancellationToken).ConfigureAwait(false);
                _sessionEndState = 1;
            }
        }
        finally { _sessionEndGate.Release(); }
        return new(ScanOperationLifecycle.Unknown, Message: "Worker cancellation was requested; completion is determined only by the in-flight operation report.", CleanupStatus: "CANCEL_REQUESTED", CanSafelyContinue: true);
    }

    public async Task FinishSessionAsync(bool cancel, CancellationToken cancellationToken)
    {
        await _sessionEndGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionEndState != 0) return;
            if (cancel) await _worker.CancelScanSessionAsync(_session, cancellationToken).ConfigureAwait(false);
            else await _worker.CloseScanSessionAsync(_session, cancellationToken).ConfigureAwait(false);
            _sessionEndState = cancel ? 1 : 2;
        }
        finally { _sessionEndGate.Release(); }
    }

    private static IEnumerable<WindowsEndpointDto> Endpoints(ScanTarget target)
    {
        if (target.Capture is not null) yield return target.Capture;
        if (target.Render is not null) yield return target.Render;
    }

    private static int PlannedAudioSeconds(ScanPlan plan)
    {
        var streamCount = plan.Capabilities.Stream ? Math.Min(plan.Settings.MaximumStreamOperations, (plan.Target.Capture is null ? 0 : 2) + (plan.Target.Render is null ? 0 : 2)) : 0;
        var duplexCount = plan.Target.PairIdentity == ScanPairIdentity.OperatorSelectedPair && plan.Target.Capture is not null && plan.Target.Render is not null && plan.Capabilities.Duplex
            ? Math.Min(plan.Settings.MaximumDuplexOperations, 2) : 0;
        return Math.Max(plan.Settings.DurationSeconds, (streamCount + duplexCount) * plan.Settings.DurationSeconds);
    }
}
