using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace KlidanAudioProbe.Gui.Core;

public enum DuplexRepeatabilityClassification
{
    REPEATABLY_CLEAN,
    INTERMITTENT_ANOMALIES,
    REPEATABLY_ANOMALOUS,
    INSUFFICIENT_EVIDENCE
}

public sealed record DuplexRepeatabilityPeriodResult(string Period, DuplexRepeatabilityClassification Classification, int UsableObservations, IReadOnlyList<string> Statuses);
public sealed record DuplexRepeatabilityRun(int Sequence, string Period, string OperationId, ScanOperationLifecycle Lifecycle,
    string CleanupStatus, string Message, DuplexValidationDto? Validation);
public sealed record DuplexRepeatabilityCounts(int Planned, int Attempted, int Completed, int Failed, int Unknown, int Skipped);
public sealed record DuplexRepeatabilityResult(string SessionId, ScanRunLifecycle Lifecycle, ScanTarget Target,
    IReadOnlyList<DuplexRepeatabilityRun> Runs, IReadOnlyList<DuplexRepeatabilityPeriodResult> Periods,
    string Summary, string Provenance, string? ReportPath = null, string? PersistenceStatus = null, string? PersistenceError = null, DuplexRepeatabilityCounts? Counts = null);

public sealed record DuplexRepeatabilityPreparation(WindowsEndpointDiscoveryDto Discovery, ScanPlan Plan, WorkerScanPlan WorkerPlan,
    string DiscoveryFingerprint, int? WorkerProcessId, IReadOnlyList<string>? Periods = null, int PauseMilliseconds = 250)
{
    public static string Fingerprint(WindowsEndpointDiscoveryDto discovery) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(discovery))));
}

public sealed record DuplexRepeatabilityReadiness(bool CanPrepare, string Reason, bool EndpointsReady,
    bool WorkerReady, bool DiscoveryReady, bool BlockedByOperation);

public interface IDuplexRepeatabilityWorkflow
{
    Task<WindowsEndpointDiscoveryDto> RefreshEndpointsAsync(CancellationToken cancellationToken);
    DuplexRepeatabilityPreparation Prepare(WindowsEndpointDiscoveryDto discovery, WindowsEndpointDto capture, WindowsEndpointDto render);
    Task<DuplexRepeatabilityResult> RunApprovedAsync(DuplexRepeatabilityPreparation preparation, Guid confirmationId,
        Action<ScanProgressEvent>? progress = null, CancellationToken cancellationToken = default);
}

public interface ICustomDuplexRepeatabilityWorkflow
{
    DuplexRepeatabilityPreparation PrepareCustom(WindowsEndpointDiscoveryDto discovery, WindowsEndpointDto capture, WindowsEndpointDto render,
        PeriodDiscoverySeriesResult series, int frames);
    Task<DuplexRepeatabilityResult> RunCustomApprovedAsync(DuplexRepeatabilityPreparation preparation, Guid confirmationId,
        Action<ScanProgressEvent>? progress = null, CancellationToken cancellationToken = default);
}

public interface IDuplexRepeatabilityAdapter
{
    ScanExecutionMode Mode { get; }
    bool IsBoundTo(ScanPlan plan, ScanSessionAuthorization authorization);
    Task<ScanAdapterResult> ExecuteAsync(ScanOperation operation, ScanPlan plan, ScanSessionAuthorization authorization, CancellationToken cancellationToken);
    Task<ScanAdapterResult> CancelAsync(ScanOperation operation, ScanPlan plan, CancellationToken cancellationToken);
    Task FinishSessionAsync(bool cancel, CancellationToken cancellationToken);
}

public sealed class DuplexRepeatabilityCoordinator(IDuplexRepeatabilityAdapter adapter)
{
    public const int RunsPerPeriod = 3;
    public const int MaximumRuns = RunsPerPeriod * 2;
    public const int OperationTimeoutSeconds = 15;
    public const int SessionCleanupTimeoutSeconds = 8;
    private static readonly string[] InterleavedPeriods = ["MINIMUM", "DEFAULT", "MINIMUM", "DEFAULT", "MINIMUM", "DEFAULT"];

