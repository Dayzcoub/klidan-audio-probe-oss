using System.Text.Json;
using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

public sealed class CustomPeriodDuplexWorkerWorkflow(NativeWorkerClient worker) : ICustomPeriodDuplexWorkflow
{
    private int _running, _mayBeActive;

    public CustomPeriodDuplexPreparation Prepare(WindowsEndpointDiscoveryDto discovery, WindowsEndpointDto capture, WindowsEndpointDto render,
        PeriodDiscoverySeriesResult series, int frames)
    {
        if (worker.State.State != WorkerConnectionState.ConnectedFake) throw new InvalidOperationException("Worker is not connected.");
        WindowsEndpointDiscoveryValidation.Validate(discovery, worker.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal));
        var currentCapture = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == capture.EndpointId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE") ?? throw new InvalidOperationException("Capture endpoint is stale or inactive.");
        var currentRender = discovery.Endpoints.SingleOrDefault(e => e.EndpointId == render.EndpointId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE") ?? throw new InvalidOperationException("Render endpoint is stale or inactive.");
        if (!worker.Capabilities.Contains("RealExclusiveDuplexValidation", StringComparer.Ordinal) || !worker.Capabilities.Contains("ScanSessionAuthorizationV1", StringComparer.Ordinal)) throw new InvalidOperationException("Worker lacks authorized Duplex capabilities.");
        var synthetic = worker.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal);
        var period = CustomPeriodDuplexEvidence.Select(series, currentCapture.EndpointId!, currentRender.EndpointId!, frames, allowSynthetic: synthetic);
        var plan = new WorkerScanPlan([new(currentCapture.EndpointId!, "CAPTURE"), new(currentRender.EndpointId!, "RENDER")], ["ValidateDuplex"], [new(48_000, "PCM16", 2, [period.AuthorizationValue], ["OFF"])], 30, 10, 1);
        return new(discovery, currentCapture, currentRender, period, plan, ExclusivePeriodDiscoveryPlanning.Fingerprint(discovery),
            $"Capture: {currentCapture.FriendlyNameDisplay}\nRender: {currentRender.FriendlyNameDisplay}\n48 кГц · PCM16 · stereo · WASAPI Exclusive\nПериод: {period.Display}\nИсточник: {(synthetic ? "TEST_FAKE_PERIOD_BACKEND" : "REAL_WASAPI_INITIALIZE_ONLY")} · BOTH_EXACT · {series.Reports.Count} evidence reports\n5 секунд · MMCSS OFF · Capture discarded · Render silence only · routing disabled.\nОдна явно подтверждённая Duplex operation; sweep/repeatability не выполняются.");
    }

    public async Task<CustomPeriodDuplexResult> RunApprovedAsync(CustomPeriodDuplexPreparation preparation, Guid confirmationId, CancellationToken cancellationToken = default)
    {
        if (confirmationId == Guid.Empty) throw new ArgumentException("Operator approval identity is required.", nameof(confirmationId));
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("Custom Duplex is already running.");
        WorkerScanSessionToken? session = null; var operationId = Guid.NewGuid();
        NativeWorkerClient.SharedOperationLease? sharedOperation = null;
        CustomPeriodDuplexResult? result = null;
        var registrationAttempted = false;
        try
        {
            if (Volatile.Read(ref _mayBeActive) != 0) throw new InvalidOperationException("Previous custom Duplex cleanup is UNKNOWN; restart the Worker.");
            sharedOperation = worker.AcquireSharedOperation("custom period Duplex");
            registrationAttempted = true;
            session = await worker.RegisterScanSessionAsync(preparation.WorkerPlan, true, confirmationId, cancellationToken).ConfigureAwait(false);
            var validation = await worker.ValidateCustomDuplexInScanAsync(operationId, preparation.Capture.EndpointId!, preparation.Render.EndpointId!, preparation.Period.AuthorizationValue, 5, "OFF", session, operationId, cancellationToken).ConfigureAwait(false);
            var report = validation.Report;
            result = new CustomPeriodDuplexResult("COMPLETED", operationId.ToString("D"), preparation.Period.AuthorizationValue, null, "NOT_SAVED", report);
            if (report.GetProperty("provenance").GetString() != "REAL_WASAPI_DUPLEX")
                result = result with { PersistenceStatus = "NOT_SAVED_SYNTHETIC", Error = "Synthetic custom-period evidence remains local to the test run." };
            else
                try { var path = CustomPeriodDuplexReportStorage.Save(result); result = result with { ReportPath = path, PersistenceStatus = "SAVED_AND_RELOADED" }; } catch (Exception ex) { result = result with { PersistenceStatus = "SAVE_FAILED", Error = $"{ex.GetType().Name}: {ex.Message}" }; }
        }
        catch (OperationCanceledException) { Volatile.Write(ref _mayBeActive, 1); throw; }
        catch
        {
            if (session is null && registrationAttempted) { Volatile.Write(ref _mayBeActive, 1); sharedOperation?.Keep(); }
            throw;
        }
        finally
        {
            if (session is { } token)
            {
                try
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(NativeWorkerClient.SessionCloseTimeoutSeconds));
                    await worker.CloseScanSessionAsync(token, closeTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _mayBeActive, 1);
                    sharedOperation?.Keep();
                    if (result is not null)
                        result = result with { Lifecycle = "UNKNOWN", PersistenceStatus = "UNKNOWN_CLEANUP", Error = $"{result.Error ?? "Primary operation completed."} Cleanup was not confirmed: {ex.GetType().Name}: {ex.Message}" };
                }
            }
            sharedOperation?.Dispose();
            Volatile.Write(ref _running, 0);
        }
        return result ?? throw new InvalidOperationException("Custom Duplex did not produce a terminal result.");
    }

}
