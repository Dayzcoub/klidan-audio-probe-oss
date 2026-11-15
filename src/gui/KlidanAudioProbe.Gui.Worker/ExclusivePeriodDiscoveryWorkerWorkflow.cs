using System.Diagnostics;
using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

public sealed class ExclusivePeriodDiscoveryWorkerWorkflow(NativeWorkerClient worker) : IExclusivePeriodDiscoveryWorkflow
{
    private const int OperationTimeoutSeconds = 20;
    private const int SessionSafetyMarginSeconds = 20;
    private const int SessionCloseWaitSeconds = NativeWorkerClient.SessionCloseTimeoutSeconds;
    private static readonly TimeSpan OperationWait = TimeSpan.FromSeconds(OperationTimeoutSeconds + 5);
    private static readonly TimeSpan SessionCloseWait = TimeSpan.FromSeconds(SessionCloseWaitSeconds);
    private int _running, _mayBeActive;
    public async Task<PeriodDiscoveryPreparation> PrepareAsync(WindowsEndpointDiscoveryDto discovery, WindowsEndpointDto capture, WindowsEndpointDto render, CancellationToken cancellationToken = default)
    {
        using var sharedOperation = worker.AcquireSharedOperation("period preflight");
        var expectedProvenance = worker.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal) ? "TEST_FAKE_FORMAT_BACKEND" : "REAL_WINDOWS_METADATA";
        if (discovery.Provenance != expectedProvenance || discovery.OperationState is not ("COMPLETE" or "PARTIAL") || capture.EndpointId is not { Length: > 0 } || render.EndpointId is not { Length: > 0 } || capture.Direction != "CAPTURE" || render.Direction != "RENDER" || capture.DeviceState != "ACTIVE" || render.DeviceState != "ACTIVE") throw new InvalidOperationException("Period discovery requires two exact active endpoints from current Windows discovery.");
        var captureQuery = await worker.QueryFormatMatrixAsync(capture.EndpointId, "CAPTURE", [2], cancellationToken).ConfigureAwait(false);
        var renderQuery = await worker.QueryFormatMatrixAsync(render.EndpointId, "RENDER", [2], cancellationToken).ConfigureAwait(false);
        var captureBounds = Bounds(captureQuery); var renderBounds = Bounds(renderQuery);
        var candidates = ExclusivePeriodDiscoveryPlanning.Generate(Math.Max(captureBounds.Minimum, renderBounds.Minimum), Math.Min(captureBounds.Default, renderBounds.Default));
        if (candidates.Count == 0) throw new InvalidOperationException("The exact Capture/Render pair has no bounded intermediate period candidates.");
        var periods = candidates.Select(c => c.AuthorizationValue).ToArray();
        var sessionLifetimeSeconds = checked(candidates.Count * 2 * OperationTimeoutSeconds + SessionCloseWaitSeconds + SessionSafetyMarginSeconds);
        var plan = new WorkerScanPlan([new(capture.EndpointId, "CAPTURE"), new(render.EndpointId, "RENDER")], ["InitializeExclusiveCustomPeriod"], [new(48_000, "PCM16", 2, periods, ["OFF"])], sessionLifetimeSeconds, 10, candidates.Count * 2);
        var planText = $"Capture: {capture.FriendlyNameDisplay} · ID {capture.EndpointId}\nRender: {render.FriendlyNameDisplay} · ID {render.EndpointId}\n48 кГц · PCM16 · 2 канала · Exclusive · Initialize-only · MMCSS OFF\nКандидаты: {string.Join(", ", candidates.Select(c => c.Display))}\nWorker session: {plan.LifetimeSeconds} с · максимум {plan.MaximumOperationCount} операций · timeout операции {OperationTimeoutSeconds} с. Start, PCM, Stream, Duplex, RTL и AudioHost не выполняются.";
        return new(discovery, capture, render, candidates, plan, ExclusivePeriodDiscoveryPlanning.Fingerprint(discovery), planText);
    }

    public async Task<PeriodDiscoverySeriesResult> RunApprovedAsync(PeriodDiscoveryPreparation preparation, Guid confirmationId, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (confirmationId == Guid.Empty) throw new ArgumentException("A non-empty operator confirmation is required.", nameof(confirmationId));
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("Period discovery is already running.");
        var reports = new List<ExclusivePeriodDiscoveryReportDto>(); var messages = new List<string>(); var lifecycle = "COMPLETED";
        var operationDiagnostics = new List<PeriodDiscoveryOperationDiagnostic>();
        var terminationReason = "COMPLETED";
        string? exceptionType = null;
        string? workerErrorCode = null;
        int? failedOperationIndex = null;
        string? lastOperationId = null;
        var sessionCloseStatus = "NOT_ATTEMPTED";
        WorkerScanSessionToken? session = null;
        NativeWorkerClient.SharedOperationLease? sharedOperation = null;
        var registrationAttempted = false;
        var registrationRejected = false;
        try
        {
            sharedOperation = worker.AcquireSharedOperation("period discovery");
            if (Volatile.Read(ref _mayBeActive) != 0) throw new InvalidOperationException("Previous period discovery cleanup is UNKNOWN; restart the Worker before retrying.");
            if (worker.State.State != WorkerConnectionState.ConnectedFake) throw new IOException("Worker disconnected before period discovery registration.");
            cancellationToken.ThrowIfCancellationRequested();
            registrationAttempted = true;
            session = await worker.RegisterScanSessionAsync(preparation.WorkerPlan, true, confirmationId, cancellationToken).ConfigureAwait(false);
            var stop = false;
            var operationIndex = 0;
            foreach (var candidate in preparation.Candidates)
            {
                foreach (var endpoint in new[] { preparation.Capture, preparation.Render })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    operationIndex++;
                    var operationId = Guid.NewGuid(); lastOperationId = operationId.ToString("D");
                    var message = $"{endpoint.Direction} · {candidate.Display}"; messages.Add(message);
                    try { progress?.Invoke(message); } catch (Exception ex) { messages.Add($"Progress callback ignored: {ex.GetType().Name}: {ex.Message}"); }
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        var operation = worker.InitializeExclusiveCustomPeriodAsync(session, operationId, endpoint.EndpointId!, endpoint.Direction, checked((int)candidate.Period100ns), candidate.Frames, cancellationToken);
                        var report = await operation.WaitAsync(OperationWait).ConfigureAwait(false);
                        reports.Add(report);
                        var item = report.Candidates[0];
                        operationDiagnostics.Add(new(operationIndex, lastOperationId, endpoint.EndpointId!, endpoint.Direction, item.Classification, stopwatch.ElapsedMilliseconds, OperationTimeoutSeconds));
                        if (item.Classification is "UNKNOWN" or "INCOMPLETE" || item.CleanupStatus != "RELEASE_ATTEMPTED")
                        {
                            terminationReason = "UNSAFE_REPORT";
                            failedOperationIndex = operationIndex;
                            lifecycle = "UNKNOWN";
                            stop = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        failedOperationIndex ??= operationIndex;
                        operationDiagnostics.Add(new(operationIndex, lastOperationId, endpoint.EndpointId!, endpoint.Direction, "UNKNOWN", stopwatch.ElapsedMilliseconds, OperationTimeoutSeconds, ex.GetType().Name, ex is WorkerCommandException command ? command.ErrorCode : null));
                        throw;
                    }
                }
                if (stop) break;
            }
        }
        catch (OperationCanceledException ex) { lifecycle = "UNKNOWN"; terminationReason = "CANCELLED"; exceptionType = ex.GetType().Name; messages.Add("Cancellation did not confirm that all native state was closed."); }
        catch (WorkerCommandException ex)
        {
            exceptionType = ex.GetType().Name; workerErrorCode = ex.ErrorCode;
            if (session is null && registrationAttempted && ex.Command == "RegisterScanSession")
            {
                registrationRejected = true; lifecycle = "FAILED"; terminationReason = ex.ErrorCode == "BUSY" ? "REGISTRATION_BUSY" : "REGISTRATION_REJECTED";
                messages.Add($"Worker confirmed registration rejection {ex.ErrorCode}: {ex.Message}");
            }
            else { lifecycle = "UNKNOWN"; terminationReason = "WORKER_ERROR"; messages.Add($"UNKNOWN: Worker {ex.ErrorCode}: {ex.Message}"); }
        }
        catch (TimeoutException ex) { lifecycle = "UNKNOWN"; terminationReason = "IPC_TIMEOUT"; exceptionType = ex.GetType().Name; messages.Add($"UNKNOWN: IPC timeout after {OperationTimeoutSeconds} seconds."); }
        catch (IOException ex) { lifecycle = "UNKNOWN"; terminationReason = "WORKER_TRANSPORT"; exceptionType = ex.GetType().Name; messages.Add($"UNKNOWN: Worker transport: {ex.Message}"); }
        catch (InvalidDataException ex) { lifecycle = "UNKNOWN"; terminationReason = "REPORT_INVALID"; exceptionType = ex.GetType().Name; messages.Add($"UNKNOWN: invalid period report: {ex.Message}"); }
        catch (Exception ex) { lifecycle = "UNKNOWN"; terminationReason = "WORKFLOW_ERROR"; exceptionType = ex.GetType().Name; messages.Add($"UNKNOWN: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            if (session is { } token)
            {
                try
                {
                    using var closeCancellation = new CancellationTokenSource(SessionCloseWait);
                    var close = worker.CloseScanSessionAsync(token, closeCancellation.Token);
                    await close.WaitAsync(SessionCloseWait).ConfigureAwait(false);
                    sessionCloseStatus = "CONFIRMED";
                }
                catch (WorkerCommandException ex) { lifecycle = "UNKNOWN"; sessionCloseStatus = ex.ErrorCode == "BUSY" ? "BUSY" : "FAILED"; if (exceptionType is null) { terminationReason = "SESSION_CLOSE_FAILED"; exceptionType = ex.GetType().Name; workerErrorCode = ex.ErrorCode; } Volatile.Write(ref _mayBeActive, 1); sharedOperation?.Keep(); messages.Add($"UNKNOWN session close: Worker {ex.ErrorCode}."); }
                catch (Exception ex) { lifecycle = "UNKNOWN"; sessionCloseStatus = worker.State.State == WorkerConnectionState.Disconnected ? "WORKER_DISCONNECTED" : "FAILED"; if (exceptionType is null) { terminationReason = "SESSION_CLOSE_FAILED"; exceptionType = ex.GetType().Name; } Volatile.Write(ref _mayBeActive, 1); sharedOperation?.Keep(); messages.Add($"UNKNOWN session close: {ex.GetType().Name}: {ex.Message}"); }
            }
            else if (registrationAttempted && !registrationRejected) { lifecycle = "UNKNOWN"; terminationReason = "REGISTRATION_OUTCOME_UNKNOWN"; sessionCloseStatus = "NOT_ATTEMPTED_REGISTRATION_UNKNOWN"; Volatile.Write(ref _mayBeActive, 1); sharedOperation?.Keep(); messages.Add("UNKNOWN: Worker registration may have committed before its response was lost."); }
            if (lifecycle == "UNKNOWN") { Volatile.Write(ref _mayBeActive, 1); sharedOperation?.Keep(); }
            sharedOperation?.Dispose();
            Volatile.Write(ref _running, 0);
        }
        var diagnostics = new PeriodDiscoveryTerminationDiagnostics(terminationReason, exceptionType, workerErrorCode, failedOperationIndex,
            lastOperationId, sessionCloseStatus, OperationTimeoutSeconds, operationDiagnostics);
        return Persist(lifecycle, reports, messages, diagnostics);
    }

    private PeriodDiscoverySeriesResult Persist(string lifecycle, IReadOnlyList<ExclusivePeriodDiscoveryReportDto> reports, IReadOnlyList<string> messages,
        PeriodDiscoveryTerminationDiagnostics diagnostics)
    {
        var matrix = BuildMatrix(reports); string? path = null; var persistence = "NOT_SAVED";
        try { path = ExclusivePeriodDiscoverySeriesJson.SaveNoOverwrite(lifecycle, reports, matrix, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "PeriodDiscovery"), diagnostics); persistence = "SAVED_READ_BACK_VALIDATED"; }
        catch (Exception ex) { persistence = $"SAVE_FAILED: {ex.GetType().Name}: {ex.Message}"; }
        return new(lifecycle, reports, messages, matrix, path, persistence, diagnostics);
    }

    private static (long Minimum, long Default) Bounds(RealFormatMatrixDto matrix)
    {
        var candidates = matrix.Candidates.Where(c => c.SampleRate == 48_000 && c.Channels == 2 && c.Encoding == "PCM16").ToArray();
        var minimum = candidates.FirstOrDefault(c => c.Period == "minimum")?.MinimumPeriod100ns;
        var @default = candidates.FirstOrDefault(c => c.Period == "default")?.DefaultPeriod100ns;
        if (minimum is not > 0 || @default is not > 0) throw new InvalidOperationException("Passive preflight did not provide endpoint-specific minimum/default periods.");
        return (minimum.Value, @default.Value);
    }

    private static string BuildMatrix(IReadOnlyList<ExclusivePeriodDiscoveryReportDto> reports)
    {
        var rows = reports.GroupBy(r => r.Candidates[0].RequestedFrames).OrderBy(g => g.Key).Select(group =>
        {
            var capture = group.FirstOrDefault(r => r.Direction == "CAPTURE")?.Candidates[0]; var render = group.FirstOrDefault(r => r.Direction == "RENDER")?.Candidates[0];
            var status = capture is null || render is null || capture.Classification is "UNKNOWN" or "INCOMPLETE" || render.Classification is "UNKNOWN" or "INCOMPLETE" ? "UNKNOWN" : capture.Classification == "ACCEPTED" && render.Classification == "ACCEPTED" ? "BOTH_EXACT" : capture.Classification == "ACCEPTED_ALIGNED" || render.Classification == "ACCEPTED_ALIGNED" ? "ALIGNED_REQUIRES_CONFIRMATION" : "NEITHER";
            return $"{group.Key} frames: {status} · Capture actual {capture?.ActualBufferFrames?.ToString() ?? "?"} · Render actual {render?.ActualBufferFrames?.ToString() ?? "?"}";
        });
        return string.Join("\n", rows);
    }
}
