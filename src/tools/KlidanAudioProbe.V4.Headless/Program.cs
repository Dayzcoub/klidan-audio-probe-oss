using System.Security.Cryptography;
using System.Text.Json;
using KlidanAudioProbe.Gui.Core;
using KlidanAudioProbe.Gui.Worker;

const string BloodyCaptureId = "{0.0.1.00000000}.{afe1b68b-8f76-4815-aa2e-1e544322465d}";
const string BloodyRenderId = "{0.0.0.00000000}.{ba4ca4cc-35d7-437a-b53b-99ffedc7e5b8}";
var options = Parse(args);
if (options is null) return 64;
if (options.Mode == "SHORT_SEARCH" && (!options.AuthorizeShortSearch || options.Scenario is not null))
    return Fail("Physical mode requires --authorize-short-search and does not accept test scenarios.", 64);
if (options.Mode == "REPEAT_SELECTED" && (!options.AuthorizeRepeatability || options.AuthorizeShortSearch || options.Scenario is not null || string.IsNullOrWhiteSpace(options.SourceEvidence)))
    return Fail("RepeatSelected requires a separate --authorize-r5 and --source-evidence; it never authorizes or runs ShortSearch.", 64);
if (options.AuthorizeRepeatability && options.Mode != "REPEAT_SELECTED")
    return Fail("--authorize-r5 is valid only for RepeatSelected.", 64);
if (options.Mode == "DRY_RUN" && options.Scenario is not ("BLOODY" or "FAILED" or "UNKNOWN" or "FIRST_CLEAN"))
    return Fail("Dry-run scenario must be BLOODY, FAILED, UNKNOWN, or FIRST_CLEAN.", 64);

var buildSha = Environment.GetEnvironmentVariable("KLIDAN_BUILD_SHA")?.ToLowerInvariant();
if (buildSha is null || buildSha.Length != 40 || buildSha.Any(c => !Uri.IsHexDigit(c)))
    return Fail("A package-verified full KLIDAN_BUILD_SHA is required.", 64);
var workerPath = Path.GetFullPath(options.WorkerPath);
if (!File.Exists(workerPath)) return Fail("The selected Worker executable is missing.", 66);
if (options.Mode == "DRY_RUN" && Path.GetFileName(workerPath) is not ("KlidanProbeWorker.FakeTest.exe" or "probe_worker_format_fixture.exe"))
    return Fail("Dry-run is restricted to the separately packaged test-only Worker binary.", 64);
if (options.Mode is "SHORT_SEARCH" or "REPEAT_SELECTED" && !Path.GetFileName(workerPath).Equals("KlidanProbeWorker.exe", StringComparison.OrdinalIgnoreCase))
    return Fail("Physical operations are restricted to the Release production Worker binary.", 64);

string? sourceEvidenceSha = null;
JsonDocument? sourceSummary = null;
if (options.Mode == "REPEAT_SELECTED")
{
    try { sourceEvidenceSha = VerifySourceEvidence(options.SourceEvidence!, out sourceSummary); }
    catch (Exception ex) { return Fail($"Original evidence verification failed: {ex.Message}", 65); }
}

var root = Path.GetFullPath(options.EvidenceRoot);
Directory.CreateDirectory(root);
var runId = Guid.NewGuid();
var runRoot = Path.Combine(root, $"v4-headless-{runId:N}");
if (Directory.Exists(runRoot)) return Fail("The evidence run directory already exists; refusing overwrite.", 73);
Directory.CreateDirectory(runRoot);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); Console.Error.WriteLine("Cancellation requested; waiting for V4 cleanup."); };

