using System.Security.Cryptography;
using System.Text;
using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

public sealed class WorkerAutomaticScanWorkflow(NativeWorkerClient worker) : IAutomaticScanWorkflow
{
    private int _running;
    private int _mayBeActive;

    public async Task<WindowsEndpointDiscoveryDto> RefreshEndpointsAsync(CancellationToken cancellationToken)
    {
        if (worker.State.State == WorkerConnectionState.Disconnected)
            await worker.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (worker.State.State != WorkerConnectionState.ConnectedFake)
            throw new InvalidOperationException($"Worker недоступен: {worker.State.Message}");
        EndpointDiscoveryQaTrace.Write("workflow-request", state: worker.State.State.ToString(), exitCode: worker.State.ExitCode);
        System.Diagnostics.Trace.TraceInformation("EndpointDiscovery stage=request-sent workerState={0}", worker.State.State);
        var discovery = await worker.ListWindowsEndpointsAsync(cancellationToken).ConfigureAwait(false);
        EndpointDiscoveryQaTrace.Write("workflow-response", state: worker.State.State.ToString(), exitCode: worker.State.ExitCode);
        System.Diagnostics.Trace.TraceInformation("EndpointDiscovery stage=response-received endpointCount={0} workerState={1}", discovery.Endpoints.Count, worker.State.State);
        WindowsEndpointDiscoveryValidation.Validate(discovery, worker.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal));
        return discovery;
    }

    public AutomaticScanPreparation Prepare(WindowsEndpointDiscoveryDto discovery, AutomaticScanSelection selection)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(selection);
        if (worker.State.State != WorkerConnectionState.ConnectedFake) throw new InvalidOperationException("Worker отключён; обновите устройства перед сканированием.");
        var testWorker = worker.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal);
        WindowsEndpointDiscoveryValidation.Validate(discovery, testWorker);

        var selectedGroup = selection.PhysicalGroupId is null ? null : PhysicalDeviceGrouping.Group(discovery.Endpoints)
            .FirstOrDefault(g => g.GroupId == selection.PhysicalGroupId);
        if (selection.PhysicalGroupId is not null && selectedGroup is null)
            throw new InvalidOperationException("Физическая группа устройства изменилась после discovery; выберите устройство заново.");
        if (selection.RequireUnambiguousPair && selectedGroup?.CanAutoSelectPair != true)
            throw new InvalidOperationException("Автоматическая пара больше не является однозначной; выберите Capture и Render явно.");

        WindowsEndpointDto? capture;
        WindowsEndpointDto? render;
        var identity = ScanPairIdentity.Unresolved;
        if (selection.ExplicitPair)
        {
            capture = Find(selection.CaptureEndpointId, "CAPTURE");
            render = Find(selection.RenderEndpointId, "RENDER");
            if (!selection.AllowUnconfirmedPair && selectedGroup is not null && (!selectedGroup.CaptureEndpoints.Any(e => e.EndpointId == capture.EndpointId) || !selectedGroup.RenderEndpoints.Any(e => e.EndpointId == render.EndpointId)))
                throw new InvalidOperationException("Выбранные endpoints больше не принадлежат одной подтверждённой физической группе.");
            identity = ScanPairIdentity.OperatorSelectedPair;
        }
        else
        {
            var endpoint = discovery.Endpoints.FirstOrDefault(e => e.EndpointId == selection.EndpointId)
                ?? throw new InvalidOperationException("Выбранный точный endpoint отсутствует в обновлённом списке; выберите устройство заново.");
            if (endpoint.Direction == "CAPTURE") { capture = endpoint; render = null; }
            else if (endpoint.Direction == "RENDER") { capture = null; render = endpoint; }
            else throw new InvalidDataException("Выбранный endpoint имеет неизвестное направление.");
            if (selectedGroup is not null && !selectedGroup.CaptureEndpoints.Any(e => e.EndpointId == endpoint.EndpointId) && !selectedGroup.RenderEndpoints.Any(e => e.EndpointId == endpoint.EndpointId))
                throw new InvalidOperationException("Выбранный endpoint больше не принадлежит выбранной физической группе.");
        }

        WindowsEndpointDto Find(string? id, string direction)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException($"Для парного сканирования требуется явно выбрать endpoint {direction}.");
            var endpoint = discovery.Endpoints.FirstOrDefault(e => e.EndpointId == id && e.Direction == direction)
                ?? throw new InvalidOperationException($"Выбранный точный endpoint {direction} отсутствует в обновлённом списке; пара не изменялась автоматически.");
            if (endpoint.DeviceState != "ACTIVE") throw new InvalidOperationException($"Endpoint {direction} больше не активен; автоматическая подмена запрещена.");
            return endpoint;
        }

        var capabilities = worker.Capabilities;
        var scanCapabilities = new ScanCapabilities(
            capabilities.Contains("ListWindowsEndpoints", StringComparer.Ordinal),
            capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal),
            capabilities.Contains("RealExclusiveFormatInitialize", StringComparer.Ordinal),
            capabilities.Contains("RealExclusiveStreamValidation", StringComparer.Ordinal),
            capabilities.Contains("RealExclusiveDuplexValidation", StringComparer.Ordinal));
        var target = new ScanTarget(capture, render, identity);
        var plan = ScanPlan.Build(target, scanCapabilities, new(DurationSeconds: 5, MmcssMode: "OFF"));
        var workerPlan = RealWorkerScanExecutionAdapter.CreateWorkerPlan(plan, capabilities);
        var queryCount = scanCapabilities.Query ? (capture is null ? 0 : 1) + (render is null ? 0 : 1) : 0;
        var initializeCount = scanCapabilities.Initialize ? Math.Min(plan.Settings.MaximumInitializeOperations, (capture is null ? 0 : 4) + (render is null ? 0 : 2)) : 0;
        var streamCount = scanCapabilities.Stream ? Math.Min(plan.Settings.MaximumStreamOperations, ((capture is null ? 0 : 1) + (render is null ? 0 : 1)) * 2) : 0;
        var duplexCount = identity == ScanPairIdentity.OperatorSelectedPair && scanCapabilities.Duplex
            ? Math.Min(plan.Settings.MaximumDuplexOperations, 2) : 0;
        var pair = identity == ScanPairIdentity.OperatorSelectedPair && capture is not null && render is not null;
        var periodPreflightQueries = pair && scanCapabilities.Initialize ? 2 : 0;
        var periodInitializeCount = pair && scanCapabilities.Initialize ? ExclusivePeriodDiscoveryPlanning.MaximumCandidates * 2 : 0;
        var shortCustomCount = pair && scanCapabilities.Duplex && scanCapabilities.Initialize ? ExclusivePeriodDiscoveryPlanning.MaximumCandidates : 0;
        var baseRepeatCount = pair && scanCapabilities.Duplex ? DuplexRepeatabilityCoordinator.MaximumRuns : 0;
        var customRepeatCount = pair && scanCapabilities.Duplex && scanCapabilities.Initialize ? ExclusivePeriodDiscoveryPlanning.MaximumCandidates * 5 : 0;
        var initialOperationCount = queryCount + initializeCount + streamCount + duplexCount;
        var sustainedOperations = pair && scanCapabilities.Duplex && capabilities.Contains("RealExclusiveSustainedDuplexValidation", StringComparer.Ordinal) ? 8 : 0;
        var sustainedValidationDuration = selection.ExtendedSustainedValidation ? 600 : 300;
        var sustainedSeconds = sustainedOperations == 0 ? 0 : 6 * 60 + 2 * sustainedValidationDuration;
        var totalOperationCount = initialOperationCount + periodPreflightQueries + periodInitializeCount + shortCustomCount + baseRepeatCount + customRepeatCount + sustainedOperations;
        var activeSeconds = (streamCount + duplexCount + shortCustomCount + baseRepeatCount + customRepeatCount) * plan.Settings.DurationSeconds + sustainedSeconds;
        var sessionCount = 1 + (periodInitializeCount > 0 ? 1 : 0) + shortCustomCount + (baseRepeatCount > 0 ? 1 : 0) + (customRepeatCount > 0 ? ExclusivePeriodDiscoveryPlanning.MaximumCandidates : 0) + sustainedOperations;
        var wallClockSeconds = checked(initialOperationCount * 60 + periodPreflightQueries * 15 + periodInitializeCount * 60 + shortCustomCount * 60 + (baseRepeatCount + customRepeatCount) * 60 + sustainedSeconds + Math.Max(0, baseRepeatCount + customRepeatCount - 1) + sessionCount * 12);
        return new(discovery, plan, workerPlan, activeSeconds, selection.AllowUnconfirmedPair)
        {
            StagedPlan = AutomaticScanPlanner.Create(discovery, plan, workerPlan),
            MaximumTotalOperations = totalOperationCount,
            MaximumWallClockSeconds = wallClockSeconds,
            SustainedDurationSeconds = sustainedValidationDuration
        };
    }

    public async Task<ScanRunResult> RunApprovedAsync(AutomaticScanPreparation preparation, Guid confirmationId,
        Action<ScanProgressEvent>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (confirmationId == Guid.Empty) throw new ArgumentException("Operator approval identity is required.", nameof(confirmationId));
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("Сканирование уже выполняется.");
        WorkerScanSessionToken? session = null;
        var registrationAttempted = false;
        NativeWorkerClient.SharedOperationLease? sharedOperation = null;
        try
        {
            sharedOperation = worker.AcquireSharedOperation("automatic scan");
            if (Volatile.Read(ref _mayBeActive) != 0) throw new InvalidOperationException("Предыдущая операция имеет неизвестный исход; требуется новый процесс приложения.");
            if (worker.State.State != WorkerConnectionState.ConnectedFake) throw new IOException("Worker отключён до регистрации плана.");
            cancellationToken.ThrowIfCancellationRequested();
            registrationAttempted = true;
            session = await worker.RegisterScanSessionAsync(preparation.WorkerPlan, operatorAccepted: true, confirmationId: confirmationId, cancellationToken: cancellationToken).ConfigureAwait(false);

            var allowedStages = Enum.GetValues<ScanStage>().ToHashSet();
            var maximumDuration = Math.Clamp(Math.Max(preparation.Plan.Settings.DurationSeconds, preparation.MaximumActiveSeconds),
                preparation.Plan.Settings.DurationSeconds, 30);
            const int maximumOperations = 24;
            var authorization = new ScanSessionAuthorization(confirmationId, true,
                ScanSessionAuthorization.Fingerprint(preparation.Plan, allowedStages, maximumDuration, maximumOperations),
                allowedStages, maximumDuration, maximumOperations);
            var adapter = new RealWorkerScanExecutionAdapter(worker, preparation.Discovery, session,
                preparation.WorkerPlan, preparation.Plan);
            // The first session is deliberately closed before the existing C4.2A/repeatability workflows run.
            var result = await new ScanCoordinator(adapter).RunAsync(preparation.Plan, authorization, progress, cancellationToken, persist: false).ConfigureAwait(false);
            sharedOperation.Dispose();
            sharedOperation = null;
            var childOperations = result.Operations.Select(ToChildOperation).ToList();
            var childEvents = new List<ScanProgressEvent>();
            var budget = new AutomaticScanBudget(preparation.MaximumTotalOperations, preparation.MaximumActiveSeconds, preparation.MaximumWallClockSeconds);
            var operationBudgetUsed = result.Operations.Count(o => o.Operation.Stage is ScanStage.Query or ScanStage.Initialize or ScanStage.Stream or ScanStage.Duplex);
            var budgetExceeded = false;
            void EmitChild(ScanProgressEvent item)
            {
                childEvents.Add(item);
                try { progress?.Invoke(item); } catch { }
            }
            if (result.Lifecycle == ScanRunLifecycle.Unknown) { Interlocked.Exchange(ref _mayBeActive, 1); return result with { ChildOperations = childOperations }; }
            PeriodDiscoverySeriesResult? period = null;
            AutomaticScanStageResult periodStage;
            var repeatedPeriods = new List<string>();
            var repeatabilityRuns = new List<DuplexRepeatabilityRun>();
            long orchestrationSequence = result.Events.Count;
            var shortDuplexStage = result.StageResults.FirstOrDefault(s => s.Stage == AutomaticScanStage.SHORT_DUPLEX)
                ?? new(AutomaticScanStage.SHORT_DUPLEX, AutomaticScanStageState.NOT_RUN, "No short Duplex stage was scheduled.");
            AutomaticScanStageResult repeatabilityStage;
            if (preparation.Plan.Target is { Capture: { } capture, Render: { } render, PairIdentity: ScanPairIdentity.OperatorSelectedPair } &&
                worker.Capabilities.Contains("RealExclusiveCustomPeriodInitialize", StringComparer.Ordinal))
            {
                try
                {
                    if (operationBudgetUsed + 2 + ExclusivePeriodDiscoveryPlanning.MaximumCandidates * 2 > preparation.MaximumTotalOperations)
                        throw new ScanBudgetExceededException("Period discovery exceeds the approved operation budget; fresh operator consent is required.");
                    var periodWorkflow = new ExclusivePeriodDiscoveryWorkerWorkflow(worker);
                    var periodPreparation = await periodWorkflow.PrepareAsync(preparation.Discovery, capture, render, cancellationToken).ConfigureAwait(false);
                    period = await periodWorkflow.RunApprovedAsync(periodPreparation, DerivedConfirmationId(confirmationId, "period-discovery", 0), message => EmitChild(new(++orchestrationSequence, DateTimeOffset.UtcNow, ScanStage.PeriodDiscovery, ScanOperationLifecycle.Running, message, null, null)), cancellationToken).ConfigureAwait(false);
                    result = result with { PeriodDiscoveryDiagnostics = period.Diagnostics };
                    childOperations.AddRange(new[] { periodPreparation.Capture, periodPreparation.Render }.Select(endpoint =>
                        new AutomaticScanChildOperation("PERIOD_DISCOVERY", null, null, "COMPLETED", "REAL_WASAPI_QUERY", "RELEASE_ATTEMPTED",
                            "Endpoint-specific period preflight query", null, null, endpoint.EndpointId, endpoint.Direction)));
                    childOperations.AddRange(period.Reports.Select(report => new AutomaticScanChildOperation(
                        "PERIOD_DISCOVERY", report.OperationId, report.ReportId, period.Lifecycle == "COMPLETED" ? "COMPLETED" : "UNKNOWN",
                        report.Provenance, report.Candidates.Count == 1 ? report.Candidates[0].CleanupStatus : "UNKNOWN",
                        "C4.2A Initialize-only period evidence", period.ReportPath,
                        report.Candidates.Count == 1 ? report.Candidates[0].RequestedFrames.ToString() : null, report.EndpointId, report.Direction)));
                    operationBudgetUsed += 2 + period.Reports.Count;
                    periodStage = new(AutomaticScanStage.PERIOD_DISCOVERY,
                        period.Lifecycle != "COMPLETED" ? AutomaticScanStageState.UNKNOWN : period.PersistenceStatus.StartsWith("SAVE_FAILED", StringComparison.Ordinal) ? AutomaticScanStageState.FAILED : AutomaticScanStageState.COMPLETED,
                        period.PersistenceStatus + (period.Diagnostics is { } diagnostics ? $"; {diagnostics.Summary}" : string.Empty));
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("no bounded intermediate", StringComparison.OrdinalIgnoreCase))
                {
                    periodStage = new(AutomaticScanStage.PERIOD_DISCOVERY, AutomaticScanStageState.SKIPPED, ex.Message);
                }
                catch (OperationCanceledException)
                {
                    periodStage = new(AutomaticScanStage.PERIOD_DISCOVERY, AutomaticScanStageState.UNKNOWN, "Period discovery cancellation left the active outcome unresolved.");
                }
                catch (WorkerCommandException ex)
                {
                    periodStage = new(AutomaticScanStage.PERIOD_DISCOVERY, AutomaticScanStageState.UNKNOWN, $"Period discovery Worker failure: {ex.ErrorCode}.");
                }
                catch (IOException ex)
                {
                    periodStage = new(AutomaticScanStage.PERIOD_DISCOVERY, AutomaticScanStageState.UNKNOWN, $"Period discovery Worker failure: {ex.Message}");
                }
                catch (ScanBudgetExceededException ex)
                {
                    budgetExceeded = true;
                    periodStage = new(AutomaticScanStage.PERIOD_DISCOVERY, AutomaticScanStageState.FAILED, ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    periodStage = new(AutomaticScanStage.PERIOD_DISCOVERY, AutomaticScanStageState.UNKNOWN, $"Period discovery binding became invalid: {ex.Message}");
                }
                catch (Exception ex)
                {
                    periodStage = new(AutomaticScanStage.PERIOD_DISCOVERY, AutomaticScanStageState.FAILED, $"Period discovery failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
                periodStage = new(AutomaticScanStage.PERIOD_DISCOVERY, AutomaticScanStageState.SKIPPED, "An exact Capture/Render pair and custom-period capability are required.");

            if (periodStage.State == AutomaticScanStageState.UNKNOWN)
            {
                result = result with { Lifecycle = ScanRunLifecycle.Unknown, Summary = result.Summary + " Period discovery stopped the scan because its outcome is UNKNOWN." };
                Interlocked.Exchange(ref _mayBeActive, 1);
                var unknownStages = MergeStages(result.StageResults, periodStage, shortDuplexStage, new(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.NOT_RUN, "Stopped after UNKNOWN period discovery."));
                ScanPersistenceResult? unknownPersistence = null;
                if (adapter.Mode == ScanExecutionMode.RealWorker)
                {
                    try { unknownPersistence = DeviceConfigurationScanStorage.Save(result.SessionId, ScanRunLifecycle.Unknown, result.Target, result.Operations, result.DuplexAssessments, result.Recommendation, candidateInventory: result.CandidateInventory, stageResults: unknownStages, childOperations: childOperations, budget: budget); }
                    catch (Exception ex) { unknownPersistence = new("SAVE_FAILED", null, [], $"{ex.GetType().Name}: {ex.Message}"); }
                }
                return result with { Persistence = unknownPersistence, StageResults = unknownStages, ChildOperations = childOperations, Events = result.Events.Concat(childEvents).ToArray() };
            }

            var exactCustomFrames = period is { Lifecycle: "COMPLETED", Reports.Count: > 0 } && period.Reports.All(r => r.Provenance is "REAL_WASAPI_INITIALIZE_ONLY" or "TEST_FAKE_PERIOD_BACKEND")
                ? period.Reports.Where(r => r.Candidates.Count == 1 && r.Candidates[0].Classification == "ACCEPTED")
                    .GroupBy(r => r.Candidates[0].RequestedFrames).Where(g => g.Select(r => r.Direction).Distinct(StringComparer.Ordinal).Count() == 2)
                    .Select(g => g.Key).Distinct().OrderBy(frames => frames).Take(ExclusivePeriodDiscoveryPlanning.MaximumCandidates).ToArray()
                : [];
            if (!budgetExceeded && exactCustomFrames.Length > 0 && preparation.Plan.Target is { Capture: { } shortCapture, Render: { } shortRender, PairIdentity: ScanPairIdentity.OperatorSelectedPair })
            {
                var customPersistenceFailed = false;
                try
                {
                    var customWorkflow = new CustomPeriodDuplexWorkerWorkflow(worker);
                    foreach (var frames in exactCustomFrames)
                    {
                        if (operationBudgetUsed + 1 > preparation.MaximumTotalOperations) throw new ScanBudgetExceededException("Custom-period Duplex exceeds the approved operation budget; fresh operator consent is required.");
                        cancellationToken.ThrowIfCancellationRequested();
                        var customPreparation = customWorkflow.Prepare(preparation.Discovery, shortCapture, shortRender, period!, checked((int)frames));
                        var custom = await customWorkflow.RunApprovedAsync(customPreparation, DerivedConfirmationId(confirmationId, "short-custom-duplex", checked((int)frames)), cancellationToken).ConfigureAwait(false);
                        childOperations.Add(ToCustomChild(custom, frames));
                        EmitChild(new(++orchestrationSequence, DateTimeOffset.UtcNow, ScanStage.ShortCustomDuplex,
                            custom.Lifecycle == "UNKNOWN" ? ScanOperationLifecycle.Unknown : custom.Lifecycle == "COMPLETED" ? ScanOperationLifecycle.Completed : ScanOperationLifecycle.Failed,
                            $"Custom Duplex · {custom.Period}", null, custom.OperationId));
                        customPersistenceFailed |= custom.PersistenceStatus != "SAVED_AND_RELOADED" && adapter.Mode == ScanExecutionMode.RealWorker;
                        operationBudgetUsed++;
                        if (custom.Lifecycle != "COMPLETED") throw new InvalidOperationException(custom.Error ?? "Custom Duplex did not complete.");
                    }
                    shortDuplexStage = new(AutomaticScanStage.SHORT_DUPLEX, customPersistenceFailed ? AutomaticScanStageState.FAILED : AutomaticScanStageState.COMPLETED,
                        customPersistenceFailed ? "Custom Duplex executed, but one or more reports were not persisted." : $"{exactCustomFrames.Length} exact custom Duplex operations completed.");
                }
                catch (OperationCanceledException)
                {
                    shortDuplexStage = new(AutomaticScanStage.SHORT_DUPLEX, AutomaticScanStageState.UNKNOWN, "Custom-period Duplex cancellation left the active outcome unresolved.");
                }
                catch (WorkerCommandException ex)
                {
                    shortDuplexStage = new(AutomaticScanStage.SHORT_DUPLEX, AutomaticScanStageState.UNKNOWN, $"Custom-period Duplex Worker failure: {ex.ErrorCode}.");
                }
                catch (ScanBudgetExceededException ex)
                {
                    budgetExceeded = true;
                    shortDuplexStage = new(AutomaticScanStage.SHORT_DUPLEX, AutomaticScanStageState.FAILED, ex.Message);
                }
                catch (Exception ex)
                {
                    budgetExceeded |= ex is ScanBudgetExceededException;
                    shortDuplexStage = new(AutomaticScanStage.SHORT_DUPLEX, AutomaticScanStageState.UNKNOWN, $"Custom-period Duplex failed closed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (shortDuplexStage.State == AutomaticScanStageState.UNKNOWN)
            {
                var stoppedStages = MergeStages(result.StageResults, periodStage, shortDuplexStage, new(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.NOT_RUN, "Stopped after UNKNOWN custom-period Duplex."));
                ScanPersistenceResult? stoppedPersistence = null;
                if (adapter.Mode == ScanExecutionMode.RealWorker)
                {
                    try { stoppedPersistence = DeviceConfigurationScanStorage.Save(result.SessionId, ScanRunLifecycle.Unknown, result.Target, result.Operations, result.DuplexAssessments, result.Recommendation, candidateInventory: result.CandidateInventory, stageResults: stoppedStages, childOperations: childOperations, budget: budget); }
                    catch (Exception ex) { stoppedPersistence = new("SAVE_FAILED", null, [], $"{ex.GetType().Name}: {ex.Message}"); }
                }
                Interlocked.Exchange(ref _mayBeActive, 1);
                return result with { Lifecycle = ScanRunLifecycle.Unknown, Persistence = stoppedPersistence, StageResults = stoppedStages, ChildOperations = childOperations, Events = result.Events.Concat(childEvents).ToArray(), Summary = result.Summary + " Custom-period Duplex stopped the scan because its outcome is UNKNOWN." };
            }

            if (!budgetExceeded && preparation.Plan.Target is { Capture: { } repeatCapture, Render: { } repeatRender, PairIdentity: ScanPairIdentity.OperatorSelectedPair } &&
                worker.Capabilities.Contains("RealExclusiveDuplexValidation", StringComparer.Ordinal))
            {
                try
                {
                    var repeatWorkflow = new DuplexRepeatabilityWorkerWorkflow(worker);
                    if (operationBudgetUsed + DuplexRepeatabilityCoordinator.MaximumRuns > preparation.MaximumTotalOperations)
                        throw new ScanBudgetExceededException("MINIMUM/DEFAULT repeatability exceeds the approved operation budget; fresh operator consent is required.");
                    var repeatPreparation = repeatWorkflow.Prepare(preparation.Discovery, repeatCapture, repeatRender);
                    var repeat = await repeatWorkflow.RunApprovedAsync(repeatPreparation, DerivedConfirmationId(confirmationId, "repeatability-base", 0), childProgress =>
                    {
                        EmitChild(childProgress with { Stage = ScanStage.Repeatability, Sequence = ++orchestrationSequence });
                    }, cancellationToken).ConfigureAwait(false);
                    childOperations.AddRange(repeat.Runs.Select(run => ToRepeatabilityChild(run, repeat.ReportPath)));
                    repeatabilityRuns.AddRange(repeat.Runs);
                    operationBudgetUsed += repeat.Runs.Count(r => r.Lifecycle != ScanOperationLifecycle.Skipped);
                    repeatedPeriods.AddRange(repeat.Runs.Where(r => r.Lifecycle == ScanOperationLifecycle.Completed).Select(r => r.Period));
                    repeatabilityStage = new(AutomaticScanStage.REPEATABILITY,
                        repeat.Lifecycle == ScanRunLifecycle.Unknown ? AutomaticScanStageState.UNKNOWN : repeat.Lifecycle == ScanRunLifecycle.Completed ? AutomaticScanStageState.COMPLETED : AutomaticScanStageState.FAILED,
                        repeat.Summary);

                    if (repeat.Lifecycle != ScanRunLifecycle.Unknown && exactCustomFrames.Length > 0)
                    {
                        foreach (var frames in exactCustomFrames)
                        {
                            if (operationBudgetUsed + 5 > preparation.MaximumTotalOperations) throw new ScanBudgetExceededException("Custom repeatability exceeds the approved operation budget; fresh operator consent is required.");
                            cancellationToken.ThrowIfCancellationRequested();
                            var customPreparation = repeatWorkflow.PrepareCustom(preparation.Discovery, repeatCapture, repeatRender, period!, checked((int)frames));
                            var custom = await repeatWorkflow.RunCustomApprovedAsync(customPreparation, DerivedConfirmationId(confirmationId, "repeatability-custom", checked((int)frames)), childProgress =>
                            {
                                EmitChild(childProgress with { Stage = ScanStage.Repeatability, Sequence = ++orchestrationSequence });
                            }, cancellationToken).ConfigureAwait(false);
                            childOperations.AddRange(custom.Runs.Select(run => ToRepeatabilityChild(run, custom.ReportPath)));
                            repeatabilityRuns.AddRange(custom.Runs);
                            operationBudgetUsed += custom.Runs.Count(r => r.Lifecycle != ScanOperationLifecycle.Skipped);
                            repeatedPeriods.AddRange(custom.Runs.Where(r => r.Lifecycle == ScanOperationLifecycle.Completed).Select(r => r.Period));
                            if (custom.Lifecycle == ScanRunLifecycle.Unknown)
                            {
                                repeatabilityStage = new(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.UNKNOWN, custom.Summary);
                                break;
                            }
                        }
                    }
                    repeatabilityStage = RepeatabilityStage(repeatabilityRuns, repeat, childOperations);
                }
                catch (OperationCanceledException)
                {
                    repeatabilityStage = new(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.UNKNOWN, "Repeatability cancellation left the active outcome unresolved.");
                }
                catch (ScanBudgetExceededException ex)
                {
                    budgetExceeded = true;
                    repeatabilityStage = new(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.FAILED, ex.Message);
                }
                catch (RepeatabilityRegistrationUnknownException ex)
                {
                    repeatabilityStage = new(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.UNKNOWN, ex.Message);
                }
                catch (WorkerCommandException ex) when (ex.Command == "RegisterScanSession" && ex.ErrorCode == "BUSY")
                {
                    repeatabilityStage = new(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.FAILED,
                        $"RegisterScanSession отклонён: Worker занят (BUSY). {ex.Message}");
                }
                catch (WorkerCommandException ex) when (ex.Command == "RegisterScanSession")
                {
                    repeatabilityStage = new(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.FAILED,
                        $"RegisterScanSession получил подтверждённый отказ {ex.ErrorCode}. {ex.Message}");
                }
                catch (Exception ex)
                {
                    budgetExceeded |= ex is ScanBudgetExceededException;
                    repeatabilityStage = new(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.FAILED, $"Repeatability failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
                repeatabilityStage = new(AutomaticScanStage.REPEATABILITY, AutomaticScanStageState.SKIPPED, "An exact Capture/Render pair and Duplex capability are required.");

            var inventory = AutomaticScanPlanner.WithRuntimeResults(result.CandidateInventory, period, repeatedPeriods, repeatabilityRuns);
            var sustainedStage = repeatabilityStage.State is AutomaticScanStageState.FAILED or AutomaticScanStageState.UNKNOWN
                ? new AutomaticScanStageResult(AutomaticScanStage.SUSTAINED_VALIDATION, AutomaticScanStageState.NOT_RUN,
                    $"Sustained validation not started: Repeatability {repeatabilityStage.State}. {repeatabilityStage.Message}")
                : new AutomaticScanStageResult(AutomaticScanStage.SUSTAINED_VALIDATION, AutomaticScanStageState.NOT_RUN, "No qualified short-evidence candidates were available.");
            var sustainedResults = new List<SustainedDuplexResult>();
            var stopSustained = false;
            if (!budgetExceeded && preparation.Plan.Target is { Capture: { } sustainedCapture, Render: { } sustainedRender, PairIdentity: ScanPairIdentity.OperatorSelectedPair } &&
                worker.Capabilities.Contains("RealExclusiveSustainedDuplexValidation", StringComparer.Ordinal) &&
                repeatabilityStage.State is not (AutomaticScanStageState.UNKNOWN or AutomaticScanStageState.FAILED))
            {
                var shortlist = SustainedDuplexPlanning.Select(inventory, repeatabilityRuns, 3);
                if (shortlist.Count > 0)
                {
                    var qualified = new List<(SustainedDuplexCandidate Candidate, string Mmcss)>();
                    try
                    {
                        var sustainedWorkflow = new SustainedDuplexWorkerWorkflow(worker);
                        foreach (var candidate in shortlist)
                        {
                            foreach (var mmcss in new[] { "OFF", "PRO_AUDIO" })
                            {
                                if (operationBudgetUsed + 1 > preparation.MaximumTotalOperations) throw new ScanBudgetExceededException("Sustained screening exceeds the approved operation budget.");
                                var screening = await sustainedWorkflow.RunAsync(preparation.Discovery, sustainedCapture, sustainedRender, candidate, 60, mmcss, DerivedConfirmationId(confirmationId, $"sustained-screening-{mmcss}", sustainedResults.Count), (operationId, message) => EmitChild(new(++orchestrationSequence, DateTimeOffset.UtcNow, ScanStage.SustainedValidation, ScanOperationLifecycle.Running, message, "PAIR", operationId)), cancellationToken).ConfigureAwait(false);
                                sustainedResults.Add(screening);
                                childOperations.Add(ToSustainedChild(screening));
                                operationBudgetUsed++;
                                if (screening.Classification == "SUSTAINED_CLEAN" && screening.Lifecycle == "COMPLETED") qualified.Add((candidate, mmcss));
                                if (!CanContinueAfterSustained(screening)) { stopSustained = true; break; }
                            }
                            if (stopSustained) break;
                        }
                        if (stopSustained)
                            sustainedStage = new(AutomaticScanStage.SUSTAINED_VALIDATION, SustainedStopState(sustainedResults), "Sustained operation stopped the ladder; no further audio work was started.");
                        else if (sustainedResults.Any(r => r.Lifecycle == "UNKNOWN" || r.Classification is "UNKNOWN" or "INCOMPLETE" || r.PersistenceStatus == "SAVE_FAILED"))
                            sustainedStage = new(AutomaticScanStage.SUSTAINED_VALIDATION, SustainedStopState(sustainedResults), "Sustained screening returned unsafe or incomplete evidence; no subsequent sustained operation was started.");
                        else if (qualified.Count == 0)
                            sustainedStage = new(AutomaticScanStage.SUSTAINED_VALIDATION, AutomaticScanStageState.COMPLETED, "NO_QUALIFIED_SUSTAINED_CANDIDATE: OFF and PRO_AUDIO screening completed, but no exact CLEAN 60-second configuration qualified for longer validation.");
                        else
                        {
                            stopSustained = false;
                            foreach (var qualifiedConfiguration in OrderLongValidationCandidates(qualified).Take(2))
                            {
                                if (operationBudgetUsed + 1 > preparation.MaximumTotalOperations) throw new ScanBudgetExceededException("Sustained validation exceeds the approved operation budget.");
                                var sustained = await sustainedWorkflow.RunAsync(preparation.Discovery, sustainedCapture, sustainedRender, qualifiedConfiguration.Candidate, preparation.SustainedDurationSeconds, qualifiedConfiguration.Mmcss, DerivedConfirmationId(confirmationId, $"sustained-validation-{qualifiedConfiguration.Mmcss}", sustainedResults.Count), (operationId, message) => EmitChild(new(++orchestrationSequence, DateTimeOffset.UtcNow, ScanStage.SustainedValidation, ScanOperationLifecycle.Running, message, "PAIR", operationId)), cancellationToken).ConfigureAwait(false);
                                sustainedResults.Add(sustained);
                                childOperations.Add(ToSustainedChild(sustained));
                                operationBudgetUsed++;
                                if (!CanContinueAfterSustained(sustained)) { stopSustained = true; break; }
                            }
                            var unknown = sustainedResults.Any(r => r.Lifecycle == "UNKNOWN" || r.Classification == "UNKNOWN");
                            var failed = sustainedResults.Any(r => r.Lifecycle == "FAILED" || r.Classification is "SUSTAINED_FAILED" or "INCOMPLETE" || r.PersistenceStatus == "SAVE_FAILED");
                            sustainedStage = new(AutomaticScanStage.SUSTAINED_VALIDATION, stopSustained ? SustainedStopState(sustainedResults) : unknown ? AutomaticScanStageState.UNKNOWN : failed ? AutomaticScanStageState.FAILED : AutomaticScanStageState.COMPLETED, $"Executed {sustainedResults.Count} continuous operations; quality: {string.Join(", ", sustainedResults.GroupBy(r => $"{r.Classification}/{r.Mmcss}").Select(g => $"{g.Key}={g.Count()}"))}.");
                        }
                    }
                    catch (ScanBudgetExceededException ex) { budgetExceeded = true; sustainedStage = new(AutomaticScanStage.SUSTAINED_VALIDATION, AutomaticScanStageState.FAILED, ex.Message); }
                    catch (Exception ex) { sustainedStage = new(AutomaticScanStage.SUSTAINED_VALIDATION, AutomaticScanStageState.UNKNOWN, $"Sustained validation failed closed: {ex.GetType().Name}: {ex.Message}"); }
                }
                else sustainedStage = new(AutomaticScanStage.SUSTAINED_VALIDATION, AutomaticScanStageState.NOT_RUN, "NO_QUALIFIED_SUSTAINED_CANDIDATE: no exact RUNTIME_TESTED short-evidence shortlist was available.");
            }
            var finalLifecycle = sustainedStage.State == AutomaticScanStageState.UNKNOWN || repeatabilityStage.State == AutomaticScanStageState.UNKNOWN || periodStage.State == AutomaticScanStageState.UNKNOWN || shortDuplexStage.State == AutomaticScanStageState.UNKNOWN ? ScanRunLifecycle.Unknown :
                repeatabilityStage.State == AutomaticScanStageState.FAILED || periodStage.State == AutomaticScanStageState.FAILED || shortDuplexStage.State == AutomaticScanStageState.FAILED ? ScanRunLifecycle.Incomplete : result.Lifecycle;
            if (sustainedStage.State == AutomaticScanStageState.FAILED) finalLifecycle = ScanRunLifecycle.Incomplete;
            var finalRecommendation = new DuplexRecommendation("NONE", "NOT_CERTIFIED", null, null, ["FINAL_SELECTION = NOT_RUN; no final selection was executed."]);
            var finalSelectionStage = new AutomaticScanStageResult(AutomaticScanStage.FINAL_SELECTION, AutomaticScanStageState.NOT_RUN, "No sustained evidence was available for final selection.");
            if (sustainedResults.Count > 0 && !stopSustained && sustainedStage.State is not (AutomaticScanStageState.UNKNOWN or AutomaticScanStageState.FAILED))
            {
                finalRecommendation = SustainedDuplexSelection.Recommend(sustainedResults.Select(r => ToSustainedEvidence(r, inventory)));
                finalSelectionStage = sustainedStage.State == AutomaticScanStageState.UNKNOWN
                    ? new(AutomaticScanStage.FINAL_SELECTION, AutomaticScanStageState.UNKNOWN, "Final selection stopped because sustained evidence is UNKNOWN.")
                    : new(AutomaticScanStage.FINAL_SELECTION, AutomaticScanStageState.COMPLETED, string.Join(" ", finalRecommendation.Reasons));
            }
            else if (sustainedStage.State is AutomaticScanStageState.UNKNOWN or AutomaticScanStageState.FAILED)
            {
                finalSelectionStage = new(AutomaticScanStage.FINAL_SELECTION, sustainedStage.State == AutomaticScanStageState.UNKNOWN ? AutomaticScanStageState.UNKNOWN : AutomaticScanStageState.NOT_RUN, "Final selection requires a fully bounded sustained stage; partial evidence remains available for diagnostics.");
            }
            var mergedStages = MergeStages(result.StageResults, periodStage, shortDuplexStage, repeatabilityStage, sustainedStage, finalSelectionStage);
            var sustainedProfile = adapter.Mode == ScanExecutionMode.RealWorker && sustainedStage.State == AutomaticScanStageState.COMPLETED && finalSelectionStage.State == AutomaticScanStageState.COMPLETED && sustainedResults.Count > 0
                ? new SustainedProfilePersistenceInput(result.SessionId, sustainedResults.Select(r => ToSustainedEvidence(r, inventory)).ToArray(), finalRecommendation)
                : null;
            ScanPersistenceResult? persistence = null;
            if (adapter.Mode == ScanExecutionMode.RealWorker)
            {
                try
                {
                    persistence = DeviceConfigurationScanStorage.Save(result.SessionId, finalLifecycle, result.Target, result.Operations,
                        result.DuplexAssessments, finalRecommendation, candidateInventory: inventory,
                        stageResults: mergedStages, childOperations: childOperations, budget: budget, sustainedProfile: sustainedProfile);
                }
                catch (Exception ex) { persistence = new("SAVE_FAILED", null, [], $"{ex.GetType().Name}: {ex.Message}"); finalLifecycle = ScanRunLifecycle.Failed; }
            }
            var finalStages = persistence is null ? mergedStages : mergedStages.Select(s => s.Stage == AutomaticScanStage.SAVE ? new(s.Stage, persistence.Status == "SAVED_AND_RELOADED" ? AutomaticScanStageState.COMPLETED : AutomaticScanStageState.FAILED, persistence.Error ?? persistence.Status) : s).ToArray();
            return result with
            {
                Lifecycle = finalLifecycle,
                Persistence = persistence,
                CandidateInventory = inventory,
                StageResults = finalStages,
                Recommendation = finalRecommendation,
                ChildOperations = childOperations,
                Events = result.Events.Concat(childEvents).ToArray(),
                Summary = result.Summary + $" Period discovery: {periodStage.State}; repeatability: {repeatabilityStage.State}."
            };
        }
        catch (Exception ex)
        {
            if (session is { } token)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                try { await worker.CancelScanSessionAsync(token, cleanup.Token).ConfigureAwait(false); }
                catch { Interlocked.Exchange(ref _mayBeActive, 1); sharedOperation?.Keep(); }
            }
            else if (registrationAttempted && ex is not WorkerCommandException)
            {
                // Registration may have committed before its response was lost.
                Interlocked.Exchange(ref _mayBeActive, 1);
                sharedOperation?.Keep();
            }
            throw;
        }
        finally { sharedOperation?.Dispose(); Volatile.Write(ref _running, 0); }
    }

    private static AutomaticScanStageResult RepeatabilityStage(IReadOnlyList<DuplexRepeatabilityRun> runs, DuplexRepeatabilityResult series, IReadOnlyList<AutomaticScanChildOperation> children)
    {
        var unknown = runs.Any(r => r.Lifecycle == ScanOperationLifecycle.Unknown) || series.Lifecycle == ScanRunLifecycle.Unknown;
        var failed = runs.Any(r => r.Lifecycle == ScanOperationLifecycle.Failed) || series.Lifecycle == ScanRunLifecycle.Failed || children.Any(c => c.Stage == "REPEATABILITY" && c.CleanupStatus is "FAILED" or "UNKNOWN");
        var statuses = runs.Select(r => r.Validation is { Report: var report } && report.TryGetProperty("duplex", out var duplex) && duplex.TryGetProperty("status", out var status) ? status.GetString() : r.Lifecycle.ToString().ToUpperInvariant()).ToArray();
        var state = unknown ? AutomaticScanStageState.UNKNOWN : failed ? AutomaticScanStageState.FAILED : runs.Count == 0 ? AutomaticScanStageState.NOT_RUN : AutomaticScanStageState.COMPLETED;
        var quality = string.Join(", ", statuses.GroupBy(s => s ?? "UNKNOWN").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}"));
        return new(AutomaticScanStage.REPEATABILITY, state, $"Executed {runs.Count} repeatability operations; quality preserved: {quality}. Execution success is separate from audio quality.");
    }

    private static bool CanContinueAfterSustained(SustainedDuplexResult result)
    {
        var report = result.Validation?.Report ?? default;
        var ok = result.Lifecycle != "UNKNOWN" && result.Classification is not ("UNKNOWN" or "INCOMPLETE") && result.PersistenceStatus != "SAVE_FAILED" &&
            result.CleanupStatus is not ("UNKNOWN" or "FAILED") && report.ValueKind == System.Text.Json.JsonValueKind.Object &&
            report.TryGetProperty("operationId", out var operation) && Guid.TryParse(operation.GetString(), out var reportOperationId) && Guid.TryParse(result.OperationId, out var resultOperationId) && reportOperationId == resultOperationId &&
            report.TryGetProperty("reportId", out var reportId) && Guid.TryParse(reportId.GetString(), out _) &&
            (result.PersistenceStatus is "SAVED_AND_RELOADED" or "NOT_SAVED_SYNTHETIC") &&
            !HasTrue(report, "capture", "deviceInvalidated") && !HasTrue(report, "render", "deviceInvalidated");
        return ok;
    }

    private static IReadOnlyList<(SustainedDuplexCandidate Candidate, string Mmcss)> OrderLongValidationCandidates(IEnumerable<(SustainedDuplexCandidate Candidate, string Mmcss)> qualified) =>
        qualified.GroupBy(x => x.Candidate.Period, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => x.Mmcss == "PRO_AUDIO" ? 0 : 1).First())
            .OrderBy(x => x.Candidate.Frames)
            .ThenBy(x => x.Mmcss, StringComparer.Ordinal)
            .ToArray();

    private static bool HasTrue(System.Text.Json.JsonElement report, string sideName, string property) =>
        report.TryGetProperty(sideName, out var side) && side.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;

    private static IReadOnlyList<AutomaticScanStageResult> MergeStages(IReadOnlyList<AutomaticScanStageResult> existing,
        AutomaticScanStageResult period, AutomaticScanStageResult shortDuplex, AutomaticScanStageResult repeatability, AutomaticScanStageResult? sustained = null, AutomaticScanStageResult? finalSelection = null) =>
        existing.Select(stage => stage.Stage switch
        {
            AutomaticScanStage.PERIOD_DISCOVERY => period,
            AutomaticScanStage.SHORT_DUPLEX => shortDuplex,
            AutomaticScanStage.REPEATABILITY => repeatability,
            AutomaticScanStage.SUSTAINED_VALIDATION => sustained ?? stage,
            AutomaticScanStage.FINAL_SELECTION => finalSelection ?? stage,
            _ => stage
        }).ToArray();

    private static Guid DerivedConfirmationId(Guid parent, string stage, int ordinal)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{parent:D}\nS2.1\n{stage}\n{ordinal}"))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static AutomaticScanChildOperation ToChildOperation(ScanOperationResult operation)
    {
        var report = operation.Evidence?.Stream?.Report ?? operation.Evidence?.Duplex?.Report ?? default;
        var reportId = report.ValueKind == System.Text.Json.JsonValueKind.Object && report.TryGetProperty("reportId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() : null;
        var provenance = operation.Evidence?.FormatMatrix?.Provenance ?? (report.ValueKind == System.Text.Json.JsonValueKind.Object && report.TryGetProperty("provenance", out var p) ? p.GetString() : null) ?? "NOT_OBSERVED";
        return new(operation.Operation.Stage.ToString().ToUpperInvariant(), operation.Operation.OperationId, reportId, operation.Lifecycle.ToString().ToUpperInvariant(), provenance, operation.CleanupStatus, operation.Message, null, operation.Operation.Period, operation.Operation.EndpointId, operation.Operation.Direction);
    }

    private static AutomaticScanChildOperation ToCustomChild(CustomPeriodDuplexResult result, long frames)
    {
        var report = result.Report.GetValueOrDefault();
        var reportId = report.ValueKind == System.Text.Json.JsonValueKind.Object && report.TryGetProperty("reportId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() : null;
        var provenance = report.ValueKind == System.Text.Json.JsonValueKind.Object && report.TryGetProperty("provenance", out var p) ? p.GetString() ?? "NOT_OBSERVED" : "NOT_OBSERVED";
        var cleanup = report.ValueKind == System.Text.Json.JsonValueKind.Object && report.TryGetProperty("capture", out var capture) && capture.TryGetProperty("cleanup", out var cleanupValue) ? cleanupValue.GetString() ?? "UNKNOWN" : "UNKNOWN";
        return new("SHORT_CUSTOM_DUPLEX", result.OperationId, reportId, result.Lifecycle, provenance, cleanup, $"{result.PersistenceStatus}: {result.Error ?? "Custom-period Duplex evidence"}", result.ReportPath, result.Period, null, "PAIR");
    }

    private static AutomaticScanChildOperation ToRepeatabilityChild(DuplexRepeatabilityRun run, string? reportPath)
    {
        var report = run.Validation?.Report ?? default;
        var reportId = report.ValueKind == System.Text.Json.JsonValueKind.Object && report.TryGetProperty("reportId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() : null;
        var provenance = report.ValueKind == System.Text.Json.JsonValueKind.Object && report.TryGetProperty("provenance", out var p) ? p.GetString() ?? "NOT_OBSERVED" : "NOT_OBSERVED";
        return new("REPEATABILITY", string.IsNullOrWhiteSpace(run.OperationId) ? null : run.OperationId, reportId, run.Lifecycle.ToString().ToUpperInvariant(), provenance, run.CleanupStatus, run.Message, reportPath, run.Period, null, "PAIR");
    }

    private static AutomaticScanChildOperation ToSustainedChild(SustainedDuplexResult result)
    {
        var report = result.Validation?.Report ?? default;
        var reportId = report.ValueKind == System.Text.Json.JsonValueKind.Object && report.TryGetProperty("reportId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() : null;
        var provenance = report.ValueKind == System.Text.Json.JsonValueKind.Object && report.TryGetProperty("provenance", out var p) ? p.GetString() ?? "NOT_OBSERVED" : "NOT_OBSERVED";
        return new("SUSTAINED_VALIDATION", result.OperationId, reportId, result.Lifecycle, provenance, result.CleanupStatus, $"{result.Classification} · MMCSS {result.Mmcss} · persistence {result.PersistenceStatus}: {result.Message}", result.ReportPath, $"{result.Period}/{result.Mmcss}/{result.DurationSeconds}s", null, "PAIR");
    }

    private static SustainedDuplexEvidence ToSustainedEvidence(SustainedDuplexResult result, IReadOnlyList<AutomaticScanCandidate> inventory) =>
        new(result.OperationId, result.Period, result.Mmcss, result.DurationSeconds, result.Lifecycle, result.Classification, result.CleanupStatus, result.PersistenceStatus, result.Validation?.Report ?? default,
            GeometryFrames(inventory, result, "CAPTURE"), GeometryFrames(inventory, result, "RENDER"), result.ReportPath);

    private static int? GeometryFrames(IReadOnlyList<AutomaticScanCandidate> inventory, SustainedDuplexResult result, string direction)
    {
        if (result.Validation?.Report is not { ValueKind: System.Text.Json.JsonValueKind.Object } report ||
            !report.TryGetProperty("requested", out var requested) ||
            !requested.TryGetProperty("captureEndpointId", out var capture) || capture.GetString() is not { } captureId ||
            !requested.TryGetProperty("renderEndpointId", out var render) || render.GetString() is not { } renderId ||
            !requested.TryGetProperty("period", out var requestedPeriod) || requestedPeriod.GetString() != result.Period ||
            !requested.TryGetProperty("mmcss", out var requestedMmcss) || requestedMmcss.GetString() != result.Mmcss ||
            !requested.TryGetProperty("sampleRateHz", out var rate) || rate.GetInt32() != 48_000 ||
            !requested.TryGetProperty("encoding", out var encoding) || encoding.GetString() != "PCM16" ||
            !requested.TryGetProperty("channels", out var channels) || channels.GetInt32() != 2) return null;
        var candidates = inventory.Where(c => c.EndpointId == (c.Direction == "CAPTURE" ? captureId : renderId) && c.Direction is "CAPTURE" or "RENDER" && c.Period == result.Period &&
            c.State == AutomaticCandidateState.RUNTIME_TESTED && c.SampleRateHz == 48_000 && c.Encoding == "PCM16" && c.Channels == 2).ToArray();
        if (candidates.Length != 2 || !candidates.Any(c => c.Direction == "CAPTURE" && c.EndpointId == captureId) || !candidates.Any(c => c.Direction == "RENDER" && c.EndpointId == renderId)) return null;
        var candidate = candidates.Single(c => c.Direction == direction);
        if (candidate.Period100ns is not { } hns || hns <= 0) return null;
        var frames = ExclusivePeriodDiscoveryPlanning.Frames(hns, candidate.SampleRateHz);
        if (result.Period.StartsWith("CUSTOM:", StringComparison.Ordinal) &&
            (!int.TryParse(result.Period.Split(':') is [_, _, var tokenFrames] ? tokenFrames : null, out var authorizedFrames) || authorizedFrames != frames)) return null;
        return frames;
    }

    private static AutomaticScanStageState SustainedStopState(IReadOnlyList<SustainedDuplexResult> results) =>
        results.Any(r => r.Lifecycle == "UNKNOWN" || r.Classification is "UNKNOWN" or "INCOMPLETE") ? AutomaticScanStageState.UNKNOWN : AutomaticScanStageState.FAILED;

    private sealed class ScanBudgetExceededException(string message) : InvalidOperationException(message);
}
