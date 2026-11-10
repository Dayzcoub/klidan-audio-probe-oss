using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

public sealed class RepeatabilityRegistrationUnknownException(Exception inner)
    : IOException($"RegisterScanSession outcome is unknown: {inner.GetType().Name}: {inner.Message}", inner);

public sealed class DuplexRepeatabilityWorkerWorkflow(NativeWorkerClient worker) : IDuplexRepeatabilityWorkflow, ICustomDuplexRepeatabilityWorkflow
{
    private int _running;
    private int _mayBeActive;

    public async Task<WindowsEndpointDiscoveryDto> RefreshEndpointsAsync(CancellationToken cancellationToken)
    {
        if (worker.State.State == WorkerConnectionState.Disconnected)
            await worker.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (worker.State.State != WorkerConnectionState.ConnectedFake)
            throw new InvalidOperationException($"Worker недоступен: {worker.State.Message}");
        var discovery = await worker.ListWindowsEndpointsAsync(cancellationToken).ConfigureAwait(false);
        WindowsEndpointDiscoveryValidation.Validate(discovery, worker.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal));
        return discovery;
    }

    public DuplexRepeatabilityPreparation Prepare(WindowsEndpointDiscoveryDto discovery, WindowsEndpointDto capture, WindowsEndpointDto render)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(render);
        if (worker.State.State != WorkerConnectionState.ConnectedFake) throw new InvalidOperationException("Worker недоступен; обновите устройства перед повторяемостью.");
        var testWorker = worker.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal);
        WindowsEndpointDiscoveryValidation.Validate(discovery, testWorker);
        var currentCapture = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == capture.EndpointId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE")
            ?? throw new InvalidOperationException("Exact Capture endpoint is not active in the prepared discovery snapshot.");
        var currentRender = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == render.EndpointId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE")
            ?? throw new InvalidOperationException("Exact Render endpoint is not active in the prepared discovery snapshot.");
        var capabilities = worker.Capabilities;
        if (!capabilities.Contains("RealExclusiveDuplexValidation", StringComparer.Ordinal) || !capabilities.Contains("ScanSessionAuthorizationV1", StringComparer.Ordinal))
            throw new InvalidOperationException("Worker does not advertise the bounded authorized Duplex capabilities.");
        var plan = ScanPlan.Build(new(currentCapture, currentRender, ScanPairIdentity.OperatorSelectedPair), new(true, false, false, false, true), new(DurationSeconds: 5, MaximumDuplexOperations: 2, MmcssMode: "OFF"));
        var workerPlan = new WorkerScanPlan(
            [new(currentCapture.EndpointId!, "CAPTURE"), new(currentRender.EndpointId!, "RENDER")],
            ["ValidateDuplex"],
            [new(48000, "PCM16", 2, ["MINIMUM", "DEFAULT"], ["OFF"])],
            600, 5, DuplexRepeatabilityCoordinator.MaximumRuns);
        return new(discovery, plan, workerPlan, DuplexRepeatabilityPreparation.Fingerprint(discovery), worker.State.ProcessId);
    }

    public DuplexRepeatabilityPreparation PrepareCustom(WindowsEndpointDiscoveryDto discovery, WindowsEndpointDto capture, WindowsEndpointDto render,
        PeriodDiscoverySeriesResult series, int frames)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var testWorker = worker.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal);
        WindowsEndpointDiscoveryValidation.Validate(discovery, testWorker);
        if (worker.State.State != WorkerConnectionState.ConnectedFake) throw new InvalidOperationException("Worker недоступен; обновите устройства перед repeatability.");
        var currentCapture = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == capture.EndpointId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE")
            ?? throw new InvalidOperationException("Exact Capture endpoint is not active in the prepared discovery snapshot.");
        var currentRender = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == render.EndpointId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE")
            ?? throw new InvalidOperationException("Exact Render endpoint is not active in the prepared discovery snapshot.");
        if (!worker.Capabilities.Contains("RealExclusiveDuplexValidation", StringComparer.Ordinal) || !worker.Capabilities.Contains("ScanSessionAuthorizationV1", StringComparer.Ordinal))
            throw new InvalidOperationException("Worker does not advertise the bounded authorized Duplex capabilities.");
        var period = CustomPeriodDuplexEvidence.Select(series, currentCapture.EndpointId!, currentRender.EndpointId!, frames,
            allowSynthetic: testWorker);
        var plan = ScanPlan.Build(new(currentCapture, currentRender, ScanPairIdentity.OperatorSelectedPair), new(true, false, false, false, true), new(DurationSeconds: 5, MaximumDuplexOperations: 5, MmcssMode: "OFF"));
        var workerPlan = new WorkerScanPlan(
            [new(currentCapture.EndpointId!, "CAPTURE"), new(currentRender.EndpointId!, "RENDER")],
            ["ValidateDuplex"], [new(48000, "PCM16", 2, [period.AuthorizationValue], ["OFF"])],
            600, 5, 5);
        return new(discovery, plan, workerPlan, DuplexRepeatabilityPreparation.Fingerprint(discovery), worker.State.ProcessId,
            Enumerable.Repeat(period.AuthorizationValue, 5).ToArray(), 250);
    }

    public async Task<DuplexRepeatabilityResult> RunApprovedAsync(DuplexRepeatabilityPreparation preparation, Guid confirmationId,
        Action<ScanProgressEvent>? progress = null, CancellationToken cancellationToken = default)
        => await RunApprovedCoreAsync(preparation, confirmationId, progress, cancellationToken).ConfigureAwait(false);

    public Task<DuplexRepeatabilityResult> RunCustomApprovedAsync(DuplexRepeatabilityPreparation preparation, Guid confirmationId,
        Action<ScanProgressEvent>? progress = null, CancellationToken cancellationToken = default)
        => RunApprovedCoreAsync(preparation, confirmationId, progress, cancellationToken);

    private async Task<DuplexRepeatabilityResult> RunApprovedCoreAsync(DuplexRepeatabilityPreparation preparation, Guid confirmationId,
        Action<ScanProgressEvent>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (confirmationId == Guid.Empty) throw new ArgumentException("Operator approval identity is required.", nameof(confirmationId));
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("Повторяемость уже выполняется.");
        WorkerScanSessionToken? session = null;
        var registrationAttempted = false;
        NativeWorkerClient.SharedOperationLease? sharedOperation = null;
        try
        {
            sharedOperation = worker.AcquireSharedOperation("repeatability");
            if (Volatile.Read(ref _mayBeActive) != 0) throw new InvalidOperationException("Previous repeatability operation has unknown cleanup; restart the Worker before retrying.");
            if (worker.State.State != WorkerConnectionState.ConnectedFake) throw new IOException("Worker отключён до регистрации repeatability plan.");
            cancellationToken.ThrowIfCancellationRequested();
            registrationAttempted = true;
            session = await worker.RegisterScanSessionAsync(preparation.WorkerPlan, true, confirmationId, cancellationToken).ConfigureAwait(false);
            var stages = new HashSet<ScanStage> { ScanStage.Duplex };
            const int maximumDuration = 30;
            var plannedPeriods = preparation.Periods ?? new[] { "MINIMUM", "DEFAULT", "MINIMUM", "DEFAULT", "MINIMUM", "DEFAULT" };
            var operationCount = plannedPeriods.Count;
            var authorization = new ScanSessionAuthorization(confirmationId, true,
                ScanSessionAuthorization.Fingerprint(preparation.Plan, stages, maximumDuration, operationCount),
                stages, maximumDuration, operationCount);
            var adapter = new RepeatabilityWorkerAdapter(worker, preparation.Discovery, session, preparation.WorkerPlan, preparation.Plan);
            var result = await new DuplexRepeatabilityCoordinator(adapter).RunAsync(preparation.Plan, authorization, plannedPeriods, preparation.PauseMilliseconds, progress, cancellationToken).ConfigureAwait(false);
            if (result.Lifecycle == ScanRunLifecycle.Unknown) { Interlocked.Exchange(ref _mayBeActive, 1); sharedOperation.Keep(); }
            if (adapter.Mode != ScanExecutionMode.RealWorker)
                return result with { PersistenceStatus = "NOT_SAVED_SYNTHETIC", PersistenceError = "Synthetic evidence is not eligible for a real repeatability report." };
            try
            {
                var reportPath = DuplexRepeatabilityReportStorage.Save(result);
                return result with { ReportPath = reportPath, PersistenceStatus = "SAVED_AND_RELOADED" };
            }
            catch (Exception saveError)
            {
                return result with { PersistenceStatus = "SAVE_FAILED", PersistenceError = $"{saveError.GetType().Name}: {saveError.Message}" };
            }
        }
        catch (Exception exception)
        {
            // BUSY is an explicit registration refusal; it does not leave an unknown native operation behind.
            if (session is null && registrationAttempted && exception is WorkerCommandException { Command: "RegisterScanSession" })
                throw;
            if (session is null && registrationAttempted)
            {
                Interlocked.Exchange(ref _mayBeActive, 1);
                sharedOperation?.Keep();
                throw new RepeatabilityRegistrationUnknownException(exception);
            }
            if (session is { } token)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(NativeWorkerClient.SessionCloseTimeoutSeconds));
                try { await worker.CancelScanSessionAsync(token, cleanup.Token).ConfigureAwait(false); }
                catch { Interlocked.Exchange(ref _mayBeActive, 1); sharedOperation?.Keep(); }
            }
            else if (registrationAttempted) { Interlocked.Exchange(ref _mayBeActive, 1); sharedOperation?.Keep(); }
            throw;
        }
        finally { sharedOperation?.Dispose(); Volatile.Write(ref _running, 0); }
    }

    private sealed class RepeatabilityWorkerAdapter(NativeWorkerClient worker, WindowsEndpointDiscoveryDto discovery,
        WorkerScanSessionToken session, WorkerScanPlan workerPlan, ScanPlan plan) : IDuplexRepeatabilityAdapter
    {
        private int _sessionEnded;
        public ScanExecutionMode Mode => worker.Capabilities.Any(c => c.StartsWith("TestInjected", StringComparison.Ordinal)) ? ScanExecutionMode.DeterministicFake : ScanExecutionMode.RealWorker;

        public bool IsBoundTo(ScanPlan candidate, ScanSessionAuthorization authorization) => candidate.Fingerprint == plan.Fingerprint && session.ScanSessionId != Guid.Empty && Volatile.Read(ref _sessionEnded) == 0 &&
            worker.State.State == WorkerConnectionState.ConnectedFake && discovery.Provenance == (Mode == ScanExecutionMode.RealWorker ? "REAL_WINDOWS_METADATA" : "TEST_FAKE_FORMAT_BACKEND") &&
            workerPlan.MaximumOperationCount >= (plan.Settings.MaximumDuplexOperations == 5 ? 5 : DuplexRepeatabilityCoordinator.MaximumRuns) && workerPlan.MaximumOperationDurationSeconds == 5 &&
            workerPlan.Endpoints.Any(e => e.EndpointId == candidate.Target.Capture!.EndpointId && e.Direction == "CAPTURE") && workerPlan.Endpoints.Any(e => e.EndpointId == candidate.Target.Render!.EndpointId && e.Direction == "RENDER") &&
            authorization.MaximumOperationCount >= (plan.Settings.MaximumDuplexOperations == 5 ? 5 : DuplexRepeatabilityCoordinator.MaximumRuns);

        public async Task<ScanAdapterResult> ExecuteAsync(ScanOperation operation, ScanPlan candidate, ScanSessionAuthorization authorization, CancellationToken cancellationToken)
        {
            if (!IsBoundTo(candidate, authorization)) return new(ScanOperationLifecycle.Unknown, Message: "Repeatability Worker session is stale.", CleanupStatus: "UNKNOWN", IdentityCurrent: false, CanSafelyContinue: false);
            var validation = await worker.ValidateDuplexInScanAsync(operation.OperationId, operation.CaptureEndpointId!, operation.RenderEndpointId!, operation.Period!, operation.DurationSeconds, operation.MmcssMode, session, Guid.Parse(operation.OperationId), cancellationToken).ConfigureAwait(false);
            var report = validation.Report;
            var cleanup = $"CAPTURE:{report.GetProperty("capture").GetProperty("cleanup").GetString()};RENDER:{report.GetProperty("render").GetProperty("cleanup").GetString()}";
            var unsafeCleanup = cleanup.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) || cleanup.Contains("FAILED", StringComparison.OrdinalIgnoreCase);
            return new(unsafeCleanup ? ScanOperationLifecycle.Unknown : ScanOperationLifecycle.Completed, new(Duplex: validation), "Real bounded Duplex repeatability report received.", cleanup, CanSafelyContinue: !unsafeCleanup);
        }

        public Task<ScanAdapterResult> CancelAsync(ScanOperation operation, ScanPlan candidate, CancellationToken cancellationToken) => worker.CancelScanSessionAsync(session, cancellationToken).ContinueWith(_ => new ScanAdapterResult(ScanOperationLifecycle.Unknown, Message: "Repeatability session cancellation requested.", CleanupStatus: "CANCEL_REQUESTED", CanSafelyContinue: false), cancellationToken);

        public async Task FinishSessionAsync(bool cancel, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _sessionEnded, 1) != 0) return;
            if (cancel) await worker.CancelScanSessionAsync(session, cancellationToken).ConfigureAwait(false);
            else await worker.CloseScanSessionAsync(session, cancellationToken).ConfigureAwait(false);
        }
    }
}
