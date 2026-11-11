using System.Text.Json;
using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

public sealed record V4AutomaticSearchResult(V2CandidateMatrixEvidence Evidence, string? EvidencePath);

/// <summary>Testable, explicitly invoked v4 search. This is not wired to the GUI scan command.</summary>
public sealed class WorkerV4AutomaticSearchWorkflow(NativeWorkerClient worker)
{
    public async Task<V4AutomaticSearchResult> RunAsync(WorkerScanV2V4PreparedPlan prepared,
        string? evidenceRoot = null, CancellationToken cancellationToken = default,
        Func<int, string, string?>? testScenario = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var plan = prepared.Plan;
        var consent = prepared.Consent;
        if (!consent.Accepted || consent.ConfirmationId == Guid.Empty || consent.DiscoveryGeneration != worker.DiscoveryGeneration ||
            WorkerScanV2ContractV4Fingerprint.Compute(prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, consent.DiscoveryGeneration) != consent.PlanFingerprint)
            throw new InvalidOperationException("V4 search requires the exact current prepared plan and accepted consent receipt.");
        var routeFingerprint = WorkerScanPlanV2Fingerprint.Route(prepared.CaptureEndpointId, prepared.RenderEndpointId, plan.Route);
        var entries = plan.Candidates.Select(c => new V2CandidateMatrixEntry(c, "NOT_TESTED", "NOT_TESTED", "NOT_TESTED", "NOT_ATTEMPTED", "NOT_ATTEMPTED", [], true,
            c.RejectionReason.Length == 0 ? "NOT_RUN" : c.RejectionReason, "INCOMPLETE", "NOT_RUN", "NOT_RUN")).ToArray();
        var operations = 0; var activeSeconds = 0; var selected = (int?)null; var state = "MINIMUM_NOT_PROVEN"; var lifecycle = "COMPLETED"; var stopReason = "CANDIDATE_DOMAIN_EXHAUSTED";
        WorkerScanSessionToken? session = null;
        var attemptedProvenance = new HashSet<string>(StringComparer.Ordinal);
        void Update(int index, Func<V2CandidateMatrixEntry, V2CandidateMatrixEntry> update) => entries[index] = update(entries[index]);
        try
        {
            session = await worker.RegisterScanSessionV2V4Async(prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, consent, cancellationToken).ConfigureAwait(false);
            foreach (var candidate in plan.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (worker.DiscoveryGeneration != consent.DiscoveryGeneration || session.ContractVersion != 4 || session.RouteFingerprint != routeFingerprint)
                    throw new InvalidOperationException("V4 plan generation or route changed during search.");
                if (candidate.RejectionReason.Length != 0)
                {
                    Update(candidate.Index, e => e with { CaptureInitializeDisposition = "REJECTED", RenderInitializeDisposition = "REJECTED", DuplexDisposition = "REJECTED", SkipReason = candidate.RejectionReason, ReasonCodes = ["CANDIDATE_CONSTRAINT_REJECTED"] });
                    continue;
                }

                var capture = await Initialize(candidate, "CAPTURE");
                Update(candidate.Index, e => e with { CaptureInitializeDisposition = MatrixInitialize(capture), CaptureCleanup = capture.GetProperty("cleanup").GetString()!, ActualCaptureFrames = Positive(capture, "actualFrames"), ActualCapturePeriod100ns = PositiveLong(capture, "actualPeriod100ns"), Provenance = capture.GetProperty("provenance").GetString()! });
                if (capture.GetProperty("initializeDisposition").GetString() != "INITIALIZED_EXACT") { FailInitialize(candidate.Index, "CAPTURE", capture); if (capture.GetProperty("initializeDisposition").GetString() == "UNKNOWN" || capture.GetProperty("cleanup").GetString() != "RELEASE_ATTEMPTED") { state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "CAPTURE_INITIALIZE_UNKNOWN"; break; } continue; }

                var render = await Initialize(candidate, "RENDER");
                Update(candidate.Index, e => e with { RenderInitializeDisposition = MatrixInitialize(render), RenderCleanup = render.GetProperty("cleanup").GetString()!, ActualRenderFrames = Positive(render, "actualFrames"), ActualRenderPeriod100ns = PositiveLong(render, "actualPeriod100ns"), Provenance = render.GetProperty("provenance").GetString()! });
                if (render.GetProperty("initializeDisposition").GetString() != "INITIALIZED_EXACT") { FailInitialize(candidate.Index, "RENDER", render); if (render.GetProperty("initializeDisposition").GetString() == "UNKNOWN" || render.GetProperty("cleanup").GetString() != "RELEASE_ATTEMPTED") { state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "RENDER_INITIALIZE_UNKNOWN"; break; } continue; }

                if (!Exact(capture, candidate, true) || !Exact(render, candidate, false))
                {
                    Update(candidate.Index, e => e with { DuplexDisposition = "FAILED", DuplexNotRun = true, SkipReason = "EXACT_INITIALIZE_GEOMETRY_MISMATCH", ReasonCodes = ["INITIALIZE_GEOMETRY_MISMATCH"] });
                    continue;
                }
                var operationId = Guid.NewGuid(); operations++;
                var duplex = await worker.ValidateCustomDuplexV2InScanAsync(session, operationId, plan.Route, prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, candidate.Index,
                    cancellationToken, testScenario?.Invoke(candidate.Index, "DUPLEX"));
                var provenance = duplex.GetProperty("provenance").GetString()!; attemptedProvenance.Add(provenance);
                activeSeconds += 5;
                var classification = V4ShortCleanEvaluator.Classify(prepared, session, candidate.Index, operationId, duplex);
                Update(candidate.Index, e => e with { DuplexDisposition = classification, DuplexNotRun = false, SkipReason = null, ReasonCodes = [duplex.GetProperty("reasonCode").GetString()!],
                    ActualCaptureFrames = Positive(duplex, "actualCaptureFrames"), ActualCapturePeriod100ns = PositiveLong(duplex, "actualCapturePeriod100ns"),
                    ActualRenderFrames = Positive(duplex, "actualRenderFrames"), ActualRenderPeriod100ns = PositiveLong(duplex, "actualRenderPeriod100ns"), Provenance = provenance,
                    CaptureCleanup = CleanupSide(duplex, "CAPTURE"), RenderCleanup = CleanupSide(duplex, "RENDER") });
                if (classification == "UNKNOWN") { state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "DUPLEX_UNKNOWN_OR_INVALID_EVIDENCE"; break; }
                if (classification == "CLEAN")
                {
                    selected = candidate.Index; state = "MINIMUM_SHORT_CLEAN_FOUND"; stopReason = "SMALLER_SHORT_CLEAN_FOUND";
                    foreach (var larger in plan.Candidates.Where(c => c.Index > candidate.Index)) Update(larger.Index, e => e with { CaptureInitializeDisposition = "SKIPPED", RenderInitializeDisposition = "SKIPPED", DuplexDisposition = "SKIPPED", DuplexNotRun = true, SkipReason = "SMALLER_SHORT_CLEAN_FOUND", ReasonCodes = ["SMALLER_SHORT_CLEAN_FOUND"] });
                    break;
                }
            }
        }
        catch (OperationCanceledException) { state = "UNKNOWN"; lifecycle = "CANCELLED"; stopReason = "OPERATOR_CANCELLED"; throw; }
        catch (Exception) { state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "WORKER_OR_EVIDENCE_FAILURE"; throw; }
        finally
        {
            if (session is { } active)
            {
                try
                {
                    if (state == "UNKNOWN" || lifecycle == "CANCELLED") await worker.CancelScanSessionAsync(active, CancellationToken.None).ConfigureAwait(false);
                    else await worker.CloseScanSessionAsync(active, CancellationToken.None).ConfigureAwait(false);
                }
                catch { state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "SESSION_CLEANUP_UNKNOWN"; }
            }
        }

        var smallest = selected is { } selectedIndex ? selectedIndex : plan.Candidates.Count;
        for (var i = 0; i < entries.Length; i++)
        {
            var item = entries[i];
            var terminal = item.DuplexDisposition is "CLEAN" or "FAILED" or "UNSUPPORTED" or "REJECTED";
            var coverage = i < smallest && terminal ? "COMPLETE" : i >= smallest && selected is not null ? "COMPLETE" : state == "UNKNOWN" && !terminal ? "UNKNOWN" : "INCOMPLETE";
            Update(i, e => e with { LowerPeriodCoverage = coverage, Provenance = e.Provenance == "NOT_RUN" ? (attemptedProvenance.Count == 1 ? attemptedProvenance.Single() : attemptedProvenance.Count == 0 ? "PLAN_ONLY" : "MIXED_PROVENANCE") : e.Provenance });
        }
        var evidenceProvenance = attemptedProvenance.Count == 1 ? attemptedProvenance.Single() : attemptedProvenance.Count == 0 ? "PLAN_ONLY" : "MIXED_PROVENANCE";
        var result = new V2CandidateMatrixEvidence(1, consent.PlanFingerprint, routeFingerprint, lifecycle, plan.CaptureBounds, plan.RenderBounds, plan.Candidates, entries, state, evidenceProvenance)
        {
            EvidenceVersion = 2, DiscoveryGeneration = consent.DiscoveryGeneration, SelectedCandidateIndex = selected,
            GlobalMinimumProven = false, UsedOperations = operations,
            MaximumOperations = plan.MaximumOperations, UsedActiveDuplexSeconds = activeSeconds, MaximumActiveDuplexSeconds = plan.MaximumActiveDuplexSeconds,
            StopReason = stopReason, RepeatabilityPending = state == "MINIMUM_SHORT_CLEAN_FOUND"
        };
        if (result.UsedOperations > result.MaximumOperations) throw new InvalidDataException("V4 audio operations exceeded the consented budget.");
        var path = evidenceRoot is null ? null : V2CandidateMatrixEvidenceJson.SaveNoOverwriteAndVerify(result, Path.Combine(evidenceRoot, $"v4-search-{Guid.NewGuid():D}.json"));
        return new(result, path);

        async Task<JsonElement> Initialize(WorkerScanV2Candidate candidate, string direction)
        {
            operations++;
            var value = await worker.InitializeCustomPeriodV2InScanAsync(session!, Guid.NewGuid(), prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, candidate.Index, direction, cancellationToken,
                testScenario?.Invoke(candidate.Index, direction + "_INITIALIZE")).ConfigureAwait(false);
            attemptedProvenance.Add(value.GetProperty("provenance").GetString()!);
            return value;
        }
        void FailInitialize(int index, string direction, JsonElement value)
        {
            var disposition = value.GetProperty("initializeDisposition").GetString()!;
            Update(index, e => e with { DuplexDisposition = disposition == "UNSUPPORTED" ? "UNSUPPORTED" : "FAILED", DuplexNotRun = true, SkipReason = direction + "_INITIALIZE_" + disposition,
                ReasonCodes = [value.GetProperty("reasonCode").GetString()!], Provenance = value.GetProperty("provenance").GetString()! });
        }
    }

    private static int? Positive(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var v) && v > 0 ? v : null;
    private static string MatrixInitialize(JsonElement e) => e.GetProperty("initializeDisposition").GetString() == "INITIALIZED_EXACT" ? "INITIALIZED" : e.GetProperty("initializeDisposition").GetString()!;
    private static long? PositiveLong(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var v) && v > 0 ? v : null;
    private static bool Exact(JsonElement e, WorkerScanV2Candidate c, bool capture) => e.GetProperty("initializeDisposition").GetString() == "INITIALIZED_EXACT" &&
        e.GetProperty("actualFrames").GetInt32() == (capture ? c.CaptureFrames : c.RenderFrames) && e.GetProperty("actualPeriod100ns").GetInt64() == (capture ? c.CapturePeriod100ns : c.RenderPeriod100ns) && e.GetProperty("cleanup").GetString() == "RELEASE_ATTEMPTED";
    private static string CleanupSide(JsonElement e, string side) => e.GetProperty("cleanup").GetString() == "CAPTURE:RELEASE_ATTEMPTED;RENDER:RELEASE_ATTEMPTED" ? "RELEASE_ATTEMPTED" : "UNKNOWN";
}
