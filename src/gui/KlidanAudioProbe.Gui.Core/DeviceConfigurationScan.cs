namespace KlidanAudioProbe.Gui.Core;

public enum DeviceScanStage { Idle, Preflight, PassiveQuery, CandidateSelection, ActiveInitialize, StreamValidation, ResultAggregation, Completed, Cancelling, Cancelled, Failed, Incomplete }
public sealed record DeviceScanConsent(string EndpointId, string Direction, string Fingerprint, Guid ConfirmationId);
public sealed record DeviceScanResult(string EndpointId, string Direction, RealFormatMatrixDto? Query, IReadOnlyList<RealFormatMatrixDto> Initializations, DeviceScanStage State, DeviceScanStage Stage, string Message)
{
    public IReadOnlyList<RealStreamValidationDto> StreamValidations { get; init; } = [];
    public bool StreamValidationRun => StreamValidations.Count != 0;
    public bool DuplexEvidenceRun => false;
    public string Recommendation => "Не сформирована: реальный запуск и стабильность потока не проверялись.";
}

public sealed record StreamValidationResultRow(string Period, string ActualBufferFrames, string InitializeOutcome, string StartOutcome, string FramesProcessedExpected, string FrameCoverage, string StreamQuality, string FaultCounters, string StopOutcome, string CleanupStatus, string FinalStatus, string Details)
{
    public static StreamValidationResultRow NotRun(string period) => new(period, "неизвестно", "NOT_RUN", "NOT_RUN", "неизвестно", "неизвестно", "NOT_RUN", "неизвестно", "NOT_RUN", "NOT_RUN", "NOT_RUN", "Проверка не запускалась.");
}

public static class DeviceConfigurationScan
{
    public const int MaximumInitializeOperations = 4;
    public const int MaximumDurationSeconds = 180;
    public static string Fingerprint(WindowsEndpointDto endpoint) => RealFormatConsent.Fingerprint(endpoint.EndpointId!, endpoint.Direction, 48_000, endpoint.Direction == "RENDER" ? 2 : 1, "C2_SCAN_PLAN_V1", "minimum+default;pcm16;48000;capture-1,2-render-2");

