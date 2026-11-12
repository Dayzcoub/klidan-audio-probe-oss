using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.Worker;

/// <summary>Invokes the manifest-pinned AudioHost QA entrypoint for one exactly authorized candidate.</summary>
public sealed class AudioHostPowerShellQualificationAdapter(string powerShell = "pwsh") : IAudioHostQualificationAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<AudioHostCandidateQualificationResult> QualifyAsync(AudioHostCandidateQualificationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            ValidateRequest(request);
            var package = Path.GetFullPath(request.PackageDirectory);
            var manifestPath = Path.Combine(package, "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false))!.AsObject();
            var buildSha = manifest["audioHostBuildInfo"]?["gitSha"]?.GetValue<string>() ?? throw new InvalidDataException("AudioHost package has no BuildSha.");
            var entrypoint = Path.Combine(package, "scripts", "qa", "Invoke-AudioHostPhysicalQa.ps1");
            if (!File.Exists(entrypoint) || !manifest["files"]!.AsArray().Any(f => f?["path"]?.GetValue<string>() == "scripts/qa/Invoke-AudioHostPhysicalQa.ps1" && HashFile(entrypoint) == f["sha256"]!.GetValue<string>()))
                throw new InvalidDataException("AudioHost physical QA entrypoint is absent or differs from its package manifest.");

            var evidenceRoot = Path.GetFullPath(request.EvidenceRoot);
            Directory.CreateDirectory(evidenceRoot);
            var runId = Guid.NewGuid();
            var runRoot = Path.Combine(evidenceRoot, runId.ToString("N"));
            Directory.CreateDirectory(runRoot);
            var templatePath = Path.Combine(package, "docs", "qa", "B5B.11-audiohost-load-qualification.json");
            if (!manifest["files"]!.AsArray().Any(f => f?["path"]?.GetValue<string>() == "docs/qa/B5B.11-audiohost-load-qualification.json" && HashFile(templatePath) == f["sha256"]!.GetValue<string>()))
                throw new InvalidDataException("AudioHost candidate template differs from its package manifest.");
            var config = JsonNode.Parse(await File.ReadAllBytesAsync(templatePath, cancellationToken).ConfigureAwait(false))!.AsObject();
            Configure(config, request, runId);
            var configPath = Path.Combine(runRoot, "qualification-request.json");
            await WriteNewVerifiedAsync(configPath, JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions), cancellationToken).ConfigureAwait(false);
            var configSha = HashFile(configPath);
            var runEvidence = Path.Combine(runRoot, "runner-evidence");

            var psi = new ProcessStartInfo(powerShell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-File"); psi.ArgumentList.Add(entrypoint);
            psi.ArgumentList.Add("-PackageDirectory"); psi.ArgumentList.Add(package);
            psi.ArgumentList.Add("-QualificationConfigPath"); psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add("-ExpectedQualificationConfigSha256"); psi.ArgumentList.Add(configSha);
            psi.ArgumentList.Add("-EvidenceRoot"); psi.ArgumentList.Add(runEvidence);
            psi.ArgumentList.Add("-AuthorizePhysicalLoad"); psi.ArgumentList.Add("-NoOpenSpeakersConfirmed");
            if (request.TestOnly) psi.Environment["KLIDAN_TEST_ONLY_PEER_SCENARIO"] = Environment.GetEnvironmentVariable("KLIDAN_TEST_ONLY_PEER_SCENARIO") == "unknown" ? "unknown" : "success";
            if (request.TestOnly)
            {
                if (request.TestFixture != "TEST_ONLY_CONTROLLED_PEER") throw new InvalidDataException("TEST_ONLY qualification requires the controlled IPC peer route.");
                var peer = Path.Combine(package, "tools", "AudioHostQaIpcPeerFixture", "AudioHostQaIpcPeerFixture.exe");
                var peerSha = HashFile(peer);
                var peerEntry = manifest["files"]!.AsArray().SingleOrDefault(f => f?["path"]?.GetValue<string>() == "tools/AudioHostQaIpcPeerFixture/AudioHostQaIpcPeerFixture.exe");
                if (peerEntry?["sha256"]?.GetValue<string>() != peerSha) throw new InvalidDataException("Controlled IPC peer differs from package manifest.");
                psi.ArgumentList.Add("-TestOnlyFixture"); psi.ArgumentList.Add("TEST_ONLY_CONTROLLED_PEER");
                psi.ArgumentList.Add("-TestOnlyPeerPath"); psi.ArgumentList.Add(peer);
                psi.ArgumentList.Add("-TestOnlyPeerSha256"); psi.ArgumentList.Add(peerSha);
                psi.ArgumentList.Add("-TestOnlyPeerScenario"); psi.ArgumentList.Add(Environment.GetEnvironmentVariable("KLIDAN_TEST_ONLY_PEER_SCENARIO") == "unknown" ? "unknown" : "success");
            }
            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return Unknown(runId, configSha, buildSha, "Could not start AudioHost QA adapter process.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(175));
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                return Unknown(runId, configSha, buildSha, cancellationToken.IsCancellationRequested ? "Operator cancellation." : "AudioHost QA adapter deadline exceeded.");
            }
            _ = await stdout.ConfigureAwait(false); _ = await stderr.ConfigureAwait(false);
            var resultFiles = Directory.Exists(runEvidence) ? Directory.GetFiles(runEvidence, "controlled-run-result.json", SearchOption.AllDirectories) : [];
            if (resultFiles.Length != 1) return Unknown(runId, configSha, buildSha, "AudioHost QA result is missing or ambiguous.");
            var resultBytes = await File.ReadAllBytesAsync(resultFiles[0], cancellationToken).ConfigureAwait(false);
            var result = JsonNode.Parse(resultBytes)!.AsObject();
            var actualConfigSha = result["qualificationConfigSha256"]?.GetValue<string>();
            if (actualConfigSha != configSha) return Unknown(runId, configSha, buildSha, "AudioHost QA result is not bound to the exact request SHA.");
            var status = result["result"]?.GetValue<string>() ?? "UNKNOWN";
            var cleanup = status switch
            {
                "LOAD_CLEAN" => ReadCleanCleanup(result),
                "LOAD_UNSTABLE" => IsProvenTransportFailure(result) ? ReadAnyCleanup(result) : "UNKNOWN",
                "TEST_FIXTURE_SUCCEEDED" => request.TestOnly && WatchdogSucceeded(result) ? "CONFIRMED" : "UNKNOWN",
                "TEST_ONLY" => request.TestOnly && WatchdogSucceeded(result) && ReadAnyCleanup(result) == "CONFIRMED" ? "CONFIRMED" : "UNKNOWN",
                "QA_FAILED" => ReadAnyCleanup(result),
                _ => "UNKNOWN"
            };
            if (!process.HasExited || process.ExitCode is not (0 or 1) || cleanup != "CONFIRMED") status = status is "LOAD_CLEAN" or "LOAD_UNSTABLE" or "QA_FAILED" ? "UNKNOWN" : status;
            if (status is not ("LOAD_CLEAN" or "LOAD_UNSTABLE" or "QA_FAILED" or "TEST_FIXTURE_SUCCEEDED" or "TEST_ONLY")) status = "UNKNOWN";
            var resultSha = Convert.ToHexString(SHA256.HashData(resultBytes)).ToLowerInvariant();
            return new(status, cleanup, status is "LOAD_CLEAN" or "LOAD_UNSTABLE" or "TEST_FIXTURE_SUCCEEDED" or "TEST_ONLY" ? null : "AudioHost qualification did not produce a clean transport result.",
                runId.ToString("D"), configSha, resultSha, buildSha, HashFile(request.RepeatabilityMatrixPath), resultFiles[0], request.TestOnly ? "TEST_ONLY" : "REAL_AUDIOHOST");
        }
        catch (OperationCanceledException) { return Unknown(null, null, null, "Operator cancellation."); }
        catch (Exception ex) { return new("QA_FAILED", "UNKNOWN", ex.Message, null, null, null, null, null, null, request.TestOnly ? "TEST_ONLY" : "REAL_AUDIOHOST"); }
    }

    private static void ValidateRequest(AudioHostCandidateQualificationRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var a = r.Authorization; var p = r.Prepared;
        if (!a.Accepted || !a.OpenSpeakersConfirmedOff || a.ConfirmationId == Guid.Empty || a.MaximumRuns != p.Plan.Candidates.Count || a.MaximumRuns is < 1 or > 8 || a.MaximumDurationSeconds != 60 * a.MaximumRuns || a.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            a.PlanFingerprint != p.Consent.PlanFingerprint || a.DiscoveryGeneration != p.Consent.DiscoveryGeneration || a.CaptureEndpointId != p.CaptureEndpointId || a.RenderEndpointId != p.RenderEndpointId ||
            !p.Consent.Accepted || p.Plan.RepeatabilityRuns != 5 || r.Candidate.RejectionReason.Length != 0 || !p.Plan.Candidates.Any(c => c == r.Candidate) ||
            p.Plan.Route.Mode != "MONO_TO_STEREO" || p.Plan.Route.Capture is not { SampleRateHz: 48_000, Channels: 2, Encoding: "PCM16" } || p.Plan.Route.Render is not { SampleRateHz: 48_000, Channels: 2, Encoding: "PCM16" } ||
            r.Candidate.CaptureFrames != r.Candidate.RenderFrames || r.Candidate.CapturePeriod100ns != r.Candidate.RenderPeriod100ns || r.Candidate.RequestedDuration100ns != r.Candidate.CapturePeriod100ns)
            throw new InvalidDataException("AudioHost qualification requires one unexpired bounded-plan authorization bound to this exact supported candidate and endpoint pair.");
        foreach (var path in new[] { r.ShortSearchSummaryPath, r.RepeatabilitySummaryPath, r.RepeatabilityMatrixPath })
            if (!File.Exists(path)) throw new FileNotFoundException("Immutable scanner source evidence is missing.", path);
    }

    private static void Configure(JsonObject c, AudioHostCandidateQualificationRequest r, Guid runId)
    {
        var p = r.Prepared; var candidate = r.Candidate;
        c["schemaVersion"] = 2; c["qualificationRunId"] = runId.ToString("D");
        c["qualificationId"] = $"KLIDAN-AUDIOHOST-B5B.17-C{candidate.CaptureFrames}-R{candidate.RenderFrames}";
        var shortSha = HashFile(r.ShortSearchSummaryPath); var repeatSha = HashFile(r.RepeatabilitySummaryPath); var matrixSha = HashFile(r.RepeatabilityMatrixPath);
        var source = c["sourceEvidence"]!.AsObject();
        source["shortSearchSummary"] = Path.GetFullPath(r.ShortSearchSummaryPath); source["shortSearchSha256"] = shortSha;
        source["repeatabilitySummary"] = Path.GetFullPath(r.RepeatabilitySummaryPath); source["repeatabilitySummarySha256"] = repeatSha; source["repeatabilityMatrixSha256"] = matrixSha;
        source["repeatabilityResult"] = "5/5 CLEAN"; source["repeatabilityCleanup"] = "CLOSE_CONFIRMED"; source["profileEligible"] = false;
        var endpoints = c["endpoints"]!.AsObject(); endpoints["captureId"] = p.CaptureEndpointId; endpoints["renderId"] = p.RenderEndpointId;
        var shortSummary = JsonNode.Parse(File.ReadAllBytes(r.ShortSearchSummaryPath))!.AsObject();
        var t = c["transport"]!.AsObject(); t["captureFrames"] = candidate.CaptureFrames; t["renderFrames"] = candidate.RenderFrames; t["period100ns"] = candidate.CapturePeriod100ns; t["requestedCandidateIndex"] = shortSummary["selectedCandidateIndex"]!.GetValue<int>();
        var captureChannels = p.Plan.Route.Capture.SelectedIndicesZeroBased.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray();
        t["selectedCaptureChannelIndicesZeroBased"] = new JsonArray(captureChannels);
        var telemetry = c["telemetry"]!.AsObject(); telemetry["maximumEventInterval100ns"] = checked((long)Math.Ceiling(candidate.CapturePeriod100ns * 1.5));
        var execution = c["execution"]!.AsObject(); execution["cadenceCalculation"] = $"Capture and render event-start interval greater than 1.5 times the requested {candidate.RequestedDuration100ns / 10_000m:0.###} ms period counts as a cadence miss.";
    }

    private static string ReadCleanCleanup(JsonObject r)
    {
        if (r["watchdogExited"]?.GetValue<bool>() != true || r["watchdogExitCode"]?.GetValue<int>() != 0 || r["watchdogOutcome"]?.GetValue<string>() != "RUNNER_SUCCEEDED" || r["runnerSummary"] is not JsonValue path || !File.Exists(path.GetValue<string>())) return "UNKNOWN";
        var s = JsonNode.Parse(File.ReadAllBytes(path.GetValue<string>()))!.AsObject();
        return s["result"]?.GetValue<string>() == "LOAD_CLEAN" && s["cleanup"]?["confirmed"]?.GetValue<bool>() == true && s["cleanup"]?["forcedTermination"]?.GetValue<bool>() == false && s["profileCreated"]?.GetValue<bool>() == false ? "CONFIRMED" : "UNKNOWN";
    }
    private static bool IsProvenTransportFailure(JsonObject r)
    {
        if (r["runnerSummary"] is not JsonValue path || !File.Exists(path.GetValue<string>())) return false;
        var s = JsonNode.Parse(File.ReadAllBytes(path.GetValue<string>()))!.AsObject();
        var failure = s["failure"]?.GetValue<string>() ?? string.Empty;
        return s["result"]?.GetValue<string>() == "LOAD_UNSTABLE" && failure.StartsWith("LOAD_FAILED:", StringComparison.Ordinal) &&
            new[] { "CALLBACK_DEADLINE", "CPU_LIMIT", "CAPTURE_OR_RING_LOSS", "RENDER_UNDERRUN", "COORDINATOR_FAULT", "PRODUCTION_GRAPH_NO_PROGRESS", "NO_PROGRESS_INTERVAL" }.Any(code => failure == "LOAD_FAILED:" + code);
    }
    private static string ReadAnyCleanup(JsonObject r)
    {
        if (r["runnerSummary"] is not JsonValue path || !File.Exists(path.GetValue<string>())) return "UNKNOWN";
        var s = JsonNode.Parse(File.ReadAllBytes(path.GetValue<string>()))!.AsObject();
        return s["cleanup"]?["confirmed"]?.GetValue<bool>() == true && s["cleanup"]?["forcedTermination"]?.GetValue<bool>() == false ? "CONFIRMED" : "UNKNOWN";
    }
    private static bool WatchdogSucceeded(JsonObject r) => r["watchdogExited"]?.GetValue<bool>() == true && r["watchdogExitCode"]?.GetValue<int>() == 0 && r["watchdogOutcome"]?.GetValue<string>() == "RUNNER_SUCCEEDED";
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static async Task WriteNewVerifiedAsync(string path, byte[] bytes, CancellationToken ct)
    {
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { await stream.WriteAsync(bytes, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false); stream.Flush(true); }
        var readBack = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(readBack))) throw new InvalidDataException("Qualification request read-back SHA mismatch.");
    }
    private static AudioHostCandidateQualificationResult Unknown(Guid? id, string? configSha, string? buildSha, string diagnostic) =>
        new("UNKNOWN", "UNKNOWN", diagnostic, id?.ToString("D"), configSha, null, buildSha, null, null, "TEST_ONLY");
}