try
{
    await using var worker = new NativeWorkerClient(workerPath);
    await worker.ConnectAsync(cancellation.Token);
    var discovery = await worker.ListWindowsEndpointsAsync(cancellation.Token);
    var captureId = options.Mode == "SHORT_SEARCH" ? BloodyCaptureId : options.Mode == "REPEAT_SELECTED" ? sourceSummary!.RootElement.GetProperty("captureEndpointId").GetString() : options.CaptureId;
    var renderId = options.Mode == "SHORT_SEARCH" ? BloodyRenderId : options.Mode == "REPEAT_SELECTED" ? sourceSummary!.RootElement.GetProperty("renderEndpointId").GetString() : options.RenderId;
    var capture = SelectEndpoint(discovery, captureId, "CAPTURE", options.Mode == "DRY_RUN");
    var render = SelectEndpoint(discovery, renderId, "RENDER", options.Mode == "DRY_RUN");
    if (capture.EndpointId == render.EndpointId) throw new InvalidDataException("Capture and Render must be distinct current endpoints.");

    var route = new WorkerScanV2Route("MONO_TO_STEREO", new(48_000, 2, "PCM16", 0, [0]), new(48_000, 2, "PCM16", 3, [0, 1]));
    var captureMatrix = await worker.QueryFormatMatrixAsync(capture.EndpointId!, "CAPTURE", [2], cancellation.Token);
    var renderMatrix = await worker.QueryFormatMatrixAsync(render.EndpointId!, "RENDER", [2], cancellation.Token);
    var repeats = options.AuthorizeRepeatability ? 5 : 0;
    var plan = options.Mode == "SHORT_SEARCH"
        ? WorkerScanV2PlanPreparation.Prepare(capture.EndpointId!, render.EndpointId!, route, captureMatrix, renderMatrix, worker.DiscoveryGeneration, repeats)
        : options.Mode == "REPEAT_SELECTED"
            ? WorkerScanV2PlanPreparation.PrepareRepeatSelected(capture.EndpointId!, render.EndpointId!, route, captureMatrix, renderMatrix, worker.DiscoveryGeneration, worker.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal))
        : PrepareFakePlan(capture.EndpointId!, render.EndpointId!, route, captureMatrix, renderMatrix, worker.DiscoveryGeneration, repeats);
    if (options.Mode == "REPEAT_SELECTED")
    {
        var original = sourceSummary!.RootElement;
        if (original.GetProperty("provenance").GetString() != "REAL_WASAPI_V4_DUPLEX" || original.GetProperty("selectionState").GetString() != "MINIMUM_SHORT_CLEAN_FOUND" ||
            original.GetProperty("selectedCandidateIndex").GetInt32() != 7 || original.GetProperty("sessionCleanup").GetString() != "CLOSE_CONFIRMED" ||
            original.GetProperty("captureEndpointId").GetString() != capture.EndpointId || original.GetProperty("renderEndpointId").GetString() != render.EndpointId)
            throw new InvalidDataException("Verified source evidence no longer matches its selected real candidate or freshly discovered endpoints.");
    }
    if (plan.Candidates.Count is < 1 or > 8 || plan.MmcssModes.Count != 1 || plan.MmcssModes[0] != "PRO_AUDIO")
        throw new InvalidDataException("The bounded plan does not satisfy the V4 candidate or PRO_AUDIO authorization contract.");
    var summary = WorkerScanV2ContractV4Fingerprint.ConsentSummary(capture.EndpointId!, render.EndpointId!, plan, worker.DiscoveryGeneration);
    Console.WriteLine($"MODE={options.Mode}; ENDPOINTS={capture.EndpointId} / {render.EndpointId}; ROUTE=MONO_TO_STEREO; FORMAT=48k PCM16; MMCSS=PRO_AUDIO; R={repeats}");
    Console.WriteLine(summary);
    var consent = WorkerScanV2ContractV4Fingerprint.CreateConsent(capture.EndpointId!, render.EndpointId!, plan,
        worker.DiscoveryGeneration, true, Guid.NewGuid());
    var prepared = new WorkerScanV2V4PreparedPlan(capture.EndpointId!, render.EndpointId!, plan, consent);
    var progress = new ConsoleProgress();
    var scenario = options.Mode == "DRY_RUN" ? DryRunScenario(options.Scenario!) : null;
    var result = options.Mode == "REPEAT_SELECTED"
        ? await new WorkerV4AutomaticSearchWorkflow(worker).RepeatSelectedAsync(prepared, sourceEvidenceSha!, runRoot, cancellation.Token,
            testScenario: null, testOnlyAllowFake: false)
        : await new WorkerV4AutomaticSearchWorkflow(worker).RunAsync(prepared, runRoot, cancellation.Token,
            testScenario: scenario, runRepeatability: repeats == 5, captureEndpoint: capture, renderEndpoint: render, progress: progress);

    var candidateFiles = new List<object>();
    foreach (var entry in result.Evidence.Candidates)
    {
        var filename = $"candidate-{entry.Candidate.Index:D2}.json";
        var path = Path.Combine(runRoot, filename);
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            buildSha,
            runId,
            candidateIndex = entry.Candidate.Index,
            planFingerprint = result.Evidence.PlanFingerprint,
            routeFingerprint = result.Evidence.RouteFingerprint,
            entry.Candidate,
            entry.CaptureInitializeDisposition,
            entry.RenderInitializeDisposition,
            entry.ActualCaptureFrames,
            entry.ActualCapturePeriod100ns,
            entry.ActualRenderFrames,
            entry.ActualRenderPeriod100ns,
            entry.CaptureCleanup,
            entry.RenderCleanup,
            entry.DuplexDisposition,
            entry.NativeDuplexDisposition,
            entry.EvaluatorReasonCode,
            entry.EvaluatorExpectedFrames,
            entry.EvaluatorActualFrames,
            entry.ReasonCodes,
            entry.SkipReason,
            entry.Provenance,
            captureInitializeEvidence = entry.CaptureInitializeEvidence,
            renderInitializeEvidence = entry.RenderInitializeEvidence,
            duplexEvidence = entry.DuplexEvidence,
            repeatability = result.Evidence.RepeatabilityRuns.Where(r => r.CandidateIndex == entry.Candidate.Index).ToArray()
        }, Options());
        WriteNoOverwriteAndVerify(path, json, "candidateIndex", entry.Candidate.Index.ToString());
        candidateFiles.Add(new { path = filename, sha256 = Hash(json) });
        Console.WriteLine($"CANDIDATE={entry.Candidate.Index}; PERIOD_MS={entry.Candidate.RequestedDuration100ns / 10_000m:0.###}; REQUESTED=C{entry.Candidate.CaptureFrames}/R{entry.Candidate.RenderFrames}; ACTUAL=C{entry.ActualCaptureFrames?.ToString() ?? "UNKNOWN"}/R{entry.ActualRenderFrames?.ToString() ?? "UNKNOWN"}; NATIVE={entry.NativeDuplexDisposition ?? entry.DuplexDisposition}; EVALUATOR={entry.DuplexDisposition}; REASON={string.Join(',', entry.ReasonCodes)}");
    }

    var matrixPath = result.EvidencePath is null ? null : Path.GetRelativePath(runRoot, result.EvidencePath);
    var report = JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        runId,
        buildSha,
        timestampUtc = DateTimeOffset.UtcNow,
        provenance = result.Evidence.Provenance,
        mode = options.Mode,
        captureEndpointId = capture.EndpointId,
        renderEndpointId = render.EndpointId,
        captureName = capture.FriendlyNameDisplay,
        renderName = render.FriendlyNameDisplay,
        route = route.Mode,
        sampleRateHz = 48_000,
        encoding = "PCM16",
        mmcss = "PRO_AUDIO",
        planFingerprint = result.Evidence.PlanFingerprint,
        discoveryGeneration = worker.DiscoveryGeneration,
        candidateCount = result.Evidence.CandidateDomain.Count,
        repeatabilityAuthorized = repeats == 5,
        sourceEvidenceSha256 = sourceEvidenceSha,
        sourceCandidateIndex = sourceEvidenceSha is null ? (int?)null : 7,
        repeatabilityRequested = result.Evidence.RepeatabilityRequested,
        repeatabilityCompleted = result.Evidence.RepeatabilityCompleted,
        selectionState = result.Evidence.SelectionState,
        selectedCandidateIndex = result.Evidence.SelectedCandidateIndex,
        globalMinimumProven = result.Evidence.GlobalMinimumProven,
        sessionCleanup = result.Evidence.SessionCleanup,
        stopReason = result.Evidence.StopReason,
        profileEligible = result.Evidence.ProfileEligible,
        profilePath = result.Evidence.ProfilePath,
        candidateMatrixPath = matrixPath,
        candidateMatrixSha256 = result.EvidencePath is null ? null : HashFile(result.EvidencePath),
        candidateFiles
    }, Options());
    var reportPath = Path.Combine(runRoot, "headless-summary.json");
    WriteNoOverwriteAndVerify(reportPath, report, "runId", runId.ToString());
    Console.WriteLine($"RESULT={result.Evidence.SelectionState}; EVIDENCE={runRoot}; SUMMARY={reportPath}; PROFILE=NOT_CREATED");
    return result.Evidence.SelectionState == "UNKNOWN" ? 3 : result.Evidence.SelectionState is "MINIMUM_NOT_PROVEN" or "REPEATABILITY_FAILED" ? 2 : 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine($"RESULT=UNKNOWN; REASON=CANCELLED_OR_CLEANUP_UNCONFIRMED; EVIDENCE={runRoot}"); return 3; }
