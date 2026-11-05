namespace KlidanAudioProbe.Gui.Core;

public enum ProbeScreen { Devices, Formats, Capture, Render, Reports, Diagnostics }
public enum OperatorStep { Device, Test, Result }
public enum SessionMode { Capture, Render }
public enum SessionLifecycle { Preparing, Ready, Started, Stopping, Stopped, Completed, Failed, Unknown }
public enum StreamQuality { Unknown, Clean, Warning, Failed }
public enum FormatStatus { SupportedReported, Unsupported, Failed, Unknown, NotTested, Initialized, Ready }
public enum ProbeEventKind { SessionRequested, ConsentAccepted, ConsentCancelled, Preparing, Ready, Started, MeterSnapshot, Stopping, Stopped, Completed, Warning, Failure, DeviceLoss, WorkerDisconnected }

public sealed record EndpointDto(string Name, string Direction, string State, string Format, int Channels, bool IsAvailable, bool IsDemo = true, StreamTopologyDto? Topology = null, IReadOnlyList<FormatTopologyDto>? FormatTopologies = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => IsDemo ? string.Format(Localization.CurrentCulture, Localization.Get("VmDemoEndpointDisplayName"), Direction, Channels) : Name;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayState => IsDemo ? Localization.Get(IsAvailable ? "VmDemoAvailable" : "VmDemoDisabled") : State;

    [System.Text.Json.Serialization.JsonIgnore]
    // shortcut: maps current demo format labels; add mappings when a fixture introduces another display format.
    public string DisplayFormat => !IsDemo ? Format : Format == "Неизвестно" ? Localization.Get("VmInspectorUnknown") : Format.Replace("кГц", Localization.Get("VmDemoKilohertz"), StringComparison.Ordinal);
}
public sealed record PeriodEvidence(FormatStatus Status, string? Hresult, string? InitializeHresult = null, string? Provenance = null)
{
    public string StatusCode => Status switch
    {
        FormatStatus.SupportedReported => "SUPPORTED_REPORTED",
        FormatStatus.Unsupported => "UNSUPPORTED",
        FormatStatus.Failed => "FAILED",
        FormatStatus.Unknown => "UNKNOWN",
        FormatStatus.NotTested => "NOT_TESTED",
        FormatStatus.Initialized => "INITIALIZED",
        FormatStatus.Ready => "READY",
        _ => "UNKNOWN"
    };

