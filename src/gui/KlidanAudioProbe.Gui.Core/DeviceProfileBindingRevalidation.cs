using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public enum DeviceProfileBindingAssessment
{
    ExactBindingsPresentInDiscovery,
    CaptureMissing,
    RenderMissing,
    BothEndpointsMissing,
    EndpointDisabledOrUnplugged,
    AmbiguousOrDuplicateIdentity,
    DiscoveryIncomplete,
    DiscoveryFailed,
    DiscoveryProvenanceInvalid,
    ProfileInvalidOrUnsupported,
    SanitizedUnboundProfile,
    AvailableMetadataContradiction,
    MetadataUnavailableOrInsufficient,
    BindingInvalidated
}

public enum DeviceProfileEndpointAssessment
{
    ExactActive,
    Missing,
    DiscoveryIncomplete,
    DisabledOrUnplugged,
    AmbiguousOrDuplicateIdentity,
    MetadataContradiction,
    MetadataUnavailableOrInsufficient,
    NotExamined
}

public enum DeviceProfileBindingInvalidationReason
{
    WorkerDisconnected,
    WorkerRestarted,
    WorkerReplaced,
    EndpointRediscovered,
    DeviceRemoved,
    ProfileSelectionChanged,
    DiscoverySnapshotStale
}

public sealed record DeviceProfileBindingRevalidationResult(
    DeviceProfileBindingAssessment Overall,
    DeviceProfileEndpointAssessment Capture,
    DeviceProfileEndpointAssessment Render,
    Guid SourceProfileId,
    string? SourceScanSessionId,
    IReadOnlyList<string> ExactEndpointIdentities,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Limitations,
    bool RequiresFurtherVerification,
    string? DiscoveryGeneration,
    string? ProfileSelectionKey,
    bool IsCurrent = true);

public static class DeviceProfileBindingRevalidation
{
    private const string ExclusiveVerificationLimitation = "EXCLUSIVE_FORMAT_AND_SUSTAINED_STABILITY_REQUIRE_FRESH_VERIFICATION";
    private const string MetadataLimitation = "DRIVER_FIRMWARE_USB_MODE_METADATA_UNAVAILABLE";

