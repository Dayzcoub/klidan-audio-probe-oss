namespace KlidanAudioProbe.Gui.Core;

public enum PhysicalIdentityStatus { Confirmed, Unresolved, Ambiguous }

public sealed record PhysicalDeviceEndpointDetailDto(
    string GroupId,
    string DisplayName,
    string EndpointId,
    string Direction,
    string DeviceState,
    string FormFactor,
    string FormFactorEvidence,
    string ContainerId,
    string ContainerIdEvidence,
    string CandidateReason);

public sealed record PhysicalDeviceGroupDto(
    string GroupId,
    string DisplayName,
    IReadOnlyList<WindowsEndpointDto> CaptureEndpoints,
    IReadOnlyList<WindowsEndpointDto> RenderEndpoints,
    string IdentityProvenance,
    PhysicalIdentityStatus Verification,
    bool IsLoopbackCapture,
    string? CaptureEndpointId,
    string? RenderEndpointId)
{
    public IReadOnlyList<WindowsEndpointDto> CaptureCandidates => CaptureEndpoints.Where(PhysicalDeviceGrouping.IsEligibleCapture).ToArray();
    public IReadOnlyList<WindowsEndpointDto> CaptureSelectableEndpoints => CaptureEndpoints.Where(PhysicalDeviceGrouping.IsSelectableCapture).ToArray();
    public IReadOnlyList<WindowsEndpointDto> RenderCandidates => RenderEndpoints;
    public bool CanAutoSelectPair => Verification == PhysicalIdentityStatus.Confirmed &&
        CaptureCandidates.Count(e => e.DeviceState == "ACTIVE" && !string.IsNullOrWhiteSpace(e.EndpointId)) == 1 &&
        RenderCandidates.Count(e => e.DeviceState == "ACTIVE" && !string.IsNullOrWhiteSpace(e.EndpointId)) == 1;
    public int ActiveCaptureSelectableCount => CaptureSelectableEndpoints.Count(PhysicalDeviceGrouping.IsActiveEndpoint);
    public int ActiveRenderCount => RenderCandidates.Count(PhysicalDeviceGrouping.IsActiveEndpoint);
    public bool RequiresExplicitPair => (Verification is PhysicalIdentityStatus.Confirmed or PhysicalIdentityStatus.Ambiguous) &&
        ActiveCaptureSelectableCount > 0 && ActiveRenderCount > 0 && !CanAutoSelectPair;
    public bool RequiresExplicitCapture => Verification == PhysicalIdentityStatus.Ambiguous && ActiveCaptureSelectableCount > 1 && ActiveRenderCount == 0;
    public bool RequiresExplicitRender => Verification == PhysicalIdentityStatus.Ambiguous && ActiveRenderCount > 1 && ActiveCaptureSelectableCount == 0;
    public bool HasPartialCapture => ActiveCaptureSelectableCount == 1 && ActiveRenderCount == 0;
    public bool HasPartialRender => ActiveRenderCount == 1 && ActiveCaptureSelectableCount == 0;
    public string DirectionSummary => Localization.Get(CaptureEndpoints.Any(PhysicalDeviceGrouping.IsActiveEndpoint) && RenderEndpoints.Any(PhysicalDeviceGrouping.IsActiveEndpoint)
        ? "VmPhysicalGroupInputOutput" : CaptureEndpoints.Any(PhysicalDeviceGrouping.IsActiveEndpoint) ? "VmPhysicalGroupInput" : "VmPhysicalGroupOutput");
    public string VerificationLabel => Verification switch
    {
        PhysicalIdentityStatus.Confirmed when CanAutoSelectPair => Localization.Get("VmPhysicalGroupConfirmedPair"),
        PhysicalIdentityStatus.Confirmed when HasPartialCapture => Localization.Get("VmPhysicalGroupCaptureAvailable"),
        PhysicalIdentityStatus.Confirmed when HasPartialRender => Localization.Get("VmPhysicalGroupRenderAvailable"),
        PhysicalIdentityStatus.Confirmed => Localization.Get("VmPhysicalGroupChooseEndpoint"),
        PhysicalIdentityStatus.Ambiguous when RequiresExplicitCapture => Localization.Get("VmPhysicalGroupChooseCapture"),
        PhysicalIdentityStatus.Ambiguous when RequiresExplicitRender => Localization.Get("VmPhysicalGroupChooseRender"),
        PhysicalIdentityStatus.Ambiguous => Localization.Get("VmPhysicalGroupChoosePair"),
        _ => Localization.Get("VmPhysicalGroupUnconfirmed")
    };
    public string IdentityProvenanceLabel => IdentityProvenance == "WINDOWS_DEVICE_CONTAINER_ID"
        ? Localization.Get("VmPhysicalGroupContainerSource")
        : Localization.Get("VmPhysicalGroupIdentityUnknown");
}