    public string AccessibleText => $"{StatusCode}. IsFormatSupported HRESULT: {Hresult ?? "неизвестно"}. Initialize HRESULT: {InitializeHresult ?? "не наблюдался"}. Происхождение: {Provenance ?? "неизвестно"}.";
}
public sealed record FormatDto(string Encoding, int SampleRate, int ChannelCount, PeriodEvidence DefaultPeriod, PeriodEvidence MinimumPeriod);
public sealed record MeterChannelDto(int Channel, double? PeakDbfs, double? RmsDbfs, double? PeakHoldDbfs, int? Clips, bool? Silent = null, string? StateText = null)
{
    public string AccessibleText => $"Канал Windows {Channel}. Peak {Level(PeakDbfs)}; RMS {Level(RmsDbfs)}; Peak Hold {Level(PeakHoldDbfs)}; клипы {Clips?.ToString() ?? "неизвестно"}; {StateText ?? "состояние неизвестно"}.";
    public string ChannelLabel => Channel.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    public string StateLabel => Silent == true ? "ТИШ" : Silent == false ? "СИГНАЛ" : StateText is null ? "НЕТ ДАННЫХ" : StateText.Contains("окна", StringComparison.OrdinalIgnoreCase) ? "БЕЗ ОКНА" : "НЕТ ДАННЫХ";
    public string PeakText => DisplayLevel("Peak", PeakDbfs, true);
    public string RmsText => DisplayLevel("RMS", RmsDbfs, true);
    public string PeakCompactText => ShortLevel(PeakDbfs);
    public string RmsCompactText => ShortLevel(RmsDbfs);
    public string LevelsCompactText => $"P {PeakCompactText} · R {RmsCompactText}";
    public string PeakHoldText => PeakHoldDbfs is double hold && double.IsFinite(hold) ? $"◆ {hold:0}" : "◆ —";
    public string ClipsText => Clips is > 0 ? $"!{Clips}" : Clips is 0 ? "0" : "?";
    private string DisplayLevel(string name, double? value, bool windowLevel) => value is double db && double.IsFinite(db) ? $"{name}: {db:0.0} dBFS" : windowLevel && Silent == true ? $"{name}: цифровая тишина" : $"{name}: нет данных";
    private static string Level(double? value) => value is double db && double.IsFinite(db) ? $"{db:0.0} dBFS" : "неизвестно";
    private static string ShortLevel(double? value) => value is double db && double.IsFinite(db) ? db.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "—";
}
public sealed record MeterSnapshotDto(long Sequence, DateTimeOffset Timestamp, IReadOnlyList<MeterChannelDto> Channels, bool IsCompleteWindow);
public sealed record SessionDto(Guid Id, SessionMode Mode, string EndpointName, int? RenderChannel, int DurationSeconds, SessionLifecycle Lifecycle, StreamQuality Quality, string Message, string? CleanupStatus, bool IsSynthetic = true, int ChannelCount = 2)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayEndpointName => IsSynthetic && EndpointName.StartsWith("ДЕМО · Данные симуляции ·", StringComparison.Ordinal)
        ? string.Format(Localization.CurrentCulture, Localization.Get("VmDemoEndpointDisplayName"), Mode, ChannelCount)
        : EndpointName;
}
public sealed record ProbeEventDto(long Sequence, DateTimeOffset Timestamp, Guid SessionId, ProbeEventKind Kind, string Message, string? Hresult = null, bool IsSynthetic = true);
public sealed record WorkerTimelineEntryDto(long Sequence, DateTimeOffset Timestamp, Guid SessionId, string Name, string Lifecycle, string StreamQuality, string CleanupEvidence, string Detail, long? ProtocolSequence = null, Guid? CorrelationId = null, bool IsSynthetic = true)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
    public string SourceText => IsSynthetic ? ProtocolSequence.HasValue ? "IPC · демо" : "Локально · демо" : ProtocolSequence.HasValue ? "IPC" : "Локально";
    public string SequenceText => ProtocolSequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string EventLabel => Name switch
    {
        "Preparing" or "Accepted" => "Подготовка", "Ready" => "Готова", "Started" => "Запущена", "MeterSnapshot" => "Снимок", "Stopping" => "Остановка", "Stopped" => "Остановлена", "Completed" => "Завершена", "Failed" or "Failure" => "Ошибка", "Warning" => "Предупреждение", "WorkerDisconnected" => "Worker отключён", "DeviceLoss" => "Потеря устройства", "SessionRequested" => "Запрос сессии", "ConsentAccepted" => "Согласие принято", "ConsentCancelled" => "Согласие отменено", "Rejected" => "Запуск отклонён", _ => Name
    };
    public string LifecycleLabel => Lifecycle switch { "COMPLETED" => "Завершена", "UNKNOWN" => "Неизвестно / не оценено", "STARTED" => "Запущена", "STOPPING" => "Остановка", "STOPPED" => "Остановлена", "FAILED" => "Ошибка", "READY" => "Готова", "PREPARING" => "Подготовка", "REQUESTED" => "Запрошена", "CONSENTED" => "Согласие принято", "CANCELLED" => "Отменена", _ => Lifecycle };
    public string QualityLabel => StreamQuality switch { "UNKNOWN" => "Неизвестно / не оценено", "CLEAN" => "Чисто", "WARNING" => "Есть предупреждения", "FAILED" => "Ошибка", _ => StreamQuality };
    public string CleanupLabel => CleanupEvidence switch { "SYNTHETIC_CLEANUP_COMPLETED" => "Фиктивная очистка завершена", "NOT_ATTEMPTED" => "Не выполнялась", "UNKNOWN" => "Неизвестно / не оценено", _ => CleanupEvidence };
    public string QualityTableLabel => StreamQuality == "UNKNOWN" ? "Не оценено" : QualityLabel;
    public string CleanupTableLabel => CleanupEvidence == "SYNTHETIC_CLEANUP_COMPLETED" ? "Фикт. очистка" : CleanupLabel;
    public string SessionShortId => SessionId == Guid.Empty ? "—" : SessionId.ToString("N")[..8];
    public string DiagnosticDetails => $"{EventLabel} · {LifecycleLabel} · {QualityLabel} · {CleanupLabel}{Environment.NewLine}Коды: {Name} · {Lifecycle} · {StreamQuality} · {CleanupEvidence}{Environment.NewLine}Сессия: {(SessionId == Guid.Empty ? "не указана" : SessionId.ToString("D"))}{(CorrelationId is Guid correlation ? $"{Environment.NewLine}Correlation: {correlation:D}" : string.Empty)}{Environment.NewLine}{Detail}";
}
public sealed record InspectorFieldDto(string Label, string Value, string? Tooltip = null, bool Copyable = false);
public sealed record InspectorSectionDto(string Heading, IReadOnlyList<InspectorFieldDto> Fields);
public sealed record ReportDto(string Name, SessionMode Mode, SessionLifecycle Lifecycle, StreamQuality Quality, DateTimeOffset Created, string Evidence, bool IsSynthetic = true);
public sealed record SelectionOption(int Value, string Label);
public sealed record StringSelectionOption(string Value, string Label);
public sealed record ChoiceOption(string Value, string Label);

public interface IProbeClient
{
    IReadOnlyList<EndpointDto> ListEndpoints(SessionMode mode);
    IReadOnlyList<FormatDto> QueryFormatMatrix();
    IReadOnlyList<ReportDto> ListReports();
}

