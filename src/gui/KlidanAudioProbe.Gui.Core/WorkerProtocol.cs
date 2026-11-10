using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public enum WorkerConnectionState { Disconnected, Starting, Connecting, Handshaking, ConnectedFake, Stopping, Incompatible, Failed }
public sealed record WorkerStateDto(WorkerConnectionState State, string Message, int? ProcessId = null, int? ExitCode = null);
public sealed record WorkerEndpointDto(string EndpointId, string Name, string Direction, string State, int ChannelCount, StreamTopologyDto Topology, bool IsSynthetic, IReadOnlyList<FormatTopologyDto>? FormatTopologies = null);
public sealed record WindowsPropertyEvidenceDto(int Hresult, string State);
public sealed record WindowsSharedModeFormatDto(WindowsPropertyEvidenceDto Property, string ParseState, int? FormatTag, string? Encoding, int? ReportedChannelCount, uint? SampleRate, int? BitsPerSample, int? ValidBitsPerSample, string TopologyState, uint? SpeakerMask, string SpeakerMaskState);
public sealed record WindowsEndpointDto(string? EndpointId, WindowsPropertyEvidenceDto EndpointIdEvidence, string? FriendlyName, WindowsPropertyEvidenceDto FriendlyNameEvidence, string Direction, string DeviceState, uint? RawDeviceState, WindowsPropertyEvidenceDto DeviceStateEvidence, string FormFactor, WindowsPropertyEvidenceDto FormFactorEvidence, WindowsSharedModeFormatDto SharedModeFormat, string? ContainerId = null, WindowsPropertyEvidenceDto? ContainerIdEvidence = null)
{
    public string FriendlyNameDisplay => FriendlyNameEvidence.State == "AVAILABLE" && !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName : Localization.Get("VmEndpointNameUnavailable");
    public string DirectionLabel => Direction == "CAPTURE" ? "Capture · вход" : Direction == "RENDER" ? "Render · выход" : "Направление неизвестно";
    public string DirectionTableLabel => Direction == "CAPTURE" ? "Вход" : Direction == "RENDER" ? "Выход" : "Неизвестно";
    public string DeviceStateLabel => DeviceState switch { "ACTIVE" => "Активно", "DISABLED" => "Отключено", "UNPLUGGED" => "Не подключено", "NOT_PRESENT" => "Отсутствует", _ => "Неизвестно" };
    public string ReportedChannelCountLabel => SharedModeFormat.ReportedChannelCount is int channels ? channels > 64 ? $"{channels} · вне модели 1–64" : channels.ToString() : "Неизвестно";
    public string ReportedChannelCountTableLabel => SharedModeFormat.ReportedChannelCount?.ToString() ?? "?";
    public string FormFactorLabel => FormFactorEvidence.State != "AVAILABLE" ? "Форм-фактор: неизвестен" : FormFactor switch { "REMOTE_NETWORK_DEVICE" => "Сетевое устройство", "SPEAKERS" => "Динамики (тип endpoint)", "LINE_LEVEL" => "Линейный уровень (тип endpoint)", "HEADPHONES" => "Наушники (тип endpoint)", "MICROPHONE" => "Микрофон (тип endpoint)", "HEADSET" => "Гарнитура (тип endpoint)", "HANDSET" => "Телефонная трубка (тип endpoint)", "DIGITAL_PASSTHROUGH" => "Цифровой passthrough", "SPDIF" => "S/PDIF", "DIGITAL_AUDIO_DISPLAY" => "Цифровой дисплей", _ => "Форм-фактор: неизвестен" };
    public string SharedModeFormatSummary
    {
        get
        {
            var format = SharedModeFormat;
            if (format.ParseState != "VALID" || format.ReportedChannelCount is null) return "Shared Mode формат недоступен или не распознан";
            var encoding = format.Encoding switch { "PCM" => "PCM", "FLOAT" => "Float", _ => "Кодирование неизвестно" };
            var topology = format.TopologyState == "UNSUPPORTED_CHANNEL_COUNT" ? $" · топология вне модели 1–64 (сообщено {format.ReportedChannelCount})" : $" · сообщено каналов: {format.ReportedChannelCount}";
            var mask = format.SpeakerMaskState == "INVALID" ? " · маска динамиков некорректна" : string.Empty;
            return $"Shared Mode · {encoding} · {format.SampleRate} Гц · {format.BitsPerSample} бит{topology}{mask}";
        }
    }
    public string SharedModeTableSummary
    {
        get
        {
            var format = SharedModeFormat;
            if (format.ParseState != "VALID") return "Неизвестно";
            var encoding = format.Encoding switch { "PCM" => "PCM", "FLOAT" => "Float", _ => "?" };
            var rate = format.SampleRate is uint sampleRate ? $"{sampleRate / 1000d:0.#} кГц" : "? Гц";
            var bits = format.BitsPerSample is int value ? $"{value} бит" : "? бит";
            return $"{encoding} · {rate} · {bits}";
        }
    }
    public string PhysicalRoutingLabel => "Физическая маршрутизация: неизвестна";
}
public sealed record WindowsDiscoveryErrorDto(string Direction, string Stage, int Hresult);
public sealed record WindowsEndpointDiscoveryDto(string Provenance, string OperationState, int CaptureHresult, int RenderHresult, IReadOnlyList<WindowsDiscoveryErrorDto> Errors, IReadOnlyList<WindowsEndpointDto> Endpoints);
public sealed record RealFormatCandidateDto(string Encoding, int SampleRate, int Channels, string Period, string ChannelMaskEvidence, string Status, string SupportStatus, string? ActivationHr, string? SupportHr, string? DevicePeriodHr, long? DefaultPeriod100ns, long? MinimumPeriod100ns, string InitializeStatus, bool InitializeAttempted, string? FirstInitializeHr, string? AlignmentInitializeHr, string? FinalInitializeHr, uint? ActualBufferFrames, string? SetEventHandleHr, string CleanupStatus)
{
    public int? ContainerBits { get; init; }
    public int? ValidBits { get; init; }
    public string QueryCleanupStatus { get; init; } = "INCOMPLETE";
    public uint QueryClientReleaseAttempts { get; init; }
    public string? PeriodQueryHr { get; init; }
    public bool? PeriodAvailable { get; init; }
    public long? RequestedPeriod100ns { get; init; }
    public string? AlignmentGetBufferSizeHr { get; init; }
    public uint? AlignmentFrames { get; init; }
    public string? AlignmentActivateHr { get; init; }
    public long? AlignmentPeriod100ns { get; init; }
    public string? ActualBufferSizeHr { get; init; }
    public long? ActualPeriod100ns { get; init; }
    public string? EventCreateHr { get; init; }
    public string? EventHandleCloseHr { get; init; }
    public bool? EventHandleReady { get; init; }
    public uint ClientReleaseAttempts { get; init; }
    public string EncodingLabel => Encoding switch { "PCM16" => "PCM16", "PCM24_IN_32" => "PCM24 в 32", "FLOAT32" => "Float32", _ => "Неизвестно" };
    public string PeriodLabel => Period switch { "default" => "Стандартный", "minimum" => "Минимальный", _ => Period };
    public string StatusLabel => Status switch { "SUPPORTED_REPORTED" => "Поддерживается (сообщил драйвер)", "UNSUPPORTED" => "Не поддерживается", "FAILED" => "Ошибка", "NOT_TESTED" => "Не проверено", "INCOMPLETE" => "Неполные данные", "INITIALIZED" => "Инициализировано", "READY" => "Готово", _ => "Неизвестно" };
}
public sealed record RealFormatMatrixDto(string Provenance, string TimestampUtc, string EndpointId, string Direction, string ReportLevel, IReadOnlyList<RealFormatCandidateDto> Candidates);
public sealed record ExclusivePeriodCandidateDto(long RequestedPeriod100ns, uint RequestedFrames, long RequestedEquivalentPeriod100ns, string Classification, string Reason, string? FinalInitializeHr, uint? AlignmentFrames, string? AlignmentInitializeHr, uint? ActualBufferFrames, long? ActualPeriod100ns, string CleanupStatus, uint ClientReleaseAttempts, bool AlignmentRequiresExplicitConfirmation = false);
public sealed record ExclusivePeriodDiscoveryReportDto(int SchemaVersion, string ReportType, string ReportId, string OperationId, string TimestampUtc, string Provenance, string EndpointId, string Direction, int SampleRateHz, string Encoding, int Channels, long MinimumPeriod100ns, long DefaultPeriod100ns, IReadOnlyList<ExclusivePeriodCandidateDto> Candidates);
public static class ExclusivePeriodDiscoveryReportJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static byte[] Serialize(ExclusivePeriodDiscoveryReportDto report)
    {
        Validate(report);
        return JsonSerializer.SerializeToUtf8Bytes(report, Options);
    }
    public static ExclusivePeriodDiscoveryReportDto Parse(ReadOnlySpan<byte> bytes)
    {
        var report = JsonSerializer.Deserialize<ExclusivePeriodDiscoveryReportDto>(bytes, Options) ?? throw new InvalidDataException("Period discovery report is empty.");
        Validate(report);
        return report;
    }
    public static string SaveNoOverwrite(ExclusivePeriodDiscoveryReportDto report, string directory)
    {
        Validate(report);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"period-discovery-{report.ReportId}.json");
        var temporary = Path.Combine(directory, $".{report.ReportId}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = Serialize(report); stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temporary, destination, false);
            _ = Parse(File.ReadAllBytes(destination));
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(ExclusivePeriodDiscoveryReportDto report)
    {
        if (report.SchemaVersion != 1 || report.ReportType != "wasapi-exclusive-period-discovery" || !Guid.TryParse(report.ReportId, out _) || !Guid.TryParse(report.OperationId, out _) || report.Provenance is not ("REAL_WASAPI_INITIALIZE_ONLY" or "TEST_FAKE_PERIOD_BACKEND") || report.Direction is not ("CAPTURE" or "RENDER") || report.SampleRateHz != 48_000 || report.Encoding != "PCM16" || report.Channels != 2 || report.MinimumPeriod100ns <= 0 || report.DefaultPeriod100ns < report.MinimumPeriod100ns || report.Candidates is null or { Count: 0 or > 8 }) throw new InvalidDataException("Period discovery report identity, format, bounds or candidate count is invalid.");
        foreach (var candidate in report.Candidates)
            if (candidate.RequestedPeriod100ns <= 0 || candidate.RequestedFrames == 0 || candidate.Classification is not ("ACCEPTED" or "ACCEPTED_ALIGNED" or "REJECTED" or "INCOMPLETE" or "UNKNOWN") || string.IsNullOrWhiteSpace(candidate.Reason) || candidate.CleanupStatus is not ("NOT_ACQUIRED" or "RELEASE_ATTEMPTED" or "FAILED" or "INCOMPLETE")) throw new InvalidDataException("Period discovery candidate evidence is invalid.");
    }
}
public sealed record RealFormatUniqueRowDto(string Format, int SampleRate, int Channels, RealFormatCandidateDto? DefaultCandidate, RealFormatCandidateDto? MinimumCandidate)
{
    public bool IsConflict { get; init; }
    public string ConflictDetails { get; init; } = "";
    public IReadOnlyList<RealFormatCandidateDto> EvidenceCandidates { get; init; } = [];
    public string SupportStatus => IsConflict ? "INCOMPLETE/CONFLICT" : DefaultCandidate?.SupportStatus ?? MinimumCandidate?.SupportStatus ?? "NOT_TESTED";
    public string SupportHresult => IsConflict ? "неоднозначно" : DefaultCandidate?.SupportHr ?? MinimumCandidate?.SupportHr ?? "не наблюдался";
    public string DisplayStatus => IsConflict ? "INCOMPLETE/CONFLICT" : EvidenceCandidates.Count > 0 && EvidenceCandidates.All(c => c.Status == "NOT_TESTED") ? "NOT_TESTED" : EvidenceCandidates.Any(c => c.Status == "INCOMPLETE" || c.SupportStatus == "INCOMPLETE") ? "INCOMPLETE" : EvidenceCandidates.Any(c => c.Status == "FAILED" || c.SupportStatus == "FAILED") ? "FAILED" : SupportStatus;
}
public static class RealFormatMatrixValidation
{
    private static readonly string[] Encodings = ["PCM16", "PCM24_IN_32", "FLOAT32"];
    private static readonly int[] Rates = [44100, 48000];