    public static async Task<DeviceScanResult> RunAsync(IWorkerClient worker, WindowsEndpointDto endpoint, DeviceScanConsent consent, Action<DeviceScanStage, string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(endpoint.EndpointId) || endpoint.DeviceState != "ACTIVE" || endpoint.Direction is not ("CAPTURE" or "RENDER")) throw new InvalidOperationException("Scan requires one exact active Windows endpoint.");
        if (worker.State.State != WorkerConnectionState.ConnectedFake || !worker.Capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal) || !worker.Capabilities.Contains("RealExclusiveFormatInitialize", StringComparer.Ordinal)) throw new InvalidOperationException("Connected worker lacks real format query or Initialize-only capability.");
        if (consent.EndpointId != endpoint.EndpointId || consent.Direction != endpoint.Direction || consent.Fingerprint != Fingerprint(endpoint) || consent.ConfirmationId == Guid.Empty) throw new InvalidOperationException("Scan consent does not match the immutable endpoint and plan.");

        RealFormatMatrixDto? query = null;
        var initializations = new List<RealFormatMatrixDto>(MaximumInitializeOperations);
        var stage = DeviceScanStage.Preflight;
        void SetStage(DeviceScanStage next, string message) { stage = next; progress?.Invoke(next, message); }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetStage(DeviceScanStage.PassiveQuery, "Пассивный запрос существующей матрицы; без Initialize.");
            var channels = endpoint.Direction == "CAPTURE" ? new[] { 2, 1 } : new[] { 2 };
            query = await worker.QueryFormatMatrixAsync(endpoint.EndpointId, endpoint.Direction, channels, cancellationToken).ConfigureAwait(false);
            if (query.Provenance != "REAL_WASAPI_QUERY" || query.ReportLevel != "PASSIVE_QUERY" || query.EndpointId != endpoint.EndpointId || query.Direction != endpoint.Direction) throw new InvalidDataException("Query response identity/provenance mismatch.");
            cancellationToken.ThrowIfCancellationRequested();
            SetStage(DeviceScanStage.CandidateSelection, "Отбор только 48 кГц PCM16 кандидатов AudioHost.");
            var plan = (from channelsCount in channels
                        from period in new[] { "minimum", "default" }
                        let row = RealFormatMatrixValidation.BuildUniqueRows(query, channels).Single(r => r.Format == "PCM16" && r.SampleRate == 48_000 && r.Channels == channelsCount)
                        let candidate = period == "minimum" ? row.MinimumCandidate : row.DefaultCandidate
                        where !row.IsConflict && row.DisplayStatus == "SUPPORTED_REPORTED" && candidate is { Status: "SUPPORTED_REPORTED", SupportStatus: "SUPPORTED_REPORTED" }
                        select (Channels: channelsCount, Period: period, Candidate: candidate!)).Take(MaximumInitializeOperations).ToArray();
            foreach (var item in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (worker.State.State != WorkerConnectionState.ConnectedFake) throw new IOException("Worker disconnected; remaining scan outcomes are unknown.");
                SetStage(DeviceScanStage.ActiveInitialize, $"Initialize-only · PCM16 · 48 кГц · {item.Channels} канал(а) · {item.Period.ToUpperInvariant()} ({initializations.Count + 1}/{plan.Length}).");
                var receipt = new RealFormatConsentDto(true, Guid.NewGuid(), RealFormatConsent.Fingerprint(endpoint.EndpointId, endpoint.Direction, 48_000, item.Channels, "PCM16", item.Period));
                var result = await worker.ValidateFormatMatrixAsync(endpoint.EndpointId, endpoint.Direction, 48_000, item.Channels, "PCM16", item.Period, receipt, cancellationToken).ConfigureAwait(false);
                if (result.Provenance != "REAL_WASAPI_INITIALIZE_ONLY" || result.ReportLevel != "INITIALIZE_ONLY" || result.EndpointId != endpoint.EndpointId || result.Direction != endpoint.Direction || result.Candidates.Count != 1 || result.Candidates[0] is not { } c || c.SampleRate != 48_000 || c.Channels != item.Channels || c.Encoding != "PCM16" || c.Period != item.Period)
                    throw new InvalidDataException("Initialize response does not match the consented scan candidate.");
                initializations.Add(result);
            }
            SetStage(DeviceScanStage.StreamValidation, "Недоступно: Start/servicing не подключены к GUI worker; stream, stability и Duplex остаются NOT_RUN.");
            SetStage(DeviceScanStage.ResultAggregation, "Объединение только подтверждённых query/Initialize evidence.");
            var message = plan.Length == 0 ? "Форматы PCM16 48 кГц не получили подтверждённую поддержку; stream не проверялся." : $"Query и Initialize завершены для {initializations.Count}/{plan.Length} кандидатов. Реальный поток не проверялся; рекомендация отсутствует.";
            SetStage(DeviceScanStage.Incomplete, message);
            return new(endpoint.EndpointId, endpoint.Direction, query, initializations.ToArray(), DeviceScanStage.Incomplete, DeviceScanStage.StreamValidation, message);
        }
        catch (OperationCanceledException)
        {
            SetStage(DeviceScanStage.Cancelled, "Операция отменена. Уже полученные query/Initialize evidence сохранены; незавершённый исход неизвестен.");
            return new(endpoint.EndpointId, endpoint.Direction, query, initializations.ToArray(), DeviceScanStage.Cancelled, stage, "Операция отменена. Незавершённый исход неизвестен.");
        }
        catch (Exception ex)
        {
            SetStage(DeviceScanStage.Incomplete, $"Сканирование прервано: {ex.GetType().Name}: {ex.Message}. Ранее полученные evidence сохранены.");
            return new(endpoint.EndpointId, endpoint.Direction, query, initializations.ToArray(), DeviceScanStage.Incomplete, stage, ex.Message);
        }
    }
}