    public async Task<DuplexRepeatabilityResult> RunAsync(ScanPlan plan, ScanSessionAuthorization authorization,
        Action<ScanProgressEvent>? progress = null, CancellationToken cancellationToken = default)
        => await RunAsync(plan, authorization, InterleavedPeriods, 250, progress, cancellationToken).ConfigureAwait(false);

    public async Task<DuplexRepeatabilityResult> RunAsync(ScanPlan plan, ScanSessionAuthorization authorization,
        IReadOnlyList<string> periods, int pauseMilliseconds, Action<ScanProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(adapter);
        if (plan.Target is not { Capture: { } capture, Render: { } render, PairIdentity: ScanPairIdentity.OperatorSelectedPair } || !plan.Capabilities.Duplex)
            throw new InvalidOperationException("Repeatability requires one exact operator-selected Capture/Render pair.");
        if (plan.Settings.DurationSeconds != 5 || plan.Settings.MmcssMode != "OFF")
            throw new InvalidOperationException("Repeatability is bounded to 5-second PCM16 stereo Duplex with MMCSS OFF.");
        if (periods is null || periods.Count is < 1 or > 24 || periods.Any(string.IsNullOrWhiteSpace) || pauseMilliseconds is < 0 or > 10_000)
            throw new InvalidOperationException("Repeatability period plan or bounded pause is invalid.");
        var stages = authorization.AllowedStages.ToHashSet();
        if (adapter.Mode == ScanExecutionMode.RealWorker && !adapter.IsBoundTo(plan, authorization))
            throw new InvalidOperationException("Repeatability Worker session is not bound to the exact pair and plan.");
        if (!authorization.Accepted || authorization.ConfirmationId == Guid.Empty || !stages.Contains(ScanStage.Duplex) ||
            authorization.PlanFingerprint != ScanSessionAuthorization.Fingerprint(plan, stages, authorization.MaximumDurationSeconds, authorization.MaximumOperationCount, authorization.AllowsPhysicalRtl, authorization.AllowsAudioHostActivation) ||
            authorization.MaximumDurationSeconds > 30 || authorization.MaximumOperationCount < periods.Count || authorization.MaximumOperationCount > 24 ||
            authorization.AllowsPhysicalRtl || authorization.AllowsAudioHostActivation)
            throw new InvalidOperationException("Repeatability authorization is missing, stale, or outside the bounded operation scope.");

        var runs = new List<DuplexRepeatabilityRun>(periods.Count);
        var lifecycle = ScanRunLifecycle.Completed;
        long sequence = 0;
        var reportIds = new HashSet<Guid>();
        var progressFailed = false;
        var sessionId = Guid.NewGuid().ToString("D");
        void Emit(ScanOperation operation, ScanOperationLifecycle state, string message)
        {
            var item = new ScanProgressEvent(++sequence, DateTimeOffset.UtcNow, ScanStage.Duplex, state, message, operation.EndpointId, operation.OperationId);
            if (!progressFailed && progress is not null) try { progress(item); } catch { progressFailed = true; }
        }

        try
        {
            for (var index = 0; index < periods.Count; index++)
            {
                var period = periods[index];
                var operation = new ScanOperation(ScanStage.Duplex, "PAIR", "PAIR", "PCM16", 48000, 2, period, plan.Settings.DurationSeconds,
                    Guid.NewGuid().ToString("D"), plan.Settings.MmcssMode, capture.EndpointId, render.EndpointId);
                Emit(operation, ScanOperationLifecycle.Pending, $"Повторяемость: {period} · запуск {index + 1}/{periods.Count}.");
                if (cancellationToken.IsCancellationRequested)
                {
                    lifecycle = ScanRunLifecycle.Cancelled;
                    AddSkippedRuns(runs, periods, "Серия отменена до запуска операции.");
                    Emit(operation, ScanOperationLifecycle.Skipped, "Серия отменена до запуска операции; остальные операции не запускались.");
                    break;
                }
                Emit(operation, ScanOperationLifecycle.Running, $"Duplex {period} · 48 кГц PCM16 · 5 с · MMCSS OFF.");
                ScanAdapterResult result;
                DuplexValidationDto? validation;
                using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                operationTimeout.CancelAfter(TimeSpan.FromSeconds(OperationTimeoutSeconds));
                try
                {
                    var operationTask = adapter.ExecuteAsync(operation, plan, authorization, operationTimeout.Token);
                    result = await operationTask.WaitAsync(operationTimeout.Token).ConfigureAwait(false);
                    validation = result.Evidence?.Duplex;
                    ValidateRun(operation, validation, adapter.Mode, reportIds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lifecycle = ScanRunLifecycle.Unknown;
                    runs.Add(new(index + 1, period, operation.OperationId, ScanOperationLifecycle.Unknown, "CANCEL_REQUESTED", "Отмена запрошена, но подтверждение остановки native operation не получено.", null));
                    Emit(operation, ScanOperationLifecycle.Unknown, "Отмена запрошена; native operation остаётся UNKNOWN.");
                    AddSkippedRuns(runs, periods, "Серия остановлена: native operation не подтверждена.");
                    break;
                }
                catch (OperationCanceledException) when (operationTimeout.IsCancellationRequested)
                {
                    lifecycle = ScanRunLifecycle.Unknown;
                    runs.Add(new(index + 1, period, operation.OperationId, ScanOperationLifecycle.Unknown, "UNKNOWN", "Worker operation timeout; native termination is unresolved.", null));
                    Emit(operation, ScanOperationLifecycle.Unknown, "Тайм-аут Worker; исход native operation UNKNOWN.");
                    AddSkippedRuns(runs, periods, "Серия остановлена: native operation может оставаться активной.");
                    break;
                }
                catch (Exception ex)
                {
                    lifecycle = ScanRunLifecycle.Unknown;
                    runs.Add(new(index + 1, period, operation.OperationId, ScanOperationLifecycle.Unknown, "UNKNOWN", $"Repeatability operation outcome is unknown: {ex.GetType().Name}: {ex.Message}", null));
                    Emit(operation, ScanOperationLifecycle.Unknown, "Исход операции UNKNOWN; серия остановлена.");
                    AddSkippedRuns(runs, periods, "Серия остановлена: Worker/native operation не подтверждена.");
                    break;
                }
                var unsafeCleanup = result.CleanupStatus.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) || result.CleanupStatus.Contains("FAILED", StringComparison.OrdinalIgnoreCase) || !result.CanSafelyContinue || result.Lifecycle == ScanOperationLifecycle.Unknown;
                var runState = unsafeCleanup ? ScanOperationLifecycle.Unknown : result.Lifecycle;
                var run = new DuplexRepeatabilityRun(index + 1, period, operation.OperationId, runState, result.CleanupStatus, result.Message, validation);
                runs.Add(run);
                Emit(operation, runState, result.Message);
                if (unsafeCleanup || !result.WorkerConnected || !result.DevicePresent || !result.IdentityCurrent)
                {
                    lifecycle = ScanRunLifecycle.Unknown;
                    AddSkippedRuns(runs, periods, "Серия остановлена: исход Duplex или cleanup не подтверждён.");
                    break;
                }
                if (runState is ScanOperationLifecycle.Failed)
                    lifecycle = ScanRunLifecycle.Incomplete;
                if (index + 1 < periods.Count && pauseMilliseconds > 0)
                {
                    try { await Task.Delay(pauseMilliseconds, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        lifecycle = ScanRunLifecycle.Cancelled;
                        AddSkippedRuns(runs, periods, "Серия отменена во время bounded pause.");
                        break;
                    }
                }
            }
        }
        finally
        {
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(SessionCleanupTimeoutSeconds));
                await adapter.FinishSessionAsync(lifecycle is ScanRunLifecycle.Cancelled or ScanRunLifecycle.Unknown, cleanupTimeout.Token)
                    .WaitAsync(cleanupTimeout.Token).ConfigureAwait(false);
            }
            catch { lifecycle = ScanRunLifecycle.Unknown; }
        }