    public static IReadOnlyList<RealFormatUniqueRowDto> BuildUniqueRows(RealFormatMatrixDto matrix, IReadOnlyCollection<int> requestedChannels)
    {
        var groups = matrix.Candidates.GroupBy(c => (c.Encoding, c.SampleRate, c.Channels))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var expected = (from rate in Rates from channels in requestedChannels from encoding in Encodings select (encoding, rate, channels)).ToHashSet();
        var keys = groups.Keys.Concat(expected).Distinct();
        return keys.Select(key => BuildRow(key, groups.GetValueOrDefault(key) ?? [], expected.Contains(key)))
            .OrderBy(r => r.SampleRate).ThenBy(r => r.Channels).ThenBy(r => r.Format, StringComparer.Ordinal).ToArray();
    }

    private static RealFormatUniqueRowDto BuildRow((string Encoding, int SampleRate, int Channels) key, RealFormatCandidateDto[] evidence, bool wasRequested)
    {
        var defaults = evidence.Where(c => string.Equals(c.Period, "default", StringComparison.OrdinalIgnoreCase)).ToArray();
        var minimums = evidence.Where(c => string.Equals(c.Period, "minimum", StringComparison.OrdinalIgnoreCase)).ToArray();
        var reasons = new List<string>();
        if (!wasRequested) reasons.Add("Формат отсутствовал в запросе");
        if (!Encodings.Contains(key.Encoding, StringComparer.Ordinal) || !Rates.Contains(key.SampleRate) || key.Channels is < 1 or > 64) reasons.Add("Некорректный формат");
        if (evidence.Length != 2 || defaults.Length != 1 || minimums.Length != 1) reasons.Add("Ожидалась ровно одна запись DEFAULT и MINIMUM");
        if (evidence.Any(c => c.Encoding != key.Encoding || c.SampleRate != key.SampleRate || c.Channels != key.Channels)) reasons.Add("Поля формата расходятся");
        if (evidence.Any(c => c.SupportStatus is not ("SUPPORTED_REPORTED" or "UNSUPPORTED" or "FAILED" or "INCOMPLETE") || c.Status is not ("SUPPORTED_REPORTED" or "UNSUPPORTED" or "FAILED" or "NOT_TESTED" or "INCOMPLETE"))) reasons.Add("Неизвестный статус запроса");
        if (evidence.Any(c => c.Status != "NOT_TESTED" && string.IsNullOrWhiteSpace(c.SupportHr))) reasons.Add("IsFormatSupported HRESULT отсутствует");
        if (evidence.Any(c => c.Status != "NOT_TESTED" && (string.IsNullOrWhiteSpace(c.QueryCleanupStatus) || c.QueryCleanupStatus == "INCOMPLETE"))) reasons.Add("Данные очистки запроса неполны");
        if (evidence.Any(c => c.InitializeAttempted || c.InitializeStatus != "NOT_TESTED" || c.FirstInitializeHr is not null || c.AlignmentInitializeHr is not null || c.FinalInitializeHr is not null)) reasons.Add("В пассивном запросе обнаружены данные Initialize");
        if (evidence.Skip(1).Any(c => !SameQueryEvidence(evidence[0], c))) reasons.Add("Общие данные IsFormatSupported между периодами противоречат друг другу");
        var conflict = reasons.Count != 0;
        return new(key.Encoding, key.SampleRate, key.Channels,
            conflict || defaults.Length != 1 ? null : defaults[0],
            conflict || minimums.Length != 1 ? null : minimums[0])
        {
            IsConflict = conflict,
            ConflictDetails = string.Join("; ", reasons),
            EvidenceCandidates = evidence
        };
    }