    public static DeviceProfileBindingRevalidationResult Evaluate(DeviceConfigurationProfile profile, WindowsEndpointDiscoveryDto discovery, string? discoveryGeneration = null, string? profileSelectionKey = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var baseResult = Identity(profile, discoveryGeneration, profileSelectionKey);
        try
        {
            DeviceConfigurationProfileJson.Validate(profile);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
        {
            return baseResult with { Overall = DeviceProfileBindingAssessment.ProfileInvalidOrUnsupported, Reasons = [$"PROFILE_INVALID_OR_UNSUPPORTED: {ex.Message}"], RequiresFurtherVerification = true };
        }

        if (profile.StableShortSelection?.ProfileKind == "TEST_ONLY")
            return baseResult with { Overall = DeviceProfileBindingAssessment.ProfileInvalidOrUnsupported, Reasons = ["TEST_ONLY_PROFILE_CANNOT_BE_BOUND_AS_A_PHYSICAL_DEVICE"], RequiresFurtherVerification = true };

        if (profile.Privacy != "LOCAL_MACHINE" || profile.EndpointBindings.Capture?.Binding != "LOCAL_EXACT" || profile.EndpointBindings.Render?.Binding != "LOCAL_EXACT")
            return baseResult with { Overall = DeviceProfileBindingAssessment.SanitizedUnboundProfile, Capture = DeviceProfileEndpointAssessment.NotExamined, Render = DeviceProfileEndpointAssessment.NotExamined, Reasons = ["PROFILE_ENDPOINT_BINDINGS_ARE_SANITIZED_OR_UNBOUND"], Limitations = [ExclusiveVerificationLimitation], RequiresFurtherVerification = true };

        if (discovery is null)
            return baseResult with { Overall = DeviceProfileBindingAssessment.DiscoveryFailed, Capture = DeviceProfileEndpointAssessment.NotExamined, Render = DeviceProfileEndpointAssessment.NotExamined, Reasons = ["DISCOVERY_RESULT_MISSING"], Limitations = [MetadataLimitation, ExclusiveVerificationLimitation], RequiresFurtherVerification = true };
        if (discovery.Provenance != "REAL_WINDOWS_METADATA")
            return baseResult with { Overall = DeviceProfileBindingAssessment.DiscoveryProvenanceInvalid, Capture = DeviceProfileEndpointAssessment.NotExamined, Render = DeviceProfileEndpointAssessment.NotExamined, Reasons = ["DISCOVERY_PROVENANCE_IS_NOT_REAL_WINDOWS_METADATA"], Limitations = [MetadataLimitation, ExclusiveVerificationLimitation], RequiresFurtherVerification = true };
        if (discovery.OperationState is not ("COMPLETE" or "PARTIAL"))
            return baseResult with { Overall = DeviceProfileBindingAssessment.DiscoveryFailed, Capture = DeviceProfileEndpointAssessment.NotExamined, Render = DeviceProfileEndpointAssessment.NotExamined, Reasons = ["DISCOVERY_OPERATION_FAILED_OR_UNKNOWN"], Limitations = [MetadataLimitation, ExclusiveVerificationLimitation], RequiresFurtherVerification = true };
        try
        {
            WindowsEndpointDiscoveryValidation.Validate(discovery);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            return baseResult with { Overall = DeviceProfileBindingAssessment.DiscoveryFailed, Capture = DeviceProfileEndpointAssessment.NotExamined, Render = DeviceProfileEndpointAssessment.NotExamined, Reasons = [$"DISCOVERY_CONTRACT_INVALID: {ex.Message}"], Limitations = [MetadataLimitation, ExclusiveVerificationLimitation], RequiresFurtherVerification = true };
        }

        var duplicateIds = discovery.Endpoints.Where(e => !string.IsNullOrWhiteSpace(e.EndpointId)).GroupBy(e => e.EndpointId!, StringComparer.Ordinal).Any(g => g.Count() > 1);
        if (duplicateIds)
            return baseResult with { Overall = DeviceProfileBindingAssessment.AmbiguousOrDuplicateIdentity, Capture = DeviceProfileEndpointAssessment.AmbiguousOrDuplicateIdentity, Render = DeviceProfileEndpointAssessment.AmbiguousOrDuplicateIdentity, Reasons = ["DISCOVERY_CONTAINS_DUPLICATE_ENDPOINT_IDENTITIES"], Limitations = [MetadataLimitation, ExclusiveVerificationLimitation], RequiresFurtherVerification = true };

        var capture = AssessEndpoint(profile.EndpointBindings.Capture!, "CAPTURE", discovery.Endpoints);
        var render = AssessEndpoint(profile.EndpointBindings.Render!, "RENDER", discovery.Endpoints);
        var reasons = new List<string>();
        var limitations = new List<string> { MetadataLimitation, ExclusiveVerificationLimitation };
        var overall = discovery.OperationState == "PARTIAL" ? DeviceProfileBindingAssessment.DiscoveryIncomplete : Overall(capture, render);
        if (discovery.OperationState == "PARTIAL") reasons.Add("DISCOVERY_SNAPSHOT_IS_PARTIAL; MISSING_ENDPOINTS_ARE_NOT_DEFINITIVE");
        if (capture == DeviceProfileEndpointAssessment.MetadataContradiction || render == DeviceProfileEndpointAssessment.MetadataContradiction) reasons.Add("AVAILABLE_ENDPOINT_METADATA_CONTRADICTS_THE_PROFILE_BINDING");
        if (capture == DeviceProfileEndpointAssessment.Missing) reasons.Add("PROFILE_CAPTURE_ENDPOINT_IS_MISSING_FROM_COMPLETE_DISCOVERY");
        if (render == DeviceProfileEndpointAssessment.Missing) reasons.Add("PROFILE_RENDER_ENDPOINT_IS_MISSING_FROM_COMPLETE_DISCOVERY");
        if (capture == DeviceProfileEndpointAssessment.DisabledOrUnplugged || render == DeviceProfileEndpointAssessment.DisabledOrUnplugged) reasons.Add("PROFILE_ENDPOINT_IS_NOT_ACTIVE");
        return baseResult with
        {
            Overall = overall,
            Capture = capture,
            Render = render,
            ExactEndpointIdentities = [profile.EndpointBindings.Capture.EndpointId!, profile.EndpointBindings.Render.EndpointId!],
            Reasons = reasons,
            Limitations = limitations,
            RequiresFurtherVerification = true
        };
    }

    public static DeviceProfileBindingRevalidationResult Evaluate(ReadOnlySpan<byte> profileBytes, WindowsEndpointDiscoveryDto discovery, string? discoveryGeneration = null, string? profileSelectionKey = null)
    {
        try
        {
            return Evaluate(DeviceConfigurationProfileJson.Parse(profileBytes.ToArray()), discovery, discoveryGeneration, profileSelectionKey);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
        {
            return new(DeviceProfileBindingAssessment.ProfileInvalidOrUnsupported, DeviceProfileEndpointAssessment.NotExamined, DeviceProfileEndpointAssessment.NotExamined, Guid.Empty, null, [], [$"PROFILE_INVALID_OR_UNSUPPORTED: {ex.Message}"], [ExclusiveVerificationLimitation], true, discoveryGeneration, profileSelectionKey);
        }
    }

    private static DeviceProfileBindingRevalidationResult Identity(DeviceConfigurationProfile profile, string? generation, string? selection) =>
        new(DeviceProfileBindingAssessment.ProfileInvalidOrUnsupported, DeviceProfileEndpointAssessment.NotExamined, DeviceProfileEndpointAssessment.NotExamined, profile.ProfileId, profile.SustainedSelection?.SourceScanSessionId, [], [], [], true, generation, selection);

    private static DeviceProfileEndpointAssessment AssessEndpoint(ProfileEndpointBinding binding, string direction, IReadOnlyList<WindowsEndpointDto> endpoints)
    {
        var matches = endpoints.Where(e => e.EndpointId == binding.EndpointId).ToArray();
        if (matches.Length == 0) return DeviceProfileEndpointAssessment.Missing;
        if (matches.Length != 1) return DeviceProfileEndpointAssessment.AmbiguousOrDuplicateIdentity;
        var endpoint = matches[0];
        if (endpoint.Direction != direction) return DeviceProfileEndpointAssessment.MetadataContradiction;
        if (endpoint.EndpointIdEvidence.State != "AVAILABLE" || string.IsNullOrWhiteSpace(endpoint.EndpointId)) return DeviceProfileEndpointAssessment.MetadataUnavailableOrInsufficient;
        return endpoint.DeviceState switch
        {
            "ACTIVE" => DeviceProfileEndpointAssessment.ExactActive,
            "DISABLED" or "UNPLUGGED" or "NOT_PRESENT" => DeviceProfileEndpointAssessment.DisabledOrUnplugged,
            _ => DeviceProfileEndpointAssessment.MetadataUnavailableOrInsufficient
        };
    }

    private static DeviceProfileBindingAssessment Overall(DeviceProfileEndpointAssessment capture, DeviceProfileEndpointAssessment render) =>
        capture is DeviceProfileEndpointAssessment.AmbiguousOrDuplicateIdentity || render is DeviceProfileEndpointAssessment.AmbiguousOrDuplicateIdentity ? DeviceProfileBindingAssessment.AmbiguousOrDuplicateIdentity :
        capture is DeviceProfileEndpointAssessment.MetadataContradiction || render is DeviceProfileEndpointAssessment.MetadataContradiction ? DeviceProfileBindingAssessment.AvailableMetadataContradiction :
        capture is DeviceProfileEndpointAssessment.DisabledOrUnplugged || render is DeviceProfileEndpointAssessment.DisabledOrUnplugged ? DeviceProfileBindingAssessment.EndpointDisabledOrUnplugged :
        capture == DeviceProfileEndpointAssessment.Missing && render == DeviceProfileEndpointAssessment.Missing ? DeviceProfileBindingAssessment.BothEndpointsMissing :
        capture == DeviceProfileEndpointAssessment.Missing ? DeviceProfileBindingAssessment.CaptureMissing :
        render == DeviceProfileEndpointAssessment.Missing ? DeviceProfileBindingAssessment.RenderMissing :
        capture is DeviceProfileEndpointAssessment.MetadataUnavailableOrInsufficient || render is DeviceProfileEndpointAssessment.MetadataUnavailableOrInsufficient ? DeviceProfileBindingAssessment.MetadataUnavailableOrInsufficient :
        DeviceProfileBindingAssessment.ExactBindingsPresentInDiscovery;
}

public sealed class DeviceProfileBindingRevalidationState
{
    public DeviceProfileBindingRevalidationResult? Current { get; private set; }
    public DeviceProfileBindingRevalidationResult? LastInvalidation { get; private set; }

    public DeviceProfileBindingRevalidationResult Evaluate(DeviceConfigurationProfile profile, WindowsEndpointDiscoveryDto discovery, string discoveryGeneration, string profileSelectionKey)
    {
        if (Current is { } prior && (prior.DiscoveryGeneration != discoveryGeneration || prior.ProfileSelectionKey != profileSelectionKey || prior.SourceProfileId != profile.ProfileId))
            Invalidate(prior.ProfileSelectionKey != profileSelectionKey || prior.SourceProfileId != profile.ProfileId ? DeviceProfileBindingInvalidationReason.ProfileSelectionChanged : DeviceProfileBindingInvalidationReason.EndpointRediscovered);
        var result = DeviceProfileBindingRevalidation.Evaluate(profile, discovery, discoveryGeneration, profileSelectionKey);
        Current = result;
        return result;
    }

    public DeviceProfileBindingRevalidationResult Invalidate(DeviceProfileBindingInvalidationReason reason)
    {
        var prior = Current ?? new(DeviceProfileBindingAssessment.BindingInvalidated, DeviceProfileEndpointAssessment.NotExamined, DeviceProfileEndpointAssessment.NotExamined, Guid.Empty, null, [], [], [], true, null, null);
        Current = prior with
        {
            Overall = DeviceProfileBindingAssessment.BindingInvalidated,
            IsCurrent = false,
            Reasons = prior.Reasons.Append($"BINDING_INVALIDATED:{ReasonCode(reason)}").ToArray(),
            RequiresFurtherVerification = true
        };
        LastInvalidation = Current;
        return Current;
    }

    private static string ReasonCode(DeviceProfileBindingInvalidationReason reason) => reason switch
    {
        DeviceProfileBindingInvalidationReason.WorkerDisconnected => "WORKER_DISCONNECTED",
        DeviceProfileBindingInvalidationReason.WorkerRestarted => "WORKER_RESTARTED",
        DeviceProfileBindingInvalidationReason.WorkerReplaced => "WORKER_REPLACED",
        DeviceProfileBindingInvalidationReason.EndpointRediscovered => "ENDPOINT_REDISCOVERED",
        DeviceProfileBindingInvalidationReason.DeviceRemoved => "DEVICE_REMOVED",
        DeviceProfileBindingInvalidationReason.ProfileSelectionChanged => "PROFILE_SELECTION_CHANGED",
        DeviceProfileBindingInvalidationReason.DiscoverySnapshotStale => "DISCOVERY_SNAPSHOT_STALE",
        _ => "UNKNOWN"
    };
}