catch (Exception ex) { Console.Error.WriteLine($"RESULT=UNKNOWN; REASON={ex.GetType().Name}; MESSAGE={ex.Message}; EVIDENCE={runRoot}"); return 3; }

static WindowsEndpointDto SelectEndpoint(WindowsEndpointDiscoveryDto discovery, string? id, string direction, bool allowDryRunSelection)
{
    var matches = discovery.Endpoints.Where(e => (id is null && allowDryRunSelection || e.EndpointId == id) && e.Direction == direction && e.DeviceState == "ACTIVE")
        .OrderBy(e => e.EndpointId, StringComparer.Ordinal).ToArray();
    if (id is null && allowDryRunSelection) return matches.FirstOrDefault() ?? throw new InvalidDataException($"Fake dry-run has no discovered active {direction} endpoint to bind.");
    return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"Exact {direction} endpoint was not uniquely present and ACTIVE in current Windows discovery.");
}

static WorkerScanV2ContractV4 PrepareFakePlan(string captureId, string renderId, WorkerScanV2Route route,
    RealFormatMatrixDto capture, RealFormatMatrixDto render, ulong generation, int repeats)
{
    static WorkerScanV2PeriodBounds Bounds(RealFormatMatrixDto matrix, string endpoint, string direction)
    {
        if (matrix.Provenance != "TEST_FAKE_FORMAT_BACKEND" || matrix.ReportLevel != "PASSIVE_QUERY" || matrix.EndpointId != endpoint || matrix.Direction != direction)
            throw new InvalidDataException("Dry-run requires exact test-only passive query provenance.");
        var rows = matrix.Candidates.Where(c => c.SampleRate == 48_000 && c.Channels == 2 && c.Encoding == "PCM16").ToArray();
        if (rows.Length != 2 || rows.Any(c => c.Status != "SUPPORTED_REPORTED" || c.SupportStatus != "SUPPORTED_REPORTED" || c.InitializeAttempted || c.InitializeStatus != "NOT_TESTED" || c.QueryCleanupStatus != "RELEASE_ATTEMPTED"))
            throw new InvalidDataException("Fake passive-query bounds are incomplete, unsupported, or include audio Initialize evidence.");
        var minimum = rows.SingleOrDefault(c => c.Period == "minimum")?.MinimumPeriod100ns;
        var @default = rows.SingleOrDefault(c => c.Period == "default")?.DefaultPeriod100ns;
        if (minimum is not > 0 || @default is not > 0 || minimum > @default) throw new InvalidDataException("Fake Worker reported invalid minimum/default bounds.");
        return new(minimum.Value, @default.Value);
    }
    var captureBounds = Bounds(capture, captureId, "CAPTURE");
    var renderBounds = Bounds(render, renderId, "RENDER");
    var candidates = WorkerScanV2CandidatePlanning.GenerateFromBounds(route, captureBounds, renderBounds);
    if (candidates.Count is < 1 or > 8) throw new InvalidDataException("Fake Worker plan is outside the V4 eight-candidate limit.");
    var budget = WorkerScanV2ContractV4Fingerprint.Budget(candidates.Count, repeats);
    return new(route, candidates, ["PRO_AUDIO"], repeats, budget.MaximumOperations, budget.MaximumActiveDuplexSeconds,
        30, budget.SessionLifetimeSeconds, 8, captureBounds, renderBounds);
}