    private static bool SameQueryEvidence(RealFormatCandidateDto a, RealFormatCandidateDto b) =>
        a.Status == b.Status && a.SupportStatus == b.SupportStatus && a.SupportHr == b.SupportHr &&
        a.ContainerBits == b.ContainerBits && a.ValidBits == b.ValidBits &&
        a.ActivationHr == b.ActivationHr && a.DevicePeriodHr == b.DevicePeriodHr &&
        a.DefaultPeriod100ns == b.DefaultPeriod100ns && a.MinimumPeriod100ns == b.MinimumPeriod100ns &&
        a.ChannelMaskEvidence == b.ChannelMaskEvidence && a.QueryCleanupStatus == b.QueryCleanupStatus &&
        a.QueryClientReleaseAttempts == b.QueryClientReleaseAttempts && a.PeriodQueryHr == b.PeriodQueryHr &&
        a.PeriodAvailable == b.PeriodAvailable && a.CleanupStatus == b.CleanupStatus;
}
public sealed record RealFormatConsentDto(bool Accepted, Guid ConfirmationId, string RequestFingerprint);
public sealed record RealStreamConsentDto(bool Accepted, Guid ConfirmationId, string RequestFingerprint);
public sealed record RealStreamValidationDto(string OperationId, string EndpointId, string Direction, System.Text.Json.JsonElement Report);
public sealed record DuplexConsentDto(bool Accepted, Guid ConfirmationId, string RequestFingerprint);
public sealed record DuplexValidationDto(string OperationId, string CaptureEndpointId, string RenderEndpointId, JsonElement Report);
public sealed record WorkerScanEndpoint(string EndpointId, string Direction);
public sealed record WorkerScanFormat(int SampleRateHz, string Encoding, int Channels, IReadOnlyList<string> Periods, IReadOnlyList<string> MmcssModes);
public sealed record WorkerScanPlan(IReadOnlyList<WorkerScanEndpoint> Endpoints, IReadOnlyList<string> AllowedOperations, IReadOnlyList<WorkerScanFormat> Formats, int LifetimeSeconds, int MaximumOperationDurationSeconds, int MaximumOperationCount);
public sealed record WorkerScanSessionToken(Guid ScanSessionId, int ContractVersion = 1, string? RouteFingerprint = null);
public sealed record WorkerScanV2Side(int SampleRateHz, int Channels, string Encoding, uint ChannelMask, IReadOnlyList<int> SelectedIndicesZeroBased);
public sealed record WorkerScanV2Route(string Mode, WorkerScanV2Side Capture, WorkerScanV2Side Render);
public sealed record WorkerScanPlanV2(WorkerScanV2Route Route, IReadOnlyList<string> Periods, IReadOnlyList<string> MmcssModes,
    int LifetimeSeconds, int MaximumOperationDurationSeconds, int MaximumOperationCount, long MaximumTotalDurationSeconds, string QaMode = "DISCOVERY");

public sealed record WorkerScanV2Candidate(int Index, long RequestedDuration100ns, int CaptureFrames, int RenderFrames,
    long CapturePeriod100ns, long RenderPeriod100ns, int CaptureAlignmentFrames, int RenderAlignmentFrames,
    int CaptureMinimumFrames, int CaptureMaximumFrames, int RenderMinimumFrames, int RenderMaximumFrames, string RejectionReason = "");
public sealed record WorkerScanV2ContractV4(WorkerScanV2Route Route, IReadOnlyList<WorkerScanV2Candidate> Candidates,
    IReadOnlyList<string> MmcssModes, int RepeatabilityRuns, int MaximumOperations, int MaximumActiveDuplexSeconds,
    int InitializeTimeoutSeconds, int SessionLifetimeSeconds, int CleanupDeadlineSeconds);