public static class PhysicalDeviceGrouping
{
    public static IReadOnlyList<PhysicalDeviceGroupDto> Group(IEnumerable<WindowsEndpointDto> endpoints)
    {
        var valid = endpoints.Where(e => e is not null && e.EndpointIdEvidence.State == "AVAILABLE" && !string.IsNullOrWhiteSpace(e.EndpointId)).ToArray();
        var groups = valid.GroupBy(e => e.ContainerIdEvidence?.State == "AVAILABLE" && !string.IsNullOrWhiteSpace(e.ContainerId)
                ? $"container:{e.ContainerId}"
                : $"endpoint:{e.EndpointId}", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var members = g.ToArray();
                var capture = members.Where(e => e.Direction == "CAPTURE").ToArray();
                var render = members.Where(e => e.Direction == "RENDER").ToArray();
                var confirmed = members.All(e => e.ContainerIdEvidence?.State == "AVAILABLE" && !string.IsNullOrWhiteSpace(e.ContainerId));
                var loopback = capture.Any(IsLoopbackEndpoint);
                var captureCandidates = capture.Where(IsEligibleCapture).ToArray();
                var captureSelectable = capture.Where(IsSelectableCapture).ToArray();
                var renderCandidates = render;
                var status = confirmed && captureSelectable.Length <= 1 && renderCandidates.Length <= 1 ? PhysicalIdentityStatus.Confirmed :
                    confirmed ? PhysicalIdentityStatus.Ambiguous : PhysicalIdentityStatus.Unresolved;
                var name = StableDisplayName(members);
                return new PhysicalDeviceGroupDto(g.Key, name, capture, render,
                    confirmed ? "WINDOWS_DEVICE_CONTAINER_ID" : "NO_TRUSTED_PHYSICAL_IDENTITY",
                    status, loopback,
                    captureSelectable.Length == 1 ? captureSelectable[0].EndpointId : null,
                    renderCandidates.Length == 1 ? renderCandidates[0].EndpointId : null);
            })
            .ToArray();
        return groups.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.GroupId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string StableDisplayName(IEnumerable<WindowsEndpointDto> endpoints) => endpoints
        .OrderBy(e => !IsActiveEndpoint(e))
        .ThenBy(IsLoopbackEndpoint)
        .ThenBy(e => e.FriendlyNameDisplay, StringComparer.OrdinalIgnoreCase)
        .ThenBy(e => e.EndpointId, StringComparer.OrdinalIgnoreCase)
        .Where(e => e.FriendlyNameEvidence.State == "AVAILABLE" && !string.IsNullOrWhiteSpace(e.FriendlyName))
        .Select(e => e.FriendlyName!)
        .FirstOrDefault() ?? Localization.Get("VmPhysicalDeviceNameUnavailable");

    internal static bool IsLoopbackEndpoint(WindowsEndpointDto endpoint)
    {
        var normalized = new string(endpoint.FriendlyNameDisplay.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Contains("loopback", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsActiveEndpoint(WindowsEndpointDto endpoint) => endpoint.DeviceState == "ACTIVE" && !string.IsNullOrWhiteSpace(endpoint.EndpointId);

    internal static bool IsSelectableCapture(WindowsEndpointDto endpoint) => !IsLoopbackEndpoint(endpoint);

    internal static bool IsEligibleCapture(WindowsEndpointDto endpoint) =>
        IsSelectableCapture(endpoint) && endpoint.FormFactorEvidence.State == "AVAILABLE" && endpoint.FormFactor != "UNKNOWN";
}
