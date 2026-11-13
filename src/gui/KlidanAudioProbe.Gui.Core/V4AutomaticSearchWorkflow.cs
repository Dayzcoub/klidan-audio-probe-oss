namespace KlidanAudioProbe.Gui.Core;

public sealed record V4AutomaticSearchResult(V2CandidateMatrixEvidence Evidence, string? EvidencePath)
{
    public string QualificationStatus { get; init; } = "NOT_QUALIFIED";
    public string? QualificationDiagnostic { get; init; }
    public string? ProfilePath { get; init; }
    public DeviceConfigurationProfile? PendingProfile { get; init; }
}
public sealed record V4AutomaticSearchProgress(string Stage, int CandidateIndex, int? CaptureFrames, int? RenderFrames, int RepeatabilityCompleted, int? ActualCaptureFrames = null, int? ActualRenderFrames = null);

public sealed record V4CandidateQualificationAttempt(int CandidateIndex, long Period100ns, string ScanResult,
    string ScanEvidencePath, string AudioHostResult, string Cleanup, string? QualificationEvidencePath);

public sealed record V4ScanQualificationResult(string Status, int? SelectedCandidateIndex,
    IReadOnlyList<V4CandidateQualificationAttempt> Attempts, V2CandidateMatrixEvidence Evidence,
    string? EvidencePath, string? Diagnostic, string? ProfilePath = null)
{
    public DeviceConfigurationProfile? PendingProfile { get; init; }
    public Guid? RunId { get; init; }
    public string? CaptureEndpointId { get; init; }
    public string? RenderEndpointId { get; init; }
}

public interface IV4ScanQualificationCoordinator
{
    Task<V4ScanQualificationResult> RunAsync(WorkerScanV2V4PreparedPlan prepared, WindowsEndpointDto capture,
        WindowsEndpointDto render, AudioHostQualificationAuthorization authorization, string audioHostPackage,
        string evidenceRoot, string? profileDirectory, IProgress<V4AutomaticSearchProgress>? progress,
        CancellationToken cancellationToken, bool testOnly = false);
}

public interface IV4AutomaticSearchWorkflow
{
    bool RequiresVerifiedBuildIdentity => false;
    Task<V4AutomaticSearchResult> RunAsync(WorkerScanV2V4PreparedPlan prepared, WindowsEndpointDto capture,
        WindowsEndpointDto render, string evidenceRoot, string profileDirectory, bool runRepeatability,
        IProgress<V4AutomaticSearchProgress>? progress, CancellationToken cancellationToken);

    Task<V4AutomaticSearchResult> RunDiagnosticAsync(WorkerScanV2V4PreparedPlan prepared, WindowsEndpointDto capture,
        WindowsEndpointDto render, string evidenceRoot, IProgress<V4AutomaticSearchProgress>? progress,
        CancellationToken cancellationToken) => throw new NotSupportedException("V4 point diagnostic transport is unavailable.");
}
