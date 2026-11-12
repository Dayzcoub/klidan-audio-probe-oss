namespace KlidanAudioProbe.Gui.Core;

public sealed record AudioHostQualificationAuthorization(Guid ConfirmationId, string PlanFingerprint, ulong DiscoveryGeneration,
    string CaptureEndpointId, string RenderEndpointId, bool Accepted, bool OpenSpeakersConfirmedOff,
    int MaximumRuns, int MaximumDurationSeconds, DateTimeOffset ExpiresAtUtc);

public sealed record AudioHostCandidateQualificationRequest(string PackageDirectory, string EvidenceRoot,
    string ShortSearchSummaryPath, string RepeatabilitySummaryPath, string RepeatabilityMatrixPath,
    WorkerScanV2V4PreparedPlan Prepared, WorkerScanV2Candidate Candidate,
    AudioHostQualificationAuthorization Authorization, bool TestOnly = false, string? TestFixture = null);

public sealed record AudioHostCandidateQualificationResult(string Status, string Cleanup, string? Diagnostic,
    string? RunId, string? QualificationConfigSha256, string? ResultSha256, string? AudioHostBuildSha,
    string? SourceEvidenceSha256, string? ResultPath, string Provenance);

public interface IAudioHostQualificationAdapter
{
    Task<AudioHostCandidateQualificationResult> QualifyAsync(AudioHostCandidateQualificationRequest request, CancellationToken cancellationToken);
}
