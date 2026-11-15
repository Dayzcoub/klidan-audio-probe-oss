using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record HistoricalAudioHostProfileImportRequest(
    string ScannerRunDirectory,
    string EvidenceIndexPath,
    string MinimumAuditPath,
    string ExpectedMinimumAuditSha256,
    string AudioHostEvidenceManifestPath,
    string ExpectedAudioHostEvidenceManifestSha256,
    string AudioHostRunDirectory,
    string OutputDirectory,
    string? ProbeVersion = null);

public static class HistoricalAudioHostProfileImporter
{
    public static string ImportToLocalProfile(HistoricalAudioHostProfileImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scannerRoot = Path.GetFullPath(request.ScannerRunDirectory);
        var audioRoot = Path.GetFullPath(request.AudioHostRunDirectory);
        var auditPath = Path.GetFullPath(request.MinimumAuditPath);
        var indexPath = Path.GetFullPath(request.EvidenceIndexPath);
        var audioManifestPath = Path.GetFullPath(request.AudioHostEvidenceManifestPath);
        var scannerManifestPath = Path.Combine(scannerRoot, "run-manifest.json");
        var runId = Guid.Parse("bc6638e5-6e3e-4f31-9a5a-c2084049d37f");
        RunEvidenceManifest.Verify(scannerRoot, scannerManifestPath, runId);

        var auditBytes = Read(auditPath);
        var auditSha = Sha(auditBytes);
        Need(HashEquals(auditSha, request.ExpectedMinimumAuditSha256), "Minimum audit SHA-256 differs from the operator-pinned value.");
        using var auditDoc = JsonDocument.Parse(auditBytes);
        var audit = auditDoc.RootElement;
        Need(Str(audit, "conclusionType") == "CONSENTED_DOMAIN_MINIMUM_AUDIT" && Str(audit, "runId") == runId.ToString("D") &&
            Str(audit, "sourceCommit") == "def5e75defb76f280009dac2f245d6d6e0ae4dd7" && Bool(audit, "minimumWithinConsentedDomainProven") &&
            !Bool(audit, "globalMinimumProven") && Str(audit, "lowerPeriodCoverage") == "COMPLETE", "Minimum audit identity, scope or conclusion is invalid.");
        var auditSelected = audit.GetProperty("selectedCandidate");
        Need(Num(auditSelected, "index") == 7 && Num(auditSelected, "period100ns") == 100_000 && Num(auditSelected, "captureFrames") == 480 && Num(auditSelected, "renderFrames") == 480 &&
            Str(auditSelected, "shortDisposition") == "CLEAN" && Num(auditSelected, "repeatabilityRequested") == 5 && Num(auditSelected, "repeatabilityCompleted") == 5 &&
            Str(auditSelected, "workerSessionCleanup") == "CLOSE_CONFIRMED", "Audit selected-candidate summary is incomplete or contradicts the source evidence.");

        var manifestBytes = Read(scannerManifestPath);
        var manifestSha = Sha(manifestBytes);
        Need(HashEquals(manifestSha, Str(audit, "sourceManifestSha256")), "Scanner manifest SHA-256 differs from the audit.");
        using var manifestDoc = JsonDocument.Parse(manifestBytes);
        var manifest = manifestDoc.RootElement;
        Need(Str(manifest, "workflowType") == "V4_SCAN_QUALIFICATION" && Str(manifest, "runId") == runId.ToString("D"), "Scanner manifest is not for the required historical run.");
        var manifestFiles = manifest.GetProperty("files").EnumerateArray().ToArray();
        string ManifestPathForSha(string hash) => manifestFiles.SingleOrDefault(x => HashEquals(Str(x, "sha256"), hash)) is { ValueKind: JsonValueKind.Object } item
            ? Path.GetFullPath(Path.Combine(scannerRoot, Str(item, "path"))) : throw new InvalidDataException("Required scanner source is not uniquely indexed by the original manifest.");

        var scannerResultPath = ManifestPathForSha(Str(audit, "qualificationEvidenceSha256"));
        Need(Path.GetFullPath(Str(audit, "qualificationEvidencePath")).Equals(scannerResultPath, StringComparison.OrdinalIgnoreCase), "Audit qualification source path does not resolve to its manifest-pinned file.");
        Need(HashEquals(Sha(Read(scannerResultPath)), Str(audit, "qualificationEvidenceSha256")), "Scanner qualification result SHA-256 differs from the audit.");
        using var scannerDoc = JsonDocument.Parse(Read(scannerResultPath));
        var scanner = scannerDoc.RootElement;
        var captureId = Str(scanner, "captureEndpointId");
        var renderId = Str(scanner, "renderEndpointId");
        Need(Str(scanner, "runId") == runId.ToString("D") && Str(scanner, "buildSha") == "def5e75defb76f280009dac2f245d6d6e0ae4dd7" &&
            captureId == Str(audit, "captureEndpointId") && renderId == Str(audit, "renderEndpointId") && Str(manifest, "captureEndpointId") == captureId && Str(manifest, "renderEndpointId") == renderId,
            "Scanner run, source commit, or exact endpoint pair differs from the audit/manifest.");
        ValidateAuditCandidates(audit, scanner, manifestFiles, scannerRoot, captureId, renderId);

        var indexBytes = Read(indexPath);
        var indexSha = Sha(indexBytes);
        using var indexDoc = JsonDocument.Parse(indexBytes);
        var index = indexDoc.RootElement;
        Need(Str(index, "sourceRunId") == runId.ToString("D") && HashEquals(Str(index, "sourceRunManifestSha256"), manifestSha) &&
            HashEquals(Str(index, "sourceRunResultSha256"), Sha(Read(scannerResultPath))) && Str(index, "sourceCommit") == Str(scanner, "buildSha"),
            "Historical evidence index does not match the original scanner evidence.");

        using var startDoc = JsonDocument.Parse(Read(Path.Combine(scannerRoot, "run-start.json")));
        var start = startDoc.RootElement;
        Need(Str(start, "runId") == runId.ToString("D") && Str(start, "captureEndpointId") == captureId && Str(start, "renderEndpointId") == renderId,
            "Original run-start endpoint identity differs from the verified scanner evidence.");

        var qualificationPath = Path.Combine(audioRoot, "qualification-request.json");
        var controlledPath = Path.Combine(audioRoot, "controlled-run-result.json");
        var watchdogConfigPath = Path.Combine(audioRoot, "watchdog-run-config.json");
        var watchdogPath = Path.Combine(audioRoot, "watchdog-journal.jsonl");
        var summaryPath = Path.Combine(audioRoot, "measurements", "load-qualification-summary.json");
        var stagePath = Path.Combine(audioRoot, "measurements", "run-stage-journal.jsonl");
        var audioManifestBytes = Read(audioManifestPath);
        var audioManifestSha = Sha(audioManifestBytes);
        Need(HashEquals(audioManifestSha, request.ExpectedAudioHostEvidenceManifestSha256), "AudioHost evidence manifest SHA-256 differs from the operator-pinned value.");
        using var audioManifestDoc = JsonDocument.Parse(audioManifestBytes);
        var audioManifest = audioManifestDoc.RootElement;
        ValidateAudioHostManifest(audioManifest, audioRoot, runId);
        var configSha = Sha(Read(qualificationPath));
        var controlledSha = Sha(Read(controlledPath));
        var summarySha = Sha(Read(summaryPath));
        var watchdogSha = Sha(Read(watchdogPath));
        using var requestDoc = JsonDocument.Parse(Read(qualificationPath));
        using var controlledDoc = JsonDocument.Parse(Read(controlledPath));
        using var watchdogConfigDoc = JsonDocument.Parse(Read(watchdogConfigPath));
        using var summaryDoc = JsonDocument.Parse(Read(summaryPath));
        var config = requestDoc.RootElement;
        var controlled = controlledDoc.RootElement;
        var watchdogConfig = watchdogConfigDoc.RootElement;
        var summary = summaryDoc.RootElement;
        var source = summary.GetProperty("sourceEvidence");
        var endpoints = summary.GetProperty("endpoints");
        var transport = summary.GetProperty("transport");
        var cleanup = summary.GetProperty("cleanup");
        Need(Str(config, "qualificationRunId") == "2b07ff5e-579d-451e-aa9b-539439aca44f" && Str(config, "qualificationState") == "PHYSICAL_QA_READY_PENDING_SEPARATE_AUTHORIZATION" &&
            Path.GetFullPath(Str(config.GetProperty("sourceEvidence"), "evidenceIndexPath")).Equals(indexPath, StringComparison.OrdinalIgnoreCase) &&
            Str(source, "sourceType") == "V4_SCAN_QUALIFICATION_HISTORICAL" && Str(source, "originalRunId") == runId.ToString("D") &&
            HashEquals(Str(source, "runManifestSha256"), manifestSha) && HashEquals(Str(source, "qualificationResultSha256"), Sha(Read(scannerResultPath))) &&
            HashEquals(Str(source, "evidenceIndexSha256"), indexSha) && Str(source, "sourceCommit") == Str(scanner, "buildSha"),
            "AudioHost request/summary historical source binding differs from original scanner evidence.");
        Need(Str(config.GetProperty("endpoints"), "captureId") == captureId && Str(config.GetProperty("endpoints"), "renderId") == renderId &&
            Bool(config.GetProperty("endpoints"), "requireFreshDiscovery") && Bool(config.GetProperty("endpoints"), "requireExactIdMatch"),
            "AudioHost config does not require fresh exact endpoint binding.");
        Need(Str(transport, "backend") == "NativeWasapiCoordinatedExclusive" && Num(transport, "sampleRateHz") == 48_000 && Str(transport, "encoding") == "PCM16" &&
            Str(transport, "voiceRouting") == "MONO_TO_STEREO" && Num(transport, "captureFrames") == 480 && Num(transport, "renderFrames") == 480 &&
            Num(transport, "period100ns") == 100_000 && Num(transport, "requestedCandidateIndex") == 7 && Str(transport, "mmcss") == "PRO_AUDIO" &&
            Num(transport, "testDurationSeconds") == 60, "AudioHost qualification geometry, route or duration is not the qualified 10 ms point.");
        Need(Str(controlled, "runId") == "55271755e4d24fd4b411bfd7e346d0c8" && Str(controlled, "result") == "LOAD_CLEAN" &&
            Str(controlled, "watchdogOutcome") == "RUNNER_SUCCEEDED" && Num(controlled, "watchdogExitCode") == 0 && Bool(controlled, "watchdogExited") &&
            HashEquals(Str(controlled, "qualificationConfigSha256"), configSha) && HashEquals(Str(controlled, "audioHostSha256"), Str(summary, "binarySha256")) &&
            Path.GetFullPath(Str(controlled, "qualificationConfigPath")).Equals(qualificationPath, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFullPath(Str(controlled, "runnerSummary")).Equals(summaryPath, StringComparison.OrdinalIgnoreCase) && Path.GetFullPath(Str(controlled, "watchdogJournal")).Equals(watchdogPath, StringComparison.OrdinalIgnoreCase) &&
            controlled.GetProperty("testOnlyFixture").ValueKind == JsonValueKind.Null && !Bool(controlled, "controlledPeerMode"), "Controlled AudioHost result or runner/watchdog identity is invalid.");
        Need(Str(summary, "qualificationRunId") == Str(config, "qualificationRunId") && Str(summary, "result") == "LOAD_CLEAN" &&
            Str(summary.GetProperty("buildInfo"), "gitSha") == "599d5c75e203fc6936b64e488050d373e884d41e" &&
            Str(summary, "expectedAudioHostGitSha") == Str(summary.GetProperty("buildInfo"), "gitSha") &&
            Str(endpoints, "captureId") == captureId && Str(endpoints, "renderId") == renderId &&
            Num(summary, "durationSeconds") >= 60 && Num(summary, "plannedDurationSeconds") == 60 &&
            summary.GetProperty("intervals").GetArrayLength() == 56 && summary.GetProperty("intervals").EnumerateArray().All(x => Str(x, "status") == "CLEAN_INTERVAL") &&
            Bool(cleanup, "confirmed") && Num(cleanup, "nativeCaptureOpen") == 0 && Num(cleanup, "nativeRenderOpen") == 0 && !Bool(cleanup, "forcedTermination") &&
            source.GetProperty("repeatabilityCleanup").GetArrayLength() == 1 && source.GetProperty("repeatabilityCleanup")[0].GetString() == "CAPTURE:RELEASE_ATTEMPTED;RENDER:RELEASE_ATTEMPTED" &&
            !Bool(summary, "profileCreated") && !Bool(summary, "persistentSettingsChanged") && !Bool(summary, "automaticRetry"), "AudioHost LOAD_CLEAN summary, interval evidence, binary identity, or native cleanup is invalid.");
        Need(Num(config, "schemaVersion") == 2 && Num(watchdogConfig, "hardDeadlineMs") > 0, "AudioHost qualification or watchdog config schema is invalid.");
        Need(Str(watchdogConfig, "runId") == "55271755e4d24fd4b411bfd7e346d0c8" && !Bool(watchdogConfig, "testMode"), "Watchdog config run ID or physical evidence mode is invalid.");
        ValidateWatchdog(watchdogPath, Str(controlled, "runId"));
        ValidateAudioHostRepeatBinding(scanner.GetProperty("candidates")[7], source, index.GetProperty("candidate"), transport, captureId, renderId);

        var sourceFiles = new List<ProfileHistoricalEvidenceFile>();
        sourceFiles.Add(new("SCANNER_RUN_MANIFEST", scannerManifestPath, manifestSha));
        sourceFiles.AddRange(manifestFiles.Select(x => new ProfileHistoricalEvidenceFile("SCANNER_EVIDENCE", Path.GetFullPath(Path.Combine(scannerRoot, Str(x, "path"))), Str(x, "sha256"))));
        sourceFiles.Add(new("HISTORICAL_EVIDENCE_INDEX", indexPath, indexSha));
        sourceFiles.Add(new("MINIMUM_AUDIT", auditPath, auditSha));
        sourceFiles.Add(new("AUDIOHOST_EVIDENCE_MANIFEST", audioManifestPath, audioManifestSha));
        sourceFiles.AddRange(audioManifest.GetProperty("files").EnumerateArray().Select(x =>
        {
            var path = Path.GetFullPath(Path.Combine(audioRoot, SafeRelativePath(Str(x, "path"))));
            return new ProfileHistoricalEvidenceFile(path.EndsWith("load-qualification-summary.json", StringComparison.OrdinalIgnoreCase) ? "AUDIOHOST_SUMMARY" : "AUDIOHOST_RUNNER_EVIDENCE", path, Str(x, "sha256"));
        }));
        ValidateStableSourceHashes(sourceFiles);

        var matrixSha = Sha(Read(scannerResultPath));
        var repeatRows = scanner.GetProperty("candidates")[7].GetProperty("repeatabilityEvidence").EnumerateArray().ToArray();
        var repeatIds = repeatRows.Select(x => Guid.Parse(Str(x, "operationId"))).ToArray();
        var qualifiedAt = DateTimeOffset.Parse(Str(summary, "startedUtc"), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
        var selection = new ProfileStableShortSelection("DEVICE", captureId, renderId, "MONO_TO_STEREO",
            new("PCM16", 48_000, 2, 16, 16, "CUSTOM:100000:480"), new("PCM16", 48_000, 2, 16, 16, "CUSTOM:100000:480"),
            [0], [0, 1], 480, 100_000, 480, 100_000, 480, 100_000, 480, 100_000, "PRO_AUDIO", 7,
            Str(scanner, "planFingerprint"), checked((ulong)Num(scanner, "discoveryGeneration")), "MINIMUM_QUALIFIED_IN_CONSENTED_DOMAIN", 5, 5, repeatIds,
            "REAL_WASAPI_V4_DUPLEX", Path.GetFileName(scannerResultPath), matrixSha, qualifiedAt);
        var binding = new ProfileHistoricalAudioHostBinding("V4_SCAN_QUALIFICATION_HISTORICAL", runId.ToString("D"), Str(config, "qualificationRunId"), Guid.Parse(Str(controlled, "runId")).ToString("D"),
            Str(scanner, "buildSha"), Str(summary.GetProperty("buildInfo"), "gitSha"), manifestSha, matrixSha, indexSha, auditSha, audioManifestSha, configSha, controlledSha, summarySha, watchdogSha,
            7, captureId, renderId, "PCM16", 48_000, "MONO_TO_STEREO", 480, 480, 100_000, "CLEAN", repeatIds, repeatRows.Select(x => Str(x, "result")).ToArray(),
            Str(source, "sessionCleanup"), repeatRows.Select(x => Str(x, "cleanup")).ToArray(), Str(controlled, "watchdogOutcome"), checked((int)Num(controlled, "watchdogExitCode")),
            Str(summary, "result"), Bool(cleanup, "confirmed"), Bool(cleanup, "forcedTermination"), Bool(audit, "minimumWithinConsentedDomainProven"), Bool(audit, "globalMinimumProven"),
            DateTimeOffset.UtcNow, sourceFiles);
        var qualification = new ProfileAudioHostQualification("LOAD_CLEAN", "CONFIRMED", Str(config, "qualificationRunId"), configSha, summarySha,
            Str(summary.GetProperty("buildInfo"), "gitSha"), matrixSha, "REAL_AUDIOHOST", captureId, renderId, 480, 480, 100_000, "PRO_AUDIO", qualifiedAt);
        var periodText = "10 ms";
        var profile = new DeviceConfigurationProfile(5, DeterministicProfileId(runId, Guid.Parse(binding.QualificationRunId), Guid.Parse(binding.RunnerRunId)), qualifiedAt,
            request.ProbeVersion ?? "historical-import-v1", "WINDOWS", "WASAPI",
            new(Environment.OSVersion.VersionString, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()), new(null, null, null, null, null, null, null),
            new(new(captureId, "CAPTURE", "ACTIVE", Str(start, "captureFriendlyName"), "LOCAL_EXACT"), new(renderId, "RENDER", "ACTIVE", Str(start, "renderFriendlyName"), "LOCAL_EXACT")),
            new([], [], []), [], [new("COORDINATED_EXCLUSIVE", selection.CaptureFormat with { Period = null }, selection.RenderFormat with { Period = null }, [], true, [], [])],
            new(new("COORDINATED_EXCLUSIVE", selection.CaptureFormat, periodText, []), null, []) { Scope = "KLIDAN_QUALIFIED", Confidence = "VERIFIED" }, "LOCAL_MACHINE")
        {
            StableShortSelection = selection,
            AudioHostQualification = qualification,
            HistoricalAudioHostBinding = binding
        };
        profile = DeviceConfigurationProfileJson.Validate(profile);

        RunEvidenceManifest.Verify(scannerRoot, scannerManifestPath, runId);
        ValidateStableSourceHashes(sourceFiles);
        var output = DeviceConfigurationProfileStorage.SaveLocal(profile, request.OutputDirectory);
        var readBack = DeviceConfigurationProfileJson.Parse(Read(output));
        Need(readBack.ProfileId == profile.ProfileId && readBack.HistoricalAudioHostBinding?.AuditSha256 == auditSha &&
            readBack.StableShortSelection?.SearchQualification == "MINIMUM_QUALIFIED_IN_CONSENTED_DOMAIN" && readBack.HistoricalAudioHostBinding.GlobalMinimumProven == false,
            "Imported Profile v5 failed identity/scope read-back.");
        return output;
    }

    private static void ValidateAudioHostManifest(JsonElement manifest, string audioRoot, Guid scannerRunId)
    {
        Need(Str(manifest, "artifactType") == "AUDIOHOST_PHYSICAL_RUN_EVIDENCE_MANIFEST" && Num(manifest, "schemaVersion") == 1 &&
            Str(manifest, "qualificationRunId") == "2b07ff5e-579d-451e-aa9b-539439aca44f" && Str(manifest, "runnerRunId") == "55271755e4d24fd4b411bfd7e346d0c8" &&
            Str(manifest, "originalScannerRunId") == scannerRunId.ToString("D") && Str(manifest, "sourceCommit") == "def5e75defb76f280009dac2f245d6d6e0ae4dd7" &&
            Str(manifest, "audioHostBuildCommit") == "599d5c75e203fc6936b64e488050d373e884d41e" &&
            Str(manifest, "captureEndpointId") == "{0.0.1.00000000}.{afe1b68b-8f76-4815-aa2e-1e544322465d}" &&
            Str(manifest, "renderEndpointId") == "{0.0.0.00000000}.{ba4ca4cc-35d7-437a-b53b-99ffedc7e5b8}" &&
            Num(manifest, "sampleRateHz") == 48_000 && Str(manifest, "encoding") == "PCM16" && Str(manifest, "route") == "MONO_TO_STEREO" &&
            Num(manifest, "captureFrames") == 480 && Num(manifest, "renderFrames") == 480 && Num(manifest, "period100ns") == 100_000 &&
            Str(manifest, "result") == "LOAD_CLEAN" && Str(manifest, "watchdogOutcome") == "RUNNER_SUCCEEDED" && Num(manifest, "watchdogExitCode") == 0 &&
            Bool(manifest, "nativeCleanupConfirmed") && !Bool(manifest, "forcedTermination"), "AudioHost physical evidence manifest identity or qualification facts are invalid.");
        var listed = manifest.GetProperty("files").EnumerateArray().ToArray();
        Need(listed.Length > 0 && listed.Select(x => Str(x, "path")).Distinct(StringComparer.OrdinalIgnoreCase).Count() == listed.Length, "AudioHost manifest file list is empty or contains duplicate paths.");
        foreach (var entry in listed)
        {
            var path = Path.GetFullPath(Path.Combine(audioRoot, SafeRelativePath(Str(entry, "path"))));
            Need(path.StartsWith(audioRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "AudioHost manifest path escapes the physical-run directory.");
            var bytes = Read(path);
            Need(bytes.LongLength == Num(entry, "length") && HashEquals(Sha(bytes), Str(entry, "sha256")), $"AudioHost manifest file SHA/length mismatch: {entry.GetProperty("path").GetString()}");
        }
        var actual = Directory.EnumerateFiles(audioRoot, "*", SearchOption.AllDirectories).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = listed.Select(x => Path.GetFullPath(Path.Combine(audioRoot, SafeRelativePath(Str(x, "path"))))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Need(actual.SetEquals(expected), "AudioHost run directory has files missing from or additional to its evidence manifest.");
    }

    private static string SafeRelativePath(string path)
    {
        Need(!Path.IsPathRooted(path) && !path.Split(['/', '\\']).Any(part => part is ".." or ""), "Evidence manifest contains an unsafe relative path.");
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static void ValidateAuditCandidates(JsonElement audit, JsonElement scanner, JsonElement[] manifestFiles, string scannerRoot, string captureId, string renderId)
    {
        var rows = scanner.GetProperty("candidates").EnumerateArray().ToArray();
        var audited = audit.GetProperty("candidates").EnumerateArray().ToArray();
        var attempts = ManifestJson(manifestFiles, scannerRoot, "scan-qualification-").GetProperty("attempts").EnumerateArray().ToArray();
        Need(rows.Length == 8 && audited.Length == 8 && attempts.Length == 8, "Historical scanner domain is not exactly eight candidates.");
        for (var i = 0; i < 8; i++)
        {
            var row = rows[i]; var proof = audited[i]; var attempt = attempts.SingleOrDefault(x => Num(x, "candidateIndex") == i);
            Need(attempt.ValueKind == JsonValueKind.Object && Num(proof, "candidateIndex") == i && Num(proof, "period100ns") == Num(row.GetProperty("candidate"), "requestedDuration100ns") &&
                Str(proof, "sourceSha256") == Sha(Read(Str(attempt, "scanEvidencePath"))) && Str(attempt, "cleanup") == "CLOSE_CONFIRMED" &&
                Str(proof, "workerSessionCleanup") == "CLOSE_CONFIRMED", $"Historical candidate {i} attempt identity, source hash, or worker close is invalid.");
            var candidate = row.GetProperty("candidate");
            var framesC = Num(candidate, "captureFrames"); var framesR = Num(candidate, "renderFrames"); var periodC = Num(candidate, "capturePeriod100ns"); var periodR = Num(candidate, "renderPeriod100ns");
            Need(Str(row, "provenance") == "REAL_WASAPI_V4_DUPLEX" && !Bool(row, "duplexNotRun") && row.GetProperty("duplexEvidence").ValueKind == JsonValueKind.Object &&
                Num(row, "actualCaptureFrames") == framesC && Num(row, "actualRenderFrames") == framesR && Num(row, "actualCapturePeriod100ns") == periodC && Num(row, "actualRenderPeriod100ns") == periodR &&
                Num(proof, "actualCaptureFrames") == framesC && Num(proof, "actualRenderFrames") == framesR && Num(proof, "actualCapturePeriod100ns") == periodC && Num(proof, "actualRenderPeriod100ns") == periodR,
                $"Historical candidate {i} lacks exact real Duplex geometry/provenance.");
            ValidateNativeReport(row.GetProperty("duplexEvidence").GetProperty("report"), captureId, renderId, framesC, framesR, periodC, periodR);
            if (i < 7) Need(Str(row, "duplexDisposition") == "FAILED" && Str(proof, "classification") == "FAILED", $"Lower candidate {i} is not proven FAILED.");
            else Need(Num(candidate, "index") == 7 && Num(candidate, "requestedDuration100ns") == 100_000 && Num(candidate, "captureFrames") == 480 && Num(candidate, "renderFrames") == 480 &&
                Str(row, "duplexDisposition") == "CLEAN" && Str(proof, "classification") == "CLEAN" &&
                Str(row, "repeatabilityStatus") == "CONFIRMED" && row.GetProperty("repeatabilityEvidence").ValueKind == JsonValueKind.Array && row.GetProperty("repeatabilityEvidence").GetArrayLength() == 5,
                "Candidate 7 does not contain exact 10 ms SHORT_CLEAN and five repeatability observations.");
            if (i == 7)
            {
                var duplex = row.GetProperty("duplexEvidence");
                Need(Str(duplex, "captureEndpointId") == Str(scanner, "captureEndpointId") && Str(duplex, "renderEndpointId") == Str(scanner, "renderEndpointId") &&
                    Num(duplex, "actualCaptureFrames") == 480 && Num(duplex, "actualRenderFrames") == 480 && Num(duplex, "actualCapturePeriod100ns") == 100_000 && Num(duplex, "actualRenderPeriod100ns") == 100_000 &&
                    Str(duplex, "duplexDisposition") == "CLEAN" && Str(duplex, "provenance") == "REAL_WASAPI_V4_DUPLEX", "SHORT native report does not bind candidate 7 to its exact endpoints and geometry.");
            }
            var expectedSource = Path.GetFullPath(Str(attempt, "scanEvidencePath"));
            Need(Path.GetFullPath(Str(proof, "sourcePath")).Equals(expectedSource, StringComparison.OrdinalIgnoreCase), $"Historical candidate {i} source path differs from the original qualification attempt.");
        }
        Need(Str(scanner, "sessionCleanup") == "CLOSE_CONFIRMED" && !Bool(scanner, "globalMinimumProven"), "Scanner cleanup/global-minimum fields contradict the historical contract.");
    }

    private static void ValidateAudioHostRepeatBinding(JsonElement selected, JsonElement source, JsonElement indexCandidate, JsonElement transport, string captureId, string renderId)
    {
        var repeats = selected.GetProperty("repeatabilityEvidence").EnumerateArray().ToArray();
        var boundIds = source.GetProperty("repeatabilityOperationIds").EnumerateArray().Select(x => Guid.Parse(x.GetString()!)).ToArray();
        Need(repeats.Length == 5 && boundIds.Length == 5 && boundIds.Distinct().Count() == 5, "Scanner R5 operation ID list is missing or duplicated.");
        foreach (var item in repeats)
        {
            var native = item.GetProperty("evidence");
            Need(Str(item, "result") == "CLEAN" && Str(item, "provenance") == "REAL_WASAPI_V4_DUPLEX" && Str(item, "cleanup") == "CAPTURE:RELEASE_ATTEMPTED;RENDER:RELEASE_ATTEMPTED" &&
                Guid.Parse(Str(item, "operationId")) != Guid.Empty && Num(item, "candidateIndex") == 7 && Num(item, "actualCaptureFrames") == 480 && Num(item, "actualRenderFrames") == 480 &&
                Num(item, "actualCapturePeriod100ns") == 100_000 && Num(item, "actualRenderPeriod100ns") == 100_000 && Str(item, "sessionId") == Str(indexCandidate, "repeatabilitySessionId"),
                "Scanner repeatability entry failed identity, geometry, result, or cleanup validation.");
            Need(Str(native, "captureEndpointId") == captureId && Str(native, "renderEndpointId") == renderId &&
                Num(native, "actualCaptureFrames") == 480 && Num(native, "actualRenderFrames") == 480 && Num(native, "actualCapturePeriod100ns") == 100_000 && Num(native, "actualRenderPeriod100ns") == 100_000 &&
                Str(native, "duplexDisposition") == "CLEAN" && Str(native, "provenance") == "REAL_WASAPI_V4_DUPLEX",
                "R5 native evidence endpoint pair differs from the AudioHost historical source binding.");
            ValidateNativeReport(native.GetProperty("report"), captureId, renderId, 480, 480, 100_000, 100_000);
        }
        Need(repeats.Select(x => Guid.Parse(Str(x, "operationId"))).Order().SequenceEqual(boundIds.Order()), "AudioHost request does not bind all five original R5 operation IDs.");
        Need(Num(transport, "requestedCandidateIndex") == 7 && Num(transport, "captureFrames") == 480 && Num(transport, "renderFrames") == 480 && Num(transport, "period100ns") == 100_000,
            "AudioHost request does not bind the original R5 candidate geometry.");
    }

    private static void ValidateNativeReport(JsonElement report, string captureId, string renderId, double captureFrames, double renderFrames, double capturePeriod, double renderPeriod)
    {
        var requested = report.GetProperty("requested");
        var route = report.GetProperty("route");
        var capture = report.GetProperty("capture");
        var render = report.GetProperty("render");
        Need(Str(report, "provenance") == "REAL_WASAPI_DUPLEX_V2" && Str(requested, "captureEndpointId") == captureId && Str(requested, "renderEndpointId") == renderId &&
            Num(requested, "sampleRateHz") == 48_000 && Str(requested, "encoding") == "PCM16" && Str(route, "mode") == "MONO_TO_STEREO" &&
            Str(capture, "endpointId") == captureId && Str(render, "endpointId") == renderId &&
            Num(capture.GetProperty("format"), "sampleRateHz") == 48_000 && Str(capture.GetProperty("format"), "encoding") == "PCM16" &&
            Num(render.GetProperty("format"), "sampleRateHz") == 48_000 && Str(render.GetProperty("format"), "encoding") == "PCM16" &&
            Num(capture, "actualBufferFrames") == captureFrames && Num(render, "actualBufferFrames") == renderFrames &&
            Num(capture, "requestedPeriod100ns") == capturePeriod && Num(render, "requestedPeriod100ns") == renderPeriod &&
            Num(capture, "actualBufferPeriod100ns") == capturePeriod && Num(render, "actualBufferPeriod100ns") == renderPeriod &&
            Str(requested, "period") == $"CUSTOM_V2:{capturePeriod:0}:{captureFrames:0}:{renderPeriod:0}:{renderFrames:0}",
            "Original native Duplex report does not prove exact endpoint pair, format, route, and per-side buffer geometry.");
    }

    private static JsonElement ManifestJson(JsonElement[] files, string root, string filePrefix)
    {
        var matches = files.Select(x => Path.Combine(root, Str(x, "path"))).Where(path => Path.GetFileName(path).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Scanner coordinator evidence is missing or ambiguous.");
        using var doc = JsonDocument.Parse(Read(matches[0]));
        return doc.RootElement.Clone();
    }

    private static void ValidateWatchdog(string path, string runId)
    {
        var lines = File.ReadAllLines(path);
        Need(lines.Length > 0, "Watchdog journal is empty.");
        var events = lines.Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Need(events.All(doc => Str(doc.RootElement, "runId") == runId && !Bool(doc.RootElement, "forcedTermination")), "Watchdog journal has a mismatched run or forced termination.");
            var final = events[^1].RootElement;
            Need(Str(final, "eventName") == "WATCHDOG_FINISH" && Str(final, "childProcessOutcome") == "RUNNER_SUCCEEDED" &&
                Num(final.GetProperty("gracefulShutdownAttempts"), "runnerExitCode") == 0 && final.GetProperty("gracefulShutdownAttempts").GetProperty("remainingProcessIds").GetArrayLength() == 0,
                "Watchdog final journal record does not prove successful runner exit and cleanup.");
        }
        finally { foreach (var doc in events) doc.Dispose(); }
    }

    private static void ValidateStableSourceHashes(IEnumerable<ProfileHistoricalEvidenceFile> files)
    {
        foreach (var file in files) Need(HashEquals(Sha(Read(file.Path)), file.Sha256), $"Historical source file changed after validation: {file.Role}.");
    }

    private static Guid DeterministicProfileId(Guid originalRunId, Guid qualificationRunId, Guid runnerRunId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"KLIDAN_PROFILE_V5_HISTORICAL|{originalRunId:D}|{qualificationRunId:D}|{runnerRunId:D}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static byte[] Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 64 * 1024 * 1024) throw new InvalidDataException($"Required evidence file is missing or too large: {path}");
        return File.ReadAllBytes(path);
    }
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool HashEquals(string left, string right) => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    private static string Str(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException($"Required evidence value {name} is null.");
    private static double Num(JsonElement value, string name) => value.GetProperty(name).GetDouble();
    private static bool Bool(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
