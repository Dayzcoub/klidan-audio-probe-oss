using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Diagnostics;

namespace KlidanAudioProbe.Gui.Core;

public interface ILaboratoryEvidenceBundleExportInteraction
{
    Task<string?> PickDestinationAsync(string suggestedFileName);
    Task<bool> ConfirmAsync(string message);
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class ParameterCommand(Action<object?> execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public event Action<Exception>? ExecutionFailed;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); }
        catch (Exception ex)
        {
            if (ExecutionFailed is { } handlers)
                foreach (Action<Exception> handler in handlers.GetInvocationList()) try { handler(ex); } catch { }
        }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public abstract class ViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
        return true;
    }
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class MainViewModel : ViewModel
{
    public const int EventHistoryLimit = 200;
    private readonly IProbeClient _client;
    private readonly IWorkerClient? _worker;
    private readonly IAutomaticScanWorkflow? _automaticScanWorkflow;
    private readonly IDuplexRepeatabilityWorkflow? _duplexRepeatabilityWorkflow;
    private readonly IExclusivePeriodDiscoveryWorkflow? _exclusivePeriodDiscoveryWorkflow;
    private readonly ICustomPeriodDuplexWorkflow? _customPeriodDuplexWorkflow;
    private readonly string? _profileDirectory;
    private readonly ILaboratoryEvidenceBundleExportInteraction? _bundleExportInteraction;
    private readonly Func<string, LaboratoryEvidenceBundleDirectories>? _bundleDirectoriesFactory;
    private readonly DeviceProfileBindingRevalidationState _savedProfileBindingState = new();
    private EvidenceLibrarySnapshot _evidenceLibrary = new([], DateTimeOffset.UtcNow);
    private string _evidenceLibraryDeviceFilter = "", _evidenceLibraryTypeFilter = "", _evidenceLibraryProvenanceFilter = "";
    private EvidenceLibraryEntry? _selectedEvidenceLibraryEntry;
    private string _evidenceLibraryComparisonMode = nameof(DuplexComparisonMode.ExactConfigurationRepeatability);
    private readonly SynchronizationContext? _uiContext;
    private readonly object _workerUiSync = new();
    private readonly WorkerUiEventBuffer _workerUiEventBuffer = new();
    private bool _workerUiDrainPosted, _workerUiOverflowed, _workerMeterStale, _workerMeterFinal;
    private int _workerMeterBank;
    private WorkerCaptureMeterSnapshotDto? _workerCaptureMeter;
    private WorkerRenderMeterSnapshotDto? _workerRenderMeter;
    private ProbeScreen _screen = ProbeScreen.Devices;
    private OperatorStep _operatorStep = OperatorStep.Device;
    private bool _developerToolsOpen;
    private readonly bool _developerToolsAvailable;
    private string _operatorTestMode = "EXCLUSIVE_FORMAT_QUERY", _realInitializePeriod = "default";
    private string _operatorResultEndpoint = "", _operatorResultMode = "", _operatorResultConfiguration = "";
    private string _deviceProfileSaveMessage = "";
    private string _operatorResultProvenance = Localization.Get("VmOperatorResultProvenanceEmpty");
    private RealFormatUniqueRowDto? _selectedRealFormatRow;
    private string _operatorResultOutcome = Localization.Get("VmOperatorResultOutcomeEmpty");
    private SessionMode _direction = SessionMode.Capture;
    private EndpointDto? _selectedCaptureEndpoint;
    private EndpointDto? _selectedRenderEndpoint;
    private FormatDto? _selectedFormat;
    private ReportDto? _selectedReport;
    private SessionDto? _session;
    private MeterSnapshotDto? _meter;
    private string _notice = Localization.Get("VmSyntheticBuildNotice");
    private bool _confirmPending;
    private bool _consentChecked;
    private long _eventSequence;
    private int _renderChannel = 1;
    private int _renderBank;
    private int _captureBank;
    private int _durationSeconds = 3;
    private Guid? _pendingSessionId;
    private PendingSession? _pending;

    private IReadOnlyList<WorkerEndpointDto> _workerEndpoints = [];
    private IReadOnlyList<WorkerEndpointDto> _lastWorkerEndpointSnapshot = [];
    private WindowsEndpointDiscoveryDto? _windowsEndpointSnapshot;
    private WindowsEndpointDiscoveryDto? _lastWindowsEndpointSnapshot;
    private bool _windowsDiscoveryCurrent, _windowsDiscoveryLoading;
    private string? _windowsDiscoveryError;
    private string? _windowsDiscoveryGeneration;
    private int _bindingCheckVersion;
    private bool _bindingCheckRunning;
    private bool _bindingCheckExpectedInitialConnection;
    private int? _bindingWorkerProcessId;
    private IReadOnlyList<DeviceConfigurationProfileStorage.SavedProfileEntry> _savedDeviceProfiles = [];
    private IReadOnlyList<DeviceConfigurationProfileStorage.SavedProfileEntry> _matchingSavedProfiles = [];
    private DeviceConfigurationProfileStorage.SavedProfileEntry? _selectedSavedDeviceProfile;
    private IReadOnlyList<HistoricalSustainedScanEntry> _historicalSustainedScans = [];
    private HistoricalSustainedScanEntry? _selectedHistoricalSustainedScan;
    private OfflineSustainedProfileRecoveryPreview? _historicalRecoveryPreview;
    private bool _historicalRecoveryConfirmationChecked, _historicalRecoveryRunning, _historicalRecoverySaving;
    private int _historicalRecoveryVersion;
    private string _historicalRecoveryStatus = Localization.Get("VmHistoricalRecoveryChooseMeasurement");
    private string? _historicalRecoveryTechnicalError;
    private DeviceProfileBindingRevalidationResult? _savedProfileBindingResult;
    private string _savedProfileOperatorStatus = Localization.Get("VmSavedProfileCheckConnection");
    private string _savedProfileOperatorHistory = Localization.Get("VmSavedProfileNoHistorySelected");
    private string _savedProfileOperatorLimitation = Localization.Get("VmSavedProfileCheckLimitation");
    private WindowsEndpointDto? _selectedWindowsEndpoint;
    private PhysicalDeviceGroupDto? _selectedPhysicalDeviceGroup;
    private IReadOnlyList<PhysicalDeviceGroupDto> _physicalDeviceGroups = [];
    private bool _allowUnconfirmedPhysicalPair;
    private RealFormatMatrixDto? _realFormatMatrix, _realInitializationResult;
    private int[] _realQueriedChannelCounts = [1, 2];
    private bool _realQueryInvalidated;
    private bool _realQueryRunning, _realInitializeRunning;
    private RealFormatCandidateDto? _selectedRealFormatCandidate, _pendingRealFormatCandidate;
    private string? _pendingRealEndpointId, _pendingRealDirection;
    private bool _realInitializeConsentPending, _realInitializeConsentChecked;
    private string _realQueryChannelCountsText = "1,2";
    private string? _realFormatError, _realInitializationError;
    private DeviceScanResult? _deviceScanResult;
    private DeviceScanConsent? _pendingDeviceScanConsent;
    private CancellationTokenSource? _deviceScanCancellation;
    private CancellationTokenSource? _streamValidationCancellation;
    private string? _activeStreamOperationId;
    private RealFormatCandidateDto? _selectedStreamCandidate;
    private RealStreamConsentDto? _pendingStreamConsent;
    private bool _streamConsentPending, _streamConsentChecked, _streamValidationRunning;
    private string _streamValidationStatus = Localization.Get("VmStreamNotValidated");
    private string _streamValidationProgress = "";
    private readonly List<RealStreamValidationDto> _streamValidations = [];
    private readonly Dictionary<string, StreamValidationResultRow> _streamValidationRows = new(StringComparer.Ordinal)
    {
        ["MINIMUM"] = StreamValidationResultRow.NotRun("MINIMUM"),
        ["DEFAULT"] = StreamValidationResultRow.NotRun("DEFAULT")
    };
    private string? _savedStreamProfilePath;
    private string? _lastStreamAttemptPeriod;
    private int? _savedStreamProfileVersion;
    private HashSet<Guid> _savedStreamOperationIds = [];
    private bool _streamAttemptedSinceProfileSave;
    private IReadOnlyList<RealFormatMatrixDto> _deviceScanInitializations = [];
    private bool _deviceScanConsentPending, _deviceScanConsentChecked, _deviceScanRunning;
    private string _deviceScanProgress = Localization.Get("VmDeviceScanNotStarted");
    private AutomaticScanPreparation? _automaticScanPreparation;
    private ScanRunResult? _automaticScanResult;
    private CancellationTokenSource? _automaticScanCancellation;
    private Task? _automaticScanTask;
    private bool _automaticScanPairEnabled, _automaticScanPreparing, _automaticScanConsentPending, _automaticScanConsentChecked, _automaticScanRunning, _automaticScanOutcomeUnknown, _automaticScanAttempted, _automaticScanTerminal;
    private string _automaticSustainedMode = "Standard (300 s)";
    private string _automaticScanStatus = Localization.Get("VmAutomaticScanNotStarted");
    private bool _automaticScanExportRunning;
    private string _automaticScanExportStatus = Localization.Get("VmAutomaticScanExportNotPrepared");
    private string _automaticScanExportTechnicalDetails = Localization.Get("VmAutomaticScanExportDetailsMissing");
    private LaboratoryEvidenceBundlePreparation? _automaticScanExportPreparation;
    private readonly ObservableCollection<AutomaticScanProgressRow> _automaticScanProgressRows = [];
    private string _windowsEndpointSearchText = string.Empty, _windowsEndpointDirectionFilter = "ALL";
    private bool _windowsEndpointActiveOnly = true;
    private WindowsEndpointDto? _selectedDuplexCapture, _selectedDuplexRender;
    private string? _pendingDuplexPeriod, _activeDuplexOperationId;
    private DuplexConsentDto? _pendingDuplexConsent;
    private bool _duplexConsentPending, _duplexConsentChecked, _duplexValidationRunning;
    private string _duplexStatus = Localization.Get("VmDuplexNotStarted");
    private readonly Dictionary<string, DuplexValidationDto> _duplexValidations = new(StringComparer.Ordinal);
    private DuplexRepeatabilityPreparation? _duplexRepeatabilityPreparation;
    private DuplexRepeatabilityResult? _duplexRepeatabilityResult;
    private CancellationTokenSource? _duplexRepeatabilityCancellation;
    private bool _duplexRepeatabilityConsentPending, _duplexRepeatabilityConsentChecked, _duplexRepeatabilityRunning;
    private string _duplexRepeatabilityStatus = Localization.Get("VmDuplexRepeatabilityNotStarted");
    private DuplexRepeatabilityPreparation? _customRepeatabilityPreparation;
    private DuplexRepeatabilityResult? _customRepeatabilityResult;
    private CancellationTokenSource? _customRepeatabilityCancellation;
    private bool _customRepeatabilityConsentPending, _customRepeatabilityConsentChecked, _customRepeatabilityRunning;
    private string _customRepeatabilityStatus = Localization.Get("VmCustomRepeatabilityNotStarted");
    private PeriodDiscoveryPreparation? _periodDiscoveryPreparation;
    private PeriodDiscoverySeriesResult? _periodDiscoveryResult;
    private string? _periodDiscoveryResultFingerprint;
    private bool _periodDiscoveryConsentPending, _periodDiscoveryConsentChecked, _periodDiscoveryRunning;
    private CancellationTokenSource? _periodDiscoveryCancellation;
    private string _periodDiscoveryStatus = Localization.Get("VmPeriodDiscoveryNotStarted");
    private string _customPeriodFramesText = "192", _customPeriodDuplexStatus = "Custom-period Duplex не запускался.";
    private CustomPeriodDuplexPreparation? _customPeriodDuplexPreparation;
    private CustomPeriodDuplexResult? _customPeriodDuplexResult;
    private bool _customPeriodDuplexConsentPending, _customPeriodDuplexConsentChecked, _customPeriodDuplexRunning;
    private CancellationTokenSource? _customPeriodDuplexCancellation;
    private bool _workerAttempted;
    private WorkerEndpointDto? _selectedWorkerEndpoint;
    private FormatTopologyDto? _selectedWorkerFormat;
    private int _workerRenderChannel = 1, _workerDurationSeconds = 3;
    private string _workerPeriod = "DEFAULT", _workerMmcss = "OFF", _workerScenario = "NORMAL";
    private bool _workerConsentPending, _workerConsentChecked;
    private bool _workerStartOutcomeUnknown;
    private int _lateWorkerStartAccepted, _lateWorkerStartRejected;
    private WorkerSessionSettingsDto? _workerPendingSettings;
    private WorkerSessionSettingsDto? _workerSessionSettings;
    private SessionDto? _workerSession;
    private string _workerNotice = Localization.Get("VmWorkerFakeNotStarted");
    private long _workerTimelineSequence;
    private readonly List<WorkerEventDto> _earlyWorkerEvents = [];
    private TaskCompletionSource? _workerTerminal;
    public MainViewModel(IProbeClient? client = null, IWorkerClient? worker = null, IAutomaticScanWorkflow? automaticScanWorkflow = null, IDuplexRepeatabilityWorkflow? duplexRepeatabilityWorkflow = null, IExclusivePeriodDiscoveryWorkflow? exclusivePeriodDiscoveryWorkflow = null, ICustomPeriodDuplexWorkflow? customPeriodDuplexWorkflow = null, string? profileDirectory = null, ILaboratoryEvidenceBundleExportInteraction? bundleExportInteraction = null, Func<string, LaboratoryEvidenceBundleDirectories>? bundleDirectoriesFactory = null, bool developerToolsAvailable = true)
    {
        _client = client ?? new FakeProbeClient();
        _worker = worker;
        _automaticScanWorkflow = automaticScanWorkflow;
        _duplexRepeatabilityWorkflow = duplexRepeatabilityWorkflow;
        _exclusivePeriodDiscoveryWorkflow = exclusivePeriodDiscoveryWorkflow;
        _customPeriodDuplexWorkflow = customPeriodDuplexWorkflow;
        _profileDirectory = profileDirectory;
        _bundleExportInteraction = bundleExportInteraction;
        _bundleDirectoriesFactory = bundleDirectoriesFactory;
        _developerToolsAvailable = developerToolsAvailable;
        _uiContext = SynchronizationContext.Current;
        CaptureEndpoints = new(_client.ListEndpoints(SessionMode.Capture));
        RenderEndpoints = new(_client.ListEndpoints(SessionMode.Render));
        Formats = new(_client.QueryFormatMatrix());
        Reports = new(_client.ListReports());
        RefreshEvidenceLibrary();
        Events = [];
        _selectedFormat = Formats.FirstOrDefault();
        _selectedReport = Reports.FirstOrDefault();

        NavigateCommand = new(p => Navigate(Enum.Parse<ProbeScreen>(p!.ToString()!)), () => _developerToolsAvailable);
        OperatorStepCommand = new(p => NavigateOperatorStep(Enum.Parse<OperatorStep>(p!.ToString()!)));
        OpenDeveloperToolsCommand = new(OpenDeveloperTools, () => _developerToolsAvailable);
        OpenManualRealToolsCommand = new(OpenManualRealTools, () => _developerToolsAvailable);
        CloseDeveloperToolsCommand = new(CloseDeveloperTools);
        DirectionCommand = new(p => Direction = Enum.Parse<SessionMode>(p!.ToString()!));
        BeginCommand = new(RequestConfirmation, CanBegin);
        ConfirmCommand = new(ConfirmStart, () => ConfirmPending && ConsentChecked && _pending is not null);
        CancelConfirmationCommand = new(CancelConfirmation, () => ConfirmPending);
        StopCommand = new(BeginStop, () => Session?.Lifecycle == SessionLifecycle.Started);
        NextPulseCommand = new(NextPulse, () => Session is { Mode: SessionMode.Capture, Lifecycle: SessionLifecycle.Started });
        DisconnectCommand = new(DisconnectWorker, () => Session?.Lifecycle == SessionLifecycle.Started);
        FailCommand = new(FailSession, () => Session?.Lifecycle == SessionLifecycle.Started);
        WarningCommand = new(CompleteWithWarning, () => Session is { Mode: SessionMode.Capture, Lifecycle: SessionLifecycle.Started });
        DeviceLossCommand = new(SimulateDeviceLoss, () => Session?.Lifecycle == SessionLifecycle.Started);
        EmptyDevicesCommand = new(() => { CaptureEndpoints.Clear(); RenderEndpoints.Clear(); SelectedCaptureEndpoint = null; SelectedRenderEndpoint = null; EndpointListChanged(); });
        RestoreDevicesCommand = new(RestoreDevices);
        if (_worker is not null) { WorkerState = _worker.State; _workerAttempted = WorkerState.State != WorkerConnectionState.Disconnected; _worker.StateChanged += WorkerStateChanged; _worker.SessionAccepted += WorkerSessionAccepted; _worker.EventReceived += WorkerEventReceived; _worker.TransportFault += WorkerTransportFaulted; }
        ConnectWorkerCommand = new(ConnectWorkerAsync, () => _worker is not null && WorkerState.State == WorkerConnectionState.Disconnected && !_workerAttempted);
        QueryWorkerEndpointsCommand = new(QueryWorkerEndpointsAsync, () => WorkerState.State == WorkerConnectionState.ConnectedFake);
        DiscoverWindowsEndpointsCommand = new(DiscoverWindowsEndpointsAsync, CanDiscoverWindowsEndpoints);
        PrepareAutomaticScanCommand = new(PrepareAutomaticScanAsync, CanPrepareAutomaticScan);
        ConfirmAutomaticScanCommand = new(ConfirmAutomaticScanAsync, () => AutomaticScanConsentPending && AutomaticScanConsentChecked && _automaticScanPreparation is not null && !_automaticScanRunning);
        CancelAutomaticScanConsentCommand = new(CancelAutomaticScanConsent, () => AutomaticScanConsentPending);
        CancelAutomaticScanCommand = new(CancelAutomaticScan, () => _automaticScanPreparing || _automaticScanRunning);
        OpenAutomaticScanLocationCommand = new(OpenAutomaticScanLocation, () => _automaticScanResult?.Persistence?.SummaryPath is { Length: > 0 });
        ExportAutomaticScanCommand = new(ExportAutomaticScanAsync, () => CanExportAutomaticScan);
        QueryRealFormatsCommand = new(QueryRealFormatsAsync, CanQueryRealFormats);
        RequestRealInitializeCommand = new(RequestRealInitialize, CanRequestRealInitialize);
        ConfirmRealInitializeCommand = new(ConfirmRealInitializeAsync, () => RealInitializeConsentPending && RealInitializeConsentChecked && _pendingRealFormatCandidate is not null);
        CancelRealInitializeCommand = new(CancelRealInitialize, () => RealInitializeConsentPending);
        RequestDeviceScanCommand = new(RequestDeviceScan, () => CanStartDeviceScan);
        ConfirmDeviceScanCommand = new(ConfirmDeviceScanAsync, () => DeviceScanConsentPending && DeviceScanConsentChecked && _pendingDeviceScanConsent is not null && !_deviceScanRunning);
        CancelDeviceScanConsentCommand = new(CancelDeviceScanConsent, () => DeviceScanConsentPending);
        CancelDeviceScanCommand = new(CancelDeviceScan, () => _deviceScanRunning);
        RequestStreamValidationCommand = new(RequestStreamValidation, CanRequestStreamValidation);
        ConfirmStreamValidationCommand = new(ConfirmStreamValidationAsync, () => _streamConsentPending && _streamConsentChecked && _pendingStreamConsent is not null && !_streamValidationRunning);
        CancelStreamConsentCommand = new(CancelStreamConsent, () => _streamConsentPending);
        CancelStreamValidationCommand = new(CancelStreamValidationAsync, () => _streamValidationRunning && _activeStreamOperationId is not null);
        RequestDuplexMinimumCommand = new(() => RequestDuplexValidation("MINIMUM"), CanRequestDuplexValidation);
        RequestDuplexDefaultCommand = new(() => RequestDuplexValidation("DEFAULT"), CanRequestDuplexValidation);
        ConfirmDuplexValidationCommand = new(ConfirmDuplexValidationAsync, () => DuplexConsentPending && DuplexConsentChecked && _pendingDuplexConsent is not null && !_duplexValidationRunning);
        CancelDuplexConsentCommand = new(CancelDuplexConsent, () => DuplexConsentPending);
        CancelDuplexValidationCommand = new(CancelDuplexValidationAsync, () => _duplexValidationRunning && _activeDuplexOperationId is not null);
        PrepareDuplexRepeatabilityCommand = new(PrepareDuplexRepeatabilityAsync, () => CanPrepareDuplexRepeatability);
        ConfirmDuplexRepeatabilityCommand = new(ConfirmDuplexRepeatabilityAsync, () => _duplexRepeatabilityConsentPending && _duplexRepeatabilityConsentChecked && _duplexRepeatabilityPreparation is not null && !_duplexRepeatabilityRunning);
        CancelDuplexRepeatabilityConsentCommand = new(CancelDuplexRepeatabilityConsent, () => _duplexRepeatabilityConsentPending);
        CancelDuplexRepeatabilityCommand = new(CancelDuplexRepeatability, () => _duplexRepeatabilityRunning);
        PreparePeriodDiscoveryCommand = new(PreparePeriodDiscoveryAsync, CanPreparePeriodDiscoveryInternal);
        ConfirmPeriodDiscoveryCommand = new(ConfirmPeriodDiscoveryAsync, () => _periodDiscoveryConsentPending && _periodDiscoveryConsentChecked && _periodDiscoveryPreparation is not null && !_periodDiscoveryRunning);
        CancelPeriodDiscoveryCommand = new(() => CancelPeriodDiscovery(), () => _periodDiscoveryConsentPending);
        CancelPeriodDiscoveryRunCommand = new(CancelPeriodDiscoveryRun, () => _periodDiscoveryRunning);
        PrepareCustomPeriodDuplexCommand = new(PrepareCustomPeriodDuplexAsync, () => CanPrepareCustomPeriodDuplex);
        ConfirmCustomPeriodDuplexCommand = new(ConfirmCustomPeriodDuplexAsync, () => _customPeriodDuplexConsentPending && _customPeriodDuplexConsentChecked && _customPeriodDuplexPreparation is not null && !_customPeriodDuplexRunning);
        CancelCustomPeriodDuplexConsentCommand = new(() => CancelCustomPeriodDuplexConsent(), () => _customPeriodDuplexConsentPending);
        CancelCustomPeriodDuplexCommand = new(CancelCustomPeriodDuplex, () => _customPeriodDuplexRunning);
        PrepareCustomRepeatabilityCommand = new(PrepareCustomRepeatabilityAsync, () => CanPrepareCustomRepeatability);
        ConfirmCustomRepeatabilityCommand = new(ConfirmCustomRepeatabilityAsync, () => _customRepeatabilityConsentPending && _customRepeatabilityConsentChecked && _customRepeatabilityPreparation is not null && !_customRepeatabilityRunning);
        CancelCustomRepeatabilityConsentCommand = new(() => CancelCustomRepeatabilityConsent(), () => _customRepeatabilityConsentPending);
        CancelCustomRepeatabilityCommand = new(CancelCustomRepeatability, () => _customRepeatabilityRunning);
        RefreshEvidenceLibraryCommand = new(RefreshEvidenceLibraryAsync);
        RefreshSavedProfilesCommand = new(RefreshSavedProfilesAsync);
        RefreshHistoricalSustainedScansCommand = new(RefreshHistoricalSustainedScans);
        PreviewHistoricalSustainedRecoveryCommand = new(PreviewHistoricalSustainedRecoveryAsync, CanPreviewHistoricalSustainedRecovery);
        SaveHistoricalSustainedRecoveryCommand = new(SaveHistoricalSustainedRecoveryAsync, CanSaveHistoricalSustainedRecovery);
        CancelHistoricalSustainedRecoveryCommand = new(CancelHistoricalSustainedRecovery, () => _historicalRecoveryPreview is not null || _historicalRecoveryRunning || _historicalRecoverySaving);
        CheckSavedProfileBindingCommand = new(CheckSavedProfileBindingAsync, CanCheckSavedProfileBinding);
        OpenEvidenceLibraryLocationCommand = new(OpenEvidenceLibraryLocation, () => _selectedEvidenceLibraryEntry is not null);
        ShutdownWorkerCommand = new(ShutdownWorkerAsync, () => WorkerState.State == WorkerConnectionState.ConnectedFake);
        RequestWorkerCaptureCommand = new(() => RequestWorkerSession("CAPTURE"), CanRequestWorkerCapture);
        RequestWorkerRenderCommand = new(() => RequestWorkerSession("RENDER"), CanRequestWorkerRender);
        ConfirmWorkerSessionCommand = new(ConfirmWorkerSessionAsync, () => WorkerConsentPending && WorkerConsentChecked && _workerPendingSettings is not null);
        CancelWorkerConsentCommand = new(CancelWorkerConsent, () => WorkerConsentPending);
        StopWorkerSessionCommand = new(StopWorkerSessionAsync, () => WorkerSession?.Lifecycle is SessionLifecycle.Preparing or SessionLifecycle.Ready or SessionLifecycle.Started);
        foreach (var command in new[] { ConnectWorkerCommand, QueryWorkerEndpointsCommand, DiscoverWindowsEndpointsCommand, QueryRealFormatsCommand, ConfirmRealInitializeCommand, ConfirmDeviceScanCommand, ShutdownWorkerCommand, ConfirmWorkerSessionCommand, StopWorkerSessionCommand })
            command.ExecutionFailed += ex => WorkerNotice = $"Операция fake worker не выполнена: {ex.GetType().Name}: {ex.Message}";
        ConfirmStreamValidationCommand.ExecutionFailed += ex => _streamValidationStatus = $"Stream validation failed: {ex.GetType().Name}: {ex.Message}";
        ConfirmDuplexValidationCommand.ExecutionFailed += ex => { _duplexStatus = $"Duplex failed/unknown: {ex.GetType().Name}: {ex.Message}"; Changed(nameof(DuplexStatus)); };
        PrepareAutomaticScanCommand.ExecutionFailed += ex => SetAutomaticScanStatus($"Сканирование не подготовлено: {ex.GetType().Name}: {ex.Message}");
        ConfirmAutomaticScanCommand.ExecutionFailed += ex => SetAutomaticScanStatus($"Исход неизвестен: {ex.GetType().Name}: {ex.Message}");
        _bindingWorkerProcessId = WorkerState.ProcessId;
        RefreshSavedProfiles();
        RefreshHistoricalSustainedScans();
        RefreshCanExecute();
        RefreshContext();
    }

    public ProbeScreen Screen { get => _screen; private set => Set(ref _screen, value); }
    public OperatorStep WorkflowStep { get => _operatorStep; private set { if (Set(ref _operatorStep, value)) { Changed(nameof(WorkflowStepLabel)); Changed(nameof(OperatorSelectedDevice)); } } }
    public string WorkflowStepLabel => WorkflowStep switch { OperatorStep.Device => Localization.Get("VmWorkflowDeviceStep"), OperatorStep.Test => Localization.Get("VmWorkflowTestStep"), _ => Localization.Get("VmWorkflowResultStep") };
    public bool DeveloperToolsOpen { get => _developerToolsOpen; set { if (Set(ref _developerToolsOpen, value)) { Changed(nameof(WorkerStatusVisible)); RefreshContext(); } } }
    public bool DeveloperToolsAvailable => _developerToolsAvailable;
    public IReadOnlyList<StringSelectionOption> LanguageOptions => Localization.Languages;
    public string SelectedLanguage
    {
        get => Localization.CurrentCulture.Name;
        set
        {
            if (value is not ("ru-RU" or "en-US") || value == SelectedLanguage) return;
            Localization.SetLanguage(value);
            Changed(nameof(SelectedLanguage));
            Changed(nameof(LanguageRestartNotice));
        }
    }
    public string LanguageRestartNotice => Localization.Get("LanguageRestartNotice");
    public bool WorkerStatusVisible => DeveloperToolsOpen || WorkerOperationActive;
    public IReadOnlyList<StringSelectionOption> OperatorTestModes { get; } = [new("EXCLUSIVE_FORMAT_QUERY", Localization.Get("VmOperatorExclusiveFormatQuery")), new("INITIALIZE_ONLY", Localization.Get("VmOperatorInitializeOnly"))];
    public IReadOnlyList<StringSelectionOption> RealInitializePeriods { get; } = [new("default", Localization.Get("VmDefaultPeriod")), new("minimum", Localization.Get("VmMinimumPeriod"))];
    public string OperatorTestMode { get => _operatorTestMode; set { if (value is "EXCLUSIVE_FORMAT_QUERY" or "INITIALIZE_ONLY" && Set(ref _operatorTestMode, value)) { Changed(nameof(NeedsInitializeFormatQuery)); Changed(nameof(RealInitializeDisabledReason)); QueryRealFormatsCommand.Refresh(); RequestRealInitializeCommand.Refresh(); } } }
    public string RealInitializePeriod { get => _realInitializePeriod; set { if (value is "default" or "minimum" && Set(ref _realInitializePeriod, value)) { SyncSelectedRealFormatCandidate(); Changed(nameof(RealSelectedCandidateDetails)); Changed(nameof(RealInitializeDisabledReason)); RequestRealInitializeCommand.Refresh(); } } }
    public RealFormatUniqueRowDto? SelectedRealFormatRow
    {
        get => _selectedRealFormatRow;
        set { if (Set(ref _selectedRealFormatRow, value)) { SyncSelectedRealFormatCandidate(); Changed(nameof(RealSelectedCandidateDetails)); Changed(nameof(RealInitializeDisabledReason)); RequestRealInitializeCommand.Refresh(); } }
    }
    public IReadOnlyList<RealFormatUniqueRowDto> RealFormatRows => RealFormatMatrix is { } matrix
        ? RealFormatMatrixValidation.BuildUniqueRows(matrix, _realQueriedChannelCounts).OrderBy(FormatStatusOrder).ThenBy(r => r.SampleRate).ThenBy(r => r.Channels).ThenBy(r => r.Format, StringComparer.Ordinal).ToArray()
        : [];
    public string RealFormatSummary
    {
        get
        {
            var rows = RealFormatRows;
            var tested = rows.Count(r => r.EvidenceCandidates.Count > 0);
            var supported = rows.Count(r => r.DisplayStatus == "SUPPORTED_REPORTED");
            var unsupported = rows.Count(r => r.DisplayStatus == "UNSUPPORTED");
            var failedIncomplete = rows.Count(r => r.DisplayStatus is "FAILED" or "INCOMPLETE/CONFLICT" or "INCOMPLETE");
            var notTested = rows.Count(r => r.DisplayStatus == "NOT_TESTED");
            var initialize = RealInitializationResult?.Candidates.FirstOrDefault()?.InitializeStatus ?? (_realInitializationError is not null ? "UNKNOWN" : "NOT_ATTEMPTED");
            return $"Форматов проверено: {tested} · Поддерживается: {supported} · Не поддерживается: {unsupported} · Ошибки/неполные: {failedIncomplete} · Не проверено: {notTested} · Initialize: {initialize}";
        }
    }
    public string RealInitializeDisabledReason
    {
        get
        {
            if (WorkerState.State != WorkerConnectionState.ConnectedFake) return Localization.Get("VmInitializeNeedsWorker");
            if (RealFormatMatrix is not null && RealFormatMatrixIsStale || SelectedWindowsEndpoint is not null && !CanUseSelectedWindowsEndpoint) return Localization.Get("VmInitializeEndpointStale");
            if (_realQueryRunning || _realInitializeRunning || WorkerOperationActive) return Localization.Get("VmInitializeOperationRunning");
            if (!HasCurrentOperatorQueryResult) return Localization.Get("VmInitializeNeedsCurrentQuery");
            if (SelectedRealFormatRow is null) return Localization.Get("VmInitializeNeedsSelectedFormat");
            if (SelectedRealFormatRow.DisplayStatus is "INCOMPLETE/CONFLICT" or "INCOMPLETE" or "NOT_TESTED") return Localization.Get("VmInitializeFormatIncomplete");
            if (SelectedRealFormatRow.DisplayStatus is "UNSUPPORTED" or "FAILED" || SelectedRealFormatCandidate is { SupportStatus: not "SUPPORTED_REPORTED" }) return Localization.Get("VmInitializeFormatUnsupported");
            if (SelectedRealFormatCandidate is null) return Localization.Get("VmInitializePeriodUnavailable");
            if (RealInitializeConsentPending) return Localization.Get("VmInitializeAwaitingConsent");
            if (_worker is null || !_worker.Capabilities.Contains("RealExclusiveFormatInitialize", StringComparer.Ordinal)) return Localization.Get("VmInitializeWorkerCapabilityMissing");
            return "";
        }
    }
    public string OperatorResultOutcome { get => _operatorResultOutcome; private set => Set(ref _operatorResultOutcome, value); }
    public string OperatorResultStreamEvidenceNote => _streamValidations.Count == 0
        ? Localization.Get("VmOperatorStreamEvidenceNone")
        : string.Format(Localization.CurrentCulture, Localization.Get("VmOperatorStreamEvidenceCount"), _streamValidations.Count);
    public string OperatorResultProfileEvidenceNote => _streamValidations.Count == 0
        ? Localization.Get("VmOperatorProfileEvidenceNoStreams")
        : Localization.Get("VmOperatorProfileEvidenceWithStreams");
    public string OperatorResultDetails => _realInitializationError ?? _realFormatError ?? (RealInitializationResult is not null ? RealInitializationSummary : RealFormatMatrix is not null ? $"{RealFormatMatrixStatus}\n{_operatorResultEndpoint}\nКонфигурация: {_operatorResultConfiguration}\nУникальных форматов: {RealFormatRows.Count}. Поддержка драйвера не является Initialize или запуском аудио." : "Реальный запрос ещё не выполнялся.");
    public string OperatorResultProvenance => _operatorResultProvenance;
    public bool DeviceScanConsentPending { get => _deviceScanConsentPending; private set { if (Set(ref _deviceScanConsentPending, value)) { Changed(nameof(DeviceScanConsentText)); ConfirmDeviceScanCommand.Refresh(); RequestDeviceScanCommand.Refresh(); } } }
    public bool DeviceScanConsentChecked { get => _deviceScanConsentChecked; set { if (Set(ref _deviceScanConsentChecked, value)) ConfirmDeviceScanCommand.Refresh(); } }
    public IReadOnlyList<RealStreamValidationDto> StreamValidations => _streamValidations.ToArray();
    public RealFormatCandidateDto? SelectedStreamCandidate { get => _selectedStreamCandidate; set { if (Set(ref _selectedStreamCandidate, value)) { Changed(nameof(StreamValidationConsentText)); Changed(nameof(StreamValidationAvailability)); RequestStreamValidationCommand.Refresh(); } } }
    public bool StreamConsentPending => _streamConsentPending;
    public bool StreamConsentChecked { get => _streamConsentChecked; set { if (Set(ref _streamConsentChecked, value)) ConfirmStreamValidationCommand.Refresh(); } }
    public bool StreamValidationRunning => _streamValidationRunning;
    public string StreamValidationStatus => _streamValidationStatus;
    public string StreamValidationProgress => _streamValidationProgress;
    public IReadOnlyList<StreamValidationResultRow> StreamValidationRows => [.. new[] { "MINIMUM", "DEFAULT" }.Select(period => _streamValidationRows[period])];
    public string StreamEvidencePersistenceStatus
    {
        get
        {
            if (_streamValidationRows.Values.Any(r => r.FinalStatus == "RUNNING")) return $"Stream: RUNNING; принятых наблюдений {_streamValidations.Count}; текущая попытка ещё не сохранена.";
            if (_streamValidations.Count == 0)
            {
                var attempt = _lastStreamAttemptPeriod is { } lastPeriod ? _streamValidationRows[lastPeriod] : null;
                if (attempt is null) return Localization.Get("VmStreamEvidenceNotRun");
                return _savedStreamProfilePath is null
                    ? string.Format(Localization.CurrentCulture, Localization.Get("VmStreamEvidenceLastAttemptNotSaved"), attempt.Period, attempt.FinalStatus)
                    : string.Format(Localization.CurrentCulture, Localization.Get("VmStreamEvidenceLastAttemptNotInProfile"), attempt.Period, attempt.FinalStatus, _savedStreamProfileVersion, _savedStreamProfilePath);
            }
            var currentIds = _streamValidations.Select(s => Guid.Parse(s.OperationId)).ToHashSet();
            if (_streamAttemptedSinceProfileSave && _savedStreamProfileVersion == 2 && _savedStreamProfilePath is not null && currentIds.SetEquals(_savedStreamOperationIds))
            {
                var latest = _streamValidationRows[_lastStreamAttemptPeriod!];
                return string.Format(Localization.CurrentCulture, Localization.Get("VmStreamEvidencePreviousSavedLatestNotIncluded"), latest.Period, latest.FinalStatus, _savedStreamProfilePath);
            }
            if (!_streamAttemptedSinceProfileSave && _savedStreamProfileVersion == 2 && _savedStreamProfilePath is not null && currentIds.SetEquals(_savedStreamOperationIds))
                return string.Format(Localization.CurrentCulture, Localization.Get("VmStreamEvidenceSessionSaved"), _savedStreamProfilePath);
            if (_streamAttemptedSinceProfileSave)
            {
                var latest = _streamValidationRows[_lastStreamAttemptPeriod!];
                return string.Format(Localization.CurrentCulture, Localization.Get("VmStreamEvidenceSessionAttemptNotSaved"), currentIds.Count, latest.Period, latest.FinalStatus);
            }
            return string.Format(Localization.CurrentCulture, Localization.Get("VmStreamEvidenceSessionNotSaved"), currentIds.Count);
        }
    }
    public string StreamValidationResultsSummary => string.Join(" · ", StreamValidationRows.Select(r => $"{r.Period}: {r.FinalStatus}"));
    public string StreamValidationConsentText => SelectedWindowsEndpoint is { } e && SelectedStreamCandidate is { } c ? string.Format(Localization.CurrentCulture, Localization.Get("VmStreamValidationConsent"), e.FriendlyNameDisplay, e.Direction, e.EndpointId, c.Channels, c.Period.ToUpperInvariant()) : Localization.Get("VmStreamValidationSelectInitializedCandidate");
    public string StreamValidationAvailability => Localization.Get(CanRequestStreamValidation() ? "VmStreamValidationAuthorizationRequired" : "VmStreamValidationNeedsCurrentInitialize");
    public bool DeviceScanRunning { get => _deviceScanRunning; private set { if (Set(ref _deviceScanRunning, value)) { CancelDeviceScanCommand.Refresh(); RequestDeviceScanCommand.Refresh(); RequestStreamValidationCommand.Refresh(); QueryRealFormatsCommand.Refresh(); RequestRealInitializeCommand.Refresh(); Changed(nameof(CanSaveDeviceProfile)); } } }
    public bool AutomaticScanPairEnabled
    {
        get => _automaticScanPairEnabled;
        set { if (AutomaticScanBusy || _automaticScanRunning) return; if (Set(ref _automaticScanPairEnabled, value)) { CancelAutomaticScanConsent(); Changed(nameof(AutomaticScanConsentText)); Changed(nameof(AutomaticScanAvailabilityReason)); PrepareAutomaticScanCommand.Refresh(); RefreshContext(); } }
    }
    public bool AutomaticScanPreparing { get => _automaticScanPreparing; private set { if (Set(ref _automaticScanPreparing, value)) { Changed(nameof(AutomaticScanBusy)); Changed(nameof(CanNavigateAwayFromAutomaticScan)); Changed(nameof(AutomaticScanAvailabilityReason)); CancelAutomaticScanCommand.Refresh(); PrepareAutomaticScanCommand.Refresh(); } } }
    public bool AutomaticScanConsentPending { get => _automaticScanConsentPending; private set { if (Set(ref _automaticScanConsentPending, value)) { Changed(nameof(AutomaticScanConsentText)); ConfirmAutomaticScanCommand.Refresh(); CancelAutomaticScanConsentCommand.Refresh(); PrepareAutomaticScanCommand.Refresh(); } } }
    public bool AutomaticScanConsentChecked { get => _automaticScanConsentChecked; set { if (Set(ref _automaticScanConsentChecked, value)) ConfirmAutomaticScanCommand.Refresh(); } }
    public bool AutomaticScanRunning { get => _automaticScanRunning; private set { if (Set(ref _automaticScanRunning, value)) { Changed(nameof(AutomaticScanBusy)); Changed(nameof(CanNavigateAwayFromAutomaticScan)); Changed(nameof(AutomaticScanAvailabilityReason)); CancelAutomaticScanCommand.Refresh(); ConfirmAutomaticScanCommand.Refresh(); PrepareAutomaticScanCommand.Refresh(); } } }
    public bool AutomaticScanBusy => AutomaticScanPreparing || AutomaticScanRunning;
    public IReadOnlyList<string> AutomaticSustainedModes { get; } = ["Standard (300 s)", "Extended (600 s)"];
    public string AutomaticSustainedMode { get => _automaticSustainedMode; set { if (AutomaticScanBusy || !AutomaticSustainedModes.Contains(value, StringComparer.Ordinal)) return; if (Set(ref _automaticSustainedMode, value)) { CancelAutomaticScanConsent(); Changed(nameof(AutomaticScanConsentText)); } } }
    public bool CanNavigateAwayFromAutomaticScan => !AutomaticScanBusy;
    public bool HasAutomaticScanResult => _automaticScanResult is not null || _automaticScanOutcomeUnknown && _automaticScanAttempted && _automaticScanTerminal;
    public bool AutomaticScanAttempted => _automaticScanAttempted;
    public ScanRunResult? AutomaticScanResult => _automaticScanResult;
    public IReadOnlyList<AutomaticScanProgressRow> AutomaticScanProgressRows => _automaticScanProgressRows;
    public string AutomaticScanStagePlanText => (_automaticScanResult?.StageResults ?? _automaticScanPreparation?.StagedPlan?.Stages ?? []).Select(s => $"{s.Stage}: {s.State} · {s.Message}").ToArray() is { Length: > 0 } rows ? string.Join("\n", rows) : Localization.Get("VmAutomaticScanPlanNotPrepared");
    public string AutomaticScanCandidateSummary => _automaticScanResult is { } result
        ? $"Кандидаты: {result.CandidateInventory.Count}; для теста {result.CandidateInventory.Count(c => c.State == AutomaticCandidateState.SUPPORTED_FOR_TEST)}; Initialize accepted {result.CandidateInventory.Count(c => c.State == AutomaticCandidateState.INITIALIZE_ACCEPTED)}; not tested {result.CandidateInventory.Count(c => c.State == AutomaticCandidateState.NOT_TESTED)}; UNKNOWN {result.CandidateInventory.Count(c => c.State == AutomaticCandidateState.UNKNOWN)}."
        : Localization.Get("VmAutomaticScanCandidatesAwaitQuery");
    public string AutomaticScanStatus => _automaticScanStatus;
    public string AutomaticScanAvailabilityReason
    {
        get
        {
            if (_automaticScanWorkflow is null) return Localization.Get("VmAutomaticScanUnavailable");
            if (_automaticScanOutcomeUnknown) return Localization.Get("VmAutomaticScanPreviousOutcomeUnknown");
            if (AutomaticScanBusy) return Localization.Get("VmAutomaticScanInProgress");
            if (DeviceScanConsentPending || RealInitializeConsentPending || DuplexConsentPending) return Localization.Get("VmAutomaticScanCancelOtherConsent");
            if (AutomaticScanPairEnabled)
                return SelectedDuplexCaptureEndpoint is null || SelectedDuplexRenderEndpoint is null ? AllowUnconfirmedPhysicalPair ? Localization.Get("VmAutomaticScanChooseUnconfirmedPair") : Localization.Get("VmAutomaticScanChoosePairEndpoints") : AllowUnconfirmedPhysicalPair ? Localization.Get("VmAutomaticScanCheckUnconfirmedPair") : Localization.Get("VmAutomaticScanCheckExactPair");
            if (SelectedWindowsEndpoint is not { EndpointId.Length: > 0, DeviceState: "ACTIVE" })
                return SelectedPhysicalDeviceGroup is { RequiresExplicitCapture: true } ? Localization.Get("VmAutomaticScanChooseCaptureOrPair") : SelectedPhysicalDeviceGroup is { RequiresExplicitRender: true } ? Localization.Get("VmAutomaticScanChooseRenderOrPair") : Localization.Get("VmAutomaticScanChooseActiveEndpoint");
            if (WorkerState.State != WorkerConnectionState.ConnectedFake && _workerAttempted) return WorkerRestartExplanation;
            return Localization.Get("VmAutomaticScanPlanBoundToEndpoint");
        }
    }
    public string AutomaticScanConsentText
    {
        get
        {
            if (_automaticScanPreparation is not { } prepared) return "План ещё не подготовлен.";
            var target = prepared.Plan.Target;
            var endpoints = new List<string>();
            if (target.Capture is { } capture) endpoints.Add($"Capture: {capture.FriendlyNameDisplay} · ID {capture.EndpointId}");
            if (target.Render is { } render) endpoints.Add($"Render: {render.FriendlyNameDisplay} · ID {render.EndpointId}");
            var operations = string.Join(", ", prepared.WorkerPlan.AllowedOperations.Select(ScanCommandLabel));
            var active = prepared.MaximumActiveSeconds == 0 ? "активное время потоков: 0 с (недоступно)" : $"суммарное активное время: не более {prepared.MaximumActiveSeconds} с";
            var identity = prepared.PairPhysicalIdentityUnconfirmed ? "Общая физическая принадлежность Capture/Render не подтверждена; пара выбрана оператором явно." : "Физическая принадлежность пары ограничена текущей проверкой Windows identity.";
            var stages = prepared.StagedPlan is { } staged ? string.Join(", ", staged.Stages.Select(s => $"{s.Stage}={s.State}")) : "стадийный план будет сохранён в результате";
            return $"{string.Join("\n", endpoints)}\n{identity}\nОперации: {operations}. Формат-кандидат: 48 кГц PCM16, периоды MINIMUM, DEFAULT и подтверждённые CUSTOM; MMCSS OFF и PRO_AUDIO проверяются раздельно. Sustained режим: {AutomaticSustainedMode}. {active}; общий лимит плана: не более {prepared.MaximumTotalOperations} операций, wall-clock bound не более {prepared.MaximumWallClockSeconds} с. Worker session: до {prepared.WorkerPlan.LifetimeSeconds} с, не более {prepared.WorkerPlan.MaximumOperationCount} операций за batch. Стадии: {stages}. Exclusive доступ может временно прервать другие приложения. Capture пакеты только считываются и отбрасываются; записи PCM нет. Render передаёт только цифровую тишину. Нет физической маршрутизации, тестового тона или RTL. Неотвечающий драйвер может оставить исход операции UNKNOWN.";
        }
    }
    public string AutomaticScanResultSummary => AutomaticScanBusy && _automaticScanAttempted ? $"СКАНИРОВАНИЕ · {_automaticScanStatus}" :
        _automaticScanOutcomeUnknown && _automaticScanAttempted ? $"НЕИЗВЕСТНО · {_automaticScanStatus}" :
        _automaticScanResult is { } result ? $"{ScanLifecycleLabel(result.Lifecycle)} · {result.Summary}" :
        _automaticScanAttempted ? $"НЕИЗВЕСТНО · {_automaticScanStatus}" : Localization.Get("VmAutomaticScanResultMissing");
    public string AutomaticScanResultHeadline => _automaticScanResult?.Lifecycle switch
    {
        ScanRunLifecycle.Completed => Localization.Get("VmAutomaticScanCompleted"),
        ScanRunLifecycle.Incomplete => Localization.Get("VmAutomaticScanPartiallyCompleted"),
        ScanRunLifecycle.Cancelled => Localization.Get("VmAutomaticScanCancelled"),
        ScanRunLifecycle.Failed => Localization.Get("VmAutomaticScanFailed"),
        ScanRunLifecycle.Unknown => Localization.Get("VmAutomaticScanOutcomeUnknown"),
        _ => Localization.Get("VmAutomaticScanResultMissing")
    } ?? (_automaticScanOutcomeUnknown && _automaticScanAttempted ? Localization.Get("VmAutomaticScanOutcomeUnknown") : Localization.Get("VmAutomaticScanResultMissing"));
    public string AutomaticScanResultDevice => SelectedPhysicalDeviceGroup?.DisplayName ?? (_automaticScanResult is not { } result ? OperatorSelectedDevice :
        string.Join(" + ", new[] { result.Target.Capture?.FriendlyNameDisplay, result.Target.Render?.FriendlyNameDisplay }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal)));
    public string AutomaticScanRecommendationText => _automaticScanResult is not { } result ? "Рекомендация отсутствует." : result.Recommendation.Preferred is { } preferred
        ? $"Выбор {result.Recommendation.Scope}: {preferred.Period} · {preferred.SampleRate / 1000} кГц {preferred.Encoding} · {preferred.CaptureBufferFrames}/{preferred.RenderBufferFrames} frames · MMCSS {preferred.Mmcss} · {preferred.DurationSeconds} с · {preferred.Confidence}. Запасной: {(result.Recommendation.Fallback is { } fallback ? $"{fallback.Period} · MMCSS {fallback.Mmcss}" : "NONE")}. AudioHost: NOT_CERTIFIED. {string.Join(" ", result.Recommendation.Reasons)}"
        : $"Финальная конфигурация не выбрана. {string.Join(" ", result.Recommendation.Reasons)} AudioHost: NOT_CERTIFIED.";
    public string AutomaticScanPhysicalLatencyText => "Не измерена";
    public string AutomaticScanResultProvenance
    {
        get
        {
            if (_automaticScanResult is not { } result) return _automaticScanAttempted ? "Исход Worker не подтверждён." : "Данные отсутствуют.";
            var provenances = result.Evidence.SelectMany(e => new[]
            {
                e.FormatMatrix?.Provenance,
                JsonProvenance(e.Stream?.Report), JsonProvenance(e.Duplex?.Report)
            }).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToArray();
            return provenances.Length == 0 ? Localization.Get("VmAutomaticScanProvenanceUnknown") :
                provenances.All(p => p!.StartsWith("TEST_FAKE_", StringComparison.Ordinal)) ? Localization.Get("VmAutomaticScanProvenanceDemo") :
                provenances.All(p => p!.StartsWith("REAL_WASAPI_", StringComparison.Ordinal)) ? Localization.Get("VmAutomaticScanProvenanceReal") :
                Localization.Get("VmAutomaticScanProvenanceMixed");
        }
    }
    public string AutomaticScanPersistenceText => _automaticScanResult is null
        ? _automaticScanOutcomeUnknown ? Localization.Get("VmAutomaticScanSaveUnknown") : _automaticScanAttempted ? Localization.Get("VmAutomaticScanSaveUnconfirmed") : Localization.Get("VmAutomaticScanReportNotSaved")
        : _automaticScanResult.Persistence is not { } saved ? Localization.Get("VmAutomaticScanReportNotSaved") : saved.Status == "SAVE_FAILED"
            ? $"SAVE_FAILED · сводка не сохранена: {saved.Error ?? "причина недоступна"}"
            : saved.Status == "SUMMARY_SAVED_PROFILE_FAILED"
                ? $"Сводка сохранена, но профиль не сформирован: {saved.SummaryPath ?? "неизвестный путь"}\nПричина: {saved.Error ?? "причина недоступна"}"
            : saved.Status == "SAVED_AND_RELOADED"
                ? $"Сводка и профиль сохранены и перечитаны: {saved.SummaryPath ?? "неизвестный путь"}\nПрофили: {(saved.ProfilePaths.Count == 0 ? "профиль не сформирован" : string.Join("\n", saved.ProfilePaths))}"
            : $"Сохранение: {saved.Status} · {saved.Error ?? "состояние не подтверждено"}";
    public string AutomaticScanTechnicalDetails
    {
        get
        {
            if (_automaticScanResult is not { } result)
                return _automaticScanAttempted ? _automaticScanStatus : "Технические сведения отсутствуют.";
            var lines = result.Operations.Select(o => $"{o.Operation.Stage} · {o.Lifecycle} · {o.Operation.OperationId} · {o.Operation.EndpointId ?? "PAIR"} · cleanup={o.CleanupStatus} · {o.Message}").ToList();
            if (result.PeriodDiscoveryDiagnostics is { } diagnostics)
            {
                lines.Add("PERIOD_DISCOVERY · " + diagnostics.Summary);
                lines.AddRange(diagnostics.Operations.Select(o => $"PERIOD_DISCOVERY · #{o.OperationIndex} · {o.Direction} · {o.Outcome} · {o.DurationMilliseconds} ms · timeout={o.TimeoutSeconds} s" + (o.WorkerErrorCode is null ? string.Empty : $" · Worker={o.WorkerErrorCode}")));
            }
            return string.Join("\n", lines);
        }
    }
    public bool AutomaticScanExportRunning => _automaticScanExportRunning;
    public bool CanExportAutomaticScan => !_automaticScanExportRunning && _automaticScanResult is { Lifecycle: ScanRunLifecycle.Completed, Persistence: { Status: "SAVED_AND_RELOADED", SummaryPath: not null } } && _bundleExportInteraction is not null;
    public string AutomaticScanExportStatus => _automaticScanExportStatus;
    public string AutomaticScanExportTechnicalDetails => _automaticScanExportTechnicalDetails;
    public DeviceScanResult? DeviceScanResult => _deviceScanResult;
    public bool HasDeviceScanResult => _deviceScanResult is not null;
    public IReadOnlyList<RealFormatCandidateDto> DeviceScanInitializeCandidates => _deviceScanResult?.Initializations.SelectMany(r => r.Candidates).ToArray() ?? [];
    public string DeviceScanProgress => _deviceScanProgress;
    public string DeviceScanConsentText => SelectedWindowsEndpoint is { } e ? $"Устройство: {e.FriendlyNameDisplay} · {e.Direction}. Пассивный запрос существующей матрицы (44,1/48 кГц, PCM16/PCM24-in-32/Float32), затем до {DeviceConfigurationScan.MaximumInitializeOperations} Initialize-only проверок только для поддержанных 48 кГц PCM16 кандидатов (Capture: каналы 1/2; Render: канал 2). Потоки не запускаются, PCM не передаётся, звук не выводится. Initialize может временно занять Exclusive endpoint. Общий предел: {DeviceConfigurationScan.MaximumDurationSeconds} секунд. Stream, стабильность и Duplex не подтверждаются." : Localization.Get("VmChooseActiveWindowsDevice");
    public bool CanStartDeviceScan => CanUseSelectedWindowsEndpoint && WorkerState.State == WorkerConnectionState.ConnectedFake && _worker is not null && _worker.Capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal) && _worker.Capabilities.Contains("RealExclusiveFormatInitialize", StringComparer.Ordinal) && !_realQueryRunning && !_realInitializeRunning && !_deviceScanRunning && !WorkerOperationActive && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending && !RealInitializeConsentPending && !DeviceScanConsentPending;
    public string DeviceScanAvailabilityReason => CanStartDeviceScan ? Localization.Get("VmDeviceScanPlanBound") : WorkerState.State != WorkerConnectionState.ConnectedFake ? Localization.Get("VmConnectWorker") : !_windowsDiscoveryCurrent ? Localization.Get("VmRefreshWindowsDevices") : SelectedWindowsEndpoint is null ? Localization.Get("VmChooseCaptureOrRenderEndpoint") : SelectedWindowsEndpoint.DeviceState != "ACTIVE" ? Localization.Get("VmSelectedEndpointInactive") : !_worker!.Capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal) || !_worker.Capabilities.Contains("RealExclusiveFormatInitialize", StringComparer.Ordinal) ? Localization.Get("VmWorkerOperationsUnsupported") : Localization.Get("VmWaitOrCancelPendingOperation");
    public string DeviceScanResultSummary
    {
        get
        {
            if (_deviceScanResult is not { } scan) return Localization.Get("VmDeviceScanNotStarted");
            var rows = scan.Query is null ? [] : RealFormatMatrixValidation.BuildUniqueRows(scan.Query, scan.Direction == "CAPTURE" ? [1, 2] : [2]);
            var supported = rows.Count(r => r.SampleRate == 48_000 && r.Format == "PCM16" && r.DisplayStatus == "SUPPORTED_REPORTED");
            var initialized = scan.Initializations.SelectMany(r => r.Candidates).Count(c => c.InitializeStatus is "INITIALIZED" or "READY");
            var failed = scan.Initializations.SelectMany(r => r.Candidates).Count(c => c.InitializeStatus is "FAILED" or "UNSUPPORTED" or "INCOMPLETE");
            var streamRuns = _streamValidations.Count(s => s.EndpointId == scan.EndpointId && s.Direction == scan.Direction);
            return $"{scan.State} · 48 кГц PCM16: сообщено поддерживаемых {supported}; Initialize успешно {initialized}, ошибки/неполные {failed}; Stream: {(streamRuns == 0 ? "NOT_RUN" : $"принятых наблюдений {streamRuns}")}; длительная Stability и Duplex: NOT_RUN.";
        }
    }
    public string DeviceScanTechnicalDetails => _deviceScanResult is not { } scan ? Localization.Get("VmDeviceScanDetailsMissing") : string.Join("\n", scan.Initializations.SelectMany(r => r.Candidates).Select(c => $"{c.Encoding} {c.SampleRate} Hz ch={c.Channels} {c.Period}: status={c.Status}; support={c.SupportStatus} IsFormatSupported={c.SupportHr ?? "unknown"}; Initialize={c.InitializeStatus} first={c.FirstInitializeHr ?? "unknown"}; alignment GetBufferSize={c.AlignmentGetBufferSizeHr ?? "not observed"}/{c.AlignmentFrames?.ToString() ?? "unknown"} frames, Activate={c.AlignmentActivateHr ?? "not observed"}, period100ns={c.AlignmentPeriod100ns?.ToString() ?? "unknown"}, Initialize={c.AlignmentInitializeHr ?? "not observed"}; final={c.FinalInitializeHr ?? "unknown"}; requestedPeriod100ns={c.RequestedPeriod100ns?.ToString() ?? "unknown"}; actualPeriod100ns={c.ActualPeriod100ns?.ToString() ?? "unknown"}; bufferFrames={c.ActualBufferFrames?.ToString() ?? "unknown"}; SetEventHandle={c.SetEventHandleHr ?? "not observed"}; eventReady={c.EventHandleReady?.ToString() ?? "unknown"}; cleanup={c.CleanupStatus}; releases={c.ClientReleaseAttempts}"));
    public bool CanSaveDeviceProfile => !_duplexValidationRunning && (HasDuplexPairProfileEvidence || HasCurrentOperatorQueryResult && CanUseSelectedWindowsEndpoint && !_realQueryRunning && !_realInitializeRunning && !_deviceScanRunning && !WorkerOperationActive && SelectedWindowsEndpoint is { } endpoint && RealFormatMatrix is { } query && query.EndpointId == endpoint.EndpointId && query.Direction == endpoint.Direction && (RealInitializationResult is null || RealInitializationResult.EndpointId == endpoint.EndpointId && RealInitializationResult.Direction == endpoint.Direction && _realInitializationError is null));
    public string DeviceProfileSaveMessage => _deviceProfileSaveMessage;
    public DeviceConfigurationProfile CreateCurrentDeviceProfile()
    {
        if (HasDuplexPairProfileEvidence) return DeviceConfigurationProfileFactory.CreateDuplex(SelectedDuplexCaptureEndpoint!, SelectedDuplexRenderEndpoint!, _duplexValidations.Values.OrderBy(v => v.Report.GetProperty("requested").GetProperty("period").GetString() == "MINIMUM" ? 0 : 1).ToArray());
        if (!CanSaveDeviceProfile || SelectedWindowsEndpoint is not { } endpoint || RealFormatMatrix is not { } query)
            throw new InvalidOperationException("A current, accepted real WASAPI result for the selected active endpoint is required.");
        var initializations = RealInitializationResult is null ? _deviceScanInitializations : _deviceScanInitializations.Append(RealInitializationResult).ToArray();
        return DeviceConfigurationProfileJson.Validate(DeviceConfigurationProfileFactory.CreateAggregated(endpoint, query, initializations, streamValidations: _streamValidations.Where(s => s.EndpointId == endpoint.EndpointId && s.Direction == endpoint.Direction).ToArray()));
    }
    public void SetDeviceProfileSaveMessage(string message) { _deviceProfileSaveMessage = message; Changed(nameof(DeviceProfileSaveMessage)); }
    public void RecordDeviceProfileSaved(string path, DeviceConfigurationProfile profile)
    {
        _savedStreamProfilePath = path;
        _savedStreamProfileVersion = profile.SchemaVersion;
        _savedStreamOperationIds = profile.StreamObservations.Select(s => s.OperationId).ToHashSet();
        _streamAttemptedSinceProfileSave = false;
        _deviceProfileSaveMessage = $"Профиль schemaVersion {profile.SchemaVersion} сохранён: {path}";
        Changed(nameof(DeviceProfileSaveMessage)); Changed(nameof(StreamEvidencePersistenceStatus));
    }
    public bool HasOperatorQueryResult => RealFormatMatrix is not null && RealInitializationResult is null && _realFormatError is null;
    public bool HasCurrentOperatorQueryResult => RealFormatMatrix is { Provenance: "REAL_WASAPI_QUERY", ReportLevel: "PASSIVE_QUERY" } matrix && _realFormatError is null && !RealFormatMatrixIsStale && SelectedWindowsEndpoint is { } selected && selected.EndpointId == matrix.EndpointId && selected.Direction == matrix.Direction;
    public bool HasCurrentDuplexPair => _windowsDiscoveryCurrent && WorkerState.State == WorkerConnectionState.ConnectedFake && SelectedDuplexCaptureEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } && SelectedDuplexRenderEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } && SelectedDuplexCaptureEndpoint.EndpointId != SelectedDuplexRenderEndpoint.EndpointId && DuplexCaptureEndpoints.Contains(SelectedDuplexCaptureEndpoint) && DuplexRenderEndpoints.Contains(SelectedDuplexRenderEndpoint);
    public bool HasDuplexPairProfileEvidence => !_duplexValidationRunning && _duplexValidations.Count > 0 && SelectedDuplexCaptureEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } capture && SelectedDuplexRenderEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } render && _duplexValidations.Values.All(v => v.CaptureEndpointId == capture.EndpointId && v.RenderEndpointId == render.EndpointId && v.Report.GetProperty("provenance").GetString() == "REAL_WASAPI_DUPLEX");
    public string DuplexWorkerAvailability => WorkerState.State == WorkerConnectionState.ConnectedFake ? Localization.Get("VmWorkerConnected") : $"{Localization.Get("VmWorkerLabel")}: {WorkerState.State switch { WorkerConnectionState.Disconnected => Localization.Get("VmWorkerDisconnected"), WorkerConnectionState.Starting => Localization.Get("VmWorkerStarting"), WorkerConnectionState.Connecting => Localization.Get("VmWorkerConnecting"), WorkerConnectionState.Handshaking => Localization.Get("VmWorkerHandshaking"), WorkerConnectionState.Stopping => Localization.Get("VmWorkerStopping"), WorkerConnectionState.Incompatible => Localization.Get("VmWorkerIncompatible"), _ => Localization.Get("VmWorkerError") }} · {WorkerState.Message}";
    public string DuplexDiscoveryFreshness => _windowsDiscoveryCurrent ? Localization.Get("VmWindowsDiscoveryCurrent") : _windowsDiscoveryLoading ? Localization.Get("VmWindowsDiscoveryRefreshing") : _windowsDiscoveryError is { Length: > 0 } error ? string.Format(Localization.CurrentCulture, Localization.Get("VmWindowsDiscoveryError"), error) : Localization.Get("VmWindowsDiscoveryStale");
    public string DuplexEvidenceFreshness => $"{DuplexWorkerAvailability} · {DuplexDiscoveryFreshness}";
    public bool NeedsInitializeFormatQuery => OperatorTestMode == "INITIALIZE_ONLY" && !HasCurrentOperatorQueryResult;
    public bool HasOperatorInitializeResult => RealInitializationResult is not null && _realInitializationError is null;
    public string OperatorResultMode => _operatorResultMode;
    public string OperatorResultConfiguration => _operatorResultConfiguration;
    public string OperatorSelectedDevice => WorkflowStep == OperatorStep.Result && _operatorResultEndpoint.Length > 0 ? _operatorResultEndpoint : SelectedWindowsEndpoint is { } e ? $"{e.FriendlyNameDisplay} · {e.Direction} · {e.DeviceState}" : SelectedPhysicalDeviceGroup?.DisplayName ?? RealFormatEndpointLabel;
    public SessionMode Direction
    {
        get => _direction;
        set { if (Set(ref _direction, value)) { Changed(nameof(EndpointRows)); Changed(nameof(SelectedEndpoint)); Changed(nameof(HasEndpoints)); Changed(nameof(IsEndpointListEmpty)); Changed(nameof(EmptyEndpointMessage)); RefreshContext(); RefreshCanExecute(); } }
    }
    public ObservableCollection<EndpointDto> CaptureEndpoints { get; }
    public ObservableCollection<EndpointDto> RenderEndpoints { get; }
    public IEnumerable<EndpointDto> EndpointRows => Direction == SessionMode.Capture ? CaptureEndpoints : RenderEndpoints;
    public bool HasEndpoints => EndpointRows.Any();
    public bool IsEndpointListEmpty => !HasEndpoints;
    public string EmptyEndpointMessage => !HasEndpoints ? string.Format(Localization.CurrentCulture, Localization.Get("VmEndpointListEmpty"), Localization.Get(Direction == SessionMode.Capture ? "VmCaptureInputDirection" : "VmRenderOutputDirection")) : SelectedEndpoint is null ? Localization.Get("VmChooseDemoEndpoint") : string.Empty;
    public EndpointDto? SelectedEndpoint
    {
        get => Direction == SessionMode.Capture ? SelectedCaptureEndpoint : SelectedRenderEndpoint;
        set { if (Direction == SessionMode.Capture) SelectedCaptureEndpoint = value; else SelectedRenderEndpoint = value; }
    }
    public EndpointDto? SelectedCaptureEndpoint { get => _selectedCaptureEndpoint; set { if (Set(ref _selectedCaptureEndpoint, value)) SelectionChanged(); } }
    public EndpointDto? SelectedRenderEndpoint { get => _selectedRenderEndpoint; set { if (Set(ref _selectedRenderEndpoint, value)) SelectionChanged(); } }
    public ObservableCollection<FormatDto> Formats { get; }
    public FormatDto? SelectedFormat { get => _selectedFormat; set { if (Set(ref _selectedFormat, value)) RefreshContext(); } }
    public ObservableCollection<ReportDto> Reports { get; }
    public ObservableCollection<EvidenceLibraryEntry> EvidenceLibraryEntries { get; } = [];
    public ObservableCollection<NormalizedDuplexComparison> EvidenceLibraryComparisons { get; } = [];
    public IReadOnlyList<string> EvidenceLibraryComparisonModes { get; } = Enum.GetNames<DuplexComparisonMode>();
    public EvidenceLibraryEntry? SelectedEvidenceLibraryEntry { get => _selectedEvidenceLibraryEntry; set { if (Set(ref _selectedEvidenceLibraryEntry, value)) { Changed(nameof(EvidenceLibraryDetails)); Changed(nameof(EvidenceLibraryComparisonText)); } } }
    public string EvidenceLibraryDeviceFilter { get => _evidenceLibraryDeviceFilter; set { if (Set(ref _evidenceLibraryDeviceFilter, value)) ApplyEvidenceLibraryFilter(); } }
    public string EvidenceLibraryTypeFilter { get => _evidenceLibraryTypeFilter; set { if (Set(ref _evidenceLibraryTypeFilter, value)) ApplyEvidenceLibraryFilter(); } }
    public string EvidenceLibraryProvenanceFilter { get => _evidenceLibraryProvenanceFilter; set { if (Set(ref _evidenceLibraryProvenanceFilter, value)) ApplyEvidenceLibraryFilter(); } }
    public string EvidenceLibraryComparisonMode { get => _evidenceLibraryComparisonMode; set { if (Set(ref _evidenceLibraryComparisonMode, value)) ApplyEvidenceLibraryFilter(); } }
    public string EvidenceLibraryStatus => string.Format(Localization.CurrentCulture, Localization.Get("VmEvidenceLibraryStatus"), EvidenceLibraryEntries.Count, _evidenceLibrary.ValidEntries.Count, _evidenceLibrary.InvalidEntries.Count);
    public string EvidenceLibraryDetails => _selectedEvidenceLibraryEntry is not { } entry ? Localization.Get("VmSelectEvidenceRecord") : $"{entry.Details}\n{Localization.Get("VmUtcLabel")}: {entry.TimestampUtcText}\n{Localization.Get("VmLocalLabel")}: {entry.TimestampLocalText}\n{Localization.Get("VmIdentityLabel")}: {entry.IdentityText}\n{Localization.Get("VmOriginalLabel")}: {entry.Path}";
    public string EvidenceLibraryComparisonText => _selectedEvidenceLibraryEntry is not { } ? Localization.Get("VmSelectDuplexRecord") : string.Join("\n", EvidenceLibraryComparisons.Select(c => c.Summary));
    public ReportDto? SelectedReport { get => _selectedReport; set { if (Set(ref _selectedReport, value)) RefreshContext(); } }
    public SessionDto? Session { get => _session; private set { if (value is not null && _session?.Id != value.Id) { _captureBank = 0; Changed(nameof(CaptureChannelBank)); } if (Set(ref _session, value)) { Changed(nameof(IsRunning)); Changed(nameof(CaptureChannelBanks)); Changed(nameof(VisibleMeterChannels)); Changed(nameof(MeterStatusText)); RefreshContext(); RefreshCanExecute(); } } }
    public MeterSnapshotDto? Meter { get => _meter; private set { if (Set(ref _meter, value)) { Changed(nameof(MeterStatusText)); Changed(nameof(VisibleMeterChannels)); RefreshContext(); } } }
    public string MeterStatusText => Meter is null || !Meter.IsCompleteWindow ? "Нет завершённого окна" : Session?.Lifecycle switch
    {
        SessionLifecycle.Unknown => $"Устарело · последнее наблюдение: окно {Meter.Sequence}; остановка и cleanup неизвестны.",
        SessionLifecycle.Stopped or SessionLifecycle.Completed or SessionLifecycle.Failed => $"Финальные значения · окно {Meter.Sequence}; качество и cleanup не подтверждались.",
        SessionLifecycle.Stopping => $"STOPPING · последние значения заморожены до терминального события; окно {Meter.Sequence}.",
        _ when Meter.Channels.All(c => c.Silent == true) => $"Окно {Meter.Sequence} · наблюдалась цифровая тишина",
        _ => $"Окно {Meter.Sequence} · LIVE · ДЕМО · Данные симуляции"
    };
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool ConfirmPending { get => _confirmPending; private set { if (Set(ref _confirmPending, value)) { ConfirmCommand.Refresh(); CancelConfirmationCommand.Refresh(); BeginCommand.Refresh(); } } }
    public bool ConsentChecked { get => _consentChecked; set { if (Set(ref _consentChecked, value)) ConfirmCommand.Refresh(); } }
    public bool IsRunning => Session?.Lifecycle is SessionLifecycle.Preparing or SessionLifecycle.Started or SessionLifecycle.Stopping;
    public int RenderChannel { get => _renderChannel; set { if (RenderChannelOptions.Any(o => o.Value == value) && Set(ref _renderChannel, value)) { Changed(nameof(RenderChannelOptions)); var bank = (value - 1) / 8; if (_renderBank != bank) { _renderBank = bank; Changed(nameof(RenderChannelBank)); } RefreshContext(); } } }
    public int DurationSeconds { get => _durationSeconds; set { if (DurationOptions.Any(o => o.Value == value) && Set(ref _durationSeconds, value)) RefreshContext(); } }
    public int RenderChannelBank { get => _renderBank; set { if (value >= 0 && value < RenderChannelBanks.Count && Set(ref _renderBank, value)) { Changed(nameof(RenderChannelOptions)); RefreshContext(); } } }
    public int CaptureChannelBank { get => _captureBank; set { if (value >= 0 && value < CaptureChannelBanks.Count && Set(ref _captureBank, value)) Changed(nameof(VisibleMeterChannels)); } }
    public IReadOnlyList<SelectionOption> CaptureChannelBanks => Enumerable.Range(0, Math.Max(1, ((Session?.ChannelCount ?? SelectedCaptureEndpoint?.Topology?.ChannelCount ?? SelectedCaptureEndpoint?.Channels ?? 2) + 7) / 8)).Select(bank => new SelectionOption(bank, $"Каналы {bank * 8 + 1}–{Math.Min(bank * 8 + 8, Session?.ChannelCount ?? SelectedCaptureEndpoint?.Topology?.ChannelCount ?? SelectedCaptureEndpoint?.Channels ?? 2)}")).ToArray();
    public IReadOnlyList<MeterChannelDto> VisibleMeterChannels => Meter?.Channels.Where(c => c.Channel > _captureBank * 8 && c.Channel <= _captureBank * 8 + 8).ToArray() ?? [];
    public IReadOnlyList<SelectionOption> RenderChannelBanks => Enumerable.Range(0, Math.Max(1, ((SelectedRenderEndpoint?.Topology?.ChannelCount ?? SelectedRenderEndpoint?.Channels ?? 2) + 7) / 8)).Select(bank => new SelectionOption(bank, $"Каналы {bank * 8 + 1}–{Math.Min(bank * 8 + 8, SelectedRenderEndpoint?.Topology?.ChannelCount ?? SelectedRenderEndpoint?.Channels ?? 2)}")).ToArray();
    public IReadOnlyList<SelectionOption> RenderChannelOptions => Enumerable.Range(_renderBank * 8 + 1, Math.Min(8, Math.Max(1, SelectedRenderEndpoint?.Topology?.ChannelCount ?? SelectedRenderEndpoint?.Channels ?? 2) - _renderBank * 8)).Select(i => new SelectionOption(i, $"Канал Windows {i}")).ToArray();
    public WorkerStateDto WorkerState { get; private set; } = new(WorkerConnectionState.Disconnected, "Worker не запускался.");
    public IReadOnlyList<WorkerEndpointDto> WorkerEndpoints { get => _workerEndpoints; private set => Set(ref _workerEndpoints, value); }
    public WorkerEndpointDto? SelectedWorkerEndpoint { get => _selectedWorkerEndpoint; set { if (value is not null && !WorkerEndpoints.Contains(value)) return; if (Set(ref _selectedWorkerEndpoint, value)) { _selectedWorkerFormat = value?.FormatTopologies?.FirstOrDefault(); _workerRenderChannel = 1; Changed(nameof(SelectedWorkerFormat)); Changed(nameof(WorkerFormatOptions)); Changed(nameof(WorkerChannelCount)); Changed(nameof(WorkerChannelSummary)); Changed(nameof(WorkerRenderChannelVisible)); Changed(nameof(WorkerRenderChannel)); Changed(nameof(WorkerRenderChannelOptions)); RefreshWorkerCommands(); } } }
    public FormatTopologyDto? SelectedWorkerFormat { get => _selectedWorkerFormat; set { if (value is not null && !WorkerFormatOptions.Contains(value)) return; if (Set(ref _selectedWorkerFormat, value)) { if (_workerRenderChannel < 1 || _workerRenderChannel > WorkerChannelCount) _workerRenderChannel = 1; Changed(nameof(WorkerChannelCount)); Changed(nameof(WorkerChannelSummary)); Changed(nameof(WorkerRenderChannel)); Changed(nameof(WorkerRenderChannelOptions)); RefreshWorkerCommands(); } } }
    public IReadOnlyList<FormatTopologyDto> WorkerFormatOptions => SelectedWorkerEndpoint?.FormatTopologies ?? [];
    public int WorkerChannelCount => SelectedWorkerFormat?.Topology.ChannelCount ?? 0;
    public string WorkerChannelSummary => SelectedWorkerEndpoint is { } endpoint && WorkerChannelCount > 0 ? $"{endpoint.Direction} · {SelectedWorkerFormat!.FormatId} · {WorkerChannelCount} каналов потока" : "Топология формата не выбрана.";
    public bool WorkerRenderChannelVisible => SelectedWorkerEndpoint?.Direction == "RENDER";
    public bool WorkerCurrentEndpointsVisible => WorkerState.State == WorkerConnectionState.ConnectedFake;
    public IReadOnlyList<WorkerEndpointDto> LastWorkerEndpointSnapshot { get => _lastWorkerEndpointSnapshot; private set { if (Set(ref _lastWorkerEndpointSnapshot, value)) { Changed(nameof(HasHistoricalWorkerEndpoints)); Changed(nameof(HistoricalWorkerEndpointNames)); } } }
    public bool HasHistoricalWorkerEndpoints => WorkerState.State == WorkerConnectionState.Disconnected && LastWorkerEndpointSnapshot.Count > 0;
    public IReadOnlyList<WindowsEndpointDto> WindowsEndpoints => _windowsDiscoveryCurrent ? _windowsEndpointSnapshot?.Endpoints ?? [] : [];
    public IReadOnlyList<PhysicalDeviceGroupDto> PhysicalDeviceGroups => _physicalDeviceGroups;
    public PhysicalDeviceGroupDto? SelectedPhysicalDeviceGroup
    {
        get => _selectedPhysicalDeviceGroup;
        set
        {
            if (AutomaticScanBusy) return;
            if (!Set(ref _selectedPhysicalDeviceGroup, value)) return;
            CancelAutomaticScanConsent();
            CancelDuplexRepeatabilityAuthorization();
            CancelPeriodDiscovery(Localization.Get("VmPeriodPlanDiscardedGroupChanged"));
            _allowUnconfirmedPhysicalPair = false;
            if (value is { CanAutoSelectPair: true })
            {
                Set(ref _selectedDuplexCapture, value.CaptureCandidates.Single(e => e.DeviceState == "ACTIVE"), nameof(SelectedDuplexCaptureEndpoint));
                Set(ref _selectedDuplexRender, value.RenderCandidates.Single(e => e.DeviceState == "ACTIVE"), nameof(SelectedDuplexRenderEndpoint));
                _automaticScanPairEnabled = true;
                Set(ref _selectedWindowsEndpoint, null, nameof(SelectedWindowsEndpoint));
            }
            else if (value is { RequiresExplicitPair: true })
            {
                _automaticScanPairEnabled = true;
                var captures = value.CaptureSelectableEndpoints.Where(PhysicalDeviceGrouping.IsActiveEndpoint).ToArray();
                Set(ref _selectedDuplexCapture, captures.Length == 1 ? captures[0] : null, nameof(SelectedDuplexCaptureEndpoint));
                Set(ref _selectedDuplexRender, null, nameof(SelectedDuplexRenderEndpoint));
                Set(ref _selectedWindowsEndpoint, null, nameof(SelectedWindowsEndpoint));
            }
            else if (value is { RequiresExplicitCapture: true } or { RequiresExplicitRender: true })
            {
                _automaticScanPairEnabled = false;
                Set(ref _selectedDuplexCapture, null, nameof(SelectedDuplexCaptureEndpoint));
                Set(ref _selectedDuplexRender, null, nameof(SelectedDuplexRenderEndpoint));
                Set(ref _selectedWindowsEndpoint, null, nameof(SelectedWindowsEndpoint));
            }
            else
            {
                _automaticScanPairEnabled = false;
                var captures = value?.CaptureSelectableEndpoints.Where(PhysicalDeviceGrouping.IsActiveEndpoint).ToArray() ?? [];
                var renders = value?.RenderCandidates.Where(e => e.DeviceState == "ACTIVE" && !string.IsNullOrWhiteSpace(e.EndpointId)).ToArray() ?? [];
                var endpoint = captures.Length == 1 ? captures[0] : renders.Length == 1 ? renders[0] : null;
                Set(ref _selectedWindowsEndpoint, endpoint, nameof(SelectedWindowsEndpoint));
                Set(ref _selectedDuplexCapture, null, nameof(SelectedDuplexCaptureEndpoint));
                Set(ref _selectedDuplexRender, null, nameof(SelectedDuplexRenderEndpoint));
            }
            Changed(nameof(AutomaticScanPairEnabled)); Changed(nameof(PhysicalGroupRequiresExplicitPair)); Changed(nameof(ShowNormalPairSelection)); Changed(nameof(AllowUnconfirmedPhysicalPair)); Changed(nameof(DuplexCaptureEndpoints)); Changed(nameof(DuplexRenderEndpoints)); Changed(nameof(AutomaticScanAvailabilityReason)); PrepareAutomaticScanCommand.Refresh(); RefreshContext(); RefreshCanExecute();
        }
    }
    public bool PhysicalGroupRequiresExplicitPair => SelectedPhysicalDeviceGroup?.RequiresExplicitPair == true;
    public bool PhysicalGroupRequiresExplicitDirection => SelectedPhysicalDeviceGroup is { RequiresExplicitCapture: true } or { RequiresExplicitRender: true };
    public IReadOnlyList<PhysicalDeviceEndpointDetailDto> PhysicalDeviceGroupDetails => _physicalDeviceGroups
        .SelectMany(group => group.CaptureEndpoints.Concat(group.RenderEndpoints).Select(endpoint => new PhysicalDeviceEndpointDetailDto(
            group.GroupId, group.DisplayName, endpoint.EndpointId ?? "неизвестен", endpoint.Direction, endpoint.DeviceState,
            endpoint.FormFactor, endpoint.FormFactorEvidence.State, endpoint.ContainerId ?? "неизвестен", endpoint.ContainerIdEvidence?.State ?? "UNAVAILABLE",
            EndpointCandidateReason(endpoint)))).ToArray();
    public bool CanOfferUnconfirmedPhysicalPair => SelectedPhysicalDeviceGroup is { } group && !group.CanAutoSelectPair &&
        WindowsEndpoints.Any(e => e.Direction == "CAPTURE" && PhysicalDeviceGrouping.IsSelectableCapture(e) && PhysicalDeviceGrouping.IsActiveEndpoint(e)) &&
        WindowsEndpoints.Any(e => e.Direction == "RENDER" && PhysicalDeviceGrouping.IsActiveEndpoint(e)) &&
        (group.ActiveCaptureSelectableCount == 0 || group.ActiveRenderCount == 0 || group.RequiresExplicitCapture || group.RequiresExplicitRender);
    public bool ShowNormalPairSelection => PhysicalGroupRequiresExplicitPair || PhysicalGroupRequiresExplicitDirection || CanOfferUnconfirmedPhysicalPair;
    public bool AllowUnconfirmedPhysicalPair
    {
        get => _allowUnconfirmedPhysicalPair;
        set
        {
            if (AutomaticScanBusy || !Set(ref _allowUnconfirmedPhysicalPair, value)) return;
            CancelAutomaticScanConsent();
            CancelDuplexRepeatabilityAuthorization();
            _automaticScanPairEnabled = value || PhysicalGroupRequiresExplicitPair;
            if (value || _automaticScanPairEnabled)
            {
                Set(ref _selectedWindowsEndpoint, null, nameof(SelectedWindowsEndpoint));
                Set(ref _selectedDuplexCapture, null, nameof(SelectedDuplexCaptureEndpoint));
                Set(ref _selectedDuplexRender, null, nameof(SelectedDuplexRenderEndpoint));
            }
            Changed(nameof(AutomaticScanPairEnabled)); Changed(nameof(ShowNormalPairSelection)); Changed(nameof(AutomaticScanAvailabilityReason));
            Changed(nameof(DuplexCaptureEndpoints)); Changed(nameof(DuplexRenderEndpoints)); PrepareAutomaticScanCommand.Refresh(); RefreshContext(); RefreshCanExecute();
        }
    }
    public IReadOnlyList<WindowsEndpointDto> DuplexCaptureEndpoints => GroupEndpoints("CAPTURE");
    public IReadOnlyList<WindowsEndpointDto> DuplexRenderEndpoints => GroupEndpoints("RENDER");
    private IReadOnlyList<WindowsEndpointDto> GroupEndpoints(string direction)
    {
        var members = AllowUnconfirmedPhysicalPair
            ? WindowsEndpoints
            : SelectedPhysicalDeviceGroup is { } group
            ? direction == "CAPTURE" ? group.CaptureEndpoints : group.RenderEndpoints
            : WindowsEndpoints;
        return members.Where(e => e.Direction == direction && e.DeviceState == "ACTIVE" && !string.IsNullOrWhiteSpace(e.EndpointId) && (direction != "CAPTURE" || PhysicalDeviceGrouping.IsSelectableCapture(e))).ToArray();
    }
    private static string EndpointCandidateReason(WindowsEndpointDto endpoint)
    {
        if (endpoint.Direction == "CAPTURE" && PhysicalDeviceGrouping.IsLoopbackEndpoint(endpoint)) return "Исключён: Loopback name hint";
        if (!PhysicalDeviceGrouping.IsActiveEndpoint(endpoint)) return "Исключён: endpoint неактивен или ID недоступен";
        if (endpoint.Direction == "CAPTURE" && !PhysicalDeviceGrouping.IsEligibleCapture(endpoint)) return "Явный выбор: FormFactor неизвестен или свойство недоступно";
        return endpoint.Direction == "CAPTURE" ? "Capture candidate" : endpoint.Direction == "RENDER" ? "Render candidate" : "Исключён: направление неизвестно";
    }
    public WindowsEndpointDto? SelectedDuplexCaptureEndpoint { get => _selectedDuplexCapture; set => SetDuplexEndpoint(ref _selectedDuplexCapture, value, true); }
    public WindowsEndpointDto? SelectedDuplexRenderEndpoint { get => _selectedDuplexRender; set => SetDuplexEndpoint(ref _selectedDuplexRender, value, false); }
    public bool DuplexConsentPending { get => _duplexConsentPending; private set { if (Set(ref _duplexConsentPending, value)) { Changed(nameof(DuplexConsentText)); ConfirmDuplexValidationCommand.Refresh(); } } }
    public bool DuplexConsentChecked { get => _duplexConsentChecked; set { if (Set(ref _duplexConsentChecked, value)) ConfirmDuplexValidationCommand.Refresh(); } }
    public bool DuplexValidationRunning => _duplexValidationRunning;
    public string DuplexStatus => _duplexStatus;
    public string DuplexConsentText => SelectedDuplexCaptureEndpoint is { } c && SelectedDuplexRenderEndpoint is { } r && _pendingDuplexPeriod is { } p ? $"Capture: {c.FriendlyNameDisplay} · {c.EndpointId}\nRender: {r.FriendlyNameDisplay} · {r.EndpointId}\n48 кГц · stereo PCM16 · WASAPI Exclusive · {p} · до 5 секунд · MMCSS OFF. Оба endpoint будут открыты Exclusive. Capture packets будут прочитаны и отброшены; микрофонное аудио не сохраняется. Render отправляет только цифровую тишину. Нет Capture→Render маршрутизации или тестового тона. Отмена координирует Stop обеих сторон; при потере worker cleanup остаётся UNKNOWN." : "Выберите активные Capture и Render endpoints.";
    public IReadOnlyList<DuplexCandidateAssessment> DuplexAssessments => _duplexValidations.Values.Select(v => DeviceConfigurationEvaluator.Evaluate(v.Report.GetProperty("requested").GetProperty("period").GetString()!, v.Report, v.CaptureEndpointId, v.RenderEndpointId, v.Report.GetProperty("requested").GetProperty("mmcss").GetString()!)).OrderBy(x => x.Period == "MINIMUM" ? 0 : 1).ToArray();
    public IReadOnlyList<DuplexResultCard> DuplexResultCards => [DeviceConfigurationEvaluator.ProjectCard("MINIMUM", _duplexValidations.GetValueOrDefault("MINIMUM")), DeviceConfigurationEvaluator.ProjectCard("DEFAULT", _duplexValidations.GetValueOrDefault("DEFAULT"))];
    public DuplexRecommendation DuplexRecommendation => DeviceConfigurationEvaluator.Recommend(DuplexAssessments);
    public bool HasDuplexResults => _duplexValidations.Count > 0;
    public string DuplexRecommendationSummary => DuplexRecommendation.Preferred is { } p ? $"PROBE_PROVISIONAL · {p.Period} · {p.Classification}. AudioHost: NOT_CERTIFIED. {string.Join(" ", DuplexRecommendation.Reasons)}" : string.Join(" ", DuplexRecommendation.Reasons);
    public DuplexRepeatabilityReadiness DuplexRepeatabilityReadiness
    {
        get
        {
            var endpoints = HasCurrentDuplexPair;
            var worker = _duplexRepeatabilityWorkflow is not null && WorkerState.State == WorkerConnectionState.ConnectedFake && !_workerStartOutcomeUnknown;
            var discovery = _windowsDiscoveryCurrent;
            var operation = AutomaticScanBusy || WorkerOperationActive || _deviceScanRunning || _realQueryRunning || _realInitializeRunning || _streamValidationRunning || _duplexValidationRunning || _duplexRepeatabilityRunning || _automaticScanOutcomeUnknown;
            var consent = AutomaticScanConsentPending || WorkerConsentPending || DuplexConsentPending || RealInitializeConsentPending || DeviceScanConsentPending || _streamConsentPending || _duplexRepeatabilityConsentPending;
            var reason = _duplexRepeatabilityWorkflow is null ? Localization.Get("VmRepeatabilityWorkflowUnavailable")
                : !worker ? _workerStartOutcomeUnknown ? Localization.Get("VmRepeatabilityWorkerOutcomeUnknown") : Localization.Get("VmRepeatabilityWorkerDisconnected")
                : !discovery ? Localization.Get("VmRepeatabilityRefreshDiscovery")
                : !endpoints ? Localization.Get("VmRepeatabilityChoosePair")
                : operation ? Localization.Get("VmRepeatabilityOperationActive")
                : consent ? Localization.Get("VmRepeatabilityAwaitingConsent")
                : Localization.Get("VmRepeatabilityReady");
            return new(!operation && !consent && endpoints && worker && discovery, reason, endpoints, worker, discovery, operation || consent);
        }
    }
    public bool CanPrepareDuplexRepeatability => DuplexRepeatabilityReadiness.CanPrepare;
    public bool DuplexRepeatabilityConsentPending => _duplexRepeatabilityConsentPending;
    public bool DuplexRepeatabilityConsentChecked { get => _duplexRepeatabilityConsentChecked; set { if (Set(ref _duplexRepeatabilityConsentChecked, value)) ConfirmDuplexRepeatabilityCommand.Refresh(); } }
    public bool DuplexRepeatabilityRunning => _duplexRepeatabilityRunning;
    public DuplexRepeatabilityResult? DuplexRepeatabilityResult => _duplexRepeatabilityResult;
    public string DuplexRepeatabilityStatus => _duplexRepeatabilityStatus;
    public string DuplexRepeatabilityAvailabilityReason => DuplexRepeatabilityReadiness.Reason;
    public string DuplexRepeatabilityPlanText => _duplexRepeatabilityPreparation is not { } preparation
        ? Localization.Get("VmRepeatabilitySelectExactEndpoints")
        : $"Capture: {preparation.Plan.Target.Capture!.FriendlyNameDisplay} · {preparation.Plan.Target.Capture.EndpointId}\nRender: {preparation.Plan.Target.Render!.FriendlyNameDisplay} · {preparation.Plan.Target.Render.EndpointId}\n" +
          $"48 кГц · {preparation.WorkerPlan.Formats.Single().Encoding} · {preparation.WorkerPlan.Formats.Single().Channels} канала\n" +
          "6 запусков · MINIMUM → DEFAULT → MINIMUM → DEFAULT → MINIMUM → DEFAULT\n" +
          $"5 секунд на операцию · 30 секунд общего активного аудио · MMCSS OFF\n" +
          $"Worker session: {preparation.WorkerPlan.LifetimeSeconds} секунд lifetime, {preparation.WorkerPlan.MaximumOperationDurationSeconds} секунд на операцию, {preparation.WorkerPlan.MaximumOperationCount} операций максимум\n" +
          "Capture data discarded; Render sends digital silence. Physical RTL excluded. AudioHost activation excluded.";
    public string DuplexRepeatabilityResultText => _duplexRepeatabilityResult is not { } result ? Localization.Get("VmRepeatabilityResultMissing") : $"{result.Summary}\n{Localization.Get("VmReportLabel")}: {result.ReportPath ?? Localization.Get("VmNotSaved")}\nPersistence: {result.PersistenceStatus ?? Localization.Get("VmNotConfirmed")}{(result.PersistenceError is null ? "" : $" · {result.PersistenceError}")}";
    public bool PeriodDiscoveryConsentPending => _periodDiscoveryConsentPending;
    public bool PeriodDiscoveryConsentChecked { get => _periodDiscoveryConsentChecked; set { if (Set(ref _periodDiscoveryConsentChecked, value)) ConfirmPeriodDiscoveryCommand.Refresh(); } }
    public bool PeriodDiscoveryRunning => _periodDiscoveryRunning;
    public string PeriodDiscoveryStatus => _periodDiscoveryStatus;
    public bool CanPreparePeriodDiscovery => CanPreparePeriodDiscoveryInternal();
    public string PeriodDiscoveryAvailabilityReason => !CanPreparePeriodDiscoveryInternal() ? PeriodDiscoveryBlockingReason() : Localization.Get("VmPeriodDiscoveryReady");
    public string PeriodDiscoveryPlanText => _periodDiscoveryPreparation?.PlanText ?? PeriodDiscoveryAvailabilityReason;
    public string PeriodDiscoveryMatrixText => _periodDiscoveryResult?.MatrixText ?? Localization.Get("VmPeriodMatrixMissing");
    public string PeriodDiscoveryReportPath => _periodDiscoveryResult?.ReportPath ?? Localization.Get("VmReportNotSaved");
    public string PeriodDiscoveryResultText => _periodDiscoveryResult is not { } result ? Localization.Get("VmPeriodDiscoveryResultMissing") : $"{result.Lifecycle}\n{result.MatrixText}\n{result.PersistenceStatus}\n{result.ReportPath ?? Localization.Get("VmReportNotSaved")}";
    public string CustomPeriodFramesText { get => _customPeriodFramesText; set { if (Set(ref _customPeriodFramesText, value)) { Changed(nameof(CustomPeriodDuplexAvailabilityReason)); Changed(nameof(CanPrepareCustomRepeatability)); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh(); } } }
    public bool CustomPeriodDuplexConsentPending => _customPeriodDuplexConsentPending;
    public bool CustomPeriodDuplexConsentChecked { get => _customPeriodDuplexConsentChecked; set { if (Set(ref _customPeriodDuplexConsentChecked, value)) ConfirmCustomPeriodDuplexCommand.Refresh(); } }
    public bool CustomPeriodDuplexRunning => _customPeriodDuplexRunning;
    public string CustomPeriodDuplexStatus => _customPeriodDuplexStatus;
    public bool CanPrepareCustomPeriodDuplex => CanPrepareCustomPeriodDuplexInternal();
    public string CustomPeriodDuplexAvailabilityReason => Localization.Get(CanPrepareCustomPeriodDuplexInternal() ? "VmCustomPeriodDuplexReady" : "VmCustomPeriodDuplexNeedsDiscovery");
    public string CustomPeriodDuplexPlanText => _customPeriodDuplexPreparation?.PlanText ?? CustomPeriodDuplexAvailabilityReason;
    public string CustomPeriodDuplexResultText => _customPeriodDuplexResult is not { } result ? Localization.Get("VmCustomPeriodDuplexResultMissing") : $"{result.Lifecycle}\n{result.PersistenceStatus}\n{result.ReportPath ?? Localization.Get("VmReportNotSaved")}{(result.Error is null ? "" : $"\n{result.Error}")}";
    public bool CustomRepeatabilityConsentPending => _customRepeatabilityConsentPending;
    public bool CustomRepeatabilityConsentChecked { get => _customRepeatabilityConsentChecked; set { if (Set(ref _customRepeatabilityConsentChecked, value)) ConfirmCustomRepeatabilityCommand.Refresh(); } }
    public bool CustomRepeatabilityRunning => _customRepeatabilityRunning;
    public string CustomRepeatabilityStatus => _customRepeatabilityStatus;
    public bool CanPrepareCustomRepeatability => CanPrepareCustomRepeatabilityInternal();
    public string CustomRepeatabilityPlanText => _customRepeatabilityPreparation is not { } p ? Localization.Get("VmCustomRepeatabilityPlanMissing") : string.Format(Localization.CurrentCulture, Localization.Get("VmCustomRepeatabilityPlanRunCount"), p.Periods!.Count, p.Periods[0]) + "\n" + string.Format(Localization.CurrentCulture, Localization.Get("VmCustomRepeatabilityPlanTiming"), p.PauseMilliseconds) + "\n" + Localization.Get("VmCustomRepeatabilityPlanSafety");
    public string CustomRepeatabilityResultText => _customRepeatabilityResult is not { } r ? Localization.Get("VmCustomRepeatabilityResultMissing") : $"{r.Summary}\n{r.ReportPath ?? Localization.Get("VmReportNotSaved")}";
    public IReadOnlyList<WindowsEndpointDto> LastWindowsEndpoints => _lastWindowsEndpointSnapshot?.Endpoints ?? [];
    private IReadOnlyList<WindowsEndpointDto> WindowsEndpointSource => _windowsDiscoveryCurrent ? WindowsEndpoints : HasHistoricalWindowsEndpoints ? LastWindowsEndpoints : [];
    public IReadOnlyList<WindowsEndpointDto> WindowsEndpointRows => WindowsEndpointSource
        .Where(e => !_windowsEndpointActiveOnly || e.DeviceState == "ACTIVE")
        .Where(e => _windowsEndpointDirectionFilter == "ALL" || e.Direction == _windowsEndpointDirectionFilter)
        .Where(e => string.IsNullOrWhiteSpace(_windowsEndpointSearchText) || e.FriendlyNameDisplay.Contains(_windowsEndpointSearchText.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(e => e.DeviceState == "ACTIVE")
        .ThenBy(e => e.FriendlyNameDisplay, StringComparer.OrdinalIgnoreCase)
        .ThenBy(e => e.Direction, StringComparer.Ordinal)
        .ToArray();
    public WindowsEndpointDto? SelectedWindowsEndpoint
    {
        get => _selectedWindowsEndpoint;
        set { if (_automaticScanRunning || _automaticScanPreparing) return; if (value is not null && !WindowsEndpointSource.Contains(value)) return; if (Set(ref _selectedWindowsEndpoint, value)) { if (AutomaticScanConsentPending) CancelAutomaticScanConsent(); if (RealFormatMatrix is not null) _realQueryInvalidated = true; _deviceScanInitializations = []; SelectedStreamCandidate = null; if (RealInitializeConsentPending) CancelRealInitialize(); if (DeviceScanConsentPending) CancelDeviceScanConsent(); if (_streamConsentPending) CancelStreamConsent(); if (_deviceScanRunning) CancelDeviceScan(); if (_streamValidationRunning) _ = CancelStreamValidationAsync(); Changed(nameof(HasSelectedWindowsEndpoint)); Changed(nameof(IsSelectedWindowsCapture)); Changed(nameof(IsSelectedWindowsRender)); Changed(nameof(WindowsEndpointInspectorFields)); Changed(nameof(CanUseSelectedWindowsEndpoint)); Changed(nameof(DeviceScanAvailabilityReason)); Changed(nameof(AutomaticScanAvailabilityReason)); Changed(nameof(RealFormatMatrixIsStale)); Changed(nameof(RealFormatMatrixStatus)); Changed(nameof(HasCurrentOperatorQueryResult)); Changed(nameof(NeedsInitializeFormatQuery)); Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(RealFormatEndpointLabel)); Changed(nameof(RealInitializationDetails)); Changed(nameof(RealInitializationSummary)); Changed(nameof(OperatorSelectedDevice)); Changed(nameof(CanSaveDeviceProfile)); Changed(nameof(DeviceScanConsentText)); Changed(nameof(StreamValidationConsentText)); Changed(nameof(StreamValidationAvailability)); RequestDeviceScanCommand.Refresh(); PrepareAutomaticScanCommand.Refresh(); RequestStreamValidationCommand.Refresh(); RefreshContext(); QueryRealFormatsCommand.Refresh(); RequestRealInitializeCommand.Refresh(); } }
    }
    public bool CanUseSelectedWindowsEndpoint => _windowsDiscoveryCurrent && SelectedWindowsEndpoint is { EndpointId.Length: > 0, DeviceState: "ACTIVE", Direction: "CAPTURE" or "RENDER" };
    public bool IsSelectedWindowsCapture => SelectedWindowsEndpoint?.Direction == "CAPTURE";
    public bool IsSelectedWindowsRender => SelectedWindowsEndpoint?.Direction == "RENDER";
    public string RealQueryChannelCountsText { get => _realQueryChannelCountsText; set { if (Set(ref _realQueryChannelCountsText, value ?? string.Empty)) QueryRealFormatsCommand.Refresh(); } }
    public RealFormatMatrixDto? RealFormatMatrix { get => _realFormatMatrix; private set { if (Set(ref _realFormatMatrix, value)) { Changed(nameof(RealFormatRows)); Changed(nameof(RealFormatSummary)); Changed(nameof(OperatorResultDetails)); Changed(nameof(OperatorResultProvenance)); Changed(nameof(HasOperatorQueryResult)); Changed(nameof(HasCurrentOperatorQueryResult)); Changed(nameof(NeedsInitializeFormatQuery)); Changed(nameof(HasOperatorInitializeResult)); Changed(nameof(RealFormatMatrixIsStale)); Changed(nameof(RealFormatMatrixStatus)); Changed(nameof(RealFormatEndpointLabel)); Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(CanSaveDeviceProfile)); RequestRealInitializeCommand.Refresh(); } } }
    public RealFormatMatrixDto? RealInitializationResult { get => _realInitializationResult; private set { if (Set(ref _realInitializationResult, value)) { Changed(nameof(RealInitializationDetails)); Changed(nameof(RealInitializationSummary)); Changed(nameof(RealFormatSummary)); Changed(nameof(OperatorResultDetails)); Changed(nameof(OperatorResultProvenance)); Changed(nameof(HasOperatorQueryResult)); Changed(nameof(HasOperatorInitializeResult)); Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(CanSaveDeviceProfile)); } } }
    public RealFormatCandidateDto? SelectedRealFormatCandidate { get => _selectedRealFormatCandidate; set { if (Set(ref _selectedRealFormatCandidate, value)) { if (RealInitializeConsentPending) CancelRealInitialize(); RequestRealInitializeCommand.Refresh(); Changed(nameof(RealInitializeConsentText)); Changed(nameof(RealSelectedCandidateDetails)); Changed(nameof(RealInitializeDisabledReason)); } } }
    public bool RealFormatMatrixIsStale => RealFormatMatrix is not null && (_realQueryInvalidated || !_windowsDiscoveryCurrent || WorkerState.State != WorkerConnectionState.ConnectedFake || SelectedWindowsEndpoint?.EndpointId != RealFormatMatrix.EndpointId || SelectedWindowsEndpoint?.Direction != RealFormatMatrix.Direction);
    public string RealFormatMatrixStatus => _realFormatError ?? (RealFormatMatrix is null ? Localization.Get("VmRealFormatQueryNotRun") : RealFormatMatrixIsStale ? Localization.Get("VmRealFormatHistoryStale") : string.Format(Localization.CurrentCulture, Localization.Get("VmRealFormatQuerySummary"), RealFormatRows.Count));
    public string RealFormatEndpointLabel => RealFormatMatrix is null ? "" : WindowsEndpointSource.FirstOrDefault(endpoint => endpoint.EndpointId == RealFormatMatrix.EndpointId && endpoint.Direction == RealFormatMatrix.Direction)?.FriendlyNameDisplay ?? Localization.Get("VmWindowsEndpointLocalIdHidden");
    public string RealSelectedCandidateDetails
    {
        get
        {
            if (SelectedRealFormatRow is { IsConflict: true } conflictRow)
                return $"{Localization.Get("VmRealFormatConflictHeader")} {conflictRow.ConflictDetails}\n" + string.Join("\n", conflictRow.EvidenceCandidates.Select(c => $"{c.Period}: Status={c.Status}, IsFormatSupported={c.SupportStatus}, HRESULT={c.SupportHr ?? "не наблюдался"}, query cleanup={c.QueryCleanupStatus}, release attempts={c.QueryClientReleaseAttempts}"));
            if (SelectedRealFormatCandidate is not { } c) return Localization.Get("VmRealFormatCandidateSelectForDetails");
            static string V<T>(T? value) where T : struct => value?.ToString() ?? "неизвестно";
            static string S(string? value) => value ?? "не наблюдался";
            return $"{c.Status} · {c.Encoding} · {c.SampleRate} Гц · {c.Channels} канал(а) · контейнер/valid bits: {V(c.ContainerBits)}/{V(c.ValidBits)} · канал-маска: {c.ChannelMaskEvidence} · период: {c.Period}\n" +
                $"Query: Activate {S(c.ActivationHr)} · IsFormatSupported {S(c.SupportHr)} ({c.SupportStatus}) · GetDevicePeriod {S(c.DevicePeriodHr)} · default/minimum {V(c.DefaultPeriod100ns)}/{V(c.MinimumPeriod100ns)} ×100 нс · query cleanup {c.QueryCleanupStatus}, Release попыток {c.QueryClientReleaseAttempts}\n" +
                $"Initialize: {c.InitializeStatus}, попытка {c.InitializeAttempted} · период API {S(c.PeriodQueryHr)}, доступен {V(c.PeriodAvailable)} · запрос {V(c.RequestedPeriod100ns)} ×100 нс · первый HRESULT {S(c.FirstInitializeHr)}\n" +
                $"Alignment retry: GetBufferSize {S(c.AlignmentGetBufferSizeHr)}, frames {V(c.AlignmentFrames)}, Activate {S(c.AlignmentActivateHr)}, период {V(c.AlignmentPeriod100ns)} ×100 нс, Initialize {S(c.AlignmentInitializeHr)} · итог {S(c.FinalInitializeHr)}\n" +
                $"Буфер: HRESULT {S(c.ActualBufferSizeHr)}, {V(c.ActualBufferFrames)} frames, {V(c.ActualPeriod100ns)} ×100 нс · event create {S(c.EventCreateHr)}, SetEventHandle {S(c.SetEventHandleHr)}, close {S(c.EventHandleCloseHr)}, ready {V(c.EventHandleReady)} · cleanup {c.CleanupStatus}, Release попыток {c.ClientReleaseAttempts}";
        }
    }
    public string RealInitializationDetails
    {
        get
        {
            if (_realInitializationError is { } error) return error;
            if (RealInitializationResult is not { } result) return Localization.Get("VmRealInitializeNotPerformed");
            var c = result.Candidates.FirstOrDefault();
            static string V<T>(T? value) where T : struct => value?.ToString() ?? "неизвестно";
            static string S(string? value) => value ?? "не наблюдался";
            var endpointName = WindowsEndpointSource.FirstOrDefault(endpoint => endpoint.EndpointId == result.EndpointId && endpoint.Direction == result.Direction)?.FriendlyNameDisplay ?? Localization.Get("VmWindowsEndpointLocalIdHidden");
            return $"{(RealFormatMatrixIsStale ? "ИСТОРИЧЕСКИЙ · " : string.Empty)}REAL INITIALIZE-ONLY · {c?.Status ?? "INCOMPLETE"} · {endpointName} · {c?.Encoding ?? "формат неизвестен"}, {V(c?.SampleRate)} Гц, {V(c?.Channels)} канал(а), {c?.Period ?? "период неизвестен"}\n" +
                $"Поддержка: {c?.SupportStatus ?? "неизвестно"} · IsFormatSupported {S(c?.SupportHr)} · Activate {S(c?.ActivationHr)} · query cleanup {c?.QueryCleanupStatus ?? "неизвестно"} ({V(c?.QueryClientReleaseAttempts)} Release попыток)\n" +
                $"Initialize: period query {S(c?.PeriodQueryHr)}, доступен {V(c?.PeriodAvailable)}, запрос {V(c?.RequestedPeriod100ns)} ×100 нс · первый {S(c?.FirstInitializeHr)} · Alignment retry: GetBufferSize {S(c?.AlignmentGetBufferSizeHr)} / {V(c?.AlignmentFrames)} frames · Activate {S(c?.AlignmentActivateHr)} · retry Initialize {S(c?.AlignmentInitializeHr)} · итог {S(c?.FinalInitializeHr)}\n" +
                $"Буфер: HRESULT {S(c?.ActualBufferSizeHr)}, {V(c?.ActualBufferFrames)} frames / {V(c?.ActualPeriod100ns)} ×100 нс · событие: create {S(c?.EventCreateHr)}, SetEventHandle {S(c?.SetEventHandleHr)}, close {S(c?.EventHandleCloseHr)}, ready {V(c?.EventHandleReady)} · cleanup {c?.CleanupStatus ?? "неизвестно"} ({V(c?.ClientReleaseAttempts)} Release попыток). Start не вызывался; PCM не передавался.";
        }
    }
    public string RealInitializationSummary
    {
        get
        {
            if (_realInitializationError is { } error) return error;
            if (RealInitializationResult is not { } result || result.Candidates.FirstOrDefault() is not { } c) return Localization.Get("VmRealInitializeNotPerformed");
            var endpointName = WindowsEndpointSource.FirstOrDefault(endpoint => endpoint.EndpointId == result.EndpointId && endpoint.Direction == result.Direction)?.FriendlyNameDisplay ?? Localization.Get("VmWindowsEndpointLocalIdHidden");
            var requestedPeriod = c.RequestedPeriod100ns is { } period ? $"{period / 10000.0:0.##} мс" : "величина неизвестна";
            var initializeHr = c.FinalInitializeHr ?? c.FirstInitializeHr ?? "не наблюдался";
            var eventReady = c.EventHandleReady switch { true => "готовность подтверждена", false => "готовность не достигнута", null => "неизвестно" };
            var cleanup = c.CleanupStatus == "RELEASE_ATTEMPTED" ? "RELEASE_ATTEMPTED · попытка освобождения; очистка не подтверждена" : c.CleanupStatus;
            return $"REAL INITIALIZE-ONLY RESULT\nУстройство: {endpointName}\nФормат: {c.Encoding} · {c.SampleRate} Гц · {c.Channels} канал(а)\nIsFormatSupported status: {c.SupportStatus}\nЗапрошенный период: {c.Period.ToUpperInvariant()} · {requestedPeriod}\nInitialize status: {c.InitializeStatus} · HRESULT: {initializeHr}\nФактический буфер: {c.ActualBufferFrames?.ToString() ?? "неизвестно"} frames\nEvent readiness: {eventReady}\nCleanup evidence: {cleanup}\nStart: NOT_STARTED · PCM: NOT_TRANSFERRED";
        }
    }
    public bool RealInitializeConsentPending { get => _realInitializeConsentPending; private set { if (Set(ref _realInitializeConsentPending, value)) { Changed(nameof(RealInitializeConsentChecked)); Changed(nameof(RealInitializeConsentText)); Changed(nameof(RealInitializeDisabledReason)); ConfirmRealInitializeCommand.Refresh(); CancelRealInitializeCommand.Refresh(); RequestDeviceScanCommand.Refresh(); } } }
    public bool RealInitializeConsentChecked { get => _realInitializeConsentChecked; set { if (Set(ref _realInitializeConsentChecked, value)) ConfirmRealInitializeCommand.Refresh(); } }
    public string RealInitializeConsentText => _pendingRealFormatCandidate is { } c && _pendingRealEndpointId is not null ? string.Format(Localization.CurrentCulture, Localization.Get("VmRealInitializeConsentDescription"), _pendingRealDirection, c.Encoding, c.SampleRate, c.Channels, c.Period) : Localization.Get("VmRealInitializeSelectSupportedFormat");
    public bool HasSelectedWindowsEndpoint => SelectedWindowsEndpoint is not null;
    public string WindowsEndpointSearchText
    {
        get => _windowsEndpointSearchText;
        set { if (Set(ref _windowsEndpointSearchText, value ?? string.Empty)) RefreshWindowsEndpointBrowser(); }
    }
    public string WindowsEndpointDirectionFilter
    {
        get => _windowsEndpointDirectionFilter;
        set { if ((value is "ALL" or "CAPTURE" or "RENDER") && Set(ref _windowsEndpointDirectionFilter, value)) RefreshWindowsEndpointBrowser(); }
    }
    public bool WindowsEndpointActiveOnly
    {
        get => _windowsEndpointActiveOnly;
        set { if (Set(ref _windowsEndpointActiveOnly, value)) RefreshWindowsEndpointBrowser(); }
    }
    public IReadOnlyList<ChoiceOption> WindowsEndpointDirectionOptions { get; } = [new("ALL", Localization.Get("VmAllDirections")), new("CAPTURE", "Capture"), new("RENDER", "Render")];
    public int WindowsEndpointTotalCount => WindowsEndpointSource.Count;
    public int WindowsEndpointDisplayedCount => WindowsEndpointRows.Count;
    public int WindowsEndpointActiveCaptureCount => WindowsEndpointSource.Count(e => e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE");
    public int WindowsEndpointActiveRenderCount => WindowsEndpointSource.Count(e => e.Direction == "RENDER" && e.DeviceState == "ACTIVE");
    public string WindowsEndpointCountsText => string.Format(Localization.CurrentCulture, Localization.Get("VmWindowsEndpointCounts"), WindowsEndpointTotalCount, WindowsEndpointDisplayedCount, WindowsEndpointActiveCaptureCount, WindowsEndpointActiveRenderCount);
    public IReadOnlyList<InspectorFieldDto> WindowsEndpointInspectorFields => SelectedWindowsEndpoint is { } e ? WindowsEndpointDetails(e) : [];
    public bool HasCurrentWindowsEndpoints => _windowsDiscoveryCurrent && WindowsEndpoints.Count > 0;
    public bool HasHistoricalWindowsEndpoints => WorkerState.State != WorkerConnectionState.ConnectedFake && _lastWindowsEndpointSnapshot is not null;
    public bool WindowsDiscoveryLoading => _windowsDiscoveryLoading;
    public string WindowsDiscoveryStatusText => _windowsDiscoveryLoading ? Localization.Get("VmWindowsEndpointsEnumerating") : _windowsDiscoveryCurrent
        ? _windowsEndpointSnapshot is { OperationState: "PARTIAL" } partial ? string.Format(Localization.CurrentCulture, Localization.Get("VmWindowsDiscoveryPartial"), partial.Endpoints.Count, partial.Errors.Count) : WindowsEndpoints.Count == 0 ? Localization.Get("VmWindowsEndpointsNoneFound") : string.Format(Localization.CurrentCulture, Localization.Get("VmWindowsEndpointsReceived"), WindowsEndpoints.Count)
        : HasHistoricalWindowsEndpoints ? Localization.Get("VmWindowsDiscoveryWorkerDisconnectedHistorical")
        : _windowsDiscoveryError is not null ? string.Format(Localization.CurrentCulture, Localization.Get("VmWindowsDiscoveryRequestFailed"), _windowsDiscoveryError)
        : Localization.Get("VmWindowsDiscoveryNotRequested");
    public IReadOnlyList<DeviceConfigurationProfileStorage.SavedProfileEntry> SavedDeviceProfiles => _savedDeviceProfiles;
    public IReadOnlyList<HistoricalSustainedScanEntry> HistoricalSustainedScans => _historicalSustainedScans;
    public HistoricalSustainedScanEntry? SelectedHistoricalSustainedScan
    {
        get => _selectedHistoricalSustainedScan;
        set { if (Set(ref _selectedHistoricalSustainedScan, value)) InvalidateHistoricalRecovery("Выбор исторического измерения изменён."); }
    }
    public string HistoricalRecoveryStatus => _historicalRecoveryStatus;
    public bool HistoricalRecoveryPreviewVisible => _historicalRecoveryPreview is not null;
    public bool HistoricalRecoveryRunning => _historicalRecoveryRunning;
    public bool HistoricalRecoverySaving => _historicalRecoverySaving;
    public bool HistoricalRecoveryConfirmationChecked { get => _historicalRecoveryConfirmationChecked; set { if (Set(ref _historicalRecoveryConfirmationChecked, value)) SaveHistoricalSustainedRecoveryCommand.Refresh(); } }
    public string HistoricalRecoveryPreviewDevice => _selectedHistoricalSustainedScan?.DeviceName ?? "—";
    public string HistoricalRecoveryPreviewDates => _historicalRecoveryPreview is null ? "—" : string.Format(Localization.CurrentCulture, Localization.Get("VmHistoricalRecoveryPreviewDates"), _historicalRecoveryPreview.HistoricalMeasurementTimestampUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Localization.CurrentCulture), _selectedHistoricalSustainedScan?.TimestampLabel ?? "—");
    public string HistoricalRecoveryPreferred => _historicalRecoveryPreview?.Recommendation.Preferred is { } p ? $"{p.Period} · {p.CaptureBufferFrames}/{p.RenderBufferFrames} frames · {p.Mmcss} · {p.DurationSeconds} с" : "NONE";
    public string HistoricalRecoveryFallback => _historicalRecoveryPreview?.Recommendation.Fallback is { } f ? $"{f.Period} · {f.CaptureBufferFrames}/{f.RenderBufferFrames} frames" : "NONE";
    public string HistoricalRecoveryConfidence => _historicalRecoveryPreview?.Recommendation.Preferred?.Confidence ?? "NONE";
    public string HistoricalRecoveryTechnicalDetails => string.Join("\n", new[] { _selectedHistoricalSustainedScan?.TechnicalDetails, _historicalRecoveryTechnicalError }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public DeviceConfigurationProfileStorage.SavedProfileEntry? SelectedSavedDeviceProfile
    {
        get => _selectedSavedDeviceProfile;
        set
        {
            if (!Set(ref _selectedSavedDeviceProfile, value)) return;
            InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason.ProfileSelectionChanged);
            Changed(nameof(SavedProfileHistoricalSummary)); Changed(nameof(SavedProfileBindingStatus)); Changed(nameof(SavedProfileBindingTechnicalDetails));
            CheckSavedProfileBindingCommand.Refresh();
        }
    }
    public bool SavedProfileBindingChecking => _bindingCheckRunning;
    public bool HasSavedProfileBindingAssessment => _savedProfileBindingResult is not null;
    public string SavedProfileBindingStatus => _savedProfileBindingResult is null ? Localization.Get("VmSavedProfileBindingNotChecked") : BindingStatus(_savedProfileBindingResult);
    public string SavedProfileOperatorStatus => _savedProfileOperatorStatus;
    public string SavedProfileOperatorHistory => _savedProfileOperatorHistory;
    public string SavedProfileOperatorLimitation => _savedProfileOperatorLimitation;
    public string SavedProfileMatchingSummary => _matchingSavedProfiles.Count == 0 ? Localization.Get("VmSavedProfileMatchesNone") : string.Join("\n", _matchingSavedProfiles.Select(p => $"{p.OperatorDisplayName} · {p.OperatorTimestampLabel} · {p.OperatorResult}"));
    public string SavedProfileHistoricalSummary
    {
        get
        {
            var profile = SelectedSavedDeviceProfile?.Profile;
            if (profile is null) return SelectedSavedDeviceProfile?.Reason ?? Localization.Get("VmSavedProfileNotSelected");
            if (profile.SchemaVersion == 4 && profile.SustainedSelection is { } sustained)
            {
                var primary = sustained.Preferred is { } p ? $"{p.Period} · {p.DurationSeconds} с · {p.Confidence}" : "NONE";
                var fallback = sustained.Fallback is { } f ? $"{f.Period} · {f.DurationSeconds} с" : "NONE";
                return $"Primary: {primary}\nFallback: {fallback}\nAudioHost: NOT_CERTIFIED · Physical RTL: NOT_MEASURED";
            }
            return $"v{profile.SchemaVersion} · исторические короткие результаты\nAudioHost: NOT_CERTIFIED · Physical RTL: NOT_MEASURED";
        }
    }
    public string SavedProfileBindingExplanation => _savedProfileBindingResult?.Overall == DeviceProfileBindingAssessment.ExactBindingsPresentInDiscovery
        ? "Совпали только точные endpoint IDs в свежем списке. Стабильность, Exclusive-период, драйвер/firmware, AudioHost и физический RTL повторно не подтверждены."
        : "Проверка не разрешает применение Primary/Fallback и не превращает исторические результаты в свежие измерения.";
    public string SavedProfileBindingTechnicalDetails => _savedProfileBindingResult is { } result
        ? $"Overall: {result.Overall}\nCapture: {result.Capture}\nRender: {result.Render}\nProfile: {result.SourceProfileId:D}\nScan session: {result.SourceScanSessionId ?? "UNKNOWN"}\nGeneration: {result.DiscoveryGeneration ?? "UNKNOWN"}\nExact IDs: {(result.ExactEndpointIdentities.Count == 0 ? "NONE" : string.Join("; ", result.ExactEndpointIdentities))}\nReasons: {(result.Reasons.Count == 0 ? "NONE" : string.Join("; ", result.Reasons))}\nLimitations: {(result.Limitations.Count == 0 ? "NONE" : string.Join("; ", result.Limitations))}\nFurther verification required: {result.RequiresFurtherVerification}"
        : "Технических данных нет.";
    public IReadOnlyList<string> HistoricalWorkerEndpointNames => LastWorkerEndpointSnapshot.Select(e => $"{e.Name} · {e.Direction} · {e.ChannelCount} каналов · synthetic").ToArray();
    public string WorkerRestartExplanation => _workerAttempted && (WorkerState.State is WorkerConnectionState.Disconnected or WorkerConnectionState.Failed or WorkerConnectionState.Incompatible) ? Localization.Get("VmWorkerRestartRequired") : string.Empty;
    public int WorkerRenderChannel { get => _workerRenderChannel; set { if (value >= 1 && value <= WorkerChannelCount && Set(ref _workerRenderChannel, value)) RefreshWorkerCommands(); } }
    public IReadOnlyList<SelectionOption> WorkerRenderChannelOptions => Enumerable.Range(1, WorkerChannelCount).Select(i => new SelectionOption(i, $"Канал Windows {i}")).ToArray();
    public int WorkerDurationSeconds { get => _workerDurationSeconds; set { if (DurationOptions.Any(o => o.Value == value) && Set(ref _workerDurationSeconds, value)) RefreshWorkerCommands(); } }
    public string WorkerPeriod { get => _workerPeriod; set { if ((value is "DEFAULT" or "MINIMUM") && Set(ref _workerPeriod, value)) RefreshWorkerCommands(); } }
    public string WorkerMmcss { get => _workerMmcss; set { if ((value is "OFF" or "PRO_AUDIO") && Set(ref _workerMmcss, value)) RefreshWorkerCommands(); } }
    public string WorkerScenario { get => _workerScenario; set { if ((value is "NORMAL" or "WARNING" or "FAILURE" or "DEVICE_LOSS" or "ALL_SILENT" or "NO_WINDOW") && Set(ref _workerScenario, value)) RefreshWorkerCommands(); } }
    public bool WorkerConsentPending { get => _workerConsentPending; private set { if (Set(ref _workerConsentPending, value)) { ConfirmWorkerSessionCommand.Refresh(); CancelWorkerConsentCommand.Refresh(); RefreshWorkerCommands(); } } }
    public bool WorkerConsentChecked { get => _workerConsentChecked; set { if (Set(ref _workerConsentChecked, value)) ConfirmWorkerSessionCommand.Refresh(); } }
    public WorkerSessionSettingsDto? WorkerPendingSettings => _workerPendingSettings;
    public WorkerSessionSettingsDto? WorkerSessionSettings { get => _workerSessionSettings; private set { if (Set(ref _workerSessionSettings, value)) { Changed(nameof(WorkerSessionSettingsText)); Changed(nameof(WorkerMeterFormatSummary)); RefreshContext(); } } }
    public SessionDto? WorkerSession { get => _workerSession; private set { if (value is not null && _workerSession?.Id != value.Id) { _workerCaptureMeter = null; _workerRenderMeter = null; _workerMeterBank = 0; _workerMeterStale = false; _workerMeterFinal = false; Changed(nameof(WorkerMeterBank)); Changed(nameof(WorkerMeterVisible)); Changed(nameof(WorkerMeterHeading)); Changed(nameof(WorkerMeterInterpretation)); Changed(nameof(WorkerVisibleMeterChannels)); Changed(nameof(WorkerMeterStatusText)); } if (Set(ref _workerSession, value)) { Changed(nameof(WorkerOperationActive)); Changed(nameof(CanSaveDeviceProfile)); Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(WorkerStatusVisible)); Changed(nameof(WorkerMeterVisible)); Changed(nameof(WorkerMeterHeading)); Changed(nameof(WorkerMeterInterpretation)); Changed(nameof(WorkerVisibleMeterChannels)); Changed(nameof(WorkerMeterStatusText)); RequestDeviceScanCommand.Refresh(); RefreshWorkerCommands(); RefreshContext(); } } }
    public bool WorkerOperationActive => WorkerSession?.Lifecycle is SessionLifecycle.Preparing or SessionLifecycle.Ready or SessionLifecycle.Started or SessionLifecycle.Stopping;
    public string WorkerNotice { get => _workerNotice; private set => Set(ref _workerNotice, value); }
    public bool WorkerMeterVisible => WorkerSession is not null;
    public string WorkerMeterHeading => WorkerSession?.Mode == SessionMode.Render ? Localization.Get("VmWorkerRenderMeterHeading") : Localization.Get("VmWorkerCaptureMeterHeading");
    public string WorkerMeterInterpretation => WorkerSession?.Mode == SessionMode.Render ? $"Канал Windows {WorkerSession.RenderChannel} · остальные каналы намеренно цифрово беззвучны. Это модель намерения, не измерение ЦАП/громкоговорителей; физическая маршрутизация UNKNOWN." : "Синтетические уровни окна; PCM, Capture endpoints и микрофон не используются.";
    public int WorkerMeterBank { get => _workerMeterBank; set { if (value >= 0 && value < WorkerMeterBanks.Count && Set(ref _workerMeterBank, value)) Changed(nameof(WorkerVisibleMeterChannels)); } }
    public IReadOnlyList<SelectionOption> WorkerMeterBanks
    {
        get
        {
            var count = WorkerSession?.Mode == SessionMode.Render ? _workerRenderMeter?.ChannelCount ?? WorkerSession?.ChannelCount ?? 0 : _workerCaptureMeter?.ChannelCount ?? WorkerSession?.ChannelCount ?? 0;
            return Enumerable.Range(0, (count + 7) / 8).Select(bank => new SelectionOption(bank, $"Каналы {bank * 8 + 1}–{Math.Min(bank * 8 + 8, count)}")).ToArray();
        }
    }
    public IReadOnlyList<MeterChannelDto> WorkerVisibleMeterChannels
    {
        get
        {
            IEnumerable<MeterChannelDto> channels = WorkerSession?.Mode switch
            {
                SessionMode.Capture => (_workerCaptureMeter?.Channels ?? []).Select(c => new MeterChannelDto(c.Index, c.WindowPeakDbfs, c.WindowRmsDbfs, c.SessionPeakHoldDbfs, c.ClipCount, c.Silent, c.Silent == true ? "Цифровая тишина" : c.Silent == false ? "Синтетическое окно" : "Нет измерения")),
                SessionMode.Render => (_workerRenderMeter?.Channels ?? []).Select(c => new MeterChannelDto(c.Index, c.IntendedPeakDbfs, c.IntendedRmsDbfs, c.SessionPeakHoldDbfs, c.ClipCount, c.DigitalSilent, c.DigitalSilent == true ? "Намеренная цифровая тишина" : c.DigitalSilent == false ? "Предполагаемый уровень · модель" : "Нет завершённого окна")),
                _ => []
            };
            return channels.Where(c => c.Channel > _workerMeterBank * 8 && c.Channel <= _workerMeterBank * 8 + 8).ToArray();
        }
    }
    public string WorkerMeterStatusText
    {
        get
        {
            var count = WorkerSession?.Mode == SessionMode.Render ? _workerRenderMeter?.ChannelCount : _workerCaptureMeter?.ChannelCount;
            var sequence = WorkerSession?.Mode == SessionMode.Render ? _workerRenderMeter?.SnapshotSequence : _workerCaptureMeter?.SnapshotSequence;
            var completed = WorkerSession?.Mode == SessionMode.Render ? _workerRenderMeter?.CompletedWindowIndex : _workerCaptureMeter?.CompletedWindowIndex;
            if (_workerMeterStale) return Localization.Get("VmWorkerMeterStale");
            if (sequence is null) return Localization.Get("VmWorkerMeterSnapshotMissing");
            if (_workerMeterFinal) return completed is null ? string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterFinalWindowMissing"), sequence) : string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterFinalData"), sequence, count);
            if (WorkerSession?.Lifecycle == SessionLifecycle.Stopping) return string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterStopping"), sequence);
            if (completed is null) return Localization.Get("VmWorkerMeterWindowMissing");
            return string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterLive"), sequence, completed, count);
        }
    }
    public string WorkerMeterModeLabel => WorkerSession?.Mode switch { SessionMode.Capture => "Capture", SessionMode.Render => Localization.Get("VmWorkerMeterRenderMode"), _ => Localization.Get("VmWorkerMeterNoSession") };
    public string WorkerMeterFormatSummary => _workerSessionSettings is { } s ? $"{s.FormatId} · {s.SampleRate} Гц · {s.ChannelCount} каналов · {s.DurationSeconds} с" : Localization.Get("VmWorkerMeterFormatUnknown");
    public string WorkerMeterSnapshotSummary => WorkerSession?.Mode == SessionMode.Render
        ? _workerRenderMeter is { } r ? $"Снимок {r.SnapshotSequence} · {r.ChannelCount} каналов" : Localization.Get("VmWorkerMeterSnapshotNoData")
        : _workerCaptureMeter is { } c ? $"Снимок {c.SnapshotSequence} · {c.ChannelCount} каналов" : Localization.Get("VmWorkerMeterSnapshotNoData");
    public string WorkerMeterCoalescingSummary => WorkerSession?.Mode == SessionMode.Render
        ? _workerRenderMeter is { } r ? $"Объединено: {r.CoalescedSnapshotCount}" : Localization.Get("VmWorkerMeterCoalescedNoData")
        : _workerCaptureMeter is { } c ? $"Объединено: {c.CoalescedSnapshotCount}" : Localization.Get("VmWorkerMeterCoalescedNoData");
    public string WorkerConsentText => _workerPendingSettings is { } s ? $"{s.Direction} · {s.EndpointId} · {s.FormatId} · {s.ChannelCount} каналов · период {s.Period} · MMCSS {s.Mmcss} · {s.DurationSeconds} с · сценарий {s.Scenario} · канал Windows {s.RenderChannel?.ToString() ?? "не применяется"}. Только синтетическая операция; физическое соответствие UNKNOWN." : "Нет настроек для подтверждения.";
    public string WorkerSessionSettingsText => _workerSessionSettings is { } s ? $"{s.Direction} · {s.EndpointId} · {s.FormatId} · {s.ChannelCount} каналов · топология {s.TopologyId} · период {s.Period} · MMCSS {s.Mmcss} · {s.DurationSeconds} с · канал Windows {s.RenderChannel?.ToString() ?? "не применяется"} · {s.Scenario}" : "Неизвестно: сессия ещё не принята worker.";
    public ObservableCollection<WorkerTimelineEntryDto> WorkerTimeline { get; } = [];
    private WorkerTimelineEntryDto? _selectedWorkerTimelineEntry;
    public WorkerTimelineEntryDto? SelectedWorkerTimelineEntry { get => _selectedWorkerTimelineEntry; set { if (Set(ref _selectedWorkerTimelineEntry, value)) { Changed(nameof(WorkerSelectedEventDetails)); RefreshContext(); } } }
    public string WorkerSelectedEventDetails => (SelectedWorkerTimelineEntry ?? WorkerTimeline.LastOrDefault())?.DiagnosticDetails ?? Localization.Get("VmWorkerEventsMissing");
    public string WorkerEmptyTimelineText => WorkerTimeline.Count == 0 ? Localization.Get("VmWorkerTimelineEmpty") : string.Empty;
    public IReadOnlyList<ChoiceOption> WorkerPeriodOptions { get; } = [new("DEFAULT", Localization.Get("VmWorkerPeriodDefault")), new("MINIMUM", Localization.Get("VmWorkerPeriodMinimum"))];
    public IReadOnlyList<ChoiceOption> WorkerMmcssOptions { get; } = [new("OFF", Localization.Get("VmWorkerMmcssOff")), new("PRO_AUDIO", Localization.Get("VmWorkerMmcssProAudio"))];
    public IReadOnlyList<ChoiceOption> WorkerScenarioOptions { get; } = [new("NORMAL", Localization.Get("VmWorkerScenarioNormal")), new("ALL_SILENT", Localization.Get("VmWorkerScenarioSilent")), new("NO_WINDOW", Localization.Get("VmWorkerScenarioNoWindow")), new("WARNING", Localization.Get("VmWorkerScenarioWarning")), new("FAILURE", Localization.Get("VmWorkerScenarioFailure")), new("DEVICE_LOSS", Localization.Get("VmWorkerScenarioDeviceLoss"))];
    public IReadOnlyList<SelectionOption> DurationOptions { get; } = [new(1, Localization.Get("VmDurationOneSecond")), new(3, Localization.Get("VmDurationThreeSeconds")), new(5, Localization.Get("VmDurationFiveSeconds")), new(10, Localization.Get("VmDurationTenSeconds"))];
    public ObservableCollection<ProbeEventDto> Events { get; }
    public ProbeEventDto? LastEvent => Events.LastOrDefault();
    public string EmptyEventMessage => Events.Count == 0 ? Localization.Get("VmDemoEventsEmpty") : string.Empty;
    public string ConfirmationText => _pending is null ? Localization.Get("VmChooseDemoDeviceFirst") : _pending.Mode == SessionMode.Render
        ? string.Format(Localization.CurrentCulture, Localization.Get("VmDemoRenderConfirmation"), _pending.Endpoint.Name, _pending.RenderChannel, _pending.DurationSeconds)
        : string.Format(Localization.CurrentCulture, Localization.Get("VmDemoCaptureConfirmation"), _pending.Endpoint.Name, _pending.DurationSeconds);
    public string InspectorTitle { get; private set; } = Localization.Get("VmInspectorDetails");
    public string InspectorProvenanceText { get; private set; } = Localization.Get("VmInspectorDemoProvenance");
    public IReadOnlyList<string> InspectorDetails { get; private set; } = [];
    public IReadOnlyList<InspectorSectionDto> InspectorSections { get; private set; } = [];
    public ParameterCommand NavigateCommand { get; }
    public ParameterCommand OperatorStepCommand { get; }
    public RelayCommand OpenDeveloperToolsCommand { get; }
    public RelayCommand OpenManualRealToolsCommand { get; }
    public RelayCommand CloseDeveloperToolsCommand { get; }
    public ParameterCommand DirectionCommand { get; }
    public RelayCommand BeginCommand { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelConfirmationCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand NextPulseCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand FailCommand { get; }
    public RelayCommand WarningCommand { get; }
    public RelayCommand EmptyDevicesCommand { get; }
    public RelayCommand RestoreDevicesCommand { get; }
    public AsyncRelayCommand ConnectWorkerCommand { get; }
    public AsyncRelayCommand QueryWorkerEndpointsCommand { get; }
    public AsyncRelayCommand DiscoverWindowsEndpointsCommand { get; }
    public AsyncRelayCommand QueryRealFormatsCommand { get; }
    public RelayCommand RequestRealInitializeCommand { get; }
    public AsyncRelayCommand ConfirmRealInitializeCommand { get; }
    public RelayCommand CancelRealInitializeCommand { get; }
    public RelayCommand RequestDeviceScanCommand { get; }
    public AsyncRelayCommand ConfirmDeviceScanCommand { get; }
    public RelayCommand CancelDeviceScanConsentCommand { get; }
    public RelayCommand CancelDeviceScanCommand { get; }
    public AsyncRelayCommand PrepareAutomaticScanCommand { get; }
    public AsyncRelayCommand ConfirmAutomaticScanCommand { get; }
    public RelayCommand CancelAutomaticScanConsentCommand { get; }
    public RelayCommand CancelAutomaticScanCommand { get; }
    public RelayCommand OpenAutomaticScanLocationCommand { get; }
    public AsyncRelayCommand ExportAutomaticScanCommand { get; }
    public RelayCommand RequestStreamValidationCommand { get; }
    public AsyncRelayCommand ConfirmStreamValidationCommand { get; }
    public RelayCommand CancelStreamConsentCommand { get; }
    public AsyncRelayCommand CancelStreamValidationCommand { get; }
    public RelayCommand RequestDuplexMinimumCommand { get; }
    public RelayCommand RequestDuplexDefaultCommand { get; }
    public AsyncRelayCommand ConfirmDuplexValidationCommand { get; }
    public RelayCommand CancelDuplexConsentCommand { get; }
    public AsyncRelayCommand CancelDuplexValidationCommand { get; }
    public AsyncRelayCommand PrepareDuplexRepeatabilityCommand { get; }
    public AsyncRelayCommand ConfirmDuplexRepeatabilityCommand { get; }
    public RelayCommand CancelDuplexRepeatabilityConsentCommand { get; }
    public RelayCommand CancelDuplexRepeatabilityCommand { get; }
    public AsyncRelayCommand PreparePeriodDiscoveryCommand { get; }
    public AsyncRelayCommand ConfirmPeriodDiscoveryCommand { get; }
    public RelayCommand CancelPeriodDiscoveryCommand { get; }
    public RelayCommand CancelPeriodDiscoveryRunCommand { get; }
    public AsyncRelayCommand PrepareCustomPeriodDuplexCommand { get; }
    public AsyncRelayCommand ConfirmCustomPeriodDuplexCommand { get; }
    public RelayCommand CancelCustomPeriodDuplexConsentCommand { get; }
    public RelayCommand CancelCustomPeriodDuplexCommand { get; }
    public AsyncRelayCommand PrepareCustomRepeatabilityCommand { get; }
    public AsyncRelayCommand ConfirmCustomRepeatabilityCommand { get; }
    public RelayCommand CancelCustomRepeatabilityConsentCommand { get; }
    public RelayCommand CancelCustomRepeatabilityCommand { get; }
    public AsyncRelayCommand RefreshEvidenceLibraryCommand { get; }
    public AsyncRelayCommand RefreshSavedProfilesCommand { get; }
    public RelayCommand RefreshHistoricalSustainedScansCommand { get; }
    public AsyncRelayCommand PreviewHistoricalSustainedRecoveryCommand { get; }
    public AsyncRelayCommand SaveHistoricalSustainedRecoveryCommand { get; }
    public RelayCommand CancelHistoricalSustainedRecoveryCommand { get; }
    public AsyncRelayCommand CheckSavedProfileBindingCommand { get; }
    public RelayCommand OpenEvidenceLibraryLocationCommand { get; }
    public AsyncRelayCommand ShutdownWorkerCommand { get; }
    public RelayCommand RequestWorkerCaptureCommand { get; }
    public RelayCommand RequestWorkerRenderCommand { get; }
    public AsyncRelayCommand ConfirmWorkerSessionCommand { get; }
    public RelayCommand CancelWorkerConsentCommand { get; }
    public AsyncRelayCommand StopWorkerSessionCommand { get; }
    public RelayCommand DeviceLossCommand { get; }

    public void Navigate(ProbeScreen screen)
    {
        if (!_developerToolsAvailable || AutomaticScanBusy || AutomaticScanRunning) return;
        DeveloperToolsOpen = true;
        if (screen == ProbeScreen.Devices) WorkflowStep = OperatorStep.Device;
        if (Set(ref _screen, screen, nameof(Screen))) { Changed(nameof(ConfirmationText)); RefreshContext(); RefreshCanExecute(); }
    }

    private void OpenManualRealTools()
    {
        if (!_developerToolsAvailable || AutomaticScanBusy || AutomaticScanRunning) return;
        DeveloperToolsOpen = true;
        Screen = ProbeScreen.Devices;
        WorkflowStep = OperatorStep.Test;
        RefreshContext();
    }

    private void OpenDeveloperTools()
    {
        if (!_developerToolsAvailable || AutomaticScanBusy || AutomaticScanRunning) return;
        DeveloperToolsOpen = true;
    }

    private void CloseDeveloperTools()
    {
        Screen = ProbeScreen.Devices;
        WorkflowStep = _automaticScanAttempted || HasDuplexResults || RealFormatMatrix is not null || RealInitializationResult is not null ? OperatorStep.Result : OperatorStep.Device;
        DeveloperToolsOpen = false;
        RefreshContext();
    }

    public void NavigateOperatorStep(OperatorStep step)
    {
        if (AutomaticScanRunning && !_automaticScanTerminal && step != OperatorStep.Test) return;
        if (step == OperatorStep.Test && !CanUseSelectedWindowsEndpoint && !HasCurrentDuplexPair) { Notice = Localization.Get("VmNavigateNeedEndpoint"); WorkflowStep = OperatorStep.Device; return; }
        if (step == OperatorStep.Result && !_automaticScanAttempted && RealFormatMatrix is null && RealInitializationResult is null && _realFormatError is null && _realInitializationError is null && !HasDuplexResults) { Notice = Localization.Get("VmNavigateNeedRealTest"); return; }
        if (Screen != ProbeScreen.Devices) Screen = ProbeScreen.Devices;
        WorkflowStep = step;
        RefreshContext();
    }

    private void SyncSelectedRealFormatCandidate()
    {
        var row = SelectedRealFormatRow;
        var candidate = row is null ? null : RealInitializePeriod == "default" ? row.DefaultCandidate : row.MinimumCandidate;
        if (!ReferenceEquals(_selectedRealFormatCandidate, candidate)) SelectedRealFormatCandidate = candidate;
    }

    public bool IsDuplexOperatorResult => _operatorResultMode == "Одновременная проверка Duplex";
    public bool IsSingleEndpointOperatorResult => !IsDuplexOperatorResult;
    public bool HasSingleEndpointScanResult => IsSingleEndpointOperatorResult && HasDeviceScanResult;
    public bool HasSingleEndpointQueryResult => IsSingleEndpointOperatorResult && HasOperatorQueryResult;
    public bool HasSingleEndpointInitializeResult => IsSingleEndpointOperatorResult && HasOperatorInitializeResult;

    private void NotifyOperatorResult()
    {
        Changed(nameof(OperatorSelectedDevice)); Changed(nameof(OperatorResultMode));
        Changed(nameof(IsDuplexOperatorResult)); Changed(nameof(IsSingleEndpointOperatorResult)); Changed(nameof(HasSingleEndpointScanResult)); Changed(nameof(HasSingleEndpointQueryResult)); Changed(nameof(HasSingleEndpointInitializeResult));
        Changed(nameof(OperatorResultConfiguration)); Changed(nameof(OperatorResultProvenance));
        Changed(nameof(OperatorResultDetails));
    }

    private static int FormatStatusOrder(RealFormatUniqueRowDto row) => row.DisplayStatus switch
    {
        "SUPPORTED_REPORTED" when !row.IsConflict => 0,
        "UNSUPPORTED" when !row.IsConflict => 1,
        "NOT_TESTED" => 2,
        "INCOMPLETE/CONFLICT" or "INCOMPLETE" => 3,
        "FAILED" => 4,
        _ => 5
    };

    public void SelectRenderChannel(int channel)
    {
        if (channel < 1 || channel > (SelectedRenderEndpoint?.Topology?.ChannelCount ?? SelectedRenderEndpoint?.Channels ?? 2)) throw new ArgumentOutOfRangeException(nameof(channel));
        RenderChannelBank = (channel - 1) / 8;
        RenderChannel = channel;
    }
    public void SelectDuration(int seconds)
    {
        if (!DurationOptions.Any(o => o.Value == seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        DurationSeconds = seconds;
    }
    public void SelectCaptureEndpoint(EndpointDto? endpoint) => SelectedCaptureEndpoint = endpoint;
    public void SelectRenderEndpoint(EndpointDto? endpoint) => SelectedRenderEndpoint = endpoint;

    public void RequestConfirmation()
    {
        if (!CanBegin()) { Notice = Localization.Get("VmDemoDeviceUnavailable"); return; }
        var mode = Screen == ProbeScreen.Capture ? SessionMode.Capture : SessionMode.Render;
        var endpoint = mode == SessionMode.Capture ? SelectedCaptureEndpoint! : SelectedRenderEndpoint!;
        _pendingSessionId = Guid.NewGuid();
        _pending = new(_pendingSessionId.Value, mode, endpoint, mode == SessionMode.Render ? RenderChannel : null, DurationSeconds, endpoint.Topology?.ChannelCount ?? endpoint.Channels);
        Record(_pendingSessionId.Value, ProbeEventKind.SessionRequested, $"Запрошена демонстрационная сессия {ModeLabel(mode)} для {endpoint.Name}.");
        ConfirmPending = true;
        ConsentChecked = false;
        Changed(nameof(ConfirmationText));
    }

    private void ConfirmStart()
    {
        if (!ConfirmPending || !ConsentChecked || _pending is null || _pendingSessionId is null) return;
        var request = _pending;
        ConfirmPending = false;
        ConsentChecked = false;
        Record(request.Id, ProbeEventKind.ConsentAccepted, "Оператор подтвердил демонстрационную симуляцию.");
        if (request.Mode == SessionMode.Capture) Meter = null;
        Session = new(request.Id, request.Mode, request.Endpoint.Name, request.RenderChannel, request.DurationSeconds, SessionLifecycle.Preparing, StreamQuality.Unknown, "ДЕМО · Данные симуляции", null, ChannelCount: request.ChannelCount);
        Record(request.Id, ProbeEventKind.Preparing, "Синтетическая подготовка.");
        Session = Session with { Lifecycle = SessionLifecycle.Started, Message = "Синтетическая сессия выполняется." };
        Record(request.Id, ProbeEventKind.Started, "Синтетическая сессия запущена.");
        Notice = Localization.Get("VmDemoStarted");
        _pending = null;
        _pendingSessionId = null;
        if (Session.Mode == SessionMode.Capture) NextPulse();
    }

    private void CancelConfirmation()
    {
        if (_pendingSessionId is Guid id) Record(id, ProbeEventKind.ConsentCancelled, "Оператор отменил подтверждение; сессия не запускалась.");
        ConfirmPending = false;
        ConsentChecked = false;
        _pending = null;
        _pendingSessionId = null;
        Changed(nameof(ConfirmationText));
        Notice = Localization.Get("VmDemoStartCancelled");
    }

    private void BeginStop()
    {
        if (Session?.Lifecycle != SessionLifecycle.Started) return;
        Session = Session with { Lifecycle = SessionLifecycle.Stopping, Message = "Останавливается (симуляция)…" };
        Record(Session.Id, ProbeEventKind.Stopping, "Запрошена остановка demo-сессии.");
    }
    public void CompleteStop()
    {
        if (Session?.Lifecycle != SessionLifecycle.Stopping) return;
        Session = Session with { Lifecycle = SessionLifecycle.Stopped, Message = "Остановлено (симуляция).", CleanupStatus = null };
        Record(Session.Id, ProbeEventKind.Stopped, "Demo-сессия остановлена.");
    }
    public void CompleteSimulation()
    {
        if (Session?.Lifecycle != SessionLifecycle.Started) return;
        Session = Session with { Lifecycle = SessionLifecycle.Completed, Quality = StreamQuality.Unknown, Message = "Симуляция завершена по заданной длительности; качество не оценивалось." };
        Record(Session.Id, ProbeEventKind.Completed, "Синтетическая длительность завершена; качество неизвестно.");
    }
    public void CompleteSimulation(Guid expectedSessionId)
    {
        if (Session?.Id == expectedSessionId) CompleteSimulation();
    }

    private void NextPulse()
    {
        if (Session is not { Mode: SessionMode.Capture, Lifecycle: SessionLifecycle.Started }) return;
        var sequence = (Meter?.Sequence ?? 0) + 1;
        var silent = sequence % 2 == 0;
        var peak = -18 - sequence % 3 * 4;
        var count = Session.ChannelCount;
        var previousHolds = Meter?.Channels.ToDictionary(c => c.Channel, c => c.PeakHoldDbfs) ?? [];
        Meter = new(sequence, DateTimeOffset.UtcNow, Enumerable.Range(1, count).Select(channel =>
        {
            double? windowPeak = silent ? null : peak - channel * 0.25;
            var priorHold = previousHolds.GetValueOrDefault(channel);
            var hold = windowPeak is double current ? priorHold is double prior ? Math.Max(prior, current) : current : priorHold;
            return new MeterChannelDto(channel, windowPeak, windowPeak is double finitePeak ? finitePeak - 7 : null, hold, 0, silent, silent ? "Цифровая тишина" : "Завершённое окно");
        }).ToArray(), true);
        Record(Session.Id, ProbeEventKind.MeterSnapshot, $"Снимок проверочных данных №{sequence}.");
    }
    private void DisconnectWorker()
    {
        if (Session?.Lifecycle != SessionLifecycle.Started) return;
        var id = Session.Id;
        Session = Session with { Lifecycle = SessionLifecycle.Unknown, Quality = StreamQuality.Unknown, Message = "Worker отключён; остановка и очистка неизвестны.", CleanupStatus = null };
        Record(id, ProbeEventKind.WorkerDisconnected, "Соединение потеряно; состояние Stop и cleanup неизвестно.");
    }
    private void FailSession()
    {
        if (Session?.Lifecycle != SessionLifecycle.Started) return;
        var id = Session.Id;
        Session = Session with { Lifecycle = SessionLifecycle.Failed, Quality = StreamQuality.Failed, Message = "Синтетическая ошибка подготовки/сессии.", CleanupStatus = null };
        Record(id, ProbeEventKind.Failure, "Синтетическая ошибка сценария.");
    }
    private void CompleteWithWarning()
    {
        if (Session is not { Mode: SessionMode.Capture, Lifecycle: SessionLifecycle.Started }) return;
        Session = Session with { Quality = StreamQuality.Warning, Message = "Синтетическое предупреждение времени Capture." };
        Record(Session.Id, ProbeEventKind.Warning, "Предупреждение о целостности временных меток Capture.");
    }
    private void SimulateDeviceLoss()
    {
        if (Session?.Lifecycle != SessionLifecycle.Started) return;
        var id = Session.Id;
        Session = Session with { Lifecycle = SessionLifecycle.Failed, Quality = StreamQuality.Unknown, Message = "Синтетическая потеря устройства; качество неизвестно.", CleanupStatus = null };
        Record(id, ProbeEventKind.DeviceLoss, "Синтетическая потеря устройства; cleanup неизвестен.", "AUDCLNT_E_DEVICE_INVALIDATED");
    }

    private bool CanBegin()
    {
        if (ConfirmPending || Session?.Lifecycle is SessionLifecycle.Preparing or SessionLifecycle.Started or SessionLifecycle.Stopping or SessionLifecycle.Unknown) return false;
        return Screen switch
        {
            ProbeScreen.Capture => SelectedCaptureEndpoint is { IsAvailable: true, Direction: "Capture" },
            ProbeScreen.Render => SelectedRenderEndpoint is { IsAvailable: true, Direction: "Render" } && RenderChannel >= 1 && RenderChannel <= (SelectedRenderEndpoint.Topology?.ChannelCount ?? SelectedRenderEndpoint.Channels) && DurationOptions.Any(o => o.Value == DurationSeconds),
            _ => false
        };
    }
    private void RestoreDevices()
    {
        CaptureEndpoints.Clear(); foreach (var endpoint in _client.ListEndpoints(SessionMode.Capture)) CaptureEndpoints.Add(endpoint);
        RenderEndpoints.Clear(); foreach (var endpoint in _client.ListEndpoints(SessionMode.Render)) RenderEndpoints.Add(endpoint);
        EndpointListChanged();
    }
    private void EndpointListChanged() { Changed(nameof(HasEndpoints)); Changed(nameof(IsEndpointListEmpty)); Changed(nameof(EmptyEndpointMessage)); }
    private void SelectionChanged()
    {
        _captureBank = 0;
        Changed(nameof(CaptureChannelBank));
        Changed(nameof(CaptureChannelBanks));
        if (SelectedRenderEndpoint is { } render)
        {
            var count = render.Topology?.ChannelCount ?? render.Channels;
            if (_renderChannel > count) _renderChannel = 1;
            _renderBank = (_renderChannel - 1) / 8;
            Changed(nameof(RenderChannel));
            Changed(nameof(RenderChannelBank));
            Changed(nameof(RenderChannelBanks));
            Changed(nameof(RenderChannelOptions));
        }
        Changed(nameof(SelectedEndpoint));
        Changed(nameof(HasEndpoints));
        Changed(nameof(IsEndpointListEmpty));
        Changed(nameof(EmptyEndpointMessage));
        RefreshContext();
        RefreshCanExecute();
    }
    private void RefreshCanExecute()
    {
        if (BeginCommand is not null) BeginCommand.Refresh();
        if (StopCommand is not null) StopCommand.Refresh();
        if (NextPulseCommand is not null) NextPulseCommand.Refresh();
        if (DisconnectCommand is not null) DisconnectCommand.Refresh();
        if (FailCommand is not null) FailCommand.Refresh();
        if (WarningCommand is not null) WarningCommand.Refresh();
        if (DeviceLossCommand is not null) DeviceLossCommand.Refresh();
        if (RequestStreamValidationCommand is not null) RequestStreamValidationCommand.Refresh();
        if (PrepareAutomaticScanCommand is not null) PrepareAutomaticScanCommand.Refresh();
        if (PrepareDuplexRepeatabilityCommand is not null) PrepareDuplexRepeatabilityCommand.Refresh();
        Changed(nameof(DuplexRepeatabilityReadiness));
        Changed(nameof(CanPrepareDuplexRepeatability));
        Changed(nameof(DuplexRepeatabilityAvailabilityReason));
        Changed(nameof(CanPreparePeriodDiscovery));
        Changed(nameof(PeriodDiscoveryAvailabilityReason));
        Changed(nameof(PeriodDiscoveryPlanText));
        Changed(nameof(CanPrepareCustomPeriodDuplex));
        Changed(nameof(CustomPeriodDuplexAvailabilityReason));
        Changed(nameof(CanPrepareCustomRepeatability));
        Changed(nameof(CustomRepeatabilityPlanText));
        PreparePeriodDiscoveryCommand?.Refresh();
        ConfirmPeriodDiscoveryCommand?.Refresh();
        CancelPeriodDiscoveryCommand?.Refresh();
        CancelPeriodDiscoveryRunCommand?.Refresh();
        PrepareCustomPeriodDuplexCommand?.Refresh();
        ConfirmCustomPeriodDuplexCommand?.Refresh();
        CancelCustomPeriodDuplexConsentCommand?.Refresh();
        CancelCustomPeriodDuplexCommand?.Refresh();
        PrepareCustomRepeatabilityCommand?.Refresh();
        ConfirmCustomRepeatabilityCommand?.Refresh();
        CancelCustomRepeatabilityConsentCommand?.Refresh();
        CancelCustomRepeatabilityCommand?.Refresh();
        RefreshEvidenceLibraryCommand?.Refresh();
        OpenEvidenceLibraryLocationCommand?.Refresh();
    }

    public void RefreshEvidenceLibrary()
    {
        try { _evidenceLibrary = EvidenceLibraryCatalog.Refresh(); }
        catch (Exception ex) { _evidenceLibrary = new([], DateTimeOffset.UtcNow); _evidenceLibraryDeviceFilter = ""; _evidenceLibraryTypeFilter = ""; _evidenceLibraryProvenanceFilter = $"CATALOG_ERROR: {ex.GetType().Name}"; }
        ApplyEvidenceLibraryFilter();
    }

    private async Task RefreshEvidenceLibraryAsync()
    {
        try
        {
            var snapshot = await Task.Run(() => EvidenceLibraryCatalog.Refresh());
            _evidenceLibrary = snapshot;
            ApplyEvidenceLibraryFilter();
        }
        catch (Exception ex)
        {
            _evidenceLibrary = new([], DateTimeOffset.UtcNow);
            _evidenceLibraryProvenanceFilter = $"CATALOG_ERROR: {ex.GetType().Name}";
            ApplyEvidenceLibraryFilter();
        }
    }

    private void ApplyEvidenceLibraryFilter()
    {
        var device = _evidenceLibraryDeviceFilter.Trim();
        var type = _evidenceLibraryTypeFilter.Trim();
        var provenance = _evidenceLibraryProvenanceFilter.Trim();
        var rows = _evidenceLibrary.Entries.Where(e =>
            (device.Length == 0 || e.IdentityText.Contains(device, StringComparison.OrdinalIgnoreCase)) &&
            (type.Length == 0 || e.ReportType.Contains(type, StringComparison.OrdinalIgnoreCase) || e.RecordType.ToString().Contains(type, StringComparison.OrdinalIgnoreCase)) &&
            (provenance.Length == 0 || e.ProvenanceLabel.Contains(provenance, StringComparison.OrdinalIgnoreCase))).ToArray();
        EvidenceLibraryEntries.Clear();
        foreach (var row in rows) EvidenceLibraryEntries.Add(row);
        EvidenceLibraryComparisons.Clear();
        var observations = NormalizedDuplexEvidence.FromEntries(rows);
        var mode = Enum.TryParse<DuplexComparisonMode>(_evidenceLibraryComparisonMode, out var parsed) ? parsed : DuplexComparisonMode.ExactConfigurationRepeatability;
        foreach (var comparison in NormalizedDuplexEvidence.Compare(observations, mode)) EvidenceLibraryComparisons.Add(comparison);
        Changed(nameof(EvidenceLibraryStatus)); Changed(nameof(EvidenceLibraryDetails)); Changed(nameof(EvidenceLibraryComparisonText));
        OpenEvidenceLibraryLocationCommand?.Refresh();
    }

    private void OpenEvidenceLibraryLocation()
    {
        if (_selectedEvidenceLibraryEntry is not { Path: { Length: > 0 } path } || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }
    private void Record(Guid sessionId, ProbeEventKind kind, string message, string? hresult = null)
    {
        Events.Add(new(++_eventSequence, DateTimeOffset.UtcNow, sessionId, kind, message, hresult));
        while (Events.Count > EventHistoryLimit) Events.RemoveAt(0);
        Changed(nameof(LastEvent));
        Changed(nameof(EmptyEventMessage));
        RefreshContext();
    }

    private async Task ConnectWorkerAsync()
    {
        if (_worker is null) return;
        _workerAttempted = true;
        Changed(nameof(WorkerState));
        ConnectWorkerCommand.Refresh();
        try { await _worker.ConnectAsync(); }
        catch { ApplyWorkerState(_worker.State); }
        QueryWorkerEndpointsCommand.Refresh();
    }

    private async Task QueryWorkerEndpointsAsync()
    {
        if (_worker is null) return;
        try
        {
            WorkerEndpoints = await _worker.ListEndpointsAsync();
            LastWorkerEndpointSnapshot = WorkerEndpoints.ToArray();
            WorkerNotice = $"Получено проверочных данных worker fake: {WorkerEndpoints.Count}. Локальные демо fixtures остаются отдельными.";
        }
        catch (WorkerCommandException ex) { WorkerNotice = $"Команда отвергнута: {ex.Command}/{ex.ErrorCode}: {ex.Message}"; }
        catch (Exception ex) { WorkerNotice = $"Запрос worker не выполнен: {ex.GetType().Name}: {ex.Message}"; }
        Changed(nameof(WorkerEndpoints));
        Changed(nameof(SelectedWorkerEndpoint)); Changed(nameof(WorkerFormatOptions)); Changed(nameof(WorkerChannelSummary)); Changed(nameof(HasHistoricalWorkerEndpoints)); Changed(nameof(HistoricalWorkerEndpointNames)); RefreshWorkerCommands(); RefreshContext();
    }

    private bool CanDiscoverWindowsEndpoints() => _worker is not null && !_windowsDiscoveryLoading && !AutomaticScanBusy && !AutomaticScanConsentPending && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending &&
        (WorkerState.State == WorkerConnectionState.ConnectedFake && _worker.Capabilities.Contains("ListWindowsEndpoints", StringComparer.Ordinal) ||
         WorkerState.State == WorkerConnectionState.Disconnected && !_workerAttempted);

    private bool CanPrepareAutomaticScan() => _automaticScanWorkflow is not null && !AutomaticScanBusy && !AutomaticScanConsentPending && !_automaticScanOutcomeUnknown && !_windowsDiscoveryLoading && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending && !WorkerOperationActive &&
        WorkerState.State == WorkerConnectionState.ConnectedFake && _worker?.Capabilities.Contains("ScanSessionAuthorizationV1", StringComparer.Ordinal) == true &&
        (AutomaticScanPairEnabled
            ? SelectedDuplexCaptureEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } && SelectedDuplexRenderEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 }
            : SelectedWindowsEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 });

    private async Task PrepareAutomaticScanAsync()
    {
        if (_automaticScanWorkflow is null || !CanPrepareAutomaticScan()) return;
        var selection = new AutomaticScanSelection(SelectedWindowsEndpoint?.EndpointId, AutomaticScanPairEnabled,
            AutomaticScanPairEnabled ? SelectedDuplexCaptureEndpoint?.EndpointId : null,
            AutomaticScanPairEnabled ? SelectedDuplexRenderEndpoint?.EndpointId : null,
            SelectedPhysicalDeviceGroup?.GroupId, SelectedPhysicalDeviceGroup?.CanAutoSelectPair == true, AllowUnconfirmedPhysicalPair, AutomaticSustainedMode.StartsWith("Extended", StringComparison.Ordinal));
        AutomaticScanPreparing = true;
        _automaticScanPreparation = null;
        AutomaticScanConsentPending = false;
        AutomaticScanConsentChecked = false;
        SetAutomaticScanStatus("Обновление Windows endpoints перед построением плана…");
        using var refresh = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var discovery = await _automaticScanWorkflow.RefreshEndpointsAsync(refresh.Token);
            ApplyAutomaticDiscovery(discovery, selection);
            var preparation = _automaticScanWorkflow.Prepare(discovery, selection);
            _automaticScanPreparation = preparation;
            _automaticScanProgressRows.Clear();
            Changed(nameof(AutomaticScanProgressRows));
            AutomaticScanConsentChecked = false;
            AutomaticScanConsentPending = true;
            SetAutomaticScanStatus("План подготовлен. Операции начнутся только после вашего подтверждения.");
            Changed(nameof(AutomaticScanConsentText));
        }
        catch (Exception ex)
        {
            InvalidateAutomaticDeviceSelection();
            SetAutomaticScanStatus($"План не подготовлен: {ex.GetType().Name}: {ex.Message}");
        }
        finally { AutomaticScanPreparing = false; }
    }

    private void ApplyAutomaticDiscovery(WindowsEndpointDiscoveryDto discovery, AutomaticScanSelection selection)
    {
        WindowsEndpointDiscoveryValidation.Validate(discovery, _worker?.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal) == true);
        InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason.EndpointRediscovered);
        _windowsDiscoveryGeneration = Guid.NewGuid().ToString("D");
        var previousId = selection.EndpointId;
        var captureId = selection.CaptureEndpointId;
        var renderId = selection.RenderEndpointId;
        _windowsEndpointSnapshot = discovery;
        _lastWindowsEndpointSnapshot = discovery;
        _windowsDiscoveryCurrent = true;
        _windowsDiscoveryError = null;
        var refreshedGroups = PhysicalDeviceGrouping.Group(discovery.Endpoints);
        _physicalDeviceGroups = refreshedGroups;
        CancelPeriodDiscovery(Localization.Get("VmPeriodPlanDiscardedDiscoveryRefreshed"));
        Changed(nameof(PhysicalDeviceGroups)); Changed(nameof(PhysicalDeviceGroupDetails));
        var refreshedGroup = refreshedGroups.FirstOrDefault(g => g.GroupId == selection.PhysicalGroupId);
        if (selection.PhysicalGroupId is not null && refreshedGroup is null)
        {
            InvalidateAutomaticDeviceSelection();
            throw new InvalidOperationException("Физическая группа устройства изменилась после обнаружения; выберите устройство заново.");
        }
        if (selection.RequireUnambiguousPair && refreshedGroup?.CanAutoSelectPair != true)
        {
            InvalidateAutomaticDeviceSelection();
            throw new InvalidOperationException("Физическая пара больше не является однозначной; выберите Capture и Render явно.");
        }
        Set(ref _selectedWindowsEndpoint, discovery.Endpoints.FirstOrDefault(e => e.EndpointId == previousId && e.DeviceState == "ACTIVE"), nameof(SelectedWindowsEndpoint));
        Set(ref _selectedDuplexCapture, discovery.Endpoints.FirstOrDefault(e => e.EndpointId == captureId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE"), nameof(SelectedDuplexCaptureEndpoint));
        Set(ref _selectedDuplexRender, discovery.Endpoints.FirstOrDefault(e => e.EndpointId == renderId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE"), nameof(SelectedDuplexRenderEndpoint));
        var group = refreshedGroup ?? (captureId is not null && renderId is not null
            ? refreshedGroups.FirstOrDefault(g => g.CaptureEndpointId == captureId && g.RenderEndpointId == renderId)
            : previousId is not null
                ? refreshedGroups.FirstOrDefault(g => g.CaptureEndpointId == previousId || g.RenderEndpointId == previousId)
                : null);
        Set(ref _selectedPhysicalDeviceGroup, group, nameof(SelectedPhysicalDeviceGroup));
        _automaticScanPairEnabled = selection.ExplicitPair;
        _allowUnconfirmedPhysicalPair = selection.AllowUnconfirmedPair;
        Changed(nameof(HasSelectedWindowsEndpoint)); Changed(nameof(WindowsEndpoints)); Changed(nameof(PhysicalGroupRequiresExplicitPair)); Changed(nameof(ShowNormalPairSelection)); Changed(nameof(AllowUnconfirmedPhysicalPair)); Changed(nameof(WindowsEndpointRows));
        Changed(nameof(DuplexCaptureEndpoints)); Changed(nameof(DuplexRenderEndpoints)); Changed(nameof(WindowsDiscoveryStatusText));
        Changed(nameof(CanUseSelectedWindowsEndpoint)); Changed(nameof(HasCurrentWindowsEndpoints)); Changed(nameof(HasHistoricalWindowsEndpoints));
        Changed(nameof(WindowsEndpointInspectorFields)); Changed(nameof(DeviceScanAvailabilityReason)); Changed(nameof(DuplexDiscoveryFreshness));
        Changed(nameof(AutomaticScanAvailabilityReason)); Changed(nameof(AutomaticScanConsentText));
        if (previousId is not null && SelectedWindowsEndpoint is null || captureId is not null && SelectedDuplexCaptureEndpoint is null || renderId is not null && SelectedDuplexRenderEndpoint is null)
            throw new InvalidOperationException("Выбранный точный endpoint исчез или стал неактивен после обновления. Автоматическая подмена устройства запрещена.");
        RefreshWindowsEndpointBrowser();
        DiscoverWindowsEndpointsCommand.Refresh();
    }

    private void InvalidateAutomaticDeviceSelection()
    {
        Set(ref _selectedPhysicalDeviceGroup, null, nameof(SelectedPhysicalDeviceGroup));
        Set(ref _selectedWindowsEndpoint, null, nameof(SelectedWindowsEndpoint));
        Set(ref _selectedDuplexCapture, null, nameof(SelectedDuplexCaptureEndpoint));
        Set(ref _selectedDuplexRender, null, nameof(SelectedDuplexRenderEndpoint));
        _automaticScanPairEnabled = false;
        Changed(nameof(AutomaticScanPairEnabled)); Changed(nameof(PhysicalGroupRequiresExplicitPair)); Changed(nameof(ShowNormalPairSelection)); Changed(nameof(AllowUnconfirmedPhysicalPair)); Changed(nameof(DuplexCaptureEndpoints)); Changed(nameof(DuplexRenderEndpoints));
        PrepareAutomaticScanCommand.Refresh();
    }

    private async Task ConfirmAutomaticScanAsync()
    {
        if (_automaticScanWorkflow is null || !AutomaticScanConsentPending || !AutomaticScanConsentChecked || _automaticScanPreparation is not { } preparation || _automaticScanRunning) return;
        var preparedCapture = preparation.Plan.Target.Capture?.EndpointId;
        var preparedRender = preparation.Plan.Target.Render?.EndpointId;
        if (WorkerState.State != WorkerConnectionState.ConnectedFake || !_windowsDiscoveryCurrent ||
            preparedCapture != (preparation.Plan.Target.PairIdentity == ScanPairIdentity.OperatorSelectedPair ? SelectedDuplexCaptureEndpoint?.EndpointId : SelectedWindowsEndpoint?.Direction == "CAPTURE" ? SelectedWindowsEndpoint.EndpointId : null) ||
            preparedRender != (preparation.Plan.Target.PairIdentity == ScanPairIdentity.OperatorSelectedPair ? SelectedDuplexRenderEndpoint?.EndpointId : SelectedWindowsEndpoint?.Direction == "RENDER" ? SelectedWindowsEndpoint.EndpointId : null))
        {
            CancelAutomaticScanConsent();
            SetAutomaticScanStatus("Worker, discovery или точный endpoint изменились. Обновите устройства и подготовьте подтверждение заново.");
            return;
        }
        AutomaticScanConsentPending = false;
        AutomaticScanConsentChecked = false;
        AutomaticScanRunning = true;
        InvalidateAutomaticScanExport("Новый scan начал работу; прежняя подготовка экспорта сброшена.");
        DeveloperToolsOpen = false;
        WorkflowStep = OperatorStep.Test;
        _automaticScanAttempted = true;
        _automaticScanTerminal = false;
        Changed(nameof(AutomaticScanAttempted)); Changed(nameof(AutomaticScanResultSummary)); Changed(nameof(AutomaticScanPersistenceText));
        _automaticScanCancellation = new CancellationTokenSource();
        var confirmationId = Guid.NewGuid(); // Created only after the operator accepts the displayed immutable plan.
        SetAutomaticScanStatus("Подтверждение принято; регистрируется ограниченная сессия Worker.");
        _automaticScanTask = RunAutomaticScanAsync(preparation, confirmationId, _automaticScanCancellation.Token);
        try { await _automaticScanTask; }
        finally
        {
            AutomaticScanRunning = false;
            _automaticScanCancellation?.Dispose(); _automaticScanCancellation = null;
            _automaticScanTask = null;
        }
    }

    private async Task RunAutomaticScanAsync(AutomaticScanPreparation preparation, Guid confirmationId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _automaticScanWorkflow!.RunApprovedAsync(preparation, confirmationId, OnAutomaticScanProgress, cancellationToken);
            InvalidateAutomaticScanExport("Получен новый результат scan; требуется отдельная проверка полноты экспорта.");
            _automaticScanResult = result;
            _automaticScanOutcomeUnknown = result.Lifecycle == ScanRunLifecycle.Unknown;
            ApplyAutomaticScanEvents(result.Events);
            _automaticScanTerminal = true;
            NotifyAutomaticScanResultBindings();
            SetAutomaticScanStatus(result.Summary);
            NavigateOperatorStep(OperatorStep.Result);
        }
        catch (Exception ex)
        {
            _automaticScanOutcomeUnknown = true;
            SetAutomaticScanStatus($"UNKNOWN · исход или закрытие Worker-сессии не подтверждены: {ex.GetType().Name}: {ex.Message}");
            _automaticScanTerminal = true;
            Changed(nameof(AutomaticScanAvailabilityReason));
            NotifyAutomaticScanResultBindings();
            NavigateOperatorStep(OperatorStep.Result);
        }
    }

    private void OnAutomaticScanProgress(ScanProgressEvent progress)
    {
        void Apply()
        {
            if (_automaticScanTerminal) return;
            var row = new AutomaticScanProgressRow(progress.OperationId ?? $"local-{progress.Sequence}", ScanStageLabel(progress.Stage), OperationLifecycleLabel(progress.Lifecycle.ToString()), progress.Action);
            var index = _automaticScanProgressRows.ToList().FindIndex(x => x.OperationId == row.OperationId);
            if (index >= 0) _automaticScanProgressRows[index] = row; else _automaticScanProgressRows.Add(row);
            Changed(nameof(AutomaticScanProgressRows));
            SetAutomaticScanStatus($"{row.Stage} · {row.State}: {row.Action}");
        }
        if (_uiContext is null) Apply(); else _uiContext.Post(_ => { try { Apply(); } catch { } }, null);
    }

    private void ApplyAutomaticScanEvents(IReadOnlyList<ScanProgressEvent> events)
    {
        _automaticScanProgressRows.Clear();
        foreach (var progress in events.OrderBy(e => e.Sequence))
        {
            var row = new AutomaticScanProgressRow(progress.OperationId ?? $"local-{progress.Sequence}", ScanStageLabel(progress.Stage), OperationLifecycleLabel(progress.Lifecycle.ToString()), progress.Action);
            var index = _automaticScanProgressRows.ToList().FindIndex(x => x.OperationId == row.OperationId);
            if (index >= 0) _automaticScanProgressRows[index] = row; else _automaticScanProgressRows.Add(row);
        }
        Changed(nameof(AutomaticScanProgressRows));
    }

    private void CancelAutomaticScanConsent()
    {
        if (!AutomaticScanConsentPending) return;
        AutomaticScanConsentPending = false;
        AutomaticScanConsentChecked = false;
        _automaticScanPreparation = null;
        SetAutomaticScanStatus("Подтверждение отменено; операции Worker не запускались.");
        Changed(nameof(AutomaticScanConsentText));
    }

    private void CancelAutomaticScan()
    {
        if (AutomaticScanConsentPending) { CancelAutomaticScanConsent(); return; }
        if (!_automaticScanRunning) return;
        _automaticScanCancellation?.Cancel();
        SetAutomaticScanStatus("Запрошена отмена. Текущий вызов драйвера может быть не прерываемым; итог и очистка ожидаются от Worker.");
    }

    private void OpenAutomaticScanLocation()
    {
        var path = _automaticScanResult?.Persistence?.SummaryPath;
        var directory = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }

    private async Task ExportAutomaticScanAsync()
    {
        if (!CanExportAutomaticScan || _bundleExportInteraction is null || _automaticScanResult?.Persistence?.SummaryPath is not { } summaryPath || _automaticScanResult.SessionId is not { } sessionId) return;
        _automaticScanExportRunning = true;
        Changed(nameof(AutomaticScanExportRunning)); Changed(nameof(CanExportAutomaticScan)); ExportAutomaticScanCommand.Refresh();
        SetAutomaticScanExport("Проверка и экспорт результатов выполняются…", "EXPORTING");
        try
        {
            var directories = _bundleDirectoriesFactory?.Invoke(summaryPath) ?? LaboratoryEvidenceBundleService.DefaultDirectories(summaryPath, _profileDirectory);
            var prepared = await Task.Run(() => LaboratoryEvidenceBundleService.Prepare(sessionId, directories));
            if (_automaticScanResult?.SessionId != sessionId) { SetAutomaticScanExport("Экспорт отменён: результат scan изменился.", "SESSION_CHANGED"); return; }
            _automaticScanExportPreparation = prepared;
            _automaticScanExportTechnicalDetails = prepared.Errors.Count == 0 ? "Полнота и identities источников прошли проверку." : string.Join("\n", prepared.Errors);
            Changed(nameof(AutomaticScanExportTechnicalDetails));
            if (!prepared.IsComplete) { SetAutomaticScanExport("Не все исходные отчёты доступны.", "INCOMPLETE"); return; }
            SetAutomaticScanExport("Результаты готовы к экспорту.", "COMPLETE");
            var suggested = $"klidan-audio-probe-{sessionId}.zip";
            var destination = await _bundleExportInteraction.PickDestinationAsync(suggested);
            if (string.IsNullOrWhiteSpace(destination)) { SetAutomaticScanExport("Экспорт отменён. Исходные данные не изменены.", "CANCELLED"); return; }
            if (_automaticScanResult?.SessionId != sessionId || _automaticScanExportPreparation?.SourceFingerprint != prepared.SourceFingerprint) { SetAutomaticScanExport("Экспорт отменён: результат scan изменился.", "SESSION_CHANGED"); return; }
            if (!await _bundleExportInteraction.ConfirmAsync("Архив содержит технические идентификаторы устройств и компьютера. Не предназначен для публичной публикации. Создать LOCAL_FULL_EVIDENCE архив?")) { SetAutomaticScanExport("Экспорт отменён. Исходные данные не изменены.", "CANCELLED"); return; }
            var path = await Task.Run(() => LaboratoryEvidenceBundleService.Export(prepared, destination));
            var verification = await Task.Run(() => LaboratoryEvidenceBundleService.Verify(path));
            if (!verification.IsValid) throw new InvalidDataException(string.Join("; ", verification.Errors));
            SetAutomaticScanExport($"Архив создан и успешно проверен: {path}", "VERIFIED");
        }
        catch (Exception ex) { SetAutomaticScanExport("Экспорт не выполнен. Исходные данные не изменены.", $"{ex.GetType().Name}: {ex.Message}"); }
        finally { _automaticScanExportRunning = false; Changed(nameof(AutomaticScanExportRunning)); Changed(nameof(CanExportAutomaticScan)); ExportAutomaticScanCommand.Refresh(); }
    }

    private void InvalidateAutomaticScanExport(string status)
    {
        _automaticScanExportPreparation = null;
        SetAutomaticScanExport(status, "INVALIDATED");
    }

    private void SetAutomaticScanExport(string status, string details)
    {
        _automaticScanExportStatus = status; _automaticScanExportTechnicalDetails = details;
        Changed(nameof(AutomaticScanExportStatus)); Changed(nameof(AutomaticScanExportTechnicalDetails)); Changed(nameof(CanExportAutomaticScan));
    }

    private void SetAutomaticScanStatus(string value)
    {
        if (Set(ref _automaticScanStatus, value, nameof(AutomaticScanStatus)))
        {
            Changed(nameof(AutomaticScanResultSummary));
            Changed(nameof(AutomaticScanTechnicalDetails));
        }
    }

    private void NotifyAutomaticScanResultBindings()
    {
        Changed(nameof(HasAutomaticScanResult));
        Changed(nameof(AutomaticScanAttempted));
        Changed(nameof(AutomaticScanResult));
        Changed(nameof(AutomaticScanResultHeadline));
        Changed(nameof(AutomaticScanResultDevice));
        Changed(nameof(AutomaticScanResultSummary));
        Changed(nameof(AutomaticScanRecommendationText));
        Changed(nameof(AutomaticScanPersistenceText));
        Changed(nameof(AutomaticScanStagePlanText));
        Changed(nameof(AutomaticScanCandidateSummary));
        Changed(nameof(AutomaticScanTechnicalDetails));
        Changed(nameof(AutomaticScanResultProvenance));
        Changed(nameof(AutomaticScanExportStatus)); Changed(nameof(AutomaticScanExportTechnicalDetails)); Changed(nameof(CanExportAutomaticScan)); Changed(nameof(AutomaticScanExportRunning));
        OpenAutomaticScanLocationCommand.Refresh();
        ExportAutomaticScanCommand.Refresh();
    }

    private static string ScanCommandLabel(string command) => Localization.Get(command switch
    {
        "QUERY" or "QueryFormatMatrix" => "VmScanCommandQuery",
        "INITIALIZE" or "ValidateFormatMatrix" => "VmScanCommandInitialize",
        "STREAM" or "ValidateStream" => "VmScanCommandStream",
        "DUPLEX" or "ValidateDuplex" => "VmScanCommandDuplex",
        _ => command
    });

    private static string? JsonProvenance(System.Text.Json.JsonElement? report)
    {
        if (report is not { ValueKind: System.Text.Json.JsonValueKind.Object } value || !value.TryGetProperty("provenance", out var provenance) || provenance.ValueKind != System.Text.Json.JsonValueKind.String) return null;
        return provenance.GetString();
    }

    private static string ScanStageLabel(ScanStage stage) => Localization.Get(stage switch
    {
        ScanStage.Discovery => "VmScanStageDiscovery", ScanStage.Query => "VmScanStageQuery",
        ScanStage.Initialize => "VmScanStageInitialize", ScanStage.Stream => "VmScanStageStream",
        ScanStage.Duplex => "VmScanStageDuplex", ScanStage.PeriodDiscovery => "VmScanStagePeriodDiscovery", ScanStage.ShortCustomDuplex => "VmScanStageCustomDuplex", ScanStage.Repeatability => "VmScanStageRepeatability", ScanStage.Evaluate => "VmScanStageEvaluate",
        ScanStage.Save => "VmScanStageSave", _ => "VmScanStageComplete"
    });

    private static string ScanLifecycleLabel(ScanRunLifecycle lifecycle) => Localization.Get(lifecycle switch
    {
        ScanRunLifecycle.Completed => "VmScanLifecycleCompleted", ScanRunLifecycle.Incomplete => "VmScanLifecycleIncomplete",
        ScanRunLifecycle.Cancelled => "VmScanLifecycleCancelled", ScanRunLifecycle.Failed => "VmScanLifecycleFailed", _ => "VmScanLifecycleUnknown"
    });

    private static string OperationLifecycleLabel(string state) => Localization.Get(state switch
    {
        "Pending" => "VmOperationLifecyclePending", "Running" => "VmOperationLifecycleRunning", "Completed" => "VmOperationLifecycleCompleted",
        "Skipped" => "VmOperationLifecycleSkipped", "Failed" => "VmOperationLifecycleFailed", _ => "VmOperationLifecycleUnknown"
    });

    private void RequestDeviceScan()
    {
        if (!CanStartDeviceScan || SelectedWindowsEndpoint is not { } endpoint) return;
        _pendingDeviceScanConsent = new(endpoint.EndpointId!, endpoint.Direction, DeviceConfigurationScan.Fingerprint(endpoint), Guid.NewGuid());
        DeviceScanConsentChecked = false;
        DeviceScanConsentPending = true;
    }

    private void CancelDeviceScanConsent()
    {
        _pendingDeviceScanConsent = null;
        DeviceScanConsentChecked = false;
        DeviceScanConsentPending = false;
        CancelDeviceScanConsentCommand.Refresh();
    }

    private bool CanRequestStreamValidation() => WorkerState.State == WorkerConnectionState.ConnectedFake && _worker is not null && _worker.Capabilities.Contains("RealExclusiveStreamValidation", StringComparer.Ordinal) && SelectedWindowsEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } endpoint && _windowsDiscoveryCurrent && _deviceScanResult is { } scan && scan.EndpointId == endpoint.EndpointId && scan.Direction == endpoint.Direction && !_deviceScanRunning && !_realQueryRunning && !_realInitializeRunning && !_streamValidationRunning && !_streamConsentPending && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending && SelectedStreamCandidate is { InitializeStatus: ("INITIALIZED" or "READY"), InitializeAttempted: true, SupportStatus: "SUPPORTED_REPORTED" } candidate && _deviceScanInitializations.SelectMany(r => r.Candidates).Contains(candidate) && candidate.SampleRate == 48_000 && candidate.Encoding == "PCM16" && candidate.Channels == 2 && (candidate.Period is "default" or "minimum");

    private void RequestStreamValidation()
    {
        if (!CanRequestStreamValidation() || SelectedWindowsEndpoint is not { EndpointId: { } endpointId } endpoint || SelectedStreamCandidate is not { } candidate) return;
        var period = candidate.Period.ToUpperInvariant();
        _pendingStreamConsent = new(true, Guid.NewGuid(), RealStreamConsent.Fingerprint(endpointId, endpoint.Direction, period, 5, "OFF"));
        StreamConsentChecked = false;
        _streamConsentPending = true;
        Changed(nameof(StreamConsentPending)); Changed(nameof(StreamValidationConsentText));
        ConfirmStreamValidationCommand.Refresh(); CancelStreamConsentCommand.Refresh(); RequestStreamValidationCommand.Refresh();
    }

    private void SetDuplexEndpoint(ref WindowsEndpointDto? field, WindowsEndpointDto? value, bool capture)
    {
        if (_automaticScanRunning || _automaticScanPreparing) return;
        if (value is null && !_windowsDiscoveryCurrent && WorkerState.State != WorkerConnectionState.ConnectedFake) return;
        var options = capture ? DuplexCaptureEndpoints : DuplexRenderEndpoints;
        if (value is not null && !options.Contains(value)) return;
        if (ReferenceEquals(field, value)) return;
        if (AutomaticScanConsentPending) CancelAutomaticScanConsent();
        CancelDuplexRepeatabilityAuthorization();
        CancelPeriodDiscovery(Localization.Get("VmPeriodPlanDiscardedPairChanged"));
        field = value;
        if (DuplexConsentPending) CancelDuplexConsent();
        if (_duplexValidationRunning && _activeDuplexOperationId is not null) _ = CancelDuplexValidationAsync();
        _duplexValidations.Clear();
        if (!_automaticScanPairEnabled && SelectedPhysicalDeviceGroup is not null && value is not null)
            SelectedWindowsEndpoint = value;
        _duplexStatus = "Пара изменилась; прежняя оценка и подтверждение сброшены.";
        Changed(capture ? nameof(SelectedDuplexCaptureEndpoint) : nameof(SelectedDuplexRenderEndpoint));
        Changed(nameof(HasCurrentDuplexPair)); Changed(nameof(DuplexConsentText)); Changed(nameof(DuplexAssessments)); Changed(nameof(DuplexResultCards)); Changed(nameof(DuplexRecommendation)); Changed(nameof(DuplexRecommendationSummary)); Changed(nameof(HasDuplexResults)); Changed(nameof(HasDuplexPairProfileEvidence)); Changed(nameof(DuplexStatus)); Changed(nameof(CanSaveDeviceProfile));
        RequestDuplexMinimumCommand.Refresh(); RequestDuplexDefaultCommand.Refresh(); PrepareDuplexRepeatabilityCommand.Refresh(); RefreshCanExecute();
        Changed(nameof(AutomaticScanAvailabilityReason)); PrepareAutomaticScanCommand.Refresh(); RefreshCanExecute();
    }

    private async Task PrepareDuplexRepeatabilityAsync()
    {
        if (!CanPrepareDuplexRepeatability || _duplexRepeatabilityWorkflow is null || SelectedDuplexCaptureEndpoint is not { } capture || SelectedDuplexRenderEndpoint is not { } render) return;
        try
        {
            var discovery = await _duplexRepeatabilityWorkflow.RefreshEndpointsAsync(CancellationToken.None).ConfigureAwait(true);
            var freshCapture = discovery.Endpoints.Single(e => e.EndpointId == capture.EndpointId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE");
            var freshRender = discovery.Endpoints.Single(e => e.EndpointId == render.EndpointId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE");
            _duplexRepeatabilityPreparation = _duplexRepeatabilityWorkflow.Prepare(discovery, freshCapture, freshRender);
            _duplexRepeatabilityResult = null; _duplexRepeatabilityConsentChecked = false; _duplexRepeatabilityConsentPending = true;
            _duplexRepeatabilityStatus = Localization.Get("VmRepeatabilityPlanPrepared");
        }
        catch (Exception ex) { _duplexRepeatabilityPreparation = null; _duplexRepeatabilityStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmRepeatabilityPlanFailed"), ex.GetType().Name, ex.Message); }
        Changed(nameof(DuplexRepeatabilityConsentPending)); Changed(nameof(DuplexRepeatabilityPlanText)); Changed(nameof(DuplexRepeatabilityResult)); Changed(nameof(DuplexRepeatabilityStatus)); ConfirmDuplexRepeatabilityCommand.Refresh(); CancelDuplexRepeatabilityConsentCommand.Refresh(); PrepareDuplexRepeatabilityCommand.Refresh();
    }

    private async Task ConfirmDuplexRepeatabilityAsync()
    {
        if (_duplexRepeatabilityWorkflow is null || !_duplexRepeatabilityConsentPending || !_duplexRepeatabilityConsentChecked || _duplexRepeatabilityPreparation is not { } preparation || _duplexRepeatabilityRunning) return;
        var capture = preparation.Plan.Target.Capture;
        var render = preparation.Plan.Target.Render;
        if (WorkerState.State != WorkerConnectionState.ConnectedFake || WorkerState.ProcessId != preparation.WorkerProcessId || AutomaticScanBusy || AutomaticScanConsentPending || WorkerConsentPending || DuplexConsentPending || RealInitializeConsentPending || DeviceScanConsentPending || _streamConsentPending || !_windowsDiscoveryCurrent || _windowsEndpointSnapshot is null || DuplexRepeatabilityPreparation.Fingerprint(_windowsEndpointSnapshot) != preparation.DiscoveryFingerprint || SelectedDuplexCaptureEndpoint?.EndpointId != capture?.EndpointId || SelectedDuplexRenderEndpoint?.EndpointId != render?.EndpointId || SelectedDuplexCaptureEndpoint?.FriendlyName != capture?.FriendlyName || SelectedDuplexRenderEndpoint?.FriendlyName != render?.FriendlyName || SelectedDuplexCaptureEndpoint?.DeviceState != "ACTIVE" || SelectedDuplexRenderEndpoint?.DeviceState != "ACTIVE")
        {
            CancelDuplexRepeatabilityAuthorization(Localization.Get("VmRepeatabilityAuthorizationInvalidated"));
            return;
        }
        _duplexRepeatabilityConsentPending = false; _duplexRepeatabilityConsentChecked = false; _duplexRepeatabilityRunning = true; _duplexRepeatabilityCancellation = new CancellationTokenSource();
        _duplexRepeatabilityStatus = Localization.Get("VmRepeatabilityApprovalAccepted");
        Changed(nameof(DuplexRepeatabilityConsentPending)); Changed(nameof(DuplexRepeatabilityConsentChecked)); Changed(nameof(DuplexRepeatabilityRunning)); Changed(nameof(DuplexRepeatabilityStatus));
        try
        {
            var result = await _duplexRepeatabilityWorkflow.RunApprovedAsync(preparation, Guid.NewGuid(), OnDuplexRepeatabilityProgress, _duplexRepeatabilityCancellation.Token).ConfigureAwait(true);
            _duplexRepeatabilityResult = result; _duplexRepeatabilityStatus = result.PersistenceStatus == "SAVE_FAILED" ? $"SAVE_FAILED · {result.PersistenceError}" : result.Summary;
        }
        catch (Exception ex) { _duplexRepeatabilityStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmRepeatabilityOutcomeUnknown"), ex.GetType().Name, ex.Message); }
        finally { _duplexRepeatabilityRunning = false; _duplexRepeatabilityCancellation?.Dispose(); _duplexRepeatabilityCancellation = null; Changed(nameof(DuplexRepeatabilityRunning)); Changed(nameof(DuplexRepeatabilityResult)); Changed(nameof(DuplexRepeatabilityResultText)); Changed(nameof(DuplexRepeatabilityStatus)); PrepareDuplexRepeatabilityCommand.Refresh(); CancelDuplexRepeatabilityCommand.Refresh(); }
    }

    private void CancelDuplexRepeatabilityConsent()
    {
        if (!_duplexRepeatabilityConsentPending) return;
        _duplexRepeatabilityConsentPending = false; _duplexRepeatabilityConsentChecked = false; _duplexRepeatabilityPreparation = null; _duplexRepeatabilityStatus = Localization.Get("VmRepeatabilityApprovalCancelled");
        Changed(nameof(DuplexRepeatabilityConsentPending)); Changed(nameof(DuplexRepeatabilityConsentChecked)); Changed(nameof(DuplexRepeatabilityPlanText)); Changed(nameof(DuplexRepeatabilityStatus)); ConfirmDuplexRepeatabilityCommand.Refresh(); PrepareDuplexRepeatabilityCommand.Refresh();
    }

    private void CancelDuplexRepeatabilityAuthorization(string? message = null)
    {
        if (!_duplexRepeatabilityConsentPending && _duplexRepeatabilityPreparation is null) return;
        _duplexRepeatabilityConsentPending = false;
        _duplexRepeatabilityConsentChecked = false;
        _duplexRepeatabilityPreparation = null;
        _duplexRepeatabilityStatus = message ?? Localization.Get("VmRepeatabilityPlanChanged");
        Changed(nameof(DuplexRepeatabilityConsentPending)); Changed(nameof(DuplexRepeatabilityConsentChecked)); Changed(nameof(DuplexRepeatabilityPlanText)); Changed(nameof(DuplexRepeatabilityStatus));
        ConfirmDuplexRepeatabilityCommand.Refresh(); CancelDuplexRepeatabilityConsentCommand.Refresh(); PrepareDuplexRepeatabilityCommand.Refresh();
    }

    private void CancelDuplexRepeatability() { if (_duplexRepeatabilityRunning) { _duplexRepeatabilityStatus = Localization.Get("VmRepeatabilityCancellationRequested"); _duplexRepeatabilityCancellation?.Cancel(); Changed(nameof(DuplexRepeatabilityStatus)); } }

    private void OnDuplexRepeatabilityProgress(ScanProgressEvent progress)
    {
        void Apply() { _duplexRepeatabilityStatus = $"{progress.Lifecycle}: {progress.Action}"; Changed(nameof(DuplexRepeatabilityStatus)); }
        if (_uiContext is null) Apply(); else _uiContext.Post(_ => { try { Apply(); } catch { } }, null);
    }

    private bool CanPrepareCustomRepeatabilityInternal() => _duplexRepeatabilityWorkflow is ICustomDuplexRepeatabilityWorkflow && CanPrepareCustomPeriodDuplexInternal() && !_customPeriodDuplexConsentPending && !_customRepeatabilityConsentPending && !_customRepeatabilityRunning;

    private async Task PrepareCustomRepeatabilityAsync()
    {
        if (!CanPrepareCustomRepeatabilityInternal() || _duplexRepeatabilityWorkflow is not ICustomDuplexRepeatabilityWorkflow workflow || _windowsEndpointSnapshot is null || _periodDiscoveryResult is null || SelectedDuplexCaptureEndpoint is not { } capture || SelectedDuplexRenderEndpoint is not { } render || !int.TryParse(_customPeriodFramesText, out var frames)) return;
        try
        {
            var discovery = await _duplexRepeatabilityWorkflow.RefreshEndpointsAsync(CancellationToken.None).ConfigureAwait(true);
            var freshCapture = discovery.Endpoints.Single(e => e.EndpointId == capture.EndpointId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE");
            var freshRender = discovery.Endpoints.Single(e => e.EndpointId == render.EndpointId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE");
            _customRepeatabilityPreparation = workflow.PrepareCustom(discovery, freshCapture, freshRender, _periodDiscoveryResult, frames);
            _customRepeatabilityResult = null; _customRepeatabilityConsentChecked = false; _customRepeatabilityConsentPending = true;
            _customRepeatabilityStatus = Localization.Get("VmCustomRepeatabilityPlanPrepared");
        }
        catch (Exception ex) { _customRepeatabilityPreparation = null; _customRepeatabilityStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmCustomRepeatabilityPlanFailed"), ex.GetType().Name, ex.Message); }
        Changed(nameof(CustomRepeatabilityConsentPending)); Changed(nameof(CustomRepeatabilityPlanText)); Changed(nameof(CustomRepeatabilityStatus)); ConfirmCustomRepeatabilityCommand.Refresh(); CancelCustomRepeatabilityConsentCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh();
    }

    private async Task ConfirmCustomRepeatabilityAsync()
    {
        if (_duplexRepeatabilityWorkflow is not ICustomDuplexRepeatabilityWorkflow workflow || !_customRepeatabilityConsentPending || !_customRepeatabilityConsentChecked || _customRepeatabilityPreparation is not { } preparation || _customRepeatabilityRunning) return;
        if (WorkerState.State != WorkerConnectionState.ConnectedFake || WorkerState.ProcessId != preparation.WorkerProcessId || !_windowsDiscoveryCurrent || _windowsEndpointSnapshot is null || DuplexRepeatabilityPreparation.Fingerprint(_windowsEndpointSnapshot) != preparation.DiscoveryFingerprint || SelectedDuplexCaptureEndpoint?.EndpointId != preparation.Plan.Target.Capture?.EndpointId || SelectedDuplexRenderEndpoint?.EndpointId != preparation.Plan.Target.Render?.EndpointId)
        { CancelCustomRepeatabilityConsent(Localization.Get("VmCustomRepeatabilityAuthorizationInvalidated")); return; }
        _customRepeatabilityConsentPending = false; _customRepeatabilityConsentChecked = false; _customRepeatabilityRunning = true; _customRepeatabilityCancellation = new CancellationTokenSource();
        Changed(nameof(CustomRepeatabilityConsentPending)); Changed(nameof(CustomRepeatabilityRunning)); PrepareCustomRepeatabilityCommand.Refresh(); ConfirmCustomRepeatabilityCommand.Refresh(); CancelCustomRepeatabilityConsentCommand.Refresh(); CancelCustomRepeatabilityCommand.Refresh();
        try { _customRepeatabilityResult = await workflow.RunCustomApprovedAsync(preparation, Guid.NewGuid(), OnDuplexRepeatabilityProgress, _customRepeatabilityCancellation.Token).ConfigureAwait(true); _customRepeatabilityStatus = _customRepeatabilityResult.Summary; }
        catch (Exception ex) { _customRepeatabilityStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmCustomRepeatabilityOutcomeUnknown"), ex.GetType().Name, ex.Message); }
        finally { _customRepeatabilityRunning = false; _customRepeatabilityCancellation?.Dispose(); _customRepeatabilityCancellation = null; Changed(nameof(CustomRepeatabilityRunning)); Changed(nameof(CustomRepeatabilityResultText)); Changed(nameof(CustomRepeatabilityStatus)); PrepareCustomRepeatabilityCommand.Refresh(); CancelCustomRepeatabilityCommand.Refresh(); }
    }

    private void CancelCustomRepeatabilityConsent(string? message = null)
    { if (!_customRepeatabilityConsentPending) return; _customRepeatabilityConsentPending = false; _customRepeatabilityConsentChecked = false; _customRepeatabilityPreparation = null; _customRepeatabilityStatus = message ?? Localization.Get("VmCustomRepeatabilityApprovalCancelled"); Changed(nameof(CustomRepeatabilityConsentPending)); Changed(nameof(CustomRepeatabilityPlanText)); Changed(nameof(CustomRepeatabilityStatus)); ConfirmCustomRepeatabilityCommand.Refresh(); CancelCustomRepeatabilityConsentCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh(); }
    private void CancelCustomRepeatability() { if (_customRepeatabilityRunning) _customRepeatabilityCancellation?.Cancel(); }

    private bool CanPreparePeriodDiscoveryInternal() => _exclusivePeriodDiscoveryWorkflow is not null && _worker is not null && _worker.Capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal) && _worker.Capabilities.Contains("RealExclusiveCustomPeriodInitialize", StringComparer.Ordinal) && WorkerState.State == WorkerConnectionState.ConnectedFake && _windowsDiscoveryCurrent && HasCurrentDuplexPair && !_workerStartOutcomeUnknown && !_automaticScanOutcomeUnknown && !AutomaticScanBusy && !WorkerOperationActive && !_deviceScanRunning && !_realQueryRunning && !_realInitializeRunning && !_streamValidationRunning && !_duplexValidationRunning && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending && !AutomaticScanConsentPending && !WorkerConsentPending && !DuplexConsentPending && !RealInitializeConsentPending && !DeviceScanConsentPending && !_streamConsentPending && !_periodDiscoveryRunning && !_periodDiscoveryConsentPending;
    private string PeriodDiscoveryBlockingReason()
    {
        if (_exclusivePeriodDiscoveryWorkflow is null) return Localization.Get("VmPeriodDiscoveryUnavailable");
        if (WorkerState.State != WorkerConnectionState.ConnectedFake) return Localization.Get("VmWorkerDisconnectedMessage");
        if (_worker is null || !_worker.Capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal) || !_worker.Capabilities.Contains("RealExclusiveCustomPeriodInitialize", StringComparer.Ordinal)) return Localization.Get("VmPeriodDiscoveryWorkerCapabilitiesMissing");
        if (_workerStartOutcomeUnknown || _automaticScanOutcomeUnknown) return Localization.Get("VmPeriodDiscoveryPriorOutcomeUnknown");
        if (!_windowsDiscoveryCurrent) return Localization.Get("VmPeriodDiscoveryRefreshWindows");
        if (!HasCurrentDuplexPair) return Localization.Get("VmPeriodDiscoverySelectExactPair");
        if (AutomaticScanBusy || WorkerOperationActive || _deviceScanRunning || _realQueryRunning || _realInitializeRunning || _streamValidationRunning || _duplexValidationRunning || _duplexRepeatabilityRunning || _periodDiscoveryRunning) return Localization.Get("VmPeriodDiscoveryOperationActive");
        if (AutomaticScanConsentPending || WorkerConsentPending || DuplexConsentPending || RealInitializeConsentPending || DeviceScanConsentPending || _streamConsentPending || _duplexRepeatabilityConsentPending || _periodDiscoveryConsentPending) return Localization.Get("VmPeriodDiscoveryAwaitingConsent");
        return Localization.Get("VmPeriodDiscoveryNeedsCurrentPair");
    }
    private async Task PreparePeriodDiscoveryAsync()
    {
        if (!CanPreparePeriodDiscoveryInternal() || _exclusivePeriodDiscoveryWorkflow is null || _windowsEndpointSnapshot is null || SelectedDuplexCaptureEndpoint is not { } capture || SelectedDuplexRenderEndpoint is not { } render) return;
        try
        {
            _periodDiscoveryPreparation = await _exclusivePeriodDiscoveryWorkflow.PrepareAsync(_windowsEndpointSnapshot, capture, render, CancellationToken.None).ConfigureAwait(true);
            _periodDiscoveryResult = null; _periodDiscoveryResultFingerprint = null; _periodDiscoveryConsentChecked = false; _periodDiscoveryConsentPending = true; _periodDiscoveryStatus = Localization.Get("VmPeriodDiscoveryPlanPrepared");
        }
        catch (Exception ex) { _periodDiscoveryPreparation = null; _periodDiscoveryStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmPeriodDiscoveryPreparationFailed"), ex.GetType().Name, ex.Message); }
        Changed(nameof(PeriodDiscoveryConsentPending)); Changed(nameof(PeriodDiscoveryPlanText)); Changed(nameof(PeriodDiscoveryStatus)); Changed(nameof(CustomPeriodDuplexAvailabilityReason)); Changed(nameof(CanPrepareCustomRepeatability)); ConfirmPeriodDiscoveryCommand.Refresh(); CancelPeriodDiscoveryCommand.Refresh(); PreparePeriodDiscoveryCommand.Refresh(); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh();
    }
    private async Task ConfirmPeriodDiscoveryAsync()
    {
        if (_exclusivePeriodDiscoveryWorkflow is null || !_periodDiscoveryConsentPending || !_periodDiscoveryConsentChecked || _periodDiscoveryPreparation is not { } preparation || _periodDiscoveryRunning || _windowsEndpointSnapshot is null || SelectedDuplexCaptureEndpoint?.EndpointId != preparation.Capture.EndpointId || SelectedDuplexRenderEndpoint?.EndpointId != preparation.Render.EndpointId || ExclusivePeriodDiscoveryPlanning.Fingerprint(_windowsEndpointSnapshot) != preparation.DiscoveryFingerprint || !_windowsDiscoveryCurrent || WorkerState.State != WorkerConnectionState.ConnectedFake)
        {
            if (_periodDiscoveryConsentPending) CancelPeriodDiscovery(Localization.Get("VmPeriodDiscoveryApprovalDiscarded"));
            return;
        }
        _periodDiscoveryConsentPending = false; _periodDiscoveryConsentChecked = false; _periodDiscoveryRunning = true; _periodDiscoveryCancellation = new CancellationTokenSource(); _periodDiscoveryStatus = Localization.Get("VmPeriodDiscoveryApprovalAccepted");
        Changed(nameof(PeriodDiscoveryConsentPending)); Changed(nameof(PeriodDiscoveryConsentChecked)); Changed(nameof(PeriodDiscoveryRunning)); Changed(nameof(PeriodDiscoveryStatus));
        try { _periodDiscoveryResult = await _exclusivePeriodDiscoveryWorkflow.RunApprovedAsync(preparation, Guid.NewGuid(), message => { _periodDiscoveryStatus = message; Changed(nameof(PeriodDiscoveryStatus)); }, _periodDiscoveryCancellation.Token).ConfigureAwait(true); _periodDiscoveryResultFingerprint = _periodDiscoveryResult.Lifecycle == "COMPLETED" ? preparation.DiscoveryFingerprint : null; _periodDiscoveryStatus = _periodDiscoveryResult.PersistenceStatus; }
        catch (Exception ex) { _periodDiscoveryResultFingerprint = null; _periodDiscoveryStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmPeriodDiscoveryOutcomeUnknown"), ex.GetType().Name, ex.Message); }
        finally { _periodDiscoveryRunning = false; _periodDiscoveryCancellation?.Dispose(); _periodDiscoveryCancellation = null; Changed(nameof(PeriodDiscoveryRunning)); Changed(nameof(PeriodDiscoveryResultText)); Changed(nameof(PeriodDiscoveryMatrixText)); Changed(nameof(PeriodDiscoveryReportPath)); Changed(nameof(PeriodDiscoveryStatus)); Changed(nameof(CustomPeriodDuplexAvailabilityReason)); Changed(nameof(CanPrepareCustomRepeatability)); PreparePeriodDiscoveryCommand.Refresh(); CancelPeriodDiscoveryRunCommand.Refresh(); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh(); }
    }
    private void CancelPeriodDiscovery(string? message = null)
    {
        if (!_periodDiscoveryConsentPending && _periodDiscoveryPreparation is null) return;
        _periodDiscoveryConsentPending = false; _periodDiscoveryConsentChecked = false; _periodDiscoveryPreparation = null; _periodDiscoveryStatus = message ?? Localization.Get("VmPeriodDiscoveryApprovalCancelled");
        Changed(nameof(PeriodDiscoveryConsentPending)); Changed(nameof(PeriodDiscoveryConsentChecked)); Changed(nameof(PeriodDiscoveryPlanText)); Changed(nameof(PeriodDiscoveryStatus)); Changed(nameof(CustomPeriodDuplexAvailabilityReason)); Changed(nameof(CanPrepareCustomRepeatability)); ConfirmPeriodDiscoveryCommand.Refresh(); CancelPeriodDiscoveryCommand.Refresh(); PreparePeriodDiscoveryCommand.Refresh(); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh();
    }
    private void CancelPeriodDiscoveryRun() { if (_periodDiscoveryRunning) { _periodDiscoveryStatus = Localization.Get("VmPeriodDiscoveryCancellationRequested"); _periodDiscoveryCancellation?.Cancel(); Changed(nameof(PeriodDiscoveryStatus)); } }

    private bool CanPrepareCustomPeriodDuplexInternal()
    {
        if (_customPeriodDuplexWorkflow is null || _worker is null || WorkerState.State != WorkerConnectionState.ConnectedFake || !_windowsDiscoveryCurrent || !HasCurrentDuplexPair || _windowsEndpointSnapshot is null || _periodDiscoveryResult is not { Lifecycle: "COMPLETED" } || _periodDiscoveryResultFingerprint != ExclusivePeriodDiscoveryPlanning.Fingerprint(_windowsEndpointSnapshot) || _customPeriodDuplexRunning || WorkerOperationActive || _periodDiscoveryRunning) return false;
        return int.TryParse(_customPeriodFramesText, out var frames) && frames > 0 && frames <= 48_000 && SelectedDuplexCaptureEndpoint is { } capture && SelectedDuplexRenderEndpoint is { } render && TrySelectCustomPeriod(capture, render, frames);
    }

    private bool TrySelectCustomPeriod(WindowsEndpointDto capture, WindowsEndpointDto render, int frames)
    {
        try { _ = CustomPeriodDuplexEvidence.Select(_periodDiscoveryResult!, capture.EndpointId!, render.EndpointId!, frames); return true; }
        catch { return false; }
    }

    private async Task PrepareCustomPeriodDuplexAsync()
    {
        if (!CanPrepareCustomPeriodDuplexInternal() || _customPeriodDuplexWorkflow is null || _windowsEndpointSnapshot is null || _periodDiscoveryResult is null || SelectedDuplexCaptureEndpoint is not { } capture || SelectedDuplexRenderEndpoint is not { } render || !int.TryParse(_customPeriodFramesText, out var frames)) return;
        try
        {
            _customPeriodDuplexPreparation = _customPeriodDuplexWorkflow.Prepare(_windowsEndpointSnapshot, capture, render, _periodDiscoveryResult, frames);
            _customPeriodDuplexConsentChecked = false; _customPeriodDuplexConsentPending = true; _customPeriodDuplexStatus = Localization.Get("VmCustomPeriodDuplexPlanPrepared");
        }
        catch (Exception ex) { _customPeriodDuplexPreparation = null; _customPeriodDuplexStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmCustomPeriodDuplexPreparationFailed"), ex.GetType().Name, ex.Message); }
        Changed(nameof(CustomPeriodDuplexConsentPending)); Changed(nameof(CustomPeriodDuplexPlanText)); Changed(nameof(CustomPeriodDuplexStatus)); Changed(nameof(CanPrepareCustomRepeatability)); ConfirmCustomPeriodDuplexCommand.Refresh(); CancelCustomPeriodDuplexConsentCommand.Refresh(); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh();
    }

    private async Task ConfirmCustomPeriodDuplexAsync()
    {
        if (_customPeriodDuplexWorkflow is null || !_customPeriodDuplexConsentPending || !_customPeriodDuplexConsentChecked || _customPeriodDuplexPreparation is not { } preparation || _windowsEndpointSnapshot is null || SelectedDuplexCaptureEndpoint?.EndpointId != preparation.Capture.EndpointId || SelectedDuplexRenderEndpoint?.EndpointId != preparation.Render.EndpointId || ExclusivePeriodDiscoveryPlanning.Fingerprint(_windowsEndpointSnapshot) != preparation.DiscoveryFingerprint || !_windowsDiscoveryCurrent || WorkerState.State != WorkerConnectionState.ConnectedFake)
        { if (_customPeriodDuplexConsentPending) CancelCustomPeriodDuplexConsent(Localization.Get("VmCustomPeriodDuplexAuthorizationInvalidated")); return; }
        _customPeriodDuplexConsentPending = false; _customPeriodDuplexConsentChecked = false; _customPeriodDuplexRunning = true; _customPeriodDuplexCancellation = new(); _customPeriodDuplexStatus = Localization.Get("VmCustomPeriodDuplexApprovalAccepted");
        Changed(nameof(CustomPeriodDuplexConsentPending)); Changed(nameof(CustomPeriodDuplexConsentChecked)); Changed(nameof(CustomPeriodDuplexRunning)); Changed(nameof(CustomPeriodDuplexStatus)); Changed(nameof(CanPrepareCustomRepeatability)); PrepareCustomRepeatabilityCommand.Refresh();
        try { _customPeriodDuplexResult = await _customPeriodDuplexWorkflow.RunApprovedAsync(preparation, Guid.NewGuid(), _customPeriodDuplexCancellation.Token).ConfigureAwait(true); _customPeriodDuplexStatus = _customPeriodDuplexResult.PersistenceStatus; }
        catch (Exception ex) { _customPeriodDuplexStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmCustomPeriodDuplexOutcomeUnknown"), ex.GetType().Name, ex.Message); }
        finally { _customPeriodDuplexRunning = false; _customPeriodDuplexCancellation?.Dispose(); _customPeriodDuplexCancellation = null; Changed(nameof(CustomPeriodDuplexRunning)); Changed(nameof(CustomPeriodDuplexResultText)); Changed(nameof(CustomPeriodDuplexStatus)); Changed(nameof(CanPrepareCustomRepeatability)); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh(); CancelCustomPeriodDuplexCommand.Refresh(); }
    }

    private void CancelCustomPeriodDuplexConsent(string? message = null)
    { _customPeriodDuplexConsentPending = false; _customPeriodDuplexConsentChecked = false; _customPeriodDuplexPreparation = null; _customPeriodDuplexStatus = message ?? Localization.Get("VmCustomPeriodDuplexApprovalCancelled"); Changed(nameof(CustomPeriodDuplexConsentPending)); Changed(nameof(CustomPeriodDuplexConsentChecked)); Changed(nameof(CustomPeriodDuplexPlanText)); Changed(nameof(CustomPeriodDuplexStatus)); Changed(nameof(CanPrepareCustomRepeatability)); ConfirmCustomPeriodDuplexCommand.Refresh(); CancelCustomPeriodDuplexConsentCommand.Refresh(); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh(); }
    private void CancelCustomPeriodDuplex() { if (_customPeriodDuplexRunning) { _customPeriodDuplexStatus = Localization.Get("VmCustomPeriodDuplexCancellationRequested"); _customPeriodDuplexCancellation?.Cancel(); Changed(nameof(CustomPeriodDuplexStatus)); } }

    private bool CanRequestDuplexValidation() => HasCurrentDuplexPair && _worker is not null && _worker.Capabilities.Contains("RealExclusiveDuplexValidation", StringComparer.Ordinal) && !_duplexValidationRunning && !_deviceScanRunning && !_realQueryRunning && !_realInitializeRunning && !_streamValidationRunning && !DuplexConsentPending && !WorkerOperationActive && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending;
    private void RequestDuplexValidation(string period)
    {
        if (!CanRequestDuplexValidation() || period is not ("MINIMUM" or "DEFAULT") || SelectedDuplexCaptureEndpoint?.EndpointId is not { } capture || SelectedDuplexRenderEndpoint?.EndpointId is not { } render) return;
        _pendingDuplexPeriod = period;
        _pendingDuplexConsent = new(true, Guid.NewGuid(), DuplexConsent.Fingerprint(capture, render, period, 5, "OFF"));
        DuplexConsentChecked = false; DuplexConsentPending = true;
        _duplexStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmDuplexConfirmationRequired"), period);
        Changed(nameof(DuplexStatus)); RequestDuplexMinimumCommand.Refresh(); RequestDuplexDefaultCommand.Refresh(); CancelDuplexConsentCommand.Refresh();
    }

    private void CancelDuplexConsent()
    {
        _pendingDuplexConsent = null; _pendingDuplexPeriod = null; DuplexConsentChecked = false; DuplexConsentPending = false;
        _duplexStatus = Localization.Get("VmDuplexApprovalCancelled"); Changed(nameof(DuplexStatus));
        CancelDuplexConsentCommand.Refresh(); RequestDuplexMinimumCommand.Refresh(); RequestDuplexDefaultCommand.Refresh();
    }

    private async Task CancelDuplexValidationAsync()
    {
        if (_worker is null || _activeDuplexOperationId is null) return;
        _duplexStatus = Localization.Get("VmDuplexCancellationRequested"); Changed(nameof(DuplexStatus));
        await _worker.CancelDuplexValidationAsync(_activeDuplexOperationId);
    }

    private async Task ConfirmDuplexValidationAsync()
    {
        if (_worker is null || !DuplexConsentPending || !DuplexConsentChecked || _pendingDuplexConsent is not { } consent || _pendingDuplexPeriod is not { } period || SelectedDuplexCaptureEndpoint is not { EndpointId: { } captureId, DeviceState: "ACTIVE" } capture || SelectedDuplexRenderEndpoint is not { EndpointId: { } renderId, DeviceState: "ACTIVE" } render) return;
        if (!HasCurrentDuplexPair || consent.RequestFingerprint != DuplexConsent.Fingerprint(captureId, renderId, period, 5, "OFF")) { CancelDuplexConsent(); _duplexStatus = "Пара или настройки изменились; требуется новое подтверждение."; Changed(nameof(DuplexStatus)); return; }
        _pendingDuplexConsent = null; _pendingDuplexPeriod = null; DuplexConsentPending = false; DuplexConsentChecked = false;
        var operationId = Guid.NewGuid().ToString("D"); _activeDuplexOperationId = operationId; _duplexValidationRunning = true;
        _duplexStatus = $"RUNNING · {period} · до 5 секунд на точной паре; Capture отбросит данные, Render отправляет тишину.";
        Changed(nameof(DuplexValidationRunning)); Changed(nameof(HasDuplexPairProfileEvidence)); Changed(nameof(CanSaveDeviceProfile)); Changed(nameof(DuplexStatus)); CancelDuplexValidationCommand.Refresh(); RequestDuplexMinimumCommand.Refresh(); RequestDuplexDefaultCommand.Refresh();
        try
        {
            var result = await _worker.ValidateDuplexAsync(operationId, captureId, renderId, period, 5, "OFF", consent);
            if (SelectedDuplexCaptureEndpoint?.EndpointId != captureId || SelectedDuplexRenderEndpoint?.EndpointId != renderId || !_windowsDiscoveryCurrent || WorkerState.State != WorkerConnectionState.ConnectedFake) throw new IOException("Pair/worker changed during Duplex; current result is unknown and cannot authorize profile recommendation.");
            _duplexValidations[period] = result;
            var assessment = DuplexAssessments.Single(a => a.Period == period);
            _duplexStatus = $"{period}: {assessment.Classification} · Capture {assessment.CaptureCoverage} · Render {assessment.RenderCoverage}. {assessment.Reason}";
            _operatorResultEndpoint = $"Capture · {capture.FriendlyNameDisplay}\nRender · {render.FriendlyNameDisplay}";
            _operatorResultMode = "Одновременная проверка Duplex";
            _operatorResultProvenance = "REAL_WASAPI_DUPLEX";
            _operatorResultConfiguration = $"48 кГц stereo PCM16 · {period} · 5 секунд · MMCSS OFF";
            OperatorResultOutcome = $"{assessment.Classification} · {assessment.Reason}";
            Changed(nameof(DuplexAssessments)); Changed(nameof(DuplexResultCards)); Changed(nameof(DuplexRecommendation)); Changed(nameof(DuplexRecommendationSummary)); Changed(nameof(HasDuplexResults)); Changed(nameof(CanSaveDeviceProfile));
            NotifyOperatorResult();
        }
        catch (Exception ex)
        {
            _duplexStatus = $"{(ex is IOException or TimeoutException or OperationCanceledException ? "UNKNOWN" : "FAILED")} · {ex.GetType().Name}: {ex.Message}. Stop/cleanup не подтверждены.";
        }
        finally
        {
            _activeDuplexOperationId = null; _duplexValidationRunning = false; Changed(nameof(DuplexValidationRunning)); Changed(nameof(HasDuplexPairProfileEvidence)); Changed(nameof(CanSaveDeviceProfile)); Changed(nameof(DuplexStatus)); CancelDuplexValidationCommand.Refresh(); RequestDuplexMinimumCommand.Refresh(); RequestDuplexDefaultCommand.Refresh();
        }
    }

    private void CancelStreamConsent()
    {
        _pendingStreamConsent = null; StreamConsentChecked = false; _streamConsentPending = false;
        Changed(nameof(StreamConsentPending)); ConfirmStreamValidationCommand.Refresh(); CancelStreamConsentCommand.Refresh(); RequestStreamValidationCommand.Refresh();
    }

    private async Task CancelStreamValidationAsync()
    {
        if (_streamValidationCancellation is null || _worker is null || _activeStreamOperationId is null) return;
        _streamValidationStatus = "Отмена принята к обработке worker; ожидается Stop и итоговый отчёт."; Changed(nameof(StreamValidationStatus));
        try { await _worker.CancelStreamValidationAsync(_activeStreamOperationId).ConfigureAwait(false); }
        catch { _streamValidationCancellation.Cancel(); throw; }
    }

    private async Task ConfirmStreamValidationAsync()
    {
        if (!_streamConsentPending || !_streamConsentChecked || _pendingStreamConsent is not { } consent || _worker is null || SelectedWindowsEndpoint is not { EndpointId: { } endpointId, DeviceState: "ACTIVE" } endpoint || SelectedStreamCandidate is not { } candidate) return;
        if (!CanUseSelectedWindowsEndpoint || WorkerState.State != WorkerConnectionState.ConnectedFake || consent.RequestFingerprint != RealStreamConsent.Fingerprint(endpointId, endpoint.Direction, candidate.Period.ToUpperInvariant(), 5, "OFF")) { CancelStreamConsent(); _streamValidationStatus = "Цель или настройки изменились; подтверждение сброшено."; Changed(nameof(StreamValidationStatus)); return; }
        CancelStreamConsent();
        var period = candidate.Period.ToUpperInvariant();
        _lastStreamAttemptPeriod = period;
        _streamAttemptedSinceProfileSave = true;
        _streamValidationRows[period] = new(period, candidate.ActualBufferFrames?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "неизвестно", candidate.InitializeStatus, "RUNNING", "неизвестно", "неизвестно", "UNKNOWN", "неизвестно", "RUNNING", "NOT_RUN", "RUNNING", "Ожидание результата worker.");
        _streamValidationRunning = true; Changed(nameof(StreamValidationRunning)); Changed(nameof(StreamValidationRows)); RequestStreamValidationCommand.Refresh(); CancelStreamValidationCommand.Refresh();
        _streamValidationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _activeStreamOperationId = Guid.NewGuid().ToString("D");
        _streamValidationProgress = $"Выполняется реальный {endpoint.Direction} stream · {period} · до 5 секунд.";
        _streamValidationStatus = $"{period}: RUNNING · {endpoint.Direction} · ожидается итоговый отчёт.";
        Changed(nameof(StreamValidationProgress)); Changed(nameof(StreamValidationStatus)); Changed(nameof(StreamEvidencePersistenceStatus));
        try
        {
            var result = await _worker.ValidateStreamAsync(_activeStreamOperationId, endpointId, endpoint.Direction, period, 5, "OFF", consent, _streamValidationCancellation.Token);
            if (result.EndpointId != endpointId || result.Direction != endpoint.Direction || WorkerState.State != WorkerConnectionState.ConnectedFake) throw new IOException("Worker or exact endpoint binding changed; stream outcome cannot be accepted as current.");
            _streamValidations.Add(result);
            if (_deviceScanResult is { } currentScan && currentScan.EndpointId == endpointId && currentScan.Direction == endpoint.Direction)
                _deviceScanResult = currentScan with { StreamValidations = _streamValidations.Where(s => s.EndpointId == endpointId && s.Direction == endpoint.Direction).ToArray() };
            var report = result.Report;
            var row = StreamValidationResultRowFromReport(period, report);
            _streamValidationRows[period] = row;
            _streamValidationStatus = $"{period}: {row.FinalStatus}. {row.Details}";
            OperatorResultOutcome = $"{period} stream: {row.FinalStatus}; Start={row.StartOutcome}; frames={row.FramesProcessedExpected}; quality={row.StreamQuality}. Длительная Stability и Duplex: NOT_RUN.";
            Changed(nameof(DeviceScanResult)); Changed(nameof(DeviceScanResultSummary)); Changed(nameof(StreamValidations)); Changed(nameof(StreamValidationRows)); Changed(nameof(StreamValidationStatus)); Changed(nameof(StreamValidationResultsSummary)); Changed(nameof(StreamEvidencePersistenceStatus)); Changed(nameof(OperatorResultStreamEvidenceNote)); Changed(nameof(OperatorResultProfileEvidenceNote)); Changed(nameof(CanSaveDeviceProfile));
        }
        catch (Exception ex)
        {
            var unknown = ex is WorkerCommandException commandError
                ? commandError.ErrorCode.Contains("TIMEOUT", StringComparison.Ordinal) || commandError.ErrorCode.Contains("UNKNOWN", StringComparison.Ordinal) || commandError.ErrorCode.Contains("DISCONNECT", StringComparison.Ordinal)
                : ex is TimeoutException or OperationCanceledException or IOException;
            var status = unknown ? "UNKNOWN" : "FAILED";
            var detail = $"{ex.GetType().Name}: {ex.Message}; Stop/cleanup не подтверждены.";
            _streamValidationRows[period] = new(period, candidate.ActualBufferFrames?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "неизвестно", candidate.InitializeStatus, "UNKNOWN", "неизвестно", "неизвестно", "UNKNOWN", "неизвестно", "UNKNOWN", "UNKNOWN", status, detail);
            _streamValidationStatus = $"{period}: {status}. {detail}";
            OperatorResultOutcome = $"{period} stream: {status}. {detail} Длительная Stability и Duplex: NOT_RUN.";
            Changed(nameof(StreamValidationRows)); Changed(nameof(StreamValidationStatus)); Changed(nameof(StreamValidationResultsSummary)); Changed(nameof(StreamEvidencePersistenceStatus));
        }
        finally
        {
            _streamValidationCancellation?.Dispose(); _streamValidationCancellation = null; _activeStreamOperationId = null; _streamValidationRunning = false;
            Changed(nameof(StreamValidationRunning)); Changed(nameof(StreamValidationRows)); RequestStreamValidationCommand.Refresh(); CancelStreamValidationCommand.Refresh();
        }
    }

    private static StreamValidationResultRow StreamValidationResultRowFromReport(string period, System.Text.Json.JsonElement report)
    {
        string? Text(string name) => report.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        string Value(string name) => report.TryGetProperty(name, out var v) && v.ValueKind is not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined) ? v.ToString() : "неизвестно";
        bool? Bool(string name) => report.TryGetProperty(name, out var v) && (v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) ? v.GetBoolean() : null;
        string Outcome(string name, string? hresultName = null)
        {
            if (!report.TryGetProperty(name, out var v) || v.ValueKind == System.Text.Json.JsonValueKind.Null) return hresultName is not null && Text(hresultName) == "NOT_ATTEMPTED" ? "NOT_RUN" : "UNKNOWN";
            return v.ValueKind == System.Text.Json.JsonValueKind.True ? "SUCCESS" : v.ValueKind == System.Text.Json.JsonValueKind.False ? "FAILED" : "UNKNOWN";
        }
        var frames = Value("framesProcessed"); var expected = Value("expectedFrames");
        var coverage = report.TryGetProperty("frameCoveragePercent", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number && c.TryGetDouble(out var percent) && double.IsFinite(percent) ? percent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%" : "неизвестно";
        var faults = string.Join(" · ", new[] { "discontinuities", "timestampErrors", "unavailableCaptureBuffers", "bufferServiceFaults", "eventWaitTimeouts" }.Select(name => $"{name}={Value(name)}"));
        var quality = Text("streamQuality") ?? "UNKNOWN";
        var cleanup = Text("cleanup") ?? "UNKNOWN";
        var start = Outcome("startSucceeded", "startHresult");
        var stop = Outcome("stopSucceeded", "stopHresult");
        var initialized = Outcome("initializeSucceeded", "finalInitializeHresult");
        string status;
        if (Bool("cancelled") == true) status = "CANCELLED";
        else if (Bool("watchdogTimedOut") == true) status = "UNKNOWN";
        else if (initialized == "FAILED" || start == "FAILED" || stop == "FAILED") status = "FAILED";
        else if (initialized == "UNKNOWN" || start == "UNKNOWN" || stop == "UNKNOWN" || frames == "неизвестно" || expected == "неизвестно" || quality == "UNKNOWN") status = "UNKNOWN";
        else if (quality == "CLEAN" && Bool("durationReached") == true && frames != "0" && !cleanup.Contains("FAILED", StringComparison.Ordinal) && !cleanup.Contains("UNKNOWN", StringComparison.Ordinal) && !cleanup.Contains("RELEASE_PENDING", StringComparison.Ordinal)) status = "COMPLETED";
        else status = "COMPLETED_WITH_WARNINGS";
        var errorStage = Text("errorStage"); var errorHresult = Text("errorHresult");
        var details = errorStage is { Length: > 0 } and not ("none" or "UNKNOWN") ? $"Этап: {errorStage}; HRESULT: {errorHresult ?? "неизвестно"}." : quality == "CLEAN" ? "Отчёт завершён; cleanup содержит только явно наблюдённые этапы." : $"Поток: {quality}; durationReached={Value("durationReached")}; cleanup: {cleanup}.";
        return new(period, Value("actualBufferFrames"), initialized, start, $"{frames} / {expected}", coverage, quality, faults, stop, cleanup, status, details);
    }

    private void CancelDeviceScan()
    {
        if (!_deviceScanRunning || _deviceScanCancellation is null) return;
        _deviceScanProgress = "Отмена запрошена; ожидание завершения текущего вызова worker.";
        Changed(nameof(DeviceScanProgress));
        _deviceScanCancellation.Cancel();
        Changed(nameof(CanSaveDeviceProfile));
    }

    private async Task ConfirmDeviceScanAsync()
    {
        if (!DeviceScanConsentPending || !DeviceScanConsentChecked || _worker is null || SelectedWindowsEndpoint is not { } endpoint || _pendingDeviceScanConsent is not { } consent) return;
        if (!CanUseSelectedWindowsEndpoint || endpoint.EndpointId != consent.EndpointId || endpoint.Direction != consent.Direction || DeviceConfigurationScan.Fingerprint(endpoint) != consent.Fingerprint || WorkerState.State != WorkerConnectionState.ConnectedFake)
        { CancelDeviceScanConsent(); WorkerNotice = "Endpoint, состояние или план изменились; подтверждение сканирования сброшено."; return; }
        CancelDeviceScanConsent();
        DeviceScanRunning = true;
        _deviceScanInitializations = [];
        _deviceScanResult = null;
        if (RealFormatMatrix is not null) _realQueryInvalidated = true;
        _realFormatError = null;
        RealInitializationResult = null;
        Changed(nameof(DeviceScanRunning)); Changed(nameof(DeviceScanResult)); Changed(nameof(HasDeviceScanResult)); Changed(nameof(HasSingleEndpointScanResult)); Changed(nameof(DeviceScanResultSummary)); Changed(nameof(CanSaveDeviceProfile));
        _deviceScanCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(DeviceConfigurationScan.MaximumDurationSeconds));
        var token = _deviceScanCancellation.Token;
        SetDeviceScanProgress(DeviceScanStage.Preflight, "Проверка выбранного активного endpoint и согласованного плана.");
        try
        {
            var result = await DeviceConfigurationScan.RunAsync(_worker, endpoint, consent, (stage, message) =>
            {
                if (_uiContext is null) SetDeviceScanProgress(stage, message);
                else _uiContext.Post(_ => { if (_deviceScanRunning) SetDeviceScanProgress(stage, message); }, null);
            }, token);
            _deviceScanResult = result;
            _deviceScanInitializations = result.Initializations;
            SelectedStreamCandidate = result.Initializations.SelectMany(r => r.Candidates).FirstOrDefault(c => c.InitializeAttempted && c.InitializeStatus is ("INITIALIZED" or "READY") && c.SampleRate == 48_000 && c.Encoding == "PCM16" && c.Channels == 2);
            _streamValidations.Clear(); _streamAttemptedSinceProfileSave = false; _lastStreamAttemptPeriod = null;
            _streamValidationRows["MINIMUM"] = StreamValidationResultRow.NotRun("MINIMUM"); _streamValidationRows["DEFAULT"] = StreamValidationResultRow.NotRun("DEFAULT");
            _streamValidationStatus = "Поток не проверялся. После успешного Initialize выберите период и запросите отдельное согласие.";
            if (result.Query is not null && SelectedWindowsEndpoint is { } current && current.EndpointId == result.EndpointId && current.Direction == result.Direction && CanUseSelectedWindowsEndpoint && WorkerState.State == WorkerConnectionState.ConnectedFake)
            {
                _realQueriedChannelCounts = endpoint.Direction == "CAPTURE" ? [1, 2] : [2];
                _realQueryInvalidated = false;
                RealFormatMatrix = result.Query;
                RealInitializationResult = null;
                _realFormatError = null;
            }
            else if (result.Query is not null)
            {
                _realQueryInvalidated = true;
            }
            _operatorResultEndpoint = $"{endpoint.FriendlyNameDisplay} · {endpoint.Direction} · {endpoint.DeviceState}";
            _operatorResultMode = "Автоматическое сканирование конфигурации";
            _operatorResultProvenance = "REAL_WASAPI_QUERY + REAL_WASAPI_INITIALIZE_ONLY";
            _operatorResultConfiguration = $"48 кГц PCM16 · {result.Initializations.Count} Initialize-only результатов";
            OperatorResultOutcome = string.Format(Localization.CurrentCulture, Localization.Get("VmScanOutcome"), result.State);
            SetDeviceScanProgress(result.State, result.Message);
            Changed(nameof(DeviceScanResult)); Changed(nameof(HasDeviceScanResult)); Changed(nameof(HasSingleEndpointScanResult)); Changed(nameof(DeviceScanInitializeCandidates)); Changed(nameof(DeviceScanResultSummary)); Changed(nameof(DeviceScanTechnicalDetails)); Changed(nameof(StreamValidations)); Changed(nameof(StreamValidationRows)); Changed(nameof(StreamValidationStatus)); Changed(nameof(StreamValidationResultsSummary)); Changed(nameof(StreamEvidencePersistenceStatus)); Changed(nameof(OperatorResultStreamEvidenceNote)); Changed(nameof(OperatorResultProfileEvidenceNote)); Changed(nameof(OperatorResultDetails)); Changed(nameof(OperatorResultProvenance)); Changed(nameof(HasCurrentOperatorQueryResult)); Changed(nameof(CanSaveDeviceProfile));
            NotifyOperatorResult();
            NavigateOperatorStep(OperatorStep.Result);
        }
        finally
        {
            _deviceScanCancellation.Dispose();
            _deviceScanCancellation = null;
            DeviceScanRunning = false;
        }
    }

    private void SetDeviceScanProgress(DeviceScanStage stage, string message)
    {
        _deviceScanProgress = $"{stage} · {message}";
        Changed(nameof(DeviceScanProgress));
    }

    private bool CanQueryRealFormats() => CanUseSelectedWindowsEndpoint && _worker is not null && WorkerState.State == WorkerConnectionState.ConnectedFake && _worker.Capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal) && !_windowsDiscoveryLoading && !_realQueryRunning && !_realInitializeRunning && !_deviceScanRunning && !WorkerOperationActive && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending && !RealInitializeConsentPending && !DeviceScanConsentPending;

    private async Task QueryRealFormatsAsync()
    {
        if (!CanQueryRealFormats() || _worker is null || SelectedWindowsEndpoint is not { EndpointId.Length: > 0 } endpoint) return;
        var id = endpoint.EndpointId;
        _deviceScanInitializations = [];
        _deviceScanResult = null;
        Changed(nameof(DeviceScanResult)); Changed(nameof(HasDeviceScanResult)); Changed(nameof(HasSingleEndpointScanResult)); Changed(nameof(DeviceScanResultSummary));
        var previousRow = SelectedRealFormatRow;
        var previousPeriod = RealInitializePeriod;
        var previousMatrixEndpoint = RealFormatMatrix?.EndpointId;
        var previousSelectionCurrent = HasCurrentOperatorQueryResult;
        _realQueryRunning = true;
        RequestDeviceScanCommand.Refresh();
        _realFormatError = Localization.Get("VmQueryInvalidated");
        Changed(nameof(RealFormatMatrixStatus)); Changed(nameof(HasOperatorQueryResult)); Changed(nameof(HasCurrentOperatorQueryResult)); Changed(nameof(NeedsInitializeFormatQuery)); Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(CanSaveDeviceProfile)); RequestRealInitializeCommand.Refresh();
        try
        {
            var requestedChannels = RealQueryChannelCountsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(value => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : -1).ToArray();
            if (requestedChannels.Length is < 1 or > 32 || requestedChannels.Any(count => count is < 1 or > 64) || requestedChannels.Distinct().Count() != requestedChannels.Length) throw new ArgumentException(Localization.Get("VmQueryChannelsValidation"));
            _operatorResultEndpoint = $"{endpoint.FriendlyNameDisplay} · {endpoint.Direction} · {endpoint.DeviceState}";
            _operatorResultMode = "Запрос Exclusive-форматов";
            _operatorResultProvenance = "REAL_WASAPI_QUERY";
            _operatorResultConfiguration = $"{string.Join(", ", requestedChannels)} каналов-кандидатов · 44,1/48 кГц · PCM16, PCM24-in-32, Float32";
            NotifyOperatorResult();
            var result = await _worker.QueryFormatMatrixAsync(id, endpoint.Direction, requestedChannels);
            if (WorkerState.State != WorkerConnectionState.ConnectedFake || !_windowsDiscoveryCurrent)
            {
                _operatorResultEndpoint = $"{endpoint.FriendlyNameDisplay} · {endpoint.Direction} · {endpoint.DeviceState}";
                _operatorResultMode = "Запрос Exclusive-форматов";
                _operatorResultConfiguration = Localization.Get("VmQueryDisconnectedConfiguration");
                _realFormatError = Localization.Get("VmQueryDisconnectedError");
                OperatorResultOutcome = Localization.Get("VmQueryDisconnectedOutcome");
                NotifyOperatorResult(); Changed(nameof(RealFormatMatrixStatus)); Changed(nameof(HasOperatorQueryResult)); Changed(nameof(HasCurrentOperatorQueryResult)); Changed(nameof(NeedsInitializeFormatQuery)); Changed(nameof(CanSaveDeviceProfile));
                if (OperatorTestMode != "INITIALIZE_ONLY") NavigateOperatorStep(OperatorStep.Result);
                return;
            }
            if (result.Provenance != "REAL_WASAPI_QUERY" || result.ReportLevel != "PASSIVE_QUERY" || result.EndpointId != id || result.Direction != endpoint.Direction) throw new InvalidDataException("Worker result did not match the selected Windows endpoint or passive-query contract.");
            _realQueriedChannelCounts = requestedChannels;
            _realQueryInvalidated = SelectedWindowsEndpoint?.EndpointId != id || SelectedWindowsEndpoint?.Direction != endpoint.Direction;
            _realFormatError = null;
            RealFormatMatrix = result; RealInitializationResult = null;
            var rows = RealFormatRows;
            var preserve = previousSelectionCurrent && previousMatrixEndpoint == id && previousRow is not null
                ? rows.FirstOrDefault(row => row.Format == previousRow.Format && row.SampleRate == previousRow.SampleRate && row.Channels == previousRow.Channels && IsEligibleSupportedRow(row, previousPeriod))
                : null;
            SelectedRealFormatRow = preserve ?? rows.FirstOrDefault(row => IsEligibleSupportedRow(row, previousPeriod));
            RealInitializePeriod = previousPeriod;
            OperatorResultOutcome = Localization.Get("VmQuerySuccessOutcome");
            Changed(nameof(RealFormatMatrixStatus)); Changed(nameof(RealFormatSummary)); Changed(nameof(HasOperatorQueryResult)); Changed(nameof(HasCurrentOperatorQueryResult)); Changed(nameof(NeedsInitializeFormatQuery)); Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(CanSaveDeviceProfile));
            NotifyOperatorResult();
            if (OperatorTestMode != "INITIALIZE_ONLY") NavigateOperatorStep(OperatorStep.Result);
        }
        catch (Exception ex) { _operatorResultEndpoint = $"{endpoint.FriendlyNameDisplay} · {endpoint.Direction} · {endpoint.DeviceState}"; _operatorResultMode = "Запрос Exclusive-форматов"; _realFormatError = $"{Localization.Get("VmQueryErrorPrefix")} REAL_WASAPI_QUERY: {ex.GetType().Name}: {ex.Message}" + (RealFormatMatrix is null ? "" : Localization.Get("VmQueryHistoricalSuffix")); OperatorResultOutcome = Localization.Get("VmQueryFailureOutcome"); NotifyOperatorResult(); Changed(nameof(RealFormatMatrixStatus)); Changed(nameof(RealFormatSummary)); Changed(nameof(HasOperatorQueryResult)); Changed(nameof(HasCurrentOperatorQueryResult)); Changed(nameof(NeedsInitializeFormatQuery)); Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(CanSaveDeviceProfile)); RequestRealInitializeCommand.Refresh(); WorkerNotice = _realFormatError; if (OperatorTestMode != "INITIALIZE_ONLY") NavigateOperatorStep(OperatorStep.Result); }
        finally { _realQueryRunning = false; Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(CanSaveDeviceProfile)); RequestRealInitializeCommand.Refresh(); RequestDeviceScanCommand.Refresh(); }
    }

    private bool CanRequestRealInitialize() => OperatorTestMode == "INITIALIZE_ONLY" && HasCurrentOperatorQueryResult && CanUseSelectedWindowsEndpoint && !_realQueryRunning && !_realInitializeRunning && !_deviceScanRunning && !WorkerOperationActive && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending && !RealInitializeConsentPending && !DeviceScanConsentPending && _worker is not null && _worker.Capabilities.Contains("RealExclusiveFormatInitialize", StringComparer.Ordinal) && SelectedRealFormatRow is { IsConflict: false, DisplayStatus: "SUPPORTED_REPORTED" } row && SelectedRealFormatCandidate is { SupportStatus: "SUPPORTED_REPORTED", Status: "SUPPORTED_REPORTED" } candidate && row.EvidenceCandidates.Contains(candidate);

    private static bool IsEligibleSupportedRow(RealFormatUniqueRowDto row, string period) => row.DisplayStatus == "SUPPORTED_REPORTED" && (period == "default" ? row.DefaultCandidate : row.MinimumCandidate) is { SupportStatus: "SUPPORTED_REPORTED", Status: "SUPPORTED_REPORTED" };

    private void RequestRealInitialize()
    {
        if (!CanRequestRealInitialize() || SelectedWindowsEndpoint?.EndpointId is not { } id || SelectedRealFormatCandidate is not { } candidate) return;
        _pendingRealEndpointId = id; _pendingRealDirection = SelectedWindowsEndpoint.Direction; _pendingRealFormatCandidate = candidate;
        RealInitializeConsentChecked = false; RealInitializeConsentPending = true; Changed(nameof(RealInitializeConsentText));
    }

    private async Task ConfirmRealInitializeAsync()
    {
        if (!RealInitializeConsentPending || !RealInitializeConsentChecked || _worker is null || _pendingRealEndpointId is null || _pendingRealDirection is null || _pendingRealFormatCandidate is not { } candidate) return;
        var endpointId = _pendingRealEndpointId; var direction = _pendingRealDirection;
        if (!CanUseSelectedWindowsEndpoint || SelectedWindowsEndpoint?.EndpointId != endpointId || SelectedWindowsEndpoint.Direction != direction || SelectedRealFormatCandidate != candidate || RealFormatMatrixIsStale)
        { CancelRealInitialize(); WorkerNotice = Localization.Get("VmInitializeSelectionChanged"); return; }
        _realInitializeRunning = true;
        RequestDeviceScanCommand.Refresh();
        Changed(nameof(RealInitializeDisabledReason)); RequestRealInitializeCommand.Refresh();
        try
        {
            _operatorResultEndpoint = OperatorSelectedDevice;
            _operatorResultMode = "Проверка Initialize-only";
            _operatorResultProvenance = "REAL_WASAPI_INITIALIZE_ONLY";
            _operatorResultConfiguration = $"{candidate.Encoding} · {candidate.SampleRate} Гц · {candidate.Channels} канал(а) · {candidate.Period}";
            NotifyOperatorResult();
            var fingerprint = RealFormatConsent.Fingerprint(endpointId, direction, candidate.SampleRate, candidate.Channels, candidate.Encoding, candidate.Period);
            var receipt = new RealFormatConsentDto(true, Guid.NewGuid(), fingerprint);
            _realInitializationError = null; Changed(nameof(RealInitializationDetails)); Changed(nameof(RealInitializationSummary)); Changed(nameof(RealFormatSummary));
            var result = await _worker.ValidateFormatMatrixAsync(endpointId, direction, candidate.SampleRate, candidate.Channels, candidate.Encoding, candidate.Period, receipt);
            if (result.Provenance != "REAL_WASAPI_INITIALIZE_ONLY" || result.EndpointId != endpointId || result.Direction != direction || result.ReportLevel != "INITIALIZE_ONLY" || result.Candidates.Count != 1 || result.Candidates[0] is not { } initialized || initialized.SampleRate != candidate.SampleRate || initialized.Channels != candidate.Channels || initialized.Encoding != candidate.Encoding || initialized.Period != candidate.Period)
                throw new InvalidDataException(Localization.Get("VmInitializeResponseMismatch"));
            RealInitializationResult = result;
            OperatorResultOutcome = string.Format(Localization.CurrentCulture, Localization.Get("VmInitializeSuccessOutcome"), RealInitializationResult.Candidates.FirstOrDefault()?.InitializeStatus ?? Localization.Get("VmInitializeResultMissing"));
            Changed(nameof(RealInitializationDetails));
            NotifyOperatorResult();
            NavigateOperatorStep(OperatorStep.Result);
        }
        catch (Exception ex) { _realInitializationError = $"{Localization.Get("VmInitializeErrorPrefix")} · {ex.GetType().Name}: {ex.Message}"; OperatorResultOutcome = Localization.Get("VmInitializeUnknownOutcome"); Changed(nameof(RealInitializationDetails)); Changed(nameof(RealInitializationSummary)); Changed(nameof(RealFormatSummary)); Changed(nameof(HasOperatorInitializeResult)); NotifyOperatorResult(); WorkerNotice = _realInitializationError; NavigateOperatorStep(OperatorStep.Result); }
        finally { CancelRealInitialize(); _realInitializeRunning = false; Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(RealFormatSummary)); RequestRealInitializeCommand.Refresh(); RequestDeviceScanCommand.Refresh(); }
    }

    private void CancelRealInitialize()
    {
        RealInitializeConsentPending = false; RealInitializeConsentChecked = false;
        _pendingRealEndpointId = null; _pendingRealDirection = null; _pendingRealFormatCandidate = null;
        Changed(nameof(RealInitializeConsentText));
    }

    private static string BindingStatus(DeviceProfileBindingRevalidationResult result) => result.Overall switch
    {
        DeviceProfileBindingAssessment.ExactBindingsPresentInDiscovery => Localization.Get("VmBindingExactFound"),
        DeviceProfileBindingAssessment.CaptureMissing => Localization.Get("VmBindingCaptureMissing"),
        DeviceProfileBindingAssessment.RenderMissing => Localization.Get("VmBindingRenderMissing"),
        DeviceProfileBindingAssessment.BothEndpointsMissing => Localization.Get("VmBindingBothMissing"),
        DeviceProfileBindingAssessment.EndpointDisabledOrUnplugged => Localization.Get("VmBindingEndpointUnavailable"),
        DeviceProfileBindingAssessment.DiscoveryIncomplete => Localization.Get("VmBindingDiscoveryIncomplete"),
        DeviceProfileBindingAssessment.DiscoveryProvenanceInvalid => Localization.Get("VmBindingInvalidProvenance"),
        DeviceProfileBindingAssessment.DiscoveryFailed => Localization.Get("VmBindingDiscoveryFailed"),
        DeviceProfileBindingAssessment.ProfileInvalidOrUnsupported => Localization.Get("VmBindingProfileInvalid"),
        DeviceProfileBindingAssessment.SanitizedUnboundProfile => Localization.Get("VmBindingUnboundProfile"),
        DeviceProfileBindingAssessment.AmbiguousOrDuplicateIdentity => Localization.Get("VmBindingIdentityAmbiguous"),
        DeviceProfileBindingAssessment.AvailableMetadataContradiction => Localization.Get("VmBindingMetadataContradiction"),
        DeviceProfileBindingAssessment.MetadataUnavailableOrInsufficient => Localization.Get("VmBindingMetadataInsufficient"),
        DeviceProfileBindingAssessment.BindingInvalidated when result.Reasons.Any(r => r.Contains("WORKER_DISCONNECTED", StringComparison.Ordinal)) => Localization.Get("VmBindingWorkerDisconnected"),
        DeviceProfileBindingAssessment.BindingInvalidated => Localization.Get("VmBindingInvalidated"),
        _ => Localization.Get("VmBindingNotRun")
    };

    private void RefreshSavedProfiles()
    {
        var previousPath = SelectedSavedDeviceProfile?.Path;
        _savedDeviceProfiles = DeviceConfigurationProfileStorage.DiscoverLocal(_profileDirectory);
        Changed(nameof(SavedDeviceProfiles));
        if (previousPath is not null)
        {
            var replacement = _savedDeviceProfiles.FirstOrDefault(p => p.Path == previousPath);
            if (replacement is null || replacement.Status != "AVAILABLE" || !ReferenceEquals(SelectedSavedDeviceProfile?.Profile, replacement.Profile))
                SelectedSavedDeviceProfile = replacement;
        }
        Changed(nameof(SavedProfileBindingStatus)); Changed(nameof(SavedProfileHistoricalSummary));
        CheckSavedProfileBindingCommand.Refresh();
    }

    private string RecoveryStorageRoot => _profileDirectory is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe")
        : Directory.GetParent(Path.GetFullPath(_profileDirectory))?.FullName ?? Path.GetFullPath(_profileDirectory);
    private string RecoveryScansDirectory => Path.Combine(RecoveryStorageRoot, "Scans");
    private string RecoveryProfilesDirectory => _profileDirectory ?? Path.Combine(RecoveryStorageRoot, "Profiles");
    private string RecoveryEvidenceDirectory => Path.Combine(RecoveryStorageRoot, "SustainedDuplex");

    private void RefreshHistoricalSustainedScans()
    {
        InvalidateHistoricalRecovery(null);
        _historicalSustainedScans = HistoricalSustainedScanCatalog.Discover(RecoveryScansDirectory, RecoveryProfilesDirectory);
        Changed(nameof(HistoricalSustainedScans));
        if (_selectedHistoricalSustainedScan is not null)
            _selectedHistoricalSustainedScan = _historicalSustainedScans.FirstOrDefault(s => s.SummaryPath == _selectedHistoricalSustainedScan.SummaryPath);
        Changed(nameof(SelectedHistoricalSustainedScan));
    }

    private bool CanPreviewHistoricalSustainedRecovery() => _selectedHistoricalSustainedScan is { IsAvailable: true } && !_historicalRecoveryRunning && !_historicalRecoverySaving;
    private bool CanSaveHistoricalSustainedRecovery() => _historicalRecoveryPreview is not null && _historicalRecoveryConfirmationChecked && !_historicalRecoveryRunning && !_historicalRecoverySaving;

    private Task PreviewHistoricalSustainedRecoveryAsync()
    {
        if (!CanPreviewHistoricalSustainedRecovery() || _selectedHistoricalSustainedScan is not { } selected) return Task.CompletedTask;
        InvalidateHistoricalRecovery("Проверка старых измерений выполняется…");
        var version = _historicalRecoveryVersion;
        _historicalRecoveryRunning = true; Changed(nameof(HistoricalRecoveryRunning)); RefreshHistoricalRecoveryCommands();
        try
        {
            var preview = OfflineSustainedProfileRecovery.Preview(selected.SessionId, RecoveryScansDirectory, RecoveryProfilesDirectory, RecoveryEvidenceDirectory);
            if (version != _historicalRecoveryVersion) return Task.CompletedTask;
            _historicalRecoveryPreview = preview;
            _historicalRecoveryStatus = preview.Recommendation.Preferred is null ? "Подтверждённая конфигурация не найдена." : "Старые измерения подтверждены текущими правилами. Просмотрите результат и подтвердите сохранение.";
            ChangedHistoricalRecovery();
        }
        catch (Exception ex) { if (version == _historicalRecoveryVersion) { _historicalRecoveryStatus = "Проверка старых измерений отклонена. См. технические сведения."; _historicalRecoveryTechnicalError = $"{ex.GetType().Name}: {ex.Message}"; Changed(nameof(HistoricalRecoveryStatus)); Changed(nameof(HistoricalRecoveryTechnicalDetails)); } }
        finally { _historicalRecoveryRunning = false; Changed(nameof(HistoricalRecoveryRunning)); RefreshHistoricalRecoveryCommands(); }
        return Task.CompletedTask;
    }

    private Task SaveHistoricalSustainedRecoveryAsync()
    {
        if (!CanSaveHistoricalSustainedRecovery() || _historicalRecoveryPreview is not { } preview) return Task.CompletedTask;
        _historicalRecoverySaving = true; Changed(nameof(HistoricalRecoverySaving)); RefreshHistoricalRecoveryCommands();
        try
        {
            var saved = OfflineSustainedProfileRecovery.Save(preview);
            _historicalRecoveryStatus = $"Создан новый v4 профиль: {saved.Profile.ProfileId:D} · {saved.Profile.CreatedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}.";
            _historicalRecoveryPreview = null; _historicalRecoveryConfirmationChecked = false; _historicalRecoveryVersion++;
            ChangedHistoricalRecovery();
            RefreshSavedProfiles();
            RefreshHistoricalSustainedScans();
        }
        catch (Exception ex) { _historicalRecoveryStatus = "Профиль не сохранён. См. технические сведения."; _historicalRecoveryTechnicalError = $"{ex.GetType().Name}: {ex.Message}"; _historicalRecoveryPreview = null; _historicalRecoveryConfirmationChecked = false; _historicalRecoveryVersion++; ChangedHistoricalRecovery(); }
        finally { _historicalRecoverySaving = false; Changed(nameof(HistoricalRecoverySaving)); RefreshHistoricalRecoveryCommands(); }
        return Task.CompletedTask;
    }

    private void CancelHistoricalSustainedRecovery() => InvalidateHistoricalRecovery("Проверка отменена.");
    private void InvalidateHistoricalRecovery(string? status)
    {
        _historicalRecoveryVersion++; _historicalRecoveryPreview = null; _historicalRecoveryConfirmationChecked = false;
        _historicalRecoveryTechnicalError = null;
        if (status is not null) _historicalRecoveryStatus = status;
        ChangedHistoricalRecovery(); RefreshHistoricalRecoveryCommands();
    }
    private void ChangedHistoricalRecovery()
    {
        Changed(nameof(HistoricalRecoveryStatus)); Changed(nameof(HistoricalRecoveryPreviewVisible)); Changed(nameof(HistoricalRecoveryConfirmationChecked));
        Changed(nameof(HistoricalRecoveryPreviewDevice)); Changed(nameof(HistoricalRecoveryPreviewDates)); Changed(nameof(HistoricalRecoveryPreferred)); Changed(nameof(HistoricalRecoveryFallback)); Changed(nameof(HistoricalRecoveryConfidence)); Changed(nameof(HistoricalRecoveryTechnicalDetails));
    }
    private void RefreshHistoricalRecoveryCommands()
    {
        PreviewHistoricalSustainedRecoveryCommand?.Refresh(); SaveHistoricalSustainedRecoveryCommand?.Refresh(); CancelHistoricalSustainedRecoveryCommand?.Refresh();
    }

    private Task RefreshSavedProfilesAsync()
    {
        RefreshSavedProfiles();
        return Task.CompletedTask;
    }

    private bool CanCheckSavedProfileBinding() => _worker is not null && (WorkerState.State == WorkerConnectionState.ConnectedFake || WorkerState.State == WorkerConnectionState.Disconnected && !_workerAttempted) && !_bindingCheckRunning && !_windowsDiscoveryLoading && !AutomaticScanBusy && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending;

    private void InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason reason)
    {
        _bindingCheckVersion++;
        if (_savedProfileBindingResult is not null || _savedProfileBindingState.Current is not null)
            _savedProfileBindingResult = _savedProfileBindingState.Invalidate(reason);
        (_savedProfileOperatorStatus, _savedProfileOperatorHistory, _savedProfileOperatorLimitation) = reason switch
        {
            DeviceProfileBindingInvalidationReason.WorkerDisconnected or DeviceProfileBindingInvalidationReason.WorkerRestarted or DeviceProfileBindingInvalidationReason.WorkerReplaced => (Localization.Get("VmBindingInvalidWorker"), Localization.Get("VmBindingAvailabilityUnknown"), Localization.Get("VmBindingReconnectWorker")),
            DeviceProfileBindingInvalidationReason.ProfileSelectionChanged => (Localization.Get("VmBindingInvalidProfileSelection"), Localization.Get("VmBindingNotConfirmed"), Localization.Get("VmBindingRetrySelectedProfile")),
            DeviceProfileBindingInvalidationReason.EndpointRediscovered => (Localization.Get("VmBindingRediscovery"), Localization.Get("VmBindingPriorDiscoveryStale"), Localization.Get("VmBindingWaitForDiscovery")),
            _ => (Localization.Get("VmBindingStaleDeviceList"), Localization.Get("VmBindingNotConfirmed"), Localization.Get("VmBindingRetryAfterRefresh"))
        };
        Changed(nameof(SavedProfileBindingStatus)); Changed(nameof(SavedProfileBindingExplanation)); Changed(nameof(SavedProfileBindingTechnicalDetails)); Changed(nameof(HasSavedProfileBindingAssessment));
        Changed(nameof(SavedProfileOperatorStatus)); Changed(nameof(SavedProfileOperatorHistory)); Changed(nameof(SavedProfileOperatorLimitation));
        CheckSavedProfileBindingCommand?.Refresh();
    }

    private async Task CheckSavedProfileBindingAsync()
    {
        if (!CanCheckSavedProfileBinding() || _worker is null) return;
        var requestVersion = ++_bindingCheckVersion;
        _bindingCheckRunning = true;
        Changed(nameof(SavedProfileBindingChecking)); CheckSavedProfileBindingCommand.Refresh();
        try
        {
            if (WorkerState.State == WorkerConnectionState.Disconnected && !_workerAttempted)
            {
                _workerAttempted = true;
                _bindingCheckExpectedInitialConnection = true;
                await _worker.ConnectAsync();
                ApplyWorkerState(_worker.State);
            }
            if (WorkerState.State != WorkerConnectionState.ConnectedFake)
            {
                InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason.WorkerDisconnected);
                return;
            }
            var workerProcessId = WorkerState.ProcessId;
            var selection = new AutomaticScanSelection(SelectedWindowsEndpoint?.EndpointId, AutomaticScanPairEnabled, SelectedDuplexCaptureEndpoint?.EndpointId, SelectedDuplexRenderEndpoint?.EndpointId, SelectedPhysicalDeviceGroup?.GroupId, SelectedPhysicalDeviceGroup?.CanAutoSelectPair == true, AllowUnconfirmedPhysicalPair, false);
            var discovery = _automaticScanWorkflow is not null
                ? await _automaticScanWorkflow.RefreshEndpointsAsync(CancellationToken.None)
                : await _worker.ListWindowsEndpointsAsync();
            if (requestVersion != _bindingCheckVersion || WorkerState.State != WorkerConnectionState.ConnectedFake || WorkerState.ProcessId != workerProcessId)
            {
                InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason.DiscoverySnapshotStale);
                return;
            }
            if (discovery.Provenance != "REAL_WINDOWS_METADATA")
            {
                InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason.DiscoverySnapshotStale);
                _savedProfileOperatorStatus = Localization.Get("VmBindingFreshMetadataRequired");
                _savedProfileOperatorHistory = Localization.Get("VmBindingNotConfirmed");
                return;
            }
            ApplyAutomaticDiscovery(discovery, selection);
            RefreshSavedProfiles();
            var generation = _windowsDiscoveryGeneration ??= Guid.NewGuid().ToString("D");
            if (discovery.OperationState == "PARTIAL")
            {
                _matchingSavedProfiles = [];
                _savedProfileOperatorStatus = Localization.Get("VmBindingDiscoveryIncomplete");
                _savedProfileOperatorHistory = Localization.Get("VmBindingMissingEndpointUnknown");
                _savedProfileOperatorLimitation = Localization.Get("VmBindingRetryFullMetadataDiscovery");
                Changed(nameof(SavedProfileMatchingSummary));
                return;
            }
            var validProfiles = _savedDeviceProfiles.Where(p => p.Profile is not null && p.Status == "AVAILABLE").ToArray();
            var v4Matches = validProfiles.Where(p => p.Profile!.SchemaVersion == 4 && p.Profile.SustainedSelection is not null && DeviceProfileBindingRevalidation.Evaluate(p.Profile, discovery, generation, p.Path).Overall == DeviceProfileBindingAssessment.ExactBindingsPresentInDiscovery).ToArray();
            var legacyMatches = validProfiles.Where(p => p.Profile!.SchemaVersion is >= 1 and <= 3 && DeviceProfileBindingRevalidation.Evaluate(p.Profile, discovery, generation, p.Path).Overall == DeviceProfileBindingAssessment.ExactBindingsPresentInDiscovery).ToArray();
            _matchingSavedProfiles = v4Matches;
            Changed(nameof(SavedProfileMatchingSummary));
            if (v4Matches.Length == 1)
            {
                SelectedSavedDeviceProfile = v4Matches[0];
                _savedProfileBindingResult = _savedProfileBindingState.Evaluate(v4Matches[0].Profile!, discovery, generation, v4Matches[0].Path);
                _savedProfileOperatorStatus = "Пара устройств найдена. Аудиоконфигурация требует повторной проверки.";
                _savedProfileOperatorHistory = HistoricalOperatorText(v4Matches[0].Profile!);
                _savedProfileOperatorLimitation = "Совпали точные Capture/Render ID. Стабильность, Exclusive-период, драйвер/firmware, AudioHost и физический RTL повторно не подтверждены.";
            }
            else if (v4Matches.Length > 1)
            {
                SelectedSavedDeviceProfile = null;
                _savedProfileBindingResult = _savedProfileBindingState.Invalidate(DeviceProfileBindingInvalidationReason.ProfileSelectionChanged);
                _savedProfileOperatorStatus = "Найдено несколько сохранённых профилей для этой пары устройств. Выбор неоднозначен.";
                _savedProfileOperatorHistory = string.Join("\n", v4Matches.Select(p => $"{p.OperatorDisplayName} · {p.OperatorTimestampLabel} · {p.OperatorResult}"));
                _savedProfileOperatorLimitation = "Профиль не выбран автоматически; откройте расширенную диагностику для выбора.";
            }
            else if (legacyMatches.Length > 0)
            {
                SelectedSavedDeviceProfile = null;
                _savedProfileOperatorStatus = "Найдены предыдущие измерения. Подтверждённого длительного профиля пока нет.";
                _savedProfileOperatorHistory = "Короткие результаты не переинтерпретируются как sustained-проверка.";
                _savedProfileOperatorLimitation = "Для v4 sustained-профиля нужна отдельная валидированная длительная проверка.";
            }
            else if (_savedDeviceProfiles.Any(p => !p.IsAvailable))
            {
                SelectedSavedDeviceProfile = null;
                _savedProfileOperatorStatus = "Сохранённый профиль повреждён или не поддерживается.";
                _savedProfileOperatorHistory = "Доступный профиль для этой пары устройств не найден.";
                _savedProfileOperatorLimitation = "Обновите локальные профили и повторите проверку.";
            }
            else
            {
                SelectedSavedDeviceProfile = null;
                _savedProfileOperatorStatus = Localization.Get("VmBindingNoSavedProfile");
                _savedProfileOperatorHistory = Localization.Get("VmBindingNoCompatibilityConclusion");
                _savedProfileOperatorLimitation = Localization.Get("VmBindingExactIdOnly");
            }
        }
        catch (Exception ex)
        {
            InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason.DiscoverySnapshotStale);
            _savedProfileOperatorStatus = $"Проверка не завершена: {ex.GetType().Name}.";
            _savedProfileOperatorHistory = "Текущий результат не подтверждён.";
            _savedProfileOperatorLimitation = "Повторите проверку после обновления Worker и списка устройств.";
        }
        finally
        {
            _bindingCheckRunning = false;
            Changed(nameof(SavedProfileBindingChecking)); Changed(nameof(SavedProfileBindingStatus)); Changed(nameof(SavedProfileBindingExplanation)); Changed(nameof(SavedProfileBindingTechnicalDetails)); Changed(nameof(HasSavedProfileBindingAssessment)); Changed(nameof(SavedProfileOperatorStatus)); Changed(nameof(SavedProfileOperatorHistory)); Changed(nameof(SavedProfileOperatorLimitation));
            CheckSavedProfileBindingCommand.Refresh();
        }
    }

    private static string HistoricalOperatorText(DeviceConfigurationProfile profile)
    {
        if (profile.SchemaVersion != 4 || profile.SustainedSelection is not { } sustained) return "Исторический длительный раздел отсутствует.";
        var primary = sustained.Preferred is { } p ? $"Primary: {p.Period} · MMCSS {p.Mmcss} · {p.DurationSeconds} с · {p.Confidence}" : "Primary: NONE";
        var fallback = sustained.Fallback is { } f ? $"Fallback: {f.Period} · {f.DurationSeconds} с" : "Fallback: NONE";
        return $"{primary}\n{fallback}\nAudioHost: NOT_CERTIFIED · Physical RTL: NOT_MEASURED";
    }

    private async Task DiscoverWindowsEndpointsAsync()
    {
        if (!CanDiscoverWindowsEndpoints() || _worker is null) return;
        InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason.EndpointRediscovered);
        var selection = new AutomaticScanSelection(SelectedWindowsEndpoint?.EndpointId, AutomaticScanPairEnabled,
            AutomaticScanPairEnabled ? SelectedDuplexCaptureEndpoint?.EndpointId : null,
            AutomaticScanPairEnabled ? SelectedDuplexRenderEndpoint?.EndpointId : null,
            SelectedPhysicalDeviceGroup?.GroupId, SelectedPhysicalDeviceGroup?.CanAutoSelectPair == true, AllowUnconfirmedPhysicalPair, AutomaticSustainedMode.StartsWith("Extended", StringComparison.Ordinal));
        if (RealFormatMatrix is not null) _realQueryInvalidated = true;
        CancelDuplexRepeatabilityAuthorization(Localization.Get("VmRepeatabilityDiscoveryUpdating"));
        CancelPeriodDiscovery(Localization.Get("VmPeriodPlanDiscardedDiscoveryUpdating"));
        _windowsDiscoveryLoading = true;_windowsDiscoveryCurrent = false;_windowsDiscoveryError = null;
        _physicalDeviceGroups = [];
        Changed(nameof(PhysicalDeviceGroups)); Changed(nameof(PhysicalDeviceGroupDetails));
        Changed(nameof(DuplexWorkerAvailability)); Changed(nameof(DuplexDiscoveryFreshness)); Changed(nameof(DuplexEvidenceFreshness));
        Changed(nameof(WindowsDiscoveryLoading));Changed(nameof(WindowsDiscoveryStatusText));Changed(nameof(WindowsEndpoints));Changed(nameof(DuplexCaptureEndpoints));Changed(nameof(DuplexRenderEndpoints));Changed(nameof(HasCurrentWindowsEndpoints));Changed(nameof(HasHistoricalWindowsEndpoints));Changed(nameof(DeviceScanAvailabilityReason));Changed(nameof(RealFormatMatrixIsStale));Changed(nameof(RealFormatMatrixStatus));Changed(nameof(HasCurrentOperatorQueryResult));Changed(nameof(NeedsInitializeFormatQuery));Changed(nameof(CanSaveDeviceProfile));RequestRealInitializeCommand.Refresh();RequestDeviceScanCommand.Refresh();
        RefreshWindowsEndpointBrowser();
        DiscoverWindowsEndpointsCommand.Refresh();
        try
        {
            if (WorkerState.State == WorkerConnectionState.Disconnected)
            {
                _workerAttempted = true;
                await _worker.ConnectAsync();
                ApplyWorkerState(_worker.State);
                if (WorkerState.State != WorkerConnectionState.ConnectedFake) throw new IOException($"{Localization.Get("VmDiscoveryWorkerDisconnected")}: {WorkerState.Message}");
            }
            var discovery = _automaticScanWorkflow is not null
                ? await _automaticScanWorkflow.RefreshEndpointsAsync(CancellationToken.None)
                : await _worker.ListWindowsEndpointsAsync();
            WindowsEndpointDiscoveryValidation.Validate(discovery);
            ApplyAutomaticDiscovery(discovery, selection);
            WorkerNotice=string.Format(Localization.CurrentCulture, Localization.Get("VmDiscoverySuccess"), discovery.Endpoints.Count);
        }
        catch(Exception ex)
        {
            InvalidateAutomaticDeviceSelection();
            _windowsDiscoveryError=ex is WorkerCommandException command ? $"{command.Command}/{command.ErrorCode}" : ex.GetType().Name;
            WorkerNotice=string.Format(Localization.CurrentCulture, Localization.Get("VmDiscoveryFailure"), _windowsDiscoveryError);
        }
        finally
        {
            _windowsDiscoveryLoading=false;
            Changed(nameof(WindowsDiscoveryLoading));Changed(nameof(WindowsDiscoveryStatusText));Changed(nameof(WindowsEndpoints));Changed(nameof(DuplexCaptureEndpoints));Changed(nameof(DuplexRenderEndpoints));Changed(nameof(LastWindowsEndpoints));Changed(nameof(HasCurrentWindowsEndpoints));Changed(nameof(HasHistoricalWindowsEndpoints));
            Changed(nameof(CanUseSelectedWindowsEndpoint));Changed(nameof(DeviceScanAvailabilityReason));Changed(nameof(AutomaticScanAvailabilityReason));Changed(nameof(RealFormatMatrixIsStale));Changed(nameof(RealFormatMatrixStatus));Changed(nameof(HasCurrentOperatorQueryResult));Changed(nameof(NeedsInitializeFormatQuery));Changed(nameof(RealInitializeDisabledReason));Changed(nameof(CanSaveDeviceProfile));Changed(nameof(RealFormatSummary));Changed(nameof(RealFormatEndpointLabel));Changed(nameof(RealInitializationDetails));Changed(nameof(RealInitializationSummary));Changed(nameof(DuplexWorkerAvailability));Changed(nameof(DuplexDiscoveryFreshness));Changed(nameof(DuplexEvidenceFreshness));QueryRealFormatsCommand.Refresh();RequestRealInitializeCommand.Refresh();RequestDeviceScanCommand.Refresh();PrepareAutomaticScanCommand.Refresh();
            RequestDuplexMinimumCommand.Refresh(); RequestDuplexDefaultCommand.Refresh(); Changed(nameof(HasCurrentDuplexPair)); Changed(nameof(CanSaveDeviceProfile));
            RefreshWindowsEndpointBrowser();
            DiscoverWindowsEndpointsCommand.Refresh();
            RefreshCanExecute();
        }
    }

    private void RefreshWindowsEndpointBrowser()
    {
        if (_selectedWindowsEndpoint is not null && !WindowsEndpointRows.Contains(_selectedWindowsEndpoint))
        {
            Set(ref _selectedWindowsEndpoint, null, nameof(SelectedWindowsEndpoint));
            Changed(nameof(HasSelectedWindowsEndpoint));
            Changed(nameof(WindowsEndpointInspectorFields));
            RefreshContext();
        }
        Changed(nameof(WindowsEndpointRows));
        Changed(nameof(WindowsEndpointTotalCount));
        Changed(nameof(WindowsEndpointDisplayedCount));
        Changed(nameof(WindowsEndpointActiveCaptureCount));
        Changed(nameof(WindowsEndpointActiveRenderCount));
        Changed(nameof(WindowsEndpointCountsText));
    }

    private static IReadOnlyList<InspectorFieldDto> WindowsEndpointDetails(WindowsEndpointDto endpoint)
    {
        var format = endpoint.SharedModeFormat;
        static string Number(int? value, string unit = "") => value is int number ? $"{number}{unit}" : Localization.Get("VmInspectorUnknown");
        static string Rate(uint? value) => value is uint rate ? $"{rate} {Localization.Get("VmInspectorHertz")}" : Localization.Get("VmInspectorUnknown");
        static string Hresult(WindowsPropertyEvidenceDto evidence) => $"{evidence.State} · HRESULT 0x{unchecked((uint)evidence.Hresult):X8}";
        var mask = format.SpeakerMask is uint speakerMask ? $"0x{speakerMask:X8}" : Localization.Get("VmInspectorUnknown");
        return [
            new(Localization.Get("VmInspectorFriendlyName"), endpoint.FriendlyNameDisplay),
            new(Localization.Get("VmInspectorDirection"), $"{endpoint.DirectionLabel} · {endpoint.DeviceStateLabel}"),
            new(Localization.Get("VmInspectorFormFactor"), endpoint.FormFactorLabel, Hresult(endpoint.FormFactorEvidence)),
            new(Localization.Get("VmInspectorSharedMode"), endpoint.SharedModeFormatSummary, $"{format.ParseState} · {Hresult(format.Property)}"),
            new(Localization.Get("VmInspectorSampleRate"), Rate(format.SampleRate)),
            new(Localization.Get("VmInspectorContainerBits"), $"{Number(format.BitsPerSample, Localization.Get("VmInspectorBits"))} / {Number(format.ValidBitsPerSample, Localization.Get("VmInspectorBits"))}"),
            new(Localization.Get("VmInspectorReportedChannels"), format.ReportedChannelCount is int channels ? $"{channels}" : Localization.Get("VmInspectorUnknown")),
            new(Localization.Get("VmInspectorTopology"), format.TopologyState),
            new(Localization.Get("VmInspectorSpeakerMask"), $"{mask} · {format.SpeakerMaskState}", Hresult(format.Property)),
            new(Localization.Get("VmInspectorPropertyAvailability"), string.Format(Localization.CurrentCulture, Localization.Get("VmInspectorPropertyAvailabilityDetails"), Localization.Get("VmInspectorId"), Hresult(endpoint.EndpointIdEvidence), Localization.Get("VmInspectorName"), Hresult(endpoint.FriendlyNameEvidence), Localization.Get("VmInspectorState"), Hresult(endpoint.DeviceStateEvidence), Localization.Get("VmInspectorFormat"), Hresult(format.Property))),
            new(Localization.Get("VmInspectorPhysicalMatch"), "UNKNOWN"),
            new(Localization.Get("VmInspectorExclusiveCapability"), "NOT TESTED")
        ];
    }

    private bool CanRequestWorkerCapture() => CanRequestWorker("CAPTURE");
    private bool CanRequestWorkerRender() => CanRequestWorker("RENDER");
    private bool CanRequestWorker(string direction) => _worker is not null && WorkerState.State == WorkerConnectionState.ConnectedFake && !WorkerConsentPending && _workerPendingSettings is null && !_workerStartOutcomeUnknown && !WorkerOperationActive && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending && SelectedWorkerEndpoint is { IsSynthetic: true } endpoint && endpoint.Direction == direction && SelectedWorkerFormat is not null && SelectedWorkerFormat.Topology.ChannelCount is >= 1 and <= 64 && DurationOptions.Any(o => o.Value == WorkerDurationSeconds) && (direction != "RENDER" || WorkerRenderChannel is >= 1 && WorkerRenderChannel <= WorkerChannelCount);

    private void RequestWorkerSession(string direction)
    {
        if (!CanRequestWorker(direction) || SelectedWorkerEndpoint is null || SelectedWorkerFormat is null) return;
        var format = SelectedWorkerFormat.FormatId;
        var encoding = format switch { "PCM16-48000" => "PCM16", "PCM24-48000" => "PCM24_IN_32", "FLOAT32-48000" => "FLOAT32", _ => throw new InvalidOperationException("Unsupported worker fake format.") };
        _workerPendingSettings = new(SelectedWorkerEndpoint.EndpointId, direction, format, 48000, encoding, SelectedWorkerFormat.Topology.ChannelCount, format, WorkerDurationSeconds, WorkerPeriod, WorkerMmcss, direction == "RENDER" ? WorkerRenderChannel : null, WorkerScenario);
        Changed(nameof(WorkerConsentText));
        WorkerConsentChecked = false;
        WorkerConsentPending = true;
        WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerConfirmationRequested"), SelectedWorkerEndpoint.Name, format, WorkerChannelCount);
        AddWorkerTimeline(Guid.Empty, "SessionRequested", "REQUESTED", "UNKNOWN", "NOT_ATTEMPTED", "Запрос сформирован в GUI; соединение использует только fake service.");
        Changed(nameof(WorkerPendingSettings));
    }

    private async Task ConfirmWorkerSessionAsync()
    {
        if (_worker is null || !WorkerConsentPending || !WorkerConsentChecked || _workerPendingSettings is not { } settings) return;
        var receipt = new ConsentReceiptDto(true, Guid.NewGuid(), WorkerConsent.Fingerprint(settings));
        Interlocked.Exchange(ref _lateWorkerStartAccepted, 0);
        Interlocked.Exchange(ref _lateWorkerStartRejected, 0);
        WorkerConsentPending = false;
        WorkerConsentChecked = false;
        AddWorkerTimeline(Guid.Empty, "ConsentAccepted", "CONSENTED", "UNKNOWN", "NOT_ATTEMPTED", "Одноразовое подтверждение сформировано и привязано к отпечатку настроек.");
        try
        {
            ApplyWorkerSessionAccepted(await _worker.StartSessionAsync(settings, receipt));
        }
        catch (WorkerCommandException ex) { _workerStartOutcomeUnknown = false; WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerStartRejected"), ex.ErrorCode, ex.Message); AddWorkerTimeline(Guid.Empty, "Rejected", "FAILED", "UNKNOWN", "NOT_ATTEMPTED", $"{ex.ErrorCode}: {ex.Message}"); }
        catch (Exception ex) { _workerStartOutcomeUnknown = Interlocked.CompareExchange(ref _lateWorkerStartAccepted, 0, 0) == 0 && Interlocked.CompareExchange(ref _lateWorkerStartRejected, 0, 0) == 0; WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerStartOutcomeUnknown"), ex.GetType().Name, ex.Message); }
        finally { _workerPendingSettings = null; Changed(nameof(WorkerPendingSettings)); Changed(nameof(WorkerConsentText)); RefreshWorkerCommands(); }
    }

    private void CancelWorkerConsent()
    {
        if (!WorkerConsentPending) return;
        WorkerConsentPending = false; WorkerConsentChecked = false; _workerPendingSettings = null; Changed(nameof(WorkerConsentText));
        AddWorkerTimeline(Guid.Empty, "ConsentCancelled", "CANCELLED", "UNKNOWN", "NOT_ATTEMPTED", "Подтверждение отменено; команда Start не отправлялась.");
        WorkerNotice = Localization.Get("VmWorkerConsentCancelled"); Changed(nameof(WorkerPendingSettings)); RefreshWorkerCommands();
    }

    private async Task StopWorkerSessionAsync()
    {
        if (_worker is null || WorkerSession is not { Lifecycle: SessionLifecycle.Preparing or SessionLifecycle.Ready or SessionLifecycle.Started } session) return;
        await _worker.StopSessionAsync(session.Id);
        if (WorkerSession?.Lifecycle is SessionLifecycle.Preparing or SessionLifecycle.Ready or SessionLifecycle.Started) WorkerSession = WorkerSession with { Lifecycle = SessionLifecycle.Stopping, Message = "Worker принял Stop; terminal event ещё ожидается." };
        WorkerNotice = Localization.Get("VmWorkerStopAccepted");
        RefreshWorkerCommands();
    }

    public async Task ShutdownWorkerAsync()
    {
        if (_worker is null) return;
        try
        {
            if (AutomaticScanConsentPending) CancelAutomaticScanConsent();
            if (_automaticScanRunning)
            {
                CancelAutomaticScan();
                if (_automaticScanTask is { } scanTask)
                    try { await scanTask; } catch { _automaticScanOutcomeUnknown = true; }
            }
            if (WorkerOperationActive && WorkerSession is { } active)
            {
                try
                {
                    if (active.Lifecycle is SessionLifecycle.Preparing or SessionLifecycle.Ready or SessionLifecycle.Started) await _worker.StopSessionAsync(active.Id);
                    await (_workerTerminal ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    WorkerSession = active with { Lifecycle = SessionLifecycle.Unknown, Quality = StreamQuality.Unknown, CleanupStatus = null, Message = "Окно закрывается; terminal cleanup не наблюдался." };
                    AddWorkerTimeline(active.Id, "WorkerDisconnected", "UNKNOWN", "UNKNOWN", "UNKNOWN", "Остановка при закрытии окна не подтверждена.");
                    await _worker.DisposeAsync();
                    return;
                }
            }
            await _worker.ShutdownAsync();
        }
        finally { await _worker.DisposeAsync(); }
    }

    private void WorkerStateChanged(WorkerStateDto state)
    {
        if (_uiContext is null) { try { ApplyWorkerState(state, true); } catch (Exception ex) { _workerNotice = $"Ошибка доставки состояния в UI: {ex.Message}"; } }
        else _uiContext.Post(_ => { try { ApplyWorkerState(state, true); } catch (Exception ex) { _workerNotice = $"Ошибка доставки состояния в UI: {ex.Message}"; } }, null);
    }

    private void WorkerSessionAccepted(WorkerSessionAcceptedDto accepted)
    {
        Interlocked.Exchange(ref _lateWorkerStartAccepted, 1);
        if (_uiContext is null) { try { ApplyWorkerSessionAccepted(accepted); } catch (Exception ex) { _workerNotice = $"Ошибка доставки позднего ответа в UI: {ex.Message}"; } }
        else _uiContext.Post(_ => { try { ApplyWorkerSessionAccepted(accepted); } catch (Exception ex) { _workerNotice = $"Ошибка доставки позднего ответа в UI: {ex.Message}"; } }, null);
    }

    private void ApplyWorkerSessionAccepted(WorkerSessionAcceptedDto accepted)
    {
        if (WorkerSession?.Id == accepted.SessionId) return;
        var settings = accepted.Settings;
        _workerStartOutcomeUnknown = false;
        var endpointName = WorkerEndpoints.FirstOrDefault(e => e.EndpointId == settings.EndpointId)?.Name ?? settings.EndpointId;
        WorkerSessionSettings = settings;
        _workerTerminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkerSession = new(accepted.SessionId, settings.Direction == "CAPTURE" ? SessionMode.Capture : SessionMode.Render,
            endpointName, settings.RenderChannel, settings.DurationSeconds,
            SessionLifecycle.Preparing, StreamQuality.Unknown, "Синтетическая операция принята worker.", "NOT_ATTEMPTED", true, settings.ChannelCount);
        WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerSettingsAccepted"), accepted.SessionId);
        AddWorkerTimeline(accepted.SessionId, "Accepted", "PREPARING", "UNKNOWN", "NOT_ATTEMPTED", $"Start response correlated; worker operation state {accepted.OperationState}.", null, accepted.CorrelationId);
        var early = _earlyWorkerEvents.Where(e => e.SessionId == accepted.SessionId).ToArray();
        _earlyWorkerEvents.RemoveAll(e => e.SessionId == accepted.SessionId);
        foreach (var evt in early) ApplyWorkerEvent(evt);
    }

    private void ApplyWorkerState(WorkerStateDto state, bool callbackDelivery = false)
    {
        if (_bindingCheckExpectedInitialConnection && state.State is WorkerConnectionState.Starting or WorkerConnectionState.Connecting or WorkerConnectionState.Handshaking)
            return;
        if (state.State == WorkerConnectionState.ConnectedFake && _bindingWorkerProcessId is not null && state.ProcessId != _bindingWorkerProcessId)
            InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason.WorkerReplaced);
        else if (state.State != WorkerConnectionState.ConnectedFake)
            InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason.WorkerDisconnected);
        if (_bindingCheckExpectedInitialConnection && (callbackDelivery || state.State != WorkerConnectionState.ConnectedFake))
            _bindingCheckExpectedInitialConnection = false;
        _bindingWorkerProcessId = state.ProcessId;
        WorkerState = state;
        Changed(nameof(WorkerState));
        if (state.State != WorkerConnectionState.ConnectedFake || _duplexRepeatabilityPreparation?.WorkerProcessId != state.ProcessId)
            CancelDuplexRepeatabilityAuthorization(Localization.Get("VmRepeatabilityWorkerChanged"));
        if (state.State != WorkerConnectionState.ConnectedFake)
            CancelPeriodDiscovery(Localization.Get("VmPeriodPlanDiscardedWorkerChanged"));
        if (state.State != WorkerConnectionState.ConnectedFake)
        {
            if (_automaticScanRunning) _automaticScanCancellation?.Cancel();
            if (AutomaticScanConsentPending) CancelAutomaticScanConsent();
            if (DeviceScanConsentPending) CancelDeviceScanConsent();
            if (_streamConsentPending) CancelStreamConsent();
            if (DuplexConsentPending) CancelDuplexConsent();
            if (_deviceScanRunning) CancelDeviceScan();
            if (_streamValidationRunning) _streamValidationCancellation?.Cancel();
            if (_duplexValidationRunning) { _duplexStatus = "UNKNOWN · worker отключён во время Duplex; обе стороны и cleanup неизвестны."; Changed(nameof(DuplexStatus)); }
            else if (HasDuplexResults) { _duplexStatus = "Сохранены последние принятые Duplex результаты; после отключения они исторические. Очистка и физическая маршрутизация из отключения не выводятся."; Changed(nameof(DuplexStatus)); }
        }
        Changed(nameof(CanSaveDeviceProfile));
        Changed(nameof(WorkerCurrentEndpointsVisible)); Changed(nameof(WorkerRestartExplanation)); Changed(nameof(HasHistoricalWorkerEndpoints)); Changed(nameof(HasHistoricalWindowsEndpoints));
        if(state.State!=WorkerConnectionState.ConnectedFake&&_windowsDiscoveryCurrent)
        {
            _windowsDiscoveryCurrent=false;
            Changed(nameof(WindowsEndpoints));Changed(nameof(HasCurrentWindowsEndpoints));Changed(nameof(HasHistoricalWindowsEndpoints));Changed(nameof(WindowsDiscoveryStatusText));
        }
        Changed(nameof(CanUseSelectedWindowsEndpoint));Changed(nameof(DeviceScanAvailabilityReason));Changed(nameof(RealFormatMatrixIsStale));Changed(nameof(RealFormatMatrixStatus));Changed(nameof(HasCurrentOperatorQueryResult));Changed(nameof(NeedsInitializeFormatQuery));Changed(nameof(RealInitializeDisabledReason));Changed(nameof(CanSaveDeviceProfile));Changed(nameof(RealFormatSummary));Changed(nameof(RealFormatEndpointLabel));Changed(nameof(RealInitializationDetails));Changed(nameof(RealInitializationSummary));Changed(nameof(StreamValidationAvailability));Changed(nameof(HasCurrentDuplexPair));Changed(nameof(DuplexCaptureEndpoints));Changed(nameof(DuplexRenderEndpoints));QueryRealFormatsCommand.Refresh();RequestRealInitializeCommand.Refresh();RequestDeviceScanCommand.Refresh();RequestStreamValidationCommand.Refresh();RequestDuplexMinimumCommand.Refresh();RequestDuplexDefaultCommand.Refresh();
        Changed(nameof(DuplexWorkerAvailability)); Changed(nameof(DuplexDiscoveryFreshness)); Changed(nameof(DuplexEvidenceFreshness)); Changed(nameof(HasDuplexPairProfileEvidence));
        RefreshWindowsEndpointBrowser();
        if (state.State == WorkerConnectionState.Disconnected)
        {
            _workerMeterStale = _workerCaptureMeter is not null || _workerRenderMeter is not null;
            Changed(nameof(WorkerMeterStatusText));
            if (WorkerEndpoints.Count > 0) LastWorkerEndpointSnapshot = WorkerEndpoints.ToArray();
            WorkerEndpoints = [];
            SelectedWorkerEndpoint = null;
            Changed(nameof(SelectedWorkerEndpoint)); Changed(nameof(WorkerFormatOptions)); Changed(nameof(WorkerChannelSummary));
            if (_workerAttempted) WorkerNotice = WorkerRestartExplanation;
        }
        ConnectWorkerCommand.Refresh();
        RequestDeviceScanCommand.Refresh();
        PrepareAutomaticScanCommand.Refresh();
        QueryWorkerEndpointsCommand.Refresh();
        DiscoverWindowsEndpointsCommand.Refresh();
        ShutdownWorkerCommand.Refresh();
        RefreshWorkerCommands();
        if (state.State == WorkerConnectionState.Disconnected && WorkerOperationActive && WorkerSession is { } active)
        {
            WorkerSession = active with { Lifecycle = SessionLifecycle.Unknown, Quality = StreamQuality.Unknown, CleanupStatus = null, Message = "Worker отключён; Stop и cleanup неизвестны." };
            _workerTerminal?.TrySetException(new IOException("Worker disconnected before terminal event."));
            AddWorkerTimeline(active.Id, "WorkerDisconnected", "UNKNOWN", "UNKNOWN", "UNKNOWN", state.Message);
        }
        else if (state.State == WorkerConnectionState.Disconnected && _workerStartOutcomeUnknown)
            AddWorkerTimeline(Guid.Empty, "WorkerDisconnected", "UNKNOWN", "UNKNOWN", "UNKNOWN", "Start outcome unknown; no response or terminal cleanup was observed.");
        RefreshCanExecute();
        RefreshContext();
    }

    private void WorkerEventReceived(WorkerEventDto evt)
    {
        if (_uiContext is null) { try { ReceiveWorkerEvent(evt); } catch (Exception ex) { _workerNotice = $"Ошибка worker event: {ex.Message}"; } }
        else
        {
            var post = false;
            lock (_workerUiSync)
            {
                if (_workerUiOverflowed) return;
                _workerUiEventBuffer.Enqueue(evt);
                if (!_workerUiDrainPosted) { _workerUiDrainPosted = true; post = true; }
            }
            if (post) try { _uiContext.Post(_ => DrainWorkerUiEvents(), null); }
            catch { lock (_workerUiSync) _workerUiDrainPosted = false; }
        }
    }

    private void DrainWorkerUiEvents()
    {
        WorkerUiEventBatch batch;
        lock (_workerUiSync) { batch = _workerUiEventBuffer.Drain(); _workerUiDrainPosted = false; }
        if (batch.Overflowed)
        {
            _workerUiOverflowed = true;
            if (WorkerSession is { } current && WorkerOperationActive)
                WorkerSession = current with { Lifecycle = SessionLifecycle.Unknown, Quality = StreamQuality.Unknown, CleanupStatus = null, Message = "Очередь UI переполнена; продолжение событий worker не подтверждено." };
            WorkerNotice = Localization.Get("VmWorkerEventQueueOverflow");
            AddWorkerTimeline(WorkerSession?.Id ?? Guid.Empty, "UI_EVENT_OVERFLOW", "UNKNOWN", "UNKNOWN", "UNKNOWN", "Ограниченная очередь управляющих событий исчерпана; lifecycle/cleanup не подтверждены.");
            return;
        }
        if (batch.CoalescedSnapshots > 0)
            AddWorkerTimeline(WorkerSession?.Id ?? Guid.Empty, "MeterSnapshotsCoalesced", WorkerSession?.Lifecycle.ToString().ToUpperInvariant() ?? "UNKNOWN", "UNKNOWN", "UNKNOWN", $"UI retained only the latest snapshot; {batch.CoalescedSnapshots} pending snapshots were coalesced.");
        foreach (var evt in batch.Events)
        {
            try { ReceiveWorkerEvent(evt); }
            catch (Exception ex) { WorkerNotice = $"Ошибка worker event: {ex.GetType().Name}: {ex.Message}"; }
        }
    }

    private void ReceiveWorkerEvent(WorkerEventDto evt)
    {
        if (WorkerSession?.Id != evt.SessionId)
        {
            if (_earlyWorkerEvents.Count < 32) _earlyWorkerEvents.Add(evt);
            else { _earlyWorkerEvents.RemoveAt(0); _earlyWorkerEvents.Add(evt); }
            return;
        }
        ApplyWorkerEvent(evt);
    }

    private void ApplyWorkerEvent(WorkerEventDto evt)
    {
        var payload = evt.Payload;
        if (evt.Name == "MeterSnapshot")
        {
            if (WorkerSession?.Lifecycle != SessionLifecycle.Started) return;
            if (evt.CaptureMeter is { } capture) _workerCaptureMeter = capture;
            else if (evt.RenderMeter is { } render) _workerRenderMeter = render;
            else throw new InvalidDataException("MeterSnapshot event has no validated typed Capture/Render DTO.");
            var channelCount = WorkerSession.Mode == SessionMode.Render ? _workerRenderMeter?.ChannelCount : _workerCaptureMeter?.ChannelCount;
            var bankCount = (channelCount.GetValueOrDefault() + 7) / 8;
            if (_workerMeterBank >= bankCount) _workerMeterBank = Math.Max(0, bankCount - 1);
            Changed(nameof(WorkerMeterBank)); Changed(nameof(WorkerMeterBanks)); Changed(nameof(WorkerVisibleMeterChannels)); Changed(nameof(WorkerMeterStatusText)); Changed(nameof(WorkerMeterSnapshotSummary)); Changed(nameof(WorkerMeterCoalescingSummary)); RefreshContext();
        }
        var lifecycleText = payload.GetProperty("lifecycle").GetString() ?? "UNKNOWN";
        var qualityText = payload.GetProperty("streamQuality").GetString() ?? "UNKNOWN";
        var cleanup = payload.GetProperty("cleanupEvidence").GetString() ?? "UNKNOWN";
        var lifecycle = evt.Name switch { "Preparing" => SessionLifecycle.Preparing, "Ready" => SessionLifecycle.Ready, "Started" => SessionLifecycle.Started, "Stopping" => SessionLifecycle.Stopping, "Completed" => SessionLifecycle.Completed, "Failed" => SessionLifecycle.Failed, _ => WorkerSession?.Lifecycle ?? SessionLifecycle.Unknown };
        var quality = qualityText switch { "CLEAN" => StreamQuality.Clean, "WARNING" => StreamQuality.Warning, "FAILED" => StreamQuality.Failed, _ => StreamQuality.Unknown };
        if (evt.Name == "Warning" && WorkerSession is { } current) lifecycle = current.Lifecycle;
        var detail = payload.TryGetProperty("failureCode", out var failure) ? failure.GetString() ?? evt.Name : payload.TryGetProperty("warningCode", out var warning) ? warning.GetString() ?? evt.Name : $"Синтетическое событие {evt.Name}.";
        WorkerSession = WorkerSession! with { Lifecycle = lifecycle, Quality = quality, CleanupStatus = cleanup, Message = detail };
        if (evt.Name is "Completed" or "Failed") { _workerMeterFinal = _workerCaptureMeter is not null || _workerRenderMeter is not null; Changed(nameof(WorkerMeterStatusText)); }
        AddWorkerTimeline(evt.SessionId, evt.Name, lifecycleText, qualityText, cleanup, detail, evt.EventSequence, evt.CorrelationId);
        if (evt.Name is "Completed" or "Failed") _workerTerminal?.TrySetResult();
        WorkerNotice = $"Событие {evt.Name} · IPC sequence {evt.EventSequence}.";
        RefreshWorkerCommands();
    }

    private void WorkerTransportFaulted(WorkerTransportFaultDto fault)
    {
        if (fault.Stage == "LATE_RESPONSE" && (fault.Command is "StartCaptureMeter" or "StartRenderChannelTest") && fault.ErrorCode is not null)
            Interlocked.Exchange(ref _lateWorkerStartRejected, 1);
        if (_uiContext is null) { try { ApplyWorkerTransportFault(fault); } catch { } }
        else _uiContext.Post(_ => { try { ApplyWorkerTransportFault(fault); } catch { } }, null);
    }

    private void ApplyWorkerTransportFault(WorkerTransportFaultDto fault)
    {
        WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerTransportFault"), fault.Stage, fault.Message);
        if ((fault.Stage is "RESPONSE_TIMEOUT" or "REQUEST_CANCELLED") && (fault.Command is "StartCaptureMeter" or "StartRenderChannelTest"))
        {
            _workerStartOutcomeUnknown = true;
            WorkerNotice = Localization.Get("VmWorkerStartOutcomePending");
            AddWorkerTimeline(Guid.Empty, "StartOutcomeUnknown", "UNKNOWN", "UNKNOWN", "UNKNOWN", fault.Message, null, fault.CorrelationId);
            RefreshWorkerCommands();
            return;
        }
        if (fault.Stage == "LATE_RESPONSE" && (fault.Command is "StartCaptureMeter" or "StartRenderChannelTest") && fault.ErrorCode is not null)
        {
            _workerStartOutcomeUnknown = false;
            WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerLateStartRejected"), fault.ErrorCode);
            AddWorkerTimeline(Guid.Empty, "Rejected", "FAILED", "UNKNOWN", "NOT_ATTEMPTED", fault.Message, null, fault.CorrelationId);
            RefreshWorkerCommands();
            return;
        }
        if ((fault.Stage is "PIPE_READ" or "PROCESS_EXIT" or "REQUEST_WRITE" or "CONNECT") && WorkerOperationActive && WorkerSession is { } active)
        {
            WorkerSession = active with { Lifecycle = SessionLifecycle.Unknown, Quality = StreamQuality.Unknown, CleanupStatus = null, Message = "Worker/pipe disconnected; Stop/cleanup unknown." };
            _workerTerminal?.TrySetException(new IOException(fault.Message));
            AddWorkerTimeline(active.Id, "WorkerDisconnected", "UNKNOWN", "UNKNOWN", "UNKNOWN", fault.Message);
        }
        else AddWorkerTimeline(WorkerSession?.Id ?? Guid.Empty, "TransportFault", "UNKNOWN", "UNKNOWN", "UNKNOWN", $"{fault.Stage}: {fault.Message}");
    }

    private void AddWorkerTimeline(Guid sessionId, string name, string lifecycle, string quality, string cleanup, string detail, long? protocolSequence = null, Guid? correlation = null)
    {
        WorkerTimeline.Add(new(++_workerTimelineSequence, DateTimeOffset.UtcNow, sessionId, name, lifecycle, quality, cleanup, detail, protocolSequence, correlation));
        while (WorkerTimeline.Count > EventHistoryLimit) WorkerTimeline.RemoveAt(0);
        Changed(nameof(WorkerEmptyTimelineText)); Changed(nameof(WorkerSelectedEventDetails));
        RefreshContext();
    }

    private void RefreshWorkerCommands()
    {
        RequestWorkerCaptureCommand?.Refresh(); RequestWorkerRenderCommand?.Refresh(); ConfirmWorkerSessionCommand?.Refresh(); CancelWorkerConsentCommand?.Refresh(); StopWorkerSessionCommand?.Refresh();
    }

    private void RefreshContext()
    {
        var windowsDeviceView = Screen == ProbeScreen.Devices && (!DeveloperToolsOpen || _windowsDiscoveryCurrent || _lastWindowsEndpointSnapshot is not null);
        InspectorProvenanceText = windowsDeviceView
            ? Localization.Get("VmInspectorWindowsProvenance")
            : Screen == ProbeScreen.Diagnostics ? Localization.Get("VmInspectorWorkerProvenance") : Localization.Get("VmInspectorDemoProvenance");
        switch (Screen)
        {
            case ProbeScreen.Devices:
                InspectorTitle = windowsDeviceView ? Localization.Get("VmInspectorWindowsDeviceTitle") : Localization.Get("VmInspectorDemoDeviceTitle");
                InspectorDetails = windowsDeviceView && SelectedWindowsEndpoint is { } windows
                    ? WindowsEndpointDetails(windows).Select(f => $"{f.Label}: {f.Value}").ToArray()
                    : windowsDeviceView
                    ? [Localization.Get("VmInspectorEndpointsNotRequested"), Localization.Get("VmInspectorConnectWorkerForDiscovery"), "Shared Mode metadata · Exclusive capability NOT TESTED"]
                    : SelectedEndpoint is { } e
                    ? [$"{e.Name}", $"{Localization.Get("VmInspectorDirectionLabel")}: {Localization.Get(Direction == SessionMode.Capture ? "VmCaptureInputDirection" : "VmRenderOutputDirection")}", $"{Localization.Get("VmInspectorStateLabel")}: {e.State}", $"{Localization.Get("VmInspectorMixerFormatLabel")}: {e.Format}", $"{Localization.Get("VmInspectorWindowsChannelsLabel")}: {e.Channels}", $"{Localization.Get("VmInspectorPhysicalConnectorsLabel")}: НЕИЗВЕСТНО"]
                    : [EmptyEndpointMessage, Localization.Get("VmInspectorPassiveDemoList")];
                break;
            case ProbeScreen.Formats:
                InspectorTitle = Localization.Get("VmInspectorFormatTitle");
                InspectorDetails = SelectedFormat is { } f
                    ? [$"{f.Encoding} · {f.SampleRate} Гц · {f.ChannelCount} кан.", .. PeriodDetails(Localization.Get("VmPeriodDefaultLabel"), f.DefaultPeriod), .. PeriodDetails(Localization.Get("VmPeriodMinimumLabel"), f.MinimumPeriod)]
                    : [Localization.Get("VmInspectorFormatNotSelected")];
                break;
            case ProbeScreen.Capture:
                InspectorTitle = Localization.Get("VmInspectorCaptureTitle");
                InspectorDetails = Session is { Mode: SessionMode.Capture } s
                    ? [$"{Localization.Get("VmInspectorDeviceLabel")}: {s.EndpointName}", $"{Localization.Get("VmInspectorStateLabel")}: {s.Lifecycle}", $"{Localization.Get("VmInspectorStreamQualityLabel")}: {s.Quality}", $"{Localization.Get("VmInspectorDurationLabel")}: {s.DurationSeconds} с", $"{Localization.Get("VmInspectorMeterLabel")}: {MeterStatusText}", $"{Localization.Get("VmInspectorCleanupLabel")}: {s.CleanupStatus ?? "НЕИЗВЕСТНО / нет данных"}"]
                    : [Localization.Get("VmInspectorCaptureNotStarted"), $"{Localization.Get("VmInspectorDeviceLabel")}: {SelectedCaptureEndpoint?.Name ?? "не выбрано"}", $"{Localization.Get("VmInspectorMeterLabel")}: {MeterStatusText}"];
                break;
            case ProbeScreen.Render:
                InspectorTitle = Localization.Get("VmInspectorRenderTitle");
                InspectorDetails = Session is { Mode: SessionMode.Render } r
                    ? [$"{Localization.Get("VmInspectorDeviceLabel")}: {r.EndpointName}", $"{Localization.Get("VmInspectorStateLabel")}: {r.Lifecycle}", $"{Localization.Get("VmInspectorStreamQualityLabel")}: {r.Quality}", $"{Localization.Get("VmInspectorWindowsChannelLabel")}: {r.RenderChannel}", $"{Localization.Get("VmInspectorDurationLabel")}: {r.DurationSeconds} с", $"{Localization.Get("VmInspectorPhysicalMatchLabel")}: НЕИЗВЕСТНО", $"{Localization.Get("VmInspectorCleanupLabel")}: {r.CleanupStatus ?? "НЕИЗВЕСТНО / нет данных"}"]
                    : [$"{Localization.Get("VmInspectorDeviceLabel")}: {SelectedRenderEndpoint?.Name ?? "не выбрано"}", $"{Localization.Get("VmInspectorWindowsChannelLabel")}: {RenderChannel}", $"{Localization.Get("VmInspectorDurationLabel")}: {DurationSeconds} с", $"{Localization.Get("VmInspectorPhysicalMatchLabel")}: НЕИЗВЕСТНО", Localization.Get("VmInspectorRenderNotStarted")];
                break;
            case ProbeScreen.Reports:
                InspectorTitle = Localization.Get("VmInspectorReportsTitle");
                InspectorDetails = SelectedReport is { } report
                    ? [$"{report.Name}", $"{Localization.Get("VmInspectorModeLabel")}: {Localization.Get(report.Mode == SessionMode.Capture ? "VmCaptureInputDirection" : "VmRenderOutputDirection")}", $"{Localization.Get("VmInspectorStateLabel")}: {report.Lifecycle}", $"{Localization.Get("VmInspectorStreamQualityLabel")}: {report.Quality}", report.Evidence, "ДЕМО · Данные симуляции"]
                    : [Localization.Get("VmInspectorReportNotSelected")];
                break;
            case ProbeScreen.Diagnostics:
                InspectorTitle = Localization.Get("VmInspectorWorkerTitle");
                InspectorDetails = [
                    $"{Localization.Get("VmInspectorConnectionLabel")}: {WorkerState.State}",
                    WorkerSession is { } ws ? $"{Localization.Get("VmInspectorSessionLabel")}: {ws.Id:D} · {ws.Lifecycle}" : Localization.Get("VmInspectorWorkerSessionNotStarted"),
                    $"{Localization.Get("VmInspectorStreamQualityLabel")}: {WorkerSession?.Quality.ToString() ?? "UNKNOWN"}",
                    $"{Localization.Get("VmInspectorCleanupLabel")}: {WorkerSession?.CleanupStatus ?? "UNKNOWN"}",
                    WorkerTimeline.LastOrDefault() is { } we ? $"{Localization.Get("VmInspectorEventLabel")}: {we.Name} · IPC sequence {we.ProtocolSequence?.ToString() ?? "локальное"}" : Localization.Get("VmInspectorWorkerEventsMissing"),
                    $"{Localization.Get("VmInspectorPhysicalMatchLabel")}: UNKNOWN · все worker данные синтетические."
                ];
                break;
            default:
                InspectorTitle = Localization.Get("VmInspectorDemoDiagnosticsTitle");
                InspectorDetails = [$"Сессия: {Session?.Id.ToString() ?? "не запускалась"}", $"Состояние: {Session?.Lifecycle.ToString() ?? "не запускалась"}", $"Качество потока: {Session?.Quality.ToString() ?? "не оценивалось"}", LastEvent is { } ev ? $"Последнее событие: №{ev.Sequence} {ev.Kind} · {ev.Timestamp:HH:mm:ss.fff}" : "Событий пока нет.", LastEvent?.Message ?? "Нет событий.", "ДЕМО · Данные симуляции"];
                break;
        }
        InspectorSections = Screen == ProbeScreen.Diagnostics ? BuildWorkerInspectorSections() :
            windowsDeviceView && SelectedWindowsEndpoint is { } selectedWindows ? [new InspectorSectionDto(Localization.Get("VmInspectorWindowsSectionTitle"), WindowsEndpointDetails(selectedWindows))] :
            [new InspectorSectionDto(Localization.Get("VmInspectorDetails"), InspectorDetails.Select(d => new InspectorFieldDto(string.Empty, d)).ToArray())];
        Changed(nameof(InspectorTitle));
        Changed(nameof(InspectorProvenanceText));
        Changed(nameof(InspectorDetails));
        Changed(nameof(InspectorSections));
    }

    private IReadOnlyList<InspectorSectionDto> BuildWorkerInspectorSections()
    {
        var settings = WorkerSessionSettings;
        var capture = WorkerSession?.Mode == SessionMode.Capture ? _workerCaptureMeter : null;
        var render = WorkerSession?.Mode == SessionMode.Render ? _workerRenderMeter : null;
        var sequence = capture?.SnapshotSequence ?? render?.SnapshotSequence;
        var completedWindow = capture?.CompletedWindowIndex ?? render?.CompletedWindowIndex;
        var channelCount = capture?.ChannelCount ?? render?.ChannelCount;
        var measurementState = _workerMeterStale ? Localization.Get("VmInspectorMeasurementStale") : _workerMeterFinal ? Localization.Get("VmInspectorMeasurementFinal") : sequence is null ? Localization.Get("VmInspectorMeasurementMissing") : completedWindow is null ? Localization.Get("VmInspectorMeasurementWindowMissing") : Localization.Get("VmInspectorMeasurementCurrent");
        var connection = WorkerState.State switch
        {
            WorkerConnectionState.ConnectedFake => Localization.Get("VmInspectorWorkerConnected"), WorkerConnectionState.Starting => Localization.Get("VmInspectorWorkerStarting"), WorkerConnectionState.Connecting => Localization.Get("VmInspectorWorkerConnecting"), WorkerConnectionState.Handshaking => Localization.Get("VmInspectorWorkerNegotiating"), WorkerConnectionState.Stopping => Localization.Get("VmInspectorWorkerStopping"), WorkerConnectionState.Incompatible => Localization.Get("VmInspectorWorkerIncompatible"), WorkerConnectionState.Failed => Localization.Get("VmInspectorWorkerFailed"), _ => Localization.Get("VmInspectorWorkerDisconnected")
        };
        var connectionFields = new List<InspectorFieldDto>
        {
            new(Localization.Get("VmInspectorStateLabel"), connection, WorkerState.State.ToString()),
            new(Localization.Get("VmInspectorTransportLabel"), Localization.Get("VmInspectorWorkerTransport")),
            new(Localization.Get("VmInspectorProvenanceLabel"), Localization.Get("VmInspectorWorkerSyntheticProvenance"), Localization.Get("VmInspectorWorkerRealDevicesNotUsed"))
        };
        if (WorkerState.State is WorkerConnectionState.Failed or WorkerConnectionState.Incompatible || WorkerState.State == WorkerConnectionState.Disconnected && WorkerSession?.Lifecycle == SessionLifecycle.Unknown)
            connectionFields.Add(new(Localization.Get("VmInspectorReasonLabel"), WorkerState.Message, WorkerState.State.ToString()));

        var sessionFields = new List<InspectorFieldDto>();
        if (WorkerSession is { } session)
        {
            sessionFields.Add(new(Localization.Get("VmInspectorModeLabel"), session.Mode == SessionMode.Capture ? "Capture" : "Render", session.Mode.ToString()));
            sessionFields.Add(new(Localization.Get("VmInspectorEndpointLabel"), settings?.EndpointId ?? session.EndpointName));
            sessionFields.Add(new(Localization.Get("VmInspectorFormatLabel"), settings is null ? Localization.Get("VmInspectorUnknown") : $"{settings.Encoding} · {settings.SampleRate} Гц · {settings.ChannelCount} каналов", settings?.FormatId));
            if (session.Mode == SessionMode.Render) sessionFields.Add(new(Localization.Get("VmInspectorWindowsChannelLabel"), settings?.RenderChannel?.ToString() ?? session.RenderChannel?.ToString() ?? Localization.Get("VmInspectorUnknown")));
            sessionFields.Add(new(Localization.Get("VmInspectorDurationLabel"), settings is null ? $"{session.DurationSeconds} с" : $"{settings.DurationSeconds} с"));
            sessionFields.Add(new(Localization.Get("VmInspectorStateLabel"), LifecycleLabel(session.Lifecycle), session.Lifecycle.ToString()));
            sessionFields.Add(new(Localization.Get("VmInspectorIdLabel"), session.Id.ToString("N")[..8]));
            sessionFields.Add(new(Localization.Get("VmInspectorFullUuidLabel"), session.Id.ToString("D"), Copyable: true));
        }
        else sessionFields.Add(new(Localization.Get("VmInspectorSessionLabel"), Localization.Get("VmInspectorNotStarted")));

        var measurementFields = new List<InspectorFieldDto>
        {
            new(Localization.Get("VmInspectorSnapshotLabel"), sequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Localization.Get("VmInspectorNoData")),
            new(Localization.Get("VmInspectorDataLabel"), measurementState),
            new(Localization.Get("VmInspectorChannelsLabel"), channelCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Localization.Get("VmInspectorUnknown")),
            new(Localization.Get("VmInspectorCoalescingLabel"), capture is not null ? string.Format(Localization.CurrentCulture, Localization.Get("VmInspectorSnapshotsCoalesced"), capture.CoalescedSnapshotCount) : render is not null ? string.Format(Localization.CurrentCulture, Localization.Get("VmInspectorSnapshotsCoalesced"), render.CoalescedSnapshotCount) : Localization.Get("VmInspectorNoData"))
        };
        var quality = WorkerSession?.Quality switch { StreamQuality.Clean => Localization.Get("VmInspectorQualityClean"), StreamQuality.Warning => Localization.Get("VmInspectorQualityWarning"), StreamQuality.Failed => Localization.Get("VmInspectorQualityFailed"), _ => Localization.Get("VmInspectorUnknownNotAssessed") };
        var cleanup = WorkerSession?.CleanupStatus switch { "SYNTHETIC_CLEANUP_COMPLETED" => Localization.Get("VmInspectorCleanupCompletedSynthetic"), "NOT_ATTEMPTED" => Localization.Get("VmInspectorCleanupNotAttempted"), null or "UNKNOWN" => Localization.Get("VmInspectorUnknownNotAssessed"), var code => code };
        return
        [
            new(Localization.Get("VmInspectorConnectionSection"), connectionFields),
            new(Localization.Get("VmInspectorSessionSection"), sessionFields),
            new(Localization.Get("VmInspectorMeasurementsSection"), measurementFields),
            new(Localization.Get("VmInspectorEvidenceSection"), [new(Localization.Get("VmInspectorStreamQualityLabel"), quality, WorkerSession?.Quality.ToString()), new(Localization.Get("VmInspectorCleanupLabel"), cleanup, WorkerSession?.CleanupStatus), new(Localization.Get("VmInspectorPhysicalRoutingLabel"), Localization.Get("VmInspectorUnknownNotAssessed")), new(Localization.Get("VmInspectorObservationLabel"), Localization.Get("VmInspectorSyntheticNoRealAudioMeasured"))])
        ];
    }

    private static string LifecycleLabel(SessionLifecycle state) => state switch
    {
        SessionLifecycle.Preparing => Localization.Get("VmInspectorLifecyclePreparing"), SessionLifecycle.Ready => Localization.Get("VmInspectorLifecycleReady"), SessionLifecycle.Started => Localization.Get("VmInspectorLifecycleStarted"), SessionLifecycle.Stopping => Localization.Get("VmInspectorLifecycleStopping"), SessionLifecycle.Stopped => Localization.Get("VmInspectorLifecycleStopped"), SessionLifecycle.Completed => Localization.Get("VmInspectorLifecycleCompleted"), SessionLifecycle.Failed => Localization.Get("VmInspectorLifecycleFailed"), _ => Localization.Get("VmInspectorUnknownNotAssessed")
    };

    public static string StatusLabel(FormatStatus status) => status switch
    {
        FormatStatus.SupportedReported => Localization.Get("VmFormatStatusSupported"),
        FormatStatus.Unsupported => Localization.Get("VmFormatStatusUnsupported"),
        FormatStatus.Failed => Localization.Get("VmFormatStatusFailed"),
        FormatStatus.Unknown => Localization.Get("VmFormatStatusUnknown"),
        FormatStatus.NotTested => Localization.Get("VmFormatStatusNotTested"),
        FormatStatus.Initialized => Localization.Get("VmFormatStatusInitialized"),
        FormatStatus.Ready => Localization.Get("VmFormatStatusReady"),
        _ => Localization.Get("VmFormatStatusUnknown")
    };

    private static string[] PeriodDetails(string label, PeriodEvidence evidence) =>
    [label, string.Format(Localization.CurrentCulture, Localization.Get("VmPeriodStatusFormat"), evidence.StatusCode), string.Format(Localization.CurrentCulture, Localization.Get("VmPeriodFormatSupportHresult"), evidence.Hresult ?? Localization.Get("VmValueUnknown")), string.Format(Localization.CurrentCulture, Localization.Get("VmPeriodInitializeHresult"), evidence.InitializeHresult ?? Localization.Get("VmValueNotObserved")), string.Format(Localization.CurrentCulture, Localization.Get("VmPeriodProvenance"), evidence.Provenance ?? Localization.Get("VmValueUnknown"))];

    public static string ModeLabel(SessionMode mode) => mode == SessionMode.Capture ? "вход (Capture)" : "выход (Render)";

    private sealed record PendingSession(Guid Id, SessionMode Mode, EndpointDto Endpoint, int? RenderChannel, int DurationSeconds, int ChannelCount);
}