public sealed record WorkerScanV2Budget(int MaximumOperations, int MaximumActiveDuplexSeconds, int SessionLifetimeSeconds);
public sealed record WorkerScanV2V4Consent(bool Accepted, Guid ConfirmationId, string PlanFingerprint, ulong DiscoveryGeneration);
public static class WorkerScanV2CandidatePlanning
{
    public static IReadOnlyList<WorkerScanV2Candidate> Generate(WorkerScanV2Route route, int captureMinFrames, int captureMaxFrames,
        int renderMinFrames, int renderMaxFrames, int captureAlignmentFrames = 1, int renderAlignmentFrames = 1)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (captureMinFrames <= 0 || renderMinFrames <= 0 || captureMaxFrames < captureMinFrames || renderMaxFrames < renderMinFrames ||
            captureAlignmentFrames <= 0 || renderAlignmentFrames <= 0) throw new ArgumentOutOfRangeException(nameof(captureMinFrames));
        static long Duration(int frames, int rate) => checked((long)(((decimal)frames * 10_000_000m + rate / 2m) / rate));
        var lower = Math.Max(Duration(captureMinFrames, route.Capture.SampleRateHz), Duration(renderMinFrames, route.Render.SampleRateHz));
        var upper = Math.Min(Duration(captureMaxFrames, route.Capture.SampleRateHz), Duration(renderMaxFrames, route.Render.SampleRateHz));
        if (upper < lower) return [];
        var periods = new SortedSet<long>();
        for (var i = 0; i < 8; i++) periods.Add(checked(lower + (long)(((decimal)(upper - lower) * i) / 7m)));
        return periods.Select((duration, index) => Make(index, duration, route, captureMinFrames, captureMaxFrames,
                renderMinFrames, renderMaxFrames, captureAlignmentFrames, renderAlignmentFrames))
            .GroupBy(c => (c.CaptureFrames, c.RenderFrames)).Select(g => g.First()).Take(8)
            .Select((candidate, index) => candidate with { Index = index }).ToArray();
    }

    public static IReadOnlyList<WorkerScanV2Candidate> For48KhzThreeToTenMs(WorkerScanV2Route route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Capture.SampleRateHz != 48_000 || route.Render.SampleRateHz != 48_000) throw new ArgumentException("This explicit grid applies only to the reviewed 48 kHz domain.", nameof(route));
        return Enumerable.Range(3, 8).Select((ms, index) => Make(index, ms * 10_000L, route, 144, 480, 144, 480)).ToArray();
    }

    public static WorkerScanV2Candidate Make(int index, long duration100ns, WorkerScanV2Route route,
        int captureMinFrames, int captureMaxFrames, int renderMinFrames, int renderMaxFrames,
        int captureAlignmentFrames = 1, int renderAlignmentFrames = 1)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (duration100ns <= 0 || duration100ns > long.MaxValue / Math.Max(route.Capture.SampleRateHz, route.Render.SampleRateHz)) throw new ArgumentOutOfRangeException(nameof(duration100ns));
        static int Frames(long duration, int rate) => checked((int)(((decimal)duration * rate + 5_000_000m) / 10_000_000m));
        var cf = Frames(duration100ns, route.Capture.SampleRateHz); var rf = Frames(duration100ns, route.Render.SampleRateHz);
        var cp = checked((long)(((decimal)cf * 10_000_000m + route.Capture.SampleRateHz / 2m) / route.Capture.SampleRateHz));
        var rp = checked((long)(((decimal)rf * 10_000_000m + route.Render.SampleRateHz / 2m) / route.Render.SampleRateHz));
        if (captureAlignmentFrames <= 0 || renderAlignmentFrames <= 0) throw new ArgumentOutOfRangeException(nameof(captureAlignmentFrames));
        var rejected = cf < captureMinFrames || cf > captureMaxFrames || rf < renderMinFrames || rf > renderMaxFrames || cf % captureAlignmentFrames != 0 || rf % renderAlignmentFrames != 0;
        var candidate = new WorkerScanV2Candidate(index, duration100ns, cf, rf, cp, rp, captureAlignmentFrames, renderAlignmentFrames,
            captureMinFrames, captureMaxFrames, renderMinFrames, renderMaxFrames,
            rejected ? $"GEOMETRY_CONSTRAINT:CAPTURE={cf}/{captureMinFrames}-{captureMaxFrames}/align={captureAlignmentFrames}/period={cp};RENDER={rf}/{renderMinFrames}-{renderMaxFrames}/align={renderAlignmentFrames}/period={rp}" : "");
        return candidate;
    }
}

public static class WorkerScanV2ContractV4Fingerprint
{
    public static WorkerScanV2V4Consent CreateConsent(string captureId, string renderId, WorkerScanV2ContractV4 plan, ulong generation, bool accepted, Guid confirmationId) =>
        new(accepted, confirmationId, Compute(captureId, renderId, plan, generation), generation);

    public static string ConsentSummary(string captureId, string renderId, WorkerScanV2ContractV4 plan, ulong generation)
    {
        var fingerprint = Compute(captureId, renderId, plan, generation);
        var periods = string.Join(", ", plan.Candidates.Select(c => $"{c.RequestedDuration100ns}×100ns C{c.CaptureFrames}/R{c.RenderFrames}"));
        return $"Capture {captureId}: {plan.Route.Capture.SampleRateHz} Hz {plan.Route.Capture.Encoding} {plan.Route.Capture.Channels}ch [{string.Join(',', plan.Route.Capture.SelectedIndicesZeroBased)}] → Render {renderId}: {plan.Route.Render.SampleRateHz} Hz {plan.Route.Render.Encoding} {plan.Route.Render.Channels}ch [{string.Join(',', plan.Route.Render.SelectedIndicesZeroBased)}]; route={plan.Route.Mode}; {plan.Candidates.Count} candidates [{periods}]; repeatability={plan.RepeatabilityRuns}; max audio operations={plan.MaximumOperations}; max active Duplex={plan.MaximumActiveDuplexSeconds}s; Initialize timeout={plan.InitializeTimeoutSeconds}s; session={plan.SessionLifetimeSeconds}s; cleanup={plan.CleanupDeadlineSeconds}s; plan fingerprint={fingerprint}. v4 transport is not enabled in this build.";
    }

    public static WorkerScanV2Budget Budget(int candidateCount, int repeatabilityRuns)
    {
        if (candidateCount is < 1 or > 8 || repeatabilityRuns is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(candidateCount));
        checked
        {
            var operations = 3 * candidateCount + repeatabilityRuns;
            var active = 5 * (candidateCount + repeatabilityRuns);
            var lifetime = 111 * candidateCount + 51 * repeatabilityRuns + 28;
            if (operations > 29 || active > 65 || lifetime > 1171) throw new ArgumentOutOfRangeException(nameof(candidateCount));
            return new(operations, active, lifetime);
        }
    }

