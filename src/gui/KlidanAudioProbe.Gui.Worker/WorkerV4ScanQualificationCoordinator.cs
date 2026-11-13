using System.Security.Cryptography;
using System.Text.Json;
using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

/// <summary>Sequences closed Worker V4 candidate sessions and one AudioHost run at a time under one bounded-plan consent.</summary>
public sealed class WorkerV4ScanQualificationCoordinator(WorkerV4AutomaticSearchWorkflow scanner, IAudioHostQualificationAdapter? audioHost,
    Func<int, string, string?>? testScenario = null) : IV4ScanQualificationCoordinator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<V4ScanQualificationResult> RunAsync(WorkerScanV2V4PreparedPlan prepared, WindowsEndpointDto capture,
        WindowsEndpointDto render, AudioHostQualificationAuthorization authorization, string audioHostPackage,
        string evidenceRoot, string? profileDirectory = null, IProgress<V4AutomaticSearchProgress>? progress = null,
        CancellationToken cancellationToken = default, bool testOnly = false)
    {
        ArgumentNullException.ThrowIfNull(prepared); ArgumentNullException.ThrowIfNull(authorization);
        if (!prepared.Consent.Accepted || prepared.Consent.ConfirmationId == Guid.Empty || prepared.Plan.RepeatabilityRuns != 5 ||
            prepared.Plan.RepeatSelected || prepared.Plan.Candidates.Count is < 1 or > 8 ||
            authorization.PlanFingerprint != prepared.Consent.PlanFingerprint || authorization.DiscoveryGeneration != prepared.Consent.DiscoveryGeneration ||
            authorization.CaptureEndpointId != prepared.CaptureEndpointId || authorization.RenderEndpointId != prepared.RenderEndpointId ||
            !authorization.Accepted || !authorization.OpenSpeakersConfirmedOff || authorization.MaximumRuns != prepared.Plan.Candidates.Count ||
            authorization.MaximumDurationSeconds != checked(60 * authorization.MaximumRuns) || authorization.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("One explicit qualification authorization must cover the exact discovered candidate plan and bounded AudioHost run/time budget.");

        Directory.CreateDirectory(evidenceRoot);
        var attempts = new List<V4CandidateQualificationAttempt>();
        var rows = prepared.Plan.Candidates.Select(c => NotTested(c, "NOT_ATTEMPTED")).ToArray();
        var usedOperations = 0; var usedActiveSeconds = 0; var audioHostRuns = 0; var audioHostSeconds = 0d;
        var lastLifecycle = "UNKNOWN"; var lastWorkerCleanup = "NOT_ATTEMPTED";
        foreach (var candidate in prepared.Plan.Candidates.OrderBy(c => c.RequestedDuration100ns).ThenBy(c => c.CaptureFrames).ThenBy(c => c.Index))
        {
            if (cancellationToken.IsCancellationRequested) return await Stop("CANCELLED", "OPERATOR_CANCELLED", candidate.Index, "CANCELLED").ConfigureAwait(false);
            if (candidate.RejectionReason.Length != 0)
            {
                rows[candidate.Index] = new(candidate, "REJECTED", "REJECTED", "UNSUPPORTED", "NOT_ATTEMPTED", "NOT_ATTEMPTED", ["UNSUPPORTED_BY_DISCOVERED_GEOMETRY"], true, "UNSUPPORTED_BY_DISCOVERED_GEOMETRY", "COMPLETE", "NOT_RUN", "REAL_WASAPI_V4_DUPLEX");
                continue;
            }
            if (usedOperations + 8 > prepared.Plan.MaximumOperations || usedActiveSeconds + 30 > prepared.Plan.MaximumActiveDuplexSeconds)
                return await Stop("QA_FAILED", "CONSENTED_WORKER_BUDGET_EXHAUSTED", candidate.Index).ConfigureAwait(false);

            var scopedCandidate = candidate with { Index = 0 };
            var scopedBudget = WorkerScanV2ContractV4Fingerprint.Budget(1, 5);
            var scopedPlan = prepared.Plan with
            {
                Candidates = [scopedCandidate], MaximumOperations = scopedBudget.MaximumOperations,
                MaximumActiveDuplexSeconds = scopedBudget.MaximumActiveDuplexSeconds, SessionLifetimeSeconds = scopedBudget.SessionLifetimeSeconds
            };
            var scopedConsent = WorkerScanV2ContractV4Fingerprint.CreateConsent(prepared.CaptureEndpointId, prepared.RenderEndpointId,
                scopedPlan, prepared.Consent.DiscoveryGeneration, true, Guid.NewGuid());
            var scoped = prepared with { Plan = scopedPlan, Consent = scopedConsent };
            V4AutomaticSearchResult scan;
            try
            {
                scan = await scanner.RunAsync(scoped, evidenceRoot, cancellationToken,
                    testScenario: (index, operation) => testScenario?.Invoke(candidate.Index, operation), runRepeatability: true,
                    captureEndpoint: capture, renderEndpoint: render, progress: progress).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await Stop("CANCELLED", "OPERATOR_CANCELLED_DURING_WORKER_SESSION", candidate.Index, "CANCELLED").ConfigureAwait(false);
            }
            usedOperations += scan.Evidence.UsedOperations; usedActiveSeconds += scan.Evidence.UsedActiveDuplexSeconds;
            lastLifecycle = scan.Evidence.Lifecycle; lastWorkerCleanup = scan.Evidence.SessionCleanup;
            var scopedRow = scan.Evidence.Candidates[0];
            rows[candidate.Index] = scopedRow with
            {
                Candidate = candidate,
                RepeatabilityEvidence = scopedRow.RepeatabilityEvidence.Select(r => r with { CandidateIndex = candidate.Index }).ToArray()
            };
            var scanPath = scan.EvidencePath;
            if (scanPath is null || scan.Evidence.SessionCleanup != "CLOSE_CONFIRMED" || scan.Evidence.Lifecycle != "COMPLETED")
                return await Stop("UNKNOWN", "WORKER_SESSION_CLOSE_OR_EVIDENCE_UNKNOWN", candidate.Index).ConfigureAwait(false);

            var row = rows[candidate.Index];
            if (row.DuplexDisposition == "UNKNOWN" || row.RepeatabilityStatus == "UNKNOWN" || row.CaptureCleanup is "UNKNOWN" or "FAILED" || row.RenderCleanup is "UNKNOWN" or "FAILED")
                return await Stop("UNKNOWN", "SCANNER_METRIC_OR_CLEANUP_UNKNOWN", candidate.Index).ConfigureAwait(false);
            if (row.DuplexDisposition is "FAILED" or "UNSUPPORTED" || row.RepeatabilityStatus == "FAILED")
            {
                var saved = await SaveAttempt(candidate, scan, "NOT_RUN", "CLOSE_CONFIRMED", null, evidenceRoot, cancellationToken).ConfigureAwait(false);
                attempts.Add(saved);
                continue;
            }
            if (row.DuplexDisposition != "CLEAN" || row.RepeatabilityStatus != "CONFIRMED" || scan.Evidence.RepeatabilityCompleted != 5 || scan.Evidence.RepeatabilityRuns.Any(r => r.Result != "CLEAN"))
                return await Stop("UNKNOWN", "SHORT_OR_REPEATABILITY_RESULT_INCOMPLETE", candidate.Index).ConfigureAwait(false);
            if (audioHost is null) return await Stop("NOT_QUALIFIED", "AUDIOHOST_ADAPTER_UNAVAILABLE", candidate.Index).ConfigureAwait(false);
            if (audioHostRuns >= authorization.MaximumRuns || audioHostSeconds + 60 > authorization.MaximumDurationSeconds)
                return await Stop("QA_FAILED", "CONSENTED_AUDIOHOST_BUDGET_EXHAUSTED", candidate.Index).ConfigureAwait(false);

            var source = await SaveQualificationSource(candidate, scan, prepared, evidenceRoot, cancellationToken).ConfigureAwait(false);
            var audioClock = System.Diagnostics.Stopwatch.StartNew(); audioHostRuns++;
            var qualification = await audioHost.QualifyAsync(new(audioHostPackage, Path.Combine(evidenceRoot, "audiohost"),
                source.ShortSummary, source.RepeatSummary, scanPath, prepared, candidate, authorization, testOnly,
                testOnly ? "TEST_ONLY_CONTROLLED_PEER" : null), cancellationToken).ConfigureAwait(false);
            audioClock.Stop(); audioHostSeconds += audioClock.Elapsed.TotalSeconds;
            var qualificationPath = await WriteQualificationEvidence(candidate, qualification, source, prepared, evidenceRoot, cancellationToken).ConfigureAwait(false);
            rows[candidate.Index] = rows[candidate.Index] with { AudioHostQualification = qualification };
            if (testOnly && (qualification.Status is "LOAD_CLEAN" or "TEST_ONLY") && qualification.Cleanup == "CONFIRMED" && qualification.Provenance == "TEST_ONLY")
            {
                attempts.Add(new(candidate.Index, candidate.CapturePeriod100ns, "TEST_ONLY_REPEATABILITY_CONFIRMED", scanPath, "TEST_ONLY_LOAD_CLEAN", "CONFIRMED", qualificationPath));
                var testEvidence = BuildCombinedEvidence(prepared, rows, candidate.Index, usedOperations, usedActiveSeconds, false, lastLifecycle, lastWorkerCleanup, "TEST_ONLY_SYNTHETIC");
                var testPath = SaveEvidence(testEvidence, Path.Combine(evidenceRoot, $"v4-test-only-qualified-c{candidate.Index:D2}-{Guid.NewGuid():D}.json"));
                var testResult = new V4ScanQualificationResult("TEST_ONLY", candidate.Index, attempts, testEvidence, testPath, "Synthetic qualification; no DEVICE profile created.");
                await SaveCoordinatorSummary(testResult, prepared, evidenceRoot, cancellationToken).ConfigureAwait(false);
                return testResult;
            }
            if (testOnly && qualification.Status == "UNKNOWN")
                return await Stop("UNKNOWN", qualification.Diagnostic ?? "Controlled peer reported UNKNOWN.", candidate.Index).ConfigureAwait(false);
            if (qualification.Status == "LOAD_CLEAN" && qualification.Cleanup == "CONFIRMED" && qualification.Provenance == "REAL_AUDIOHOST")
            {
                attempts.Add(new(candidate.Index, candidate.CapturePeriod100ns, "REPEATABILITY_CONFIRMED", scanPath, qualification.Status, qualification.Cleanup, qualificationPath));
                var combined = BuildCombinedEvidence(prepared, rows, candidate.Index, usedOperations, usedActiveSeconds, false, lastLifecycle, lastWorkerCleanup, "LOAD_CLEAN");
                var combinedPath = SaveEvidence(combined, Path.Combine(evidenceRoot, $"v4-audiohost-qualified-{Guid.NewGuid():D}.json"));
                DeviceConfigurationProfile? pendingProfile = null;
                if (!testOnly && capture.DeviceState == "ACTIVE" && render.DeviceState == "ACTIVE")
                {
                    var shortSha = HashFile(source.ShortSummary); var repeatSha = HashFile(source.RepeatSummary); var repeatMatrixSha = HashFile(scanPath);
                    var profileQualification = new ProfileAudioHostQualification("LOAD_CLEAN", "CONFIRMED", qualification.RunId!, qualification.QualificationConfigSha256!, qualification.ResultSha256!, qualification.AudioHostBuildSha!, repeatMatrixSha, "REAL_AUDIOHOST", prepared.CaptureEndpointId, prepared.RenderEndpointId, candidate.CaptureFrames, candidate.RenderFrames, candidate.CapturePeriod100ns, prepared.Plan.MmcssModes[0], DateTimeOffset.UtcNow,
                        shortSha, repeatSha, repeatMatrixSha, HashFile(combinedPath));
                    var captureFormat = new ProfileAudioFormat("PCM16", 48_000, 2, 16, 16, $"CUSTOM:{candidate.CaptureFrames}:{candidate.CapturePeriod100ns}");
                    var renderFormat = new ProfileAudioFormat("PCM16", 48_000, 2, 16, 16, $"CUSTOM:{candidate.RenderFrames}:{candidate.RenderPeriod100ns}");
                    var selection = new ProfileStableShortSelection("DEVICE", prepared.CaptureEndpointId, prepared.RenderEndpointId, prepared.Plan.Route.Mode,
                        captureFormat, renderFormat, prepared.Plan.Route.Capture.SelectedIndicesZeroBased, prepared.Plan.Route.Render.SelectedIndicesZeroBased,
                        candidate.CaptureFrames, candidate.CapturePeriod100ns, candidate.CaptureFrames, candidate.CapturePeriod100ns,
                        candidate.RenderFrames, candidate.RenderPeriod100ns, candidate.RenderFrames, candidate.RenderPeriod100ns, prepared.Plan.MmcssModes[0], candidate.Index,
                        prepared.Consent.PlanFingerprint, prepared.Consent.DiscoveryGeneration, "MINIMUM_QUALIFIED_IN_CONSENTED_DOMAIN", 5, 5,
                        combined.RepeatabilityRuns.Select(r => r.OperationId).ToArray(), "REAL_WASAPI_V4_DUPLEX", Path.GetFileName(combinedPath), HashFile(combinedPath), DateTimeOffset.UtcNow, capture.ContainerId);
                    var profile = DeviceConfigurationProfileFactory.CreateStableShortFromV4(prepared, combinedPath, selection, capture, render,
                        audioHostQualification: profileQualification);
                    pendingProfile = profile;
                }
                await SaveCoordinatorSummary(new("LOAD_CLEAN", candidate.Index, attempts, combined, combinedPath, null) { PendingProfile = pendingProfile }, prepared, evidenceRoot, cancellationToken).ConfigureAwait(false);
                return new("LOAD_CLEAN", candidate.Index, attempts, combined, combinedPath, null) { PendingProfile = pendingProfile };
            }
            attempts.Add(new(candidate.Index, candidate.CapturePeriod100ns, "REPEATABILITY_CONFIRMED", scanPath, qualification.Status, qualification.Cleanup, qualificationPath));
            if (qualification.Status == "LOAD_UNSTABLE" && qualification.Cleanup == "CONFIRMED") continue;
            if (qualification.Status == "TEST_FIXTURE_SUCCEEDED" && testOnly && qualification.Cleanup == "CONFIRMED")
            {
                var testEvidence = BuildCombinedEvidence(prepared, rows, candidate.Index, usedOperations, usedActiveSeconds, false, lastLifecycle, lastWorkerCleanup, "TEST_ONLY_SYNTHETIC");
                var testPath = SaveEvidence(testEvidence, Path.Combine(evidenceRoot, $"v4-test-only-qualified-c{candidate.Index:D2}-{Guid.NewGuid():D}.json"));
                var testResult = new V4ScanQualificationResult("TEST_ONLY", candidate.Index, attempts, testEvidence, testPath, "Synthetic AudioHost fixture did not qualify a DEVICE profile.");
                await SaveCoordinatorSummary(testResult, prepared, evidenceRoot, cancellationToken).ConfigureAwait(false);
                return testResult;
            }
            return await Stop(qualification.Status is "QA_FAILED" or "UNKNOWN" or "NOT_QUALIFIED" ? qualification.Status : "UNKNOWN",
                qualification.Diagnostic ?? "AudioHost qualification result or cleanup was not acceptable.", candidate.Index).ConfigureAwait(false);
        }

        var exhausted = BuildCombinedEvidence(prepared, rows, null, usedOperations, usedActiveSeconds, false, lastLifecycle, lastWorkerCleanup,
            rows.Any(r => r.AudioHostQualification?.Status == "LOAD_UNSTABLE") ? "ALL_CANDIDATES_TRANSPORT_UNSTABLE" : "ALL_CANDIDATES_FAILED_SHORT_OR_REPEATABILITY");
        var finalPath = SaveEvidence(exhausted, Path.Combine(evidenceRoot, $"v4-no-stable-buffer-{Guid.NewGuid():D}.json"));
        await SaveCoordinatorSummary(new("NO_STABLE_BUFFER", null, attempts, exhausted, finalPath, "All discovered candidates failed with confirmed cleanup."), prepared, evidenceRoot, cancellationToken).ConfigureAwait(false);
        return new("NO_STABLE_BUFFER", null, attempts, exhausted, finalPath, "All discovered candidates failed with confirmed cleanup.");

        async Task<V4ScanQualificationResult> Stop(string status, string message, int candidateIndex, string? lifecycle = null)
        {
            var stopEvidence = BuildCombinedEvidence(prepared, rows, null, usedOperations, usedActiveSeconds, false,
                lifecycle ?? lastLifecycle, lastWorkerCleanup, message);
            var path = SaveEvidence(stopEvidence,
                Path.Combine(evidenceRoot, $"v4-qualification-{status.ToLowerInvariant()}-{Guid.NewGuid():D}.json"));
            var observedCleanup = candidateIndex >= 0 && candidateIndex < rows.Length
                ? rows[candidateIndex].AudioHostQualification?.Cleanup ?? lastWorkerCleanup
                : lastWorkerCleanup;
            attempts.Add(new(candidateIndex, prepared.Plan.Candidates[candidateIndex].CapturePeriod100ns, status, path, status, observedCleanup, null));
            await SaveCoordinatorSummary(new(status, null, attempts, stopEvidence, path, message), prepared, evidenceRoot, CancellationToken.None).ConfigureAwait(false);
            return new(status, null, attempts, stopEvidence, path, message);
        }
    }

    private static V2CandidateMatrixEntry NotTested(WorkerScanV2Candidate c, string why) => new(c, "NOT_TESTED", "NOT_TESTED", "NOT_TESTED", "NOT_ATTEMPTED", "NOT_ATTEMPTED", [why], true, why, "INCOMPLETE", "NOT_RUN", "NOT_RUN");

    private async Task<(string ShortSummary, string RepeatSummary)> SaveQualificationSource(WorkerScanV2Candidate c, V4AutomaticSearchResult scan,
        WorkerScanV2V4PreparedPlan prepared, string root, CancellationToken ct)
    {
        var entry = scan.Evidence.Candidates[0];
        var scopedCandidate = c with { Index = 0 };
        var dir = Path.Combine(root, "qualification-source", Guid.NewGuid().ToString("N")); var shortDir = Path.Combine(dir, "short"); var repeatDir = Path.Combine(dir, "repeat");
        Directory.CreateDirectory(shortDir); Directory.CreateDirectory(repeatDir);
        var shortCandidate = new { runId = prepared.RunId, captureEndpointId = prepared.CaptureEndpointId, renderEndpointId = prepared.RenderEndpointId, candidateIndex = 0, candidate = scopedCandidate, actualCaptureFrames = entry.ActualCaptureFrames, actualCapturePeriod100ns = entry.ActualCapturePeriod100ns, actualRenderFrames = entry.ActualRenderFrames, actualRenderPeriod100ns = entry.ActualRenderPeriod100ns, duplexDisposition = entry.DuplexDisposition, nativeDuplexDisposition = entry.NativeDuplexDisposition ?? "CLEAN", evaluatorReasonCode = entry.EvaluatorReasonCode ?? "CLEAN", captureInitializeEvidence = entry.CaptureInitializeEvidence, renderInitializeEvidence = entry.RenderInitializeEvidence, duplexEvidence = entry.DuplexEvidence, provenance = entry.Provenance };
        var shortBytes = JsonSerializer.SerializeToUtf8Bytes(shortCandidate, Json); await WriteNewVerified(Path.Combine(shortDir, "candidate-00.json"), shortBytes, ct).ConfigureAwait(false);
        var shortSummary = new { runId = prepared.RunId, provenance = entry.Provenance, mode = "SHORT_SEARCH", captureEndpointId = prepared.CaptureEndpointId, renderEndpointId = prepared.RenderEndpointId, route = prepared.Plan.Route.Mode, sampleRateHz = 48_000, encoding = "PCM16", candidateCount = 1, repeatabilityRequested = 0, repeatabilityCompleted = 0, sessionCleanup = "CLOSE_CONFIRMED", selectedCandidateIndex = 0, candidateFiles = new[] { new { path = "candidate-00.json", sha256 = Hash(shortBytes) } } };
        var shortSummaryPath = Path.Combine(shortDir, "short-search-summary.json"); var shortSummaryBytes = JsonSerializer.SerializeToUtf8Bytes(shortSummary, Json); await WriteNewVerified(shortSummaryPath, shortSummaryBytes, ct).ConfigureAwait(false);
        var repeatCandidate = new { runId = prepared.RunId, captureEndpointId = prepared.CaptureEndpointId, renderEndpointId = prepared.RenderEndpointId, candidateIndex = 0, candidate = scopedCandidate, actualCaptureFrames = entry.ActualCaptureFrames, actualCapturePeriod100ns = entry.ActualCapturePeriod100ns, actualRenderFrames = entry.ActualRenderFrames, actualRenderPeriod100ns = entry.ActualRenderPeriod100ns, duplexDisposition = entry.DuplexDisposition, nativeDuplexDisposition = entry.NativeDuplexDisposition ?? "CLEAN", evaluatorReasonCode = entry.EvaluatorReasonCode ?? "CLEAN", captureInitializeEvidence = entry.CaptureInitializeEvidence, renderInitializeEvidence = entry.RenderInitializeEvidence, duplexEvidence = entry.DuplexEvidence, repeatability = entry.RepeatabilityEvidence, provenance = entry.Provenance };
        var repeatCandidateBytes = JsonSerializer.SerializeToUtf8Bytes(repeatCandidate, Json); await WriteNewVerified(Path.Combine(repeatDir, "candidate-00.json"), repeatCandidateBytes, ct).ConfigureAwait(false);
        var r5Summary = new { runId = prepared.RunId, provenance = entry.Provenance, mode = "REPEAT_SELECTED", sourceEvidenceSha256 = Hash(shortSummaryBytes), captureEndpointId = prepared.CaptureEndpointId, renderEndpointId = prepared.RenderEndpointId, route = "MONO_TO_STEREO", sampleRateHz = 48_000, encoding = "PCM16", candidateCount = 1, repeatabilityRequested = 5, repeatabilityCompleted = 5, selectionState = "REPEATABILITY_CONFIRMED", sessionCleanup = "CLOSE_CONFIRMED", profileEligible = false, candidateMatrixSha256 = HashFile(scan.EvidencePath!), candidateMatrixPath = Path.GetFullPath(scan.EvidencePath!), selectedCandidateIndex = 0, candidateFiles = new[] { new { path = "candidate-00.json", sha256 = Hash(repeatCandidateBytes) } } };
        var repeatSummaryPath = Path.Combine(repeatDir, "repeatability-summary.json"); await WriteNewVerified(repeatSummaryPath, JsonSerializer.SerializeToUtf8Bytes(r5Summary, Json), ct).ConfigureAwait(false);
        return (shortSummaryPath, repeatSummaryPath);
    }

    private static V2CandidateMatrixEvidence BuildCombinedEvidence(WorkerScanV2V4PreparedPlan p, V2CandidateMatrixEntry[] entries, int? selected,
        int operations, int activeSeconds, bool diagnosticOnly, string lifecycle, string workerCleanup, string stopReason)
    {
        var rowSet = entries;
        for (var i = 0; i < rowSet.Length; i++) rowSet[i] = rowSet[i] with { LowerPeriodCoverage = selected is { } s && i <= s ? "COMPLETE" : selected is null && rowSet[i].DuplexDisposition is ("FAILED" or "UNSUPPORTED") ? "COMPLETE" : "INCOMPLETE" };
        var repeatRuns = selected is { } idx ? rowSet[idx].RepeatabilityEvidence : [];
        return new(1, p.Consent.PlanFingerprint, WorkerScanPlanV2Fingerprint.Route(p.CaptureEndpointId, p.RenderEndpointId, p.Plan.Route), lifecycle, p.Plan.CaptureBounds, p.Plan.RenderBounds, p.Plan.Candidates, rowSet,
            selected is null ? "MINIMUM_NOT_PROVEN" : "REPEATABILITY_CONFIRMED", rowSet.Any(e => e.Provenance.StartsWith("TEST_FAKE", StringComparison.Ordinal)) ? "TEST_FAKE_WASAPI_V4_DUPLEX" : "REAL_WASAPI_V4_DUPLEX")
        {
            RunId = p.RunId, CaptureEndpointId = p.CaptureEndpointId, RenderEndpointId = p.RenderEndpointId,
            EvidenceVersion = 3, DiscoveryGeneration = p.Consent.DiscoveryGeneration, SelectedCandidateIndex = selected, GlobalMinimumProven = false,
            BuildSha = Environment.GetEnvironmentVariable("KLIDAN_BUILD_SHA") ?? "UNKNOWN", UsedOperations = operations, MaximumOperations = p.Plan.MaximumOperations,
            UsedActiveDuplexSeconds = activeSeconds, MaximumActiveDuplexSeconds = p.Plan.MaximumActiveDuplexSeconds, StopReason = selected is null ? stopReason : "MINIMUM_QUALIFIED_IN_CONSENTED_CANDIDATE_DOMAIN",
            RepeatabilityRequested = selected is null ? 0 : 5, RepeatabilityCompleted = repeatRuns.Count, RepeatabilityRuns = repeatRuns,
            SessionCleanup = workerCleanup, ProfileEligible = false
        };
    }

    private static Task<V4CandidateQualificationAttempt> SaveAttempt(WorkerScanV2Candidate candidate, V4AutomaticSearchResult scan, string audioStatus, string cleanup, string? qPath, string root, CancellationToken ct)
    {
        var path = scan.EvidencePath ?? string.Empty;
        return Task.FromResult(new V4CandidateQualificationAttempt(candidate.Index, candidate.CapturePeriod100ns, scan.Evidence.SelectionState, path, audioStatus, cleanup, qPath));
    }

    private static async Task<string> WriteQualificationEvidence(WorkerScanV2Candidate c, AudioHostCandidateQualificationResult result,
        (string ShortSummary, string RepeatSummary) source, WorkerScanV2V4PreparedPlan prepared, string root, CancellationToken ct)
    {
        var path = Path.Combine(root, $"audiohost-result-c{c.Index:D2}-{Guid.NewGuid():D}.json");
        await WriteNewVerified(path, JsonSerializer.SerializeToUtf8Bytes(new { runId = prepared.RunId, captureEndpointId = prepared.CaptureEndpointId, renderEndpointId = prepared.RenderEndpointId, candidateIndex = c.Index, candidate = c, result, shortSearchSha256 = HashFile(source.ShortSummary), repeatabilitySummarySha256 = HashFile(source.RepeatSummary) }, Json), ct).ConfigureAwait(false);
        return path;
    }
    private static string SaveEvidence(V2CandidateMatrixEvidence evidence, string path) => V2CandidateMatrixEvidenceJson.SaveNoOverwriteAndVerify(evidence, path);
    private static async Task SaveCoordinatorSummary(V4ScanQualificationResult result, WorkerScanV2V4PreparedPlan prepared, string root, CancellationToken ct)
    {
        result = result with { RunId = prepared.RunId, CaptureEndpointId = prepared.CaptureEndpointId, RenderEndpointId = prepared.RenderEndpointId };
        var path = Path.Combine(root, $"scan-qualification-{Guid.NewGuid():D}.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        await WriteNewVerified(path, bytes, ct).ConfigureAwait(false);
        var readBack = JsonSerializer.Deserialize<V4ScanQualificationResult>(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("Terminal qualification evidence could not be read back.");
        if (readBack.RunId != prepared.RunId || readBack.CaptureEndpointId != prepared.CaptureEndpointId || readBack.RenderEndpointId != prepared.RenderEndpointId || readBack.Status != result.Status || readBack.Evidence.Lifecycle != result.Evidence.Lifecycle ||
            readBack.Evidence.SessionCleanup != result.Evidence.SessionCleanup || readBack.Evidence.StopReason != result.Evidence.StopReason ||
            readBack.Attempts.Count != result.Attempts.Count)
            throw new InvalidDataException("Terminal qualification evidence read-back differs from actual run state.");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string HashFile(string path) => Hash(File.ReadAllBytes(path));
    private static async Task WriteNewVerified(string path, byte[] bytes, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var f = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { await f.WriteAsync(bytes, ct).ConfigureAwait(false); await f.FlushAsync(ct).ConfigureAwait(false); f.Flush(true); }
            File.Move(temporary, path, overwrite: false);
            var readback = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(readback))) throw new InvalidDataException("Qualification source evidence failed SHA-256 read-back.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
