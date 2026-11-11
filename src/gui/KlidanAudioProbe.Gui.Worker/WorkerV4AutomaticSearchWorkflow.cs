using System.Text.Json;
using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

/// <summary>Runs the explicitly consented V4 search through one worker session.</summary>
public sealed class WorkerV4AutomaticSearchWorkflow(NativeWorkerClient worker) : IV4AutomaticSearchWorkflow
{
    public async Task<V4AutomaticSearchResult> RepeatSelectedAsync(WorkerScanV2V4PreparedPlan prepared, string sourceEvidenceSha256,
        string? evidenceRoot, CancellationToken cancellationToken = default, Func<int, string?>? testScenario = null, bool testOnlyAllowFake = false)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var plan = prepared.Plan;
        if (plan.Candidates.Count != 1 || plan.Candidates[0] is not { RequestedDuration100ns: 100_000, CaptureFrames: 480, RenderFrames: 480 } candidate ||
            !plan.RepeatSelected || plan.RepeatabilityRuns != 5 || plan.MaximumOperations != 7 || plan.MaximumActiveDuplexSeconds != 25 ||
            plan.MmcssModes.Count != 1 || plan.MmcssModes[0] != "PRO_AUDIO" || plan.Route.Mode != "MONO_TO_STEREO" ||
            plan.Route.Capture is not { SampleRateHz: 48_000, Channels: 2, Encoding: "PCM16" } ||
            plan.Route.Render is not { SampleRateHz: 48_000, Channels: 2, Encoding: "PCM16" } ||
            sourceEvidenceSha256.Length != 64 || !sourceEvidenceSha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("RepeatSelected requires the verified 10 ms C480/R480 candidate, fixed route, fresh R=5 consent and seven-operation/25-second budget.");
        if (!prepared.Consent.Accepted || prepared.Consent.ConfirmationId == Guid.Empty || prepared.Consent.DiscoveryGeneration != worker.DiscoveryGeneration ||
            WorkerScanV2ContractV4Fingerprint.Compute(prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, prepared.Consent.DiscoveryGeneration) != prepared.Consent.PlanFingerprint)
            throw new InvalidOperationException("RepeatSelected requires a new consent receipt bound to current discovery and the one-candidate plan.");

        var routeFingerprint = WorkerScanPlanV2Fingerprint.Route(prepared.CaptureEndpointId, prepared.RenderEndpointId, plan.Route);
        if (testScenario is not null && !testOnlyAllowFake) throw new InvalidOperationException("Injected scenarios are restricted to test-only Worker runs.");
        if (testScenario is not null && !worker.Capabilities.Contains("TestInjectedDuplexBackend", StringComparer.Ordinal)) throw new InvalidOperationException("Fake repeat scenarios require a test-only Worker.");
        var session = await worker.RegisterScanSessionV2V4Async(prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, prepared.Consent, cancellationToken, true).ConfigureAwait(false);
        var runs = new List<V4RepeatabilityRunEvidence>(5); var used = 0; var state = "UNKNOWN"; var cleanup = "UNKNOWN"; var stop = "REPEATABILITY_NOT_COMPLETED";
        JsonElement captureInit = default, renderInit = default;
        try
        {
            captureInit = await Initialize("CAPTURE");
            renderInit = captureInit.GetProperty("initializeDisposition").GetString() == "INITIALIZED_EXACT" ? await Initialize("RENDER") : default;
            var initialized = captureInit.GetProperty("initializeDisposition").GetString() == "INITIALIZED_EXACT" && renderInit.ValueKind == JsonValueKind.Object &&
                renderInit.GetProperty("initializeDisposition").GetString() == "INITIALIZED_EXACT" && Exact(captureInit, candidate, true) && Exact(renderInit, candidate, false);
            if (!initialized) stop = "INITIALIZE_FAILED_OR_UNKNOWN";
            else
            {
                state = "REPEATABILITY_CONFIRMED";
                for (var i = 0; i < 5; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var operationId = Guid.NewGuid();
                    JsonElement duplex;
                    try { duplex = await worker.ValidateCustomDuplexV2InScanAsync(session, operationId, plan.Route, prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, candidate.Index, cancellationToken, testScenario?.Invoke(i)); }
                    catch { runs.Add(UnknownRepeat(operationId, session, candidate, "WORKER_OPERATION_UNKNOWN")); used++; state = "UNKNOWN"; stop = "REPEATABILITY_WORKER_FAILURE"; break; }
                    used++;
                    var classification = V4ShortCleanEvaluator.Classify(prepared, session, candidate.Index, operationId, duplex);
                    runs.Add(ToRepeatabilityEvidence(duplex, session, candidate, classification));
                    if (evidenceRoot is not null)
                    {
                        Directory.CreateDirectory(evidenceRoot);
                        var repeatPath = Path.Combine(evidenceRoot, $"repeat-{i + 1:D2}.json");
                        var repeatJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            schemaVersion = 1, runNumber = i + 1, sourceEvidenceSha256, sourceCandidateIndex = 7, candidateIndex = candidate.Index,
                            operationId, sessionId = session.ScanSessionId, classification, evidence = duplex
                        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
                        using (var stream = new FileStream(repeatPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { stream.Write(repeatJson); stream.Flush(true); }
                        using var readBack = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(repeatPath));
                        if (readBack.RootElement.GetProperty("operationId").GetGuid() != operationId || readBack.RootElement.GetProperty("runNumber").GetInt32() != i + 1)
                            throw new InvalidDataException("Repeat evidence read-back identity mismatch.");
                    }
                    if (classification == "UNKNOWN") { state = "UNKNOWN"; stop = "REPEATABILITY_UNKNOWN"; break; }
                    if (classification == "FAILED") { state = "REPEATABILITY_FAILED"; stop = "REPEATABILITY_FAILED"; break; }
                }
                if (runs.Count == 5 && runs.All(r => r.Result == "CLEAN")) stop = "REPEATABILITY_5_OF_5_CLEAN";
            }
        }
        catch (OperationCanceledException) { state = "UNKNOWN"; stop = "OPERATOR_CANCELLED"; }
        catch { state = "UNKNOWN"; stop = "WORKER_OR_EVIDENCE_FAILURE"; }

        try { await worker.CloseScanSessionAsync(session, CancellationToken.None).ConfigureAwait(false); cleanup = "CLOSE_CONFIRMED"; }
        catch { state = "UNKNOWN"; cleanup = "UNKNOWN"; stop = "SESSION_CLEANUP_UNKNOWN"; }
        var entry = new V2CandidateMatrixEntry(candidate, captureInit.ValueKind == JsonValueKind.Object ? MatrixInitialize(captureInit) : "UNKNOWN", renderInit.ValueKind == JsonValueKind.Object ? MatrixInitialize(renderInit) : "NOT_TESTED", "NOT_TESTED", captureInit.ValueKind == JsonValueKind.Object ? "RELEASE_ATTEMPTED" : "UNKNOWN", renderInit.ValueKind == JsonValueKind.Object ? "RELEASE_ATTEMPTED" : "NOT_ATTEMPTED", [], true,
            stop, "COMPLETE", state == "REPEATABILITY_CONFIRMED" ? "CONFIRMED" : state == "REPEATABILITY_FAILED" ? "FAILED" : "UNKNOWN", testOnlyAllowFake ? "TEST_FAKE_WASAPI_V4_DUPLEX" : "REAL_WASAPI_V4_DUPLEX") { CaptureCleanup = captureInit.ValueKind == JsonValueKind.Object ? "RELEASE_ATTEMPTED" : "UNKNOWN", RenderCleanup = renderInit.ValueKind == JsonValueKind.Object ? "RELEASE_ATTEMPTED" : "NOT_ATTEMPTED", CaptureInitializeEvidence = captureInit.ValueKind == JsonValueKind.Object ? captureInit.Clone() : null, RenderInitializeEvidence = renderInit.ValueKind == JsonValueKind.Object ? renderInit.Clone() : null,
            RepeatabilityStatus = state == "REPEATABILITY_CONFIRMED" ? "CONFIRMED" : state == "REPEATABILITY_FAILED" ? "FAILED" : "UNKNOWN" };
        var evidence = new V2CandidateMatrixEvidence(1, prepared.Consent.PlanFingerprint, routeFingerprint, cleanup == "UNKNOWN" ? "UNKNOWN" : "COMPLETED",
            plan.CaptureBounds, plan.RenderBounds, plan.Candidates, [entry], state, testOnlyAllowFake ? "TEST_FAKE_WASAPI_V4_DUPLEX" : "REAL_WASAPI_V4_DUPLEX")
        {
            EvidenceVersion = 3, DiscoveryGeneration = prepared.Consent.DiscoveryGeneration, SelectedCandidateIndex = candidate.Index, BuildSha = Environment.GetEnvironmentVariable("KLIDAN_BUILD_SHA") ?? "UNKNOWN",
            GlobalMinimumProven = false, UsedOperations = used, MaximumOperations = 7, UsedActiveDuplexSeconds = 5 * runs.Count, MaximumActiveDuplexSeconds = 25,
            StopReason = stop, RepeatabilityRequested = 5, RepeatabilityCompleted = runs.Count, RepeatabilityRuns = runs, SessionCleanup = cleanup,
            SourceEvidenceSha256 = sourceEvidenceSha256, SourceCandidateIndex = 7
        };
        string? path = null;
        if (evidenceRoot is not null) path = V2CandidateMatrixEvidenceJson.SaveNoOverwriteAndVerify(evidence, Path.Combine(evidenceRoot, $"v4-repeat-selected-{Guid.NewGuid():D}.json"));
        return new(evidence, path);

        async Task<JsonElement> Initialize(string direction)
        {
            used++;
            return await worker.InitializeCustomPeriodV2InScanAsync(session, Guid.NewGuid(), prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, candidate.Index, direction, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task<V4AutomaticSearchResult> IV4AutomaticSearchWorkflow.RunAsync(WorkerScanV2V4PreparedPlan prepared, WindowsEndpointDto capture,
        WindowsEndpointDto render, string evidenceRoot, string profileDirectory, bool runRepeatability,
        IProgress<V4AutomaticSearchProgress>? progress, CancellationToken cancellationToken)
    {
        if (runRepeatability && prepared.Plan.RepeatabilityRuns != 5) throw new ArgumentException("GUI repeatability authorization is available only for an R=5 plan.", nameof(runRepeatability));
        return await RunAsync(prepared, evidenceRoot, cancellationToken, runRepeatability: runRepeatability,
            captureEndpoint: capture, renderEndpoint: render, profileDirectory: profileDirectory, progress: progress).ConfigureAwait(false);
    }

    async Task<V4AutomaticSearchResult> IV4AutomaticSearchWorkflow.RunDiagnosticAsync(WorkerScanV2V4PreparedPlan prepared, WindowsEndpointDto capture,
        WindowsEndpointDto render, string evidenceRoot, IProgress<V4AutomaticSearchProgress>? progress, CancellationToken cancellationToken)
    {
        if (prepared.Plan.Candidates.Count != 1 || prepared.Plan.RepeatabilityRuns != 0 || prepared.Plan.MaximumOperations != 3 ||
            prepared.Plan.MaximumActiveDuplexSeconds != 5 || prepared.Plan.Candidates[0].RequestedDuration100ns is not (50_000 or 100_000) ||
            prepared.Plan.Route.Mode != "MONO_TO_STEREO" || prepared.Plan.Route.Capture.Encoding != "PCM16" || prepared.Plan.Route.Render.Encoding != "PCM16" ||
            prepared.Plan.Route.Capture.SampleRateHz != 48_000 || prepared.Plan.Route.Render.SampleRateHz != 48_000)
            throw new InvalidOperationException("Point diagnostics require exactly one 5/10 ms 48 kHz PCM16 MONO_TO_STEREO candidate, R=0, and the three-operation/five-second budget.");
        return await RunAsync(prepared, evidenceRoot, cancellationToken, diagnosticOnly: true,
            captureEndpoint: capture, renderEndpoint: render, progress: progress).ConfigureAwait(false);
    }

    public async Task<V4AutomaticSearchResult> RunAsync(WorkerScanV2V4PreparedPlan prepared,
        string? evidenceRoot = null, CancellationToken cancellationToken = default,
        Func<int, string, string?>? testScenario = null, bool runRepeatability = false, DeviceConfigurationProfile? profileBase = null,
        WindowsEndpointDto? captureEndpoint = null, WindowsEndpointDto? renderEndpoint = null, string? profileDirectory = null,
        IProgress<V4AutomaticSearchProgress>? progress = null, bool diagnosticOnly = false)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var plan = prepared.Plan;
        var consent = prepared.Consent;
        if (diagnosticOnly && (plan.Candidates.Count != 1 || plan.RepeatabilityRuns != 0 || plan.MaximumOperations != 3 || plan.MaximumActiveDuplexSeconds != 5 || runRepeatability))
            throw new InvalidOperationException("Diagnostic execution cannot exceed one candidate, three operations, five active seconds, or R=0.");
        if (!consent.Accepted || consent.ConfirmationId == Guid.Empty || consent.DiscoveryGeneration != worker.DiscoveryGeneration ||
            WorkerScanV2ContractV4Fingerprint.Compute(prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, consent.DiscoveryGeneration) != consent.PlanFingerprint)
            throw new InvalidOperationException("V4 search requires the exact current prepared plan and accepted consent receipt.");
        var routeFingerprint = WorkerScanPlanV2Fingerprint.Route(prepared.CaptureEndpointId, prepared.RenderEndpointId, plan.Route);
        var entries = plan.Candidates.Select(c => new V2CandidateMatrixEntry(c, "NOT_TESTED", "NOT_TESTED", "NOT_TESTED", "NOT_ATTEMPTED", "NOT_ATTEMPTED", [], true,
            c.RejectionReason.Length == 0 ? "NOT_RUN" : c.RejectionReason, "INCOMPLETE", "NOT_RUN", "NOT_RUN")).ToArray();
        var operations = 0; var activeSeconds = 0; var selected = (int?)null; var state = "MINIMUM_NOT_PROVEN"; var lifecycle = "COMPLETED"; var stopReason = "CANDIDATE_DOMAIN_EXHAUSTED";
        var repeatRuns = new List<V4RepeatabilityRunEvidence>(); var sessionCleanup = "NOT_ATTEMPTED";
        var diagnosticBuildSha = Environment.GetEnvironmentVariable("KLIDAN_BUILD_SHA")?.ToLowerInvariant() ?? "UNKNOWN";
        var diagnosticTimestamp = diagnosticOnly ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        if (diagnosticOnly) selected = plan.Candidates[0].Index;
        WorkerScanSessionToken? session = null;
        var attemptedProvenance = new HashSet<string>(StringComparer.Ordinal);
        void Update(int index, Func<V2CandidateMatrixEntry, V2CandidateMatrixEntry> update) => entries[index] = update(entries[index]);
        try
        {
            session = await worker.RegisterScanSessionV2V4Async(prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, consent, cancellationToken, runRepeatability).ConfigureAwait(false);
            foreach (var candidate in plan.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new("CANDIDATE", candidate.Index, candidate.CaptureFrames, candidate.RenderFrames, repeatRuns.Count));
                if (worker.DiscoveryGeneration != consent.DiscoveryGeneration || session.ContractVersion != 4 || session.RouteFingerprint != routeFingerprint)
                    throw new InvalidOperationException("V4 plan generation or route changed during search.");
                if (candidate.RejectionReason.Length != 0)
                {
                    Update(candidate.Index, e => e with { CaptureInitializeDisposition = "REJECTED", RenderInitializeDisposition = "REJECTED", DuplexDisposition = "REJECTED", SkipReason = candidate.RejectionReason, ReasonCodes = ["CANDIDATE_CONSTRAINT_REJECTED"] });
                    continue;
                }

                var capture = await Initialize(candidate, "CAPTURE");
                Update(candidate.Index, e => e with { CaptureInitializeDisposition = MatrixInitialize(capture), CaptureCleanup = capture.GetProperty("cleanup").GetString()!, ActualCaptureFrames = Positive(capture, "actualFrames"), ActualCapturePeriod100ns = PositiveLong(capture, "actualPeriod100ns"), Provenance = capture.GetProperty("provenance").GetString()!, CaptureInitializeEvidence = capture.Clone() });
                progress?.Report(new("CAPTURE_INITIALIZED", candidate.Index, candidate.CaptureFrames, candidate.RenderFrames, repeatRuns.Count, Positive(capture, "actualFrames")));
                if (capture.GetProperty("initializeDisposition").GetString() != "INITIALIZED_EXACT") { FailInitialize(candidate.Index, "CAPTURE", capture); if (capture.GetProperty("initializeDisposition").GetString() == "UNKNOWN" || capture.GetProperty("cleanup").GetString() != "RELEASE_ATTEMPTED") { state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "CAPTURE_INITIALIZE_UNKNOWN"; break; } continue; }

                var render = await Initialize(candidate, "RENDER");
                Update(candidate.Index, e => e with { RenderInitializeDisposition = MatrixInitialize(render), RenderCleanup = render.GetProperty("cleanup").GetString()!, ActualRenderFrames = Positive(render, "actualFrames"), ActualRenderPeriod100ns = PositiveLong(render, "actualPeriod100ns"), Provenance = render.GetProperty("provenance").GetString()!, RenderInitializeEvidence = render.Clone() });
                progress?.Report(new("RENDER_INITIALIZED", candidate.Index, candidate.CaptureFrames, candidate.RenderFrames, repeatRuns.Count, Positive(capture, "actualFrames"), Positive(render, "actualFrames")));
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
                var diagnosticFrames = V4ShortCleanEvaluator.CoverageMismatch(duplex);
                Update(candidate.Index, e => e with { DuplexDisposition = classification, DuplexNotRun = false, SkipReason = null, ReasonCodes = [duplex.GetProperty("reasonCode").GetString()!, V4ShortCleanEvaluator.ExplainClassification(duplex, classification)], NativeDuplexDisposition = duplex.GetProperty("duplexDisposition").GetString(), EvaluatorReasonCode = V4ShortCleanEvaluator.ExplainClassification(duplex, classification), EvaluatorExpectedFrames = diagnosticFrames.Expected, EvaluatorActualFrames = diagnosticFrames.Actual, DuplexEvidence = duplex.Clone(),
                    ActualCaptureFrames = Positive(duplex, "actualCaptureFrames"), ActualCapturePeriod100ns = PositiveLong(duplex, "actualCapturePeriod100ns"),
                    ActualRenderFrames = Positive(duplex, "actualRenderFrames"), ActualRenderPeriod100ns = PositiveLong(duplex, "actualRenderPeriod100ns"), Provenance = provenance,
                    CaptureCleanup = CleanupSide(duplex, "CAPTURE"), RenderCleanup = CleanupSide(duplex, "RENDER") });
                progress?.Report(new("DUPLEX_" + classification, candidate.Index, candidate.CaptureFrames, candidate.RenderFrames, repeatRuns.Count,
                    Positive(duplex, "actualCaptureFrames"), Positive(duplex, "actualRenderFrames")));
                if (classification == "UNKNOWN") { state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "DUPLEX_UNKNOWN_OR_INVALID_EVIDENCE"; break; }
                if (classification == "CLEAN")
                {
                    progress?.Report(new("SHORT_CLEAN", candidate.Index, candidate.CaptureFrames, candidate.RenderFrames, repeatRuns.Count));
                    selected = candidate.Index; state = "MINIMUM_SHORT_CLEAN_FOUND"; stopReason = diagnosticOnly ? "DIAGNOSTIC_CANDIDATE_COMPLETED" : candidate.Index == plan.Candidates.Max(c => c.Index) ? "CANDIDATE_DOMAIN_EXHAUSTED" : "SMALLER_SHORT_CLEAN_FOUND";
                    foreach (var larger in plan.Candidates.Where(c => c.Index > candidate.Index)) Update(larger.Index, e => e with { CaptureInitializeDisposition = "SKIPPED", RenderInitializeDisposition = "SKIPPED", DuplexDisposition = "SKIPPED", DuplexNotRun = true, SkipReason = "SMALLER_SHORT_CLEAN_FOUND", ReasonCodes = ["SMALLER_SHORT_CLEAN_FOUND"] });
                    if (runRepeatability && plan.RepeatabilityRuns > 0)
                    {
                        for (var run = 0; run < plan.RepeatabilityRuns; run++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            progress?.Report(new($"REPEATABILITY_{run + 1}_OF_{plan.RepeatabilityRuns}", candidate.Index, candidate.CaptureFrames, candidate.RenderFrames, repeatRuns.Count));
                            var repeatId = Guid.NewGuid(); operations++;
                            JsonElement repeatEvidence;
                            try
                            {
                                repeatEvidence = await worker.ValidateCustomDuplexV2InScanAsync(session, repeatId, plan.Route, prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, candidate.Index,
                                    cancellationToken, testScenario?.Invoke(candidate.Index, $"REPEAT_{run + 1}")).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                repeatRuns.Add(UnknownRepeat(repeatId, session, candidate, "OPERATION_CANCELLED")); state = "UNKNOWN"; lifecycle = "CANCELLED"; stopReason = "REPEATABILITY_CANCELLED_UNKNOWN"; break;
                            }
                            catch (Exception)
                            {
                                repeatRuns.Add(UnknownRepeat(repeatId, session, candidate, "WORKER_OPERATION_UNKNOWN")); state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "REPEATABILITY_WORKER_FAILURE"; break;
                            }
                            attemptedProvenance.Add(repeatEvidence.GetProperty("provenance").GetString()!); activeSeconds += 5;
                            var repeatClass = V4ShortCleanEvaluator.Classify(prepared, session, candidate.Index, repeatId, repeatEvidence);
                            var repeat = ToRepeatabilityEvidence(repeatEvidence, session, candidate, repeatClass);
                            repeatRuns.Add(repeat);
                            progress?.Report(new("REPEATABILITY", candidate.Index, candidate.CaptureFrames, candidate.RenderFrames, repeatRuns.Count));
                            Update(candidate.Index, e => e with { RepeatabilityStatus = repeatClass == "CLEAN" ? "NOT_RUN" : repeatClass == "FAILED" ? "FAILED" : "UNKNOWN" });
                            if (repeatClass == "UNKNOWN") { state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "REPEATABILITY_UNKNOWN"; break; }
                            if (repeatClass == "FAILED") { state = "REPEATABILITY_FAILED"; stopReason = "REPEATABILITY_FAILED_NO_FALLBACK"; break; }
                        }
                        if (state == "MINIMUM_SHORT_CLEAN_FOUND" && repeatRuns.Count == plan.RepeatabilityRuns)
                        {
                            if (plan.RepeatabilityRuns == 5) { state = "REPEATABILITY_CONFIRMED"; stopReason = "REPEATABILITY_5_OF_5_CLEAN"; Update(candidate.Index, e => e with { RepeatabilityStatus = "CONFIRMED" }); }
                            else stopReason = "REPEATABILITY_PRELIMINARY";
                        }
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) { state = "UNKNOWN"; lifecycle = "CANCELLED"; stopReason = "OPERATOR_CANCELLED"; }
        catch (Exception) { state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "WORKER_OR_EVIDENCE_FAILURE"; }
        if (diagnosticOnly && state != "UNKNOWN") stopReason = "DIAGNOSTIC_CANDIDATE_COMPLETED";
        var smallest = selected is { } selectedIndex ? selectedIndex : plan.Candidates.Count;
        for (var i = 0; i < entries.Length; i++)
        {
            var item = entries[i];
            var terminal = item.DuplexDisposition is "CLEAN" or "FAILED" or "UNSUPPORTED" or "REJECTED";
            var coverage = i < smallest && terminal ? "COMPLETE" : i >= smallest && selected is not null ? "COMPLETE" : state == "UNKNOWN" && !terminal ? "UNKNOWN" : "INCOMPLETE";
            Update(i, e => e with { LowerPeriodCoverage = coverage, Provenance = e.Provenance == "NOT_RUN" ? AggregateProvenance(attemptedProvenance) : e.Provenance });
        }
        var evidenceProvenance = AggregateProvenance(attemptedProvenance);
        V2CandidateMatrixEvidence CreateEvidence() => new(1, consent.PlanFingerprint, routeFingerprint, lifecycle, plan.CaptureBounds, plan.RenderBounds, plan.Candidates, entries,
            diagnosticOnly ? "DIAGNOSTIC_ONLY" : state, evidenceProvenance)
        {
            EvidenceVersion = diagnosticOnly ? 4 : 3, DiagnosticOnly = diagnosticOnly, DiscoveryGeneration = consent.DiscoveryGeneration, SelectedCandidateIndex = selected,
            BuildSha = diagnosticBuildSha, DiagnosticTimestampUtc = diagnosticTimestamp,
            GlobalMinimumProven = false, UsedOperations = operations,
            MaximumOperations = plan.MaximumOperations, UsedActiveDuplexSeconds = activeSeconds, MaximumActiveDuplexSeconds = plan.MaximumActiveDuplexSeconds,
            StopReason = stopReason, RepeatabilityPending = !diagnosticOnly && state == "MINIMUM_SHORT_CLEAN_FOUND",
            RepeatabilityRequested = diagnosticOnly ? 0 : runRepeatability ? plan.RepeatabilityRuns : 0, RepeatabilityCompleted = repeatRuns.Count, RepeatabilityRuns = repeatRuns,
            SessionCleanup = sessionCleanup
        };
        var result = CreateEvidence();
        string? path = null;
        if (evidenceRoot is not null)
        {
            try { path = V2CandidateMatrixEvidenceJson.SaveNoOverwriteAndVerify(result, Path.Combine(evidenceRoot, $"v4-{(diagnosticOnly ? "diagnostic" : "search")}-open-session-{Guid.NewGuid():D}.json")); }
            catch { state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "EVIDENCE_SAVE_OR_READBACK_FAILED"; result = CreateEvidence(); }
        }
        if (session is { } active)
        {
            try
            {
                if (state == "UNKNOWN" || lifecycle == "CANCELLED") await worker.CancelScanSessionAsync(active, CancellationToken.None).ConfigureAwait(false);
                else await worker.CloseScanSessionAsync(active, CancellationToken.None).ConfigureAwait(false);
                sessionCleanup = lifecycle == "CANCELLED" || state == "UNKNOWN" ? "CANCEL_CONFIRMED" : "CLOSE_CONFIRMED";
            }
            catch { sessionCleanup = "UNKNOWN"; state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "SESSION_CLEANUP_UNKNOWN"; }
        }
        result = CreateEvidence();
        if (result.UsedOperations > result.MaximumOperations) throw new InvalidDataException("V4 audio operations exceeded the consented budget.");
        if (evidenceRoot is not null)
        {
            try { path = V2CandidateMatrixEvidenceJson.SaveNoOverwriteAndVerify(result, Path.Combine(evidenceRoot, $"v4-{(diagnosticOnly ? "diagnostic" : "search")}-closed-session-{Guid.NewGuid():D}.json")); }
            catch { state = "UNKNOWN"; lifecycle = "UNKNOWN"; stopReason = "FINAL_EVIDENCE_SAVE_OR_READBACK_FAILED"; result = CreateEvidence(); }
        }
        return new(result, path);

        async Task<JsonElement> Initialize(WorkerScanV2Candidate candidate, string direction)
        {
            progress?.Report(new($"INITIALIZE_{direction}", candidate.Index, candidate.CaptureFrames, candidate.RenderFrames, repeatRuns.Count));
            operations++;
            var value = await worker.InitializeCustomPeriodV2InScanAsync(session!, Guid.NewGuid(), prepared.CaptureEndpointId, prepared.RenderEndpointId, plan, candidate.Index, direction, cancellationToken,
                testScenario?.Invoke(candidate.Index, direction + "_INITIALIZE")).ConfigureAwait(false);
            attemptedProvenance.Add(value.GetProperty("provenance").GetString()!);
            return value;
        }
        static string AggregateProvenance(IEnumerable<string> operations)
        {
            var sources = operations.Select(p => p.StartsWith("TEST_FAKE_", StringComparison.Ordinal) ? "TEST_FAKE" : p.StartsWith("REAL_", StringComparison.Ordinal) ? "REAL" : "UNKNOWN").Distinct(StringComparer.Ordinal).ToArray();
            return sources.Length == 0 ? "PLAN_ONLY" : sources.Length == 1 && sources[0] == "REAL" ? "REAL_WASAPI_V4_DUPLEX" : sources.Length == 1 && sources[0] == "TEST_FAKE" ? "TEST_FAKE_WASAPI_V4_DUPLEX" : "MIXED_PROVENANCE";
        }
        void FailInitialize(int index, string direction, JsonElement value)
        {
            var disposition = value.GetProperty("initializeDisposition").GetString()!;
            Update(index, e => e with { DuplexDisposition = disposition == "UNSUPPORTED" ? "UNSUPPORTED" : "FAILED", DuplexNotRun = true, SkipReason = direction + "_INITIALIZE_" + disposition,
                ReasonCodes = [value.GetProperty("reasonCode").GetString()!], Provenance = value.GetProperty("provenance").GetString()! });
        }
    }

    private static int? Positive(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var v) && v > 0 ? v : null;
    private static V4RepeatabilityRunEvidence ToRepeatabilityEvidence(JsonElement e, WorkerScanSessionToken session, WorkerScanV2Candidate candidate, string classification) =>
        new V4RepeatabilityRunEvidence(e.GetProperty("operationId").GetGuid(), session.ScanSessionId, session.RouteFingerprint!, candidate.Index, classification, e.GetProperty("reasonCode").GetString()!,
            candidate.CaptureFrames, candidate.CapturePeriod100ns, Positive(e, "actualCaptureFrames"), PositiveLong(e, "actualCapturePeriod100ns"),
            candidate.RenderFrames, candidate.RenderPeriod100ns, Positive(e, "actualRenderFrames"), PositiveLong(e, "actualRenderPeriod100ns"),
            e.GetProperty("cleanup").GetString()!, e.GetProperty("provenance").GetString()!) { Evidence = e.Clone() };
    private static V4RepeatabilityRunEvidence UnknownRepeat(Guid operationId, WorkerScanSessionToken session, WorkerScanV2Candidate candidate, string reason) =>
        new(operationId, session.ScanSessionId, session.RouteFingerprint!, candidate.Index, "UNKNOWN", reason, candidate.CaptureFrames, candidate.CapturePeriod100ns, null, null,
            candidate.RenderFrames, candidate.RenderPeriod100ns, null, null, "UNKNOWN", "UNKNOWN");
    private static string MatrixInitialize(JsonElement e) => e.GetProperty("initializeDisposition").GetString() == "INITIALIZED_EXACT" ? "INITIALIZED" : e.GetProperty("initializeDisposition").GetString()!;
    private static long? PositiveLong(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var v) && v > 0 ? v : null;
    private static bool Exact(JsonElement e, WorkerScanV2Candidate c, bool capture) => e.GetProperty("initializeDisposition").GetString() == "INITIALIZED_EXACT" &&
        e.GetProperty("actualFrames").GetInt32() == (capture ? c.CaptureFrames : c.RenderFrames) && e.GetProperty("actualPeriod100ns").GetInt64() == (capture ? c.CapturePeriod100ns : c.RenderPeriod100ns) && e.GetProperty("cleanup").GetString() == "RELEASE_ATTEMPTED";
    private static string CleanupSide(JsonElement e, string side) => e.GetProperty("cleanup").GetString() == "CAPTURE:RELEASE_ATTEMPTED;RENDER:RELEASE_ATTEMPTED" ? "RELEASE_ATTEMPTED" : "UNKNOWN";
}
