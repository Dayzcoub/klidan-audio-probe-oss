using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using KlidanAudioProbe.Gui.Core;
using Microsoft.Win32.SafeHandles;

namespace KlidanAudioProbe.Gui.Worker;

public sealed class NativeWorkerClient(string? workerPath = null) : IWorkerClient
{
    private const string Product = "KLIDAN_AUDIO_PROBE_WORKER";
    private const int ProtocolMajor = 1, ProtocolMinor = 0, SchemaVersion = 1;
    private const int MaximumDuplexDurationSeconds = 10;
    private const int MaximumSustainedDuplexDurationSeconds = 600;
    private const int DuplexChildDeadlineSlackSeconds = 25;
    private const int DuplexGracefulShutdownSeconds = 3, DuplexForcedShutdownWaitSeconds = 3;
    private const int DuplexSetupAndReportSeconds = 10;
    internal const int SessionCloseTimeoutSeconds = 8;
    private static readonly TimeSpan DuplexResponseTimeout = TimeSpan.FromSeconds(MaximumDuplexDurationSeconds + DuplexChildDeadlineSlackSeconds + DuplexGracefulShutdownSeconds + DuplexForcedShutdownWaitSeconds + DuplexSetupAndReportSeconds);
    private static readonly TimeSpan SustainedDuplexResponseTimeout = TimeSpan.FromSeconds(MaximumSustainedDuplexDurationSeconds + DuplexChildDeadlineSlackSeconds + DuplexGracefulShutdownSeconds + DuplexForcedShutdownWaitSeconds + DuplexSetupAndReportSeconds);
    private const uint PipeOpenDuplex = 3, FirstPipeInstance = 0x00080000, RejectRemoteClients = 8;
    private readonly string _workerPath = Path.GetFullPath(workerPath ?? Path.Combine(AppContext.BaseDirectory, "KlidanProbeWorker.exe"));
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<Guid, SessionCursor> _sessions = new();
    private readonly object _pendingSync = new();
    private readonly object _terminalSync = new();
    private readonly Queue<Guid> _terminalSessionOrder = new();
    private readonly HashSet<Guid> _terminalSessions = [];
    private const int TerminalSessionHistoryLimit = 64;
    private readonly CancellationTokenSource _lifetime = new();
    private SafeJobHandle? _job;
    private SafeProcessHandle? _process;
    private NamedPipeServerStream? _pipe;
    private int? _pid;
    private WorkerStateDto _state = new(WorkerConnectionState.Disconnected, "Worker отключён.");
    private IReadOnlyList<string> _capabilities = [];
    private bool _everStarted, _disposed, _shutdownRequested;
    private int _transportEnded;
    private int _sharedOperation;
    private ulong _discoveryGeneration;
    public ulong DiscoveryGeneration => _discoveryGeneration;
    private long _lastEventSequence;
    private string? _pipeName;

    public WorkerStateDto State => _state;

    internal SharedOperationLease AcquireSharedOperation(string name)
    {
        if (Interlocked.CompareExchange(ref _sharedOperation, 1, 0) != 0)
            throw new InvalidOperationException($"Worker already has an unresolved {name} or other shared operation.");
        return new SharedOperationLease(() => Volatile.Write(ref _sharedOperation, 0));
    }

    internal sealed class SharedOperationLease(Action release) : IDisposable
    {
        private int _released;
        private bool _keep;
        public void Keep() => _keep = true;
        public void Dispose() { if (!_keep && Interlocked.Exchange(ref _released, 1) == 0) release(); }
    }
    public IReadOnlyList<string> Capabilities => _capabilities;
    internal string? PipeNameForTests => _pipeName;
    internal Action<Guid>? RequestWrittenForTest { get; set; }
    internal TimeSpan? ResponseTimeoutOverrideForTest { get; set; }
    internal static TimeSpan ResponseTimeoutForTest(string command) => GetResponseTimeout(command);
    internal SafePipeHandle? PipeHandleForTests => _pipe?.SafePipeHandle;
    internal uint PipeOpenModeForTests => PipeOpenDuplex | FirstPipeInstance;
    internal uint PipeModeForTests => RejectRemoteClients;
    public event Action<WorkerStateDto>? StateChanged;
    public event Action<WorkerSessionAcceptedDto>? SessionAccepted;
    public event Action<WorkerEventDto>? EventReceived;
    public event Action<WorkerTransportFaultDto>? TransportFault;

