using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Globalization;

namespace KlidanAudioProbe.Gui.Core;

public sealed record V2AutoShortCandidateDisplay(string Period, string Result, string Buffer, string Reason, string Details);

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
    private enum OperatorResultKind { None, Duplex, DeviceScan, FormatQuery, InitializeOnly }
    private readonly IProbeClient _client;
    private readonly IWorkerClient? _worker;
    private readonly IAutomaticScanWorkflow? _automaticScanWorkflow;
    private readonly IDuplexRepeatabilityWorkflow? _duplexRepeatabilityWorkflow;
    private readonly IExclusivePeriodDiscoveryWorkflow? _exclusivePeriodDiscoveryWorkflow;
    private readonly ICustomPeriodDuplexWorkflow? _customPeriodDuplexWorkflow;
    private readonly IV4AutomaticSearchWorkflow? _v4AutomaticSearchWorkflow;
    private readonly IV4ScanQualificationCoordinator? _v4ScanQualificationCoordinator;
    private readonly string? _profileDirectory;
    private readonly string? _v4EvidenceDirectory;
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
    private string _operatorResultEndpoint = "", _operatorResultConfiguration = "";
    private OperatorResultKind _operatorResultMode;
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
    private ulong? _windowsDiscoveryWorkerGeneration;
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
    private V2AutomaticScanResult? _automaticScanV2Result;
    private WorkerScanV2Route? _automaticScanV2Route;
    private string? _automaticScanV2DiscoveryGeneration;
    private string? _automaticScanV2CaptureEndpointId, _automaticScanV2RenderEndpointId;
    private string _automaticScanEngine = "V2_ADAPTIVE", _automaticScanV2Mode = "MONO_TO_STEREO", _automaticScanV2Period = "MINIMUM", _automaticScanV2QaMode = "DISCOVERY";
    private bool _automaticScanFullBudgetConfirmed;
    private WorkerScanV2ContractV4? _v4PreparedPlan;
    private WorkerScanV2V4Consent? _v4ConsentReceipt;
    private string? _v4CaptureEndpointId, _v4RenderEndpointId, _v4CaptureFriendlyName, _v4RenderFriendlyName, _v4UiDiscoveryGeneration;
    private ulong? _v4WorkerDiscoveryGeneration;
    private string? _v4PlanFingerprint;
    private int _v4DiagnosticPeriodMilliseconds = 5;
    private string _v4DiagnosticMmcssMode = "OFF";
    private bool _v4DiagnosticPlanMode, _v4DiagnosticConsentChecked, _v4DiagnosticRunStarted;
    private CancellationTokenSource? _v4PreparationCancellation;
    private int _v4PreparationVersion, _v4RepeatabilityRuns = 5;
    private bool _v4Preparing, _v4ConsentPending, _v4ConsentChecked;
    private string? _v4AudioHostConsentPlanFingerprint;
    private bool _v4ScanRunning;
    private WindowsEndpointDto? _v4CaptureEndpoint, _v4RenderEndpoint;
    private CancellationTokenSource? _v4ScanCancellation;
    private TaskCompletionSource? _v4ScanTerminal;
    private V4AutomaticSearchResult? _v4SearchResult;
    private string _v4ProgressText = "", _v4EvidencePath = "";
    private string _v4PlanStatusKey = "VmV4PlanNotPrepared";
    private string? _v4PlanStatusArgument;
    private string _automaticScanV2CaptureFormat = "48000|PCM16", _automaticScanV2RenderFormat = "48000|PCM16";
    private int _automaticScanV2CaptureChannels = 2, _automaticScanV2RenderChannels = 2;
    private int _automaticScanV2CaptureChannel1 = 1, _automaticScanV2CaptureChannel2 = 2, _automaticScanV2RenderChannel1 = 1, _automaticScanV2RenderChannel2 = 2;
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
    private string _customPeriodFramesText = "192", _customPeriodDuplexStatus = Localization.Get("VmCustomPeriodDuplexNotStarted");
    private CustomPeriodDuplexPreparation? _customPeriodDuplexPreparation;
    private CustomPeriodDuplexResult? _customPeriodDuplexResult;
    private bool _customPeriodDuplexConsentPending, _customPeriodDuplexConsentChecked, _customPeriodDuplexRunning;
    private CancellationTokenSource? _customPeriodDuplexCancellation;
    private bool _workerAttempted;
    private int _windowsDiscoveryRequestGeneration;
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
    public MainViewModel(IProbeClient? client = null, IWorkerClient? worker = null, IAutomaticScanWorkflow? automaticScanWorkflow = null, IDuplexRepeatabilityWorkflow? duplexRepeatabilityWorkflow = null, IExclusivePeriodDiscoveryWorkflow? exclusivePeriodDiscoveryWorkflow = null, ICustomPeriodDuplexWorkflow? customPeriodDuplexWorkflow = null, string? profileDirectory = null, ILaboratoryEvidenceBundleExportInteraction? bundleExportInteraction = null, Func<string, LaboratoryEvidenceBundleDirectories>? bundleDirectoriesFactory = null, bool developerToolsAvailable = true, IV4AutomaticSearchWorkflow? v4AutomaticSearchWorkflow = null, string? evidenceDirectory = null, IV4ScanQualificationCoordinator? v4ScanQualificationCoordinator = null)
    {
        _client = client ?? new FakeProbeClient();
        _worker = worker;
        _automaticScanWorkflow = automaticScanWorkflow;
        _duplexRepeatabilityWorkflow = duplexRepeatabilityWorkflow;
        _exclusivePeriodDiscoveryWorkflow = exclusivePeriodDiscoveryWorkflow;
        _customPeriodDuplexWorkflow = customPeriodDuplexWorkflow;
        _v4AutomaticSearchWorkflow = v4AutomaticSearchWorkflow;
        _v4ScanQualificationCoordinator = v4ScanQualificationCoordinator;
        _profileDirectory = profileDirectory;
        _v4EvidenceDirectory = evidenceDirectory;
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
        ConfirmAutomaticScanCommand = new(ConfirmAutomaticScanAsync, CanConfirmAutomaticScan);
        CancelAutomaticScanConsentCommand = new(CancelAutomaticScanConsent, () => AutomaticScanConsentPending);
        PrepareV4CustomPlanCommand = new(PrepareV4CustomPlanAsync, CanPrepareV4CustomPlan);
        PrepareV4DiagnosticPlanCommand = new(PrepareV4DiagnosticPlanAsync, CanPrepareV4DiagnosticPlan);
        AcceptV4CustomPlanCommand = new(AcceptV4CustomPlan, CanAcceptV4CustomPlan);
        AcceptV4DiagnosticPlanCommand = new(AcceptV4CustomPlan, CanAcceptV4DiagnosticPlan);
        CancelV4CustomPlanCommand = new(CancelV4CustomPlan, () => _v4Preparing || _v4ConsentPending || _v4ConsentReceipt is not null);
        StartV4ScanCommand = new(StartV4ScanAsync, CanStartV4Scan);
        StartV4DiagnosticCommand = new(StartV4ScanAsync, CanStartV4Diagnostic);
        CancelV4ScanCommand = new(CancelV4Scan, () => V4ScanRunning);
        CancelAutomaticScanCommand = new(CancelAutomaticScan, () => _automaticScanPreparing || _automaticScanRunning);
        OpenAutomaticScanLocationCommand = new(OpenAutomaticScanLocation, CanOpenAutomaticScanLocation);
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
            command.ExecutionFailed += ex => WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerFakeOperationFailed"), ex.GetType().Name, ex.Message);
        ConfirmStreamValidationCommand.ExecutionFailed += ex => _streamValidationStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmStreamCommandFailed"), ex.GetType().Name, ex.Message);
        ConfirmDuplexValidationCommand.ExecutionFailed += ex => { _duplexStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmDuplexCommandFailed"), ex.GetType().Name, ex.Message); Changed(nameof(DuplexStatus)); };
        PrepareAutomaticScanCommand.ExecutionFailed += ex => SetAutomaticScanStatus(string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanPrepareCommandFailed"), ex.GetType().Name, ex.Message));
        ConfirmAutomaticScanCommand.ExecutionFailed += ex => SetAutomaticScanStatus(string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanConfirmCommandUnknown"), ex.GetType().Name, ex.Message));
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
            Changed(nameof(V4PlanStatus)); Changed(nameof(V4AudioGateText)); Changed(nameof(V4ConsentSummary));
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
            return string.Format(Localization.CurrentCulture, Localization.Get("VmRealFormatSummary"), tested, supported, unsupported, failedIncomplete, notTested, initialize);
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
    public string OperatorResultDetails => _realInitializationError ?? _realFormatError ?? (RealInitializationResult is not null ? RealInitializationSummary : RealFormatMatrix is not null ? string.Format(Localization.CurrentCulture, Localization.Get("VmOperatorResultDetails"), RealFormatMatrixStatus, _operatorResultEndpoint, _operatorResultConfiguration, RealFormatRows.Count) : Localization.Get("VmOperatorQueryNotRun"));
    public string OperatorResultProvenance => _operatorResultProvenance;
    public bool DeviceScanConsentPending { get => _deviceScanConsentPending; private set { if (Set(ref _deviceScanConsentPending, value)) { Changed(nameof(DeviceScanConsentText)); ConfirmDeviceScanCommand.Refresh(); RequestDeviceScanCommand.Refresh(); RefreshV4Commands(); } } }
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
            if (_streamValidationRows.Values.Any(r => r.FinalStatus == "RUNNING")) return string.Format(Localization.CurrentCulture, Localization.Get("VmStreamEvidenceRunningSummary"), _streamValidations.Count);
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
    public string StreamValidationConsentText => SelectedWindowsEndpoint is { } e && SelectedStreamCandidate is { } c ? string.Format(Localization.CurrentCulture, Localization.Get("VmStreamValidationConsent"), e.FriendlyNameDisplay, DirectionDisplayLabel(e.Direction), e.EndpointId, c.Channels, c.Period.ToUpperInvariant()) : Localization.Get("VmStreamValidationSelectInitializedCandidate");
    public string StreamValidationAvailability => Localization.Get(CanRequestStreamValidation() ? "VmStreamValidationAuthorizationRequired" : "VmStreamValidationNeedsCurrentInitialize");
    public bool DeviceScanRunning { get => _deviceScanRunning; private set { if (Set(ref _deviceScanRunning, value)) { CancelDeviceScanCommand.Refresh(); RequestDeviceScanCommand.Refresh(); RequestStreamValidationCommand.Refresh(); QueryRealFormatsCommand.Refresh(); RequestRealInitializeCommand.Refresh(); Changed(nameof(CanSaveDeviceProfile)); RefreshV4Commands(); } } }
    public bool AutomaticScanPairEnabled
    {
        get => _automaticScanPairEnabled;
        set { if (AutomaticScanBusy || _automaticScanRunning) return; if (Set(ref _automaticScanPairEnabled, value)) { CancelAutomaticScanConsent(); Changed(nameof(AutomaticScanConsentText)); Changed(nameof(AutomaticScanAvailabilityReason)); PrepareAutomaticScanCommand.Refresh(); RefreshContext(); } }
    }
    public IReadOnlyList<ChoiceOption> AutomaticScanEngineOptions { get; } = [new("V2_ADAPTIVE", Localization.Get("VmScanEngineAdaptiveV2")), new("V1_COMPATIBLE", Localization.Get("VmScanEngineCompatibleV1"))];
    public string AutomaticScanEngineDescription => Localization.Get(AutomaticScanEngine == "V2_ADAPTIVE" ? "VmScanEngineV2Description" : "VmScanEngineV1Description");
    public string AutomaticScanEngine { get => _automaticScanEngine; set { if (AutomaticScanBusy || value is not ("V2_ADAPTIVE" or "V1_COMPATIBLE") || !Set(ref _automaticScanEngine, value)) return; if (value == "V2_ADAPTIVE") AutomaticScanPairEnabled = true; ResetAutomaticScanV2Consent(); Changed(nameof(AutomaticScanEngineDescription)); Changed(nameof(AutomaticScanV2ControlsVisible)); Changed(nameof(AutomaticScanV1ControlsVisible)); Changed(nameof(AutomaticScanAvailabilityReason)); Changed(nameof(AutomaticScanV2Availability)); Changed(nameof(AutomaticScanConsentText)); PrepareAutomaticScanCommand.Refresh(); } }
    public bool AutomaticScanV2ControlsVisible => AutomaticScanEngine == "V2_ADAPTIVE";
    public bool AutomaticScanV1ControlsVisible => AutomaticScanEngine == "V1_COMPATIBLE";
    public IReadOnlyList<ChoiceOption> AutomaticScanV2Modes { get; } = [new("MONO_TO_STEREO", Localization.Get("VmRouteMonoStereo")), new("STEREO_TO_STEREO", Localization.Get("VmRouteStereoStereo")), new("MONO_TO_MONO", Localization.Get("VmRouteMonoMono"))];
    public IReadOnlyList<ChoiceOption> AutomaticScanV2Periods { get; } = [new("MINIMUM", Localization.Get("VmPeriodMinimum")), new("DEFAULT", Localization.Get("VmPeriodDefault"))];
    public IReadOnlyList<ChoiceOption> AutomaticScanV2QaModes { get; } = [new("DISCOVERY", Localization.Get("VmV2QaDiscovery")), new("SHORT", Localization.Get("VmV2QaShort")), new("AUTO_SHORT", Localization.Get("VmV2QaAutoShort")), new("FULL", Localization.Get("VmV2QaFull"))];
    public IReadOnlyList<ChoiceOption> AutomaticScanV2Formats { get; } = [new("48000|PCM16", "48 kHz · PCM16"), new("48000|PCM24_IN_32", "48 kHz · PCM24-in-32"), new("48000|FLOAT32", "48 kHz · Float32"), new("44100|PCM16", "44.1 kHz · PCM16"), new("44100|PCM24_IN_32", "44.1 kHz · PCM24-in-32"), new("44100|FLOAT32", "44.1 kHz · Float32")];
    public IReadOnlyList<SelectionOption> AutomaticScanV2ChannelCounts => V2ChannelCounts(SelectedDuplexCaptureEndpoint);
    public IReadOnlyList<SelectionOption> AutomaticScanV2RenderChannelCounts => V2ChannelCounts(SelectedDuplexRenderEndpoint);
    public IReadOnlyList<SelectionOption> AutomaticScanV2CaptureChannelOptions => Enumerable.Range(1, Math.Max(1, _automaticScanV2CaptureChannels)).Select(i => new SelectionOption(i, i.ToString(System.Globalization.CultureInfo.CurrentCulture))).ToArray();
    public IReadOnlyList<SelectionOption> AutomaticScanV2RenderChannelOptions => Enumerable.Range(1, Math.Max(1, _automaticScanV2RenderChannels)).Select(i => new SelectionOption(i, i.ToString(System.Globalization.CultureInfo.CurrentCulture))).ToArray();
    public string AutomaticScanV2Mode { get => _automaticScanV2Mode; set { if (AutomaticScanBusy || !AutomaticScanV2Modes.Any(o => o.Value == value) || !Set(ref _automaticScanV2Mode, value)) return; ResetAutomaticScanV2Consent(); Changed(nameof(AutomaticScanV2RouteSummary)); Changed(nameof(AutomaticScanV2ChannelControlsText)); Changed(nameof(AutomaticScanConsentText)); PrepareAutomaticScanCommand.Refresh(); } }
    public string AutomaticScanV2Period { get => _automaticScanV2Period; set { if (AutomaticScanBusy || value is not ("MINIMUM" or "DEFAULT") || !Set(ref _automaticScanV2Period, value)) return; ResetAutomaticScanV2Consent(); Changed(nameof(AutomaticScanConsentText)); PrepareAutomaticScanCommand.Refresh(); } }
    public string AutomaticScanV2QaMode { get => _automaticScanV2QaMode; set { if (AutomaticScanBusy || !AutomaticScanV2QaModes.Any(o => o.Value == value) || !Set(ref _automaticScanV2QaMode, value)) return; ResetAutomaticScanV2Consent(); Changed(nameof(AutomaticScanConsentText)); Changed(nameof(AutomaticScanFullBudgetConfirmationVisible)); PrepareAutomaticScanCommand.Refresh(); } }
    public bool AutomaticScanFullBudgetConfirmationVisible => AutomaticScanEngine == "V2_ADAPTIVE" && AutomaticScanV2QaMode == "FULL";
    public bool AutomaticScanFullBudgetConfirmed { get => _automaticScanFullBudgetConfirmed; set { if (Set(ref _automaticScanFullBudgetConfirmed, value)) ConfirmAutomaticScanCommand.Refresh(); } }
    public string AutomaticScanV2CaptureFormat { get => _automaticScanV2CaptureFormat; set { if (AutomaticScanBusy || !AutomaticScanV2Formats.Any(o => o.Value == value) || !Set(ref _automaticScanV2CaptureFormat, value)) return; ResetAutomaticScanV2Consent(); Changed(nameof(AutomaticScanConsentText)); PrepareAutomaticScanCommand.Refresh(); } }
    public string AutomaticScanV2RenderFormat { get => _automaticScanV2RenderFormat; set { if (AutomaticScanBusy || !AutomaticScanV2Formats.Any(o => o.Value == value) || !Set(ref _automaticScanV2RenderFormat, value)) return; ResetAutomaticScanV2Consent(); Changed(nameof(AutomaticScanConsentText)); PrepareAutomaticScanCommand.Refresh(); } }
    public int AutomaticScanV2CaptureChannels { get => _automaticScanV2CaptureChannels; set => SetV2Channels(ref _automaticScanV2CaptureChannels, value, true); }
    public int AutomaticScanV2RenderChannels { get => _automaticScanV2RenderChannels; set => SetV2Channels(ref _automaticScanV2RenderChannels, value, false); }
    public int AutomaticScanV2CaptureChannel1 { get => _automaticScanV2CaptureChannel1; set => SetV2Channel(ref _automaticScanV2CaptureChannel1, value, nameof(AutomaticScanV2CaptureChannel1)); }
    public int AutomaticScanV2CaptureChannel2 { get => _automaticScanV2CaptureChannel2; set => SetV2Channel(ref _automaticScanV2CaptureChannel2, value, nameof(AutomaticScanV2CaptureChannel2)); }
    public int AutomaticScanV2RenderChannel1 { get => _automaticScanV2RenderChannel1; set => SetV2Channel(ref _automaticScanV2RenderChannel1, value, nameof(AutomaticScanV2RenderChannel1)); }
    public int AutomaticScanV2RenderChannel2 { get => _automaticScanV2RenderChannel2; set => SetV2Channel(ref _automaticScanV2RenderChannel2, value, nameof(AutomaticScanV2RenderChannel2)); }
    public IReadOnlyList<SelectionOption> V4RepeatabilityOptions { get; } = [new(0, Localization.Get("VmV4SearchOnly")), new(5, "5")];
    public int V4RepeatabilityRuns { get => _v4RepeatabilityRuns; set { if (AutomaticScanBusy || value is < 0 or > 5 || !Set(ref _v4RepeatabilityRuns, value)) return; InvalidateV4PreparedPlan("VmV4PlanInvalidated"); } }
    public IReadOnlyList<int> V4DiagnosticPeriods { get; } = [5, 10];
    public int V4DiagnosticPeriodMilliseconds { get => _v4DiagnosticPeriodMilliseconds; set { if (value is not (5 or 10) || !Set(ref _v4DiagnosticPeriodMilliseconds, value)) return; InvalidateV4PreparedPlan("VmV4PlanInvalidated"); } }
    public IReadOnlyList<ChoiceOption> V4DiagnosticMmcssOptions { get; } = [new("OFF", Localization.Get("VmWorkerMmcssOff")), new("PRO_AUDIO", Localization.Get("VmWorkerMmcssProAudio"))];
    public string V4DiagnosticMmcssMode { get => _v4DiagnosticMmcssMode; set { if (AutomaticScanBusy || value is not ("OFF" or "PRO_AUDIO") || !Set(ref _v4DiagnosticMmcssMode, value)) return; InvalidateV4PreparedPlan("VmV4PlanInvalidated"); } }
    public bool V4DiagnosticPlanMode => _v4DiagnosticPlanMode;
    public bool V4DiagnosticConsentPending => _v4DiagnosticPlanMode && _v4ConsentPending;
    public bool V4SearchConsentPending => !_v4DiagnosticPlanMode && _v4ConsentPending;
    public bool V4DiagnosticReceiptCreated => _v4DiagnosticPlanMode && V4ReceiptCreated;
    public bool V4DiagnosticRunVisible => _v4DiagnosticPlanMode && (V4DiagnosticReceiptCreated || _v4DiagnosticRunStarted);
    public bool V4SearchReceiptCreated => !_v4DiagnosticPlanMode && V4ReceiptCreated;
    public bool V4DiagnosticConsentChecked { get => _v4DiagnosticConsentChecked; set { if (Set(ref _v4DiagnosticConsentChecked, value)) AcceptV4DiagnosticPlanCommand.Refresh(); } }
    public string V4DiagnosticAvailabilityReason => GetV4DiagnosticDisabledReason();
    public bool V4PlanPreparing => _v4Preparing;
    public bool V4ConsentPending => _v4ConsentPending;
    public bool V4ConsentChecked { get => _v4ConsentChecked; set { if (Set(ref _v4ConsentChecked, value)) AcceptV4CustomPlanCommand.Refresh(); } }
    public bool V4PlanReady => _v4PreparedPlan is not null;
    public WorkerScanV2V4Consent? V4ConsentReceipt => _v4ConsentReceipt;
    public bool V4ReceiptCreated => _v4ConsentReceipt is not null;
    public string V4PlanStatus => _v4PlanStatusKey == "VmV4PlanBlocked" ? string.Format(Localization.CurrentCulture, Localization.Get(_v4PlanStatusKey), _v4PlanStatusArgument) : Localization.Get(_v4PlanStatusKey);
    public bool V4CustomPlanControlsVisible => AutomaticScanEngine == "V2_ADAPTIVE";
    public bool V4SearchPlanUiVisible => V4CustomPlanControlsVisible;
    public string V4AudioGateText => Localization.Get(_v4AutomaticSearchWorkflow is null ? "VmV4AudioGateClosed" : "VmV4AudioGateOpen");
    public string V4PrepareDisabledReason => GetV4PrepareDisabledReason();
    public bool V4ScanRunning { get => _v4ScanRunning; private set { if (Set(ref _v4ScanRunning, value)) { Changed(nameof(AutomaticScanBusy)); Changed(nameof(V4ProgressText)); RefreshV4Commands(); PrepareAutomaticScanCommand.Refresh(); ConfirmAutomaticScanCommand.Refresh(); RequestDeviceScanCommand.Refresh(); QueryRealFormatsCommand.Refresh(); RequestStreamValidationCommand.Refresh(); } } }
    public string V4ProgressText => _v4ProgressText;
    public string V4ResultState => _v4SearchResult?.Evidence.DiagnosticOnly == true ? "DIAGNOSTIC_ONLY" : _v4SearchResult?.Evidence.SelectionState switch { "MINIMUM_SHORT_CLEAN_FOUND" => "PRELIMINARY", "REPEATABILITY_CONFIRMED" => "REPEATABILITY_CONFIRMED", "REPEATABILITY_FAILED" => "REPEATABILITY_FAILED", "UNKNOWN" => "UNKNOWN", _ => "MINIMUM_NOT_PROVEN" };
    public string V4ResultSummary => BuildV4ResultSummary();
    public string V4EvidencePath => _v4EvidencePath;
    public string V4PlanFingerprint => _v4PlanFingerprint ?? Localization.Get("VmV4NoFingerprint");
    public string V4ConsentSummary => BuildV4ConsentSummary();
    public bool AutomaticScanV2SecondCaptureVisible => AutomaticScanV2Mode == "STEREO_TO_STEREO";
    public bool AutomaticScanV2SecondRenderVisible => AutomaticScanV2Mode != "MONO_TO_MONO";
    public string AutomaticScanV2ChannelControlsText => string.Format(Localization.CurrentCulture, Localization.Get("VmV2StreamGeometry"), _automaticScanV2CaptureChannels, _automaticScanV2RenderChannels);
    public string AutomaticScanV2PhysicalMappingWarning => Localization.Get("VmV2PhysicalMappingUnknown");
    public string AutomaticScanV2RouteSummary => BuildV2Route() is { } route ? string.Format(Localization.CurrentCulture, Localization.Get("VmV2RouteSummary"), route.Mode, string.Join("/", route.Capture.SelectedIndicesZeroBased.Select(i => i + 1)), string.Join("/", route.Render.SelectedIndicesZeroBased.Select(i => i + 1))) : Localization.Get("VmV2RouteInvalid");
    public string AutomaticScanV2Availability => !AutomaticScanV2ControlsVisible ? Localization.Get("VmV2NotSelected") : _automaticScanWorkflow is null || _worker?.Capabilities.Contains("RealExclusiveDuplexValidationV2", StringComparer.Ordinal) != true ? Localization.Get("VmV2CapabilityUnavailable") : !HasCurrentDuplexPair ? Localization.Get("VmV2SelectPair") : BuildV2Route() is null ? Localization.Get("VmV2RouteInvalid") : Localization.Get("VmV2ReadyToQuery");
    public bool AutomaticScanPreparing { get => _automaticScanPreparing; private set { if (Set(ref _automaticScanPreparing, value)) { Changed(nameof(AutomaticScanBusy)); Changed(nameof(CanNavigateAwayFromAutomaticScan)); Changed(nameof(AutomaticScanAvailabilityReason)); CancelAutomaticScanCommand.Refresh(); PrepareAutomaticScanCommand.Refresh(); RefreshV4Commands(); } } }
    public bool AutomaticScanConsentPending { get => _automaticScanConsentPending; private set { if (Set(ref _automaticScanConsentPending, value)) { Changed(nameof(AutomaticScanConsentText)); ConfirmAutomaticScanCommand.Refresh(); CancelAutomaticScanConsentCommand.Refresh(); PrepareAutomaticScanCommand.Refresh(); RefreshV4Commands(); } } }
    public bool AutomaticScanConsentChecked { get => _automaticScanConsentChecked; set { if (Set(ref _automaticScanConsentChecked, value)) ConfirmAutomaticScanCommand.Refresh(); } }
    public bool AutomaticScanRunning { get => _automaticScanRunning; private set { if (Set(ref _automaticScanRunning, value)) { Changed(nameof(AutomaticScanBusy)); Changed(nameof(CanNavigateAwayFromAutomaticScan)); Changed(nameof(AutomaticScanAvailabilityReason)); CancelAutomaticScanCommand.Refresh(); ConfirmAutomaticScanCommand.Refresh(); PrepareAutomaticScanCommand.Refresh(); RefreshV4Commands(); } } }
    public bool AutomaticScanBusy => AutomaticScanPreparing || AutomaticScanRunning || V4ScanRunning || _v4Preparing;
    public IReadOnlyList<ChoiceOption> AutomaticSustainedModes { get; } = [new("Standard (300 s)", Localization.Get("VmAutomaticSustainedStandard")), new("Extended (600 s)", Localization.Get("VmAutomaticSustainedExtended"))];
    public string AutomaticSustainedMode { get => _automaticSustainedMode; set { if (AutomaticScanBusy || !AutomaticSustainedModes.Any(option => option.Value == value)) return; if (Set(ref _automaticSustainedMode, value)) { CancelAutomaticScanConsent(); Changed(nameof(AutomaticScanConsentText)); } } }
    public bool CanNavigateAwayFromAutomaticScan => !AutomaticScanBusy;
    public bool HasAutomaticScanResult => _automaticScanV2Result is not null || _automaticScanResult is not null || _automaticScanOutcomeUnknown && _automaticScanAttempted && _automaticScanTerminal;
    public bool AutomaticScanAttempted => _automaticScanAttempted;
    public ScanRunResult? AutomaticScanResult => _automaticScanResult;
    public IReadOnlyList<AutomaticScanProgressRow> AutomaticScanProgressRows => _automaticScanProgressRows;
    public string AutomaticScanStagePlanText => _automaticScanV2Result is { } v2 ? string.Join("\n", v2.Stages.Select(s => $"{AutomaticScanStageLabel(s.Stage)}: {AutomaticScanStageStateLabel(s.State)} · {V2ProgressMessageLabel(s.Message)}")) : (_automaticScanResult?.StageResults ?? _automaticScanPreparation?.StagedPlan?.Stages ?? []).Select(s => $"{AutomaticScanStageLabel(s.Stage)}: {AutomaticScanStageStateLabel(s.State)} · {AutomaticScanStageMessageLabel(s.Message)}").ToArray() is { Length: > 0 } rows ? string.Join("\n", rows) : Localization.Get("VmAutomaticScanPlanNotPrepared");
    public string AutomaticScanCandidateSummary => _automaticScanResult is { } result
        ? string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanCandidatesSummary"), result.CandidateInventory.Count, result.CandidateInventory.Count(c => c.State == AutomaticCandidateState.SUPPORTED_FOR_TEST), result.CandidateInventory.Count(c => c.State == AutomaticCandidateState.INITIALIZE_ACCEPTED), result.CandidateInventory.Count(c => c.State == AutomaticCandidateState.NOT_TESTED), result.CandidateInventory.Count(c => c.State == AutomaticCandidateState.UNKNOWN))
        : Localization.Get("VmAutomaticScanCandidatesAwaitQuery");
    public string AutomaticScanStatus => _automaticScanStatus;
    public string AutomaticScanAvailabilityReason
    {
        get
        {
            if (_automaticScanWorkflow is null) return Localization.Get("VmAutomaticScanUnavailable");
            if (AutomaticScanEngine == "V2_ADAPTIVE") return AutomaticScanV2Availability;
            if (_automaticScanOutcomeUnknown) return Localization.Get("VmAutomaticScanPreviousOutcomeUnknown");
            if (AutomaticScanBusy) return Localization.Get("VmAutomaticScanInProgress");
            if (DeviceScanConsentPending || RealInitializeConsentPending || DuplexConsentPending) return Localization.Get("VmAutomaticScanCancelOtherConsent");
            if (AutomaticScanPairEnabled)
                return SelectedDuplexCaptureEndpoint is null || SelectedDuplexRenderEndpoint is null ? AllowUnconfirmedPhysicalPair ? Localization.Get("VmAutomaticScanChooseUnconfirmedPair") : Localization.Get("VmAutomaticScanChoosePairEndpoints") : AllowUnconfirmedPhysicalPair ? Localization.Get("VmAutomaticScanCheckUnconfirmedPair") : Localization.Get("VmAutomaticScanCheckExactPair");
            if (SelectedWindowsEndpoint is not { EndpointId.Length: > 0, DeviceState: "ACTIVE" })
                return PhysicalDeviceGroups.Count == 0 ? Localization.Get("VmNoActiveAudioDevices") : SelectedPhysicalDeviceGroup is { RequiresExplicitCapture: true } ? Localization.Get("VmAutomaticScanChooseCaptureOrPair") : SelectedPhysicalDeviceGroup is { RequiresExplicitRender: true } ? Localization.Get("VmAutomaticScanChooseRenderOrPair") : Localization.Get("VmAutomaticScanChooseActiveEndpoint");
            if (WorkerState.State != WorkerConnectionState.ConnectedFake && _workerAttempted) return WorkerRestartExplanation;
            return Localization.Get("VmAutomaticScanPlanBoundToEndpoint");
        }
    }
    public string AutomaticScanConsentText
    {
        get
        {
            if (AutomaticScanEngine == "V2_ADAPTIVE" && _automaticScanV2Route is { } v2 && SelectedDuplexCaptureEndpoint?.EndpointId is { } v2Capture && SelectedDuplexRenderEndpoint?.EndpointId is { } v2Render)
            {
                var fingerprint = WorkerScanPlanV2Fingerprint.Route(v2Capture, v2Render, v2);
                var consent = string.Format(Localization.CurrentCulture, Localization.Get("VmV2ScanConsentSummary"), SelectedDuplexCaptureEndpoint.FriendlyNameDisplay, v2Capture,
                    SelectedDuplexRenderEndpoint.FriendlyNameDisplay, v2Render, v2.Mode, string.Join("/", v2.Capture.SelectedIndicesZeroBased.Select(i => i + 1)),
                    v2.Capture.Channels, v2.Capture.SampleRateHz, v2.Capture.Encoding, string.Join("/", v2.Render.SelectedIndicesZeroBased.Select(i => i + 1)),
                    v2.Render.Channels, v2.Render.SampleRateHz, v2.Render.Encoding, AutomaticScanV2QaMode == "AUTO_SHORT" ? "DEFAULT + MINIMUM" : AutomaticScanV2Period, fingerprint,
                    AutomaticScanV2QaMode, AutomaticScanV2QaMode == "FULL" ? 12 : AutomaticScanV2QaMode == "AUTO_SHORT" ? 6 : AutomaticScanV2QaMode == "SHORT" ? 3 : 0,
                    AutomaticScanV2QaMode == "FULL" ? 995 : AutomaticScanV2QaMode == "AUTO_SHORT" ? 10 : AutomaticScanV2QaMode == "SHORT" ? 5 : 0,
                    AutomaticScanV2QaMode == "FULL" ? 1200 : AutomaticScanV2QaMode == "AUTO_SHORT" ? 180 : 120);
                return AutomaticScanV2QaMode == "AUTO_SHORT" ? consent + " " + Localization.Get("VmV2QaAutoShortConsent") : consent;
            }
            if (_automaticScanPreparation is not { } prepared) return Localization.Get("VmAutomaticScanPlanMissing");
            var target = prepared.Plan.Target;
            var endpoints = new List<string>();
            if (target.Capture is { } capture) endpoints.Add($"{Localization.Get("VmCaptureLabel")}: {capture.FriendlyNameDisplay} · ID {capture.EndpointId}");
            if (target.Render is { } render) endpoints.Add($"{Localization.Get("VmRenderLabel")}: {render.FriendlyNameDisplay} · ID {render.EndpointId}");
            var operations = string.Join(", ", prepared.WorkerPlan.AllowedOperations.Select(ScanCommandLabel));
            var active = prepared.MaximumActiveSeconds == 0 ? Localization.Get("VmAutomaticScanActiveUnavailable") : string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanActiveBound"), prepared.MaximumActiveSeconds);
            var identity = Localization.Get(prepared.PairPhysicalIdentityUnconfirmed ? "VmAutomaticScanIdentityUnconfirmed" : "VmAutomaticScanIdentityCurrent");
            var stages = prepared.StagedPlan is { } staged ? string.Join(", ", staged.Stages.Select(s => $"{AutomaticScanStageLabel(s.Stage)}={AutomaticScanStageStateLabel(s.State)}")) : Localization.Get("VmAutomaticScanStagesSaved");
            var sustainedMode = AutomaticSustainedModes.Single(option => option.Value == AutomaticSustainedMode).Label;
            return string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanConsentSummary"), string.Join("\n", endpoints), identity, operations, sustainedMode, active, prepared.MaximumTotalOperations, prepared.MaximumWallClockSeconds, prepared.WorkerPlan.LifetimeSeconds, prepared.WorkerPlan.MaximumOperationCount, stages);
        }
    }
    public string AutomaticScanResultSummary => _automaticScanV2Result is { QaMode: "AUTO_SHORT", Lifecycle: "AUTO_SHORT_COMPLETED" } autoShortDone ? string.Format(Localization.CurrentCulture, Localization.Get("VmV2AutoShortResultSummary"), autoShortDone.Qualification) :
        AutomaticScanBusy && _automaticScanAttempted ? string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanResultScanning"), _automaticScanStatus) :
        _automaticScanV2Result is { Lifecycle: "UNKNOWN" } unknownV2 ? string.Format(Localization.CurrentCulture, Localization.Get("VmV2UnknownOperatorSummary"), V2StageLabel(unknownV2.WorkerError?.Stage ?? unknownV2.Operations.LastOrDefault(o => o.State == "UNKNOWN")?.Stage ?? "SHORT_DUPLEX"), unknownV2.WorkerError?.ErrorCode ?? Localization.Get("VmV2WorkerErrorCodeUnavailable"), Localization.Get("VmV2OperationCleanupUnconfirmed"), unknownV2.WorkerError?.WorkerState ?? Localization.Get("VmV2WorkerStateUnknown"), Localization.Get("VmV2OpenReportForDetails")) :
        _automaticScanOutcomeUnknown && _automaticScanAttempted ? string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanResultUnknown"), _automaticScanStatus) :
        _automaticScanV2Result is { } v2 ? string.Format(Localization.CurrentCulture, Localization.Get("VmV2ResultSummary"), V2LifecycleLabel(v2.Lifecycle), V2QaModeSummaryLabel(v2.QaMode), v2.Period, v2.Operations.Count(o => o.State == "COMPLETED"), V2ResultErrorLabel(v2)) :
        _automaticScanResult is { } result ? AutomaticScanSummaryDisplay(result) :
        _automaticScanAttempted ? string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanResultUnknown"), _automaticScanStatus) : Localization.Get("VmAutomaticScanResultMissing");
    public string AutomaticScanResultHeadline => _automaticScanV2Result is { Lifecycle: "UNKNOWN" } ? Localization.Get("VmV2CheckStopped") : _automaticScanV2Result is { } v2Headline ? V2LifecycleLabel(v2Headline.Lifecycle) : _automaticScanResult?.Lifecycle switch
    {
        ScanRunLifecycle.Completed => Localization.Get("VmAutomaticScanCompleted"),
        ScanRunLifecycle.Incomplete => Localization.Get("VmAutomaticScanPartiallyCompleted"),
        ScanRunLifecycle.Cancelled => Localization.Get("VmAutomaticScanCancelled"),
        ScanRunLifecycle.Failed => Localization.Get("VmAutomaticScanFailed"),
        ScanRunLifecycle.Unknown => Localization.Get("VmAutomaticScanOutcomeUnknown"),
        _ => Localization.Get("VmAutomaticScanResultMissing")
    } ?? (_automaticScanOutcomeUnknown && _automaticScanAttempted ? Localization.Get("VmAutomaticScanOutcomeUnknown") : Localization.Get("VmAutomaticScanResultMissing"));
    public string AutomaticScanResultDevice => _automaticScanV2Result is { } v2Result ? $"{v2Result.CaptureFriendlyName} + {v2Result.RenderFriendlyName}" : SelectedPhysicalDeviceGroup?.DisplayName ?? (_automaticScanResult is not { } result ? OperatorSelectedDevice :
        string.Join(" + ", new[] { result.Target.Capture?.FriendlyNameDisplay, result.Target.Render?.FriendlyNameDisplay }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal)));
    public string AutomaticScanRecommendationText => _automaticScanV2Result is { } v2Result ? V2RecommendationText(v2Result) : _automaticScanResult is not { } result ? Localization.Get("VmAutomaticScanRecommendationMissing") : result.Recommendation.Preferred is { } preferred
        ? string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanRecommendationSelected"), result.Recommendation.Scope, preferred.Period, preferred.SampleRate / 1000, preferred.Encoding, preferred.CaptureBufferFrames, preferred.RenderBufferFrames, preferred.Mmcss, preferred.DurationSeconds, preferred.Confidence, result.Recommendation.Fallback is { } fallback ? $"{fallback.Period} · MMCSS {fallback.Mmcss}" : "NONE", string.Join(" ", result.Recommendation.Reasons.Select(RecommendationReasonLabel)))
        : string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanFinalConfigurationMissing"), string.Join(" ", result.Recommendation.Reasons.Select(RecommendationReasonLabel)));
    public bool AutomaticScanAutoShortVisible => _automaticScanV2Result?.QaMode == "AUTO_SHORT";
    public string AutomaticScanRecommendationPeriodDisplay
    {
        get
        {
            if (_automaticScanV2Result is not { QaMode: "AUTO_SHORT", RecommendedPeriod: { } period } result) return Localization.Get("VmAutomaticScanRecommendationMissing");
            var candidate = result.AutoShortCandidates.FirstOrDefault(c => c.Period == period);
            return candidate?.Period100ns is > 0 ? FormatPeriodMilliseconds(candidate.Period100ns.Value) : Localization.Get("VmValueUnknown");
        }
    }
    public IReadOnlyList<V2AutoShortCandidateDisplay> AutomaticScanAutoShortCandidates => (_automaticScanV2Result?.AutoShortCandidates ?? []).Select(c =>
        new V2AutoShortCandidateDisplay($"{c.Period} · {FormatPeriodMilliseconds(c.Period100ns)} / {FormatPeriodMilliseconds(c.RenderPeriod100ns)}", Localization.Get(c.Status switch
        {
            "SHORT_CLEAN" => "VmAutoCandidateClean", "SHORT_FAILED" => "VmAutoCandidateFailed", "UNKNOWN" => "VmAutoCandidateUnknown", _ => "VmAutoCandidateUnsupported"
        }), c.BufferSummary, Localization.Get(c.Status switch
        {
            "SHORT_CLEAN" => "VmAutoCandidateReasonClean", "SHORT_FAILED" => "VmAutoCandidateReasonFailed", "UNKNOWN" => "VmAutoCandidateReasonUnknown", _ => "VmAutoCandidateReasonUnsupported"
        }), $"{Localization.Get("VmAutoCandidateDetailsSupport")}: {c.SupportSummary}; {Localization.Get("VmAutoCandidateDetailsPeriods")}: {FormatPeriodMilliseconds(c.Period100ns)} / {FormatPeriodMilliseconds(c.RenderPeriod100ns)}; {Localization.Get("VmAutoCandidateDetailsReason")}: {c.Reason}")).ToArray();
    public string AutomaticScanPhysicalLatencyText => Localization.Get("VmAutomaticScanLatencyNotMeasured");
    public string AutomaticScanResultProvenance
    {
        get
        {
            if (_automaticScanV2Result is { } v2)
            {
                var sources = v2.Operations.Select(o => o.Provenance).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToArray();
                return sources.Length > 0 && sources.All(p => p!.StartsWith("REAL_WASAPI_", StringComparison.Ordinal)) ? Localization.Get("VmAutomaticScanProvenanceReal") : Localization.Get("VmAutomaticScanProvenanceUnknown");
            }
            if (_automaticScanResult is not { } result) return _automaticScanAttempted ? Localization.Get("VmAutomaticScanWorkerOutcomeUnconfirmed") : Localization.Get("VmAutomaticScanDataMissing");
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
    public string AutomaticScanPersistenceText => _automaticScanV2Result is { } v2Result ? string.Format(Localization.CurrentCulture, Localization.Get("VmV2EvidencePath"), v2Result.EvidencePath ?? Localization.Get("VmNotSaved")) : _automaticScanResult is null
        ? _automaticScanOutcomeUnknown ? Localization.Get("VmAutomaticScanSaveUnknown") : _automaticScanAttempted ? Localization.Get("VmAutomaticScanSaveUnconfirmed") : Localization.Get("VmAutomaticScanReportNotSaved")
        : _automaticScanResult.Persistence is not { } saved ? Localization.Get("VmAutomaticScanReportNotSaved") : saved.Status == "SAVE_FAILED"
            ? string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanPersistenceFailed"), saved.Error ?? Localization.Get("VmAutomaticScanErrorUnavailable"))
            : saved.Status == "SUMMARY_SAVED_PROFILE_FAILED"
                ? string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanProfileNotCreated"), saved.SummaryPath ?? Localization.Get("VmAutomaticScanPathUnknown"), saved.Error ?? Localization.Get("VmAutomaticScanErrorUnavailable"))
            : saved.Status == "SAVED_AND_RELOADED"
                ? string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanSavedAndReloaded"), saved.SummaryPath ?? Localization.Get("VmAutomaticScanPathUnknown"), saved.ProfilePaths.Count == 0 ? Localization.Get("VmAutomaticScanProfileNotCreatedShort") : string.Join("\n", saved.ProfilePaths))
            : string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanSaveStatus"), saved.Status, saved.Error ?? Localization.Get("VmAutomaticScanStateUnconfirmed"));
    public string AutomaticScanTechnicalDetails
    {
        get
        {
            if (_automaticScanV2Result is { } v2)
                return string.Join("\n", v2.Operations.Select(o => $"{o.Stage} · {o.State} · {o.Period} · {o.DurationSeconds} · {Localization.Get("CleanupLabel")}={o.Cleanup} · {o.Provenance}")) + $"\nroute={v2.RouteFingerprint}\n{v2.Error}" + (v2.WorkerError is { } d ? $"\nworkerCode={d.ErrorCode ?? "UNKNOWN"}\nworkerMessage={d.ErrorMessage ?? "UNKNOWN"}\nexceptionType={d.ExceptionType}\nstage={d.Stage}\nsessionId={d.SessionId}\noperationId={d.OperationId}\nipcState={d.IpcState}\nworkerState={d.WorkerState}\ncancellation={d.CancellationOutcome}\nsessionCleanup={d.SessionCleanupOutcome}" : string.Empty);
            if (_automaticScanResult is not { } result)
                return _automaticScanAttempted ? _automaticScanStatus : Localization.Get("VmAutomaticScanTechnicalDetailsMissing");
            var lines = result.Operations.Select(o => $"{o.Operation.Stage} · {o.Lifecycle} · {o.Operation.OperationId} · {o.Operation.EndpointId ?? "PAIR"} · {Localization.Get("CleanupLabel")}={o.CleanupStatus} · {o.Message}").ToList();
            if (result.PeriodDiscoveryDiagnostics is { } diagnostics)
            {
                lines.Add("PERIOD_DISCOVERY · " + diagnostics.Summary);
                lines.AddRange(diagnostics.Operations.Select(o => $"PERIOD_DISCOVERY · #{o.OperationIndex} · {o.Direction} · {o.Outcome} · {o.DurationMilliseconds} {Localization.Get("VmAutomaticScanMillisecondsAbbreviation")} · {Localization.Get("VmAutomaticScanTimeoutLabel")}={o.TimeoutSeconds} {Localization.Get("VmInspectorSecondsAbbreviation")}" + (o.WorkerErrorCode is null ? string.Empty : $" · {Localization.Get("VmWorkerLabel")}={o.WorkerErrorCode}")));
            }
            return string.Join("\n", lines);
        }
    }
    public bool AutomaticScanExportRunning => _automaticScanExportRunning;
    public bool CanExportAutomaticScan => !_automaticScanExportRunning && _bundleExportInteraction is not null &&
        (_automaticScanV2Result?.EvidencePath is { Length: > 0 } v2Path && File.Exists(v2Path) ||
         _automaticScanResult is { Lifecycle: ScanRunLifecycle.Completed, Persistence: { Status: "SAVED_AND_RELOADED", SummaryPath: not null } });
    public string AutomaticScanExportStatus => HasAutomaticScanResult && new[] { Localization.Get("VmScanExportInvalidatedByNewScan", CultureInfo.GetCultureInfo("en-US")), Localization.Get("VmScanExportInvalidatedByNewScan", CultureInfo.GetCultureInfo("ru-RU")) }.Contains(_automaticScanExportStatus, StringComparer.Ordinal) ? string.Empty : _automaticScanExportStatus;
    public string AutomaticScanExportTechnicalDetails => _automaticScanExportTechnicalDetails;
    public DeviceScanResult? DeviceScanResult => _deviceScanResult;
    public bool HasDeviceScanResult => _deviceScanResult is not null;
    public IReadOnlyList<RealFormatCandidateDto> DeviceScanInitializeCandidates => _deviceScanResult?.Initializations.SelectMany(r => r.Candidates).ToArray() ?? [];
    public string DeviceScanProgress => _deviceScanProgress;
    public string DeviceScanConsentText => SelectedWindowsEndpoint is { } e ? string.Format(Localization.CurrentCulture, Localization.Get("VmDeviceScanConsent"), e.FriendlyNameDisplay, DirectionDisplayLabel(e.Direction), DeviceConfigurationScan.MaximumInitializeOperations, DeviceConfigurationScan.MaximumDurationSeconds) : Localization.Get("VmChooseActiveWindowsDevice");
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
            return string.Format(Localization.CurrentCulture, Localization.Get("VmDeviceScanResultSummary"), DeviceScanStageLabel(scan.State), supported, initialized, failed, streamRuns == 0 ? "NOT_RUN" : string.Format(Localization.CurrentCulture, Localization.Get("VmDeviceScanStreamObservations"), streamRuns));
        }
    }
    public string DeviceScanTechnicalDetails => _deviceScanResult is not { } scan ? Localization.Get("VmDeviceScanDetailsMissing") : string.Join("\n", scan.Initializations.SelectMany(r => r.Candidates).Select(c => $"{c.Encoding} {c.SampleRate} {Localization.Get("VmInspectorHertz")} {Localization.Get("VmInspectorChannelAbbreviation")}={c.Channels} {c.Period}: {Localization.Get("StatusLabel")}={c.Status}; {Localization.Get("VmDeviceScanDetailSupport")}={c.SupportStatus} IsFormatSupported={c.SupportHr ?? "unknown"}; Initialize={c.InitializeStatus} {Localization.Get("VmDeviceScanDetailFirst")}={c.FirstInitializeHr ?? "unknown"}; {Localization.Get("VmDeviceScanDetailAlignment")} GetBufferSize={c.AlignmentGetBufferSizeHr ?? "not observed"}/{c.AlignmentFrames?.ToString() ?? "unknown"} {Localization.Get("VmDeviceScanDetailFrames")}, Activate={c.AlignmentActivateHr ?? "not observed"}, {Localization.Get("VmDeviceScanDetailPeriod100ns")}={c.AlignmentPeriod100ns?.ToString() ?? "unknown"}, Initialize={c.AlignmentInitializeHr ?? "not observed"}; {Localization.Get("VmDeviceScanDetailFinal")}={c.FinalInitializeHr ?? "unknown"}; {Localization.Get("VmDeviceScanDetailRequestedPeriod")}={c.RequestedPeriod100ns?.ToString() ?? "unknown"}; {Localization.Get("VmDeviceScanDetailActualPeriod")}={c.ActualPeriod100ns?.ToString() ?? "unknown"}; {Localization.Get("VmDeviceScanDetailBufferFrames")}={c.ActualBufferFrames?.ToString() ?? "unknown"}; SetEventHandle={c.SetEventHandleHr ?? "not observed"}; {Localization.Get("VmDeviceScanDetailEventReady")}={c.EventHandleReady?.ToString() ?? "unknown"}; {Localization.Get("CleanupLabel")}={c.CleanupStatus}; {Localization.Get("VmDeviceScanDetailReleases")}={c.ClientReleaseAttempts}"));
    public bool CanSaveDeviceProfile => !_duplexValidationRunning && (HasDuplexPairProfileEvidence || HasCurrentOperatorQueryResult && CanUseSelectedWindowsEndpoint && !_realQueryRunning && !_realInitializeRunning && !_deviceScanRunning && !WorkerOperationActive && SelectedWindowsEndpoint is { } endpoint && RealFormatMatrix is { } query && query.EndpointId == endpoint.EndpointId && query.Direction == endpoint.Direction && (RealInitializationResult is null || RealInitializationResult.EndpointId == endpoint.EndpointId && RealInitializationResult.Direction == endpoint.Direction && _realInitializationError is null));
    public string DeviceProfileSaveMessage => _deviceProfileSaveMessage;
    public DeviceConfigurationProfile CreateCurrentDeviceProfile()
    {
        if (HasDuplexPairProfileEvidence) return DeviceConfigurationProfileFactory.CreateDuplex(SelectedDuplexCaptureEndpoint!, SelectedDuplexRenderEndpoint!, _duplexValidations.Values.OrderBy(v => v.Report.GetProperty("requested").GetProperty("period").GetString() == "MINIMUM" ? 0 : 1).ToArray());
        if (!CanSaveDeviceProfile || SelectedWindowsEndpoint is not { } endpoint || RealFormatMatrix is not { } query)
            throw new InvalidOperationException(Localization.Get("VmDeviceProfileCurrentResultRequired"));
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
        _deviceProfileSaveMessage = string.Format(Localization.CurrentCulture, Localization.Get("VmDeviceProfileSaved"), profile.SchemaVersion, path);
        Changed(nameof(DeviceProfileSaveMessage)); Changed(nameof(StreamEvidencePersistenceStatus));
    }
    public bool HasOperatorQueryResult => RealFormatMatrix is not null && RealInitializationResult is null && _realFormatError is null;
    public bool HasCurrentOperatorQueryResult => RealFormatMatrix is { Provenance: "REAL_WASAPI_QUERY", ReportLevel: "PASSIVE_QUERY" } matrix && _realFormatError is null && !RealFormatMatrixIsStale && SelectedWindowsEndpoint is { } selected && selected.EndpointId == matrix.EndpointId && selected.Direction == matrix.Direction;
    public bool HasCurrentDuplexPair => _windowsDiscoveryCurrent && WorkerState.State == WorkerConnectionState.ConnectedFake && SelectedDuplexCaptureEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } && SelectedDuplexRenderEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } && SelectedDuplexCaptureEndpoint.EndpointId != SelectedDuplexRenderEndpoint.EndpointId && DuplexCaptureEndpoints.Contains(SelectedDuplexCaptureEndpoint) && DuplexRenderEndpoints.Contains(SelectedDuplexRenderEndpoint);
    public bool HasDuplexPairProfileEvidence => !_duplexValidationRunning && _duplexValidations.Count > 0 && SelectedDuplexCaptureEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } capture && SelectedDuplexRenderEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } render && _duplexValidations.Values.All(v => v.CaptureEndpointId == capture.EndpointId && v.RenderEndpointId == render.EndpointId && v.Report.GetProperty("provenance").GetString() == "REAL_WASAPI_DUPLEX");
    public string WorkerStateDisplayMessage => WorkerState.State == WorkerConnectionState.Disconnected && WorkerState.Message == Localization.Get("VmWorkerDisconnectedDefaultSource", CultureInfo.GetCultureInfo("ru-RU")) ? Localization.Get("VmWorkerDisconnectedMessage") : WorkerState.Message;
    public string DuplexWorkerAvailability => WorkerState.State == WorkerConnectionState.ConnectedFake ? Localization.Get("VmWorkerConnected") : $"{Localization.Get("VmWorkerLabel")}: {WorkerState.State switch { WorkerConnectionState.Disconnected => Localization.Get("VmWorkerDisconnected"), WorkerConnectionState.Starting => Localization.Get("VmWorkerStarting"), WorkerConnectionState.Connecting => Localization.Get("VmWorkerConnecting"), WorkerConnectionState.Handshaking => Localization.Get("VmWorkerHandshaking"), WorkerConnectionState.Stopping => Localization.Get("VmWorkerStopping"), WorkerConnectionState.Incompatible => Localization.Get("VmWorkerIncompatible"), _ => Localization.Get("VmWorkerError") }} · {WorkerStateDisplayMessage}";
    public string DuplexDiscoveryFreshness => _windowsDiscoveryCurrent ? Localization.Get("VmWindowsDiscoveryCurrent") : _windowsDiscoveryLoading ? Localization.Get("VmWindowsDiscoveryRefreshing") : _windowsDiscoveryError is { Length: > 0 } error ? string.Format(Localization.CurrentCulture, Localization.Get("VmWindowsDiscoveryError"), error) : Localization.Get("VmWindowsDiscoveryStale");
    public string DuplexEvidenceFreshness => $"{DuplexWorkerAvailability} · {DuplexDiscoveryFreshness}";
    public bool NeedsInitializeFormatQuery => OperatorTestMode == "INITIALIZE_ONLY" && !HasCurrentOperatorQueryResult;
    public bool HasOperatorInitializeResult => RealInitializationResult is not null && _realInitializationError is null;
    public string OperatorResultMode => _operatorResultMode switch
    {
        OperatorResultKind.Duplex => Localization.Get("VmOperatorModeDuplex"),
        OperatorResultKind.DeviceScan => Localization.Get("VmOperatorModeDeviceScan"),
        OperatorResultKind.FormatQuery => Localization.Get("VmOperatorModeFormatQuery"),
        OperatorResultKind.InitializeOnly => Localization.Get("VmOperatorModeInitializeOnly"),
        _ => string.Empty
    };
    public string OperatorResultConfiguration => _operatorResultConfiguration;
    public string OperatorSelectedDevice => WorkflowStep == OperatorStep.Result && _operatorResultEndpoint.Length > 0 ? _operatorResultEndpoint : SelectedWindowsEndpoint is { } e ? $"{e.FriendlyNameDisplay} · {DirectionDisplayLabel(e.Direction)} · {e.DeviceState}" : SelectedPhysicalDeviceGroup?.DisplayName ?? RealFormatEndpointLabel;
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
    public string MeterStatusText => Meter is null || !Meter.IsCompleteWindow ? Localization.Get("VmMeterWindowMissing") : Session?.Lifecycle switch
    {
        SessionLifecycle.Unknown => string.Format(Localization.CurrentCulture, Localization.Get("VmMeterStale"), Meter.Sequence),
        SessionLifecycle.Stopped or SessionLifecycle.Completed or SessionLifecycle.Failed => string.Format(Localization.CurrentCulture, Localization.Get("VmMeterFinal"), Meter.Sequence),
        SessionLifecycle.Stopping => string.Format(Localization.CurrentCulture, Localization.Get("VmMeterStopping"), Meter.Sequence),
        _ when Meter.Channels.All(c => c.Silent == true) => string.Format(Localization.CurrentCulture, Localization.Get("VmMeterDigitalSilence"), Meter.Sequence),
        _ => string.Format(Localization.CurrentCulture, Localization.Get("VmMeterDemoLive"), Meter.Sequence)
    };
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool ConfirmPending { get => _confirmPending; private set { if (Set(ref _confirmPending, value)) { ConfirmCommand.Refresh(); CancelConfirmationCommand.Refresh(); BeginCommand.Refresh(); } } }
    public bool ConsentChecked { get => _consentChecked; set { if (Set(ref _consentChecked, value)) ConfirmCommand.Refresh(); } }
    public bool IsRunning => Session?.Lifecycle is SessionLifecycle.Preparing or SessionLifecycle.Started or SessionLifecycle.Stopping;
    public int RenderChannel { get => _renderChannel; set { if (RenderChannelOptions.Any(o => o.Value == value) && Set(ref _renderChannel, value)) { Changed(nameof(RenderChannelOptions)); var bank = (value - 1) / 8; if (_renderBank != bank) { _renderBank = bank; Changed(nameof(RenderChannelBank)); } RefreshContext(); } } }
    public int DurationSeconds { get => _durationSeconds; set { if (DurationOptions.Any(o => o.Value == value) && Set(ref _durationSeconds, value)) RefreshContext(); } }
    public int RenderChannelBank { get => _renderBank; set { if (value >= 0 && value < RenderChannelBanks.Count && Set(ref _renderBank, value)) { Changed(nameof(RenderChannelOptions)); RefreshContext(); } } }
    public int CaptureChannelBank { get => _captureBank; set { if (value >= 0 && value < CaptureChannelBanks.Count && Set(ref _captureBank, value)) Changed(nameof(VisibleMeterChannels)); } }
    public IReadOnlyList<SelectionOption> CaptureChannelBanks => Enumerable.Range(0, Math.Max(1, ((Session?.ChannelCount ?? SelectedCaptureEndpoint?.Topology?.ChannelCount ?? SelectedCaptureEndpoint?.Channels ?? 2) + 7) / 8)).Select(bank => new SelectionOption(bank, string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterBankRange"), bank * 8 + 1, Math.Min(bank * 8 + 8, Session?.ChannelCount ?? SelectedCaptureEndpoint?.Topology?.ChannelCount ?? SelectedCaptureEndpoint?.Channels ?? 2)))).ToArray();
    public IReadOnlyList<MeterChannelDto> VisibleMeterChannels => Meter?.Channels.Where(c => c.Channel > _captureBank * 8 && c.Channel <= _captureBank * 8 + 8).ToArray() ?? [];
    public IReadOnlyList<SelectionOption> RenderChannelBanks => Enumerable.Range(0, Math.Max(1, ((SelectedRenderEndpoint?.Topology?.ChannelCount ?? SelectedRenderEndpoint?.Channels ?? 2) + 7) / 8)).Select(bank => new SelectionOption(bank, string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterBankRange"), bank * 8 + 1, Math.Min(bank * 8 + 8, SelectedRenderEndpoint?.Topology?.ChannelCount ?? SelectedRenderEndpoint?.Channels ?? 2)))).ToArray();
    public IReadOnlyList<SelectionOption> RenderChannelOptions => Enumerable.Range(_renderBank * 8 + 1, Math.Min(8, Math.Max(1, SelectedRenderEndpoint?.Topology?.ChannelCount ?? SelectedRenderEndpoint?.Channels ?? 2) - _renderBank * 8)).Select(i => new SelectionOption(i, string.Format(Localization.CurrentCulture, Localization.Get("VmWindowsChannelOption"), i))).ToArray();
    public WorkerStateDto WorkerState { get; private set; } = new(WorkerConnectionState.Disconnected, Localization.Get("VmWorkerInitialDisconnected"));
    public IReadOnlyList<WorkerEndpointDto> WorkerEndpoints { get => _workerEndpoints; private set => Set(ref _workerEndpoints, value); }
    public WorkerEndpointDto? SelectedWorkerEndpoint { get => _selectedWorkerEndpoint; set { if (value is not null && !WorkerEndpoints.Contains(value)) return; if (Set(ref _selectedWorkerEndpoint, value)) { _selectedWorkerFormat = value?.FormatTopologies?.FirstOrDefault(); _workerRenderChannel = 1; Changed(nameof(SelectedWorkerFormat)); Changed(nameof(WorkerFormatOptions)); Changed(nameof(WorkerChannelCount)); Changed(nameof(WorkerChannelSummary)); Changed(nameof(WorkerRenderChannelVisible)); Changed(nameof(WorkerRenderChannel)); Changed(nameof(WorkerRenderChannelOptions)); RefreshWorkerCommands(); } } }
    public FormatTopologyDto? SelectedWorkerFormat { get => _selectedWorkerFormat; set { if (value is not null && !WorkerFormatOptions.Contains(value)) return; if (Set(ref _selectedWorkerFormat, value)) { if (_workerRenderChannel < 1 || _workerRenderChannel > WorkerChannelCount) _workerRenderChannel = 1; Changed(nameof(WorkerChannelCount)); Changed(nameof(WorkerChannelSummary)); Changed(nameof(WorkerRenderChannel)); Changed(nameof(WorkerRenderChannelOptions)); RefreshWorkerCommands(); } } }
    public IReadOnlyList<FormatTopologyDto> WorkerFormatOptions => SelectedWorkerEndpoint?.FormatTopologies ?? [];
    public int WorkerChannelCount => SelectedWorkerFormat?.Topology.ChannelCount ?? 0;
    public string WorkerChannelSummary => SelectedWorkerEndpoint is { } endpoint && WorkerChannelCount > 0 ? string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerChannelSummary"), DirectionDisplayLabel(endpoint.Direction), SelectedWorkerFormat!.FormatId, WorkerChannelCount) : Localization.Get("VmWorkerFormatNotSelected");
    public bool WorkerRenderChannelVisible => SelectedWorkerEndpoint?.Direction == "RENDER";
    public bool WorkerCurrentEndpointsVisible => WorkerState.State == WorkerConnectionState.ConnectedFake;
    public IReadOnlyList<WorkerEndpointDto> LastWorkerEndpointSnapshot { get => _lastWorkerEndpointSnapshot; private set { if (Set(ref _lastWorkerEndpointSnapshot, value)) { Changed(nameof(HasHistoricalWorkerEndpoints)); Changed(nameof(HistoricalWorkerEndpointNames)); } } }
    public bool HasHistoricalWorkerEndpoints => WorkerState.State == WorkerConnectionState.Disconnected && LastWorkerEndpointSnapshot.Count > 0;
    public IReadOnlyList<WindowsEndpointDto> WindowsEndpoints => _windowsDiscoveryCurrent ? _windowsEndpointSnapshot?.Endpoints ?? [] : [];
    public IReadOnlyList<PhysicalDeviceGroupDto> PhysicalDeviceGroups
    {
        get
        {
            var active = _physicalDeviceGroups.Where(group => group.CaptureEndpoints.Concat(group.RenderEndpoints).Any(PhysicalDeviceGrouping.IsActiveEndpoint)).ToArray();
            if (active.Length == _physicalDeviceGroups.Count && active.Select(group => group.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == active.Length) return _physicalDeviceGroups;
            return active.GroupBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
                .SelectMany(groups => groups.Count() == 1 ? groups : groups.OrderBy(group => group.GroupId, StringComparer.OrdinalIgnoreCase)
                    .Select((group, index) => group with { DisplayName = string.Format(Localization.CurrentCulture, Localization.Get("VmPhysicalGroupDuplicateName"), group.DisplayName, index + 1) }))
                .OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.GroupId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
    public string PhysicalDeviceGroupStatusText => PhysicalDeviceGroups.Count == 0 ? Localization.Get("VmNoActiveAudioDevices") : Localization.Get("VmPhysicalGroupEndpointNote");
    public PhysicalDeviceGroupDto? SelectedPhysicalDeviceGroup
    {
        get => _selectedPhysicalDeviceGroup;
        set
        {
            if (AutomaticScanBusy || value is not null && !PhysicalDeviceGroups.Any(group => group.GroupId == value.GroupId)) return;
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
            Changed(nameof(AutomaticScanPairEnabled)); Changed(nameof(PhysicalGroupRequiresExplicitPair)); Changed(nameof(ShowNormalPairSelection)); Changed(nameof(AllowUnconfirmedPhysicalPair)); Changed(nameof(DuplexCaptureEndpoints)); Changed(nameof(DuplexRenderEndpoints)); RefreshV2EndpointBindings(); Changed(nameof(AutomaticScanAvailabilityReason)); PrepareAutomaticScanCommand.Refresh(); RefreshContext(); RefreshCanExecute();
        }
    }
    public bool PhysicalGroupRequiresExplicitPair => SelectedPhysicalDeviceGroup?.RequiresExplicitPair == true;
    public bool PhysicalGroupRequiresExplicitDirection => SelectedPhysicalDeviceGroup is { RequiresExplicitCapture: true } or { RequiresExplicitRender: true };
    public IReadOnlyList<PhysicalDeviceEndpointDetailDto> PhysicalDeviceGroupDetails => _physicalDeviceGroups
        .SelectMany(group => group.CaptureEndpoints.Concat(group.RenderEndpoints).Select(endpoint => new PhysicalDeviceEndpointDetailDto(
            group.GroupId, group.DisplayName, endpoint.EndpointId ?? Localization.Get("VmValueUnknown"), endpoint.Direction, endpoint.DeviceState,
            endpoint.FormFactor, endpoint.FormFactorEvidence.State, endpoint.ContainerId ?? Localization.Get("VmValueUnknown"), endpoint.ContainerIdEvidence?.State ?? "UNAVAILABLE",
            EndpointCandidateReason(endpoint)))).ToArray();
    public bool CanOfferUnconfirmedPhysicalPair => SelectedPhysicalDeviceGroup is { } group && !group.CanAutoSelectPair &&
        WindowsEndpoints.Any(e => e.Direction == "CAPTURE" && PhysicalDeviceGrouping.IsSelectableCapture(e) && PhysicalDeviceGrouping.IsActiveEndpoint(e)) &&
        WindowsEndpoints.Any(e => e.Direction == "RENDER" && PhysicalDeviceGrouping.IsActiveEndpoint(e)) &&
        (group.ActiveCaptureSelectableCount == 0 || group.ActiveRenderCount == 0 || group.RequiresExplicitCapture || group.RequiresExplicitRender);
    public bool ShowNormalPairSelection => PhysicalGroupRequiresExplicitPair || PhysicalGroupRequiresExplicitDirection || CanOfferUnconfirmedPhysicalPair;
    public bool ShowNormalPairSelectionForCurrentEngine => AutomaticScanV1ControlsVisible && ShowNormalPairSelection;
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
        if (endpoint.Direction == "CAPTURE" && PhysicalDeviceGrouping.IsLoopbackEndpoint(endpoint)) return Localization.Get("VmEndpointCandidateLoopbackExcluded");
        if (!PhysicalDeviceGrouping.IsActiveEndpoint(endpoint)) return Localization.Get("VmEndpointCandidateInactive");
        if (endpoint.Direction == "CAPTURE" && !PhysicalDeviceGrouping.IsEligibleCapture(endpoint)) return Localization.Get("VmEndpointCandidateCaptureExplicit");
        return Localization.Get(endpoint.Direction == "CAPTURE" ? "VmEndpointCandidateCapture" : endpoint.Direction == "RENDER" ? "VmEndpointCandidateRender" : "VmEndpointCandidateUnknownDirection");
    }
    public WindowsEndpointDto? SelectedDuplexCaptureEndpoint { get => _selectedDuplexCapture; set => SetDuplexEndpoint(ref _selectedDuplexCapture, value, true); }
    public WindowsEndpointDto? SelectedDuplexRenderEndpoint { get => _selectedDuplexRender; set => SetDuplexEndpoint(ref _selectedDuplexRender, value, false); }
    public bool DuplexConsentPending { get => _duplexConsentPending; private set { if (Set(ref _duplexConsentPending, value)) { Changed(nameof(DuplexConsentText)); ConfirmDuplexValidationCommand.Refresh(); RefreshV4Commands(); } } }
    public bool DuplexConsentChecked { get => _duplexConsentChecked; set { if (Set(ref _duplexConsentChecked, value)) ConfirmDuplexValidationCommand.Refresh(); } }
    public bool DuplexValidationRunning => _duplexValidationRunning;
    public string DuplexStatus => _duplexStatus;
    public string DuplexConsentText => SelectedDuplexCaptureEndpoint is { } c && SelectedDuplexRenderEndpoint is { } r && _pendingDuplexPeriod is { } p ? string.Format(Localization.CurrentCulture, Localization.Get("VmDuplexConsent"), c.FriendlyNameDisplay, c.EndpointId, r.FriendlyNameDisplay, r.EndpointId, p) : Localization.Get("VmDuplexSelectActivePair");
    public IReadOnlyList<DuplexCandidateAssessment> DuplexAssessments => _duplexValidations.Values.Select(v => DeviceConfigurationEvaluator.Evaluate(v.Report.GetProperty("requested").GetProperty("period").GetString()!, v.Report, v.CaptureEndpointId, v.RenderEndpointId, v.Report.GetProperty("requested").GetProperty("mmcss").GetString()!)).OrderBy(x => x.Period == "MINIMUM" ? 0 : 1).ToArray();
    public IReadOnlyList<DuplexResultCard> DuplexResultCards => [DeviceConfigurationEvaluator.ProjectCard("MINIMUM", _duplexValidations.GetValueOrDefault("MINIMUM")), DeviceConfigurationEvaluator.ProjectCard("DEFAULT", _duplexValidations.GetValueOrDefault("DEFAULT"))];
    public DuplexRecommendation DuplexRecommendation => DeviceConfigurationEvaluator.Recommend(DuplexAssessments);
    public bool HasDuplexResults => _duplexValidations.Count > 0;
    public string DuplexRecommendationSummary => DuplexRecommendation.Preferred is { } p ? string.Format(Localization.CurrentCulture, Localization.Get("VmDuplexRecommendationSummary"), p.Period, p.Classification, string.Join(" ", DuplexRecommendation.Reasons.Select(RecommendationReasonLabel))) : string.Join(" ", DuplexRecommendation.Reasons.Select(RecommendationReasonLabel));
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
        : string.Format(Localization.CurrentCulture, Localization.Get("VmRepeatabilityPlanEndpoints"), preparation.Plan.Target.Capture!.FriendlyNameDisplay, preparation.Plan.Target.Capture.EndpointId, preparation.Plan.Target.Render!.FriendlyNameDisplay, preparation.Plan.Target.Render.EndpointId) + "\n" +
          string.Format(Localization.CurrentCulture, Localization.Get("VmRepeatabilityPlanFormat"), preparation.WorkerPlan.Formats.Single().Encoding, preparation.WorkerPlan.Formats.Single().Channels) + "\n" +
          Localization.Get("VmRepeatabilityPlanOrder") + "\n" +
          Localization.Get("VmRepeatabilityPlanTimeLimit") + "\n" +
          string.Format(Localization.CurrentCulture, Localization.Get("VmRepeatabilityPlanWorkerSession"), preparation.WorkerPlan.LifetimeSeconds, preparation.WorkerPlan.MaximumOperationDurationSeconds, preparation.WorkerPlan.MaximumOperationCount) + "\n" +
          Localization.Get("VmRepeatabilityPlanSafetyLimits");
    public string DuplexRepeatabilityResultText => _duplexRepeatabilityResult is not { } result ? Localization.Get("VmRepeatabilityResultMissing") : $"{RepeatabilitySummaryDisplay(result)}\n{Localization.Get("VmReportLabel")}: {result.ReportPath ?? Localization.Get("VmNotSaved")}\n{Localization.Get("VmPersistenceLabel")}: {result.PersistenceStatus ?? Localization.Get("VmNotConfirmed")}{(result.PersistenceError is null ? "" : $" · {result.PersistenceError}")}";
    public bool PeriodDiscoveryConsentPending => _periodDiscoveryConsentPending;
    public bool PeriodDiscoveryConsentChecked { get => _periodDiscoveryConsentChecked; set { if (Set(ref _periodDiscoveryConsentChecked, value)) ConfirmPeriodDiscoveryCommand.Refresh(); } }
    public bool PeriodDiscoveryRunning => _periodDiscoveryRunning;
    public string PeriodDiscoveryStatus => _periodDiscoveryStatus;
    public bool CanPreparePeriodDiscovery => CanPreparePeriodDiscoveryInternal();
    public string PeriodDiscoveryAvailabilityReason => !CanPreparePeriodDiscoveryInternal() ? PeriodDiscoveryBlockingReason() : Localization.Get("VmPeriodDiscoveryReady");
    public string PeriodDiscoveryPlanText => _periodDiscoveryPreparation?.PlanText ?? PeriodDiscoveryAvailabilityReason;
    public string PeriodDiscoveryMatrixText => _periodDiscoveryResult?.MatrixText ?? Localization.Get("VmPeriodMatrixMissing");
    public string PeriodDiscoveryReportPath => _periodDiscoveryResult?.ReportPath ?? Localization.Get("VmReportNotSaved");
    public string PeriodDiscoveryResultText => _periodDiscoveryResult is not { } result ? Localization.Get("VmPeriodDiscoveryResultMissing") : string.Format(Localization.CurrentCulture, Localization.Get("VmPeriodDiscoveryResultSummary"), result.Lifecycle, result.MatrixText, result.PersistenceStatus, result.ReportPath ?? Localization.Get("VmReportNotSaved"));
    public string CustomPeriodFramesText { get => _customPeriodFramesText; set { if (Set(ref _customPeriodFramesText, value)) { Changed(nameof(CustomPeriodDuplexAvailabilityReason)); Changed(nameof(CanPrepareCustomRepeatability)); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh(); } } }
    public bool CustomPeriodDuplexConsentPending => _customPeriodDuplexConsentPending;
    public bool CustomPeriodDuplexConsentChecked { get => _customPeriodDuplexConsentChecked; set { if (Set(ref _customPeriodDuplexConsentChecked, value)) ConfirmCustomPeriodDuplexCommand.Refresh(); } }
    public bool CustomPeriodDuplexRunning => _customPeriodDuplexRunning;
    public string CustomPeriodDuplexStatus => _customPeriodDuplexStatus;
    public bool CanPrepareCustomPeriodDuplex => CanPrepareCustomPeriodDuplexInternal();
    public string CustomPeriodDuplexAvailabilityReason => Localization.Get(CanPrepareCustomPeriodDuplexInternal() ? "VmCustomPeriodDuplexReady" : "VmCustomPeriodDuplexNeedsDiscovery");
    public string CustomPeriodDuplexPlanText => _customPeriodDuplexPreparation?.PlanText ?? CustomPeriodDuplexAvailabilityReason;
    public string CustomPeriodDuplexResultText => _customPeriodDuplexResult is not { } result ? Localization.Get("VmCustomPeriodDuplexResultMissing") : string.Format(Localization.CurrentCulture, Localization.Get("VmCustomPeriodDuplexResultSummary"), result.Lifecycle, result.PersistenceStatus, result.ReportPath ?? Localization.Get("VmReportNotSaved"), result.Error is null ? string.Empty : string.Format(Localization.CurrentCulture, Localization.Get("VmErrorDetail"), result.Error));
    public bool CustomRepeatabilityConsentPending => _customRepeatabilityConsentPending;
    public bool CustomRepeatabilityConsentChecked { get => _customRepeatabilityConsentChecked; set { if (Set(ref _customRepeatabilityConsentChecked, value)) ConfirmCustomRepeatabilityCommand.Refresh(); } }
    public bool CustomRepeatabilityRunning => _customRepeatabilityRunning;
    public string CustomRepeatabilityStatus => _customRepeatabilityStatus;
    public bool CanPrepareCustomRepeatability => CanPrepareCustomRepeatabilityInternal();
    public string CustomRepeatabilityPlanText => _customRepeatabilityPreparation is not { } p ? Localization.Get("VmCustomRepeatabilityPlanMissing") : string.Format(Localization.CurrentCulture, Localization.Get("VmCustomRepeatabilityPlanRunCount"), p.Periods!.Count, p.Periods[0]) + "\n" + string.Format(Localization.CurrentCulture, Localization.Get("VmCustomRepeatabilityPlanTiming"), p.PauseMilliseconds) + "\n" + Localization.Get("VmCustomRepeatabilityPlanSafety");
    public string CustomRepeatabilityResultText => _customRepeatabilityResult is not { } r ? Localization.Get("VmCustomRepeatabilityResultMissing") : $"{RepeatabilitySummaryDisplay(r)}\n{r.ReportPath ?? Localization.Get("VmReportNotSaved")}";
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
                return $"{Localization.Get("VmRealFormatConflictHeader")} {conflictRow.ConflictDetails}\n" + string.Join("\n", conflictRow.EvidenceCandidates.Select(c => string.Format(Localization.CurrentCulture, Localization.Get("VmRealCandidateConflictDetails"), c.Period, c.Status, c.SupportStatus, c.SupportHr ?? Localization.Get("VmRealEvidenceNotObserved"), c.QueryCleanupStatus, c.QueryClientReleaseAttempts)));
            if (SelectedRealFormatCandidate is not { } c) return Localization.Get("VmRealFormatCandidateSelectForDetails");
            static string V<T>(T? value) where T : struct => value?.ToString() ?? Localization.Get("VmRealEvidenceUnknown");
            static string S(string? value) => value ?? Localization.Get("VmRealEvidenceNotObserved");
            return string.Format(Localization.CurrentCulture, Localization.Get("VmRealCandidateFormatDetails"), c.Status, c.Encoding, c.SampleRate, c.Channels, V(c.ContainerBits), V(c.ValidBits), c.ChannelMaskEvidence, c.Period) + "\n" +
                string.Format(Localization.CurrentCulture, Localization.Get("VmRealCandidateQueryDetails"), S(c.ActivationHr), S(c.SupportHr), c.SupportStatus, S(c.DevicePeriodHr), V(c.DefaultPeriod100ns), V(c.MinimumPeriod100ns), c.QueryCleanupStatus, c.QueryClientReleaseAttempts) + "\n" +
                string.Format(Localization.CurrentCulture, Localization.Get("VmRealCandidateInitializeDetails"), c.InitializeStatus, c.InitializeAttempted, S(c.PeriodQueryHr), V(c.PeriodAvailable), V(c.RequestedPeriod100ns), S(c.FirstInitializeHr)) + "\n" +
                string.Format(Localization.CurrentCulture, Localization.Get("VmRealCandidateAlignmentDetails"), S(c.AlignmentGetBufferSizeHr), V(c.AlignmentFrames), S(c.AlignmentActivateHr), V(c.AlignmentPeriod100ns), S(c.AlignmentInitializeHr), S(c.FinalInitializeHr)) + "\n" +
                string.Format(Localization.CurrentCulture, Localization.Get("VmRealCandidateBufferDetails"), S(c.ActualBufferSizeHr), V(c.ActualBufferFrames), V(c.ActualPeriod100ns), S(c.EventCreateHr), S(c.SetEventHandleHr), S(c.EventHandleCloseHr), V(c.EventHandleReady), c.CleanupStatus, c.ClientReleaseAttempts);
        }
    }
    public string RealInitializationDetails
    {
        get
        {
            if (_realInitializationError is { } error) return error;
            if (RealInitializationResult is not { } result) return Localization.Get("VmRealInitializeNotPerformed");
            var c = result.Candidates.FirstOrDefault();
            static string V<T>(T? value) where T : struct => value?.ToString() ?? Localization.Get("VmRealEvidenceUnknown");
            static string S(string? value) => value ?? Localization.Get("VmRealEvidenceNotObserved");
            var endpointName = WindowsEndpointSource.FirstOrDefault(endpoint => endpoint.EndpointId == result.EndpointId && endpoint.Direction == result.Direction)?.FriendlyNameDisplay ?? Localization.Get("VmWindowsEndpointLocalIdHidden");
            return string.Format(Localization.CurrentCulture, Localization.Get("VmRealInitializationDetailsFormat"), RealFormatMatrixIsStale ? Localization.Get("VmRealHistoricalPrefix") : string.Empty, c?.Status ?? "INCOMPLETE", endpointName, c?.Encoding ?? Localization.Get("VmRealFormatUnknown"), V(c?.SampleRate), V(c?.Channels), c?.Period ?? Localization.Get("VmRealPeriodUnknown")) + "\n" +
                string.Format(Localization.CurrentCulture, Localization.Get("VmRealInitializationSupportDetails"), c?.SupportStatus ?? Localization.Get("VmRealEvidenceUnknown"), S(c?.SupportHr), S(c?.ActivationHr), c?.QueryCleanupStatus ?? Localization.Get("VmRealEvidenceUnknown"), V(c?.QueryClientReleaseAttempts)) + "\n" +
                string.Format(Localization.CurrentCulture, Localization.Get("VmRealInitializationApiDetails"), S(c?.PeriodQueryHr), V(c?.PeriodAvailable), V(c?.RequestedPeriod100ns), S(c?.FirstInitializeHr), S(c?.AlignmentGetBufferSizeHr), V(c?.AlignmentFrames), S(c?.AlignmentActivateHr), S(c?.AlignmentInitializeHr), S(c?.FinalInitializeHr)) + "\n" +
                string.Format(Localization.CurrentCulture, Localization.Get("VmRealInitializationBufferDetails"), S(c?.ActualBufferSizeHr), V(c?.ActualBufferFrames), V(c?.ActualPeriod100ns), S(c?.EventCreateHr), S(c?.SetEventHandleHr), S(c?.EventHandleCloseHr), V(c?.EventHandleReady), c?.CleanupStatus ?? Localization.Get("VmRealEvidenceUnknown"), V(c?.ClientReleaseAttempts));
        }
    }
    public string RealInitializationSummary
    {
        get
        {
            if (_realInitializationError is { } error) return error;
            if (RealInitializationResult is not { } result || result.Candidates.FirstOrDefault() is not { } c) return Localization.Get("VmRealInitializeNotPerformed");
            var endpointName = WindowsEndpointSource.FirstOrDefault(endpoint => endpoint.EndpointId == result.EndpointId && endpoint.Direction == result.Direction)?.FriendlyNameDisplay ?? Localization.Get("VmWindowsEndpointLocalIdHidden");
            var requestedPeriod = c.RequestedPeriod100ns is { } period ? string.Format(Localization.CurrentCulture, Localization.Get("VmRealRequestedPeriodMilliseconds"), period / 10000.0) : Localization.Get("VmRealPeriodValueUnknown");
            var initializeHr = c.FinalInitializeHr ?? c.FirstInitializeHr ?? Localization.Get("VmRealEvidenceNotObserved");
            var eventReady = c.EventHandleReady switch { true => Localization.Get("VmRealEventReady"), false => Localization.Get("VmRealEventNotReady"), null => Localization.Get("VmRealEvidenceUnknown") };
            var cleanup = c.CleanupStatus == "RELEASE_ATTEMPTED" ? Localization.Get("VmRealCleanupReleaseAttempted") : c.CleanupStatus;
            return string.Format(Localization.CurrentCulture, Localization.Get("VmRealInitializationSummary"), endpointName, c.Encoding, c.SampleRate, c.Channels, c.SupportStatus, c.Period.ToUpperInvariant(), requestedPeriod, c.InitializeStatus, initializeHr, c.ActualBufferFrames?.ToString() ?? Localization.Get("VmRealEvidenceUnknown"), eventReady, cleanup);
        }
    }
    public bool RealInitializeConsentPending { get => _realInitializeConsentPending; private set { if (Set(ref _realInitializeConsentPending, value)) { Changed(nameof(RealInitializeConsentChecked)); Changed(nameof(RealInitializeConsentText)); Changed(nameof(RealInitializeDisabledReason)); ConfirmRealInitializeCommand.Refresh(); CancelRealInitializeCommand.Refresh(); RequestDeviceScanCommand.Refresh(); RefreshV4Commands(); } } }
    public bool RealInitializeConsentChecked { get => _realInitializeConsentChecked; set { if (Set(ref _realInitializeConsentChecked, value)) ConfirmRealInitializeCommand.Refresh(); } }
    public string RealInitializeConsentText => _pendingRealFormatCandidate is { } c && _pendingRealEndpointId is not null && _pendingRealDirection is { } direction ? string.Format(Localization.CurrentCulture, Localization.Get("VmRealInitializeConsentDescription"), DirectionDisplayLabel(direction), c.Encoding, c.SampleRate, c.Channels, c.Period) : Localization.Get("VmRealInitializeSelectSupportedFormat");
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
    public IReadOnlyList<ChoiceOption> WindowsEndpointDirectionOptions { get; } = [new("ALL", Localization.Get("VmAllDirections")), new("CAPTURE", Localization.Get("VmCaptureLabel")), new("RENDER", Localization.Get("VmRenderLabel"))];
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
        set { if (Set(ref _selectedHistoricalSustainedScan, value)) InvalidateHistoricalRecovery(Localization.Get("VmHistoricalSelectionChanged")); }
    }
    public string HistoricalRecoveryStatus => _historicalRecoveryStatus;
    public bool HistoricalRecoveryPreviewVisible => _historicalRecoveryPreview is not null;
    public bool HistoricalRecoveryRunning => _historicalRecoveryRunning;
    public bool HistoricalRecoverySaving => _historicalRecoverySaving;
    public bool HistoricalRecoveryConfirmationChecked { get => _historicalRecoveryConfirmationChecked; set { if (Set(ref _historicalRecoveryConfirmationChecked, value)) SaveHistoricalSustainedRecoveryCommand.Refresh(); } }
    public string HistoricalRecoveryPreviewDevice => _selectedHistoricalSustainedScan?.DeviceName ?? "—";
    public string HistoricalRecoveryPreviewDates => _historicalRecoveryPreview is null ? "—" : string.Format(Localization.CurrentCulture, Localization.Get("VmHistoricalRecoveryPreviewDates"), _historicalRecoveryPreview.HistoricalMeasurementTimestampUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Localization.CurrentCulture), _selectedHistoricalSustainedScan?.TimestampLabel ?? "—");
    public string HistoricalRecoveryPreferred => _historicalRecoveryPreview?.Recommendation.Preferred is { } p ? string.Format(Localization.CurrentCulture, Localization.Get("VmHistoricalRecoveryPreferred"), p.Period, p.CaptureBufferFrames, p.RenderBufferFrames, p.Mmcss, p.DurationSeconds) : Localization.Get("VmHistoricalRecoveryPreferredMissing");
    public string HistoricalRecoveryFallback => _historicalRecoveryPreview?.Recommendation.Fallback is { } f ? string.Format(Localization.CurrentCulture, Localization.Get("VmHistoricalRecoveryFallback"), f.Period, f.CaptureBufferFrames, f.RenderBufferFrames) : Localization.Get("VmHistoricalRecoveryFallbackMissing");
    public string HistoricalRecoveryConfidence => HistoricalConfidenceLabel(_historicalRecoveryPreview?.Recommendation.Preferred?.Confidence);
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
                var primary = sustained.Preferred is { } p ? string.Format(Localization.CurrentCulture, Localization.Get("VmSavedProfilePrimary"), p.Period, p.DurationSeconds, p.Confidence) : "NONE";
                var fallback = sustained.Fallback is { } f ? string.Format(Localization.CurrentCulture, Localization.Get("VmSavedProfileFallback"), f.Period, f.DurationSeconds) : "NONE";
                return string.Format(Localization.CurrentCulture, Localization.Get("VmSavedProfileEvidenceSummary"), primary, fallback, Localization.Get("VmSavedProfileEvidenceCaveat"));
            }
            if (profile.SchemaVersion == 5 && profile.StableShortSelection is { ProfileKind: "DEVICE" } shortRun)
                return string.Format(Localization.CurrentCulture, Localization.Get("VmV5SavedProfileSummary"), shortRun.CaptureFormat.Period, shortRun.CaptureFormat.Encoding,
                    shortRun.CaptureFormat.SampleRate, shortRun.CaptureFormat.Channels, shortRun.RepeatabilityCompleted, shortRun.RepeatabilityRequested);
            return string.Format(Localization.CurrentCulture, Localization.Get("VmSavedProfileShortHistory"), profile.SchemaVersion, Localization.Get("VmSavedProfileEvidenceCaveat"));
        }
    }
    public string SavedProfileBindingExplanation => _savedProfileBindingResult?.Overall == DeviceProfileBindingAssessment.ExactBindingsPresentInDiscovery
        ? Localization.Get("VmSavedProfileExactBindingExplanation")
        : Localization.Get("VmSavedProfileBindingNoRecommendation");
    public string SavedProfileBindingTechnicalDetails => _savedProfileBindingResult is { } result
        ? $"{Localization.Get("VmOverallLabel")}: {result.Overall}\n{Localization.Get("VmCaptureLabel")}: {result.Capture}\n{Localization.Get("VmRenderLabel")}: {result.Render}\n{Localization.Get("VmProfileLabel")}: {result.SourceProfileId:D}\n{Localization.Get("VmScanSessionLabel")}: {result.SourceScanSessionId ?? "UNKNOWN"}\n{Localization.Get("VmGenerationLabel")}: {result.DiscoveryGeneration ?? "UNKNOWN"}\n{Localization.Get("VmExactIdsLabel")}: {(result.ExactEndpointIdentities.Count == 0 ? "NONE" : string.Join("; ", result.ExactEndpointIdentities))}\n{Localization.Get("VmReasonsLabel")}: {(result.Reasons.Count == 0 ? "NONE" : string.Join("; ", result.Reasons))}\n{Localization.Get("VmLimitationsLabel")}: {(result.Limitations.Count == 0 ? "NONE" : string.Join("; ", result.Limitations))}\n{Localization.Get("VmFurtherVerificationRequiredLabel")}: {result.RequiresFurtherVerification}"
        : Localization.Get("VmSavedProfileTechnicalDetailsMissing");
    public IReadOnlyList<string> HistoricalWorkerEndpointNames => LastWorkerEndpointSnapshot.Select(e => string.Format(Localization.CurrentCulture, Localization.Get("VmHistoricalWorkerEndpoint"), e.Name, DirectionDisplayLabel(e.Direction), e.ChannelCount)).ToArray();
    public string WorkerRestartExplanation => _workerAttempted && (WorkerState.State is WorkerConnectionState.Disconnected or WorkerConnectionState.Failed or WorkerConnectionState.Incompatible) ? Localization.Get("VmWorkerRestartRequired") : string.Empty;
    public int WorkerRenderChannel { get => _workerRenderChannel; set { if (value >= 1 && value <= WorkerChannelCount && Set(ref _workerRenderChannel, value)) RefreshWorkerCommands(); } }
    public IReadOnlyList<SelectionOption> WorkerRenderChannelOptions => Enumerable.Range(1, WorkerChannelCount).Select(i => new SelectionOption(i, string.Format(Localization.CurrentCulture, Localization.Get("VmWindowsChannelOption"), i))).ToArray();
    public int WorkerDurationSeconds { get => _workerDurationSeconds; set { if (DurationOptions.Any(o => o.Value == value) && Set(ref _workerDurationSeconds, value)) RefreshWorkerCommands(); } }
    public string WorkerPeriod { get => _workerPeriod; set { if ((value is "DEFAULT" or "MINIMUM") && Set(ref _workerPeriod, value)) RefreshWorkerCommands(); } }
    public string WorkerMmcss { get => _workerMmcss; set { if ((value is "OFF" or "PRO_AUDIO") && Set(ref _workerMmcss, value)) RefreshWorkerCommands(); } }
    public string WorkerScenario { get => _workerScenario; set { if ((value is "NORMAL" or "WARNING" or "FAILURE" or "DEVICE_LOSS" or "ALL_SILENT" or "NO_WINDOW") && Set(ref _workerScenario, value)) RefreshWorkerCommands(); } }
    public bool WorkerConsentPending { get => _workerConsentPending; private set { if (Set(ref _workerConsentPending, value)) { ConfirmWorkerSessionCommand.Refresh(); CancelWorkerConsentCommand.Refresh(); RefreshWorkerCommands(); } } }
    public bool WorkerConsentChecked { get => _workerConsentChecked; set { if (Set(ref _workerConsentChecked, value)) ConfirmWorkerSessionCommand.Refresh(); } }
    public WorkerSessionSettingsDto? WorkerPendingSettings => _workerPendingSettings;
    public WorkerSessionSettingsDto? WorkerSessionSettings { get => _workerSessionSettings; private set { if (Set(ref _workerSessionSettings, value)) { Changed(nameof(WorkerSessionSettingsText)); Changed(nameof(WorkerMeterFormatSummary)); RefreshContext(); } } }
    public SessionDto? WorkerSession { get => _workerSession; private set { if (value is not null && _workerSession?.Id != value.Id) { _workerCaptureMeter = null; _workerRenderMeter = null; _workerMeterBank = 0; _workerMeterStale = false; _workerMeterFinal = false; Changed(nameof(WorkerMeterBank)); Changed(nameof(WorkerMeterVisible)); Changed(nameof(WorkerMeterHeading)); Changed(nameof(WorkerMeterInterpretation)); Changed(nameof(WorkerVisibleMeterChannels)); Changed(nameof(WorkerMeterStatusText)); } if (Set(ref _workerSession, value)) { Changed(nameof(WorkerOperationActive)); Changed(nameof(CanSaveDeviceProfile)); Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(WorkerStatusVisible)); Changed(nameof(WorkerMeterVisible)); Changed(nameof(WorkerMeterHeading)); Changed(nameof(WorkerMeterInterpretation)); Changed(nameof(WorkerVisibleMeterChannels)); Changed(nameof(WorkerMeterStatusText)); RequestDeviceScanCommand.Refresh(); RefreshWorkerCommands(); RefreshV4Commands(); RefreshContext(); } } }
    public bool WorkerOperationActive => WorkerSession?.Lifecycle is SessionLifecycle.Preparing or SessionLifecycle.Ready or SessionLifecycle.Started or SessionLifecycle.Stopping;
    public string WorkerNotice { get => _workerNotice; private set => Set(ref _workerNotice, value); }
    public bool WorkerMeterVisible => WorkerSession is not null;
    public string WorkerMeterHeading => WorkerSession?.Mode == SessionMode.Render ? Localization.Get("VmWorkerRenderMeterHeading") : Localization.Get("VmWorkerCaptureMeterHeading");
    public string WorkerMeterInterpretation => WorkerSession?.Mode == SessionMode.Render ? string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerRenderMeterInterpretation"), WorkerSession.RenderChannel) : Localization.Get("VmWorkerCaptureMeterInterpretation");
    public int WorkerMeterBank { get => _workerMeterBank; set { if (value >= 0 && value < WorkerMeterBanks.Count && Set(ref _workerMeterBank, value)) Changed(nameof(WorkerVisibleMeterChannels)); } }
    public IReadOnlyList<SelectionOption> WorkerMeterBanks
    {
        get
        {
            var count = WorkerSession?.Mode == SessionMode.Render ? _workerRenderMeter?.ChannelCount ?? WorkerSession?.ChannelCount ?? 0 : _workerCaptureMeter?.ChannelCount ?? WorkerSession?.ChannelCount ?? 0;
            return Enumerable.Range(0, (count + 7) / 8).Select(bank => new SelectionOption(bank, string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterBankRange"), bank * 8 + 1, Math.Min(bank * 8 + 8, count)))).ToArray();
        }
    }
    public IReadOnlyList<MeterChannelDto> WorkerVisibleMeterChannels
    {
        get
        {
            IEnumerable<MeterChannelDto> channels = WorkerSession?.Mode switch
            {
                SessionMode.Capture => (_workerCaptureMeter?.Channels ?? []).Select(c => new MeterChannelDto(c.Index, c.WindowPeakDbfs, c.WindowRmsDbfs, c.SessionPeakHoldDbfs, c.ClipCount, c.Silent, c.Silent == true ? Localization.Get("VmWorkerMeterDigitalSilence") : c.Silent == false ? Localization.Get("VmWorkerMeterSyntheticWindow") : Localization.Get("VmWorkerMeterNoMeasurement"))),
                SessionMode.Render => (_workerRenderMeter?.Channels ?? []).Select(c => new MeterChannelDto(c.Index, c.IntendedPeakDbfs, c.IntendedRmsDbfs, c.SessionPeakHoldDbfs, c.ClipCount, c.DigitalSilent, c.DigitalSilent == true ? Localization.Get("VmWorkerMeterIntendedSilence") : c.DigitalSilent == false ? Localization.Get("VmWorkerMeterIntendedModelLevel") : Localization.Get("VmWorkerMeterNoCompletedWindow"))),
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
    public string WorkerMeterModeLabel => WorkerSession?.Mode switch { SessionMode.Capture => Localization.Get("VmCaptureLabel"), SessionMode.Render => Localization.Get("VmWorkerMeterRenderMode"), _ => Localization.Get("VmWorkerMeterNoSession") };
    public string WorkerMeterFormatSummary => _workerSessionSettings is { } s ? string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterFormatSummary"), s.FormatId, s.SampleRate, s.ChannelCount, s.DurationSeconds) : Localization.Get("VmWorkerMeterFormatUnknown");
    public string WorkerMeterSnapshotSummary => WorkerSession?.Mode == SessionMode.Render
        ? _workerRenderMeter is { } r ? string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterSnapshotSummary"), r.SnapshotSequence, r.ChannelCount) : Localization.Get("VmWorkerMeterSnapshotNoData")
        : _workerCaptureMeter is { } c ? string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterSnapshotSummary"), c.SnapshotSequence, c.ChannelCount) : Localization.Get("VmWorkerMeterSnapshotNoData");
    public string WorkerMeterCoalescingSummary => WorkerSession?.Mode == SessionMode.Render
        ? _workerRenderMeter is { } r ? string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterCoalescedSummary"), r.CoalescedSnapshotCount) : Localization.Get("VmWorkerMeterCoalescedNoData")
        : _workerCaptureMeter is { } c ? string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerMeterCoalescedSummary"), c.CoalescedSnapshotCount) : Localization.Get("VmWorkerMeterCoalescedNoData");
    public string WorkerConsentText => _workerPendingSettings is { } s ? string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerConsentSummary"), DirectionDisplayLabel(s.Direction), s.EndpointId, s.FormatId, s.ChannelCount, s.Period, s.Mmcss, s.DurationSeconds, s.Scenario, s.RenderChannel?.ToString() ?? Localization.Get("VmWorkerMeterNotApplicable")) : Localization.Get("VmWorkerConsentMissing");
    public string WorkerSessionSettingsText => _workerSessionSettings is { } s ? string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerSessionSettingsSummary"), DirectionDisplayLabel(s.Direction), s.EndpointId, s.FormatId, s.ChannelCount, s.TopologyId, s.Period, s.Mmcss, s.DurationSeconds, s.RenderChannel?.ToString() ?? Localization.Get("VmWorkerMeterNotApplicable"), s.Scenario) : Localization.Get("VmWorkerSessionSettingsMissing");
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
        ? string.Format(Localization.CurrentCulture, Localization.Get("VmDemoRenderConfirmation"), _pending.Endpoint.DisplayName, _pending.RenderChannel, _pending.DurationSeconds)
        : string.Format(Localization.CurrentCulture, Localization.Get("VmDemoCaptureConfirmation"), _pending.Endpoint.DisplayName, _pending.DurationSeconds);
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
    public AsyncRelayCommand PrepareV4CustomPlanCommand { get; }
    public AsyncRelayCommand PrepareV4DiagnosticPlanCommand { get; }
    public RelayCommand AcceptV4CustomPlanCommand { get; }
    public RelayCommand AcceptV4DiagnosticPlanCommand { get; }
    public RelayCommand CancelV4CustomPlanCommand { get; }
    public AsyncRelayCommand StartV4ScanCommand { get; }
    public AsyncRelayCommand StartV4DiagnosticCommand { get; }
    public RelayCommand CancelV4ScanCommand { get; }
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

    public bool IsDuplexOperatorResult => _operatorResultMode == OperatorResultKind.Duplex;
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
        Record(_pendingSessionId.Value, ProbeEventKind.SessionRequested, string.Format(Localization.CurrentCulture, Localization.Get("VmDemoSessionRequested"), Localization.Get(mode == SessionMode.Capture ? "VmCaptureInputDirection" : "VmRenderOutputDirection"), endpoint.Name));
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
        Record(request.Id, ProbeEventKind.ConsentAccepted, Localization.Get("VmDemoConsentAccepted"));
        if (request.Mode == SessionMode.Capture) Meter = null;
        Session = new(request.Id, request.Mode, request.Endpoint.Name, request.RenderChannel, request.DurationSeconds, SessionLifecycle.Preparing, StreamQuality.Unknown, Localization.Get("VmInspectorDemoSimulation"), null, ChannelCount: request.ChannelCount);
        Record(request.Id, ProbeEventKind.Preparing, Localization.Get("VmDemoPreparing"));
        Session = Session with { Lifecycle = SessionLifecycle.Started, Message = Localization.Get("VmDemoSessionRunning") };
        Record(request.Id, ProbeEventKind.Started, Localization.Get("VmDemoSessionStarted"));
        Notice = Localization.Get("VmDemoStarted");
        _pending = null;
        _pendingSessionId = null;
        if (Session.Mode == SessionMode.Capture) NextPulse();
    }

    private void CancelConfirmation()
    {
        if (_pendingSessionId is Guid id) Record(id, ProbeEventKind.ConsentCancelled, Localization.Get("VmDemoConsentCancelled"));
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
        Session = Session with { Lifecycle = SessionLifecycle.Stopping, Message = Localization.Get("VmDemoStopping") };
        Record(Session.Id, ProbeEventKind.Stopping, Localization.Get("VmDemoStopRequested"));
    }
    public void CompleteStop()
    {
        if (Session?.Lifecycle != SessionLifecycle.Stopping) return;
        Session = Session with { Lifecycle = SessionLifecycle.Stopped, Message = Localization.Get("VmDemoStopped"), CleanupStatus = null };
        Record(Session.Id, ProbeEventKind.Stopped, Localization.Get("VmDemoSessionStopped"));
    }
    public void CompleteSimulation()
    {
        if (Session?.Lifecycle != SessionLifecycle.Started) return;
        Session = Session with { Lifecycle = SessionLifecycle.Completed, Quality = StreamQuality.Unknown, Message = Localization.Get("VmDemoDurationCompleted") };
        Record(Session.Id, ProbeEventKind.Completed, Localization.Get("VmDemoDurationEvent"));
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
            return new MeterChannelDto(channel, windowPeak, windowPeak is double finitePeak ? finitePeak - 7 : null, hold, 0, silent, Localization.Get(silent ? "VmDemoDigitalSilence" : "VmDemoCompletedWindow"));
        }).ToArray(), true);
        Record(Session.Id, ProbeEventKind.MeterSnapshot, string.Format(Localization.CurrentCulture, Localization.Get("VmDemoMeterSnapshot"), sequence));
    }
    private void DisconnectWorker()
    {
        if (Session?.Lifecycle != SessionLifecycle.Started) return;
        var id = Session.Id;
        Session = Session with { Lifecycle = SessionLifecycle.Unknown, Quality = StreamQuality.Unknown, Message = Localization.Get("VmDemoWorkerDisconnected"), CleanupStatus = null };
        Record(id, ProbeEventKind.WorkerDisconnected, Localization.Get("VmDemoConnectionLost"));
    }
    private void FailSession()
    {
        if (Session?.Lifecycle != SessionLifecycle.Started) return;
        var id = Session.Id;
        Session = Session with { Lifecycle = SessionLifecycle.Failed, Quality = StreamQuality.Failed, Message = Localization.Get("VmDemoSessionFailure"), CleanupStatus = null };
        Record(id, ProbeEventKind.Failure, Localization.Get("VmDemoScenarioFailure"));
    }
    private void CompleteWithWarning()
    {
        if (Session is not { Mode: SessionMode.Capture, Lifecycle: SessionLifecycle.Started }) return;
        Session = Session with { Quality = StreamQuality.Warning, Message = Localization.Get("VmDemoCaptureTimingWarning") };
        Record(Session.Id, ProbeEventKind.Warning, Localization.Get("VmDemoCaptureTimestampWarning"));
    }
    private void SimulateDeviceLoss()
    {
        if (Session?.Lifecycle != SessionLifecycle.Started) return;
        var id = Session.Id;
        Session = Session with { Lifecycle = SessionLifecycle.Failed, Quality = StreamQuality.Unknown, Message = Localization.Get("VmDemoDeviceLoss"), CleanupStatus = null };
        Record(id, ProbeEventKind.DeviceLoss, Localization.Get("VmDemoDeviceLossEvent"), "AUDCLNT_E_DEVICE_INVALIDATED");
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
        RefreshV4Commands();
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
            WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerFakeFixturesReceived"), WorkerEndpoints.Count);
        }
        catch (WorkerCommandException ex) { WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerCommandRejected"), ex.Command, ex.ErrorCode, ex.Message); }
        catch (Exception ex) { WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerQueryFailed"), ex.GetType().Name, ex.Message); }
        Changed(nameof(WorkerEndpoints));
        Changed(nameof(SelectedWorkerEndpoint)); Changed(nameof(WorkerFormatOptions)); Changed(nameof(WorkerChannelSummary)); Changed(nameof(HasHistoricalWorkerEndpoints)); Changed(nameof(HistoricalWorkerEndpointNames)); RefreshWorkerCommands(); RefreshContext();
    }

    private bool CanDiscoverWindowsEndpoints() => _worker is not null && !_windowsDiscoveryLoading && !_bindingCheckRunning && !AutomaticScanBusy && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending &&
        (WorkerState.State == WorkerConnectionState.ConnectedFake && _worker.Capabilities.Contains("ListWindowsEndpoints", StringComparer.Ordinal) ||
         WorkerState.State == WorkerConnectionState.Disconnected && !_workerAttempted);

    private static IReadOnlyList<SelectionOption> V2ChannelCounts(WindowsEndpointDto? endpoint)
    {
        var counts = new[] { 1, 2, 8, 16, 32, endpoint?.SharedModeFormat.ReportedChannelCount ?? 0 }.Where(c => c is >= 1 and <= 32).Distinct().Order().ToArray();
        return counts.Select(c => new SelectionOption(c, string.Format(Localization.CurrentCulture, Localization.Get("VmV2StreamChannelCountOption"), c))).ToArray();
    }

    private void ResetAutomaticScanV2Consent()
    {
        InvalidateV4PreparedPlan("VmV4PlanInvalidated");
        if (AutomaticScanConsentPending) CancelAutomaticScanConsent();
        _automaticScanV2Route = null;
        _automaticScanV2DiscoveryGeneration = null;
        _automaticScanV2CaptureEndpointId = _automaticScanV2RenderEndpointId = null;
        Changed(nameof(AutomaticScanConsentText));
    }

    private bool CanConfirmAutomaticScan()
    {
        if (!AutomaticScanConsentPending || !AutomaticScanConsentChecked || AutomaticScanFullBudgetConfirmationVisible && !AutomaticScanFullBudgetConfirmed || AutomaticScanBusy || WorkerOperationActive ||
            WorkerState.State != WorkerConnectionState.ConnectedFake || !_windowsDiscoveryCurrent) return false;
        if (AutomaticScanEngine == "V2_ADAPTIVE")
        {
            var captureId = SelectedDuplexCaptureEndpoint?.EndpointId;
            var renderId = SelectedDuplexRenderEndpoint?.EndpointId;
            var route = BuildV2Route();
            return _automaticScanV2Route is { } approvedRoute && route is not null &&
                _worker?.Capabilities.Contains("RealExclusiveDuplexValidationV2", StringComparer.Ordinal) == true &&
                _automaticScanV2DiscoveryGeneration == _windowsDiscoveryGeneration &&
                captureId == _automaticScanV2CaptureEndpointId && renderId == _automaticScanV2RenderEndpointId &&
                captureId is { Length: > 0 } && renderId is { Length: > 0 } &&
                WorkerScanPlanV2Fingerprint.Route(captureId, renderId, route) == WorkerScanPlanV2Fingerprint.Route(captureId, renderId, approvedRoute);
        }

        if (_automaticScanPreparation is not { } preparation || _worker?.Capabilities.Contains("ScanSessionAuthorizationV1", StringComparer.Ordinal) != true) return false;
        var target = preparation.Plan.Target;
        if (AutomaticScanPairEnabled)
            return target.Capture?.EndpointId == SelectedDuplexCaptureEndpoint?.EndpointId &&
                target.Render?.EndpointId == SelectedDuplexRenderEndpoint?.EndpointId &&
                target.Capture?.EndpointId is { Length: > 0 } && target.Render?.EndpointId is { Length: > 0 };
        var selectedId = SelectedWindowsEndpoint?.EndpointId;
        var plannedId = target.Capture?.EndpointId ?? target.Render?.EndpointId;
        return selectedId is { Length: > 0 } && selectedId == plannedId && (target.Capture is null ^ target.Render is null);
    }

    private void SetV2Channels(ref int field, int value, bool capture)
    {
        var endpoint = capture ? SelectedDuplexCaptureEndpoint : SelectedDuplexRenderEndpoint;
        if (AutomaticScanBusy || !V2ChannelCounts(endpoint).Any(o => o.Value == value) || !Set(ref field, value, capture ? nameof(AutomaticScanV2CaptureChannels) : nameof(AutomaticScanV2RenderChannels))) return;
        if (capture) { _automaticScanV2CaptureChannel1 = 1; _automaticScanV2CaptureChannel2 = Math.Min(2, value); Changed(nameof(AutomaticScanV2CaptureChannelOptions)); Changed(nameof(AutomaticScanV2CaptureChannel1)); Changed(nameof(AutomaticScanV2CaptureChannel2)); }
        else { _automaticScanV2RenderChannel1 = 1; _automaticScanV2RenderChannel2 = Math.Min(2, value); Changed(nameof(AutomaticScanV2RenderChannelOptions)); Changed(nameof(AutomaticScanV2RenderChannel1)); Changed(nameof(AutomaticScanV2RenderChannel2)); }
        ResetAutomaticScanV2Consent(); Changed(nameof(AutomaticScanV2ChannelControlsText)); PrepareAutomaticScanCommand.Refresh();
    }

    private void SetV2Channel(ref int field, int value, string propertyName)
    {
        if (AutomaticScanBusy || value < 1 || value > (propertyName.Contains("Capture", StringComparison.Ordinal) ? _automaticScanV2CaptureChannels : _automaticScanV2RenderChannels) || !Set(ref field, value, propertyName)) return;
        ResetAutomaticScanV2Consent(); Changed(nameof(AutomaticScanV2RouteSummary)); PrepareAutomaticScanCommand.Refresh();
    }

    private void ApplyV2RouteSelection(WorkerScanV2Route route)
    {
        _automaticScanV2Mode = route.Mode;
        _automaticScanV2CaptureChannels = route.Capture.Channels;
        _automaticScanV2RenderChannels = route.Render.Channels;
        _automaticScanV2CaptureFormat = $"{route.Capture.SampleRateHz}|{route.Capture.Encoding}";
        _automaticScanV2RenderFormat = $"{route.Render.SampleRateHz}|{route.Render.Encoding}";
        _automaticScanV2CaptureChannel1 = route.Capture.SelectedIndicesZeroBased[0] + 1;
        _automaticScanV2CaptureChannel2 = route.Capture.SelectedIndicesZeroBased.LastOrDefault() + 1;
        _automaticScanV2RenderChannel1 = route.Render.SelectedIndicesZeroBased[0] + 1;
        _automaticScanV2RenderChannel2 = route.Render.SelectedIndicesZeroBased.LastOrDefault() + 1;
        Changed(nameof(AutomaticScanV2Mode)); Changed(nameof(AutomaticScanV2CaptureChannels)); Changed(nameof(AutomaticScanV2RenderChannels));
        Changed(nameof(AutomaticScanV2CaptureFormat)); Changed(nameof(AutomaticScanV2RenderFormat));
        Changed(nameof(AutomaticScanV2CaptureChannel1)); Changed(nameof(AutomaticScanV2CaptureChannel2));
        Changed(nameof(AutomaticScanV2RenderChannel1)); Changed(nameof(AutomaticScanV2RenderChannel2));
        Changed(nameof(AutomaticScanV2SecondCaptureVisible)); Changed(nameof(AutomaticScanV2SecondRenderVisible));
        Changed(nameof(AutomaticScanV2CaptureChannelOptions)); Changed(nameof(AutomaticScanV2RenderChannelOptions));
        Changed(nameof(AutomaticScanV2ChannelControlsText)); Changed(nameof(AutomaticScanV2RouteSummary));
    }

    private WorkerScanV2Route? BuildV2Route()
    {
        if (!int.TryParse(_automaticScanV2CaptureFormat.Split('|')[0], out var captureRate) || !int.TryParse(_automaticScanV2RenderFormat.Split('|')[0], out var renderRate)) return null;
        var captureEncoding = _automaticScanV2CaptureFormat.Split('|').Last(); var renderEncoding = _automaticScanV2RenderFormat.Split('|').Last();
        var captureIndices = _automaticScanV2Mode == "STEREO_TO_STEREO" ? new[] { _automaticScanV2CaptureChannel1 - 1, _automaticScanV2CaptureChannel2 - 1 } : new[] { _automaticScanV2CaptureChannel1 - 1 };
        var renderIndices = _automaticScanV2Mode == "MONO_TO_MONO" ? new[] { _automaticScanV2RenderChannel1 - 1 } : new[] { _automaticScanV2RenderChannel1 - 1, _automaticScanV2RenderChannel2 - 1 };
        if (captureIndices.Any(i => i < 0 || i >= _automaticScanV2CaptureChannels) || renderIndices.Any(i => i < 0 || i >= _automaticScanV2RenderChannels) || captureIndices.Distinct().Count() != captureIndices.Length || renderIndices.Distinct().Count() != renderIndices.Length) return null;
        try { return new WorkerScanV2Route(_automaticScanV2Mode,
            new(captureRate, _automaticScanV2CaptureChannels, captureEncoding, 0, captureIndices),
            new(renderRate, _automaticScanV2RenderChannels, renderEncoding, 0, renderIndices)); }
        catch (ArgumentException) { return null; }
    }

    private bool CanPrepareAutomaticScan() => _automaticScanWorkflow is not null && !AutomaticScanBusy && !AutomaticScanConsentPending && !_v4ConsentPending && _v4ConsentReceipt is null && !_automaticScanOutcomeUnknown && !_windowsDiscoveryLoading && !_duplexRepeatabilityRunning && !_duplexRepeatabilityConsentPending && !WorkerOperationActive &&
        WorkerState.State == WorkerConnectionState.ConnectedFake && (_automaticScanEngine == "V2_ADAPTIVE"
            ? _worker?.Capabilities.Contains("RealExclusiveDuplexValidationV2", StringComparer.Ordinal) == true && HasCurrentDuplexPair && BuildV2Route() is not null
            : _worker?.Capabilities.Contains("ScanSessionAuthorizationV1", StringComparer.Ordinal) == true && (AutomaticScanPairEnabled
                ? SelectedDuplexCaptureEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } && SelectedDuplexRenderEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 }
                : SelectedWindowsEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 }));

    private bool CanPrepareV4CustomPlan() => string.IsNullOrEmpty(GetV4PrepareDisabledReason());
    private bool CanPrepareV4DiagnosticPlan() => string.IsNullOrEmpty(GetV4DiagnosticDisabledReason());

    private string GetV4DiagnosticDisabledReason()
    {
        var reason = GetV4PrepareDisabledReason();
        if (reason.Length != 0) return reason;
        if (_v4ConsentPending || _v4ConsentReceipt is not null) return Localization.Get("VmV4DiagnosticPlanPending");
        if (_v4AutomaticSearchWorkflow is null) return Localization.Get("VmV4DiagnosticUnavailable");
        var buildSha = Environment.GetEnvironmentVariable("KLIDAN_BUILD_SHA");
        if (buildSha is not { Length: 40 } || !buildSha.All(Uri.IsHexDigit)) return Localization.Get("VmV4DiagnosticBuildShaMissing");
        var route = BuildV2Route();
        if (route is null || route.Mode != "MONO_TO_STEREO" || route.Capture.SampleRateHz != 48_000 || route.Render.SampleRateHz != 48_000 || route.Capture.Encoding != "PCM16" || route.Render.Encoding != "PCM16")
            return Localization.Get("VmV4DiagnosticNeedsBloodyFormat");
        return string.Empty;
    }

    private string GetV4PrepareDisabledReason()
    {
        if (_v4Preparing) return Localization.Get("VmV4PrepareBusy");
        if (_automaticScanOutcomeUnknown || _workerStartOutcomeUnknown) return Localization.Get("VmV4PrepareOutcomeUnknown");
        if (AutomaticScanBusy || WorkerOperationActive || _bindingCheckRunning || _deviceScanRunning || _realQueryRunning || _realInitializeRunning || _streamValidationRunning || _duplexValidationRunning || _duplexRepeatabilityRunning || _periodDiscoveryRunning || _customPeriodDuplexRunning)
            return Localization.Get("VmV4PrepareOperationActive");
        if (AutomaticScanConsentPending || _v4ConsentPending || _v4ConsentReceipt is not null || DeviceScanConsentPending || RealInitializeConsentPending || DuplexConsentPending || _streamConsentPending || _duplexRepeatabilityConsentPending || _periodDiscoveryConsentPending || _customPeriodDuplexConsentPending)
            return Localization.Get("VmV4PrepareOtherConsentPending");
        if (WorkerState.State != WorkerConnectionState.ConnectedFake || _worker is null) return Localization.Get("VmV4PrepareWorkerDisconnected");
        if (_windowsDiscoveryLoading) return Localization.Get("VmV4PrepareDiscoveryLoading");
        if (!_windowsDiscoveryCurrent || _windowsDiscoveryWorkerGeneration != _worker.DiscoveryGeneration) return Localization.Get("VmV4PrepareDiscoveryStale");
        if (_worker.DiscoveryGeneration == 0 || !_worker.Capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal)) return Localization.Get("VmV4PrepareCapabilityMissing");
        if (!HasCurrentDuplexPair) return Localization.Get("VmV4PrepareSelectPair");
        if (BuildV2Route() is null) return Localization.Get("VmV4PrepareInvalidRoute");
        return string.Empty;
    }

    private void RefreshV4Commands()
    {
        PrepareV4CustomPlanCommand?.Refresh(); AcceptV4CustomPlanCommand?.Refresh(); CancelV4CustomPlanCommand?.Refresh();
        PrepareV4DiagnosticPlanCommand?.Refresh(); AcceptV4DiagnosticPlanCommand?.Refresh(); StartV4DiagnosticCommand?.Refresh();
        StartV4ScanCommand?.Refresh(); CancelV4ScanCommand?.Refresh(); Changed(nameof(V4PrepareDisabledReason)); Changed(nameof(V4DiagnosticAvailabilityReason)); Changed(nameof(V4DiagnosticPlanMode)); Changed(nameof(V4SearchPlanUiVisible)); Changed(nameof(V4DiagnosticConsentPending)); Changed(nameof(V4SearchConsentPending)); Changed(nameof(V4DiagnosticReceiptCreated)); Changed(nameof(V4DiagnosticRunVisible)); Changed(nameof(V4SearchReceiptCreated));
    }

    private Task PrepareV4CustomPlanAsync() => PrepareV4PlanAsync(false);
    private Task PrepareV4DiagnosticPlanAsync() => PrepareV4PlanAsync(true);

    private async Task PrepareV4PlanAsync(bool diagnosticOnly)
    {
        if (!(diagnosticOnly ? CanPrepareV4DiagnosticPlan() : CanPrepareV4CustomPlan()) || _worker is null || BuildV2Route() is not { } route ||
            SelectedDuplexCaptureEndpoint is not { DeviceState: "ACTIVE", EndpointId.Length: > 0 } capture ||
            SelectedDuplexRenderEndpoint is not { DeviceState: "ACTIVE", EndpointId.Length: > 0 } render ||
            _windowsDiscoveryGeneration is not { } uiGeneration) return;
        InvalidateV4PreparedPlan("VmV4PlanPreparationStarted");
        _v4DiagnosticPlanMode = diagnosticOnly;
        var version = Interlocked.Increment(ref _v4PreparationVersion);
        var workerGeneration = _worker.DiscoveryGeneration;
        var processId = WorkerState.ProcessId;
        var routeFingerprint = WorkerScanPlanV2Fingerprint.Route(capture.EndpointId, render.EndpointId, route);
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _v4PreparationCancellation = cancellation;
        _v4Preparing = true;
        _v4PlanStatusKey = "VmV4PlanQueryingBounds"; _v4PlanStatusArgument = null;
        Changed(nameof(V4PlanPreparing)); Changed(nameof(AutomaticScanBusy)); Changed(nameof(V4PlanStatus));
        RefreshV4Commands();
        try
        {
            var captureMatrix = await _worker.QueryFormatMatrixAsync(capture.EndpointId, "CAPTURE", [route.Capture.Channels], cancellation.Token);
            if (!IsCurrentV4Preparation(version, workerGeneration, uiGeneration, processId, capture.EndpointId, render.EndpointId, routeFingerprint, route)) return;
            var renderMatrix = await _worker.QueryFormatMatrixAsync(render.EndpointId, "RENDER", [route.Render.Channels], cancellation.Token);
            if (!IsCurrentV4Preparation(version, workerGeneration, uiGeneration, processId, capture.EndpointId, render.EndpointId, routeFingerprint, route)) return;
            var plan = WorkerScanV2PlanPreparation.Prepare(capture.EndpointId, render.EndpointId, route, captureMatrix, renderMatrix, workerGeneration,
                diagnosticOnly ? 0 : V4RepeatabilityRuns, diagnosticOnly ? V4DiagnosticPeriodMilliseconds : null, V4DiagnosticMmcssMode);
            var fingerprint = WorkerScanV2ContractV4Fingerprint.Compute(capture.EndpointId, render.EndpointId, plan, workerGeneration);
            if (!IsCurrentV4Preparation(version, workerGeneration, uiGeneration, processId, capture.EndpointId, render.EndpointId, routeFingerprint, route)) return;
            _v4PreparedPlan = plan;
            _v4CaptureEndpointId = capture.EndpointId; _v4RenderEndpointId = render.EndpointId;
            _v4CaptureEndpoint = capture; _v4RenderEndpoint = render;
            _v4CaptureFriendlyName = capture.FriendlyNameDisplay; _v4RenderFriendlyName = render.FriendlyNameDisplay;
            _v4WorkerDiscoveryGeneration = workerGeneration; _v4UiDiscoveryGeneration = uiGeneration;
            _v4PlanFingerprint = fingerprint;
            _v4ConsentReceipt = null; _v4ConsentChecked = false; _v4DiagnosticConsentChecked = false; _v4ConsentPending = true;
            _v4PlanStatusKey = "VmV4PlanAwaitingConsent"; _v4PlanStatusArgument = null;
        }
        catch (OperationCanceledException) when (version != Volatile.Read(ref _v4PreparationVersion) || cancellation.IsCancellationRequested)
        {
            if (version == Volatile.Read(ref _v4PreparationVersion)) { _v4PlanStatusKey = "VmV4PlanCancelled"; _v4PlanStatusArgument = null; }
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref _v4PreparationVersion)) { _v4PlanStatusKey = "VmV4PlanBlocked"; _v4PlanStatusArgument = ex.Message; }
        }
        finally
        {
            if (ReferenceEquals(_v4PreparationCancellation, cancellation)) _v4PreparationCancellation = null;
            cancellation.Dispose();
            if (version == Volatile.Read(ref _v4PreparationVersion)) _v4Preparing = false;
            Changed(nameof(AutomaticScanBusy));
            Changed(nameof(V4PlanPreparing)); Changed(nameof(V4PlanReady)); Changed(nameof(V4ConsentPending)); Changed(nameof(V4ConsentReceipt));
            Changed(nameof(V4PlanStatus)); Changed(nameof(V4PlanFingerprint)); Changed(nameof(V4ConsentSummary)); Changed(nameof(V4ReceiptCreated));
            RefreshV4Commands();
        }
    }

    private bool IsCurrentV4Preparation(int version, ulong workerGeneration, string uiGeneration, int? processId,
        string captureId, string renderId, string routeFingerprint, WorkerScanV2Route route) =>
        version == Volatile.Read(ref _v4PreparationVersion) && _v4PreparationCancellation is { IsCancellationRequested: false } &&
        _worker is not null && WorkerState.State == WorkerConnectionState.ConnectedFake && WorkerState.ProcessId == processId &&
        _worker.DiscoveryGeneration == workerGeneration && _windowsDiscoveryCurrent && !_windowsDiscoveryLoading && _windowsDiscoveryGeneration == uiGeneration &&
        SelectedDuplexCaptureEndpoint?.EndpointId == captureId && SelectedDuplexRenderEndpoint?.EndpointId == renderId &&
        BuildV2Route() is { } currentRoute && WorkerScanPlanV2Fingerprint.Route(captureId, renderId, currentRoute) == routeFingerprint &&
        WorkerScanPlanV2Fingerprint.Route(captureId, renderId, route) == routeFingerprint;

    private bool IsCurrentV4Plan() => _v4PreparedPlan is { } prepared && _v4CaptureEndpointId is { Length: > 0 } captureId && _v4RenderEndpointId is { Length: > 0 } renderId && _v4WorkerDiscoveryGeneration is { } generation &&
        _v4UiDiscoveryGeneration is { } uiGeneration && _v4PlanFingerprint is { } fingerprint && _worker is not null &&
        WorkerState.State == WorkerConnectionState.ConnectedFake && _worker.DiscoveryGeneration == generation && _windowsDiscoveryCurrent && _windowsDiscoveryGeneration == uiGeneration &&
        SelectedDuplexCaptureEndpoint?.EndpointId == captureId && SelectedDuplexRenderEndpoint?.EndpointId == renderId && BuildV2Route() is { } route &&
        WorkerScanV2ContractV4Fingerprint.Compute(captureId, renderId, prepared, generation) == fingerprint &&
        WorkerScanPlanV2Fingerprint.Route(captureId, renderId, route) == WorkerScanPlanV2Fingerprint.Route(captureId, renderId, prepared.Route);

    private bool CanAcceptV4CustomPlan() => !_v4DiagnosticPlanMode && CanAcceptV4Plan(_v4ConsentChecked);
    private bool CanAcceptV4DiagnosticPlan() => _v4DiagnosticPlanMode && CanAcceptV4Plan(_v4DiagnosticConsentChecked);
    private bool CanAcceptV4Plan(bool checkedConsent) => _v4ConsentPending && _v4PreparedPlan is not null && _v4ConsentReceipt is null && checkedConsent && !_v4Preparing && !AutomaticScanBusy && !WorkerOperationActive && IsCurrentV4Plan() &&
        _v4CaptureEndpointId is { Length: > 0 } captureId && _v4RenderEndpointId is { Length: > 0 } renderId && _v4WorkerDiscoveryGeneration is { } generation &&
        _v4UiDiscoveryGeneration is not null && _v4PlanFingerprint is not null && _worker is not null;

    private void AcceptV4CustomPlan()
    {
        if (!(V4DiagnosticPlanMode ? CanAcceptV4DiagnosticPlan() : CanAcceptV4CustomPlan()) || _worker is null || _v4PreparedPlan is null || _v4CaptureEndpointId is null || _v4RenderEndpointId is null || _v4WorkerDiscoveryGeneration is null) return;
        _v4ConsentReceipt = WorkerScanV2ContractV4Fingerprint.CreateConsent(_v4CaptureEndpointId, _v4RenderEndpointId, _v4PreparedPlan, _v4WorkerDiscoveryGeneration.Value, true, Guid.NewGuid());
        _v4AudioHostConsentPlanFingerprint = !_v4DiagnosticPlanMode && _v4ConsentChecked ? _v4PlanFingerprint : null;
        _v4ConsentPending = false; _v4ConsentChecked = false; _v4DiagnosticConsentChecked = false;
        _v4PlanStatusKey = "VmV4ReceiptCreatedReady"; _v4PlanStatusArgument = null;
        Changed(nameof(V4ConsentPending)); Changed(nameof(V4ConsentChecked)); Changed(nameof(V4ConsentReceipt)); Changed(nameof(V4ReceiptCreated)); Changed(nameof(V4PlanStatus)); Changed(nameof(V4ConsentSummary));
        RefreshV4Commands();
    }

    private bool CanStartV4Scan() => !_v4DiagnosticPlanMode && CanStartV4Plan();
    private bool CanStartV4Diagnostic()
    {
        if (!_v4DiagnosticPlanMode || !CanStartV4Plan() || _v4PreparedPlan is not { Candidates.Count: 1, RepeatabilityRuns: 0, MaximumOperations: 3, MaximumActiveDuplexSeconds: 5, SessionLifetimeSeconds: 139, Route.Mode: "MONO_TO_STEREO" } plan ||
            plan.Route.Capture.SampleRateHz != 48_000 || plan.Route.Render.SampleRateHz != 48_000 || plan.Route.Capture.Encoding != "PCM16" || plan.Route.Render.Encoding != "PCM16" || plan.MmcssModes.Count != 1 || plan.MmcssModes[0] != "OFF") return false;
        var candidate = plan.Candidates[0];
        return candidate.Index == 0 && ((candidate.RequestedDuration100ns == 50_000 && candidate.CaptureFrames == 240 && candidate.RenderFrames == 240 && candidate.CapturePeriod100ns == 50_000 && candidate.RenderPeriod100ns == 50_000) ||
            (candidate.RequestedDuration100ns == 100_000 && candidate.CaptureFrames == 480 && candidate.RenderFrames == 480 && candidate.CapturePeriod100ns == 100_000 && candidate.RenderPeriod100ns == 100_000));
    }
    private bool CanStartV4Plan() => !V4ScanRunning && _v4AutomaticSearchWorkflow is not null && _v4PreparedPlan is { RepeatabilityRuns: 0 or 5 } prepared &&
        _v4ConsentReceipt is { Accepted: true } receipt && _v4CaptureEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } capture &&
        _v4RenderEndpoint is { DeviceState: "ACTIVE", EndpointId.Length: > 0 } render && receipt.PlanFingerprint == _v4PlanFingerprint && IsCurrentV4Plan() &&
        !AutomaticScanBusy && !WorkerOperationActive && !_deviceScanRunning && !_realQueryRunning && !_realInitializeRunning && !_duplexValidationRunning &&
        !_duplexRepeatabilityRunning && !_periodDiscoveryRunning && !_customPeriodDuplexRunning;

    private async Task StartV4ScanAsync()
    {
        if (!(V4DiagnosticPlanMode ? CanStartV4Diagnostic() : CanStartV4Scan()) || _v4AutomaticSearchWorkflow is null || _v4PreparedPlan is null || _v4ConsentReceipt is null || _v4CaptureEndpoint is null || _v4RenderEndpoint is null) return;
        var prepared = new WorkerScanV2V4PreparedPlan(_v4CaptureEndpointId!, _v4RenderEndpointId!, _v4PreparedPlan, _v4ConsentReceipt);
        _v4DiagnosticRunStarted = V4DiagnosticPlanMode;
        _v4ConsentReceipt = null;
        Changed(nameof(V4ConsentReceipt)); Changed(nameof(V4ReceiptCreated)); RefreshV4Commands();
        var capture = _v4CaptureEndpoint; var render = _v4RenderEndpoint;
        var cancellation = new CancellationTokenSource(); _v4ScanCancellation = cancellation;
        var terminal = _v4ScanTerminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _v4SearchResult = null; _v4EvidencePath = ""; _v4ProgressText = Localization.Get("VmV4ScanStarting");
        Changed(nameof(V4ResultState)); Changed(nameof(V4ResultSummary)); Changed(nameof(V4EvidencePath)); Changed(nameof(V4ProgressText));
        V4ScanRunning = true;
        var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe");
        var evidenceRoot = _v4EvidenceDirectory ?? Path.Combine(localRoot, "Scans", "V4");
        var profileDirectory = _profileDirectory ?? Path.Combine(localRoot, "Profiles");
        var progress = new Progress<V4AutomaticSearchProgress>(p =>
        {
            _v4ProgressText = string.Format(Localization.CurrentCulture, Localization.Get("VmV4ScanProgress"), p.Stage, p.CandidateIndex + 1,
                p.CaptureFrames?.ToString(CultureInfo.CurrentCulture) ?? "?", p.RenderFrames?.ToString(CultureInfo.CurrentCulture) ?? "?", p.RepeatabilityCompleted,
                p.ActualCaptureFrames?.ToString(CultureInfo.CurrentCulture) ?? "?", p.ActualRenderFrames?.ToString(CultureInfo.CurrentCulture) ?? "?");
            Changed(nameof(V4ProgressText));
        });
        try
        {
            V4AutomaticSearchResult result;
            if (V4DiagnosticPlanMode)
                result = await _v4AutomaticSearchWorkflow.RunDiagnosticAsync(prepared, capture, render, evidenceRoot, progress, cancellation.Token);
            else if (_v4ScanQualificationCoordinator is not null && prepared.Plan.RepeatabilityRuns == 5)
            {
                var auth = new AudioHostQualificationAuthorization(prepared.Consent.ConfirmationId, prepared.Consent.PlanFingerprint,
                    prepared.Consent.DiscoveryGeneration, prepared.CaptureEndpointId, prepared.RenderEndpointId, prepared.Consent.Accepted,
                    OpenSpeakersConfirmedOff: prepared.Consent.Accepted && _v4AudioHostConsentPlanFingerprint == prepared.Consent.PlanFingerprint,
                    prepared.Plan.Candidates.Count, checked(60 * prepared.Plan.Candidates.Count),
                    DateTimeOffset.UtcNow.AddSeconds(prepared.Plan.SessionLifetimeSeconds + 175 * prepared.Plan.Candidates.Count + 30));
                var package = Environment.GetEnvironmentVariable("KLIDAN_AUDIOHOST_QA_PACKAGE") ?? string.Empty;
                var qualified = await _v4ScanQualificationCoordinator.RunAsync(prepared, capture, render, auth, package, evidenceRoot,
                    profileDirectory, progress, cancellation.Token);
                result = new(qualified.Evidence, qualified.EvidencePath) { QualificationStatus = qualified.Status, QualificationDiagnostic = qualified.Diagnostic, ProfilePath = qualified.ProfilePath };
                _v4ProgressText = qualified.Diagnostic is null ? qualified.Status : $"{qualified.Status}: {qualified.Diagnostic}";
            }
            else result = await _v4AutomaticSearchWorkflow.RunAsync(prepared, capture, render, evidenceRoot, profileDirectory,
                    prepared.Plan.RepeatabilityRuns == 5, progress, cancellation.Token);
            _v4SearchResult = result; _v4EvidencePath = result.EvidencePath ?? "";
            _v4ProgressText = result.QualificationStatus == "NOT_QUALIFIED" && result.QualificationDiagnostic is null
                ? result.Evidence.StopReason
                : $"{result.QualificationStatus}{(string.IsNullOrWhiteSpace(result.QualificationDiagnostic) ? "" : $": {result.QualificationDiagnostic}")}";
        }
        catch (Exception ex)
        {
            _v4ProgressText = string.Format(Localization.CurrentCulture, Localization.Get("VmV4ScanError"), ex.GetType().Name, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_v4ScanCancellation, cancellation)) _v4ScanCancellation = null;
            cancellation.Dispose(); V4ScanRunning = false;
            terminal.TrySetResult();
            if (ReferenceEquals(_v4ScanTerminal, terminal)) _v4ScanTerminal = null;
            Changed(nameof(V4ResultState)); Changed(nameof(V4ResultSummary)); Changed(nameof(V4EvidencePath)); Changed(nameof(V4ProgressText));
            StartV4ScanCommand.Refresh(); CancelV4ScanCommand.Refresh(); CancelV4CustomPlanCommand.Refresh();
        }
    }

    private void CancelV4Scan()
    {
        _v4ScanCancellation?.Cancel();
        _v4ProgressText = Localization.Get("VmV4ScanCancellationRequested"); Changed(nameof(V4ProgressText));
    }

    private string BuildV4ResultSummary()
    {
        if (_v4SearchResult is not { } result) return _v4ProgressText;
        if (result.Evidence.DiagnosticOnly) return BuildV4DiagnosticResultSummary(result.Evidence);
        var evidence = result.Evidence;
        var state = evidence.SelectionState switch { "MINIMUM_SHORT_CLEAN_FOUND" => "PRELIMINARY", "REPEATABILITY_CONFIRMED" => "REPEATABILITY_CONFIRMED", "REPEATABILITY_FAILED" => "REPEATABILITY_FAILED", "UNKNOWN" => "UNKNOWN", _ => "MINIMUM_NOT_PROVEN" };
        var selected = evidence.SelectedCandidateIndex;
        var selectedRow = selected is { } index && index < evidence.Candidates.Count ? evidence.Candidates[index] : null;
        var candidate = selectedRow?.Candidate;
        var period = candidate is null ? "UNKNOWN" : (candidate.RequestedDuration100ns / 10_000m).ToString("0.###", Localization.CurrentCulture) + " ms";
        var captureActual = selectedRow?.ActualCapturePeriod100ns is { } cp ? (cp / 10_000m).ToString("0.###", Localization.CurrentCulture) + " ms" : "UNKNOWN";
        var renderActual = selectedRow?.ActualRenderPeriod100ns is { } rp ? (rp / 10_000m).ToString("0.###", Localization.CurrentCulture) + " ms" : "UNKNOWN";
        var lowerProven = selected is { } si && evidence.Candidates.Take(si).All(c => c.LowerPeriodCoverage == "COMPLETE" && (c.DuplexDisposition is "FAILED" or "UNSUPPORTED" || c.DuplexDisposition == "CLEAN" && c.RepeatabilityStatus == "FAILED" || c.DuplexDisposition == "CLEAN" && c.AudioHostQualification is { Status: "LOAD_UNSTABLE", Cleanup: "CONFIRMED" }));
        var minimum = lowerProven && candidate is not null ? string.Format(Localization.CurrentCulture, Localization.Get("VmV4MinimumWithinDomain"), period) : Localization.Get("VmV4MinimumNotProven");
        var reasons = string.Join("; ", evidence.Candidates.Where(c => c.DuplexDisposition is "FAILED" or "UNSUPPORTED" or "UNKNOWN").Select(c => $"{c.Candidate.Index + 1}: {c.DuplexDisposition} {string.Join(',', c.ReasonCodes)}"));
        var rows = string.Join(Environment.NewLine, evidence.Candidates.Select(c => $"{(c.Candidate.RequestedDuration100ns / 10_000m).ToString("0.###", Localization.CurrentCulture)} ms · {c.DuplexDisposition}{(c.NativeDuplexDisposition is { } native && native != c.DuplexDisposition ? $" (Native {native}; evaluator {c.EvaluatorReasonCode}; expected ≥{c.EvaluatorExpectedFrames?.ToString() ?? "?"}, actual {c.EvaluatorActualFrames?.ToString() ?? "?"} frames)" : "")} · Capture {c.ActualCaptureFrames?.ToString() ?? "?"}f / Render {c.ActualRenderFrames?.ToString() ?? "?"}f · {c.SkipReason ?? string.Join(',', c.ReasonCodes)}"));
        var qualification = _v4SearchResult is { } search ? $"AudioHost: {search.QualificationStatus}{(string.IsNullOrWhiteSpace(search.QualificationDiagnostic) ? "" : $" · {search.QualificationDiagnostic}")}" : "AudioHost: NOT_QUALIFIED";
        var profile = _v4SearchResult?.ProfilePath is { } savedProfile ? $"{qualification} · PROFILE_SAVED: {savedProfile}" : _v4SearchResult?.QualificationStatus == "TEST_ONLY" ? $"{qualification} · TEST_ONLY; no DEVICE profile" : evidence.ProfileEligible ? $"{qualification} · PROFILE_SAVED: {evidence.ProfilePath}" : evidence.Provenance.Contains("TEST_FAKE", StringComparison.Ordinal) ? "TEST_ONLY" : evidence.StopReason.Contains("PROFILE_SAVE_OR_READBACK_FAILED", StringComparison.Ordinal) ? $"{qualification} · {evidence.StopReason}" : $"{qualification} · PROFILE_NOT_CREATED";
        return string.Format(Localization.CurrentCulture, Localization.Get("VmV4ScanResults"), state, period, selectedRow?.ActualCaptureFrames?.ToString() ?? "UNKNOWN", selectedRow?.ActualRenderFrames?.ToString() ?? "UNKNOWN", minimum, selectedRow?.DuplexDisposition == "CLEAN" ? "SHORT_CLEAN" : "NO", captureActual, renderActual, evidence.RepeatabilityCompleted, profile, string.IsNullOrWhiteSpace(reasons) ? "NONE" : reasons, rows, _v4EvidencePath);
    }

    private string BuildV4DiagnosticResultSummary(V2CandidateMatrixEvidence evidence)
    {
        var row = evidence.Candidates[0];
        var candidate = row.Candidate;
        var report = row.DuplexEvidence is { ValueKind: System.Text.Json.JsonValueKind.Object } duplex && duplex.TryGetProperty("report", out var native) ? native : default;
        static string Value(System.Text.Json.JsonElement side, string name) => side.ValueKind == System.Text.Json.JsonValueKind.Object && side.TryGetProperty(name, out var value) && value.ValueKind is not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined) ? value.ToString() : "UNKNOWN";
        string SideSummary(string direction, int? actualFrames, long? actualPeriod)
        {
            var side = report.ValueKind == System.Text.Json.JsonValueKind.Object && report.TryGetProperty(direction.ToLowerInvariant(), out var value) ? value : default;
            var actual = actualFrames is { } frames && actualPeriod is { } period ? $"{frames}f/{period / 10_000m:0.###} ms" : "UNKNOWN";
            return string.Format(Localization.CurrentCulture, Localization.Get("VmV4DiagnosticSideSummary"), direction, actual,
                Value(side, "events"), Value(side, "eventIntervalMedianMilliseconds"), Value(side, "eventIntervalP95Milliseconds"), Value(side, "eventIntervalP99Milliseconds"),
                Value(side, "firstEventDelayMilliseconds"), Value(side, "eventTimeoutCount"), Value(side, "eventFailureCount"), Value(side, "framesPerEventMean"),
                Value(side, "frameCoveragePercent"), Value(side, "discontinuities"), Value(side, "eventIntervalMaximumMilliseconds"),
                Value(side, "eventIntervalMaximumEndOffsetMilliseconds"), Value(side, "longEventIntervalThresholdMilliseconds"), Value(side, "longEventIntervals"));
        }
        var requestedMs = candidate.RequestedDuration100ns / 10_000m;
        var nativeDisposition = row.NativeDuplexDisposition ?? "UNKNOWN";
        var reason = row.EvaluatorReasonCode ?? string.Join(',', row.ReasonCodes);
        return string.Join(Environment.NewLine,
            string.Format(Localization.CurrentCulture, Localization.Get("VmV4DiagnosticResultHeader"), requestedMs.ToString("0.###", Localization.CurrentCulture), candidate.CaptureFrames, candidate.RenderFrames, nativeDisposition, row.DuplexDisposition, reason, row.CaptureCleanup, row.RenderCleanup),
            SideSummary("CAPTURE", row.ActualCaptureFrames, row.ActualCapturePeriod100ns),
            SideSummary("RENDER", row.ActualRenderFrames, row.ActualRenderPeriod100ns),
            Localization.Get("VmV4DiagnosticOnlyNoProfile"),
            _v4EvidencePath);
    }

    private void CancelV4CustomPlan() => InvalidateV4PreparedPlan("VmV4PlanCancelled");

    private void InvalidateV4PreparedPlan(string reasonKey)
    {
        _v4ScanCancellation?.Cancel();
        var hadState = _v4Preparing || _v4PreparedPlan is not null || _v4ConsentPending || _v4ConsentReceipt is not null || _v4DiagnosticPlanMode || _v4DiagnosticConsentChecked;
        if (!hadState) { RefreshV4Commands(); return; }
        Interlocked.Increment(ref _v4PreparationVersion);
        _v4PreparationCancellation?.Cancel();
        _v4PreparedPlan = null; _v4ConsentReceipt = null; _v4AudioHostConsentPlanFingerprint = null; _v4ConsentPending = false; _v4ConsentChecked = false; _v4DiagnosticConsentChecked = false; _v4DiagnosticPlanMode = false; _v4DiagnosticRunStarted = false;
        _v4CaptureEndpointId = _v4RenderEndpointId = _v4CaptureFriendlyName = _v4RenderFriendlyName = _v4UiDiscoveryGeneration = null;
        _v4CaptureEndpoint = _v4RenderEndpoint = null;
        _v4WorkerDiscoveryGeneration = null; _v4PlanFingerprint = null; _v4Preparing = false; _v4PlanStatusKey = reasonKey; _v4PlanStatusArgument = null;
        Changed(nameof(V4PlanPreparing)); Changed(nameof(V4PlanReady)); Changed(nameof(V4ConsentPending)); Changed(nameof(V4ConsentChecked)); Changed(nameof(V4ConsentReceipt)); Changed(nameof(V4ReceiptCreated));
        Changed(nameof(V4PlanStatus)); Changed(nameof(V4PlanFingerprint)); Changed(nameof(V4ConsentSummary)); Changed(nameof(V4DiagnosticConsentChecked)); Changed(nameof(V4DiagnosticPlanMode));
        RefreshV4Commands();
    }

    private string BuildV4ConsentSummary()
    {
        if (_v4PreparedPlan is not { } plan || _v4CaptureEndpointId is null || _v4RenderEndpointId is null || _v4WorkerDiscoveryGeneration is null) return Localization.Get("VmV4PlanNotPrepared");
        static string Duration(long value) => (value / 10_000m).ToString("0.###", Localization.CurrentCulture);
        var bounds = string.Format(Localization.CurrentCulture, Localization.Get("VmV4BoundsSummary"),
            Duration(plan.CaptureBounds.MinimumPeriod100ns), Duration(plan.CaptureBounds.DefaultPeriod100ns), Duration(plan.RenderBounds.MinimumPeriod100ns), Duration(plan.RenderBounds.DefaultPeriod100ns),
            Duration(Math.Max(plan.CaptureBounds.MinimumPeriod100ns, plan.RenderBounds.MinimumPeriod100ns)), Duration(Math.Min(plan.CaptureBounds.DefaultPeriod100ns, plan.RenderBounds.DefaultPeriod100ns)));
        var candidates = string.Join(", ", plan.Candidates.Select(c => string.Format(Localization.CurrentCulture, Localization.Get("VmV4CandidateSummary"), Duration(c.RequestedDuration100ns), c.CaptureFrames, Duration(c.CapturePeriod100ns), c.RenderFrames, Duration(c.RenderPeriod100ns), c.RejectionReason.Length == 0 ? "" : $" ({c.RejectionReason})")));
        var capture = SelectedEndpointSummary(_v4CaptureFriendlyName, _v4CaptureEndpointId, plan.Route.Capture);
        var render = SelectedEndpointSummary(_v4RenderFriendlyName, _v4RenderEndpointId, plan.Route.Render);
        var consent = string.Format(Localization.CurrentCulture, Localization.Get("VmV4ConsentSummary"), capture, render, plan.Route.Mode, bounds, plan.Candidates.Count, candidates,
            plan.CaptureBounds.AlignmentFrames is null ? "ALIGNMENT_UNKNOWN" : plan.CaptureBounds.AlignmentFrames.Value.ToString(CultureInfo.CurrentCulture),
            plan.RenderBounds.AlignmentFrames is null ? "ALIGNMENT_UNKNOWN" : plan.RenderBounds.AlignmentFrames.Value.ToString(CultureInfo.CurrentCulture),
            plan.RepeatabilityRuns, plan.MaximumOperations, plan.MaximumActiveDuplexSeconds, plan.SessionLifetimeSeconds, plan.InitializeTimeoutSeconds, plan.CleanupDeadlineSeconds, _v4PlanFingerprint);
        if (!_v4DiagnosticPlanMode && plan.RepeatabilityRuns == 5)
            consent += Environment.NewLine + string.Format(Localization.CurrentCulture, Localization.Get("VmV4AudioHostConsent"), plan.Candidates.Count, 60 * plan.Candidates.Count);
        if (_v4DiagnosticPlanMode) consent = string.Format(Localization.CurrentCulture, Localization.Get("V4DiagnosticConsentScope"), string.Join(",", plan.MmcssModes)) + Environment.NewLine + consent;
        return _v4ConsentReceipt is { } receipt ? consent + Environment.NewLine + string.Format(Localization.CurrentCulture, Localization.Get("VmV4ReceiptSummary"), receipt.ConfirmationId, receipt.DiscoveryGeneration) : consent;

        static string SelectedEndpointSummary(string? name, string id, WorkerScanV2Side side) => string.Format(Localization.CurrentCulture, Localization.Get("VmV4EndpointSummary"), name, id, side.SampleRateHz, side.Encoding, side.Channels, string.Join("/", side.SelectedIndicesZeroBased.Select(i => i + 1)));
    }

    private async Task PrepareAutomaticScanAsync()
    {
        if (_automaticScanWorkflow is null || !CanPrepareAutomaticScan()) return;
        var v2Route = _automaticScanEngine == "V2_ADAPTIVE" ? BuildV2Route() : null;
        if (_automaticScanEngine == "V2_ADAPTIVE" && v2Route is null) return;
        var selection = new AutomaticScanSelection(SelectedWindowsEndpoint?.EndpointId, AutomaticScanPairEnabled,
            AutomaticScanPairEnabled ? SelectedDuplexCaptureEndpoint?.EndpointId : null,
            AutomaticScanPairEnabled ? SelectedDuplexRenderEndpoint?.EndpointId : null,
            SelectedPhysicalDeviceGroup?.GroupId, SelectedPhysicalDeviceGroup?.CanAutoSelectPair == true, AllowUnconfirmedPhysicalPair, AutomaticSustainedMode.StartsWith("Extended", StringComparison.Ordinal));
        CancelAutomaticScanConsent();
        AutomaticScanPreparing = true;
        _automaticScanPreparation = null;
        AutomaticScanConsentPending = false;
        AutomaticScanConsentChecked = false;
        SetAutomaticScanStatus(Localization.Get("VmAutomaticScanRefreshingBeforePlan"));
        using var refresh = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var discovery = await _automaticScanWorkflow.RefreshEndpointsAsync(refresh.Token);
            ApplyAutomaticDiscovery(discovery, selection);
            if (v2Route is not null)
            {
                if (SelectedDuplexCaptureEndpoint?.EndpointId is not { } captureId || SelectedDuplexRenderEndpoint?.EndpointId is not { } renderId || _worker is null || _worker.Capabilities.Contains("RealExclusiveDuplexValidationV2", StringComparer.Ordinal) != true)
                    throw new InvalidOperationException(Localization.Get("VmV2SelectPair"));
                ApplyV2RouteSelection(v2Route);
                _automaticScanV2Route = v2Route;
                _automaticScanV2DiscoveryGeneration = _windowsDiscoveryGeneration;
                _automaticScanV2CaptureEndpointId = captureId;
                _automaticScanV2RenderEndpointId = renderId;
                _automaticScanPreparation = null;
                _automaticScanProgressRows.Clear(); Changed(nameof(AutomaticScanProgressRows));
                AutomaticScanConsentChecked = false; AutomaticScanConsentPending = true;
                SetAutomaticScanStatus(Localization.Get("VmAutomaticScanPlanPreparedConsentRequired"));
                Changed(nameof(AutomaticScanConsentText));
                return;
            }
            var preparation = _automaticScanWorkflow.Prepare(discovery, selection);
            _automaticScanPreparation = preparation;
            _automaticScanProgressRows.Clear();
            Changed(nameof(AutomaticScanProgressRows));
            AutomaticScanConsentChecked = false;
            AutomaticScanConsentPending = true;
            SetAutomaticScanStatus(Localization.Get("VmAutomaticScanPlanPreparedConsentRequired"));
            Changed(nameof(AutomaticScanConsentText));
        }
        catch (Exception ex)
        {
            InvalidateAutomaticDeviceSelection();
            SetAutomaticScanStatus(string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanPlanPrepareFailed"), ex.GetType().Name, ex.Message));
        }
        finally { AutomaticScanPreparing = false; }
    }

    private void ApplyAutomaticDiscovery(WindowsEndpointDiscoveryDto discovery, AutomaticScanSelection selection)
    {
        InvalidateV4PreparedPlan("VmV4PlanInvalidated");
        WindowsEndpointDiscoveryValidation.Validate(discovery, _worker?.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal) == true);
        CancelAutomaticScanConsent();
        InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason.EndpointRediscovered);
        _windowsDiscoveryGeneration = Guid.NewGuid().ToString("D");
        var previousId = selection.EndpointId;
        var captureId = selection.CaptureEndpointId;
        var renderId = selection.RenderEndpointId;
        _windowsEndpointSnapshot = discovery;
        _lastWindowsEndpointSnapshot = discovery;
        _windowsDiscoveryCurrent = true;
        _windowsDiscoveryWorkerGeneration = _worker?.DiscoveryGeneration;
        _windowsDiscoveryError = null;
        var refreshedGroups = PhysicalDeviceGrouping.Group(discovery.Endpoints);
        _physicalDeviceGroups = refreshedGroups;
        CancelPeriodDiscovery(Localization.Get("VmPeriodPlanDiscardedDiscoveryRefreshed"));
        Changed(nameof(PhysicalDeviceGroups)); Changed(nameof(PhysicalDeviceGroupDetails)); Changed(nameof(PhysicalDeviceGroupStatusText));
        var refreshedGroup = PhysicalDeviceGroups.FirstOrDefault(g => g.GroupId == selection.PhysicalGroupId);
        if (selection.PhysicalGroupId is not null && refreshedGroup is null)
        {
            InvalidateAutomaticDeviceSelection();
            throw new InvalidOperationException(Localization.Get("VmAutomaticScanPhysicalGroupChanged"));
        }
        if (selection.RequireUnambiguousPair && refreshedGroup?.CanAutoSelectPair != true)
        {
            InvalidateAutomaticDeviceSelection();
            throw new InvalidOperationException(Localization.Get("VmAutomaticScanPairAmbiguous"));
        }
        Set(ref _selectedWindowsEndpoint, discovery.Endpoints.FirstOrDefault(e => e.EndpointId == previousId && e.DeviceState == "ACTIVE"), nameof(SelectedWindowsEndpoint));
        Set(ref _selectedDuplexCapture, discovery.Endpoints.FirstOrDefault(e => e.EndpointId == captureId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE"), nameof(SelectedDuplexCaptureEndpoint));
        Set(ref _selectedDuplexRender, discovery.Endpoints.FirstOrDefault(e => e.EndpointId == renderId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE"), nameof(SelectedDuplexRenderEndpoint));
        var group = refreshedGroup ?? (captureId is not null && renderId is not null
            ? PhysicalDeviceGroups.FirstOrDefault(g => g.CaptureEndpointId == captureId && g.RenderEndpointId == renderId)
            : previousId is not null
                ? PhysicalDeviceGroups.FirstOrDefault(g => g.CaptureEndpointId == previousId || g.RenderEndpointId == previousId)
                : null);
        Set(ref _selectedPhysicalDeviceGroup, group, nameof(SelectedPhysicalDeviceGroup));
        RefreshV2EndpointBindings();
        _automaticScanPairEnabled = selection.ExplicitPair;
        _allowUnconfirmedPhysicalPair = selection.AllowUnconfirmedPair;
        Changed(nameof(HasSelectedWindowsEndpoint)); Changed(nameof(WindowsEndpoints)); Changed(nameof(PhysicalGroupRequiresExplicitPair)); Changed(nameof(ShowNormalPairSelection)); Changed(nameof(AllowUnconfirmedPhysicalPair)); Changed(nameof(WindowsEndpointRows));
        Changed(nameof(DuplexCaptureEndpoints)); Changed(nameof(DuplexRenderEndpoints)); Changed(nameof(WindowsDiscoveryStatusText));
        Changed(nameof(CanUseSelectedWindowsEndpoint)); Changed(nameof(HasCurrentWindowsEndpoints)); Changed(nameof(HasHistoricalWindowsEndpoints));
        Changed(nameof(WindowsEndpointInspectorFields)); Changed(nameof(DeviceScanAvailabilityReason)); Changed(nameof(DuplexDiscoveryFreshness));
        Changed(nameof(AutomaticScanAvailabilityReason)); Changed(nameof(AutomaticScanConsentText));
        if (previousId is not null && SelectedWindowsEndpoint is null || captureId is not null && SelectedDuplexCaptureEndpoint is null || renderId is not null && SelectedDuplexRenderEndpoint is null)
        {
            InvalidateAutomaticDeviceSelection();
            throw new InvalidOperationException(Localization.Get("VmAutomaticScanEndpointUnavailable"));
        }
        RefreshWindowsEndpointBrowser();
        DiscoverWindowsEndpointsCommand.Refresh();
    }

    private void InvalidateAutomaticDeviceSelection()
    {
        CancelAutomaticScanConsent();
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
        if (_automaticScanWorkflow is null || !CanConfirmAutomaticScan()) return;
        var isV2 = AutomaticScanEngine == "V2_ADAPTIVE";
        var preparation = _automaticScanPreparation;
        var route = isV2 ? BuildV2Route() : null;
        var preparedCapture = isV2 ? SelectedDuplexCaptureEndpoint?.EndpointId : preparation?.Plan.Target.Capture?.EndpointId;
        var preparedRender = isV2 ? SelectedDuplexRenderEndpoint?.EndpointId : preparation?.Plan.Target.Render?.EndpointId;
        var target = preparation?.Plan.Target;
        var invalidV1Endpoints = !isV2 && (target is null || target.Capture is null && string.IsNullOrWhiteSpace(preparedRender) ||
            target.Render is null && string.IsNullOrWhiteSpace(preparedCapture) || target.Capture is not null && target.Render is not null &&
            (string.IsNullOrWhiteSpace(preparedCapture) || string.IsNullOrWhiteSpace(preparedRender)));
        if (isV2 && (_automaticScanV2Route is null || route is null || string.IsNullOrWhiteSpace(preparedCapture) || string.IsNullOrWhiteSpace(preparedRender) ||
            WorkerScanPlanV2Fingerprint.Route(preparedCapture, preparedRender, route) != WorkerScanPlanV2Fingerprint.Route(preparedCapture, preparedRender, _automaticScanV2Route) || _automaticScanV2DiscoveryGeneration != _windowsDiscoveryGeneration) || invalidV1Endpoints)
        {
            CancelAutomaticScanConsent();
            SetAutomaticScanStatus(Localization.Get("VmScanAuthorizationInvalidated"));
            return;
        }
        AutomaticScanConsentPending = false;
        AutomaticScanConsentChecked = false;
        AutomaticScanRunning = true;
        InvalidateAutomaticScanExport(Localization.Get("VmScanExportInvalidatedByNewScan"));
        DeveloperToolsOpen = false;
        WorkflowStep = OperatorStep.Test;
        _automaticScanAttempted = true;
        _automaticScanTerminal = false;
        _automaticScanResult = null;
        _automaticScanV2Result = null;
        Changed(nameof(AutomaticScanAttempted)); Changed(nameof(AutomaticScanResultSummary)); Changed(nameof(AutomaticScanPersistenceText)); Changed(nameof(AutomaticScanAutoShortVisible)); Changed(nameof(AutomaticScanAutoShortCandidates));
        OpenAutomaticScanLocationCommand.Refresh();
        _automaticScanCancellation = new CancellationTokenSource();
        var confirmationId = Guid.NewGuid(); // Created only after the operator accepts the displayed immutable plan.
        SetAutomaticScanStatus(Localization.Get("VmScanConsentAccepted"));
        _automaticScanTask = isV2
            ? RunAutomaticScanV2Async(preparedCapture!, preparedRender!, route!, _automaticScanV2Period, _automaticScanV2QaMode, confirmationId, _automaticScanCancellation.Token)
            : RunAutomaticScanAsync(preparation!, confirmationId, _automaticScanCancellation.Token);
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
            InvalidateAutomaticScanExport(Localization.Get("VmScanExportNeedsFreshValidation"));
            _automaticScanResult = result;
            _automaticScanOutcomeUnknown = result.Lifecycle == ScanRunLifecycle.Unknown;
            ApplyAutomaticScanEvents(result.Events);
            _automaticScanTerminal = true;
            NotifyAutomaticScanResultBindings();
            SetAutomaticScanStatus(AutomaticScanSummaryDisplay(result));
            NavigateOperatorStep(OperatorStep.Result);
        }
        catch (Exception ex)
        {
            _automaticScanOutcomeUnknown = true;
            SetAutomaticScanStatus(string.Format(Localization.CurrentCulture, Localization.Get("VmScanOutcomeUnknown"), ex.GetType().Name, ex.Message));
            _automaticScanTerminal = true;
            Changed(nameof(AutomaticScanAvailabilityReason));
            NotifyAutomaticScanResultBindings();
            NavigateOperatorStep(OperatorStep.Result);
        }
    }

    private async Task RunAutomaticScanV2Async(string captureEndpointId, string renderEndpointId, WorkerScanV2Route route, string period, string qaMode, Guid confirmationId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _automaticScanWorkflow!.RunV2Async(captureEndpointId, renderEndpointId, route, period, qaMode, confirmationId, OnAutomaticScanV2Progress, cancellationToken);
            _automaticScanV2Result = result;
            _automaticScanOutcomeUnknown = result.Lifecycle == "UNKNOWN";
            _automaticScanTerminal = true;
            NotifyAutomaticScanResultBindings();
            SetAutomaticScanStatus(AutomaticScanResultSummary);
            NavigateOperatorStep(OperatorStep.Result);
        }
        catch (Exception ex)
        {
            _automaticScanOutcomeUnknown = true;
            SetAutomaticScanStatus(string.Format(Localization.CurrentCulture, Localization.Get("VmScanOutcomeUnknown"), ex.GetType().Name, ex.Message));
            _automaticScanTerminal = true;
            Changed(nameof(AutomaticScanAvailabilityReason));
            NotifyAutomaticScanResultBindings();
            NavigateOperatorStep(OperatorStep.Result);
        }
    }

    private void OnAutomaticScanV2Progress(V2AutomaticScanProgress progress)
    {
        void Apply()
        {
            if (_automaticScanTerminal) return;
            var stage = V2StageLabel(progress.Stage);
            var state = Enum.TryParse<AutomaticScanStageState>(progress.State, true, out var parsedState) ? AutomaticScanStageStateLabel(parsedState) : progress.State;
            var operationId = progress.OperationId ?? $"v2-{progress.Stage}";
            var action = V2ProgressMessageLabel(progress.Message);
            var row = new AutomaticScanProgressRow(operationId, stage, state, progress.DurationSeconds is int duration
                ? string.Format(Localization.CurrentCulture, Localization.Get("VmV2OperationProgress"), action, duration)
                : action);
            var index = _automaticScanProgressRows.ToList().FindIndex(x => x.OperationId == row.OperationId);
            if (index >= 0) _automaticScanProgressRows[index] = row; else _automaticScanProgressRows.Add(row);
            Changed(nameof(AutomaticScanProgressRows));
            SetAutomaticScanStatus(string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanProgressDetail"), row.Stage, row.State, row.Action));
        }
        if (_uiContext is null) Apply(); else _uiContext.Post(_ => { try { Apply(); } catch { } }, null);
    }

    private static string V2ProgressMessageLabel(string code) => Localization.Get(code switch
    {
        "V2_DISCOVERY_VERIFIED" => "VmV2ProgressDiscoveryVerified", "V2_FORMAT_QUERY_COMPLETED" => "VmV2ProgressFormatQuery", "V2_FORMAT_NOT_CONFIRMED" => "VmV2ProgressFormatNotConfirmed",
        "V2_ROUTE_PERIOD_CONFIRMED" => "VmV2ProgressRouteConfirmed", "V2_INITIALIZE_CLEAN" => "VmV2ProgressInitializeClean",
        "V2_SHORT_DUPLEX_CLEAN" => "VmV2ProgressShortClean", "V2_SHORT_DUPLEX_NOT_QUALIFIED" => "VmV2ProgressShortUnknown",
        "V2_REPEATABILITY_CLEAN" => "VmV2ProgressRepeatabilityClean", "V2_REPEATABILITY_FAILED" => "VmV2ProgressRepeatabilityFailed",
        "V2_SUSTAINED_CLEAN" => "VmV2ProgressSustainedClean", "V2_SUSTAINED_NOT_QUALIFIED" => "VmV2ProgressSustainedStopped",
        "V2_FINAL_SELECTION_COMPLETE" => "VmV2ProgressFinalSelection", "V2_EVIDENCE_SAVED_READBACK" => "VmV2ProgressEvidenceSaved", "V2_EVIDENCE_SAVE_FAILED" => "VmV2ProgressEvidenceSaveFailed", "V2_INITIALIZE_FAILED" => "VmV2ProgressInitializeFailed",
        "V2_SHORT_QA_COMPLETE" => "VmV2ProgressShortQaComplete", "V2_DISCOVERY_COMPLETE" => "VmV2ProgressDiscoveryComplete",
        "NOT_REQUESTED" or "V2_QUALIFICATION_NOT_REQUESTED" => "VmV2ProgressNotRequested", "V2_OPERATION_UNKNOWN" => "VmV2ProgressOperationUnknown", _ => code
    });

    private static string V2StageLabel(string stage) => Localization.Get(stage switch
    {
        "DISCOVERY" => "VmScanStageDiscovery", "FORMAT_QUERY" => "VmScanStageQuery", "INITIALIZE" => "VmScanStageInitialize",
        "SHORT_DUPLEX" => "VmV2StageShortDuplex", "REPEATABILITY" => "VmScanStageRepeatability",
        "SUSTAINED_VALIDATION" => "VmAutomaticScanStageSustained", "FINAL_SELECTION" => "VmAutomaticScanStageFinalSelection",
        "SAVE" => "VmScanStageSave", _ => stage
    });

    private void OnAutomaticScanProgress(ScanProgressEvent progress)
    {
        void Apply()
        {
            if (_automaticScanTerminal) return;
            var row = new AutomaticScanProgressRow(progress.OperationId ?? $"local-{progress.Sequence}", ScanStageLabel(progress.Stage), OperationLifecycleLabel(progress.Lifecycle.ToString()), progress.Action);
            var index = _automaticScanProgressRows.ToList().FindIndex(x => x.OperationId == row.OperationId);
            if (index >= 0) _automaticScanProgressRows[index] = row; else _automaticScanProgressRows.Add(row);
            Changed(nameof(AutomaticScanProgressRows));
            SetAutomaticScanStatus(string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanProgressDetail"), row.Stage, row.State, row.Action));
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
        SetAutomaticScanStatus(Localization.Get("VmScanConsentCancelled"));
        Changed(nameof(AutomaticScanConsentText));
    }

    private void CancelAutomaticScan()
    {
        if (AutomaticScanConsentPending) { CancelAutomaticScanConsent(); return; }
        if (!_automaticScanRunning) return;
        _automaticScanCancellation?.Cancel();
        SetAutomaticScanStatus(Localization.Get("VmScanCancellationRequested"));
    }

    private void OpenAutomaticScanLocation()
    {
        var path = _automaticScanV2Result?.EvidencePath ?? _automaticScanResult?.Persistence?.SummaryPath;
        var directory = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }

    private bool CanOpenAutomaticScanLocation()
    {
        var path = _automaticScanV2Result?.EvidencePath ?? _automaticScanResult?.Persistence?.SummaryPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var directory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory);
    }

    private async Task ExportAutomaticScanAsync()
    {
        if (_automaticScanV2Result is { EvidencePath: { Length: > 0 } v2Source } v2Result && CanExportAutomaticScan && _bundleExportInteraction is not null)
        {
            _automaticScanExportRunning = true;
            Changed(nameof(AutomaticScanExportRunning)); Changed(nameof(CanExportAutomaticScan)); ExportAutomaticScanCommand.Refresh();
            try
            {
                var destination = await _bundleExportInteraction.PickDestinationAsync($"klidan-audio-probe-{v2Result.SessionId}.json");
                if (string.IsNullOrWhiteSpace(destination)) { SetAutomaticScanExport(Localization.Get("VmExportCancelledUnchanged"), "CANCELLED"); return; }
                if (_automaticScanV2Result?.SessionId != v2Result.SessionId || !string.Equals(_automaticScanV2Result.EvidencePath, v2Source, StringComparison.Ordinal)) { SetAutomaticScanExport(Localization.Get("VmExportScanChanged"), "SESSION_CHANGED"); return; }
                if (!await _bundleExportInteraction.ConfirmAsync(Localization.Get("VmExportPrivacyConfirmation"))) { SetAutomaticScanExport(Localization.Get("VmExportCancelledUnchanged"), "CANCELLED"); return; }
                await Task.Run(() => File.Copy(v2Source, destination, overwrite: false));
                var hashes = await Task.Run(() => (Source: SHA256.HashData(File.ReadAllBytes(v2Source)), Copy: SHA256.HashData(File.ReadAllBytes(destination))));
                if (!CryptographicOperations.FixedTimeEquals(hashes.Source, hashes.Copy)) throw new InvalidDataException("Exported V2 report does not match its source evidence.");
                SetAutomaticScanExport(string.Format(Localization.CurrentCulture, Localization.Get("VmExportVerified"), destination), "VERIFIED_V2_REPORT");
            }
            catch (Exception ex) { SetAutomaticScanExport(Localization.Get("VmExportFailedUnchanged"), $"{ex.GetType().Name}: {ex.Message}"); }
            finally { _automaticScanExportRunning = false; Changed(nameof(AutomaticScanExportRunning)); Changed(nameof(CanExportAutomaticScan)); ExportAutomaticScanCommand.Refresh(); }
            return;
        }
        if (!CanExportAutomaticScan || _bundleExportInteraction is null || _automaticScanResult?.Persistence?.SummaryPath is not { } summaryPath || _automaticScanResult.SessionId is not { } sessionId) return;
        _automaticScanExportRunning = true;
        Changed(nameof(AutomaticScanExportRunning)); Changed(nameof(CanExportAutomaticScan)); ExportAutomaticScanCommand.Refresh();
        SetAutomaticScanExport(Localization.Get("VmExportPreparing"), "EXPORTING");
        try
        {
            var directories = _bundleDirectoriesFactory?.Invoke(summaryPath) ?? LaboratoryEvidenceBundleService.DefaultDirectories(summaryPath, _profileDirectory);
            var prepared = await Task.Run(() => LaboratoryEvidenceBundleService.Prepare(sessionId, directories));
            if (_automaticScanResult?.SessionId != sessionId) { SetAutomaticScanExport(Localization.Get("VmExportScanChanged"), "SESSION_CHANGED"); return; }
            _automaticScanExportPreparation = prepared;
            _automaticScanExportTechnicalDetails = prepared.Errors.Count == 0 ? Localization.Get("VmExportSourceValidationPassed") : string.Join("\n", prepared.Errors);
            Changed(nameof(AutomaticScanExportTechnicalDetails));
            if (!prepared.IsComplete) { SetAutomaticScanExport(Localization.Get("VmExportSourceReportsUnavailable"), "INCOMPLETE"); return; }
            SetAutomaticScanExport(Localization.Get("VmExportReady"), "COMPLETE");
            var suggested = $"klidan-audio-probe-{sessionId}.zip";
            var destination = await _bundleExportInteraction.PickDestinationAsync(suggested);
            if (string.IsNullOrWhiteSpace(destination)) { SetAutomaticScanExport(Localization.Get("VmExportCancelledUnchanged"), "CANCELLED"); return; }
            if (_automaticScanResult?.SessionId != sessionId || _automaticScanExportPreparation?.SourceFingerprint != prepared.SourceFingerprint) { SetAutomaticScanExport(Localization.Get("VmExportScanChanged"), "SESSION_CHANGED"); return; }
            if (!await _bundleExportInteraction.ConfirmAsync(Localization.Get("VmExportPrivacyConfirmation"))) { SetAutomaticScanExport(Localization.Get("VmExportCancelledUnchanged"), "CANCELLED"); return; }
            var path = await Task.Run(() => LaboratoryEvidenceBundleService.Export(prepared, destination));
            var verification = await Task.Run(() => LaboratoryEvidenceBundleService.Verify(path));
            if (!verification.IsValid) throw new InvalidDataException(string.Join("; ", verification.Errors));
            SetAutomaticScanExport(string.Format(Localization.CurrentCulture, Localization.Get("VmExportVerified"), path), "VERIFIED");
        }
        catch (Exception ex) { SetAutomaticScanExport(Localization.Get("VmExportFailedUnchanged"), $"{ex.GetType().Name}: {ex.Message}"); }
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
        Changed(nameof(AutomaticScanRecommendationPeriodDisplay));
        Changed(nameof(AutomaticScanAutoShortVisible)); Changed(nameof(AutomaticScanAutoShortCandidates));
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

    private static string AutomaticScanStageLabel(AutomaticScanStage stage) => Localization.Get(stage switch
    {
        AutomaticScanStage.DISCOVERY => "VmScanStageDiscovery", AutomaticScanStage.FORMAT_QUERY => "VmScanStageQuery",
        AutomaticScanStage.PERIOD_DISCOVERY => "VmScanStagePeriodDiscovery", AutomaticScanStage.INITIALIZE => "VmScanStageInitialize",
        AutomaticScanStage.SHORT_STREAM => "VmScanStageStream", AutomaticScanStage.SHORT_DUPLEX => "VmScanStageDuplex",
        AutomaticScanStage.REPEATABILITY => "VmScanStageRepeatability", AutomaticScanStage.SUSTAINED_VALIDATION => "VmAutomaticScanStageSustained",
        AutomaticScanStage.FINAL_SELECTION => "VmAutomaticScanStageFinalSelection", _ => "VmScanStageSave"
    });

    private static string AutomaticScanStageStateLabel(AutomaticScanStageState state) => Localization.Get(state switch
    {
        AutomaticScanStageState.PLANNED => "VmAutomaticScanStatePlanned", AutomaticScanStageState.COMPLETED => "VmOperationLifecycleCompleted",
        AutomaticScanStageState.NOT_RUN => "VmAutomaticScanStateNotRun", AutomaticScanStageState.FAILED => "VmOperationLifecycleFailed",
        AutomaticScanStageState.UNKNOWN => "VmOperationLifecycleUnknown", _ => "VmOperationLifecycleSkipped"
    });

    private static string AutomaticScanStageMessageLabel(string message) => Localization.Get(message switch
    {
        "Runs automatically for an exact Capture/Render pair with current custom-period capability." => "VmAutomaticScanStageMessageExactPair",
        "Runs automatically only for candidates within the approved bounded scope." => "VmAutomaticScanStageMessageApprovedScope",
        "Runs qualified continuous 60-second screening and bounded Standard/Extended MMCSS validation when exact short evidence qualifies." => "VmAutomaticScanStageMessageSustained",
        "Evidence-based sustained primary/fallback selection; AudioHost remains NOT_CERTIFIED." => "VmAutomaticScanStageMessageSelection",
        "Bounded stage is included in the immutable scan scope." => "VmAutomaticScanStageMessageBounded",
        "Final selection awaits sustained evidence." => "VmAutomaticScanStageMessageAwaitEvidence",
        "No operation-backed automatic stage was scheduled." => "VmAutomaticScanStageMessageNoAutomaticOperation",
        "No operation was scheduled." => "VmAutomaticScanStageMessageNoOperation",
        _ => message
    });

    private static string ScanLifecycleLabel(ScanRunLifecycle lifecycle) => Localization.Get(lifecycle switch
    {
        ScanRunLifecycle.Completed => "VmScanLifecycleCompleted", ScanRunLifecycle.Incomplete => "VmScanLifecycleIncomplete",
        ScanRunLifecycle.Cancelled => "VmScanLifecycleCancelled", ScanRunLifecycle.Failed => "VmScanLifecycleFailed", _ => "VmScanLifecycleUnknown"
    });

    private static string AutomaticScanSummaryDisplay(ScanRunResult result)
    {
        var completed = result.Operations.Count(o => o.Lifecycle == ScanOperationLifecycle.Completed);
        var skipped = result.Operations.Count(o => o.Lifecycle == ScanOperationLifecycle.Skipped);
        var errors = result.Operations.Count(o => o.Lifecycle is ScanOperationLifecycle.Failed or ScanOperationLifecycle.Unknown);
        var sourceSummary = string.Format(CultureInfo.GetCultureInfo("ru-RU"), Localization.Get("VmAutomaticScanSummarySource", CultureInfo.GetCultureInfo("ru-RU")), result.Lifecycle, completed, skipped, errors);
        return result.Summary != sourceSummary ? result.Summary : string.Format(Localization.CurrentCulture, Localization.Get("VmAutomaticScanSummaryDisplay"), ScanLifecycleLabel(result.Lifecycle), completed, skipped, errors);
    }

    private static string V2LifecycleLabel(string lifecycle) => Localization.Get(lifecycle switch
    {
        "AUTO_SHORT_COMPLETED" => "VmAutoShortCompleted",
        "COMPLETED" or "SHORT_COMPLETED" => "VmAutomaticScanCompleted", "UNKNOWN" => "VmAutomaticScanOutcomeUnknown",
        "FAILED" => "VmAutomaticScanFailed", _ => "VmAutomaticScanPartiallyCompleted"
    });

    private static string FormatPeriodMilliseconds(long? period100ns) => period100ns is > 0
        ? string.Format(Localization.CurrentCulture, Localization.Get("VmAutoPeriodMilliseconds"), period100ns.Value / 10000m)
        : Localization.Get("VmValueUnknown");

    private static string V2QaModeLabel(string qaMode) => Localization.Get(qaMode switch { "DISCOVERY" => "VmV2QaDiscovery", "SHORT" => "VmV2QaShort", "AUTO_SHORT" => "VmV2QaAutoShort", _ => "VmV2QaFull" });
    private static string V2QaModeSummaryLabel(string qaMode) => Localization.Get(qaMode switch { "DISCOVERY" => "VmV2QaDiscoverySummary", "SHORT" => "VmV2QaShortSummary", "AUTO_SHORT" => "VmV2QaAutoShortSummary", _ => "VmV2QaFullSummary" });
    private static string V2ResultErrorLabel(V2AutomaticScanResult result) => result.Error is null ? Localization.Get("VmValueUnknown") :
        result.Error.Contains("SHORT_DUPLEX_NOT_CLEAN", StringComparison.Ordinal) ? Localization.Get("VmV2ProgressShortUnknown") :
        result.Error.Contains("REPEATABILITY_NOT_CLEAN", StringComparison.Ordinal) ? Localization.Get("VmV2ProgressRepeatabilityFailed") :
        result.Error.Contains("SUSTAINED_NOT_CLEAN", StringComparison.Ordinal) ? Localization.Get("VmV2ProgressSustainedStopped") :
        result.Error.Contains("SESSION_CLEANUP_UNKNOWN", StringComparison.Ordinal) ? Localization.Get("VmV2ProgressOperationUnknown") : Localization.Get("VmV2ProgressOperationFailed");

    private static string V2RecommendationText(V2AutomaticScanResult result)
    {
        if (result.QaMode == "AUTO_SHORT")
        {
            var captureFormat = $"{result.Route.Capture.SampleRateHz} Hz {result.Route.Capture.Encoding} {result.Route.Capture.Channels}ch [{string.Join("/", result.Route.Capture.SelectedIndicesZeroBased.Select(i => i + 1))}]";
            var renderFormat = $"{result.Route.Render.SampleRateHz} Hz {result.Route.Render.Encoding} {result.Route.Render.Channels}ch [{string.Join("/", result.Route.Render.SelectedIndicesZeroBased.Select(i => i + 1))}]";
            var recommendation = result.RecommendedPeriod is { } p ? $"{p} / Capture {captureFormat} / Render {renderFormat} / {result.Route.Mode}" : Localization.Get("VmAutomaticScanRecommendationMissing");
            return $"{recommendation} · {result.Qualification} / NOT_CERTIFIED";
        }
        var report = result.Operations.FirstOrDefault(o => o.Stage == "SHORT_DUPLEX" && o.Evidence is not null)?.Evidence;
        var captureFrames = report is { } c && c.TryGetProperty("capture", out var capture) && capture.TryGetProperty("actualBufferFrames", out var cf) ? cf.ToString() : Localization.Get("VmValueUnknown");
        var renderFrames = report is { } r && r.TryGetProperty("render", out var render) && render.TryGetProperty("actualBufferFrames", out var rf) ? rf.ToString() : Localization.Get("VmValueUnknown");
        return string.Format(Localization.CurrentCulture, Localization.Get("VmV2ResultConfiguration"), result.Period,
            result.Route.Mode, result.Route.Capture.SampleRateHz, result.Route.Capture.Encoding, string.Join("/", result.Route.Capture.SelectedIndicesZeroBased.Select(i => i + 1)),
            result.Route.Render.SampleRateHz, result.Route.Render.Encoding, string.Join("/", result.Route.Render.SelectedIndicesZeroBased.Select(i => i + 1)),
            captureFrames, renderFrames, result.Lifecycle);
    }

    private static string RepeatabilitySummaryDisplay(DuplexRepeatabilityResult result)
    {
        var counts = result.Counts ?? new(result.Runs.Count, result.Runs.Count(r => !string.IsNullOrWhiteSpace(r.OperationId)), result.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Completed), result.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Failed), result.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Unknown), result.Runs.Count(r => r.Lifecycle == ScanOperationLifecycle.Skipped));
        var periods = string.Join("; ", result.Periods.Select(p => $"{p.Period}: {p.Classification}"));
        var sourceSummary = string.Format(CultureInfo.GetCultureInfo("ru-RU"), Localization.Get("VmRepeatabilitySummarySource", CultureInfo.GetCultureInfo("ru-RU")), counts.Planned, counts.Attempted, counts.Completed, counts.Failed, counts.Unknown, counts.Skipped, periods);
        if (result.Summary != sourceSummary) return result.Summary;
        var displayPeriods = string.Join("; ", result.Periods.Select(p => $"{p.Period}: {Localization.Get(p.Classification switch
        {
            DuplexRepeatabilityClassification.REPEATABLY_CLEAN => "VmRepeatabilityClassClean",
            DuplexRepeatabilityClassification.INTERMITTENT_ANOMALIES => "VmRepeatabilityClassIntermittent",
            DuplexRepeatabilityClassification.REPEATABLY_ANOMALOUS => "VmRepeatabilityClassAnomalous",
            _ => "VmRepeatabilityClassInsufficient"
        })}"));
        return string.Format(Localization.CurrentCulture, Localization.Get("VmRepeatabilitySummaryDisplay"), counts.Planned, counts.Attempted, counts.Completed, counts.Failed, counts.Unknown, counts.Skipped, displayPeriods);
    }

    private static string RecommendationReasonLabel(string reason)
    {
        var key = reason switch
        {
            var source when source == Localization.Get("VmRecommendationReasonAmbiguousPeriod", CultureInfo.GetCultureInfo("ru-RU")) => "VmRecommendationReasonAmbiguousPeriod",
            var source when source == Localization.Get("VmRecommendationReasonDifferentPairs", CultureInfo.GetCultureInfo("ru-RU")) => "VmRecommendationReasonDifferentPairs",
            var source when source == Localization.Get("VmRecommendationReasonMinimumSelected", CultureInfo.GetCultureInfo("ru-RU")) => "VmRecommendationReasonMinimumSelected",
            var source when source == Localization.Get("VmRecommendationReasonNoAudioHostActivation", CultureInfo.GetCultureInfo("ru-RU")) => "VmRecommendationReasonNoAudioHostActivation",
            var source when source == Localization.Get("VmRecommendationReasonDefaultSelected", CultureInfo.GetCultureInfo("ru-RU")) => "VmRecommendationReasonDefaultSelected",
            var source when source == Localization.Get("VmRecommendationReasonShortRunOnly", CultureInfo.GetCultureInfo("ru-RU")) => "VmRecommendationReasonShortRunOnly",
            var source when source == Localization.Get("VmRecommendationReasonNoCleanCandidate", CultureInfo.GetCultureInfo("ru-RU")) => "VmRecommendationReasonNoCleanCandidate",
            var source when source == Localization.Get("VmRecommendationReasonNoSeparateProof", CultureInfo.GetCultureInfo("ru-RU")) => "VmRecommendationReasonNoSeparateProof",
            _ => null
        };
        return key is null ? reason : Localization.Get(key);
    }

    private static string HistoricalConfidenceLabel(string? confidence) => confidence switch
    {
        null or "" or "NOT_AVAILABLE" => Localization.Get("VmConfidenceNone"),
        "HIGH" => Localization.Get("VmConfidenceHigh"), "MEDIUM" => Localization.Get("VmConfidenceMedium"), "LOW" => Localization.Get("VmConfidenceLow"),
        "PROVISIONAL" => Localization.Get("VmConfidenceProvisional"),
        "PROVISIONAL_SINGLE_SUSTAINED_RUN" => Localization.Get("VmConfidenceProvisionalSingleRun"),
        "VERIFIED_SUSTAINED_CLEAN" => Localization.Get("VmConfidenceVerifiedSustained"),
        _ => confidence
    };

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
        RefreshV4Commands();
    }

    private void SetDuplexEndpoint(ref WindowsEndpointDto? field, WindowsEndpointDto? value, bool capture)
    {
        if (_automaticScanRunning || _automaticScanPreparing) return;
        if (value is null && !_windowsDiscoveryCurrent && WorkerState.State != WorkerConnectionState.ConnectedFake) return;
        var options = capture ? DuplexCaptureEndpoints : DuplexRenderEndpoints;
        if (value is not null && !options.Contains(value)) return;
        if (ReferenceEquals(field, value)) return;
        if (AutomaticScanConsentPending) CancelAutomaticScanConsent();
        _automaticScanFullBudgetConfirmed = false;
        Changed(nameof(AutomaticScanFullBudgetConfirmed));
        CancelDuplexRepeatabilityAuthorization();
        CancelPeriodDiscovery(Localization.Get("VmPeriodPlanDiscardedPairChanged"));
        field = value;
        RefreshV2EndpointBindings();
        if (DuplexConsentPending) CancelDuplexConsent();
        if (_duplexValidationRunning && _activeDuplexOperationId is not null) _ = CancelDuplexValidationAsync();
        _duplexValidations.Clear();
        if (!_automaticScanPairEnabled && SelectedPhysicalDeviceGroup is not null && value is not null)
            SelectedWindowsEndpoint = value;
        _duplexStatus = Localization.Get("VmDuplexPairInvalidated");
        Changed(capture ? nameof(SelectedDuplexCaptureEndpoint) : nameof(SelectedDuplexRenderEndpoint));
        Changed(nameof(HasCurrentDuplexPair)); Changed(nameof(DuplexConsentText)); Changed(nameof(DuplexAssessments)); Changed(nameof(DuplexResultCards)); Changed(nameof(DuplexRecommendation)); Changed(nameof(DuplexRecommendationSummary)); Changed(nameof(HasDuplexResults)); Changed(nameof(HasDuplexPairProfileEvidence)); Changed(nameof(DuplexStatus)); Changed(nameof(CanSaveDeviceProfile));
        RequestDuplexMinimumCommand.Refresh(); RequestDuplexDefaultCommand.Refresh(); PrepareDuplexRepeatabilityCommand.Refresh(); RefreshCanExecute();
        Changed(nameof(AutomaticScanAvailabilityReason)); PrepareAutomaticScanCommand.Refresh(); RefreshCanExecute();
    }

    private void RefreshV2EndpointBindings()
    {
        static int DefaultChannels(WindowsEndpointDto? endpoint) => endpoint?.SharedModeFormat.ReportedChannelCount is int n and >= 1 and <= 32 ? n : 2;
        _automaticScanV2CaptureChannels = DefaultChannels(SelectedDuplexCaptureEndpoint);
        _automaticScanV2RenderChannels = DefaultChannels(SelectedDuplexRenderEndpoint);
        _automaticScanV2CaptureChannel1 = _automaticScanV2RenderChannel1 = 1;
        _automaticScanV2CaptureChannel2 = Math.Min(2, _automaticScanV2CaptureChannels);
        _automaticScanV2RenderChannel2 = Math.Min(2, _automaticScanV2RenderChannels);
        Changed(nameof(AutomaticScanV2ChannelCounts)); Changed(nameof(AutomaticScanV2RenderChannelCounts));
        Changed(nameof(AutomaticScanV2CaptureChannels)); Changed(nameof(AutomaticScanV2RenderChannels));
        Changed(nameof(AutomaticScanV2CaptureChannelOptions)); Changed(nameof(AutomaticScanV2RenderChannelOptions));
        Changed(nameof(AutomaticScanV2CaptureChannel1)); Changed(nameof(AutomaticScanV2CaptureChannel2));
        Changed(nameof(AutomaticScanV2RenderChannel1)); Changed(nameof(AutomaticScanV2RenderChannel2));
        Changed(nameof(AutomaticScanV2ChannelControlsText)); Changed(nameof(AutomaticScanV2RouteSummary));
        ResetAutomaticScanV2Consent();
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
        Changed(nameof(DuplexRepeatabilityConsentPending)); Changed(nameof(DuplexRepeatabilityPlanText)); Changed(nameof(DuplexRepeatabilityResult)); Changed(nameof(DuplexRepeatabilityStatus)); ConfirmDuplexRepeatabilityCommand.Refresh(); CancelDuplexRepeatabilityConsentCommand.Refresh(); PrepareDuplexRepeatabilityCommand.Refresh(); RefreshV4Commands();
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
        _duplexRepeatabilityConsentPending = false; _duplexRepeatabilityConsentChecked = false; _duplexRepeatabilityRunning = true; _duplexRepeatabilityCancellation = new CancellationTokenSource(); RefreshV4Commands();
        _duplexRepeatabilityStatus = Localization.Get("VmRepeatabilityApprovalAccepted");
        Changed(nameof(DuplexRepeatabilityConsentPending)); Changed(nameof(DuplexRepeatabilityConsentChecked)); Changed(nameof(DuplexRepeatabilityRunning)); Changed(nameof(DuplexRepeatabilityStatus));
        try
        {
            var result = await _duplexRepeatabilityWorkflow.RunApprovedAsync(preparation, Guid.NewGuid(), OnDuplexRepeatabilityProgress, _duplexRepeatabilityCancellation.Token).ConfigureAwait(true);
            _duplexRepeatabilityResult = result; _duplexRepeatabilityStatus = result.PersistenceStatus == "SAVE_FAILED" ? $"SAVE_FAILED · {result.PersistenceError}" : RepeatabilitySummaryDisplay(result);
        }
        catch (Exception ex) { _duplexRepeatabilityStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmRepeatabilityOutcomeUnknown"), ex.GetType().Name, ex.Message); }
        finally { _duplexRepeatabilityRunning = false; _duplexRepeatabilityCancellation?.Dispose(); _duplexRepeatabilityCancellation = null; Changed(nameof(DuplexRepeatabilityRunning)); Changed(nameof(DuplexRepeatabilityResult)); Changed(nameof(DuplexRepeatabilityResultText)); Changed(nameof(DuplexRepeatabilityStatus)); PrepareDuplexRepeatabilityCommand.Refresh(); CancelDuplexRepeatabilityCommand.Refresh(); RefreshV4Commands(); }
    }

    private void CancelDuplexRepeatabilityConsent()
    {
        if (!_duplexRepeatabilityConsentPending) return;
        _duplexRepeatabilityConsentPending = false; _duplexRepeatabilityConsentChecked = false; _duplexRepeatabilityPreparation = null; _duplexRepeatabilityStatus = Localization.Get("VmRepeatabilityApprovalCancelled");
        Changed(nameof(DuplexRepeatabilityConsentPending)); Changed(nameof(DuplexRepeatabilityConsentChecked)); Changed(nameof(DuplexRepeatabilityPlanText)); Changed(nameof(DuplexRepeatabilityStatus)); ConfirmDuplexRepeatabilityCommand.Refresh(); PrepareDuplexRepeatabilityCommand.Refresh(); RefreshV4Commands();
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
        void Apply() { _duplexRepeatabilityStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmRepeatabilityProgressStatus"), OperationLifecycleLabel(progress.Lifecycle.ToString()), progress.Action); Changed(nameof(DuplexRepeatabilityStatus)); }
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
        try { _customRepeatabilityResult = await workflow.RunCustomApprovedAsync(preparation, Guid.NewGuid(), OnDuplexRepeatabilityProgress, _customRepeatabilityCancellation.Token).ConfigureAwait(true); _customRepeatabilityStatus = RepeatabilitySummaryDisplay(_customRepeatabilityResult); }
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
        _periodDiscoveryConsentPending = false; _periodDiscoveryConsentChecked = false; _periodDiscoveryRunning = true; _periodDiscoveryCancellation = new CancellationTokenSource(); _periodDiscoveryStatus = Localization.Get("VmPeriodDiscoveryApprovalAccepted"); RefreshV4Commands();
        Changed(nameof(PeriodDiscoveryConsentPending)); Changed(nameof(PeriodDiscoveryConsentChecked)); Changed(nameof(PeriodDiscoveryRunning)); Changed(nameof(PeriodDiscoveryStatus));
        try { _periodDiscoveryResult = await _exclusivePeriodDiscoveryWorkflow.RunApprovedAsync(preparation, Guid.NewGuid(), message => { _periodDiscoveryStatus = message; Changed(nameof(PeriodDiscoveryStatus)); }, _periodDiscoveryCancellation.Token).ConfigureAwait(true); _periodDiscoveryResultFingerprint = _periodDiscoveryResult.Lifecycle == "COMPLETED" ? preparation.DiscoveryFingerprint : null; _periodDiscoveryStatus = _periodDiscoveryResult.PersistenceStatus; }
        catch (Exception ex) { _periodDiscoveryResultFingerprint = null; _periodDiscoveryStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmPeriodDiscoveryOutcomeUnknown"), ex.GetType().Name, ex.Message); }
        finally { _periodDiscoveryRunning = false; _periodDiscoveryCancellation?.Dispose(); _periodDiscoveryCancellation = null; Changed(nameof(PeriodDiscoveryRunning)); Changed(nameof(PeriodDiscoveryResultText)); Changed(nameof(PeriodDiscoveryMatrixText)); Changed(nameof(PeriodDiscoveryReportPath)); Changed(nameof(PeriodDiscoveryStatus)); Changed(nameof(CustomPeriodDuplexAvailabilityReason)); Changed(nameof(CanPrepareCustomRepeatability)); PreparePeriodDiscoveryCommand.Refresh(); CancelPeriodDiscoveryRunCommand.Refresh(); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh(); RefreshV4Commands(); }
    }
    private void CancelPeriodDiscovery(string? message = null)
    {
        if (!_periodDiscoveryConsentPending && _periodDiscoveryPreparation is null) return;
        _periodDiscoveryConsentPending = false; _periodDiscoveryConsentChecked = false; _periodDiscoveryPreparation = null; _periodDiscoveryStatus = message ?? Localization.Get("VmPeriodDiscoveryApprovalCancelled");
        Changed(nameof(PeriodDiscoveryConsentPending)); Changed(nameof(PeriodDiscoveryConsentChecked)); Changed(nameof(PeriodDiscoveryPlanText)); Changed(nameof(PeriodDiscoveryStatus)); Changed(nameof(CustomPeriodDuplexAvailabilityReason)); Changed(nameof(CanPrepareCustomRepeatability)); ConfirmPeriodDiscoveryCommand.Refresh(); CancelPeriodDiscoveryCommand.Refresh(); PreparePeriodDiscoveryCommand.Refresh(); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh(); RefreshV4Commands();
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
        Changed(nameof(CustomPeriodDuplexConsentPending)); Changed(nameof(CustomPeriodDuplexPlanText)); Changed(nameof(CustomPeriodDuplexStatus)); Changed(nameof(CanPrepareCustomRepeatability)); ConfirmCustomPeriodDuplexCommand.Refresh(); CancelCustomPeriodDuplexConsentCommand.Refresh(); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh(); RefreshV4Commands();
    }

    private async Task ConfirmCustomPeriodDuplexAsync()
    {
        if (_customPeriodDuplexWorkflow is null || !_customPeriodDuplexConsentPending || !_customPeriodDuplexConsentChecked || _customPeriodDuplexPreparation is not { } preparation || _windowsEndpointSnapshot is null || SelectedDuplexCaptureEndpoint?.EndpointId != preparation.Capture.EndpointId || SelectedDuplexRenderEndpoint?.EndpointId != preparation.Render.EndpointId || ExclusivePeriodDiscoveryPlanning.Fingerprint(_windowsEndpointSnapshot) != preparation.DiscoveryFingerprint || !_windowsDiscoveryCurrent || WorkerState.State != WorkerConnectionState.ConnectedFake)
        { if (_customPeriodDuplexConsentPending) CancelCustomPeriodDuplexConsent(Localization.Get("VmCustomPeriodDuplexAuthorizationInvalidated")); return; }
        _customPeriodDuplexConsentPending = false; _customPeriodDuplexConsentChecked = false; _customPeriodDuplexRunning = true; _customPeriodDuplexCancellation = new(); _customPeriodDuplexStatus = Localization.Get("VmCustomPeriodDuplexApprovalAccepted"); RefreshV4Commands();
        Changed(nameof(CustomPeriodDuplexConsentPending)); Changed(nameof(CustomPeriodDuplexConsentChecked)); Changed(nameof(CustomPeriodDuplexRunning)); Changed(nameof(CustomPeriodDuplexStatus)); Changed(nameof(CanPrepareCustomRepeatability)); PrepareCustomRepeatabilityCommand.Refresh();
        try { _customPeriodDuplexResult = await _customPeriodDuplexWorkflow.RunApprovedAsync(preparation, Guid.NewGuid(), _customPeriodDuplexCancellation.Token).ConfigureAwait(true); _customPeriodDuplexStatus = _customPeriodDuplexResult.PersistenceStatus; }
        catch (Exception ex) { _customPeriodDuplexStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmCustomPeriodDuplexOutcomeUnknown"), ex.GetType().Name, ex.Message); }
        finally { _customPeriodDuplexRunning = false; _customPeriodDuplexCancellation?.Dispose(); _customPeriodDuplexCancellation = null; Changed(nameof(CustomPeriodDuplexRunning)); Changed(nameof(CustomPeriodDuplexResultText)); Changed(nameof(CustomPeriodDuplexStatus)); Changed(nameof(CanPrepareCustomRepeatability)); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh(); CancelCustomPeriodDuplexCommand.Refresh(); RefreshV4Commands(); }
    }

    private void CancelCustomPeriodDuplexConsent(string? message = null)
    { _customPeriodDuplexConsentPending = false; _customPeriodDuplexConsentChecked = false; _customPeriodDuplexPreparation = null; _customPeriodDuplexStatus = message ?? Localization.Get("VmCustomPeriodDuplexApprovalCancelled"); Changed(nameof(CustomPeriodDuplexConsentPending)); Changed(nameof(CustomPeriodDuplexConsentChecked)); Changed(nameof(CustomPeriodDuplexPlanText)); Changed(nameof(CustomPeriodDuplexStatus)); Changed(nameof(CanPrepareCustomRepeatability)); ConfirmCustomPeriodDuplexCommand.Refresh(); CancelCustomPeriodDuplexConsentCommand.Refresh(); PrepareCustomPeriodDuplexCommand.Refresh(); PrepareCustomRepeatabilityCommand.Refresh(); RefreshV4Commands(); }
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
        if (!HasCurrentDuplexPair || consent.RequestFingerprint != DuplexConsent.Fingerprint(captureId, renderId, period, 5, "OFF")) { CancelDuplexConsent(); _duplexStatus = Localization.Get("VmDuplexPairChanged"); Changed(nameof(DuplexStatus)); return; }
        _pendingDuplexConsent = null; _pendingDuplexPeriod = null; DuplexConsentPending = false; DuplexConsentChecked = false;
        var operationId = Guid.NewGuid().ToString("D"); _activeDuplexOperationId = operationId; _duplexValidationRunning = true; RefreshV4Commands();
        _duplexStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmDuplexRunning"), period);
        Changed(nameof(DuplexValidationRunning)); Changed(nameof(HasDuplexPairProfileEvidence)); Changed(nameof(CanSaveDeviceProfile)); Changed(nameof(DuplexStatus)); CancelDuplexValidationCommand.Refresh(); RequestDuplexMinimumCommand.Refresh(); RequestDuplexDefaultCommand.Refresh();
        try
        {
            var result = await _worker.ValidateDuplexAsync(operationId, captureId, renderId, period, 5, "OFF", consent);
            if (SelectedDuplexCaptureEndpoint?.EndpointId != captureId || SelectedDuplexRenderEndpoint?.EndpointId != renderId || !_windowsDiscoveryCurrent || WorkerState.State != WorkerConnectionState.ConnectedFake) throw new IOException(Localization.Get("VmDuplexBindingChanged"));
            _duplexValidations[period] = result;
            var assessment = DuplexAssessments.Single(a => a.Period == period);
            _duplexStatus = $"{period}: {assessment.Classification} · {Localization.Get("VmCaptureLabel")} {assessment.CaptureCoverage} · {Localization.Get("VmRenderLabel")} {assessment.RenderCoverage}. {assessment.Reason}";
            _operatorResultEndpoint = $"{Localization.Get("VmCaptureLabel")} · {capture.FriendlyNameDisplay}\n{Localization.Get("VmRenderLabel")} · {render.FriendlyNameDisplay}";
            _operatorResultMode = OperatorResultKind.Duplex;
            _operatorResultProvenance = "REAL_WASAPI_DUPLEX";
            _operatorResultConfiguration = string.Format(Localization.CurrentCulture, Localization.Get("VmOperatorDuplexConfiguration"), period);
            OperatorResultOutcome = $"{assessment.Classification} · {assessment.Reason}";
            Changed(nameof(DuplexAssessments)); Changed(nameof(DuplexResultCards)); Changed(nameof(DuplexRecommendation)); Changed(nameof(DuplexRecommendationSummary)); Changed(nameof(HasDuplexResults)); Changed(nameof(CanSaveDeviceProfile));
            NotifyOperatorResult();
        }
        catch (Exception ex)
        {
            _duplexStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmDuplexFailureStatus"), ex is IOException or TimeoutException or OperationCanceledException ? "UNKNOWN" : "FAILED", ex.GetType().Name, ex.Message, Localization.Get("VmDuplexCleanupUnconfirmed"));
        }
        finally
        {
            _activeDuplexOperationId = null; _duplexValidationRunning = false; Changed(nameof(DuplexValidationRunning)); Changed(nameof(HasDuplexPairProfileEvidence)); Changed(nameof(CanSaveDeviceProfile)); Changed(nameof(DuplexStatus)); CancelDuplexValidationCommand.Refresh(); RequestDuplexMinimumCommand.Refresh(); RequestDuplexDefaultCommand.Refresh(); RefreshV4Commands();
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
        _streamValidationStatus = Localization.Get("VmStreamCancellationAccepted"); Changed(nameof(StreamValidationStatus));
        try { await _worker.CancelStreamValidationAsync(_activeStreamOperationId).ConfigureAwait(false); }
        catch { _streamValidationCancellation.Cancel(); throw; }
    }

    private async Task ConfirmStreamValidationAsync()
    {
        if (!_streamConsentPending || !_streamConsentChecked || _pendingStreamConsent is not { } consent || _worker is null || SelectedWindowsEndpoint is not { EndpointId: { } endpointId, DeviceState: "ACTIVE" } endpoint || SelectedStreamCandidate is not { } candidate) return;
        if (!CanUseSelectedWindowsEndpoint || WorkerState.State != WorkerConnectionState.ConnectedFake || consent.RequestFingerprint != RealStreamConsent.Fingerprint(endpointId, endpoint.Direction, candidate.Period.ToUpperInvariant(), 5, "OFF")) { CancelStreamConsent(); _streamValidationStatus = Localization.Get("VmStreamConsentInvalidated"); Changed(nameof(StreamValidationStatus)); return; }
        CancelStreamConsent();
        var period = candidate.Period.ToUpperInvariant();
        _lastStreamAttemptPeriod = period;
        _streamAttemptedSinceProfileSave = true;
        _streamValidationRows[period] = new(period, candidate.ActualBufferFrames?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Localization.Get("VmStreamUnknownValue"), candidate.InitializeStatus, "RUNNING", Localization.Get("VmStreamUnknownValue"), Localization.Get("VmStreamUnknownValue"), "UNKNOWN", Localization.Get("VmStreamUnknownValue"), "RUNNING", "NOT_RUN", "RUNNING", Localization.Get("VmStreamAwaitingWorkerResult"));
        _streamValidationRunning = true; Changed(nameof(StreamValidationRunning)); Changed(nameof(StreamValidationRows)); RequestStreamValidationCommand.Refresh(); CancelStreamValidationCommand.Refresh(); RefreshV4Commands();
        _streamValidationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _activeStreamOperationId = Guid.NewGuid().ToString("D");
        _streamValidationProgress = string.Format(Localization.CurrentCulture, Localization.Get("VmStreamProgress"), DirectionDisplayLabel(endpoint.Direction), period);
        _streamValidationStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmStreamRunning"), period, DirectionDisplayLabel(endpoint.Direction));
        Changed(nameof(StreamValidationProgress)); Changed(nameof(StreamValidationStatus)); Changed(nameof(StreamEvidencePersistenceStatus));
        try
        {
            var result = await _worker.ValidateStreamAsync(_activeStreamOperationId, endpointId, endpoint.Direction, period, 5, "OFF", consent, _streamValidationCancellation.Token);
            if (result.EndpointId != endpointId || result.Direction != endpoint.Direction || WorkerState.State != WorkerConnectionState.ConnectedFake) throw new IOException(Localization.Get("VmStreamBindingChanged"));
            _streamValidations.Add(result);
            if (_deviceScanResult is { } currentScan && currentScan.EndpointId == endpointId && currentScan.Direction == endpoint.Direction)
                _deviceScanResult = currentScan with { StreamValidations = _streamValidations.Where(s => s.EndpointId == endpointId && s.Direction == endpoint.Direction).ToArray() };
            var report = result.Report;
            var row = StreamValidationResultRowFromReport(period, report);
            _streamValidationRows[period] = row;
            _streamValidationStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmStreamStatusDetail"), period, row.FinalStatus, row.Details);
            OperatorResultOutcome = string.Format(Localization.CurrentCulture, Localization.Get("VmStreamResultOutcome"), period, row.FinalStatus, row.StartOutcome, row.FramesProcessedExpected, row.StreamQuality, Localization.Get("VmStreamStabilityDuplexNotRun"));
            Changed(nameof(DeviceScanResult)); Changed(nameof(DeviceScanResultSummary)); Changed(nameof(StreamValidations)); Changed(nameof(StreamValidationRows)); Changed(nameof(StreamValidationStatus)); Changed(nameof(StreamValidationResultsSummary)); Changed(nameof(StreamEvidencePersistenceStatus)); Changed(nameof(OperatorResultStreamEvidenceNote)); Changed(nameof(OperatorResultProfileEvidenceNote)); Changed(nameof(CanSaveDeviceProfile));
        }
        catch (Exception ex)
        {
            var unknown = ex is WorkerCommandException commandError
                ? commandError.ErrorCode.Contains("TIMEOUT", StringComparison.Ordinal) || commandError.ErrorCode.Contains("UNKNOWN", StringComparison.Ordinal) || commandError.ErrorCode.Contains("DISCONNECT", StringComparison.Ordinal)
                : ex is TimeoutException or OperationCanceledException or IOException;
            var status = unknown ? "UNKNOWN" : "FAILED";
            var detail = string.Format(Localization.CurrentCulture, Localization.Get("VmStreamFailureDetail"), ex.GetType().Name, ex.Message, Localization.Get("VmDuplexCleanupUnconfirmed"));
            _streamValidationRows[period] = new(period, candidate.ActualBufferFrames?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Localization.Get("VmStreamUnknownValue"), candidate.InitializeStatus, "UNKNOWN", Localization.Get("VmStreamUnknownValue"), Localization.Get("VmStreamUnknownValue"), "UNKNOWN", Localization.Get("VmStreamUnknownValue"), "UNKNOWN", "UNKNOWN", status, detail);
            _streamValidationStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmStreamStatusDetail"), period, status, detail);
            OperatorResultOutcome = string.Format(Localization.CurrentCulture, Localization.Get("VmStreamFailureOutcome"), period, status, detail, Localization.Get("VmStreamStabilityDuplexNotRun"));
            Changed(nameof(StreamValidationRows)); Changed(nameof(StreamValidationStatus)); Changed(nameof(StreamValidationResultsSummary)); Changed(nameof(StreamEvidencePersistenceStatus));
        }
        finally
        {
            _streamValidationCancellation?.Dispose(); _streamValidationCancellation = null; _activeStreamOperationId = null; _streamValidationRunning = false; RefreshV4Commands();
            Changed(nameof(StreamValidationRunning)); Changed(nameof(StreamValidationRows)); RequestStreamValidationCommand.Refresh(); CancelStreamValidationCommand.Refresh();
        }
    }

    private static StreamValidationResultRow StreamValidationResultRowFromReport(string period, System.Text.Json.JsonElement report)
    {
        const string MissingReportValue = "\0";
        string? Text(string name) => report.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        string Value(string name) => report.TryGetProperty(name, out var v) && v.ValueKind is not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined) ? v.ToString() : MissingReportValue;
        string Display(string value) => value == MissingReportValue ? Localization.Get("VmStreamUnknownValue") : value;
        bool? Bool(string name) => report.TryGetProperty(name, out var v) && (v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) ? v.GetBoolean() : null;
        string Outcome(string name, string? hresultName = null)
        {
            if (!report.TryGetProperty(name, out var v) || v.ValueKind == System.Text.Json.JsonValueKind.Null) return hresultName is not null && Text(hresultName) == "NOT_ATTEMPTED" ? "NOT_RUN" : "UNKNOWN";
            return v.ValueKind == System.Text.Json.JsonValueKind.True ? "SUCCESS" : v.ValueKind == System.Text.Json.JsonValueKind.False ? "FAILED" : "UNKNOWN";
        }
        var frames = Value("framesProcessed"); var expected = Value("expectedFrames");
        var coverage = report.TryGetProperty("frameCoveragePercent", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number && c.TryGetDouble(out var percent) && double.IsFinite(percent) ? percent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%" : MissingReportValue;
        var faults = string.Join(" · ", new[] { "discontinuities", "timestampErrors", "unavailableCaptureBuffers", "bufferServiceFaults", "eventWaitTimeouts", "longEventIntervals", "eventIntervalMaximumMilliseconds", "eventIntervalMaximumEndOffsetMilliseconds", "longEventIntervalThresholdMilliseconds" }.Select(name => $"{name}={Display(Value(name))}"));
        var quality = Text("streamQuality") ?? "UNKNOWN";
        var cleanup = Text("cleanup") ?? "UNKNOWN";
        var start = Outcome("startSucceeded", "startHresult");
        var stop = Outcome("stopSucceeded", "stopHresult");
        var initialized = Outcome("initializeSucceeded", "finalInitializeHresult");
        string status;
        if (Bool("cancelled") == true) status = "CANCELLED";
        else if (Bool("watchdogTimedOut") == true) status = "UNKNOWN";
        else if (initialized == "FAILED" || start == "FAILED" || stop == "FAILED") status = "FAILED";
        else if (initialized == "UNKNOWN" || start == "UNKNOWN" || stop == "UNKNOWN" || frames == MissingReportValue || expected == MissingReportValue || quality == "UNKNOWN") status = "UNKNOWN";
        else if (quality == "CLEAN" && Bool("durationReached") == true && frames != "0" && !cleanup.Contains("FAILED", StringComparison.Ordinal) && !cleanup.Contains("UNKNOWN", StringComparison.Ordinal) && !cleanup.Contains("RELEASE_PENDING", StringComparison.Ordinal)) status = "COMPLETED";
        else status = "COMPLETED_WITH_WARNINGS";
        var errorStage = Text("errorStage"); var errorHresult = Text("errorHresult");
        var details = errorStage is { Length: > 0 } and not ("none" or "UNKNOWN") ? string.Format(Localization.CurrentCulture, Localization.Get("VmStreamDetailsStageHresult"), errorStage, Display(errorHresult ?? MissingReportValue)) : quality == "CLEAN" ? Localization.Get("VmStreamDetailsCleanReport") : string.Format(Localization.CurrentCulture, Localization.Get("VmStreamDetailsQualityAndCleanup"), quality, Display(Value("durationReached")), cleanup);
        return new(period, Display(Value("actualBufferFrames")), initialized, start, $"{Display(frames)} / {Display(expected)}", Display(coverage), quality, faults, stop, cleanup, status, details);
    }

    private void CancelDeviceScan()
    {
        if (!_deviceScanRunning || _deviceScanCancellation is null) return;
        _deviceScanProgress = Localization.Get("VmDeviceScanCancellationWaiting");
        Changed(nameof(DeviceScanProgress));
        _deviceScanCancellation.Cancel();
        Changed(nameof(CanSaveDeviceProfile));
    }

    private async Task ConfirmDeviceScanAsync()
    {
        if (!DeviceScanConsentPending || !DeviceScanConsentChecked || _worker is null || SelectedWindowsEndpoint is not { } endpoint || _pendingDeviceScanConsent is not { } consent) return;
        if (!CanUseSelectedWindowsEndpoint || endpoint.EndpointId != consent.EndpointId || endpoint.Direction != consent.Direction || DeviceConfigurationScan.Fingerprint(endpoint) != consent.Fingerprint || WorkerState.State != WorkerConnectionState.ConnectedFake)
        { CancelDeviceScanConsent(); WorkerNotice = Localization.Get("VmDeviceScanConsentInvalidated"); return; }
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
        SetDeviceScanProgress(DeviceScanStage.Preflight, Localization.Get("VmDeviceScanPreflight"));
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
            _streamValidationStatus = Localization.Get("VmDeviceScanStreamSeparateConsentRequired");
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
            _operatorResultEndpoint = $"{endpoint.FriendlyNameDisplay} · {DirectionDisplayLabel(endpoint.Direction)} · {endpoint.DeviceState}";
            _operatorResultMode = OperatorResultKind.DeviceScan;
            _operatorResultProvenance = "REAL_WASAPI_QUERY + REAL_WASAPI_INITIALIZE_ONLY";
            _operatorResultConfiguration = string.Format(Localization.CurrentCulture, Localization.Get("VmOperatorDeviceScanConfiguration"), result.Initializations.Count);
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
        _deviceScanProgress = $"{DeviceScanStageLabel(stage)} · {message}";
        Changed(nameof(DeviceScanProgress));
    }

    private static string DeviceScanStageLabel(DeviceScanStage stage) => stage switch
    {
        DeviceScanStage.Idle => Localization.Get("VmDeviceScanStageIdle"),
        DeviceScanStage.Preflight => Localization.Get("VmDeviceScanStagePreflight"),
        DeviceScanStage.PassiveQuery => Localization.Get("VmDeviceScanStagePassiveQuery"),
        DeviceScanStage.CandidateSelection => Localization.Get("VmDeviceScanStageCandidateSelection"),
        DeviceScanStage.ActiveInitialize => Localization.Get("VmDeviceScanStageActiveInitialize"),
        DeviceScanStage.StreamValidation => Localization.Get("VmDeviceScanStageStreamValidation"),
        DeviceScanStage.ResultAggregation => Localization.Get("VmDeviceScanStageResultAggregation"),
        DeviceScanStage.Completed => Localization.Get("VmDeviceScanStageCompleted"),
        DeviceScanStage.Cancelling => Localization.Get("VmDeviceScanStageCancelling"),
        DeviceScanStage.Cancelled => Localization.Get("VmDeviceScanStageCancelled"),
        DeviceScanStage.Failed => Localization.Get("VmDeviceScanStageFailed"),
        DeviceScanStage.Incomplete => Localization.Get("VmDeviceScanStageIncomplete"),
        _ => stage.ToString()
    };

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
        _realQueryRunning = true; RefreshV4Commands();
        RequestDeviceScanCommand.Refresh();
        _realFormatError = Localization.Get("VmQueryInvalidated");
        Changed(nameof(RealFormatMatrixStatus)); Changed(nameof(HasOperatorQueryResult)); Changed(nameof(HasCurrentOperatorQueryResult)); Changed(nameof(NeedsInitializeFormatQuery)); Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(CanSaveDeviceProfile)); RequestRealInitializeCommand.Refresh();
        try
        {
            var requestedChannels = RealQueryChannelCountsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(value => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : -1).ToArray();
            if (requestedChannels.Length is < 1 or > 32 || requestedChannels.Any(count => count is < 1 or > 64) || requestedChannels.Distinct().Count() != requestedChannels.Length) throw new ArgumentException(Localization.Get("VmQueryChannelsValidation"));
            _operatorResultEndpoint = $"{endpoint.FriendlyNameDisplay} · {DirectionDisplayLabel(endpoint.Direction)} · {endpoint.DeviceState}";
            _operatorResultMode = OperatorResultKind.FormatQuery;
            _operatorResultProvenance = "REAL_WASAPI_QUERY";
            _operatorResultConfiguration = string.Format(Localization.CurrentCulture, Localization.Get("VmOperatorFormatQueryConfiguration"), string.Join(", ", requestedChannels));
            NotifyOperatorResult();
            var result = await _worker.QueryFormatMatrixAsync(id, endpoint.Direction, requestedChannels);
            if (WorkerState.State != WorkerConnectionState.ConnectedFake || !_windowsDiscoveryCurrent)
            {
                _operatorResultEndpoint = $"{endpoint.FriendlyNameDisplay} · {DirectionDisplayLabel(endpoint.Direction)} · {endpoint.DeviceState}";
                _operatorResultMode = OperatorResultKind.FormatQuery;
                _operatorResultConfiguration = Localization.Get("VmQueryDisconnectedConfiguration");
                _realFormatError = Localization.Get("VmQueryDisconnectedError");
                OperatorResultOutcome = Localization.Get("VmQueryDisconnectedOutcome");
                NotifyOperatorResult(); Changed(nameof(RealFormatMatrixStatus)); Changed(nameof(HasOperatorQueryResult)); Changed(nameof(HasCurrentOperatorQueryResult)); Changed(nameof(NeedsInitializeFormatQuery)); Changed(nameof(CanSaveDeviceProfile));
                if (OperatorTestMode != "INITIALIZE_ONLY") NavigateOperatorStep(OperatorStep.Result);
                return;
            }
            if (result.Provenance != "REAL_WASAPI_QUERY" || result.ReportLevel != "PASSIVE_QUERY" || result.EndpointId != id || result.Direction != endpoint.Direction) throw new InvalidDataException(Localization.Get("VmQueryResponseMismatch"));
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
        catch (Exception ex) { _operatorResultEndpoint = $"{endpoint.FriendlyNameDisplay} · {DirectionDisplayLabel(endpoint.Direction)} · {endpoint.DeviceState}"; _operatorResultMode = OperatorResultKind.FormatQuery; _realFormatError = $"{Localization.Get("VmQueryErrorPrefix")} REAL_WASAPI_QUERY: {ex.GetType().Name}: {ex.Message}" + (RealFormatMatrix is null ? "" : Localization.Get("VmQueryHistoricalSuffix")); OperatorResultOutcome = Localization.Get("VmQueryFailureOutcome"); NotifyOperatorResult(); Changed(nameof(RealFormatMatrixStatus)); Changed(nameof(RealFormatSummary)); Changed(nameof(HasOperatorQueryResult)); Changed(nameof(HasCurrentOperatorQueryResult)); Changed(nameof(NeedsInitializeFormatQuery)); Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(CanSaveDeviceProfile)); RequestRealInitializeCommand.Refresh(); WorkerNotice = _realFormatError; if (OperatorTestMode != "INITIALIZE_ONLY") NavigateOperatorStep(OperatorStep.Result); }
        finally { _realQueryRunning = false; Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(CanSaveDeviceProfile)); RequestRealInitializeCommand.Refresh(); RequestDeviceScanCommand.Refresh(); RefreshV4Commands(); }
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
        _realInitializeRunning = true; RefreshV4Commands();
        RequestDeviceScanCommand.Refresh();
        Changed(nameof(RealInitializeDisabledReason)); RequestRealInitializeCommand.Refresh();
        try
        {
            _operatorResultEndpoint = OperatorSelectedDevice;
            _operatorResultMode = OperatorResultKind.InitializeOnly;
            _operatorResultProvenance = "REAL_WASAPI_INITIALIZE_ONLY";
            _operatorResultConfiguration = string.Format(Localization.CurrentCulture, Localization.Get("VmOperatorInitializeConfiguration"), candidate.Encoding, candidate.SampleRate, candidate.Channels, candidate.Period);
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
        finally { CancelRealInitialize(); _realInitializeRunning = false; Changed(nameof(RealInitializeDisabledReason)); Changed(nameof(RealFormatSummary)); RequestRealInitializeCommand.Refresh(); RequestDeviceScanCommand.Refresh(); RefreshV4Commands(); }
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
        InvalidateHistoricalRecovery(Localization.Get("VmHistoricalRecoveryChecking"));
        var version = _historicalRecoveryVersion;
        _historicalRecoveryRunning = true; Changed(nameof(HistoricalRecoveryRunning)); RefreshHistoricalRecoveryCommands();
        try
        {
            var preview = OfflineSustainedProfileRecovery.Preview(selected.SessionId, RecoveryScansDirectory, RecoveryProfilesDirectory, RecoveryEvidenceDirectory);
            if (version != _historicalRecoveryVersion) return Task.CompletedTask;
            _historicalRecoveryPreview = preview;
            _historicalRecoveryStatus = preview.Recommendation.Preferred is null ? Localization.Get("VmHistoricalRecoveryConfigurationMissing") : Localization.Get("VmHistoricalRecoveryPreviewReady");
            ChangedHistoricalRecovery();
        }
        catch (Exception ex) { if (version == _historicalRecoveryVersion) { _historicalRecoveryStatus = Localization.Get("VmHistoricalRecoveryRejected"); _historicalRecoveryTechnicalError = $"{ex.GetType().Name}: {ex.Message}"; Changed(nameof(HistoricalRecoveryStatus)); Changed(nameof(HistoricalRecoveryTechnicalDetails)); } }
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
            _historicalRecoveryStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmHistoricalRecoveryProfileCreated"), saved.Profile.ProfileId, saved.Profile.CreatedAtUtc.ToLocalTime());
            _historicalRecoveryPreview = null; _historicalRecoveryConfirmationChecked = false; _historicalRecoveryVersion++;
            ChangedHistoricalRecovery();
            RefreshSavedProfiles();
            RefreshHistoricalSustainedScans();
        }
        catch (Exception ex) { _historicalRecoveryStatus = Localization.Get("VmHistoricalRecoverySaveFailed"); _historicalRecoveryTechnicalError = $"{ex.GetType().Name}: {ex.Message}"; _historicalRecoveryPreview = null; _historicalRecoveryConfirmationChecked = false; _historicalRecoveryVersion++; ChangedHistoricalRecovery(); }
        finally { _historicalRecoverySaving = false; Changed(nameof(HistoricalRecoverySaving)); RefreshHistoricalRecoveryCommands(); }
        return Task.CompletedTask;
    }

    private void CancelHistoricalSustainedRecovery() => InvalidateHistoricalRecovery(Localization.Get("VmHistoricalRecoveryCancelled"));
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
        _bindingCheckRunning = true; RefreshV4Commands();
        Changed(nameof(SavedProfileBindingChecking)); CheckSavedProfileBindingCommand.Refresh(); DiscoverWindowsEndpointsCommand.Refresh();
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
            var v4Matches = validProfiles.Where(p => p.Profile is { } profile && (profile switch
            {
                { SchemaVersion: 4, SustainedSelection: not null } => true,
                { SchemaVersion: 5, StableShortSelection.ProfileKind: "DEVICE" } => true,
                _ => false
            }) && DeviceProfileBindingRevalidation.Evaluate(profile, discovery, generation, p.Path).Overall == DeviceProfileBindingAssessment.ExactBindingsPresentInDiscovery).ToArray();
            var legacyMatches = validProfiles.Where(p => p.Profile!.SchemaVersion is >= 1 and <= 3 && DeviceProfileBindingRevalidation.Evaluate(p.Profile, discovery, generation, p.Path).Overall == DeviceProfileBindingAssessment.ExactBindingsPresentInDiscovery).ToArray();
            _matchingSavedProfiles = v4Matches;
            Changed(nameof(SavedProfileMatchingSummary));
            if (v4Matches.Length == 1)
            {
                SelectedSavedDeviceProfile = v4Matches[0];
                _savedProfileBindingResult = _savedProfileBindingState.Evaluate(v4Matches[0].Profile!, discovery, generation, v4Matches[0].Path);
                _savedProfileOperatorStatus = Localization.Get("VmBindingExactFound");
                _savedProfileOperatorHistory = HistoricalOperatorText(v4Matches[0].Profile!);
                _savedProfileOperatorLimitation = Localization.Get("VmBindingExactIdsOnlyLimitation");
            }
            else if (v4Matches.Length > 1)
            {
                SelectedSavedDeviceProfile = null;
                _savedProfileBindingResult = _savedProfileBindingState.Invalidate(DeviceProfileBindingInvalidationReason.ProfileSelectionChanged);
                _savedProfileOperatorStatus = Localization.Get("VmBindingMultipleProfilesFound");
                _savedProfileOperatorHistory = string.Join("\n", v4Matches.Select(p => $"{p.OperatorDisplayName} · {p.OperatorTimestampLabel} · {p.OperatorResult}"));
                _savedProfileOperatorLimitation = Localization.Get("VmBindingMultipleProfilesChooseManually");
            }
            else if (legacyMatches.Length > 0)
            {
                SelectedSavedDeviceProfile = null;
                _savedProfileOperatorStatus = Localization.Get("VmBindingLegacyMeasurementsFound");
                _savedProfileOperatorHistory = Localization.Get("VmBindingShortResultsNotSustained");
                _savedProfileOperatorLimitation = Localization.Get("VmBindingSustainedValidationRequired");
            }
            else if (_savedDeviceProfiles.Any(p => !p.IsAvailable))
            {
                SelectedSavedDeviceProfile = null;
                _savedProfileOperatorStatus = Localization.Get("VmBindingProfileCorruptOrUnsupported");
                _savedProfileOperatorHistory = Localization.Get("VmBindingMatchingProfileNotFound");
                _savedProfileOperatorLimitation = Localization.Get("VmBindingRefreshProfiles");
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
            _savedProfileOperatorStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmBindingCheckFailed"), ex.GetType().Name);
            _savedProfileOperatorHistory = Localization.Get("VmBindingCurrentResultUnconfirmed");
            _savedProfileOperatorLimitation = Localization.Get("VmBindingRetryAfterRefresh");
        }
        finally
        {
            _bindingCheckRunning = false; RefreshV4Commands();
            Changed(nameof(SavedProfileBindingChecking)); Changed(nameof(SavedProfileBindingStatus)); Changed(nameof(SavedProfileBindingExplanation)); Changed(nameof(SavedProfileBindingTechnicalDetails)); Changed(nameof(HasSavedProfileBindingAssessment)); Changed(nameof(SavedProfileOperatorStatus)); Changed(nameof(SavedProfileOperatorHistory)); Changed(nameof(SavedProfileOperatorLimitation));
            CheckSavedProfileBindingCommand.Refresh(); DiscoverWindowsEndpointsCommand.Refresh();
        }
    }

    private static string HistoricalOperatorText(DeviceConfigurationProfile profile)
    {
        if (profile.SchemaVersion == 5 && profile.StableShortSelection is { ProfileKind: "DEVICE" } shortRun)
            return string.Format(Localization.CurrentCulture, Localization.Get("VmV5SavedProfileSummary"), shortRun.CaptureFormat.Period, shortRun.CaptureFormat.Encoding,
                shortRun.CaptureFormat.SampleRate, shortRun.CaptureFormat.Channels, shortRun.RepeatabilityCompleted, shortRun.RepeatabilityRequested);
        if (profile.SchemaVersion != 4 || profile.SustainedSelection is not { } sustained) return Localization.Get("VmHistoricalOperatorSectionMissing");
        var primary = sustained.Preferred is { } p ? string.Format(Localization.CurrentCulture, Localization.Get("VmHistoricalOperatorPrimary"), p.Period, p.Mmcss, p.DurationSeconds, p.Confidence) : Localization.Get("VmHistoricalOperatorPrimaryNone");
        var fallback = sustained.Fallback is { } f ? string.Format(Localization.CurrentCulture, Localization.Get("VmHistoricalOperatorFallback"), f.Period, f.DurationSeconds) : Localization.Get("VmHistoricalOperatorFallbackNone");
        return string.Format(Localization.CurrentCulture, Localization.Get("VmHistoricalOperatorSummary"), primary, fallback, Localization.Get("VmSavedProfileEvidenceCaveat"));
    }

    private async Task DiscoverWindowsEndpointsAsync()
    {
        if (!CanDiscoverWindowsEndpoints() || _worker is null) return;
        var generation = Interlocked.Increment(ref _windowsDiscoveryRequestGeneration);
        var operation = Guid.NewGuid();
        using var traceOperation = EndpointDiscoveryQaTrace.BeginOperation(operation);
        var discoveryTimer = System.Diagnostics.Stopwatch.StartNew();
        void Trace(string stage) => System.Diagnostics.Trace.TraceInformation("EndpointDiscovery stage={0} elapsedMs={1} workerState={2} loading={3}", stage, discoveryTimer.ElapsedMilliseconds, WorkerState.State, _windowsDiscoveryLoading);
        EndpointDiscoveryQaTrace.Write("gui-click-accepted", state: WorkerState.State.ToString(), loading: _windowsDiscoveryLoading);
        Trace("begin");
        InvalidateSavedProfileBinding(DeviceProfileBindingInvalidationReason.EndpointRediscovered);
        var selection = new AutomaticScanSelection(SelectedWindowsEndpoint?.EndpointId, AutomaticScanPairEnabled,
            AutomaticScanPairEnabled ? SelectedDuplexCaptureEndpoint?.EndpointId : null,
            AutomaticScanPairEnabled ? SelectedDuplexRenderEndpoint?.EndpointId : null,
            SelectedPhysicalDeviceGroup?.GroupId, SelectedPhysicalDeviceGroup?.CanAutoSelectPair == true, AllowUnconfirmedPhysicalPair, AutomaticSustainedMode.StartsWith("Extended", StringComparison.Ordinal));
        if (RealFormatMatrix is not null) _realQueryInvalidated = true;
        CancelDuplexRepeatabilityAuthorization(Localization.Get("VmRepeatabilityDiscoveryUpdating"));
        CancelPeriodDiscovery(Localization.Get("VmPeriodPlanDiscardedDiscoveryUpdating"));
        InvalidateV4PreparedPlan("VmV4PlanInvalidated");
        _windowsDiscoveryLoading = true;_windowsDiscoveryCurrent = false;_windowsDiscoveryError = null; RefreshV4Commands();
        EndpointDiscoveryQaTrace.Write("loading-enter", state: WorkerState.State.ToString(), loading: true);
        Changed(nameof(SavedProfileBindingChecking)); CheckSavedProfileBindingCommand.Refresh();
        _physicalDeviceGroups = [];
        Changed(nameof(PhysicalDeviceGroups)); Changed(nameof(PhysicalDeviceGroupDetails)); Changed(nameof(PhysicalDeviceGroupStatusText));
        Changed(nameof(DuplexWorkerAvailability)); Changed(nameof(DuplexDiscoveryFreshness)); Changed(nameof(DuplexEvidenceFreshness));
        Changed(nameof(WindowsDiscoveryLoading));Changed(nameof(WindowsDiscoveryStatusText));Changed(nameof(WindowsEndpoints));Changed(nameof(DuplexCaptureEndpoints));Changed(nameof(DuplexRenderEndpoints));Changed(nameof(HasCurrentWindowsEndpoints));Changed(nameof(HasHistoricalWindowsEndpoints));Changed(nameof(DeviceScanAvailabilityReason));Changed(nameof(RealFormatMatrixIsStale));Changed(nameof(RealFormatMatrixStatus));Changed(nameof(HasCurrentOperatorQueryResult));Changed(nameof(NeedsInitializeFormatQuery));Changed(nameof(CanSaveDeviceProfile));RequestRealInitializeCommand.Refresh();RequestDeviceScanCommand.Refresh();
        RefreshWindowsEndpointBrowser();
        DiscoverWindowsEndpointsCommand.Refresh();
        try
        {
            if (WorkerState.State == WorkerConnectionState.Disconnected)
            {
                Trace("connect-start");
                EndpointDiscoveryQaTrace.Write("viewmodel-connect-start", state: WorkerState.State.ToString(), loading: true);
                _workerAttempted = true;
                await _worker.ConnectAsync();
                ApplyWorkerState(_worker.State);
                Trace("connect-complete");
                EndpointDiscoveryQaTrace.Write("viewmodel-connect-complete", state: WorkerState.State.ToString(), exitCode: WorkerState.ExitCode, loading: true);
                if (WorkerState.State != WorkerConnectionState.ConnectedFake) throw new IOException($"{Localization.Get("VmDiscoveryWorkerDisconnected")}: {WorkerState.Message}");
            }
            Trace("request-start");
            EndpointDiscoveryQaTrace.Write("discovery-request-start", state: WorkerState.State.ToString(), exitCode: WorkerState.ExitCode, loading: true);
            using var discoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var discovery = _automaticScanWorkflow is not null
                ? await _automaticScanWorkflow.RefreshEndpointsAsync(discoveryTimeout.Token)
                : await _worker.ListWindowsEndpointsAsync(discoveryTimeout.Token);
            Trace("response-received");
            WindowsEndpointDiscoveryValidation.Validate(discovery);
            if (generation != Volatile.Read(ref _windowsDiscoveryRequestGeneration)) { Trace("late-result-discarded"); return; }
            ApplyAutomaticDiscovery(discovery, selection);
            EndpointDiscoveryQaTrace.Write("ui-update-complete", state: WorkerState.State.ToString(), exitCode: WorkerState.ExitCode, loading: true);
            WorkerNotice=string.Format(Localization.CurrentCulture, Localization.Get("VmDiscoverySuccess"), discovery.Endpoints.Count);
            _savedProfileOperatorStatus = Localization.Get("VmBindingDiscoveryCompleted");
            _savedProfileOperatorHistory = Localization.Get("VmBindingNotConfirmed");
            _savedProfileOperatorLimitation = Localization.Get("VmBindingCheckAfterDiscovery");
            Trace("success");
        }
        catch(Exception ex)
        {
            Trace($"failure-{ex.GetType().Name}");
            EndpointDiscoveryQaTrace.Write(ex is OperationCanceledException ? "discovery-cancelled" : "discovery-failure", state: WorkerState.State.ToString(), exitCode: WorkerState.ExitCode, exceptionType: ex.GetType().Name, errorCode: ex is WorkerCommandException commandFailure ? commandFailure.ErrorCode : ex is System.ComponentModel.Win32Exception win32Failure ? win32Failure.NativeErrorCode.ToString() : null, loading: _windowsDiscoveryLoading);
            if (generation == Volatile.Read(ref _windowsDiscoveryRequestGeneration))
            {
                InvalidateAutomaticDeviceSelection();
                _windowsDiscoveryError=ex is WorkerCommandException command ? $"{command.Command}/{command.ErrorCode}" : ex.GetType().Name;
                WorkerNotice=string.Format(Localization.CurrentCulture, Localization.Get("VmDiscoveryFailure"), _windowsDiscoveryError);
                _savedProfileOperatorStatus = string.Format(Localization.CurrentCulture, Localization.Get("VmEndpointRefreshFailure"), _windowsDiscoveryError);
                _savedProfileOperatorHistory = Localization.Get("VmBindingCurrentResultUnconfirmed");
                _savedProfileOperatorLimitation = Localization.Get("VmBindingRetryAfterRefresh");
            }
        }
        finally
        {
            Trace("cleanup-start");
            _windowsDiscoveryLoading=false;
            EndpointDiscoveryQaTrace.Write("loading-exit", state: WorkerState.State.ToString(), exitCode: WorkerState.ExitCode, loading: false);
            Changed(nameof(WindowsDiscoveryLoading));Changed(nameof(WindowsDiscoveryStatusText));Changed(nameof(WindowsEndpoints));Changed(nameof(DuplexCaptureEndpoints));Changed(nameof(DuplexRenderEndpoints));Changed(nameof(LastWindowsEndpoints));Changed(nameof(HasCurrentWindowsEndpoints));Changed(nameof(HasHistoricalWindowsEndpoints));
            Changed(nameof(SavedProfileBindingChecking)); Changed(nameof(SavedProfileOperatorStatus)); Changed(nameof(SavedProfileOperatorHistory)); Changed(nameof(SavedProfileOperatorLimitation));
            Changed(nameof(CanUseSelectedWindowsEndpoint));Changed(nameof(DeviceScanAvailabilityReason));Changed(nameof(AutomaticScanAvailabilityReason));Changed(nameof(RealFormatMatrixIsStale));Changed(nameof(RealFormatMatrixStatus));Changed(nameof(HasCurrentOperatorQueryResult));Changed(nameof(NeedsInitializeFormatQuery));Changed(nameof(RealInitializeDisabledReason));Changed(nameof(CanSaveDeviceProfile));Changed(nameof(RealFormatSummary));Changed(nameof(RealFormatEndpointLabel));Changed(nameof(RealInitializationDetails));Changed(nameof(RealInitializationSummary));Changed(nameof(DuplexWorkerAvailability));Changed(nameof(DuplexDiscoveryFreshness));Changed(nameof(DuplexEvidenceFreshness));QueryRealFormatsCommand.Refresh();RequestRealInitializeCommand.Refresh();RequestDeviceScanCommand.Refresh();PrepareAutomaticScanCommand.Refresh();
            RequestDuplexMinimumCommand.Refresh(); RequestDuplexDefaultCommand.Refresh(); Changed(nameof(HasCurrentDuplexPair)); Changed(nameof(CanSaveDeviceProfile));
            RefreshWindowsEndpointBrowser();
            DiscoverWindowsEndpointsCommand.Refresh();
            RefreshCanExecute();
            Trace("cleanup-complete");
            EndpointDiscoveryQaTrace.Write("gui-cleanup-complete", state: WorkerState.State.ToString(), exitCode: WorkerState.ExitCode, loading: _windowsDiscoveryLoading);
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
        var encoding = format switch { "PCM16-48000" => "PCM16", "PCM24-48000" => "PCM24_IN_32", "FLOAT32-48000" => "FLOAT32", _ => throw new InvalidOperationException(Localization.Get("VmWorkerUnsupportedFakeFormat")) };
        _workerPendingSettings = new(SelectedWorkerEndpoint.EndpointId, direction, format, 48000, encoding, SelectedWorkerFormat.Topology.ChannelCount, format, WorkerDurationSeconds, WorkerPeriod, WorkerMmcss, direction == "RENDER" ? WorkerRenderChannel : null, WorkerScenario);
        Changed(nameof(WorkerConsentText));
        WorkerConsentChecked = false;
        WorkerConsentPending = true;
        WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerConfirmationRequested"), SelectedWorkerEndpoint.Name, format, WorkerChannelCount);
        AddWorkerTimeline(Guid.Empty, "SessionRequested", "REQUESTED", "UNKNOWN", "NOT_ATTEMPTED", Localization.Get("VmWorkerGuiSessionRequestDetail"));
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
        AddWorkerTimeline(Guid.Empty, "ConsentAccepted", "CONSENTED", "UNKNOWN", "NOT_ATTEMPTED", Localization.Get("VmWorkerConsentFingerprintBound"));
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
        AddWorkerTimeline(Guid.Empty, "ConsentCancelled", "CANCELLED", "UNKNOWN", "NOT_ATTEMPTED", Localization.Get("VmWorkerConsentCancelledDetail"));
        WorkerNotice = Localization.Get("VmWorkerConsentCancelled"); Changed(nameof(WorkerPendingSettings)); RefreshWorkerCommands();
    }

    private async Task StopWorkerSessionAsync()
    {
        if (_worker is null || WorkerSession is not { Lifecycle: SessionLifecycle.Preparing or SessionLifecycle.Ready or SessionLifecycle.Started } session) return;
        await _worker.StopSessionAsync(session.Id);
        if (WorkerSession?.Lifecycle is SessionLifecycle.Preparing or SessionLifecycle.Ready or SessionLifecycle.Started) WorkerSession = WorkerSession with { Lifecycle = SessionLifecycle.Stopping, Message = Localization.Get("VmWorkerStopAcceptedAwaitingTerminal") };
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
            if (V4ScanRunning)
            {
                CancelV4Scan();
                if (_v4ScanTerminal is { } v4Terminal)
                    try { await v4Terminal.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
                    catch { _v4ProgressText = "UNKNOWN · SESSION_CLEANUP_UNCONFIRMED"; Changed(nameof(V4ProgressText)); }
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
                    WorkerSession = active with { Lifecycle = SessionLifecycle.Unknown, Quality = StreamQuality.Unknown, CleanupStatus = null, Message = Localization.Get("VmWorkerWindowClosingCleanupUnknown") };
                    AddWorkerTimeline(active.Id, "WorkerDisconnected", "UNKNOWN", "UNKNOWN", "UNKNOWN", Localization.Get("VmWorkerWindowCloseStopUnconfirmed"));
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
        if (_uiContext is null) { try { ApplyWorkerState(state, true); } catch (Exception ex) { _workerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerStateDispatchFailed"), ex.Message); } }
        else _uiContext.Post(_ => { try { ApplyWorkerState(state, true); } catch (Exception ex) { _workerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerStateDispatchFailed"), ex.Message); } }, null);
    }

    private void WorkerSessionAccepted(WorkerSessionAcceptedDto accepted)
    {
        Interlocked.Exchange(ref _lateWorkerStartAccepted, 1);
        if (_uiContext is null) { try { ApplyWorkerSessionAccepted(accepted); } catch (Exception ex) { _workerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerSessionDispatchFailed"), ex.Message); } }
        else _uiContext.Post(_ => { try { ApplyWorkerSessionAccepted(accepted); } catch (Exception ex) { _workerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerSessionDispatchFailed"), ex.Message); } }, null);
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
            SessionLifecycle.Preparing, StreamQuality.Unknown, Localization.Get("VmWorkerSyntheticOperationAccepted"), "NOT_ATTEMPTED", true, settings.ChannelCount);
        WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerSettingsAccepted"), accepted.SessionId);
        AddWorkerTimeline(accepted.SessionId, "Accepted", "PREPARING", "UNKNOWN", "NOT_ATTEMPTED", string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerStartResponseCorrelated"), accepted.OperationState), null, accepted.CorrelationId);
        var early = _earlyWorkerEvents.Where(e => e.SessionId == accepted.SessionId).ToArray();
        _earlyWorkerEvents.RemoveAll(e => e.SessionId == accepted.SessionId);
        foreach (var evt in early) ApplyWorkerEvent(evt);
    }

    private void ApplyWorkerState(WorkerStateDto state, bool callbackDelivery = false)
    {
        if (state.State == WorkerConnectionState.ConnectedFake && _windowsDiscoveryWorkerGeneration is { } discoveryGeneration && _worker?.DiscoveryGeneration != discoveryGeneration)
        {
            _windowsDiscoveryCurrent = false;
            Changed(nameof(HasCurrentWindowsEndpoints)); Changed(nameof(WindowsDiscoveryStatusText)); Changed(nameof(HasCurrentDuplexPair)); Changed(nameof(DuplexDiscoveryFreshness));
        }
        if (state.State != WorkerConnectionState.ConnectedFake || _worker?.DiscoveryGeneration != _v4WorkerDiscoveryGeneration)
            InvalidateV4PreparedPlan("VmV4PlanInvalidated");
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
        Changed(nameof(WorkerStateDisplayMessage));
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
            if (_duplexValidationRunning) { _duplexStatus = Localization.Get("VmDuplexDisconnectedDuringValidation"); Changed(nameof(DuplexStatus)); }
            else if (HasDuplexResults) { _duplexStatus = Localization.Get("VmDuplexHistoricalAfterDisconnect"); Changed(nameof(DuplexStatus)); }
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
            WorkerSession = active with { Lifecycle = SessionLifecycle.Unknown, Quality = StreamQuality.Unknown, CleanupStatus = null, Message = Localization.Get("VmWorkerDisconnectedCleanupUnknown") };
            _workerTerminal?.TrySetException(new IOException(Localization.Get("VmWorkerDisconnectedBeforeTerminalEvent")));
            AddWorkerTimeline(active.Id, "WorkerDisconnected", "UNKNOWN", "UNKNOWN", "UNKNOWN", state.Message);
        }
        else if (state.State == WorkerConnectionState.Disconnected && _workerStartOutcomeUnknown)
            AddWorkerTimeline(Guid.Empty, "WorkerDisconnected", "UNKNOWN", "UNKNOWN", "UNKNOWN", Localization.Get("VmWorkerStartOutcomeUnknownDetail"));
        RefreshCanExecute();
        RefreshContext();
    }

    private void WorkerEventReceived(WorkerEventDto evt)
    {
        if (_uiContext is null) { try { ReceiveWorkerEvent(evt); } catch (Exception ex) { _workerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerEventCallbackFailed"), ex.Message); } }
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
                WorkerSession = current with { Lifecycle = SessionLifecycle.Unknown, Quality = StreamQuality.Unknown, CleanupStatus = null, Message = Localization.Get("VmWorkerUiQueueOverflow") };
            WorkerNotice = Localization.Get("VmWorkerEventQueueOverflow");
            AddWorkerTimeline(WorkerSession?.Id ?? Guid.Empty, "UI_EVENT_OVERFLOW", "UNKNOWN", "UNKNOWN", "UNKNOWN", Localization.Get("VmWorkerUiQueueOverflowDetails"));
            return;
        }
        if (batch.CoalescedSnapshots > 0)
            AddWorkerTimeline(WorkerSession?.Id ?? Guid.Empty, "MeterSnapshotsCoalesced", WorkerSession?.Lifecycle.ToString().ToUpperInvariant() ?? "UNKNOWN", "UNKNOWN", "UNKNOWN", string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerSnapshotsCoalescedDetails"), batch.CoalescedSnapshots));
        foreach (var evt in batch.Events)
        {
            try { ReceiveWorkerEvent(evt); }
            catch (Exception ex) { WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerEventCallbackFailed"), $"{ex.GetType().Name}: {ex.Message}"); }
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
            else throw new InvalidDataException(Localization.Get("VmWorkerMeterSnapshotDtoInvalid"));
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
        var detail = payload.TryGetProperty("failureCode", out var failure) ? failure.GetString() ?? evt.Name : payload.TryGetProperty("warningCode", out var warning) ? warning.GetString() ?? evt.Name : string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerSyntheticEventDetail"), evt.Name);
        WorkerSession = WorkerSession! with { Lifecycle = lifecycle, Quality = quality, CleanupStatus = cleanup, Message = detail };
        if (evt.Name is "Completed" or "Failed") { _workerMeterFinal = _workerCaptureMeter is not null || _workerRenderMeter is not null; Changed(nameof(WorkerMeterStatusText)); }
        AddWorkerTimeline(evt.SessionId, evt.Name, lifecycleText, qualityText, cleanup, detail, evt.EventSequence, evt.CorrelationId);
        if (evt.Name is "Completed" or "Failed") _workerTerminal?.TrySetResult();
        WorkerNotice = string.Format(Localization.CurrentCulture, Localization.Get("VmWorkerEventNotice"), evt.Name, evt.EventSequence);
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
            WorkerSession = active with { Lifecycle = SessionLifecycle.Unknown, Quality = StreamQuality.Unknown, CleanupStatus = null, Message = Localization.Get("VmWorkerPipeDisconnectedCleanupUnknown") };
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
                    ? [Localization.Get("VmInspectorEndpointsNotRequested"), Localization.Get("VmInspectorConnectWorkerForDiscovery"), Localization.Get("VmInspectorSharedMetadataNotTested")]
                    : SelectedEndpoint is { } e
                    ? [$"{e.Name}", $"{Localization.Get("VmInspectorDirectionLabel")}: {Localization.Get(Direction == SessionMode.Capture ? "VmCaptureInputDirection" : "VmRenderOutputDirection")}", $"{Localization.Get("VmInspectorStateLabel")}: {e.State}", $"{Localization.Get("VmInspectorMixerFormatLabel")}: {e.Format}", $"{Localization.Get("VmInspectorWindowsChannelsLabel")}: {e.Channels}", $"{Localization.Get("VmInspectorPhysicalConnectorsLabel")}: {Localization.Get("VmInspectorUnknownUpper")}"]
                    : [EmptyEndpointMessage, Localization.Get("VmInspectorPassiveDemoList")];
                break;
            case ProbeScreen.Formats:
                InspectorTitle = Localization.Get("VmInspectorFormatTitle");
                InspectorDetails = SelectedFormat is { } f
                    ? [$"{f.Encoding} · {f.SampleRate} {Localization.Get("VmInspectorHertz")} · {f.ChannelCount} {Localization.Get("VmInspectorChannelAbbreviation")}", .. PeriodDetails(Localization.Get("VmPeriodDefaultLabel"), f.DefaultPeriod), .. PeriodDetails(Localization.Get("VmPeriodMinimumLabel"), f.MinimumPeriod)]
                    : [Localization.Get("VmInspectorFormatNotSelected")];
                break;
            case ProbeScreen.Capture:
                InspectorTitle = Localization.Get("VmInspectorCaptureTitle");
                InspectorDetails = Session is { Mode: SessionMode.Capture } s
                    ? [$"{Localization.Get("VmInspectorDeviceLabel")}: {s.DisplayEndpointName}", $"{Localization.Get("VmInspectorStateLabel")}: {s.Lifecycle}", $"{Localization.Get("VmInspectorStreamQualityLabel")}: {s.Quality}", $"{Localization.Get("VmInspectorDurationLabel")}: {s.DurationSeconds} {Localization.Get("VmInspectorSecondsAbbreviation")}", $"{Localization.Get("VmInspectorMeterLabel")}: {MeterStatusText}", $"{Localization.Get("VmInspectorCleanupLabel")}: {s.CleanupStatus ?? Localization.Get("VmInspectorCleanupUnknownNoData")}"]
                    : [Localization.Get("VmInspectorCaptureNotStarted"), $"{Localization.Get("VmInspectorDeviceLabel")}: {SelectedCaptureEndpoint?.DisplayName ?? Localization.Get("VmInspectorNotSelected")}", $"{Localization.Get("VmInspectorMeterLabel")}: {MeterStatusText}"];
                break;
            case ProbeScreen.Render:
                InspectorTitle = Localization.Get("VmInspectorRenderTitle");
                InspectorDetails = Session is { Mode: SessionMode.Render } r
                    ? [$"{Localization.Get("VmInspectorDeviceLabel")}: {r.DisplayEndpointName}", $"{Localization.Get("VmInspectorStateLabel")}: {r.Lifecycle}", $"{Localization.Get("VmInspectorStreamQualityLabel")}: {r.Quality}", $"{Localization.Get("VmInspectorWindowsChannelLabel")}: {r.RenderChannel}", $"{Localization.Get("VmInspectorDurationLabel")}: {r.DurationSeconds} {Localization.Get("VmInspectorSecondsAbbreviation")}", $"{Localization.Get("VmInspectorPhysicalMatchLabel")}: {Localization.Get("VmInspectorUnknownUpper")}", $"{Localization.Get("VmInspectorCleanupLabel")}: {r.CleanupStatus ?? Localization.Get("VmInspectorCleanupUnknownNoData")}"]
                    : [$"{Localization.Get("VmInspectorDeviceLabel")}: {SelectedRenderEndpoint?.DisplayName ?? Localization.Get("VmInspectorNotSelected")}", $"{Localization.Get("VmInspectorWindowsChannelLabel")}: {RenderChannel}", $"{Localization.Get("VmInspectorDurationLabel")}: {DurationSeconds} {Localization.Get("VmInspectorSecondsAbbreviation")}", $"{Localization.Get("VmInspectorPhysicalMatchLabel")}: {Localization.Get("VmInspectorUnknownUpper")}", Localization.Get("VmInspectorRenderNotStarted")];
                break;
            case ProbeScreen.Reports:
                InspectorTitle = Localization.Get("VmInspectorReportsTitle");
                InspectorDetails = SelectedReport is { } report
                    ? [$"{report.Name}", $"{Localization.Get("VmInspectorModeLabel")}: {Localization.Get(report.Mode == SessionMode.Capture ? "VmCaptureInputDirection" : "VmRenderOutputDirection")}", $"{Localization.Get("VmInspectorStateLabel")}: {report.Lifecycle}", $"{Localization.Get("VmInspectorStreamQualityLabel")}: {report.Quality}", report.Evidence, Localization.Get("VmInspectorDemoSimulation")]
                    : [Localization.Get("VmInspectorReportNotSelected")];
                break;
            case ProbeScreen.Diagnostics:
                InspectorTitle = Localization.Get("VmInspectorWorkerTitle");
                InspectorDetails = [
                    $"{Localization.Get("VmInspectorConnectionLabel")}: {WorkerState.State}",
                    WorkerSession is { } ws ? $"{Localization.Get("VmInspectorSessionLabel")}: {ws.Id:D} · {ws.Lifecycle}" : Localization.Get("VmInspectorWorkerSessionNotStarted"),
                    $"{Localization.Get("VmInspectorStreamQualityLabel")}: {WorkerSession?.Quality.ToString() ?? "UNKNOWN"}",
                    $"{Localization.Get("VmInspectorCleanupLabel")}: {WorkerSession?.CleanupStatus ?? "UNKNOWN"}",
                    WorkerTimeline.LastOrDefault() is { } we ? $"{Localization.Get("VmInspectorEventLabel")}: {we.Name} · {Localization.Get("VmInspectorIpcSequence")}: {we.ProtocolSequence?.ToString() ?? Localization.Get("VmInspectorSequenceLocal")}" : Localization.Get("VmInspectorWorkerEventsMissing"),
                    $"{Localization.Get("VmInspectorPhysicalMatchLabel")}: UNKNOWN · {Localization.Get("VmInspectorWorkerDataSynthetic")}"
                ];
                break;
            default:
                InspectorTitle = Localization.Get("VmInspectorDemoDiagnosticsTitle");
                InspectorDetails = [string.Format(Localization.CurrentCulture, Localization.Get("VmInspectorSessionValue"), Session?.Id.ToString() ?? Localization.Get("VmInspectorNotStarted")), string.Format(Localization.CurrentCulture, Localization.Get("VmInspectorStateValue"), Session?.Lifecycle.ToString() ?? Localization.Get("VmInspectorNotStarted")), string.Format(Localization.CurrentCulture, Localization.Get("VmInspectorQualityValue"), Session?.Quality.ToString() ?? Localization.Get("VmInspectorNotAssessed")), LastEvent is { } ev ? string.Format(Localization.CurrentCulture, Localization.Get("VmInspectorLastEvent"), ev.Sequence, ev.Kind, ev.Timestamp) : Localization.Get("VmInspectorNoEventsYet"), LastEvent?.Message ?? Localization.Get("VmInspectorNoEvents"), Localization.Get("VmInspectorDemoSimulation")];
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
            sessionFields.Add(new(Localization.Get("VmInspectorModeLabel"), Localization.Get(session.Mode == SessionMode.Capture ? "VmCaptureLabel" : "VmRenderLabel"), session.Mode.ToString()));
            sessionFields.Add(new(Localization.Get("VmInspectorEndpointLabel"), settings?.EndpointId ?? session.EndpointName));
            sessionFields.Add(new(Localization.Get("VmInspectorFormatLabel"), settings is null ? Localization.Get("VmInspectorUnknown") : string.Format(Localization.CurrentCulture, Localization.Get("VmInspectorWorkerFormatValue"), settings.Encoding, settings.SampleRate, Localization.Get("VmInspectorHertz"), settings.ChannelCount, Localization.Get("VmInspectorChannelAbbreviation")), settings?.FormatId));
            if (session.Mode == SessionMode.Render) sessionFields.Add(new(Localization.Get("VmInspectorWindowsChannelLabel"), settings?.RenderChannel?.ToString() ?? session.RenderChannel?.ToString() ?? Localization.Get("VmInspectorUnknown")));
            sessionFields.Add(new(Localization.Get("VmInspectorDurationLabel"), string.Format(Localization.CurrentCulture, Localization.Get("VmInspectorDurationValue"), settings?.DurationSeconds ?? session.DurationSeconds, Localization.Get("VmInspectorSecondsAbbreviation"))));
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

    private static string DirectionDisplayLabel(string direction) => direction switch { "CAPTURE" => Localization.Get("VmCaptureLabel"), "RENDER" => Localization.Get("VmRenderLabel"), _ => direction };

    public static string ModeLabel(SessionMode mode) => Localization.Get(mode == SessionMode.Capture ? "VmCaptureInputDirection" : "VmRenderOutputDirection");

    private sealed record PendingSession(Guid Id, SessionMode Mode, EndpointDto Endpoint, int? RenderChannel, int DurationSeconds, int ChannelCount);
}
