namespace KlidanAudioProbe.Gui.Core;

public sealed record V4AutomaticSearchResult(V2CandidateMatrixEvidence Evidence, string? EvidencePath);
public sealed record V4AutomaticSearchProgress(string Stage, int CandidateIndex, int? CaptureFrames, int? RenderFrames, int RepeatabilityCompleted, int? ActualCaptureFrames = null, int? ActualRenderFrames = null);

public interface IV4AutomaticSearchWorkflow
{
    Task<V4AutomaticSearchResult> RunAsync(WorkerScanV2V4PreparedPlan prepared, WindowsEndpointDto capture,
        WindowsEndpointDto render, string evidenceRoot, string profileDirectory, bool runRepeatability,
        IProgress<V4AutomaticSearchProgress>? progress, CancellationToken cancellationToken);
}