public sealed class FakeProbeClient : IProbeClient
{
    public IReadOnlyList<EndpointDto> ListEndpoints(SessionMode mode) => mode == SessionMode.Capture
        ? [new("ДЕМО · Данные симуляции · Capture · 2 канала", "Capture", "Демо · доступно", "48 кГц · PCM16", 2, true, Topology: DemoTopologyFixtures.Independent(2)), new("ДЕМО · Данные симуляции · Capture · 32 канала", "Capture", "Демо · отключено", "Неизвестно", 32, false, Topology: DemoTopologyFixtures.Independent(32)), new("ДЕМО · Данные симуляции · Capture · 64 канала", "Capture", "Демо · доступно", "48 кГц · PCM16", 64, true, Topology: DemoTopologyFixtures.Independent(64), FormatTopologies: [new("PCM16-48000", DemoTopologyFixtures.Independent(64)), new("FLOAT32-48000", DemoTopologyFixtures.Independent(48))])]
        : [new("ДЕМО · Данные симуляции · Render · 8 каналов", "Render", "Демо · доступно", "48 кГц · PCM16", 8, true, Topology: DemoTopologyFixtures.Independent(8)), new("ДЕМО · Данные симуляции · Render · 64 канала", "Render", "Демо · доступно", "48 кГц · PCM16", 64, true, Topology: DemoTopologyFixtures.Independent(64), FormatTopologies: [new("PCM16-48000", DemoTopologyFixtures.Independent(64)), new("FLOAT32-48000", DemoTopologyFixtures.Independent(32))])];

    public IReadOnlyList<FormatDto> QueryFormatMatrix() =>
    [
        F("PCM16",44100,1,FormatStatus.SupportedReported,FormatStatus.Unsupported,"S_OK","AUDCLNT_E_UNSUPPORTED_FORMAT"),
        F("PCM16",48000,1,FormatStatus.SupportedReported,FormatStatus.SupportedReported,"S_OK","S_OK"),
        F("PCM16",44100,2,FormatStatus.Unsupported,FormatStatus.NotTested,"AUDCLNT_E_UNSUPPORTED_FORMAT",null),
        F("PCM16",48000,2,FormatStatus.SupportedReported,FormatStatus.SupportedReported,"S_OK","S_OK"),
        F("PCM24-in-32",44100,1,FormatStatus.Unknown,FormatStatus.Unsupported,null,"AUDCLNT_E_UNSUPPORTED_FORMAT"),
        F("PCM24-in-32",48000,1,FormatStatus.Unsupported,FormatStatus.Unsupported,"AUDCLNT_E_UNSUPPORTED_FORMAT","AUDCLNT_E_UNSUPPORTED_FORMAT"),
        F("PCM24-in-32",44100,2,FormatStatus.SupportedReported,FormatStatus.Failed,"S_OK","AUDCLNT_E_DEVICE_IN_USE"),
        F("PCM24-in-32",48000,2,FormatStatus.Unsupported,FormatStatus.NotTested,"AUDCLNT_E_UNSUPPORTED_FORMAT",null),
        F("Float32",44100,1,FormatStatus.Failed,FormatStatus.NotTested,"AUDCLNT_E_DEVICE_IN_USE",null),
        F("Float32",48000,1,FormatStatus.SupportedReported,FormatStatus.SupportedReported,"S_OK","S_OK"),
        F("Float32",44100,2,FormatStatus.Unsupported,FormatStatus.Unsupported,"AUDCLNT_E_UNSUPPORTED_FORMAT","AUDCLNT_E_UNSUPPORTED_FORMAT"),
        F("Float32",48000,2,FormatStatus.SupportedReported,FormatStatus.Failed,"S_OK","AUDCLNT_E_DEVICE_IN_USE")
    ];

    public IReadOnlyList<ReportDto> ListReports() =>
    [
        new("Демо · Capture baseline", SessionMode.Capture, SessionLifecycle.Completed, StreamQuality.Clean, DateTimeOffset.Parse("2026-10-30T10:00:00Z"), "Синтетические данные · пропусков нет"),
        new("Демо · Capture warning", SessionMode.Capture, SessionLifecycle.Completed, StreamQuality.Warning, DateTimeOffset.Parse("2026-10-30T10:10:00Z"), "Синтетическое предупреждение времени Capture"),
        new("Демо · Render interrupted", SessionMode.Render, SessionLifecycle.Unknown, StreamQuality.Unknown, DateTimeOffset.Parse("2026-10-30T10:20:00Z"), "Остановка неизвестна после отключения worker")
    ];

    private static FormatDto F(string encoding, int rate, int channels, FormatStatus defaultStatus, FormatStatus minimumStatus, string? defaultHr, string? minimumHr) =>
        new(encoding, rate, channels, Evidence(defaultStatus, defaultHr), Evidence(minimumStatus, minimumHr));

    private static PeriodEvidence Evidence(FormatStatus status, string? hresult) =>
        new(status, hresult, Provenance: status switch
        {
            FormatStatus.SupportedReported or FormatStatus.Unsupported or FormatStatus.Failed => "PASSIVE_QUERY",
            FormatStatus.Initialized or FormatStatus.Ready => "INITIALIZE_ONLY",
            _ => null
        });
}