        var periodResults = periods.Distinct(StringComparer.Ordinal).Select(period => Classify(period, runs, periods.Count(p => p == period))).ToArray();
        if (runs.Count < periods.Count && lifecycle == ScanRunLifecycle.Completed) lifecycle = ScanRunLifecycle.Incomplete;
        var counts = new DuplexRepeatabilityCounts(periods.Count, runs.Count(r => !string.IsNullOrWhiteSpace(r.OperationId)), runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Completed), runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Failed), runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Unknown), runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Skipped));
        return new(sessionId, lifecycle, new(capture, render, ScanPairIdentity.OperatorSelectedPair), runs.ToArray(), periodResults,
            $"План: {counts.Planned}; attempted: {counts.Attempted}; completed: {counts.Completed}; failed: {counts.Failed}; unknown: {counts.Unknown}; skipped: {counts.Skipped}. {string.Join("; ", periodResults.Select(p => $"{p.Period}: {p.Classification}"))}",
            adapter.Mode == ScanExecutionMode.RealWorker ? "REAL_WASAPI_DUPLEX" : "TEST_FAKE_WASAPI_DUPLEX", Counts: counts);

        void AddSkippedRuns(List<DuplexRepeatabilityRun> target, IReadOnlyList<string> plannedPeriods, string message)
        {
            for (var skipped = target.Count + 1; skipped <= plannedPeriods.Count; skipped++)
                target.Add(new(skipped, plannedPeriods[skipped - 1], string.Empty, ScanOperationLifecycle.Skipped, "NOT_ATTEMPTED", message, null));
        }
    }

    private static DuplexRepeatabilityPeriodResult Classify(string period, IReadOnlyList<DuplexRepeatabilityRun> runs, int requiredObservations)
    {
        var observations = runs.Where(r => r.Period == period && r.Lifecycle == ScanOperationLifecycle.Completed && r.Validation is not null && r.Validation.Report.GetProperty("duplex").GetProperty("status").GetString() is not ("CANCELLED" or "INCOMPLETE")).ToArray();
        var statuses = runs.Where(r => r.Period == period).Select(r => r.Validation?.Report.GetProperty("duplex").GetProperty("status").GetString() ?? r.Lifecycle.ToString().ToUpperInvariant()).ToArray();
        if (observations.Length < requiredObservations) return new(period, DuplexRepeatabilityClassification.INSUFFICIENT_EVIDENCE, observations.Length, statuses);
        var clean = observations.Count(r => r.Validation!.Report.GetProperty("duplex").GetProperty("status").GetString() == "DUPLEX_CLEAN");
        return new(period, clean == requiredObservations ? DuplexRepeatabilityClassification.REPEATABLY_CLEAN : clean == 0 ? DuplexRepeatabilityClassification.REPEATABLY_ANOMALOUS : DuplexRepeatabilityClassification.INTERMITTENT_ANOMALIES, observations.Length, statuses);
    }

    private static void ValidateRun(ScanOperation operation, DuplexValidationDto? validation, ScanExecutionMode mode, HashSet<Guid> reportIds)
    {
        if (validation is null || validation.OperationId != operation.OperationId || validation.CaptureEndpointId != operation.CaptureEndpointId || validation.RenderEndpointId != operation.RenderEndpointId)
            throw new InvalidDataException("Repeatability Duplex identity does not match the immutable operation.");
        var report = validation.Report;
        if (!report.TryGetProperty("reportId", out var reportIdElement) || !reportIdElement.TryGetGuid(out var reportId) || !reportIds.Add(reportId))
            throw new InvalidDataException("Repeatability Duplex report ID is missing or duplicated.");
        var provenance = report.GetProperty("provenance").GetString();
        var expected = mode == ScanExecutionMode.RealWorker ? "REAL_WASAPI_DUPLEX" : "TEST_FAKE_WASAPI_DUPLEX";
        var requested = report.GetProperty("requested");
        var status = report.GetProperty("duplex").GetProperty("status").GetString();
        if (provenance != expected || requested.GetProperty("captureEndpointId").GetString() != operation.CaptureEndpointId || requested.GetProperty("renderEndpointId").GetString() != operation.RenderEndpointId || requested.GetProperty("period").GetString() != operation.Period || requested.GetProperty("durationSeconds").GetInt32() != operation.DurationSeconds || requested.GetProperty("sampleRateHz").GetInt32() != 48000 || requested.GetProperty("channels").GetInt32() != 2 || requested.GetProperty("encoding").GetString() != "PCM16" || requested.GetProperty("mmcss").GetString() != "OFF" || status is not ("DUPLEX_CLEAN" or "DUPLEX_WARNINGS" or "FAILED" or "INCOMPLETE" or "CANCELLED"))
            throw new InvalidDataException("Repeatability Duplex report settings, provenance, or status do not match the immutable operation.");
    }
}

