using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace KlidanAudioProbe.Gui.Core;

public sealed record DeviceConfigurationProfile(
    int SchemaVersion,
    Guid ProfileId,
    DateTimeOffset CreatedAtUtc,
    string ProbeVersion,
    string Platform,
    string Backend,
    ProfileEnvironment Environment,
    ProfileDeviceIdentity Device,
    ProfileEndpointBindings EndpointBindings,
    ProfileCapabilityEvidence CapabilityEvidence,
    IReadOnlyList<ProfileEvidence> Evidence,
    IReadOnlyList<ProfileAudioHostAssessment> AudioHostAssessments,
    ProfileRecommendation Recommendation,
    string Privacy)
{
    public IReadOnlyList<ProfileStreamObservation> StreamObservations { get; init; } = [];
    public IReadOnlyList<ProfileDuplexObservation> DuplexObservations { get; init; } = [];
    public ProfileSustainedSelection? SustainedSelection { get; init; }
}

public sealed record ProfileEnvironment(string? OsDescription, string? ProcessArchitecture);
public sealed record ProfileDeviceIdentity(string? Name, string? Manufacturer, string? Model, string? DriverVersion, string? Firmware, string? HardwareClock, string? UsbMode);
public sealed record ProfileEndpointBindings(ProfileEndpointBinding? Capture, ProfileEndpointBinding? Render);
public sealed record ProfileEndpointBinding(string? EndpointId, string Direction, string State, string? FriendlyName, string Binding);
public sealed record ProfileCapabilityEvidence(IReadOnlyList<Guid> Capture, IReadOnlyList<Guid> Render, IReadOnlyList<Guid> Duplex);
public sealed record ProfileAudioFormat(string Encoding, int SampleRate, int Channels, int ContainerBits, int ValidBits, string? Period);
public sealed record ProfileEvidence(
    Guid Id,
    string SourceProvenance,
    string SourceReference,
    DateTimeOffset ObservedAtUtc,
    string EndpointDirection,
    ProfileAudioFormat Format,
    string ChannelMaskEvidence,
    string FormatSupport,
    string? IsFormatSupportedHresult,
    string Initialize,
    bool InitializeAttempted,
    string? FirstInitializeHresult,
    string? AlignmentInitializeHresult,
    string? FinalInitializeHresult,
    long? RequestedPeriod100ns,
    long? ActualPeriod100ns,
    uint? BufferFrames,
    bool? EventReady,
    string Cleanup,
    string StreamStart,
    string PcmObservation,
    string Stability,
    string Duplex,
    string PhysicalQa,
    string Routing)
{
    public string OperationStatus { get; init; } = "UNKNOWN";
    public string? ActivationHresult { get; init; }
    public string? DevicePeriodHresult { get; init; }
    public long? DefaultDevicePeriod100ns { get; init; }
    public long? MinimumDevicePeriod100ns { get; init; }
    public string QueryCleanup { get; init; } = "INCOMPLETE";
    public uint QueryClientReleaseAttempts { get; init; }
    public uint ClientReleaseAttempts { get; init; }
    public string? EventCreateHresult { get; init; }
    public string? SetEventHandleHresult { get; init; }
    public string? EventHandleCloseHresult { get; init; }
    public string? AlignmentGetBufferSizeHresult { get; init; }
    public uint? AlignmentFrames { get; init; }
    public string? AlignmentActivateHresult { get; init; }
    public long? AlignmentPeriod100ns { get; init; }
}
public sealed record ProfileAudioHostAssessment(
    string Mode,
    ProfileAudioFormat? ProcessingFormat,
    ProfileAudioFormat? PhysicalFormat,
    IReadOnlyList<Guid> Evidence,
    bool EligibleForRecommendation,
    IReadOnlyList<string> UnverifiedRequirements,
    IReadOnlyList<string> KnownIncompatibilities);
public sealed record ProfileConfigurationChoice(string Mode, ProfileAudioFormat Format, string? Period, IReadOnlyList<Guid> Evidence);
public sealed record ProfileRecommendation(ProfileConfigurationChoice? Preferred, ProfileConfigurationChoice? Fallback, IReadOnlyList<string> UnavailableReasons)
{
    public string Scope { get; init; } = "NONE";
    public string Confidence { get; init; } = "NOT_AVAILABLE";
}
public sealed record ProfileSustainedSelection(string SourceScanSessionId, ProfileSustainedConfiguration? Preferred, ProfileSustainedConfiguration? Fallback, string Scope, string Confidence, IReadOnlyList<string> UnavailableReasons, IReadOnlyList<ProfileSustainedEvidenceReference> Evidence);
public sealed record ProfileSustainedConfiguration(string CaptureEndpointId, string RenderEndpointId, ProfileAudioFormat Format, string Period, int CaptureBufferFrames, int RenderBufferFrames, string Mmcss, int DurationSeconds, string Quality, string Confidence, IReadOnlyList<Guid> OperationIds, IReadOnlyList<Guid> ReportIds, IReadOnlyList<Guid> EvidenceReferences);
public sealed record ProfileSustainedEvidenceReference(Guid EvidenceId, Guid OperationId, Guid ReportId, string SourceProvenance, string SourceReference, string CaptureEndpointId, string RenderEndpointId, ProfileAudioFormat Format, string Period, string Mmcss, int DurationSeconds, string Quality, string Classification, string CleanupStatus, int CaptureBufferFrames, int RenderBufferFrames);
public sealed record ProfileStreamObservation(Guid InitializeEvidenceId, Guid OperationId, string Provenance, DateTimeOffset ObservedAtUtc, string Direction, ProfileAudioFormat Format, int DurationSeconds, string? ActivationHresult, string? IsFormatSupportedHresult, string? InitializeHresult, string? StartHresult, bool? StartSucceeded, long? Events, long? Packets, long? FramesProcessed, long? ExpectedFrames, double? FrameCoveragePercent, double? ElapsedMilliseconds, bool? DurationReached, long? Discontinuities, long? TimestampErrors, long? UnavailableCaptureBuffers, long? BufferServiceFaults, long? EventWaitTimeouts, string StreamQuality, bool? StopSucceeded, string? StopHresult, string Cleanup, string CleanupDetail, string PhysicalPlayback, string PhysicalRouting);
public sealed record ProfileDuplexObservation(Guid EvidenceId, Guid OperationId, string CaptureEndpointId, string RenderEndpointId, string Period, string Mmcss, int DurationSeconds, JsonElement Report);