    private void SetState(WorkerConnectionState state, string message, int? exitCode = null)
    {
        _state = new(state, message, _pid, exitCode);
        EndpointDiscoveryQaTrace.Write("worker-state", state: state.ToString(), exitCode: exitCode);
        RaiseSafely(StateChanged, _state);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        EndpointDiscoveryQaTrace.Write("worker-connect-enter", state: _state.State.ToString(), exitCode: QueryExitCode(_process));
        if (_everStarted) throw new InvalidOperationException("Automatic worker restart is disabled; create a new application session to retry.");
        _everStarted = true;
        try
        {
            if (!File.Exists(_workerPath)) throw new FileNotFoundException("Bundled native worker was not found beside the GUI.", _workerPath);
            SetState(WorkerConnectionState.Starting, "Worker запускается.");
            _pipeName = $"KlidanAudioProbe.{Guid.NewGuid():N}";
            _pipe = CreateSecurePipe(_pipeName);
            _job = CreateKillOnCloseJob();
            var commandLine = new StringBuilder($"\"{_workerPath}\" --pipe \\\\.\\pipe\\{_pipeName}");
            var startup = new STARTUPINFO { cb = (uint)Marshal.SizeOf<STARTUPINFO>() };
            if (!CreateProcessW(_workerPath, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00000004 | 0x08000000, IntPtr.Zero, Path.GetDirectoryName(_workerPath), ref startup, out var processInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed.");
            _pid = unchecked((int)processInfo.dwProcessId);
            _process = new SafeProcessHandle(processInfo.hProcess, true);
            using var thread = new SafeKernelHandle(processInfo.hThread, true);
            if (!AssignProcessToJobObject(_job, _process)) { TerminateProcess(_process, 1); throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed before worker resume."); }
            if (ResumeThread(thread) == uint.MaxValue) { TerminateProcess(_process, 1); throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed."); }
            SetState(WorkerConnectionState.Connecting, "Подключение к локальному worker.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var clientPid) || clientPid != processInfo.dwProcessId)
                throw new UnauthorizedAccessException("Named Pipe client PID does not match the GUI-owned child.");
            if (!PathEquals(QueryImagePath(_process), _workerPath)) throw new UnauthorizedAccessException("Connected process image does not match the bundled worker.");

            SetState(WorkerConnectionState.Handshaking, "Согласование версии протокола.");
            var requestId = Guid.NewGuid();
            await IpcFrameCodec.WriteJsonAsync(_pipe, new { bootstrapVersion = 1, kind = "request", requestId, command = "Handshake", payload = new { protocol = new { major = ProtocolMajor, minMinor = ProtocolMinor, maxMinor = ProtocolMinor }, schemas = new { command = new[] { SchemaVersion }, @event = new[] { SchemaVersion }, report = new[] { SchemaVersion } } } }, timeout.Token).ConfigureAwait(false);
            using var handshake = IpcFrameCodec.Parse(await IpcFrameCodec.ReadPayloadAsync(_pipe, timeout.Token).ConfigureAwait(false));
            var root = handshake.RootElement;
            try
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var handshakeKind) || handshakeKind.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Handshake response envelope is missing.");
                if (handshakeKind.GetString() == "error")
                {
                    if (!root.TryGetProperty("correlationId", out var errorCorrelation) || errorCorrelation.ValueKind != JsonValueKind.String || !errorCorrelation.TryGetGuid(out var errorId) || errorId != requestId)
                        throw new InvalidDataException("Handshake error is not correlated to the request.");
                    throw ReadProtocolError(root);
                }
                if (handshakeKind.GetString() != "response" || !root.TryGetProperty("bootstrapVersion", out var bootstrap) || !bootstrap.TryGetInt32(out var bootstrapVersion) || bootstrapVersion != 1 ||
                    !root.TryGetProperty("correlationId", out var responseCorrelation) || responseCorrelation.ValueKind != JsonValueKind.String || !responseCorrelation.TryGetGuid(out var responseId) || responseId != requestId ||
                    !root.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.String || product.GetString() != Product ||
                    !root.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.Object ||
                    !protocol.TryGetProperty("major", out var major) || !major.TryGetInt32(out var protocolMajor) ||
                    !protocol.TryGetProperty("minor", out var minor) || !minor.TryGetInt32(out var protocolMinor) ||
                    !root.TryGetProperty("schemas", out var schemas) || schemas.ValueKind != JsonValueKind.Object ||
                    !TryInt(schemas, "command", out var commandSchema) || !TryInt(schemas, "event", out var eventSchema) || !TryInt(schemas, "report", out var reportSchema) ||
                    !WorkerProtocolCompatibility.IsCompatible(protocolMajor, protocolMinor, commandSchema, eventSchema, reportSchema) ||
                    !root.TryGetProperty("workerVersion", out var workerVersion) || workerVersion.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(workerVersion.GetString()) || workerVersion.GetString()!.Length > 64 ||
                    !root.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array ||
                    capabilities.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(c.GetString()) || c.GetString()!.Length > 64))
                    throw new InvalidDataException("Handshake response identity, version, schema, or required fields are incompatible.");
                _capabilities = capabilities.EnumerateArray().Select(c => c.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
            }
            catch (WorkerProtocolException ex) { SetState(WorkerConnectionState.Incompatible, $"Согласование worker отклонено: {ex.ErrorCode}."); throw; }
            catch (Exception ex) { SetState(WorkerConnectionState.Incompatible, "Ответ worker не прошёл согласование протокола."); throw new WorkerProtocolException("HANDSHAKE", "INCOMPATIBLE_PROTOCOL", ex.Message); }
            using (ExecutionContext.SuppressFlow())
            {
                _readerTask = ReaderLoopAsync(_lifetime.Token);
                _ = MonitorExitAsync(_process);
            }
            SetState(WorkerConnectionState.ConnectedFake, "Подключено к синтетическому worker · без аудиоустройств.");
            EndpointDiscoveryQaTrace.Write("worker-connect-complete", state: _state.State.ToString(), exitCode: QueryExitCode(_process));
        }
        catch (Exception ex)
        {
            EndpointDiscoveryQaTrace.Write("worker-connect-failure", state: _state.State.ToString(), exitCode: QueryExitCode(_process), exceptionType: ex.GetType().Name, errorCode: ex is Win32Exception win32 ? win32.NativeErrorCode.ToString() : null);
            if (_state.State == WorkerConnectionState.Incompatible) KillOwnedWorker();
            else
            {
                SetState(WorkerConnectionState.Failed, $"Ошибка worker: {ex.GetType().Name}: {ex.Message}");
                RaiseSafely(TransportFault, new WorkerTransportFaultDto("CONNECT", ex.Message, QueryExitCode(_process)));
            }
            KillOwnedWorker();
            throw;
        }
    }

    private Task? _readerTask;

    public async Task<IReadOnlyList<WorkerEndpointDto>> ListEndpointsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("ListEndpoints", new { direction = "BOTH" }, cancellationToken).ConfigureAwait(false);
        var payload = response.RootElement.GetProperty("payload");
        if (!payload.TryGetProperty("provenance", out var provenance) || provenance.GetString() != "FAKE_SYNTHETIC") throw new InvalidDataException("ListEndpoints response is not marked FAKE_SYNTHETIC.");
        var endpoints = payload.GetProperty("endpoints").Deserialize<WorkerEndpointDto[]>(JsonOptions) ?? [];
        foreach (var endpoint in endpoints) WorkerEndpointValidation.Validate(endpoint);
        return endpoints;
    }

    public async Task<WindowsEndpointDiscoveryDto> ListWindowsEndpointsAsync(CancellationToken cancellationToken = default)
    {
        if (!_capabilities.Contains("ListWindowsEndpoints", StringComparer.Ordinal)) throw new WorkerCommandException("ListWindowsEndpoints", "NOT_SUPPORTED", "The connected worker did not advertise this capability.");
        if (_state.State != WorkerConnectionState.ConnectedFake) throw new InvalidOperationException("Worker transport is not connected.");
        using var response = await SendAsync("ListWindowsEndpoints", new { }, cancellationToken, responseTimeoutOverride: ResponseTimeoutOverrideForTest ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        System.Diagnostics.Trace.TraceInformation("EndpointDiscovery stage=payload-parse workerState={0}", _state.State);
        var discovery = response.RootElement.GetProperty("payload").Deserialize<WindowsEndpointDiscoveryDto>(JsonOptions)
            ?? throw new InvalidDataException("Windows endpoint response payload is missing.");
        if (response.RootElement.GetProperty("payload").TryGetProperty("discoveryGeneration", out var generation))
        {
            if (!generation.TryGetUInt64(out _discoveryGeneration) || _discoveryGeneration == 0) throw new InvalidDataException("Worker discovery returned an invalid generation identity.");
        }
        else if (_capabilities.Contains("ScanSessionAuthorizationV2", StringComparer.Ordinal)) throw new InvalidDataException("V2-capable Worker discovery omitted its generation identity.");
        else checked { _discoveryGeneration++; }
        WindowsEndpointDiscoveryValidation.Validate(discovery, _capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal));
        System.Diagnostics.Trace.TraceInformation("EndpointDiscovery stage=validated endpointCount={0} workerState={1}", discovery.Endpoints.Count, _state.State);
        return discovery;
    }

    public async Task<WorkerScanSessionToken> RegisterScanSessionAsync(WorkerScanPlan plan, bool operatorAccepted, Guid confirmationId, CancellationToken cancellationToken = default)
    {
        if (!_capabilities.Contains("ScanSessionAuthorizationV1", StringComparer.Ordinal)) throw new WorkerCommandException("RegisterScanSession", "NOT_SUPPORTED", "Worker does not implement scan-session authorization.");
        var maximumDuration = plan.AllowedOperations.Contains("ValidateSustainedDuplex", StringComparer.Ordinal) ? MaximumSustainedDuplexDurationSeconds : MaximumDuplexDurationSeconds;
        if (!operatorAccepted || confirmationId == Guid.Empty || plan.LifetimeSeconds is < 1 or > 660 || plan.MaximumOperationDurationSeconds < 1 || plan.MaximumOperationDurationSeconds > maximumDuration || plan.MaximumOperationCount is < 1 or > 24 || plan.Endpoints.Count is < 1 or > 2 || plan.Formats.Count is < 1 or > 32) throw new ArgumentException("An explicitly accepted scan plan with bounded endpoints, settings and limits is required.", nameof(plan));
        if (plan.AllowedOperations.Contains("InitializeExclusiveCustomPeriod", StringComparer.Ordinal) && plan.Formats.SelectMany(f => f.Periods).Any(period => !period.StartsWith("CUSTOM:", StringComparison.Ordinal) || period.Count(c => c == ':') != 2)) throw new ArgumentException("Custom-period authorization must enumerate only explicit CUSTOM:<100ns>:<frames> values.", nameof(plan));
        var payloadValue = new { endpoints = plan.Endpoints.Select(e => new { endpointId = e.EndpointId, direction = e.Direction }), allowedOperations = plan.AllowedOperations, formats = plan.Formats.Select(f => new { sampleRateHz = f.SampleRateHz, encoding = f.Encoding, channels = f.Channels, periods = f.Periods, mmcssModes = f.MmcssModes }), lifetimeSeconds = plan.LifetimeSeconds, maximumOperationDurationSeconds = plan.MaximumOperationDurationSeconds, maximumOperationCount = plan.MaximumOperationCount, planFingerprint = WorkerScanPlanFingerprint.Compute(plan), authorization = new { accepted = true, confirmationId } };
        using var response = await SendAsync("RegisterScanSession", payloadValue, cancellationToken).ConfigureAwait(false);
        var payload = response.RootElement.GetProperty("payload");
        if (!payload.TryGetProperty("scanSessionId", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String || !id.TryGetGuid(out var sessionId) || sessionId == Guid.Empty || payload.GetProperty("state").GetString() != "AUTHORIZED") throw new InvalidDataException("Worker did not return a valid opaque scan-session token.");
        return new(sessionId);
    }

    public async Task<WorkerScanSessionToken> RegisterScanSessionV2Async(string captureEndpointId, string renderEndpointId, WorkerScanPlanV2 plan, bool operatorAccepted, Guid confirmationId, CancellationToken cancellationToken = default)
    {
        if (!_capabilities.Contains("ScanSessionAuthorizationV2", StringComparer.Ordinal)) throw new WorkerCommandException("RegisterScanSessionV2", "NOT_SUPPORTED", "Worker does not implement V2 scan authorization.");
        if (!operatorAccepted || confirmationId == Guid.Empty || _discoveryGeneration == 0) throw new ArgumentException("Fresh discovery and explicit operator acceptance are required for V2 scan authorization.", nameof(plan));
        var routeFingerprint = WorkerScanPlanV2Fingerprint.Route(captureEndpointId, renderEndpointId, plan.Route);
        var planFingerprint = WorkerScanPlanV2Fingerprint.Compute(captureEndpointId, renderEndpointId, plan, _discoveryGeneration);
        var route = new { mode = plan.Route.Mode, capture = new { sampleRateHz = plan.Route.Capture.SampleRateHz, channels = plan.Route.Capture.Channels, encoding = plan.Route.Capture.Encoding, channelMask = plan.Route.Capture.ChannelMask, selectedIndicesZeroBased = plan.Route.Capture.SelectedIndicesZeroBased }, render = new { sampleRateHz = plan.Route.Render.SampleRateHz, channels = plan.Route.Render.Channels, encoding = plan.Route.Render.Encoding, channelMask = plan.Route.Render.ChannelMask, selectedIndicesZeroBased = plan.Route.Render.SelectedIndicesZeroBased } };
        using var response = await SendAsync("RegisterScanSessionV2", new { contractVersion = 3, qaMode = plan.QaMode, captureEndpointId, renderEndpointId, route, routeFingerprint, periods = plan.Periods, mmcssModes = plan.MmcssModes, sustainedDurationsSeconds = plan.QaMode == "FULL" ? new[] { 60, 300, 600 } : [], lifetimeSeconds = plan.LifetimeSeconds, maximumOperationDurationSeconds = plan.MaximumOperationDurationSeconds, maximumOperationCount = plan.MaximumOperationCount, maximumTotalDurationSeconds = plan.MaximumTotalDurationSeconds, planFingerprint, authorization = new { accepted = true, confirmationId } }, cancellationToken).ConfigureAwait(false);
        var payload = response.RootElement.GetProperty("payload");
        if (!payload.TryGetProperty("scanSessionId", out var id) || !id.TryGetGuid(out var sessionId) || sessionId == Guid.Empty || payload.GetProperty("contractVersion").GetInt32() != 3 || payload.GetProperty("state").GetString() != "AUTHORIZED") throw new InvalidDataException("Worker did not return a valid V2 scan-session token.");
        return new(sessionId, 3, routeFingerprint);
    }

    public async Task<WorkerScanSessionToken> RegisterScanSessionV2V4Async(string captureEndpointId, string renderEndpointId,
        WorkerScanV2ContractV4 plan, WorkerScanV2V4Consent consent, CancellationToken cancellationToken = default)
    {
        if (!_capabilities.Contains("ScanSessionAuthorizationV2ContractV4", StringComparer.Ordinal)) throw new WorkerCommandException("RegisterScanSessionV2", "NOT_SUPPORTED", "Worker does not implement V2 contract v4.");
        if (!consent.Accepted || consent.ConfirmationId == Guid.Empty || _discoveryGeneration == 0 || consent.DiscoveryGeneration != _discoveryGeneration) throw new ArgumentException("Fresh discovery and explicit operator acceptance are required for V2 contract v4.", nameof(consent));
        var routeFingerprint = WorkerScanPlanV2Fingerprint.Route(captureEndpointId, renderEndpointId, plan.Route);
        var planFingerprint = WorkerScanV2ContractV4Fingerprint.Compute(captureEndpointId, renderEndpointId, plan, _discoveryGeneration);
        if (!string.Equals(consent.PlanFingerprint, planFingerprint, StringComparison.Ordinal)) throw new ArgumentException("V4 consent does not bind the exact prepared plan.", nameof(consent));
        var route = new { mode = plan.Route.Mode, capture = new { sampleRateHz = plan.Route.Capture.SampleRateHz, channels = plan.Route.Capture.Channels, encoding = plan.Route.Capture.Encoding, channelMask = plan.Route.Capture.ChannelMask, selectedIndicesZeroBased = plan.Route.Capture.SelectedIndicesZeroBased }, render = new { sampleRateHz = plan.Route.Render.SampleRateHz, channels = plan.Route.Render.Channels, encoding = plan.Route.Render.Encoding, channelMask = plan.Route.Render.ChannelMask, selectedIndicesZeroBased = plan.Route.Render.SelectedIndicesZeroBased } };
        var candidates = plan.Candidates.Select(c => new { index = c.Index, requestedDuration100ns = c.RequestedDuration100ns, captureFrames = c.CaptureFrames, renderFrames = c.RenderFrames, capturePeriod100ns = c.CapturePeriod100ns, renderPeriod100ns = c.RenderPeriod100ns, captureAlignmentFrames = c.CaptureAlignmentFrames, captureAlignmentKnown = c.CaptureAlignmentKnown, renderAlignmentFrames = c.RenderAlignmentFrames, renderAlignmentKnown = c.RenderAlignmentKnown, captureMinimumFrames = c.CaptureMinimumFrames, captureMaximumFrames = c.CaptureMaximumFrames, renderMinimumFrames = c.RenderMinimumFrames, renderMaximumFrames = c.RenderMaximumFrames, rejectionReason = c.RejectionReason }).ToArray();
        var captureBounds = new { minimumPeriod100ns = plan.CaptureBounds.MinimumPeriod100ns, defaultPeriod100ns = plan.CaptureBounds.DefaultPeriod100ns, alignmentFrames = plan.CaptureBounds.AlignmentFrames };
        var renderBounds = new { minimumPeriod100ns = plan.RenderBounds.MinimumPeriod100ns, defaultPeriod100ns = plan.RenderBounds.DefaultPeriod100ns, alignmentFrames = plan.RenderBounds.AlignmentFrames };
        using var response = await SendAsync("RegisterScanSessionV2", new { contractVersion = 4, captureEndpointId, renderEndpointId, route, routeFingerprint,
            candidates, captureBounds, renderBounds, mmcssModes = plan.MmcssModes, repeatabilityRuns = plan.RepeatabilityRuns,
            maximumOperationCount = plan.MaximumOperations, maximumActiveDuplexSeconds = plan.MaximumActiveDuplexSeconds,
            initializeTimeoutSeconds = plan.InitializeTimeoutSeconds, sessionLifetimeSeconds = plan.SessionLifetimeSeconds,
            cleanupDeadlineSeconds = plan.CleanupDeadlineSeconds, planFingerprint, authorization = new { accepted = consent.Accepted, confirmationId = consent.ConfirmationId } }, cancellationToken).ConfigureAwait(false);
        var payload = response.RootElement.GetProperty("payload");
        if (!payload.TryGetProperty("scanSessionId", out var id) || !id.TryGetGuid(out var sessionId) || sessionId == Guid.Empty || payload.GetProperty("contractVersion").GetInt32() != 4 || payload.GetProperty("state").GetString() != "AUTHORIZED") throw new InvalidDataException("Worker did not return a valid v4 scan-session token.");
        return new(sessionId, 4, routeFingerprint);
    }

    public async Task CancelScanSessionAsync(WorkerScanSessionToken session, CancellationToken cancellationToken = default) => await EndScanSessionAsync("CancelScanSession", session, cancellationToken).ConfigureAwait(false);
    public async Task CloseScanSessionAsync(WorkerScanSessionToken session, CancellationToken cancellationToken = default) => await EndScanSessionAsync("CloseScanSession", session, cancellationToken).ConfigureAwait(false);
    private async Task EndScanSessionAsync(string command, WorkerScanSessionToken session, CancellationToken cancellationToken)
    {
        if (session.ScanSessionId == Guid.Empty) throw new ArgumentException("A valid Worker scan-session token is required.", nameof(session));
        using var response = await SendAsync(command, new { scanSessionId = session.ScanSessionId }, cancellationToken).ConfigureAwait(false);
        if (response.RootElement.GetProperty("payload").GetProperty("state").GetString() != "INVALIDATED") throw new InvalidDataException("Worker did not confirm scan authorization invalidation.");
    }

    public async Task<RealFormatMatrixDto> QueryFormatMatrixAsync(string endpointId, string direction, IReadOnlyList<int>? channelCounts = null, CancellationToken cancellationToken = default)
    {
        if (!_capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal)) throw new WorkerCommandException("QueryFormatMatrix", "NOT_SUPPORTED", "Worker does not advertise real WASAPI format queries.");
        if (string.IsNullOrWhiteSpace(endpointId) || endpointId.Length > 32768 || direction is not ("CAPTURE" or "RENDER")) throw new ArgumentException("An exact endpoint ID and direction are required.");
        var channels = channelCounts ?? new[] { 1, 2 };
        if (channels.Count is < 1 or > 64 || channels.Any(c => c is < 1 or > 64) || channels.Distinct().Count() != channels.Count) throw new ArgumentOutOfRangeException(nameof(channelCounts));
        if (channels.Count > 32) throw new ArgumentOutOfRangeException(nameof(channelCounts), "At most 32 channel counts fit the bounded request contract.");
        var result = await SendRealMatrixAsync("QueryFormatMatrix", new { endpointId, direction, channelCounts = channels }, endpointId, direction, cancellationToken).ConfigureAwait(false);
        var expected = (from rate in new[] { 44100, 48000 } from count in channels from encoding in new[] { "PCM16", "PCM24_IN_32", "FLOAT32" } from period in new[] { "default", "minimum" } select (rate, count, encoding, period)).ToHashSet();
        if (result.ReportLevel != "PASSIVE_QUERY" || result.Candidates.Count != expected.Count || result.Candidates.Any(c => !expected.Remove((c.SampleRate,c.Channels,c.Encoding,c.Period)))) throw new InvalidDataException("Passive query result does not match the requested bounded candidate matrix.");
        return result;
    }

    public async Task<RealFormatMatrixDto> QueryFormatMatrixInScanAsync(WorkerScanSessionToken session, Guid operationId, string endpointId, string direction, IReadOnlyList<int>? channelCounts = null, CancellationToken cancellationToken = default)
    {
        if (session.ScanSessionId == Guid.Empty || operationId == Guid.Empty) throw new ArgumentException("A valid scan token and unique operation UUID are required.");
        if (!_capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal)) throw new WorkerCommandException("QueryFormatMatrix", "NOT_SUPPORTED", "Worker does not advertise real WASAPI format queries.");
        var channels = channelCounts ?? new[] { 1, 2 };
        if (channels.Count is < 1 or > 32 || channels.Any(c => c is < 1 or > 64) || channels.Distinct().Count() != channels.Count) throw new ArgumentOutOfRangeException(nameof(channelCounts));
        var payload = new Dictionary<string, object?> { ["endpointId"] = endpointId, ["direction"] = direction, ["channelCounts"] = channels, ["scanSessionId"] = session.ScanSessionId, ["scanOperationId"] = operationId };
        var result = await SendRealMatrixAsync("QueryFormatMatrix", payload, endpointId, direction, cancellationToken).ConfigureAwait(false);
        var expected = (from rate in new[] { 44100, 48000 } from count in channels from encoding in new[] { "PCM16", "PCM24_IN_32", "FLOAT32" } from period in new[] { "default", "minimum" } select (rate, count, encoding, period)).ToHashSet();
        if (result.ReportLevel != "PASSIVE_QUERY" || result.Candidates.Count != expected.Count || result.Candidates.Any(c => !expected.Remove((c.SampleRate,c.Channels,c.Encoding,c.Period)))) throw new InvalidDataException("Passive query result does not match the authorized candidate matrix.");
        return result;
    }

    public async Task<RealFormatMatrixDto> ValidateFormatMatrixInScanAsync(WorkerScanSessionToken session, Guid operationId, string endpointId, string direction, int sampleRate, int channels, string encoding, string period, CancellationToken cancellationToken = default)
    {
        if (session.ScanSessionId == Guid.Empty || session.ContractVersion is not (1 or 2 or 3) || operationId == Guid.Empty || string.IsNullOrWhiteSpace(endpointId) || direction is not ("CAPTURE" or "RENDER") || sampleRate is not (44100 or 48000) || channels < 1 || channels > (session.ContractVersion >= 2 ? 32 : 64) || encoding is not ("PCM16" or "PCM24_IN_32" or "FLOAT32") || period is not ("DEFAULT" or "MINIMUM")) throw new ArgumentException("Initialize-only scan settings are invalid.");
        var payload = new Dictionary<string, object?> { ["endpointId"] = endpointId, ["direction"] = direction, ["sampleRate"] = sampleRate, ["channels"] = channels, ["encoding"] = encoding, ["period"] = period, ["scanSessionId"] = session.ScanSessionId, ["scanOperationId"] = operationId };
        var result = await SendRealMatrixAsync("ValidateFormatMatrix", payload, endpointId, direction, cancellationToken).ConfigureAwait(false);
        if (result.ReportLevel != "INITIALIZE_ONLY" || result.Candidates.Count != 1 || result.Candidates[0] is not { } candidate || candidate.SampleRate != sampleRate || candidate.Channels != channels || candidate.Encoding != encoding || candidate.Period != period.ToLowerInvariant()) throw new InvalidDataException("Initialize-only response differs from the approved scan settings.");
        return result;
    }

    public async Task<ExclusivePeriodDiscoveryReportDto> InitializeExclusiveCustomPeriodAsync(WorkerScanSessionToken session, Guid operationId, string endpointId, string direction, int period100ns, int frames, CancellationToken cancellationToken = default)
    {
        if (session.ContractVersion != 1 || session.ScanSessionId == Guid.Empty || operationId == Guid.Empty || string.IsNullOrWhiteSpace(endpointId) || direction is not ("CAPTURE" or "RENDER") || period100ns <= 0 || frames is <= 0 or > 48_000) throw new ArgumentException("Custom period discovery requires a v1 authorized endpoint and bounded 48 kHz frame request.");
        var equivalent = ((long)frames * 10_000_000 + 24_000) / 48_000;
        if (Math.Abs(equivalent - period100ns) > 1) throw new ArgumentException("Custom period duration must match the requested 48 kHz frame count.");
        if (!_capabilities.Contains("RealExclusiveCustomPeriodInitialize", StringComparer.Ordinal)) throw new WorkerCommandException("InitializeExclusiveCustomPeriod", "NOT_SUPPORTED", "Worker does not advertise custom Exclusive period discovery.");
        using var response = await SendAsync("InitializeExclusiveCustomPeriod", new { operationId, endpointId, direction, sampleRate = 48_000, channels = 2, encoding = "PCM16", period = $"CUSTOM:{period100ns}:{frames}", period100ns, frames, scanSessionId = session.ScanSessionId, scanOperationId = operationId }, cancellationToken).ConfigureAwait(false);
        var payload = response.RootElement.GetProperty("payload");
        if (payload.GetProperty("operationId").GetGuid() != operationId || payload.GetProperty("endpointId").GetString() != endpointId || payload.GetProperty("direction").GetString() != direction) throw new InvalidDataException("Custom-period response identity differs from the authorized operation.");
        var report = payload.GetProperty("report");
        if (report.GetProperty("schemaVersion").GetInt32() != 1 || report.GetProperty("reportType").GetString() != "wasapi-exclusive-period-discovery" || report.GetProperty("operationId").GetGuid() != operationId || report.GetProperty("endpointId").GetString() != endpointId || report.GetProperty("direction").GetString() != direction || report.GetProperty("provenance").GetString() is not ("REAL_WASAPI_INITIALIZE_ONLY" or "TEST_FAKE_PERIOD_BACKEND")) throw new InvalidDataException("Custom-period report identity or provenance is invalid.");
        var format = report.GetProperty("format");
        if (format.GetProperty("sampleRateHz").GetInt32() != 48_000 || format.GetProperty("encoding").GetString() != "PCM16" || format.GetProperty("channels").GetInt32() != 2) throw new InvalidDataException("Custom-period report format differs from the authorized format.");
        var items = report.GetProperty("candidates").EnumerateArray().Select(item => new ExclusivePeriodCandidateDto(item.GetProperty("requestedPeriod100ns").GetInt64(), item.GetProperty("requestedFrames").GetUInt32(), item.GetProperty("requestedEquivalentPeriod100ns").GetInt64(), item.GetProperty("classification").GetString() ?? "UNKNOWN", item.GetProperty("reason").GetString() ?? "", item.GetProperty("finalInitializeHr").ValueKind == JsonValueKind.String ? item.GetProperty("finalInitializeHr").GetString() : null, item.GetProperty("alignmentFrames").ValueKind == JsonValueKind.Number ? item.GetProperty("alignmentFrames").GetUInt32() : null, item.GetProperty("alignmentInitializeHr").ValueKind == JsonValueKind.String ? item.GetProperty("alignmentInitializeHr").GetString() : null, item.GetProperty("actualBufferFrames").ValueKind == JsonValueKind.Number ? item.GetProperty("actualBufferFrames").GetUInt32() : null, item.GetProperty("actualPeriod100ns").ValueKind == JsonValueKind.Number ? item.GetProperty("actualPeriod100ns").GetInt64() : null, item.GetProperty("cleanupStatus").GetString() ?? "INCOMPLETE", item.GetProperty("clientReleaseAttempts").GetUInt32(), item.TryGetProperty("alignmentRequiresExplicitConfirmation", out var explicitConfirmation) && explicitConfirmation.ValueKind == JsonValueKind.True)).ToArray();
        if (items.Length != 1 || items[0].RequestedPeriod100ns != period100ns || items[0].RequestedFrames != frames || items[0].Classification is not ("ACCEPTED" or "ACCEPTED_ALIGNED" or "REJECTED" or "INCOMPLETE" or "UNKNOWN")) throw new InvalidDataException("Custom-period report candidate does not match the authorized request.");
        return new(1, "wasapi-exclusive-period-discovery", report.GetProperty("reportId").GetGuid().ToString("D"), operationId.ToString("D"), report.GetProperty("timestampUtc").GetString() ?? "", report.GetProperty("provenance").GetString()!, endpointId, direction, 48_000, "PCM16", 2, report.GetProperty("devicePeriods").GetProperty("minimum100ns").GetInt64(), report.GetProperty("devicePeriods").GetProperty("default100ns").GetInt64(), items);
    }

    public async Task<RealFormatMatrixDto> ValidateFormatMatrixAsync(string endpointId, string direction, int sampleRate, int channels, string encoding, string period, RealFormatConsentDto consent, CancellationToken cancellationToken = default)
    {
        if (!_capabilities.Contains("RealExclusiveFormatInitialize", StringComparer.Ordinal)) throw new WorkerCommandException("ValidateFormatMatrix", "NOT_SUPPORTED", "Worker does not advertise real Initialize-only checks.");
        if (string.IsNullOrWhiteSpace(endpointId) || endpointId.Length > 32768 || direction is not ("CAPTURE" or "RENDER") || sampleRate is not (44100 or 48000) || channels is < 1 or > 64 || encoding is not ("PCM16" or "PCM24_IN_32" or "FLOAT32") || period is not ("DEFAULT" or "MINIMUM" or "default" or "minimum")) throw new ArgumentException("The selected format or period is not a supported exact candidate.");
        if (!consent.Accepted || consent.ConfirmationId == Guid.Empty || consent.RequestFingerprint != RealFormatConsent.Fingerprint(endpointId, direction, sampleRate, channels, encoding, period)) throw new ArgumentException("Fresh consent must match the exact endpoint, format, and period.");
        var result = await SendRealMatrixAsync("ValidateFormatMatrix", new { endpointId, direction, sampleRate, channels, encoding, period = period.ToUpperInvariant(), consentReceipt = new { accepted = consent.Accepted, confirmationId = consent.ConfirmationId, requestFingerprint = consent.RequestFingerprint } }, endpointId, direction, cancellationToken).ConfigureAwait(false);
        if (result.ReportLevel != "INITIALIZE_ONLY" || result.Candidates.Count != 1 || result.Candidates[0] is not { } candidate || candidate.SampleRate != sampleRate || candidate.Channels != channels || candidate.Encoding != encoding || candidate.Period != period.ToLowerInvariant() || (!candidate.InitializeAttempted && candidate.InitializeStatus is ("INITIALIZED" or "READY"))) throw new InvalidDataException("Initialize-only response does not match the single consented format and period.");
        return result;
    }

    public async Task<JsonElement> InitializeCustomPeriodV2InScanAsync(WorkerScanSessionToken session, Guid operationId,
        string captureEndpointId, string renderEndpointId, WorkerScanV2ContractV4 plan, int candidateIndex,
        string direction, CancellationToken cancellationToken = default, string? testScenario = null)
    {
        if (session.ContractVersion != 4 || session.ScanSessionId == Guid.Empty || operationId == Guid.Empty ||
            direction is not ("CAPTURE" or "RENDER") || !_capabilities.Contains("V2CustomTransportContractV4", StringComparer.Ordinal))
            throw new ArgumentException("An explicitly registered v4 custom transport session is required.");
        if (testScenario is not null && (!_capabilities.Contains("TestInjectedDuplexBackend", StringComparer.Ordinal) ||
            testScenario is not ("INIT_FAILED" or "INIT_UNSUPPORTED" or "ALIGNMENT_MISMATCH" or "ACTUAL_FRAMES_MISMATCH" or "ACTUAL_PERIOD_MISMATCH" or "CLEANUP_UNKNOWN")))
            throw new ArgumentException("Injected v4 Initialize scenarios are available only in test workers.", nameof(testScenario));
        var routeFingerprint = WorkerScanPlanV2Fingerprint.Route(captureEndpointId, renderEndpointId, plan.Route);
        var candidate = plan.Candidates.SingleOrDefault(c => c.Index == candidateIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(candidateIndex));
        if (candidate.RejectionReason.Length != 0) throw new ArgumentException("Rejected candidate cannot be initialized.", nameof(candidateIndex));
        var capture = direction == "CAPTURE";
        var endpointId = capture ? captureEndpointId : renderEndpointId;
        var side = capture ? plan.Route.Capture : plan.Route.Render;
        var frames = capture ? candidate.CaptureFrames : candidate.RenderFrames;
        var period = capture ? candidate.CapturePeriod100ns : candidate.RenderPeriod100ns;
        var payload = new { operationId, scanOperationId = operationId, scanSessionId = session.ScanSessionId,
            candidateIndex, endpointId, direction, sampleRate = side.SampleRateHz, channels = side.Channels,
            encoding = side.Encoding, requestedPeriod100ns = period, requestedFrames = frames, routeFingerprint };
        var request = new Dictionary<string, object?> { ["operationId"] = operationId, ["scanOperationId"] = operationId,
            ["scanSessionId"] = session.ScanSessionId, ["candidateIndex"] = candidateIndex, ["endpointId"] = endpointId,
            ["direction"] = direction, ["sampleRate"] = side.SampleRateHz, ["channels"] = side.Channels,
            ["encoding"] = side.Encoding, ["requestedPeriod100ns"] = period, ["requestedFrames"] = frames, ["routeFingerprint"] = routeFingerprint };
        if (testScenario is not null) request["testScenario"] = testScenario;
        using var response = await SendAsync("InitializeCustomPeriodV2", request, cancellationToken).ConfigureAwait(false);
        var result = response.RootElement.GetProperty("payload");
        var disposition = result.GetProperty("initializeDisposition").GetString();
        var cleanup = result.GetProperty("cleanup").GetString();
        var actualFrames = result.GetProperty("actualFrames");
        var actualPeriod = result.GetProperty("actualPeriod100ns");
        var initializeHresult = result.GetProperty("initializeHresult");
        var missingField = new[] { "contractVersion", "operationId", "scanSessionId", "candidateIndex", "routeFingerprint", "endpointId", "direction", "sampleRateHz", "channels", "encoding", "requestedPeriod100ns", "requestedFrames", "actualPeriod100ns", "actualFrames", "alignmentFrames", "alignmentReason", "initializeDisposition", "initializeHresult", "reasonCode", "cleanup", "provenance" }.FirstOrDefault(name => !result.TryGetProperty(name, out _));
        if (missingField is not null) throw new InvalidDataException($"V4 Initialize evidence is missing required field {missingField}.");
        if (result.GetProperty("contractVersion").GetInt32() != 4 || result.GetProperty("operationId").GetGuid() != operationId ||
            result.GetProperty("scanSessionId").GetGuid() != session.ScanSessionId || result.GetProperty("candidateIndex").GetInt32() != candidateIndex ||
            result.GetProperty("routeFingerprint").GetString() != routeFingerprint || result.GetProperty("endpointId").GetString() != endpointId ||
            result.GetProperty("direction").GetString() != direction || result.GetProperty("sampleRateHz").GetInt32() != side.SampleRateHz ||
            result.GetProperty("channels").GetInt32() != side.Channels || result.GetProperty("encoding").GetString() != side.Encoding ||
            result.GetProperty("requestedPeriod100ns").GetInt64() != period ||
            result.GetProperty("requestedFrames").GetInt32() != frames ||
            disposition is not ("INITIALIZED_EXACT" or "FAILED" or "UNSUPPORTED" or "UNKNOWN") ||
            (actualFrames.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)) ||
            (actualPeriod.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)) ||
            (initializeHresult.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)) ||
            cleanup is not ("RELEASE_ATTEMPTED" or "FAILED" or "UNKNOWN") ||
            result.GetProperty("alignmentFrames").ValueKind is not (JsonValueKind.Number or JsonValueKind.Null) ||
            string.IsNullOrWhiteSpace(result.GetProperty("alignmentReason").GetString()) ||
            string.IsNullOrWhiteSpace(result.GetProperty("reasonCode").GetString()) ||
            result.GetProperty("provenance").GetString() is not ("REAL_WASAPI_V4_INITIALIZE" or "TEST_FAKE_WASAPI_V4_INITIALIZE") ||
            disposition == "INITIALIZED_EXACT" && (actualFrames.GetInt32() != frames || actualPeriod.GetInt64() != period || initializeHresult.GetInt32() != 0 || cleanup != "RELEASE_ATTEMPTED"))
            throw new InvalidDataException("V4 Initialize evidence omits required identity, exact geometry, disposition, or cleanup fields.");
        return result.Clone();
    }

    public Task<RealStreamValidationDto> ValidateStreamInScanAsync(string operationId, string endpointId, string direction, string period, int durationSeconds, string mmcss,
        WorkerScanSessionToken scanSession, Guid scanOperationId, CancellationToken cancellationToken = default) =>
        ValidateStreamCoreAsync(operationId, endpointId, direction, period, durationSeconds, mmcss, null, cancellationToken, null, scanSession, scanOperationId);

    public Task<RealStreamValidationDto> ValidateStreamAsync(string operationId, string endpointId, string direction, string period, int durationSeconds, string mmcss, RealStreamConsentDto consent, CancellationToken cancellationToken = default, string? testScenario = null, WorkerScanSessionToken? scanSession = null, Guid? scanOperationId = null) =>
        ValidateStreamCoreAsync(operationId, endpointId, direction, period, durationSeconds, mmcss, consent, cancellationToken, testScenario, scanSession, scanOperationId);

    private async Task<RealStreamValidationDto> ValidateStreamCoreAsync(string operationId, string endpointId, string direction, string period, int durationSeconds, string mmcss, RealStreamConsentDto? consent, CancellationToken cancellationToken, string? testScenario, WorkerScanSessionToken? scanSession, Guid? scanOperationId)
    {
        if (!_capabilities.Contains("RealExclusiveStreamValidation", StringComparer.Ordinal)) throw new WorkerCommandException("ValidateStream", "NOT_SUPPORTED", "Worker does not advertise real stream validation.");
        if (!Guid.TryParse(operationId, out var requestedOperationId) || requestedOperationId == Guid.Empty || string.IsNullOrWhiteSpace(endpointId) || endpointId.Length > 32768 || direction is not ("CAPTURE" or "RENDER") || period is not ("DEFAULT" or "MINIMUM") || durationSeconds is < 1 or > 10 || mmcss is not ("OFF" or "PRO_AUDIO")) throw new ArgumentException("Real stream validation requires one operation ID, exact endpoint and bounded 48 kHz stereo PCM16 settings.");
        if (scanSession is null && (consent is null || !consent.Accepted || consent.ConfirmationId == Guid.Empty || consent.RequestFingerprint != RealStreamConsent.Fingerprint(endpointId, direction, period, durationSeconds, mmcss)) || scanSession is not null && (scanSession.ScanSessionId == Guid.Empty || scanOperationId != requestedOperationId)) throw new ArgumentException("Fresh manual consent or a matching Worker-authorized scan operation is required.");
        if (testScenario is not null && (!_capabilities.Contains("TestInjectedStreamBackend", StringComparer.Ordinal) || testScenario is not ("CLEAN" or "START_FAIL" or "ZERO_FRAMES" or "LOW_COVERAGE" or "DISCONTINUITY" or "TIMEOUT" or "CANCELLED" or "DEVICE_LOSS" or "CLEANUP_UNKNOWN" or "WAIT_FOR_CANCEL" or "INVALID_REPORT_ID" or "MISSING_REPORT_ID" or "UPPERCASE_REPORT_ID" or "MISMATCH_OPERATION_ID" or "MISMATCH_ENDPOINT" or "MISMATCH_RESPONSE_DIRECTION" or "MISMATCH_REPORT_DIRECTION" or "MISMATCH_PERIOD" or "MISMATCH_DURATION" or "MISMATCH_SAMPLE_RATE" or "MISMATCH_ENCODING" or "MISMATCH_CHANNELS" or "MISMATCH_PROVENANCE"))) throw new ArgumentException("Injected stream scenarios are available only in test workers.", nameof(testScenario));
        var requestPayload = new Dictionary<string, object?> { ["operationId"] = requestedOperationId, ["endpointId"] = endpointId, ["direction"] = direction, ["sampleRate"] = 48000, ["channels"] = 2, ["encoding"] = "PCM16", ["period"] = period, ["durationSeconds"] = durationSeconds, ["mmcss"] = mmcss };
        if (scanSession is null) requestPayload["consentReceipt"] = new { accepted = consent!.Accepted, confirmationId = consent.ConfirmationId, requestFingerprint = consent.RequestFingerprint };
        else { requestPayload["scanSessionId"] = scanSession.ScanSessionId; requestPayload["scanOperationId"] = scanOperationId; }
        if (testScenario is not null) requestPayload["testScenario"] = testScenario;
        using var response = await SendAsync("ValidateStream", requestPayload, cancellationToken).ConfigureAwait(false);
        var payload = response.RootElement.GetProperty("payload");
        var resultOperationId = payload.GetProperty("operationId").GetString();
        var report = payload.GetProperty("report");
        var expectedProvenance = _capabilities.Contains("TestInjectedStreamBackend", StringComparer.Ordinal) ? "TEST_FAKE_STREAM_BACKEND" : "REAL_WASAPI_STREAM_VALIDATION";
        static void Require(bool valid, string field)
        {
            if (!valid) throw new InvalidDataException($"Stream response contract mismatch at {field}.");
        }
        static bool HasString(JsonElement value, string name, string expected) => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String && string.Equals(member.GetString(), expected, StringComparison.Ordinal);
        static bool HasInt(JsonElement value, string name, int expected) => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.Number && member.TryGetInt32(out var actual) && actual == expected;
        static bool HasUuid(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String && Guid.TryParse(member.GetString(), out var id) && id != Guid.Empty;

        Require(Guid.TryParse(resultOperationId, out var returnedOperationId) && returnedOperationId != Guid.Empty && returnedOperationId == requestedOperationId, "operationId");
        Require(payload.TryGetProperty("endpointId", out var resultEndpoint) && resultEndpoint.ValueKind == JsonValueKind.String && resultEndpoint.GetString() == endpointId, "endpointId");
        Require(payload.TryGetProperty("direction", out var resultDirection) && resultDirection.ValueKind == JsonValueKind.String && resultDirection.GetString() == direction, "direction");
        Require(report.ValueKind == JsonValueKind.Object, "report");
        Require(HasInt(report, "schemaVersion", 1), "schemaVersion");
        Require(HasString(report, "reportType", "wasapi-stream-validation"), "reportType");
        Require(HasUuid(report, "reportId"), "reportId");
        Require(HasString(report, "provenance", expectedProvenance), "provenance");
        Require(HasString(report, "direction", direction), "report.direction");
        Require(HasString(report, "period", period), "report.period");
        Require(HasInt(report, "durationSeconds", durationSeconds), "report.durationSeconds");
        var format = report.TryGetProperty("format", out var reportFormat) ? reportFormat : default;
        Require(HasInt(format, "sampleRateHz", 48000), "format.sampleRateHz");
        Require(HasInt(format, "channels", 2), "format.channels");
        Require(HasString(format, "encoding", "PCM16"), "format.encoding");
        foreach (var name in new[] { "events", "packets", "framesProcessed", "expectedFrames", "discontinuities", "timestampErrors", "unavailableCaptureBuffers", "bufferServiceFaults", "eventWaitTimeouts" })
            if (!report.TryGetProperty(name, out var value) || value.ValueKind is not (System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.Null) || value.ValueKind == System.Text.Json.JsonValueKind.Number && (!value.TryGetInt64(out var count) || count < 0)) throw new InvalidDataException($"Invalid numeric stream evidence: {name}.");
        foreach (var name in new[] { "durationReached", "startSucceeded", "stopSucceeded" })
            if (!report.TryGetProperty(name, out var value) || value.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.Null)) throw new InvalidDataException($"Invalid boolean stream evidence: {name}.");
        if (!report.TryGetProperty("streamQuality", out var qualityValue) || qualityValue.ValueKind != System.Text.Json.JsonValueKind.String || qualityValue.GetString() is not ("CLEAN" or "ANOMALIES_PRESENT" or "INCOMPLETE" or "UNKNOWN")) throw new InvalidDataException("Unknown stream quality classification.");
        if (qualityValue.GetString() == "CLEAN")
        {
            long? Count(string name) => report.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Number ? report.GetProperty(name).GetInt64() : null;
            var started = report.GetProperty("startSucceeded").ValueKind == System.Text.Json.JsonValueKind.True;
            var durationReached = report.GetProperty("durationReached").ValueKind == System.Text.Json.JsonValueKind.True;
            var stopped = report.GetProperty("stopSucceeded").ValueKind == System.Text.Json.JsonValueKind.True;
            var frames = Count("framesProcessed"); var expected = Count("expectedFrames"); var buffer = report.GetProperty("actualBufferFrames").TryGetInt32(out var actualBuffer) ? actualBuffer : 0;
            var cleanCounters = new[] { "discontinuities", "timestampErrors", "unavailableCaptureBuffers", "bufferServiceFaults" }.All(name => Count(name) == 0);
            if (!started || !durationReached || !stopped || frames is null || expected is null || expected <= 0 || buffer <= 0 || !cleanCounters || frames < expected - Math.Max(2L * buffer, expected.Value / 10)) throw new InvalidDataException("Worker marked stream CLEAN without successful Start, bounded completion, Stop and sufficient fault-free frame servicing.");
        }
        Require(HasString(report, "physicalPlayback", "NOT_MEASURED"), "physicalPlayback");
        Require(HasString(report, "physicalRouting", "UNKNOWN"), "physicalRouting");
        return new(resultOperationId!, endpointId, direction, report.Clone());
    }

    public async Task CancelStreamValidationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(operationId, out var id) || id == Guid.Empty) throw new ArgumentException("A valid active stream operation ID is required.", nameof(operationId));
        using var response = await SendAsync("CancelStreamValidation", new { operationId = id }, cancellationToken).ConfigureAwait(false);
        if (response.RootElement.GetProperty("payload").GetProperty("completion").GetString() != "PENDING") throw new InvalidDataException("Worker cancellation response did not preserve pending completion semantics.");
    }

    public Task<DuplexValidationDto> ValidateDuplexInScanAsync(string operationId, string captureEndpointId, string renderEndpointId, string period, int durationSeconds, string mmcss,
        WorkerScanSessionToken scanSession, Guid scanOperationId, CancellationToken cancellationToken = default) =>
        ValidateDuplexCoreAsync(operationId, captureEndpointId, renderEndpointId, period, durationSeconds, mmcss, null, cancellationToken, null, scanSession, scanOperationId);

    public Task<DuplexValidationDto> ValidateCustomDuplexInScanAsync(Guid operationId, string captureEndpointId, string renderEndpointId, string period, int durationSeconds, string mmcss,
        WorkerScanSessionToken scanSession, Guid scanOperationId, CancellationToken cancellationToken = default) =>
        ValidateDuplexCoreAsync(operationId.ToString("D"), captureEndpointId, renderEndpointId, period, durationSeconds, mmcss, null, cancellationToken, null, scanSession, scanOperationId);

    public Task<DuplexValidationDto> ValidateDuplexAsync(string operationId, string captureEndpointId, string renderEndpointId, string period, int durationSeconds, string mmcss, DuplexConsentDto consent, CancellationToken cancellationToken = default, string? testScenario = null, WorkerScanSessionToken? scanSession = null, Guid? scanOperationId = null) =>
        ValidateDuplexCoreAsync(operationId, captureEndpointId, renderEndpointId, period, durationSeconds, mmcss, consent, cancellationToken, testScenario, scanSession, scanOperationId);

    public Task<DuplexValidationDto> ValidateSustainedDuplexInScanAsync(string operationId, string captureEndpointId, string renderEndpointId, string period, int durationSeconds, string mmcss, WorkerScanSessionToken session, Guid scanOperationId, CancellationToken cancellationToken = default) =>
        ValidateDuplexCoreAsync(operationId, captureEndpointId, renderEndpointId, period, durationSeconds, mmcss, null, cancellationToken, null, session, scanOperationId, true);

    public Task<DuplexValidationDto> ValidateDuplexV2InScanAsync(string operationId, WorkerScanSessionToken session, Guid scanOperationId, WorkerScanV2Route route, string captureEndpointId, string renderEndpointId, string period, int durationSeconds, string mmcss, bool sustained = false, CancellationToken cancellationToken = default, string? testScenario = null)
    {
        var command = sustained ? "ValidateSustainedDuplexV2" : "ValidateDuplexV2";
        if (session.ContractVersion != 3 || session.ScanSessionId == Guid.Empty || scanOperationId == Guid.Empty || !Guid.TryParse(operationId, out var operation) || operation != scanOperationId || session.RouteFingerprint != WorkerScanPlanV2Fingerprint.Route(captureEndpointId, renderEndpointId, route) || !_capabilities.Contains(sustained ? "RealExclusiveSustainedDuplexValidationV2" : "RealExclusiveDuplexValidationV2", StringComparer.Ordinal) || period is not ("MINIMUM" or "DEFAULT") || mmcss is not ("OFF" or "PRO_AUDIO") || (sustained ? durationSeconds is not (60 or 300 or 600) : durationSeconds is < 1 or > 10)) throw new ArgumentException("V2 operation must match its authorized route, operation identity, and bounded stage.");
        if (testScenario is not null && (!_capabilities.Contains("TestInjectedDuplexBackend", StringComparer.Ordinal) || testScenario is not ("CLEAN" or "BENIGN_INTERMEDIATE_WAIT" or "SERVICE_FAILURE" or "WATCHDOG" or "LONG_EVENT_INTERVAL" or "PACKET_DRAIN_LIMIT" or "CAPTURE_FAIL" or "RENDER_FAIL" or "INSUFFICIENT_FRAMES" or "NO_OVERLAP" or "CANCELLED" or "CLEANUP_UNKNOWN" or "WAIT_FOR_CANCEL" or "COMMAND_ERROR" or "NO_RESPONSE"))) throw new ArgumentException("Injected V2 scenarios are available only in test workers.", nameof(testScenario));
        var payload = new Dictionary<string, object?> { ["contractVersion"] = 2, ["operationId"] = operation, ["captureEndpointId"] = captureEndpointId, ["renderEndpointId"] = renderEndpointId, ["mode"] = route.Mode,
            ["capture"] = new { sampleRateHz = route.Capture.SampleRateHz, channels = route.Capture.Channels, encoding = route.Capture.Encoding, channelMask = route.Capture.ChannelMask, selectedIndicesZeroBased = route.Capture.SelectedIndicesZeroBased },
            ["render"] = new { sampleRateHz = route.Render.SampleRateHz, channels = route.Render.Channels, encoding = route.Render.Encoding, channelMask = route.Render.ChannelMask, selectedIndicesZeroBased = route.Render.SelectedIndicesZeroBased },
            ["period"] = period, ["durationSeconds"] = durationSeconds, ["mmcss"] = mmcss, ["scanSessionId"] = session.ScanSessionId, ["scanOperationId"] = scanOperationId };
        if (testScenario is not null) payload["testScenario"] = testScenario;
        return ValidateDuplexV2CoreAsync(command, operation, session, route, captureEndpointId, renderEndpointId, period, durationSeconds, mmcss, payload, cancellationToken);
    }

    public async Task<JsonElement> ValidateCustomDuplexV2InScanAsync(WorkerScanSessionToken session, Guid operationId,
        WorkerScanV2Route route, string captureEndpointId, string renderEndpointId, WorkerScanV2ContractV4 plan,
        int candidateIndex, CancellationToken cancellationToken = default, string? testScenario = null)
    {
        if (session.ContractVersion != 4 || session.ScanSessionId == Guid.Empty || operationId == Guid.Empty ||
            !_capabilities.Contains("V2CustomTransportContractV4", StringComparer.Ordinal))
            throw new ArgumentException("An explicitly registered v4 custom transport session is required.");
        if (testScenario is not null && (!_capabilities.Contains("TestInjectedDuplexBackend", StringComparer.Ordinal) ||
            testScenario is not ("CLEAN" or "BENIGN_INTERMEDIATE_WAIT" or "SERVICE_FAILURE" or "WATCHDOG" or "LONG_EVENT_INTERVAL" or "PACKET_DRAIN_LIMIT" or "CAPTURE_FAIL" or "RENDER_FAIL" or "INSUFFICIENT_FRAMES" or "NO_OVERLAP" or "CANCELLED" or "CLEANUP_UNKNOWN" or "WAIT_FOR_CANCEL" or "COMMAND_ERROR" or "NO_RESPONSE")))
            throw new ArgumentException("Injected v4 Duplex scenarios are available only in test workers.", nameof(testScenario));
        var routeFingerprint = WorkerScanPlanV2Fingerprint.Route(captureEndpointId, renderEndpointId, route);
        var candidate = plan.Candidates.SingleOrDefault(c => c.Index == candidateIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(candidateIndex));
        if (candidate.RejectionReason.Length != 0 || plan.MmcssModes.Count != 1 || plan.MmcssModes[0] != "OFF")
            throw new ArgumentException("V4 Duplex requires an eligible consented candidate and MMCSS OFF.", nameof(candidateIndex));
        var payload = new Dictionary<string, object?> { ["contractVersion"] = 2, ["operationId"] = operationId,
            ["scanOperationId"] = operationId, ["scanSessionId"] = session.ScanSessionId, ["captureEndpointId"] = captureEndpointId,
            ["renderEndpointId"] = renderEndpointId, ["mode"] = route.Mode,
            ["capture"] = new { sampleRateHz = route.Capture.SampleRateHz, channels = route.Capture.Channels, encoding = route.Capture.Encoding, channelMask = route.Capture.ChannelMask, selectedIndicesZeroBased = route.Capture.SelectedIndicesZeroBased },
            ["render"] = new { sampleRateHz = route.Render.SampleRateHz, channels = route.Render.Channels, encoding = route.Render.Encoding, channelMask = route.Render.ChannelMask, selectedIndicesZeroBased = route.Render.SelectedIndicesZeroBased },
            ["routeFingerprint"] = routeFingerprint, ["candidateIndex"] = candidateIndex,
            ["captureFrames"] = candidate.CaptureFrames, ["capturePeriod100ns"] = candidate.CapturePeriod100ns,
            ["renderFrames"] = candidate.RenderFrames, ["renderPeriod100ns"] = candidate.RenderPeriod100ns,
            ["period"] = "CUSTOM_V2", ["durationSeconds"] = 5, ["mmcss"] = "OFF" };
        if (testScenario is not null) payload["testScenario"] = testScenario;
        using var response = await SendAsync("ValidateCustomDuplexV2", payload, cancellationToken).ConfigureAwait(false);
        var result = response.RootElement.GetProperty("payload");
        var disposition = result.GetProperty("duplexDisposition").GetString();
        var cleanup = result.GetProperty("cleanup").GetString();
        var actualCaptureFrames = result.GetProperty("actualCaptureFrames");
        var actualCapturePeriod = result.GetProperty("actualCapturePeriod100ns");
        var actualRenderFrames = result.GetProperty("actualRenderFrames");
        var actualRenderPeriod = result.GetProperty("actualRenderPeriod100ns");
        var missingField = new[] { "contractVersion", "operationId", "scanSessionId", "candidateIndex", "captureEndpointId", "renderEndpointId", "routeFingerprint", "requestedCaptureFrames", "requestedCapturePeriod100ns", "requestedRenderFrames", "requestedRenderPeriod100ns", "actualCaptureFrames", "actualCapturePeriod100ns", "actualRenderFrames", "actualRenderPeriod100ns", "duplexDisposition", "reasonCode", "cleanup", "provenance" }.FirstOrDefault(name => !result.TryGetProperty(name, out _));
        if (missingField is not null) throw new InvalidDataException($"V4 Duplex evidence is missing required field {missingField}.");
        if (result.GetProperty("contractVersion").GetInt32() != 4 || result.GetProperty("operationId").GetGuid() != operationId ||
            result.GetProperty("scanSessionId").GetGuid() != session.ScanSessionId || result.GetProperty("candidateIndex").GetInt32() != candidateIndex ||
            result.GetProperty("captureEndpointId").GetString() != captureEndpointId || result.GetProperty("renderEndpointId").GetString() != renderEndpointId || result.GetProperty("routeFingerprint").GetString() != routeFingerprint ||
            result.GetProperty("requestedCaptureFrames").GetInt32() != candidate.CaptureFrames ||
            result.GetProperty("requestedCapturePeriod100ns").GetInt64() != candidate.CapturePeriod100ns ||
            result.GetProperty("requestedRenderFrames").GetInt32() != candidate.RenderFrames ||
            result.GetProperty("requestedRenderPeriod100ns").GetInt64() != candidate.RenderPeriod100ns ||
            (actualCaptureFrames.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)) ||
            (actualCapturePeriod.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)) ||
            (actualRenderFrames.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)) ||
            (actualRenderPeriod.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)) ||
            disposition is not ("CLEAN" or "FAILED" or "UNKNOWN") || string.IsNullOrWhiteSpace(result.GetProperty("reasonCode").GetString()) ||
            cleanup is not ("CAPTURE:RELEASE_ATTEMPTED;RENDER:RELEASE_ATTEMPTED" or "UNKNOWN") ||
            result.GetProperty("provenance").GetString() is not ("REAL_WASAPI_V4_DUPLEX" or "TEST_FAKE_WASAPI_V4_DUPLEX") ||
            disposition == "CLEAN" && (actualCaptureFrames.GetInt32() != candidate.CaptureFrames || actualCapturePeriod.GetInt64() != candidate.CapturePeriod100ns || actualRenderFrames.GetInt32() != candidate.RenderFrames || actualRenderPeriod.GetInt64() != candidate.RenderPeriod100ns || cleanup != "CAPTURE:RELEASE_ATTEMPTED;RENDER:RELEASE_ATTEMPTED"))
            throw new InvalidDataException("V4 Duplex evidence omits required identity or per-side geometry.");
        return result.Clone();
    }

    private async Task<DuplexValidationDto> ValidateDuplexV2CoreAsync(string command, Guid operation, WorkerScanSessionToken session, WorkerScanV2Route route, string captureId, string renderId, string period, int duration, string mmcss, object payload, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(command, payload, cancellationToken).ConfigureAwait(false);
        var body = response.RootElement.GetProperty("payload"); var report = body.GetProperty("report"); var evidenceRoute = report.GetProperty("route");
        var expectedType = command == "ValidateSustainedDuplexV2" ? "wasapi-sustained-duplex-validation-v2" : "wasapi-duplex-validation-v2";
        var expectedProvenance = _capabilities.Contains("TestInjectedDuplexBackend", StringComparer.Ordinal) ? command == "ValidateSustainedDuplexV2" ? "TEST_FAKE_WASAPI_SUSTAINED_DUPLEX_V2" : "TEST_FAKE_WASAPI_DUPLEX_V2" : command == "ValidateSustainedDuplexV2" ? "REAL_WASAPI_SUSTAINED_DUPLEX_V2" : "REAL_WASAPI_DUPLEX_V2";
        static bool RouteSideMatches(JsonElement side, string endpointId, WorkerScanV2Side expected) => side.ValueKind == JsonValueKind.Object && side.GetProperty("endpointId").GetString() == endpointId && side.GetProperty("sampleRateHz").GetInt32() == expected.SampleRateHz && side.GetProperty("channels").GetInt32() == expected.Channels && side.GetProperty("encoding").GetString() == expected.Encoding && side.GetProperty("channelMask").GetUInt32() == expected.ChannelMask && side.GetProperty("selectedIndicesZeroBased").EnumerateArray().Select(i => i.GetInt32()).SequenceEqual(expected.SelectedIndicesZeroBased);
        if (!Guid.TryParse(body.GetProperty("operationId").GetString(), out var returned) || returned != operation || body.GetProperty("scanSessionId").GetGuid() != session.ScanSessionId || body.GetProperty("captureEndpointId").GetString() != captureId || body.GetProperty("renderEndpointId").GetString() != renderId || !Guid.TryParse(report.GetProperty("operationId").GetString(), out var reportOperation) || reportOperation != operation || report.GetProperty("reportType").GetString() != expectedType || report.GetProperty("provenance").GetString() != expectedProvenance || evidenceRoute.GetProperty("fingerprint").GetString() != session.RouteFingerprint || evidenceRoute.GetProperty("mode").GetString() != route.Mode || evidenceRoute.GetProperty("renderPolicy").GetString() != "SILENCE_ONLY" || !RouteSideMatches(evidenceRoute.GetProperty("capture"), captureId, route.Capture) || !RouteSideMatches(evidenceRoute.GetProperty("render"), renderId, route.Render) || report.GetProperty("requested").GetProperty("captureEndpointId").GetString() != captureId || report.GetProperty("requested").GetProperty("renderEndpointId").GetString() != renderId || report.GetProperty("requested").GetProperty("period").GetString() != period || report.GetProperty("requested").GetProperty("durationSeconds").GetInt32() != duration || report.GetProperty("requested").GetProperty("mmcss").GetString() != mmcss) throw new InvalidDataException("V2 Duplex response differs from its authorized operation or route.");
        return new(operation.ToString("D"), captureId, renderId, report.Clone());
    }

    private async Task<DuplexValidationDto> ValidateDuplexCoreAsync(string operationId, string captureEndpointId, string renderEndpointId, string period, int durationSeconds, string mmcss, DuplexConsentDto? consent, CancellationToken cancellationToken, string? testScenario, WorkerScanSessionToken? scanSession, Guid? scanOperationId, bool sustained = false)
    {
        var command = sustained ? "ValidateSustainedDuplex" : "ValidateDuplex";
        if (!_capabilities.Contains(sustained ? "RealExclusiveSustainedDuplexValidation" : "RealExclusiveDuplexValidation", StringComparer.Ordinal) && !(sustained && _capabilities.Contains("TestInjectedDuplexBackend", StringComparer.Ordinal))) throw new WorkerCommandException(command, "NOT_SUPPORTED", "Worker does not advertise the requested real Duplex validation.");
        if (!Guid.TryParse(operationId, out var operation) || operation == Guid.Empty || string.IsNullOrWhiteSpace(captureEndpointId) || string.IsNullOrWhiteSpace(renderEndpointId) || captureEndpointId == renderEndpointId || captureEndpointId.Length > 32768 || renderEndpointId.Length > 32768 || !IsDuplexPeriod(period) || durationSeconds is < 1 || (!sustained && durationSeconds > 10) || (sustained && durationSeconds is not (60 or 300 or 600)) || mmcss is not ("OFF" or "PRO_AUDIO")) throw new ArgumentException("Duplex requires an exact distinct endpoint pair and bounded immutable settings.");
        if (scanSession is null && (consent is null || !consent.Accepted || consent.ConfirmationId == Guid.Empty || consent.RequestFingerprint != DuplexConsent.Fingerprint(captureEndpointId, renderEndpointId, period, durationSeconds, mmcss)) || scanSession is not null && (scanSession.ScanSessionId == Guid.Empty || scanOperationId != operation)) throw new ArgumentException("Fresh manual Duplex consent or a matching Worker-authorized scan operation is required.");
        if (testScenario is not null && (!_capabilities.Contains("TestInjectedDuplexBackend", StringComparer.Ordinal) || testScenario is not ("CLEAN" or "BENIGN_INTERMEDIATE_WAIT" or "SERVICE_FAILURE" or "WATCHDOG" or "LONG_EVENT_INTERVAL" or "PACKET_DRAIN_LIMIT" or "CAPTURE_FAIL" or "RENDER_FAIL" or "INSUFFICIENT_FRAMES" or "NO_OVERLAP" or "CANCELLED" or "CLEANUP_UNKNOWN" or "WAIT_FOR_CANCEL" or "DELAY_RESPONSE" or "NO_RESPONSE"))) throw new ArgumentException("Injected Duplex fixtures are available only in the test worker.", nameof(testScenario));
        var payload = new Dictionary<string, object?> { ["operationId"] = operation, ["captureEndpointId"] = captureEndpointId, ["renderEndpointId"] = renderEndpointId, ["sampleRate"] = 48000, ["channels"] = 2, ["encoding"] = "PCM16", ["period"] = period, ["durationSeconds"] = durationSeconds, ["mmcss"] = mmcss };
        if (scanSession is null) payload["consentReceipt"] = new { accepted = true, confirmationId = consent!.ConfirmationId, requestFingerprint = consent.RequestFingerprint };
        else { payload["scanSessionId"] = scanSession.ScanSessionId; payload["scanOperationId"] = scanOperationId; }
        if (testScenario is not null) payload["testScenario"] = testScenario;
        using var response = await SendAsync(command, payload, cancellationToken).ConfigureAwait(false);
        var body = response.RootElement.GetProperty("payload");
        if (!Guid.TryParse(body.GetProperty("operationId").GetString(), out var returned) || returned != operation || body.GetProperty("captureEndpointId").GetString() != captureEndpointId || body.GetProperty("renderEndpointId").GetString() != renderEndpointId) throw new InvalidDataException("Duplex response identity differs from the consented operation.");
        var report = body.GetProperty("report");
        var provenance = _capabilities.Contains("TestInjectedDuplexBackend", StringComparer.Ordinal) ? (sustained ? "TEST_FAKE_WASAPI_SUSTAINED_DUPLEX" : "TEST_FAKE_WASAPI_DUPLEX") : (sustained ? "REAL_WASAPI_SUSTAINED_DUPLEX" : "REAL_WASAPI_DUPLEX");
        var requested = report.GetProperty("requested");
        if (report.GetProperty("schemaVersion").GetInt32() != (sustained ? 1 : 2) || report.GetProperty("reportType").GetString() != (sustained ? "wasapi-sustained-duplex-validation" : "wasapi-duplex-validation") || report.GetProperty("provenance").GetString() != provenance || !Guid.TryParse(report.GetProperty("reportId").GetString(), out _) || !Guid.TryParse(report.GetProperty("operationId").GetString(), out var reportedOperation) || reportedOperation != operation || requested.GetProperty("captureEndpointId").GetString() != captureEndpointId || requested.GetProperty("renderEndpointId").GetString() != renderEndpointId || requested.GetProperty("period").GetString() != period || requested.GetProperty("mmcss").GetString() != mmcss || requested.GetProperty("durationSeconds").GetInt32() != durationSeconds || requested.GetProperty("sampleRateHz").GetInt32() != 48000 || requested.GetProperty("channels").GetInt32() != 2 || requested.GetProperty("encoding").GetString() != "PCM16" || report.GetProperty("capture").GetProperty("endpointId").GetString() != captureEndpointId || report.GetProperty("capture").GetProperty("direction").GetString() != "CAPTURE" || report.GetProperty("render").GetProperty("endpointId").GetString() != renderEndpointId || report.GetProperty("render").GetProperty("direction").GetString() != "RENDER" || report.GetProperty("physicalRtl").GetString() != "NOT_MEASURED" || report.GetProperty("audioHostEligibility").GetString() != "NOT_CERTIFIED") throw new InvalidDataException("Duplex report schema, settings, provenance or endpoint identity differs from the request.");
        foreach (var sideName in new[] { "capture", "render" })
        {
            var side = report.GetProperty(sideName);
            foreach (var field in new[] { "framesProcessed", "expectedFrames", "events", "packets", "discontinuities", "timestampErrors", "serviceFaults", "packetDrainLimitHits", "intermediateWaitTimeouts", "longEventIntervals" }) if (!side.GetProperty(field).TryGetInt64(out var value) || value < 0) throw new InvalidDataException($"Invalid Duplex counter {sideName}.{field}.");
            foreach (var field in new[] { "devicePositionAnomalies", "unavailableCaptureBuffers", "silentBuffersServiced" }) { var item = side.GetProperty(field); if (item.ValueKind != System.Text.Json.JsonValueKind.Null && (!item.TryGetInt64(out var value) || value < 0)) throw new InvalidDataException($"Invalid Duplex counter {sideName}.{field}."); }
            if (!side.GetProperty("eventIntervalsObserved").TryGetInt64(out var intervals) || intervals < 0) throw new InvalidDataException($"Invalid Duplex event interval count {sideName}.");
            foreach (var field in new[] { "frameCoveragePercent", "eventIntervalMedianMilliseconds", "eventIntervalP95Milliseconds", "eventIntervalP99Milliseconds" }) { var item = side.GetProperty(field); if (item.ValueKind != System.Text.Json.JsonValueKind.Null && (item.ValueKind != System.Text.Json.JsonValueKind.Number || !double.IsFinite(item.GetDouble()) || item.GetDouble() < 0)) throw new InvalidDataException($"Invalid Duplex timing/coverage {sideName}.{field}."); }
            if (side.TryGetProperty("eventIntervalMaximumMilliseconds", out var maximum) && maximum.ValueKind != System.Text.Json.JsonValueKind.Null && (maximum.ValueKind != System.Text.Json.JsonValueKind.Number || !double.IsFinite(maximum.GetDouble()) || maximum.GetDouble() < 0)) throw new InvalidDataException($"Invalid Duplex timing/coverage {sideName}.eventIntervalMaximumMilliseconds.");
            var elapsed = side.GetProperty("elapsedMilliseconds"); if (elapsed.ValueKind != System.Text.Json.JsonValueKind.Number || !double.IsFinite(elapsed.GetDouble()) || elapsed.GetDouble() < 0) throw new InvalidDataException($"Invalid Duplex timing {sideName}.");
            var expectedFrames = side.GetProperty("expectedFrames").GetInt64(); var frames = side.GetProperty("framesProcessed").GetInt64();
            if (expectedFrames == 0 && frames > 0 || expectedFrames > 0 && frames > expectedFrames + 48000L * durationSeconds) throw new InvalidDataException($"Duplex frame counts are inconsistent: {sideName}.");
            foreach (var field in new[] { "activationHresult", "devicePeriodHresult", "isFormatSupportedHresult", "initializeHresult", "startHresult", "stopHresult", "getBufferSizeHresult", "setEventHandleHresult", "errorHresult" }) if (side.GetProperty(field).ValueKind is not (System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Null)) throw new InvalidDataException($"Invalid Duplex HRESULT {sideName}.{field}.");
            if (side.GetProperty("quality").GetString() is not ("CLEAN" or "ANOMALIES_PRESENT" or "SEVERE_ANOMALIES" or "UNKNOWN") || side.GetProperty("cleanup").GetString() is not ("NOT_ATTEMPTED" or "RELEASE_ATTEMPTED" or "UNKNOWN" or "FAILED")) throw new InvalidDataException($"Invalid Duplex state {sideName}.");
            if (side.GetProperty("peerStopped").ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) throw new InvalidDataException($"Invalid Duplex peer-stop evidence {sideName}.");
            foreach (var field in new[] { "watchdogTimedOut", "deviceInvalidated", "durationReached", "cancelled" }) if (side.GetProperty(field).ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) throw new InvalidDataException($"Invalid Duplex lifecycle evidence {sideName}.{field}.");
        }
        var duplex = report.GetProperty("duplex");
        var duplexStatus = duplex.GetProperty("status").GetString();
        if (duplexStatus is not ("DUPLEX_CLEAN" or "DUPLEX_WARNINGS" or "FAILED" or "INCOMPLETE" or "CANCELLED") || duplex.GetProperty("peerStopRequested").ValueKind != System.Text.Json.JsonValueKind.True && duplex.GetProperty("peerStopRequested").ValueKind != System.Text.Json.JsonValueKind.False) throw new InvalidDataException("Unknown Duplex lifecycle result.");
        var assessment = DeviceConfigurationEvaluator.Evaluate(period, report, captureEndpointId, renderEndpointId, mmcss);
        if (provenance == "REAL_WASAPI_DUPLEX" && duplexStatus == "DUPLEX_CLEAN" && assessment.Classification != "DUPLEX_CLEAN_SHORT_RUN") throw new InvalidDataException("DUPLEX_CLEAN conflicts with incomplete or contradictory servicing, HRESULT, overlap, or cleanup evidence.");
        return new(operation.ToString("D"), captureEndpointId, renderEndpointId, report.Clone());
    }

    private static bool IsDuplexPeriod(string value) => value is "MINIMUM" or "DEFAULT" || value.StartsWith("CUSTOM:", StringComparison.Ordinal) && value.Split(':') is [_, var duration, var frames] && long.TryParse(duration, out var period100ns) && uint.TryParse(frames, out var requestedFrames) && period100ns > 0 && requestedFrames > 0 && requestedFrames <= 48_000 && ((period100ns >= ((long)requestedFrames * 10_000_000 + 24_000) / 48_000 ? period100ns - ((long)requestedFrames * 10_000_000 + 24_000) / 48_000 : ((long)requestedFrames * 10_000_000 + 24_000) / 48_000 - period100ns) <= 1);

    public async Task CancelDuplexValidationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(operationId, out var id) || id == Guid.Empty) throw new ArgumentException("A valid Duplex operation ID is required.", nameof(operationId));
        using var response = await SendAsync("CancelDuplexValidation", new { operationId = id }, cancellationToken).ConfigureAwait(false);
        if (response.RootElement.GetProperty("payload").GetProperty("completion").GetString() != "PENDING") throw new InvalidDataException("Duplex cancellation was not acknowledged as pending.");
    }

    private async Task<RealFormatMatrixDto> SendRealMatrixAsync(string command, object payload, string endpointId, string direction, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(command, payload, cancellationToken).ConfigureAwait(false);
        var body = response.RootElement.GetProperty("payload");
        var expectedProvenance = _capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal)
            ? "TEST_FAKE_FORMAT_BACKEND"
            : command == "QueryFormatMatrix" ? "REAL_WASAPI_QUERY" : "REAL_WASAPI_INITIALIZE_ONLY";
        if (body.GetProperty("provenance").GetString() != expectedProvenance || body.GetProperty("endpointIdentityReference").GetString() != endpointId || body.GetProperty("direction").GetString() != direction)
            throw new InvalidDataException("Real format response identity or provenance does not match the request.");
        var report = body.GetProperty("candidateReport");
        var candidates = new List<RealFormatCandidateDto>();
        foreach (var item in report.GetProperty("candidates").EnumerateArray())
        {
            var requested = item.GetProperty("requested");
            string? Text(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            long? Long(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : null;
            uint? UInt(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var result) ? result : null;
            bool? Bool(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
            var status = item.GetProperty("status").GetString() ?? "INCOMPLETE";
            var support = item.GetProperty("isFormatSupportStatus").GetString() ?? "INCOMPLETE";
            var initialize = item.GetProperty("initializeStatus").GetString() ?? "NOT_TESTED";
            var cleanup = item.GetProperty("cleanupStatus").GetString() ?? "INCOMPLETE";
            if (status is not ("SUPPORTED_REPORTED" or "INITIALIZED" or "READY" or "UNSUPPORTED" or "FAILED" or "NOT_TESTED" or "INCOMPLETE") || support is not ("SUPPORTED_REPORTED" or "UNSUPPORTED" or "FAILED" or "INCOMPLETE") || initialize is not ("INITIALIZED" or "READY" or "UNSUPPORTED" or "FAILED" or "NOT_TESTED" or "INCOMPLETE")) throw new InvalidDataException("Real format response contains an unknown required status enum.");
            if (!item.TryGetProperty("startCalled", out var startCalled) || startCalled.ValueKind != JsonValueKind.False || !item.TryGetProperty("audioTransferred", out var audioTransferred) || audioTransferred.ValueKind != JsonValueKind.False) throw new InvalidDataException("Format diagnostics must explicitly report that Start and PCM transfer were not called.");
            if (requested.GetProperty("sampleRateHz").GetInt32() is not (44100 or 48000) || requested.GetProperty("channels").GetInt32() is < 1 or > 64 || Text(requested,"format") is not ("PCM16" or "PCM24_IN_32" or "FLOAT32") || Text(requested,"periodMode") is not ("default" or "minimum") || Text(requested,"channelMaskEvidence") is not ("STANDARD_MONO_STEREO_MASK" or "UNKNOWN_NOT_ASSIGNED")) throw new InvalidDataException("Real format response contains an unrepresentable format candidate.");
            var alignment = item.TryGetProperty("alignmentRetry", out var retry) ? retry : default;
            var candidate = new RealFormatCandidateDto(requested.GetProperty("format").GetString()!, requested.GetProperty("sampleRateHz").GetInt32(), requested.GetProperty("channels").GetInt32(), requested.GetProperty("periodMode").GetString()!, Text(requested, "channelMaskEvidence") ?? "UNKNOWN", status, support, Text(item, "queryClientActivationHr"), Text(item, "isFormatSupportedHr"), Text(item, "devicePeriodHr"), Long(item, "defaultDevicePeriod100ns"), Long(item, "minimumDevicePeriod100ns"), initialize, item.GetProperty("initializeAttempted").GetBoolean(), Text(item, "firstInitializeHr"), Text(alignment, "initializeHr"), Text(item, "finalInitializeHr"), UInt(item, "actualBufferFrames"), Text(item, "setEventHandleHr"), cleanup)
            {
                ContainerBits = requested.TryGetProperty("containerBits", out var containerBits) && containerBits.TryGetInt32(out var container) ? container : null,
                ValidBits = requested.TryGetProperty("validBits", out var validBits) && validBits.TryGetInt32(out var valid) ? valid : null,
                QueryCleanupStatus = Text(item, "queryCleanupStatus") ?? "INCOMPLETE",
                QueryClientReleaseAttempts = UInt(item, "queryClientReleaseAttempts") ?? 0,
                PeriodQueryHr = Text(item, "periodQueryHr"),
                PeriodAvailable = Bool(item, "periodAvailable"),
                RequestedPeriod100ns = Long(item, "requestedPeriod100ns"),
                AlignmentGetBufferSizeHr = Text(alignment, "getBufferSizeHr"),
                AlignmentFrames = UInt(alignment, "alignedFrames"),
                AlignmentActivateHr = Text(alignment, "activateHr"),
                AlignmentPeriod100ns = Long(alignment, "retryPeriod100ns"),
                ActualBufferSizeHr = Text(item, "actualBufferSizeHr"),
                ActualPeriod100ns = Long(item, "actualPeriod100ns"),
                EventCreateHr = Text(item, "eventCreateHr"),
                EventHandleCloseHr = Text(item, "eventHandleCloseHr"),
                EventHandleReady = Bool(item, "eventHandleReady"),
                ClientReleaseAttempts = UInt(item, "clientReleaseAttempts") ?? 0
            };
            candidates.Add(candidate);
        }
        if (candidates.Count is 0 or > 768) throw new InvalidDataException("Real format response candidate count is outside the bounded schema.");
        var timestamp = body.GetProperty("timestampUtc").GetString() ?? throw new InvalidDataException("Real format response timestamp is missing.");
        return new(expectedProvenance, timestamp, endpointId, direction, body.GetProperty("reportLevel").GetString() ?? "UNKNOWN", candidates);
    }

    public async Task<WorkerSessionAcceptedDto> StartSessionAsync(WorkerSessionSettingsDto s, ConsentReceiptDto c, CancellationToken cancellationToken = default)
    {
        var command = s.Direction == "CAPTURE" ? "StartCaptureMeter" : "StartRenderChannelTest";
        using var response = await SendAsync(command, new
        {
            endpointId = s.EndpointId, direction = s.Direction, formatId = s.FormatId, sampleRate = s.SampleRate,
            encoding = s.Encoding, channelCount = s.ChannelCount, topologyId = s.TopologyId, durationSeconds = s.DurationSeconds,
            period = s.Period, mmcss = s.Mmcss, renderChannel = s.RenderChannel, scenario = s.Scenario,
            consentReceipt = new { accepted = c.Accepted, confirmationId = c.ConfirmationId, requestFingerprint = c.RequestFingerprint }
        }, cancellationToken, expectedSettings: s).ConfigureAwait(false);
        if (!response.RootElement.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String) throw new InvalidDataException("Accepted session response has no sessionId.");
        var acceptedSettings = response.RootElement.GetProperty("payload").GetProperty("settings").Deserialize<WorkerSessionSettingsDto>(JsonOptions) ?? throw new InvalidDataException("Accepted session response has no immutable settings.");
        if (acceptedSettings != s) throw new InvalidDataException("Worker accepted settings differ from the confirmed request.");
        var operationState = response.RootElement.GetProperty("payload").GetProperty("operationState").GetString() ?? throw new InvalidDataException("Accepted session response has no operation state.");
        return new(id.GetGuid(), response.RootElement.GetProperty("correlationId").GetGuid(), acceptedSettings, operationState);
    }

    public async Task StopSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var _ = await SendAsync("StopSession", new { sessionId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _pipe is null || !_pipe.IsConnected || _process is null) { EndTransport("SHUTDOWN", "Worker is disconnected.", QueryExitCode(_process)); KillOwnedWorker(); return; }
        var shutdownRejected = false;
        try
        {
            SetState(WorkerConnectionState.Stopping, "Запрошено штатное завершение worker.");
            _shutdownRequested = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await SendAsync("Shutdown", new { mode = "IDLE" }, timeout.Token, allowStopping: true).ConfigureAwait(false);
            if (await Task.Run(() => WaitForSingleObject(_process, 2000), timeout.Token).ConfigureAwait(false) != 0)
            {
                TerminateJobObject(_job!, 2);
                SetState(WorkerConnectionState.Failed, "Worker превысил срок завершения; процесс остановлен принудительно.");
            }
            else SetState(WorkerConnectionState.Disconnected, "Worker штатно завершён.", QueryExitCode(_process));
        }
        catch (WorkerCommandException ex) when (ex.ErrorCode == "BUSY")
        {
            _shutdownRequested = false;
            shutdownRejected = true;
            SetState(WorkerConnectionState.ConnectedFake, "Worker занят fake-сессией; сначала завершите её.");
            RaiseSafely(TransportFault, new WorkerTransportFaultDto("SHUTDOWN_REJECTED", ex.Message, QueryExitCode(_process)));
        }
        catch (Exception ex)
        {
            if (_job is { IsInvalid: false, IsClosed: false }) TerminateJobObject(_job, 2);
            SetState(WorkerConnectionState.Failed, $"Shutdown не подтверждён: {ex.GetType().Name}: {ex.Message}");
            RaiseSafely(TransportFault, new WorkerTransportFaultDto("SHUTDOWN", ex.Message, QueryExitCode(_process)));
        }
        finally { if (!shutdownRejected) { _lifetime.Cancel(); KillOwnedWorker(); } }
    }

    private async Task<JsonDocument> SendAsync(string command, object payload, CancellationToken cancellationToken, bool allowStopping = false, WorkerSessionSettingsDto? expectedSettings = null, TimeSpan? responseTimeoutOverride = null)
    {
        if ((_state.State != WorkerConnectionState.ConnectedFake && !(allowStopping && _state.State == WorkerConnectionState.Stopping)) || _pipe is null) throw new InvalidOperationException("Worker is not connected.");
        var traceTimer = Stopwatch.StartNew();
        void Trace(string stage) => System.Diagnostics.Trace.TraceInformation("WorkerIpc stage={0} command={1} elapsedMs={2} workerState={3}", stage, command, traceTimer.ElapsedMilliseconds, _state.State);
        var id = Guid.NewGuid();
        var traceDiscovery = command == "ListWindowsEndpoints";
        if (traceDiscovery) EndpointDiscoveryQaTrace.Write("ipc-request-created", id, _state.State.ToString(), QueryExitCode(_process));
        var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(command, completion, command is "StartCaptureMeter" or "StartRenderChannelTest", expectedSettings);
        lock (_pendingSync)
        {
            if (_pending.Count >= 32) throw new InvalidOperationException("Worker request limit reached.");
            if (pending.IsStart && (_pending.Values.Any(p => p.IsUnresolvedStart) || _sessions.Values.Any(s => !s.Terminal))) throw new InvalidOperationException("A Start outcome or session is already unresolved on this worker.");
            if (!_pending.TryAdd(id, pending)) throw new InvalidOperationException("Duplicate request UUID.");
        }
        var acquired = false;
        try
        {
            Trace("waiting-write-lock");
            using (var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token))
            {
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                try { await _writeGate.WaitAsync(writeTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    Trace("write-lock-timeout");
                    throw new TimeoutException($"Timed out waiting to send {command}.");
                }
            }
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!pending.MarkSent()) throw new InvalidOperationException("Request was cancelled before write.");
            Trace("request-write-start");
            try
            {
                var request = new { protocolVersion = new { major = ProtocolMajor, minor = ProtocolMinor }, schemaVersion = SchemaVersion, kind = "request", requestId = id, correlationId = (Guid?)null, sessionId = (Guid?)null, eventSequence = 0L, commandOrEvent = command, payload };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await IpcFrameCodec.WriteJsonAsync(_pipe, request, timeout.Token).ConfigureAwait(false);
                RequestWrittenForTest?.Invoke(id);
                if (traceDiscovery) EndpointDiscoveryQaTrace.Write("ipc-request-sent", id, _state.State.ToString(), QueryExitCode(_process));
                Trace("request-sent");
            }
            catch (Exception ex)
            {
                if (traceDiscovery) EndpointDiscoveryQaTrace.Write("ipc-write-failure", id, _state.State.ToString(), QueryExitCode(_process), ex.GetType().Name, ex is Win32Exception writeError ? writeError.NativeErrorCode.ToString() : null);
                EndTransport("REQUEST_WRITE", ex.Message, QueryExitCode(_process));
                throw new IOException("Worker request write failed; transport outcome is unknown.", ex);
            }
            finally { _writeGate.Release(); acquired = false; }
            var responseTimeout = responseTimeoutOverride ?? ResponseTimeoutOverrideForTest ?? GetResponseTimeout(command);
            try { var result = await completion.Task.WaitAsync(responseTimeout, cancellationToken).ConfigureAwait(false); Trace("response-received"); return result; }
            catch (TimeoutException) { if (traceDiscovery) EndpointDiscoveryQaTrace.Write("ipc-response-timeout", id, _state.State.ToString(), QueryExitCode(_process)); return AbandonTimedOutRequest(); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                return AbandonTimedOutRequest();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Trace("cancelled");
                if (traceDiscovery) EndpointDiscoveryQaTrace.Write("ipc-cancelled", id, _state.State.ToString(), QueryExitCode(_process));
                if (!pending.TryAbandon(out var mayHaveBeenSent)) return await completion.Task.ConfigureAwait(false);
                if (!mayHaveBeenSent) RemovePending(id, pending);
                completion.TrySetCanceled(cancellationToken);
                if (mayHaveBeenSent) RaiseSafely(TransportFault, new WorkerTransportFaultDto("REQUEST_CANCELLED", $"Caller cancelled {command} after its frame may have been sent; outcome remains unknown.", CorrelationId: id, Command: command));
                if (mayHaveBeenSent && IsSupervisedNativeOperation(command)) EndTransport(command == "CloseScanSession" ? "SESSION_CLOSE_CANCELLED" : "NATIVE_OPERATION_CANCELLED", "A real native operation may already be running; worker and child processes were terminated, and operation/cleanup outcome is unknown.", QueryExitCode(_process));
                throw;
            }
            JsonDocument AbandonTimedOutRequest()
            {
                Trace("response-timeout");
                if (!pending.TryAbandon(out _)) return completion.Task.GetAwaiter().GetResult();
                completion.TrySetCanceled();
                RaiseSafely(TransportFault, new WorkerTransportFaultDto("RESPONSE_TIMEOUT", $"No response arrived for {command}; command outcome remains unknown.", CorrelationId: id, Command: command));
                if (IsSupervisedNativeOperation(command)) EndTransport(command.Contains("Duplex", StringComparison.Ordinal) ? "DUPLEX_SUPERVISION_TIMEOUT" : command is "InitializeExclusiveCustomPeriod" or "InitializeCustomPeriodV2" ? "PERIOD_DISCOVERY_SUPERVISION_TIMEOUT" : command == "CloseScanSession" ? "SESSION_CLOSE_SUPERVISION_TIMEOUT" : "NATIVE_DRIVER_TIMEOUT", "Real native operation exceeded the bounded GUI deadline; worker and child processes were terminated, and operation/cleanup outcome is unknown.", QueryExitCode(_process));
                throw new TimeoutException($"No response arrived for {command} within the response deadline.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !pending.WasSent)
        {
            if (pending.TryAbandon(out _))
            {
                RemovePending(id, pending);
                completion.TrySetCanceled(cancellationToken);
            }
            throw;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (pending.TryAbandon(out var mayHaveBeenSent) && !mayHaveBeenSent) RemovePending(id, pending);
            throw new IOException($"Worker transport closed while waiting for {command}; the command outcome is unknown.");
        }
        finally { if (acquired) _writeGate.Release(); Trace("send-complete"); }
    }

    private static bool IsSupervisedNativeOperation(string command) => command is "QueryFormatMatrix" or "ValidateFormatMatrix" or "ValidateStream" or "ValidateDuplex" or "ValidateSustainedDuplex" or "ValidateDuplexV2" or "ValidateSustainedDuplexV2" or "InitializeExclusiveCustomPeriod" or "InitializeCustomPeriodV2" or "ValidateCustomDuplexV2" or "CloseScanSession";

    private static TimeSpan GetResponseTimeout(string command) => command switch
    {
        "ValidateStream" => TimeSpan.FromSeconds(45),
        "ValidateDuplex" => DuplexResponseTimeout,
        "ValidateSustainedDuplex" => SustainedDuplexResponseTimeout,
        "ValidateDuplexV2" => DuplexResponseTimeout,
        "ValidateCustomDuplexV2" => DuplexResponseTimeout,
        "ValidateSustainedDuplexV2" => SustainedDuplexResponseTimeout,
        "InitializeExclusiveCustomPeriod" => TimeSpan.FromSeconds(20),
        "InitializeCustomPeriodV2" => TimeSpan.FromSeconds(30),
        "QueryFormatMatrix" or "ValidateFormatMatrix" => TimeSpan.FromSeconds(30),
        "CloseScanSession" => TimeSpan.FromSeconds(SessionCloseTimeoutSeconds),
        _ => TimeSpan.FromSeconds(5)
    };

    private void RemovePending(Guid id, PendingRequest pending)
    {
        lock (_pendingSync)
            if (_pending.TryGetValue(id, out var current) && ReferenceEquals(current, pending)) _pending.TryRemove(id, out _);
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _pipe is { IsConnected: true })
            {
                var bytes = await IpcFrameCodec.ReadPayloadAsync(_pipe, cancellationToken).ConfigureAwait(false);
                using var document = IpcFrameCodec.Parse(bytes);
                var root = document.RootElement;
                var kind = root.GetProperty("kind").GetString();
                if (kind == "response") DispatchResponse(document);
                else if (kind == "event") DispatchEvent(root);
                else if (kind == "error") throw ReadProtocolError(root);
                else throw new InvalidDataException("Unexpected IPC message kind.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { EndpointDiscoveryQaTrace.Write("ipc-read-failure", state: _state.State.ToString(), exitCode: QueryExitCode(_process), exceptionType: ex.GetType().Name, errorCode: ex is Win32Exception readError ? readError.NativeErrorCode.ToString() : null); if (!_shutdownRequested) EndTransport("PIPE_READ", ex.Message, QueryExitCode(_process)); }
    }

    private void DispatchResponse(JsonDocument document)
    {
        var root = document.RootElement;
        ValidateEnvelope(root, "response");
        var correlation = root.GetProperty("correlationId").GetGuid();
        var command = root.GetProperty("commandOrEvent").GetString() ?? string.Empty;
        if (!_pending.TryGetValue(correlation, out var pending))
        {
            RaiseSafely(TransportFault, new WorkerTransportFaultDto("LATE_RESPONSE", $"Ignored response with unknown correlationId {correlation:D}."));
            return;
        }
        if (command != pending.Command) throw new InvalidDataException("Response command/correlation mismatch.");
        var status = root.GetProperty("status").GetString();
        var accepted = status == "OK";
        var sessionId = Guid.Empty;
        WorkerSessionSettingsDto? acceptedSettings = null;
        var operationState = "ACCEPTED";
        if (accepted && pending.IsStart)
        {
            if (!root.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String || !session.TryGetGuid(out sessionId)) throw new InvalidDataException("Accepted Start response has no valid sessionId.");
            var payload = root.GetProperty("payload");
            if (payload.GetProperty("acceptance").GetString() != "ACCEPTED") throw new InvalidDataException("Start response is not an acceptance.");
            acceptedSettings = payload.GetProperty("settings").Deserialize<WorkerSessionSettingsDto>(JsonOptions) ?? throw new InvalidDataException("Accepted Start response has no immutable settings.");
            if (pending.ExpectedSettings is null || acceptedSettings != pending.ExpectedSettings) throw new InvalidDataException("Worker accepted settings differ from the confirmed request.");
            if (!payload.TryGetProperty("operationState", out var state) || state.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(state.GetString())) throw new InvalidDataException("Accepted Start response has no worker operation state.");
            operationState = state.GetString()!;
            if (operationState != "ACCEPTED") throw new InvalidDataException("Worker operation state is unsupported.");
        }
        else if (!accepted)
        {
            if (status != "ERROR" || !root.TryGetProperty("errorCode", out var error) || error.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("message", out var text) || text.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("Worker rejection response lacks a valid typed error.");
        }
        bool abandoned;
        lock (_pendingSync)
        {
            if (acceptedSettings is not null && _sessions.ContainsKey(sessionId)) throw new InvalidDataException("Worker reused an accepted sessionId.");
            if (!pending.TryComplete(out abandoned))
            {
                RaiseSafely(TransportFault, new WorkerTransportFaultDto("DUPLICATE_RESPONSE", $"Ignored duplicate response for {command}."));
                return;
            }
            if (acceptedSettings is not null && !_sessions.TryAdd(sessionId, new(correlation, acceptedSettings.Direction, acceptedSettings.ChannelCount, acceptedSettings.RenderChannel)))
            {
                _pending.TryRemove(correlation, out _);
                pending.Completion.TrySetException(new InvalidDataException("Worker reused an accepted sessionId."));
                throw new InvalidDataException("Worker reused an accepted sessionId.");
            }
            _pending.TryRemove(correlation, out _);
        }
        if (command == "ListWindowsEndpoints") EndpointDiscoveryQaTrace.Write("ipc-response-received", correlation, _state.State.ToString(), QueryExitCode(_process), errorCode: accepted ? null : root.GetProperty("errorCode").GetString());
        if (!accepted)
        {
            var code = root.GetProperty("errorCode").GetString()!;
            var message = root.GetProperty("message").ValueKind == JsonValueKind.String ? root.GetProperty("message").GetString()! : "Worker rejected command.";
            if (abandoned) RaiseSafely(TransportFault, new WorkerTransportFaultDto("LATE_RESPONSE", $"Late rejection {command}/{code}: {message}", CorrelationId: correlation, Command: command, ErrorCode: code));
            else pending.Completion.TrySetException(new WorkerCommandException(command, code, message));
            return;
        }
        if (abandoned)
        {
            if (acceptedSettings is not null) RaiseSafely(SessionAccepted, new WorkerSessionAcceptedDto(sessionId, correlation, acceptedSettings, operationState));
            else RaiseSafely(TransportFault, new WorkerTransportFaultDto("LATE_RESPONSE", $"Late response received for {command}; the expired request is not reported as success.", CorrelationId: correlation, Command: command));
            return;
        }
        pending.Completion.TrySetResult(JsonDocument.Parse(root.GetRawText()));
    }

    private void DispatchEvent(JsonElement root)
    {
        ValidateEnvelope(root, "event");
        var sequence = root.GetProperty("eventSequence").GetInt64();
        var sessionId = root.GetProperty("sessionId").GetGuid();
        var correlation = root.GetProperty("correlationId").GetGuid();
        var name = root.GetProperty("commandOrEvent").GetString() ?? string.Empty;
        if (IsTerminalSession(sessionId) || _sessions.TryGetValue(sessionId, out var known) && known.Terminal)
        {
            RaiseSafely(TransportFault, new WorkerTransportFaultDto("AFTER_TERMINAL_EVENT", $"Ignored duplicate/late event {name}."));
            return;
        }
        if (sequence <= _lastEventSequence) throw new InvalidDataException("Worker eventSequence is not strictly increasing.");
        _lastEventSequence = sequence;
        if (!_sessions.TryGetValue(sessionId, out var cursor))
        {
            if (_pending.TryGetValue(correlation, out var pendingStart) && pendingStart.IsStart && pendingStart.WasSent) throw new InvalidDataException("Session event arrived before its Start acceptance response.");
            RaiseSafely(TransportFault, new WorkerTransportFaultDto("STALE_SESSION_EVENT", $"Ignored event {name} for unaccepted session {sessionId:D}."));
            return;
        }
        if (cursor.CorrelationId != correlation || sequence <= cursor.LastSequence) throw new InvalidDataException("Session event correlation/sequence is invalid.");
        if (name is not ("Preparing" or "Ready" or "Started" or "Stopping" or "Completed" or "Failed" or "Warning" or "MeterSnapshot")) throw new InvalidDataException("Unknown required session event enum.");
        var payload = root.GetProperty("payload");
        if (!payload.GetProperty("synthetic").GetBoolean()) throw new InvalidDataException("Fake worker session event lacks synthetic provenance.");
        WorkerCaptureMeterSnapshotDto? captureMeter = null;
        WorkerRenderMeterSnapshotDto? renderMeter = null;
        if (name == "MeterSnapshot")
        {
            if (!cursor.Started || cursor.Stopping) throw new InvalidDataException("MeterSnapshot arrived outside the active fake session state.");
            if (cursor.Direction == "CAPTURE") captureMeter = ParseCaptureMeter(payload, cursor);
            else if (cursor.Direction == "RENDER") renderMeter = ParseRenderMeter(payload, cursor);
            else throw new InvalidDataException("MeterSnapshot has no accepted Capture/Render direction.");
            if (captureMeter is not null && captureMeter.SnapshotSequence <= cursor.LastSnapshotSequence || renderMeter is not null && renderMeter.SnapshotSequence <= cursor.LastSnapshotSequence)
                throw new InvalidDataException("snapshotSequence must increase; coalesced gaps are allowed.");
            cursor.LastSnapshotSequence = captureMeter?.SnapshotSequence ?? renderMeter!.SnapshotSequence;
        }
        cursor.LastSequence = sequence;
        if (name == "Started") cursor.Started = true;
        if (name == "Stopping") cursor.Stopping = true;
        if (name is "Completed" or "Failed") cursor.Terminal = true;
        RaiseSafely(EventReceived, new(sequence, sessionId, correlation, name, payload.Clone(), captureMeter, renderMeter));
        if (name is "Completed" or "Failed") RememberTerminalSession(sessionId);
    }

    private static WorkerCaptureMeterSnapshotDto ParseCaptureMeter(JsonElement payload, SessionCursor cursor)
    {
        var snapshot = ReadSnapshotHeader(payload, cursor.ChannelCount);
        var channels = payload.GetProperty("channels");
        if (channels.ValueKind != JsonValueKind.Array || channels.GetArrayLength() != cursor.ChannelCount) throw new InvalidDataException("Capture snapshot channel count does not match the accepted format.");
        var result = new List<WorkerCaptureMeterChannelDto>(cursor.ChannelCount);
        var noWindow = snapshot.WindowIndex is null;
        for (var i = 0; i < cursor.ChannelCount; i++)
        {
            var item = channels[i];
            if (!TryInt(item, "index", out var index) || index != i + 1) throw new InvalidDataException("Capture snapshot channel indices must be ordered one-based stream indices.");
            var peak = ReadNullableLevel(item, "windowPeakDbfs");
            var rms = ReadNullableLevel(item, "windowRmsDbfs");
            var silent = ReadNullableBoolean(item, "silent");
            var hold = ReadNullableLevel(item, "sessionPeakHoldDbfs");
            var clips = ReadNullableNonnegativeInt(item, "clipCount");
            if (noWindow && (peak is not null || rms is not null || silent is not null || hold is not null || clips is not null) ||
                silent == true && (peak is not null || rms is not null) ||
                silent == false && (peak is null || rms is null || peak < rms) ||
                silent is null && (peak is not null || rms is not null || clips is not null))
                throw new InvalidDataException("Capture snapshot nullable levels do not match its window/silence state.");
            result.Add(new(index, peak, rms, silent, hold, clips));
        }
        return new(snapshot.Sequence, snapshot.WindowIndex, snapshot.DurationMs, snapshot.ChannelCount, result, snapshot.Quality, true, snapshot.Coalesced);
    }

    private static WorkerRenderMeterSnapshotDto ParseRenderMeter(JsonElement payload, SessionCursor cursor)
    {
        var snapshot = ReadSnapshotHeader(payload, cursor.ChannelCount);
        if (!TryInt(payload, "selectedChannelIndex", out var selected) || selected != cursor.SelectedChannel) throw new InvalidDataException("Render snapshot selected channel differs from immutable accepted settings.");
        if (!payload.TryGetProperty("intendedOutputModel", out var model) || model.ValueKind != JsonValueKind.String || model.GetString() != "SYNTHETIC_INTENDED_OUTPUT_NOT_MEASURED") throw new InvalidDataException("Render snapshot lacks its explicit synthetic intended-output marker.");
        var channels = payload.GetProperty("channels");
        if (channels.ValueKind != JsonValueKind.Array || channels.GetArrayLength() != cursor.ChannelCount) throw new InvalidDataException("Render snapshot channel count does not match the accepted format.");
        var result = new List<WorkerRenderMeterChannelDto>(cursor.ChannelCount);
        var noWindow = snapshot.WindowIndex is null;
        for (var i = 0; i < cursor.ChannelCount; i++)
        {
            var item = channels[i];
            if (!TryInt(item, "index", out var index) || index != i + 1) throw new InvalidDataException("Render snapshot channel indices must be ordered one-based stream indices.");
            var peak = ReadNullableLevel(item, "intendedPeakDbfs");
            var rms = ReadNullableLevel(item, "intendedRmsDbfs");
            var silent = ReadNullableBoolean(item, "digitalSilent");
            var hold = ReadNullableLevel(item, "sessionPeakHoldDbfs");
            var clips = ReadNullableNonnegativeInt(item, "clipCount");
            var selectedChannel = index == selected;
            if (noWindow)
            {
                if (peak is not null || rms is not null || silent is not null || hold is not null || clips is not null)
                    throw new InvalidDataException("Render no-window snapshot must leave every channel measurement null.");
            }
            else if (selectedChannel)
            {
                if (silent != false || peak is null || rms is null || peak < rms || hold is null || hold < peak || clips is null)
                    throw new InvalidDataException("Selected Render completed-window values or peak hold are invalid.");
            }
            else if (silent != true || peak is not null || rms is not null || hold is not null || clips is null)
                throw new InvalidDataException("Unselected Render channel must be digitally silent in a completed window.");
            result.Add(new(index, peak, rms, silent, hold, clips));
        }
        return new(snapshot.Sequence, snapshot.WindowIndex, snapshot.DurationMs, snapshot.ChannelCount, selected, result, snapshot.Quality, model.GetString()!, true, snapshot.Coalesced);
    }

    private static SnapshotHeader ReadSnapshotHeader(JsonElement payload, int acceptedChannelCount)
    {
        if (!payload.TryGetProperty("snapshotSequence", out var sequence) || !sequence.TryGetInt64(out var snapshotSequence) || snapshotSequence <= 0 ||
            !payload.TryGetProperty("completedWindowIndex", out var window) || window.ValueKind != JsonValueKind.Null && (!window.TryGetInt64(out var windowIndex) || windowIndex <= 0) ||
            !TryInt(payload, "windowDurationMs", out var duration) || duration is < 1 or > 1000 ||
            !TryInt(payload, "channelCount", out var channelCount) || channelCount != acceptedChannelCount || channelCount is < 1 or > 64 ||
            !payload.TryGetProperty("streamQuality", out var quality) || quality.ValueKind != JsonValueKind.String || quality.GetString() is not ("UNKNOWN" or "WARNING" or "FAILED") ||
            !payload.TryGetProperty("synthetic", out var synthetic) || synthetic.ValueKind != JsonValueKind.True ||
            !payload.TryGetProperty("coalescedSnapshotCount", out var coalesced) || !coalesced.TryGetInt64(out var coalescedCount) || coalescedCount < 0)
            throw new InvalidDataException("MeterSnapshot header is malformed or does not match the accepted session.");
        return new(snapshotSequence, window.ValueKind == JsonValueKind.Null ? null : window.GetInt64(), duration, channelCount, quality.GetString()!, coalescedCount);
    }

    private static double? ReadNullableLevel(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) throw new InvalidDataException($"MeterSnapshot is missing {name}.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var level) || !double.IsFinite(level) || level is < -200 or > 0) throw new InvalidDataException($"MeterSnapshot {name} must be null or a finite dBFS value in [-200, 0].");
        return level;
    }

    private static bool? ReadNullableBoolean(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) throw new InvalidDataException($"MeterSnapshot is missing {name}.");
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null, _ => throw new InvalidDataException($"MeterSnapshot {name} must be boolean or null.") };
    }

    private static int? ReadNullableNonnegativeInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) throw new InvalidDataException($"MeterSnapshot is missing {name}.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetInt32(out var count) || count < 0) throw new InvalidDataException($"MeterSnapshot {name} must be a nonnegative integer or null.");
        return count;
    }

    private bool IsTerminalSession(Guid sessionId)
    {
        lock (_terminalSync) return _terminalSessions.Contains(sessionId);
    }

    private void RememberTerminalSession(Guid sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        lock (_terminalSync)
        {
            if (!_terminalSessions.Add(sessionId)) return;
            _terminalSessionOrder.Enqueue(sessionId);
            while (_terminalSessionOrder.Count > TerminalSessionHistoryLimit) _terminalSessions.Remove(_terminalSessionOrder.Dequeue());
        }
    }

    private sealed record SnapshotHeader(long Sequence, long? WindowIndex, int DurationMs, int ChannelCount, string Quality, long Coalesced);

    private static void ValidateEnvelope(JsonElement root, string expectedKind)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.Object ||
            !TryInt(protocol, "major", out var major) || major != ProtocolMajor || !TryInt(protocol, "minor", out var minor) || minor != ProtocolMinor ||
            !root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != SchemaVersion ||
            !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != expectedKind ||
            !root.TryGetProperty("requestId", out var requestId) || requestId.ValueKind != JsonValueKind.Null ||
            !root.TryGetProperty("correlationId", out var correlation) || correlation.ValueKind != JsonValueKind.String || !correlation.TryGetGuid(out _) ||
            !root.TryGetProperty("sessionId", out var session) || (session.ValueKind != JsonValueKind.Null && (session.ValueKind != JsonValueKind.String || !session.TryGetGuid(out _))) ||
            !root.TryGetProperty("eventSequence", out var sequence) || !sequence.TryGetInt64(out var sequenceValue) ||
            !root.TryGetProperty("commandOrEvent", out var command) || command.ValueKind != JsonValueKind.String || !ValidCommand(command.GetString()) ||
            !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("IPC envelope is missing required fields or contains invalid metadata.");
        if (expectedKind == "response" && sequenceValue != 0)
            throw new InvalidDataException("Response correlation/session/eventSequence metadata is invalid.");
        if (expectedKind == "event" && (sequenceValue <= 0 || session.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("Event correlation/session/eventSequence metadata is invalid.");
        if (expectedKind == "response" && (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() is not ("OK" or "ERROR")))
            throw new InvalidDataException("Response status is invalid.");
    }

    private static bool ValidCommand(string? command) => !string.IsNullOrEmpty(command) && command.Length <= 64 && command.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static bool TryInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var element) && element.TryGetInt32(out value);
    }

    private void EndTransport(string stage, string message, int? exitCode)
    {
        if (Interlocked.Exchange(ref _transportEnded, 1) != 0) return;
        EndpointDiscoveryQaTrace.Write("end-transport-" + stage, state: _state.State.ToString(), exitCode: exitCode);
        lock (_pendingSync)
        {
            foreach (var entry in _pending.ToArray())
                if (_pending.TryRemove(entry.Key, out var pending) && pending.TryFail())
                    pending.Completion.TrySetException(new IOException($"Worker disconnected during {pending.Command}: {message}"));
        }
        SetState(WorkerConnectionState.Disconnected, $"Worker/pipe отключён на этапе {stage}; активная операция и cleanup неизвестны.", exitCode);
        RaiseSafely(TransportFault, new WorkerTransportFaultDto(stage, message, exitCode));
        KillOwnedWorker();
    }

    internal Task<JsonDocument> SendForTestAsync(string command, object payload) => SendAsync(command, payload, CancellationToken.None);
    internal void SeedEventCursorForTest(Guid sessionId, Guid correlationId, long sequence, bool terminal = false)
    {
        _lastEventSequence = sequence;
        var cursor = new SessionCursor(correlationId, "CAPTURE", 1, null) { LastSequence = sequence, Terminal = terminal };
        _sessions[sessionId] = cursor;
        if (terminal) RememberTerminalSession(sessionId);
    }
    internal void SeedMeterSessionForTest(Guid sessionId, Guid correlationId, string direction, int channelCount, int? selectedChannel = null)
    {
        _sessions[sessionId] = new(correlationId, direction, channelCount, selectedChannel) { Started = true };
    }
    internal int SessionCursorCountForTest => _sessions.Count;
    internal int TerminalSessionCountForTest { get { lock (_terminalSync) return _terminalSessions.Count; } }
    internal Guid SeedTimedOutRequestForTest(string command, WorkerSessionSettingsDto? expectedSettings = null)
    {
        var id = Guid.NewGuid();
        var request = new PendingRequest(command, new(TaskCreationOptions.RunContinuationsAsynchronously), command is "StartCaptureMeter" or "StartRenderChannelTest", expectedSettings);
        request.MarkSent(); request.TryAbandon(out _); request.Completion.TrySetCanceled();
        lock (_pendingSync) if (!_pending.TryAdd(id, request)) throw new InvalidOperationException("Could not seed timed-out request.");
        return id;
    }
    internal Guid SeedSentRequestForTest(string command, WorkerSessionSettingsDto? expectedSettings = null)
    {
        var id = Guid.NewGuid(); var request = new PendingRequest(command, new(TaskCreationOptions.RunContinuationsAsynchronously), command is "StartCaptureMeter" or "StartRenderChannelTest", expectedSettings);
        request.MarkSent();
        lock (_pendingSync) if (!_pending.TryAdd(id, request)) throw new InvalidOperationException("Could not seed request.");
        return id;
    }
    internal Task<JsonDocument> PendingTaskForTest(Guid id) => _pending.TryGetValue(id, out var request) ? request.Completion.Task : throw new KeyNotFoundException();
    internal bool AbandonRequestForTest(Guid id)
    {
        if (!_pending.TryGetValue(id, out var pending) || !pending.TryAbandon(out _)) return false;
        pending.Completion.TrySetCanceled();
        return true;
    }
    internal int PendingCountForTest => _pending.Count;
    internal void DispatchEventForTest(string json)
    {
        using var document = IpcFrameCodec.Parse(Encoding.UTF8.GetBytes(json));
        DispatchEvent(document.RootElement);
    }
    internal void DispatchResponseForTest(string json)
    {
        using var document = IpcFrameCodec.Parse(Encoding.UTF8.GetBytes(json));
        DispatchResponse(document);
    }
    internal async Task<WorkerTransportFaultDto> SendRawFrameForTestAsync(byte[] frame)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Worker is not connected.");
        var fault = new TaskCompletionSource<WorkerTransportFaultDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFault(WorkerTransportFaultDto value) => fault.TrySetResult(value);
        TransportFault += OnFault;
        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try { await pipe.WriteAsync(frame).ConfigureAwait(false); await pipe.FlushAsync().ConfigureAwait(false); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { _writeGate.Release(); }
            return await fault.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally { TransportFault -= OnFault; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static WorkerProtocolException ReadProtocolError(JsonElement root)
    {
        static string Bounded(JsonElement value, string name, string fallback, int limit)
        {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(field.GetString())) return fallback;
            var text = field.GetString()!;
            return text[..Math.Min(text.Length, limit)];
        }
        var hresult = root.TryGetProperty("hresult", out var hr) && hr.TryGetInt32(out var value) ? value : (int?)null;
        return new(Bounded(root, "failureStage", "UNKNOWN", 64), Bounded(root, "errorCode", "PROTOCOL_ERROR", 64), Bounded(root, "message", "Worker rejected protocol input.", 512), hresult);
    }
    private static void RaiseSafely<T>(Action<T>? handlers, T args)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList()) try { handler(args); } catch { }
    }
    private static NamedPipeServerStream CreateSecurePipe(string name)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("Current user SID unavailable.");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{sid})", 1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = descriptor, bInheritHandle = false };
            var handle = CreateNamedPipeW($"\\\\.\\pipe\\{name}", PipeOpenDuplex | 0x40000000 | FirstPipeInstance, RejectRemoteClients, 1, 65536, 65536, 0, ref security);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateNamedPipeW failed.");
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }
    private SafeJobHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = new() { LimitFlags = 0x00002000 } };
        if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())) { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return job;
    }
    private static string QueryImagePath(SafeProcessHandle process) { var buffer = new StringBuilder(32768); var size = (uint)buffer.Capacity; if (!QueryFullProcessImageNameW(process, 0, buffer, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error()); return buffer.ToString(); }
    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private int? QueryExitCode(SafeProcessHandle? process) => process is not null && GetExitCodeProcess(process, out var code) ? unchecked((int)code) : null;
    private async Task MonitorExitAsync(SafeProcessHandle process)
    {
        if (await Task.Run(() => WaitForSingleObject(process, uint.MaxValue)).ConfigureAwait(false) == 0 && !_shutdownRequested)
        {
            EndpointDiscoveryQaTrace.Write("worker-process-exit", state: _state.State.ToString(), exitCode: QueryExitCode(process));
            EndTransport("PROCESS_EXIT", "Worker process exited unexpectedly.", QueryExitCode(process));
        }
    }
    private void KillOwnedWorker()
    {
        try { if (_job is { IsInvalid: false, IsClosed: false }) TerminateJobObject(_job, 1); } catch { }
        _pipe?.Dispose(); _pipe = null; _job?.Dispose(); _job = null; _process?.Dispose(); _process = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        KillOwnedWorker();
        if (_readerTask is not null) try { await _readerTask.ConfigureAwait(false); } catch { }
        _lifetime.Dispose(); _writeGate.Dispose();
    }

    private sealed class PendingRequest(string command, TaskCompletionSource<JsonDocument> completion, bool isStart, WorkerSessionSettingsDto? expectedSettings)
    {
        private readonly object _sync = new();
        private RequestState _state;
        public string Command { get; } = command;
        public TaskCompletionSource<JsonDocument> Completion { get; } = completion;
        public bool IsStart { get; } = isStart;
        public WorkerSessionSettingsDto? ExpectedSettings { get; } = expectedSettings;
        public bool WasSent { get { lock (_sync) return _state is RequestState.Sent or RequestState.AbandonedAfterSend or RequestState.Completed; } }
        public bool IsUnresolvedStart { get { lock (_sync) return IsStart && _state is RequestState.NotSent or RequestState.Sent or RequestState.AbandonedAfterSend; } }
        public bool MarkSent() { lock (_sync) { if (_state != RequestState.NotSent) return false; _state = RequestState.Sent; return true; } }
        public bool TryAbandon(out bool mayHaveBeenSent)
        {
            lock (_sync)
            {
                mayHaveBeenSent = _state is RequestState.Sent or RequestState.AbandonedAfterSend;
                if (_state == RequestState.Completed || _state is RequestState.AbandonedBeforeSend or RequestState.AbandonedAfterSend) return false;
                _state = mayHaveBeenSent ? RequestState.AbandonedAfterSend : RequestState.AbandonedBeforeSend;
                return true;
            }
        }
        public bool TryComplete(out bool wasAbandoned)
        {
            lock (_sync)
            {
                wasAbandoned = _state == RequestState.AbandonedAfterSend;
                if (_state is RequestState.Completed or RequestState.AbandonedBeforeSend) return false;
                _state = RequestState.Completed;
                return true;
            }
        }
        public bool TryFail()
        {
            lock (_sync)
            {
                if (_state == RequestState.Completed) return false;
                _state = RequestState.Completed;
                return true;
            }
        }
    }
    private enum RequestState { NotSent, Sent, AbandonedBeforeSend, AbandonedAfterSend, Completed }
    private sealed class SessionCursor(Guid correlationId, string direction, int channelCount, int? selectedChannel)
    {
        public Guid CorrelationId { get; } = correlationId;
        public string Direction { get; } = direction;
        public int ChannelCount { get; } = channelCount;
        public int? SelectedChannel { get; } = selectedChannel;
        public long LastSequence { get; set; }
        public long LastSnapshotSequence { get; set; }
        public volatile bool Started;
        public volatile bool Stopping;
        public volatile bool Terminal;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO { public uint cb; public string? lpReserved, lpDesktop, lpTitle; public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public ushort wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid { public SafeJobHandle() : base(true) { } protected override bool ReleaseHandle() => CloseHandle(handle); }
    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid { public SafeKernelHandle(IntPtr value, bool owns = true) : base(owns) => SetHandle(value); protected override bool ReleaseHandle() => CloseHandle(handle); }
    private sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid { public SafeProcessHandle(IntPtr value, bool owns = true) : base(owns) => SetHandle(value); protected override bool ReleaseHandle() => CloseHandle(handle); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION processInfo);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeKernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeJobHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SECURITY_ATTRIBUTES attributes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientPid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder imageName, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