public sealed record DuplexRepeatabilityReport(int SchemaVersion, string ReportType, string SessionId, DateTimeOffset TimestampUtc,
    string Lifecycle, string Provenance, string CaptureEndpointId, string RenderEndpointId, int DurationSeconds, string MmcssMode,
    IReadOnlyList<DuplexRepeatabilityRun> Runs, IReadOnlyList<DuplexRepeatabilityPeriodResult> Periods,
    int? CompletedRuns = null, int? FailedRuns = null, int? UnknownRuns = null, int? SkippedRuns = null, int? PlannedRuns = null, int? AttemptedRuns = null);

public static class DuplexRepeatabilityReportStorage
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Save(DuplexRepeatabilityResult result, string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Runs.Count > DuplexRepeatabilityCoordinator.MaximumRuns || result.Target.Capture?.EndpointId is not { Length: > 0 } capture || result.Target.Render?.EndpointId is not { Length: > 0 } render)
            throw new InvalidDataException("Repeatability report requires one exact Capture/Render pair and at most six runs.");
        if (result.Provenance != "REAL_WASAPI_DUPLEX" || result.Runs.Any(r => r.Validation is { Report: var report } && report.GetProperty("provenance").GetString() != "REAL_WASAPI_DUPLEX"))
            throw new InvalidDataException("Synthetic Duplex evidence cannot enter a real repeatability report.");
        rootDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "Repeatability");
        Directory.CreateDirectory(rootDirectory);
        var path = Path.Combine(rootDirectory, $"duplex-repeatability-{result.SessionId}.json");
        if (File.Exists(path)) throw new IOException("A repeatability report with this session identity already exists.");
        var counts = result.Counts ?? new(result.Runs.Count, result.Runs.Count(r => !string.IsNullOrWhiteSpace(r.OperationId)), result.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Completed), result.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Failed), result.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Unknown), result.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Skipped));
        var report = new DuplexRepeatabilityReport(1, "wasapi-duplex-repeatability", result.SessionId, DateTimeOffset.UtcNow, result.Lifecycle.ToString().ToUpperInvariant(), result.Provenance, capture, render, 5, "OFF", result.Runs, result.Periods,
            counts.Completed, counts.Failed, counts.Unknown, counts.Skipped, counts.Planned, counts.Attempted);
        ValidateReport(report);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("Repeatability report exceeds the bounded size limit.");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, bytes);
        try { File.Move(temp, path); } catch { if (File.Exists(temp)) File.Delete(temp); throw; }
        _ = Load(path);
        return path;
    }

    public static DuplexRepeatabilityReport Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Repeatability report is missing or outside the bounded size limit.");
        var report = JsonSerializer.Deserialize<DuplexRepeatabilityReport>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("Repeatability report JSON is invalid.");
        ValidateReport(report);
        return report;
    }

    private static void ValidateReport(DuplexRepeatabilityReport report)
    {
        if (report.SchemaVersion != 1 || report.ReportType != "wasapi-duplex-repeatability" || !Guid.TryParse(report.SessionId, out _) ||
            report.Runs.Count > DuplexRepeatabilityCoordinator.MaximumRuns || report.Provenance != "REAL_WASAPI_DUPLEX" ||
            string.IsNullOrWhiteSpace(report.CaptureEndpointId) || string.IsNullOrWhiteSpace(report.RenderEndpointId) ||
            report.DurationSeconds != 5 || report.MmcssMode != "OFF")
            throw new InvalidDataException("Repeatability report contract validation failed.");

        var operationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reportIds = new HashSet<Guid>();
        foreach (var run in report.Runs)
        {
            if (string.IsNullOrWhiteSpace(run.OperationId))
            {
                if (run.Lifecycle != ScanOperationLifecycle.Skipped || run.Validation is not null) throw new InvalidDataException("Repeatability report contains an invalid skipped run.");
                continue;
            }
            if (!Guid.TryParse(run.OperationId, out _) || !operationIds.Add(run.OperationId))
                throw new InvalidDataException("Repeatability report contains a missing or duplicated operation ID.");
            if (run.Validation is null) continue;
            var validation = run.Validation;
            if (validation.OperationId != run.OperationId || validation.CaptureEndpointId != report.CaptureEndpointId || validation.RenderEndpointId != report.RenderEndpointId ||
                !validation.Report.TryGetProperty("reportId", out var reportIdElement) || !reportIdElement.TryGetGuid(out var reportId) || !reportIds.Add(reportId) ||
                validation.Report.GetProperty("provenance").GetString() != "REAL_WASAPI_DUPLEX")
                throw new InvalidDataException("Repeatability report contains mismatched or duplicated real evidence.");
            var requested = validation.Report.GetProperty("requested");
            if (requested.GetProperty("captureEndpointId").GetString() != report.CaptureEndpointId || requested.GetProperty("renderEndpointId").GetString() != report.RenderEndpointId ||
                requested.GetProperty("durationSeconds").GetInt32() != 5 || requested.GetProperty("sampleRateHz").GetInt32() != 48000 || requested.GetProperty("channels").GetInt32() != 2 ||
                requested.GetProperty("encoding").GetString() != "PCM16" || requested.GetProperty("mmcss").GetString() != "OFF" ||
                !IsAllowedPeriod(requested.GetProperty("period").GetString()))
                throw new InvalidDataException("Repeatability report contains evidence with invalid settings.");
        }

        static bool IsAllowedPeriod(string? period) => period is "MINIMUM" or "DEFAULT" || CustomDuplexPeriod.IsValidAuthorization(period);
        if (report.PlannedRuns is { } planned && report.AttemptedRuns is { } attempted && report.CompletedRuns is { } completed && report.FailedRuns is { } failed && report.UnknownRuns is { } unknown && report.SkippedRuns is { } skipped &&
            (planned < report.Runs.Count || attempted != report.Runs.Count(r => !string.IsNullOrWhiteSpace(r.OperationId)) || completed != report.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Completed) || failed != report.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Failed) || unknown != report.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Unknown) || skipped != report.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Skipped) || attempted + skipped != report.Runs.Count))
            throw new InvalidDataException("Repeatability aggregate counts do not match individual runs.");
    }
}