public static class DeviceConfigurationProfileFactory
{
    public static DeviceConfigurationProfile CreateDuplex(WindowsEndpointDto capture, WindowsEndpointDto render, IReadOnlyList<DuplexValidationDto> validations, string? probeVersion = null, Guid? profileId = null, DateTimeOffset? createdAtUtc = null)
    {
        if (capture.Direction != "CAPTURE" || render.Direction != "RENDER" || capture.DeviceState != "ACTIVE" || render.DeviceState != "ACTIVE" || string.IsNullOrWhiteSpace(capture.EndpointId) || string.IsNullOrWhiteSpace(render.EndpointId) || capture.EndpointId == render.EndpointId || validations.Count is < 1 or > 2) throw new InvalidDataException("A Duplex profile requires an exact active Capture/Render pair and one or two observations.");
        var observations = new List<ProfileDuplexObservation>(); var evidence = new List<ProfileEvidence>();
        foreach (var validation in validations)
        {
            var root = validation.Report;
            var requested = root.GetProperty("requested"); var period = requested.GetProperty("period").GetString()!; var mmcss = requested.GetProperty("mmcss").GetString()!; var duration = requested.GetProperty("durationSeconds").GetInt32();
            if (!Guid.TryParse(validation.OperationId, out var operationId) || operationId == Guid.Empty || !Guid.TryParse(root.GetProperty("operationId").GetString(), out var reportOperationId) || reportOperationId != operationId ||
                validation.CaptureEndpointId != capture.EndpointId || validation.RenderEndpointId != render.EndpointId || root.GetProperty("provenance").GetString() != "REAL_WASAPI_DUPLEX" ||
                requested.GetProperty("captureEndpointId").GetString() != capture.EndpointId || requested.GetProperty("renderEndpointId").GetString() != render.EndpointId ||
                period is not ("MINIMUM" or "DEFAULT") || mmcss is not ("OFF" or "PRO_AUDIO") || duration is < 1 or > 10 || requested.GetProperty("sampleRateHz").GetInt32() != 48000 || requested.GetProperty("channels").GetInt32() != 2 || requested.GetProperty("encoding").GetString() != "PCM16" || observations.Any(o => o.Period == period)) throw new InvalidDataException("Duplex profile evidence must match its exact pair, operation identity and bounded settings.");
            var id = Guid.NewGuid(); var timestamp = DateTimeOffset.Parse(root.GetProperty("timestampUtc").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
            observations.Add(new(id, operationId, capture.EndpointId, render.EndpointId, period, mmcss, duration, root.Clone()));
            var report = root; var side = report.GetProperty("capture"); var supportHr = side.GetProperty("isFormatSupportedHresult").GetString(); var initHr = side.GetProperty("initializeHresult").ValueKind == JsonValueKind.String ? side.GetProperty("initializeHresult").GetString() : null;
            var init = initHr is null ? "NOT_RUN" : DeviceConfigurationProfileJson.IsSuccessfulHresult(initHr) ? "INITIALIZED" : "FAILED";
            var duplexStatus = report.GetProperty("duplex").GetProperty("status").GetString() ?? "INCOMPLETE";
            var duplexEvidence = duplexStatus switch
            {
                "DUPLEX_CLEAN" => "DUPLEX_QA",
                "DUPLEX_WARNINGS" => "DUPLEX_WARNINGS",
                "FAILED" => "FAILED",
                "CANCELLED" => "DUPLEX_CANCELLED",
                _ => "INCOMPLETE"
            };
            evidence.Add(new(id, "REAL_WASAPI_DUPLEX", validation.OperationId, timestamp, "DUPLEX", new("PCM16", 48000, 2, 16, 16, period), "NOT_APPLICABLE", DeviceConfigurationProfileJson.IsSuccessfulHresult(supportHr) ? "SUPPORTED_REPORTED" : supportHr is null ? "UNKNOWN" : "UNSUPPORTED", supportHr, init, initHr is not null, initHr, null, initHr, null, null, NullableUInt(side, "actualBufferFrames"), side.GetProperty("ready").GetBoolean(), side.GetProperty("cleanup").GetString() ?? "UNKNOWN", side.GetProperty("startSucceeded").GetBoolean() ? "STREAM_STARTED" : "NOT_RUN", side.GetProperty("framesProcessed").GetInt64() > 0 ? "STREAM_OBSERVED" : "NOT_RUN", "NOT_RUN", duplexEvidence, "NOT_RUN", "UNKNOWN") { OperationStatus = duplexStatus switch { "DUPLEX_CLEAN" => "DUPLEX_CLEAN_SHORT_RUN", "CANCELLED" => "DUPLEX_CANCELLED", _ => duplexStatus } });
        }
        var assessed = observations.Select(o => DeviceConfigurationEvaluator.Evaluate(o.Period, o.Report, o.CaptureEndpointId, o.RenderEndpointId, o.Mmcss)).ToArray();
        var recommendation = DeviceConfigurationEvaluator.Recommend(assessed);
        ProfileConfigurationChoice? Choice(DuplexCandidateAssessment? c) => c is null ? null : new("PROBE_PROVISIONAL", new("PCM16", 48000, 2, 16, 16, c.Period), c.Period, observations.Where(o => o.Period == c.Period).Select(o => o.EvidenceId).ToArray());
        var p = new DeviceConfigurationProfile(3, profileId ?? Guid.NewGuid(), (createdAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(), probeVersion ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown", "WINDOWS", "WASAPI", new(System.Runtime.InteropServices.RuntimeInformation.OSDescription, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()), new(null, null, null, null, null, null, null), new(new(capture.EndpointId, "CAPTURE", "ACTIVE", capture.FriendlyName, "LOCAL_EXACT"), new(render.EndpointId, "RENDER", "ACTIVE", render.FriendlyName, "LOCAL_EXACT")), new([], [], evidence.Select(e => e.Id).ToArray()), evidence, [new("COORDINATED_EXCLUSIVE", new("FLOAT32", 48000, 2, 32, 32, null), new("PCM16", 48000, 2, 16, 16, null), evidence.Select(e => e.Id).ToArray(), false, ["Probe Duplex is not coordinated AudioHost validation.", "Five-second runs do not establish long-duration stability.", "Physical RTL and KLIDAN QA are not measured."], [])], new(Choice(recommendation.Preferred), Choice(recommendation.Fallback), recommendation.Reasons) { Scope = recommendation.Scope, Confidence = recommendation.Preferred?.Confidence ?? "NOT_AVAILABLE" }, "LOCAL_MACHINE") { DuplexObservations = observations };
        return DeviceConfigurationProfileJson.Validate(p);
    }

    private static uint? NullableUInt(JsonElement parent, string name) => parent.GetProperty(name).ValueKind == JsonValueKind.Number && parent.GetProperty(name).TryGetUInt32(out var value) ? value : null;
    public static DeviceConfigurationProfile Create(
        WindowsEndpointDto endpoint,
        RealFormatMatrixDto? query,
        RealFormatMatrixDto? initialization = null,
        string? probeVersion = null,
        Guid? profileId = null,
        DateTimeOffset? createdAtUtc = null)
        => CreateAggregated(endpoint, query, initialization is null ? [] : [initialization], probeVersion, profileId, createdAtUtc);

    public static DeviceConfigurationProfile CreateAggregated(
        WindowsEndpointDto endpoint,
        RealFormatMatrixDto? query,
        IReadOnlyList<RealFormatMatrixDto> initializations,
        string? probeVersion = null,
        Guid? profileId = null,
        DateTimeOffset? createdAtUtc = null,
        IReadOnlyList<RealStreamValidationDto>? streamValidations = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(initializations);
        if (query is null && initializations.Count == 0) throw new InvalidDataException("At least one accepted real query or Initialize-only result is required.");
        if (string.IsNullOrWhiteSpace(endpoint.EndpointId) || endpoint.EndpointIdEvidence.State != "AVAILABLE" || endpoint.DeviceStateEvidence.State != "AVAILABLE" || endpoint.DeviceState != "ACTIVE" || endpoint.Direction is not ("CAPTURE" or "RENDER"))
            throw new InvalidDataException("A profile requires the exact ID of an active real Capture or Render endpoint.");
        if (query is not null && (query.Candidates is null || query.Candidates.Count > 1024 || query.Candidates.Any(c => c is null || string.IsNullOrWhiteSpace(c.Encoding) || string.IsNullOrWhiteSpace(c.Period)))) throw new InvalidDataException("Passive query evidence is missing, malformed, or exceeds the profile limit.");
        if (initializations.Count > 64 || initializations.Any(initialization => initialization is null || initialization.Candidates is null || initialization.Candidates.Count != 1 || initialization.Candidates[0] is null || string.IsNullOrWhiteSpace(initialization.Candidates[0].Encoding) || string.IsNullOrWhiteSpace(initialization.Candidates[0].Period))) throw new InvalidDataException("Initialize-only evidence must contain at most 64 single-candidate results.");
        if (query is not null && (query.Provenance != "REAL_WASAPI_QUERY" || query.ReportLevel != "PASSIVE_QUERY" || query.EndpointId != endpoint.EndpointId || query.Direction != endpoint.Direction))
            throw new InvalidDataException("The passive query must match the selected endpoint ID and direction.");
        foreach (var initialization in initializations)
            if (initialization.Provenance != "REAL_WASAPI_INITIALIZE_ONLY" || initialization.ReportLevel != "INITIALIZE_ONLY" || initialization.EndpointId != endpoint.EndpointId || initialization.Direction != endpoint.Direction || initialization.Candidates.Count != 1)
                throw new InvalidDataException("Initialize-only evidence must match the selected endpoint and contain one exact candidate.");

        var evidence = new List<ProfileEvidence>((query?.Candidates.Count ?? 0) + initializations.Count);
        if (query is not null)
            for (var i = 0; i < query.Candidates.Count; i++)
                evidence.Add(FromCandidate(query.Candidates[i], endpoint.Direction, query.Provenance, $"{query.TimestampUtc}#{i}", DateTimeOffset.Parse(query.TimestampUtc, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime()));
        foreach (var initialization in initializations)
        {
            var candidate = initialization.Candidates[0];
            if (candidate.SampleRate is < 8_000 or > 384_000 || candidate.Channels is < 1 or > 64 || candidate.Period is not ("default" or "minimum"))
                throw new InvalidDataException("Initialize-only candidate contains invalid format or period values.");
            evidence.Add(FromCandidate(candidate, endpoint.Direction, initialization.Provenance, initialization.TimestampUtc, DateTimeOffset.Parse(initialization.TimestampUtc, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime()));
        }
        ValidatePassiveQueryRows(evidence);

        var endpointBinding = new ProfileEndpointBinding(endpoint.EndpointId, endpoint.Direction, endpoint.DeviceState, endpoint.FriendlyName, "LOCAL_EXACT");
        var ids = evidence.Select(e => e.Id).ToArray();
        var empty = Array.Empty<Guid>();
        var captureRefs = endpoint.Direction == "CAPTURE" ? ids : empty;
        var renderRefs = endpoint.Direction == "RENDER" ? ids : empty;
        var nativeFormat = new ProfileAudioFormat("PCM16", 48_000, 2, 16, 16, "MINIMUM");
        var assessments = new[]
        {
            new ProfileAudioHostAssessment("SHARED", new("FLOAT32", 48_000, 2, 32, 32, null), null, empty, false,
                ["AudioHost Shared stream has not been observed for this endpoint; physical negotiated format is unknown."], []),
            new ProfileAudioHostAssessment("COORDINATED_EXCLUSIVE", new("FLOAT32", 48_000, 2, 32, 32, null), nativeFormat,
                evidence.Where(e => e.Format.Encoding == nativeFormat.Encoding && e.Format.SampleRate == nativeFormat.SampleRate && e.Format.Channels == nativeFormat.Channels).Select(e => e.Id).ToArray(), false,
                ["Capture and Render must be validated together.", "Minimum-period coordinated stream Start and PCM observation are not present.", "Stability and KLIDAN physical QA are not present."], [])
        };
        var profile = new DeviceConfigurationProfile(1, profileId ?? Guid.NewGuid(), (createdAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            probeVersion ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            "WINDOWS", "WASAPI", new(System.Runtime.InteropServices.RuntimeInformation.OSDescription, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
            new(endpoint.FriendlyName, null, null, null, null, null, null),
            endpoint.Direction == "CAPTURE" ? new(endpointBinding, null) : new(null, endpointBinding),
            new(captureRefs, renderRefs, empty), evidence, assessments,
            new(null, null, ["No eligible AudioHost configuration is established by format query or Initialize-only evidence.", "Keep the current Shared mode and endpoint settings unless separate stream and physical QA authorizes a change."]),
            "LOCAL_MACHINE");
        if (streamValidations is { Count: > 0 })
        {
            var observations = new List<ProfileStreamObservation>(streamValidations.Count);
            foreach (var item in streamValidations)
            {
                if (item.EndpointId != endpoint.EndpointId || item.Direction != endpoint.Direction || item.Report.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Stream evidence endpoint/direction does not match the profile binding.");
                var report = item.Report;
                var period = report.GetProperty("period").GetString() ?? throw new InvalidDataException("Stream period is missing.");
                var fmt = report.GetProperty("format");
                var encoding = fmt.GetProperty("encoding").GetString() ?? throw new InvalidDataException("Stream format is missing.");
                var rate = fmt.GetProperty("sampleRateHz").GetInt32(); var channels = fmt.GetProperty("channels").GetInt32();
                var init = evidence.SingleOrDefault(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY" && e.EndpointDirection == endpoint.Direction && e.Format.Encoding == encoding && e.Format.SampleRate == rate && e.Format.Channels == channels && e.Format.Period == period)
                    ?? throw new InvalidDataException("Stream result has no matching accepted Initialize evidence.");
                string? Text(string name) => report.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                string? HresultText(string name) => Text(name) is { Length: > 0 } value && value != "NOT_ATTEMPTED" && value != "UNKNOWN" ? value : null;
                long? Number(string name) => report.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
                double? Decimal(string name) => report.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
                bool? Bool(string name) => report.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
                var cleanupDetail = Text("cleanup") ?? "UNKNOWN";
                var cleanup = cleanupDetail.Contains("FAILED", StringComparison.Ordinal) ? "FAILED" : cleanupDetail.Contains("RELEASE_ATTEMPTED", StringComparison.Ordinal) ? "RELEASE_ATTEMPTED" : cleanupDetail.Contains("NOT_ACQUIRED", StringComparison.Ordinal) ? "NOT_ACQUIRED" : cleanupDetail.Contains("NOT_ATTEMPTED", StringComparison.Ordinal) ? "NOT_RUN" : "UNKNOWN";
                observations.Add(new(init.Id, Guid.Parse(item.OperationId), Text("provenance") ?? "", DateTimeOffset.Parse(Text("timestampUtc")!, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime(), endpoint.Direction,
                    new(encoding, rate, channels, 16, 16, period), report.GetProperty("durationSeconds").GetInt32(), HresultText("activationHresult"), HresultText("queryHresult"), HresultText("finalInitializeHresult"), HresultText("startHresult"), Bool("startSucceeded"), Number("events"), Number("packets"), Number("framesProcessed"), Number("expectedFrames"), Decimal("frameCoveragePercent"), Decimal("elapsedMilliseconds"), Bool("durationReached"), Number("discontinuities"), Number("timestampErrors"), Number("unavailableCaptureBuffers"), Number("bufferServiceFaults"), Number("eventWaitTimeouts"), Text("streamQuality") ?? "UNKNOWN", Bool("stopSucceeded"), HresultText("stopHresult"), cleanup, cleanupDetail, Text("physicalPlayback") ?? "NOT_MEASURED", Text("physicalRouting") ?? "UNKNOWN"));
            }
            profile = profile with { SchemaVersion = 2, StreamObservations = observations };
        }
        return DeviceConfigurationProfileJson.Validate(profile);
    }

    private static ProfileEvidence FromCandidate(RealFormatCandidateDto c, string direction, string provenance, string sourceReference, DateTimeOffset observedAt)
    {
        var (container, valid) = c.Encoding switch
        {
            "PCM16" => (16, 16),
            "PCM24_IN_32" => (32, 24),
            "FLOAT32" => (32, 32),
            _ => (c.ContainerBits ?? 0, c.ValidBits ?? 0)
        };
        var isInit = provenance == "REAL_WASAPI_INITIALIZE_ONLY";
        return new ProfileEvidence(Guid.NewGuid(), provenance, sourceReference, observedAt,
            direction, new(c.Encoding, c.SampleRate, c.Channels, c.ContainerBits ?? container, c.ValidBits ?? valid, c.Period.ToUpperInvariant()),
            c.ChannelMaskEvidence, State(c.SupportStatus), c.SupportHr, isInit && c.InitializeAttempted ? State(c.InitializeStatus) : "NOT_RUN", isInit && c.InitializeAttempted,
            isInit ? c.FirstInitializeHr : null, isInit ? c.AlignmentInitializeHr : null, isInit ? c.FinalInitializeHr : null,
            isInit ? c.RequestedPeriod100ns : null, isInit ? c.ActualPeriod100ns : null, isInit ? c.ActualBufferFrames : null,
            isInit ? c.EventHandleReady : null,
            isInit ? c.CleanupStatus : c.QueryCleanupStatus,
            "NOT_RUN", "NOT_RUN", "NOT_RUN", "NOT_RUN", "NOT_RUN", "UNKNOWN")
        {
            OperationStatus = State(c.Status), ActivationHresult = c.ActivationHr, DevicePeriodHresult = c.DevicePeriodHr,
            DefaultDevicePeriod100ns = c.DefaultPeriod100ns, MinimumDevicePeriod100ns = c.MinimumPeriod100ns,
            QueryCleanup = c.QueryCleanupStatus, QueryClientReleaseAttempts = c.QueryClientReleaseAttempts,
            ClientReleaseAttempts = isInit ? c.ClientReleaseAttempts : 0, EventCreateHresult = isInit ? c.EventCreateHr : null,
            SetEventHandleHresult = isInit ? c.SetEventHandleHr : null, EventHandleCloseHresult = isInit ? c.EventHandleCloseHr : null,
            AlignmentGetBufferSizeHresult = isInit ? c.AlignmentGetBufferSizeHr : null, AlignmentFrames = isInit ? c.AlignmentFrames : null,
            AlignmentActivateHresult = isInit ? c.AlignmentActivateHr : null, AlignmentPeriod100ns = isInit ? c.AlignmentPeriod100ns : null
        };

        static string State(string value) => value == "NOT_TESTED" ? "NOT_RUN" : value;
    }

    internal static void ValidatePassiveQueryRows(IReadOnlyList<ProfileEvidence> evidence)
    {
        var queries = evidence.Where(e => e.SourceProvenance == "REAL_WASAPI_QUERY").ToArray();
        if (queries.Any(e => !HasQueryRowReference(e.SourceReference))) throw new InvalidDataException("Passive query record is missing a valid source row reference.");
        var duplicate = queries.GroupBy(e => (Operation: e.SourceReference[..e.SourceReference.LastIndexOf('#')], e.EndpointDirection, e.Format.Encoding, e.Format.SampleRate, e.Format.Channels, e.Format.Period))
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException("Passive query contains a duplicate format/period record.");
        var duplicateSourceRecord = evidence.GroupBy(e => (e.SourceProvenance, e.SourceReference, e.EndpointDirection, e.Format.Encoding, e.Format.SampleRate, e.Format.Channels, e.Format.Period)).FirstOrDefault(g => g.Count() > 1);
        if (duplicateSourceRecord is not null) throw new InvalidDataException("Profile contains duplicate evidence from the same source operation.");
        if (evidence.Any(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY" && !DateTimeOffset.TryParse(e.SourceReference, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)))
            throw new InvalidDataException("Initialize-only source reference must be its observation timestamp.");
        foreach (var group in queries.GroupBy(e => (Operation: e.SourceReference[..e.SourceReference.LastIndexOf('#')], e.EndpointDirection, e.Format.Encoding, e.Format.SampleRate, e.Format.Channels)))
        {
            var first = group.First();
            if (group.Any(e => e.OperationStatus != first.OperationStatus || e.FormatSupport != first.FormatSupport || e.IsFormatSupportedHresult != first.IsFormatSupportedHresult || e.ActivationHresult != first.ActivationHresult || e.DevicePeriodHresult != first.DevicePeriodHresult || e.DefaultDevicePeriod100ns != first.DefaultDevicePeriod100ns || e.MinimumDevicePeriod100ns != first.MinimumDevicePeriod100ns || e.ChannelMaskEvidence != first.ChannelMaskEvidence || e.QueryCleanup != first.QueryCleanup || e.QueryClientReleaseAttempts != first.QueryClientReleaseAttempts))
                throw new InvalidDataException("DEFAULT/MINIMUM rows contradict the shared exact-format query evidence.");
        }

        static bool HasQueryRowReference(string reference)
        {
            var separator = reference.LastIndexOf('#');
            return separator > 0 && int.TryParse(reference.AsSpan(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var row) && row >= 0 && DateTimeOffset.TryParse(reference[..separator], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
        }
    }

    public static DeviceConfigurationProfile CreateSustained(DeviceConfigurationProfile baseProfile, string scanSessionId, IReadOnlyList<SustainedDuplexEvidence> sustainedEvidence, DuplexRecommendation recommendation)
    {
        if (baseProfile.SchemaVersion != 3 || baseProfile.Privacy != "LOCAL_MACHINE" || baseProfile.EndpointBindings.Capture?.EndpointId is not { } captureId || baseProfile.EndpointBindings.Render?.EndpointId is not { } renderId || !Guid.TryParse(scanSessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
            throw new InvalidDataException("A sustained v4 profile requires a validated local v3 exact Capture/Render profile and scan session.");
        if (sustainedEvidence is null || sustainedEvidence.Count == 0) throw new InvalidDataException("A sustained v4 profile requires persisted sustained evidence.");
        var references = sustainedEvidence.Select(e => ToReference(e, captureId, renderId)).ToArray();
        ProfileSustainedEvidenceReference? Reference(DuplexCandidateAssessment? assessment) => assessment is null ? null : references.FirstOrDefault(r => Guid.TryParse(assessment.Report.GetProperty("operationId").GetString(), out var operation) && operation == r.OperationId && Guid.TryParse(assessment.Report.GetProperty("reportId").GetString(), out var report) && report == r.ReportId);
        ProfileSustainedConfiguration? Configuration(DuplexCandidateAssessment? assessment)
        {
            var reference = Reference(assessment);
            if (assessment is null || reference is null) return null;
            return new(captureId, renderId, new("PCM16", 48_000, 2, 16, 16, assessment.Period), assessment.Period, reference.CaptureBufferFrames, reference.RenderBufferFrames, assessment.Mmcss, assessment.DurationSeconds, "DUPLEX_CLEAN", assessment.Confidence, [reference.OperationId], [reference.ReportId], [reference.EvidenceId]);
        }
        var preferred = Configuration(recommendation.Preferred);
        var fallback = Configuration(recommendation.Fallback);
        var selectionConfidence = preferred?.Confidence ?? (references.Any(r => r.DurationSeconds >= 300 && r.Quality == "DUPLEX_WARNINGS") ? "WARNINGS" : references.Any(r => r.DurationSeconds >= 300) ? "MIXED_RESULTS" : "INCOMPLETE");
        var selection = new ProfileSustainedSelection(sessionGuid.ToString("D"), preferred, fallback, recommendation.Scope, selectionConfidence, recommendation.Reasons, references);
        var unavailable = new ProfileRecommendation(null, null, recommendation.Reasons) { Scope = "NONE", Confidence = "NOT_AVAILABLE" };
        return DeviceConfigurationProfileJson.Validate(baseProfile with { SchemaVersion = 4, DuplexObservations = [], Recommendation = unavailable, SustainedSelection = selection });

        static ProfileSustainedEvidenceReference ToReference(SustainedDuplexEvidence evidence, string captureId, string renderId)
        {
            if (evidence.Lifecycle != "COMPLETED" || evidence.PersistenceStatus != "SAVED_AND_RELOADED" || string.IsNullOrWhiteSpace(evidence.ReportPath) || evidence.CleanupStatus != "RELEASE_ATTEMPTED" || evidence.Classification is not ("SUSTAINED_CLEAN" or "SUSTAINED_WARNINGS"))
                throw new InvalidDataException("Sustained v4 evidence must be completed, persisted, real and safely cleaned.");
            var report = evidence.Report;
            if (report.ValueKind != JsonValueKind.Object || report.GetProperty("schemaVersion").GetInt32() != 1 || report.GetProperty("reportType").GetString() != "wasapi-sustained-duplex-validation" || report.GetProperty("provenance").GetString() != "REAL_WASAPI_SUSTAINED_DUPLEX" || report.GetProperty("physicalRtl").GetString() != "NOT_MEASURED")
                throw new InvalidDataException("Sustained v4 evidence report contract or provenance is invalid.");
            if (!Guid.TryParse(evidence.OperationId, out var operationId) || operationId == Guid.Empty || !Guid.TryParse(report.GetProperty("operationId").GetString(), out var reportOperation) || reportOperation == Guid.Empty || reportOperation != operationId || !Guid.TryParse(report.GetProperty("reportId").GetString(), out var reportId) || reportId == Guid.Empty)
                throw new InvalidDataException("Sustained v4 evidence identities are invalid.");
            var requested = report.GetProperty("requested");
            if (requested.GetProperty("captureEndpointId").GetString() != captureId || requested.GetProperty("renderEndpointId").GetString() != renderId || requested.GetProperty("period").GetString() != evidence.Period || requested.GetProperty("mmcss").GetString() != evidence.Mmcss || requested.GetProperty("durationSeconds").GetInt32() != evidence.DurationSeconds || requested.GetProperty("sampleRateHz").GetInt32() != 48_000 || requested.GetProperty("encoding").GetString() != "PCM16" || requested.GetProperty("channels").GetInt32() != 2 || evidence.DurationSeconds is not (60 or 300 or 600) || evidence.CaptureFrames is not (> 0) || evidence.RenderFrames is not (> 0))
                throw new InvalidDataException("Sustained v4 evidence settings or independent geometry differ from the exact scan.");
            var status = report.GetProperty("duplex").GetProperty("status").GetString();
            if ((evidence.Classification == "SUSTAINED_CLEAN" && status != "DUPLEX_CLEAN") || (evidence.Classification == "SUSTAINED_WARNINGS" && status != "DUPLEX_WARNINGS"))
                throw new InvalidDataException("Sustained v4 evidence classification contradicts the persisted report.");
            foreach (var (name, direction, frames) in new[] { ("capture", "CAPTURE", evidence.CaptureFrames.Value), ("render", "RENDER", evidence.RenderFrames.Value) })
            {
                var side = report.GetProperty(name);
                if (side.GetProperty("endpointId").GetString() != (name == "capture" ? captureId : renderId) || side.GetProperty("direction").GetString() != direction || side.GetProperty("durationReached").ValueKind != JsonValueKind.True || side.GetProperty("stopSucceeded").ValueKind != JsonValueKind.True || side.GetProperty("cleanup").GetString() != "RELEASE_ATTEMPTED" || side.GetProperty("actualBufferFrames").GetInt32() != frames || HasTrue(side, "deviceInvalidated") || !CountersAreNonnegative(side))
                    throw new InvalidDataException("Sustained v4 evidence side identity, duration, cleanup, geometry or quality counters are invalid.");
            }
            var quality = status == "DUPLEX_CLEAN" ? "DUPLEX_CLEAN" : "DUPLEX_WARNINGS";
            return new(reportId, operationId, reportId, "REAL_WASAPI_SUSTAINED_DUPLEX", $"SUSTAINED_REPORT:{reportId:D}", captureId, renderId, new("PCM16", 48_000, 2, 16, 16, evidence.Period), evidence.Period, evidence.Mmcss, evidence.DurationSeconds, quality, evidence.Classification, evidence.CleanupStatus, evidence.CaptureFrames.Value, evidence.RenderFrames.Value);
        }
        static bool HasTrue(JsonElement side, string name) => side.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        static bool CountersAreNonnegative(JsonElement side) => new[] { "framesProcessed", "expectedFrames", "events", "packets", "discontinuities", "timestampErrors", "serviceFaults", "eventIntervalsObserved", "packetDrainLimitHits", "intermediateWaitTimeouts", "longEventIntervals" }.All(name => side.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) && count >= 0);
    }
}

public static class DeviceConfigurationProfileJson
{
    public const int MaximumBytes = 1_048_576;
    public static bool IsSuccessfulHresult(string? value) => value == "S_OK" || string.Equals(value, "0x00000000", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        WriteIndented = true
    };

    public static byte[] SerializeLocal(DeviceConfigurationProfile profile) => Serialize(Validate(profile));

    public static byte[] SerializeSanitized(DeviceConfigurationProfile profile)
    {
        Validate(profile);
        var sanitized = profile with
        {
            Device = new(null, null, null, null, null, null, null),
            EndpointBindings = new(Sanitize(profile.EndpointBindings.Capture), Sanitize(profile.EndpointBindings.Render)),
            Evidence = profile.Evidence.Select(e => e.SourceProvenance == "OPERATOR_PROVIDED" ? e with { SourceReference = $"OPERATOR_PROVIDED_REDACTED-{e.Id:D}" } : e).ToArray(),
            DuplexObservations = profile.DuplexObservations.Select(o => o with { CaptureEndpointId = "REDACTED", RenderEndpointId = "REDACTED", Report = RedactEndpointIds(o.Report) }).ToArray(),
            SustainedSelection = profile.SustainedSelection is { } sustained ? SanitizeSustainedSelection(sustained) : null,
            Privacy = "SANITIZED_PORTABLE"
        };
        return Serialize(Validate(sanitized));
    }

    public static DeviceConfigurationProfile Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is 0 or > MaximumBytes) throw new InvalidDataException("Profile size is empty or exceeds 1 MiB.");
        using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        var profile = JsonSerializer.Deserialize<DeviceConfigurationProfile>(utf8, Options) ?? throw new InvalidDataException("Profile JSON is empty.");
        return Validate(profile);
    }

    public static DeviceConfigurationProfile Validate(DeviceConfigurationProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        Need(p.SchemaVersion is 1 or 2 or 3 or 4, "Unsupported profile schemaVersion.");
        Need(p.SchemaVersion == 4 || p.SustainedSelection is null, "Only v4 profiles may contain sustained selection evidence.");
        Need(p.ProfileId != Guid.Empty, "profileId must be a UUID.");
        Need(p.CreatedAtUtc.Offset == TimeSpan.Zero, "createdAtUtc must be UTC.");
        if (p.Environment is null || p.Device is null || p.EndpointBindings is null || p.CapabilityEvidence is null || p.Recommendation is null || p.Evidence is null || p.AudioHostAssessments is null)
            throw new InvalidDataException("Required profile objects are missing.");
        if (p.CapabilityEvidence.Capture is null || p.CapabilityEvidence.Render is null || p.CapabilityEvidence.Duplex is null || p.Recommendation.UnavailableReasons is null)
            throw new InvalidDataException("Required evidence/recommendation lists are missing.");
        Need(!string.IsNullOrWhiteSpace(p.ProbeVersion) && p.ProbeVersion.Length <= 64 && p.Platform == "WINDOWS" && p.Backend == "WASAPI", "Invalid Probe build or backend metadata.");
        Need(p.Environment.OsDescription is null or { Length: <= 256 } && p.Environment.ProcessArchitecture is null or { Length: <= 32 }, "Environment metadata exceeds bounds.");
        Need(p.Device.Name is null or { Length: <= 256 } && p.Device.Manufacturer is null or { Length: <= 128 } && p.Device.Model is null or { Length: <= 128 } && p.Device.DriverVersion is null or { Length: <= 128 } && p.Device.Firmware is null or { Length: <= 128 } && p.Device.HardwareClock is null or { Length: <= 128 } && p.Device.UsbMode is null or { Length: <= 128 }, "Device metadata exceeds bounds.");
        Need(p.Privacy is "LOCAL_MACHINE" or "SANITIZED_PORTABLE", "Invalid privacy classification.");
        Need(p.Evidence.Count is > 0 and <= 1024, "Evidence is missing or exceeds the record limit.");
        Need(p.AudioHostAssessments.Count <= 8, "AudioHost assessment count is invalid.");

        var bindings = new Dictionary<string, ProfileEndpointBinding>(StringComparer.Ordinal);
        var boundDirections = new HashSet<string>(StringComparer.Ordinal);
        AddBinding(p.EndpointBindings.Capture, "CAPTURE");
        AddBinding(p.EndpointBindings.Render, "RENDER");
        Need(boundDirections.Count > 0, "At least one endpoint binding is required.");
        void AddBinding(ProfileEndpointBinding? binding, string direction)
        {
            if (binding is null) return;
            Need(binding.Direction == direction && binding.State == "ACTIVE", "Endpoint direction/state is inconsistent.");
            boundDirections.Add(direction);
            Need(binding.Binding is "LOCAL_EXACT" or "SANITIZED_UNBOUND", "Invalid endpoint binding kind.");
            Need(binding.EndpointId is null or { Length: <= 2048 } && binding.FriendlyName is null or { Length: <= 256 }, "Endpoint identity exceeds bounds.");
            if (p.Privacy == "LOCAL_MACHINE") Need(binding.Binding == "LOCAL_EXACT" && !string.IsNullOrWhiteSpace(binding.EndpointId), "Local profile must preserve exact endpoint IDs.");
            else Need(binding.Binding == "SANITIZED_UNBOUND" && binding.EndpointId is null && binding.FriendlyName is null, "Sanitized profile must not retain endpoint identity.");
            if (binding.EndpointId is not null) Need(bindings.TryAdd(binding.EndpointId, binding), "Duplicate endpoint ID binding.");
        }

        var evidenceById = new Dictionary<Guid, ProfileEvidence>();
        foreach (var e in p.Evidence)
        {
            if (e is null || e.Format is null) throw new InvalidDataException("Evidence records and format are required.");
            Need(e.Id != Guid.Empty && evidenceById.TryAdd(e.Id, e), "Evidence IDs must be unique UUIDs.");
            Need(e.SourceProvenance is "REAL_WASAPI_QUERY" or "REAL_WASAPI_INITIALIZE_ONLY" or "REAL_WASAPI_DUPLEX" or "OPERATOR_PROVIDED", "Unknown evidence provenance.");
            Need(!string.IsNullOrWhiteSpace(e.SourceReference) && e.SourceReference.Length <= 256 && e.ObservedAtUtc.Offset == TimeSpan.Zero, "Invalid evidence source or timestamp.");
            Need(e.EndpointDirection is "CAPTURE" or "RENDER" or "DUPLEX", "Invalid evidence direction.");
            Need(EnumEvidence(e.FormatSupport) && EnumEvidence(e.Initialize) && EnumEvidence(e.StreamStart) && EnumEvidence(e.PcmObservation) && EnumEvidence(e.Stability) && EnumEvidence(e.Duplex) && EnumEvidence(e.PhysicalQa), "Unknown evidence state.");
            Need(e.ChannelMaskEvidence is not null && e.ChannelMaskEvidence.Length <= 128 && ValidHresult(e.IsFormatSupportedHresult) && ValidHresult(e.FirstInitializeHresult) && ValidHresult(e.AlignmentInitializeHresult) && ValidHresult(e.FinalInitializeHresult), "Invalid HRESULT or channel-mask evidence.");
            Need(EnumEvidence(e.OperationStatus) && ValidHresult(e.ActivationHresult) && ValidHresult(e.DevicePeriodHresult) && ValidHresult(e.EventCreateHresult) && ValidHresult(e.SetEventHandleHresult) && ValidHresult(e.EventHandleCloseHresult) && ValidHresult(e.AlignmentGetBufferSizeHresult) && ValidHresult(e.AlignmentActivateHresult), "Invalid operation-stage evidence.");
            Need(e.QueryCleanup is "NOT_ACQUIRED" or "RELEASE_ATTEMPTED" or "FAILED" or "INCOMPLETE", "Unknown query cleanup status.");
            Need(e.Format.Encoding is "PCM16" or "PCM24_IN_32" or "FLOAT32" && e.Format.SampleRate is >= 8_000 and <= 384_000 && e.Format.Channels is >= 1 and <= 64, "Format is outside the supported profile model.");
            Need((e.Format.Encoding, e.Format.ContainerBits, e.Format.ValidBits) is ("PCM16", 16, 16) or ("PCM24_IN_32", 32, 24) or ("FLOAT32", 32, 32), "Encoding and bit geometry disagree.");
            Need(e.SourceProvenance is not ("REAL_WASAPI_QUERY" or "REAL_WASAPI_INITIALIZE_ONLY") || e.Format.Period is "DEFAULT" or "MINIMUM", "Real WASAPI evidence requires an exact period.");
            Need(e.FormatSupport != "SUPPORTED_REPORTED" || IsSuccessfulHresult(e.IsFormatSupportedHresult), "SUPPORTED_REPORTED requires a successful exact-format HRESULT.");
            Need(e.OperationStatus != "SUPPORTED_REPORTED" || IsSuccessfulHresult(e.IsFormatSupportedHresult), "SUPPORTED_REPORTED operation requires a successful exact-format HRESULT.");
            Need(e.Initialize != "INITIALIZED" || IsSuccessfulHresult(e.FinalInitializeHresult), "INITIALIZED requires a successful final Initialize HRESULT.");
            Need(e.OperationStatus != "INITIALIZED" || IsSuccessfulHresult(e.FinalInitializeHresult), "INITIALIZED operation requires a successful final Initialize HRESULT.");
            Need(e.Format.Period is null or "DEFAULT" or "MINIMUM", "Invalid period value.");
            Need(e.Cleanup is "NOT_RUN" or "NOT_ACQUIRED" or "RELEASE_ATTEMPTED" or "CLEANUP_CONFIRMED" or "FAILED" or "INCOMPLETE" or "UNKNOWN", "Unknown cleanup state.");
            Need(e.BufferFrames is null or > 0 && e.RequestedPeriod100ns is null or > 0 && e.ActualPeriod100ns is null or > 0, "Invalid buffer/period geometry.");
            Need(e.DefaultDevicePeriod100ns is null or > 0 && e.MinimumDevicePeriod100ns is null or > 0 && e.AlignmentPeriod100ns is null or > 0 && e.AlignmentFrames is null or > 0, "Invalid device/alignment period geometry.");
            Need(!e.InitializeAttempted ? e.Initialize == "NOT_RUN" && e.FirstInitializeHresult is null && e.AlignmentInitializeHresult is null && e.FinalInitializeHresult is null : e.Initialize is not "NOT_RUN", "Initialize attempt and evidence disagree.");
            Need(e.Initialize != "READY" || IsReady(e), "READY requires successful Initialize, buffer geometry and event readiness.");
            Need(e.OperationStatus != "READY" || IsReady(e), "READY operation requires successful Initialize, buffer geometry and event readiness.");
            Need(e.SourceProvenance != "REAL_WASAPI_QUERY" || !e.InitializeAttempted && e.Initialize == "NOT_RUN", "A passive query cannot claim Initialize.");
            Need(e.SourceProvenance != "REAL_WASAPI_INITIALIZE_ONLY" || e.Format.Period is "DEFAULT" or "MINIMUM", "Initialize-only evidence requires an exact period.");
            Need(e.StreamStart == "NOT_RUN" || e.StreamStart == "STREAM_STARTED", "Unknown stream start state.");
            Need(e.PcmObservation == "NOT_RUN" || e.PcmObservation == "STREAM_OBSERVED", "Unknown PCM observation state.");
            Need(e.Stability == "NOT_RUN" || e.Stability == "STABILITY_QA", "Unknown stability state.");
            Need(e.Duplex is "NOT_RUN" or "DUPLEX_QA" or "DUPLEX_WARNINGS" or "FAILED" or "INCOMPLETE" or "DUPLEX_CANCELLED", "Unknown duplex state.");
            Need(e.PhysicalQa == "NOT_RUN" || e.PhysicalQa == "KLIDAN_PHYSICAL_QA", "Unknown physical QA state.");
            Need(e.Routing is "UNKNOWN" or "OBSERVED" or "PHYSICALLY_VERIFIED", "Unknown routing state.");
            Need(e.EndpointDirection == "DUPLEX" ? boundDirections.Contains("CAPTURE") && boundDirections.Contains("RENDER") : boundDirections.Contains(e.EndpointDirection), "Evidence has no matching endpoint binding.");
        }
        var streamObservations = p.StreamObservations ?? throw new InvalidDataException("Stream observation list is missing.");
        Need(streamObservations.Count <= 64, "Stream observation list exceeds its limit.");
        Need(p.SchemaVersion == 1 ? streamObservations.Count == 0 : p.SchemaVersion == 2 ? streamObservations.Count > 0 : streamObservations.Count <= 64, "Profile schema version and stream observation presence disagree.");
        var streamIds = new HashSet<Guid>();
        foreach (var s in streamObservations)
        {
            if (s is null || s.Format is null) throw new InvalidDataException("Stream observations and formats are required.");
            Need(s.OperationId != Guid.Empty && streamIds.Add(s.OperationId) && s.Provenance == "REAL_WASAPI_STREAM_VALIDATION" && s.ObservedAtUtc.Offset == TimeSpan.Zero, "Invalid stream operation identity, provenance or timestamp.");
            Need(s.Direction is "CAPTURE" or "RENDER" && boundDirections.Contains(s.Direction), "Stream direction has no matching endpoint binding.");
            Need(s.Format.Encoding == "PCM16" && s.Format.SampleRate == 48_000 && s.Format.Channels == 2 && s.Format.ContainerBits == 16 && s.Format.ValidBits == 16 && s.Format.Period is "DEFAULT" or "MINIMUM" && s.DurationSeconds is >= 1 and <= 10, "Stream format or bounds are unsupported.");
            Need(evidenceById.TryGetValue(s.InitializeEvidenceId, out var init) && init.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY" && init.EndpointDirection == s.Direction && init.Format == s.Format, "Stream observation does not reference matching Initialize evidence.");
            Need((s.StreamQuality is "CLEAN" or "ANOMALIES_PRESENT" or "INCOMPLETE" or "UNKNOWN") && (s.Cleanup is "NOT_RUN" or "NOT_ACQUIRED" or "RELEASE_ATTEMPTED" or "CLEANUP_CONFIRMED" or "FAILED" or "INCOMPLETE" or "UNKNOWN") && !string.IsNullOrWhiteSpace(s.CleanupDetail) && s.CleanupDetail.Length <= 256, "Invalid stream quality or cleanup state.");
            Need(s.PhysicalPlayback == "NOT_MEASURED" && s.PhysicalRouting == "UNKNOWN", "Stream test cannot claim physical playback or routing.");
            Need(Hresult(s.ActivationHresult) && Hresult(s.IsFormatSupportedHresult) && Hresult(s.InitializeHresult) && Hresult(s.StartHresult) && Hresult(s.StopHresult), "Invalid stream HRESULT.");
            Need(Nonnegative(s.Events) && Nonnegative(s.Packets) && Nonnegative(s.FramesProcessed) && Nonnegative(s.ExpectedFrames) && Nonnegative(s.Discontinuities) && Nonnegative(s.TimestampErrors) && Nonnegative(s.UnavailableCaptureBuffers) && Nonnegative(s.BufferServiceFaults) && Nonnegative(s.EventWaitTimeouts), "Negative stream counters are invalid.");
            Need(s.FrameCoveragePercent is null or >= 0 and <= 1000 && s.ElapsedMilliseconds is null or >= 0 && (s.StartSucceeded != true || HresultSuccess(s.StartHresult)), "Invalid stream coverage, duration or Start evidence.");
            if (s.StreamQuality == "CLEAN")
            {
                var tolerance = Math.Max(2L * init!.BufferFrames.GetValueOrDefault(), s.ExpectedFrames.GetValueOrDefault() / 10);
                Need(s.StartSucceeded == true && s.DurationReached == true && s.StopSucceeded == true && s.Events is > 0 && s.FramesProcessed is not null && s.ExpectedFrames is > 0 && s.FramesProcessed >= s.ExpectedFrames - tolerance && s.Discontinuities == 0 && s.TimestampErrors == 0 && s.UnavailableCaptureBuffers == 0 && s.BufferServiceFaults == 0, "CLEAN stream evidence requires Start, bounded duration, sufficient servicing, fault-free counters and Stop.");
            }
        }
        var duplexObservations = p.DuplexObservations ?? throw new InvalidDataException("Duplex observation list is missing.");
        Need(duplexObservations.Count <= 2 && (p.SchemaVersion == 3 ? duplexObservations.Count is > 0 and <= 2 && boundDirections.Contains("CAPTURE") && boundDirections.Contains("RENDER") : duplexObservations.Count == 0), "Profile schema version and Duplex pair observations disagree.");
        var duplexIds = new HashSet<Guid>(); var duplexPeriods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in duplexObservations)
        {
            if (d is null || d.Report.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Duplex report is missing.");
            Need(d.EvidenceId != Guid.Empty && duplexIds.Add(d.EvidenceId) && evidenceById.TryGetValue(d.EvidenceId, out var duplexEvidence) && duplexEvidence.SourceProvenance == "REAL_WASAPI_DUPLEX" && duplexEvidence.EndpointDirection == "DUPLEX", "Duplex observation must reference exact real Duplex evidence.");
            Need(d.OperationId != Guid.Empty && (d.CaptureEndpointId == p.EndpointBindings.Capture?.EndpointId || p.Privacy == "SANITIZED_PORTABLE" && d.CaptureEndpointId == "REDACTED"), "Duplex Capture binding differs from profile pair.");
            Need(d.OperationId != Guid.Empty && (d.RenderEndpointId == p.EndpointBindings.Render?.EndpointId || p.Privacy == "SANITIZED_PORTABLE" && d.RenderEndpointId == "REDACTED"), "Duplex Render binding differs from profile pair.");
            Need(d.Period is "MINIMUM" or "DEFAULT" && d.Mmcss is "OFF" or "PRO_AUDIO" && d.DurationSeconds is >= 1 and <= 10 && duplexPeriods.Add(d.Period), "Duplex configuration is invalid or duplicated.");
            var report = d.Report;
            var reportSchema = report.GetProperty("schemaVersion").GetInt32();
            Need(reportSchema is 1 or 2 && report.GetProperty("reportType").GetString() == "wasapi-duplex-validation" && report.GetProperty("provenance").GetString() == "REAL_WASAPI_DUPLEX" && Guid.TryParse(report.GetProperty("reportId").GetString(), out _) && Guid.TryParse(report.GetProperty("operationId").GetString(), out var reportOperation) && reportOperation == d.OperationId && DateTimeOffset.TryParse(report.GetProperty("timestampUtc").GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _) && !string.IsNullOrWhiteSpace(report.GetProperty("probeVersion").GetString()), "Duplex report schema/provenance/operation identity is invalid.");
            var requested = report.GetProperty("requested");
            Need(requested.GetProperty("period").GetString() == d.Period && requested.GetProperty("mmcss").GetString() == d.Mmcss && requested.GetProperty("durationSeconds").GetInt32() == d.DurationSeconds && requested.GetProperty("sampleRateHz").GetInt32() == 48000 && requested.GetProperty("encoding").GetString() == "PCM16" && requested.GetProperty("channels").GetInt32() == 2, "Duplex report settings differ from its observation.");
            var expectedCaptureId = p.Privacy == "LOCAL_MACHINE" ? d.CaptureEndpointId : "REDACTED";
            var expectedRenderId = p.Privacy == "LOCAL_MACHINE" ? d.RenderEndpointId : "REDACTED";
            Need(requested.GetProperty("captureEndpointId").GetString() == expectedCaptureId && requested.GetProperty("renderEndpointId").GetString() == expectedRenderId, "Duplex report does not match its exact endpoint bindings.");
            var duplexStatus = report.GetProperty("duplex").GetProperty("status").GetString();
            Need(duplexStatus is "DUPLEX_CLEAN" or "DUPLEX_WARNINGS" or "FAILED" or "INCOMPLETE" or "CANCELLED", "Unknown Duplex operation outcome.");
            foreach (var (name, direction, endpointId) in new[] { ("capture", "CAPTURE", expectedCaptureId), ("render", "RENDER", expectedRenderId) })
            {
                var side = report.GetProperty(name);
                Need(side.GetProperty("direction").GetString() == direction && side.GetProperty("endpointId").GetString() == endpointId, "Duplex side identity/direction is invalid.");
                foreach (var counter in new[] { "framesProcessed", "expectedFrames", "events", "packets", "discontinuities", "timestampErrors", "serviceFaults", "eventIntervalsObserved" }) Need(side.GetProperty(counter).TryGetInt64(out var count) && count >= 0, $"Duplex counter {name}.{counter} is invalid.");
                if (reportSchema == 2) foreach (var counter in new[] { "packetDrainLimitHits", "intermediateWaitTimeouts", "longEventIntervals" }) Need(side.GetProperty(counter).TryGetInt64(out var count) && count >= 0, $"Duplex counter {name}.{counter} is invalid.");
                foreach (var field in new[] { "activationHresult", "devicePeriodHresult", "isFormatSupportedHresult", "initializeHresult", "startHresult", "stopHresult", "getBufferSizeHresult", "setEventHandleHresult", "errorHresult" }) Need(side.GetProperty(field).ValueKind is JsonValueKind.Null or JsonValueKind.String, $"Duplex HRESULT {name}.{field} is invalid.");
                Need(side.GetProperty("quality").GetString() is "CLEAN" or "ANOMALIES_PRESENT" or "SEVERE_ANOMALIES" or "UNKNOWN" && side.GetProperty("cleanup").GetString() is "NOT_ATTEMPTED" or "RELEASE_ATTEMPTED" or "UNKNOWN" or "FAILED", $"Duplex quality or cleanup state is invalid for {name}.");
            }
            if (p.Privacy == "SANITIZED_PORTABLE") Need(!ReportContainsEndpointIdentity(report), "Sanitized Duplex report retains endpoint identity.");
        }
        DeviceConfigurationProfileFactory.ValidatePassiveQueryRows(p.Evidence);
        ValidateRefs(p.CapabilityEvidence.Capture, "CAPTURE");
        ValidateRefs(p.CapabilityEvidence.Render, "RENDER");
        ValidateRefs(p.CapabilityEvidence.Duplex, "DUPLEX");
        var capabilityRefs = p.CapabilityEvidence.Capture.Concat(p.CapabilityEvidence.Render).Concat(p.CapabilityEvidence.Duplex).ToArray();
        Need(capabilityRefs.Distinct().Count() == capabilityRefs.Length && evidenceById.Keys.All(capabilityRefs.Contains), "Every evidence record must be referenced once by its matching capability direction.");
        foreach (var assessment in p.AudioHostAssessments)
        {
            if (assessment is null) throw new InvalidDataException("AudioHost assessments cannot be null.");
            if (assessment.Evidence is null || assessment.UnverifiedRequirements is null || assessment.KnownIncompatibilities is null)
                throw new InvalidDataException("AudioHost assessment arrays are required.");
            Need(assessment.Mode is "SHARED" or "COORDINATED_EXCLUSIVE", "Unknown AudioHost mode.");
            ValidateFormat(assessment.ProcessingFormat); ValidateFormat(assessment.PhysicalFormat);
            Need(assessment.Evidence.All(id => evidenceById.ContainsKey(id)), "Assessment references unknown evidence.");
            Need(assessment.UnverifiedRequirements.All(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 512) && assessment.KnownIncompatibilities.All(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 512), "AudioHost assessment text exceeds bounds.");
            Need(!assessment.EligibleForRecommendation, "C1 cannot mark an AudioHost configuration recommendation-eligible.");
        }
        Need(p.Recommendation.UnavailableReasons.Count <= 32, "Recommendation reasons are excessive.");
        Need(p.Recommendation.UnavailableReasons.All(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 512), "Recommendation reason exceeds bounds.");
        if (p.SchemaVersion < 3) Need(p.Recommendation.Preferred is null && p.Recommendation.Fallback is null && p.Recommendation.UnavailableReasons.Count > 0, "C1 recommendations must remain unavailable until stream and physical QA exist.");
        else if (p.SchemaVersion == 4)
        {
            Need(p.Recommendation.Preferred is null && p.Recommendation.Fallback is null && p.Recommendation.Scope == "NONE", "v4 sustained selection must not reuse a v3 short-run recommendation.");
            ValidateSustainedSelection(p.SustainedSelection, p.Privacy, p.EndpointBindings);
        }
        else
        {
            Need(p.Recommendation.Scope is "NONE" or "PROBE_PROVISIONAL" && p.Recommendation.Confidence is "PROVISIONAL" or "LOW" or "NOT_AVAILABLE", "Invalid v3 recommendation scope or confidence.");
            Need(p.Recommendation.Preferred is null || p.Recommendation.Scope == "PROBE_PROVISIONAL" && p.Recommendation.Preferred.Mode == "PROBE_PROVISIONAL" && p.Recommendation.Preferred.Evidence.All(id => duplexIds.Contains(id)), "Preferred choice requires matching provisional Duplex evidence.");
            Need(p.Recommendation.Fallback is null || p.Recommendation.Scope == "PROBE_PROVISIONAL" && p.Recommendation.Fallback.Mode == "PROBE_PROVISIONAL" && p.Recommendation.Fallback.Evidence.All(id => duplexIds.Contains(id)), "Fallback choice requires matching provisional Duplex evidence.");
        }
        return p;

        static void ValidateSustainedSelection(ProfileSustainedSelection? selection, string privacy, ProfileEndpointBindings bindings)
        {
            if (selection is null) throw new InvalidDataException("v4 sustained selection is required.");
            Need(Guid.TryParse(selection.SourceScanSessionId, out var sessionId) && sessionId != Guid.Empty, "v4 source scan session identity is invalid.");
            Need(selection.Scope is "SUSTAINED_VALIDATED" or "NONE" && selection.Confidence is "VERIFIED_SUSTAINED_CLEAN" or "PROVISIONAL_SINGLE_SUSTAINED_RUN" or "MIXED_RESULTS" or "WARNINGS" or "INCOMPLETE" or "UNKNOWN" or "NOT_TESTED", "Invalid v4 sustained selection state.");
            Need(selection.UnavailableReasons is not null && selection.UnavailableReasons.Count <= 32 && selection.UnavailableReasons.All(r => !string.IsNullOrWhiteSpace(r) && r.Length <= 512), "Invalid v4 sustained selection reasons.");
            var evidenceList = selection.Evidence ?? throw new InvalidDataException("v4 sustained evidence references are missing.");
            Need(evidenceList.Count <= 64, "v4 sustained evidence references exceed the bound.");
            var references = new Dictionary<Guid, ProfileSustainedEvidenceReference>();
            foreach (var evidence in evidenceList)
            {
                if (evidence is null) throw new InvalidDataException("v4 sustained evidence reference is missing.");
                var format = evidence.Format ?? throw new InvalidDataException("v4 sustained evidence format is missing.");
                Need(evidence.EvidenceId != Guid.Empty && references.TryAdd(evidence.EvidenceId, evidence), "v4 sustained evidence IDs must be unique and non-empty.");
                Need(evidence.OperationId != Guid.Empty && evidence.ReportId != Guid.Empty && evidence.SourceProvenance == "REAL_WASAPI_SUSTAINED_DUPLEX" && !string.IsNullOrWhiteSpace(evidence.SourceReference) && evidence.SourceReference.Length <= 256, "v4 sustained evidence identity or provenance is invalid.");
                Need(evidence.CaptureEndpointId == "REDACTED" && evidence.RenderEndpointId == "REDACTED" ? privacy == "SANITIZED_PORTABLE" : privacy == "LOCAL_MACHINE" && evidence.CaptureEndpointId == bindings.Capture?.EndpointId && evidence.RenderEndpointId == bindings.Render?.EndpointId, "v4 sustained evidence endpoints do not match the profile binding.");
                Need(format.Encoding == "PCM16" && format.SampleRate == 48_000 && format.Channels == 2 && format.ContainerBits == 16 && format.ValidBits == 16 && IsSustainedPeriod(format.Period), "v4 sustained format is invalid.");
                Need(IsSustainedPeriod(evidence.Period) && evidence.Period == format.Period && evidence.Mmcss is ("OFF" or "PRO_AUDIO") && evidence.DurationSeconds is (60 or 300 or 600) && evidence.CaptureBufferFrames > 0 && evidence.RenderBufferFrames > 0, "v4 sustained evidence settings or geometry are invalid.");
                Need(evidence.Quality is ("DUPLEX_CLEAN" or "DUPLEX_WARNINGS" or "INCOMPLETE" or "UNKNOWN") && evidence.Classification is ("SUSTAINED_CLEAN" or "SUSTAINED_WARNINGS" or "INCOMPLETE" or "UNKNOWN") && evidence.CleanupStatus == "RELEASE_ATTEMPTED", "v4 sustained evidence quality or cleanup is invalid.");
            }
            if (selection.Preferred is null)
            {
                Need(selection.Fallback is null && selection.Scope == "NONE", "v4 unavailable sustained selection cannot contain a configuration.");
                return;
            }
            var preferred = selection.Preferred;
            Need(selection.Scope == "SUSTAINED_VALIDATED" && preferred.DurationSeconds is (300 or 600) && preferred.Quality == "DUPLEX_CLEAN" && preferred.Confidence is ("VERIFIED_SUSTAINED_CLEAN" or "PROVISIONAL_SINGLE_SUSTAINED_RUN"), "v4 preferred configuration lacks qualifying long clean evidence.");
            ValidateConfiguration(preferred, references, privacy, bindings);
            if (selection.Fallback is { } fallback)
            {
                Need(fallback.DurationSeconds is (300 or 600) && fallback.Quality == "DUPLEX_CLEAN", "v4 fallback lacks qualifying long clean evidence.");
                ValidateConfiguration(fallback, references, privacy, bindings);
                Need(fallback.CaptureBufferFrames > preferred.CaptureBufferFrames && fallback.RenderBufferFrames > preferred.RenderBufferFrames, "v4 fallback must have a separately validated larger buffer period.");
            }
        }

        static void ValidateConfiguration(ProfileSustainedConfiguration configuration, IReadOnlyDictionary<Guid, ProfileSustainedEvidenceReference> references, string privacy, ProfileEndpointBindings bindings)
        {
            Need(configuration.Format is not null && configuration.Format.Encoding == "PCM16" && configuration.Format.SampleRate == 48_000 && configuration.Format.Channels == 2 && configuration.Format.ContainerBits == 16 && configuration.Format.ValidBits == 16 && configuration.Format.Period == configuration.Period && IsSustainedPeriod(configuration.Period), "v4 sustained configuration format is invalid.");
            Need(configuration.CaptureBufferFrames > 0 && configuration.RenderBufferFrames > 0 && configuration.Mmcss is ("OFF" or "PRO_AUDIO") && configuration.DurationSeconds is (300 or 600) && configuration.Quality == "DUPLEX_CLEAN", "v4 sustained configuration is invalid.");
            Need(configuration.CaptureEndpointId == "REDACTED" && configuration.RenderEndpointId == "REDACTED" ? privacy == "SANITIZED_PORTABLE" : privacy == "LOCAL_MACHINE" && configuration.CaptureEndpointId == bindings.Capture?.EndpointId && configuration.RenderEndpointId == bindings.Render?.EndpointId, "v4 sustained configuration endpoints do not match the profile binding.");
            Need(configuration.OperationIds is not null && configuration.ReportIds is not null && configuration.EvidenceReferences is not null && configuration.OperationIds.Count > 0 && configuration.OperationIds.All(id => id != Guid.Empty) && configuration.ReportIds.Count == configuration.OperationIds.Count && configuration.ReportIds.All(id => id != Guid.Empty) && configuration.EvidenceReferences.Count == configuration.OperationIds.Count, "v4 sustained configuration references are incomplete.");
            var operationIds = configuration.OperationIds ?? throw new InvalidDataException("v4 sustained operation references are missing.");
            var reportIds = configuration.ReportIds ?? throw new InvalidDataException("v4 sustained report references are missing.");
            var evidenceReferences = configuration.EvidenceReferences ?? throw new InvalidDataException("v4 sustained evidence references are missing.");
            Need(evidenceReferences.All(id => references.TryGetValue(id, out var evidence) && evidence.OperationId != Guid.Empty && evidence.ReportId != Guid.Empty && evidence.CaptureEndpointId == configuration.CaptureEndpointId && evidence.RenderEndpointId == configuration.RenderEndpointId && evidence.Period == configuration.Period && evidence.Mmcss == configuration.Mmcss && evidence.DurationSeconds == configuration.DurationSeconds && evidence.Quality == configuration.Quality && evidence.CaptureBufferFrames == configuration.CaptureBufferFrames && evidence.RenderBufferFrames == configuration.RenderBufferFrames && operationIds.Contains(evidence.OperationId) && reportIds.Contains(evidence.ReportId)), "v4 sustained configuration references do not match its evidence.");
        }

        static bool IsSustainedPeriod(string? period) => period is "MINIMUM" or "DEFAULT" || period is { Length: > 8 } && period.StartsWith("CUSTOM:", StringComparison.Ordinal) && long.TryParse(period.Split(':') is [_, var period100ns, _] ? period100ns : null, out var hns) && hns > 0 && int.TryParse(period.Split(':') is [_, _, var frames] ? frames : null, out var frameCount) && frameCount > 0;

        void ValidateRefs(IReadOnlyList<Guid> refs, string direction)
        {
            if (refs is null) throw new InvalidDataException($"{direction} evidence reference list is missing.");
            Need(refs.All(id => evidenceById.TryGetValue(id, out var e) && (e.EndpointDirection == direction || direction == "DUPLEX" && e.EndpointDirection == "DUPLEX")), $"Invalid {direction} evidence reference.");
            Need(refs.Distinct().Count() == refs.Count, "Duplicate capability evidence reference.");
        }
        static bool EnumEvidence(string value) => value is "NOT_RUN" or "SUPPORTED_REPORTED" or "INITIALIZED" or "STREAM_STARTED" or "STREAM_OBSERVED" or "STABILITY_QA" or "DUPLEX_QA" or "KLIDAN_PHYSICAL_QA" or "UNSUPPORTED" or "FAILED" or "INCOMPLETE" or "UNKNOWN" or "READY" or "FORMAT_ONLY" or "INITIALIZE_ONLY" or "SINGLE_STREAM_OBSERVED" or "DUPLEX_OBSERVED" or "DUPLEX_CLEAN_SHORT_RUN" or "DUPLEX_WARNINGS" or "DUPLEX_CANCELLED";
        static bool IsReady(ProfileEvidence e) => e.InitializeAttempted && IsSuccessfulHresult(e.FinalInitializeHresult) && e.BufferFrames is > 0 && e.EventReady == true && IsSuccessfulHresult(e.SetEventHandleHresult);
        static bool Hresult(string? value) => value is null || value == "S_OK" || value is { Length: 10 } && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && value.AsSpan(2).ToString().All(char.IsAsciiHexDigit) || value == "UNKNOWN";
        static bool HresultSuccess(string? value) => value == "S_OK" || value == "0x00000000";
        static bool Nonnegative(long? value) => value is null or >= 0;
        static bool ValidHresult(string? value) => value is null || value == "S_OK" || value is { Length: 10 } && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && value.AsSpan(2).ToString().All(char.IsAsciiHexDigit);
        static void ValidateFormat(ProfileAudioFormat? format)
        {
            if (format is null) return;
            if (format.Encoding is not ("PCM16" or "PCM24_IN_32" or "FLOAT32") || format.SampleRate is < 8_000 or > 384_000 || format.Channels is < 1 or > 64 || (format.Encoding, format.ContainerBits, format.ValidBits) is not (("PCM16", 16, 16) or ("PCM24_IN_32", 32, 24) or ("FLOAT32", 32, 32)) || format.Period is not (null or "DEFAULT" or "MINIMUM"))
                throw new InvalidDataException("Invalid AudioHost format assessment.");
        }
    }

    private static byte[] Serialize(DeviceConfigurationProfile profile)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(profile, Options);
        if (data.Length > MaximumBytes) throw new InvalidDataException("Profile exceeds 1 MiB.");
        return data;
    }

    private static ProfileEndpointBinding? Sanitize(ProfileEndpointBinding? binding) => binding is null ? null : binding with { EndpointId = null, FriendlyName = null, Binding = "SANITIZED_UNBOUND" };
    private static ProfileSustainedSelection SanitizeSustainedSelection(ProfileSustainedSelection selection) => selection with
    {
        Preferred = SanitizeSustainedConfiguration(selection.Preferred),
        Fallback = SanitizeSustainedConfiguration(selection.Fallback),
        Evidence = selection.Evidence.Select(e => e with { CaptureEndpointId = "REDACTED", RenderEndpointId = "REDACTED", SourceReference = $"SUSTAINED_REPORT:{e.ReportId:D}" }).ToArray()
    };
    private static ProfileSustainedConfiguration? SanitizeSustainedConfiguration(ProfileSustainedConfiguration? configuration) => configuration is null ? null : configuration with { CaptureEndpointId = "REDACTED", RenderEndpointId = "REDACTED" };

    private static JsonElement RedactEndpointIds(JsonElement element)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) WriteRedacted(element, writer); using var document = JsonDocument.Parse(stream.ToArray()); return document.RootElement.Clone();
    }
    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject(); foreach (var property in element.EnumerateObject()) { writer.WritePropertyName(property.Name); if (property.Name is "endpointId" or "captureEndpointId" or "renderEndpointId") writer.WriteStringValue("REDACTED"); else WriteRedacted(property.Value, writer); } writer.WriteEndObject(); break;
            case JsonValueKind.Array: writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteRedacted(item, writer); writer.WriteEndArray(); break;
            default: element.WriteTo(writer); break;
        }
    }
    private static bool ReportContainsEndpointIdentity(JsonElement report) => report.GetProperty("requested").GetProperty("captureEndpointId").GetString() is not "REDACTED" && report.GetProperty("requested").GetProperty("renderEndpointId").GetString() is not "REDACTED";

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}

public static class DeviceConfigurationProfileStorage
{
    public const int MaximumDiscoveredFiles = 128;
    public const long MaximumDiscoveredBytes = 16 * 1024 * 1024;

    public sealed record SavedProfileEntry(string Path, DeviceConfigurationProfile? Profile, string DisplayName, string Status, string Reason)
    {
        public bool IsAvailable => Profile is not null;
        public bool IsSustained => Profile?.SchemaVersion == 4 && Profile.SustainedSelection is not null;
        public string TechnicalDisplayName => System.IO.Path.GetFileName(Path);
        public DateTimeOffset? MeasurementTimestampUtc => Profile is null ? null : Profile.SchemaVersion >= 3
            ? Profile.DuplexObservations.Select(o => o.Report.TryGetProperty("timestampUtc", out var value) && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp.ToUniversalTime() : DateTimeOffset.MinValue).DefaultIfEmpty().Max() is var duplexTimestamp && duplexTimestamp > DateTimeOffset.MinValue ? duplexTimestamp : null
            : Profile.Evidence.Select(e => e.ObservedAtUtc).Where(t => t > DateTimeOffset.MinValue).DefaultIfEmpty().Max() is var legacyTimestamp && legacyTimestamp > DateTimeOffset.MinValue ? legacyTimestamp : null;
        public DateTimeOffset? SortTimestampUtc => MeasurementTimestampUtc ?? (Profile?.CreatedAtUtc > DateTimeOffset.MinValue ? Profile.CreatedAtUtc.ToUniversalTime() : null);
        public string OperatorTimestampLabel => SortTimestampUtc is { } timestamp
            ? $"{(MeasurementTimestampUtc is not null ? "Измерение" : "Создан профиль")}: {timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"))}"
            : "Дата неизвестна";
        public string TechnicalTimestamp => MeasurementTimestampUtc?.ToString("O", CultureInfo.InvariantCulture) ?? Profile?.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture) ?? "UNKNOWN";
        public string OperatorResult => Profile?.SchemaVersion == 4 && Profile.SustainedSelection is { } sustained
            ? $"v4 · Primary {sustained.Preferred?.Period ?? "NONE"} · {sustained.Preferred?.Mmcss ?? "—"} · {sustained.Preferred?.DurationSeconds.ToString() ?? "—"} с · {sustained.Confidence}"
            : Profile?.SchemaVersion == 3 ? "v3 · Short Duplex profile" : Profile is not null ? $"v{Profile.SchemaVersion} · Legacy profile" : "Профиль недоступен";
        public string HistoricalSelection => Profile?.SchemaVersion == 4 && Profile.SustainedSelection is { } sustained
            ? $"Primary: {sustained.Preferred?.Period ?? "NONE"} · Fallback: {sustained.Fallback?.Period ?? "NONE"} · {sustained.Confidence}"
            : Profile is not null ? $"v{Profile.SchemaVersion} · предыдущие измерения" : "Недоступно";
        public string OperatorDisplayName => Profile is { } profile
            ? profile.EndpointBindings.Capture?.FriendlyName is { Length: > 0 } capture && profile.EndpointBindings.Render?.FriendlyName is { Length: > 0 } render
                ? $"{capture} + {render}"
                : profile.Device.Name is { Length: > 0 } name ? name : "Сохранённое устройство"
            : "Профиль недоступен";
    }

    public static IReadOnlyList<SavedProfileEntry> DiscoverLocal(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "Profiles");
        if (!Directory.Exists(directory)) return [];
        var paths = Directory.EnumerateFiles(directory, "profile-*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = new List<SavedProfileEntry>();
        long totalBytes = 0;
        foreach (var path in paths.Take(MaximumDiscoveredFiles))
        {
            try
            {
                var length = new FileInfo(path).Length;
                if (length is <= 0 or > DeviceConfigurationProfileJson.MaximumBytes)
                {
                    entries.Add(new(path, null, Path.GetFileName(path), "UNAVAILABLE", "Профиль повреждён или превышает ограничение размера."));
                    continue;
                }
                if (totalBytes + length > MaximumDiscoveredBytes)
                {
                    entries.Add(new(path, null, Path.GetFileName(path), "UNAVAILABLE", "Профиль не прочитан: превышен общий лимит локального каталога."));
                    continue;
                }
                totalBytes += length;
                var profile = DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(path));
                entries.Add(new(path, profile, OperatorName(profile), "AVAILABLE", profile.SchemaVersion == 4 ? "Длительный профиль" : "Предыдущие измерения"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                entries.Add(new(path, null, Path.GetFileName(path), "UNAVAILABLE", $"Профиль повреждён или не поддерживается: {ex.Message}"));
            }
        }
        if (paths.Length > MaximumDiscoveredFiles)
            entries.Add(new(directory, null, "Ограничение каталога", "UNAVAILABLE", $"Показаны первые {MaximumDiscoveredFiles} профилей; остальные не прочитаны."));
        return entries.OrderByDescending(entry => entry.SortTimestampUtc ?? DateTimeOffset.MinValue).ThenBy(entry => entry.OperatorDisplayName, StringComparer.Ordinal).ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();

        static string OperatorName(DeviceConfigurationProfile profile) =>
            profile.EndpointBindings.Capture?.FriendlyName is { Length: > 0 } capture && profile.EndpointBindings.Render?.FriendlyName is { Length: > 0 } render
                ? $"{capture} + {render}"
                : profile.Device.Name is { Length: > 0 } name ? name : "Сохранённое устройство";
    }

    public static string SaveLocal(DeviceConfigurationProfile profile, string? directory = null)
    {
        var bytes = DeviceConfigurationProfileJson.SerializeLocal(profile);
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "Profiles");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"profile-{profile.ProfileId:D}.json");
        var temporary = Path.Combine(directory, $".{profile.ProfileId:D}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