    public static string Canonicalize(string captureId, string renderId, WorkerScanV2ContractV4 plan, ulong generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId); ArgumentException.ThrowIfNullOrWhiteSpace(renderId);
        ArgumentNullException.ThrowIfNull(plan);
        var budget = Budget(plan.Candidates.Count, plan.RepeatabilityRuns);
        if (captureId == renderId || plan.MaximumOperations != budget.MaximumOperations || plan.MaximumActiveDuplexSeconds != budget.MaximumActiveDuplexSeconds ||
            plan.SessionLifetimeSeconds != budget.SessionLifetimeSeconds || plan.InitializeTimeoutSeconds != 30 || plan.CleanupDeadlineSeconds != 8 ||
            plan.MmcssModes.Count != 1 || plan.MmcssModes[0] != "OFF") throw new ArgumentException("V4 authorization budget or endpoint scope is invalid.");
        var route = WorkerScanPlanV2Fingerprint.Route(captureId, renderId, plan.Route);
        var canonical = $"SCAN_PLAN_V2|4|{generation}|{captureId}|{renderId}|{route}|{plan.RepeatabilityRuns}|{budget.MaximumOperations}|{budget.MaximumActiveDuplexSeconds}|{plan.InitializeTimeoutSeconds}|{plan.SessionLifetimeSeconds}|{plan.CleanupDeadlineSeconds}|\n";
        long previousDuration = 0;
        var seenGeometry = new HashSet<(long, long)>();
        for (var i = 0; i < plan.Candidates.Count; i++)
        {
            var candidate = plan.Candidates[i];
            ValidateCandidate(candidate, plan.Route);
            if (candidate.Index != i || candidate.RequestedDuration100ns <= previousDuration || !seenGeometry.Add((candidate.CapturePeriod100ns, candidate.RenderPeriod100ns))) throw new ArgumentException("V4 candidates must be unique, contiguous, and ordered by duration.");
            previousDuration = candidate.RequestedDuration100ns;
            canonical += $"C|{candidate.Index}|{candidate.RequestedDuration100ns}|{candidate.CaptureFrames}|{candidate.RenderFrames}|{candidate.CapturePeriod100ns}|{candidate.RenderPeriod100ns}|{candidate.CaptureAlignmentFrames}|{candidate.RenderAlignmentFrames}|{candidate.CaptureMinimumFrames}|{candidate.CaptureMaximumFrames}|{candidate.RenderMinimumFrames}|{candidate.RenderMaximumFrames}|{candidate.RejectionReason}\n";
        }
        foreach (var mode in plan.MmcssModes) canonical += $"M|{mode}\n";
        return canonical;
    }

    public static string Compute(string captureId, string renderId, WorkerScanV2ContractV4 plan, ulong generation) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(captureId, renderId, plan, generation)))).ToLowerInvariant();

    private static void ValidateCandidate(WorkerScanV2Candidate c, WorkerScanV2Route route)
    {
        if (c.Index < 0 || c.RequestedDuration100ns <= 0 || c.CaptureFrames <= 0 || c.RenderFrames <= 0 || c.CapturePeriod100ns <= 0 || c.RenderPeriod100ns <= 0 ||
            c.CaptureMinimumFrames <= 0 || c.RenderMinimumFrames <= 0 || c.CaptureMaximumFrames < c.CaptureMinimumFrames || c.RenderMaximumFrames < c.RenderMinimumFrames || c.CaptureAlignmentFrames <= 0 || c.RenderAlignmentFrames <= 0 ||
            (c.RejectionReason.Length == 0 && (c.CaptureFrames < c.CaptureMinimumFrames || c.CaptureFrames > c.CaptureMaximumFrames || c.RenderFrames < c.RenderMinimumFrames || c.RenderFrames > c.RenderMaximumFrames || c.CaptureFrames % c.CaptureAlignmentFrames != 0 || c.RenderFrames % c.RenderAlignmentFrames != 0)) ||
            c.CaptureFrames != (int)(((decimal)c.RequestedDuration100ns * route.Capture.SampleRateHz + 5_000_000m) / 10_000_000m) ||
            c.RenderFrames != (int)(((decimal)c.RequestedDuration100ns * route.Render.SampleRateHz + 5_000_000m) / 10_000_000m) ||
            c.CapturePeriod100ns != (long)(((decimal)c.CaptureFrames * 10_000_000m + route.Capture.SampleRateHz / 2m) / route.Capture.SampleRateHz) ||
            c.RenderPeriod100ns != (long)(((decimal)c.RenderFrames * 10_000_000m + route.Render.SampleRateHz / 2m) / route.Render.SampleRateHz) ||
            c.Index >= 8) throw new ArgumentException("V4 candidate geometry is invalid.", nameof(c));
    }
}