static Func<int, string, string?> DryRunScenario(string scenario) => scenario switch
{
    "BLOODY" => (index, operation) => operation == "DUPLEX" ? index < 2 ? "SERVICE_FAILURE" : index == 2 ? "CLEAN" : null : null,
    "FAILED" => (_, operation) => operation == "DUPLEX" ? "SERVICE_FAILURE" : null,
    "UNKNOWN" => (index, operation) => operation == "DUPLEX" && index == 0 ? "CLEANUP_UNKNOWN" : null,
    "FIRST_CLEAN" => (_, operation) => operation == "DUPLEX" ? "CLEAN" : null,
    _ => throw new ArgumentOutOfRangeException(nameof(scenario))
};

static void WriteNoOverwriteAndVerify(string path, byte[] bytes, string identityProperty, string expectedIdentity)
{
    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
    {
        stream.Write(bytes);
        stream.Flush(true);
    }
    using var readback = JsonDocument.Parse(File.ReadAllBytes(path));
    var actual = readback.RootElement.GetProperty(identityProperty).ToString();
    if (actual != expectedIdentity) throw new InvalidDataException($"Evidence read-back identity mismatch: {identityProperty}.");
}

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static string HashFile(string path) => Hash(File.ReadAllBytes(path));
static string VerifySourceEvidence(string summaryPath, out JsonDocument summary)
{
    var bytes = File.ReadAllBytes(Path.GetFullPath(summaryPath));
    summary = JsonDocument.Parse(bytes);
    var root = summary.RootElement;
    if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("mode").GetString() != "SHORT_SEARCH" ||
        root.GetProperty("buildSha").GetString() != "a22939757964da4a89532085a6c21baf9016ef65" || root.GetProperty("route").GetString() != "MONO_TO_STEREO" ||
        root.GetProperty("sampleRateHz").GetInt32() != 48_000 || root.GetProperty("encoding").GetString() != "PCM16" || root.GetProperty("mmcss").GetString() != "PRO_AUDIO" ||
        root.GetProperty("selectionState").GetString() != "MINIMUM_SHORT_CLEAN_FOUND" || root.GetProperty("selectedCandidateIndex").GetInt32() != 7 ||
        root.GetProperty("repeatabilityAuthorized").GetBoolean() || root.GetProperty("repeatabilityCompleted").GetInt32() != 0 ||
        root.GetProperty("sessionCleanup").GetString() != "CLOSE_CONFIRMED" || root.GetProperty("profileEligible").GetBoolean())
        throw new InvalidDataException("Source summary has an unexpected result, route or cleanup state.");
    var directory = Path.GetDirectoryName(Path.GetFullPath(summaryPath))!;
    foreach (var item in root.GetProperty("candidateFiles").EnumerateArray())
        if (HashFile(SourcePath(directory, item.GetProperty("path").GetString()!)) != item.GetProperty("sha256").GetString()) throw new InvalidDataException("Candidate evidence SHA-256 mismatch.");
    var matrixPath = SourcePath(directory, root.GetProperty("candidateMatrixPath").GetString()!);
    if (HashFile(matrixPath) != root.GetProperty("candidateMatrixSha256").GetString()) throw new InvalidDataException("Candidate matrix SHA-256 mismatch.");
    using var matrix = JsonDocument.Parse(File.ReadAllBytes(matrixPath));
    var selected = matrix.RootElement.GetProperty("candidates").EnumerateArray().Single(e => e.GetProperty("candidate").GetProperty("index").GetInt32() == 7);
    var capInit = selected.GetProperty("captureInitializeEvidence"); var renInit = selected.GetProperty("renderInitializeEvidence"); var duplex = selected.GetProperty("duplexEvidence");
    var report = duplex.GetProperty("report"); var requested = report.GetProperty("requested"); var route = report.GetProperty("route");
    if (duplex.GetProperty("routeFingerprint").GetString() != matrix.RootElement.GetProperty("routeFingerprint").GetString() ||
        selected.GetProperty("candidate").GetProperty("requestedDuration100ns").GetInt64() != 100_000 ||
        selected.GetProperty("candidate").GetProperty("captureFrames").GetInt32() != 480 || selected.GetProperty("candidate").GetProperty("renderFrames").GetInt32() != 480 ||
        selected.GetProperty("duplexDisposition").GetString() != "CLEAN" || selected.GetProperty("provenance").GetString() != "REAL_WASAPI_V4_DUPLEX" ||
        selected.GetProperty("captureCleanup").GetString() != "RELEASE_ATTEMPTED" || selected.GetProperty("renderCleanup").GetString() != "RELEASE_ATTEMPTED" ||
        duplex.GetProperty("cleanup").GetString() != "CAPTURE:RELEASE_ATTEMPTED;RENDER:RELEASE_ATTEMPTED" ||
        capInit.GetProperty("endpointId").GetString() != root.GetProperty("captureEndpointId").GetString() || renInit.GetProperty("endpointId").GetString() != root.GetProperty("renderEndpointId").GetString() ||
        duplex.GetProperty("captureEndpointId").GetString() != root.GetProperty("captureEndpointId").GetString() || duplex.GetProperty("renderEndpointId").GetString() != root.GetProperty("renderEndpointId").GetString() ||
        requested.GetProperty("captureEndpointId").GetString() != root.GetProperty("captureEndpointId").GetString() || requested.GetProperty("renderEndpointId").GetString() != root.GetProperty("renderEndpointId").GetString() ||
        requested.GetProperty("sampleRateHz").GetInt32() != 48_000 || requested.GetProperty("encoding").GetString() != "PCM16" || requested.GetProperty("mmcss").GetString() != "PRO_AUDIO" || requested.GetProperty("durationSeconds").GetInt32() != 5 ||
        route.GetProperty("mode").GetString() != "MONO_TO_STEREO" || route.GetProperty("capture").GetProperty("sampleRateHz").GetInt32() != 48_000 || route.GetProperty("render").GetProperty("sampleRateHz").GetInt32() != 48_000 ||
        route.GetProperty("capture").GetProperty("encoding").GetString() != "PCM16" || route.GetProperty("render").GetProperty("encoding").GetString() != "PCM16")
        throw new InvalidDataException("Selected candidate evidence or per-endpoint cleanup is not confirmed CLEAN.");
    return Hash(bytes);

    static string SourcePath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source evidence contains an unsafe relative path.");
        return path;
    }
}
static int Fail(string message, int code) { Console.Error.WriteLine($"ERROR={message}"); return code; }
static JsonSerializerOptions Options() => new(JsonSerializerDefaults.Web) { WriteIndented = true };