public static class WorkerScanPlanV2Fingerprint
{
    public static string Route(string captureEndpointId, string renderEndpointId, WorkerScanV2Route route)
    {
        ArgumentNullException.ThrowIfNull(route); ArgumentException.ThrowIfNullOrWhiteSpace(captureEndpointId); ArgumentException.ThrowIfNullOrWhiteSpace(renderEndpointId);
        if (captureEndpointId == renderEndpointId) throw new ArgumentException("Capture and Render endpoints must be distinct.");
        var captureCount = route.Mode == "STEREO_TO_STEREO" ? 2 : route.Mode is "MONO_TO_STEREO" or "MONO_TO_MONO" ? 1 : 0;
        var renderCount = route.Mode == "MONO_TO_MONO" ? 1 : route.Mode is "MONO_TO_STEREO" or "STEREO_TO_STEREO" ? 2 : 0;
        ValidateSide(route.Capture, captureCount); ValidateSide(route.Render, renderCount);
        var canonical = $"2|{captureEndpointId}|{renderEndpointId}|{route.Mode}|";
        static string Side(WorkerScanV2Side s) => $"{s.SampleRateHz},{s.Channels},{s.Encoding},{s.ChannelMask}|";
        canonical += Side(route.Capture) + Side(route.Render);
        for (var i = 0; i < captureCount; i++) canonical += $"C{route.Capture.SelectedIndicesZeroBased[i]}|";
        for (var i = 0; i < renderCount; i++) canonical += $"R{route.Render.SelectedIndicesZeroBased[i]}|";
        ulong hash = 14695981039346656037UL; foreach (var b in Encoding.UTF8.GetBytes(canonical)) { hash ^= b; hash *= 1099511628211UL; }
        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
    public static string Compute(string captureEndpointId, string renderEndpointId, WorkerScanPlanV2 plan, ulong discoveryGeneration)
    {
        var (lifetime, maxDuration, maxOperations, maxTotal) = plan.QaMode switch { "SHORT" => (120, 5, 3, 5L), "AUTO_SHORT" => (180, 5, 6, 10L), "FULL" => (1200, 600, 12, 995L), _ => throw new ArgumentException("V2 audio authorization requires a bounded QA mode.", nameof(plan)) };
        if (plan.LifetimeSeconds != lifetime || plan.MaximumOperationDurationSeconds != maxDuration || plan.MaximumOperationCount != maxOperations || plan.MaximumTotalDurationSeconds != maxTotal || plan.Periods.Count is < 1 or > 2 || plan.Periods.Any(p => p is not ("MINIMUM" or "DEFAULT")) || plan.Periods.Distinct(StringComparer.Ordinal).Count() != plan.Periods.Count || plan.MmcssModes.Count is < 1 or > 2 || plan.MmcssModes.Any(m => m is not ("OFF" or "PRO_AUDIO")) || plan.MmcssModes.Distinct(StringComparer.Ordinal).Count() != plan.MmcssModes.Count || plan.QaMode == "AUTO_SHORT" && (plan.MmcssModes.Count != 1 || plan.MmcssModes[0] != "OFF")) throw new ArgumentException("V2 scan scope is outside its bounded authorization limits.", nameof(plan));
        var route = Route(captureEndpointId, renderEndpointId, plan.Route);
        var canonical = $"SCAN_PLAN_V2|3|{discoveryGeneration}|{plan.QaMode}|{plan.LifetimeSeconds}|{maxDuration}|{maxOperations}|{maxTotal}|{route}\n" +
            string.Concat(plan.Periods.Order(StringComparer.Ordinal).Select(p => $"P|{p}\n")) +
            string.Concat(plan.MmcssModes.Order(StringComparer.Ordinal).Select(m => $"M|{m}\n")) + (plan.QaMode == "FULL" ? "D|60\nD|300\nD|600\n" : "");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
    private static void ValidateSide(WorkerScanV2Side side, int count)
    {
        if (count == 0 || side.SampleRateHz is not (44100 or 48000) || side.Channels is < 1 or > 32 || side.Encoding is not ("PCM16" or "PCM24_IN_32" or "FLOAT32") || side.SelectedIndicesZeroBased.Count != count || side.SelectedIndicesZeroBased.Any(i => i < 0 || i >= side.Channels) || side.SelectedIndicesZeroBased.Distinct().Count() != count || (side.ChannelMask != 0 && System.Numerics.BitOperations.PopCount(side.ChannelMask) != side.Channels)) throw new ArgumentException("V2 route side is outside its bounded format or channel scope.");
    }
}
public static class WorkerScanPlanFingerprint
{
    public static string Compute(WorkerScanPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new List<string> { $"C3.2|{plan.LifetimeSeconds}|{plan.MaximumOperationDurationSeconds}|{plan.MaximumOperationCount}\n" };
        lines.AddRange(plan.Endpoints.Select(e => $"E|{e.Direction}|{Encoding.UTF8.GetByteCount(e.EndpointId)}|{e.EndpointId}\n"));
        lines.AddRange(plan.AllowedOperations.Select(o => $"O|{o}\n"));
        lines.AddRange(from f in plan.Formats from period in f.Periods from mmcss in f.MmcssModes select $"F|{f.SampleRateHz}|{f.Encoding}|{f.Channels}|{period}|{mmcss}\n");
        var header = lines[0];
        var canonical = header + string.Concat(lines.Skip(1).Where(l => l.StartsWith("E|", StringComparison.Ordinal)).Order(StringComparer.Ordinal)) +
            string.Concat(lines.Skip(1).Where(l => l.StartsWith("O|", StringComparison.Ordinal)).Order(StringComparer.Ordinal)) +
            string.Concat(lines.Skip(1).Where(l => l.StartsWith("F|", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
public static class RealFormatConsent
{
    public static string Canonicalize(string endpointId, string direction, int sampleRate, int channels, string encoding, string period) =>
        string.Join('|', endpointId, direction, sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), channels.ToString(System.Globalization.CultureInfo.InvariantCulture), encoding, period.ToLowerInvariant());
    public static string Fingerprint(string endpointId, string direction, int sampleRate, int channels, string encoding, string period) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(endpointId, direction, sampleRate, channels, encoding, period)))).ToLowerInvariant();
}
public static class RealStreamConsent
{
    public static string Fingerprint(string endpointId, string direction, string period, int durationSeconds, string mmcss)
    {
        var canonical = string.Join('|', endpointId, direction, "48000", "2", "PCM16", period.ToUpperInvariant(), durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), mmcss, "REAL_WASAPI_STREAM_V1");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
public static class DuplexConsent
{
    public static string Fingerprint(string captureId, string renderId, string period, int durationSeconds, string mmcss)
    {
        var canonical = string.Join('|', captureId, renderId, "48000", "2", "PCM16", period.ToUpperInvariant(), durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), mmcss, "REAL_WASAPI_DUPLEX_V1");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
public sealed record WorkerRequestDto(int ProtocolMajor, int ProtocolMinor, int SchemaVersion, Guid RequestId, string Command, object Payload);
public sealed record WorkerResponseDto(int ProtocolMajor, int ProtocolMinor, int SchemaVersion, Guid CorrelationId, string Command, string Status, string? ErrorCode, string? Message, JsonElement Payload);
public sealed record WorkerHandshakeRequestDto(int BootstrapVersion, Guid RequestId, int ProtocolMajor, int MinMinor, int MaxMinor, int CommandSchema, int EventSchema, int ReportSchema);
public sealed record WorkerHandshakeResponseDto(int BootstrapVersion, Guid CorrelationId, int ProtocolMajor, int ProtocolMinor, int CommandSchema, int EventSchema, int ReportSchema, string WorkerVersion, IReadOnlyList<string> Capabilities);
public sealed record WorkerSessionSettingsDto(string EndpointId, string Direction, string FormatId, int SampleRate, string Encoding, int ChannelCount, string TopologyId, int DurationSeconds, string Period, string Mmcss, int? RenderChannel, string Scenario);
public sealed record ConsentReceiptDto(bool Accepted, Guid ConfirmationId, string RequestFingerprint);
public sealed record WorkerSessionAcceptedDto(Guid SessionId, Guid CorrelationId, WorkerSessionSettingsDto Settings, string OperationState = "ACCEPTED");
public sealed record WorkerCaptureMeterChannelDto(int Index, double? WindowPeakDbfs, double? WindowRmsDbfs, bool? Silent, double? SessionPeakHoldDbfs, int? ClipCount);
public sealed record WorkerRenderMeterChannelDto(int Index, double? IntendedPeakDbfs, double? IntendedRmsDbfs, bool? DigitalSilent, double? SessionPeakHoldDbfs, int? ClipCount);
public sealed record WorkerCaptureMeterSnapshotDto(long SnapshotSequence, long? CompletedWindowIndex, int WindowDurationMs, int ChannelCount, IReadOnlyList<WorkerCaptureMeterChannelDto> Channels, string StreamQuality, bool Synthetic, long CoalescedSnapshotCount);
public sealed record WorkerRenderMeterSnapshotDto(long SnapshotSequence, long? CompletedWindowIndex, int WindowDurationMs, int ChannelCount, int SelectedChannelIndex, IReadOnlyList<WorkerRenderMeterChannelDto> Channels, string StreamQuality, string IntendedOutputModel, bool Synthetic, long CoalescedSnapshotCount);
public sealed record WorkerEventDto(long EventSequence, Guid SessionId, Guid CorrelationId, string Name, JsonElement Payload, WorkerCaptureMeterSnapshotDto? CaptureMeter = null, WorkerRenderMeterSnapshotDto? RenderMeter = null);
public sealed record WorkerTransportFaultDto(string Stage, string Message, int? ExitCode = null, Guid? CorrelationId = null, string? Command = null, string? ErrorCode = null);
public sealed class WorkerCommandException(string command, string errorCode, string message) : IOException($"{command}/{errorCode}: {message}")
{
    public string Command { get; } = command;
    public string ErrorCode { get; } = errorCode;
}

public static class WorkerConsent
{
    public static string Canonicalize(WorkerSessionSettingsDto s) => string.Join('|', s.EndpointId, s.Direction, s.FormatId, s.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Encoding, s.ChannelCount.ToString(System.Globalization.CultureInfo.InvariantCulture), s.TopologyId, s.DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Period, s.Mmcss, s.RenderChannel?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null", s.Scenario);
    public static string Fingerprint(WorkerSessionSettingsDto s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(s)))).ToLowerInvariant();
}

public static class WorkerProtocolCompatibility
{
    public static bool IsCompatible(int major, int minor, int commandSchema, int eventSchema, int reportSchema) => major == 1 && minor == 0 && commandSchema == 1 && eventSchema == 1 && reportSchema == 1;
}

public sealed class WorkerProtocolException(string stage, string errorCode, string message, int? hresult = null) : IOException($"{stage}/{errorCode}: {message}")
{
    public string Stage { get; } = stage;
    public string ErrorCode { get; } = errorCode;
    public int? Hresult { get; } = hresult;
}

public static class IpcFrameCodec
{
    public const int MaximumPayloadBytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        ValidatePayload(payload);
        var frame = new byte[payload.Length + sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(uint)));
        return frame;
    }

    public static byte[] EncodeJson<T>(T message) => Encode(JsonSerializer.SerializeToUtf8Bytes(message));

    public static async Task<byte[]> ReadPayloadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(uint)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaximumPayloadBytes) throw new InvalidDataException($"Invalid frame length: {length}.");
        var payload = new byte[(int)length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        ValidatePayload(payload);
        return payload;
    }

    public static async Task WriteJsonAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var frame = EncodeJson(message);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static JsonDocument Parse(ReadOnlySpan<byte> payload)
    {
        ValidatePayload(payload);
        var document = JsonDocument.Parse(payload.ToArray(), JsonOptions);
        try { RejectDuplicateProperties(document.RootElement, 0); }
        catch { document.Dispose(); throw; }
        return document;
    }

    public static void ValidatePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > MaximumPayloadBytes) throw new InvalidDataException("Payload length is outside the protocol bounds.");
        if (payload.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) throw new InvalidDataException("UTF-8 BOM is not allowed.");
        try { _ = StrictUtf8.GetString(payload); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Payload is not valid UTF-8.", ex); }
        using var parsed = ParseJsonOnly(payload);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("IPC payload must be a JSON object.");
    }

    private static JsonDocument ParseJsonOnly(ReadOnlySpan<byte> payload)
    {
        var doc = JsonDocument.Parse(payload.ToArray(), JsonOptions);
        try { RejectDuplicateProperties(doc.RootElement, 0); }
        catch { doc.Dispose(); throw; }
        return doc;
    }

    private static void RejectDuplicateProperties(JsonElement value, int depth)
    {
        if (depth > 32) throw new InvalidDataException("JSON nesting exceeds 32 levels.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate JSON property: {property.Name}.");
                RejectDuplicateProperties(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item, depth + 1);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await stream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Truncated IPC frame.");
            read += count;
        }
    }
}

public interface IWorkerClient : IAsyncDisposable
{
    WorkerStateDto State { get; }
    IReadOnlyList<string> Capabilities => Array.Empty<string>();
    event Action<WorkerStateDto>? StateChanged;
    event Action<WorkerSessionAcceptedDto>? SessionAccepted;
    event Action<WorkerEventDto>? EventReceived;
    event Action<WorkerTransportFaultDto>? TransportFault;
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkerEndpointDto>> ListEndpointsAsync(CancellationToken cancellationToken = default);
    Task<WindowsEndpointDiscoveryDto> ListWindowsEndpointsAsync(CancellationToken cancellationToken = default) => Task.FromException<WindowsEndpointDiscoveryDto>(new NotSupportedException("Windows endpoint discovery is unavailable in this worker."));
    Task<RealFormatMatrixDto> QueryFormatMatrixAsync(string endpointId, string direction, IReadOnlyList<int>? channelCounts = null, CancellationToken cancellationToken = default) => Task.FromException<RealFormatMatrixDto>(new NotSupportedException("Real Exclusive format queries are unavailable in this worker."));
    Task<RealFormatMatrixDto> ValidateFormatMatrixAsync(string endpointId, string direction, int sampleRate, int channels, string encoding, string period, RealFormatConsentDto consent, CancellationToken cancellationToken = default) => Task.FromException<RealFormatMatrixDto>(new NotSupportedException("Real Exclusive Initialize-only checks are unavailable in this worker."));
    Task<ExclusivePeriodDiscoveryReportDto> InitializeExclusiveCustomPeriodAsync(WorkerScanSessionToken session, Guid operationId, string endpointId, string direction, int period100ns, int frames, CancellationToken cancellationToken = default) => Task.FromException<ExclusivePeriodDiscoveryReportDto>(new NotSupportedException("Custom Exclusive period discovery is unavailable in this worker."));
    Task<RealStreamValidationDto> ValidateStreamAsync(string operationId, string endpointId, string direction, string period, int durationSeconds, string mmcss, RealStreamConsentDto consent, CancellationToken cancellationToken = default, string? testScenario = null) => Task.FromException<RealStreamValidationDto>(new NotSupportedException("Real WASAPI stream validation is unavailable in this worker."));
    Task CancelStreamValidationAsync(string operationId, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Real WASAPI stream cancellation is unavailable in this worker."));
    Task<DuplexValidationDto> ValidateDuplexAsync(string operationId, string captureEndpointId, string renderEndpointId, string period, int durationSeconds, string mmcss, DuplexConsentDto consent, CancellationToken cancellationToken = default, string? testScenario = null) => Task.FromException<DuplexValidationDto>(new NotSupportedException("Real WASAPI Duplex validation is unavailable in this worker."));
    Task<DuplexValidationDto> ValidateSustainedDuplexInScanAsync(string operationId, string captureEndpointId, string renderEndpointId, string period, int durationSeconds, string mmcss, WorkerScanSessionToken session, Guid scanOperationId, CancellationToken cancellationToken = default) => Task.FromException<DuplexValidationDto>(new NotSupportedException("Sustained WASAPI Duplex validation is unavailable in this worker."));
    Task<DuplexValidationDto> ValidateCustomDuplexInScanAsync(Guid operationId, string captureEndpointId, string renderEndpointId, string period, int durationSeconds, string mmcss, WorkerScanSessionToken session, Guid scanOperationId, CancellationToken cancellationToken = default) => Task.FromException<DuplexValidationDto>(new NotSupportedException("Custom-period Duplex validation is unavailable in this worker."));
    Task CancelDuplexValidationAsync(string operationId, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Real WASAPI Duplex cancellation is unavailable in this worker."));
    Task<WorkerSessionAcceptedDto> StartSessionAsync(WorkerSessionSettingsDto settings, ConsentReceiptDto consent, CancellationToken cancellationToken = default);
    Task StopSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

public sealed record ChannelDto(int Index, string? Role = null, string? Name = null);
public sealed record ChannelGroupDto(string Id, string? Name, string Kind, IReadOnlyList<int> ChannelIndices);
public sealed record FormatTopologyDto(string FormatId, StreamTopologyDto Topology);
public sealed record StreamTopologyDto(int ChannelCount, IReadOnlyList<ChannelDto> Channels, IReadOnlyList<ChannelGroupDto>? Groups = null, uint? SpeakerMask = null)
{
    public void Validate()
    {
        if (ChannelCount is < 1 or > 64 || Channels.Count != ChannelCount || Channels.Select(c => c.Index).Distinct().Count() != ChannelCount || !Channels.Select(c => c.Index).Order().SequenceEqual(Enumerable.Range(1, ChannelCount)))
            throw new ArgumentException("A topology must describe each stream index from 1 through ChannelCount exactly once.");
        var groups = Groups ?? [];
        if (groups.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != groups.Count) throw new ArgumentException("Logical group identifiers must be unique within a topology.");
        foreach (var group in groups)
            if (group.Kind is not ("MONO" or "STEREO_PAIR" or "GROUP" or "UNKNOWN") || group.ChannelIndices.Count == 0 || group.ChannelIndices.Any(i => i < 1 || i > ChannelCount) || group.ChannelIndices.Distinct().Count() != group.ChannelIndices.Count)
                throw new ArgumentException("Logical group membership must contain distinct, in-range stream indices.");
    }
}

public static class WorkerEndpointValidation
{
    public static void Validate(WorkerEndpointDto endpoint)
    {
        if (!endpoint.IsSynthetic || endpoint.State != "SYNTHETIC" || endpoint.Direction is not ("CAPTURE" or "RENDER") || endpoint.ChannelCount != endpoint.Topology.ChannelCount)
            throw new InvalidDataException("Worker endpoint response contains an invalid required enum or topology.");
        endpoint.Topology.Validate();
        foreach (var format in endpoint.FormatTopologies ?? [])
        {
            if (string.IsNullOrWhiteSpace(format.FormatId)) throw new InvalidDataException("Format topology is missing a required format identifier.");
            format.Topology.Validate();
        }
    }
}

public static class WindowsEndpointDiscoveryValidation
{
    public static void Validate(WindowsEndpointDiscoveryDto discovery, bool allowTestProvenance = false)
    {
        if (discovery is null || discovery.Provenance != "REAL_WINDOWS_METADATA" && !(allowTestProvenance && discovery.Provenance == "TEST_FAKE_FORMAT_BACKEND") || discovery.OperationState is not ("COMPLETE" or "PARTIAL") || discovery.Errors is null || discovery.Endpoints is null ||
            discovery.OperationState == "COMPLETE" && (discovery.Errors.Count != 0 || discovery.CaptureHresult < 0 || discovery.RenderHresult < 0) ||
            discovery.OperationState == "PARTIAL" && discovery.Errors.Count == 0 && discovery.CaptureHresult >= 0 && discovery.RenderHresult >= 0)
            throw new InvalidDataException("Windows endpoint response has invalid provenance or operation state.");
        foreach (var endpoint in discovery.Endpoints)
        {
            if (endpoint is null || endpoint.EndpointIdEvidence is null || endpoint.FriendlyNameEvidence is null || endpoint.DeviceStateEvidence is null || endpoint.FormFactorEvidence is null || endpoint.SharedModeFormat is null || endpoint.SharedModeFormat.Property is null ||
                endpoint.Direction is not ("CAPTURE" or "RENDER") || endpoint.DeviceState is not ("ACTIVE" or "DISABLED" or "NOT_PRESENT" or "UNPLUGGED" or "UNKNOWN") ||
                endpoint.EndpointIdEvidence.State is not ("AVAILABLE" or "UNAVAILABLE" or "MALFORMED" or "UNKNOWN") ||
                endpoint.FriendlyNameEvidence.State is not ("AVAILABLE" or "UNAVAILABLE" or "MALFORMED" or "UNKNOWN") ||
                endpoint.DeviceStateEvidence.State is not ("AVAILABLE" or "UNAVAILABLE" or "MALFORMED" or "UNKNOWN") ||
                endpoint.FormFactorEvidence.State is not ("AVAILABLE" or "UNAVAILABLE" or "MALFORMED" or "UNKNOWN") ||
                endpoint.SharedModeFormat.Property.State is not ("AVAILABLE" or "UNAVAILABLE" or "MALFORMED" or "UNKNOWN") ||
                endpoint.SharedModeFormat.TopologyState is not ("UNKNOWN" or "REPRESENTABLE_1_TO_64" or "UNSUPPORTED_CHANNEL_COUNT") ||
                endpoint.SharedModeFormat.ParseState is not ("UNKNOWN" or "AVAILABLE" or "UNAVAILABLE" or "MALFORMED" or "VALID"))
                throw new InvalidDataException("Windows endpoint response contains an invalid direction, state or topology status.");
            if (endpoint.DeviceStateEvidence.State == "AVAILABLE" != endpoint.RawDeviceState.HasValue || endpoint.DeviceStateEvidence.State != "AVAILABLE" && endpoint.DeviceState != "UNKNOWN")
                throw new InvalidDataException("Windows device-state value contradicts its availability evidence.");
            var format = endpoint.SharedModeFormat;
            if (format.ParseState == "VALID" && (format.ReportedChannelCount is < 1 or > ushort.MaxValue || format.SampleRate is null or 0 || format.BitsPerSample is null or <= 0 || format.ValidBitsPerSample is null or <= 0 || format.FormatTag is null || format.Encoding is null) ||
                format.ParseState != "VALID" && (format.ReportedChannelCount is not null || format.SampleRate is not null || format.BitsPerSample is not null || format.ValidBitsPerSample is not null || format.FormatTag is not null || format.Encoding is not null))
                throw new InvalidDataException("Windows shared-mode format contains invalid numeric metadata.");
            if (format.ReportedChannelCount is > 64 && format.TopologyState != "UNSUPPORTED_CHANNEL_COUNT")
                throw new InvalidDataException("An unrepresentable channel count must remain explicitly unsupported.");
            if (format.ReportedChannelCount is <= 64 and >= 1 && format.TopologyState != "REPRESENTABLE_1_TO_64")
                throw new InvalidDataException("A representable channel count has an inconsistent topology state.");
            if (format.SpeakerMaskState is not ("UNKNOWN" or "VALID" or "INVALID"))
                throw new InvalidDataException("Windows speaker mask status is invalid.");
        }
    }
}

public static class DemoTopologyFixtures
{
    public static StreamTopologyDto Independent(int count) => Create(count);
    public static StreamTopologyDto StereoPairs(int pairs) => Create(pairs * 2, Enumerable.Range(0, pairs).Select(i => new ChannelGroupDto($"pair-{i + 1}", $"Stereo pair {i + 1}", "STEREO_PAIR", [i * 2 + 1, i * 2 + 2])).ToArray());
    public static StreamTopologyDto Mixed4Mono3Stereo1Six => Create(16,
        [new("mono-1", "Mono 1", "MONO", [1]), new("mono-2", "Mono 2", "MONO", [2]), new("mono-3", "Mono 3", "MONO", [3]), new("mono-4", "Mono 4", "MONO", [4]),
         new("stereo-1", "Stereo 1", "STEREO_PAIR", [5, 6]), new("stereo-2", "Stereo 2", "STEREO_PAIR", [7, 8]), new("stereo-3", "Stereo 3", "STEREO_PAIR", [9, 10]), new("group-6", "Six-channel group", "GROUP", [11, 12, 13, 14, 15, 16])]);
    public static IReadOnlyList<(int CaptureChannels, int RenderChannels)> AsymmetricCounts => [(2, 8), (32, 32), (48, 48), (64, 32), (16, 64)];

    public static StreamTopologyDto Create(int count, IReadOnlyList<ChannelGroupDto>? groups = null)
    {
        var result = new StreamTopologyDto(count, Enumerable.Range(1, count).Select(i => new ChannelDto(i)).ToArray(), groups);
        result.Validate();
        return result;
    }
}