static RunOptions? Parse(string[] args)
{
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var key = args[i];
        if (!key.StartsWith("--", StringComparison.Ordinal)) return null;
        if (key is "--authorize-short-search" or "--authorize-r5") flags.Add(key);
        else if (key is "--mode" or "--worker-path" or "--evidence-root" or "--scenario" or "--capture-id" or "--render-id" or "--source-evidence")
        {
            if (++i >= args.Length || !values.TryAdd(key, args[i])) return null;
        }
        else return null;
    }
        if (!values.TryGetValue("--mode", out var mode) || mode is not ("DRY_RUN" or "SHORT_SEARCH" or "REPEAT_SELECTED") ||
        !values.TryGetValue("--worker-path", out var worker) || string.IsNullOrWhiteSpace(worker) ||
        !values.TryGetValue("--evidence-root", out var evidence) || string.IsNullOrWhiteSpace(evidence)) return null;
    return new(mode, worker!, evidence!, values.GetValueOrDefault("--scenario"), values.GetValueOrDefault("--capture-id"), values.GetValueOrDefault("--render-id"), values.GetValueOrDefault("--source-evidence"), flags.Contains("--authorize-short-search"), flags.Contains("--authorize-r5"));
}

sealed record RunOptions(string Mode, string WorkerPath, string EvidenceRoot, string? Scenario, string? CaptureId, string? RenderId, string? SourceEvidence,
    bool AuthorizeShortSearch, bool AuthorizeRepeatability);

sealed class ConsoleProgress : IProgress<V4AutomaticSearchProgress>
{
    public void Report(V4AutomaticSearchProgress value) => Console.WriteLine($"STAGE={value.Stage}; CANDIDATE={value.CandidateIndex}; REQUESTED=C{value.CaptureFrames}/R{value.RenderFrames}; ACTUAL=C{value.ActualCaptureFrames}/R{value.ActualRenderFrames}; REPEATS={value.RepeatabilityCompleted}");
}
