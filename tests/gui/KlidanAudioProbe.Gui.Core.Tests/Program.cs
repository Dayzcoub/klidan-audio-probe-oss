using KlidanAudioProbe.Gui.Core;
using KlidanAudioProbe.Gui.App;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Localization = KlidanAudioProbe.Gui.Core.Localization;

var testThread = new Thread(RunTests);
testThread.SetApartmentState(ApartmentState.STA);
testThread.Start();
testThread.Join();

void RunTests()
{
var checks = 0;
void Check(bool condition, string name)
{
    checks++;
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}
void CaptureUiScreenshot(Window window, string fileName, double scale = 1.0)
{
    var directory = Environment.GetEnvironmentVariable("KLIDAN_UI_SCREENSHOT_DIR");
    if (string.IsNullOrWhiteSpace(directory)) return;
    Directory.CreateDirectory(directory);
    var width = Math.Max(1, (int)Math.Round(window.ActualWidth * 96 * scale / 96));
    var height = Math.Max(1, (int)Math.Round(window.ActualHeight * 96 * scale / 96));
    var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
    bitmap.Render(window);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(Path.Combine(directory, fileName)); encoder.Save(stream);
    Check(File.Exists(Path.Combine(directory, fileName)) && new FileInfo(Path.Combine(directory, fileName)).Length > 0, $"WPF regression screenshot saved: {fileName} ({width}×{height})");
}
V2AutoShortCandidate AutoCandidate(string period, string status) => new(period, period == "DEFAULT" ? 100000 : 30000, period == "DEFAULT" ? 480 : 144, "SUPPORTED_REPORTED", "SUPPORTED_REPORTED", status, "fixture");
var autoCaptureId = "auto-capture"; var autoRenderId = "auto-render";
var autoRoute = new WorkerScanV2Route("MONO_TO_STEREO", new(48000, 2, "PCM16", 3, [0]), new(48000, 2, "PCM16", 3, [0, 1]));
var autoRouteFingerprint = WorkerScanPlanV2Fingerprint.Route(autoCaptureId, autoRenderId, autoRoute);
RealFormatCandidateDto PassiveCandidate(int channels, string period, long min = 30_000, long @default = 100_000, string support = "SUPPORTED_REPORTED", int rate = 48_000) => new("PCM16", rate, channels, period, "UNKNOWN_NOT_ASSIGNED", support, support, "0x00000000", "0x00000000", "0x00000000", @default, min, "NOT_TESTED", false, null, null, null, null, null, "NOT_ACQUIRED") { QueryCleanupStatus = "RELEASE_ATTEMPTED", QueryClientReleaseAttempts = 1 };
RealFormatMatrixDto PassiveMatrix(string endpoint, string direction, int channels, long min = 30_000, long @default = 100_000, string support = "SUPPORTED_REPORTED", int rate = 48_000, bool omitBounds = false) => new("REAL_WASAPI_QUERY", "2026-11-10T00:00:00Z", endpoint, direction, "PASSIVE_QUERY", omitBounds ? [] : [PassiveCandidate(channels, "minimum", min, @default, support, rate), PassiveCandidate(channels, "default", min, @default, support, rate)]);
var passiveRoute = new WorkerScanV2Route("MONO_TO_STEREO", new(48000, 8, "PCM16", 0, [7]), new(48000, 2, "PCM16", 0, [0, 1]));
var preparedFromPassiveBounds = WorkerScanV2PlanPreparation.Prepare("cap", "ren", passiveRoute, PassiveMatrix("cap", "CAPTURE", 8), PassiveMatrix("ren", "RENDER", 2), 9, 5);
Check(preparedFromPassiveBounds.Candidates.Select(c => c.CaptureFrames).SequenceEqual(new[] { 144, 192, 240, 288, 336, 384, 432, 480 }) && preparedFromPassiveBounds.CaptureBounds.AlignmentFrames is null && preparedFromPassiveBounds.RenderBounds.AlignmentFrames is null && preparedFromPassiveBounds.MmcssModes.SequenceEqual(["PRO_AUDIO"]), "automatic passive preparation uses discovered 3–10 ms bounds, independent geometry, unknown alignment, and PRO_AUDIO");
var flow8Route = new WorkerScanV2Route("MONO_TO_STEREO", new(48000, 1, "PCM16", 0, [0]), new(48000, 2, "PCM16", 0, [0, 1]));
var flow8Prepared = WorkerScanV2PlanPreparation.Prepare("cap", "ren", flow8Route, PassiveMatrix("cap", "CAPTURE", 1), PassiveMatrix("ren", "RENDER", 2), 9, 2);
Check(flow8Prepared.Route.Capture.Channels == 1 && flow8Prepared.Route.Render.Channels == 2 && flow8Prepared.Candidates.First().CaptureFrames == 144 && flow8Prepared.Candidates.First().RenderFrames == 144, "FLOW 8 passive plan preserves mono Capture and stereo Render at their selected exact formats");
var fiveMsMinimum = WorkerScanV2PlanPreparation.Prepare("cap", "ren", passiveRoute, PassiveMatrix("cap", "CAPTURE", 8, min: 50_000), PassiveMatrix("ren", "RENDER", 2, min: 50_000), 10, 5);
var sevenMsMinimum = WorkerScanV2PlanPreparation.Prepare("cap", "ren", passiveRoute, PassiveMatrix("cap", "CAPTURE", 8, min: 70_000), PassiveMatrix("ren", "RENDER", 2, min: 70_000), 11, 5);
Check(fiveMsMinimum.Candidates[0].RequestedDuration100ns == 50_000 && sevenMsMinimum.Candidates[0].RequestedDuration100ns == 70_000 && fiveMsMinimum.Candidates[^1].RequestedDuration100ns == 100_000 && sevenMsMinimum.Candidates[^1].RequestedDuration100ns == 100_000, "V4 search starts at arbitrary reported 5 ms and 7 ms minima and retains each device DEFAULT");
var pointRoute = new WorkerScanV2Route("MONO_TO_STEREO", new(48000, 2, "PCM16", 3, [0]), new(48000, 2, "PCM16", 3, [0, 1]));
var point5 = WorkerScanV2PlanPreparation.Prepare("bloody-cap", "bloody-ren", pointRoute, PassiveMatrix("bloody-cap", "CAPTURE", 2), PassiveMatrix("bloody-ren", "RENDER", 2), 9, 0, 5);
var point10 = WorkerScanV2PlanPreparation.Prepare("bloody-cap", "bloody-ren", pointRoute, PassiveMatrix("bloody-cap", "CAPTURE", 2), PassiveMatrix("bloody-ren", "RENDER", 2), 9, 0, 10);
Check(point5.Candidates.Count == 1 && point5.Candidates[0].CaptureFrames == 240 && point5.Candidates[0].RenderFrames == 240 && point5.MmcssModes.SequenceEqual(["OFF"]) && point5.MaximumOperations == 3 && point5.MaximumActiveDuplexSeconds == 5 && point5.SessionLifetimeSeconds == 139 && point10.Candidates.Count == 1 && point10.Candidates[0].CaptureFrames == 480 && point10.Candidates[0].RenderFrames == 480 && WorkerScanV2ContractV4Fingerprint.Compute("bloody-cap", "bloody-ren", point5, 9) != WorkerScanV2ContractV4Fingerprint.Compute("bloody-cap", "bloody-ren", point10, 9), "V4 point diagnostic remains a distinct OFF-authorized 240-frame 5ms or 480-frame 10ms single candidate");
Throws<InvalidDataException>(() => WorkerScanV2PlanPreparation.Prepare("bloody-cap", "bloody-ren", pointRoute, PassiveMatrix("bloody-cap", "CAPTURE", 2), PassiveMatrix("bloody-ren", "RENDER", 2), 9, 5, 10, "PRO_AUDIO"), "PointDiagnostic retains strict R=0 preparation");
var repeatSourceCandidate = WorkerScanV2CandidatePlanning.Make(4, 50_000, pointRoute, 144, 480, 144, 480);
var repeatPrepared = WorkerScanV2PlanPreparation.PrepareRepeatSelected("cap", "ren", pointRoute, PassiveMatrix("cap", "CAPTURE", 2), PassiveMatrix("ren", "RENDER", 2), 10, repeatSourceCandidate);
Check(repeatPrepared.RepeatSelected && repeatPrepared.RepeatabilityRuns == 5 && repeatPrepared.Candidates is [{ Index: 0, RequestedDuration100ns: 50_000, CaptureFrames: 240, RenderFrames: 240 }] && repeatPrepared.MmcssModes.SequenceEqual(["PRO_AUDIO"]) && repeatPrepared.MaximumOperations == 7 && repeatPrepared.MaximumActiveDuplexSeconds == 25, "RepeatSelected preserves the source-selected 5 ms candidate and derives its R5 budget from fresh passive bounds");
Throws<InvalidDataException>(() => WorkerScanV2PlanPreparation.PrepareRepeatSelected("cap", "ren", pointRoute, PassiveMatrix("cap", "CAPTURE", 2), PassiveMatrix("ren", "RENDER", 2), 10, repeatSourceCandidate with { RequestedDuration100ns = 110_000 }), "RepeatSelected rejects source candidate outside fresh device bounds");
Throws<InvalidDataException>(() => WorkerScanV2PlanPreparation.Prepare("bloody-cap", "bloody-ren", pointRoute, PassiveMatrix("bloody-cap", "CAPTURE", 2, min: 60_000), PassiveMatrix("bloody-ren", "RENDER", 2), 9, 0, 5), "point diagnostic refuses a requested period outside confirmed passive bounds");
Throws<InvalidDataException>(() => WorkerScanV2PlanPreparation.Prepare("bloody-cap", "bloody-ren", pointRoute with { Render = pointRoute.Render with { Encoding = "PCM24_IN_32" } }, PassiveMatrix("bloody-cap", "CAPTURE", 2), PassiveMatrix("bloody-ren", "RENDER", 2), 9, 0, 5), "point diagnostic refuses unreviewed formats");
Throws<InvalidDataException>(() => WorkerScanV2PlanPreparation.Prepare("bloody-cap", "bloody-ren", pointRoute, PassiveMatrix("bloody-cap", "CAPTURE", 2), PassiveMatrix("bloody-ren", "RENDER", 2), 9, 0, 10, "INVALID"), "point diagnostic rejects an unapproved MMCSS mode before consent");
var mixedPrepared = WorkerScanV2PlanPreparation.Prepare("cap", "ren", flow8Route with { Capture = flow8Route.Capture with { SampleRateHz = 44100 } }, PassiveMatrix("cap", "CAPTURE", 1, rate: 44100), PassiveMatrix("ren", "RENDER", 2), 9, 0);
Check(mixedPrepared.Candidates.All(c => c.CaptureFrames == (int)(((decimal)c.RequestedDuration100ns * 44100 + 5_000_000m) / 10_000_000m) && c.RenderFrames == (int)(((decimal)c.RequestedDuration100ns * 48000 + 5_000_000m) / 10_000_000m)), "passive plan independently converts mixed Capture/Render sample rates to frames");
Throws<InvalidDataException>(() => WorkerScanV2PlanPreparation.Prepare("cap", "ren", passiveRoute, PassiveMatrix("wrong", "CAPTURE", 8), PassiveMatrix("ren", "RENDER", 2), 9, 5), "passive preparation blocks mismatched endpoint identity");
Throws<InvalidDataException>(() => WorkerScanV2PlanPreparation.Prepare("cap", "ren", passiveRoute, PassiveMatrix("cap", "CAPTURE", 8, omitBounds: true), PassiveMatrix("ren", "RENDER", 2), 9, 5), "passive preparation blocks missing selected-format period bounds");
Throws<InvalidDataException>(() => WorkerScanV2PlanPreparation.Prepare("cap", "ren", passiveRoute, PassiveMatrix("cap", "CAPTURE", 8, support: "NOT_SUPPORTED"), PassiveMatrix("ren", "RENDER", 2), 9, 5), "passive preparation blocks an unsupported selected format");
Throws<InvalidDataException>(() => WorkerScanV2PlanPreparation.Prepare("cap", "ren", passiveRoute, PassiveMatrix("cap", "CAPTURE", 8, min: 60_000, @default: 70_000), PassiveMatrix("ren", "RENDER", 2, min: 30_000, @default: 50_000), 9, 5), "passive preparation blocks disjoint endpoint period ranges");
var v4Grid = WorkerScanV2CandidatePlanning.For48KhzThreeToTenMs(autoRoute);
var v4Budget = WorkerScanV2ContractV4Fingerprint.Budget(8, 5);
Check(v4Grid.Select(c => c.CaptureFrames).SequenceEqual(new[] { 144, 192, 240, 288, 336, 384, 432, 480 }) && v4Grid.All(c => c.RenderFrames > 0 && c.RequestedDuration100ns == c.CapturePeriod100ns && c.RequestedDuration100ns == c.RenderPeriod100ns), "V4 48 kHz grid has exact frame and period geometry");
var asymmetricRoute = autoRoute with { Capture = autoRoute.Capture with { SampleRateHz = 44100, Channels = 1, ChannelMask = 0 }, Render = autoRoute.Render with { SampleRateHz = 48000 } };
var asymmetricCandidates = WorkerScanV2CandidatePlanning.Generate(asymmetricRoute, 132, 441, 144, 480);
Check(asymmetricCandidates.Count is > 0 and <= 8 && asymmetricCandidates.All(c => c.CaptureFrames == (int)(((decimal)c.RequestedDuration100ns * 44100 + 5_000_000m) / 10_000_000m) && c.RenderFrames == (int)(((decimal)c.RequestedDuration100ns * 48000 + 5_000_000m) / 10_000_000m)) && asymmetricCandidates.Zip(asymmetricCandidates.Skip(1)).All(p => p.First.CapturePeriod100ns < p.Second.CapturePeriod100ns), "V4 computes independently rounded asymmetric 44.1/48 kHz geometry in ascending unique actual periods");
var prioritizedCandidates = WorkerScanV2CandidatePlanning.GenerateFromBounds(autoRoute, new(30_000, 100_000), new(30_000, 100_000));
Check(prioritizedCandidates.Select(c => c.RequestedDuration100ns).SequenceEqual(new long[] { 30_000, 40_000, 50_000, 60_000, 70_000, 80_000, 90_000, 100_000 }), "V4 candidate planning orders all 1 ms buffers from the reported minimum before the default boundary");
var wideDomainCandidates = WorkerScanV2CandidatePlanning.GenerateFromBounds(autoRoute, new(30_000, 200_000), new(30_000, 200_000));
Check(wideDomainCandidates.Count == 8 && wideDomainCandidates[0].RequestedDuration100ns == 30_000 && wideDomainCandidates[^1].RequestedDuration100ns == 200_000 && wideDomainCandidates.Zip(wideDomainCandidates.Skip(1)).All(p => p.First.RequestedDuration100ns < p.Second.RequestedDuration100ns), "V4 wide device domains retain both reported bounds and an ascending bounded search instead of omitting DEFAULT");
Check(v4Budget == new WorkerScanV2Budget(64, 240, 2956) && WorkerScanV2ContractV4Fingerprint.Budget(1, 0) == new WorkerScanV2Budget(3, 5, 139), "V4 budget covers five repeats for every candidate within a bounded session");
using var diagnosticReportFixture = JsonDocument.Parse("{\"report\":{\"duplex\":{\"status\":\"DUPLEX_CLEAN\"},\"capture\":{\"events\":998,\"eventIntervalMedianMilliseconds\":5,\"eventIntervalP95Milliseconds\":5.1,\"eventIntervalP99Milliseconds\":5.2,\"firstEventDelayMilliseconds\":2,\"eventTimeoutCount\":0,\"eventFailureCount\":0,\"framesPerEventMean\":240,\"frameCoveragePercent\":100,\"discontinuities\":0,\"quality\":\"ANOMALIES_PRESENT\",\"longEventIntervals\":1,\"eventIntervalMaximumMilliseconds\":22.5,\"eventIntervalMaximumEndOffsetMilliseconds\":231.4,\"longEventIntervalThresholdMilliseconds\":15},\"render\":{\"events\":500}}}");
var pointDiagnosticEvidence = new V2CandidateMatrixEvidence(1, new string('a', 64), new string('b', 16), "COMPLETED", point5.CaptureBounds, point5.RenderBounds, point5.Candidates,
    [new(point5.Candidates[0], "INITIALIZED", "INITIALIZED", "CLEAN", "RELEASE_ATTEMPTED", "RELEASE_ATTEMPTED", ["DIAGNOSTIC_ONLY"], false, null, "COMPLETE", "NOT_RUN", "TEST_FAKE_WASAPI_V4_DUPLEX")
    { ActualCaptureFrames = 240, ActualCapturePeriod100ns = 50_000, ActualRenderFrames = 240, ActualRenderPeriod100ns = 50_000, DuplexEvidence = diagnosticReportFixture.RootElement.Clone(), NativeDuplexDisposition = "CLEAN", EvaluatorReasonCode = "CLEAN" }],
    "DIAGNOSTIC_ONLY", "TEST_FAKE_WASAPI_V4_DUPLEX") { EvidenceVersion = 4, DiagnosticOnly = true, BuildSha = new string('c', 40), DiagnosticTimestampUtc = DateTimeOffset.UtcNow, DiscoveryGeneration = 9, SelectedCandidateIndex = 0, GlobalMinimumProven = false, UsedOperations = 3, MaximumOperations = 3, UsedActiveDuplexSeconds = 5, MaximumActiveDuplexSeconds = 5, StopReason = "DIAGNOSTIC_CANDIDATE_COMPLETED", RepeatabilityPending = false, RepeatabilityRequested = 0, RepeatabilityCompleted = 0, SessionCleanup = "CLOSE_CONFIRMED" };
var pointDiagnosticJson = V2CandidateMatrixEvidenceJson.SerializeRoundTrip(pointDiagnosticEvidence);
Check(pointDiagnosticJson.Contains("DIAGNOSTIC_ONLY", StringComparison.Ordinal) && pointDiagnosticJson.Contains(new string('c', 40), StringComparison.Ordinal) && !pointDiagnosticEvidence.GlobalMinimumProven && !pointDiagnosticEvidence.ProfileEligible, "diagnostic-only evidence round-trips one exact candidate, build SHA, timestamp, and closed profile/minimum gates");
Check(pointDiagnosticJson.Contains("eventIntervalMaximumMilliseconds", StringComparison.Ordinal) && pointDiagnosticJson.Contains("eventIntervalMaximumEndOffsetMilliseconds", StringComparison.Ordinal) && pointDiagnosticJson.Contains("longEventIntervals", StringComparison.Ordinal), "diagnostic evidence read-back retains per-side event outlier details for the GUI summary");
Throws<InvalidDataException>(() => V2CandidateMatrixEvidenceJson.Validate(pointDiagnosticEvidence with { ProfileEligible = true }), "point diagnostic evidence cannot be promoted to any profile");
var v4Bounds = new WorkerScanV2PeriodBounds(30_000, 100_000);
var v4Plan = new WorkerScanV2ContractV4(autoRoute, v4Grid, ["OFF"], 5, 64, 240, 30, 2956, 8, v4Bounds, v4Bounds);
var v4Canonical = WorkerScanV2ContractV4Fingerprint.Canonicalize("capture-кириллица", "render-音声", v4Plan, 7);
var v4ExpectedCanonical = "SCAN_PLAN_V2|4|7|capture-кириллица|render-音声|d2e58c0f94fa7718|5|64|240|30|2956|8|\nB|30000|100000|ALIGNMENT_UNKNOWN|30000|100000|ALIGNMENT_UNKNOWN\n" + string.Join("\n", v4Grid.Select(c => $"C|{c.Index}|{c.RequestedDuration100ns}|{c.CaptureFrames}|{c.RenderFrames}|{c.CapturePeriod100ns}|{c.RenderPeriod100ns}|{c.CaptureAlignmentFrames}|{c.CaptureAlignmentKnown.ToString().ToLowerInvariant()}|{c.RenderAlignmentFrames}|{c.RenderAlignmentKnown.ToString().ToLowerInvariant()}|{c.CaptureMinimumFrames}|{c.CaptureMaximumFrames}|{c.RenderMinimumFrames}|{c.RenderMaximumFrames}|{c.RejectionReason}")) + "\nM|OFF\n";
Check(v4Canonical == v4ExpectedCanonical && WorkerScanV2ContractV4Fingerprint.Compute("capture-кириллица", "render-音声", v4Plan, 7).Length == 64, "C# and C++ v4 Unicode canonical serialization matches the shared full 8-candidate byte vector and discriminator");
var proAudioPlan = v4Plan with { MmcssModes = ["PRO_AUDIO"] };
Check(WorkerScanV2ContractV4Fingerprint.Canonicalize("capture-кириллица", "render-音声", proAudioPlan, 7).EndsWith("M|PRO_AUDIO\n", StringComparison.Ordinal) && WorkerScanV2ContractV4Fingerprint.Compute("capture-кириллица", "render-音声", proAudioPlan, 7) != WorkerScanV2ContractV4Fingerprint.Compute("capture-кириллица", "render-音声", v4Plan, 7), "V4 consent fingerprint binds PRO_AUDIO distinctly from OFF");
var v4Consent = WorkerScanV2ContractV4Fingerprint.ConsentSummary("capture-кириллица", "render-音声", v4Plan, 7);
Check(v4Consent.Contains("Capture capture-кириллица", StringComparison.Ordinal) && v4Consent.Contains("Render render-音声", StringComparison.Ordinal) && v4Consent.Contains("48000 Hz", StringComparison.Ordinal) && v4Consent.Contains("8 candidates", StringComparison.Ordinal) && v4Consent.Contains("MMCSS=OFF", StringComparison.Ordinal) && v4Consent.Contains("repeatability=5", StringComparison.Ordinal) && v4Consent.Contains("max audio operations=64", StringComparison.Ordinal) && v4Consent.Contains("active Duplex=240s", StringComparison.Ordinal) && v4Consent.Contains("session=2956s", StringComparison.Ordinal) && v4Consent.Contains("AudioHost load qualification", StringComparison.Ordinal), "V4 consent summary displays exact scope, MMCSS mode, budgets, and the separate AudioHost-load profile gate");
Throws<ArgumentException>(() => WorkerScanV2ContractV4Fingerprint.Canonicalize("capture", "render", v4Plan with { MaximumOperations = 28 }, 7), "V4 rejects mutated operation budget");
Throws<ArgumentException>(() => WorkerScanV2ContractV4Fingerprint.Canonicalize("capture", "render", v4Plan with { Candidates = v4Grid.Reverse().ToArray() }, 7), "V4 rejects reordered candidates");
V2CandidateMatrixEntry MatrixRow(WorkerScanV2Candidate c) => new(c, "NOT_TESTED", "NOT_TESTED", "NOT_TESTED", "NOT_ATTEMPTED", "NOT_ATTEMPTED", ["B1_PLAN_ONLY"], true, "V4_AUDIO_EXECUTION_NOT_ENABLED", "INCOMPLETE", "NOT_RUN", "PLAN_ONLY");
var matrixRows = v4Grid.Select(MatrixRow).ToArray();
var matrixEvidence = new V2CandidateMatrixEvidence(1, WorkerScanV2ContractV4Fingerprint.Compute("capture-кириллица", "render-音声", v4Plan, 7), WorkerScanPlanV2Fingerprint.Route("capture-кириллица", "render-音声", autoRoute), "PLANNED", v4Bounds, v4Bounds, v4Grid, matrixRows, "MINIMUM_NOT_PROVEN", "PLAN_ONLY");
var displayCandidate = v4Grid[0];
var displayRow = new V2CandidateMatrixEntry(displayCandidate, "INITIALIZED", "INITIALIZED", "CLEAN", "RELEASE_ATTEMPTED", "RELEASE_ATTEMPTED", [], false, null, "COMPLETE", "CONFIRMED", "TEST_FAKE_WASAPI_V4_DUPLEX") { ActualCaptureFrames = displayCandidate.CaptureFrames, ActualCapturePeriod100ns = displayCandidate.CapturePeriod100ns, ActualRenderFrames = displayCandidate.RenderFrames, ActualRenderPeriod100ns = displayCandidate.RenderPeriod100ns };
var displayEvidence = matrixEvidence with { Lifecycle = "COMPLETED", CandidateDomain = [displayCandidate], Candidates = [displayRow], SelectionState = "REPEATABILITY_CONFIRMED", Provenance = "TEST_FAKE_WASAPI_V4_DUPLEX", EvidenceVersion = 3, DiscoveryGeneration = 7, SelectedCandidateIndex = 0, RepeatabilityRequested = 5, RepeatabilityCompleted = 5, SessionCleanup = "CLOSE_CONFIRMED" };
var displayVm = new MainViewModel();
typeof(MainViewModel).GetField("_v4SearchResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(displayVm, new V4AutomaticSearchResult(displayEvidence, "evidence.json") { QualificationStatus = "LOAD_CLEAN", ProfilePath = "profile-v5.json" });
typeof(MainViewModel).GetField("_v4EvidencePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(displayVm, "evidence.json");
typeof(MainViewModel).GetField("_v4ProgressText", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(displayVm, "LOAD_CLEAN");
Check(displayVm.V4ProgressText == "LOAD_CLEAN" && displayVm.V4ResultSummary.Contains("LOAD_CLEAN", StringComparison.Ordinal) && displayVm.V4ResultSummary.Contains("profile-v5.json", StringComparison.Ordinal) && displayVm.V4ResultSummary.Contains("evidence.json", StringComparison.Ordinal) && displayVm.V4ResultSummary.Contains("3 ms", StringComparison.Ordinal), $"GUI preserves final qualified status, selected period, evidence path and saved Profile v5 path [{displayVm.V4ResultSummary}]");
typeof(MainViewModel).GetField("_v4SearchResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(displayVm, new V4AutomaticSearchResult(displayEvidence, "evidence.json") { QualificationStatus = "QA_FAILED", QualificationDiagnostic = "IPC response missing" });
typeof(MainViewModel).GetField("_v4ProgressText", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(displayVm, "QA_FAILED: IPC response missing");
var failedSummary = displayVm.V4ResultSummary;
Check(displayVm.V4ProgressText == "QA_FAILED: IPC response missing" && failedSummary.Contains("AudioHost: QA_FAILED", StringComparison.Ordinal) && failedSummary.Contains("IPC response missing", StringComparison.Ordinal) && failedSummary.Contains("TEST_ONLY; no DEVICE profile", StringComparison.Ordinal), $"GUI retains the final AudioHost failure reason and does not display a profile for a failed qualification [{failedSummary.Replace(Environment.NewLine, " | ")}]");
Check(V2CandidateMatrixEvidenceJson.SerializeRoundTrip(matrixEvidence).Contains("V4_AUDIO_EXECUTION_NOT_ENABLED", StringComparison.Ordinal), "V4 candidate matrix schema model validates and round-trips plan-only NOT_TESTED evidence");
Throws<InvalidDataException>(() => V2CandidateMatrixEvidenceJson.Validate(matrixEvidence with { Candidates = [matrixRows[0] with { DuplexNotRun = false }, .. matrixRows.Skip(1)] }), "V4 evidence rejects contradictory NOT_TESTED duplex state");
JsonElement AutoShortReport(string period, string status = "DUPLEX_CLEAN", int captureWaits = 0, int renderWaits = 0, int serviceFaults = 0, bool watchdog = false, string cleanup = "RELEASE_ATTEMPTED")
{
    var operationId = Guid.NewGuid().ToString("D");
    object Side(string endpoint, string direction, int waits) => new { endpointId = endpoint, direction, quality = status == "DUPLEX_CLEAN" && serviceFaults == 0 ? "CLEAN" : "ANOMALIES_PRESENT", cleanup, startSucceeded = true, stopSucceeded = true, durationReached = true, framesProcessed = 240000, expectedFrames = 240000, actualBufferFrames = 480, frameCoveragePercent = 99.8, discontinuities = 0, timestampErrors = 0, serviceFaults, intermediateWaitTimeouts = waits, packetDrainLimitHits = 0, longEventIntervals = 0, deviceInvalidated = false, watchdogTimedOut = watchdog, cancelled = false };
    var report = new { schemaVersion = 3, reportType = "wasapi-duplex-validation-v2", reportId = Guid.NewGuid().ToString("D"), operationId, provenance = "TEST_FAKE_WASAPI_DUPLEX_V2",
        requested = new { captureEndpointId = autoCaptureId, renderEndpointId = autoRenderId, period, durationSeconds = 5, mmcss = "OFF", sampleRateHz = 48000, channels = 2, encoding = "PCM16" },
        route = new { fingerprint = autoRouteFingerprint, mode = autoRoute.Mode,
            capture = new { endpointId = autoCaptureId, sampleRateHz = 48000, encoding = "PCM16", channels = 2, channelMask = 3, selectedIndicesZeroBased = new[] { 0 } },
            render = new { endpointId = autoRenderId, sampleRateHz = 48000, encoding = "PCM16", channels = 2, channelMask = 3, selectedIndicesZeroBased = new[] { 0, 1 } } },
        capture = Side(autoCaptureId, "CAPTURE", captureWaits), render = Side(autoRenderId, "RENDER", renderWaits), duplex = new { status } };
    return JsonDocument.Parse(JsonSerializer.Serialize(report)).RootElement.Clone();
}
V2AutoShortCandidate Classified(string period, JsonElement report) => V2AutoShortEvaluator.ClassifyReport(period, period == "DEFAULT" ? 100000 : 30000, period == "DEFAULT" ? 480 : 144, report.GetProperty("operationId").GetString()!, autoCaptureId, autoRenderId, autoRoute, autoRouteFingerprint, report);
var waitCaptureClean = Classified("DEFAULT", AutoShortReport("DEFAULT", captureWaits: 1));
var waitRenderClean = Classified("DEFAULT", AutoShortReport("DEFAULT", renderWaits: 4));
Check(waitCaptureClean.Status == "SHORT_CLEAN" && waitRenderClean.Status == "SHORT_CLEAN", "production V2 report classifier permits benign intermediate waits when Native Duplex remains CLEAN");
var defaultCleanCandidate = Classified("DEFAULT", AutoShortReport("DEFAULT", captureWaits: 1));
var minimumFailedCandidate = Classified("MINIMUM", AutoShortReport("MINIMUM", "DUPLEX_WARNINGS"));
Check(V2AutoShortEvaluator.Evaluate([defaultCleanCandidate, minimumFailedCandidate]).RecommendedPeriod == "DEFAULT", "schema-backed DEFAULT CLEAN with benign wait followed by MINIMUM FAILED recommends DEFAULT");
var serviceFailureCandidate = Classified("DEFAULT", AutoShortReport("DEFAULT", serviceFaults: 1));
var watchdogCandidate = Classified("DEFAULT", AutoShortReport("DEFAULT", watchdog: true));
var cleanupUnknownCandidate = Classified("DEFAULT", AutoShortReport("DEFAULT", cleanup: "UNKNOWN"));
Check(serviceFailureCandidate.Status != "SHORT_CLEAN" && serviceFailureCandidate.Reason.Contains("SERVICEFAULTS", StringComparison.Ordinal) && watchdogCandidate.Status != "SHORT_CLEAN" && watchdogCandidate.Reason.Contains("WATCHDOG", StringComparison.Ordinal) && cleanupUnknownCandidate.Status == "UNKNOWN" && cleanupUnknownCandidate.Reason.Contains("CLEANUP_UNCONFIRMED", StringComparison.Ordinal), "V2 evaluator retains service-fault, watchdog and cleanup reasons without upgrading unsafe reports to SHORT_CLEAN");
var autoAssessments = new[]
{
    ("CLEAN_FAILED", new[] { AutoCandidate("DEFAULT", "SHORT_CLEAN"), AutoCandidate("MINIMUM", "SHORT_FAILED") }, "DEFAULT"),
    ("FAILED_CLEAN", new[] { AutoCandidate("DEFAULT", "SHORT_FAILED"), AutoCandidate("MINIMUM", "SHORT_CLEAN") }, "MINIMUM"),
    ("CLEAN_CLEAN", new[] { AutoCandidate("DEFAULT", "SHORT_CLEAN"), AutoCandidate("MINIMUM", "SHORT_CLEAN") }, "MINIMUM"),
    ("FAILED_FAILED", new[] { AutoCandidate("DEFAULT", "SHORT_FAILED"), AutoCandidate("MINIMUM", "SHORT_FAILED") }, null),
    ("UNKNOWN_NONE", new[] { AutoCandidate("DEFAULT", "UNKNOWN") }, null),
    ("CLEAN_UNKNOWN", new[] { AutoCandidate("DEFAULT", "SHORT_CLEAN"), AutoCandidate("MINIMUM", "UNKNOWN") }, null),
    ("UNSUPPORTED_CLEAN", new[] { AutoCandidate("DEFAULT", "UNSUPPORTED"), AutoCandidate("MINIMUM", "SHORT_CLEAN") }, "MINIMUM"),
    ("CLEAN_UNSUPPORTED", new[] { AutoCandidate("DEFAULT", "SHORT_CLEAN"), AutoCandidate("MINIMUM", "UNSUPPORTED") }, "DEFAULT")
};
foreach (var (name, candidates, expectedPeriod) in autoAssessments)
{
    var evaluated = V2AutoShortEvaluator.Evaluate(candidates);
    Check(evaluated.RecommendedPeriod == expectedPeriod && evaluated.Qualification == "NOT_CERTIFIED" && (name != "CLEAN_UNKNOWN" || evaluated.Candidates.Any(c => c.Status == "SHORT_CLEAN")), $"AUTO_SHORT evaluator {name} preserves bounded recommendation and uncertainty semantics");
}
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, name); return; }
    throw new InvalidOperationException($"FAIL: {name}");
}
const string baselineLanguage = "en-US";
KlidanAudioProbe.Gui.Core.Localization.SetLanguage("en-US", false);
Check(KlidanAudioProbe.Gui.Core.Localization.Get("AppTitle").Contains("Windows diagnostics", StringComparison.Ordinal) && KlidanAudioProbe.Gui.Core.Localization.Get("Language").Length > 0, "English localization resources are complete for the public shell");
Check(MainViewModel.ModeLabel(SessionMode.Capture) == KlidanAudioProbe.Gui.Core.Localization.Get("VmCaptureInputDirection") && MainViewModel.ModeLabel(SessionMode.Render) == KlidanAudioProbe.Gui.Core.Localization.Get("VmRenderOutputDirection"), "public mode labels follow en-US without changing SessionMode values");
KlidanAudioProbe.Gui.Core.Localization.SetLanguage("ru-RU", false);
Check(KlidanAudioProbe.Gui.Core.Localization.Get("AppTitle").Contains("диагностика", StringComparison.Ordinal) && KlidanAudioProbe.Gui.Core.Localization.Get("Language") == "Язык", "Russian localization resources are complete for the public shell");
Check(MainViewModel.ModeLabel(SessionMode.Capture) == KlidanAudioProbe.Gui.Core.Localization.Get("VmCaptureInputDirection") && MainViewModel.ModeLabel(SessionMode.Render) == KlidanAudioProbe.Gui.Core.Localization.Get("VmRenderOutputDirection"), "public mode labels follow ru-RU without changing SessionMode values");
var eventConverterCases = new (ProbeEventKind Value, string Resource)[]
{
    (ProbeEventKind.SessionRequested, "UiEventSessionRequested"), (ProbeEventKind.ConsentAccepted, "UiEventConsentAccepted"),
    (ProbeEventKind.ConsentCancelled, "UiEventConsentCancelled"), (ProbeEventKind.Preparing, "VmInspectorLifecyclePreparing"),
    (ProbeEventKind.Ready, "UiEventReady"), (ProbeEventKind.Started, "UiEventStarted"),
    (ProbeEventKind.MeterSnapshot, "UiEventMeterSnapshot"), (ProbeEventKind.Stopping, "VmInspectorLifecycleStopping"),
    (ProbeEventKind.Stopped, "UiEventStopped"), (ProbeEventKind.Completed, "UiEventCompleted"),
    (ProbeEventKind.Warning, "UiEventWarning"), (ProbeEventKind.Failure, "UiEventFailure"),
    (ProbeEventKind.DeviceLoss, "UiEventDeviceLoss"), (ProbeEventKind.WorkerDisconnected, "UiEventWorkerDisconnected")
};
var sessionStateConverterCases = new (object Value, string Resource)[]
{
    (SessionMode.Capture, "InputCapture"), (SessionMode.Render, "OutputRender"),
    (SessionLifecycle.Preparing, "VmInspectorLifecyclePreparing"), (SessionLifecycle.Ready, "VmInspectorLifecycleReady"),
    (SessionLifecycle.Started, "UiSessionLifecycleStartedDemo"), (SessionLifecycle.Stopping, "UiSessionLifecycleStoppingDemo"),
    (SessionLifecycle.Stopped, "UiSessionLifecycleStoppedDemo"), (SessionLifecycle.Completed, "UiSessionLifecycleCompletedDemo"),
    (SessionLifecycle.Failed, "UiSessionLifecycleFailedDemo"), (SessionLifecycle.Unknown, "VmInspectorUnknownUpper"),
    (StreamQuality.Clean, "VmInspectorQualityClean"), (StreamQuality.Warning, "VmInspectorQualityWarning"),
    (StreamQuality.Failed, "VmInspectorQualityFailed"), (StreamQuality.Unknown, "VmInspectorUnknownUpper")
};
var eventConverter = new EventKindConverter();
var sessionStateConverter = new SessionStateConverter();
var scanSummaryOperations = new[]
{
    new ScanOperationResult(new(ScanStage.Query), ScanOperationLifecycle.Completed, "", "NOT_APPLICABLE", null),
    new ScanOperationResult(new(ScanStage.Stream), ScanOperationLifecycle.Skipped, "", "NOT_ATTEMPTED", null),
    new ScanOperationResult(new(ScanStage.Duplex), ScanOperationLifecycle.Unknown, "", "UNKNOWN", null)
};
const string scanSummarySource = "Completed · завершено 1 · пропущено 1 · ошибки 1. Physical RTL=NOT_MEASURED · AudioHost=NOT_CERTIFIED.";
var recommendationFixture = DeviceConfigurationEvaluator.Recommend([]);
var scanSummaryFixture = new ScanRunResult("localization-fixture", ScanRunLifecycle.Completed, new(null, null), scanSummaryOperations, [], [], recommendationFixture, scanSummarySource, false, "NOT_CERTIFIED");
var summaryVm = new MainViewModel();
typeof(MainViewModel).GetField("_automaticScanResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(summaryVm, scanSummaryFixture);
var repeatabilityCountsFixture = new DuplexRepeatabilityCounts(2, 2, 1, 0, 0, 1);
const string repeatabilitySummarySource = "План: 2; attempted: 2; completed: 1; failed: 0; unknown: 0; skipped: 1. MINIMUM: REPEATABLY_CLEAN; DEFAULT: INSUFFICIENT_EVIDENCE";
var repeatabilitySummaryFixture = new DuplexRepeatabilityResult("localization-fixture", ScanRunLifecycle.Completed, new(null, null), [],
    [new("MINIMUM", DuplexRepeatabilityClassification.REPEATABLY_CLEAN, 3, ["DUPLEX_CLEAN"]), new("DEFAULT", DuplexRepeatabilityClassification.INSUFFICIENT_EVIDENCE, 0, ["NOT_RUN"])],
    repeatabilitySummarySource, "TEST_FAKE", Counts: repeatabilityCountsFixture);
typeof(MainViewModel).GetField("_duplexRepeatabilityResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(summaryVm, repeatabilitySummaryFixture);
foreach (var language in new[] { "en-US", "ru-RU" })
{
    KlidanAudioProbe.Gui.Core.Localization.SetLanguage(language, false);
    var converterCulture = System.Globalization.CultureInfo.GetCultureInfo(language);
    var demoFixtures = new FakeProbeClient();
    var demoCapture = demoFixtures.ListEndpoints(SessionMode.Capture)[0];
    var demoDisabled = demoFixtures.ListEndpoints(SessionMode.Capture)[1];
    var demoSession = new SessionDto(Guid.Empty, SessionMode.Capture, demoCapture.Name, null, 1, SessionLifecycle.Preparing, StreamQuality.Unknown, "fixture", null, ChannelCount: demoCapture.Channels);
    Check(demoCapture.DisplayName == string.Format(converterCulture, KlidanAudioProbe.Gui.Core.Localization.Get("VmDemoEndpointDisplayName"), "Capture", 2) && demoCapture.DisplayState == KlidanAudioProbe.Gui.Core.Localization.Get("VmDemoAvailable") && demoCapture.DisplayFormat == string.Format(converterCulture, "48 {0} · PCM16", KlidanAudioProbe.Gui.Core.Localization.Get("VmDemoKilohertz")) && demoDisabled.DisplayState == KlidanAudioProbe.Gui.Core.Localization.Get("VmDemoDisabled") && demoDisabled.DisplayFormat == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorUnknown"), $"local demo device display labels follow {language}");
    var serializedDemo = JsonSerializer.Serialize(demoCapture);
    var serializedSession = JsonSerializer.Serialize(demoSession);
    Check(demoCapture.Name == "ДЕМО · Данные симуляции · Capture · 2 канала" && demoCapture.State == "Демо · доступно" && demoCapture.Format == "48 кГц · PCM16" && demoSession.EndpointName == demoCapture.Name && demoSession.DisplayEndpointName == demoCapture.DisplayName && !serializedDemo.Contains("DisplayName", StringComparison.Ordinal) && !serializedDemo.Contains("DisplayState", StringComparison.Ordinal) && !serializedDemo.Contains("DisplayFormat", StringComparison.Ordinal) && !serializedSession.Contains("DisplayEndpointName", StringComparison.Ordinal), $"demo display localization preserves raw data and JSON shape in {language}");
    var periodEvidence = new PeriodEvidence(FormatStatus.NotTested, null, null, null);
    var expectedUnknown = KlidanAudioProbe.Gui.Core.Localization.Get("VmValueUnknown");
    var expectedNotObserved = KlidanAudioProbe.Gui.Core.Localization.Get("VmValueNotObserved");
    Check(periodEvidence.DisplayAccessibleText == string.Format(converterCulture, KlidanAudioProbe.Gui.Core.Localization.Get("VmPeriodEvidenceAccessibleText"), periodEvidence.StatusCode, expectedUnknown, expectedNotObserved, expectedUnknown) && periodEvidence.AccessibleText.Contains("не наблюдался", StringComparison.Ordinal), $"period evidence accessibility text localizes without altering its source value for {language}");
    var periodEvidenceJson = JsonSerializer.Serialize(periodEvidence);
    Check(periodEvidenceJson.Contains("AccessibleText", StringComparison.Ordinal) && !periodEvidenceJson.Contains("DisplayAccessibleText", StringComparison.Ordinal), $"period evidence JSON shape excludes localized UI projection for {language}");
    var disconnectedWorker = new GuiStateWorkerClient([]);
    var disconnectedWorkerVm = new MainViewModel(worker: disconnectedWorker);
    disconnectedWorker.SetState(new(WorkerConnectionState.Disconnected, "Worker отключён."));
    Check(disconnectedWorkerVm.WorkerState.Message == "Worker отключён." && disconnectedWorkerVm.WorkerStateDisplayMessage == KlidanAudioProbe.Gui.Core.Localization.Get("VmWorkerDisconnectedMessage") && disconnectedWorkerVm.DuplexWorkerAvailability.EndsWith(KlidanAudioProbe.Gui.Core.Localization.Get("VmWorkerDisconnectedMessage"), StringComparison.Ordinal), $"default disconnected worker message localizes only in UI for {language}");
    disconnectedWorker.SetState(new(WorkerConnectionState.Disconnected, "pipe detail"));
    Check(disconnectedWorkerVm.WorkerStateDisplayMessage == "pipe detail", $"worker diagnostic detail remains unchanged for {language}");
    foreach (var (value, resource) in eventConverterCases)
        Check((string)eventConverter.Convert(value, typeof(string), null!, converterCulture) == KlidanAudioProbe.Gui.Core.Localization.Get(resource), $"EventKindConverter localizes {value} in {language}");
    foreach (var (value, resource) in sessionStateConverterCases)
        Check((string)sessionStateConverter.Convert(value, typeof(string), null!, converterCulture) == KlidanAudioProbe.Gui.Core.Localization.Get(resource), $"SessionStateConverter localizes {value} in {language}");
    Check((string)eventConverter.Convert(null!, typeof(string), null!, converterCulture) == KlidanAudioProbe.Gui.Core.Localization.Get("UiEventUnknown") && (string)eventConverter.Convert((ProbeEventKind)999, typeof(string), null!, converterCulture) == string.Format(converterCulture, KlidanAudioProbe.Gui.Core.Localization.Get("UiEventUnknownWithValue"), (ProbeEventKind)999), $"EventKindConverter localizes missing and unknown values in {language}");
    Check((string)sessionStateConverter.Convert(null!, typeof(string), null!, converterCulture) == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorUnknownUpper") && (string)sessionStateConverter.Convert((SessionLifecycle)999, typeof(string), null!, converterCulture) == string.Format(converterCulture, KlidanAudioProbe.Gui.Core.Localization.Get("UiSessionUnknownWithValue"), (SessionLifecycle)999), $"SessionStateConverter localizes missing and unknown values in {language}");
    Check(summaryVm.AutomaticScanResultSummary == string.Format(converterCulture, KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticScanSummaryDisplay"), KlidanAudioProbe.Gui.Core.Localization.Get("VmScanLifecycleCompleted"), 1, 1, 1) && scanSummaryFixture.Summary == scanSummarySource, $"automatic scan summary localizes its display without changing source summary in {language}");
    Check(summaryVm.AutomaticScanRecommendationText.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmRecommendationReasonNoCleanCandidate"), StringComparison.Ordinal), $"recommendation explanation localizes in {language}");
    var expectedRepeatabilitySummary = string.Format(converterCulture, KlidanAudioProbe.Gui.Core.Localization.Get("VmRepeatabilitySummaryDisplay"), 2, 2, 1, 0, 0, 1,
        $"MINIMUM: {KlidanAudioProbe.Gui.Core.Localization.Get("VmRepeatabilityClassClean")}; DEFAULT: {KlidanAudioProbe.Gui.Core.Localization.Get("VmRepeatabilityClassInsufficient")}");
    Check(summaryVm.DuplexRepeatabilityResultText.StartsWith(expectedRepeatabilitySummary, StringComparison.Ordinal) && repeatabilitySummaryFixture.Summary == repeatabilitySummarySource, $"repeatability summary localizes its display without changing source summary in {language}");
}
KlidanAudioProbe.Gui.Core.Localization.SetLanguage(baselineLanguage, false);
var lockedDeveloperVm = new MainViewModel(developerToolsAvailable: false);
Check(!lockedDeveloperVm.DeveloperToolsAvailable && !lockedDeveloperVm.OpenDeveloperToolsCommand.CanExecute(null) && !lockedDeveloperVm.NavigateCommand.CanExecute("Diagnostics"), "Developer Tools are unavailable without the explicit startup flag");
typeof(MainViewModel).GetMethod("SetDeviceScanProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(lockedDeveloperVm, [DeviceScanStage.Preflight, "fixture"]);
Check(lockedDeveloperVm.DeviceScanProgress.StartsWith(KlidanAudioProbe.Gui.Core.Localization.Get("VmDeviceScanStagePreflight"), StringComparison.Ordinal), "device scan progress localizes its stage without altering the DeviceScanStage enum");
WindowsEndpointDto GroupEndpoint(string id, string direction, string name, string? container, string? formFactor = null, string state = "ACTIVE")
{
    var available = new WindowsPropertyEvidenceDto(0, "AVAILABLE");
    var containerEvidence = container is null ? new WindowsPropertyEvidenceDto(unchecked((int)0x80070490), "UNAVAILABLE") : available;
    return new(id, available, name, available, direction, state, 1, available, formFactor ?? "MICROPHONE", available,
        new(new(unchecked((int)0x80070490), "UNAVAILABLE"), "UNKNOWN", null, null, null, null, null, null, "UNKNOWN", null, "UNKNOWN"), container, containerEvidence);
}
var grouped = PhysicalDeviceGrouping.Group([
    GroupEndpoint("capture-a", "CAPTURE", "Audient iD4", "{DEVICE-A}", "MICROPHONE"),
    GroupEndpoint("render-a", "RENDER", "Audient iD4", "{DEVICE-A}", "LINE_LEVEL"),
    GroupEndpoint("capture-loop", "CAPTURE", "Loop-back 1/2 (2- Audient iD4)", "{DEVICE-A}", "MICROPHONE"),
    GroupEndpoint("render-b", "RENDER", "Speakers", "{DEVICE-B}")]);
var audient = grouped.Single(g => g.GroupId == "container:{DEVICE-A}");
Check(grouped.Count == 2 && audient.Verification == PhysicalIdentityStatus.Confirmed, "container identity groups endpoints and confirms the physical device");
Check(audient.IsLoopbackCapture && audient.CanAutoSelectPair && audient.CaptureEndpointId == "capture-a" && audient.RenderEndpointId == "render-a", "Loop-back Capture is excluded while the ordinary Capture/Render pair remains auto-selectable");
var reversedAudient = PhysicalDeviceGrouping.Group([
    GroupEndpoint("render-loop", "RENDER", "Loop-back 1/2 (2- Audient iD4)", "{DEVICE-A}"),
    GroupEndpoint("capture-loop-reversed", "CAPTURE", "Loop-back 1/2 (2- Audient iD4)", "{DEVICE-A}"),
    GroupEndpoint("render-analogue-reversed", "RENDER", "Analogue 1/2 (2- Audient iD4)", "{DEVICE-A}"),
    GroupEndpoint("capture-analogue-reversed", "CAPTURE", "Analogue 1/2 (2- Audient iD4)", "{DEVICE-A}")]).Single();
Check(!reversedAudient.DisplayName.Contains("Loop-back", StringComparison.OrdinalIgnoreCase), "physical group display name does not inherit Loop-back from endpoint order");
var duplicateDisplayGroups = PhysicalDeviceGrouping.Group([
    GroupEndpoint("same-name-a", "CAPTURE", "Analogue 1/2 (Audient iD4)", "{GROUP-A}"),
    GroupEndpoint("same-name-b", "CAPTURE", "Analogue 1/2 (Audient iD4)", "{GROUP-B}")]);
Check(duplicateDisplayGroups.Count == 2 && duplicateDisplayGroups.All(g => g.DisplayName == "Analogue 1/2 (Audient iD4)") && duplicateDisplayGroups.Select(g => g.GroupId).Distinct().Count() == 2, "same-name endpoints with different Container IDs remain separate without asserting physical identity");
var mixedActiveGroup = PhysicalDeviceGrouping.Group([GroupEndpoint("active-input", "CAPTURE", "Interface", "{MIXED}"), GroupEndpoint("disabled-output", "RENDER", "Interface", "{MIXED}", state: "DISABLED")]).Single();
Check(mixedActiveGroup.CaptureEndpoints.Count == 1 && mixedActiveGroup.RenderEndpoints.Single().DeviceState == "DISABLED" && mixedActiveGroup.DirectionSummary == Localization.Get("VmPhysicalGroupInput"), "diagnostic grouping retains inactive endpoints while operator direction summary shows only active directions");
var originalUiCulture = Localization.CurrentCulture.Name;
Localization.SetLanguage("en-US", false); var groupStatusEnglish = mixedActiveGroup.VerificationLabel;
Localization.SetLanguage("ru-RU", false); var groupStatusRussian = mixedActiveGroup.VerificationLabel;
Localization.SetLanguage(originalUiCulture, false);
Check(groupStatusEnglish != groupStatusRussian && groupStatusEnglish == Localization.Get("VmPhysicalGroupCaptureAvailable", System.Globalization.CultureInfo.GetCultureInfo("en-US")) && groupStatusRussian == Localization.Get("VmPhysicalGroupCaptureAvailable", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")), "physical grouping operator statuses are localized in English and Russian resources");
Check(PhysicalDeviceGrouping.Group([GroupEndpoint("a", "CAPTURE", "Loopback", "{LOOP}"), GroupEndpoint("b", "RENDER", "Output", "{LOOP}")]).Single().CaptureCandidates.Count == 0, "loopback-only Capture is never asserted analogue");
Check(PhysicalDeviceGrouping.Group([GroupEndpoint("a", "CAPTURE", "Loop back", "{LOOP}"), GroupEndpoint("b", "RENDER", "Output", "{LOOP}")]).Single().CaptureCandidates.Count == 0, "space-separated loopback is excluded");
Check(PhysicalDeviceGrouping.Group([GroupEndpoint("a", "CAPTURE", "loop_back", "{LOOP}"), GroupEndpoint("b", "RENDER", "Output", "{LOOP}")]).Single().CaptureCandidates.Count == 0, "underscore loopback is excluded");
Check(!PhysicalDeviceGrouping.Group([GroupEndpoint("a", "CAPTURE", "Unclassified input", "{UNKNOWN}", "UNKNOWN"), GroupEndpoint("b", "RENDER", "Output", "{UNKNOWN}")]).Single().CanAutoSelectPair, "unknown Capture type is not asserted analogue");
var ambiguousCapture = PhysicalDeviceGrouping.Group([GroupEndpoint("c1", "CAPTURE", "Mic 1", "{AMB}"), GroupEndpoint("c2", "CAPTURE", "Mic 2", "{AMB}"), GroupEndpoint("r", "RENDER", "Output", "{AMB}")]).Single();
Check(ambiguousCapture.RequiresExplicitPair && !ambiguousCapture.CanAutoSelectPair, "two ordinary Capture endpoints require explicit pairing");
var ambiguousRender = PhysicalDeviceGrouping.Group([GroupEndpoint("c", "CAPTURE", "Mic", "{AMBR}"), GroupEndpoint("r1", "RENDER", "Output 1", "{AMBR}"), GroupEndpoint("r2", "RENDER", "Output 2", "{AMBR}")]).Single();
Check(ambiguousRender.RequiresExplicitPair, "multiple Render endpoints require explicit pairing");
var unresolved = PhysicalDeviceGrouping.Group([GroupEndpoint("capture-u", "CAPTURE", "Unknown", null), GroupEndpoint("render-u", "RENDER", "Unknown", null)]);
Check(unresolved.Count == 2 && unresolved.All(g => g.Verification == PhysicalIdentityStatus.Unresolved), "missing container identity fails closed instead of grouping by friendly name");
var singlePair = PhysicalDeviceGrouping.Group([GroupEndpoint("capture-s", "CAPTURE", "DEXP U700", "{DEVICE-S}"), GroupEndpoint("render-s", "RENDER", "DEXP U700", "{DEVICE-S}")]).Single();
Check(singlePair.CanAutoSelectPair && singlePair.CaptureEndpointId == "capture-s" && singlePair.RenderEndpointId == "render-s", "one confirmed Capture/Render pair is auto-selectable with exact endpoint IDs");
var captureOnly = PhysicalDeviceGrouping.Group([GroupEndpoint("capture-only", "CAPTURE", "DEXP U700 Input", "{CAPTURE-ONLY}")]).Single();
Check(captureOnly.HasPartialCapture && !captureOnly.RequiresExplicitPair && captureOnly.CaptureSelectableEndpoints.Count == 1, "single Capture group remains available for a partial scan");
var renderOnly = PhysicalDeviceGrouping.Group([GroupEndpoint("render-only", "RENDER", "DEXP U700 Output", "{RENDER-ONLY}")]).Single();
Check(renderOnly.HasPartialRender && !renderOnly.RequiresExplicitPair, "single Render group remains available for a partial scan");
var unknownCapture = PhysicalDeviceGrouping.Group([GroupEndpoint("unknown-capture", "CAPTURE", "Unknown input", "{UNKNOWN-CAPTURE}", "UNKNOWN"), GroupEndpoint("unknown-render", "RENDER", "Output", "{UNKNOWN-CAPTURE}")]).Single();
Check(!unknownCapture.CanAutoSelectPair && unknownCapture.RequiresExplicitPair && unknownCapture.CaptureSelectableEndpoints.Count == 1, "unknown FormFactor Capture is selectable only through explicit pairing");
bool PumpUntil(Func<bool> condition, TimeSpan timeout)
{
    var frame = new DispatcherFrame();
    var started = DateTime.UtcNow;
    var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
    timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow - started >= timeout) frame.Continue = false; };
    timer.Start();
    Dispatcher.PushFrame(frame);
    timer.Stop();
    return condition();
}
void Start(MainViewModel vm)
{
    if (vm.Screen == ProbeScreen.Capture && vm.SelectedCaptureEndpoint is null) vm.SelectCaptureEndpoint(vm.CaptureEndpoints.FirstOrDefault(e => e.IsAvailable));
    if (vm.Screen == ProbeScreen.Render && vm.SelectedRenderEndpoint is null) vm.SelectRenderEndpoint(vm.RenderEndpoints.FirstOrDefault(e => e.IsAvailable));
    vm.BeginCommand.Execute(null);
    Check(vm.ConfirmPending, "session request opens fresh confirmation");
    vm.ConsentChecked = true;
    vm.ConfirmCommand.Execute(null);
    Check(vm.Session?.Lifecycle == SessionLifecycle.Started, "accepted confirmation starts synthetic session");
}
RealStreamValidationDto StreamReport(string operationId, string endpointId, string period, bool? initialize = true, bool? start = true, long? frames = 240000, long? expected = 240000, double? coverage = 100, bool? durationReached = true, bool? stop = true, string quality = "CLEAN", string cleanup = "STOP_SUCCEEDED;EVENT_RELEASE_ATTEMPTED;CLIENT_RELEASE_ATTEMPTED;ENDPOINT_RELEASE_ATTEMPTED", bool cancelled = false, bool watchdog = false, long discontinuities = 0, string errorStage = "none", string? errorHresult = null)
{
    var report = new
    {
        schemaVersion = 1, reportType = "wasapi-stream-validation", reportId = Guid.NewGuid(), timestampUtc = "2026-10-31T00:00:00Z", provenance = "REAL_WASAPI_STREAM_VALIDATION", probeVersion = "fixture", direction = "CAPTURE",
        format = new { sampleRateHz = 48000, encoding = "PCM16", channels = 2 }, period, durationSeconds = 5, activationHresult = "0x00000000", devicePeriodHresult = "0x00000000", queryHresult = "0x00000000", formatSupported = true,
        initializeSucceeded = initialize, firstInitializeHresult = "0x00000000", finalInitializeHresult = initialize == true ? "0x00000000" : "0x80004005", requestedPeriod100ns = period == "MINIMUM" ? 30000 : 100000,
        actualBufferFrames = period == "MINIMUM" ? 144 : 480, actualBufferPeriod100ns = period == "MINIMUM" ? 30000 : 100000, actualBufferFramesHresult = "0x00000000", setEventHandleHresult = "0x00000000", eventCreateHresult = "0x00000000", eventReady = true,
        startSucceeded = start, startHresult = start == true ? "0x00000000" : start == false ? "0x80004005" : null, events = frames is > 0 ? 500 : 0, packets = frames is > 0 ? 500 : 0, framesProcessed = frames, expectedFrames = expected,
        frameCoveragePercent = coverage, elapsedMilliseconds = 5000, durationReached, discontinuities, timestampErrors = 0, unavailableCaptureBuffers = 0, bufferServiceFaults = 0, eventWaitTimeouts = 0,
        watchdogTimedOut = watchdog, cancelled, deviceInvalidated = false, streamQuality = quality, stopSucceeded = stop, stopHresult = stop == true ? "0x00000000" : stop == false ? "0x80004005" : null,
        cleanup, errorStage, errorHresult, physicalPlayback = "NOT_MEASURED", physicalRouting = "UNKNOWN"
    };
    using var document = JsonDocument.Parse(JsonSerializer.Serialize(report));
    return new(operationId, endpointId, "CAPTURE", document.RootElement.Clone());
}
DuplexValidationDto DuplexReport(string operationId, string captureId, string renderId, string period, string status = "DUPLEX_CLEAN", string provenance = "REAL_WASAPI_DUPLEX", long? captureFrames = 240000, long? renderFrames = 240000, double overlapMs = 5000, long intermediateWaits = 0, long serviceFaults = 0, long packetDrainLimitHits = 0, long longEventIntervals = 0, bool watchdogTimedOut = false)
{
    var actualBufferFrames = period switch { "MINIMUM" => 144, "DEFAULT" => 480, _ => int.Parse(period.Split(':')[2]) };
    object Side(string id, string direction, long? frames) => new
    {
        endpointId = id, direction, activationHresult = "0x00000000", devicePeriodHresult = "0x00000000", isFormatSupportedHresult = "0x00000000",
        initializeHresult = "0x00000000", startHresult = "0x00000000", stopHresult = "0x00000000", getBufferSizeHresult = "0x00000000", setEventHandleHresult = "0x00000000",
        actualBufferFrames, framesProcessed = frames ?? 0, expectedFrames = 240000, events = frames is > 0 ? 1667 : 0, packets = frames is > 0 ? 1667 : 0,
        discontinuities = 0, timestampErrors = 0, serviceFaults, packetDrainLimitHits, intermediateWaitTimeouts = direction == "CAPTURE" ? intermediateWaits : 0, longEventIntervals, devicePositionAnomalies = direction == "CAPTURE" ? 0 : (long?)null, unavailableCaptureBuffers = direction == "CAPTURE" ? 0 : (long?)null, silentBuffersServiced = direction == "RENDER" ? 1667 : (long?)null, eventIntervalsObserved = 1666, frameCoveragePercent = frames is null ? 0d : 100d * frames.Value / 240000, eventIntervalMedianMilliseconds = 3d, eventIntervalP95Milliseconds = 3.1d, eventIntervalP99Milliseconds = 3.4d, elapsedMilliseconds = 5000d, startOffsetMilliseconds = 0.03,
        ready = true, startSucceeded = true, stopSucceeded = true, peerStopped = false, watchdogTimedOut, deviceInvalidated = false, durationReached = true, cancelled = false, quality = status == "DUPLEX_CLEAN" ? "CLEAN" : "ANOMALIES_PRESENT", cleanup = "RELEASE_ATTEMPTED", errorStage = "NONE", errorHresult = (string?)null
    };
    var data = new
    {
        schemaVersion = 2, reportType = "wasapi-duplex-validation", reportId = Guid.NewGuid().ToString("D"), operationId, timestampUtc = "2026-10-31T00:00:00Z", probeVersion = "fixture", provenance,
        requested = new { captureEndpointId = captureId, renderEndpointId = renderId, sampleRateHz = 48000, encoding = "PCM16", channels = 2, period, durationSeconds = 5, mmcss = "OFF" },
        capture = Side(captureId, "CAPTURE", captureFrames), render = Side(renderId, "RENDER", renderFrames),
        duplex = new { bothReadyBeforeStart = true, coordinatedStartAuthorized = true, cancelled = false, peerStopRequested = false, status, startDifferenceMilliseconds = 0.03, simultaneousRuntimeMilliseconds = overlapMs },
        physicalRtl = "NOT_MEASURED", audioHostEligibility = "NOT_CERTIFIED"
    };
    using var document = JsonDocument.Parse(JsonSerializer.Serialize(data));
    return new(operationId, captureId, renderId, document.RootElement.Clone());
}
void BeginStream(MainViewModel vm, string period)
{
    vm.SelectedStreamCandidate = vm.DeviceScanInitializeCandidates.Single(c => c.Channels == 2 && c.Period == period.ToLowerInvariant());
    vm.RequestStreamValidationCommand.Execute(null);
    vm.StreamConsentChecked = true;
    var previousContext = SynchronizationContext.Current;
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
    vm.ConfirmStreamValidationCommand.Execute(null);
    SynchronizationContext.SetSynchronizationContext(previousContext);
}
var app = new App();
app.InitializeComponent();
app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
using (var payloadDoc = JsonDocument.Parse("{\"synthetic\":true}"))
{
    var latestOnly = new WorkerUiEventBuffer();
    for (var i = 1; i <= 40; i++) latestOnly.Enqueue(new(i, Guid.NewGuid(), Guid.NewGuid(), "MeterSnapshot", payloadDoc.RootElement.Clone()));
    latestOnly.Enqueue(new(41, Guid.NewGuid(), Guid.NewGuid(), "Completed", payloadDoc.RootElement.Clone()));
    var batch = latestOnly.Drain();
    Check(batch.Events.Count == 2 && batch.Events.Any(e => e.Name == "Completed") && batch.Events.Single(e => e.Name == "MeterSnapshot").EventSequence == 40 && batch.CoalescedSnapshots == 39, "slow UI consumer retains one latest snapshot, counts coalescing, and preserves terminal control event");
}
Check(new MeterChannelDto(1, null, null, -18, 0, true).PeakText.Contains("тишина", StringComparison.Ordinal) && new MeterChannelDto(1, null, null, null, null).PeakText.Contains("нет данных", StringComparison.Ordinal), "meter labels distinguish completed silence from unavailable levels without infinity");
var comboStyle = (Style)app.Resources[typeof(ComboBox)];
var comboTemplate = (ControlTemplate)comboStyle.Setters.OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value;
Check(comboStyle.Setters.OfType<Setter>().Any(s => s.Property == Control.ForegroundProperty && Equals(s.Value, app.Resources["TextBrush"])), "closed ComboBox uses approved readable foreground token");
Check(comboStyle.Setters.OfType<Setter>().Any(s => s.Property == Control.BackgroundProperty && Equals(s.Value, app.Resources["TwoBrush"])), "closed ComboBox uses dark token surface");
Check(comboStyle.Setters.OfType<Setter>().Any(s => s.Property == FrameworkElement.HorizontalAlignmentProperty && Equals(s.Value, HorizontalAlignment.Stretch)) && comboStyle.Setters.OfType<Setter>().Any(s => s.Property == Control.HorizontalContentAlignmentProperty && Equals(s.Value, HorizontalAlignment.Stretch)), "shared ComboBox stretches its control and content");
Check(comboTemplate.Triggers.OfType<Trigger>().Any(t => t.Property == ComboBox.IsKeyboardFocusWithinProperty) && comboTemplate.Triggers.OfType<Trigger>().Any(t => t.Property == ComboBox.IsDropDownOpenProperty) && comboTemplate.Triggers.OfType<Trigger>().Any(t => t.Property == UIElement.IsEnabledProperty), "ComboBox template defines keyboard focus, open and disabled states");
var languageWindow = new MainWindow(new MainViewModel(developerToolsAvailable: false)) { Width = 1024, Height = 720, ShowActivated = false };
languageWindow.Show(); languageWindow.UpdateLayout();
var languageSelector = (ComboBox)languageWindow.FindName("LanguageSelector")!;
Check(languageSelector.Width >= 150 && languageSelector.MinWidth >= 150 && languageSelector.ItemTemplate is not null && languageSelector.Items.Cast<StringSelectionOption>().Select(x => x.Label).SequenceEqual(new[] { "Русский", "English" }), "language ComboBox uses an explicit label template and a bounded width suitable for 1024-DIP layout");
languageWindow.Close();
foreach (var (language, expectedFormat) in new[] { ("ru-RU", "Основной: {0}"), ("en-US", "Preferred: {0}") })
{
    KlidanAudioProbe.Gui.Core.Localization.SetLanguage(language, false);
    var localizedWindow = new MainWindow(new MainViewModel(developerToolsAvailable: false));
    var preview = (Border)localizedWindow.FindName("HistoricalRecoveryPreview")!;
    var preferredText = ((StackPanel)preview.Child).Children.OfType<TextBlock>().Single(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == "HistoricalRecoveryPreferred");
    Check(BindingOperations.GetBinding(preferredText, TextBlock.TextProperty)?.StringFormat == expectedFormat, $"{language} WPF loads the localized preferred-profile binding format");
    localizedWindow.Close();
}
KlidanAudioProbe.Gui.Core.Localization.SetLanguage(baselineLanguage, false);
var comboControl = new ComboBox { Style = comboStyle };
comboControl.ApplyTemplate();
var popup = (Popup)comboTemplate.FindName("PART_Popup", comboControl);
Check(popup.Child is Border popupBorder && Equals(popupBorder.Background, app.Resources["OneBrush"]) && BindingOperations.GetBinding(popupBorder, FrameworkElement.MinWidthProperty) is Binding popupWidth && popupWidth.Path.Path == "ActualWidth" && popup.Placement == PlacementMode.Bottom, "ComboBox popup uses dark surface, control-width minimum and bottom placement");
var itemStyle = (Style)app.Resources[typeof(ComboBoxItem)];
var itemTemplate = (ControlTemplate)itemStyle.Setters.OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value;
Check(itemTemplate.Triggers.OfType<Trigger>().Any(t => t.Property == ComboBoxItem.IsHighlightedProperty) && itemTemplate.Triggers.OfType<Trigger>().Any(t => t.Property == ComboBoxItem.IsSelectedProperty) && itemTemplate.Triggers.OfType<Trigger>().Any(t => t.Property == UIElement.IsEnabledProperty), "dropdown items define hover, selected and disabled states");
Check(app.Resources.Contains(typeof(ScrollBar)), "scrollbar uses the local dark theme style");
var windowChromeStyle = (Style)app.Resources["WindowChromeButton"];
var windowChromeSetters = windowChromeStyle.Setters.OfType<Setter>().ToDictionary(s => s.Property, s => s.Value);
Check(Equals(windowChromeSetters[FrameworkElement.WidthProperty], 42d) && Equals(windowChromeSetters[FrameworkElement.HeightProperty], 34d) && Equals(windowChromeSetters[FrameworkElement.MinHeightProperty], 0d) && Equals(windowChromeSetters[Control.PaddingProperty], new Thickness(0)), "window chrome style overrides global button sizing with compact fixed geometry");
var windowChromeTemplate = (ControlTemplate)windowChromeSetters[Control.TemplateProperty];
var windowChromeTriggers = windowChromeTemplate.Triggers;
Check(windowChromeTriggers.OfType<Trigger>().Any(t => t.Property.Name == "IsMouseOver") && windowChromeTriggers.OfType<Trigger>().Any(t => t.Property.Name == "IsKeyboardFocused") && windowChromeTriggers.OfType<Trigger>().Any(t => t.Property.Name == "IsEnabled"), "window chrome buttons provide hover, keyboard focus and disabled states");
var closeHover = windowChromeTriggers.OfType<MultiDataTrigger>().SingleOrDefault(t => t.Conditions.OfType<Condition>().Any(c => c.Value as string == "Закрыть"));
Check(closeHover is not null && closeHover.Conditions.OfType<Condition>().Any(c => c.Value as string == "True" || c.Value is bool value && value && (c.Binding as Binding)?.Path.Path == "IsMouseOver") && closeHover.Setters.OfType<Setter>().Any(s => s.Property == Control.BackgroundProperty && Equals(s.Value, app.Resources["DangerBrush"])), "close button has a red hover trigger conditioned on its close identity");
var chromeButton = new Button { Style = windowChromeStyle };
System.Windows.Automation.AutomationProperties.SetName(chromeButton, "Закрыть");
chromeButton.ApplyTemplate();
var chromeBorder = (Border)windowChromeTemplate.FindName("WindowButtonChrome", chromeButton);
Check(chromeBorder.CornerRadius == new CornerRadius(6), "window chrome template uses compact rounded corners");

var profileEndpoint = new WindowsEndpointDto("{CAPTURE-ENDPOINT-LOCAL}", new(0, "AVAILABLE"), "Audient iD4 MKII", new(0, "AVAILABLE"), "CAPTURE", "ACTIVE", 1, new(0, "AVAILABLE"), "UNKNOWN", new(unchecked((int)0x80004005), "UNAVAILABLE"), new(new(unchecked((int)0x80004005), "UNAVAILABLE"), "UNAVAILABLE", null, null, null, null, null, null, "UNKNOWN", null, "UNKNOWN"));
RealFormatCandidateDto ProfileCandidate(string period, string status = "SUPPORTED_REPORTED") => new("PCM16", 48000, 2, period, "NOT_APPLICABLE", status, status, "0x00000000", status == "SUPPORTED_REPORTED" ? "0x00000000" : "0x88890008", "0x00000000", 100000, 30000, "NOT_TESTED", false, null, null, null, null, null, "RELEASE_ATTEMPTED") { QueryCleanupStatus = "RELEASE_ATTEMPTED", QueryClientReleaseAttempts = 1 };
var profileQuery = new RealFormatMatrixDto("REAL_WASAPI_QUERY", "2026-10-30T10:00:00.000Z", profileEndpoint.EndpointId!, "CAPTURE", "PASSIVE_QUERY", [ProfileCandidate("default"), ProfileCandidate("minimum")]);
var profileInitializeCandidate = new RealFormatCandidateDto("PCM16", 48000, 2, "default", "NOT_APPLICABLE", "INITIALIZED", "SUPPORTED_REPORTED", "0x00000000", "0x00000000", "0x00000000", 100000, 30000, "INITIALIZED", true, "0x00000000", null, "0x00000000", 480, "0x00000000", "RELEASE_ATTEMPTED")
{
    RequestedPeriod100ns = 100000, ActualPeriod100ns = 100000, ActualBufferFrames = 480, EventCreateHr = "0x00000000", EventHandleReady = true,
    SetEventHandleHr = "0x00000000", ClientReleaseAttempts = 1
};
var profileInitialize = new RealFormatMatrixDto("REAL_WASAPI_INITIALIZE_ONLY", "2026-10-30T10:01:00.000Z", profileEndpoint.EndpointId!, "CAPTURE", "INITIALIZE_ONLY", [profileInitializeCandidate]);
var captureProfile = DeviceConfigurationProfileFactory.Create(profileEndpoint, profileQuery, profileInitialize, "C1-test", Guid.Parse("10000000-0000-4000-8000-000000000001"), DateTimeOffset.Parse("2026-10-30T10:02:00Z"));
using var streamReportDocument = JsonDocument.Parse("""{"schemaVersion":1,"reportType":"wasapi-stream-validation","reportId":"20000000-0000-4000-8000-000000000001","timestampUtc":"2026-10-30T10:03:00Z","provenance":"REAL_WASAPI_STREAM_VALIDATION","probeVersion":"0.1.0","direction":"CAPTURE","format":{"sampleRateHz":48000,"encoding":"PCM16","channels":2},"period":"DEFAULT","durationSeconds":5,"activationHresult":"0x00000000","queryHresult":"0x00000000","initializeSucceeded":true,"finalInitializeHresult":"0x00000000","actualBufferFrames":480,"startSucceeded":true,"startHresult":"0x00000000","events":500,"packets":500,"framesProcessed":240000,"expectedFrames":240000,"frameCoveragePercent":100,"elapsedMilliseconds":5000,"durationReached":true,"discontinuities":0,"timestampErrors":0,"unavailableCaptureBuffers":0,"bufferServiceFaults":0,"eventWaitTimeouts":0,"streamQuality":"CLEAN","stopSucceeded":true,"stopHresult":"0x00000000","cleanup":"STOP_SUCCEEDED;EVENT_RELEASE_ATTEMPTED;CLIENT_RELEASE_ATTEMPTED;ENDPOINT_RELEASE_ATTEMPTED","physicalPlayback":"NOT_MEASURED","physicalRouting":"UNKNOWN"}""");
var streamProfile = DeviceConfigurationProfileFactory.CreateAggregated(profileEndpoint, profileQuery, [profileInitialize], streamValidations: [new(Guid.Parse("20000000-0000-4000-8000-000000000001").ToString("D"), profileEndpoint.EndpointId!, "CAPTURE", streamReportDocument.RootElement.Clone())]);
var streamProfileRoundTrip = DeviceConfigurationProfileJson.Parse(DeviceConfigurationProfileJson.SerializeLocal(streamProfile));
Check(streamProfileRoundTrip.StreamObservations is [{ Provenance: "REAL_WASAPI_STREAM_VALIDATION", Direction: "CAPTURE", Format: { Period: "DEFAULT", SampleRate: 48000, Channels: 2 }, StartSucceeded: true, FramesProcessed: 240000, StreamQuality: "CLEAN", StopSucceeded: true, Cleanup: "RELEASE_ATTEMPTED", CleanupDetail: "STOP_SUCCEEDED;EVENT_RELEASE_ATTEMPTED;CLIENT_RELEASE_ATTEMPTED;ENDPOINT_RELEASE_ATTEMPTED", PhysicalPlayback: "NOT_MEASURED", PhysicalRouting: "UNKNOWN" }] && streamProfileRoundTrip.CapabilityEvidence.Render.Count == 0 && streamProfileRoundTrip.CapabilityEvidence.Duplex.Count == 0 && streamProfileRoundTrip.Recommendation.Preferred is null, "profile round-trip retains raw cleanup stages and real Capture stream servicing without fabricating Render, Duplex or AudioHost recommendation");
var queryOnlyProfile = DeviceConfigurationProfileFactory.Create(profileEndpoint, profileQuery, probeVersion: "C1-test", profileId: Guid.NewGuid(), createdAtUtc: DateTimeOffset.UtcNow);
Check(queryOnlyProfile.Evidence.All(e => e.Initialize == "NOT_RUN" && e.StreamStart == "NOT_RUN" && e.PcmObservation == "NOT_RUN") && queryOnlyProfile.Recommendation.Preferred is null, "passive-query-only profile never fabricates Initialize, Start, PCM, or recommendation evidence");
Check(captureProfile.EndpointBindings.Capture?.EndpointId == profileEndpoint.EndpointId && captureProfile.EndpointBindings.Render is null && captureProfile.CapabilityEvidence.Render.Count == 0 && captureProfile.CapabilityEvidence.Duplex.Count == 0, "partial Capture profile preserves exact local binding and leaves Render/Duplex evidence absent");
Check(captureProfile.CapabilityEvidence.Capture.All(id => captureProfile.Evidence.Any(e => e.Id == id)) && captureProfile.CapabilityEvidence.Duplex.Count == 0, "capability references resolve to evidence records and missing Duplex remains explicit");
Check(captureProfile.Evidence.Any(e => e.Format.Period == "DEFAULT") && captureProfile.Evidence.Any(e => e.Format.Period == "MINIMUM") && captureProfile.Evidence.Single(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY").Initialize == "INITIALIZED", "profile preserves DEFAULT/MINIMUM query evidence and separate Initialize-only result");
Check(captureProfile.Evidence.Single(e => e.SourceProvenance == "REAL_WASAPI_QUERY" && e.Format.Period == "DEFAULT") is { ActivationHresult: "0x00000000", IsFormatSupportedHresult: "0x00000000", DevicePeriodHresult: "0x00000000", DefaultDevicePeriod100ns: 100000, MinimumDevicePeriod100ns: 30000, QueryCleanup: "RELEASE_ATTEMPTED", QueryClientReleaseAttempts: 1 } && captureProfile.Evidence.Single(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY") is { RequestedPeriod100ns: 100000, ActualPeriod100ns: 100000, BufferFrames: 480, EventCreateHresult: "0x00000000", SetEventHandleHresult: "0x00000000", ClientReleaseAttempts: 1 }, "profile preserves worker-style hexadecimal query and Initialize API-stage, period, buffer and release evidence");
Check(captureProfile.Evidence.Single(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY") is { StreamStart: "NOT_RUN", PcmObservation: "NOT_RUN", Cleanup: "RELEASE_ATTEMPTED", BufferFrames: 480, EventReady: true } && captureProfile.Recommendation.Preferred is null && captureProfile.Recommendation.Fallback is null, "operator-reported Audient Capture fixture is Initialize-only, cleanup is not upgraded, and no Exclusive recommendation is emitted");
Check(DeviceConfigurationProfileJson.IsSuccessfulHresult("S_OK") && DeviceConfigurationProfileJson.IsSuccessfulHresult("0x00000000") && !DeviceConfigurationProfileJson.IsSuccessfulHresult("S_FALSE") && !DeviceConfigurationProfileJson.IsSuccessfulHresult("0x00000001") && !DeviceConfigurationProfileJson.IsSuccessfulHresult("0x80004005") && !DeviceConfigurationProfileJson.IsSuccessfulHresult(null), "single HRESULT predicate accepts only S_OK and its hexadecimal zero representation");
var hexMatrixCandidates = (from rate in new[] { 44100, 48000 } from channels in new[] { 1, 2 } from encoding in new[] { "PCM16", "PCM24_IN_32", "FLOAT32" } from period in new[] { "default", "minimum" } let unsupported = rate == 44100 && encoding == "PCM16" select new RealFormatCandidateDto(encoding, rate, channels, period, "UNKNOWN", unsupported ? "UNSUPPORTED" : "SUPPORTED_REPORTED", unsupported ? "UNSUPPORTED" : "SUPPORTED_REPORTED", "0x00000000", unsupported ? "0x88890008" : "0x00000000", "0x00000000", 100000, 30000, "NOT_TESTED", false, null, null, null, null, null, "RELEASE_ATTEMPTED") { QueryCleanupStatus = "RELEASE_ATTEMPTED", QueryClientReleaseAttempts = 1 }).ToArray();
var hexMatrix = profileQuery with { Candidates = hexMatrixCandidates };
var hexInitialize = profileInitialize with { Candidates = [profileInitializeCandidate with { Status = "READY", InitializeStatus = "READY" }] };
var hexProfile = DeviceConfigurationProfileFactory.Create(profileEndpoint, hexMatrix, hexInitialize, "C1-test", Guid.NewGuid(), DateTimeOffset.Parse("2026-10-30T10:02:00Z"));
Check(hexMatrix.Candidates.Count == 24 && hexProfile.Evidence.Count == 25 && hexProfile.Evidence.Single(e => e.Format.Encoding == "PCM16" && e.Format.SampleRate == 48000 && e.Format.Channels == 2 && e.Format.Period == "DEFAULT" && e.SourceProvenance == "REAL_WASAPI_QUERY").FormatSupport == "SUPPORTED_REPORTED" && hexProfile.Evidence.Single(e => e.Format.Encoding == "PCM16" && e.Format.SampleRate == 44100 && e.Format.Channels == 2 && e.Format.Period == "DEFAULT").FormatSupport == "UNSUPPORTED", "worker-shaped 24-row matrix preserves supported 48 kHz PCM16 and unsupported 44.1 kHz PCM16 separately");
var preservedStatuses = new[] { "UNSUPPORTED", "FAILED", "INCOMPLETE", "NOT_TESTED" }.Select(status => DeviceConfigurationProfileFactory.Create(profileEndpoint, profileQuery with { Candidates = [ProfileCandidate("default", status), ProfileCandidate("minimum", status)] }, probeVersion: "C1-test", profileId: Guid.NewGuid(), createdAtUtc: DateTimeOffset.UtcNow)).ToArray();
Check(preservedStatuses.Select(p => p.Evidence[0].OperationStatus).SequenceEqual(["UNSUPPORTED", "FAILED", "INCOMPLETE", "NOT_RUN"]), "unsupported, failed, incomplete and untested operation states remain distinct and are never promoted to success");
Check(hexProfile.Evidence.Single(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY") is { OperationStatus: "READY", Initialize: "READY", FinalInitializeHresult: "0x00000000", BufferFrames: 480, EventReady: true, Cleanup: "RELEASE_ATTEMPTED", StreamStart: "NOT_RUN", PcmObservation: "NOT_RUN" } && hexProfile.Recommendation.Preferred is null && hexProfile.CapabilityEvidence.Duplex.Count == 0, "hexadecimal Initialize-only evidence keeps 480-frame readiness separate from cleanup, Start, PCM, Duplex and recommendation");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Validate(hexProfile with { Evidence = hexProfile.Evidence.Select(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY" ? e with { FinalInitializeHresult = null } : e).ToArray() }), "READY with missing final Initialize HRESULT is rejected");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Validate(hexProfile with { Evidence = hexProfile.Evidence.Select(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY" ? e with { FinalInitializeHresult = "0x00000001" } : e).ToArray() }), "READY with nonzero final Initialize HRESULT is rejected");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Validate(captureProfile with { Evidence = captureProfile.Evidence.Select(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY" ? e with { FinalInitializeHresult = null } : e).ToArray() }), "INITIALIZED with missing final Initialize HRESULT is rejected");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Validate(hexProfile with { Evidence = hexProfile.Evidence.Select(e => e.SourceProvenance == "REAL_WASAPI_QUERY" && e.Format.Period == "DEFAULT" && e.Format.SampleRate == 48000 && e.Format.Encoding == "PCM16" && e.Format.Channels == 2 ? e with { IsFormatSupportedHresult = "0x00000001" } : e).ToArray() }), "nonzero HRESULT cannot validate SUPPORTED_REPORTED");
var audientObservedCandidate = profileInitializeCandidate with { ActivationHr = null, DevicePeriodHr = null, DefaultPeriod100ns = null, MinimumPeriod100ns = null, ActualPeriod100ns = null, EventCreateHr = null, ClientReleaseAttempts = 0 };
var audientObservedInit = profileInitialize with { Candidates = [audientObservedCandidate] };
var audientInitProfile = DeviceConfigurationProfileFactory.Create(profileEndpoint, null, audientObservedInit, "C1-test", Guid.NewGuid(), DateTimeOffset.Parse("2026-10-30T10:01:00Z"));
var operatorAudientFixture = DeviceConfigurationProfileJson.Validate(audientInitProfile with { Evidence = [audientInitProfile.Evidence.Single() with { SourceProvenance = "OPERATOR_PROVIDED", SourceReference = "operator-reported Audient iD4 MKII Capture result; no Start or PCM" }] });
Check(operatorAudientFixture.Evidence is [{ SourceProvenance: "OPERATOR_PROVIDED", Format: { Period: "DEFAULT", Encoding: "PCM16", SampleRate: 48000, Channels: 2 }, RequestedPeriod100ns: 100000, ActualPeriod100ns: null, BufferFrames: 480, EventReady: true, ActivationHresult: null, DevicePeriodHresult: null, Cleanup: "RELEASE_ATTEMPTED", StreamStart: "NOT_RUN", PcmObservation: "NOT_RUN" }] && operatorAudientFixture.CapabilityEvidence.Render.Count == 0 && operatorAudientFixture.CapabilityEvidence.Duplex.Count == 0, "operator-reported Audient fixture preserves only supplied Capture Initialize facts and leaves unreported fields unknown");
var localProfileBytes = DeviceConfigurationProfileJson.SerializeLocal(captureProfile);
var parsedProfile = DeviceConfigurationProfileJson.Parse(localProfileBytes);
Check(parsedProfile.EndpointBindings.Capture?.EndpointId == profileEndpoint.EndpointId && parsedProfile.Evidence.Count == captureProfile.Evidence.Count && parsedProfile.Evidence.Single(e => e.SourceProvenance == "REAL_WASAPI_QUERY" && e.Format.Period == "DEFAULT").IsFormatSupportedHresult == "0x00000000", "strict profile JSON round-trip preserves exact local identity and raw hexadecimal HRESULT evidence");
var sanitizedProfileBytes = DeviceConfigurationProfileJson.SerializeSanitized(captureProfile);
var sanitizedJson = Encoding.UTF8.GetString(sanitizedProfileBytes);
var parsedSanitized = DeviceConfigurationProfileJson.Parse(sanitizedProfileBytes);
Check(parsedSanitized.Privacy == "SANITIZED_PORTABLE" && parsedSanitized.EndpointBindings.Capture is { EndpointId: null, FriendlyName: null, Binding: "SANITIZED_UNBOUND" } && parsedSanitized.Device.Name is null && !sanitizedJson.Contains(profileEndpoint.EndpointId!, StringComparison.Ordinal) && parsedSanitized.Evidence.Single(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY").FinalInitializeHresult == "0x00000000", "sanitized profile round-trip removes local identity while preserving diagnostic HRESULT evidence");
var privateOperatorEvidence = captureProfile with { Device = captureProfile.Device with { Model = "Synthetic serial SYNTHETIC-SERIAL-123", DriverVersion = "fixtures/private/driver" }, Evidence = captureProfile.Evidence.Select((e, i) => e with { SourceProvenance = "OPERATOR_PROVIDED", SourceReference = $"fixtures/private/{profileEndpoint.EndpointId}-{i}" }).ToArray() };
var privateSanitizedJson = Encoding.UTF8.GetString(DeviceConfigurationProfileJson.SerializeSanitized(privateOperatorEvidence));
Check(!privateSanitizedJson.Contains("SYNTHETIC-SERIAL-123", StringComparison.Ordinal) && !privateSanitizedJson.Contains("fixtures/private", StringComparison.OrdinalIgnoreCase) && !privateSanitizedJson.Contains(profileEndpoint.EndpointId!, StringComparison.Ordinal) && privateSanitizedJson.Contains("OPERATOR_PROVIDED_REDACTED", StringComparison.Ordinal), "sanitized export redacts synthetic device identity and untrusted evidence references that may contain serials, paths or endpoint IDs");
var renderEndpoint = profileEndpoint with { EndpointId = "{RENDER-ENDPOINT-LOCAL}", Direction = "RENDER" };
var duplexMinimum = DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM");
var duplexDefault = DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "DEFAULT");
var minimumAssessment = DeviceConfigurationEvaluator.Evaluate("MINIMUM", duplexMinimum.Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF");
var defaultAssessment = DeviceConfigurationEvaluator.Evaluate("DEFAULT", duplexDefault.Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF");
var duplexRecommendation = DeviceConfigurationEvaluator.Recommend([minimumAssessment, defaultAssessment]);
Check(minimumAssessment.Classification == "DUPLEX_CLEAN_SHORT_RUN" && defaultAssessment.Classification == "DUPLEX_CLEAN_SHORT_RUN" && duplexRecommendation.Preferred?.Period == "MINIMUM" && duplexRecommendation.Fallback?.Period == "DEFAULT" && duplexRecommendation.AudioHostEligibility == "NOT_CERTIFIED", "pair evaluator ranks two independently clean short runs as provisional MINIMUM preferred and DEFAULT fallback, never AudioHost certified");
var minOnlyRecommendation = DeviceConfigurationEvaluator.Recommend([minimumAssessment]);
Check(minOnlyRecommendation.Preferred?.Period == "MINIMUM" && minOnlyRecommendation.Fallback is null, "single verified period never receives an invented fallback");
Check(DeviceConfigurationEvaluator.Recommend([minimumAssessment, minimumAssessment]).Preferred is null, "duplicate period evidence cannot silently select the last candidate");
Check(DeviceConfigurationEvaluator.Evaluate("MINIMUM", DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", provenance: "TEST_FAKE_WASAPI_DUPLEX").Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF").Classification == "INCOMPLETE", "synthetic Duplex CLEAN fixture cannot produce a real recommendation");
Check(DeviceConfigurationEvaluator.Evaluate("MINIMUM", DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", captureFrames: 100_000).Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF").Classification == "INCOMPLETE", "insufficient Capture frames prevent a clean Duplex classification");
Check(DeviceConfigurationEvaluator.Evaluate("MINIMUM", DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", intermediateWaits: 1).Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF").Classification == "DUPLEX_CLEAN_SHORT_RUN", "benign intermediate wait expiry on both sides is retained as a diagnostic without becoming a service fault");
Check(DeviceConfigurationEvaluator.Evaluate("MINIMUM", DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", status: "DUPLEX_WARNINGS", serviceFaults: 1).Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF").Classification == "DUPLEX_WARNINGS" && DeviceConfigurationEvaluator.Evaluate("MINIMUM", DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", status: "DUPLEX_WARNINGS", packetDrainLimitHits: 1).Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF").Classification == "DUPLEX_WARNINGS" && DeviceConfigurationEvaluator.Evaluate("MINIMUM", DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", status: "DUPLEX_WARNINGS", longEventIntervals: 1).Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF").Classification == "DUPLEX_WARNINGS" && DeviceConfigurationEvaluator.Evaluate("MINIMUM", DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", status: "DUPLEX_WARNINGS", watchdogTimedOut: true).Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF").Classification == "DUPLEX_WARNINGS", "service faults, packet drain limits, long event intervals and watchdog results cannot enter clean recommendations");
var warningAssessment = DeviceConfigurationEvaluator.Evaluate("MINIMUM", DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", status: "DUPLEX_WARNINGS", serviceFaults: 1).Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF");
Check(DeviceConfigurationEvaluator.Recommend([warningAssessment]).Preferred is null && DeviceConfigurationEvaluator.Recommend([warningAssessment]).Fallback is null, "warning-only Duplex evidence never receives a preferred or fallback recommendation");
Check(DeviceConfigurationEvaluator.Evaluate("MINIMUM", duplexMinimum.Report, "different-capture", renderEndpoint.EndpointId!, "OFF").Classification == "INCOMPLETE", "report from a different endpoint pair is not combined into this candidate");
var otherPairDefault = DuplexReport(Guid.NewGuid().ToString("D"), "other-capture", renderEndpoint.EndpointId!, "DEFAULT");
var mismatchedPairDefault = DeviceConfigurationEvaluator.Evaluate("DEFAULT", otherPairDefault.Report, "other-capture", renderEndpoint.EndpointId!, "OFF");
Check(DeviceConfigurationEvaluator.Recommend([minimumAssessment, mismatchedPairDefault]).Preferred is null, "different Capture/Render pairs cannot be merged into one period ranking");
var duplexProfile = DeviceConfigurationProfileFactory.CreateDuplex(profileEndpoint, renderEndpoint, [duplexMinimum, duplexDefault], "C2c-test", Guid.NewGuid(), DateTimeOffset.Parse("2026-10-31T00:01:00Z"));
var stableShort = new ProfileStableShortSelection("TEST_ONLY", profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "STEREO_TO_STEREO", new("PCM16", 48000, 2, 16, 16, "CUSTOM:240:50000"), new("PCM16", 48000, 2, 16, 16, "CUSTOM:240:50000"), [0, 1], [0, 1], 240, 50000, 240, 50000, 240, 50000, 240, 50000, "OFF", 2, new string('a', 64), 7, "MINIMUM_SHORT_CLEAN_FOUND", 5, 5, Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray(), "TEST_FAKE_WASAPI_V4_DUPLEX", "v4-search.json", new string('b', 64), DateTimeOffset.UtcNow);
var stableShortProfile = DeviceConfigurationProfileFactory.CreateStableShort(duplexProfile, stableShort);
var stableShortRoundTrip = DeviceConfigurationProfileJson.Parse(DeviceConfigurationProfileJson.SerializeLocal(stableShortProfile));
Check(stableShortRoundTrip.SchemaVersion == 5 && stableShortRoundTrip.StableShortSelection?.RepeatabilityCompleted == 5 && stableShortRoundTrip.StableShortSelection.ProfileKind == "TEST_ONLY", "profile v5 round-trip preserves exact short-run geometry and isolates TEST_FAKE provenance as test-only");
Check(stableShortRoundTrip.AudioHostQualification is null, "legacy v5 short-only profile remains valid without AudioHost qualification");
var testQualification = new ProfileAudioHostQualification("TEST_FIXTURE_SUCCEEDED", "CONFIRMED", Guid.NewGuid().ToString("D"), new string('c', 64), new string('d', 64), new string('e', 40), new string('f', 64), "TEST_ONLY", stableShort.CaptureEndpointId, stableShort.RenderEndpointId, 240, 240, 50000, "PRO_AUDIO", DateTimeOffset.UtcNow, new string('1', 64), new string('2', 64), new string('f', 64), stableShort.SourceEvidenceSha256);
var testQualifiedProfile = stableShortProfile with { StableShortSelection = stableShort with { Mmcss = "PRO_AUDIO" }, AudioHostQualification = testQualification };
var testQualifiedRoundTrip = DeviceConfigurationProfileJson.Parse(DeviceConfigurationProfileJson.SerializeLocal(testQualifiedProfile));
Check(testQualifiedRoundTrip.AudioHostQualification?.Provenance == "TEST_ONLY" && testQualifiedRoundTrip.StableShortSelection?.Mmcss == "PRO_AUDIO" && testQualifiedRoundTrip.Recommendation.Preferred is null, "v5 permits a read-back TEST_ONLY PRO_AUDIO fixture without enabling a device recommendation");
var testOnlyProfileDirectory = Path.Combine(Path.GetTempPath(), "klidan-v5-test-only-readback-" + Guid.NewGuid().ToString("N"));
try
{
    var testOnlyProfilePath = DeviceConfigurationProfileStorage.SaveLocal(testQualifiedRoundTrip, testOnlyProfileDirectory);
    var retainedProfilePath = Environment.GetEnvironmentVariable("KLIDAN_TEST_ONLY_PROFILE_OUTPUT");
    if (!string.IsNullOrWhiteSpace(retainedProfilePath))
    {
        retainedProfilePath = Path.GetFullPath(retainedProfilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(retainedProfilePath)!);
        File.Copy(testOnlyProfilePath, retainedProfilePath, overwrite: true);
        testOnlyProfilePath = retainedProfilePath;
    }
    var diskReadback = DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(testOnlyProfilePath));
    Check(diskReadback.StableShortSelection?.ProfileKind == "TEST_ONLY" && diskReadback.AudioHostQualification?.Provenance == "TEST_ONLY" && diskReadback.AudioHostQualification?.Result == "TEST_FIXTURE_SUCCEEDED" && diskReadback.Recommendation.Preferred is null, $"synthetic qualified Profile v5 saves, validates and reads back from disk as TEST_ONLY without a DEVICE recommendation [path={testOnlyProfilePath}]");
}
finally { if (Directory.Exists(testOnlyProfileDirectory)) Directory.Delete(testOnlyProfileDirectory, true); }
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Validate(testQualifiedProfile with { StableShortSelection = testQualifiedProfile.StableShortSelection! with { ProfileKind = "DEVICE", EvidenceProvenance = "REAL_WASAPI_V4_DUPLEX" } }), "synthetic AudioHost fixture cannot promote a DEVICE profile");
Throws<InvalidDataException>(() => DeviceConfigurationProfileFactory.CreateStableShort(duplexProfile, stableShort with { ProfileKind = "DEVICE" }), "profile v5 rejects synthetic provenance for a physical device profile");
var testOnlyBinding = DeviceProfileBindingRevalidation.Evaluate(stableShortProfile, new WindowsEndpointDiscoveryDto("REAL_WINDOWS_METADATA", "COMPLETE", 0, 0, [], [profileEndpoint, renderEndpoint]));
Check(testOnlyBinding.Overall == DeviceProfileBindingAssessment.ProfileInvalidOrUnsupported && testOnlyBinding.Reasons.Contains("TEST_ONLY_PROFILE_CANNOT_BE_BOUND_AS_A_PHYSICAL_DEVICE"), "a TEST_ONLY v5 profile cannot be accepted as a physical device binding");
Throws<InvalidDataException>(() => DeviceConfigurationProfileFactory.CreateStableShort(duplexProfile, stableShort with { RepeatabilityCompleted = 4 }), "profile v5 refuses fewer than five CLEAN repeats");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.SerializeSanitized(stableShortProfile), "profile v5 exact endpoint identity cannot be exported through a lossy sanitized representation");
var stableProfileDirectory = Path.Combine(Path.GetTempPath(), "klidan-v5-profile-test-" + Guid.NewGuid().ToString("N"));
try
{
    var stableProfilePath = DeviceConfigurationProfileStorage.SaveLocal(stableShortProfile, stableProfileDirectory);
    Check(DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(stableProfilePath)).StableShortSelection?.SourceEvidenceSha256 == stableShort.SourceEvidenceSha256, "v5 profile atomic no-overwrite storage reads back the exact synthetic evidence hash");
    Throws<IOException>(() => DeviceConfigurationProfileStorage.SaveLocal(stableShortProfile, stableProfileDirectory), "v5 profile storage refuses to overwrite an existing profile");
}
finally { if (Directory.Exists(stableProfileDirectory)) Directory.Delete(stableProfileDirectory, true); }
var warningDuplex = DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", status: "DUPLEX_WARNINGS", serviceFaults: 1);
var failedDuplex = DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", status: "FAILED");
var incompleteDuplex = DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", status: "INCOMPLETE");
var cancelledDuplex = DuplexReport(Guid.NewGuid().ToString("D"), profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "MINIMUM", status: "CANCELLED");
var warningProfile = DeviceConfigurationProfileFactory.CreateDuplex(profileEndpoint, renderEndpoint, [warningDuplex, duplexDefault], "C3.5.3-test");
Check(warningProfile.Evidence.Single(e => e.Format.Period == "MINIMUM" && e.SourceProvenance == "REAL_WASAPI_DUPLEX").Duplex == "DUPLEX_WARNINGS" && warningProfile.Evidence.Single(e => e.Format.Period == "MINIMUM" && e.SourceProvenance == "REAL_WASAPI_DUPLEX").OperationStatus == "DUPLEX_WARNINGS" && warningProfile.DuplexObservations.Single(o => o.Period == "MINIMUM").Report.GetProperty("duplex").GetProperty("status").GetString() == "DUPLEX_WARNINGS", "non-clean Duplex warning preserves its profile state and native report status");
Check(DeviceConfigurationProfileFactory.CreateDuplex(profileEndpoint, renderEndpoint, [failedDuplex, duplexDefault], "C3.5.3-test").Evidence.Any(e => e.Duplex == "FAILED") && DeviceConfigurationProfileFactory.CreateDuplex(profileEndpoint, renderEndpoint, [incompleteDuplex, duplexDefault], "C3.5.3-test").Evidence.Any(e => e.Duplex == "INCOMPLETE") && DeviceConfigurationProfileFactory.CreateDuplex(profileEndpoint, renderEndpoint, [cancelledDuplex, duplexDefault], "C3.5.3-test").Evidence.Any(e => e.Duplex == "DUPLEX_CANCELLED"), "failed, incomplete and cancelled Duplex outcomes remain distinct profile states");
var warningDefaultRecommendation = DeviceConfigurationEvaluator.Recommend([DeviceConfigurationEvaluator.Evaluate("MINIMUM", warningDuplex.Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF"), defaultAssessment]);
var failedDefaultRecommendation = DeviceConfigurationEvaluator.Recommend([DeviceConfigurationEvaluator.Evaluate("MINIMUM", failedDuplex.Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF"), defaultAssessment]);
Check(warningDefaultRecommendation.Preferred?.Period == "DEFAULT" && warningDefaultRecommendation.Fallback is null && failedDefaultRecommendation.Preferred?.Period == "DEFAULT" && failedDefaultRecommendation.Fallback is null, "clean DEFAULT remains the only provisional recommendation when MINIMUM is warning or failed");
var failedDefault = failedDuplex with { OperationId = Guid.NewGuid().ToString("D") };
var bothNonCleanRecommendation = DeviceConfigurationEvaluator.Recommend([DeviceConfigurationEvaluator.Evaluate("MINIMUM", warningDuplex.Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF"), DeviceConfigurationEvaluator.Evaluate("DEFAULT", failedDefault.Report, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "OFF")]);
Check(bothNonCleanRecommendation.Preferred is null && bothNonCleanRecommendation.Fallback is null, "two non-clean Duplex candidates cannot produce a provisional recommendation");
Check(DeviceConfigurationProfileJson.Parse(DeviceConfigurationProfileJson.SerializeLocal(warningProfile)).Evidence.Any(e => e.Duplex == "DUPLEX_WARNINGS"), "v3 round-trip accepts and preserves non-clean Duplex evidence");
var lowercaseOperation = Guid.Parse(duplexMinimum.OperationId).ToString("D").ToLowerInvariant();
var uppercaseOperation = lowercaseOperation.ToUpperInvariant();
var caseVariantReport = DeviceConfigurationProfileFactory.CreateDuplex(profileEndpoint, renderEndpoint, [duplexMinimum with { OperationId = uppercaseOperation }], "C2c-test");
Check(caseVariantReport.DuplexObservations.Single().OperationId == Guid.Parse(lowercaseOperation), "real Duplex report operation UUID matching is case-insensitive and retains parsed identity");
Throws<InvalidDataException>(() => DeviceConfigurationProfileFactory.CreateDuplex(profileEndpoint, renderEndpoint, [duplexMinimum with { OperationId = Guid.NewGuid().ToString("D") }]), "Duplex profile rejects an operation UUID different from its report");
Throws<InvalidDataException>(() => DeviceConfigurationProfileFactory.CreateDuplex(profileEndpoint, renderEndpoint, [duplexMinimum with { OperationId = "" }]), "Duplex profile rejects an empty operation UUID");
Throws<InvalidDataException>(() => DeviceConfigurationProfileFactory.CreateDuplex(profileEndpoint, renderEndpoint, [duplexMinimum with { OperationId = "not-a-guid" }]), "Duplex profile rejects a malformed operation UUID");
Throws<InvalidDataException>(() => DeviceConfigurationProfileFactory.CreateDuplex(profileEndpoint, renderEndpoint, [duplexMinimum with { CaptureEndpointId = "other-capture" }]), "Duplex profile rejects mismatched endpoint identity");
var settingsMismatchJson = System.Text.Json.Nodes.JsonNode.Parse(duplexMinimum.Report.GetRawText())!.AsObject();
settingsMismatchJson["requested"]!["sampleRateHz"] = 44100;
var settingsMismatch = duplexMinimum with { Report = System.Text.Json.JsonDocument.Parse(settingsMismatchJson.ToJsonString()).RootElement.Clone() };
Throws<InvalidDataException>(() => DeviceConfigurationProfileFactory.CreateDuplex(profileEndpoint, renderEndpoint, [settingsMismatch]), "Duplex profile rejects configuration that differs from the requested profile format");
var duplexRoundTrip = DeviceConfigurationProfileJson.Parse(DeviceConfigurationProfileJson.SerializeLocal(duplexProfile));
Check(duplexRoundTrip.SchemaVersion == 3 && duplexRoundTrip.EndpointBindings.Capture?.EndpointId == profileEndpoint.EndpointId && duplexRoundTrip.EndpointBindings.Render?.EndpointId == renderEndpoint.EndpointId && duplexRoundTrip.DuplexObservations.Count == 2 && duplexRoundTrip.CapabilityEvidence.Duplex.Count == 2 && duplexRoundTrip.Recommendation.Preferred?.Period == "MINIMUM" && duplexRoundTrip.Recommendation.Fallback?.Period == "DEFAULT", "profile v3 round-trip preserves exact pair, independent periods and evaluator-derived recommendation");
var scanStoreRoot = Path.Combine(Path.GetTempPath(), $"klidan-scan-store-{Guid.NewGuid():N}");
try
{
    ScanOperation DuplexOperation(DuplexValidationDto d, string period) => new(ScanStage.Duplex, profileEndpoint.EndpointId, "PAIR", "PCM16", 48000, 2, period, 5, d.OperationId, "OFF", profileEndpoint.EndpointId, renderEndpoint.EndpointId);
    var pairRenderQuery = profileQuery with { EndpointId = renderEndpoint.EndpointId!, Direction = "RENDER" };
    var pairRenderInitialize = profileInitialize with { EndpointId = renderEndpoint.EndpointId!, Direction = "RENDER" };
    var captureStream = new RealStreamValidationDto(Guid.Parse("20000000-0000-4000-8000-000000000001").ToString("D"), profileEndpoint.EndpointId!, "CAPTURE", streamReportDocument.RootElement.Clone());
    var scanQuery = new ScanOperationResult(new(ScanStage.Query, profileEndpoint.EndpointId, "CAPTURE", OperationId: Guid.NewGuid().ToString("D")), ScanOperationLifecycle.Completed, "query accepted", "RELEASE_ATTEMPTED", new(FormatMatrix: profileQuery));
    var scanInitialize = new ScanOperationResult(new(ScanStage.Initialize, profileEndpoint.EndpointId, "CAPTURE", "PCM16", 48000, 2, "DEFAULT", OperationId: Guid.NewGuid().ToString("D")), ScanOperationLifecycle.Completed, "initialize accepted", "RELEASE_ATTEMPTED", new(FormatMatrix: profileInitialize));
    var scanRenderQuery = new ScanOperationResult(new(ScanStage.Query, renderEndpoint.EndpointId, "RENDER", OperationId: Guid.NewGuid().ToString("D")), ScanOperationLifecycle.Completed, "query accepted", "RELEASE_ATTEMPTED", new(FormatMatrix: pairRenderQuery));
    var scanRenderInitialize = new ScanOperationResult(new(ScanStage.Initialize, renderEndpoint.EndpointId, "RENDER", "PCM16", 48000, 2, "DEFAULT", OperationId: Guid.NewGuid().ToString("D")), ScanOperationLifecycle.Completed, "initialize accepted", "RELEASE_ATTEMPTED", new(FormatMatrix: pairRenderInitialize));
    var scanStream = new ScanOperationResult(new(ScanStage.Stream, profileEndpoint.EndpointId, "CAPTURE", "PCM16", 48000, 2, "DEFAULT", 5, captureStream.OperationId), ScanOperationLifecycle.Completed, "stream accepted", "RELEASE_ATTEMPTED", new(Stream: captureStream));
    var realDuplexOperations = new[]
    {
        new ScanOperationResult(DuplexOperation(duplexMinimum, "MINIMUM"), ScanOperationLifecycle.Completed, "real fixture", "RELEASE_ATTEMPTED", new(Duplex: duplexMinimum)),
        new ScanOperationResult(DuplexOperation(duplexDefault, "DEFAULT"), ScanOperationLifecycle.Completed, "real fixture", "RELEASE_ATTEMPTED", new(Duplex: duplexDefault))
    };
    var allScanOperations = new[] { scanQuery, scanInitialize, scanRenderQuery, scanRenderInitialize, scanStream }.Concat(realDuplexOperations).ToArray();
    var savedScan = DeviceConfigurationScanStorage.Save(Guid.NewGuid().ToString("D"), ScanRunLifecycle.Completed,
        new(profileEndpoint, renderEndpoint, ScanPairIdentity.OperatorSelectedPair), allScanOperations,
        [minimumAssessment, defaultAssessment], duplexRecommendation, scanStoreRoot,
        stageResults: [new(AutomaticScanStage.SAVE, AutomaticScanStageState.PLANNED, "pending")]);
    var savedScanSummary = DeviceConfigurationScanStorage.LoadSummary(savedScan.SummaryPath!);
    var linkedReportPath = Path.Combine(scanStoreRoot, "repeatability-report.json");
    File.WriteAllText(linkedReportPath, "{}", Encoding.UTF8);
    var linkedChild = new AutomaticScanChildOperation("REPEATABILITY", duplexMinimum.OperationId, duplexMinimum.Report.GetProperty("reportId").GetString(), "COMPLETED", "REAL_WASAPI_DUPLEX", "RELEASE_ATTEMPTED", "linked fixture", linkedReportPath, "MINIMUM", profileEndpoint.EndpointId, "PAIR");
    var linkedSave = DeviceConfigurationScanStorage.Save(Guid.NewGuid().ToString("D"), ScanRunLifecycle.Incomplete, new(profileEndpoint, renderEndpoint, ScanPairIdentity.OperatorSelectedPair), allScanOperations, [minimumAssessment, defaultAssessment], duplexRecommendation, Path.Combine(scanStoreRoot, "linked"), childOperations: [linkedChild], budget: new AutomaticScanBudget(86, 300, 1600));
    var linkedSummary = DeviceConfigurationScanStorage.LoadSummary(linkedSave.SummaryPath!);
    Check(linkedSummary.ChildOperations.Single().ReportId == linkedChild.ReportId && linkedSummary.ChildOperations.Single().OperationId == linkedChild.OperationId && linkedSummary.ChildOperations.Single().ReportPath == linkedReportPath && linkedSummary.AutomaticBudget?.MaximumOperations == 86 && linkedSummary.AutomaticBudget.MaximumActiveSeconds == 300 && linkedSummary.Lifecycle == "INCOMPLETE", "automatic scan read-back preserves child report identity, operation identity, persistence path, approved budget and partial lifecycle");
    var savedPairProfile = DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(savedScan.ProfilePaths.Single()));
    var inventory = AutomaticScanPlanner.FromQueries([profileQuery, profileQuery with { EndpointId = renderEndpoint.EndpointId!, Direction = "RENDER" }]);
    var stagedPlan = AutomaticScanPlanner.Create(new("REAL_WINDOWS_METADATA", "COMPLETED", 0, 0, [], [profileEndpoint, renderEndpoint]), ScanPlan.Build(new(profileEndpoint, renderEndpoint, ScanPairIdentity.OperatorSelectedPair), new(true, true, true, false, false)), new([new(profileEndpoint.EndpointId!, "CAPTURE"), new(renderEndpoint.EndpointId!, "RENDER")], ["QueryFormatMatrix", "ValidateFormatMatrix"], [], 600, 10, 8));
    Check(inventory.Any(c => c.State == AutomaticCandidateState.SUPPORTED_FOR_TEST && c.ContainerBits is null && c.ValidBits is null) && inventory.Any(c => c.Period.StartsWith("CUSTOM:", StringComparison.Ordinal) && c.State == AutomaticCandidateState.NOT_TESTED) && stagedPlan.Stages.Single(s => s.Stage == AutomaticScanStage.PERIOD_DISCOVERY).State == AutomaticScanStageState.NOT_RUN && stagedPlan.Stages.Single(s => s.Stage == AutomaticScanStage.INITIALIZE).State == AutomaticScanStageState.PLANNED, "S1 creates deterministic exact candidate inventory, bounded custom candidates and keeps later period/runtimes explicitly NOT_RUN");
    Check(inventory.Where(c => c.EndpointId == profileEndpoint.EndpointId && c.SampleRateHz == 48000 && c.Encoding == "PCM16" && c.Channels == 2 && c.Period is "MINIMUM" or "DEFAULT").ToDictionary(c => c.Period).All(p => p.Value.Period100ns == (p.Key == "MINIMUM" ? 30000 : 100000)), "S2.2 preserves endpoint-specific MINIMUM 144-frame and DEFAULT 480-frame period geometry");
    var customInventory = inventory.Where(c => c.EndpointId == profileEndpoint.EndpointId && c.Period.StartsWith("CUSTOM:", StringComparison.Ordinal)).ToArray();
    var customPeriodReports = customInventory.SelectMany(candidate => new[] { "CAPTURE", "RENDER" }.Select(direction => new ExclusivePeriodDiscoveryReportDto(1, "wasapi-exclusive-period-discovery", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "2026-11-01T00:00:00Z", "REAL_WASAPI_INITIALIZE_ONLY", direction == "CAPTURE" ? profileEndpoint.EndpointId! : renderEndpoint.EndpointId!, direction, 48000, "PCM16", 2, 30000, 100000, [new( candidate.Period100ns!.Value, (uint)int.Parse(candidate.Period.Split(':')[2]), candidate.Period100ns.Value, "ACCEPTED", "exact", "0x00000000", null, null, (uint)int.Parse(candidate.Period.Split(':')[2]), candidate.Period100ns.Value, "RELEASE_ATTEMPTED", 1)]))).ToArray();
    var periodFixture = new PeriodDiscoverySeriesResult("COMPLETED", customPeriodReports, [], "", null, "SAVED_READ_BACK_VALIDATED");
    var fullPeriods = new[] { "MINIMUM", "DEFAULT", "MINIMUM", "DEFAULT", "MINIMUM", "DEFAULT" }.Concat(customInventory.Select(c => c.Period).SelectMany(p => Enumerable.Repeat(p, 5))).ToArray();
    var fullRuns = fullPeriods.Select((period, index) => { var operationId = Guid.NewGuid().ToString("D"); return new DuplexRepeatabilityRun(index + 1, period, operationId, ScanOperationLifecycle.Completed, "RELEASE_ATTEMPTED", "fixture", DuplexReport(operationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, period, intermediateWaits: index < 15 ? 1 : 0)); }).ToArray();
    var runtimeInventory = AutomaticScanPlanner.WithRuntimeResults(inventory, periodFixture, fullRuns.Select(r => r.Period), fullRuns);
    Check(fullRuns.Length == 46 && fullRuns.Sum(r => r.Validation!.Report.GetProperty("capture").GetProperty("intermediateWaitTimeouts").GetInt64()) == 15 && fullRuns.All(r => r.Validation!.Report.GetProperty("duplex").GetProperty("status").GetString() == "DUPLEX_CLEAN") && runtimeInventory.Where(c => c.SampleRateHz == 48000 && c.Encoding == "PCM16" && c.Channels == 2 && (c.Period is "MINIMUM" or "DEFAULT" || c.Period.StartsWith("CUSTOM:", StringComparison.Ordinal))).All(c => c.State == AutomaticCandidateState.RUNTIME_TESTED) && runtimeInventory.Where(c => c.SampleRateHz != 48000 || c.Encoding != "PCM16" || c.Channels != 2).All(c => c.State != AutomaticCandidateState.RUNTIME_TESTED), "S2.2 exact runtime identity includes all 46 CLEAN repetitions, preserves 15 recovered waits, and prevents cross-format promotion");
    Check(savedScan.Status == "SAVED_AND_RELOADED", "paired scan save succeeds");
    Check(savedScanSummary.AutomaticStages.Count == 1 && savedScanSummary.AutomaticStages.Single().State == AutomaticScanStageState.COMPLETED, $"persisted SAVE stage reflects the actual save result ({savedScanSummary.AutomaticStages.Count}:{string.Join(',', savedScanSummary.AutomaticStages.Select(s => $"{s.Stage}={s.State}"))})");
    Check(savedScanSummary.Operations.Count == allScanOperations.Length && savedScan.ProfilePaths.Count == 1 && savedPairProfile.SchemaVersion == 3 && savedPairProfile.DuplexObservations.Count == 2 && savedPairProfile.Evidence.Any(e => e.SourceProvenance == "REAL_WASAPI_QUERY" && e.EndpointDirection == "CAPTURE") && savedPairProfile.Evidence.Any(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY" && e.EndpointDirection == "CAPTURE") && savedPairProfile.Evidence.Any(e => e.SourceProvenance == "REAL_WASAPI_QUERY" && e.EndpointDirection == "RENDER") && savedPairProfile.StreamObservations.Count == 1 && savedPairProfile.StreamObservations[0].Provenance == "REAL_WASAPI_STREAM_VALIDATION" && savedPairProfile.CapabilityEvidence.Capture.All(id => savedPairProfile.Evidence.Any(e => e.Id == id)) && savedPairProfile.CapabilityEvidence.Render.All(id => savedPairProfile.Evidence.Any(e => e.Id == id)) && savedPairProfile.CapabilityEvidence.Duplex.All(id => savedPairProfile.Evidence.Any(e => e.Id == id)) && savedPairProfile.AudioHostAssessments.All(a => !a.EligibleForRecommendation), "paired scan automatically saves one reloaded profile v3 containing exact Capture/Render query, Initialize, stream and Duplex evidence without AudioHost certification");
    var warningDefaultOperations = new[]
    {
        new ScanOperationResult(DuplexOperation(warningDuplex, "MINIMUM"), ScanOperationLifecycle.Completed, "warning fixture", "RELEASE_ATTEMPTED", new(Duplex: warningDuplex)),
        new ScanOperationResult(DuplexOperation(duplexDefault, "DEFAULT"), ScanOperationLifecycle.Completed, "clean fixture", "RELEASE_ATTEMPTED", new(Duplex: duplexDefault))
    };
    var warningDefaultSave = DeviceConfigurationScanStorage.Save(Guid.NewGuid().ToString("D"), ScanRunLifecycle.Completed, new(profileEndpoint, renderEndpoint, ScanPairIdentity.OperatorSelectedPair), warningDefaultOperations, [warningDefaultRecommendation.Preferred!, defaultAssessment], warningDefaultRecommendation, Path.Combine(scanStoreRoot, "warning-default"));
    var warningDefaultProfile = DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(warningDefaultSave.ProfilePaths.Single()));
    Check(warningDefaultSave.Status == "SAVED_AND_RELOADED" && warningDefaultProfile.Evidence.Any(e => e.Duplex == "DUPLEX_WARNINGS") && warningDefaultProfile.Evidence.Any(e => e.Duplex == "DUPLEX_QA") && warningDefaultProfile.Recommendation.Preferred?.Period == "DEFAULT", "v3 save/read-back preserves warning MINIMUM with clean DEFAULT and recommends only DEFAULT");
    var invalidDuplex = duplexMinimum with { OperationId = Guid.NewGuid().ToString("D") };
    var failedProfileSave = DeviceConfigurationScanStorage.Save(Guid.NewGuid().ToString("D"), ScanRunLifecycle.Completed, new(profileEndpoint, renderEndpoint, ScanPairIdentity.OperatorSelectedPair), [new ScanOperationResult(DuplexOperation(invalidDuplex, "MINIMUM"), ScanOperationLifecycle.Completed, "invalid profile fixture", "RELEASE_ATTEMPTED", new(Duplex: invalidDuplex))], [], new("NONE", "NOT_CERTIFIED", null, null, ["Profile fixture failed."]), Path.Combine(scanStoreRoot, "profile-failure"));
    Check(failedProfileSave.Status == "SUMMARY_SAVED_PROFILE_FAILED" && failedProfileSave.SummaryPath is not null && File.Exists(failedProfileSave.SummaryPath) && DeviceConfigurationScanStorage.LoadSummary(failedProfileSave.SummaryPath).Lifecycle == "COMPLETED" && failedProfileSave.ProfilePaths.Count == 0 && failedProfileSave.Error?.Contains("Duplex profile evidence", StringComparison.Ordinal) == true, "profile conversion failure saves the actual completed scan summary without claiming a profile was saved");
    Throws<IOException>(() => DeviceConfigurationScanStorage.Save(savedScanSummary.SessionId, ScanRunLifecycle.Completed,
        new(profileEndpoint, renderEndpoint, ScanPairIdentity.OperatorSelectedPair), realDuplexOperations,
        [minimumAssessment, defaultAssessment], duplexRecommendation, scanStoreRoot), "automatic scan persistence refuses to overwrite a prior summary");
    var incompleteRoot = Path.Combine(scanStoreRoot, "incomplete");
    var incompleteSave = DeviceConfigurationScanStorage.Save(Guid.NewGuid().ToString("D"), ScanRunLifecycle.Incomplete,
        new(profileEndpoint, null), [new(new(ScanStage.Query, profileEndpoint.EndpointId, "CAPTURE"), ScanOperationLifecycle.Unknown, "worker disconnected", "UNKNOWN", null)],
        [], new("PROBE_PROVISIONAL", "NOT_CERTIFIED", null, null, ["No complete evidence."]), incompleteRoot);
    Check(DeviceConfigurationScanStorage.LoadSummary(incompleteSave.SummaryPath!).Lifecycle == "INCOMPLETE" && incompleteSave.ProfilePaths.Count == 0, "incomplete real scan is retained as a truthful local summary without inventing a profile");
    using var fakeStreamJson = JsonDocument.Parse(streamReportDocument.RootElement.GetRawText().Replace("REAL_WASAPI_STREAM_VALIDATION", "TEST_FAKE_STREAM_BACKEND", StringComparison.Ordinal));
    var fakeStream = new RealStreamValidationDto(Guid.Parse("20000000-0000-4000-8000-000000000001").ToString("D"), profileEndpoint.EndpointId!, "CAPTURE", fakeStreamJson.RootElement.Clone());
    Throws<InvalidDataException>(() => DeviceConfigurationScanStorage.Save(Guid.NewGuid().ToString("D"), ScanRunLifecycle.Completed,
        new(profileEndpoint, null), [new(new(ScanStage.Stream, profileEndpoint.EndpointId, "CAPTURE"), ScanOperationLifecycle.Completed, "fixture", "RELEASE_ATTEMPTED", new(Stream: fakeStream))],
        [], new("PROBE_PROVISIONAL", "NOT_CERTIFIED", null, null, []), Path.Combine(scanStoreRoot, "synthetic")), "synthetic Worker evidence cannot enter real scan persistence");
}
finally { if (Directory.Exists(scanStoreRoot)) Directory.Delete(scanStoreRoot, recursive: true); }
var legacyV1 = System.Text.Json.Nodes.JsonNode.Parse(DeviceConfigurationProfileJson.SerializeLocal(duplexProfile))!.AsObject();
foreach (var observation in legacyV1["duplexObservations"]!.AsArray())
    foreach (var sideName in new[] { "capture", "render" })
    {
        var side = observation!["report"]![sideName]!.AsObject();
        side.Remove("packetDrainLimitHits"); side.Remove("intermediateWaitTimeouts"); side.Remove("longEventIntervals");
        observation["report"]!["schemaVersion"] = 1;
    }
Check(DeviceConfigurationProfileJson.Parse(Encoding.UTF8.GetBytes(legacyV1.ToJsonString())).DuplexObservations.Count == 2, "profile v3 remains able to read existing embedded Duplex report v1 observations");
var duplexSanitizedBytes = DeviceConfigurationProfileJson.SerializeSanitized(duplexProfile);
var duplexSanitized = DeviceConfigurationProfileJson.Parse(duplexSanitizedBytes);
Check(duplexSanitized.Privacy == "SANITIZED_PORTABLE" && duplexSanitized.DuplexObservations.All(o => o.CaptureEndpointId == "REDACTED" && o.RenderEndpointId == "REDACTED") && !Encoding.UTF8.GetString(duplexSanitizedBytes).Contains(profileEndpoint.EndpointId!, StringComparison.Ordinal) && !Encoding.UTF8.GetString(duplexSanitizedBytes).Contains(renderEndpoint.EndpointId!, StringComparison.Ordinal), "sanitized v3 profile removes endpoint IDs from pair bindings and nested Duplex reports");
var renderQuery = profileQuery with { EndpointId = renderEndpoint.EndpointId!, Direction = "RENDER", Candidates = [ProfileCandidate("default", "UNSUPPORTED"), ProfileCandidate("minimum", "UNSUPPORTED")] };
var renderProfile = DeviceConfigurationProfileFactory.Create(renderEndpoint, renderQuery, probeVersion: "C1-test", profileId: Guid.NewGuid(), createdAtUtc: DateTimeOffset.UtcNow);
Check(renderProfile.EndpointBindings.Capture is null && renderProfile.EndpointBindings.Render?.EndpointId == renderEndpoint.EndpointId && renderProfile.CapabilityEvidence.Render.Count == 2 && renderProfile.CapabilityEvidence.Capture.Count == 0 && renderProfile.Evidence.All(e => e.FormatSupport == "UNSUPPORTED"), "partial Render profile remains direction-specific and preserves unsupported exact-format evidence");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Parse(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":1}")), "duplicate JSON properties are rejected");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Parse(Encoding.UTF8.GetBytes("{\"schemaVersion\":3}")), "unknown schema/missing required fields are rejected");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Parse(new byte[DeviceConfigurationProfileJson.MaximumBytes + 1]), "profile payload size limit is enforced before parsing");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Validate(captureProfile with { SchemaVersion = 3 }), "semantic validation rejects unsupported schema version");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Validate(captureProfile with { Evidence = [captureProfile.Evidence[0] with { IsFormatSupportedHresult = "0x88890008" }] }), "contradictory SUPPORTED_REPORTED and HRESULT evidence is rejected");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Validate(captureProfile with { Evidence = [captureProfile.Evidence[0] with { ActivationHresult = "S_NOT_A_HRESULT" }] }), "malformed symbolic HRESULT values are rejected");
Throws<InvalidDataException>(() => DeviceConfigurationProfileFactory.Create(profileEndpoint, profileQuery with { Candidates = [ProfileCandidate("default"), ProfileCandidate("default")] }), "duplicate format/period candidates cannot enter a profile");
Throws<InvalidDataException>(() => DeviceConfigurationProfileFactory.Create(profileEndpoint, profileQuery with { Candidates = [ProfileCandidate("default"), ProfileCandidate("minimum", "UNSUPPORTED")] }), "contradictory period rows cannot enter a profile");
Throws<JsonException>(() => DeviceConfigurationProfileJson.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(localProfileBytes).Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"untrustedExtra\": true,", StringComparison.Ordinal))), "profile deserializer rejects unknown security-sensitive fields");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.Validate(captureProfile with { Recommendation = new(new("COORDINATED_EXCLUSIVE", new("PCM16", 48000, 2, 16, 16, "MINIMUM"), "MINIMUM", captureProfile.CapabilityEvidence.Capture), null, []) }), "Initialize evidence alone cannot authorize an AudioHost recommendation");
var profileTemp = Path.Combine(Path.GetTempPath(), $"klidan-profile-tests-{Guid.NewGuid():N}");
try
{
    var savedProfile = DeviceConfigurationProfileStorage.SaveLocal(captureProfile, profileTemp);
    Check(File.Exists(savedProfile) && DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(savedProfile)).EndpointBindings.Capture?.EndpointId == profileEndpoint.EndpointId, "local profile save creates a validated atomic file");
    Throws<IOException>(() => DeviceConfigurationProfileStorage.SaveLocal(captureProfile, profileTemp), "local profile save refuses to overwrite an existing profile");
}
finally { if (Directory.Exists(profileTemp)) Directory.Delete(profileTemp, recursive: true); }

var vm = new MainViewModel();
Check(vm.Session is null && vm.Events.Count == 0 && vm.Meter is null, "no session, meter, or fake error event on launch");
Check(vm.WorkflowStep == OperatorStep.Device && !vm.DeveloperToolsOpen && vm.WorkerState.State == WorkerConnectionState.Disconnected, "first launch starts on Device without connecting a worker or opening Developer Tools");
Check(vm.InspectorProvenanceText == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWindowsProvenance") && vm.InspectorTitle == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWindowsDeviceTitle"), "empty first-launch inspector is localized Windows metadata context, not DEMO provenance");
vm.NavigateOperatorStep(OperatorStep.Test);
Check(vm.WorkflowStep == OperatorStep.Device, "test step refuses to proceed without a selected current real Windows endpoint");
Check(vm.CaptureEndpoints.All(e => e.IsDemo) && vm.RenderEndpoints.All(e => e.IsDemo), "all endpoint fixtures are synthetic");
Check(!vm.CanSaveDeviceProfile, "local demo or first-launch state cannot create a real device profile");
Check(vm.SelectedCaptureEndpoint is null && vm.SelectedRenderEndpoint is null, "no endpoint is preselected on launch");
Check(vm.CaptureEndpoints[1].IsAvailable == false, "disabled endpoint is not marked available");
Check(vm.Formats.Count == 12 && vm.Formats.SelectMany(f => new[] { f.DefaultPeriod, f.MinimumPeriod }).Count() == 24, "12 candidates and 24 period outcomes");
Check(vm.Formats.SelectMany(f => new[] { f.DefaultPeriod.Status, f.MinimumPeriod.Status }).Distinct().Count() >= 5, "matrix shows distinct supported, unsupported, failed and unknown/not-tested states");
var statusLabels = new Dictionary<FormatStatus, string>
{
    [FormatStatus.SupportedReported] = KlidanAudioProbe.Gui.Core.Localization.Get("VmFormatStatusSupported"), [FormatStatus.Unsupported] = KlidanAudioProbe.Gui.Core.Localization.Get("VmFormatStatusUnsupported"), [FormatStatus.Failed] = KlidanAudioProbe.Gui.Core.Localization.Get("VmFormatStatusFailed"),
    [FormatStatus.Unknown] = KlidanAudioProbe.Gui.Core.Localization.Get("VmFormatStatusUnknown"), [FormatStatus.NotTested] = KlidanAudioProbe.Gui.Core.Localization.Get("VmFormatStatusNotTested"), [FormatStatus.Initialized] = KlidanAudioProbe.Gui.Core.Localization.Get("VmFormatStatusInitialized"), [FormatStatus.Ready] = KlidanAudioProbe.Gui.Core.Localization.Get("VmFormatStatusReady")
};
Check(statusLabels.All(pair => MainViewModel.StatusLabel(pair.Key) == pair.Value), "format matrix uses localized concise status labels with text and symbols");
Check(new PeriodEvidence(FormatStatus.Unknown, null).StatusCode == "UNKNOWN" && new PeriodEvidence(FormatStatus.NotTested, null).StatusCode == "NOT_TESTED", "UNKNOWN and NOT_TESTED remain distinct machine-readable states");
var accessibleEvidence = new PeriodEvidence(FormatStatus.Initialized, "S_OK", "S_OK", "INITIALIZE_ONLY").AccessibleText;
Check(accessibleEvidence.Contains("INITIALIZED", StringComparison.Ordinal) && accessibleEvidence.Contains("IsFormatSupported HRESULT: S_OK", StringComparison.Ordinal) && accessibleEvidence.Contains("Initialize HRESULT: S_OK", StringComparison.Ordinal) && accessibleEvidence.Contains("INITIALIZE_ONLY", StringComparison.Ordinal), "accessible format-cell text preserves status, HRESULTs and provenance");
var passiveEvidence = vm.Formats.SelectMany(f => new[] { f.DefaultPeriod, f.MinimumPeriod }).First(p => p.Status == FormatStatus.SupportedReported);
Check(passiveEvidence.Provenance == "PASSIVE_QUERY" && passiveEvidence.AccessibleText.Contains("S_OK", StringComparison.Ordinal), "passive format results expose provenance and raw query HRESULT");
Check(((Style)app.Resources[typeof(DataGrid)]).Setters.OfType<Setter>().Any(s => s.Property == DataGrid.RowHeightProperty && Equals(s.Value, 42d)), "DataGrid uses a consistent compact row height");
var configuredRepoRoot = Environment.GetEnvironmentVariable("KLIDAN_TEST_REPOSITORY_ROOT");
var repoRoot = configuredRepoRoot is null ? new DirectoryInfo(AppContext.BaseDirectory) : new DirectoryInfo(configuredRepoRoot);
while (configuredRepoRoot is null && repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "src/gui/KlidanAudioProbe.Gui.App/MainWindow.xaml"))) repoRoot = repoRoot.Parent;
if (repoRoot is null) throw new InvalidOperationException("Could not locate repository source for static WPF checks.");
var mainWindowXaml = File.ReadAllText(Path.Combine(repoRoot.FullName, "src/gui/KlidanAudioProbe.Gui.App/MainWindow.xaml"));
var appXaml = File.ReadAllText(Path.Combine(repoRoot.FullName, "src/gui/KlidanAudioProbe.Gui.App/App.xaml"));
var localizedXamlFiles = Directory.GetFiles(Path.Combine(repoRoot.FullName, "src/gui"), "*.xaml", SearchOption.AllDirectories);
var requiredLocalizationKeys = localizedXamlFiles.SelectMany(path => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), @"\{loc:Loc\s+([A-Za-z0-9_]+)").Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups[1].Value)).Distinct(StringComparer.Ordinal).ToArray();
foreach (var resourceFile in new[] { "UiResources.resx", "UiResources.ru-RU.resx" })
{
    var resourcePath = Path.Combine(repoRoot.FullName, "src/gui/KlidanAudioProbe.Gui.Core", resourceFile);
    var resourceKeys = System.Xml.Linq.XDocument.Load(resourcePath).Root!.Elements("data").Select(element => (string?)element.Attribute("name")).Where(name => name is not null).ToHashSet(StringComparer.Ordinal);
    var missing = requiredLocalizationKeys.Where(key => !resourceKeys.Contains(key)).ToArray();
    Check(missing.Length == 0, $"all {requiredLocalizationKeys.Length} XAML localization keys exist in {resourceFile}" + (missing.Length == 0 ? string.Empty : $": {string.Join(", ", missing)}"));
}
var mainWindowCode = File.ReadAllText(Path.Combine(repoRoot.FullName, "src/gui/KlidanAudioProbe.Gui.App/MainWindow.xaml.cs"));
using (var profileSchema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repoRoot.FullName, "docs/schemas/device-configuration-profile-v1.schema.json"))))
using (var streamSchema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repoRoot.FullName, "docs/schemas/device-configuration-profile-v2.schema.json"))))
using (var duplexSchema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repoRoot.FullName, "docs/schemas/device-configuration-profile-v3.schema.json"))))
using (var sustainedSchema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repoRoot.FullName, "docs/schemas/device-configuration-profile-v4.schema.json"))))
using (var duplexReportSchemaV1 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repoRoot.FullName, "docs/schemas/wasapi-duplex-validation-v1.schema.json"))))
using (var duplexReportSchemaV2 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repoRoot.FullName, "docs/schemas/wasapi-duplex-validation-v2.schema.json"))))
    Check(profileSchema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 1 && streamSchema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 2 && streamSchema.RootElement.GetProperty("required").EnumerateArray().Any(v => v.GetString() == "streamObservations") && duplexSchema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 3 && duplexSchema.RootElement.GetProperty("required").EnumerateArray().Any(v => v.GetString() == "duplexObservations") && sustainedSchema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 4 && sustainedSchema.RootElement.GetProperty("required").EnumerateArray().Any(v => v.GetString() == "sustainedSelection") && !duplexSchema.RootElement.GetProperty("additionalProperties").GetBoolean() && duplexReportSchemaV1.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 1 && duplexReportSchemaV2.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 2 && duplexReportSchemaV2.RootElement.GetProperty("properties").GetProperty("reportType").GetProperty("const").GetString() == "wasapi-duplex-validation" && new[] { "packetDrainLimitHits", "intermediateWaitTimeouts", "longEventIntervals" }.All(n => duplexReportSchemaV2.RootElement.GetProperty("$defs").GetProperty("side").GetProperty("required").EnumerateArray().Any(v => v.GetString() == n)), "Duplex report schemas v1/v2, strict v3 profile and sustained v4 profile schemas preserve their separate contracts");
using (var stableSchema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repoRoot.FullName, "docs/schemas/device-configuration-profile-v5.schema.json"))))
    Check(stableSchema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 5 && stableSchema.RootElement.GetProperty("required").EnumerateArray().Any(v => v.GetString() == "stableShortSelection") && stableSchema.RootElement.GetProperty("properties").GetProperty("duplexObservations").GetProperty("maxItems").GetInt32() == 0 && stableSchema.RootElement.GetProperty("properties").GetProperty("evidence").GetProperty("minItems").GetInt32() == 0 && stableSchema.RootElement.GetProperty("properties").GetProperty("stableShortSelection").GetProperty("properties").TryGetProperty("deviceContainerId", out _), "v5 accepts a direct V4 evidence reference without V3 Duplex rows while preserving its versioned profile schema");
    using var matrixSchema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repoRoot.FullName, "docs", "schemas", "v2-auto-short-candidate-matrix-v2.schema.json")));
    Check(matrixSchema.RootElement.GetProperty("properties").GetProperty("evidenceVersion").GetProperty("enum").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(new[] { 3, 4 }) && matrixSchema.RootElement.GetProperty("required").EnumerateArray().Any(v => v.GetString() == "diagnosticOnly") && matrixSchema.RootElement.GetProperty("$defs").GetProperty("result").GetProperty("properties").TryGetProperty("duplexEvidence", out _), "versioned V4 matrix schema preserves Profile v5 source reports and adds explicit one-candidate diagnostic evidence");
var profileSource = File.ReadAllText(Path.Combine(repoRoot.FullName, "src/gui/KlidanAudioProbe.Gui.Core/DeviceConfigurationProfile.cs"));
Check(!profileSource.Contains("IAudioClient", StringComparison.Ordinal) && !profileSource.Contains("IMMDevice", StringComparison.Ordinal) && !profileSource.Contains("Wasapi", StringComparison.Ordinal), "profile creation and validation are pure data operations without hardware APIs");
Check(mainWindowXaml.Contains("x:Name=\"OperatorTestPanel\"", StringComparison.Ordinal) && mainWindowXaml.Contains("ItemsSource=\"{Binding RealFormatRows}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Command=\"{Binding QueryRealFormatsCommand}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Command=\"{Binding RequestRealInitializeCommand}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Command=\"{Binding ConfirmRealInitializeCommand}\"", StringComparison.Ordinal) && !mainWindowXaml.Contains("Binding RealFormatMatrix.EndpointId", StringComparison.Ordinal), "operator test step exposes the real query and fresh-consent Initialize flow without endpoint IDs");
Check(mainWindowXaml.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", StringComparison.Ordinal), "local format table does not expose a horizontal scrollbar");
Check(mainWindowXaml.Contains("CommandParameter=\"Device\"", StringComparison.Ordinal) && mainWindowXaml.Contains("CommandParameter=\"Test\"", StringComparison.Ordinal) && mainWindowXaml.Contains("CommandParameter=\"Result\"", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"OperatorResultPanel\"", StringComparison.Ordinal) && mainWindowXaml.Contains("{loc:Loc WorkerFakeSynthetic}", StringComparison.Ordinal), "three-step operator flow and isolated developer tools are present");
Check(mainWindowXaml.Contains("{loc:Loc RealWindowsDiagnosticsHeading}", StringComparison.Ordinal) && mainWindowXaml.Contains("{loc:Loc SyntheticFixturesHeading}", StringComparison.Ordinal) && mainWindowXaml.Contains("DuplexRepeatabilityAvailabilityReason", StringComparison.Ordinal) && mainWindowXaml.Contains("VerticalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal), "real diagnostics, synthetic fixtures, repeatability readiness and inspector scrolling have explicit UI sections");
Check(mainWindowXaml.Contains("Content=\"{loc:Loc DiscoverPeriods}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("PeriodDiscoveryAvailabilityReason", StringComparison.Ordinal) && mainWindowXaml.Contains("PeriodDiscoveryConsentPending", StringComparison.Ordinal) && mainWindowXaml.Contains("PeriodDiscoveryMatrixText", StringComparison.Ordinal) && mainWindowXaml.Contains("PrepareCustomPeriodDuplexButton", StringComparison.Ordinal) && mainWindowXaml.Contains("PrepareCustomRepeatabilityCommand", StringComparison.Ordinal), "C4.2A/C4.2B custom Duplex controls expose readiness, explicit preparation and bounded repeatability bindings");
Check(mainWindowXaml.Contains("EvidenceLibraryEntries", StringComparison.Ordinal) && mainWindowXaml.Contains("EvidenceLibraryDeviceFilter", StringComparison.Ordinal) && mainWindowXaml.Contains("EvidenceLibraryProvenanceFilter", StringComparison.Ordinal) && mainWindowXaml.Contains("OpenEvidenceLibraryLocationCommand", StringComparison.Ordinal) && mainWindowXaml.Contains("RefreshEvidenceLibraryCommand", StringComparison.Ordinal), "E1.1 Evidence Library exposes bounded local filtering, details, original location and explicit refresh bindings");
Check(mainWindowXaml.Contains("AutomaticScanCandidateSummary", StringComparison.Ordinal) && mainWindowXaml.Contains("AutomaticScanStagePlanText", StringComparison.Ordinal), "S1 normal scan exposes candidate coverage and staged limitations");
Check(mainWindowXaml.Contains("EvidenceLibraryComparisons", StringComparison.Ordinal) && mainWindowXaml.Contains("EvidenceLibraryComparisonMode", StringComparison.Ordinal) && mainWindowXaml.Contains("{loc:Loc NormalizedDuplexComparisons}", StringComparison.Ordinal), "E1.2 GUI exposes exact and exploratory normalized Duplex comparison states");
var customCandidate = new ExclusivePeriodCandidateDto(40_000, 192, 40_000, "ACCEPTED", "BOTH_EXACT", "0x00000000", null, null, 192, 40_000, "RELEASE_ATTEMPTED", 1);
ExclusivePeriodDiscoveryReportDto CustomReport(string id, string endpoint, string direction) => new(1, "wasapi-exclusive-period-discovery", id, id, "2026-11-01T12:00:00Z", "REAL_WASAPI_INITIALIZE_ONLY", endpoint, direction, 48_000, "PCM16", 2, 30_000, 100_000, [customCandidate]);
var customSeries = new PeriodDiscoverySeriesResult("COMPLETED", [CustomReport("capture-report", "capture-current", "CAPTURE"), CustomReport("render-report", "render-current", "RENDER")], [], "BOTH_EXACT", null, "SAVED_AND_RELOADED");
var selectedCustom = CustomPeriodDuplexEvidence.Select(customSeries, "capture-current", "render-current", 192);
Check(selectedCustom.AuthorizationValue == "CUSTOM:40000:192", "C4.2B.1 accepts only matching REAL BOTH_EXACT evidence and preserves exact period geometry");
Throws<InvalidDataException>(() => { _ = CustomPeriodDuplexEvidence.Select(customSeries with { Lifecycle = "UNKNOWN" }, "capture-current", "render-current", 192); }, "C4.2B.1 rejects UNKNOWN discovery evidence fail-closed");
Throws<InvalidDataException>(() => { _ = CustomPeriodDuplexEvidence.Select(customSeries with { Reports = customSeries.Reports.Select(r => r with { Provenance = "TEST_FAKE_PERIOD_BACKEND" }).ToArray() }, "capture-current", "render-current", 192); }, "C4.2B.1 keeps synthetic and REAL period evidence isolated");
using var duplexJson = JsonDocument.Parse("{\"provenance\":\"REAL_WASAPI_DUPLEX\"}");
var customRoot = Path.Combine(Path.GetTempPath(), "klidan-custom-period-test-" + Guid.NewGuid().ToString("N"));
var customPath = CustomPeriodDuplexReportStorage.Save(new CustomPeriodDuplexResult("COMPLETED", Guid.NewGuid().ToString("D"), selectedCustom.AuthorizationValue, null, "NOT_SAVED", duplexJson.RootElement.Clone()), customRoot);
Check(JsonDocument.Parse(File.ReadAllBytes(customPath)).RootElement.GetProperty("report").GetProperty("provenance").GetString() == "REAL_WASAPI_DUPLEX", "C4.2B.1 persists REAL report provenance unchanged and reads it back");
Directory.Delete(customRoot, true);
Check(mainWindowXaml.Contains("ShowNormalPairSelection", StringComparison.Ordinal) && mainWindowXaml.Contains("{loc:Loc AmbiguousCaptureRender}", StringComparison.Ordinal) && !mainWindowXaml.Contains("в Developer Tools. Loopback", StringComparison.Ordinal), "ambiguous physical pairing is exposed in the normal device screen while manual tools remain isolated");
Check(mainWindowXaml.Contains("Content=\"{loc:Loc SaveKlidanProfile}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("IsEnabled=\"{Binding CanSaveDeviceProfile}\"", StringComparison.Ordinal) && mainWindowCode.Contains("DeviceConfigurationProfileStorage.SaveLocal(profile, _profileDirectory)", StringComparison.Ordinal) && mainWindowCode.Contains("_vm.RecordDeviceProfileSaved(path, profile)", StringComparison.Ordinal), "real operator result exposes a current-evidence-gated local profile save action with schema/path reporting");
Check(appXaml.Contains("<ContentPresenter x:Name=\"SelectionPresenter\" Grid.Column=\"0\"", StringComparison.Ordinal) && appXaml.Contains("<ColumnDefinition Width=\"34\"/>", StringComparison.Ordinal), "selected ComboBox text is constrained to the area before the visible arrow");
Check(appXaml.Contains("HorizontalScrollBarVisibility=\"Disabled\"", StringComparison.Ordinal) && appXaml.Contains("CanContentScroll=\"False\"", StringComparison.Ordinal) && appXaml.Contains("MaxHeight=\"320\"", StringComparison.Ordinal), "long dropdown choices remain reachable in a bounded-height pixel-scrolling popup");
Check(mainWindowXaml.Contains("x:Name=\"WorkerFakeSettingsGrid\"", StringComparison.Ordinal) && mainWindowXaml.Contains("WorkerFakeSettingsGrid_SizeChanged", StringComparison.Ordinal) && !mainWindowXaml.Contains("StackPanel Orientation=\"Horizontal\" Margin=\"0,8,0,0\"", StringComparison.Ordinal), "Worker Fake controls use the responsive labeled grid, not content-sized horizontal rows");
Check(new[] { "WorkerDurationSetting", "WorkerPeriodSetting", "WorkerMmcssSetting", "WorkerScenarioSetting" }.All(name => mainWindowXaml.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal)) && mainWindowXaml.Contains("MinWidth=\"160\"", StringComparison.Ordinal), "all four Worker Fake selectors have labeled, minimum-width grid cells");
Check(mainWindowXaml.Split("<ComboBox ", StringSplitOptions.None).Length - 1 >= 23 && !mainWindowXaml.Contains("<ComboBox Style=", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"SavedDeviceProfileSelector\"", StringComparison.Ordinal), "all dropdowns, including language, V2 routing and saved profile selection, use the shared ComboBox presentation");
Check(mainWindowXaml.IndexOf("{loc:Loc ScannerSectionHeading}", StringComparison.Ordinal) < mainWindowXaml.IndexOf("x:Name=\"PhysicalDeviceSelector\"", StringComparison.Ordinal) && mainWindowXaml.IndexOf("x:Name=\"PhysicalDeviceSelector\"", StringComparison.Ordinal) < mainWindowXaml.IndexOf("x:Name=\"V2RouteSelectionCard\"", StringComparison.Ordinal) && mainWindowXaml.IndexOf("x:Name=\"V2RouteSelectionCard\"", StringComparison.Ordinal) < mainWindowXaml.IndexOf("x:Name=\"DeviceScanButton\"", StringComparison.Ordinal) && mainWindowXaml.IndexOf("x:Name=\"DeviceScanButton\"", StringComparison.Ordinal) < mainWindowXaml.IndexOf("x:Name=\"AutomaticScanConsentCard\"", StringComparison.Ordinal) && mainWindowXaml.IndexOf("x:Name=\"AutomaticScanConsentCard\"", StringComparison.Ordinal) < mainWindowXaml.IndexOf("{loc:Loc ProgressSectionHeading}", StringComparison.Ordinal) && mainWindowXaml.IndexOf("{loc:Loc ProgressSectionHeading}", StringComparison.Ordinal) < mainWindowXaml.IndexOf("x:Name=\"AutomaticScanProgressCard\"", StringComparison.Ordinal) && mainWindowXaml.IndexOf("x:Name=\"AutomaticScanProgressCard\"", StringComparison.Ordinal) < mainWindowXaml.IndexOf("{loc:Loc ProfilesSectionHeading}", StringComparison.Ordinal) && mainWindowXaml.IndexOf("{loc:Loc ProfilesSectionHeading}", StringComparison.Ordinal) < mainWindowXaml.IndexOf("x:Name=\"CheckSavedProfileBindingButton\"", StringComparison.Ordinal) && mainWindowXaml.IndexOf("x:Name=\"CheckSavedProfileBindingButton\"", StringComparison.Ordinal) < mainWindowXaml.IndexOf("x:Name=\"HistoricalRecoveryCard\"", StringComparison.Ordinal) && mainWindowXaml.IndexOf("x:Name=\"HistoricalRecoveryCard\"", StringComparison.Ordinal) < mainWindowXaml.IndexOf("{loc:Loc ReportsSectionHeading}", StringComparison.Ordinal) && mainWindowXaml.IndexOf("{loc:Loc ReportsSectionHeading}", StringComparison.Ordinal) < mainWindowXaml.IndexOf("x:Name=\"LatestAutomaticReportCard\"", StringComparison.Ordinal), "operator XAML orders scan device/route/launch/consent, then progress status, profile binding/recovery, and report content");
Check(mainWindowXaml.Contains("{loc:Loc HistoricalRecoveryTitle}", StringComparison.Ordinal) && mainWindowXaml.Contains("{loc:Loc CheckOldMeasurements}", StringComparison.Ordinal) && mainWindowXaml.Contains("{loc:Loc SaveRecoveredProfile}", StringComparison.Ordinal) && mainWindowXaml.Contains("HistoricalRecoveryConfirmation", StringComparison.Ordinal), "historical recovery UI exposes selection, preview and explicit confirmation without audio controls");
Check(mainWindowXaml.Contains("AutomationProperties.HelpText=\"{Binding DefaultPeriod.DisplayAccessibleText}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("AutomationProperties.HelpText=\"{Binding MinimumPeriod.DisplayAccessibleText}\"", StringComparison.Ordinal), "both period cells bind localized accessibility evidence");
Check(mainWindowXaml.Contains("ItemsSource=\"{Binding CaptureChannelBanks}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("ItemsSource=\"{Binding RenderChannelBanks}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("ItemsSource=\"{Binding VisibleMeterChannels}\"", StringComparison.Ordinal), "Capture and Render expose dynamic keyboard-accessible channel banks");
Check(mainWindowXaml.Contains("Command=\"{Binding ConnectWorkerCommand}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Command=\"{Binding QueryWorkerEndpointsCommand}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Binding WorkerStateDisplayMessage", StringComparison.Ordinal), "Diagnostics provides deliberate worker connect and endpoint query controls with a localized display message");
Check(mainWindowXaml.Contains("x:Name=\"WorkerChannelCountText\" Text=\"{Binding WorkerChannelSummary}\"", StringComparison.Ordinal), "Diagnostics channel summary uses a real ViewModel text binding");
Check(mainWindowXaml.Contains("AutomationProperties.Name=\"{loc:Loc WindowControls}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("ColumnDefinition Width=\"5\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Style=\"{StaticResource WindowChromeButton}\"", StringComparison.Ordinal), "titlebar uses centered dedicated controls with explicit separation and shared right margin");
Check(mainWindowXaml.Contains("StrokeStartLineCap=\"Square\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Value=\"Maximized\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Value=\"M 6,2 L 12,2 12,8 M 9,5 L 3,5 3,12 9,12 Z\"", StringComparison.Ordinal), "titlebar uses centered vector icons and switches maximize to restore geometry");
Check(mainWindowXaml.Contains("Click=\"Minimize_Click\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Click=\"Maximize_Click\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Click=\"Close_Click\"", StringComparison.Ordinal) && mainWindowCode.Contains("if (e.ClickCount == 2) Maximize_Click(sender, e); else DragMove();", StringComparison.Ordinal), "titlebar controls are wired and double-click maximizes while a normal drag moves the window");
Check(mainWindowXaml.Contains("{loc:Loc DisconnectedWorkerHistory}", StringComparison.Ordinal) && mainWindowXaml.Contains("Binding HistoricalWorkerEndpointNames", StringComparison.Ordinal), "disconnected endpoint snapshot has an explicitly historical presentation");
Check(mainWindowXaml.Contains("Visibility=\"{Binding WorkerRenderChannelVisible, Converter={StaticResource BoolVisibility}}\"", StringComparison.Ordinal), "Render channel selector is visibility-bound to endpoint direction");
Check(mainWindowXaml.Contains("Visibility=\"{Binding IsEndpointListEmpty, Converter={StaticResource BoolVisibility}}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Command=\"{Binding RestoreDevicesCommand}\"", StringComparison.Ordinal), "empty endpoint state is explicit and offers demo restore");
Check(mainWindowXaml.Contains("Visibility=\"{Binding HasEndpoints, Converter={StaticResource BoolVisibility}}\"", StringComparison.Ordinal) && appXaml.Contains("Property=\"RowHeight\" Value=\"42\"", StringComparison.Ordinal), "endpoint list hides when empty and matrix row height is fixed");
Check(mainWindowXaml.Contains("Content=\"{loc:Loc StopWorker}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Content=\"{loc:Loc StopLocal}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Visibility=\"{Binding WorkerOperationActive, Converter={StaticResource BoolVisibility}}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Binding WorkerSession.Lifecycle, Converter={StaticResource SessionStateText}", StringComparison.Ordinal), "separate worker and local Stop controls bind their own lifecycle status");

var workerFormats = new[] { 2, 8, 32, 48, 64 }.Select(n => new FormatTopologyDto($"PCM16-{n}", DemoTopologyFixtures.Independent(n))).ToArray();
var unavailableEvidence = new WindowsPropertyEvidenceDto(unchecked((int)0x80070490), "UNAVAILABLE");
var availableEvidence = new WindowsPropertyEvidenceDto(0, "AVAILABLE");
var windowsFixture = new WindowsEndpointDiscoveryDto("REAL_WINDOWS_METADATA", "COMPLETE", 0, 0, [],
[
    new("local-windows-id", availableEvidence, "Fixture Interface", availableEvidence, "CAPTURE", "ACTIVE", 1, availableEvidence, "UNKNOWN", unavailableEvidence,
        new(availableEvidence, "VALID", 1, "PCM", 2, 48000, 16, 16, "REPRESENTABLE_1_TO_64", null, "UNKNOWN"))
]);
WindowsEndpointDiscoveryValidation.Validate(windowsFixture);
var windowsPairFixture = windowsFixture with
{
    Endpoints =
    [
        windowsFixture.Endpoints[0],
        windowsFixture.Endpoints[0] with { EndpointId = "local-render-id", Direction = "RENDER", FriendlyName = "Fixture Render" }
    ]
};
var physicalPairFixture = windowsPairFixture with
{
    Endpoints = windowsPairFixture.Endpoints.Select(e => e with
    {
        ContainerId = "{PHYSICAL-A}",
        ContainerIdEvidence = availableEvidence,
        FormFactor = e.Direction == "CAPTURE" ? "MICROPHONE" : "LINE_LEVEL",
        FormFactorEvidence = availableEvidence
    }).ToArray()
};
var physicalWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = physicalPairFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true };
var physicalWorkflow = new GuiAutomaticScanWorkflow(physicalPairFixture);
var physicalVm = new MainViewModel(worker: physicalWorker, automaticScanWorkflow: physicalWorkflow);
physicalVm.AutomaticScanEngine = "V1_COMPATIBLE";
physicalWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for physical selector fixture."));
var mixedActiveDiscovery = physicalPairFixture with { Endpoints = physicalPairFixture.Endpoints.Select(e => e with { DeviceState = e.Direction == "CAPTURE" ? "ACTIVE" : "DISABLED" }).Append(physicalPairFixture.Endpoints[0] with { EndpointId = "not-present-endpoint", DeviceState = "NOT_PRESENT", ContainerId = "{INACTIVE-ONLY}" }).ToArray() };
var activeFilterWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = mixedActiveDiscovery, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true };
var activeFilterWorkflow = new GuiAutomaticScanWorkflow(mixedActiveDiscovery);
var activeFilterVm = new MainViewModel(worker: activeFilterWorker, automaticScanWorkflow: activeFilterWorkflow) { AutomaticScanEngine = "V1_COMPATIBLE" };
activeFilterWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for active endpoint filtering fixture."));
activeFilterVm.DiscoverWindowsEndpointsCommand.Execute(null);
var activeFilterReady = PumpUntil(() => activeFilterVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
Check(activeFilterReady && activeFilterVm.PhysicalDeviceGroups.Count == 1 && activeFilterVm.PhysicalDeviceGroups[0].DisplayName == "Fixture Interface" && activeFilterVm.PhysicalDeviceGroupDetails.Count == 3 && activeFilterVm.PhysicalDeviceGroups[0].DirectionSummary == Localization.Get("VmPhysicalGroupInput") && activeFilterVm.DuplexRenderEndpoints.Count == 0, $"operator picker includes only ACTIVE endpoint groups without numbering inactive same-name groups while diagnostics retain disabled and NOT_PRESENT endpoints [ready={activeFilterReady}, groups={activeFilterVm.PhysicalDeviceGroups.Count}, name={activeFilterVm.PhysicalDeviceGroups.FirstOrDefault()?.DisplayName}, details={activeFilterVm.PhysicalDeviceGroupDetails.Count}, direction={activeFilterVm.PhysicalDeviceGroups.FirstOrDefault()?.DirectionSummary}, render={activeFilterVm.DuplexRenderEndpoints.Count}]");
activeFilterVm.SelectedPhysicalDeviceGroup = activeFilterVm.PhysicalDeviceGroups.Single();
activeFilterVm.PrepareAutomaticScanCommand.Execute(null);
Check(PumpUntil(() => activeFilterVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2)), "active single Capture prepares a scan consent before endpoint-loss refresh");
activeFilterWorker.WindowsDiscovery = mixedActiveDiscovery with { Endpoints = mixedActiveDiscovery.Endpoints.Select(e => e with { DeviceState = "DISABLED" }).ToArray() };
activeFilterWorkflow.Discovery = activeFilterWorker.WindowsDiscovery;
Check(activeFilterVm.DiscoverWindowsEndpointsCommand.CanExecute(null), "endpoint discovery refresh remains available while consent is pending so stale authorization can be invalidated");
activeFilterVm.DiscoverWindowsEndpointsCommand.Execute(null);
var lostEndpointRefresh = PumpUntil(() => !activeFilterVm.WindowsDiscoveryLoading, TimeSpan.FromSeconds(2));
Check(lostEndpointRefresh && activeFilterVm.PhysicalDeviceGroups.Count == 0 && activeFilterVm.SelectedPhysicalDeviceGroup is null && activeFilterVm.SelectedWindowsEndpoint is null && !activeFilterVm.AutomaticScanConsentPending && activeFilterVm.AutomaticScanAvailabilityReason == Localization.Get("VmNoActiveAudioDevices"), $"after refresh removes all active endpoints, picker and selection clear, consent resets, and the scan reason explains no active devices [done={lostEndpointRefresh}, groups={activeFilterVm.PhysicalDeviceGroups.Count}, group={activeFilterVm.SelectedPhysicalDeviceGroup?.GroupId}, endpoint={activeFilterVm.SelectedWindowsEndpoint?.EndpointId}, consent={activeFilterVm.AutomaticScanConsentPending}, reason={activeFilterVm.AutomaticScanAvailabilityReason}, status={activeFilterVm.WindowsDiscoveryStatusText}]");
var manyDevicesDiscovery = physicalPairFixture with { Endpoints = Enumerable.Range(1, 12).Select(i => physicalPairFixture.Endpoints[0] with { EndpointId = $"choice-{i}", FriendlyName = "USB Interface", ContainerId = $"{{CHOICE-{i}}}" }).ToArray() };
var manyDevicesWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = manyDevicesDiscovery, AdvertiseWindowsDiscovery = true };
var manyDevicesVm = new MainViewModel(worker: manyDevicesWorker); manyDevicesWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for picker scrolling fixture."));
manyDevicesVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => manyDevicesVm.PhysicalDeviceGroups.Count == 12, TimeSpan.FromSeconds(2)), "large device picker fixture discovers twelve separately identified groups");
manyDevicesVm.SelectedPhysicalDeviceGroup = manyDevicesVm.PhysicalDeviceGroups[0];
var manyDevicesWindow = new MainWindow(manyDevicesVm) { Width = 1280, Height = 720, ShowActivated = false }; manyDevicesWindow.Show(); manyDevicesWindow.UpdateLayout();
var pickerCombo = (ComboBox)manyDevicesWindow.FindName("PhysicalDeviceSelector");
pickerCombo.ApplyTemplate();
var physicalPopup = (Popup)pickerCombo.Template.FindName("PART_Popup", pickerCombo);
pickerCombo.IsDropDownOpen = true; Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); manyDevicesWindow.UpdateLayout();
System.Windows.DependencyObject popupFind(System.Windows.DependencyObject parent, Type wanted)
{
    if (wanted.IsInstanceOfType(parent)) return parent;
    for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        if (popupFind(System.Windows.Media.VisualTreeHelper.GetChild(parent, i), wanted) is { } found) return found;
    return null!;
}
var popupScroll = (ScrollViewer)popupFind((System.Windows.DependencyObject)physicalPopup.Child, typeof(ScrollViewer));
pickerCombo.SelectedIndex = 0;
var initialOffset = popupScroll.VerticalOffset;
var hoveredPickerRow = (ComboBoxItem)popupFind((System.Windows.DependencyObject)physicalPopup.Child, typeof(ComboBoxItem));
hoveredPickerRow.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.MouseWheelEvent });
manyDevicesWindow.UpdateLayout();
var wheelDownOffset = popupScroll.VerticalOffset;
hoveredPickerRow.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.MouseWheelEvent }); manyDevicesWindow.UpdateLayout();
Check(!popupScroll.CanContentScroll && popupScroll.ScrollableHeight > 0 && wheelDownOffset > initialOffset && popupScroll.VerticalOffset < wheelDownOffset && pickerCombo.SelectedIndex == 0, "wheel over a picker row scrolls both directions in pixel mode without changing selection");
popupScroll.ScrollToVerticalOffset(popupScroll.ScrollableHeight); manyDevicesWindow.UpdateLayout();
Check(popupScroll.VerticalOffset == popupScroll.ScrollableHeight && manyDevicesVm.PhysicalDeviceGroups[11].DisplayName.Length > 0 && pickerCombo.SelectedIndex == 0, "the last of twelve picker entries is reachable by scrolling without selecting it");
var popupKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pickerCombo), Environment.TickCount, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent };
pickerCombo.RaiseEvent(popupKey); manyDevicesWindow.UpdateLayout();
Check(pickerCombo.SelectedItem is PhysicalDeviceGroupDto selectedAfterKey && manyDevicesVm.PhysicalDeviceGroups.Contains(selectedAfterKey), "keyboard navigation remains available within the device picker");
var enterKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pickerCombo), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
pickerCombo.RaiseEvent(enterKey); manyDevicesWindow.UpdateLayout();
Check(!pickerCombo.IsDropDownOpen && pickerCombo.SelectedIndex is >= 0 and < 12, "Enter closes the picker with a valid keyboard-selected item");
var selectionBeforeEscape = pickerCombo.SelectedIndex; pickerCombo.IsDropDownOpen = true;
var escapeKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pickerCombo), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
pickerCombo.RaiseEvent(escapeKey); manyDevicesWindow.UpdateLayout();
Check(!pickerCombo.IsDropDownOpen && pickerCombo.SelectedIndex == selectionBeforeEscape, "Escape closes the picker without committing a different item");
Check(new[] { (Width: 1920d, Height: 1080d, Dpi: 100), (Width: 1536d, Height: 864d, Dpi: 125), (Width: 1280d, Height: 720d, Dpi: 150) }.All(size => { manyDevicesWindow.Width = size.Width; manyDevicesWindow.Height = size.Height; manyDevicesWindow.UpdateLayout(); return pickerCombo.ActualWidth > 0 && pickerCombo.ActualWidth <= manyDevicesWindow.ActualWidth && pickerCombo.ActualHeight > 0; }), "device picker remains reachable at Full HD effective work areas for 100%, 125% and 150% scaling");
manyDevicesWindow.Height = 720; manyDevicesWindow.Top = SystemParameters.WorkArea.Bottom - manyDevicesWindow.Height; manyDevicesWindow.UpdateLayout(); pickerCombo.IsDropDownOpen = true; Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); manyDevicesWindow.UpdateLayout();
var edgePopup = (FrameworkElement)physicalPopup.Child; var edgePoint = manyDevicesWindow.PointFromScreen(edgePopup.PointToScreen(new Point(0, 0))); var edgeBounds = new Rect(edgePoint, new Size(edgePopup.ActualWidth, edgePopup.ActualHeight));
Check(edgeBounds.Top >= SystemParameters.WorkArea.Top && edgeBounds.Bottom <= SystemParameters.WorkArea.Bottom, "picker popup flips or fits within the WorkArea when opened near its lower edge");
var screenshotDirectory = Environment.GetEnvironmentVariable("KLIDAN_UI_SCREENSHOT_DIR");
if (!string.IsNullOrWhiteSpace(screenshotDirectory))
{
    Directory.CreateDirectory(screenshotDirectory);
    manyDevicesWindow.Width = 1280; manyDevicesWindow.Height = 720; pickerCombo.IsDropDownOpen = false; manyDevicesWindow.UpdateLayout();
    void SavePng(string path, System.Windows.Media.Visual visual, int width, int height)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
    SavePng(Path.Combine(screenshotDirectory, "device-picker-closed.png"), manyDevicesWindow, (int)manyDevicesWindow.ActualWidth, (int)manyDevicesWindow.ActualHeight);
    pickerCombo.IsDropDownOpen = true; Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); manyDevicesWindow.UpdateLayout();
    var popupChild = (System.Windows.FrameworkElement)physicalPopup.Child; var popupPoint = manyDevicesWindow.PointFromScreen(popupChild.PointToScreen(new Point(0, 0)));
    var baseImage = new System.Windows.Media.Imaging.RenderTargetBitmap((int)manyDevicesWindow.ActualWidth, (int)manyDevicesWindow.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); baseImage.Render(manyDevicesWindow);
    var popupImage = new System.Windows.Media.Imaging.RenderTargetBitmap((int)popupChild.ActualWidth, (int)popupChild.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); popupImage.Render(popupChild);
    var combined = new System.Windows.Media.DrawingVisual(); using (var dc = combined.RenderOpen()) { dc.DrawImage(baseImage, new Rect(0, 0, baseImage.Width, baseImage.Height)); dc.DrawImage(popupImage, new Rect(popupPoint.X, popupPoint.Y, popupChild.ActualWidth, popupChild.ActualHeight)); }
    SavePng(Path.Combine(screenshotDirectory, "device-picker-open.png"), combined, (int)manyDevicesWindow.ActualWidth, (int)manyDevicesWindow.ActualHeight);
}
manyDevicesWindow.Close();
var physicalWindow = new MainWindow(physicalVm) { Width = 1280, Height = 800, ShowActivated = false };
physicalWindow.Show(); physicalWindow.UpdateLayout();
var inspectorPane = (Border)physicalWindow.FindName("Inspector");
Check(inspectorPane.Visibility == Visibility.Collapsed, "operator scan screen hides the technical inspector by default");
physicalVm.DeveloperToolsOpen = true; physicalWindow.UpdateLayout();
Check(inspectorPane.Visibility == Visibility.Visible, "technical inspector remains available in Developer Tools");
physicalVm.DeveloperToolsOpen = false; physicalWindow.UpdateLayout();
physicalVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => physicalVm.PhysicalDeviceGroups.Count == 1, TimeSpan.FromSeconds(2)), "physical selector discovery exposes one confirmed group");
physicalWindow.UpdateLayout();
var physicalSelector = (ComboBox)physicalWindow.FindName("PhysicalDeviceSelector");
var physicalScanButton = (Button)physicalWindow.FindName("DeviceScanButton");
var stableGroups = physicalVm.PhysicalDeviceGroups;
physicalSelector.IsDropDownOpen = true;
physicalSelector.SelectedIndex = 0;
physicalSelector.IsDropDownOpen = false;
Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
Check(ReferenceEquals(stableGroups, physicalVm.PhysicalDeviceGroups) && ReferenceEquals(physicalSelector.SelectedItem, physicalVm.SelectedPhysicalDeviceGroup) && physicalVm.SelectedDuplexCaptureEndpoint?.EndpointId == "local-windows-id" && physicalVm.SelectedDuplexRenderEndpoint?.EndpointId == "local-render-id", "real WPF ComboBox selection remains visible and retains the exact confirmed pair without rebinding");
physicalVm.DeveloperToolsOpen = true; physicalVm.DeveloperToolsOpen = false; physicalWindow.UpdateLayout();
Check(ReferenceEquals(physicalSelector.SelectedItem, physicalVm.SelectedPhysicalDeviceGroup) && physicalScanButton.IsEnabled, "harmless ViewModel notifications preserve selection and enable one-click Scan");
Check(physicalVm.WorkflowStep == OperatorStep.Device && !((Button)physicalWindow.FindName("OperatorTestStepButton")).IsVisible && !((Button)physicalWindow.FindName("OperatorResultStepButton")).IsVisible, "normal mode hides legacy Test/Result navigation");
var v1PairDiscovery = windowsPairFixture with { Endpoints = windowsPairFixture.Endpoints.Concat([windowsPairFixture.Endpoints[1] with { EndpointId = "alternate-render-id", FriendlyName = "Alternate Render" }]).ToArray() };
var v1PairWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = v1PairDiscovery, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true };
var v1PairWorkflow = new GuiAutomaticScanWorkflow(v1PairDiscovery);
var v1PairVm = new MainViewModel(worker: v1PairWorker, automaticScanWorkflow: v1PairWorkflow) { AutomaticScanEngine = "V1_COMPATIBLE" };
v1PairWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for V1 endpoint-plan binding fixture."));
v1PairVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => v1PairVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2)), "V1 endpoint-plan binding discovery completes");
v1PairVm.AutomaticScanPairEnabled = true;
v1PairVm.SelectedDuplexCaptureEndpoint = v1PairVm.DuplexCaptureEndpoints.Single(e => e.EndpointId == "local-windows-id");
v1PairVm.SelectedDuplexRenderEndpoint = v1PairVm.DuplexRenderEndpoints.Single(e => e.EndpointId == "local-render-id");
v1PairVm.PrepareAutomaticScanCommand.Execute(null);
Check(PumpUntil(() => v1PairVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2)), "V1 exact Capture/Render plan prepares");
v1PairVm.AutomaticScanConsentChecked = true;
Check(v1PairVm.ConfirmAutomaticScanCommand.CanExecute(null), "V1 pair confirmation accepts the exact endpoint pair captured in its plan");
v1PairVm.SelectedDuplexRenderEndpoint = v1PairVm.DuplexRenderEndpoints.Single(e => e.EndpointId == "alternate-render-id");
typeof(MainViewModel).GetField("_automaticScanConsentPending", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(v1PairVm, true);
typeof(MainViewModel).GetField("_automaticScanConsentChecked", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(v1PairVm, true);
Check(!v1PairVm.ConfirmAutomaticScanCommand.CanExecute(null), "V1 confirmation refuses a Render endpoint changed after the authorized plan");
v1PairVm.ConfirmAutomaticScanCommand.Execute(null);
Check(v1PairWorkflow.RunCalls == 0, "V1 endpoint changed after preparation cannot reach the workflow handler");
bool RunV1Confirmation(string endpointId, bool pair)
{
    var discovery = v1PairDiscovery;
    var worker = new GuiStateWorkerClient([]) { WindowsDiscovery = discovery, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true };
    var workflow = new GuiAutomaticScanWorkflow(discovery);
    var viewModel = new MainViewModel(worker: worker, automaticScanWorkflow: workflow) { AutomaticScanEngine = "V1_COMPATIBLE", AutomaticScanPairEnabled = pair };
    worker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for V1 command-chain fixture."));
    viewModel.DiscoverWindowsEndpointsCommand.Execute(null);
    if (!PumpUntil(() => viewModel.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2))) return false;
    if (pair)
    {
        viewModel.SelectedDuplexCaptureEndpoint = viewModel.DuplexCaptureEndpoints.Single(e => e.EndpointId == "local-windows-id");
        viewModel.SelectedDuplexRenderEndpoint = viewModel.DuplexRenderEndpoints.Single(e => e.EndpointId == endpointId);
    }
    else viewModel.SelectedWindowsEndpoint = viewModel.WindowsEndpoints.Single(e => e.EndpointId == endpointId);
    var window = new MainWindow(viewModel) { Width = 1280, Height = 800, ShowActivated = false };
    window.Show(); window.UpdateLayout();
    viewModel.PrepareAutomaticScanCommand.Execute(null);
    if (!PumpUntil(() => viewModel.AutomaticScanConsentPending, TimeSpan.FromSeconds(2))) { window.Close(); return false; }
    ((CheckBox)window.FindName("AutomaticScanConsentCheckBox")).IsChecked = true;
    window.UpdateLayout();
    var confirm = (Button)window.FindName("ConfirmAutomaticScanButton");
    var enabled = confirm.IsEnabled;
    if (enabled) confirm.Command!.Execute(confirm.CommandParameter);
    var ran = PumpUntil(() => workflow.RunCalls == 1, TimeSpan.FromSeconds(2));
    window.Close();
    var expectedCapture = pair || endpointId == "local-windows-id" ? "local-windows-id" : null;
    var expectedRender = pair || endpointId == "local-render-id" || endpointId == "alternate-render-id" ? endpointId : null;
    return enabled && ran && workflow.PreparedPlan?.Target.Capture?.EndpointId == expectedCapture && workflow.PreparedPlan?.Target.Render?.EndpointId == expectedRender;
}
Check(RunV1Confirmation("local-windows-id", false), "V1 single Capture WPF confirmation invokes the workflow command with the planned endpoint");
Check(RunV1Confirmation("local-render-id", false), "V1 single Render WPF confirmation invokes the workflow command with the planned endpoint");
Check(RunV1Confirmation("local-render-id", true), "V1 paired Duplex WPF confirmation invokes the workflow command with the planned endpoint pair");
var bloodyEnablementDiscovery = physicalPairFixture with { Endpoints = physicalPairFixture.Endpoints.Select(e => e with { FriendlyName = e.Direction == "CAPTURE" ? "Bloody Capture" : "Bloody Render" }).ToArray() };
var bloodyEnablementWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = bloodyEnablementDiscovery, AdvertiseWindowsDiscovery = true, AdvertisePassiveFormatQuery = true, DiscoveryGeneration = 1 };
var bloodyEnablementVm = new MainViewModel(worker: bloodyEnablementWorker, automaticScanWorkflow: new GuiAutomaticScanWorkflow(bloodyEnablementDiscovery)) { AutomaticScanEngine = "V2_ADAPTIVE" };
var bloodyEnablementWindow = new MainWindow(bloodyEnablementVm) { Width = 1280, Height = 800, ShowActivated = false };
bloodyEnablementWindow.Show(); bloodyEnablementWindow.UpdateLayout();
var bloodyPrepareButton = (Button)bloodyEnablementWindow.FindName("V4PreparePlanButton");
var bloodyPrepareReason = (TextBlock)bloodyEnablementWindow.FindName("V4PrepareDisabledReasonText");
Check(!bloodyPrepareButton.IsEnabled && bloodyPrepareReason.Text == Localization.Get("VmV4PrepareWorkerDisconnected"), "V4 Prepare starts disabled with a concrete disconnected Worker reason");
bloodyEnablementWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for Bloody V4 enablement regression."));
PumpUntil(() => bloodyEnablementVm.WorkerState.State == WorkerConnectionState.ConnectedFake, TimeSpan.FromSeconds(2));
Check(!bloodyPrepareButton.IsEnabled && bloodyPrepareReason.Text == Localization.Get("VmV4PrepareDiscoveryStale"), "Worker connection alone cannot enable V4 Prepare before current Windows discovery");
bloodyEnablementVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => bloodyEnablementVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2)), "Bloody V4 enablement regression receives current Windows endpoint discovery");
Check(!bloodyPrepareButton.IsEnabled && bloodyPrepareReason.Text == Localization.Get("VmV4PrepareSelectPair"), "current discovery still requires an explicitly selected active pair");
bloodyEnablementVm.SelectedPhysicalDeviceGroup = bloodyEnablementVm.PhysicalDeviceGroups.Single();
bloodyEnablementVm.V4RepeatabilityRuns = 0;
bloodyEnablementVm.AutomaticScanV2RenderChannel2 = bloodyEnablementVm.AutomaticScanV2RenderChannel1;
bloodyEnablementWindow.UpdateLayout();
Check(!bloodyPrepareButton.IsEnabled && bloodyPrepareReason.Text == Localization.Get("VmV4PrepareInvalidRoute"), "duplicate Render channel keeps V4 Prepare disabled with the route reason");
bloodyEnablementVm.AutomaticScanV2RenderChannel2 = 2;
bloodyEnablementWindow.UpdateLayout();
Check(bloodyEnablementVm.SelectedDuplexCaptureEndpoint?.FriendlyName == "Bloody Capture" && bloodyEnablementVm.SelectedDuplexRenderEndpoint?.FriendlyName == "Bloody Render" && bloodyEnablementVm.AutomaticScanV2Mode == "MONO_TO_STEREO" && bloodyEnablementVm.V4RepeatabilityRuns == 0 && bloodyPrepareButton.IsEnabled && bloodyPrepareReason.Text.Length == 0, "Worker connected → current discovery → Bloody Capture/Render → valid MONO_TO_STEREO with R=0 enables the WPF Prepare button");
var activeWorkerSession = new SessionDto(Guid.NewGuid(), SessionMode.Capture, "busy fixture", null, 5, SessionLifecycle.Started, StreamQuality.Unknown, "Active operation fixture", null);
typeof(MainViewModel).GetProperty("WorkerSession")!.SetValue(bloodyEnablementVm, activeWorkerSession);
bloodyEnablementWindow.UpdateLayout();
Check(!bloodyPrepareButton.IsEnabled && bloodyPrepareReason.Text == Localization.Get("VmV4PrepareOperationActive"), "active Worker session disables V4 Prepare as busy");
typeof(MainViewModel).GetProperty("WorkerSession")!.SetValue(bloodyEnablementVm, activeWorkerSession with { Lifecycle = SessionLifecycle.Completed });
bloodyEnablementWindow.UpdateLayout();
Check(bloodyPrepareButton.IsEnabled, "V4 Prepare reenables after the Worker operation reaches a terminal state");
bloodyEnablementWorker.DiscoveryGeneration++;
bloodyEnablementWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Discovery generation changed."));
bloodyEnablementWindow.UpdateLayout();
Check(!bloodyPrepareButton.IsEnabled && bloodyPrepareReason.Text == Localization.Get("VmV4PrepareDiscoveryStale"), "Worker discovery-generation change stales the endpoint list and disables V4 Prepare");
bloodyEnablementVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => bloodyEnablementVm.HasCurrentWindowsEndpoints && bloodyPrepareButton.IsEnabled, TimeSpan.FromSeconds(2)), "fresh rediscovery restores V4 Prepare for the same selected pair");
bloodyEnablementWorker.SetState(new(WorkerConnectionState.Disconnected, "Disconnected during enablement regression."));
bloodyEnablementWindow.UpdateLayout();
Check(!bloodyPrepareButton.IsEnabled && bloodyPrepareReason.Text == Localization.Get("VmV4PrepareWorkerDisconnected"), "Worker disconnect immediately disables V4 Prepare");
bloodyEnablementWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Reconnected."));
bloodyEnablementVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => bloodyEnablementVm.HasCurrentWindowsEndpoints && bloodyPrepareButton.IsEnabled, TimeSpan.FromSeconds(2)), "reconnect and fresh discovery restore V4 Prepare");
var passiveQueryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
bloodyEnablementWorker.QueryGate = passiveQueryGate;
bloodyPrepareButton.Command!.Execute(null);
Check(PumpUntil(() => bloodyEnablementVm.V4PlanPreparing && bloodyEnablementWorker.RealQueryCalls > 0, TimeSpan.FromSeconds(2)) && !bloodyPrepareButton.IsEnabled && bloodyPrepareReason.Text == Localization.Get("VmV4PrepareBusy"), "active passive preparation disables a duplicate WPF launch and reports preparation busy");
bloodyEnablementVm.CancelV4CustomPlanCommand.Execute(null);
Check(PumpUntil(() => !bloodyEnablementVm.V4PlanPreparing && bloodyPrepareButton.IsEnabled, TimeSpan.FromSeconds(2)), "cancelling passive preparation restores the WPF command after cleanup");
bloodyEnablementWindow.Close();
var v2RouteWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = physicalPairFixture, AdvertiseWindowsDiscovery = true, AdvertiseV2 = true, AdvertisePassiveFormatQuery = true, DiscoveryGeneration = 1,
    RealQueryFactory = (id, direction, counts, _) => Task.FromResult(PassiveMatrix(id, direction, direction == "CAPTURE" ? 8 : 2)) };
var v2Workflow = new GuiAutomaticScanWorkflow(physicalPairFixture);
var v2RouteVm = new MainViewModel(worker: v2RouteWorker, automaticScanWorkflow: v2Workflow);
v2RouteWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for V2 route selection fixture."));
v2RouteVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => v2RouteVm.PhysicalDeviceGroups.Count == 1, TimeSpan.FromSeconds(2)), "V2 route discovery completes from deterministic endpoint fixtures");
var enCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US"); var ruCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
Check(Localization.Get("VmScanEngineV1Description", enCulture).Contains("Basic check", StringComparison.Ordinal) && Localization.Get("VmScanEngineV1Description", ruCulture).Contains("Базовая проверка", StringComparison.Ordinal) && Localization.Get("VmScanEngineV2Description", enCulture).Contains("active Capture", StringComparison.Ordinal) && Localization.Get("VmScanEngineV2Description", ruCulture).Contains("Нужны активные endpoint", StringComparison.Ordinal) && Localization.Get("VmV2CheckStopped", ruCulture) == "Проверка остановлена" && Localization.Get("VmV2CheckStopped", enCulture) == "Check stopped" && Localization.Get("VmV2QaShortSummary", enCulture).Contains("not certified", StringComparison.Ordinal) && Localization.Get("VmV2QaShortSummary", ruCulture).Contains("не сертифицирована", StringComparison.Ordinal) && Localization.Get("VmAutoSupportColumn", enCulture).Contains("Capture / Render", StringComparison.Ordinal) && Localization.Get("VmAutoSupportColumn", ruCulture).Contains("Capture / Render", StringComparison.Ordinal), "V1/V2 descriptions, Short non-certification, and AUTO_SHORT comparison headings are localized in RU and EN");
v2RouteVm.SelectedPhysicalDeviceGroup = v2RouteVm.PhysicalDeviceGroups.Single();
v2RouteVm.AutomaticScanV2CaptureChannels = 8;
v2RouteVm.AutomaticScanV2CaptureChannel1 = 8;
v2RouteVm.AutomaticScanV2RenderChannels = 2;
Check(v2RouteVm.AutomaticScanEngine == "V2_ADAPTIVE" && v2RouteVm.AutomaticScanV2Period == "MINIMUM" && v2RouteVm.AutomaticScanV2QaMode == "DISCOVERY" && v2RouteVm.AutomaticScanV2RouteSummary.Contains("8", StringComparison.Ordinal), "V2 starts with Discovery and MINIMUM-first while permitting the last one-based channel in a multichannel stream");
v2RouteVm.AutomaticScanV2QaMode = "AUTO_SHORT";
v2RouteVm.PrepareAutomaticScanCommand.Execute(null);
Check(v2RouteVm.AutomaticScanV2QaModes.Any(m => m.Value == "AUTO_SHORT") && v2RouteVm.AutomaticScanConsentText.Contains("DEFAULT + MINIMUM", StringComparison.Ordinal) && v2RouteVm.AutomaticScanConsentText.Contains("local-windows-id", StringComparison.Ordinal) && v2RouteVm.AutomaticScanConsentText.Contains("local-render-id", StringComparison.Ordinal) && v2RouteVm.AutomaticScanConsentText.Contains(WorkerScanPlanV2Fingerprint.Route("local-windows-id", "local-render-id", new("MONO_TO_STEREO", new(48000, 8, "PCM16", 0, [7]), new(48000, 2, "PCM16", 0, [0, 1]))), StringComparison.Ordinal) && v2RouteVm.AutomaticScanConsentText.Contains(Localization.Get("VmV2QaAutoShortConsent"), StringComparison.Ordinal) && Localization.Get("VmV2QaAutoShort", ruCulture).Contains("Автоподбор", StringComparison.Ordinal), $"AUTO_SHORT consent names periods, formats, route, duration ceiling, MMCSS OFF, and uncertainty stop conditions in localized UI [mode={v2RouteVm.AutomaticScanV2QaMode}; text={v2RouteVm.AutomaticScanConsentText}; expected={Localization.Get("VmV2QaAutoShortConsent")}; ru={Localization.Get("VmV2QaAutoShort", ruCulture)}]");
v2RouteVm.AutomaticScanV2QaMode = "DISCOVERY";
foreach (var mode in new[] { "MONO_TO_STEREO", "STEREO_TO_STEREO", "MONO_TO_MONO" }) { v2RouteVm.AutomaticScanV2Mode = mode; Check(v2RouteVm.AutomaticScanV2Modes.Any(m => m.Value == v2RouteVm.AutomaticScanV2Mode), $"V2 route mode {mode} is selectable"); }
v2RouteVm.AutomaticScanV2Mode = "MONO_TO_STEREO";
var v2LayoutWindow = new MainWindow(v2RouteVm) { Width = 1280, Height = 800, ShowActivated = false };
v2LayoutWindow.Show(); v2LayoutWindow.UpdateLayout();
var v4PlanCard = (Border)v2LayoutWindow.FindName("V4CustomPlanCard");
var v4PrepareButton = (Button)v2LayoutWindow.FindName("V4PreparePlanButton");
var v4StartButton = (Button)v2LayoutWindow.FindName("V4StartScanButton");
var v4ResultsText = (TextBlock)v2LayoutWindow.FindName("V4ScanResultsText");
var v4EvidencePathText = (TextBlock)v2LayoutWindow.FindName("V4EvidencePathText");
Check(v4StartButton.Command == v2RouteVm.StartV4ScanCommand && !v4StartButton.IsEnabled && v4ResultsText.TextWrapping == TextWrapping.Wrap && v4EvidencePathText.TextWrapping == TextWrapping.Wrap, "V4 scan, result, and evidence controls are bound and long diagnostics wrap while the audio gate is closed");
Check(v2RouteVm.V4CustomPlanControlsVisible && v4PlanCard.Visibility == Visibility.Visible && !v2RouteVm.AcceptV4CustomPlanCommand.CanExecute(null), "WPF exposes separate v4 plan preview while keeping consent unavailable before passive preparation");
v4PrepareButton.Command!.Execute(null);
Check(PumpUntil(() => v2RouteVm.V4ConsentPending, TimeSpan.FromSeconds(3)) && v2RouteWorker.RealQueryCalls == 2 && v2RouteVm.V4ConsentSummary.Contains("144", StringComparison.Ordinal) && v2RouteVm.V4ConsentSummary.Contains("480", StringComparison.Ordinal) && v2RouteVm.V4ConsentSummary.Contains("ALIGNMENT_UNKNOWN", StringComparison.Ordinal) && v2RouteVm.V4ConsentSummary.Contains("29", StringComparison.Ordinal) && v2RouteVm.V4PlanFingerprint.Length == 64, "GUI prepares exact eight-candidate v4 consent from passive Capture/Render bounds with alignment explicitly unknown");
v2LayoutWindow.UpdateLayout();
var v4ConsentSummaryText = v2LayoutWindow.FindName("V4ConsentSummaryText") as TextBlock;
var v4CaptureNameField = typeof(MainViewModel).GetField("_v4CaptureFriendlyName", BindingFlags.Instance | BindingFlags.NonPublic)!;
var v4RenderNameField = typeof(MainViewModel).GetField("_v4RenderFriendlyName", BindingFlags.Instance | BindingFlags.NonPublic)!;
var originalV4CaptureName = v4CaptureNameField.GetValue(v2RouteVm); var originalV4RenderName = v4RenderNameField.GetValue(v2RouteVm);
v4CaptureNameField.SetValue(v2RouteVm, "BEHRINGER FLOW 8 USB Capture endpoint with long studio and location label");
v4RenderNameField.SetValue(v2RouteVm, "BEHRINGER FLOW 8 USB Render endpoint with long studio and location label");
typeof(ViewModel).GetMethod("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(v2RouteVm, ["V4ConsentSummary"]);
v2LayoutWindow.UpdateLayout();
Check(v4ConsentSummaryText?.Text.Contains("long studio and location label", StringComparison.Ordinal) == true && v4ConsentSummaryText.TextWrapping == TextWrapping.Wrap, "V4 consent wraps long Capture/Render endpoint labels without clipping the summary");
v4PlanCard.BringIntoView(); v2LayoutWindow.UpdateLayout();
var v4ScrollViewer = (ScrollViewer)v2LayoutWindow.FindName("OperatorContentScrollViewer");
v4ScrollViewer.ScrollToVerticalOffset(Math.Max(0, v4ScrollViewer.VerticalOffset - 140)); v2LayoutWindow.UpdateLayout();
foreach (var scale in new[] { 1d, 1.25d, 1.5d })
{
    v2LayoutWindow.Width = 1920 / scale; v2LayoutWindow.Height = 1080 / scale; v2LayoutWindow.UpdateLayout();
    Check(v4PlanCard.ActualWidth <= v2LayoutWindow.ActualWidth && v4PrepareButton.ActualWidth > 0 && v4ConsentSummaryText?.TextWrapping == TextWrapping.Wrap && v4ResultsText.ActualWidth <= v4PlanCard.ActualWidth && v4EvidencePathText.ActualWidth <= v4PlanCard.ActualWidth, $"V4 consent, results, evidence path, and long plan text remain in the WPF layout at Full HD {scale:P0} scaling");
    CaptureUiScreenshot(v2LayoutWindow, $"AS-N4B.2.2B1.1-v4-consent-{scale:P0}.png", scale);
}
var v4EvidenceCulture = Localization.CurrentCulture.Name; Localization.SetLanguage("en-US", false);
var v4EnglishWindow = new MainWindow(v2RouteVm) { Width = 1920, Height = 1080, ShowActivated = false }; v4EnglishWindow.Show(); v4EnglishWindow.UpdateLayout();
var v4EnglishCard = (Border)v4EnglishWindow.FindName("V4CustomPlanCard"); v4EnglishCard.BringIntoView();
var v4EnglishScroll = (ScrollViewer)v4EnglishWindow.FindName("OperatorContentScrollViewer");
v4EnglishScroll.ScrollToEnd(); PumpUntil(() => v4EnglishScroll.VerticalOffset > 0, TimeSpan.FromSeconds(2)); v4EnglishWindow.UpdateLayout();
var v4EnglishCardTop = v4EnglishCard.TransformToAncestor(v4EnglishScroll).Transform(new Point(0, 0)).Y;
v4EnglishScroll.ScrollToVerticalOffset(Math.Max(0, v4EnglishScroll.VerticalOffset + v4EnglishCardTop - 80));
PumpUntil(() => { v4EnglishWindow.UpdateLayout(); var y = v4EnglishCard.TransformToAncestor(v4EnglishScroll).Transform(new Point(0, 0)).Y; return y is >= 20 and <= 120; }, TimeSpan.FromSeconds(2));
Check(((TextBlock)v4EnglishWindow.FindName("V4ConsentSummaryText")).Text.Contains("Requested", StringComparison.Ordinal) && ((TextBlock)v4EnglishWindow.FindName("V4AudioGateText")).Text.Contains("Preview and consent only", StringComparison.Ordinal) && ((TextBlock)v4EnglishWindow.FindName("V4PlanStatusText")).Text.StartsWith("Plan prepared", StringComparison.Ordinal), "English WPF consent, status and closed audio gate render from English resources");
CaptureUiScreenshot(v4EnglishWindow, "AS-N4B.2.2B1.1-v4-consent-en.png", 1d); v4EnglishWindow.Close();
Localization.SetLanguage(v4EvidenceCulture, false); v2LayoutWindow.UpdateLayout();
v4CaptureNameField.SetValue(v2RouteVm, originalV4CaptureName); v4RenderNameField.SetValue(v2RouteVm, originalV4RenderName);
typeof(ViewModel).GetMethod("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(v2RouteVm, ["V4ConsentSummary"]);
v2LayoutWindow.UpdateLayout();
var v4AcceptButton = (Button)v2LayoutWindow.FindName("V4AcceptPlanButton"); var v4ConsentCheck = (CheckBox)v2LayoutWindow.FindName("V4ConsentCheckBox");
Check(!v4AcceptButton.IsEnabled && v2RouteWorker.RealInitializeCalls == 0 && v2RouteWorker.DuplexCalls == 0, "v4 consent cannot accept before operator check and preparation performs no Initialize or Duplex");
v4ConsentCheck.IsChecked = true; v2LayoutWindow.UpdateLayout();
Check(v4AcceptButton.IsEnabled, "WPF enables v4 consent acceptance only after explicit plan review");
v4AcceptButton.Command!.Execute(null);
Check(v2RouteVm.V4ReceiptCreated && v2RouteVm.V4ConsentReceipt?.PlanFingerprint == v2RouteVm.V4PlanFingerprint && v2RouteVm.V4ConsentReceipt?.DiscoveryGeneration == v2RouteWorker.DiscoveryGeneration && v2RouteWorker.RealInitializeCalls == 0 && v2RouteWorker.DuplexCalls == 0, "v4 receipt binds the displayed plan fingerprint and Worker generation without opening audio transport");
Check(Localization.Get("V4ConsentCheck", System.Globalization.CultureInfo.GetCultureInfo("en-US")).Contains("open speakers", StringComparison.OrdinalIgnoreCase) && typeof(MainViewModel).GetField("_v4AudioHostConsentPlanFingerprint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(v2RouteVm) as string == v2RouteVm.V4PlanFingerprint, "physical AudioHost permission is explicit and bound to the accepted endpoint plan");
Check(Localization.Get("VmV4AudioGateClosed", ruCulture).Contains("профиля", StringComparison.Ordinal) && Localization.Get("V4PlanHeading", System.Globalization.CultureInfo.GetCultureInfo("en-US")).Contains("CUSTOM", StringComparison.Ordinal), "v4 consent gate is localized in Russian and English");
v2RouteVm.V4RepeatabilityRuns = 4;
Check(!v2RouteVm.V4PlanReady && !v2RouteVm.V4ReceiptCreated && v2RouteVm.V4ConsentReceipt is null, "changing repeatability budget invalidates the prepared v4 plan and receipt");
Check(typeof(MainViewModel).GetField("_v4AudioHostConsentPlanFingerprint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(v2RouteVm) is null, "changing the plan also revokes its AudioHost physical-run permission");
v2RouteVm.V4RepeatabilityRuns = 5;
void PrepareFreshV4ForInvalidation(string label)
{
    v2RouteVm.PrepareV4CustomPlanCommand.Execute(null);
    Check(PumpUntil(() => v2RouteVm.V4ConsentPending, TimeSpan.FromSeconds(3)), label);
}
PrepareFreshV4ForInvalidation("v4 plan can be prepared again for route-field invalidation");
v2RouteVm.AutomaticScanV2CaptureChannel1 = 7;
Check(!v2RouteVm.V4PlanReady && !v2RouteVm.V4ConsentPending, "changing the selected Capture channel invalidates the v4 plan");
v2RouteVm.AutomaticScanV2CaptureChannel1 = 8;
PrepareFreshV4ForInvalidation("v4 plan can be prepared again for format-domain invalidation");
v2RouteVm.AutomaticScanV2CaptureFormat = "44100|PCM16";
Check(!v2RouteVm.V4PlanReady && !v2RouteVm.V4ConsentPending, "changing the selected Capture format invalidates the v4 candidate domain");
v2RouteVm.AutomaticScanV2CaptureFormat = "48000|PCM16";
PrepareFreshV4ForInvalidation("v4 plan can be prepared again for route-mode invalidation");
v2RouteVm.AutomaticScanV2Mode = "STEREO_TO_STEREO";
Check(!v2RouteVm.V4PlanReady && !v2RouteVm.V4ConsentPending, "changing route mode invalidates the prepared v4 plan");
v2RouteVm.AutomaticScanV2Mode = "MONO_TO_STEREO";
var savedV4RenderEndpoint = v2RouteVm.SelectedDuplexRenderEndpoint;
PrepareFreshV4ForInvalidation("v4 plan can be prepared again for endpoint invalidation");
v2RouteVm.SelectedDuplexRenderEndpoint = null;
Check(!v2RouteVm.V4PlanReady && !v2RouteVm.V4ConsentPending, "changing the Render endpoint invalidates the prepared v4 plan");
v2RouteVm.SelectedDuplexRenderEndpoint = savedV4RenderEndpoint;
v2RouteVm.AutomaticScanV2CaptureChannels = 8;
v2RouteVm.AutomaticScanV2CaptureChannel1 = 8;
var latePassive = new TaskCompletionSource<RealFormatMatrixDto>(TaskCreationOptions.RunContinuationsAsynchronously);
v2RouteWorker.RealQueryFactory = (_, direction, _, _) => latePassive.Task;
var previousV4Queries = v2RouteWorker.RealQueryCalls; var previousV4Completions = v2RouteWorker.RealQueryCompletions;
var previousV4Context = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
v2RouteVm.PrepareV4CustomPlanCommand.Execute(null); SynchronizationContext.SetSynchronizationContext(previousV4Context);
Check(PumpUntil(() => v2RouteWorker.RealQueryCalls > previousV4Queries && v2RouteVm.V4PlanPreparing, TimeSpan.FromSeconds(2)), "v4 passive query begins before the stale-response cancellation test");
v2RouteVm.CancelV4CustomPlanCommand.Execute(null);
latePassive.SetResult(PassiveMatrix("local-windows-id", "CAPTURE", 8));
Check(PumpUntil(() => v2RouteWorker.RealQueryCompletions > previousV4Completions && v2RouteVm.PrepareV4CustomPlanCommand.CanExecute(null), TimeSpan.FromSeconds(2)) && !v2RouteVm.V4PlanReady && !v2RouteVm.V4ConsentPending && v2RouteVm.V4ConsentReceipt is null, "late passive response cannot restore a cancelled prepared plan or consent");
v2RouteWorker.RealQueryFactory = (id, direction, counts, _) => Task.FromResult(PassiveMatrix(id, direction, direction == "CAPTURE" ? 8 : 2));
var v2RouteCard = (Border)v2LayoutWindow.FindName("V2RouteSelectionCard");
var v2ScanButton = (Button)v2LayoutWindow.FindName("DeviceScanButton");
foreach (var size in new[] { (1920d, 1080d), (1536d, 864d), (1280d, 720d), (1366d, 768d) })
{
    v2LayoutWindow.Width = size.Item1; v2LayoutWindow.Height = size.Item2; v2LayoutWindow.UpdateLayout();
    Check(v2RouteCard.Visibility == Visibility.Visible && v2RouteCard.ActualWidth <= v2LayoutWindow.ActualWidth && v2ScanButton.ActualWidth > 0 && v2ScanButton.ActualWidth <= v2LayoutWindow.ActualWidth, $"V2 scanner route and launch controls fit at {size.Item1:0}×{size.Item2:0} DIP");
}
v2RouteVm.PrepareAutomaticScanCommand.Execute(null);
Check(PumpUntil(() => v2RouteVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2)) && v2RouteVm.AutomaticScanConsentText.Contains("MINIMUM", StringComparison.Ordinal) && v2RouteVm.AutomaticScanConsentText.Contains("8", StringComparison.Ordinal), "V2 consent binds the exact route and selected period");
v2LayoutWindow.UpdateLayout();
var confirmV2Button = (Button)v2LayoutWindow.FindName("ConfirmAutomaticScanButton");
var v2ConsentCheckBox = (CheckBox)v2LayoutWindow.FindName("AutomaticScanConsentCheckBox");
Check(!v2RouteVm.ConfirmAutomaticScanCommand.CanExecute(null) && !confirmV2Button.IsEnabled, "V2 confirmation stays disabled until explicit consent is checked");
v2ConsentCheckBox.IsChecked = true; v2LayoutWindow.UpdateLayout();
Check(v2RouteVm.ConfirmAutomaticScanCommand.CanExecute(null) && confirmV2Button.IsEnabled, "WPF confirmation button becomes enabled for a current authorized V2 route");
confirmV2Button.Command!.Execute(confirmV2Button.CommandParameter);
Check(PumpUntil(() => v2RouteVm.HasAutomaticScanResult && v2RouteVm.AutomaticScanResultHeadline == KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticScanCompleted"), TimeSpan.FromSeconds(2)) && v2Workflow.RunV2Calls == 1 && v2Workflow.LastV2CaptureId == "local-windows-id" && v2Workflow.LastV2RenderId == "local-render-id" && v2Workflow.LastV2Period == "MINIMUM" && v2Workflow.LastV2Route?.Capture.SelectedIndicesZeroBased.SequenceEqual([7]) == true, $"WPF V2 selection, prepare, checked consent and confirmation invoke the V2 workflow with exact endpoint, route and period and show result [has={v2RouteVm.HasAutomaticScanResult}; headline={v2RouteVm.AutomaticScanResultHeadline}; calls={v2Workflow.RunV2Calls}; capture={v2Workflow.LastV2CaptureId}; render={v2Workflow.LastV2RenderId}; period={v2Workflow.LastV2Period}; route={v2Workflow.LastV2Route}; status={v2RouteVm.AutomaticScanStatus}; consent={v2RouteVm.AutomaticScanConsentPending}]");
v2RouteVm.PrepareAutomaticScanCommand.Execute(null); Check(PumpUntil(() => v2RouteVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2)), "V2 consent is prepared before mode-switch regression");
v2RouteVm.AutomaticScanEngine = "V1_COMPATIBLE";
Check(!v2RouteVm.AutomaticScanConsentPending && !v2RouteVm.ConfirmAutomaticScanCommand.CanExecute(null) && v2RouteVm.AutomaticScanEngineDescription == Localization.Get("VmScanEngineV1Description"), "switching V2 to V1 clears pending consent without starting audio and updates the explanation");
v2RouteVm.AutomaticScanEngine = "V2_ADAPTIVE";
Check(v2RouteVm.AutomaticScanEngineDescription == Localization.Get("VmScanEngineV2Description"), "switching back to V2 updates its route requirement explanation");
var openV2ReportButton = (Button)v2LayoutWindow.FindName("OpenAutomaticScanLocationButton");
Check(!openV2ReportButton.IsEnabled && !v2RouteVm.OpenAutomaticScanLocationCommand.CanExecute(null), "V2 result without an evidence file keeps the WPF report-folder command disabled");
var v2OpenFolder = Path.Combine(Path.GetTempPath(), $"klidan-v2-open-folder-{Guid.NewGuid():N}"); Directory.CreateDirectory(v2OpenFolder);
var v2OpenEvidence = Path.Combine(v2OpenFolder, "evidence.json"); File.WriteAllText(v2OpenEvidence, "{}");
var currentV2Result = (V2AutomaticScanResult)typeof(MainViewModel).GetField("_automaticScanV2Result", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(v2RouteVm)!;
var autoUiCandidates = new[] { AutoCandidate("DEFAULT", "SHORT_CLEAN") with { RenderBufferFrames = 480, RenderPeriod100ns = 100000 }, AutoCandidate("MINIMUM", "SHORT_FAILED") with { RenderBufferFrames = 144, RenderPeriod100ns = 30000, Reason = "CAPTURE_COVERAGE_BELOW_99;RENDER_DISCONTINUITIES_NONZERO;" + new string('x', 320) } };
void DisplayAutoShort(WorkerScanV2Route route, string capName, string renName, IReadOnlyList<V2AutoShortCandidate> candidates, string? recommended, string lifecycle = "AUTO_SHORT_COMPLETED", string qualification = "SHORT_CLEAN")
{
    typeof(MainViewModel).GetField("_automaticScanV2Result", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(v2RouteVm, currentV2Result with { Lifecycle = lifecycle, QaMode = "AUTO_SHORT", Route = route, CaptureFriendlyName = capName, RenderFriendlyName = renName, AutoShortCandidates = candidates, RecommendedPeriod = recommended, Qualification = qualification });
    typeof(MainViewModel).GetMethod("NotifyAutomaticScanResultBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(v2RouteVm, null);
}
var bloodyUiRoute = new WorkerScanV2Route("MONO_TO_STEREO", new(48000, 2, "PCM16", 3, [0]), new(48000, 2, "PCM16", 3, [0, 1]));
DisplayAutoShort(bloodyUiRoute, "Bloody Capture endpoint with a deliberately long descriptive name", "Bloody Render endpoint with a deliberately long descriptive name", autoUiCandidates, "DEFAULT");
v2LayoutWindow.Width = 1920; v2LayoutWindow.Height = 1080; v2LayoutWindow.UpdateLayout();
var autoComparison = (Expander)v2LayoutWindow.FindName("AutomaticScanAutoShortComparison");
var recommendationPeriod = (TextBlock)v2LayoutWindow.FindName("AutomaticScanRecommendationPeriodText");
var autoHeadline = (TextBlock)v2LayoutWindow.FindName("AutomaticScanResultHeadlineText");
Check(v2RouteVm.AutomaticScanAutoShortVisible && v2RouteVm.AutomaticScanAutoShortCandidates.Count == 2 && autoComparison.Visibility == Visibility.Visible && v2RouteVm.AutomaticScanResultHeadline == Localization.Get("VmAutoShortCompleted") && v2RouteVm.AutomaticScanResultSummary.Contains(Localization.Get("VmV2AutoShortResultSummary").Contains("NOT_CERTIFIED", StringComparison.Ordinal) ? "NOT_CERTIFIED" : "НЕ СЕРТИФИЦИРОВАНО", StringComparison.Ordinal) && v2RouteVm.AutomaticScanRecommendationPeriodDisplay.Contains("10", StringComparison.Ordinal) && v2RouteVm.AutomaticScanAutoShortCandidates[0].Period.Contains("10", StringComparison.Ordinal) && v2RouteVm.AutomaticScanAutoShortCandidates[1].Result == Localization.Get("VmAutoCandidateFailed") && v2RouteVm.AutomaticScanAutoShortCandidates[1].Reason.Length < 80 && v2RouteVm.AutomaticScanAutoShortCandidates[1].Details.Contains("CAPTURE_COVERAGE_BELOW_99", StringComparison.Ordinal) && autoHeadline.Text == Localization.Get("VmAutoShortCompleted") && recommendationPeriod.ActualWidth <= autoComparison.ActualWidth && autoComparison.ActualWidth <= 1920 && autoComparison.ActualHeight <= 1080 && !v2RouteVm.AutomaticScanResultSummary.Contains("SCANNING", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(v2RouteVm.AutomaticScanExportStatus), $"Bloody AUTO_SHORT UI separates completed status, DEFAULT 10 ms recommendation, concise candidate rows and expandable diagnostic codes [headline={v2RouteVm.AutomaticScanResultHeadline}; summary={v2RouteVm.AutomaticScanResultSummary}; period={v2RouteVm.AutomaticScanRecommendationPeriodDisplay}; candidate={v2RouteVm.AutomaticScanAutoShortCandidates[1]}; headlineText={autoHeadline.Text}; widths={recommendationPeriod.ActualWidth}/{autoComparison.ActualWidth}]");
CaptureUiScreenshot(v2LayoutWindow, "AS-N4B.2.1-Bloody-DEFAULT-10ms.png");
var flowUiRoute = new WorkerScanV2Route("MONO_TO_STEREO", new(48000, 1, "PCM16", 0, [0]), new(48000, 2, "PCM16", 3, [0, 1]));
var flowCandidates = new[] { AutoCandidate("DEFAULT", "SHORT_CLEAN") with { RenderBufferFrames = 480, RenderPeriod100ns = 100000 }, AutoCandidate("MINIMUM", "SHORT_CLEAN") with { RenderBufferFrames = 144, RenderPeriod100ns = 30000 } };
DisplayAutoShort(flowUiRoute, "BEHRINGER FLOW 8 Capture", "BEHRINGER FLOW 8 Render", flowCandidates, "MINIMUM"); v2LayoutWindow.UpdateLayout();
Check(v2RouteVm.AutomaticScanResultHeadline == Localization.Get("VmAutoShortCompleted") && v2RouteVm.AutomaticScanAutoShortCandidates.All(c => c.Result == Localization.Get("VmAutoCandidateClean")) && v2RouteVm.AutomaticScanRecommendationPeriodDisplay.Contains("3", StringComparison.Ordinal) && v2RouteVm.AutomaticScanResultDevice.Contains("BEHRINGER FLOW 8", StringComparison.Ordinal), "FLOW 8 AUTO_SHORT UI recommends the smaller 3 ms candidate when both results are clean");
v2LayoutWindow.Width = 1920; v2LayoutWindow.Height = 1080; v2LayoutWindow.UpdateLayout(); CaptureUiScreenshot(v2LayoutWindow, "AS-N4B.2.1-FLOW8-MINIMUM-3ms.png");
DisplayAutoShort(flowUiRoute, "BEHRINGER FLOW 8 USB Capture endpoint with an extended product and location description", "BEHRINGER FLOW 8 USB Render endpoint with an extended product and location description", [flowCandidates[0], flowCandidates[1] with { Status = "UNKNOWN", Reason = "WORKER_DISCONNECT_CLEANUP_UNCONFIRMED;" + new string((char)120, 320) }], null, "UNKNOWN", "UNKNOWN"); v2LayoutWindow.UpdateLayout(); ((Expander)v2LayoutWindow.FindName("AutomaticScanAutoShortDetails")).IsExpanded = true; v2LayoutWindow.UpdateLayout();
Check(v2RouteVm.AutomaticScanResultHeadline == Localization.Get("VmV2CheckStopped") && v2RouteVm.AutomaticScanRecommendationPeriodDisplay == Localization.Get("VmAutomaticScanRecommendationMissing") && v2RouteVm.AutomaticScanAutoShortCandidates[1].Result == Localization.Get("VmAutoCandidateUnknown") && v2RouteVm.AutomaticScanAutoShortCandidates[1].Details.Length > 250, "AUTO_SHORT UNKNOWN displays no recommended period and remains distinct from candidate results");
var autoPriorUiLanguage = Localization.CurrentCulture.Name; Localization.SetLanguage("ru-RU", false); var autoRuStatus = v2RouteVm.AutomaticScanResultHeadline; var autoRuPeriod = v2RouteVm.AutomaticScanAutoShortCandidates[0].Period; Localization.SetLanguage("en-US", false); var autoEnStatus = v2RouteVm.AutomaticScanResultHeadline; var autoEnPeriod = v2RouteVm.AutomaticScanAutoShortCandidates[0].Period; Localization.SetLanguage(autoPriorUiLanguage, false);
Check(autoRuStatus == Localization.Get("VmV2CheckStopped", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) && autoRuPeriod.Contains("мс", StringComparison.Ordinal) && autoEnStatus == Localization.Get("VmV2CheckStopped", System.Globalization.CultureInfo.GetCultureInfo("en-US")) && autoEnPeriod.Contains("ms", StringComparison.Ordinal), "AUTO_SHORT unknown state and millisecond units localize in RU/EN");
foreach (var (physicalW, physicalH, scale) in new[] { (1920d, 1080d, 1.0), (1920d, 1080d, 1.25), (1920d, 1080d, 1.5) })
{
    v2LayoutWindow.Width = physicalW / scale; v2LayoutWindow.Height = physicalH / scale; v2LayoutWindow.UpdateLayout();
    Check(autoComparison.ActualWidth <= v2LayoutWindow.ActualWidth && autoComparison.ActualHeight <= v2LayoutWindow.ActualHeight && ((TextBlock)v2LayoutWindow.FindName("AutomaticScanResultDeviceText")).TextWrapping == TextWrapping.Wrap, $"AUTO_SHORT result and long endpoint labels fit 1920×1080 at {scale:P0} effective scaling");
    CaptureUiScreenshot(v2LayoutWindow, $"AS-N4B.2.1-UNKNOWN-1920x1080-{scale:P0}.png", scale);
}
typeof(MainViewModel).GetField("_automaticScanV2Result", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(v2RouteVm, currentV2Result);
typeof(MainViewModel).GetMethod("NotifyAutomaticScanResultBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(v2RouteVm, null);
v2LayoutWindow.Width = 1280; v2LayoutWindow.Height = 800; v2LayoutWindow.UpdateLayout();
var unknownDiagnostics = new V2WorkerErrorDiagnostics("BUSY", "Worker rejected command", "WorkerCommandException", "SHORT_DUPLEX", currentV2Result.SessionId, Guid.NewGuid().ToString("D"), "RESPONSE_ERROR", "ConnectedFake", "NOT_CANCELLED", "CANCELLED");
typeof(MainViewModel).GetField("_automaticScanV2Result", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(v2RouteVm, currentV2Result with { Lifecycle = "UNKNOWN", EvidencePath = v2OpenEvidence, WorkerError = unknownDiagnostics });
typeof(MainViewModel).GetField("_automaticScanOutcomeUnknown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(v2RouteVm, true);
typeof(MainViewModel).GetMethod("NotifyAutomaticScanResultBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(v2RouteVm, null);
v2LayoutWindow.UpdateLayout();
Check(openV2ReportButton.IsEnabled && v2RouteVm.OpenAutomaticScanLocationCommand.CanExecute(null) && v2RouteVm.AutomaticScanResultHeadline == Localization.Get("VmV2CheckStopped") && v2RouteVm.AutomaticScanResultSummary.Contains("BUSY", StringComparison.Ordinal) && v2RouteVm.AutomaticScanResultSummary.Contains(Localization.Get("VmV2OpenReportForDetails"), StringComparison.Ordinal), "V2 UNKNOWN shows stopped stage/code/safety guidance and keeps its saved report available");
var priorUiLanguage = Localization.CurrentCulture.Name; Localization.SetLanguage("ru-RU", false); var ruUnknownSummary = v2RouteVm.AutomaticScanResultSummary; var ruUnknownHeadline = v2RouteVm.AutomaticScanResultHeadline; Localization.SetLanguage("en-US", false); var enUnknownSummary = v2RouteVm.AutomaticScanResultSummary; var enUnknownHeadline = v2RouteVm.AutomaticScanResultHeadline; Localization.SetLanguage(priorUiLanguage, false);
Check(ruUnknownHeadline == "Проверка остановлена" && ruUnknownSummary.Contains("Короткий Duplex", StringComparison.Ordinal) && enUnknownHeadline == "Check stopped" && enUnknownSummary.Contains("Short Duplex", StringComparison.Ordinal) && !System.Text.RegularExpressions.Regex.IsMatch(ruUnknownSummary + enUnknownSummary, @"\{[0-9]+\}|Vm[A-Za-z]+"), "V2 UNKNOWN stage and message render in RU/EN without raw placeholders or localization keys");
typeof(MainViewModel).GetField("_automaticScanOutcomeUnknown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(v2RouteVm, false);
v2RouteVm.PrepareAutomaticScanCommand.Execute(null);
Check(PumpUntil(() => v2RouteVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2)), "a new V2 scan can prepare after the prior run completed");
v2ConsentCheckBox.IsChecked = true; v2LayoutWindow.UpdateLayout(); confirmV2Button.Command!.Execute(confirmV2Button.CommandParameter);
Check(PumpUntil(() => v2Workflow.RunV2Calls == 2 && v2RouteVm.HasAutomaticScanResult, TimeSpan.FromSeconds(2)) && !openV2ReportButton.IsEnabled && !v2RouteVm.OpenAutomaticScanLocationCommand.CanExecute(null), "starting and completing a new V2 scan clears the prior report-folder target");
File.Delete(v2OpenEvidence); v2RouteVm.OpenAutomaticScanLocationCommand.Refresh(); v2LayoutWindow.UpdateLayout();
Check(!openV2ReportButton.IsEnabled && !v2RouteVm.OpenAutomaticScanLocationCommand.CanExecute(null), "V2 report-folder command disables when its evidence file is removed");
Directory.Delete(v2OpenFolder);
v2LayoutWindow.UpdateLayout();
double Top(FrameworkElement element) => element.TransformToAncestor(v2LayoutWindow).Transform(new System.Windows.Point(0, 0)).Y;
var scanTop = Top((FrameworkElement)v2LayoutWindow.FindName("V2RouteSelectionCard"));
var launchTop = Top((FrameworkElement)v2LayoutWindow.FindName("DeviceScanButton"));
var consentTop = Top((FrameworkElement)v2LayoutWindow.FindName("AutomaticScanConsentCard"));
var progressHeadingTop = Top((FrameworkElement)v2LayoutWindow.FindName("ProgressSectionHeadingText"));
var progressCardTop = Top((FrameworkElement)v2LayoutWindow.FindName("AutomaticScanProgressCard"));
var profilesHeadingTop = Top((FrameworkElement)v2LayoutWindow.FindName("ProfilesSectionHeadingText"));
var profileActionTop = Top((FrameworkElement)v2LayoutWindow.FindName("CheckSavedProfileBindingButton"));
var historyTop = Top((FrameworkElement)v2LayoutWindow.FindName("HistoricalRecoveryCard"));
var reportsHeadingTop = Top((FrameworkElement)v2LayoutWindow.FindName("ReportsSectionHeadingText"));
var reportCardTop = Top((FrameworkElement)v2LayoutWindow.FindName("LatestAutomaticReportCard"));
var progressCard = (Border)v2LayoutWindow.FindName("AutomaticScanProgressCard");
Check(progressCard.Visibility == Visibility.Visible && ((Border)v2LayoutWindow.FindName("LatestAutomaticReportCard")).Visibility == Visibility.Visible && scanTop < launchTop && launchTop < consentTop && consentTop < progressHeadingTop && progressHeadingTop < progressCardTop && progressCardTop < profilesHeadingTop && profilesHeadingTop < profileActionTop && profileActionTop < historyTop && historyTop < reportsHeadingTop && reportsHeadingTop < reportCardTop, "WPF visual tree orders scan inputs, launch and consent, persistent progress, profile controls, recovery and report content");
Check(((TextBlock)v2LayoutWindow.FindName("AutomaticScanProgressStatusText")).Visibility == Visibility.Visible && ((ItemsControl)v2LayoutWindow.FindName("AutomaticScanProgressRowsControl")).Items.Count > 0 && ((Button)v2LayoutWindow.FindName("CancelAutomaticScanProgressButton")).Visibility == Visibility.Collapsed && ((TextBlock)v2LayoutWindow.FindName("SustainedValidationHeading")).Visibility == Visibility.Collapsed, "completed V2 scan retains status and stage rows, hides cancel, and hides V1-only sustained heading");
v2LayoutWindow.Close();
v2RouteVm.PrepareAutomaticScanCommand.Execute(null);
Check(PumpUntil(() => v2RouteVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2)), "V2 plan can be prepared again after a completed run");
v2RouteVm.AutomaticScanV2Period = "DEFAULT";
Check(!v2RouteVm.AutomaticScanConsentPending && !v2RouteVm.ConfirmAutomaticScanCommand.CanExecute(null) && v2RouteVm.AutomaticScanV2Period == "DEFAULT", "changing V2 period after preparation invalidates consent and command execution");
v2RouteVm.PrepareAutomaticScanCommand.Execute(null); Check(PumpUntil(() => v2RouteVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2)), "V2 route consent prepares for format invalidation test");
v2RouteVm.AutomaticScanV2CaptureFormat = "44100|PCM16";
Check(!v2RouteVm.AutomaticScanConsentPending && !v2RouteVm.ConfirmAutomaticScanCommand.CanExecute(null), "changing V2 format after preparation invalidates consent and command execution");
v2RouteVm.PrepareAutomaticScanCommand.Execute(null); Check(PumpUntil(() => v2RouteVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2)), "V2 route consent prepares for endpoint invalidation test");
v2RouteVm.SelectedDuplexRenderEndpoint = null;
Check(!v2RouteVm.AutomaticScanConsentPending && !v2RouteVm.ConfirmAutomaticScanCommand.CanExecute(null), "changing V2 endpoint after preparation invalidates consent and command execution");
v2RouteVm.SelectedDuplexRenderEndpoint = v2RouteVm.DuplexRenderEndpoints.Single();
var v2QaWindow = new MainWindow(v2RouteVm) { Width = 1280, Height = 800, ShowActivated = false }; v2QaWindow.Show(); v2QaWindow.UpdateLayout();
v2RouteVm.AutomaticScanV2QaMode = "SHORT"; v2RouteVm.PrepareAutomaticScanCommand.Execute(null);
Check(PumpUntil(() => v2RouteVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2)) && v2RouteVm.AutomaticScanConsentText.Contains("SHORT", StringComparison.Ordinal), "Short QA plan presents bounded consent after fresh route preparation");
var qaConsent = (CheckBox)v2QaWindow.FindName("AutomaticScanConsentCheckBox"); var qaConfirm = (Button)v2QaWindow.FindName("ConfirmAutomaticScanButton");
qaConsent.IsChecked = true; v2QaWindow.UpdateLayout();
Check(qaConfirm.IsEnabled, "Short QA requires the selected-plan consent"); qaConfirm.Command!.Execute(qaConfirm.CommandParameter);
Check(PumpUntil(() => v2Workflow.LastV2QaMode == "SHORT" && v2RouteVm.HasAutomaticScanResult, TimeSpan.FromSeconds(2)), "GUI dispatches selected Short QA depth to V2 workflow");
v2RouteVm.AutomaticScanV2QaMode = "FULL"; v2RouteVm.PrepareAutomaticScanCommand.Execute(null);
Check(PumpUntil(() => v2RouteVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2)), "Full QA requires a fresh plan and consent");
v2RouteVm.AutomaticScanV2QaMode = "DISCOVERY";
Check(!v2RouteVm.AutomaticScanConsentPending && !v2RouteVm.AutomaticScanFullBudgetConfirmed && !v2RouteVm.ConfirmAutomaticScanCommand.CanExecute(null), "changing QA depth after Full preparation clears consent and the separate budget approval");
v2RouteVm.AutomaticScanV2QaMode = "FULL"; v2RouteVm.PrepareAutomaticScanCommand.Execute(null);
Check(PumpUntil(() => v2RouteVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2)), "Full QA requires a new plan after depth change");
var fullBudget = (CheckBox)v2QaWindow.FindName("AutomaticScanFullBudgetCheckBox"); qaConsent.IsChecked = true; v2QaWindow.UpdateLayout();
Check(v2RouteVm.AutomaticScanFullBudgetConfirmationVisible && !v2RouteVm.AutomaticScanFullBudgetConfirmed && !qaConfirm.IsEnabled, "Full QA remains disabled pending separate full-budget consent");
fullBudget.IsChecked = true; v2QaWindow.UpdateLayout(); Check(qaConfirm.IsEnabled, "Full QA starts only after explicit full-budget consent"); qaConfirm.Command!.Execute(qaConfirm.CommandParameter);
Check(PumpUntil(() => v2Workflow.LastV2QaMode == "FULL" && v2RouteVm.HasAutomaticScanResult, TimeSpan.FromSeconds(2)), "GUI dispatches explicitly confirmed Full QA depth to V2 workflow");
v2QaWindow.Close();
v2RouteVm.AutomaticScanEngine = "V1_COMPATIBLE";
Check(((StackPanel)physicalWindow.FindName("DevicesPanel")).Visibility == Visibility.Visible && ((StackPanel)physicalWindow.FindName("OperatorTestPanel")).Visibility == Visibility.Collapsed && ((StackPanel)physicalWindow.FindName("OperatorResultPanel")).Visibility == Visibility.Collapsed, "Device page is the only primary page visible in normal mode");
physicalVm.OpenDeveloperToolsCommand.Execute(null); physicalWindow.UpdateLayout();
Check(((StackPanel)physicalWindow.FindName("DevicesPanel")).Visibility == Visibility.Visible && ((StackPanel)physicalWindow.FindName("OperatorTestPanel")).Visibility == Visibility.Collapsed && ((StackPanel)physicalWindow.FindName("OperatorResultPanel")).Visibility == Visibility.Collapsed, "Developer Tools device navigation keeps the Device page alone visible");
Check(((Button)physicalWindow.FindName("PreparePeriodDiscoveryButton")).IsVisible && !((Button)physicalWindow.FindName("PreparePeriodDiscoveryButton")).IsEnabled && ((TextBlock)physicalWindow.FindName("PeriodDiscoveryAvailabilityReasonText")).Text == KlidanAudioProbe.Gui.Core.Localization.Get("VmPeriodDiscoveryUnavailable"), "Period discovery entry is visible with an explicit localized unavailable reason");
physicalVm.OpenManualRealToolsCommand.Execute(null); physicalWindow.UpdateLayout();
Check(((StackPanel)physicalWindow.FindName("DevicesPanel")).Visibility == Visibility.Collapsed && ((StackPanel)physicalWindow.FindName("OperatorTestPanel")).Visibility == Visibility.Visible && ((StackPanel)physicalWindow.FindName("OperatorResultPanel")).Visibility == Visibility.Collapsed, "Developer Tools real Test navigation keeps the Test page alone visible");
physicalVm.CloseDeveloperToolsCommand.Execute(null); physicalWindow.UpdateLayout();
Check(physicalVm.PhysicalDeviceGroupDetails.Count == physicalVm.WindowsEndpoints.Count, "Developer Tools grouping summary exposes every discovered endpoint with identity evidence");
physicalWorkflow.Discovery = physicalPairFixture;
physicalVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => !physicalVm.WindowsDiscoveryLoading && physicalVm.SelectedPhysicalDeviceGroup is { GroupId: "container:{PHYSICAL-A}" }, TimeSpan.FromSeconds(2)) && physicalVm.SelectedDuplexCaptureEndpoint?.EndpointId == "local-windows-id" && physicalVm.SelectedDuplexRenderEndpoint?.EndpointId == "local-render-id", "unchanged discovery reconciles the selected physical group by stable identity");
physicalWorkflow.Discovery = physicalPairFixture with { Endpoints = physicalPairFixture.Endpoints.Select(e => e with { ContainerId = "{PHYSICAL-CHANGED}" }).ToArray() };
physicalVm.DiscoverWindowsEndpointsCommand.Execute(null);
PumpUntil(() => !physicalVm.WindowsDiscoveryLoading, TimeSpan.FromSeconds(2)); physicalWindow.UpdateLayout();
Check(physicalVm.SelectedPhysicalDeviceGroup is null && physicalVm.SelectedDuplexCaptureEndpoint is null && physicalVm.SelectedDuplexRenderEndpoint is null && !physicalScanButton.IsEnabled, "changed Container ID invalidates the physical selection and Scan availability");
physicalWindow.Close();
var availabilityWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = physicalPairFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true, AdvertiseCustomPeriod = true };
var availabilityVm = new MainViewModel(worker: availabilityWorker, duplexRepeatabilityWorkflow: new AvailabilityDuplexWorkflow(physicalPairFixture), exclusivePeriodDiscoveryWorkflow: new AvailabilityPeriodWorkflow(), customPeriodDuplexWorkflow: new AvailabilityCustomPeriodWorkflow());
var availabilityWindow = new MainWindow(availabilityVm) { Width = 1280, Height = 800, ShowActivated = false };
availabilityWindow.Show(); availabilityVm.OpenDeveloperToolsCommand.Execute(null); availabilityWindow.UpdateLayout();
var availabilityDuplexButton = (Button)availabilityWindow.FindName("PrepareDuplexRepeatabilityButton");
var availabilityPeriodButton = (Button)availabilityWindow.FindName("PreparePeriodDiscoveryButton");
var availabilityCustomButton = (Button)availabilityWindow.FindName("PrepareCustomPeriodDuplexButton");
var availabilityRepeatButton = (Button)availabilityWindow.FindName("PrepareCustomRepeatabilityButton");
Check(!availabilityDuplexButton.IsEnabled && !availabilityPeriodButton.IsEnabled && !availabilityCustomButton.IsEnabled && !availabilityRepeatButton.IsEnabled, "Disconnected Worker keeps all period Prepare buttons disabled");
availabilityWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for availability fixture.")); availabilityWindow.UpdateLayout();
Check(!availabilityDuplexButton.IsEnabled && !availabilityPeriodButton.IsEnabled && !availabilityCustomButton.IsEnabled && !availabilityRepeatButton.IsEnabled, "Connected Worker without current pair keeps all period Prepare buttons disabled");
availabilityVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => !availabilityVm.WindowsDiscoveryLoading, TimeSpan.FromSeconds(2)), "availability fixture refresh completes"); availabilityWindow.UpdateLayout();
availabilityVm.SelectedPhysicalDeviceGroup = availabilityVm.PhysicalDeviceGroups.Single(); availabilityWindow.UpdateLayout();
Check(availabilityVm.HasCurrentDuplexPair && availabilityDuplexButton.IsEnabled && availabilityPeriodButton.IsEnabled, "Current exact Capture/Render pair enables both Prepare buttons");
typeof(MainViewModel).GetField("_periodDiscoveryResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(availabilityVm, customSeries with { Reports = [CustomReport("capture-current", "local-windows-id", "CAPTURE"), CustomReport("render-current", "local-render-id", "RENDER")] });
typeof(MainViewModel).GetField("_periodDiscoveryResultFingerprint", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(availabilityVm, ExclusivePeriodDiscoveryPlanning.Fingerprint(physicalPairFixture));
typeof(MainViewModel).GetMethod("RefreshCanExecute", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(availabilityVm, null); availabilityWindow.UpdateLayout();
Check(availabilityCustomButton.IsEnabled && availabilityVm.CustomPeriodDuplexAvailabilityReason == KlidanAudioProbe.Gui.Core.Localization.Get("VmCustomPeriodDuplexReady"), "Completed BOTH_EXACT evidence immediately enables the default 192-frame custom Duplex button");
Check(availabilityRepeatButton.IsEnabled && availabilityVm.PrepareCustomRepeatabilityCommand.CanExecute(null), "Completed BOTH_EXACT evidence immediately enables the custom repeatability command through the visual tree");
availabilityVm.CustomPeriodFramesText = "193"; availabilityWindow.UpdateLayout();
Check(!availabilityRepeatButton.IsEnabled && !availabilityVm.PrepareCustomRepeatabilityCommand.CanExecute(null), "Invalid custom period input immediately disables repeatability in the visual tree");
availabilityVm.CustomPeriodFramesText = "192"; availabilityWindow.UpdateLayout();
Check(availabilityRepeatButton.IsEnabled && availabilityVm.PrepareCustomRepeatabilityCommand.CanExecute(null), "Restoring valid custom period input raises command readiness again");
availabilityVm.SelectedDuplexRenderEndpoint = null; availabilityWindow.UpdateLayout();
Check(!availabilityDuplexButton.IsEnabled && !availabilityPeriodButton.IsEnabled && !availabilityCustomButton.IsEnabled && !availabilityRepeatButton.IsEnabled, "Changing an endpoint immediately disables all period Prepare buttons");
availabilityWorker.WindowsDiscovery = physicalPairFixture with { CaptureHresult = 1 };
availabilityVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => !availabilityVm.WindowsDiscoveryLoading, TimeSpan.FromSeconds(2)), "valid rediscovery completes");
availabilityVm.SelectedDuplexRenderEndpoint = availabilityVm.DuplexRenderEndpoints.Single(); availabilityWindow.UpdateLayout();
Check(availabilityDuplexButton.IsEnabled && availabilityPeriodButton.IsEnabled, "Valid rediscovery and reselection re-enable both Prepare buttons");
Check(!availabilityCustomButton.IsEnabled, "Stale rediscovery disables custom Duplex until fresh completed evidence is available");
Check(!availabilityRepeatButton.IsEnabled && !availabilityVm.PrepareCustomRepeatabilityCommand.CanExecute(null), "Stale rediscovery disables custom repeatability through the visual tree");
typeof(MainViewModel).GetField("_periodDiscoveryResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(availabilityVm, customSeries with { Lifecycle = "UNKNOWN", Reports = [CustomReport("capture-unknown", "local-windows-id", "CAPTURE"), CustomReport("render-unknown", "local-render-id", "RENDER")] });
typeof(MainViewModel).GetMethod("RefreshCanExecute", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(availabilityVm, null); availabilityWindow.UpdateLayout();
Check(!availabilityCustomButton.IsEnabled, "UNKNOWN period discovery remains fail-closed for custom Duplex");
Check(!availabilityRepeatButton.IsEnabled && !availabilityVm.PrepareCustomRepeatabilityCommand.CanExecute(null), "UNKNOWN period discovery remains fail-closed for custom repeatability");
typeof(MainViewModel).GetField("_automaticScanOutcomeUnknown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(availabilityVm, true);
typeof(MainViewModel).GetMethod("RefreshCanExecute", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(availabilityVm, null); availabilityWindow.UpdateLayout();
Check(!availabilityDuplexButton.IsEnabled && !availabilityPeriodButton.IsEnabled, "UNKNOWN operation state immediately disables both Prepare buttons");
availabilityWindow.Close();
var ambiguousDiscovery = physicalPairFixture with
{
    Endpoints = physicalPairFixture.Endpoints
        .Append(physicalPairFixture.Endpoints[0] with { EndpointId = "local-second-capture-id", FriendlyName = "Second Capture" })
        .Concat(physicalPairFixture.Endpoints.Select(e => e with { EndpointId = $"other-{e.EndpointId}", ContainerId = "{PHYSICAL-B}", FriendlyName = "Other " + e.FriendlyName }))
        .ToArray()
};
var ambiguousWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = ambiguousDiscovery, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true };
var ambiguousVm = new MainViewModel(worker: ambiguousWorker, automaticScanWorkflow: new GuiAutomaticScanWorkflow(ambiguousDiscovery));
ambiguousVm.AutomaticScanEngine = "V1_COMPATIBLE";
ambiguousWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for ambiguous selector fixture."));
var ambiguousWindow = new MainWindow(ambiguousVm) { Width = 1280, Height = 800, ShowActivated = false };
ambiguousWindow.Show(); ambiguousWindow.UpdateLayout(); ambiguousVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => ambiguousVm.PhysicalDeviceGroups.Count == 2, TimeSpan.FromSeconds(2)), "ambiguous selector discovery exposes both physical groups");
ambiguousVm.SelectedPhysicalDeviceGroup = ambiguousVm.PhysicalDeviceGroups.Single(g => g.GroupId == "container:{PHYSICAL-A}"); ambiguousWindow.UpdateLayout();
Check(ambiguousVm.ShowNormalPairSelection && ambiguousVm.DuplexCaptureEndpoints.Select(e => e.EndpointId).OrderBy(x => x).SequenceEqual(new[] { "local-second-capture-id", "local-windows-id" }) && ambiguousVm.DuplexRenderEndpoints.Select(e => e.EndpointId).SequenceEqual(new[] { "local-render-id" }), "ambiguous normal pairing exposes only endpoints from the selected physical group");
ambiguousWindow.Close();
var audientLikeDiscovery = physicalPairFixture with
{
    Endpoints =
    [
        physicalPairFixture.Endpoints[0] with { EndpointId = "audient-analogue-capture", ContainerId = "{AUDIENT}", FriendlyName = "Analogue 1/2 (2- Audient iD4)", FormFactor = "MICROPHONE" },
        physicalPairFixture.Endpoints[0] with { EndpointId = "audient-loopback-capture", ContainerId = "{AUDIENT}", FriendlyName = "Loop-back 1/2 (2- Audient iD4)", FormFactor = "UNKNOWN" },
        physicalPairFixture.Endpoints[1] with { EndpointId = "audient-analogue-render", ContainerId = "{AUDIENT}", FriendlyName = "Analogue 1/2 (2- Audient iD4)", FormFactor = "LINE_LEVEL" },
        physicalPairFixture.Endpoints[1] with { EndpointId = "audient-loopback-render", ContainerId = "{AUDIENT}", FriendlyName = "Loop-back 1/2 (2- Audient iD4)", FormFactor = "UNKNOWN" },
        physicalPairFixture.Endpoints[1] with { EndpointId = "second-device-render", ContainerId = "{SECOND}", FriendlyName = "Second device Render" }
    ]
};
var audientWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = audientLikeDiscovery, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true };
var audientVm = new MainViewModel(worker: audientWorker, automaticScanWorkflow: new GuiAutomaticScanWorkflow(audientLikeDiscovery));
audientVm.AutomaticScanEngine = "V1_COMPATIBLE";
audientWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for WPF endpoint selector fixture."));
var audientWindow = new MainWindow(audientVm) { Width = 1280, Height = 800, ShowActivated = false };
audientWindow.Show(); audientWindow.UpdateLayout(); audientVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => audientVm.PhysicalDeviceGroups.Count == 2, TimeSpan.FromSeconds(2)), "Audient-like WPF fixture exposes the Audient and second physical groups");
var audientGroup = audientVm.PhysicalDeviceGroups.Single(g => g.GroupId == "container:{AUDIENT}");
audientVm.SelectedPhysicalDeviceGroup = audientGroup; audientWindow.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); audientWindow.UpdateLayout();
var capturePairSelector = (ComboBox)audientWindow.FindName("NormalCapturePairSelector");
var renderPairSelector = (ComboBox)audientWindow.FindName("NormalRenderPairSelector");
var audientScanButton = (Button)audientWindow.FindName("DeviceScanButton");
Check(capturePairSelector.Items.Cast<WindowsEndpointDto>().Select(e => e.EndpointId).SequenceEqual(new[] { "audient-analogue-capture" }) && renderPairSelector.Items.Cast<WindowsEndpointDto>().Select(e => e.EndpointId).OrderBy(x => x).SequenceEqual(new[] { "audient-analogue-render", "audient-loopback-render" }) && audientVm.SelectedDuplexCaptureEndpoint?.EndpointId == "audient-analogue-capture" && audientVm.SelectedDuplexRenderEndpoint is null && !audientGroup.DisplayName.Contains("Loop-back", StringComparison.OrdinalIgnoreCase), "actual WPF ComboBoxes contain only eligible Audient members, preselect the unique Capture, leave ambiguous Render empty and show a stable group name");
capturePairSelector.SelectedIndex = 0; renderPairSelector.SelectedIndex = 0; audientWindow.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
Check(audientVm.SelectedDuplexCaptureEndpoint?.EndpointId == "audient-analogue-capture" && audientVm.SelectedDuplexRenderEndpoint?.EndpointId == "audient-analogue-render" && audientScanButton.IsEnabled, "actual WPF endpoint selection retains the valid pair and enables Scan");
audientVm.SelectedPhysicalDeviceGroup = audientVm.PhysicalDeviceGroups.Single(g => g.GroupId == "container:{SECOND}"); audientWindow.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
Check(capturePairSelector.Items.Count == 0 && renderPairSelector.Items.Cast<WindowsEndpointDto>().Select(e => e.EndpointId).SequenceEqual(new[] { "second-device-render" }) && audientVm.SelectedDuplexCaptureEndpoint is null && audientVm.SelectedDuplexRenderEndpoint is null, "switching physical groups refreshes actual ComboBox Items and clears the old pair");
audientWindow.Close();
var crossDiscovery = physicalPairFixture with
{
    Endpoints =
    [
        physicalPairFixture.Endpoints[0],
        physicalPairFixture.Endpoints[1] with { EndpointId = "cross-render-id", ContainerId = "{PHYSICAL-B}", FriendlyName = "Other Render" }
    ]
};
var crossWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = crossDiscovery, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true };
var crossWorkflow = new GuiAutomaticScanWorkflow(crossDiscovery);
var crossVm = new MainViewModel(worker: crossWorker, automaticScanWorkflow: crossWorkflow);
crossVm.AutomaticScanEngine = "V1_COMPATIBLE";
crossWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for cross-container pairing fixture."));
var crossWindow = new MainWindow(crossVm) { Width = 1280, Height = 800, ShowActivated = false };
crossWindow.Show(); crossWindow.UpdateLayout(); crossVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => crossVm.PhysicalDeviceGroups.Count == 2, TimeSpan.FromSeconds(2)), "cross-container fixture exposes separate Capture and Render groups");
crossVm.SelectedPhysicalDeviceGroup = crossVm.PhysicalDeviceGroups.Single(g => g.GroupId == "container:{PHYSICAL-A}"); crossWindow.UpdateLayout();
var crossScanButton = (Button)crossWindow.FindName("DeviceScanButton");
Check(crossVm.SelectedWindowsEndpoint?.EndpointId == "local-windows-id" && crossScanButton.IsEnabled && crossVm.ShowNormalPairSelection && crossVm.CanOfferUnconfirmedPhysicalPair, "single-direction physical group permits partial Capture scan and offers explicit cross-group pairing");
crossVm.AllowUnconfirmedPhysicalPair = true;
crossVm.SelectedDuplexCaptureEndpoint = crossVm.DuplexCaptureEndpoints.Single(e => e.EndpointId == "local-windows-id");
crossVm.SelectedDuplexRenderEndpoint = crossVm.DuplexRenderEndpoints.Single(e => e.EndpointId == "cross-render-id");
crossScanButton.Command!.Execute(crossScanButton.CommandParameter); PumpUntil(() => crossVm.AutomaticScanConsentPending, TimeSpan.FromSeconds(2));
Check(crossVm.AutomaticScanConsentPending && crossWorkflow.LastSelection?.AllowUnconfirmedPair == true && crossWorkflow.PreparedPlan?.Target.PairIdentity == ScanPairIdentity.OperatorSelectedPair && crossWorkflow.PreparedPlan.Target.Capture?.EndpointId == "local-windows-id" && crossWorkflow.PreparedPlan.Target.Render?.EndpointId == "cross-render-id" && crossVm.AutomaticScanConsentText.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticScanIdentityUnconfirmed"), StringComparison.OrdinalIgnoreCase), "explicit cross-container pair prepares as OPERATOR_SELECTED with one consent and exact endpoint IDs");
crossVm.CancelAutomaticScanConsentCommand.Execute(null); crossWindow.Close();
var duplexUiWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsPairFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true };
var duplexUiVm = new MainViewModel(worker: duplexUiWorker);
duplexUiWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for injected Duplex fixture."));
duplexUiVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => duplexUiVm.WindowsEndpoints.Count == 2, TimeSpan.FromSeconds(2)), "explicit discovery supplies separate Capture and Render pair choices");
Check(duplexUiVm.SelectedDuplexCaptureEndpoint is null && duplexUiVm.SelectedDuplexRenderEndpoint is null, "Duplex never pairs endpoints automatically");
duplexUiVm.SelectedDuplexCaptureEndpoint = duplexUiVm.DuplexCaptureEndpoints.Single();
duplexUiVm.SelectedDuplexRenderEndpoint = duplexUiVm.DuplexRenderEndpoints.Single();
duplexUiVm.NavigateOperatorStep(OperatorStep.Test);
Check(duplexUiVm.RequestDuplexMinimumCommand.CanExecute(null) && duplexUiVm.RequestDuplexDefaultCommand.CanExecute(null), "selected active exact pair exposes independent period operations");
duplexUiVm.RequestDuplexMinimumCommand.Execute(null);
Check(duplexUiVm.DuplexConsentPending && !duplexUiVm.ConfirmDuplexValidationCommand.CanExecute(null), "Duplex requires a fresh checked consent before operation dispatch");
duplexUiVm.CancelDuplexConsentCommand.Execute(null);
Check(!duplexUiVm.DuplexConsentPending && duplexUiWorker.DuplexCalls == 0, "cancelled Duplex consent clears authorization without contacting worker");
duplexUiWorker.DuplexHandler = (operationId, captureId, renderId, period, _) => Task.FromResult(DuplexReport(operationId, captureId, renderId, period));
void RunDuplexUiPeriod(RelayCommand request)
{
    request.Execute(null); duplexUiVm.DuplexConsentChecked = true;
    var context = SynchronizationContext.Current;
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
    duplexUiVm.ConfirmDuplexValidationCommand.Execute(null);
    SynchronizationContext.SetSynchronizationContext(context);
    Check(PumpUntil(() => !duplexUiVm.DuplexValidationRunning, TimeSpan.FromSeconds(3)), "Duplex worker operation reaches a terminal UI state");
}
RunDuplexUiPeriod(duplexUiVm.RequestDuplexMinimumCommand);
Check(duplexUiVm.WorkflowStep == OperatorStep.Test && duplexUiVm.DuplexResultCards[0].FinalStatus == "DUPLEX_CLEAN" && duplexUiVm.DuplexResultCards[1].FinalStatus == "NOT_RUN" && duplexUiVm.RequestDuplexDefaultCommand.CanExecute(null) && duplexUiVm.WorkerState.State == WorkerConnectionState.ConnectedFake && duplexUiVm.HasCurrentDuplexPair, "accepted MINIMUM stays on the Test step with current worker/discovery and DEFAULT action available");
RunDuplexUiPeriod(duplexUiVm.RequestDuplexDefaultCommand);
Check(duplexUiVm.WorkflowStep == OperatorStep.Test && duplexUiVm.DuplexResultCards.Select(c => c.Period).SequenceEqual(new[] { "MINIMUM", "DEFAULT" }) && duplexUiVm.DuplexResultCards.All(c => c.FinalStatus == "DUPLEX_CLEAN"), "sequential MINIMUM and DEFAULT results stay on Test and remain available together");
Check(duplexUiVm.DuplexRecommendation.Preferred?.Period == "MINIMUM" && duplexUiVm.DuplexRecommendation.Fallback?.Period == "DEFAULT" && duplexUiVm.DuplexRecommendation.AudioHostEligibility == "NOT_CERTIFIED", "UI result preserves provisional Probe recommendation separately from AudioHost eligibility");
Check(duplexUiWorker.DuplexCalls == 2 && duplexUiWorker.DuplexConsentIds.Distinct().Count() == 2 && duplexUiWorker.LastDuplexConsent?.RequestFingerprint == DuplexConsent.Fingerprint("local-windows-id", "local-render-id", "DEFAULT", 5, "OFF") && duplexUiVm.CanSaveDeviceProfile && duplexUiVm.CreateCurrentDeviceProfile().SchemaVersion == 3, "period-specific fresh consent and pair-bound profile v3 use exact immutable settings");
var duplexSaveDirectory = Path.Combine(Path.GetTempPath(), $"klidan-duplex-profile-save-{Guid.NewGuid():N}");
try
{
    var pairProfileToSave = duplexUiVm.CreateCurrentDeviceProfile();
    var duplexProfilePath = DeviceConfigurationProfileStorage.SaveLocal(pairProfileToSave, duplexSaveDirectory);
    var reloadedDuplexProfile = DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(duplexProfilePath));
    Check(reloadedDuplexProfile.SchemaVersion == 3 && reloadedDuplexProfile.DuplexObservations.Select(o => o.Period).OrderBy(p => p).SequenceEqual(new[] { "DEFAULT", "MINIMUM" }) && reloadedDuplexProfile.DuplexObservations.All(o => o.Report.GetProperty("schemaVersion").GetInt32() == 2) && reloadedDuplexProfile.Recommendation.Scope == "PROBE_PROVISIONAL" && reloadedDuplexProfile.AudioHostAssessments.All(a => !a.EligibleForRecommendation), "atomic local profile save/reload retains both v2 Duplex reports in v3 and keeps AudioHost NOT_CERTIFIED");
    Throws<IOException>(() => DeviceConfigurationProfileStorage.SaveLocal(pairProfileToSave, duplexSaveDirectory), "Duplex profile save refuses to overwrite an existing profile");
}
finally { if (Directory.Exists(duplexSaveDirectory)) Directory.Delete(duplexSaveDirectory, recursive: true); }
var duplexSaveNotifications = new List<string>();
duplexUiVm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(duplexUiVm.HasDuplexPairProfileEvidence) or nameof(duplexUiVm.CanSaveDeviceProfile)) duplexSaveNotifications.Add(e.PropertyName); };
duplexUiVm.NavigateOperatorStep(OperatorStep.Result);
var duplexSaveWindow = new MainWindow(duplexUiVm) { Width = 1024, Height = 720, ShowActivated = false };
duplexSaveWindow.Show(); duplexSaveWindow.UpdateLayout();
var duplexSaveButton = (Button)duplexSaveWindow.FindName("SaveDeviceProfileButton");
var duplexStreamNote = (TextBlock)duplexSaveWindow.FindName("OperatorResultStreamEvidenceNoteText");
Check(duplexSaveWindow.FindName("SingleEndpointScanResultCard") is Border { Visibility: Visibility.Collapsed } && ((DataGrid)duplexSaveWindow.FindName("OperatorResultFormatGrid")).Visibility == Visibility.Collapsed && ((TextBlock)duplexSaveWindow.FindName("OperatorResultTechnicalDetails")).Visibility == Visibility.Collapsed && ((Expander)duplexSaveWindow.FindName("InitializeTechnicalDetailsExpander")).Visibility == Visibility.Collapsed && !duplexStreamNote.IsVisible && duplexUiVm.IsDuplexOperatorResult, "Duplex Result hides contradictory single-endpoint query, scan and stream placeholders");
duplexUiVm.NavigateOperatorStep(OperatorStep.Test);
var pendingDuplex = new TaskCompletionSource<DuplexValidationDto>(TaskCreationOptions.RunContinuationsAsynchronously);
string? pendingDuplexOperationId = null;
duplexUiWorker.DuplexHandler = (operationId, _, _, _, _) => { pendingDuplexOperationId = operationId; return pendingDuplex.Task; };
duplexUiVm.RequestDuplexMinimumCommand.Execute(null); duplexUiVm.DuplexConsentChecked = true;
var duplexContext = SynchronizationContext.Current;
SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
duplexUiVm.ConfirmDuplexValidationCommand.Execute(null);
SynchronizationContext.SetSynchronizationContext(duplexContext);
duplexSaveWindow.UpdateLayout();
Check(duplexUiVm.DuplexValidationRunning && !duplexUiVm.HasDuplexPairProfileEvidence && !duplexUiVm.CanSaveDeviceProfile && !duplexSaveButton.IsEnabled, "active Duplex validation raises eligibility changes and disables the actual profile-save button");
pendingDuplex.SetResult(DuplexReport(pendingDuplexOperationId!, "local-windows-id", "local-render-id", "MINIMUM"));
Check(PumpUntil(() => !duplexUiVm.DuplexValidationRunning, TimeSpan.FromSeconds(3)), "gated Duplex completion reaches terminal state");
duplexSaveWindow.UpdateLayout();
Check(duplexUiVm.HasDuplexPairProfileEvidence && duplexUiVm.CanSaveDeviceProfile && duplexSaveButton.IsEnabled && duplexSaveNotifications.Count(n => n == nameof(duplexUiVm.HasDuplexPairProfileEvidence)) >= 2 && duplexSaveNotifications.Count(n => n == nameof(duplexUiVm.CanSaveDeviceProfile)) >= 2, "accepted current-pair evidence re-enables Save and notifies both derived eligibility bindings after completion");
duplexSaveWindow.Close();
duplexUiWorker.DuplexHandler = (_, _, _, _, _) => Task.FromException<DuplexValidationDto>(new TimeoutException("injected next attempt timeout"));
RunDuplexUiPeriod(duplexUiVm.RequestDuplexDefaultCommand);
Check(duplexUiVm.DuplexResultCards.All(c => c.FinalStatus == "DUPLEX_CLEAN") && duplexUiVm.DuplexAssessments.Count == 2 && duplexUiVm.DuplexRecommendation.Fallback?.Period == "DEFAULT", "a later failed attempt does not overwrite either previously accepted period report");
duplexUiVm.NavigateOperatorStep(OperatorStep.Result);
var duplexStatusWindow = new MainWindow(duplexUiVm) { Width = 1024, Height = 720, ShowActivated = false }; duplexStatusWindow.Show(); duplexStatusWindow.UpdateLayout();
Check(((TextBlock)duplexStatusWindow.FindName("DuplexWorkerAvailabilityText")).Text == KlidanAudioProbe.Gui.Core.Localization.Get("VmWorkerConnected") && ((TextBlock)duplexStatusWindow.FindName("DuplexDiscoveryFreshnessText")).Text == KlidanAudioProbe.Gui.Core.Localization.Get("VmWindowsDiscoveryCurrent"), "result screen independently presents healthy worker and current endpoint discovery after accepted child completion");
duplexUiWorker.SetState(new(WorkerConnectionState.Disconnected, "Injected worker exit.")); duplexStatusWindow.UpdateLayout();
Check(((TextBlock)duplexStatusWindow.FindName("DuplexWorkerAvailabilityText")).Text.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmWorkerDisconnected"), StringComparison.Ordinal) && ((TextBlock)duplexStatusWindow.FindName("DuplexDiscoveryFreshnessText")).Text.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmWindowsDiscoveryStale"), StringComparison.Ordinal) && duplexUiVm.DuplexAssessments.Count == 2 && !duplexUiVm.RequestDuplexMinimumCommand.CanExecute(null), $"actual worker disconnect marks discovery stale separately, preserves accepted evidence, and disables another run [worker={((TextBlock)duplexStatusWindow.FindName("DuplexWorkerAvailabilityText")).Text}; discovery={((TextBlock)duplexStatusWindow.FindName("DuplexDiscoveryFreshnessText")).Text}; assessments={duplexUiVm.DuplexAssessments.Count}; canRun={duplexUiVm.RequestDuplexMinimumCommand.CanExecute(null)}]");
duplexStatusWindow.Close();
var pairChangeFixture = windowsPairFixture with { Endpoints = windowsPairFixture.Endpoints.Append(windowsFixture.Endpoints[0] with { EndpointId = "other-capture-id", FriendlyName = "Other Capture" }).ToArray() };
var pairChangeWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = pairChangeFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true };
var pairChangeVm = new MainViewModel(worker: pairChangeWorker);
pairChangeWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for pair invalidation fixture."));
pairChangeVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(PumpUntil(() => pairChangeVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2)), "pair invalidation fixture discovers current endpoints");
pairChangeVm.SelectedDuplexCaptureEndpoint = pairChangeVm.DuplexCaptureEndpoints.Single(e => e.EndpointId == "local-windows-id");
pairChangeVm.SelectedDuplexRenderEndpoint = pairChangeVm.DuplexRenderEndpoints.Single();
pairChangeWorker.DuplexHandler = (operationId, captureId, renderId, period, _) => Task.FromResult(DuplexReport(operationId, captureId, renderId, period));
pairChangeVm.RequestDuplexMinimumCommand.Execute(null); pairChangeVm.DuplexConsentChecked = true;
var pairChangeContext = SynchronizationContext.Current;
SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
pairChangeVm.ConfirmDuplexValidationCommand.Execute(null);
SynchronizationContext.SetSynchronizationContext(pairChangeContext);
Check(PumpUntil(() => !pairChangeVm.DuplexValidationRunning, TimeSpan.FromSeconds(3)) && pairChangeVm.CanSaveDeviceProfile, "accepted current pair evidence makes pair-bound profile save eligible");
pairChangeVm.SelectedDuplexCaptureEndpoint = pairChangeVm.DuplexCaptureEndpoints.Single(e => e.EndpointId == "other-capture-id");
Check(!pairChangeVm.HasDuplexPairProfileEvidence && !pairChangeVm.CanSaveDeviceProfile && !pairChangeVm.HasDuplexResults, "changing the selected Capture endpoint invalidates prior-pair evidence and profile-save eligibility");
Throws<InvalidDataException>(() => WindowsEndpointDiscoveryValidation.Validate(windowsFixture with { Provenance = "FAKE_SYNTHETIC" }), "Windows endpoint validator rejects fake provenance on the real metadata command");
Throws<InvalidDataException>(() => WindowsEndpointDiscoveryValidation.Validate(windowsFixture with { OperationState = "PARTIAL" }), "partial enumeration requires explicit HRESULT or failure-stage evidence");
Check(mainWindowXaml.Contains("Visibility=\"{Binding IsSelectedWindowsCapture, Converter={StaticResource BoolVisibility}}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Visibility=\"{Binding IsSelectedWindowsRender, Converter={StaticResource BoolVisibility}}\"", StringComparison.Ordinal), "future Capture and Render test modes bind independently to endpoint direction");
var endpointTemplate = mainWindowXaml.Split("<DataTemplate x:Key=\"EndpointTemplate\"", StringSplitOptions.None)[1].Split("</DataTemplate>", StringSplitOptions.None)[0];
Check(endpointTemplate.Contains("{Binding DisplayName}", StringComparison.Ordinal) && endpointTemplate.Contains("{Binding DisplayState}", StringComparison.Ordinal) && endpointTemplate.Contains("{Binding DisplayFormat}", StringComparison.Ordinal), "demo endpoint rows bind localized display values separately from stored values");
Check(mainWindowXaml.Contains("{loc:Loc DemoMeterLegend}", StringComparison.Ordinal) && mainWindowXaml.Contains("{loc:Loc WorkerFakePipe}", StringComparison.Ordinal) && mainWindowXaml.Contains("{loc:Loc WindowsDevicesTitle}", StringComparison.Ordinal) && mainWindowXaml.Contains("ItemsSource=\"{Binding WindowsEndpointRows}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("EnableRowVirtualization=\"True\"", StringComparison.Ordinal) && !mainWindowXaml.Contains("WindowsEndpointTemplate", StringComparison.Ordinal), "Windows devices use one compact virtualized table, not per-endpoint cards");
var realCandidate = new RealFormatCandidateDto("PCM16",48000,2,"default","STANDARD_MONO_STEREO_MASK","SUPPORTED_REPORTED","SUPPORTED_REPORTED","0x00000000","0x00000000","0x00000000",100000,30000,"NOT_TESTED",false,null,null,null,null,null,"RELEASE_ATTEMPTED") { QueryCleanupStatus = "RELEASE_ATTEMPTED", QueryClientReleaseAttempts = 1 };
var realCandidates = (from rate in new[] { 44100, 48000 } from channels in new[] { 1, 2 } from encoding in new[] { "PCM16", "PCM24_IN_32", "FLOAT32" } from period in new[] { "default", "minimum" } select realCandidate with { Encoding = encoding, SampleRate = rate, Channels = channels, Period = period }).ToArray();
var realQueryResponse = new RealFormatMatrixDto("REAL_WASAPI_QUERY","2026-10-31T00:00:00.000Z","local-windows-id","CAPTURE","PASSIVE_QUERY",realCandidates);
var realInitResponse = realQueryResponse with { Provenance = "REAL_WASAPI_INITIALIZE_ONLY", ReportLevel = "INITIALIZE_ONLY", Candidates = [realCandidate with { Status = "READY", InitializeStatus = "READY", InitializeAttempted = true, FirstInitializeHr = "0x00000000", FinalInitializeHr = "0x00000000", RequestedPeriod100ns = 100000, ActualBufferFrames = 480, SetEventHandleHr = "0x00000000", EventHandleReady = true, ClientReleaseAttempts = 1 }] };
var realWorkflowWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse, RealInitializeResponse = realInitResponse };
var realWorkflowVm = new MainViewModel(worker: realWorkflowWorker);
realWorkflowWorker.SetState(new(WorkerConnectionState.ConnectedFake,"Connected fake."));
realWorkflowVm.DiscoverWindowsEndpointsCommand.Execute(null);
SpinWait.SpinUntil(() => realWorkflowVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
realWorkflowVm.SelectedWindowsEndpoint = realWorkflowVm.WindowsEndpoints.Single();
Check(realWorkflowVm.IsSelectedWindowsCapture && !realWorkflowVm.IsSelectedWindowsRender, "test modes match the selected Capture endpoint direction");
Check(realWorkflowVm.QueryRealFormatsCommand.CanExecute(null), "real query requires the current active selected Windows endpoint and advertised worker capability");
realWorkflowVm.QueryRealFormatsCommand.Execute(null);
SpinWait.SpinUntil(() => realWorkflowVm.RealFormatMatrix is not null, TimeSpan.FromSeconds(2));
Check(realWorkflowWorker.RealQueryCalls == 1 && realWorkflowWorker.LastRealEndpointId == "local-windows-id" && realWorkflowWorker.LastRealDirection == "CAPTURE" && realWorkflowVm.RealFormatMatrix?.Provenance == "REAL_WASAPI_QUERY", "GUI sends exact selected endpoint identity and keeps real query provenance separate");
Check(realWorkflowVm.RealFormatEndpointLabel == "Fixture Interface" && !realWorkflowVm.RealInitializationDetails.Contains("local-windows-id", StringComparison.Ordinal), "real format presentation resolves friendly identity without exposing the raw endpoint ID");
Check(realWorkflowVm.RealFormatRows.Count == 12 && realWorkflowVm.WorkflowStep == OperatorStep.Result && realWorkflowVm.OperatorResultMode == KlidanAudioProbe.Gui.Core.Localization.Get("VmOperatorModeFormatQuery") && realWorkflowVm.OperatorResultOutcome == KlidanAudioProbe.Gui.Core.Localization.Get("VmQuerySuccessOutcome"), "one query produces 12 unique-format rows and a distinct operator result state");
var queryModeTestCulture = KlidanAudioProbe.Gui.Core.Localization.CurrentCulture.Name;
foreach (var language in new[] { "en-US", "ru-RU" })
{
    KlidanAudioProbe.Gui.Core.Localization.SetLanguage(language, false);
    Check(realWorkflowVm.OperatorResultMode == KlidanAudioProbe.Gui.Core.Localization.Get("VmOperatorModeFormatQuery") && !realWorkflowVm.IsDuplexOperatorResult, $"operator mode displays localized format-query label in {language} while retaining its internal result kind");
}
KlidanAudioProbe.Gui.Core.Localization.SetLanguage(queryModeTestCulture, false);
var confidenceCulture = KlidanAudioProbe.Gui.Core.Localization.CurrentCulture.Name;
foreach (var language in new[] { "en-US", "ru-RU" })
{
    KlidanAudioProbe.Gui.Core.Localization.SetLanguage(language, false);
    var confidenceVm = new MainViewModel();
    var confidenceCode = "PROVISIONAL_SINGLE_SUSTAINED_RUN";
    var confidenceAssessment = new DuplexCandidateAssessment("capture", "render", 48000, "FLOAT32", 2, "CUSTOM:100000:480", "PRO_AUDIO", 30, "CLEAN", confidenceCode, "480", "480", "FULL", "FULL", "fixture", default);
    var confidencePreview = new OfflineSustainedProfileRecoveryPreview("session", "summary", "profile", null!, [], new DuplexRecommendation("SUSTAINED_VALIDATED", "NOT_CERTIFIED", confidenceAssessment, null, []), "fingerprint", DateTimeOffset.UtcNow, "scans", "profiles", "evidence");
    typeof(MainViewModel).GetField("_historicalRecoveryPreview", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(confidenceVm, confidencePreview);
    var expectedConfidence = language == "ru-RU" ? "ПРЕДВАРИТЕЛЬНАЯ: ОДИН ДЛИТЕЛЬНЫЙ ЗАПУСК (PROVISIONAL_SINGLE_SUSTAINED_RUN)" : confidenceCode;
    Check(confidenceVm.HistoricalRecoveryConfidence == expectedConfidence, $"historical confidence {confidencePreview.Recommendation.Preferred!.Confidence} displays as {expectedConfidence} in {language}");
    var periodVm = new MainViewModel();
    var periodResult = new PeriodDiscoverySeriesResult("COMPLETED", [], [], "BOTH_EXACT", @"C:\evidence\period.json", "SAVED_AND_RELOADED");
    typeof(MainViewModel).GetField("_periodDiscoveryResult", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(periodVm, periodResult);
    Check(periodVm.PeriodDiscoveryResultText.Contains(language == "ru-RU" ? "Состояние:" : "Lifecycle:", StringComparison.Ordinal) && periodVm.PeriodDiscoveryResultText.Contains("COMPLETED", StringComparison.Ordinal) && periodVm.PeriodDiscoveryResultText.Contains("BOTH_EXACT", StringComparison.Ordinal) && periodVm.PeriodDiscoveryResultText.Contains("SAVED_AND_RELOADED", StringComparison.Ordinal) && periodVm.PeriodDiscoveryResultText.Contains(periodResult.ReportPath!, StringComparison.Ordinal), $"period discovery localizes labels in {language} while preserving evidence values and path");
    var customVm = new MainViewModel();
    var customResult = new CustomPeriodDuplexResult("FAILED", Guid.NewGuid().ToString("D"), "CUSTOM:100000:480", @"C:\evidence\custom.json", "SAVE_FAILED", null, "IOException: fixture");
    typeof(MainViewModel).GetField("_customPeriodDuplexResult", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(customVm, customResult);
    Check(customVm.CustomPeriodDuplexResultText.Contains(language == "ru-RU" ? "Состояние:" : "Lifecycle:", StringComparison.Ordinal) && customVm.CustomPeriodDuplexResultText.Contains("FAILED", StringComparison.Ordinal) && customVm.CustomPeriodDuplexResultText.Contains("SAVE_FAILED", StringComparison.Ordinal) && customVm.CustomPeriodDuplexResultText.Contains(customResult.ReportPath!, StringComparison.Ordinal) && customVm.CustomPeriodDuplexResultText.Contains("IOException: fixture", StringComparison.Ordinal), $"custom-period summary localizes labels in {language} while preserving status, error and path");
}
KlidanAudioProbe.Gui.Core.Localization.SetLanguage(confidenceCulture, false);
Check(realWorkflowVm.RealFormatRows.First().DisplayStatus == "SUPPORTED_REPORTED" && realWorkflowVm.SelectedRealFormatRow?.DisplayStatus == "SUPPORTED_REPORTED" && realWorkflowVm.RealFormatSummary.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmRealFormatSummary").Split("{0}", StringSplitOptions.None)[0], StringComparison.Ordinal) && realWorkflowVm.RealFormatSummary.Contains("Initialize: NOT_ATTEMPTED", StringComparison.Ordinal), "supported formats sort first and the compact summary separates query counts from unattempted Initialize");
Check(!realWorkflowVm.OperatorResultOutcome.Contains("CLEAN", StringComparison.Ordinal) && !realWorkflowVm.OperatorResultOutcome.Contains("Started", StringComparison.Ordinal), "successful query result does not claim clean stream quality or audio start");
var scanMatrixResponse = realQueryResponse with { Candidates = realCandidates.Select(c => c.SampleRate == 44100 ? c with { Status = "UNSUPPORTED", SupportStatus = "UNSUPPORTED", SupportHr = "0x88890008" } : c).ToArray() };
var buttonWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true };
var automaticUiWorkflow = new GuiAutomaticScanWorkflow(windowsFixture) { HoldRunUntilReleased = true };
var originalUiContext = SynchronizationContext.Current;
SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
var buttonVm = new MainViewModel(worker: buttonWorker, automaticScanWorkflow: automaticUiWorkflow);
buttonVm.AutomaticScanEngine = "V1_COMPATIBLE";
var automaticResultNotifications = new HashSet<string>(StringComparer.Ordinal);
buttonVm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(buttonVm.AutomaticScanResultHeadline) or nameof(buttonVm.AutomaticScanResultDevice) or nameof(buttonVm.AutomaticScanResultSummary) or nameof(buttonVm.AutomaticScanRecommendationText) or nameof(buttonVm.AutomaticScanRecommendationPeriodDisplay) or nameof(buttonVm.AutomaticScanPersistenceText)) automaticResultNotifications.Add(e.PropertyName); };
var buttonWindow = new MainWindow(buttonVm) { Width = 1280, Height = 800, ShowActivated = false };
buttonWindow.Show(); buttonWindow.UpdateLayout();
var actualScanButton = (Button)buttonWindow.FindName("DeviceScanButton");
buttonVm.DiscoverWindowsEndpointsCommand.Execute(null);
PumpUntil(() => buttonVm.HasCurrentWindowsEndpoints && automaticUiWorkflow.RefreshCalls == 1, TimeSpan.FromSeconds(2));
buttonVm.SelectedWindowsEndpoint = buttonVm.WindowsEndpoints.Single(); buttonWindow.UpdateLayout();
Check(actualScanButton.IsEnabled, "actual WPF one-click Scan button enables only after connection, fresh discovery and exact active endpoint selection");
Check(((Grid)buttonWindow.FindName("WindowsEndpointFilterGrid")).Visibility == Visibility.Collapsed && ((DataGrid)buttonWindow.FindName("WindowsEndpointGrid")).Visibility == Visibility.Collapsed, "normal scanner hides endpoint search and technical browser while retaining them for Developer Tools");
actualScanButton.Command!.Execute(actualScanButton.CommandParameter); buttonWindow.UpdateLayout();
Check(buttonVm.AutomaticScanConsentPending && automaticUiWorkflow.RunCalls == 0 && !actualScanButton.IsEnabled, "one immutable plan prompts once and does not execute before operator consent");
Check(buttonVm.AutomaticScanStagePlanText.Contains($"{KlidanAudioProbe.Gui.Core.Localization.Get("VmScanStageDiscovery")}: {KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticScanStatePlanned")}", StringComparison.Ordinal) && buttonVm.AutomaticScanStagePlanText.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticScanStageMessageBounded"), StringComparison.Ordinal) && !buttonVm.AutomaticScanStagePlanText.Contains("DISCOVERY: PLANNED", StringComparison.Ordinal), $"stage plan localizes labels and fixed explanations while preserving machine enum values [{buttonVm.AutomaticScanStagePlanText}]");
Check(buttonVm.AutomaticScanConsentText.Contains($"{KlidanAudioProbe.Gui.Core.Localization.Get("VmScanStageDiscovery")}={KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticScanStatePlanned")}", StringComparison.Ordinal) && !buttonVm.AutomaticScanConsentText.Contains("DISCOVERY=PLANNED", StringComparison.Ordinal), "consent summary localizes stage labels without changing staged-plan enum values");
buttonVm.CancelAutomaticScanConsentCommand.Execute(null); buttonWindow.UpdateLayout();
Check(!buttonVm.AutomaticScanConsentPending && automaticUiWorkflow.RunCalls == 0 && actualScanButton.IsEnabled, "refusing consent clears pending approval and performs no operation");
 actualScanButton.Command!.Execute(actualScanButton.CommandParameter);
 buttonVm.AutomaticScanConsentChecked = true;
 Check(buttonVm.ConfirmAutomaticScanCommand.CanExecute(null), "V1 single-endpoint confirmation remains enabled for its exact prepared endpoint");
 buttonVm.ConfirmAutomaticScanCommand.Execute(null);
PumpUntil(() => automaticUiWorkflow.RunStarted.Task.IsCompleted, TimeSpan.FromSeconds(2)); buttonWindow.UpdateLayout();
Check(buttonVm.AutomaticScanRunning && !actualScanButton.IsEnabled && !buttonVm.PrepareAutomaticScanCommand.CanExecute(null) && buttonVm.WorkflowStep == OperatorStep.Test, $"accepted scan enters the actual running screen, disables duplicate scans and keeps cancellation visible [running={buttonVm.AutomaticScanRunning}; button={actualScanButton.IsEnabled}; prepare={buttonVm.PrepareAutomaticScanCommand.CanExecute(null)}; step={buttonVm.WorkflowStep}; status={buttonVm.AutomaticScanStatus}]");
buttonVm.OperatorStepCommand.Execute(OperatorStep.Device); buttonWindow.UpdateLayout();
Check(buttonVm.WorkflowStep == OperatorStep.Test, "navigation cannot hide an active scan and its Stop control");
buttonVm.CancelAutomaticScanCommand.Execute(null);
Check(buttonVm.AutomaticScanRunning && !buttonVm.HasAutomaticScanResult, "cancellation request does not prematurely claim terminal completion");
automaticUiWorkflow.ReleaseRun.TrySetResult();
PumpUntil(() => !buttonVm.AutomaticScanRunning && buttonVm.AutomaticScanResult is not null, TimeSpan.FromSeconds(3)); buttonWindow.UpdateLayout();
Check(buttonVm.AutomaticScanResult?.Lifecycle == ScanRunLifecycle.Cancelled && buttonVm.AutomaticScanProgressRows.Count > 0 && buttonVm.AutomaticScanPersistenceText.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticScanReportNotSaved"), StringComparison.OrdinalIgnoreCase) && buttonWorker.RealQueryCalls == 0, "cancelled test-only workflow preserves operation progress without real hardware calls or a fabricated saved report");
var automaticResultPanel = (StackPanel)buttonWindow.FindName("OperatorResultPanel");
Check(buttonVm.AutomaticScanResultProvenance.StartsWith("UNKNOWN", StringComparison.Ordinal) && automaticResultPanel.Visibility == Visibility.Visible && ((StackPanel)buttonWindow.FindName("DevicesPanel")).Visibility == Visibility.Collapsed && ((StackPanel)buttonWindow.FindName("OperatorTestPanel")).Visibility == Visibility.Collapsed, "result screen alone presents unknown provenance and retains the terminal scan result");
Check(automaticResultNotifications.SetEquals([nameof(buttonVm.AutomaticScanResultHeadline), nameof(buttonVm.AutomaticScanResultDevice), nameof(buttonVm.AutomaticScanResultSummary), nameof(buttonVm.AutomaticScanRecommendationText), nameof(buttonVm.AutomaticScanRecommendationPeriodDisplay), nameof(buttonVm.AutomaticScanPersistenceText)]) && ((TextBlock)buttonWindow.FindName("AutomaticScanResultHeadlineText")).Text == KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticScanCancelled") && ((TextBlock)buttonWindow.FindName("AutomaticScanResultDeviceText")).Text == "Fixture Interface" && ((TextBlock)buttonWindow.FindName("AutomaticScanRecommendationText")).Text.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticScanFinalConfigurationMissing").Split("{0}", StringSplitOptions.None)[0], StringComparison.Ordinal) && ((TextBlock)buttonWindow.FindName("AutomaticScanPersistenceText")).Text == KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticScanReportNotSaved"), $"actual WPF result bindings receive terminal headline, device, summary, final selection and persistence notifications [notifs={string.Join(",", automaticResultNotifications)}; headline={((TextBlock)buttonWindow.FindName("AutomaticScanResultHeadlineText")).Text}; device={((TextBlock)buttonWindow.FindName("AutomaticScanResultDeviceText")).Text}; rec={((TextBlock)buttonWindow.FindName("AutomaticScanRecommendationText")).Text}; persist={((TextBlock)buttonWindow.FindName("AutomaticScanPersistenceText")).Text}]");
Check(buttonVm.AutomaticScanPairEnabled == false && automaticUiWorkflow.PreparedPlan?.Target.Capture?.EndpointId == "local-windows-id" && automaticUiWorkflow.LastConfirmationId != Guid.Empty, "authorization is issued only after consent and binds the refreshed exact endpoint");
buttonVm.OpenManualRealToolsCommand.Execute(null); buttonWindow.UpdateLayout();
Check(buttonVm.DeveloperToolsOpen && buttonVm.WorkflowStep == OperatorStep.Test && ((StackPanel)buttonWindow.FindName("OperatorTestPanel")).Visibility == Visibility.Visible, "legacy manual real controls remain available only through Developer Tools");
buttonVm.CloseDeveloperToolsCommand.Execute(null); buttonWindow.Close();
SynchronizationContext.SetSynchronizationContext(originalUiContext);
buttonWindow.Close();
var scanWorkflowWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true, RealMatrixResponse = scanMatrixResponse };
var previousSynchronizationContext = SynchronizationContext.Current;
SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
var scanWorkflowVm = new MainViewModel(worker: scanWorkflowWorker);
SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
scanWorkflowWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected fake."));
PumpUntil(() => scanWorkflowVm.WorkerState.State == WorkerConnectionState.ConnectedFake, TimeSpan.FromSeconds(2));
scanWorkflowVm.DiscoverWindowsEndpointsCommand.Execute(null);
PumpUntil(() => scanWorkflowVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
scanWorkflowVm.SelectedWindowsEndpoint = scanWorkflowVm.WindowsEndpoints.Single();
scanWorkflowWorker.RealInitializeFactory = (endpointId, channels, encoding, period) => realInitResponse with
{
    EndpointId = endpointId,
    Direction = "CAPTURE",
    TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
    Candidates = [realCandidate with { Channels = channels, Encoding = encoding, Period = period, Status = period == "minimum" && channels == 1 ? "FAILED" : "INITIALIZED", InitializeStatus = period == "minimum" && channels == 1 ? "FAILED" : "INITIALIZED", InitializeAttempted = true, FirstInitializeHr = period == "minimum" && channels == 1 ? "0x8889000A" : "0x00000000", FinalInitializeHr = period == "minimum" && channels == 1 ? "0x8889000A" : "0x00000000", RequestedPeriod100ns = period == "minimum" ? 30000 : 100000, ActualBufferFrames = period == "minimum" && channels == 1 ? null : period == "minimum" ? 144u : 480u, EventHandleReady = period == "minimum" && channels == 1 ? null : true, SetEventHandleHr = period == "minimum" && channels == 1 ? null : "0x00000000", ClientReleaseAttempts = 1 }]
};
scanWorkflowVm.RequestDeviceScanCommand.Execute(null);
Check(scanWorkflowVm.DeviceScanConsentPending && scanWorkflowWorker.RealQueryCalls == 0 && !scanWorkflowVm.DeviceScanConsentChecked, "automatic scan opens a fresh consent gate without starting any endpoint operation");
scanWorkflowVm.CancelDeviceScanConsentCommand.Execute(null);
Check(!scanWorkflowVm.DeviceScanConsentPending && scanWorkflowWorker.RealInitializeCalls == 0, "cancelled scan consent clears the plan and performs no Initialize");
scanWorkflowVm.RequestDeviceScanCommand.Execute(null);
var scanConsentCulture = KlidanAudioProbe.Gui.Core.Localization.CurrentCulture.Name;
foreach (var (language, streamPhrase, durationPhrase) in new[]
{
    ("en-US", "Streams will not start", "180 seconds"),
    ("ru-RU", "Потоки не запускаются", "180 секунд")
})
{
    KlidanAudioProbe.Gui.Core.Localization.SetLanguage(language, false);
    var consentText = scanWorkflowVm.DeviceScanConsentText;
    Check(consentText.Contains("Initialize-only", StringComparison.OrdinalIgnoreCase) && consentText.Contains(streamPhrase, StringComparison.OrdinalIgnoreCase) && consentText.Contains(durationPhrase, StringComparison.Ordinal), $"scan consent describes real operations, no Start/output, and bounded duration in {language}");
}
KlidanAudioProbe.Gui.Core.Localization.SetLanguage(scanConsentCulture, false);
scanWorkflowVm.DeviceScanConsentChecked = true;
scanWorkflowVm.ConfirmDeviceScanCommand.Execute(null);
SpinWait.SpinUntil(() => !scanWorkflowVm.DeviceScanRunning && scanWorkflowVm.DeviceScanResult is not null, TimeSpan.FromSeconds(5));
Check(scanWorkflowWorker.RealQueryCalls == 1 && scanWorkflowWorker.RealInitializeCalls == 4 && scanWorkflowVm.DeviceScanResult?.State == DeviceScanStage.Incomplete && scanWorkflowVm.DeviceScanInitializeCandidates.All(c => c.SampleRate == 48000 && c.Encoding == "PCM16"), "one scan queries selected endpoint then initializes only four bounded 48 kHz PCM16 Capture candidates");
Check(scanWorkflowVm.DeviceScanInitializeCandidates.Any(c => c.Period == "minimum" && c.Channels == 1 && c.InitializeStatus == "FAILED" && c.FinalInitializeHr == "0x8889000A") && scanWorkflowVm.DeviceScanInitializeCandidates.Any(c => c.Period == "default" && c.Channels == 1 && c.InitializeStatus == "INITIALIZED"), "failed MINIMUM Initialize is preserved and the DEFAULT fallback candidate is still checked independently");
Check(scanWorkflowVm.DeviceScanTechnicalDetails.Contains($"{KlidanAudioProbe.Gui.Core.Localization.Get("StatusLabel")}=" , StringComparison.OrdinalIgnoreCase) && scanWorkflowVm.DeviceScanTechnicalDetails.Contains("IsFormatSupported=", StringComparison.Ordinal) && scanWorkflowVm.DeviceScanTechnicalDetails.Contains("0x8889000A", StringComparison.Ordinal), "scan technical details localize presentation labels while preserving API names and HRESULT values");
Check(scanWorkflowVm.DeviceScanResult is { StreamValidationRun: false, DuplexEvidenceRun: false } && scanWorkflowVm.DeviceScanResultSummary.Contains("NOT_RUN", StringComparison.Ordinal) && scanWorkflowVm.DeviceScanResult.Recommendation.Contains("не проверялись", StringComparison.Ordinal), "scan never fabricates stream, stability, Duplex or recommendation evidence");
Check(scanWorkflowVm.CanSaveDeviceProfile && scanWorkflowVm.CreateCurrentDeviceProfile().Evidence.Count == 28 && scanWorkflowVm.CreateCurrentDeviceProfile().Recommendation.Preferred is null, "automatic scan profile aggregates query and all accepted Initialize results without a recommendation");
Check(scanWorkflowWorker.LastStartSettings is null, "real automatic scan never routes through fake StartSession or synthesizes stream evidence");
var automaticProfile = scanWorkflowVm.CreateCurrentDeviceProfile();
Check(automaticProfile.Evidence.Any(e => e.Format.SampleRate == 44100 && e.FormatSupport == "UNSUPPORTED") && automaticProfile.Evidence.All(e => e.SourceProvenance != "REAL_WASAPI_INITIALIZE_ONLY" || e.Format is { SampleRate: 48000, Encoding: "PCM16" }), "profile retains unsupported 44.1 kHz query evidence while initializing only compatible 48 kHz PCM16 candidates");
var automaticProfileRoundTrip = DeviceConfigurationProfileJson.Parse(DeviceConfigurationProfileJson.SerializeLocal(automaticProfile));
var automaticSanitized = DeviceConfigurationProfileJson.Parse(DeviceConfigurationProfileJson.SerializeSanitized(automaticProfile));
Check(automaticProfileRoundTrip.Evidence.Count == 28 && automaticProfileRoundTrip.EndpointBindings.Capture?.EndpointId == "local-windows-id" && automaticSanitized.EndpointBindings.Capture is { EndpointId: null, Binding: "SANITIZED_UNBOUND" } && automaticSanitized.Evidence.Count == 28, "aggregated scan profile supports strict local and endpoint-redacted sanitized round trips");
Check(scanWorkflowVm.RequestStreamValidationCommand.CanExecute(null), "stream validation is offered only after successful Initialize on the exact current endpoint");
scanWorkflowVm.RequestStreamValidationCommand.Execute(null);
var streamConsentCulture = KlidanAudioProbe.Gui.Core.Localization.CurrentCulture.Name;
foreach (var (language, noPcmPhrase) in new[] { ("en-US", "without saving PCM"), ("ru-RU", "без сохранения PCM") })
{
    KlidanAudioProbe.Gui.Core.Localization.SetLanguage(language, false);
    Check(scanWorkflowVm.StreamConsentPending && scanWorkflowVm.StreamValidationConsentText.Contains(scanWorkflowVm.SelectedWindowsEndpoint!.EndpointId!, StringComparison.Ordinal) && scanWorkflowVm.StreamValidationConsentText.Contains("Exclusive endpoint", StringComparison.Ordinal) && scanWorkflowVm.StreamValidationConsentText.Contains(noPcmPhrase, StringComparison.OrdinalIgnoreCase), $"stream consent displays exact endpoint, reservation and no PCM persistence in {language}");
}
KlidanAudioProbe.Gui.Core.Localization.SetLanguage(streamConsentCulture, false);
scanWorkflowVm.CancelStreamConsentCommand.Execute(null);
Check(!scanWorkflowVm.StreamConsentPending && scanWorkflowWorker.RealStreamCalls == 0, "cancelled stream consent performs no operation");
scanWorkflowVm.RequestStreamValidationCommand.Execute(null); scanWorkflowVm.StreamConsentChecked = true; scanWorkflowVm.ConfirmStreamValidationCommand.Execute(null);
SpinWait.SpinUntil(() => !scanWorkflowVm.StreamValidationRunning && scanWorkflowVm.StreamValidations.Count == 1, TimeSpan.FromSeconds(2));
Check(scanWorkflowWorker.RealStreamCalls == 1 && scanWorkflowVm.DeviceScanResult?.StreamValidationRun == true && scanWorkflowVm.StreamValidationRows.Any(r => r is { StartOutcome: "SUCCESS", FramesProcessedExpected: "240000 / 240000", StreamQuality: "CLEAN", FinalStatus: "COMPLETED" }), "freshly accepted stream request preserves separate Start, frame-service and quality observations");
Throws<InvalidDataException>(() => scanWorkflowVm.CreateCurrentDeviceProfile(), "synthetic test stream provenance cannot be saved as real WASAPI profile evidence");
scanWorkflowVm.NavigateOperatorStep(OperatorStep.Device);
var scanWindow = new MainWindow(scanWorkflowVm) { Width = 1280, Height = 800, ShowActivated = false };
scanWindow.Show(); scanWindow.UpdateLayout();
var scanButton = (Button)scanWindow.FindName("DeviceScanButton");
Check(scanButton.Content?.ToString() == KlidanAudioProbe.Gui.Core.Localization.Get("ScanButton") && BindingOperations.GetBinding(scanButton, Button.CommandProperty)?.Path.Path == "PrepareAutomaticScanCommand", "real WPF device screen binds the operator scan action to the automatic plan workflow");
scanWindow.Close();
scanWorkflowVm.RequestDeviceScanCommand.Execute(null); scanWorkflowVm.DeviceScanConsentChecked = true; scanWorkflowVm.ConfirmDeviceScanCommand.Execute(null);
PumpUntil(() => !scanWorkflowVm.DeviceScanRunning && scanWorkflowVm.DeviceScanResult is not null, TimeSpan.FromSeconds(5));
var streamProfileDirectory = Path.Combine(Path.GetTempPath(), $"klidan-stream-profile-ui-{Guid.NewGuid():N}");
try
{
    var cleanMinimumGate = new TaskCompletionSource<RealStreamValidationDto>(TaskCreationOptions.RunContinuationsAsynchronously);
    scanWorkflowWorker.RealStreamHandler = (operationId, endpointId, period, _) => period == "MINIMUM"
        ? cleanMinimumGate.Task
        : Task.FromResult(StreamReport(operationId, endpointId, period));
    var streamResultsWindow = new MainWindow(scanWorkflowVm, streamProfileDirectory) { Width = 1024, Height = 720, ShowActivated = false };
    streamResultsWindow.Show(); streamResultsWindow.UpdateLayout();
    var streamCards = (ItemsControl)streamResultsWindow.FindName("StreamValidationResultsCards");
    var streamProgressPanel = (StackPanel)streamResultsWindow.FindName("StreamValidationProgressPanel");
    var streamStopButton = (Button)streamResultsWindow.FindName("StreamValidationStopButton");
    var streamStatusText = (TextBlock)streamResultsWindow.FindName("StreamValidationStatusText");
    var initialMinimumCard = (ContentPresenter?)streamCards.ItemContainerGenerator.ContainerFromIndex(0);
    var initialDefaultCard = (ContentPresenter?)streamCards.ItemContainerGenerator.ContainerFromIndex(1);
    var cardsMarkupStart = mainWindowXaml.IndexOf("StreamValidationResultsCards", StringComparison.Ordinal);
    var cardsMarkupEnd = mainWindowXaml.IndexOf("</ItemsControl>", cardsMarkupStart, StringComparison.Ordinal);
    var cardsMarkup = cardsMarkupEnd > cardsMarkupStart ? mainWindowXaml[cardsMarkupStart..cardsMarkupEnd] : "";
    Check(streamCards.Items.Count == 2 && initialMinimumCard?.Visibility == Visibility.Visible && initialDefaultCard?.Visibility == Visibility.Visible && scanWorkflowVm.StreamValidationRows.All(r => r.FinalStatus == "NOT_RUN") && !mainWindowXaml.Contains("StreamValidationResultsGrid", StringComparison.Ordinal) && !cardsMarkup.Contains("ScrollViewer.HorizontalScrollBarVisibility", StringComparison.Ordinal), "both MINIMUM and DEFAULT NOT_RUN cards are visible at launch with no horizontal results grid");
    BeginStream(scanWorkflowVm, "minimum");
    PumpUntil(() => scanWorkflowVm.StreamValidationRunning, TimeSpan.FromSeconds(2)); streamResultsWindow.UpdateLayout();
    Check(streamProgressPanel.Visibility == Visibility.Visible && streamStopButton.IsVisible, "stream progress and Stop button are visible only during the active MINIMUM attempt");
    cleanMinimumGate.SetResult(StreamReport(Guid.NewGuid().ToString("D"), scanWorkflowVm.SelectedWindowsEndpoint!.EndpointId!, "MINIMUM"));
    PumpUntil(() => !scanWorkflowVm.StreamValidationRunning && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM").FinalStatus == "COMPLETED", TimeSpan.FromSeconds(3)); streamResultsWindow.UpdateLayout();
    var minimumRow = scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM");
    var minimumCard = (ContentPresenter?)streamCards.ItemContainerGenerator.ContainerFromIndex(0);
    Check(streamProgressPanel.Visibility == Visibility.Collapsed && !streamStopButton.IsVisible && streamCards.Visibility == Visibility.Visible && minimumCard is { Visibility: Visibility.Visible, ActualWidth: > 250 } && streamStatusText.Visibility == Visibility.Visible && streamStatusText.Text.Contains("MINIMUM: COMPLETED", StringComparison.Ordinal) && minimumRow is { ActualBufferFrames: "144", InitializeOutcome: "SUCCESS", StartOutcome: "SUCCESS", FramesProcessedExpected: "240000 / 240000", FrameCoverage: "100.0%", FinalStatus: "COMPLETED" } && scanWorkflowVm.OperatorResultOutcome.Contains("MINIMUM stream: COMPLETED", StringComparison.Ordinal), "clean MINIMUM result remains readable in the actual WPF card after StreamValidationRunning becomes false at 1024 DIP");
    BeginStream(scanWorkflowVm, "default");
    PumpUntil(() => !scanWorkflowVm.StreamValidationRunning && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "DEFAULT").FinalStatus == "COMPLETED", TimeSpan.FromSeconds(3)); streamResultsWindow.UpdateLayout();
    var defaultCard = (ContentPresenter?)streamCards.ItemContainerGenerator.ContainerFromIndex(1);
    Check(streamCards.Visibility == Visibility.Visible && streamCards.Items.Count == 2 && minimumCard?.Visibility == Visibility.Visible && defaultCard?.Visibility == Visibility.Visible && streamCards.ActualWidth <= streamResultsWindow.ActualWidth && scanWorkflowVm.StreamValidationRows.Select(r => r.Period).SequenceEqual(["MINIMUM", "DEFAULT"]) && scanWorkflowVm.StreamValidationRows.All(r => r.FinalStatus == "COMPLETED"), "MINIMUM and DEFAULT cards remain visible together without horizontal scrolling at 1024 DIP");
    streamResultsWindow.Width = 1440; streamResultsWindow.Height = 900; streamResultsWindow.UpdateLayout();
    Check(streamCards.ActualWidth > 700 && minimumCard is { ActualWidth: > 330 } && defaultCard is { ActualWidth: > 330 }, "both responsive result cards expand at the 1440-DIP default window width");
    Check(scanWorkflowVm.DeviceScanResultSummary.Contains(string.Format(KlidanAudioProbe.Gui.Core.Localization.CurrentCulture, KlidanAudioProbe.Gui.Core.Localization.Get("VmDeviceScanStreamObservations"), 2), StringComparison.Ordinal) && !scanWorkflowVm.DeviceScanResultSummary.Contains("real stream start and stability were not tested", StringComparison.Ordinal) && scanWorkflowVm.DeviceScanResultSummary.Contains("Duplex: NOT_RUN", StringComparison.Ordinal), "scan summary reflects accepted stream observations while keeping Duplex and long-duration stability NOT_RUN");
    Check(scanWorkflowVm.OperatorResultStreamEvidenceNote.Contains(string.Format(KlidanAudioProbe.Gui.Core.Localization.CurrentCulture, KlidanAudioProbe.Gui.Core.Localization.Get("VmOperatorStreamEvidenceCount"), 2), StringComparison.Ordinal) && scanWorkflowVm.OperatorResultProfileEvidenceNote.Contains("streamObservations", StringComparison.Ordinal) && !mainWindowXaml.Contains("Start не выполнялся.", StringComparison.Ordinal), "result-screen caveats reflect accepted stream reports without overstating stability or Duplex");
    var saveProfileButton = (Button)streamResultsWindow.FindName("SaveDeviceProfileButton");
    saveProfileButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); streamResultsWindow.UpdateLayout();
    var savedPath = scanWorkflowVm.DeviceProfileSaveMessage.Split(": ", 2).Last();
    var savedStreamProfile = DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(savedPath));
    var persistenceBlock = (TextBlock)streamResultsWindow.FindName("StreamEvidencePersistenceStatusText");
    var persistenceText = persistenceBlock.Text;
    Check(saveProfileButton.IsEnabled && savedStreamProfile.SchemaVersion == 2 && savedStreamProfile.StreamObservations.Count == 2 && savedStreamProfile.StreamObservations.Select(s => s.Format.Period).Order().SequenceEqual(["DEFAULT", "MINIMUM"]), "actual WPF Save action writes both accepted period results into profile v2 streamObservations");
    Check(scanWorkflowVm.DeviceProfileSaveMessage.Contains("schemaVersion 2", StringComparison.Ordinal) && scanWorkflowVm.DeviceProfileSaveMessage.Contains(savedPath, StringComparison.Ordinal) && persistenceText.Contains(string.Format(KlidanAudioProbe.Gui.Core.Localization.CurrentCulture, KlidanAudioProbe.Gui.Core.Localization.Get("VmStreamEvidenceSessionSaved"), savedPath), StringComparison.Ordinal) && persistenceText.Contains(savedPath, StringComparison.Ordinal) && persistenceBlock.TextWrapping == TextWrapping.Wrap && persistenceBlock.ActualWidth <= streamCards.ActualWidth + 1, "saved profile path/schema remains readable and wraps within the result content width");

    var longFailureStage = "IAudioClient::Start · " + string.Concat(Enumerable.Repeat("driver failure detail that must wrap without truncation ", 8));
    scanWorkflowWorker.RealStreamHandler = (operationId, endpointId, period, _) => Task.FromResult(StreamReport(operationId, endpointId, period, start: false, frames: 0, coverage: 0, durationReached: false, stop: null, quality: "INCOMPLETE", cleanup: "STOP_NOT_ATTEMPTED;EVENT_RELEASE_ATTEMPTED", errorStage: longFailureStage, errorHresult: "0x80004005"));
    BeginStream(scanWorkflowVm, "minimum");
    PumpUntil(() => !scanWorkflowVm.StreamValidationRunning && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM").FinalStatus == "FAILED", TimeSpan.FromSeconds(3)); streamResultsWindow.UpdateLayout();
    var minimumPresenter = (ContentPresenter)streamCards.ItemContainerGenerator.ContainerFromIndex(0);
    var detailsExpander = (Expander)streamCards.ItemTemplate.FindName("StreamResultDetailsExpander", minimumPresenter);
    detailsExpander.IsExpanded = true; streamResultsWindow.UpdateLayout();
    var detailsText = (TextBlock)streamCards.ItemTemplate.FindName("StreamResultDetailsText", minimumPresenter);
    Check(streamCards.Visibility == Visibility.Visible && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM") is { StartOutcome: "FAILED", FinalStatus: "FAILED", Details: var startFailure } && startFailure.Contains("IAudioClient::Start", StringComparison.Ordinal) && detailsText.TextWrapping == TextWrapping.Wrap && detailsText.ActualHeight > detailsText.FontSize, "long Start failure details wrap in the visible result card without truncation");

    scanWorkflowWorker.RealStreamHandler = (operationId, endpointId, period, _) => Task.FromResult(StreamReport(operationId, endpointId, period, frames: 0, coverage: 0, quality: "INCOMPLETE"));
    BeginStream(scanWorkflowVm, "default");
    PumpUntil(() => !scanWorkflowVm.StreamValidationRunning && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "DEFAULT").FinalStatus == "COMPLETED_WITH_WARNINGS", TimeSpan.FromSeconds(3)); streamResultsWindow.UpdateLayout();
    Check(streamCards.Visibility == Visibility.Visible && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "DEFAULT") is { FramesProcessedExpected: "0 / 240000", FinalStatus: "COMPLETED_WITH_WARNINGS" }, "zero serviced frames remain visible as completed with warnings, never as a working stream");

    scanWorkflowWorker.RealStreamHandler = (_, _, _, _) => Task.FromException<RealStreamValidationDto>(new TimeoutException("worker response deadline expired"));
    BeginStream(scanWorkflowVm, "minimum");
    PumpUntil(() => !scanWorkflowVm.StreamValidationRunning && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM").FinalStatus == "UNKNOWN", TimeSpan.FromSeconds(3)); streamResultsWindow.UpdateLayout();
    Check(streamCards.Visibility == Visibility.Visible && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM") is { FinalStatus: "UNKNOWN", CleanupStatus: "UNKNOWN", Details: var timeoutDetail } && timeoutDetail.Contains("worker response deadline expired", StringComparison.Ordinal) && scanWorkflowVm.StreamEvidencePersistenceStatus.Contains("UNKNOWN", StringComparison.Ordinal) && scanWorkflowVm.OperatorResultOutcome.Contains("worker response deadline expired", StringComparison.Ordinal), $"worker timeout persists UNKNOWN, actual reason and updated persistence/result summaries [{scanWorkflowVm.StreamEvidencePersistenceStatus} | {scanWorkflowVm.OperatorResultOutcome}]");

    using (var missingStreamEvidence = JsonDocument.Parse("{}"))
    {
        var missingRow = (StreamValidationResultRow)typeof(MainViewModel).GetMethod("StreamValidationResultRowFromReport", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["MINIMUM", missingStreamEvidence.RootElement])!;
        var unknownText = KlidanAudioProbe.Gui.Core.Localization.Get("VmStreamUnknownValue");
        Check(missingRow.FinalStatus == "UNKNOWN" && missingRow.ActualBufferFrames == unknownText && missingRow.FramesProcessedExpected == $"{unknownText} / {unknownText}" && missingRow.FrameCoverage == unknownText, "stream projection localizes unknown display values while retaining machine status UNKNOWN");
    }

    var cancelGate = new TaskCompletionSource<RealStreamValidationDto>(TaskCreationOptions.RunContinuationsAsynchronously);
    scanWorkflowWorker.RealStreamHandler = (operationId, endpointId, period, _) => cancelGate.Task;
    scanWorkflowWorker.StreamCancelRequested = () => cancelGate.TrySetResult(StreamReport(Guid.NewGuid().ToString("D"), scanWorkflowVm.SelectedWindowsEndpoint!.EndpointId!, "MINIMUM", start: true, frames: 50000, coverage: 20.8, durationReached: false, quality: "INCOMPLETE", cancelled: true, cleanup: "STOP_SUCCEEDED;EVENT_RELEASE_ATTEMPTED;CLIENT_RELEASE_ATTEMPTED"));
    BeginStream(scanWorkflowVm, "minimum"); PumpUntil(() => scanWorkflowVm.StreamValidationRunning, TimeSpan.FromSeconds(2));
    scanWorkflowVm.CancelStreamValidationCommand.Execute(null);
    PumpUntil(() => !scanWorkflowVm.StreamValidationRunning && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM").FinalStatus == "CANCELLED", TimeSpan.FromSeconds(3)); streamResultsWindow.UpdateLayout();
    Check(streamCards.Visibility == Visibility.Visible && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM") is { FinalStatus: "CANCELLED", StopOutcome: "SUCCESS" }, "operator cancellation keeps the final CANCELLED result and observed Stop evidence visible");
    scanWorkflowWorker.StreamCancelRequested = null;

    scanWorkflowWorker.RealStreamHandler = (operationId, endpointId, period, _) => Task.FromResult(StreamReport(operationId, endpointId, period, cleanup: "STOP_SUCCEEDED;EVENT_RELEASE_FAILED;CLIENT_RELEASE_ATTEMPTED"));
    BeginStream(scanWorkflowVm, "minimum");
    PumpUntil(() => !scanWorkflowVm.StreamValidationRunning && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM").FinalStatus == "COMPLETED_WITH_WARNINGS", TimeSpan.FromSeconds(3)); streamResultsWindow.UpdateLayout();
    Check(streamCards.Visibility == Visibility.Visible && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM") is { CleanupStatus: "STOP_SUCCEEDED;EVENT_RELEASE_FAILED;CLIENT_RELEASE_ATTEMPTED", FinalStatus: "COMPLETED_WITH_WARNINGS" }, "cleanup failure remains visible verbatim and prevents an unqualified completed status");

    scanWorkflowWorker.RealStreamHandler = (_, _, _, _) => { scanWorkflowWorker.SetState(new(WorkerConnectionState.Disconnected, "worker disconnected during stream test")); return Task.FromException<RealStreamValidationDto>(new IOException("worker disconnected during stream test")); };
    BeginStream(scanWorkflowVm, "minimum");
    PumpUntil(() => !scanWorkflowVm.StreamValidationRunning && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM").FinalStatus == "UNKNOWN", TimeSpan.FromSeconds(3)); streamResultsWindow.UpdateLayout();
    Check(streamCards.Visibility == Visibility.Visible && scanWorkflowVm.StreamValidationRows.Single(r => r.Period == "MINIMUM") is { FinalStatus: "UNKNOWN", StopOutcome: "UNKNOWN", CleanupStatus: "UNKNOWN" }, "worker disconnect retains the visible UNKNOWN result without inventing Stop or cleanup");
    streamResultsWindow.Close();
}
finally { if (Directory.Exists(streamProfileDirectory)) Directory.Delete(streamProfileDirectory, recursive: true); }
var renderScanEndpoint = profileEndpoint with { EndpointId = "render-local-id", Direction = "RENDER" };
var renderScanWorker = new GuiStateWorkerClient([]) { AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse with { EndpointId = "render-local-id", Direction = "RENDER" } };
renderScanWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected."));
renderScanWorker.RealInitializeFactory = (endpointId, channels, encoding, period) => realInitResponse with { EndpointId = endpointId, Direction = "RENDER", TimestampUtc = DateTimeOffset.UtcNow.ToString("O"), Candidates = [realInitResponse.Candidates.Single() with { Channels = channels, Encoding = encoding, Period = period }] };
var renderConsent = new DeviceScanConsent(renderScanEndpoint.EndpointId!, "RENDER", DeviceConfigurationScan.Fingerprint(renderScanEndpoint), Guid.NewGuid());
var renderScanResult = DeviceConfigurationScan.RunAsync(renderScanWorker, renderScanEndpoint, renderConsent).GetAwaiter().GetResult();
Check(renderScanWorker.RealInitializeCalls == 2 && renderScanResult.Initializations.All(i => i.Direction == "RENDER" && i.Candidates.Single().Channels == 2), "Render scan independently validates only its exact stereo endpoint and does not infer Capture or Duplex");
var interruptedScanWorker = new GuiStateWorkerClient([]) { AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse with { EndpointId = profileEndpoint.EndpointId! } };
interruptedScanWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected."));
interruptedScanWorker.RealInitializeFactory = (endpointId, channels, encoding, period) =>
{
    var response = realInitResponse with { EndpointId = endpointId, TimestampUtc = DateTimeOffset.UtcNow.ToString("O"), Candidates = [realInitResponse.Candidates.Single() with { Channels = channels, Encoding = encoding, Period = period }] };
    interruptedScanWorker.SetState(new(WorkerConnectionState.Disconnected, "Simulated worker loss."));
    return response;
};
var interruptedScan = DeviceConfigurationScan.RunAsync(interruptedScanWorker, profileEndpoint, new(profileEndpoint.EndpointId!, "CAPTURE", DeviceConfigurationScan.Fingerprint(profileEndpoint), Guid.NewGuid())).GetAwaiter().GetResult();
Check(interruptedScan.State == DeviceScanStage.Incomplete && interruptedScan.Initializations.Count == 1 && interruptedScan.Initializations[0].Candidates.Single().Period == "minimum", "worker loss mid-scan keeps completed evidence and marks the remaining plan incomplete");
var cancelScanWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse, QueryGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
var cancelScanVm = new MainViewModel(worker: cancelScanWorker); cancelScanWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected."));
cancelScanVm.DiscoverWindowsEndpointsCommand.Execute(null); SpinWait.SpinUntil(() => cancelScanVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2)); cancelScanVm.SelectedWindowsEndpoint = cancelScanVm.WindowsEndpoints.Single();
cancelScanVm.RequestDeviceScanCommand.Execute(null); cancelScanVm.DeviceScanConsentChecked = true; cancelScanVm.ConfirmDeviceScanCommand.Execute(null);
SpinWait.SpinUntil(() => cancelScanVm.DeviceScanRunning && cancelScanWorker.RealQueryCalls == 1, TimeSpan.FromSeconds(2));
cancelScanVm.CancelDeviceScanCommand.Execute(null);
SpinWait.SpinUntil(() => !cancelScanVm.DeviceScanRunning && cancelScanVm.DeviceScanResult is not null, TimeSpan.FromSeconds(2));
Check(cancelScanVm.DeviceScanResult?.State == DeviceScanStage.Cancelled && cancelScanWorker.RealInitializeCalls == 0 && !cancelScanVm.CanSaveDeviceProfile, "operator cancellation stops the pending scan, preserves unknown query outcome and prevents Initialize or profile promotion");
var badConsentWorker = new GuiStateWorkerClient([]) { AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse };
badConsentWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected."));
Throws<InvalidOperationException>(() => DeviceConfigurationScan.RunAsync(badConsentWorker, profileEndpoint, new(profileEndpoint.EndpointId!, "CAPTURE", "wrong", Guid.NewGuid())).GetAwaiter().GetResult(), "scan consent fingerprint mismatch fails closed before any worker operation");
var scanCapabilities = new ScanCapabilities(true, true, true, true, true);
var pairedScanTarget = new ScanTarget(profileEndpoint, renderEndpoint, ScanPairIdentity.OperatorSelectedPair, ScanCapturePath.Analogue);
var pairedScanPlan = ScanPlan.Build(pairedScanTarget, scanCapabilities);
ScanSessionAuthorization ScanAuth(ScanPlan plan)
{
    var stages = Enum.GetValues<ScanStage>().ToHashSet();
    return new(Guid.NewGuid(), true, ScanSessionAuthorization.Fingerprint(plan, stages, 5, 24), stages, 5, 24);
}
RealFormatMatrixDto FakeScanQuery(ScanOperation operation)
{
    var channels = operation.Direction == "CAPTURE" ? new[] { 1, 2 } : new[] { 2 };
    var candidates = realCandidates.Where(c => channels.Contains(c.Channels)).Select(c => c with { SupportHr = "0x00000000" }).ToArray();
    return new("TEST_FAKE_WASAPI_QUERY", "2026-11-01T00:00:00Z", operation.EndpointId!, operation.Direction!, "PASSIVE_QUERY", candidates);
}
ScanAdapterResult FakeScanExecution(ScanOperation operation, ScanPlan plan, ScanSessionAuthorization authorization)
{
    if (operation.Stage == ScanStage.Discovery) return new(ScanOperationLifecycle.Completed, Message: "synthetic discovery snapshot current", CleanupStatus: "NOT_APPLICABLE");
    if (operation.Stage == ScanStage.Query) return new(ScanOperationLifecycle.Completed, new(FormatMatrix: FakeScanQuery(operation)), "synthetic query", "RELEASE_ATTEMPTED");
    if (operation.Stage == ScanStage.Initialize)
    {
        var candidate = realCandidate with { Period = operation.Period!, Channels = operation.Channels!.Value, Encoding = operation.Encoding!, Status = "READY", SupportStatus = "SUPPORTED_REPORTED", InitializeStatus = "READY", InitializeAttempted = true, FinalInitializeHr = "0x00000000", ActualBufferFrames = operation.Period == "minimum" ? 144u : 480u };
        var matrix = new RealFormatMatrixDto("TEST_FAKE_WASAPI_INITIALIZE", "2026-11-01T00:00:00Z", operation.EndpointId!, operation.Direction!, "INITIALIZE_ONLY", [candidate]);
        return new(ScanOperationLifecycle.Completed, new(FormatMatrix: matrix), "synthetic Initialize", "RELEASE_ATTEMPTED");
    }
    if (operation.Stage == ScanStage.Stream)
    {
        var data = new { schemaVersion = 1, reportType = "wasapi-stream-validation", reportId = Guid.NewGuid(), provenance = "TEST_FAKE_STREAM_BACKEND", direction = operation.Direction, format = new { sampleRateHz = operation.SampleRateHz, encoding = operation.Encoding, channels = operation.Channels }, period = operation.Period, durationSeconds = operation.DurationSeconds, physicalPlayback = "NOT_MEASURED", physicalRouting = "UNKNOWN" };
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(data));
        var stream = new RealStreamValidationDto(operation.OperationId, operation.EndpointId!, operation.Direction!, report.RootElement.Clone());
        return new(ScanOperationLifecycle.Completed, new(Stream: stream), "synthetic stream", "RELEASE_ATTEMPTED");
    }
    if (operation.Stage == ScanStage.Duplex)
    {
        var duplex = DuplexReport(operation.OperationId, plan.Target.Capture!.EndpointId!, plan.Target.Render!.EndpointId!, operation.Period!, provenance: "TEST_FAKE_WASAPI_DUPLEX");
        return new(ScanOperationLifecycle.Completed, new(Duplex: duplex), "synthetic Duplex", "RELEASE_ATTEMPTED");
    }
    return new(ScanOperationLifecycle.Completed);
}
var scanCalls = new List<ScanOperation>();
var fakeScanAdapter = new DelegateScanExecutionAdapter(scanCapabilities, async (operation, plan, authorization, token) =>
{
    scanCalls.Add(operation);
    token.ThrowIfCancellationRequested();
    await Task.Yield();
    return FakeScanExecution(operation, plan, authorization);
});
var scanEvents = new List<ScanProgressEvent>();
var coordinatedScan = new ScanCoordinator(fakeScanAdapter).RunAsync(pairedScanPlan, ScanAuth(pairedScanPlan), scanEvents.Add).GetAwaiter().GetResult();
Check(coordinatedScan.Lifecycle == ScanRunLifecycle.Completed && scanCalls.Select(o => o.Stage).Distinct().Contains(ScanStage.Query) && scanCalls.Any(o => o.Stage == ScanStage.Initialize) && scanCalls.Any(o => o.Stage == ScanStage.Stream) && scanCalls.Any(o => o.Stage == ScanStage.Duplex && o.CaptureEndpointId == profileEndpoint.EndpointId && o.RenderEndpointId == renderEndpoint.EndpointId) && coordinatedScan.Operations.Single(o => o.Operation.Stage == ScanStage.Save).Lifecycle == ScanOperationLifecycle.Skipped, "headless fake scan executes supported stages sequentially, binds both Duplex endpoint IDs, and skips real profile save");
Check(coordinatedScan.Evidence.SelectMany(e => new[] { e.FormatMatrix?.Provenance, e.Stream?.Report.GetProperty("provenance").GetString(), e.Duplex?.Report.GetProperty("provenance").GetString() }).Where(p => p is not null).All(p => p!.StartsWith("TEST_FAKE_", StringComparison.Ordinal)) && coordinatedScan.Recommendation.Preferred is null && coordinatedScan.DuplexAssessments.All(a => a.Classification == "INCOMPLETE"), "fake evidence provenance stays synthetic and cannot produce Probe or AudioHost recommendations");
Check(coordinatedScan.Evidence.Select(e => e.FormatMatrix?.EndpointId ?? e.Stream?.EndpointId ?? e.Duplex?.CaptureEndpointId).Where(id => id is not null).All(id => id is "{CAPTURE-ENDPOINT-LOCAL}" or "{RENDER-ENDPOINT-LOCAL}") && !coordinatedScan.PhysicalRtlMeasured && coordinatedScan.AudioHostEligibility == "NOT_CERTIFIED" && scanEvents.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, scanEvents.Count).Select(i => (long)i)) && scanEvents.Any(e => e.Lifecycle == ScanOperationLifecycle.Pending) && scanEvents.Last().Stage == ScanStage.Completed, "coordinator preserves exact endpoint evidence, monotonic progress and explicit RTL/AudioHost limits");
Throws<InvalidOperationException>(() => ScanPlan.Build(new(null, null), scanCapabilities), "scan planning rejects missing Capture and Render endpoints");
var singleScanPlan = ScanPlan.Build(new(profileEndpoint, null), scanCapabilities);
var singleScan = new ScanCoordinator(fakeScanAdapter).RunAsync(singleScanPlan, ScanAuth(singleScanPlan)).GetAwaiter().GetResult();
Check(singleScan.Lifecycle == ScanRunLifecycle.Completed && singleScan.Operations.Any(o => o.Operation.Stage == ScanStage.Duplex && o.Lifecycle == ScanOperationLifecycle.Skipped), "single Capture scan stays valid while Duplex is explicitly skipped for missing pair");
var repeatStages = new HashSet<ScanStage> { ScanStage.Duplex };
ScanSessionAuthorization RepeatAuth(ScanPlan plan) => new(Guid.NewGuid(), true, ScanSessionAuthorization.Fingerprint(plan, repeatStages, 30, DuplexRepeatabilityCoordinator.MaximumRuns), repeatStages, 30, DuplexRepeatabilityCoordinator.MaximumRuns);
var cleanRepeatAdapter = new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, provenance: "REAL_WASAPI_DUPLEX"));
var cleanRepeat = new DuplexRepeatabilityCoordinator(cleanRepeatAdapter).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan)).GetAwaiter().GetResult();
Check(cleanRepeat.Lifecycle == ScanRunLifecycle.Completed && cleanRepeat.Runs.Count == 6 && cleanRepeat.Runs.Select(r => r.Period).SequenceEqual(["MINIMUM", "DEFAULT", "MINIMUM", "DEFAULT", "MINIMUM", "DEFAULT"]) && cleanRepeat.Runs.Select(r => r.OperationId).Distinct().Count() == 6 && cleanRepeat.Periods.All(p => p.Classification == DuplexRepeatabilityClassification.REPEATABLY_CLEAN), "repeatability executes six independently identified Duplex operations in the interleaved order and classifies both periods clean");
var customRepeatPeriods = Enumerable.Repeat("CUSTOM:50000:240", 5).ToArray();
var customRepeatAuth = new ScanSessionAuthorization(Guid.NewGuid(), true, ScanSessionAuthorization.Fingerprint(pairedScanPlan, repeatStages, 30, 5), repeatStages, 30, 5);
var customRepeat = new DuplexRepeatabilityCoordinator(new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, provenance: "REAL_WASAPI_DUPLEX"))).RunAsync(pairedScanPlan, customRepeatAuth, customRepeatPeriods, 0).GetAwaiter().GetResult();
Check(customRepeat.Lifecycle == ScanRunLifecycle.Completed && customRepeat.Runs.Count == 5 && customRepeat.Runs.All(r => r.Period == "CUSTOM:50000:240") && customRepeat.Runs.Select(r => r.OperationId).Distinct().Count() == 5 && CustomDuplexPeriod.IsValidAuthorization("CUSTOM:50000:240"), "bounded repeatability executes five sequential operations for one exact custom period with immutable authorized geometry");
var mixedRepeatAdapter = new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, status: operation.Period == "MINIMUM" && index == 2 ? "DUPLEX_WARNINGS" : "DUPLEX_CLEAN", provenance: "REAL_WASAPI_DUPLEX", serviceFaults: operation.Period == "MINIMUM" && index == 2 ? 1 : 0));
var mixedRepeat = new DuplexRepeatabilityCoordinator(mixedRepeatAdapter).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan)).GetAwaiter().GetResult();
Check(mixedRepeat.Lifecycle == ScanRunLifecycle.Completed && mixedRepeat.Periods.Single(p => p.Period == "MINIMUM").Classification == DuplexRepeatabilityClassification.INTERMITTENT_ANOMALIES && mixedRepeat.Periods.Single(p => p.Period == "DEFAULT").Classification == DuplexRepeatabilityClassification.REPEATABLY_CLEAN && mixedRepeat.Runs.Count(r => r.Validation?.Report.GetProperty("duplex").GetProperty("status").GetString() == "DUPLEX_WARNINGS") == 1, "verified warning evidence continues the series and remains separate from clean observations");
var anomalousRepeatAdapter = new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, status: "DUPLEX_WARNINGS", provenance: "REAL_WASAPI_DUPLEX", serviceFaults: 1));
var anomalousRepeat = new DuplexRepeatabilityCoordinator(anomalousRepeatAdapter).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan)).GetAwaiter().GetResult();
Check(anomalousRepeat.Periods.All(p => p.Classification == DuplexRepeatabilityClassification.REPEATABLY_ANOMALOUS) && anomalousRepeat.Periods.All(p => p.Statuses.All(s => s == "DUPLEX_WARNINGS")), "three valid non-clean observations per period classify as repeatably anomalous without averaging statuses");
var unknownRepeatAdapter = new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, provenance: "REAL_WASAPI_DUPLEX"), unknownCleanupAt: 1);
var unknownRepeat = new DuplexRepeatabilityCoordinator(unknownRepeatAdapter).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan)).GetAwaiter().GetResult();
Check(unknownRepeat.Lifecycle == ScanRunLifecycle.Unknown && unknownRepeat.Runs.Count == 6 && unknownRepeat.Counts is { Planned: 6, Attempted: 2, Completed: 1, Unknown: 1, Skipped: 4 } && unknownRepeat.Runs[1].Lifecycle == ScanOperationLifecycle.Unknown && unknownRepeat.Runs.Skip(2).All(r => r.Lifecycle == ScanOperationLifecycle.Skipped) && unknownRepeat.Periods.All(p => p.Classification == DuplexRepeatabilityClassification.INSUFFICIENT_EVIDENCE), "unknown cleanup stops the series and reports attempted/completed/unknown/skipped counts separately");
var timeoutRepeatAdapter = new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, provenance: "REAL_WASAPI_DUPLEX"), exceptionAt: 1);
var timeoutRepeat = new DuplexRepeatabilityCoordinator(timeoutRepeatAdapter).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan)).GetAwaiter().GetResult();
Check(timeoutRepeat.Lifecycle == ScanRunLifecycle.Unknown && timeoutRepeat.Runs[1].Lifecycle == ScanOperationLifecycle.Unknown && timeoutRepeat.Runs[1].Message.Contains("TimeoutException", StringComparison.Ordinal) && timeoutRepeat.Runs.Skip(2).All(r => r.Lifecycle == ScanOperationLifecycle.Skipped), "native timeout is retained as UNKNOWN and prevents subsequent repeatability operations");
JsonElement? duplicateNativeReport = null;
var duplicateReport = new DuplexRepeatabilityCoordinator(new RepeatabilityFixtureAdapter((operation, index) =>
{
    var evidence = DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, provenance: "REAL_WASAPI_DUPLEX");
    if (index == 0) duplicateNativeReport = evidence.Report;
    return index == 1 ? evidence with { Report = duplicateNativeReport!.Value } : evidence;
})).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan)).GetAwaiter().GetResult();
Check(duplicateReport.Lifecycle == ScanRunLifecycle.Unknown && duplicateReport.Runs[1].Lifecycle == ScanOperationLifecycle.Unknown && duplicateReport.Runs.Skip(2).All(r => r.Lifecycle == ScanOperationLifecycle.Skipped), "duplicate native report IDs stop repeatability without accepting duplicate evidence");
var progressFaultRepeat = new DuplexRepeatabilityCoordinator(new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, provenance: "REAL_WASAPI_DUPLEX"))).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan), _ => throw new InvalidOperationException("observer" )).GetAwaiter().GetResult();
Check(progressFaultRepeat.Lifecycle == ScanRunLifecycle.Completed && progressFaultRepeat.Runs.Count == 6, "progress callback exceptions cannot alter repeatability execution");
var nonUsableStatusRepeat = new DuplexRepeatabilityCoordinator(new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, status: index % 2 == 0 ? "CANCELLED" : "INCOMPLETE", provenance: "REAL_WASAPI_DUPLEX"))).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan)).GetAwaiter().GetResult();
Check(nonUsableStatusRepeat.Periods.All(p => p.UsableObservations == 0 && p.Classification == DuplexRepeatabilityClassification.INSUFFICIENT_EVIDENCE), "cancelled and incomplete native statuses do not count as usable repeatability observations");
var closureFailureRepeat = new DuplexRepeatabilityCoordinator(new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, provenance: "REAL_WASAPI_DUPLEX"), finishFailure: true)).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan)).GetAwaiter().GetResult();
Check(closureFailureRepeat.Lifecycle == ScanRunLifecycle.Unknown && closureFailureRepeat.Runs.Count == 6, "unverified session closure makes the repeatability result UNKNOWN");
var nonCooperativeClosure = new DuplexRepeatabilityCoordinator(new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, provenance: "REAL_WASAPI_DUPLEX"), finishNeverCompletes: true)).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan)).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
Check(nonCooperativeClosure.Lifecycle == ScanRunLifecycle.Unknown && nonCooperativeClosure.Runs.Count == 6, "non-cooperative session closure is independently bounded and remains UNKNOWN");
Throws<InvalidOperationException>(() => new DuplexRepeatabilityCoordinator(new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, provenance: "REAL_WASAPI_DUPLEX"))).RunAsync(pairedScanPlan, new(Guid.NewGuid(), true, ScanSessionAuthorization.Fingerprint(pairedScanPlan, repeatStages, 30, 5), repeatStages, 30, 5)).GetAwaiter().GetResult(), "repeatability refuses an authorization operation limit below six");
using (var repeatCancellation = new CancellationTokenSource())
{
    var cancellationAdapter = new RepeatabilityFixtureAdapter((operation, index) => { if (index == 0) repeatCancellation.Cancel(); return DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, provenance: "REAL_WASAPI_DUPLEX"); });
    var cancelledRepeat = new DuplexRepeatabilityCoordinator(cancellationAdapter).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan), cancellationToken: repeatCancellation.Token).GetAwaiter().GetResult();
    Check(cancelledRepeat.Lifecycle == ScanRunLifecycle.Cancelled && cancelledRepeat.Counts is { Planned: 6, Attempted: 1, Completed: 1, Failed: 0, Unknown: 0, Skipped: 5 } && cancelledRepeat.Runs[0].Lifecycle == ScanOperationLifecycle.Completed && cancelledRepeat.Runs.Skip(1).All(r => r.Lifecycle == ScanOperationLifecycle.Skipped), "cancellation between runs preserves the completed observation and reports the remaining operations as skipped");
}
using (var beforeFirstCancellation = new CancellationTokenSource())
{
    beforeFirstCancellation.Cancel();
    var cancelledBeforeFirst = new DuplexRepeatabilityCoordinator(new RepeatabilityFixtureAdapter((operation, index) => DuplexReport(operation.OperationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, operation.Period!, provenance: "REAL_WASAPI_DUPLEX"))).RunAsync(pairedScanPlan, RepeatAuth(pairedScanPlan), cancellationToken: beforeFirstCancellation.Token).GetAwaiter().GetResult();
    Check(cancelledBeforeFirst.Lifecycle == ScanRunLifecycle.Cancelled && cancelledBeforeFirst.Counts is { Planned: 6, Attempted: 0, Completed: 0, Failed: 0, Unknown: 0, Skipped: 6 } && cancelledBeforeFirst.Runs.All(r => r.Lifecycle == ScanOperationLifecycle.Skipped), "cancellation before the first run reports the complete planned series as skipped");
}
var repeatReportRoot = Path.Combine(Path.GetTempPath(), $"klidan-repeatability-{Guid.NewGuid():N}");
try
{
    var repeatReportPath = DuplexRepeatabilityReportStorage.Save(cleanRepeat, repeatReportRoot);
    var repeatReport = DuplexRepeatabilityReportStorage.Load(repeatReportPath);
    Check(repeatReport.SchemaVersion == 1 && repeatReport.ReportType == "wasapi-duplex-repeatability" && repeatReport.Runs.Count == 6 && repeatReport.PlannedRuns == 6 && repeatReport.AttemptedRuns == 6 && repeatReport.CompletedRuns == 6 && repeatReport.FailedRuns == 0 && repeatReport.UnknownRuns == 0 && repeatReport.SkippedRuns == 0 && repeatReport.Provenance == "REAL_WASAPI_DUPLEX" && repeatReport.Periods.All(p => p.Classification == DuplexRepeatabilityClassification.REPEATABLY_CLEAN), "repeatability report v1 is atomic, no-overwrite, read-back verified, and aggregate counts match six native-provenance observations");
    var customReportRoot = Path.Combine(Path.GetTempPath(), $"klidan-custom-repeatability-{Guid.NewGuid():N}");
    try
    {
        var customReport = DuplexRepeatabilityReportStorage.Load(DuplexRepeatabilityReportStorage.Save(customRepeat, customReportRoot));
        Check(customReport.Runs.Count == 5 && customReport.PlannedRuns == 5 && customReport.AttemptedRuns == 5 && customReport.Runs.All(r => r.Period == "CUSTOM:50000:240") && customReport.CompletedRuns == 5 && customReport.Periods.Single().Classification == DuplexRepeatabilityClassification.REPEATABLY_CLEAN, "custom repeatability aggregate persists every individual report and consistent planned/attempted/completed counts without provenance conversion");
    }
    finally { if (Directory.Exists(customReportRoot)) Directory.Delete(customReportRoot, recursive: true); }
    Throws<IOException>(() => DuplexRepeatabilityReportStorage.Save(cleanRepeat with { SessionId = repeatReport.SessionId }, repeatReportRoot), "repeatability report refuses to overwrite an existing session report");
}
finally { if (Directory.Exists(repeatReportRoot)) Directory.Delete(repeatReportRoot, recursive: true); }
RealStreamValidationDto MutatedStream(ScanOperation operation, string? reportId = null, bool removeReportId = false,
    string? ipcOperationId = null, string? ipcEndpointId = null, string? ipcDirection = null, string? reportDirection = null,
    string? reportPeriod = null, int? reportDuration = null, int? sampleRate = null, string? encoding = null,
    int? channels = null, string? provenance = null)
{
    var standard = StreamReport(operation.OperationId, operation.EndpointId!, operation.Period!);
    var report = JsonNode.Parse(standard.Report.GetRawText())!.AsObject();
    report["provenance"] = "TEST_FAKE_STREAM_BACKEND";
    if (removeReportId) report.Remove("reportId");
    else if (reportId is not null) report["reportId"] = reportId;
    if (reportDirection is not null) report["direction"] = reportDirection;
    if (reportPeriod is not null) report["period"] = reportPeriod;
    if (reportDuration is not null) report["durationSeconds"] = reportDuration;
    if (sampleRate is not null) report["format"]!["sampleRateHz"] = sampleRate;
    if (encoding is not null) report["format"]!["encoding"] = encoding;
    if (channels is not null) report["format"]!["channels"] = channels;
    if (provenance is not null) report["provenance"] = provenance;
    using var document = JsonDocument.Parse(report.ToJsonString());
    return new(ipcOperationId ?? standard.OperationId, ipcEndpointId ?? standard.EndpointId,
        ipcDirection ?? standard.Direction, document.RootElement.Clone());
}
ScanRunResult RunStreamEvidenceCase(Func<ScanOperation, RealStreamValidationDto> streamFactory)
{
    var adapter = new DelegateScanExecutionAdapter(scanCapabilities, (operation, plan, authorization, _) =>
        Task.FromResult(operation.Stage == ScanStage.Stream
            ? new ScanAdapterResult(ScanOperationLifecycle.Completed, new(Stream: streamFactory(operation)), "stream fixture", "RELEASE_ATTEMPTED")
            : FakeScanExecution(operation, plan, authorization)));
    return new ScanCoordinator(adapter).RunAsync(singleScanPlan, ScanAuth(singleScanPlan)).GetAwaiter().GetResult();
}
var uppercaseStreamOperationScan = RunStreamEvidenceCase(operation =>
{
    var upperOperation = operation.OperationId.ToUpperInvariant();
    return MutatedStream(operation, reportId: "B0000000-0000-4000-8000-000000000001", ipcOperationId: upperOperation);
});
Check(uppercaseStreamOperationScan.Lifecycle == ScanRunLifecycle.Completed && uppercaseStreamOperationScan.Operations.Any(o => o.Operation.Stage == ScanStage.Stream && o.Lifecycle == ScanOperationLifecycle.Completed),
    "Coordinator accepts independent uppercase native report UUID and uppercase matching IPC operation UUID");
void CheckStreamRejected(string field, Func<ScanOperation, RealStreamValidationDto> factory)
{
    var result = RunStreamEvidenceCase(factory);
    var operation = result.Operations.First(o => o.Operation.Stage == ScanStage.Stream);
    Check(result.Lifecycle == ScanRunLifecycle.Unknown && operation.Lifecycle == ScanOperationLifecycle.Unknown && operation.Message.Contains(field, StringComparison.Ordinal) &&
        !operation.Message.Contains(profileEndpoint.EndpointId!, StringComparison.Ordinal) && !operation.Message.Contains(renderEndpoint.EndpointId!, StringComparison.Ordinal),
        $"Coordinator rejects invalid Stream identity/settings at {field} without echoing endpoint IDs");
}
CheckStreamRejected("reportId", op => MutatedStream(op, removeReportId: true));
CheckStreamRejected("reportId", op => MutatedStream(op, reportId: "not-a-uuid"));
CheckStreamRejected("ipc.operationId", op => MutatedStream(op, ipcOperationId: Guid.NewGuid().ToString("D")));
CheckStreamRejected("ipc.endpointId", op => MutatedStream(op, ipcEndpointId: "different-endpoint"));
CheckStreamRejected("ipc.direction", op => MutatedStream(op, ipcDirection: "RENDER"));
CheckStreamRejected("direction", op => MutatedStream(op, reportDirection: "RENDER"));
CheckStreamRejected("period", op => MutatedStream(op, reportPeriod: "DEFAULT"));
CheckStreamRejected("durationSeconds", op => MutatedStream(op, reportDuration: 4));
CheckStreamRejected("format.sampleRateHz", op => MutatedStream(op, sampleRate: 44100));
CheckStreamRejected("format.encoding", op => MutatedStream(op, encoding: "FLOAT32"));
CheckStreamRejected("format.channels", op => MutatedStream(op, channels: 1));
CheckStreamRejected("provenance", op => MutatedStream(op, provenance: "REAL_WASAPI_STREAM_VALIDATION"));
var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var serializedAdapter = new DelegateScanExecutionAdapter(scanCapabilities, async (operation, plan, authorization, token) =>
{
    if (operation.Stage == ScanStage.Discovery) { scanEntered.TrySetResult(); await releaseScan.Task.WaitAsync(token); }
    return FakeScanExecution(operation, plan, authorization);
});
var serializedCoordinator = new ScanCoordinator(serializedAdapter);
var activeScanTask = serializedCoordinator.RunAsync(singleScanPlan, ScanAuth(singleScanPlan));
scanEntered.Task.GetAwaiter().GetResult();
Throws<InvalidOperationException>(() => serializedCoordinator.RunAsync(singleScanPlan, ScanAuth(singleScanPlan)).GetAwaiter().GetResult(), "one coordinator rejects overlapping scan sessions");
releaseScan.TrySetResult();
Check(activeScanTask.GetAwaiter().GetResult().Lifecycle == ScanRunLifecycle.Completed, "the original scan completes after the competing request is rejected");
var ambiguousTarget = new ScanTarget(profileEndpoint with { FriendlyName = "Same name" }, renderEndpoint with { FriendlyName = "Same name" }, ScanPairIdentity.Ambiguous);
var ambiguousPlan = ScanPlan.Build(ambiguousTarget, scanCapabilities);
var ambiguousScan = new ScanCoordinator(fakeScanAdapter).RunAsync(ambiguousPlan, ScanAuth(ambiguousPlan)).GetAwaiter().GetResult();
Check(ambiguousScan.Operations.Any(o => o.Operation.Stage == ScanStage.Duplex && o.Lifecycle == ScanOperationLifecycle.Skipped && o.Message.Contains("Ambiguous", StringComparison.Ordinal)), "same friendly names never establish pairing; ambiguous physical identity skips Duplex");
var unsupportedAdapter = new DelegateScanExecutionAdapter(scanCapabilities, (operation, plan, authorization, _) =>
{
    var result = FakeScanExecution(operation, plan, authorization);
    if (operation.Stage == ScanStage.Query)
    {
        var matrix = result.Evidence!.FormatMatrix! with { Candidates = result.Evidence.FormatMatrix.Candidates.Select(c => c with { Status = "UNSUPPORTED", SupportStatus = "UNSUPPORTED", SupportHr = "0x88890008" }).ToArray() };
        result = result with { Evidence = new(FormatMatrix: matrix) };
    }
    return Task.FromResult(result);
});
var unsupportedScan = new ScanCoordinator(unsupportedAdapter).RunAsync(singleScanPlan, ScanAuth(singleScanPlan)).GetAwaiter().GetResult();
Check(unsupportedScan.Lifecycle == ScanRunLifecycle.Completed && unsupportedScan.Operations.All(o => o.Operation.Stage != ScanStage.Initialize || o.Lifecycle == ScanOperationLifecycle.Skipped) && !unsupportedScan.Evidence.Any(e => e.FormatMatrix?.ReportLevel == "INITIALIZE_ONLY"), "unsupported formats are recorded without Initialize attempts or invented success");
var noQueryCapabilities = scanCapabilities with { Query = false };
var noQueryPlan = ScanPlan.Build(new(profileEndpoint, null), noQueryCapabilities);
var noQueryAdapter = new DelegateScanExecutionAdapter(noQueryCapabilities, (operation, plan, authorization, _) => Task.FromResult(FakeScanExecution(operation, plan, authorization)));
var noQueryScan = new ScanCoordinator(noQueryAdapter).RunAsync(noQueryPlan, ScanAuth(noQueryPlan)).GetAwaiter().GetResult();
Check(noQueryScan.Operations.Any(o => o.Operation.Stage == ScanStage.Query && o.Lifecycle == ScanOperationLifecycle.Skipped && o.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase)), "missing query capability is a visible skipped stage");
var candidateCalls = 0;
var recoverableAdapter = new DelegateScanExecutionAdapter(scanCapabilities, (operation, plan, authorization, _) =>
{
    if (operation.Stage == ScanStage.Initialize && ++candidateCalls == 1)
        return Task.FromResult(new ScanAdapterResult(ScanOperationLifecycle.Failed, Message: "synthetic candidate unsupported after query", CleanupStatus: "RELEASE_ATTEMPTED", CanSafelyContinue: true));
    return Task.FromResult(FakeScanExecution(operation, plan, authorization));
});
var recoverableScan = new ScanCoordinator(recoverableAdapter).RunAsync(singleScanPlan, ScanAuth(singleScanPlan)).GetAwaiter().GetResult();
Check(candidateCalls == 4 && recoverableScan.Lifecycle == ScanRunLifecycle.Incomplete && recoverableScan.Operations.Count(o => o.Operation.Stage == ScanStage.Initialize && o.Lifecycle == ScanOperationLifecycle.Failed) == 1 && recoverableScan.Operations.Any(o => o.Operation.Stage == ScanStage.Stream && o.Lifecycle == ScanOperationLifecycle.Completed) && recoverableScan.Operations.Single(o => o.Operation.Stage == ScanStage.Completed).Lifecycle == ScanOperationLifecycle.Failed, "one safely cleaned candidate failure is retained while later candidates continue and terminal progress reflects incomplete aggregation");
var unsafeCalls = new List<ScanOperation>();
var unsafeAdapter = new DelegateScanExecutionAdapter(scanCapabilities, (operation, plan, authorization, _) =>
{
    unsafeCalls.Add(operation);
    if (operation.Stage == ScanStage.Initialize) return Task.FromResult(new ScanAdapterResult(ScanOperationLifecycle.Failed, Message: "release failed", CleanupStatus: "EVENT_RELEASE_FAILED", CanSafelyContinue: false));
    return Task.FromResult(FakeScanExecution(operation, plan, authorization));
});
var unsafeScan = new ScanCoordinator(unsafeAdapter).RunAsync(singleScanPlan, ScanAuth(singleScanPlan)).GetAwaiter().GetResult();
Check(unsafeScan.Lifecycle == ScanRunLifecycle.Incomplete && unsafeCalls.Count(o => o.Stage == ScanStage.Initialize) == 1 && !unsafeCalls.Any(o => o.Stage is ScanStage.Stream or ScanStage.Duplex) && unsafeScan.Operations.Any(o => o.CleanupStatus == "EVENT_RELEASE_FAILED"), "failed cleanup stops the scan before later active operations");
var unknownCleanupAdapter = new DelegateScanExecutionAdapter(scanCapabilities, (operation, plan, authorization, _) =>
{
    var result = FakeScanExecution(operation, plan, authorization);
    return Task.FromResult(operation.Stage == ScanStage.Query ? result with { CleanupStatus = "UNKNOWN" } : result);
});
var unknownCleanupScan = new ScanCoordinator(unknownCleanupAdapter).RunAsync(singleScanPlan, ScanAuth(singleScanPlan)).GetAwaiter().GetResult();
Check(unknownCleanupScan.Lifecycle == ScanRunLifecycle.Unknown && unknownCleanupScan.Operations.Single(o => o.Operation.Stage == ScanStage.Query).Lifecycle == ScanOperationLifecycle.Unknown && !unknownCleanupScan.Operations.Any(o => o.Operation.Stage == ScanStage.Initialize && o.Lifecycle != ScanOperationLifecycle.Skipped), "explicit UNKNOWN cleanup stops unsafe follow-up and preserves the operation as UNKNOWN");
var disconnectedAdapter = new DelegateScanExecutionAdapter(scanCapabilities, (operation, plan, authorization, _) => Task.FromResult(operation.Stage == ScanStage.Discovery ? new ScanAdapterResult(ScanOperationLifecycle.Unknown, Message: "worker disconnected", WorkerConnected: false, CleanupStatus: "UNKNOWN", CanSafelyContinue: false) : FakeScanExecution(operation, plan, authorization)));
var disconnectedScan = new ScanCoordinator(disconnectedAdapter).RunAsync(singleScanPlan, ScanAuth(singleScanPlan)).GetAwaiter().GetResult();
Check(disconnectedScan.Lifecycle == ScanRunLifecycle.Unknown && !disconnectedScan.Operations.Any(o => o.Operation.Stage == ScanStage.Query && o.Lifecycle == ScanOperationLifecycle.Completed), "worker disconnect leaves outcome UNKNOWN and blocks subsequent endpoint operations");
var failedDiscoveryAdapter = new DelegateScanExecutionAdapter(scanCapabilities, (operation, plan, authorization, _) => Task.FromResult(operation.Stage == ScanStage.Discovery
    ? new ScanAdapterResult(ScanOperationLifecycle.Failed, Message: "fresh identity verification failed", CanSafelyContinue: true)
    : FakeScanExecution(operation, plan, authorization)));
var failedDiscoveryScan = new ScanCoordinator(failedDiscoveryAdapter).RunAsync(singleScanPlan, ScanAuth(singleScanPlan)).GetAwaiter().GetResult();
var failedDiscoverySummary = string.Join(",", failedDiscoveryScan.Operations.Select(o => $"{o.Operation.Stage}:{o.Lifecycle}"));
Check(failedDiscoveryScan.Lifecycle == ScanRunLifecycle.Incomplete && failedDiscoveryScan.Operations.All(o => o.Operation.Stage != ScanStage.Query || o.Lifecycle == ScanOperationLifecycle.Skipped) && failedDiscoveryScan.Operations.Single(o => o.Operation.Stage == ScanStage.Evaluate).Lifecycle == ScanOperationLifecycle.Completed, $"failed fresh discovery cannot continue into endpoint operations, but still evaluates retained evidence [{failedDiscoveryScan.Lifecycle}; {failedDiscoverySummary}]");
var invalidatedAdapter = new DelegateScanExecutionAdapter(scanCapabilities, (operation, plan, authorization, _) => Task.FromResult(operation.Stage == ScanStage.Query ? new ScanAdapterResult(ScanOperationLifecycle.Failed, Message: "device invalidated", DevicePresent: false, CleanupStatus: "UNKNOWN", CanSafelyContinue: false) : FakeScanExecution(operation, plan, authorization)));
var invalidatedScan = new ScanCoordinator(invalidatedAdapter).RunAsync(singleScanPlan, ScanAuth(singleScanPlan)).GetAwaiter().GetResult();
Check(invalidatedScan.Lifecycle == ScanRunLifecycle.Unknown && !invalidatedScan.Operations.Any(o => o.Operation.Stage == ScanStage.Initialize && o.Lifecycle == ScanOperationLifecycle.Completed), "device invalidation with unverified cleanup is terminal UNKNOWN and stops further operations");
var waitingQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var cancelAdapter = new DelegateScanExecutionAdapter(scanCapabilities, async (operation, plan, authorization, token) =>
{
    if (operation.Stage == ScanStage.Query) { queryStarted.TrySetResult(); await waitingQuery.Task.WaitAsync(token); }
    return FakeScanExecution(operation, plan, authorization);
}, (_, _, token) => Task.FromResult(new ScanAdapterResult(ScanOperationLifecycle.Skipped, Message: "cooperative fake cleanup", CleanupStatus: "RELEASE_ATTEMPTED", CanSafelyContinue: true)));
using (var scanCancellation = new CancellationTokenSource())
{
    var cancelTask = new ScanCoordinator(cancelAdapter).RunAsync(singleScanPlan, ScanAuth(singleScanPlan), cancellationToken: scanCancellation.Token);
    queryStarted.Task.GetAwaiter().GetResult(); scanCancellation.Cancel();
    var cancelledScan = cancelTask.GetAwaiter().GetResult();
    Check(cancelledScan.Lifecycle == ScanRunLifecycle.Cancelled && cancelledScan.Operations.Any(o => o.Operation.Stage == ScanStage.Query && o.CleanupStatus == "RELEASE_ATTEMPTED") && !cancelledScan.Operations.Any(o => o.Operation.Stage == ScanStage.Initialize && o.Lifecycle == ScanOperationLifecycle.Completed), "cancellation awaits bounded adapter cleanup and does not continue active stages");
}
var timeoutPlan = ScanPlan.Build(new(profileEndpoint, null), scanCapabilities, new(OperationTimeoutSeconds: 1, CleanupTimeoutSeconds: 1));
var timeoutCancelCalls = 0;
var timeoutAdapter = new DelegateScanExecutionAdapter(scanCapabilities, (_, _, _, _) => new TaskCompletionSource<ScanAdapterResult>().Task,
    (_, _, _) => { timeoutCancelCalls++; return Task.FromResult(new ScanAdapterResult(ScanOperationLifecycle.Skipped, Message: "bounded cleanup", CleanupStatus: "RELEASE_ATTEMPTED", CanSafelyContinue: true)); });
var timedCoordinator = new ScanCoordinator(timeoutAdapter);
var timeoutScan = timedCoordinator.RunAsync(timeoutPlan, ScanAuth(timeoutPlan)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
Check(timeoutScan.Lifecycle == ScanRunLifecycle.Unknown && timeoutCancelCalls == 1 && timeoutScan.Operations.Any(o => o.Operation.Stage == ScanStage.Discovery && o.CleanupStatus == "RELEASE_ATTEMPTED"), "operation deadline bounds a nonresponsive adapter wait and reports UNKNOWN when the execution task may remain active");
Throws<InvalidOperationException>(() => timedCoordinator.RunAsync(timeoutPlan, ScanAuth(timeoutPlan)).GetAwaiter().GetResult(), "coordinator cannot start another scan while a timed-out native operation may still be active");
var wrongIdentityAdapter = new DelegateScanExecutionAdapter(scanCapabilities, (operation, plan, authorization, _) =>
{
    if (operation.Stage == ScanStage.Query && operation.Direction == "RENDER")
    {
        var wrong = FakeScanQuery(operation) with { EndpointId = "wrong-endpoint" };
        return Task.FromResult(new ScanAdapterResult(ScanOperationLifecycle.Completed, new(FormatMatrix: wrong), "malformed identity", "RELEASE_ATTEMPTED"));
    }
    return Task.FromResult(FakeScanExecution(operation, plan, authorization));
});
var identityScan = new ScanCoordinator(wrongIdentityAdapter).RunAsync(pairedScanPlan, ScanAuth(pairedScanPlan)).GetAwaiter().GetResult();
Check(identityScan.Lifecycle == ScanRunLifecycle.Unknown && identityScan.Evidence.Count(e => e.FormatMatrix?.ReportLevel == "PASSIVE_QUERY") == 1 && identityScan.Evidence.Single(e => e.FormatMatrix?.ReportLevel == "PASSIVE_QUERY").FormatMatrix!.EndpointId == profileEndpoint.EndpointId, "mismatched later endpoint evidence is rejected as UNKNOWN while earlier exact-endpoint evidence is retained");
var physicalAuthPlan = ScanPlan.Build(new(profileEndpoint, null), scanCapabilities);
var rtlAuth = ScanAuth(physicalAuthPlan) with { AllowsPhysicalRtl = true };
Throws<InvalidOperationException>(() => new ScanCoordinator(fakeScanAdapter).RunAsync(physicalAuthPlan, rtlAuth).GetAwaiter().GetResult(), "scan-session authorization cannot enable physical RTL or AudioHost activation");
var staleScope = ScanAuth(physicalAuthPlan) with { AllowedStages = new HashSet<ScanStage> { ScanStage.Discovery } };
Throws<InvalidOperationException>(() => new ScanCoordinator(fakeScanAdapter).RunAsync(physicalAuthPlan, staleScope).GetAwaiter().GetResult(), "scan-session authorization fingerprint binds the allowed operation scope");
var oneStageSet = Enum.GetValues<ScanStage>().ToHashSet();
var oneOperationAuth = new ScanSessionAuthorization(Guid.NewGuid(), true, ScanSessionAuthorization.Fingerprint(physicalAuthPlan, oneStageSet, 5, 1), oneStageSet, 5, 1);
var boundedScan = new ScanCoordinator(fakeScanAdapter).RunAsync(physicalAuthPlan, oneOperationAuth).GetAwaiter().GetResult();
Check(boundedScan.Lifecycle == ScanRunLifecycle.Incomplete && boundedScan.Operations.Single(o => o.Operation.Stage == ScanStage.Discovery).Lifecycle == ScanOperationLifecycle.Completed && boundedScan.Operations.Single(o => o.Operation.Stage == ScanStage.Query).Lifecycle == ScanOperationLifecycle.Failed, "authorization operation-count limit produces a bounded incomplete result instead of throwing or running another operation");
var callbackFailureScan = new ScanCoordinator(fakeScanAdapter).RunAsync(physicalAuthPlan, ScanAuth(physicalAuthPlan), _ => throw new InvalidOperationException("UI callback failure")).GetAwaiter().GetResult();
Check(callbackFailureScan.Lifecycle == ScanRunLifecycle.Completed && callbackFailureScan.Events.Count > 0 && callbackFailureScan.Events[^1].Lifecycle == ScanOperationLifecycle.Completed, "progress subscriber exceptions cannot corrupt coordinator execution or terminal progress status");
var initFirstWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse };
var initFirstVm = new MainViewModel(worker: initFirstWorker);
initFirstWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected fake."));
initFirstVm.DiscoverWindowsEndpointsCommand.Execute(null); SpinWait.SpinUntil(() => initFirstVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
initFirstVm.SelectedWindowsEndpoint = initFirstVm.WindowsEndpoints.Single(); initFirstVm.NavigateOperatorStep(OperatorStep.Test); initFirstVm.OperatorTestMode = "INITIALIZE_ONLY";
Check(initFirstVm.NeedsInitializeFormatQuery && initFirstVm.QueryRealFormatsCommand.CanExecute(null) && !initFirstVm.RequestRealInitializeCommand.CanExecute(null) && initFirstVm.RealInitializeDisabledReason == KlidanAudioProbe.Gui.Core.Localization.Get("VmInitializeNeedsCurrentQuery"), "first-launch Initialize-only explains and enables its required passive query without enabling Initialize");
initFirstVm.QueryRealFormatsCommand.Execute(null); SpinWait.SpinUntil(() => initFirstWorker.RealQueryCalls == 1 && initFirstVm.HasCurrentOperatorQueryResult, TimeSpan.FromSeconds(2));
Check(initFirstVm.OperatorTestMode == "INITIALIZE_ONLY" && initFirstVm.WorkflowStep == OperatorStep.Test && !initFirstVm.NeedsInitializeFormatQuery && initFirstWorker.RealInitializeCalls == 0, "successful prerequisite query preserves Initialize-only mode and never auto-runs Initialize");
initFirstVm.SelectedRealFormatRow = initFirstVm.RealFormatRows.Single(r => r.Format == "PCM16" && r.SampleRate == 48000 && r.Channels == 2);
Check(initFirstVm.RequestRealInitializeCommand.CanExecute(null), "supported format becomes selectable for explicit Initialize-only after its current query");
initFirstVm.RequestRealInitializeCommand.Execute(null); initFirstVm.CancelRealInitializeCommand.Execute(null);
Check(!initFirstVm.RealInitializeConsentPending && initFirstWorker.RealInitializeCalls == 0, "cancelled consent after prerequisite query never invokes Initialize");
var queriedEndpoint = initFirstVm.WindowsEndpoints.Single(); initFirstVm.SelectedWindowsEndpoint = null; initFirstVm.SelectedWindowsEndpoint = queriedEndpoint;
Check(!initFirstVm.HasCurrentOperatorQueryResult && initFirstVm.RealFormatMatrixIsStale && !initFirstVm.RequestRealInitializeCommand.CanExecute(null) && initFirstVm.RealInitializeDisabledReason == KlidanAudioProbe.Gui.Core.Localization.Get("VmInitializeEndpointStale"), "historical query cannot regain Initialize authorization after endpoint selection changes");
var prerequisiteWindow = new MainWindow(initFirstVm) { Width = 1024, Height = 720, ShowActivated = false }; prerequisiteWindow.Show(); prerequisiteWindow.UpdateLayout();
var prerequisiteNotice = (Border)typeof(MainWindow).GetField("InitializeQueryPrerequisiteNotice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prerequisiteWindow)!;
Check(prerequisiteNotice.Visibility == Visibility.Visible, "runtime Initialize-only workflow visibly explains missing or stale query prerequisite"); prerequisiteWindow.Close();

var mixedCandidates = realCandidates
    .Where(c => !(c.Encoding == "PCM24_IN_32" && c.SampleRate == 48000 && c.Channels == 2 && c.Period == "minimum"))
    .Select(c => (c.Encoding, c.SampleRate, c.Channels) switch
    {
        ("PCM16", 44100, 1) => c with { Status = "UNSUPPORTED", SupportStatus = "UNSUPPORTED", SupportHr = "0x88890008" },
        ("PCM24_IN_32", 44100, 1) => c with { Status = "NOT_TESTED", SupportStatus = "INCOMPLETE", SupportHr = null, QueryCleanupStatus = "NO_CLIENT_ACQUIRED", QueryClientReleaseAttempts = 0 },
        ("FLOAT32", 44100, 2) => c with { Status = "FAILED", SupportStatus = "FAILED", SupportHr = "0x80004005" },
        _ => c
    }).ToArray();
var mixedWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse with { Candidates = mixedCandidates } };
var mixedVm = new MainViewModel(worker: mixedWorker); mixedWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected fake."));
mixedVm.DiscoverWindowsEndpointsCommand.Execute(null); SpinWait.SpinUntil(() => mixedVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
mixedVm.SelectedWindowsEndpoint = mixedVm.WindowsEndpoints.Single(); mixedVm.NavigateOperatorStep(OperatorStep.Test); mixedVm.OperatorTestMode = "INITIALIZE_ONLY"; mixedVm.QueryRealFormatsCommand.Execute(null);
SpinWait.SpinUntil(() => mixedVm.HasCurrentOperatorQueryResult, TimeSpan.FromSeconds(2));
var sortedStatuses = mixedVm.RealFormatRows.Select(r => r.DisplayStatus).Distinct().ToArray();
Check(sortedStatuses.SequenceEqual(["SUPPORTED_REPORTED", "UNSUPPORTED", "NOT_TESTED", "INCOMPLETE/CONFLICT", "FAILED"]) && mixedVm.SelectedRealFormatRow?.DisplayStatus == "SUPPORTED_REPORTED", "mixed matrix orders supported, unsupported, not tested, conflict, then failed and defaults to the first supported row");
var manualRow = mixedVm.RealFormatRows.Single(r => r.Format == "PCM24_IN_32" && r.SampleRate == 48000 && r.Channels == 1);
mixedVm.SelectedRealFormatRow = manualRow; mixedVm.RealInitializePeriod = "minimum"; mixedVm.QueryRealFormatsCommand.Execute(null);
SpinWait.SpinUntil(() => mixedWorker.RealQueryCalls == 2 && !mixedVm.RealFormatMatrixStatus.StartsWith("Выполняется", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
Check(mixedVm.SelectedRealFormatRow is { Format: "PCM24_IN_32", SampleRate: 48000, Channels: 1 } && mixedVm.RealInitializePeriod == "minimum", "refresh preserves a valid manual supported-format and period selection");
mixedVm.SelectedRealFormatRow = mixedVm.RealFormatRows.Single(r => r.DisplayStatus == "UNSUPPORTED");
Check(!mixedVm.RequestRealInitializeCommand.CanExecute(null) && mixedVm.RealInitializeDisabledReason == KlidanAudioProbe.Gui.Core.Localization.Get("VmInitializeFormatUnsupported"), "unsupported selection has an explicit Initialize-disabled reason");
mixedVm.SelectedRealFormatRow = mixedVm.RealFormatRows.Single(r => r.DisplayStatus == "INCOMPLETE/CONFLICT");
Check(!mixedVm.RequestRealInitializeCommand.CanExecute(null) && mixedVm.RealInitializeDisabledReason == KlidanAudioProbe.Gui.Core.Localization.Get("VmInitializeFormatIncomplete"), "conflicting evidence has an explicit Initialize-disabled reason");
var mixedWindow = new MainWindow(mixedVm) { Width = 1280, Height = 800, ShowActivated = false }; mixedWindow.Show(); mixedWindow.UpdateLayout();
var mixedGrid = (DataGrid)typeof(MainWindow).GetField("OperatorRealFormatGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixedWindow)!;
var mixedInitializeButton = (Button)typeof(MainWindow).GetField("OperatorInitializeButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixedWindow)!;
var mixedInitializeReason = (TextBlock)typeof(MainWindow).GetField("RealInitializeDisabledReasonText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixedWindow)!;
var mixedSummary = (TextBlock)typeof(MainWindow).GetField("RealFormatSummaryText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixedWindow)!;
Check(mixedGrid.Items.Count == 12 && ((RealFormatUniqueRowDto)mixedGrid.Items[0]).DisplayStatus == "SUPPORTED_REPORTED" && !mixedInitializeButton.IsEnabled && mixedInitializeReason.Text == KlidanAudioProbe.Gui.Core.Localization.Get("VmInitializeFormatIncomplete") && mixedSummary.Text.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmRealFormatSummary").Split("{2}", StringSplitOptions.None)[0].Split('·')[2].Trim(), StringComparison.Ordinal), "runtime WPF matrix is support-sorted, summarizes outcomes, and explains the selected conflict"); mixedWindow.Close();
var allUnsupportedWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse with { Candidates = realCandidates.Select(c => c with { Status = "UNSUPPORTED", SupportStatus = "UNSUPPORTED", SupportHr = "0x88890008" }).ToArray() } };
var allUnsupportedVm = new MainViewModel(worker: allUnsupportedWorker); allUnsupportedWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected fake."));
allUnsupportedVm.DiscoverWindowsEndpointsCommand.Execute(null); SpinWait.SpinUntil(() => allUnsupportedVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
allUnsupportedVm.SelectedWindowsEndpoint = allUnsupportedVm.WindowsEndpoints.Single(); allUnsupportedVm.NavigateOperatorStep(OperatorStep.Test); allUnsupportedVm.OperatorTestMode = "INITIALIZE_ONLY"; allUnsupportedVm.QueryRealFormatsCommand.Execute(null);
SpinWait.SpinUntil(() => allUnsupportedVm.HasCurrentOperatorQueryResult, TimeSpan.FromSeconds(2));
Check(allUnsupportedVm.SelectedRealFormatRow is null && allUnsupportedVm.RealInitializeDisabledReason == KlidanAudioProbe.Gui.Core.Localization.Get("VmInitializeNeedsSelectedFormat"), "all-unsupported results do not auto-select an ineligible format and explain that none is selected");

var conflictingCandidates = realCandidates.Concat([realCandidates[0] with { SupportStatus = "UNSUPPORTED", Status = "UNSUPPORTED", SupportHr = "0x88900001" }]).ToArray();
var conflictWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse with { Candidates = conflictingCandidates } };
var conflictVm = new MainViewModel(worker: conflictWorker); conflictWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected fake."));
conflictVm.DiscoverWindowsEndpointsCommand.Execute(null); SpinWait.SpinUntil(() => conflictVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
conflictVm.SelectedWindowsEndpoint = conflictVm.WindowsEndpoints.Single(); conflictVm.NavigateOperatorStep(OperatorStep.Test); conflictVm.OperatorTestMode = "INITIALIZE_ONLY"; conflictVm.QueryRealFormatsCommand.Execute(null);
SpinWait.SpinUntil(() => conflictVm.HasCurrentOperatorQueryResult, TimeSpan.FromSeconds(2));
var conflictRow = conflictVm.RealFormatRows.Single(r => r.Format == "PCM16" && r.SampleRate == 44100 && r.Channels == 1); conflictVm.SelectedRealFormatRow = conflictRow;
Check(conflictRow.IsConflict && conflictRow.SupportStatus == "INCOMPLETE/CONFLICT" && conflictRow.EvidenceCandidates.Count == 3 && !conflictVm.RequestRealInitializeCommand.CanExecute(null), "duplicate contradictory periods are surfaced as conflict and cannot authorize Initialize");
var hresultConflict = realQueryResponse with { Candidates = realCandidates.Select((c, i) => i == 1 ? c with { SupportHr = "0x88900001" } : c).ToArray() };
Check(RealFormatMatrixValidation.BuildUniqueRows(hresultConflict, [1, 2]).Single(r => r.Format == "PCM16" && r.SampleRate == 44100 && r.Channels == 1).IsConflict, "period records with inconsistent IsFormatSupported HRESULTs are classified as conflict");
var missingQueryEvidence = realQueryResponse with { Candidates = realCandidates.Select((c, i) => i == 0 ? c with { SupportHr = null } : c).ToArray() };
Check(RealFormatMatrixValidation.BuildUniqueRows(missingQueryEvidence, [1, 2]).Single(r => r.Format == "PCM16" && r.SampleRate == 44100 && r.Channels == 1).IsConflict, "missing shared IsFormatSupported HRESULT becomes incomplete conflict evidence");

var missingPeriodWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse with { Candidates = realCandidates.Where(c => !(c.Encoding == "PCM16" && c.SampleRate == 44100 && c.Channels == 1 && c.Period == "minimum")).ToArray() } };
var missingPeriodVm = new MainViewModel(worker: missingPeriodWorker); missingPeriodWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected fake."));
missingPeriodVm.DiscoverWindowsEndpointsCommand.Execute(null); SpinWait.SpinUntil(() => missingPeriodVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
missingPeriodVm.SelectedWindowsEndpoint = missingPeriodVm.WindowsEndpoints.Single(); missingPeriodVm.NavigateOperatorStep(OperatorStep.Test); missingPeriodVm.OperatorTestMode = "INITIALIZE_ONLY"; missingPeriodVm.QueryRealFormatsCommand.Execute(null);
SpinWait.SpinUntil(() => missingPeriodVm.HasCurrentOperatorQueryResult, TimeSpan.FromSeconds(2));
var missingPeriodRow = missingPeriodVm.RealFormatRows.Single(r => r.Format == "PCM16" && r.SampleRate == 44100 && r.Channels == 1);
Check(missingPeriodRow.IsConflict && missingPeriodRow.DefaultCandidate is null && missingPeriodRow.MinimumCandidate is null, "missing period response creates an incomplete row with no silently selected candidate");

var invalidEnvelopeWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse with { ReportLevel = "INITIALIZE_ONLY" } };
var invalidEnvelopeVm = new MainViewModel(worker: invalidEnvelopeWorker); invalidEnvelopeWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected fake."));
invalidEnvelopeVm.DiscoverWindowsEndpointsCommand.Execute(null); SpinWait.SpinUntil(() => invalidEnvelopeVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
invalidEnvelopeVm.SelectedWindowsEndpoint = invalidEnvelopeVm.WindowsEndpoints.Single(); invalidEnvelopeVm.NavigateOperatorStep(OperatorStep.Test); invalidEnvelopeVm.OperatorTestMode = "INITIALIZE_ONLY"; invalidEnvelopeVm.QueryRealFormatsCommand.Execute(null);
SpinWait.SpinUntil(() => invalidEnvelopeVm.RealFormatMatrixStatus.Contains("InvalidDataException", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
Check(!invalidEnvelopeVm.HasCurrentOperatorQueryResult && !invalidEnvelopeVm.RequestRealInitializeCommand.CanExecute(null), "malformed decoded IPC report level is rejected instead of reusing prior evidence");
var failedReuseWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true, RealMatrixResponse = realQueryResponse };
var failedReuseVm = new MainViewModel(worker: failedReuseWorker); failedReuseWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected fake."));
failedReuseVm.DiscoverWindowsEndpointsCommand.Execute(null); SpinWait.SpinUntil(() => failedReuseVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
failedReuseVm.SelectedWindowsEndpoint = failedReuseVm.WindowsEndpoints.Single(); failedReuseVm.NavigateOperatorStep(OperatorStep.Test); failedReuseVm.OperatorTestMode = "INITIALIZE_ONLY"; failedReuseVm.QueryRealFormatsCommand.Execute(null);
SpinWait.SpinUntil(() => failedReuseVm.HasCurrentOperatorQueryResult, TimeSpan.FromSeconds(2)); failedReuseWorker.RealMatrixResponse = null; failedReuseVm.QueryRealFormatsCommand.Execute(null);
SpinWait.SpinUntil(() => failedReuseVm.RealFormatMatrixStatus.Contains("NotSupportedException", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
Check(failedReuseVm.RealFormatMatrix is not null && !failedReuseVm.HasCurrentOperatorQueryResult && !failedReuseVm.RequestRealInitializeCommand.CanExecute(null), "failed refresh retains prior matrix only as historical evidence and cannot authorize Initialize");

var queryResultWindow = new MainWindow(realWorkflowVm) { Width = 1280, Height = 800, ShowActivated = false };
queryResultWindow.Show(); queryResultWindow.UpdateLayout();
var queryResultGrid = (DataGrid)typeof(MainWindow).GetField("OperatorResultFormatGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queryResultWindow)!;
var queryResultPanel = (StackPanel)typeof(MainWindow).GetField("OperatorResultPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queryResultWindow)!;
Check(queryResultPanel.Visibility == Visibility.Visible && queryResultGrid.Visibility == Visibility.Visible && queryResultGrid.Items.Count == 12, "runtime query result screen shows all unique formats with HRESULT columns");
queryResultWindow.Close();
realWorkflowVm.NavigateOperatorStep(OperatorStep.Test);
realWorkflowVm.OperatorTestMode = "INITIALIZE_ONLY";
realWorkflowVm.SelectedRealFormatRow = realWorkflowVm.RealFormatRows.Single(r => r.Format == "PCM16" && r.SampleRate == 48000 && r.Channels == 2);
realWorkflowVm.RealInitializePeriod = "minimum"; realWorkflowVm.NavigateOperatorStep(OperatorStep.Device); realWorkflowVm.NavigateOperatorStep(OperatorStep.Test);
Check(realWorkflowVm.SelectedWindowsEndpoint?.EndpointId == "local-windows-id" && realWorkflowVm.SelectedRealFormatRow?.Format == "PCM16" && realWorkflowVm.RealInitializePeriod == "minimum", "test/device navigation preserves the selected endpoint, format and period");
realWorkflowVm.RealInitializePeriod = "default";
realWorkflowVm.RequestRealInitializeCommand.Execute(null);
Check(realWorkflowVm.RealInitializeConsentPending && !realWorkflowVm.RealInitializeConsentChecked && realWorkflowWorker.RealInitializeCalls == 0, "Initialize-only is fresh-consent gated and cancellation is not an operation");
realWorkflowVm.CancelRealInitializeCommand.Execute(null);
Check(!realWorkflowVm.RealInitializeConsentPending && realWorkflowWorker.RealInitializeCalls == 0, "cancelled Initialize-only consent clears without IPC");
realWorkflowVm.RequestRealInitializeCommand.Execute(null); realWorkflowVm.RealInitializeConsentChecked = true;
realWorkflowVm.RealInitializePeriod = "minimum";
Check(!realWorkflowVm.RealInitializeConsentPending && realWorkflowWorker.RealInitializeCalls == 0 && realWorkflowVm.SelectedRealFormatCandidate?.Period == "minimum", "changing period invalidates pending one-use consent and selects the exact period candidate");
realWorkflowVm.RequestRealInitializeCommand.Execute(null); realWorkflowVm.RealInitializeConsentChecked = true;
realWorkflowVm.RealInitializePeriod = "default";
realWorkflowVm.RequestRealInitializeCommand.Execute(null); realWorkflowVm.RealInitializeConsentChecked = true;
realWorkflowVm.ConfirmRealInitializeCommand.Execute(null);
SpinWait.SpinUntil(() => realWorkflowVm.RealInitializationResult is not null, TimeSpan.FromSeconds(2));
Check(realWorkflowWorker.RealInitializeCalls == 1 && realWorkflowWorker.LastRealConsent?.RequestFingerprint == RealFormatConsent.Fingerprint("local-windows-id","CAPTURE",48000,2,"PCM16","default"), "accepted Initialize-only consent fingerprint binds endpoint, direction, exact format and period");
Check(realWorkflowVm.WorkflowStep == OperatorStep.Result && realWorkflowVm.OperatorResultMode == KlidanAudioProbe.Gui.Core.Localization.Get("VmOperatorModeInitializeOnly") && realWorkflowVm.OperatorResultOutcome.Contains("READY", StringComparison.Ordinal), "Initialize result is shown separately from query support and retains exact mode");
Check(realWorkflowVm.CanSaveDeviceProfile, "current accepted 24-row query and Initialize result enable the real profile-save path");
var guiCreatedProfile = realWorkflowVm.CreateCurrentDeviceProfile();
var guiSaveDirectory = Path.Combine(Path.GetTempPath(), $"klidan-profile-gui-save-{Guid.NewGuid():N}");
try
{
    var guiSavedProfilePath = DeviceConfigurationProfileStorage.SaveLocal(guiCreatedProfile, guiSaveDirectory);
    var guiSavedProfile = DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(guiSavedProfilePath));
    var guiSanitizedProfile = DeviceConfigurationProfileJson.Parse(DeviceConfigurationProfileJson.SerializeSanitized(guiSavedProfile));
    Check(guiSavedProfile.EndpointBindings.Capture?.EndpointId == "local-windows-id" && guiSavedProfile.Evidence.Any(e => e.IsFormatSupportedHresult == "0x00000000") && guiSavedProfile.Evidence.Any(e => e.SourceProvenance == "REAL_WASAPI_INITIALIZE_ONLY" && e.BufferFrames == 480 && e.EventReady == true && e.Cleanup == "RELEASE_ATTEMPTED") && guiSavedProfile.Recommendation.Preferred is null, "GUI CreateCurrentDeviceProfile to SaveLocal round-trip preserves real-shaped query/init evidence without recommendation");
    Check(guiSanitizedProfile.Privacy == "SANITIZED_PORTABLE" && guiSanitizedProfile.EndpointBindings.Capture is { EndpointId: null, Binding: "SANITIZED_UNBOUND" } && guiSanitizedProfile.Evidence.All(e => e.StreamStart == "NOT_RUN" && e.PcmObservation == "NOT_RUN"), "GUI-produced sanitized export round-trip stays unbound and does not fabricate Start or PCM");
}
finally { if (Directory.Exists(guiSaveDirectory)) Directory.Delete(guiSaveDirectory, recursive: true); }
var operatorWindow = new MainWindow(realWorkflowVm) { Width = 1280, Height = 800, ShowActivated = false };
operatorWindow.Show(); operatorWindow.UpdateLayout();
var resultPanel = (StackPanel)typeof(MainWindow).GetField("OperatorResultPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operatorWindow)!;
var testPanel = (StackPanel)typeof(MainWindow).GetField("OperatorTestPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operatorWindow)!;
var operatorGrid = (DataGrid)typeof(MainWindow).GetField("OperatorRealFormatGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operatorWindow)!;
var operatorModeCombo = (ComboBox)typeof(MainWindow).GetField("OperatorTestModeCombo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operatorWindow)!;
var operatorPeriodCombo = (ComboBox)typeof(MainWindow).GetField("OperatorInitializePeriodCombo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operatorWindow)!;
var testStepButton = (Button)typeof(MainWindow).GetField("OperatorTestStepButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operatorWindow)!;
var resultDetails = (TextBlock)typeof(MainWindow).GetField("OperatorResultTechnicalDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operatorWindow)!;
var resultGrid = (DataGrid)typeof(MainWindow).GetField("OperatorResultFormatGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operatorWindow)!;
var initializeDetailsExpander = (Expander)typeof(MainWindow).GetField("InitializeTechnicalDetailsExpander", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operatorWindow)!;
Check((operatorWindow.Title.Contains("Windows diagnostics", StringComparison.Ordinal) || operatorWindow.Title.Contains("диагностика Windows", StringComparison.Ordinal)) && resultPanel.Visibility == Visibility.Visible && testPanel.Visibility == Visibility.Collapsed, "runtime WPF opens the real-result step with a localized Windows diagnostic title");
Check(resultGrid.Visibility == Visibility.Collapsed && resultDetails.Text.Contains("INITIALIZE-ONLY", StringComparison.OrdinalIgnoreCase), "Initialize result screen shows detailed initialization evidence without repeating the passive matrix");
foreach (var initializeLanguage in new[] { "en-US", "ru-RU" })
{
    KlidanAudioProbe.Gui.Core.Localization.SetLanguage(initializeLanguage, false);
    var initializeCulture = KlidanAudioProbe.Gui.Core.Localization.CurrentCulture;
    var initializeSummary = realWorkflowVm.RealInitializationSummary;
    Check(initializeSummary.Contains("Fixture Interface", StringComparison.Ordinal) && initializeSummary.Contains(initializeLanguage == "ru-RU" ? "PCM16 · 48000 Гц · 2 канал(а)" : "PCM16 · 48000 Hz · 2 channels", StringComparison.Ordinal) && initializeSummary.Contains("SUPPORTED_REPORTED", StringComparison.Ordinal) && initializeSummary.Contains("READY", StringComparison.Ordinal) && initializeSummary.Contains("0x00000000", StringComparison.Ordinal) && initializeSummary.Contains("480 frames", StringComparison.Ordinal) && initializeSummary.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmRealEventReady"), StringComparison.Ordinal) && initializeSummary.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmRealCleanupReleaseAttempted"), StringComparison.Ordinal) && initializeSummary.Contains("NOT_STARTED", StringComparison.Ordinal) && initializeSummary.Contains("NOT_TRANSFERRED", StringComparison.Ordinal), $"successful Initialize summary localizes in {initializeLanguage} and preserves separate support, Initialize, Start and cleanup evidence");
}
KlidanAudioProbe.Gui.Core.Localization.SetLanguage(baselineLanguage, false);
Check(initializeDetailsExpander.Visibility == Visibility.Visible && !initializeDetailsExpander.IsExpanded && ((TextBlock)initializeDetailsExpander.Content).Text.Length > 0 && realWorkflowVm.RealInitializationDetails.Contains("release attempts", StringComparison.Ordinal), "full HRESULT, alignment and COM release details are collapsed by default in the technical section");
realWorkflowVm.OpenDeveloperToolsCommand.Execute(null);
realWorkflowVm.NavigateOperatorStep(OperatorStep.Test); operatorWindow.UpdateLayout();
Check(testPanel.Visibility == Visibility.Visible && operatorGrid.Items.Count == 12 && operatorGrid.Columns.Count == 5, "runtime WPF test page presents 12 unique format rows with compact evidence columns");
Check(testStepButton.Foreground == app.Resources["AmberBrush"] && testStepButton.Background == app.Resources["ThreeBrush"], "runtime progress navigation visibly highlights the current operator step");
operatorModeCombo.SelectedValue = "EXCLUSIVE_FORMAT_QUERY"; operatorModeCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
operatorModeCombo.SelectedValue = "INITIALIZE_ONLY"; operatorModeCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
operatorPeriodCombo.SelectedValue = "minimum"; operatorPeriodCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
Check(realWorkflowVm.OperatorTestMode == "INITIALIZE_ONLY" && realWorkflowVm.RealInitializePeriod == "minimum" && realWorkflowVm.SelectedRealFormatCandidate?.Period == "minimum", "runtime WPF mode and period ComboBoxes update typed immutable request settings");
foreach (var size in new[] { (1024d, 720d), (1280d, 800d), (1440d, 900d) })
{
    operatorWindow.Width = size.Item1; operatorWindow.Height = size.Item2;
    realWorkflowVm.NavigateOperatorStep(OperatorStep.Test); operatorWindow.UpdateLayout();
    Check(testPanel.Visibility == Visibility.Visible && operatorGrid.ActualWidth > 0 && operatorGrid.Columns.Sum(c => c.ActualWidth) <= operatorGrid.ActualWidth + 1, $"runtime operator test matrix fits at {size.Item1:0}×{size.Item2:0} DIP");
}
realWorkflowVm.NavigateOperatorStep(OperatorStep.Result); operatorWindow.UpdateLayout();
Check(resultPanel.Visibility == Visibility.Visible && testPanel.Visibility == Visibility.Collapsed, "operator result navigation replaces configuration without mixing developer pages");
operatorWindow.Close();
var retainedResultDevice = realWorkflowVm.OperatorSelectedDevice;
realWorkflowVm.SelectedWindowsEndpoint = null;
realWorkflowVm.NavigateOperatorStep(OperatorStep.Device);
realWorkflowVm.NavigateOperatorStep(OperatorStep.Result);
Check(realWorkflowVm.OperatorSelectedDevice == retainedResultDevice, "device navigation preserves the last real result identity");
realWorkflowVm.NavigateOperatorStep(OperatorStep.Test); realWorkflowVm.RequestRealInitializeCommand.Execute(null);
realWorkflowVm.SelectedWindowsEndpoint = null;
Check(!realWorkflowVm.RealInitializeConsentPending && !realWorkflowVm.RequestRealInitializeCommand.CanExecute(null), "changing endpoint cancels pending consent and invalidates the prior query for Initialize-only");
realWorkflowVm.NavigateOperatorStep(OperatorStep.Result);
realWorkflowVm.Navigate(ProbeScreen.Diagnostics);
Check(realWorkflowVm.DeveloperToolsOpen && realWorkflowVm.WorkflowStep == OperatorStep.Result, "synthetic diagnostics are isolated in Developer Tools without replacing result history");
realWorkflowWorker.SetState(new(WorkerConnectionState.Disconnected,"Worker exited."));
Check(realWorkflowVm.RealFormatMatrixIsStale && !realWorkflowVm.RequestRealInitializeCommand.CanExecute(null) && realWorkflowVm.RealInitializeDisabledReason == KlidanAudioProbe.Gui.Core.Localization.Get("VmInitializeNeedsWorker"), "worker disconnect retains real query result as historical and explains why Initialize is disabled");
var failedQueryWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true, AdvertiseRealFormatProbe = true };
var failedQueryVm = new MainViewModel(worker: failedQueryWorker);
failedQueryWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected fake."));
failedQueryVm.DiscoverWindowsEndpointsCommand.Execute(null);
SpinWait.SpinUntil(() => failedQueryVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
failedQueryVm.SelectedWindowsEndpoint = failedQueryVm.WindowsEndpoints.Single();
failedQueryVm.QueryRealFormatsCommand.Execute(null);
SpinWait.SpinUntil(() => failedQueryVm.RealFormatMatrixStatus.Contains("NotSupportedException", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
Check(failedQueryVm.WorkflowStep == OperatorStep.Result && !failedQueryVm.HasOperatorQueryResult && failedQueryVm.OperatorResultOutcome == KlidanAudioProbe.Gui.Core.Localization.Get("VmQueryFailureOutcome"), "query error navigates to an explicit failed/unknown result without displaying stale matrix rows");
var discoveryWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture, AdvertiseWindowsDiscovery = true };
var discoveryVm = new MainViewModel(worker: discoveryWorker);
Check(discoveryWorker.WindowsDiscoveryCalls == 0 && discoveryVm.DiscoverWindowsEndpointsCommand.CanExecute(null), "Windows discovery remains operator-triggered and offers to connect the Worker from the explicit discovery action");
discoveryWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected (fake)."));
Check(discoveryVm.DiscoverWindowsEndpointsCommand.CanExecute(null) && discoveryWorker.WindowsDiscoveryCalls == 0, "Windows discovery becomes available only after explicit capability handshake");
discoveryVm.DiscoverWindowsEndpointsCommand.Execute(null);
SpinWait.SpinUntil(() => discoveryVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2));
Check(discoveryWorker.WindowsDiscoveryCalls == 1 && discoveryVm.WindowsEndpoints.Single().FriendlyNameDisplay == "Fixture Interface" && discoveryVm.WindowsDiscoveryStatusText.Contains("1", StringComparison.Ordinal), "explicit discovery request presents validated Windows metadata separately from demo endpoints");
Check(discoveryVm.HasCurrentWindowsEndpoints && discoveryVm.DiscoverWindowsEndpointsCommand.CanExecute(null), "completed discovery updates current GUI binding and enables a repeat refresh");
Check(discoveryVm.SavedProfileOperatorStatus == KlidanAudioProbe.Gui.Core.Localization.Get("VmBindingDiscoveryCompleted"), "completed discovery replaces the stale loading message without implying saved profile verification");
var pendingRefreshGate = new TaskCompletionSource<WindowsEndpointDiscoveryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
var pendingRefreshWorker = new GuiStateWorkerClient([]) { AdvertiseWindowsDiscovery = true };
var pendingRefreshWorkflow = new GuiAutomaticScanWorkflow(windowsFixture) { RefreshGate = pendingRefreshGate };
var pendingRefreshVm = new MainViewModel(worker: pendingRefreshWorker, automaticScanWorkflow: pendingRefreshWorkflow);
pendingRefreshVm.AutomaticScanEngine = "V1_COMPATIBLE";
pendingRefreshWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected (fake)."));
pendingRefreshVm.DiscoverWindowsEndpointsCommand.Execute(null);
Check(pendingRefreshVm.WindowsDiscoveryLoading && !pendingRefreshVm.DiscoverWindowsEndpointsCommand.CanExecute(null) && pendingRefreshWorkflow.RefreshCalls == 1, "a pending discovery disables concurrent refresh requests");
pendingRefreshGate.SetResult(windowsFixture);
Check(PumpUntil(() => !pendingRefreshVm.WindowsDiscoveryLoading && pendingRefreshVm.HasCurrentWindowsEndpoints, TimeSpan.FromSeconds(2)) && pendingRefreshVm.DiscoverWindowsEndpointsCommand.CanExecute(null), "gated discovery releases loading and re-enables refresh after one result");
discoveryWorker.WindowsDiscovery = null;
discoveryVm.DiscoverWindowsEndpointsCommand.Execute(null);
SpinWait.SpinUntil(() => !discoveryVm.WindowsDiscoveryLoading && discoveryVm.WindowsDiscoveryStatusText.Contains("NotSupportedException", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
Check(discoveryWorker.WindowsDiscoveryCalls == 2 && !discoveryVm.HasHistoricalWindowsEndpoints && discoveryVm.WindowsDiscoveryStatusText.Contains("NotSupportedException", StringComparison.Ordinal), "failed refresh is shown as a current query failure, not a disconnected historical snapshot");
Check(!discoveryVm.WindowsDiscoveryLoading && !discoveryVm.HasCurrentWindowsEndpoints && discoveryVm.DiscoverWindowsEndpointsCommand.CanExecute(null), "failed discovery clears loading, keeps stale endpoints unavailable, and enables an explicit retry");
Check(discoveryVm.SavedProfileOperatorStatus.Contains("NotSupportedException", StringComparison.Ordinal), "failed discovery replaces the stale loading message with its failure while keeping profile binding unverified");
discoveryWorker.SetState(new(WorkerConnectionState.Disconnected, "Worker exited."));
Check(!discoveryVm.HasCurrentWindowsEndpoints && discoveryVm.HasHistoricalWindowsEndpoints && discoveryVm.LastWindowsEndpoints.Single().EndpointId == "local-windows-id", "worker disconnect retains Windows endpoint snapshot as historical evidence only");
var emptyDiscoveryWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture with { Endpoints = [] }, AdvertiseWindowsDiscovery = true };
var emptyDiscoveryVm = new MainViewModel(worker: emptyDiscoveryWorker);
emptyDiscoveryWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected (fake)."));
emptyDiscoveryVm.DiscoverWindowsEndpointsCommand.Execute(null);
SpinWait.SpinUntil(() => emptyDiscoveryWorker.WindowsDiscoveryCalls == 1 && !emptyDiscoveryVm.WindowsDiscoveryLoading, TimeSpan.FromSeconds(2));
emptyDiscoveryWorker.SetState(new(WorkerConnectionState.Disconnected, "Worker exited."));
Check(emptyDiscoveryVm.HasHistoricalWindowsEndpoints && emptyDiscoveryVm.LastWindowsEndpoints.Count == 0 && emptyDiscoveryVm.WindowsDiscoveryStatusText == KlidanAudioProbe.Gui.Core.Localization.Get("VmWindowsDiscoveryWorkerDisconnectedHistorical"), "successful empty enumeration remains historical evidence after worker disconnect");
var legacyWorker = new GuiStateWorkerClient([]);
legacyWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected (fake)."));
var legacyVm = new MainViewModel(worker: legacyWorker);
Check(!legacyVm.DiscoverWindowsEndpointsCommand.CanExecute(null) && legacyWorker.WindowsDiscoveryCalls == 0, "older protocol-1.0 worker without the additive capability remains compatible and is not queried");
WindowsEndpointDto WindowsEndpoint(int i)
{
    var direction = i % 2 == 0 ? "CAPTURE" : "RENDER";
    var state = (i % 3) switch { 0 => "DISABLED", 1 => "ACTIVE", _ => "UNPLUGGED" };
    var deviceEvidence = availableEvidence;
    var missingFormat = i == 6;
    var channelCount = i == 86 ? 128 : 8;
    var format = missingFormat
        ? new WindowsSharedModeFormatDto(unavailableEvidence, "UNAVAILABLE", null, null, null, null, null, null, "UNKNOWN", null, "UNKNOWN")
        : new WindowsSharedModeFormatDto(availableEvidence, "VALID", 65534, "PCM", channelCount, 48000, 24, 20, channelCount > 64 ? "UNSUPPORTED_CHANNEL_COUNT" : "REPRESENTABLE_1_TO_64", i == 1 ? 3u : null, i == 1 ? "INVALID" : "UNKNOWN");
    return new($"win-{i:D3}", availableEvidence, $"Audio {i % 9}", availableEvidence, direction, state, state == "ACTIVE" ? 1u : 2u, deviceEvidence, "SPEAKERS", availableEvidence, format);
}
var endpoints87 = Enumerable.Range(0, 87).Select(WindowsEndpoint).ToArray();
var browserWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = windowsFixture with { Endpoints = endpoints87 }, AdvertiseWindowsDiscovery = true };
var browserVm = new MainViewModel(worker: browserWorker);
browserWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected (fake)."));
browserVm.DiscoverWindowsEndpointsCommand.Execute(null);
SpinWait.SpinUntil(() => browserVm.HasCurrentWindowsEndpoints && browserVm.WindowsEndpointTotalCount == 87, TimeSpan.FromSeconds(2));
var activeCaptureCount = endpoints87.Count(e => e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE");
var activeRenderCount = endpoints87.Count(e => e.Direction == "RENDER" && e.DeviceState == "ACTIVE");
Check(browserVm.WindowsEndpointTotalCount == 87 && browserVm.WindowsEndpointDisplayedCount == endpoints87.Count(e => e.DeviceState == "ACTIVE") && browserVm.WindowsEndpointActiveCaptureCount == activeCaptureCount && browserVm.WindowsEndpointActiveRenderCount == activeRenderCount && browserVm.WindowsEndpointActiveOnly, "87 endpoint records are preserved with Active-only default and independent visible counts");
browserVm.WindowsEndpointDirectionFilter = "CAPTURE";
Check(browserVm.WindowsEndpointRows.All(e => e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE"), "Capture direction filter is local and respects Active-only state");
browserVm.WindowsEndpointActiveOnly = false;
Check(browserVm.WindowsEndpointRows.All(e => e.Direction == "CAPTURE") && browserVm.WindowsEndpointDisplayedCount == endpoints87.Count(e => e.Direction == "CAPTURE"), "All-states Capture filter shows disabled and unplugged endpoints");
browserVm.WindowsEndpointSearchText = "aUdIo 2";
Check(browserVm.WindowsEndpointRows.Count > 1 && browserVm.WindowsEndpointRows.All(e => e.FriendlyNameDisplay.Contains("Audio 2", StringComparison.OrdinalIgnoreCase)), "name search is case-insensitive and retains duplicate friendly names");
browserVm.WindowsEndpointDirectionFilter = "RENDER";
browserVm.WindowsEndpointSearchText = string.Empty;
Check(browserVm.WindowsEndpointRows.Count == endpoints87.Count(e => e.Direction == "RENDER"), "Render direction filter is separate from Capture");
browserVm.WindowsEndpointDirectionFilter = "ALL";
Check(browserVm.WindowsEndpointRows.Count == 87 && browserVm.WindowsEndpointRows.First().DeviceState == "ACTIVE" && browserVm.WindowsEndpointRows.Last().DeviceState != "ACTIVE" && browserVm.WindowsEndpointRows.Select(e => e.FriendlyNameDisplay).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 87, "all 87 rows remain distinct and are sorted active-first without merging names");
browserVm.SelectedWindowsEndpoint = browserVm.WindowsEndpointRows.Single(e => e.EndpointId == "win-086");
Check(browserVm.SelectedWindowsEndpoint?.ReportedChannelCountLabel == "128 · вне модели 1–64" && browserVm.WindowsEndpointInspectorFields.Any(f => f.Label == "Exclusive capability" && f.Value == "NOT TESTED") && browserVm.InspectorTitle.Contains("REAL_WINDOWS_METADATA", StringComparison.Ordinal), "selected inspector preserves exact over-64 channel count and unknown capability semantics");
browserVm.SelectedWindowsEndpoint = browserVm.WindowsEndpointRows.Single(e => e.EndpointId == "win-006");
Check(browserVm.WindowsEndpointInspectorFields.Any(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorSampleRate") && f.Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorUnknown")) && browserVm.WindowsEndpointInspectorFields.Any(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorSharedMode") && f.Tooltip?.Contains("UNAVAILABLE", StringComparison.Ordinal) == true), "missing format metadata remains unknown with property evidence in selected details");
browserVm.SelectedWindowsEndpoint = browserVm.WindowsEndpointRows.Single(e => e.EndpointId == "win-001");
Check(browserVm.WindowsEndpointInspectorFields.Any(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorSpeakerMask") && f.Value.Contains("INVALID", StringComparison.Ordinal)) && browserVm.WindowsEndpointInspectorFields.Any(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorFormFactor")), "selected device inspector includes mask evidence and endpoint form factor");
Check(browserWorker.WindowsDiscoveryCalls == 1, "filtering, search, selection, and sort projection never trigger another MMDevice enumeration");
browserWorker.SetState(new(WorkerConnectionState.Disconnected, "Worker exited."));
Check(browserVm.HasHistoricalWindowsEndpoints && !browserVm.HasCurrentWindowsEndpoints && browserVm.WindowsEndpointRows.Count == 87 && browserVm.SelectedWindowsEndpoint?.EndpointId == "win-001" && browserWorker.WindowsDiscoveryCalls == 1, "worker disconnect retains all 87 rows and selected details as historical without re-enumerating");
var provenanceVm = new MainViewModel();
Check(provenanceVm.InspectorProvenanceText == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWindowsProvenance"), "empty Windows device view never shows DEMO provenance");
provenanceVm.Navigate(ProbeScreen.Devices);
Check(provenanceVm.InspectorProvenanceText == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorDemoProvenance"), "local demo inspector retains its demo provenance inside Developer Tools");
provenanceVm.Navigate(ProbeScreen.Diagnostics);
Check(provenanceVm.InspectorProvenanceText == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWorkerProvenance"), "Worker Fake diagnostics retain separate synthetic provenance");
provenanceVm.CloseDeveloperToolsCommand.Execute(null);
Check(!provenanceVm.DeveloperToolsOpen && provenanceVm.Screen == ProbeScreen.Devices && provenanceVm.InspectorProvenanceText == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWindowsProvenance"), "returning from Developer Tools resets inspector to the real Windows device context");
string? SelectedComboLabel(ComboBox combo)
{
    combo.ApplyTemplate();
    var presenter = (ContentPresenter)combo.Template.FindName("SelectionPresenter", combo);
    TextBlock? FindText(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock text) return text;
            if (FindText(child) is { } found) return found;
        }
        return null;
    }
    return FindText(presenter)?.Text;
}
browserVm.OpenDeveloperToolsCommand.Execute(null);
var endpointWindow = new MainWindow { DataContext = browserVm, Width = 1024, Height = 720, ShowActivated = false };
endpointWindow.Show(); endpointWindow.UpdateLayout();
var searchBox = (TextBox)typeof(MainWindow).GetField("WindowsEndpointSearch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(endpointWindow)!;
var directionCombo = (ComboBox)typeof(MainWindow).GetField("WindowsEndpointDirection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(endpointWindow)!;
var sustainedModeCombo = (ComboBox)typeof(MainWindow).GetField("SustainedModeSelector", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(endpointWindow)!;
var activeCheck = (CheckBox)typeof(MainWindow).GetField("WindowsEndpointActiveOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(endpointWindow)!;
var endpointGrid = (DataGrid)typeof(MainWindow).GetField("WindowsEndpointGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(endpointWindow)!;
var inlineDetails = (Border)typeof(MainWindow).GetField("WindowsEndpointInlineDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(endpointWindow)!;
var rightInspector = (Border)typeof(MainWindow).GetField("Inspector", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(endpointWindow)!;
var inspectorProvenance = (TextBlock)typeof(MainWindow).GetField("InspectorProvenance", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(endpointWindow)!;
Check(endpointGrid.Items.Count == 87 && endpointGrid.Items.Cast<object>().Cast<WindowsEndpointDto>().Select(e => e.FriendlyNameDisplay).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 87, "runtime virtualized table keeps all 87 endpoint records and duplicate friendly names");
Check(searchBox.ActualHeight == 40 && searchBox.Background == app.Resources["TwoBrush"] && searchBox.Foreground == app.Resources["TextBrush"] && searchBox.CaretBrush == app.Resources["AmberBrush"] && searchBox.SelectionBrush == app.Resources["FocusBrush"] && searchBox.SelectionOpacity == 0.45 && !searchBox.AcceptsReturn && searchBox.TextWrapping == TextWrapping.NoWrap, "runtime search TextBox uses dark readable single-line 40-DIP style with caret and selection colors");
Keyboard.Focus(searchBox); endpointWindow.UpdateLayout();
Check(searchBox.IsKeyboardFocused && searchBox.BorderBrush == app.Resources["FocusBrush"] && searchBox.BorderThickness == new Thickness(2), "runtime search TextBox focus draws the KLIDAN focus border");
Check(SelectedComboLabel(directionCombo) == KlidanAudioProbe.Gui.Core.Localization.Get("VmAllDirections") && directionCombo.SelectedValue?.ToString() == "ALL", "runtime direction ComboBox displays the localized selected label instead of ChoiceOption.ToString()");
directionCombo.IsDropDownOpen = true; endpointWindow.UpdateLayout();
var directionPopup = (Popup)directionCombo.Template.FindName("PART_Popup", directionCombo);
Check(directionPopup.IsOpen, "runtime direction dropdown opens with its themed popup");
Check(sustainedModeCombo.SelectedValue?.ToString() == "Standard (300 s)" && SelectedComboLabel(sustainedModeCombo) == KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticSustainedStandard"), "sustained mode ComboBox localizes its label while retaining the invariant selection value");
sustainedModeCombo.SelectedValue = "Extended (600 s)"; sustainedModeCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource(); endpointWindow.UpdateLayout();
Check(browserVm.AutomaticSustainedMode == "Extended (600 s)" && SelectedComboLabel(sustainedModeCombo) == KlidanAudioProbe.Gui.Core.Localization.Get("VmAutomaticSustainedExtended"), "sustained mode selection retains its invariant value and updates the localized label");
sustainedModeCombo.SelectedValue = "Standard (300 s)"; sustainedModeCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource(); endpointWindow.UpdateLayout();
directionCombo.IsDropDownOpen = false; endpointWindow.UpdateLayout();
directionCombo.SelectedValue = "CAPTURE"; directionCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource(); endpointWindow.UpdateLayout();
Check(browserVm.WindowsEndpointDirectionFilter == "CAPTURE" && SelectedComboLabel(directionCombo) == KlidanAudioProbe.Gui.Core.Localization.Get("VmCaptureLabel"), "runtime direction selection displays the localized Capture label and updates the typed filter binding");
directionCombo.SelectedValue = "RENDER"; directionCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource(); endpointWindow.UpdateLayout();
Check(browserVm.WindowsEndpointDirectionFilter == "RENDER" && SelectedComboLabel(directionCombo) == KlidanAudioProbe.Gui.Core.Localization.Get("VmRenderLabel"), "runtime direction selection displays the localized Render label and updates the typed filter binding");
directionCombo.SelectedValue = "ALL"; directionCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource(); endpointWindow.UpdateLayout();
Check(SelectedComboLabel(directionCombo) == KlidanAudioProbe.Gui.Core.Localization.Get("VmAllDirections"), "runtime direction ComboBox restores the localized All label after changing selection");
searchBox.Text = "Audio 2"; searchBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
activeCheck.IsChecked = false; activeCheck.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource(); endpointWindow.UpdateLayout();
Check(browserVm.WindowsEndpointSearchText == "Audio 2" && !browserVm.WindowsEndpointActiveOnly && endpointGrid.Items.Count == browserVm.WindowsEndpointRows.Count && endpointGrid.Items.Count > 1, "runtime search and Active-only bindings update the 87-row table locally without another endpoint query");
searchBox.Text = string.Empty; searchBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
activeCheck.IsChecked = true; activeCheck.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
directionCombo.SelectedValue = "CAPTURE"; directionCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource(); endpointWindow.UpdateLayout();
Check(endpointGrid.Items.Count == browserVm.WindowsEndpointRows.Count && endpointGrid.Items.Cast<object>().Cast<WindowsEndpointDto>().All(e => e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE"), "runtime filtering updates visible virtualized rows for Capture and Active state");
browserVm.SelectedWindowsEndpoint = browserVm.WindowsEndpointRows.First(); endpointWindow.UpdateLayout();
Check(inspectorProvenance.Text == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWindowsProvenance") && browserVm.InspectorProvenanceText == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWindowsProvenance") && inspectorProvenance.Text != KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorDemoProvenance"), "real Windows endpoint inspector reports Windows metadata provenance");
Check(endpointGrid.Visibility == Visibility.Collapsed || endpointGrid.Columns.Sum(c => c.ActualWidth) <= endpointGrid.ActualWidth + 1, "minimum supported 1024-DIP layout hides the technical endpoint table from the normal view or fits it without horizontal scrolling");
endpointWindow.Width = 1280; endpointWindow.UpdateLayout();
Check(endpointGrid.Visibility == Visibility.Collapsed || endpointGrid.Columns.Sum(c => c.ActualWidth) <= endpointGrid.ActualWidth + 1, "1280-DIP layout keeps the technical table hidden from the normal view or fitted without horizontal scrolling");
endpointWindow.Width = 1440; endpointWindow.UpdateLayout();
Check((endpointGrid.Visibility == Visibility.Collapsed || endpointGrid.Columns.Sum(c => c.ActualWidth) <= endpointGrid.ActualWidth + 1) && browserWorker.WindowsDiscoveryCalls == 1, "1440-DIP layout keeps the technical table hidden from the normal view or fitted and performs no additional MMDevice query");
endpointWindow.Close();
var fakeWorker = new GuiStateWorkerClient(
[
    new("demo-cap", "ДЕМО · Capture", "CAPTURE", "SYNTHETIC", 2, DemoTopologyFixtures.Independent(2), true, workerFormats),
    new("demo-render", "ДЕМО · Render", "RENDER", "SYNTHETIC", 64, DemoTopologyFixtures.Independent(64), true, workerFormats)
]);
var workerVm = new MainViewModel(worker: fakeWorker);
workerVm.ConnectWorkerCommand.Execute(null);
SpinWait.SpinUntil(() => workerVm.WorkerState.State == WorkerConnectionState.ConnectedFake, TimeSpan.FromSeconds(2));
workerVm.QueryWorkerEndpointsCommand.Execute(null);
SpinWait.SpinUntil(() => workerVm.WorkerEndpoints.Count == 2, TimeSpan.FromSeconds(2));
var bindingWindow = new MainWindow { DataContext = workerVm };
bindingWindow.ShowActivated = false;
bindingWindow.Show();
bindingWindow.UpdateLayout();
var maximizeIcon = (System.Windows.Shapes.Path)typeof(MainWindow).GetField("MaximizeIcon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bindingWindow)!;
var expectedMaximizeGeometry = System.Windows.Media.Geometry.Parse("M 2,2 L 12,2 12,12 2,12 Z").ToString(System.Globalization.CultureInfo.InvariantCulture);
var expectedRestoreGeometry = System.Windows.Media.Geometry.Parse("M 6,2 L 12,2 12,8 M 9,5 L 3,5 3,12 9,12 Z").ToString(System.Globalization.CultureInfo.InvariantCulture);
string EffectiveMaximizeGeometry() => maximizeIcon.Data.ToString(System.Globalization.CultureInfo.InvariantCulture);
Check(EffectiveMaximizeGeometry() == expectedMaximizeGeometry, "runtime maximize icon uses the effective Normal-state Path.Data geometry");
bindingWindow.WindowState = WindowState.Maximized;
bindingWindow.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
bindingWindow.UpdateLayout();
Check(EffectiveMaximizeGeometry() == expectedRestoreGeometry, "runtime maximize icon changes effective Path.Data to restore geometry when maximized");
bindingWindow.WindowState = WindowState.Normal;
bindingWindow.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
bindingWindow.UpdateLayout();
Check(EffectiveMaximizeGeometry() == expectedMaximizeGeometry, "runtime maximize icon returns to normal geometry after restore");
var channelSummary = (TextBlock)typeof(MainWindow).GetField("WorkerChannelCountText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bindingWindow)!;
channelSummary.SetBinding(TextBlock.TextProperty, new Binding(nameof(workerVm.WorkerChannelSummary)) { Source = workerVm });
workerVm.SelectedWorkerEndpoint = workerVm.WorkerEndpoints.Single(e => e.Direction == "CAPTURE");
channelSummary.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
Check(workerVm.SelectedWorkerEndpoint?.Direction == "CAPTURE" && !workerVm.WorkerRenderChannelVisible && channelSummary.Text.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmCaptureLabel"), StringComparison.Ordinal) && channelSummary.Text.Contains("2 stream channels", StringComparison.Ordinal), "Capture selection shows its localized direction and topology count while retaining the invariant machine value");
workerVm.SelectedWorkerEndpoint = workerVm.WorkerEndpoints.Single(e => e.Direction == "RENDER");
channelSummary.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
Check(workerVm.WorkerRenderChannelVisible && channelSummary.Text.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmRenderLabel"), StringComparison.Ordinal), "Render selection shows its localized direction and Windows channel selector");
foreach (var channelCount in new[] { 2, 8, 32, 48, 64 })
{
    workerVm.SelectedWorkerFormat = workerVm.WorkerFormatOptions.Single(f => f.Topology.ChannelCount == channelCount);
    channelSummary.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
    Check(channelSummary.Text.Contains($"{channelCount} stream channels", StringComparison.Ordinal), $"WPF bound channel-count text displays {channelCount}");
}
workerVm.WorkerRenderChannel = 64;
workerVm.SelectedWorkerFormat = workerVm.WorkerFormatOptions.First(f => f.Topology.ChannelCount == 2);
Check(workerVm.WorkerRenderChannel == 1 && workerVm.WorkerRenderChannelOptions.Count == 2, "format change resets an out-of-range Render channel and rebuilds choices");
workerVm.WorkerRenderChannel = 2;
workerVm.WorkerRenderChannel = 3;
Check(workerVm.WorkerRenderChannel == 2, "Render channel assignment outside the selected format count is ignored");
workerVm.SelectedWorkerFormat = workerVm.WorkerFormatOptions.First(f => f.Topology.ChannelCount == 64);
Check(workerVm.WorkerRenderChannel == 2, "format change preserves a still-valid selected Render channel");
var settingsGrid = (Grid)typeof(MainWindow).GetField("WorkerFakeSettingsGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bindingWindow)!;
var updateSettingsLayout = typeof(MainWindow).GetMethod("UpdateWorkerFakeSettingsLayout", BindingFlags.Instance | BindingFlags.NonPublic)!;
updateSettingsLayout.Invoke(bindingWindow, [500d]);
Check(Grid.GetRow((FrameworkElement)typeof(MainWindow).GetField("WorkerDurationSetting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bindingWindow)!) == 0 && Grid.GetRow((FrameworkElement)typeof(MainWindow).GetField("WorkerPeriodSetting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bindingWindow)!) == 1 && Grid.GetRow((FrameworkElement)typeof(MainWindow).GetField("WorkerMmcssSetting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bindingWindow)!) == 2 && Grid.GetRow((FrameworkElement)typeof(MainWindow).GetField("WorkerScenarioSetting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bindingWindow)!) == 3 && settingsGrid.ColumnDefinitions[1].Width.Value == 0, "narrow Worker Fake settings reflow to one stretched column");
updateSettingsLayout.Invoke(bindingWindow, [700d]);
Check(settingsGrid.ColumnDefinitions[1].Width.IsStar && Grid.GetRow((FrameworkElement)typeof(MainWindow).GetField("WorkerPeriodSetting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bindingWindow)!) == 0 && Grid.GetColumn((FrameworkElement)typeof(MainWindow).GetField("WorkerPeriodSetting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bindingWindow)!) == 1, "wide Worker Fake settings restore the two-column grid");
fakeWorker.SetState(new(WorkerConnectionState.Disconnected, "Worker exited."));
workerVm.SelectedWorkerEndpoint = workerVm.LastWorkerEndpointSnapshot[0];
Check(workerVm.WorkerEndpoints.Count == 0 && workerVm.SelectedWorkerEndpoint is null && workerVm.HasHistoricalWorkerEndpoints && workerVm.HistoricalWorkerEndpointNames.Count == 2, "disconnect retains the last endpoint response as historical evidence and clears current selection");
Check(workerVm.WorkerRestartExplanation == KlidanAudioProbe.Gui.Core.Localization.Get("VmWorkerRestartRequired") && !workerVm.WorkerCurrentEndpointsVisible, "worker shutdown explains the no-restart policy and hides current availability");
Check(!workerVm.RequestWorkerRenderCommand.CanExecute(null) && !workerVm.RequestWorkerCaptureCommand.CanExecute(null) && !workerVm.StopWorkerSessionCommand.CanExecute(null), "cached disconnected endpoint data cannot start or stop a worker operation");
bindingWindow.Close();

foreach (var channel in new[] { 1, 2 })
{
    vm.SelectRenderChannel(channel);
    Check(vm.RenderChannel == channel && vm.RenderChannelOptions.Any(o => o.Value == channel && o.Value.GetType() == typeof(int)), $"channel {channel} selection is an integer option");
}
foreach (var seconds in new[] { 1, 3, 5, 10 })
{
    vm.SelectDuration(seconds);
    Check(vm.DurationSeconds == seconds && vm.DurationOptions.Any(o => o.Value == seconds && o.Value.GetType() == typeof(int)), $"duration {seconds} selection is an integer option");
}
var channelCombo = new ComboBox { ItemsSource = vm.RenderChannelOptions, SelectedValuePath = nameof(SelectionOption.Value) };
channelCombo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(vm.RenderChannel)) { Source = vm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
channelCombo.SelectedValue = 2;
channelCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
Check(vm.RenderChannel == 2 && channelCombo.SelectedValue is int, "actual WPF channel ComboBox two-way binding preserves Int32");
vm.RenderChannel = 1;
channelCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateTarget();
Check(channelCombo.SelectedValue is int channelValue && channelValue == 1, "ViewModel channel updates actual WPF selection as Int32");
var durationCombo = new ComboBox { ItemsSource = vm.DurationOptions, SelectedValuePath = nameof(SelectionOption.Value) };
durationCombo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(vm.DurationSeconds)) { Source = vm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
durationCombo.SelectedValue = 10;
durationCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
Check(vm.DurationSeconds == 10 && durationCombo.SelectedValue is int, "actual WPF duration ComboBox two-way binding preserves Int32");
vm.DurationSeconds = 3;
durationCombo.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateTarget();
Check(durationCombo.SelectedValue is int secondsValue && secondsValue == 3, "ViewModel duration updates actual WPF selection as Int32");
Throws<ArgumentOutOfRangeException>(() => vm.SelectRenderChannel(9), "invalid channel rejected");
Throws<ArgumentOutOfRangeException>(() => vm.SelectDuration(2), "invalid duration rejected");

var independent8 = DemoTopologyFixtures.Independent(8);
var stereoPairs = DemoTopologyFixtures.StereoPairs(4);
var mixedTopology = DemoTopologyFixtures.Mixed4Mono3Stereo1Six;
Check(independent8.Groups is null && stereoPairs.Groups?.Count == 4 && mixedTopology.ChannelCount == 16 && mixedTopology.Groups?.Count == 8, "logical channel groups are optional explicit metadata, never inferred from ordering");
Check(DemoTopologyFixtures.Independent(32).ChannelCount == 32 && DemoTopologyFixtures.Independent(48).ChannelCount == 48 && DemoTopologyFixtures.Independent(64).ChannelCount == 64, "independent channel topology fixtures cover 32, 48, and 64 discrete channels");
Check(DemoTopologyFixtures.AsymmetricCounts.SequenceEqual(new[] { (2, 8), (32, 32), (48, 48), (64, 32), (16, 64) }), "Capture and Render topology fixtures include required asymmetric layouts");
var maskless48 = DemoTopologyFixtures.Independent(48);
Check(maskless48.SpeakerMask is null, "32-bit speaker mask remains separate and unknown for 48-channel topology");
var topologyVm = new MainViewModel();
topologyVm.Navigate(ProbeScreen.Render);
topologyVm.SelectRenderEndpoint(new("ДЕМО · Render · 64 канала", "Render", "SYNTHETIC", "Формат A", 64, true, Topology: DemoTopologyFixtures.Independent(64), FormatTopologies: [new("Формат A", DemoTopologyFixtures.Independent(64)), new("Формат B", DemoTopologyFixtures.Independent(48))]));
foreach (var boundary in new[] { 1, 2, 8, 9, 16, 17, 32, 33, 48, 49, 64 })
{
    topologyVm.SelectRenderChannel(boundary);
    Check(topologyVm.RenderChannel == boundary && topologyVm.RenderChannelOptions.Any(o => o.Value == boundary) && topologyVm.RenderChannelBank == (boundary - 1) / 8, $"dynamic Render bank selects Windows channel {boundary}");
}
Check(topologyVm.RenderChannelBanks.Count == 8 && topologyVm.RenderChannelOptions.Count == 8, "64 Render channels remain discoverable in eight keyboard-selectable banks of eight");
Check(topologyVm.SelectedRenderEndpoint!.FormatTopologies!.Select(f => f.Topology.ChannelCount).SequenceEqual(new[] { 64, 48 }), "endpoint channel counts remain format-dependent");
Throws<ArgumentException>(() => DemoTopologyFixtures.Create(65), "topology rejects more than 64 stream channels");
Throws<ArgumentException>(() => new StreamTopologyDto(2, [new(1), new(1)]).Validate(), "topology rejects duplicate or missing stream indices");
Throws<ArgumentException>(() => new StreamTopologyDto(2, [new(1), new(2)], [new("bad", "Unknown-required", "PAIRING_GUESS", [1, 2])]).Validate(), "topology rejects unknown required group kinds");
Throws<InvalidDataException>(() => WorkerEndpointValidation.Validate(new("fake", "OUTPUT", "SYNTHETIC", "SYNTHETIC", 1, DemoTopologyFixtures.Independent(1), true)), "unknown endpoint direction enum fails closed");
var capture64Vm = new MainViewModel();
capture64Vm.Navigate(ProbeScreen.Capture);
capture64Vm.SelectCaptureEndpoint(new("ДЕМО · Capture · 64 канала", "Capture", "SYNTHETIC", "Формат A", 64, true, Topology: DemoTopologyFixtures.Independent(64)));
Start(capture64Vm);
Check(capture64Vm.Meter?.Channels.Count == 64 && capture64Vm.Meter.Channels.Select(c => c.Channel).SequenceEqual(Enumerable.Range(1, 64)), "Capture meter supports 64 ordered channels without stereo DTO limits");
Check(capture64Vm.VisibleMeterChannels.Count == 8 && capture64Vm.VisibleMeterChannels.First().Channel == 1, "Capture meter presents an accessible bank of eight channels");
capture64Vm.CaptureChannelBank = 6;
Check(capture64Vm.VisibleMeterChannels.Select(c => c.Channel).SequenceEqual(Enumerable.Range(49, 8)), "Capture channel banks expose later channels without changing stream ordering");

IpcFrameCodec.ValidatePayload(Encoding.UTF8.GetBytes("{\"ok\":true}"));
Throws<InvalidDataException>(() => IpcFrameCodec.ValidatePayload([]), "protocol rejects empty payloads");
Throws<InvalidDataException>(() => IpcFrameCodec.Encode(new byte[IpcFrameCodec.MaximumPayloadBytes + 1]), "protocol rejects oversize payloads");
Throws<Exception>(() => IpcFrameCodec.Parse(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}")), "protocol rejects duplicate JSON keys");
Throws<Exception>(() => IpcFrameCodec.Parse([0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D]), "protocol rejects invalid UTF-8");
Throws<Exception>(() => IpcFrameCodec.Parse(Encoding.UTF8.GetBytes("{\"a\": [}")), "protocol rejects malformed JSON");
Throws<Exception>(() => IpcFrameCodec.Parse(Encoding.UTF8.GetBytes("{\"a\":1,}")), "protocol rejects trailing commas");
var frames = IpcFrameCodec.Encode(Encoding.UTF8.GetBytes("{\"n\":1}")).Concat(IpcFrameCodec.Encode(Encoding.UTF8.GetBytes("{\"n\":2}"))).ToArray();
var fragmented = new FragmentReadStream(frames, 2);
var firstFrame = IpcFrameCodec.ReadPayloadAsync(fragmented, CancellationToken.None).GetAwaiter().GetResult();
var secondFrame = IpcFrameCodec.ReadPayloadAsync(fragmented, CancellationToken.None).GetAwaiter().GetResult();
Check(IpcFrameCodec.Parse(firstFrame).RootElement.GetProperty("n").GetInt32() == 1 && IpcFrameCodec.Parse(secondFrame).RootElement.GetProperty("n").GetInt32() == 2, "length-prefixed protocol handles split reads and combined frames");
Throws<EndOfStreamException>(() => IpcFrameCodec.ReadPayloadAsync(new MemoryStream([1, 0]), CancellationToken.None).GetAwaiter().GetResult(), "protocol rejects truncated headers");
Throws<EndOfStreamException>(() => IpcFrameCodec.ReadPayloadAsync(new MemoryStream([3, 0, 0, 0, 123]), CancellationToken.None).GetAwaiter().GetResult(), "protocol rejects truncated payloads");

Check(!vm.BeginCommand.CanExecute(null), "cannot request session outside matching Capture/Render screen");
vm.Navigate(ProbeScreen.Capture);
Check(!vm.BeginCommand.CanExecute(null), "Capture session requires explicit endpoint selection");
vm.SelectCaptureEndpoint(vm.CaptureEndpoints[0]);
Check(vm.BeginCommand.CanExecute(null), "available Capture endpoint selection enables Capture session");
vm.RequestConfirmation();
var requestId = vm.Events[^1].SessionId;
vm.CancelConfirmationCommand.Execute(null);
Check(vm.Session is null && vm.Events[^1].Kind == ProbeEventKind.ConsentCancelled && vm.Events[^1].SessionId == requestId, "consent cancellation records same request identity and starts no session");

Start(vm);
var started = vm.Session!;
var expectedOrder = new[] { ProbeEventKind.SessionRequested, ProbeEventKind.ConsentAccepted, ProbeEventKind.Preparing, ProbeEventKind.Started, ProbeEventKind.MeterSnapshot };
Check(vm.Events.TakeLast(expectedOrder.Length).Select(e => e.Kind).SequenceEqual(expectedOrder), "accepted Capture event sequence is typed and ordered");
Check(vm.Events.TakeLast(expectedOrder.Length).All(e => e.SessionId == started.Id && e.IsSynthetic && e.Timestamp != default), "events carry session identity, timestamp and synthetic provenance");
Check(vm.Meter is { IsCompleteWindow: true } && vm.MeterStatusText != KlidanAudioProbe.Gui.Core.Localization.Get("VmMeterWindowMissing"), "Capture meter snapshot is available");
vm.NextPulseCommand.Execute(null); vm.NextPulseCommand.Execute(null); vm.NextPulseCommand.Execute(null);
Check(vm.Meter?.Channels.All(c => c.PeakDbfs is null && c.RmsDbfs is null && c.Silent == true) == true && vm.MeterStatusText.StartsWith(string.Format(KlidanAudioProbe.Gui.Core.Localization.CurrentCulture, KlidanAudioProbe.Gui.Core.Localization.Get("VmMeterDigitalSilence"), vm.Meter.Sequence).Split('·')[0].Trim(), StringComparison.Ordinal), "completed silent meter window is distinct from unavailable");
Check(vm.Meter?.Channels.All(c => c.PeakHoldDbfs is double hold && double.IsFinite(hold)) == true, "session peak hold remains finite and available through a later silent window");

vm.SelectCaptureEndpoint(vm.CaptureEndpoints[1]);
Check(!vm.BeginCommand.CanExecute(null) && vm.SelectedCaptureEndpoint?.IsAvailable == false, "unavailable Capture endpoint cannot begin a session");
vm.Navigate(ProbeScreen.Devices);
Check(vm.SelectedCaptureEndpoint?.IsAvailable == false && vm.InspectorDetails.Any(d => d.Contains("отключено", StringComparison.Ordinal)), "selected endpoint row and contextual inspector persist independently of focus");
vm.SelectCaptureEndpoint(vm.CaptureEndpoints[0]);
vm.Navigate(ProbeScreen.Render);
Check(vm.Session?.Id == started.Id && vm.Session.EndpointName == started.EndpointName && vm.Session.Mode == SessionMode.Capture, "navigation preserves active session's immutable mode and endpoint");
Check(vm.InspectorDetails.All(d => !d.Contains("Render channel", StringComparison.Ordinal)), "Capture inspector does not present stale Render context");
Check(!vm.BeginCommand.CanExecute(null), "Render page cannot start using a Capture endpoint/session");
vm.Navigate(ProbeScreen.Capture);
vm.WarningCommand.Execute(null);
Check(vm.Session?.Lifecycle == SessionLifecycle.Started && vm.Session.Quality == StreamQuality.Warning && vm.Events[^1].Kind == ProbeEventKind.Warning, "warning updates quality without collapsing lifecycle");
vm.StopCommand.Execute(null);
Check(vm.Session?.Lifecycle == SessionLifecycle.Stopping && vm.Events[^1].Kind == ProbeEventKind.Stopping, "Stop records STOPPING");
Check(vm.MeterStatusText.StartsWith("STOPPING", StringComparison.Ordinal), "STOPPING is distinct from live and final meter presentation");
Check(!vm.StopCommand.CanExecute(null) && !vm.BeginCommand.CanExecute(null), "Stop cannot repeat and no new session starts while stopping");
vm.CompleteStop();
Check(vm.Session?.Lifecycle == SessionLifecycle.Stopped && vm.Session.CleanupStatus is null && vm.Events[^1].Kind == ProbeEventKind.Stopped, "Stop completion records STOPPED without fabricated cleanup evidence");
var stoppedId = vm.Session!.Id;
vm.CompleteSimulation(stoppedId);
Check(vm.Session?.Lifecycle == SessionLifecycle.Stopped, "late duration completion cannot overwrite a stopped session");

var workflow = new MainViewModel();
workflow.EmptyDevicesCommand.Execute(null);
Check(workflow.IsEndpointListEmpty && !workflow.HasEndpoints && workflow.EmptyEndpointMessage.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmCaptureInputDirection"), StringComparison.Ordinal), "empty endpoint fixture exposes a clear checked-data state");
workflow.RestoreDevicesCommand.Execute(null);
Check(workflow.HasEndpoints && !workflow.IsEndpointListEmpty && workflow.SelectedCaptureEndpoint is null && workflow.SelectedRenderEndpoint is null, "restore repopulates fixtures without auto-selection");
workflow.SelectCaptureEndpoint(workflow.CaptureEndpoints[0]);
workflow.Direction = SessionMode.Render;
workflow.SelectRenderEndpoint(workflow.RenderEndpoints[0]);
workflow.Direction = SessionMode.Capture;
Check(workflow.SelectedCaptureEndpoint?.Direction == "Capture" && workflow.SelectedRenderEndpoint?.Direction == "Render", "Capture and Render fixture selections remain separate");
workflow.Navigate(ProbeScreen.Capture);
var cancelCommandRequeries = 0;
workflow.CancelConfirmationCommand.CanExecuteChanged += (_, _) => cancelCommandRequeries++;
workflow.BeginCommand.Execute(null);
var cancelledId = workflow.Events[^1].SessionId;
Check(workflow.CancelConfirmationCommand.CanExecute(null) && cancelCommandRequeries > 0, "WPF confirmation buttons receive a fresh command-state notification");
workflow.CancelConfirmationCommand.Execute(null);
Check(workflow.Session is null && workflow.Events[^1] is { Kind: ProbeEventKind.ConsentCancelled } cancelled && cancelled.SessionId == cancelledId, "fresh Capture consent can be cancelled without starting a session");
Start(workflow);
var captureId = workflow.Session!.Id;
var initialSessionHold = workflow.Meter!.Channels[0].PeakHoldDbfs;
Check(workflow.Session.Mode == SessionMode.Capture && workflow.Meter is { Channels: var activeChannels } && activeChannels.Any(c => c.PeakDbfs is double p && !double.IsNegativeInfinity(p)), "accepted Capture session immediately emits a non-silent meter snapshot");
workflow.NextPulseCommand.Execute(null);
var holdBeforeSilence = workflow.Meter?.Channels[0].PeakHoldDbfs;
Check(workflow.Meter?.Channels.All(c => c.PeakDbfs is null && c.RmsDbfs is null && c.Silent == true) == true, "next alternating meter snapshot is explicit digital silence, not infinity");
Check(workflow.Meter?.Channels[0].PeakHoldDbfs == holdBeforeSilence, "silent window preserves session peak hold");
workflow.NextPulseCommand.Execute(null);
var returningMeter = workflow.Meter!;
Check(returningMeter.Channels.Any(c => c.PeakDbfs is double p && !double.IsNegativeInfinity(p)), "signal returns after the silent meter snapshot");
Check(returningMeter.Channels[0].PeakHoldDbfs == Math.Max(initialSessionHold.GetValueOrDefault(), returningMeter.Channels[0].PeakDbfs.GetValueOrDefault()), "peak hold retains the maximum across non-silent, silent, non-silent windows");
Check(workflow.Events.Where(e => e.Kind == ProbeEventKind.MeterSnapshot).All(e => e.SessionId == captureId), "meter history carries the active Capture session identity");
Check(!workflow.BeginCommand.CanExecute(null), "a second session cannot start while Capture is active");
workflow.StopCommand.Execute(null);
var stoppingEvents = workflow.Events.Count(e => e.Kind == ProbeEventKind.Stopping);
workflow.StopCommand.Execute(null);
Check(workflow.Session?.Lifecycle == SessionLifecycle.Stopping && workflow.Events.Count(e => e.Kind == ProbeEventKind.Stopping) == stoppingEvents, "repeated Stop leaves one STOPPING event");
workflow.CompleteStop();
Check(workflow.Session?.Lifecycle == SessionLifecycle.Stopped && workflow.Events.TakeLast(2).Select(e => e.Kind).SequenceEqual([ProbeEventKind.Stopping, ProbeEventKind.Stopped]), "Capture stop history is ordered STOPPING then STOPPED");
var stoppedMeter = workflow.Meter!;
workflow.NextPulseCommand.Execute(null);
Check(workflow.Meter == stoppedMeter && workflow.MeterStatusText?.StartsWith(string.Format(KlidanAudioProbe.Gui.Core.Localization.CurrentCulture, KlidanAudioProbe.Gui.Core.Localization.Get("VmMeterFinal"), stoppedMeter.Sequence).Split('·')[0].Trim(), StringComparison.Ordinal) == true, "local meter freezes its final snapshot after Stop");
workflow.Navigate(ProbeScreen.Render);
workflow.SelectRenderEndpoint(workflow.RenderEndpoints.First(e => e.IsAvailable));
workflow.SelectRenderChannel(2);
workflow.SelectDuration(10);
Start(workflow);
var renderId = workflow.Session!.Id;
Check(workflow.Session.Mode == SessionMode.Render && workflow.Session.RenderChannel == 2 && workflow.Session.DurationSeconds == 10, "Render fixture starts with channel 2 and 10-second settings");
workflow.CompleteSimulation(captureId);
Check(workflow.Session is { Id: var activeId, Lifecycle: SessionLifecycle.Started } && activeId == renderId, "stale Capture timer identity cannot complete a newer Render session");
workflow.SelectRenderChannel(1);
workflow.SelectDuration(1);
workflow.Navigate(ProbeScreen.Diagnostics);
Check(workflow.Session?.Id == renderId && workflow.Session.RenderChannel == 2 && workflow.Session.DurationSeconds == 10, "navigation and setting changes preserve active session identity and settings");
workflow.DisconnectCommand.Execute(null);
Check(workflow.Session?.Lifecycle == SessionLifecycle.Unknown && workflow.Session.CleanupStatus is null && !workflow.BeginCommand.CanExecute(null), "worker disconnect preserves unknown cleanup and blocks another session");
Check(!workflow.Events.Any(e => e.SessionId == renderId && e.Kind is ProbeEventKind.Stopping or ProbeEventKind.Stopped or ProbeEventKind.Completed), "disconnect does not fabricate Stop or Completed events");
workflow.CompleteSimulation(renderId);
Check(workflow.Session?.Lifecycle == SessionLifecycle.Unknown, "late duration callback cannot overwrite disconnected session");

var renderVm = new MainViewModel();
renderVm.Navigate(ProbeScreen.Render);
renderVm.SelectRenderChannel(2);
renderVm.SelectDuration(10);
Start(renderVm);
var immutableRenderSession = renderVm.Session!;
renderVm.SelectRenderChannel(1);
renderVm.SelectDuration(1);
Check(renderVm.Session?.RenderChannel == 2 && renderVm.Session.DurationSeconds == 10, "live settings changes do not mutate active Render session settings");
Check(renderVm.InspectorDetails.Any(d => d.Contains($"{KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWindowsChannelLabel")}: 2", StringComparison.Ordinal)) && renderVm.InspectorDetails.Any(d => d.Contains(string.Format(KlidanAudioProbe.Gui.Core.Localization.CurrentCulture, KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorDurationValue"), 10, KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorSecondsAbbreviation")), StringComparison.Ordinal)), "Render inspector follows selected live-session snapshot");
renderVm.Navigate(ProbeScreen.Capture);
Check(renderVm.InspectorDetails.Any(d => d == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorCaptureNotStarted")) && renderVm.InspectorDetails.All(d => !d.Contains($"{KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorStateLabel")}: ЗАПУЩЕНА", StringComparison.Ordinal) && !d.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWindowsChannelLabel"), StringComparison.Ordinal)), "Capture inspector does not show stale Render session context");
renderVm.Navigate(ProbeScreen.Render);
renderVm.DisconnectCommand.Execute(null);
Check(renderVm.Session?.Lifecycle == SessionLifecycle.Unknown && renderVm.Session.CleanupStatus is null, "worker disconnect means UNKNOWN and does not invent cleanup");
Check(renderVm.Events[^1].Kind == ProbeEventKind.WorkerDisconnected && !renderVm.Events.Any(e => e.Kind is ProbeEventKind.Stopping or ProbeEventKind.Stopped), "disconnect records no synthetic Stop/Stopped event");

var scenarioVm = new MainViewModel();
scenarioVm.Navigate(ProbeScreen.Capture);
Start(scenarioVm);
scenarioVm.FailCommand.Execute(null);
Check(scenarioVm.Session?.Lifecycle == SessionLifecycle.Failed && scenarioVm.Events[^1] is { Kind: ProbeEventKind.Failure, Hresult: null }, "generic failure event omits an unrelated WASAPI error code");
scenarioVm.Navigate(ProbeScreen.Capture);
Start(scenarioVm);
scenarioVm.WarningCommand.Execute(null);
Check(scenarioVm.Session?.Lifecycle == SessionLifecycle.Started && scenarioVm.Events[^1] is { Kind: ProbeEventKind.Warning, Hresult: null }, "Capture timestamp warning has no fabricated device-loss HRESULT");
scenarioVm.DeviceLossCommand.Execute(null);
Check(scenarioVm.Session?.Lifecycle == SessionLifecycle.Failed && scenarioVm.Session.Quality == StreamQuality.Unknown && scenarioVm.Events[^1] is { Kind: ProbeEventKind.DeviceLoss, Hresult: "AUDCLNT_E_DEVICE_INVALIDATED" }, "explicit device loss alone reports AUDCLNT_E_DEVICE_INVALIDATED");
scenarioVm.Navigate(ProbeScreen.Capture);
Start(scenarioVm);
scenarioVm.CompleteSimulation();
Check(scenarioVm.Session?.Lifecycle == SessionLifecycle.Completed && scenarioVm.Events[^1].Kind == ProbeEventKind.Completed, "duration completion records Completed separately from quality");

var limited = new MainViewModel();
limited.Navigate(ProbeScreen.Capture);
Start(limited);
for (var i = 0; i < MainViewModel.EventHistoryLimit + 25; i++) limited.NextPulseCommand.Execute(null);
Check(limited.Events.Count == MainViewModel.EventHistoryLimit, "event history remains bounded");
Check(limited.Events.Zip(limited.Events.Skip(1), (a, b) => b.Sequence > a.Sequence).All(x => x), "retained event sequence stays strictly increasing after coalescing old history");
Check(limited.Events.All(e => e.SessionId == limited.Session!.Id), "bounded event history retains session identity");

var empty = new MainViewModel(new EmptyProbeClient());
empty.Navigate(ProbeScreen.Capture);
Check(!empty.BeginCommand.CanExecute(null) && empty.SelectedCaptureEndpoint is null, "empty Capture fixture cannot start");
empty.Navigate(ProbeScreen.Render);
Check(!empty.BeginCommand.CanExecute(null) && empty.SelectedRenderEndpoint is null, "Capture fixture cannot satisfy Render selection");

var diagnostics = new MainViewModel();
Check(diagnostics.Events.Count == 0 && diagnostics.EmptyEventMessage == KlidanAudioProbe.Gui.Core.Localization.Get("VmDemoEventsEmpty"), "diagnostics begin empty, without fabricated failures");
Check(!typeof(IProbeClient).GetMethods().Any(m => m.Name.Contains("Start", StringComparison.Ordinal) || m.Name.Contains("Capture", StringComparison.Ordinal) || m.Name.Contains("Render", StringComparison.Ordinal)), "typed fake client exposes no audio activation/start methods");
diagnostics.Navigate(ProbeScreen.Formats);
diagnostics.SelectedFormat = diagnostics.Formats[^1];
var formatDetails = string.Join("\n", diagnostics.InspectorDetails);
Check(formatDetails.Contains("FAILED", StringComparison.Ordinal) && formatDetails.Contains("IsFormatSupported HRESULT: AUDCLNT_E_DEVICE_IN_USE", StringComparison.Ordinal) && formatDetails.Contains("Initialize HRESULT: not observed", StringComparison.Ordinal) && formatDetails.Contains("Provenance: PASSIVE_QUERY", StringComparison.Ordinal), "format inspector shows exact status, query HRESULT, unobserved Initialize and provenance");
diagnostics.Navigate(ProbeScreen.Reports);
diagnostics.SelectedReport = diagnostics.Reports[1];
Check(diagnostics.InspectorDetails.Any(d => d.Contains("warning", StringComparison.OrdinalIgnoreCase)), "report inspector follows selected report evidence");

var lateReplyVm = new MainViewModel();
var unknownStart = typeof(MainViewModel).GetField("_workerStartOutcomeUnknown", BindingFlags.Instance | BindingFlags.NonPublic)!;
typeof(MainViewModel).GetMethod("ApplyWorkerTransportFault", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(lateReplyVm, [new WorkerTransportFaultDto("REQUEST_CANCELLED", "StartCaptureMeter write may have committed", CorrelationId: Guid.NewGuid(), Command: "StartCaptureMeter")]);
Check((bool)unknownStart.GetValue(lateReplyVm)! && lateReplyVm.WorkerNotice.Length > 0 && !lateReplyVm.RequestWorkerCaptureCommand.CanExecute(null), "post-write Start cancellation keeps outcome UNKNOWN and blocks another operation");
unknownStart.SetValue(lateReplyVm, true);
typeof(MainViewModel).GetMethod("ApplyWorkerTransportFault", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(lateReplyVm, [new WorkerTransportFaultDto("LATE_RESPONSE", "Late rejection StartCaptureMeter/BUSY", CorrelationId: Guid.NewGuid(), Command: "StartCaptureMeter", ErrorCode: "BUSY")]);
Check(!(bool)unknownStart.GetValue(lateReplyVm)! && lateReplyVm.WorkerTimeline.Last().Name == "Rejected", "late correlated Start rejection clears unknown outcome and reaches diagnostics");

var connectedOnlyClient = new GuiStateWorkerClient([]);
var connectedOnlyVm = new MainViewModel(worker: connectedOnlyClient);
connectedOnlyClient.SetState(new(WorkerConnectionState.ConnectedFake, "Connected (fake)."));
connectedOnlyVm.Navigate(ProbeScreen.Diagnostics);
Check(connectedOnlyVm.InspectorSections.Select(s => s.Heading).SequenceEqual([KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorConnectionSection"), KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorSessionSection"), KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorMeasurementsSection"), KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorEvidenceSection")]) && connectedOnlyVm.InspectorSections[0].Fields[0].Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWorkerConnected") && connectedOnlyVm.InspectorSections[1].Fields.Single().Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorNotStarted"), "connected worker without a session has independent connection and idle-session inspector state");
Check(connectedOnlyVm.InspectorSections[2].Fields.All(f => f.Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorNoData") || f.Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorUnknown")) && connectedOnlyVm.InspectorSections[3].Fields.Any(f => f.Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorUnknownNotAssessed")), "empty measurements and physical evidence remain explicitly unknown");
var consentEndpoint = new WorkerEndpointDto("synthetic-capture-64", "ДЕМО · Capture · 64 канала", "CAPTURE", "SYNTHETIC", 64, DemoTopologyFixtures.Independent(64), true, [new("PCM16-48000", DemoTopologyFixtures.Independent(64))]);
typeof(MainViewModel).GetProperty(nameof(MainViewModel.WorkerEndpoints))!.SetValue(connectedOnlyVm, new[] { consentEndpoint });
connectedOnlyVm.SelectedWorkerEndpoint = consentEndpoint;
var workerConsentTextNotified = false;
connectedOnlyVm.PropertyChanged += (_, e) => workerConsentTextNotified |= e.PropertyName == nameof(MainViewModel.WorkerConsentText);
typeof(MainViewModel).GetMethod("RequestWorkerSession", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(connectedOnlyVm, ["CAPTURE"]);
Check(workerConsentTextNotified && connectedOnlyVm.WorkerConsentText.Contains("64 channels", StringComparison.Ordinal) && connectedOnlyVm.WorkerConsentText.Contains("PCM16-48000", StringComparison.Ordinal), "fresh Worker Fake consent refreshes and displays the immutable selected settings");
connectedOnlyVm.CancelWorkerConsentCommand.Execute(null);
Check(!connectedOnlyVm.WorkerConsentPending && connectedOnlyVm.WorkerConsentText == KlidanAudioProbe.Gui.Core.Localization.Get("VmWorkerConsentMissing"), "cancelling Worker Fake consent clears the displayed pending settings without sending Start");
var parameterClient = new GuiStateWorkerClient([]);
var parameterVm = new MainViewModel(worker: parameterClient);
parameterClient.SetState(new(WorkerConnectionState.ConnectedFake, "Connected (fake)."));
typeof(MainViewModel).GetProperty(nameof(MainViewModel.WorkerEndpoints))!.SetValue(parameterVm, new[] { consentEndpoint });
parameterVm.SelectedWorkerEndpoint = consentEndpoint;
var durationParameter = new ComboBox { ItemsSource = parameterVm.DurationOptions, SelectedValuePath = nameof(SelectionOption.Value) };
durationParameter.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(parameterVm.WorkerDurationSeconds)) { Source = parameterVm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
durationParameter.SelectedValue = 10;
durationParameter.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
var periodParameter = new ComboBox { ItemsSource = parameterVm.WorkerPeriodOptions, SelectedValuePath = nameof(ChoiceOption.Value) };
periodParameter.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(parameterVm.WorkerPeriod)) { Source = parameterVm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
periodParameter.SelectedValue = "MINIMUM";
periodParameter.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
var mmcssParameter = new ComboBox { ItemsSource = parameterVm.WorkerMmcssOptions, SelectedValuePath = nameof(ChoiceOption.Value) };
mmcssParameter.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(parameterVm.WorkerMmcss)) { Source = parameterVm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
mmcssParameter.SelectedValue = "PRO_AUDIO";
mmcssParameter.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
var scenarioParameter = new ComboBox { ItemsSource = parameterVm.WorkerScenarioOptions, SelectedValuePath = nameof(ChoiceOption.Value) };
scenarioParameter.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(parameterVm.WorkerScenario)) { Source = parameterVm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
scenarioParameter.SelectedValue = "WARNING";
scenarioParameter.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource();
Check(parameterVm.WorkerDurationSeconds == 10 && parameterVm.WorkerPeriod == "MINIMUM" && parameterVm.WorkerMmcss == "PRO_AUDIO" && parameterVm.WorkerScenario == "WARNING", "actual two-way parameter ComboBoxes update all Worker ViewModel values without type conversion");
typeof(MainViewModel).GetMethod("RequestWorkerSession", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(parameterVm, ["CAPTURE"]);
var selectedWorkerSettings = parameterVm.WorkerPendingSettings!;
Check(selectedWorkerSettings.DurationSeconds == 10 && selectedWorkerSettings.Period == "MINIMUM" && selectedWorkerSettings.Mmcss == "PRO_AUDIO" && selectedWorkerSettings.Scenario == "WARNING" && parameterVm.WorkerConsentText.Contains("10 s", StringComparison.Ordinal) && parameterVm.WorkerConsentText.Contains("MINIMUM", StringComparison.Ordinal) && parameterVm.WorkerConsentText.Contains("PRO_AUDIO", StringComparison.Ordinal) && parameterVm.WorkerConsentText.Contains("WARNING", StringComparison.Ordinal), "consent text and immutable pending settings reflect the current ComboBox selections");
parameterVm.WorkerConsentChecked = true;
parameterVm.ConfirmWorkerSessionCommand.Execute(null);
Check(SpinWait.SpinUntil(() => parameterClient.LastConsent is not null, TimeSpan.FromSeconds(2)) && parameterClient.LastConsent!.RequestFingerprint == WorkerConsent.Fingerprint(selectedWorkerSettings) && parameterClient.LastStartSettings == selectedWorkerSettings, "confirmed request fingerprint matches the immutable selected settings");
(MainViewModel Vm, GuiStateWorkerClient Client) CreateWorkerSession(string direction, int channels)
{
    var client = new GuiStateWorkerClient([]);
    var vm = new MainViewModel(worker: client);
    client.SetState(new(WorkerConnectionState.ConnectedFake, "Connected (fake)."));
    vm.Navigate(ProbeScreen.Diagnostics);
    var id = Guid.NewGuid();
    var settings = new WorkerSessionSettingsDto("synthetic-endpoint", direction, "PCM16-48000", 48000, "PCM16", channels, $"topology-{channels}", 5, "DEFAULT", "OFF", direction == "RENDER" ? channels : null, "NORMAL");
    typeof(MainViewModel).GetMethod("ApplyWorkerSessionAccepted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [new WorkerSessionAcceptedDto(id, Guid.NewGuid(), settings)]);
    void Event(string name, string lifecycle, string cleanup = "NOT_ATTEMPTED", WorkerCaptureMeterSnapshotDto? meter = null)
    {
        using var payload = JsonDocument.Parse($"{{\"lifecycle\":\"{lifecycle}\",\"streamQuality\":\"UNKNOWN\",\"cleanupEvidence\":\"{cleanup}\"}}");
        typeof(MainViewModel).GetMethod("ApplyWorkerEvent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [new WorkerEventDto(1, id, Guid.NewGuid(), name, payload.RootElement.Clone(), meter)]);
    }
    Event("Started", "STARTED");
    return (vm, client);
}

var (captureInspectorVm, captureInspectorClient) = CreateWorkerSession("CAPTURE", 64);
Check(captureInspectorVm.WorkerSession is { Mode: SessionMode.Capture, Lifecycle: SessionLifecycle.Started } && captureInspectorVm.InspectorSections[1].Fields.Any(f => f.Value.Contains($"64 {KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorChannelAbbreviation")}", StringComparison.Ordinal)) && captureInspectorVm.InspectorSections[1].Fields.Single(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorModeLabel")).Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmCaptureLabel") && captureInspectorVm.InspectorSections[1].Fields.All(f => f.Label != KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWindowsChannelLabel")), "running 64-channel Capture inspector shows localized mode and format topology without a Render selector");
Check(captureInspectorVm.RealInitializeDisabledReason == KlidanAudioProbe.Gui.Core.Localization.Get("VmInitializeOperationRunning"), "an active operation has an explicit Initialize-disabled reason");
var captureChannels = Enumerable.Range(1, 64).Select(i => new WorkerCaptureMeterChannelDto(i, -12, -20, false, -6, 0)).ToArray();
using (var meterPayload = JsonDocument.Parse("{\"lifecycle\":\"STARTED\",\"streamQuality\":\"UNKNOWN\",\"cleanupEvidence\":\"NOT_ATTEMPTED\"}"))
    typeof(MainViewModel).GetMethod("ApplyWorkerEvent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(captureInspectorVm, [new WorkerEventDto(2, captureInspectorVm.WorkerSession!.Id, Guid.NewGuid(), "MeterSnapshot", meterPayload.RootElement.Clone(), new WorkerCaptureMeterSnapshotDto(7, 3, 100, 64, captureChannels, "UNKNOWN", true, 2))]);
Check(captureInspectorVm.WorkerVisibleMeterChannels.Count == 8 && captureInspectorVm.WorkerVisibleMeterChannels[0].Channel == 1 && captureInspectorVm.WorkerMeterBanks.Count == 8 && captureInspectorVm.WorkerMeterSnapshotSummary.Contains("7", StringComparison.Ordinal) && captureInspectorVm.WorkerMeterCoalescingSummary.Contains("2", StringComparison.Ordinal), "64-channel Capture snapshot is projected into bounded eight-channel banks with observed sequence and coalescing");
captureInspectorVm.WorkerMeterBank = 7;
Check(captureInspectorVm.WorkerVisibleMeterChannels.Count == 8 && captureInspectorVm.WorkerVisibleMeterChannels[^1].Channel == 64 && captureInspectorVm.WorkerVisibleMeterChannels[0].AccessibleText.Contains("Peak", StringComparison.Ordinal), "last meter bank includes channel 64 with full accessible numeric detail");
foreach (var count in new[] { 8, 16, 32, 48, 64 })
{
    var (bankVm, _) = CreateWorkerSession("CAPTURE", count);
    var channels = Enumerable.Range(1, count).Select(i => new WorkerCaptureMeterChannelDto(i, -12, -20, false, -6, 0)).ToArray();
    using var bankPayload = JsonDocument.Parse("{\"lifecycle\":\"STARTED\",\"streamQuality\":\"UNKNOWN\",\"cleanupEvidence\":\"NOT_ATTEMPTED\"}");
    typeof(MainViewModel).GetMethod("ApplyWorkerEvent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bankVm, [new WorkerEventDto(2, bankVm.WorkerSession!.Id, Guid.NewGuid(), "MeterSnapshot", bankPayload.RootElement.Clone(), new WorkerCaptureMeterSnapshotDto(1, 0, 100, count, channels, "UNKNOWN", true, 0))]);
    bankVm.WorkerMeterBank = bankVm.WorkerMeterBanks.Count - 1;
    var expectedVisible = count - (bankVm.WorkerMeterBank * 8);
    Check(bankVm.WorkerMeterBanks.Count == count / 8 && bankVm.WorkerVisibleMeterChannels.Count == expectedVisible && bankVm.WorkerVisibleMeterChannels[^1].Channel == count, $"worker compact bank switching exposes all {count} channels in groups of eight");
}
captureInspectorClient.SetState(new(WorkerConnectionState.Disconnected, "Worker exited unexpectedly."));
Check(captureInspectorVm.WorkerSession?.Lifecycle == SessionLifecycle.Unknown && captureInspectorVm.InspectorSections[2].Fields.Single(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorDataLabel")).Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorMeasurementStale") && captureInspectorVm.InspectorSections[3].Fields.Single(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorCleanupLabel")).Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorUnknownNotAssessed"), "disconnect preserves the last session and meter as stale with unknown cleanup");

var (renderInspectorVm, _) = CreateWorkerSession("RENDER", 8);
Check(renderInspectorVm.WorkerSession is { Mode: SessionMode.Render, RenderChannel: 8 } && renderInspectorVm.InspectorSections[1].Fields.Any(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWindowsChannelLabel") && f.Value == "8") && renderInspectorVm.InspectorSections[1].Fields.Single(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorModeLabel")).Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmRenderLabel"), "running Render inspector shows localized mode and immutable selected Windows channel");
using (var completedPayload = JsonDocument.Parse("{\"lifecycle\":\"COMPLETED\",\"streamQuality\":\"UNKNOWN\",\"cleanupEvidence\":\"SYNTHETIC_CLEANUP_COMPLETED\"}"))
    typeof(MainViewModel).GetMethod("ApplyWorkerEvent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(renderInspectorVm, [new WorkerEventDto(2, renderInspectorVm.WorkerSession!.Id, Guid.NewGuid(), "Completed", completedPayload.RootElement.Clone())]);
Check(renderInspectorVm.WorkerSession?.Lifecycle == SessionLifecycle.Completed && !renderInspectorVm.WorkerOperationActive && renderInspectorVm.InspectorSections[1].Fields.Single(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorStateLabel")).Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorLifecycleCompleted") && renderInspectorVm.InspectorSections[3].Fields.Single(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorCleanupLabel")).Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorCleanupCompletedSynthetic"), "completed Render operation is not active and translated cleanup remains explicitly synthetic");
var (failedInspectorVm, _) = CreateWorkerSession("CAPTURE", 2);
using (var failedPayload = JsonDocument.Parse("{\"lifecycle\":\"FAILED\",\"streamQuality\":\"FAILED\",\"cleanupEvidence\":\"UNKNOWN\",\"failureCode\":\"SYNTHETIC_FAILURE\"}"))
    typeof(MainViewModel).GetMethod("ApplyWorkerEvent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(failedInspectorVm, [new WorkerEventDto(2, failedInspectorVm.WorkerSession!.Id, Guid.NewGuid(), "Failed", failedPayload.RootElement.Clone())]);
Check(failedInspectorVm.WorkerSession?.Lifecycle == SessionLifecycle.Failed && failedInspectorVm.InspectorSections[0].Fields[0].Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorWorkerConnected") && failedInspectorVm.InspectorSections[3].Fields.Single(f => f.Label == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorCleanupLabel")).Value == KlidanAudioProbe.Gui.Core.Localization.Get("VmInspectorUnknownNotAssessed"), "failed operation remains separate from connected worker and unknown cleanup");

var projectedWorkerEvent = new WorkerTimelineEntryDto(3, DateTimeOffset.UtcNow, Guid.NewGuid(), "Completed", "COMPLETED", "UNKNOWN", "SYNTHETIC_CLEANUP_COMPLETED", "synthetic completion", 42);
Check(projectedWorkerEvent.SourceText == "IPC · демо" && projectedWorkerEvent.SequenceText == "42" && projectedWorkerEvent.EventLabel == "Завершена" && projectedWorkerEvent.LifecycleLabel == "Завершена" && projectedWorkerEvent.QualityLabel == "Неизвестно / не оценено" && projectedWorkerEvent.CleanupLabel == "Фиктивная очистка завершена" && projectedWorkerEvent.SessionShortId.Length == 8, "worker event projection keeps IPC sequence, lifecycle, quality, cleanup and short session identity distinct");
var localProjection = projectedWorkerEvent with { ProtocolSequence = null, IsSynthetic = true, Name = "Warning" };
Check(localProjection.SourceText == "Локально · демо" && localProjection.EventLabel == "Предупреждение" && localProjection.DiagnosticDetails.Contains("synthetic completion", StringComparison.Ordinal), "local demo event remains distinct and selected detail retains full explanation");
Check(projectedWorkerEvent.QualityTableLabel == "Не оценено" && projectedWorkerEvent.CleanupTableLabel == "Фикт. очистка" && projectedWorkerEvent.DiagnosticDetails.Contains("Фиктивная очистка завершена", StringComparison.Ordinal) && projectedWorkerEvent.DiagnosticDetails.Contains("SYNTHETIC_CLEANUP_COMPLETED", StringComparison.Ordinal), "compact event cells keep full Russian semantics and raw cleanup code in details");
renderInspectorVm.SelectedWorkerTimelineEntry = renderInspectorVm.WorkerTimeline[0];
Check(renderInspectorVm.WorkerSelectedEventDetails.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmWorkerStartResponseCorrelated").Split("{0}", StringSplitOptions.None)[0], StringComparison.Ordinal) && renderInspectorVm.WorkerSelectedEventDetails.Contains("Синтетическое событие Started", StringComparison.Ordinal) == false, "selected worker event detail follows the selected timeline row instead of repeating the latest event");
var compactWindow = new MainWindow { DataContext = renderInspectorVm };
var workerTimelineGrid = (DataGrid)typeof(MainWindow).GetField("WorkerTimelineGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compactWindow)!;
var meterBankList = (ListBox)typeof(MainWindow).GetField("WorkerMeterBankList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compactWindow)!;
var inspectorSectionList = (ItemsControl)typeof(MainWindow).GetField("InspectorSectionList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compactWindow)!;
Check(workerTimelineGrid.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.WorkerTimeline) && workerTimelineGrid.GetBindingExpression(Selector.SelectedItemProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.SelectedWorkerTimelineEntry), "compact event table binds ordered events and selected-event details");
Check(meterBankList.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.WorkerVisibleMeterChannels) && mainWindowXaml.Contains("UniformGrid Columns=\"8\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Binding AccessibleText", StringComparison.Ordinal), "compact vertical meter bank binds eight accessible channels without horizontal scrolling");
Check(mainWindowXaml.Contains("Orientation=\"Vertical\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Binding LevelsCompactText", StringComparison.Ordinal) && mainWindowXaml.Contains("Binding PeakHoldText", StringComparison.Ordinal) && mainWindowXaml.Contains("Binding ClipsText", StringComparison.Ordinal), "mini-meter shows distinct peak/RMS bars, hold marker, clip state and compact levels");
Check(inspectorSectionList.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == nameof(MainViewModel.InspectorSections) && renderInspectorVm.InspectorSections.Count == 4, "four-section inspector binding projects the current four-section worker state");
Check(mainWindowXaml.Contains("InspectorSections", StringComparison.Ordinal) && mainWindowXaml.Contains("Copyable", StringComparison.Ordinal) && mainWindowXaml.Contains("DataGridTextColumn Header=\"{loc:Loc CleanupLabel}\"", StringComparison.Ordinal), "inspector sections, copyable UUID field and compact event evidence columns are present");
compactWindow.Close();

var timerWindow = new MainWindow();
var timerVm = (MainViewModel)typeof(MainWindow).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(timerWindow)!;
timerVm.Navigate(ProbeScreen.Capture);
timerVm.SelectCaptureEndpoint(timerVm.CaptureEndpoints.First(e => e.IsAvailable));
Start(timerVm);
var meterTimer = (DispatcherTimer)typeof(MainWindow).GetField("_meterTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(timerWindow)!;
var stopTimer = (DispatcherTimer)typeof(MainWindow).GetField("_stopTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(timerWindow)!;
var durationTimer = (DispatcherTimer)typeof(MainWindow).GetField("_durationTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(timerWindow)!;
Check(meterTimer.IsEnabled && durationTimer.IsEnabled, "Capture start enables only bounded fake timers");
timerVm.StopCommand.Execute(null);
Check(!meterTimer.IsEnabled && !durationTimer.IsEnabled && stopTimer.IsEnabled && timerVm.Session?.Lifecycle == SessionLifecycle.Stopping, "Stop stops meter and duration timers while STOPPING remains visible");
timerWindow.Close();
Check(!meterTimer.IsEnabled && !stopTimer.IsEnabled && !durationTimer.IsEnabled, "closing the window stops every demo timer");

var periodReport = new ExclusivePeriodDiscoveryReportDto(1, "wasapi-exclusive-period-discovery", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "2026-11-01T00:00:00Z", "TEST_FAKE_PERIOD_BACKEND", "endpoint", "CAPTURE", 48000, "PCM16", 2, 30000, 100000, [new(30000, 144, 30000, "ACCEPTED", "exact initialize", "0x00000000", null, null, 144, 30000, "RELEASE_ATTEMPTED", 1)]);
var periodRoundTrip = ExclusivePeriodDiscoveryReportJson.Parse(ExclusivePeriodDiscoveryReportJson.Serialize(periodReport));
Check(periodRoundTrip.Candidates.Single().RequestedFrames == 144 && periodRoundTrip.Provenance == "TEST_FAKE_PERIOD_BACKEND", "custom-period capability report round-trips with explicit fake provenance and frame evidence");
var periodDirectory = Path.Combine(Path.GetTempPath(), "klidan-period-report-" + Guid.NewGuid().ToString("N"));
var periodPath = ExclusivePeriodDiscoveryReportJson.SaveNoOverwrite(periodReport, periodDirectory);
var duplicatePeriodRejected = false;
try { ExclusivePeriodDiscoveryReportJson.SaveNoOverwrite(periodReport, periodDirectory); } catch (IOException) { duplicatePeriodRejected = true; }
Check(File.Exists(periodPath) && duplicatePeriodRejected, "custom-period report persistence is atomic, read-back validated and no-overwrite");
var catalogRoot = Path.Combine(Path.GetTempPath(), "klidan-evidence-catalog-" + Guid.NewGuid().ToString("N"));
try
{
    var catalogPeriodDirectory = Path.Combine(catalogRoot, "PeriodDiscovery"); Directory.CreateDirectory(catalogPeriodDirectory);
    var catalogPeriodPath = ExclusivePeriodDiscoveryReportJson.SaveNoOverwrite(periodReport with { ReportId = Guid.NewGuid().ToString("D"), OperationId = Guid.NewGuid().ToString("D") }, catalogPeriodDirectory);
    File.Copy(catalogPeriodPath, Path.Combine(catalogPeriodDirectory, "duplicate.json"));
    File.WriteAllText(Path.Combine(catalogRoot, "corrupt.json"), "{\"schemaVersion\":1,\"reportType\":\"wasapi-duplex-validation\"");
    var catalog = EvidenceLibraryCatalog.Refresh(catalogRoot);
    Check(catalog.Entries.Count == 3 && catalog.ValidEntries.Count == 1 && catalog.InvalidEntries.Count == 2 && catalog.ValidEntries.Single().Provenance == "SYNTHETIC" && catalog.ValidEntries.Single().IsHistorical && catalog.ValidEntries.Single().EndpointId == "endpoint", "evidence catalog indexes valid synthetic period evidence, retains corrupt/duplicate files as invalid, and preserves exact endpoint identity");
    Check(EvidenceLibraryCatalog.CompareDuplex(catalog.ValidEntries).Count == 0, "incompatible or non-Duplex evidence is not compared as Duplex measurements");
}
finally { if (Directory.Exists(catalogRoot)) Directory.Delete(catalogRoot, recursive: true); }
var normalizedRoot = Path.Combine(Path.GetTempPath(), "klidan-normalized-duplex-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(normalizedRoot);
try
{
    var clean = DuplexReport(Guid.NewGuid().ToString("D"), "capture", "render", "CUSTOM:30000:144").Report;
    var warning = DuplexReport(Guid.NewGuid().ToString("D"), "capture", "render", "CUSTOM:30000:144", status: "DUPLEX_WARNINGS", serviceFaults: 1).Report;
    var differentPeriod = DuplexReport(Guid.NewGuid().ToString("D"), "capture", "render", "MINIMUM").Report;
    File.WriteAllBytes(Path.Combine(normalizedRoot, "clean.json"), JsonSerializer.SerializeToUtf8Bytes(clean));
    File.WriteAllBytes(Path.Combine(normalizedRoot, "warning.json"), JsonSerializer.SerializeToUtf8Bytes(warning));
    File.WriteAllBytes(Path.Combine(normalizedRoot, "minimum.json"), JsonSerializer.SerializeToUtf8Bytes(differentPeriod));
    var repeatSession = Guid.NewGuid().ToString("D");
    var repeatReports = Enumerable.Range(0, 5).Select(_ => DuplexReport(Guid.NewGuid().ToString("D"), "capture", "render", "CUSTOM:30000:144").Report).ToArray();
    var repeatRuns = repeatReports.Select(report => new { sequence = 1, period = "CUSTOM:30000:144", operationId = report.GetProperty("operationId").GetString(), lifecycle = 2, cleanupStatus = "RELEASE_ATTEMPTED", message = "fixture", validation = new { operationId = report.GetProperty("operationId").GetString(), captureEndpointId = "capture", renderEndpointId = "render", report } }).ToArray();
    var repeat = new { schemaVersion = 1, reportType = "wasapi-duplex-repeatability", sessionId = repeatSession, timestampUtc = "2026-11-01T00:00:00Z", lifecycle = "COMPLETED", provenance = "REAL_WASAPI_DUPLEX", captureEndpointId = "capture", renderEndpointId = "render", durationSeconds = 5, mmcssMode = "OFF", runs = repeatRuns, periods = Array.Empty<object>(), completedRuns = 5, failedRuns = 0, unknownRuns = 0, skippedRuns = 0, plannedRuns = 5, attemptedRuns = 5 };
    File.WriteAllBytes(Path.Combine(normalizedRoot, "repeatability.json"), JsonSerializer.SerializeToUtf8Bytes(repeat));
    var normalizedCatalog = EvidenceLibraryCatalog.Refresh(normalizedRoot);
    var normalized = NormalizedDuplexEvidence.FromCatalog(normalizedCatalog);
    var series = NormalizedDuplexEvidence.Series(normalizedCatalog);
    var exact = NormalizedDuplexEvidence.Compare(normalized, DuplexComparisonMode.ExactConfigurationRepeatability);
    var exploration = NormalizedDuplexEvidence.Compare(normalized, DuplexComparisonMode.CrossPeriodExploration);
    Check(series.Count == 1 && series[0].SessionId == repeatSession && series[0].CompletedRuns == 5 && normalized.Count == 8 && normalized.Count(o => o.OperationId is not null) == 8 && normalized.All(o => o.Configuration.CaptureEndpointId == "capture" && o.Configuration.RenderEndpointId == "render"), "E1.2 normalizes standalone and five embedded repeatability operations without collapsing identities");
    Check(exact.Count == 2 && exact.Any(c => c.TotalObservations == 7 && c.Clean == 6 && c.Warnings == 1), "E1.2 exact comparison includes provenance and requested geometry and preserves five-run aggregate");
    Check(exploration.Count == 1 && exploration[0].TotalObservations == 8 && exploration[0].TotalDiscontinuities == 0, "E1.2 exploratory comparison combines periods only for compatible endpoint/format/MMCSS/duration and keeps nullable metrics truthful");
}
finally { if (Directory.Exists(normalizedRoot)) Directory.Delete(normalizedRoot, recursive: true); }
var seriesDirectory = Path.Combine(Path.GetTempPath(), "klidan-period-series-" + Guid.NewGuid().ToString("N"));
var emptySeriesPath = ExclusivePeriodDiscoverySeriesJson.SaveNoOverwrite("UNKNOWN", [], "", seriesDirectory);
Check(File.ReadAllText(emptySeriesPath).Contains("\"provenance\": \"MIXED_OR_TEST\"", StringComparison.Ordinal), "empty period series cannot be labeled REAL");
ExclusivePeriodDiscoverySeriesJson.ValidateReadBack(emptySeriesPath);
File.WriteAllText(emptySeriesPath, File.ReadAllText(emptySeriesPath).Replace("wasapi-exclusive-period-discovery-series", "tampered", StringComparison.Ordinal));
var tamperedSeriesRejected = false;
try { ExclusivePeriodDiscoverySeriesJson.ValidateReadBack(emptySeriesPath); } catch (InvalidDataException) { tamperedSeriesRejected = true; }
Check(tamperedSeriesRejected, "tampered period series fails complete read-back validation");
Directory.Delete(seriesDirectory, true);
Directory.Delete(periodDirectory, true);
using var sustainedFixture = JsonDocument.Parse(DuplexReport("op", "capture", "render", "MINIMUM", status: "DUPLEX_WARNINGS", provenance: "TEST_FAKE_WASAPI_SUSTAINED_DUPLEX").Report.GetRawText().Replace("\"durationSeconds\":5", "\"durationSeconds\":60", StringComparison.Ordinal));
var sustainedReport = sustainedFixture.RootElement.Clone();
var sustainedInventory = new[]
{
    new AutomaticScanCandidate("capture", "CAPTURE", 48_000, "PCM16", 2, 16, 16, "MINIMUM", 30_000, "OFF", AutomaticCandidateState.RUNTIME_TESTED, "fixture", "REAL_WASAPI_QUERY"),
    new AutomaticScanCandidate("render", "RENDER", 48_000, "PCM16", 2, 16, 16, "MINIMUM", 30_000, "OFF", AutomaticCandidateState.RUNTIME_TESTED, "fixture", "REAL_WASAPI_QUERY"),
    new AutomaticScanCandidate("capture", "CAPTURE", 48_000, "FLOAT32", 2, 32, 32, "MINIMUM", 30_000, "OFF", AutomaticCandidateState.NOT_TESTED, "fixture", "REAL_WASAPI_QUERY")
};
var sustainedRuns = new[] { new DuplexRepeatabilityRun(1, "MINIMUM", "op", ScanOperationLifecycle.Completed, "RELEASE_ATTEMPTED", "fixture", new DuplexValidationDto("op", "capture", "render", sustainedReport)) };
var sustainedShortlist = SustainedDuplexPlanning.Select(sustainedInventory, sustainedRuns);
Check(sustainedShortlist.Count == 1 && sustainedShortlist[0].CaptureEndpointId == "capture" && SustainedDuplexClassification.Classify(sustainedReport, 60) == "SUSTAINED_WARNINGS", "S3 sustained shortlist uses exact runtime-tested identity and preserves warning quality");
Check(SustainedDuplexClassification.Classify(sustainedReport, 300) == "INCOMPLETE", "S3 sustained classification fails closed when requested duration was not reached");
var defaultReports = new[] { "DUPLEX_CLEAN", "DUPLEX_CLEAN", "DUPLEX_WARNINGS" }.Select((status, index) => DuplexReport($"default-{index}", "capture", "render", "DEFAULT", status)).ToArray();
var aggregateInventory = sustainedInventory.Concat(new[]
{
    new AutomaticScanCandidate("capture", "CAPTURE", 48_000, "PCM16", 2, 16, 16, "DEFAULT", 100_000, "OFF", AutomaticCandidateState.RUNTIME_TESTED, "fixture", "REAL_WASAPI_QUERY"),
    new AutomaticScanCandidate("render", "RENDER", 48_000, "PCM16", 2, 16, 16, "DEFAULT", 100_000, "OFF", AutomaticCandidateState.RUNTIME_TESTED, "fixture", "REAL_WASAPI_QUERY")
}).ToArray();
var aggregateRuns = sustainedRuns.Concat(defaultReports.Select((report, index) => new DuplexRepeatabilityRun(index + 2, "DEFAULT", $"default-{index}", ScanOperationLifecycle.Completed, "RELEASE_ATTEMPTED", "fixture", report))).ToArray();
var aggregateShortlist = SustainedDuplexPlanning.Select(aggregateInventory, aggregateRuns);
Check(aggregateShortlist.Select(c => c.Period).FirstOrDefault() == "DEFAULT", "S3 shortlist ranks a repeatedly clean period ahead of a lower-period warning-only period using all verified repeatability results");
DuplexValidationDto SustainedReport(string operationId, string captureId, string renderId, string period, string mmcss, string status, int duration, string provenance = "REAL_WASAPI_SUSTAINED_DUPLEX")
{
    var normalizedOperationId = Guid.TryParse(operationId, out var parsedOperationId) && parsedOperationId != Guid.Empty ? parsedOperationId.ToString("D") : Guid.NewGuid().ToString("D");
    var raw = DuplexReport(normalizedOperationId, captureId, renderId, period, status, "REAL_WASAPI_DUPLEX").Report.GetRawText()
        .Replace("\"provenance\":\"REAL_WASAPI_DUPLEX\"", $"\"provenance\":\"{provenance}\"", StringComparison.Ordinal)
        .Replace("\"durationSeconds\":5", $"\"durationSeconds\":{duration}", StringComparison.Ordinal)
        .Replace("\"mmcss\":\"OFF\"", $"\"mmcss\":\"{mmcss}\"", StringComparison.Ordinal)
        .Replace("\"schemaVersion\":2", "\"schemaVersion\":1", StringComparison.Ordinal)
        .Replace("\"reportType\":\"wasapi-duplex-validation\"", "\"reportType\":\"wasapi-sustained-duplex-validation\"", StringComparison.Ordinal)
    .Replace("\"physicalRtl\":\"NOT_MEASURED\"", "\"physicalRtl\":\"NOT_MEASURED\"", StringComparison.Ordinal);
    var root = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
    root["physicalRtl"] = "NOT_MEASURED";
    foreach (var name in new[] { "capture", "render" })
    {
        root[name]!["endpointId"] = name == "capture" ? captureId : renderId;
        root[name]!["direction"] = name.ToUpperInvariant();
        root[name]!["durationReached"] = true;
        root[name]!["stopSucceeded"] = true;
        root[name]!["cleanup"] = "RELEASE_ATTEMPTED";
        root[name]!["frameCoveragePercent"] = 99.99;
        root[name]!["quality"] = status == "DUPLEX_CLEAN" ? "CLEAN" : "ANOMALIES_PRESENT";
        root[name]!["elapsedMilliseconds"] = duration * 1000d;
    }
    root["duplex"]!["simultaneousRuntimeMilliseconds"] = duration * 1000d;
    raw = root.ToJsonString();
    using var document = JsonDocument.Parse(raw);
    return new(normalizedOperationId, captureId, renderId, document.RootElement.Clone());
}
SustainedDuplexEvidence SustainedEvidence(DuplexValidationDto report, string period, string mmcss, int duration, string classification = "SUSTAINED_CLEAN", string cleanup = "RELEASE_ATTEMPTED", string persistence = "SAVED_AND_RELOADED", int? captureFrames = null, int? renderFrames = null) =>
    new(report.OperationId, period, mmcss, duration, "COMPLETED", classification, cleanup, persistence, report.Report,
        captureFrames ?? CustomFrames(period), renderFrames ?? CustomFrames(period));
int? CustomFrames(string period) => period.StartsWith("CUSTOM:", StringComparison.Ordinal) && int.TryParse(period.Split(':') is [_, _, var frames] ? frames : null, out var value) ? value : null;
var s9Off60 = SustainedReport("s9-off-60", "capture", "render", "CUSTOM:90000:432", "OFF", "DUPLEX_WARNINGS", 60);
var s9Off300 = SustainedReport("s9-off-300", "capture", "render", "CUSTOM:90000:432", "OFF", "DUPLEX_WARNINGS", 300);
var s9Pro300 = SustainedReport("s9-pro-300", "capture", "render", "CUSTOM:90000:432", "PRO_AUDIO", "DUPLEX_CLEAN", 300);
var s6Off60 = SustainedReport("s6-off-60", "capture", "render", "CUSTOM:60000:288", "OFF", "DUPLEX_WARNINGS", 60);
var s6Pro60 = SustainedReport("s6-pro-60", "capture", "render", "CUSTOM:60000:288", "PRO_AUDIO", "DUPLEX_CLEAN", 60);
var default60 = SustainedReport("default-60", "capture", "render", "DEFAULT", "OFF", "DUPLEX_CLEAN", 60);
var default300 = SustainedReport("default-300", "capture", "render", "DEFAULT", "OFF", "DUPLEX_CLEAN", 300);
var sustainedSelection = SustainedDuplexSelection.Recommend([
    SustainedEvidence(s9Off60, "CUSTOM:90000:432", "OFF", 60, "SUSTAINED_WARNINGS"),
    SustainedEvidence(s9Off300, "CUSTOM:90000:432", "OFF", 300, "SUSTAINED_WARNINGS"),
    SustainedEvidence(s9Pro300, "CUSTOM:90000:432", "PRO_AUDIO", 300),
    SustainedEvidence(s6Off60, "CUSTOM:60000:288", "OFF", 60, "SUSTAINED_WARNINGS"),
    SustainedEvidence(s6Pro60, "CUSTOM:60000:288", "PRO_AUDIO", 60),
    SustainedEvidence(default60, "DEFAULT", "OFF", 60),
    SustainedEvidence(default300, "DEFAULT", "OFF", 300, captureFrames: 480, renderFrames: 480)
]);
Check(sustainedSelection.Scope == "SUSTAINED_VALIDATED" && sustainedSelection.Preferred?.Period == "CUSTOM:90000:432" && sustainedSelection.Preferred.Mmcss == "PRO_AUDIO" && sustainedSelection.Fallback?.Period == "DEFAULT" && sustainedSelection.Fallback.Mmcss == "OFF" && sustainedSelection.Preferred.Confidence == "PROVISIONAL_SINGLE_SUSTAINED_RUN" && sustainedSelection.Preferred.Report.GetProperty("operationId").GetString() == s9Pro300.Report.GetProperty("operationId").GetString(), "S4 selects the lowest exact sustained-clean mode and a separately validated larger-period fallback with provisional single-run confidence");
var benignWaitRoot = JsonNode.Parse(s9Pro300.Report.GetRawText())!.AsObject();
benignWaitRoot["capture"]!["intermediateWaitTimeouts"] = 1;
benignWaitRoot["render"]!["intermediateWaitTimeouts"] = 2;
using var benignWaitDocument = JsonDocument.Parse(benignWaitRoot.ToJsonString());
var benignWaitEvidence = SustainedEvidence(s9Pro300 with { Report = benignWaitDocument.RootElement.Clone() }, "CUSTOM:90000:432", "PRO_AUDIO", 300);
Check(SustainedDuplexSelection.Recommend([benignWaitEvidence]).Preferred is not null && benignWaitEvidence.Report.GetProperty("capture").GetProperty("intermediateWaitTimeouts").GetInt64() == 1 && benignWaitEvidence.Report.GetProperty("render").GetProperty("intermediateWaitTimeouts").GetInt64() == 2, "S4 re-evaluates persisted clean evidence with benign Capture/Render waits without changing diagnostic counters or source data");
foreach (var (field, value) in new[] { ("serviceFaults", 1.0), ("discontinuities", 1.0), ("timestampErrors", 1.0), ("packetDrainLimitHits", 1.0), ("longEventIntervals", 1.0), ("frameCoveragePercent", 98.0) })
{
    var blockedRoot = JsonNode.Parse(s9Pro300.Report.GetRawText())!.AsObject();
    blockedRoot["capture"]![field] = value;
    using var blockedDocument = JsonDocument.Parse(blockedRoot.ToJsonString());
    Check(SustainedDuplexSelection.Recommend([SustainedEvidence(s9Pro300 with { Report = blockedDocument.RootElement.Clone() }, "CUSTOM:90000:432", "PRO_AUDIO", 300)]).Preferred is null, $"S4 still blocks sustained selection for {field}");
}
var oneCandidate = SustainedDuplexSelection.Recommend([SustainedEvidence(s9Pro300, "CUSTOM:90000:432", "PRO_AUDIO", 300)]);
Check(oneCandidate.Preferred is not null && oneCandidate.Fallback is null && oneCandidate.Reasons.Any(r => r.Contains("Fallback = NONE", StringComparison.Ordinal)), "S4 does not invent a fallback when only one verified sustained configuration exists");
var conflicting = SustainedDuplexSelection.Recommend([SustainedEvidence(s9Pro300, "CUSTOM:90000:432", "PRO_AUDIO", 300), SustainedEvidence(SustainedReport("s9-pro-300-warning", "capture", "render", "CUSTOM:90000:432", "PRO_AUDIO", "DUPLEX_WARNINGS", 300), "CUSTOM:90000:432", "PRO_AUDIO", 300, "SUSTAINED_WARNINGS")]);
Check(conflicting.Preferred is null || conflicting.Preferred.Period != "CUSTOM:90000:432", "S4 does not select a configuration with conflicting long-run clean/warning evidence");
var shortCleanLongWarning = SustainedDuplexSelection.Recommend([SustainedEvidence(SustainedReport("s9-off-60-clean", "capture", "render", "CUSTOM:90000:432", "OFF", "DUPLEX_CLEAN", 60), "CUSTOM:90000:432", "OFF", 60), SustainedEvidence(s9Off300, "CUSTOM:90000:432", "OFF", 300, "SUSTAINED_WARNINGS")]);
Check(shortCleanLongWarning.Preferred is null, "S4 does not let clean 60-second evidence override warning-filled 300-second evidence for the same exact mode");
var badCleanRaw = s9Pro300.Report.GetRawText().Replace("\"discontinuities\":0", "\"discontinuities\":4", StringComparison.Ordinal);
using var badCleanDocument = JsonDocument.Parse(badCleanRaw);
var badClean = SustainedEvidence(s9Pro300 with { Report = badCleanDocument.RootElement.Clone() }, "CUSTOM:90000:432", "PRO_AUDIO", 300);
Check(SustainedDuplexSelection.Recommend([badClean]).Preferred is null, "S4 preserves discontinuities and rejects a falsely clean long-run measurement");
using var corruptSustained = JsonDocument.Parse("{}");
var invalidEvidence = SustainedEvidence(SustainedReport("wrong-pair", "other-capture", "render", "CUSTOM:90000:432", "PRO_AUDIO", "DUPLEX_CLEAN", 300), "CUSTOM:90000:432", "PRO_AUDIO", 300) with { Report = corruptSustained.RootElement.Clone() };
var preservedSelection = SustainedDuplexSelection.Recommend([SustainedEvidence(s9Pro300, "CUSTOM:90000:432", "PRO_AUDIO", 300), invalidEvidence, SustainedEvidence(SustainedReport("unsafe", "capture", "render", "DEFAULT", "OFF", "DUPLEX_CLEAN", 300), "DEFAULT", "OFF", 300, cleanup: "UNKNOWN")]);
Check(preservedSelection.Preferred?.Report.GetProperty("operationId").GetString() == s9Pro300.Report.GetProperty("operationId").GetString(), "S4 ignores corrupt, mismatched-endpoint and unsafe-cleanup evidence without fabricating a replacement");
foreach (var mutation in new[] { ("endpointId", "other"), ("actualBufferFrames", "999"), ("durationReached", "false") })
{
    var raw = s9Pro300.Report.GetRawText().Replace(mutation.Item1 == "endpointId" ? "\"endpointId\":\"capture\"" : mutation.Item1 == "actualBufferFrames" ? "\"actualBufferFrames\":432" : "\"durationReached\":true", mutation.Item1 == "endpointId" ? "\"endpointId\":\"other\"" : mutation.Item1 == "actualBufferFrames" ? "\"actualBufferFrames\":999" : "\"durationReached\":false", StringComparison.Ordinal);
    using var mutated = JsonDocument.Parse(raw);
    Check(SustainedDuplexSelection.Recommend([SustainedEvidence(s9Pro300 with { Report = mutated.RootElement.Clone() }, "CUSTOM:90000:432", "PRO_AUDIO", 300)]).Preferred is null, $"S4 rejects contradictory sustained {mutation.Item1} evidence");
}
foreach (var side in new[] { "capture", "render" })
{
    var root = System.Text.Json.Nodes.JsonNode.Parse(s9Pro300.Report.GetRawText())!.AsObject();
    root[side]!["actualBufferFrames"] = 431;
    using var mutated = JsonDocument.Parse(root.ToJsonString());
    var geometry = SustainedEvidence(s9Pro300, "CUSTOM:90000:432", "PRO_AUDIO", 300) with { Report = mutated.RootElement.Clone() };
    Check(SustainedDuplexSelection.Recommend([geometry]).Preferred is null, $"S4 independently rejects mismatched {side} actual buffer geometry");
}
var audientMinimum = SustainedEvidence(SustainedReport("audient-minimum", "capture", "render", "MINIMUM", "OFF", "DUPLEX_CLEAN", 300), "MINIMUM", "OFF", 300, captureFrames: 144, renderFrames: 144);
var audientDefault = SustainedEvidence(default300, "DEFAULT", "OFF", 300, captureFrames: 480, renderFrames: 480);
Check(SustainedDuplexSelection.Recommend([audientMinimum, audientDefault]).Preferred?.Period == "MINIMUM", "S4 accepts separately verified Audient-like MINIMUM/DEFAULT geometry");
var alternateDefaultReport = default300.Report.GetRawText().Replace("\"actualBufferFrames\":480", "\"actualBufferFrames\":384", StringComparison.Ordinal);
using var alternateDefaultDocument = JsonDocument.Parse(alternateDefaultReport);
var alternateDefault = SustainedEvidence(default300 with { Report = alternateDefaultDocument.RootElement.Clone() }, "DEFAULT", "OFF", 300, captureFrames: 384, renderFrames: 384);
var alternateMinimumBase = SustainedReport("other-device-minimum", "capture", "render", "MINIMUM", "OFF", "DUPLEX_CLEAN", 300);
var alternateMinimumRaw = alternateMinimumBase.Report.GetRawText().Replace("\"actualBufferFrames\":144", "\"actualBufferFrames\":96", StringComparison.Ordinal);
using var alternateMinimumDocument = JsonDocument.Parse(alternateMinimumRaw);
var alternateMinimum = SustainedEvidence(alternateMinimumBase with { Report = alternateMinimumDocument.RootElement.Clone() }, "MINIMUM", "OFF", 300, captureFrames: 96, renderFrames: 96);
Check(SustainedDuplexSelection.Recommend([alternateMinimum, alternateDefault]).Preferred?.Period == "MINIMUM" && SustainedDuplexSelection.Recommend([alternateMinimum, alternateDefault]).Fallback?.Period == "DEFAULT", "S4 accepts non-Audient device-specific MINIMUM/DEFAULT geometry without constants");
Check(SustainedDuplexSelection.Recommend([alternateDefault with { CaptureFrames = null, RenderFrames = null }]).Preferred is null, "S4 rejects missing independently verified DEFAULT geometry");
Check(SustainedDuplexSelection.Recommend([SustainedEvidence(s9Pro300, "CUSTOM:90000:432", "PRO_AUDIO", 300)]).Preferred?.Period == "CUSTOM:90000:432", "S4 accepts exact custom period/frame identity");
Check(SustainedDuplexSelection.Recommend([SustainedEvidence(s9Pro300, "CUSTOM:90000:432", "PRO_AUDIO", 300, captureFrames: 431, renderFrames: 432)]).Preferred is null, "S4 rejects conflicting verified custom-period geometry");
var realOperation = Guid.Parse("70000000-0000-4000-8000-000000000001").ToString("D");
var realReport = SustainedReport(realOperation, "capture", "render", "CUSTOM:70000:336", "PRO_AUDIO", "DUPLEX_CLEAN", 300);
var uppercaseNativeReport = realReport.Report.GetRawText().Replace(realOperation, realOperation.ToUpperInvariant(), StringComparison.Ordinal);
using var uppercaseNativeDocument = JsonDocument.Parse(uppercaseNativeReport);
var uppercaseNativeEvidence = SustainedEvidence(realReport with { Report = uppercaseNativeDocument.RootElement.Clone() }, "CUSTOM:70000:336", "PRO_AUDIO", 300) with { OperationId = realOperation };
var uppercaseIdentitySelection = SustainedDuplexSelection.Recommend([uppercaseNativeEvidence]);
Check(uppercaseIdentitySelection.Preferred?.Period == "CUSTOM:70000:336" && uppercaseIdentitySelection.Preferred.Mmcss == "PRO_AUDIO" && uppercaseIdentitySelection.Fallback is null, "S4 accepts uppercase native/lowercase Worker GUID identity and keeps fallback NONE");
var wrongGuidEvidence = uppercaseNativeEvidence with { OperationId = Guid.Parse("70000000-0000-4000-8000-000000000002").ToString("D") };
Check(SustainedDuplexSelection.Recommend([wrongGuidEvidence]).Preferred is null, "S4 rejects a sustained report with a genuinely different operation GUID");
var malformedOperationRaw = uppercaseNativeEvidence.Report.GetRawText().Replace(uppercaseNativeEvidence.Report.GetProperty("operationId").GetString()!, "not-a-guid", StringComparison.Ordinal);
using var malformedOperationDocument = JsonDocument.Parse(malformedOperationRaw);
var malformedOperationEvidence = uppercaseNativeEvidence with { OperationId = "not-a-guid", Report = malformedOperationDocument.RootElement.Clone() };
Check(SustainedDuplexSelection.Recommend([malformedOperationEvidence]).Preferred is null, "S4 rejects identical malformed sustained operation identities");
var emptyOperation = Guid.Empty.ToString("D");
var emptyOperationRaw = uppercaseNativeEvidence.Report.GetRawText().Replace(uppercaseNativeEvidence.Report.GetProperty("operationId").GetString()!, emptyOperation, StringComparison.Ordinal);
using var emptyOperationDocument = JsonDocument.Parse(emptyOperationRaw);
var emptyOperationEvidence = uppercaseNativeEvidence with { OperationId = emptyOperation, Report = emptyOperationDocument.RootElement.Clone() };
Check(SustainedDuplexSelection.Recommend([emptyOperationEvidence]).Preferred is null, "S4 rejects Guid.Empty sustained operation identities");
var warningReport = SustainedReport(realOperation, "capture", "render", "CUSTOM:70000:336", "PRO_AUDIO", "DUPLEX_WARNINGS", 300);
Check(SustainedDuplexSelection.Recommend([SustainedEvidence(warningReport, "CUSTOM:70000:336", "PRO_AUDIO", 300, "SUSTAINED_WARNINGS")]).Preferred is null, "S4 does not recommend a 300-second sustained warning");
ProfileSustainedEvidenceReference SustainedProfileEvidence(DuplexValidationDto report, string period, string mmcss, int duration, int frames, string quality = "DUPLEX_CLEAN", string classification = "SUSTAINED_CLEAN", string cleanup = "RELEASE_ATTEMPTED", string provenance = "REAL_WASAPI_SUSTAINED_DUPLEX")
{
    var reportId = Guid.Parse(report.Report.GetProperty("reportId").GetString()!);
    return new(Guid.NewGuid(), Guid.Parse(report.OperationId), reportId, provenance, $"SUSTAINED_REPORT:{reportId:D}", profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, new("PCM16", 48000, 2, 16, 16, period), period, mmcss, duration, quality, classification, cleanup, frames, frames);
}
var sustainedPrimaryEvidence = SustainedProfileEvidence(realReport, "CUSTOM:70000:336", "PRO_AUDIO", 300, 336);
var sustainedFallbackEvidence = SustainedProfileEvidence(default300, "DEFAULT", "OFF", 300, 480);
ProfileSustainedConfiguration SustainedConfiguration(ProfileSustainedEvidenceReference evidence, string confidence = "PROVISIONAL_SINGLE_SUSTAINED_RUN") => new(profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, evidence.Format, evidence.Period, evidence.CaptureBufferFrames, evidence.RenderBufferFrames, evidence.Mmcss, evidence.DurationSeconds, evidence.Quality, confidence, [evidence.OperationId], [evidence.ReportId], [evidence.EvidenceId]);
var sustainedProfileSelection = new ProfileSustainedSelection(Guid.NewGuid().ToString("D"), SustainedConfiguration(sustainedPrimaryEvidence), SustainedConfiguration(sustainedFallbackEvidence), "SUSTAINED_VALIDATED", "PROVISIONAL_SINGLE_SUSTAINED_RUN", [], [sustainedPrimaryEvidence, sustainedFallbackEvidence]);
var sustainedProfile = duplexProfile with
{
    SchemaVersion = 4,
    ProfileId = Guid.NewGuid(),
    DuplexObservations = [],
    Recommendation = new(null, null, ["Sustained selection is stored separately from the v3 short-run recommendation."]) { Scope = "NONE", Confidence = "NOT_AVAILABLE" },
    SustainedSelection = sustainedProfileSelection
};
var sustainedProfileBytes = DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile);
var sustainedProfileRoundTrip = DeviceConfigurationProfileJson.Parse(sustainedProfileBytes);
Check(sustainedProfileRoundTrip.SchemaVersion == 4 && sustainedProfileRoundTrip.SustainedSelection?.Preferred?.DurationSeconds == 300 && sustainedProfileRoundTrip.SustainedSelection.Fallback?.CaptureBufferFrames == 480 && sustainedProfileRoundTrip.Recommendation.Preferred is null, "v4 round-trip preserves a long-duration primary, independently larger fallback and separate unavailable v3 recommendation");
var measuredUtc = DateTimeOffset.Parse("2025-01-02T03:04:05Z");
var measuredNode = JsonNode.Parse(duplexProfile.DuplexObservations[0].Report.GetRawText())!.AsObject();
measuredNode["timestampUtc"] = measuredUtc.ToString("O");
using var measuredDocument = JsonDocument.Parse(measuredNode.ToJsonString());
var measuredProfile = duplexProfile with
{
    CreatedAtUtc = measuredUtc.AddDays(10),
    DuplexObservations = [duplexProfile.DuplexObservations[0] with { Report = measuredDocument.RootElement.Clone() }]
};
var measuredEntry = new DeviceConfigurationProfileStorage.SavedProfileEntry("measurement.json", measuredProfile, "Same device", "AVAILABLE", "");
Check(measuredEntry.MeasurementTimestampUtc == measuredUtc && measuredEntry.OperatorTimestampLabel == $"Измерение: {measuredUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))}" && !measuredEntry.OperatorTimestampLabel.Contains('T'), "saved profile list prefers the verified measurement timestamp and formats it in local Russian time");
var unknownEntry = new DeviceConfigurationProfileStorage.SavedProfileEntry("unknown.json", null, "Same device", "UNAVAILABLE", "");
Check(unknownEntry.OperatorTimestampLabel == "Дата неизвестна", "saved profile list does not fabricate an unavailable timestamp");
Check(new DeviceConfigurationProfileStorage.SavedProfileEntry("new.json", sustainedProfile, "Same device", "AVAILABLE", "").OperatorResult.Contains("PRO_AUDIO", StringComparison.Ordinal) && new DeviceConfigurationProfileStorage.SavedProfileEntry("old.json", duplexProfile, "Same device", "AVAILABLE", "").OperatorResult == "v3 · Short Duplex profile", "saved profile list shows sustained configuration details and distinguishes v3 short Duplex");
var timestampRoot = Path.Combine(Path.GetTempPath(), $"klidan-profile-timestamps-{Guid.NewGuid():N}");
Directory.CreateDirectory(timestampRoot);
try
{
    File.WriteAllBytes(Path.Combine(timestampRoot, "profile-old.json"), DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile with { ProfileId = Guid.NewGuid(), CreatedAtUtc = measuredUtc }));
    File.WriteAllBytes(Path.Combine(timestampRoot, "profile-new.json"), DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile with { ProfileId = Guid.NewGuid(), CreatedAtUtc = measuredUtc.AddDays(1) }));
    var sortedEntries = DeviceConfigurationProfileStorage.DiscoverLocal(timestampRoot);
    Check(sortedEntries.Count == 2 && sortedEntries[0].SortTimestampUtc > sortedEntries[1].SortTimestampUtc && sortedEntries[0].OperatorDisplayName == sortedEntries[1].OperatorDisplayName && sortedEntries[0].Path.EndsWith("profile-new.json", StringComparison.Ordinal), "saved profiles sort newest first and keep duplicate device names deterministically comparable");
}
finally { if (Directory.Exists(timestampRoot)) Directory.Delete(timestampRoot, true); }
var sanitizedSustainedBytes = DeviceConfigurationProfileJson.SerializeSanitized(sustainedProfile);
var sanitizedSustainedText = Encoding.UTF8.GetString(sanitizedSustainedBytes);
var sanitizedSustained = DeviceConfigurationProfileJson.Parse(sanitizedSustainedBytes);
Check(sanitizedSustained.Privacy == "SANITIZED_PORTABLE" && sanitizedSustained.SustainedSelection?.Preferred?.CaptureEndpointId == "REDACTED" && sanitizedSustained.SustainedSelection.Evidence.All(e => e.CaptureEndpointId == "REDACTED") && !sanitizedSustainedText.Contains(profileEndpoint.EndpointId!, StringComparison.Ordinal) && !sanitizedSustainedText.Contains(renderEndpoint.EndpointId!, StringComparison.Ordinal), "v4 sanitized export removes sustained endpoint identity while retaining bounded report references");
var primaryWithoutFallback = sustainedProfile with { SustainedSelection = sustainedProfileSelection with { Fallback = null, Evidence = [sustainedPrimaryEvidence] } };
Check(DeviceConfigurationProfileJson.Parse(DeviceConfigurationProfileJson.SerializeLocal(primaryWithoutFallback)).SustainedSelection?.Fallback is null, "v4 primary without independently validated fallback remains explicit Fallback = NONE");
var unavailableSustained = sustainedProfile with { SustainedSelection = new(Guid.NewGuid().ToString("D"), null, null, "NONE", "WARNINGS", ["No qualifying long-duration clean configuration."], []) };
Check(DeviceConfigurationProfileJson.Parse(DeviceConfigurationProfileJson.SerializeLocal(unavailableSustained)).SustainedSelection?.Preferred is null, "v4 no-qualifying result is an explicit unavailable selection");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile with { SustainedSelection = sustainedProfileSelection with { Preferred = SustainedConfiguration(sustainedPrimaryEvidence with { DurationSeconds = 60 }), Evidence = [sustainedPrimaryEvidence with { DurationSeconds = 60 }] } }), "v4 rejects clean 60-second evidence as a sustained recommendation");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile with { SustainedSelection = sustainedProfileSelection with { Preferred = SustainedConfiguration(sustainedPrimaryEvidence with { Quality = "DUPLEX_WARNINGS" }), Evidence = [sustainedPrimaryEvidence with { Quality = "DUPLEX_WARNINGS", Classification = "SUSTAINED_WARNINGS" }] } }), "v4 rejects 300-second warning evidence");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile with { SustainedSelection = sustainedProfileSelection with { Preferred = SustainedConfiguration(sustainedPrimaryEvidence with { CaptureBufferFrames = 337 }) } }), "v4 rejects independently mismatched Capture geometry");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile with { SustainedSelection = sustainedProfileSelection with { Preferred = SustainedConfiguration(sustainedPrimaryEvidence with { Mmcss = "OFF" }) } }), "v4 rejects a different MMCSS mode");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile with { SustainedSelection = sustainedProfileSelection with { Preferred = SustainedConfiguration(sustainedPrimaryEvidence) with { CaptureEndpointId = "other-capture" } } }), "v4 rejects a different endpoint pair");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile with { SustainedSelection = sustainedProfileSelection with { Evidence = [sustainedPrimaryEvidence with { SourceProvenance = "TEST_FAKE_WASAPI_SUSTAINED_DUPLEX" }] } }), "v4 rejects non-real sustained provenance");
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile with { SustainedSelection = sustainedProfileSelection with { Evidence = [sustainedPrimaryEvidence with { CleanupStatus = "UNKNOWN" }] } }), "v4 rejects unsafe sustained cleanup");
var malformedSustainedJson = Encoding.UTF8.GetString(sustainedProfileBytes).Replace(sustainedPrimaryEvidence.OperationId.ToString("D"), "not-a-guid", StringComparison.Ordinal);
Throws<JsonException>(() => DeviceConfigurationProfileJson.Parse(Encoding.UTF8.GetBytes(malformedSustainedJson)), "v4 rejects malformed operation evidence references");
var emptySustained = sustainedProfileSelection with { Preferred = SustainedConfiguration(sustainedPrimaryEvidence with { OperationId = Guid.Empty }), Evidence = [sustainedPrimaryEvidence with { OperationId = Guid.Empty }] };
Throws<InvalidDataException>(() => DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile with { SustainedSelection = emptySustained }), "v4 rejects Guid.Empty sustained operation identities");
var persistedPrimaryReport = SustainedReport("71000000-0000-4000-8000-000000000001", profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, "CUSTOM:70000:336", "PRO_AUDIO", "DUPLEX_CLEAN", 300);
var sustainedStorageRoot = Path.Combine(Path.GetTempPath(), $"klidan-sustained-profile-{Guid.NewGuid():N}");
try
{
    var sustainedEvidenceRoot = Path.Combine(sustainedStorageRoot, "SustainedDuplex");
    Directory.CreateDirectory(sustainedEvidenceRoot);
    var persistedPath = Path.Combine(sustainedEvidenceRoot, "sustained-duplex-71000000-0000-4000-8000-000000000001.json");
    var persistedPrimary = SustainedEvidence(persistedPrimaryReport, "CUSTOM:70000:336", "PRO_AUDIO", 300, captureFrames: 336, renderFrames: 336) with { ReportPath = persistedPath };
    void WritePersisted(JsonElement report, string operationId = "71000000-0000-4000-8000-000000000001", string period = "CUSTOM:70000:336", string lifecycle = "COMPLETED", string classification = "SUSTAINED_CLEAN")
    {
        File.WriteAllBytes(persistedPath, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, reportType = "wasapi-sustained-duplex", lifecycle, classification, operationId, period, report }));
    }
    WritePersisted(persistedPrimary.Report);
    var persistedRecommendation = SustainedDuplexSelection.Recommend([persistedPrimary]);
    var resolvedPrimary = SustainedDuplexEvidenceResolver.Resolve([persistedPrimary], profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, sustainedEvidenceRoot);
    var createdV4 = DeviceConfigurationProfileFactory.CreateSustained(duplexProfile, "72000000-0000-4000-8000-000000000001", resolvedPrimary, persistedRecommendation);
    Check(createdV4.SchemaVersion == 4 && createdV4.SustainedSelection?.Preferred?.Period == "CUSTOM:70000:336" && createdV4.SustainedSelection.Preferred.CaptureBufferFrames == 336 && createdV4.SustainedSelection.Fallback is null && createdV4.SustainedSelection.SourceScanSessionId == "72000000-0000-4000-8000-000000000001", "saved real sustained evidence creates v4 Primary and preserves the scan session, geometry and fallback NONE");
    File.Delete(persistedPath);
    Throws<InvalidDataException>(() => SustainedDuplexEvidenceResolver.Resolve([persistedPrimary], profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, sustainedEvidenceRoot), "missing sustained report is rejected");
    WritePersisted(persistedPrimary.Report);
    var MutateReport = (Action<Action<System.Text.Json.Nodes.JsonObject>>)(mutation =>
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(persistedPrimary.Report.GetRawText())!.AsObject();
        mutation(node);
        WritePersisted(JsonDocument.Parse(node.ToJsonString()).RootElement.Clone());
    });
    MutateReport(node => node["reportId"] = "71000000-0000-4000-8000-000000000099");
    Throws<InvalidDataException>(() => SustainedDuplexEvidenceResolver.Resolve([persistedPrimary], profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, sustainedEvidenceRoot), "substituted report identity is rejected");
    WritePersisted(persistedPrimary.Report);
    MutateReport(node => node["requested"]!["period"] = "DEFAULT");
    Throws<InvalidDataException>(() => SustainedDuplexEvidenceResolver.Resolve([persistedPrimary], profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, sustainedEvidenceRoot), "conflicting persisted period is rejected");
    WritePersisted(persistedPrimary.Report);
    MutateReport(node => node["requested"]!["mmcss"] = "OFF");
    Throws<InvalidDataException>(() => SustainedDuplexEvidenceResolver.Resolve([persistedPrimary], profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, sustainedEvidenceRoot), "conflicting persisted MMCSS is rejected");
    WritePersisted(persistedPrimary.Report);
    MutateReport(node => node["capture"]!["actualBufferFrames"] = 337);
    Throws<InvalidDataException>(() => SustainedDuplexEvidenceResolver.Resolve([persistedPrimary], profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, sustainedEvidenceRoot), "conflicting persisted Capture geometry is rejected");
    WritePersisted(persistedPrimary.Report);
    MutateReport(node => node["render"]!["serviceFaults"] = 1);
    Throws<InvalidDataException>(() => SustainedDuplexEvidenceResolver.Resolve([persistedPrimary], profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, sustainedEvidenceRoot), "conflicting persisted quality counters are rejected");
    WritePersisted(persistedPrimary.Report);
    MutateReport(node => node["provenance"] = "TEST_FAKE_WASAPI_SUSTAINED_DUPLEX");
    Throws<InvalidDataException>(() => SustainedDuplexEvidenceResolver.Resolve([persistedPrimary], profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, sustainedEvidenceRoot), "synthetic persisted provenance is rejected");
    File.WriteAllText(persistedPath, "{");
    Throws<InvalidDataException>(() => SustainedDuplexEvidenceResolver.Resolve([persistedPrimary], profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, sustainedEvidenceRoot), "corrupt sustained report is rejected");
    WritePersisted(persistedPrimary.Report);
    Throws<InvalidDataException>(() => SustainedDuplexEvidenceResolver.Resolve([persistedPrimary with { ReportPath = Path.Combine(sustainedEvidenceRoot, "..", "outside.json") }], profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, sustainedEvidenceRoot), "out-of-root sustained report hint is rejected");
    var upperOperation = persistedPrimary.OperationId.ToUpperInvariant();
    var upperReportRaw = persistedPrimary.Report.GetRawText().Replace(persistedPrimary.OperationId, upperOperation, StringComparison.Ordinal);
    using var upperReportDocument = JsonDocument.Parse(upperReportRaw);
    WritePersisted(upperReportDocument.RootElement.Clone(), upperOperation);
    var upperEvidence = persistedPrimary with { OperationId = upperOperation, Report = upperReportDocument.RootElement.Clone(), ReportPath = persistedPath };
    Check(SustainedDuplexEvidenceResolver.Resolve([upperEvidence], profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, sustainedEvidenceRoot).Single().OperationId == persistedPrimary.OperationId, "uppercase/lowercase persisted GUID identities are accepted semantically");
    WritePersisted(persistedPrimary.Report);
    var sustainedSaveSession = "73000000-0000-4000-8000-000000000001";
    var sustainedOperation = new ScanOperationResult(new(ScanStage.Duplex, profileEndpoint.EndpointId, "PAIR", "PCM16", 48000, 2, "MINIMUM", 5, duplexMinimum.OperationId, "OFF", profileEndpoint.EndpointId, renderEndpoint.EndpointId), ScanOperationLifecycle.Completed, "short real fixture", "RELEASE_ATTEMPTED", new(Duplex: duplexMinimum));
    var sustainedSave = DeviceConfigurationScanStorage.Save(sustainedSaveSession, ScanRunLifecycle.Completed, new(profileEndpoint, renderEndpoint, ScanPairIdentity.OperatorSelectedPair), [sustainedOperation], [], persistedRecommendation, sustainedStorageRoot, sustainedProfile: new(sustainedSaveSession, [persistedPrimary], persistedRecommendation, sustainedEvidenceRoot));
    var sustainedSavedProfile = DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(sustainedSave.ProfilePaths.Single()));
    Check(sustainedSave.Status == "SAVED_AND_RELOADED" && sustainedSavedProfile.SchemaVersion == 4 && sustainedSavedProfile.SustainedSelection?.Preferred?.OperationIds.Single().ToString("D") == persistedPrimary.OperationId && DeviceConfigurationScanStorage.LoadSummary(sustainedSave.SummaryPath!).ProfilePaths.Single() == sustainedSave.ProfilePaths.Single(), "automatic scan persistence atomically saves and reloads the authoritative v4 profile with original sustained operation identity");
    var sustainedCatalogRoot = Path.Combine(sustainedStorageRoot, "catalog");
    Directory.CreateDirectory(sustainedCatalogRoot);
    File.WriteAllBytes(Path.Combine(sustainedCatalogRoot, "sustained.json"), JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, reportType = "wasapi-sustained-duplex", lifecycle = "COMPLETED", classification = "SUSTAINED_CLEAN", operationId = persistedPrimary.OperationId, period = persistedPrimary.Period, report = persistedPrimary.Report }));
    File.WriteAllText(Path.Combine(sustainedCatalogRoot, "corrupt-sustained.json"), "{}");
    var sustainedCatalog = EvidenceLibraryCatalog.Refresh(sustainedCatalogRoot);
    Check(sustainedCatalog.ValidEntries.Any(e => e.RecordType == EvidenceLibraryRecordType.SustainedDuplex && e.OperationId == persistedPrimary.OperationId) && sustainedCatalog.InvalidEntries.Any(e => e.Path.EndsWith("corrupt-sustained.json", StringComparison.Ordinal)), "EvidenceLibrary catalogs valid persisted sustained reports and retains corrupt sustained data as invalid");
    var rejectedSession = "73000000-0000-4000-8000-000000000002";
    var rejectedSave = DeviceConfigurationScanStorage.Save(rejectedSession, ScanRunLifecycle.Completed, new(profileEndpoint, renderEndpoint, ScanPairIdentity.OperatorSelectedPair), [sustainedOperation], [], persistedRecommendation, Path.Combine(sustainedStorageRoot, "rejected"), sustainedProfile: new(rejectedSession, [persistedPrimary], persistedRecommendation, Path.Combine(sustainedStorageRoot, "missing-evidence-root")));
    Check(rejectedSave.Status == "SUMMARY_SAVED_PROFILE_FAILED" && rejectedSave.SummaryPath is not null && DeviceConfigurationScanStorage.LoadSummary(rejectedSave.SummaryPath).ProfilePaths.Count == 0, "v4 profile-save rejection preserves the original scan summary without a fabricated profile");
}
finally { if (Directory.Exists(sustainedStorageRoot)) Directory.Delete(sustainedStorageRoot, true); }
WindowsEndpointDto DiscoveryEndpoint(string id, string direction, string state = "ACTIVE", string name = "Audient") => windowsFixture.Endpoints[0] with { EndpointId = id, Direction = direction, DeviceState = state, FriendlyName = name };
WindowsEndpointDiscoveryDto Discovery(params WindowsEndpointDto[] endpoints) => new("REAL_WINDOWS_METADATA", "COMPLETE", 0, 0, [], endpoints);
var bindingDiscovery = Discovery(DiscoveryEndpoint(profileEndpoint.EndpointId!, "CAPTURE"), DiscoveryEndpoint(renderEndpoint.EndpointId!, "RENDER"));
var exactBinding = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, bindingDiscovery, "discovery-1", "profile-selection-1");
Check(exactBinding.Overall == DeviceProfileBindingAssessment.ExactBindingsPresentInDiscovery && exactBinding.Capture == DeviceProfileEndpointAssessment.ExactActive && exactBinding.Render == DeviceProfileEndpointAssessment.ExactActive && exactBinding.ExactEndpointIdentities.SequenceEqual([profileEndpoint.EndpointId!, renderEndpoint.EndpointId!]) && exactBinding.RequiresFurtherVerification, "S5-B1 accepts an exact active Capture/Render pair without promoting historical evidence to current verification");
var swappedBinding = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, Discovery(DiscoveryEndpoint(profileEndpoint.EndpointId!, "RENDER"), DiscoveryEndpoint(renderEndpoint.EndpointId!, "CAPTURE")), "discovery-2", "profile-selection-1");
Check(swappedBinding.Overall == DeviceProfileBindingAssessment.AvailableMetadataContradiction && swappedBinding.Capture == DeviceProfileEndpointAssessment.MetadataContradiction && swappedBinding.Render == DeviceProfileEndpointAssessment.MetadataContradiction, "S5-B1 rejects swapped endpoint directions");
var captureMissing = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, Discovery(DiscoveryEndpoint(renderEndpoint.EndpointId!, "RENDER")), "discovery-3", "profile-selection-1");
var bothMissing = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, Discovery(), "discovery-4", "profile-selection-1");
Check(captureMissing.Overall == DeviceProfileBindingAssessment.CaptureMissing && bothMissing.Overall == DeviceProfileBindingAssessment.BothEndpointsMissing, "S5-B1 distinguishes missing Capture from both missing endpoints in complete discovery");
var sameNameDifferentId = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, Discovery(DiscoveryEndpoint("different-capture", "CAPTURE", name: "Audient"), DiscoveryEndpoint(renderEndpoint.EndpointId!, "RENDER", name: "Audient")), "discovery-5", "profile-selection-1");
Check(sameNameDifferentId.Overall == DeviceProfileBindingAssessment.CaptureMissing, "S5-B1 never matches a renamed or duplicate-friendly-name endpoint by name");
var disabledBinding = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, Discovery(DiscoveryEndpoint(profileEndpoint.EndpointId!, "CAPTURE", "DISABLED"), DiscoveryEndpoint(renderEndpoint.EndpointId!, "RENDER")), "discovery-6", "profile-selection-1");
Check(disabledBinding.Overall == DeviceProfileBindingAssessment.EndpointDisabledOrUnplugged && disabledBinding.Capture == DeviceProfileEndpointAssessment.DisabledOrUnplugged, "S5-B1 reports disabled endpoints distinctly");
var partialBinding = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, new WindowsEndpointDiscoveryDto("REAL_WINDOWS_METADATA", "PARTIAL", unchecked((int)0x80070490), 0, [new("CAPTURE", "ENUMERATION", unchecked((int)0x80070490))], [DiscoveryEndpoint(renderEndpoint.EndpointId!, "RENDER")]), "discovery-7", "profile-selection-1");
Check(partialBinding.Overall == DeviceProfileBindingAssessment.DiscoveryIncomplete && partialBinding.Capture == DeviceProfileEndpointAssessment.Missing && partialBinding.Reasons.Any(r => r.Contains("NOT_DEFINITIVE", StringComparison.Ordinal)), "S5-B1 does not call a missing endpoint definitively absent in partial discovery");
var syntheticBinding = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, bindingDiscovery with { Provenance = "TEST_FAKE_FORMAT_BACKEND" }, "discovery-8", "profile-selection-1");
var failedBinding = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, bindingDiscovery with { OperationState = "FAILED" }, "discovery-9", "profile-selection-1");
Check(syntheticBinding.Overall == DeviceProfileBindingAssessment.DiscoveryProvenanceInvalid && failedBinding.Overall == DeviceProfileBindingAssessment.DiscoveryFailed, "S5-B1 rejects synthetic and failed discovery before comparing endpoints");
var duplicateBinding = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, Discovery(DiscoveryEndpoint(profileEndpoint.EndpointId!, "CAPTURE"), DiscoveryEndpoint(profileEndpoint.EndpointId!, "CAPTURE"), DiscoveryEndpoint(renderEndpoint.EndpointId!, "RENDER")), "discovery-10", "profile-selection-1");
Check(duplicateBinding.Overall == DeviceProfileBindingAssessment.AmbiguousOrDuplicateIdentity, "S5-B1 rejects duplicate endpoint identities");
var renamedBinding = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, Discovery(DiscoveryEndpoint(profileEndpoint.EndpointId!, "CAPTURE", name: "Renamed Interface"), DiscoveryEndpoint(renderEndpoint.EndpointId!, "RENDER", name: "Renamed Interface")), "discovery-11", "profile-selection-1");
Check(renamedBinding.Overall == DeviceProfileBindingAssessment.ExactBindingsPresentInDiscovery, "S5-B1 accepts a renamed endpoint when its exact identity is stable");
var insufficientMetadata = DeviceProfileBindingRevalidation.Evaluate(sustainedProfile, Discovery(DiscoveryEndpoint(profileEndpoint.EndpointId!, "CAPTURE") with { EndpointIdEvidence = unavailableEvidence }, DiscoveryEndpoint(renderEndpoint.EndpointId!, "RENDER")), "discovery-12", "profile-selection-1");
Check(insufficientMetadata.Overall == DeviceProfileBindingAssessment.MetadataUnavailableOrInsufficient && insufficientMetadata.Limitations.Any(l => l.Contains("DRIVER_FIRMWARE", StringComparison.Ordinal)), "S5-B1 exposes unavailable identity and driver metadata limitations");
var sanitizedBinding = DeviceProfileBindingRevalidation.Evaluate(DeviceConfigurationProfileJson.Parse(DeviceConfigurationProfileJson.SerializeSanitized(sustainedProfile)), bindingDiscovery, "discovery-13", "profile-selection-1");
var corruptBinding = DeviceProfileBindingRevalidation.Evaluate(Encoding.UTF8.GetBytes("{\"schemaVersion\":4}"), bindingDiscovery, "discovery-14", "profile-selection-1");
Check(sanitizedBinding.Overall == DeviceProfileBindingAssessment.SanitizedUnboundProfile && corruptBinding.Overall == DeviceProfileBindingAssessment.ProfileInvalidOrUnsupported && corruptBinding.SourceProfileId == Guid.Empty, "S5-B1 keeps sanitized and corrupt profiles unbound and non-applicable");
var revalidationState = new DeviceProfileBindingRevalidationState();
_ = revalidationState.Evaluate(sustainedProfile, bindingDiscovery, "generation-1", "selection-1");
var invalidatedByGeneration = revalidationState.Evaluate(sustainedProfile, bindingDiscovery, "generation-2", "selection-1");
var generationInvalidation = revalidationState.LastInvalidation;
var invalidatedByWorker = revalidationState.Invalidate(DeviceProfileBindingInvalidationReason.WorkerDisconnected);
Check(invalidatedByGeneration.Overall == DeviceProfileBindingAssessment.ExactBindingsPresentInDiscovery && generationInvalidation?.Reasons.Any(r => r.Contains("ENDPOINT_REDISCOVERED", StringComparison.Ordinal)) == true && invalidatedByWorker.Overall == DeviceProfileBindingAssessment.BindingInvalidated && !invalidatedByWorker.IsCurrent, "S5-B1 invalidates prior results on discovery generation changes and worker disconnect");
foreach (var invalidation in new[] { DeviceProfileBindingInvalidationReason.WorkerRestarted, DeviceProfileBindingInvalidationReason.WorkerReplaced, DeviceProfileBindingInvalidationReason.DeviceRemoved, DeviceProfileBindingInvalidationReason.ProfileSelectionChanged, DeviceProfileBindingInvalidationReason.DiscoverySnapshotStale })
    Check(revalidationState.Invalidate(invalidation).Reasons.Any(r => r.EndsWith(invalidation switch { DeviceProfileBindingInvalidationReason.WorkerRestarted => "WORKER_RESTARTED", DeviceProfileBindingInvalidationReason.WorkerReplaced => "WORKER_REPLACED", DeviceProfileBindingInvalidationReason.DeviceRemoved => "DEVICE_REMOVED", DeviceProfileBindingInvalidationReason.ProfileSelectionChanged => "PROFILE_SELECTION_CHANGED", _ => "DISCOVERY_SNAPSHOT_STALE" }, StringComparison.Ordinal)), $"S5-B1 exposes explicit {invalidation} invalidation");
var recoveryRoot = Path.Combine(Path.GetTempPath(), $"klidan-offline-recovery-{Guid.NewGuid():N}");
var recoveryScans = Path.Combine(recoveryRoot, "Scans");
var recoveryProfiles = Path.Combine(recoveryRoot, "Profiles");
var recoveryEvidence = Path.Combine(recoveryRoot, "SustainedDuplex");
Directory.CreateDirectory(recoveryScans); Directory.CreateDirectory(recoveryProfiles); Directory.CreateDirectory(recoveryEvidence);
try
{
    var recoverySession = "f60e743c-5656-42be-99a8-ddcf3b6f3f7f";
    var recoveryBasePath = DeviceConfigurationProfileStorage.SaveLocal(duplexProfile, recoveryProfiles);
    JsonElement RecoveryReport(string operationId, string period, string mmcss, int durationSeconds, string status, int serviceFaults = 0)
    {
        var node = JsonNode.Parse(DuplexReport(operationId, profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, period, status: status, serviceFaults: serviceFaults, provenance: "REAL_WASAPI_SUSTAINED_DUPLEX").Report.GetRawText())!.AsObject();
        node["schemaVersion"] = 1;
        node["reportType"] = "wasapi-sustained-duplex-validation";
        node["timestampUtc"] = "2026-10-31T10:00:00Z";
        node["requested"]!["mmcss"] = mmcss;
        node["requested"]!["durationSeconds"] = durationSeconds;
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }
    void WriteRecoveryEvidence(JsonElement report, string classification, string period)
    {
        var operationId = report.GetProperty("operationId").GetString()!;
        var envelope = new { schemaVersion = 1, reportType = "wasapi-sustained-duplex", lifecycle = "COMPLETED", classification, operationId, period, report };
        File.WriteAllBytes(Path.Combine(recoveryEvidence, $"sustained-duplex-{Guid.Parse(operationId):D}.json"), JsonSerializer.SerializeToUtf8Bytes(envelope));
    }
    var cleanOperation = Guid.NewGuid().ToString("D");
    var warningOperation = Guid.NewGuid().ToString("D");
    var cleanReport = RecoveryReport(cleanOperation, "CUSTOM:70000:336", "PRO_AUDIO", 300, "DUPLEX_CLEAN");
    var recoveryWarningReport = RecoveryReport(warningOperation, "CUSTOM:40000:192", "PRO_AUDIO", 300, "DUPLEX_WARNINGS", serviceFaults: 1);
    WriteRecoveryEvidence(cleanReport, "SUSTAINED_CLEAN", "CUSTOM:70000:336");
    WriteRecoveryEvidence(recoveryWarningReport, "SUSTAINED_WARNINGS", "CUSTOM:40000:192");
    var recoveryCandidates = new[]
    {
        new AutomaticScanSummaryCandidate(profileEndpoint.EndpointId!, "CAPTURE", 48_000, "PCM16", 2, 16, 16, "CUSTOM:70000:336", 70_000, "OFF", "RUNTIME_TESTED", "fixture", "REAL_WASAPI_QUERY"),
        new AutomaticScanSummaryCandidate(renderEndpoint.EndpointId!, "RENDER", 48_000, "PCM16", 2, 16, 16, "CUSTOM:70000:336", 70_000, "OFF", "RUNTIME_TESTED", "fixture", "REAL_WASAPI_QUERY"),
        new AutomaticScanSummaryCandidate(profileEndpoint.EndpointId!, "CAPTURE", 48_000, "PCM16", 2, 16, 16, "CUSTOM:40000:192", 40_000, "OFF", "RUNTIME_TESTED", "fixture", "REAL_WASAPI_QUERY"),
        new AutomaticScanSummaryCandidate(renderEndpoint.EndpointId!, "RENDER", 48_000, "PCM16", 2, 16, 16, "CUSTOM:40000:192", 40_000, "OFF", "RUNTIME_TESTED", "fixture", "REAL_WASAPI_QUERY")
    };
    var recoveryChildren = new[]
    {
        new AutomaticScanChildOperation("SUSTAINED_VALIDATION", cleanOperation.ToUpperInvariant(), cleanReport.GetProperty("reportId").GetString()!.ToUpperInvariant(), "COMPLETED", "REAL_WASAPI_SUSTAINED_DUPLEX", "RELEASE_ATTEMPTED", "fixture", Path.Combine(recoveryRoot, "untrusted.json"), "CUSTOM:70000:336/PRO_AUDIO/300s", null, "PAIR"),
        new AutomaticScanChildOperation("SUSTAINED_VALIDATION", warningOperation, recoveryWarningReport.GetProperty("reportId").GetString(), "COMPLETED", "REAL_WASAPI_SUSTAINED_DUPLEX", "RELEASE_ATTEMPTED", "fixture", null, "CUSTOM:40000:192/PRO_AUDIO/300s", null, "PAIR")
    };
    var recoverySummary = new DeviceConfigurationScanSummary(1, recoverySession, DateTimeOffset.Parse("2026-10-31T12:00:00Z"), "COMPLETED", profileEndpoint.EndpointId!, renderEndpoint.EndpointId!, [], [], new("NONE", "NOT_CERTIFIED", null, null, ["historical NONE"]), [recoveryBasePath], "NOT_MEASURED", "NOT_CERTIFIED")
    {
        CandidateInventory = recoveryCandidates,
        ChildOperations = recoveryChildren,
        AutomaticStages = [new(AutomaticScanStage.SUSTAINED_VALIDATION, AutomaticScanStageState.COMPLETED, "fixture")]
    };
    var recoverySummaryPath = Path.Combine(recoveryScans, $"scan-{recoverySession}.json");
    File.WriteAllBytes(recoverySummaryPath, JsonSerializer.SerializeToUtf8Bytes(recoverySummary));
    var summaryHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(recoverySummaryPath)));
    var cleanHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(recoveryEvidence, $"sustained-duplex-{cleanOperation}.json"))));
    var preview = OfflineSustainedProfileRecovery.Preview(recoverySession, recoveryScans, recoveryProfiles, recoveryEvidence);
    Check(preview.Recommendation.Preferred?.Period == "CUSTOM:70000:336" && preview.Recommendation.Preferred.Mmcss == "PRO_AUDIO" && preview.Recommendation.Preferred.Confidence == "PROVISIONAL_SINGLE_SUSTAINED_RUN" && preview.Recommendation.Fallback is null && preview.Recommendation.AudioHostEligibility == "NOT_CERTIFIED" && preview.HistoricalMeasurementTimestampUtc < DateTimeOffset.UtcNow, "S5-R1 previews the historical NONE as a recovered 7ms PRO_AUDIO primary with no fallback");
    var originalSummaryBytes = File.ReadAllBytes(recoverySummaryPath);
    var cleanEvidencePath = Path.Combine(recoveryEvidence, $"sustained-duplex-{cleanOperation}.json");
    var originalCleanEvidenceBytes = File.ReadAllBytes(cleanEvidencePath);
    var originalBaseProfileBytes = File.ReadAllBytes(recoveryBasePath);
    File.WriteAllBytes(recoverySummaryPath, JsonSerializer.SerializeToUtf8Bytes(recoverySummary with { CandidateInventory = recoveryCandidates.Select(c => c.Period == "CUSTOM:70000:336" ? c with { Period = "CUSTOM:70000:337" } : c).ToArray() }));
    Throws<InvalidDataException>(() => OfflineSustainedProfileRecovery.Preview(recoverySession, recoveryScans, recoveryProfiles, recoveryEvidence), "S5-R1 rejects a custom period/frame token mismatch");
    File.WriteAllBytes(recoverySummaryPath, JsonSerializer.SerializeToUtf8Bytes(recoverySummary with { CandidateInventory = recoveryCandidates.Select(c => c.Period == "CUSTOM:70000:336" && c.Direction == "CAPTURE" ? c with { Period100ns = 70_001 } : c).ToArray() }));
    Throws<InvalidDataException>(() => OfflineSustainedProfileRecovery.Preview(recoverySession, recoveryScans, recoveryProfiles, recoveryEvidence), "S5-R1 rejects an incorrect historical custom Period100ns");
    File.WriteAllBytes(recoverySummaryPath, JsonSerializer.SerializeToUtf8Bytes(recoverySummary with { CandidateInventory = recoveryCandidates.Concat([recoveryCandidates[0] with { Period100ns = 70_001 }]).ToArray() }));
    Throws<InvalidDataException>(() => OfflineSustainedProfileRecovery.Preview(recoverySession, recoveryScans, recoveryProfiles, recoveryEvidence), "S5-R2.1 rejects contradictory candidate geometry instead of choosing one record");
    File.WriteAllBytes(recoverySummaryPath, originalSummaryBytes);
    var captureGeometryNode = JsonNode.Parse(Encoding.UTF8.GetString(originalCleanEvidenceBytes))!.AsObject();
    captureGeometryNode["report"]!["capture"]!["actualBufferFrames"] = 337;
    File.WriteAllText(cleanEvidencePath, captureGeometryNode.ToJsonString());
    Throws<InvalidDataException>(() => OfflineSustainedProfileRecovery.Preview(recoverySession, recoveryScans, recoveryProfiles, recoveryEvidence), "S5-R1 rejects contradictory Capture geometry");
    var renderGeometryNode = JsonNode.Parse(Encoding.UTF8.GetString(originalCleanEvidenceBytes))!.AsObject();
    renderGeometryNode["report"]!["render"]!["actualBufferFrames"] = 337;
    File.WriteAllText(cleanEvidencePath, renderGeometryNode.ToJsonString());
    Throws<InvalidDataException>(() => OfflineSustainedProfileRecovery.Preview(recoverySession, recoveryScans, recoveryProfiles, recoveryEvidence), "S5-R1 rejects contradictory Render geometry");
    File.WriteAllBytes(cleanEvidencePath, originalCleanEvidenceBytes);
    File.WriteAllBytes(recoverySummaryPath, JsonSerializer.SerializeToUtf8Bytes(recoverySummary with { TimestampUtc = recoverySummary.TimestampUtc.AddSeconds(1) }));
    Throws<InvalidDataException>(() => OfflineSustainedProfileRecovery.Save(preview), "S5-R1 rejects a changed summary after preview");
    File.WriteAllBytes(recoverySummaryPath, originalSummaryBytes);
    var changedReportNode = JsonNode.Parse(Encoding.UTF8.GetString(originalCleanEvidenceBytes))!.AsObject();
    changedReportNode["report"]!["timestampUtc"] = "2026-10-31T10:01:00Z";
    File.WriteAllText(cleanEvidencePath, changedReportNode.ToJsonString());
    Throws<InvalidDataException>(() => OfflineSustainedProfileRecovery.Save(preview), "S5-R1 rejects a changed sustained report after preview");
    File.WriteAllBytes(cleanEvidencePath, originalCleanEvidenceBytes);
    File.WriteAllBytes(recoveryBasePath, DeviceConfigurationProfileJson.SerializeLocal(duplexProfile with { CreatedAtUtc = duplexProfile.CreatedAtUtc.AddSeconds(1) }));
    Throws<InvalidDataException>(() => OfflineSustainedProfileRecovery.Save(preview), "S5-R1 rejects a changed base profile after preview");
    File.WriteAllBytes(recoveryBasePath, originalBaseProfileBytes);
    File.WriteAllBytes(recoverySummaryPath, new byte[8 * 1024 * 1024 + 1]);
    Throws<InvalidDataException>(() => OfflineSustainedProfileRecovery.Preview(recoverySession, recoveryScans, recoveryProfiles, recoveryEvidence), "S5-R1 rejects an oversized historical scan summary before parsing");
    File.WriteAllBytes(recoverySummaryPath, originalSummaryBytes);
    var recoveryVm = new MainViewModel(profileDirectory: recoveryProfiles);
    var recoveryWindow = new MainWindow(recoveryVm, recoveryProfiles);
    recoveryWindow.Show(); recoveryWindow.UpdateLayout();
    Check(recoveryVm.HistoricalSustainedScans.Count == 1 && recoveryVm.HistoricalSustainedScans[0].IsAvailable && recoveryVm.HistoricalSustainedScans[0].HasSustainedEvidence && recoveryVm.HistoricalSustainedScans[0].TimestampLabel.Contains("31.10.2026", StringComparison.Ordinal), "S5-R2 discovers one bounded historical scan with readable date and sustained availability");
    recoveryVm.SelectedHistoricalSustainedScan = recoveryVm.HistoricalSustainedScans[0];
    Check(!recoveryVm.SaveHistoricalSustainedRecoveryCommand.CanExecute(null), "S5-R2 keeps Save disabled until a fresh preview and explicit confirmation");
    recoveryVm.PreviewHistoricalSustainedRecoveryCommand.Execute(null);
    Check(PumpUntil(() => recoveryVm.HistoricalRecoveryPreviewVisible, TimeSpan.FromSeconds(3)) && recoveryVm.HistoricalRecoveryPreferred.StartsWith("CUSTOM:70000:336", StringComparison.Ordinal) && recoveryVm.HistoricalRecoveryFallback == "NONE", "S5-R2 previews the historical NONE as the validated 7ms primary with fallback NONE");
    Check(!recoveryVm.SaveHistoricalSustainedRecoveryCommand.CanExecute(null), "S5-R2 requires explicit confirmation after Preview");
    var uiSummaryBytes = File.ReadAllBytes(recoverySummaryPath);
    File.WriteAllBytes(recoverySummaryPath, JsonSerializer.SerializeToUtf8Bytes(recoverySummary with { TimestampUtc = recoverySummary.TimestampUtc.AddSeconds(1) }));
    recoveryVm.HistoricalRecoveryConfirmationChecked = true; recoveryVm.SaveHistoricalSustainedRecoveryCommand.Execute(null);
    Check(!recoveryVm.HistoricalRecoveryPreviewVisible && recoveryVm.HistoricalRecoveryStatus == KlidanAudioProbe.Gui.Core.Localization.Get("VmHistoricalRecoverySaveFailed") && recoveryVm.HistoricalRecoveryTechnicalDetails.Contains("InvalidDataException", StringComparison.Ordinal), "S5-R2 source changes invalidate the approved preview and keep details out of the operator headline");
    File.WriteAllBytes(recoverySummaryPath, uiSummaryBytes);
    recoveryVm.RefreshHistoricalSustainedScansCommand.Execute(null); recoveryVm.SelectedHistoricalSustainedScan = recoveryVm.HistoricalSustainedScans[0]; recoveryVm.PreviewHistoricalSustainedRecoveryCommand.Execute(null);
    Check(recoveryVm.HistoricalRecoveryPreviewVisible, "S5-R2 requires a new Preview after a changed source");
    recoveryVm.HistoricalRecoveryConfirmationChecked = false;
    recoveryVm.HistoricalRecoveryConfirmationChecked = true;
    recoveryVm.SaveHistoricalSustainedRecoveryCommand.Execute(null);
    recoveryVm.SaveHistoricalSustainedRecoveryCommand.Execute(null);
    Check(PumpUntil(() => !recoveryVm.HistoricalRecoverySaving, TimeSpan.FromSeconds(3)) && DeviceConfigurationProfileStorage.DiscoverLocal(recoveryProfiles).Count(p => p.IsSustained) == 1 && !recoveryVm.HistoricalRecoveryPreviewVisible, "S5-R2 double-click save creates one v4, refreshes state and clears the approved preview");
    recoveryWindow.Close();
    var sixtyReport = RecoveryReport(Guid.NewGuid().ToString("D"), "CUSTOM:70000:336", "PRO_AUDIO", 60, "DUPLEX_CLEAN");
    var sixtyEvidence = new SustainedDuplexEvidence(sixtyReport.GetProperty("operationId").GetString()!, "CUSTOM:70000:336", "PRO_AUDIO", 60, "COMPLETED", "SUSTAINED_CLEAN", "RELEASE_ATTEMPTED", "SAVED_AND_RELOADED", sixtyReport, 336, 336);
    Check(SustainedDuplexSelection.Recommend(preview.Evidence.Append(sixtyEvidence)).Fallback is null, "S5-R1 does not promote a 60-second clean run to fallback");
    var recoveredPath = DeviceConfigurationProfileStorage.DiscoverLocal(recoveryProfiles).Single(p => p.IsSustained && p.Profile?.SustainedSelection?.SourceScanSessionId == recoverySession).Path;
    var recoveredProfile = DeviceConfigurationProfileJson.Parse(File.ReadAllBytes(recoveredPath));
    Check(recoveredProfile.SchemaVersion == 4 && recoveredProfile.ProfileId != duplexProfile.ProfileId && recoveredProfile.CreatedAtUtc > duplexProfile.CreatedAtUtc && recoveredProfile.SustainedSelection?.SourceScanSessionId == recoverySession && recoveredProfile.SustainedSelection.Preferred?.Period == "CUSTOM:70000:336" && recoveredProfile.SustainedSelection.Fallback is null, "S5-R1 writes a new v4 profile with distinct creation identity and preserved historical session/evidence identities");
    Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(recoverySummaryPath))) == summaryHash && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(recoveryEvidence, $"sustained-duplex-{cleanOperation}.json")))) == cleanHash, "S5-R1 recovery does not mutate the historical scan or sustained report files");
    Throws<IOException>(() => OfflineSustainedProfileRecovery.Save(preview), "S5-R1 rejects repeated recovery for an already recovered historical scan");
    var corruptPath = Path.Combine(recoveryEvidence, $"sustained-duplex-{warningOperation}.json");
    File.WriteAllText(corruptPath, "{");
    Throws<InvalidDataException>(() => OfflineSustainedProfileRecovery.Preview(recoverySession, recoveryScans, recoveryProfiles, recoveryEvidence), "S5-R1 rejects missing or corrupt original sustained reports");
}
finally { if (Directory.Exists(recoveryRoot)) Directory.Delete(recoveryRoot, true); }
var savedProfilesRoot = Path.Combine(Path.GetTempPath(), $"klidan-saved-profiles-ui-{Guid.NewGuid():N}");
Directory.CreateDirectory(savedProfilesRoot);
try
{
    Check(DeviceConfigurationProfileStorage.DiscoverLocal(savedProfilesRoot).Count == 0, "S5-B2 empty local profile directory remains empty");
    File.WriteAllBytes(Path.Combine(savedProfilesRoot, "profile-v4.json"), DeviceConfigurationProfileJson.SerializeLocal(sustainedProfile));
    File.WriteAllBytes(Path.Combine(savedProfilesRoot, "profile-v3.json"), DeviceConfigurationProfileJson.SerializeLocal(duplexProfile));
    File.WriteAllBytes(Path.Combine(savedProfilesRoot, "profile-sanitized.json"), DeviceConfigurationProfileJson.SerializeSanitized(sustainedProfile));
    File.WriteAllText(Path.Combine(savedProfilesRoot, "profile-invalid.json"), "{\"schemaVersion\":4}");
    var savedEntries = DeviceConfigurationProfileStorage.DiscoverLocal(savedProfilesRoot);
    Check(savedEntries.Count == 4 && savedEntries.Count(e => e.IsAvailable) == 3 && savedEntries.Any(e => !e.IsAvailable && e.Reason.Contains("повреждён", StringComparison.OrdinalIgnoreCase)) && savedEntries.Any(e => e.Profile?.SchemaVersion == 3) && savedEntries.Any(e => e.IsSustained), "S5-B2 discovers valid v4, legacy v3, sanitized and invalid profiles without trusting malformed data");
    var initialWorker = new GuiStateWorkerClient([]) { AdvertiseWindowsDiscovery = true, WindowsDiscovery = bindingDiscovery };
    var initialVm = new MainViewModel(worker: initialWorker, profileDirectory: savedProfilesRoot);
    var initialWindow = new MainWindow(initialVm, savedProfilesRoot);
    initialWindow.UpdateLayout();
    Check(initialVm.WorkerState.State == WorkerConnectionState.Disconnected && initialVm.CheckSavedProfileBindingCommand.CanExecute(null) && ((System.Windows.Controls.Button)initialWindow.FindName("CheckSavedProfileBindingButton")).IsEnabled, "S5-B2.2 exposes one enabled connection check from the pristine disconnected Worker state");
    initialVm.CheckSavedProfileBindingCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => !initialVm.SavedProfileBindingChecking && initialVm.HasSavedProfileBindingAssessment, TimeSpan.FromSeconds(3)) && initialWorker.ConnectCalls == 1 && initialVm.SavedProfileOperatorStatus == KlidanAudioProbe.Gui.Core.Localization.Get("VmBindingExactFound") && initialWorker.RealInitializeCalls == 0 && initialWorker.RealStreamCalls == 0 && initialWorker.DuplexCalls == 0, "S5-B2.2 one explicit check connects once, discovers and evaluates without audio operations");
    initialWorker.SetState(new(WorkerConnectionState.Disconnected, "Unexpected Worker loss."));
    Check(initialVm.SavedProfileOperatorStatus != KlidanAudioProbe.Gui.Core.Localization.Get("VmBindingExactFound") && initialVm.SavedProfileOperatorStatus.Contains(KlidanAudioProbe.Gui.Core.Localization.Get("VmWorkerDisconnected"), StringComparison.Ordinal) && !initialVm.CheckSavedProfileBindingCommand.CanExecute(null), "S5-B2.2 Worker loss immediately clears visible success and disables automatic restart");
    Check(initialWorker.ConnectCalls == 1 && initialWorker.WindowsDiscoveryCalls == 1, "S5-B2.2 preserves the no-restart policy after an unexpected Worker disconnect");
    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
    Check(((System.Windows.Controls.TextBlock)initialWindow.FindName("SavedProfileOperatorStatusText")).Text == initialVm.SavedProfileOperatorStatus && !((System.Windows.Controls.TextBlock)initialWindow.FindName("SavedProfileOperatorStatusText")).Text.Contains("Пара устройств найдена", StringComparison.Ordinal), "S5-B2.2 actual WPF status binding removes stale current-success text");
    initialWindow.Close();
    var queuedContext = new QueuedSynchronizationContext();
    var previousBindingContext = SynchronizationContext.Current;
    SynchronizationContext.SetSynchronizationContext(queuedContext);
    var sequenceWorker = new GuiStateWorkerClient([])
    {
        AdvertiseWindowsDiscovery = true,
        WindowsDiscovery = bindingDiscovery,
        ConnectStates = [
            new(WorkerConnectionState.Starting, "Starting."),
            new(WorkerConnectionState.Connecting, "Connecting.", 321),
            new(WorkerConnectionState.Handshaking, "Handshaking.", 321),
            new(WorkerConnectionState.ConnectedFake, "Connected.", 321)]
    };
    var sequenceGate = new TaskCompletionSource<WindowsEndpointDiscoveryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
    var sequenceWorkflow = new GuiAutomaticScanWorkflow(bindingDiscovery) { RefreshGate = sequenceGate };
    var sequenceVm = new MainViewModel(worker: sequenceWorker, automaticScanWorkflow: sequenceWorkflow, profileDirectory: savedProfilesRoot);
    sequenceVm.AutomaticScanEngine = "V1_COMPATIBLE";
    SynchronizationContext.SetSynchronizationContext(previousBindingContext);
    var sequenceWindow = new MainWindow(sequenceVm, savedProfilesRoot);
    sequenceWindow.UpdateLayout();
    SynchronizationContext.SetSynchronizationContext(queuedContext);
    sequenceVm.CheckSavedProfileBindingCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => sequenceWorkflow.RefreshCalls == 1, TimeSpan.FromSeconds(2)), "S5-B2.3 starts the queued initial connection sequence");
    queuedContext.Drain();
    Check(sequenceVm.WorkerState.State == WorkerConnectionState.ConnectedFake && sequenceVm.CheckSavedProfileBindingCommand.CanExecute(null) == false, "S5-B2.3 queued Starting/Connecting/Handshaking callbacks do not invalidate the active check");
    sequenceGate.TrySetResult(bindingDiscovery);
    Check(SpinWait.SpinUntil(() => { queuedContext.Drain(); return !sequenceVm.SavedProfileBindingChecking; }, TimeSpan.FromSeconds(3)) && sequenceVm.SavedProfileOperatorStatus == KlidanAudioProbe.Gui.Core.Localization.Get("VmBindingExactFound") && sequenceWorker.ConnectCalls == 1 && sequenceWorker.RealInitializeCalls == 0 && sequenceWorker.RealStreamCalls == 0 && sequenceWorker.DuplexCalls == 0, "S5-B2.3 one explicit check survives the real queued connection states without audio operations");
    queuedContext.Drain();
    sequenceWindow.DataContext = null;
    sequenceWindow.DataContext = sequenceVm;
    sequenceWindow.UpdateLayout();
    var sequenceStatusText = ((System.Windows.Controls.TextBlock)sequenceWindow.FindName("SavedProfileOperatorStatusText")).Text;
    var sequenceStatusBlock = (System.Windows.Controls.TextBlock)sequenceWindow.FindName("SavedProfileOperatorStatusText");
    BindingOperations.SetBinding(sequenceStatusBlock, System.Windows.Controls.TextBlock.TextProperty, new Binding(nameof(MainViewModel.SavedProfileOperatorStatus)) { Source = sequenceVm });
    BindingOperations.GetBindingExpression(sequenceStatusBlock, System.Windows.Controls.TextBlock.TextProperty)?.UpdateTarget();
    sequenceStatusText = sequenceStatusBlock.Text;
    var sequenceButton = (System.Windows.Controls.Button)sequenceWindow.FindName("CheckSavedProfileBindingButton");
    Check(sequenceStatusText == KlidanAudioProbe.Gui.Core.Localization.Get("VmBindingExactFound") && sequenceButton.IsEnabled && sequenceVm.CheckSavedProfileBindingCommand.CanExecute(null), $"S5-B2.3 WPF status and command readiness remain visible after queued connection delivery [status={sequenceStatusText}; enabled={sequenceButton.IsEnabled}; canExecute={sequenceVm.CheckSavedProfileBindingCommand.CanExecute(null)}]");
    sequenceWindow.Close();
    SynchronizationContext.SetSynchronizationContext(previousBindingContext);
    var bindingWorker = new GuiStateWorkerClient([]) { AdvertiseWindowsDiscovery = true, WindowsDiscovery = bindingDiscovery };
    bindingWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for saved profile binding."));
    var bindingVm = new MainViewModel(worker: bindingWorker, automaticScanWorkflow: new GuiAutomaticScanWorkflow(bindingDiscovery), profileDirectory: savedProfilesRoot);
    bindingVm.AutomaticScanEngine = "V1_COMPATIBLE";
    Check(bindingVm.SavedDeviceProfiles.Count == 4 && bindingVm.CheckSavedProfileBindingCommand.CanExecute(null), "S5-B2 starts with one operator connection action and no JSON selection requirement");
    bindingVm.SelectedSavedDeviceProfile = bindingVm.SavedDeviceProfiles.Single(e => e.Profile?.SchemaVersion == 4 && e.IsSustained && e.Profile.Privacy == "LOCAL_MACHINE");
    Check(bindingVm.SavedProfileHistoricalSummary.Contains("Primary:", StringComparison.Ordinal) && bindingVm.SavedProfileHistoricalSummary.Contains("Fallback:", StringComparison.Ordinal) && bindingVm.SavedProfileHistoricalSummary.Contains("NOT_CERTIFIED", StringComparison.Ordinal) && bindingVm.CheckSavedProfileBindingCommand.CanExecute(null), "S5-B2 displays v4 historical Primary/Fallback and enables only the read-only binding check");
    var savedProfileWindow = new MainWindow(bindingVm, savedProfilesRoot);
    ((System.Windows.Controls.Expander)savedProfileWindow.FindName("SavedProfileDiagnosticsExpander")).IsExpanded = true;
    savedProfileWindow.UpdateLayout();
    Check(savedProfileWindow.FindName("SavedDeviceProfileSelector") is System.Windows.Controls.ComboBox && bindingVm.SavedDeviceProfiles.Count == 4 && savedProfileWindow.FindName("CheckSavedProfileBindingButton") is System.Windows.Controls.Button checkButton && checkButton.IsEnabled && savedProfileWindow.FindName("RefreshSavedProfilesButton") is System.Windows.Controls.Button refreshButton && refreshButton.IsEnabled, "S5-B2 actual WPF controls expose profile binding and command CanExecute state");
    savedProfileWindow.Close();
    bindingVm.CheckSavedProfileBindingCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => !bindingVm.SavedProfileBindingChecking && bindingVm.HasSavedProfileBindingAssessment, TimeSpan.FromSeconds(3)) && bindingVm.SavedProfileBindingStatus == KlidanAudioProbe.Gui.Core.Localization.Get("VmBindingExactFound") && bindingWorker.WindowsDiscoveryCalls == 0, "S5-B2 checks a fresh workflow discovery without invoking audio operations");
    var selectedPath = bindingVm.SelectedSavedDeviceProfile!.Path;
    bindingWorker.SetState(new(WorkerConnectionState.Disconnected, "Worker disconnected during binding UI test."));
    Check(bindingVm.SavedProfileBindingStatus == KlidanAudioProbe.Gui.Core.Localization.Get("VmBindingWorkerDisconnected") && bindingVm.SavedProfileBindingTechnicalDetails.Contains("WORKER_DISCONNECTED", StringComparison.Ordinal) && bindingVm.SavedProfileOperatorStatus != KlidanAudioProbe.Gui.Core.Localization.Get("VmBindingExactFound"), "S5-B2.2 invalidates the current binding result and visible success on Worker disconnect");
    bindingVm.SelectedSavedDeviceProfile = bindingVm.SavedDeviceProfiles.Single(e => e.Profile?.Privacy == "SANITIZED_PORTABLE");
    Check(bindingVm.SavedProfileBindingStatus != KlidanAudioProbe.Gui.Core.Localization.Get("VmBindingExactFound") && bindingVm.SavedProfileOperatorStatus != KlidanAudioProbe.Gui.Core.Localization.Get("VmBindingExactFound"), "S5-B2.2 profile selection immediately invalidates the displayed result");
    bindingVm.SelectedSavedDeviceProfile = bindingVm.SavedDeviceProfiles.Single(e => e.Path == selectedPath);
    File.Delete(selectedPath);
    bindingVm.RefreshSavedProfilesCommand.Execute(null);
    Check(bindingVm.SelectedSavedDeviceProfile is null && bindingVm.SavedDeviceProfiles.Any(e => !e.IsAvailable), "S5-B2 handles a profile disappearing during refresh without retaining trusted selection");
    var lateWorker = new GuiStateWorkerClient([]) { AdvertiseWindowsDiscovery = true, WindowsDiscovery = bindingDiscovery };
    lateWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for late discovery fixture."));
    var lateGate = new TaskCompletionSource<WindowsEndpointDiscoveryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
    var lateWorkflow = new GuiAutomaticScanWorkflow(bindingDiscovery) { RefreshGate = lateGate };
    var lateVm = new MainViewModel(worker: lateWorker, automaticScanWorkflow: lateWorkflow, profileDirectory: savedProfilesRoot);
    lateVm.AutomaticScanEngine = "V1_COMPATIBLE";
    lateVm.CheckSavedProfileBindingCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => lateWorkflow.RefreshCalls == 1, TimeSpan.FromSeconds(2)), "S5-B2.2 starts the asynchronous discovery race fixture");
    lateWorker.SetState(new(WorkerConnectionState.Disconnected, "Worker replaced during discovery."));
    lateGate.TrySetResult(bindingDiscovery);
    Check(SpinWait.SpinUntil(() => !lateVm.SavedProfileBindingChecking, TimeSpan.FromSeconds(3)) && lateVm.WorkerState.State == WorkerConnectionState.Disconnected && lateVm.SavedProfileOperatorStatus != KlidanAudioProbe.Gui.Core.Localization.Get("VmBindingExactFound"), $"S5-B2.2 rejects a late discovery response after Worker invalidation [state={lateVm.WorkerState.State}; status={lateVm.SavedProfileOperatorStatus}]");
}
finally { if (Directory.Exists(savedProfilesRoot)) Directory.Delete(savedProfilesRoot, true); }
var bundleSecurityRoot = Path.Combine(Path.GetTempPath(), $"klidan-bundle-security-{Guid.NewGuid():N}");
Directory.CreateDirectory(bundleSecurityRoot);
try
{
    var unsafeBundle = Path.Combine(bundleSecurityRoot, "unsafe.zip");
    using (var archive = System.IO.Compression.ZipFile.Open(unsafeBundle, System.IO.Compression.ZipArchiveMode.Create))
    using (var stream = archive.CreateEntry("../outside.json").Open()) stream.WriteByte(1);
    var unsafeResult = LaboratoryEvidenceBundleService.Verify(unsafeBundle);
    Check(!unsafeResult.IsValid && unsafeResult.Errors.Count > 0, "L2-A offline verifier rejects unsafe ZIP paths without Worker or source directories");
    var boundedBundle = Path.Combine(bundleSecurityRoot, "bounded.zip");
    using (var archive = System.IO.Compression.ZipFile.Open(boundedBundle, System.IO.Compression.ZipArchiveMode.Create))
    using (var stream = archive.CreateEntry("manifest.json").Open()) stream.Write(new byte[1024]);
    var boundedResult = LaboratoryEvidenceBundleService.Verify(boundedBundle);
    Check(!boundedResult.IsValid, "L2-A offline verifier rejects malformed or incomplete manifests");
}
finally { if (Directory.Exists(bundleSecurityRoot)) Directory.Delete(bundleSecurityRoot, true); }
Check(mainWindowXaml.Contains("{loc:Loc ExportResults}", StringComparison.Ordinal) && mainWindowXaml.Contains("AutomaticScanExportStatusText", StringComparison.Ordinal) && mainWindowXaml.Contains("LOCAL_FULL_EVIDENCE", StringComparison.Ordinal) == false, "L2-B result screen exposes one explicit export action with a separate status and technical-details area");
Check(mainWindowXaml.Contains("AutomaticScanAutoShortComparison", StringComparison.Ordinal) && mainWindowXaml.Contains("AutomaticScanAutoShortCandidates", StringComparison.Ordinal) && mainWindowXaml.Contains("VmAutoCandidateDetails", StringComparison.Ordinal) && mainWindowXaml.Contains("VmAutoFramesColumn", StringComparison.Ordinal) && mainWindowXaml.Contains("VmAutoReasonColumn", StringComparison.Ordinal), "AUTO_SHORT result screen exposes candidate comparison for support, period, buffer frames, and exclusion reason");
Check(!new MainViewModel().CanExportAutomaticScan && !new MainViewModel().ExportAutomaticScanCommand.CanExecute(null), "L2-B export is disabled without an explicitly completed persisted result and native export interaction");
var bundleUiRoot = Path.Combine(Path.GetTempPath(), $"klidan-bundle-ui-{Guid.NewGuid():N}");
var bundleUiScans = Path.Combine(bundleUiRoot, "Scans");
var bundleUiProfiles = Path.Combine(bundleUiRoot, "Profiles");
var bundleUiSustained = Path.Combine(bundleUiRoot, "SustainedDuplex");
Directory.CreateDirectory(bundleUiScans); Directory.CreateDirectory(bundleUiProfiles); Directory.CreateDirectory(bundleUiSustained);
try
{
    var bundleSession = "74000000-0000-4000-8000-000000000001";
    var bundleProfile = DeviceConfigurationProfileJson.Validate(sustainedProfile with
    {
        ProfileId = Guid.NewGuid(),
        SustainedSelection = sustainedProfile.SustainedSelection! with { SourceScanSessionId = bundleSession }
    });
    var bundleProfilePath = DeviceConfigurationProfileStorage.SaveLocal(bundleProfile, bundleUiProfiles);
    var bundleReports = new[]
    {
        (Evidence: sustainedPrimaryEvidence, Report: realReport, Classification: "SUSTAINED_CLEAN"),
        (Evidence: sustainedFallbackEvidence, Report: default300, Classification: "SUSTAINED_CLEAN")
    };
    foreach (var item in bundleReports)
    {
        var path = Path.Combine(bundleUiSustained, $"sustained-duplex-{item.Evidence.OperationId}.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, reportType = "wasapi-sustained-duplex", lifecycle = "COMPLETED", classification = item.Classification, operationId = item.Evidence.OperationId, period = item.Evidence.Period, report = item.Report.Report }));
    }
    var bundleSummary = new DeviceConfigurationScanSummary(1, bundleSession, DateTimeOffset.UtcNow, "COMPLETED", profileEndpoint.EndpointId!, renderEndpoint.EndpointId,
        [], [], new("NONE", "NOT_CERTIFIED", null, null, []), [bundleProfilePath], "NOT_MEASURED", "NOT_CERTIFIED")
    {
        AutomaticStages = [new(AutomaticScanStage.SAVE, AutomaticScanStageState.COMPLETED, "SAVED_AND_RELOADED")],
        ChildOperations = bundleReports.Select(item => new AutomaticScanChildOperation("SUSTAINED_VALIDATION", item.Evidence.OperationId.ToString("D"), item.Evidence.ReportId.ToString("D"), "COMPLETED", "REAL_WASAPI_SUSTAINED_DUPLEX", "RELEASE_ATTEMPTED", "fixture", Path.Combine(bundleUiRoot, "untrusted-path.json"), $"{item.Evidence.Period}/{item.Evidence.Mmcss}/{item.Evidence.DurationSeconds}s", null, "PAIR")).ToArray()
    };
    var bundleSummaryPath = Path.Combine(bundleUiScans, $"scan-{bundleSession}.json");
    File.WriteAllBytes(bundleSummaryPath, JsonSerializer.SerializeToUtf8Bytes(bundleSummary, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    _ = DeviceConfigurationScanStorage.LoadSummary(bundleSummaryPath);
    var bundleDirectories = new LaboratoryEvidenceBundleDirectories(bundleUiScans, bundleUiProfiles, Path.Combine(bundleUiRoot, "PeriodDiscovery"), Path.Combine(bundleUiRoot, "CustomDuplex"), Path.Combine(bundleUiRoot, "Repeatability"), bundleUiSustained);
    var bundleResult = new ScanRunResult(bundleSession, ScanRunLifecycle.Completed, new(profileEndpoint, renderEndpoint, ScanPairIdentity.OperatorSelectedPair), [], [], [], new("NONE", "NOT_CERTIFIED", null, null, []), "completed fixture", false, "NOT_CERTIFIED", new("SAVED_AND_RELOADED", bundleSummaryPath, [bundleProfilePath]));
    var interaction = new FakeBundleExportInteraction();
    var bundleVm = new MainViewModel(worker: new GuiStateWorkerClient([]), bundleExportInteraction: interaction, bundleDirectoriesFactory: _ => bundleDirectories);
    typeof(MainViewModel).GetField("_automaticScanResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bundleVm, bundleResult);
    typeof(MainViewModel).GetMethod("NotifyAutomaticScanResultBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bundleVm, null);
    var bundleWindow = new MainWindow(bundleVm, bundleUiProfiles); bundleWindow.UpdateLayout();
    var exportButton = (System.Windows.Controls.Button)bundleWindow.FindName("ExportAutomaticScanButton");
    var openV1ReportButton = (Button)bundleWindow.FindName("OpenAutomaticScanLocationButton");
    Check(openV1ReportButton.IsEnabled && bundleVm.OpenAutomaticScanLocationCommand.CanExecute(null), "V1 persisted summary enables the actual WPF report-folder button");
    Check(bundleVm.CanExportAutomaticScan && exportButton.IsEnabled == bundleVm.ExportAutomaticScanCommand.CanExecute(null), "L2-B.1 completed v4 scan enables the real WPF export button from Command.CanExecute");

    var primaryReportPath = Path.Combine(bundleUiSustained, $"sustained-duplex-{sustainedPrimaryEvidence.OperationId:D}.json");
    File.Delete(primaryReportPath); var pickerCallsBeforeMissing = interaction.PickCalls; interaction.NextDestination = Path.Combine(bundleUiRoot, "missing-report.zip"); bundleVm.ExportAutomaticScanCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => !bundleVm.AutomaticScanExportRunning, TimeSpan.FromSeconds(3)) && interaction.PickCalls == pickerCallsBeforeMissing && bundleVm.AutomaticScanExportStatus.Length > 0, "L2-B.1 missing required evidence fails before the destination dialog");
    File.WriteAllBytes(primaryReportPath, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, reportType = "wasapi-sustained-duplex", lifecycle = "COMPLETED", classification = "SUSTAINED_CLEAN", operationId = sustainedPrimaryEvidence.OperationId, period = sustainedPrimaryEvidence.Period, report = realReport.Report }));

    var mismatchedSessionVm = new MainViewModel(bundleExportInteraction: interaction, bundleDirectoriesFactory: _ => bundleDirectories);
    typeof(MainViewModel).GetField("_automaticScanResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mismatchedSessionVm, bundleResult with { SessionId = "74000000-0000-4000-8000-000000000099" });
    typeof(MainViewModel).GetMethod("NotifyAutomaticScanResultBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mismatchedSessionVm, null);
    var pickerCallsBeforeSession = interaction.PickCalls; mismatchedSessionVm.ExportAutomaticScanCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => !mismatchedSessionVm.AutomaticScanExportRunning, TimeSpan.FromSeconds(3)) && interaction.PickCalls == pickerCallsBeforeSession && mismatchedSessionVm.AutomaticScanExportStatus.Length > 0, "L2-B.1 different scan session cannot inherit the previous export preparation");

    var cancelledDestination = Path.Combine(bundleUiRoot, "cancelled.zip");
    interaction.NextDestination = null; bundleVm.ExportAutomaticScanCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => !bundleVm.AutomaticScanExportRunning, TimeSpan.FromSeconds(3)) && !File.Exists(cancelledDestination), "L2-B.1 destination cancellation creates no ZIP");
    interaction.NextDestination = cancelledDestination; interaction.ConfirmResult = false; bundleVm.ExportAutomaticScanCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => !bundleVm.AutomaticScanExportRunning, TimeSpan.FromSeconds(3)) && !File.Exists(cancelledDestination), "L2-B.1 privacy confirmation cancellation creates no ZIP");

    var exportPath = Path.Combine(bundleUiRoot, "completed.zip"); interaction.NextDestination = exportPath; interaction.ConfirmResult = true; bundleVm.ExportAutomaticScanCommand.Execute(null); bundleVm.ExportAutomaticScanCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => !bundleVm.AutomaticScanExportRunning, TimeSpan.FromSeconds(5)) && File.Exists(exportPath) && LaboratoryEvidenceBundleService.Verify(exportPath).IsValid && interaction.PickCalls == 3, "L2-B.1 confirmed export creates one verified ZIP and repeated clicks do not overlap");
    var originalZipHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(exportPath)));
    interaction.NextDestination = exportPath; interaction.ConfirmResult = true; bundleVm.ExportAutomaticScanCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => !bundleVm.AutomaticScanExportRunning, TimeSpan.FromSeconds(3)) && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(exportPath))) == originalZipHash && bundleVm.AutomaticScanExportStatus.Length > 0, "L2-B.1 existing destination is never overwritten and false success is not shown");

    var mutationPath = Path.Combine(bundleUiRoot, "mutation.zip"); interaction.MutateOnPick = () => File.AppendAllText(bundleSummaryPath, " "); interaction.NextDestination = mutationPath; interaction.ConfirmResult = true; bundleVm.ExportAutomaticScanCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => !bundleVm.AutomaticScanExportRunning, TimeSpan.FromSeconds(3)) && !File.Exists(mutationPath) && bundleVm.AutomaticScanExportStatus.Length > 0, "L2-B.1 source mutation after Prepare rejects export before publication");
    interaction.MutateOnPick = null; bundleWindow.Close();
    var v2EvidencePath = Path.Combine(bundleUiRoot, "v2-evidence.json");
    var v2EvidenceBytes = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"lifecycle\":\"COMPLETED\"}");
    File.WriteAllBytes(v2EvidencePath, v2EvidenceBytes);
    var v2Route = new WorkerScanV2Route("MONO_TO_STEREO", new(48000, 1, "PCM16", 0, [0]), new(48000, 2, "PCM16", 0, [0, 1]));
    var v2ExportDestination = Path.Combine(bundleUiRoot, "v2-export.json"); interaction.NextDestination = v2ExportDestination; interaction.ConfirmResult = true;
    var v2ExportVm = new MainViewModel(bundleExportInteraction: interaction);
    var v2ExportResult = new V2AutomaticScanResult(Guid.NewGuid().ToString("D"), "UNKNOWN", "capture-id", "render-id", "route-fingerprint", v2Route, "MINIMUM", [], [], v2EvidencePath, "fixture");
    typeof(MainViewModel).GetField("_automaticScanV2Result", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(v2ExportVm, v2ExportResult);
    typeof(MainViewModel).GetMethod("NotifyAutomaticScanResultBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(v2ExportVm, null);
    v2ExportVm.ExportAutomaticScanCommand.Execute(null);
    Check(SpinWait.SpinUntil(() => !v2ExportVm.AutomaticScanExportRunning, TimeSpan.FromSeconds(3)) && File.Exists(v2ExportDestination) && File.ReadAllBytes(v2ExportDestination).SequenceEqual(v2EvidenceBytes), "V2 evidence export preserves the report bytes and permits diagnostic export of non-CLEAN results");

    var incompleteVm = new MainViewModel(bundleExportInteraction: new FakeBundleExportInteraction(), bundleDirectoriesFactory: _ => bundleDirectories);
    typeof(MainViewModel).GetField("_automaticScanResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(incompleteVm, bundleResult with { Lifecycle = ScanRunLifecycle.Unknown });
    typeof(MainViewModel).GetMethod("NotifyAutomaticScanResultBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(incompleteVm, null);
    Check(!incompleteVm.CanExportAutomaticScan && !incompleteVm.ExportAutomaticScanCommand.CanExecute(null), "L2-B.1 UNKNOWN or incomplete scan cannot start a complete export");

    var generationWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = physicalPairFixture, AdvertiseWindowsDiscovery = true, AdvertiseV2 = true, AdvertisePassiveFormatQuery = true, DiscoveryGeneration = 11,
        RealQueryFactory = (id, direction, counts, _) => Task.FromResult(PassiveMatrix(id, direction, direction == "CAPTURE" ? 8 : 2)) };
    var generationVm = new MainViewModel(worker: generationWorker);
    generationWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for V4 generation invalidation fixture."));
    generationVm.DiscoverWindowsEndpointsCommand.Execute(null);
    Check(PumpUntil(() => generationVm.PhysicalDeviceGroups.Count == 1, TimeSpan.FromSeconds(2)), "V4 generation invalidation fixture discovers exact endpoints");
    generationVm.AutomaticScanEngine = "V2_ADAPTIVE";
    generationVm.SelectedPhysicalDeviceGroup = generationVm.PhysicalDeviceGroups.Single();
    generationVm.AutomaticScanV2CaptureChannels = 8; generationVm.AutomaticScanV2CaptureChannel1 = 8;
    generationVm.AutomaticScanV2RenderChannels = 2;
    var generationWindow = new MainWindow(generationVm) { Width = 1280, Height = 800, ShowActivated = false };
    generationWindow.Show(); generationWindow.UpdateLayout();
    ((Button)generationWindow.FindName("V4PreparePlanButton")).Command!.Execute(null);
    var generationPrepared = PumpUntil(() => generationVm.V4ConsentPending, TimeSpan.FromSeconds(3));
    generationWindow.Close();
    Check(generationPrepared, $"V4 generation invalidation fixture prepares passive consent [can={generationVm.PrepareV4CustomPlanCommand.CanExecute(null)}, pair={generationVm.HasCurrentDuplexPair}, state={generationVm.WorkerState.State}, caps={string.Join(',', generationWorker.Capabilities)}, capture={generationVm.SelectedDuplexCaptureEndpoint?.EndpointId}, render={generationVm.SelectedDuplexRenderEndpoint?.EndpointId}, status={generationVm.V4PlanStatus}]");
    generationVm.V4ConsentChecked = true; generationVm.AcceptV4CustomPlanCommand.Execute(null);
    Check(generationVm.V4ReceiptCreated, "V4 generation invalidation fixture accepts the exact prepared plan");
    generationWorker.DiscoveryGeneration++;
    generationVm.DiscoverWindowsEndpointsCommand.Execute(null);
    Check(!generationVm.V4PlanReady && !generationVm.V4ConsentPending && generationVm.V4ConsentReceipt is null,
        "Worker discovery generation refresh immediately invalidates prepared v4 plan and consent receipt");

    var previousDiagnosticBuildSha = Environment.GetEnvironmentVariable("KLIDAN_BUILD_SHA");
    Environment.SetEnvironmentVariable("KLIDAN_BUILD_SHA", new string('d', 40));
    var pointDiscovery = physicalPairFixture with { Endpoints = physicalPairFixture.Endpoints.Select(e => e with { FriendlyName = e.Direction == "CAPTURE" ? "Bloody Capture" : "Bloody Render" }).ToArray() };
    var pointWorker = new GuiStateWorkerClient([]) { WindowsDiscovery = pointDiscovery, AdvertiseWindowsDiscovery = true, AdvertiseV2 = true, AdvertisePassiveFormatQuery = true, DiscoveryGeneration = 21,
        RealQueryFactory = (id, direction, counts, _) => Task.FromResult(PassiveMatrix(id, direction, counts!.Single())) };
    var pointWorkflow = new PointDiagnosticWorkflowFake();
    var pointVm = new MainViewModel(worker: pointWorker, automaticScanWorkflow: new GuiAutomaticScanWorkflow(pointDiscovery), v4AutomaticSearchWorkflow: pointWorkflow);
    pointWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Connected for one-candidate point diagnostic."));
    pointVm.DiscoverWindowsEndpointsCommand.Execute(null);
    Check(PumpUntil(() => pointVm.PhysicalDeviceGroups.Count == 1, TimeSpan.FromSeconds(2)), "point diagnostic discovers the exact Bloody Capture/Render fixture");
    pointVm.SelectedPhysicalDeviceGroup = pointVm.PhysicalDeviceGroups.Single();
    pointVm.AutomaticScanV2CaptureChannels = 2; pointVm.AutomaticScanV2CaptureChannel1 = 1;
    pointVm.AutomaticScanV2RenderChannels = 2; pointVm.AutomaticScanV2RenderChannel1 = 1; pointVm.AutomaticScanV2RenderChannel2 = 2;
    var pointWindow = new MainWindow(pointVm) { Width = 1280, Height = 800, ShowActivated = false };
    pointWindow.Show(); pointWindow.UpdateLayout();
    var pointPrepareButton = (Button)pointWindow.FindName("V4PrepareDiagnosticButton");
    var pointMmcssSelector = (ComboBox)pointWindow.FindName("V4DiagnosticMmcssSelector");
    var pointConsentBox = (CheckBox)pointWindow.FindName("V4DiagnosticConsentCheckBox");
    var pointConsentPanel = (Border)pointWindow.FindName("V4DiagnosticConsentPanel");
    var pointRunPanel = (StackPanel)pointWindow.FindName("V4DiagnosticRunPanel");
    var pointAcceptButton = (Button)pointWindow.FindName("V4AcceptDiagnosticButton");
    var pointStartButton = (Button)pointWindow.FindName("V4StartDiagnosticButton");
    var pointProgressText = (TextBlock)pointWindow.FindName("V4DiagnosticProgressText");
    var pointResultsText = (TextBlock)pointWindow.FindName("V4DiagnosticResultsText");
    Check(pointPrepareButton.IsEnabled && !pointAcceptButton.IsEnabled && !pointStartButton.IsEnabled && !string.IsNullOrWhiteSpace(pointConsentBox.Content?.ToString()), "diagnostic visual tree has a dedicated period, consent and start path disabled until its one-candidate plan exists");
    Check(pointVm.V4SearchPlanUiVisible && pointWindow.FindName("V4PreparePlanButton") is Button { Visibility: Visibility.Visible }, "point diagnostics remain alongside the full V4 search controls");
    pointPrepareButton.Command!.Execute(null);
    Check(PumpUntil(() => pointVm.V4DiagnosticConsentPending, TimeSpan.FromSeconds(3)), "5 ms point plan completes passive bounds before consent");
    pointWindow.UpdateLayout();
    Check(pointConsentPanel.Visibility == Visibility.Visible && pointRunPanel.Visibility != Visibility.Visible, "prepared diagnostic exposes consent panel before issuing a receipt");
    var pointPlanField = typeof(MainViewModel).GetField("_v4PreparedPlan", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var point5Plan = (WorkerScanV2ContractV4)pointPlanField.GetValue(pointVm)!;
    Check(point5Plan.Candidates.Count == 1 && point5Plan.Candidates[0].CaptureFrames == 240 && point5Plan.Candidates[0].RenderFrames == 240 && point5Plan.RepeatabilityRuns == 0 && point5Plan.MaximumOperations == 3 && point5Plan.MaximumActiveDuplexSeconds == 5 && point5Plan.SessionLifetimeSeconds == 139 && pointVm.V4ConsentSummary.Contains("240", StringComparison.Ordinal), "5 ms GUI consent fingerprint covers only C240/R240, R=0 and the canonical 3-op/5-second/139-second scope");
    Check(!pointVm.AcceptV4CustomPlanCommand.CanExecute(null) && !pointVm.StartV4ScanCommand.CanExecute(null) && !pointVm.StartV4DiagnosticCommand.CanExecute(null), "diagnostic plan cannot use full-search consent or start without diagnostic consent");
    pointVm.V4DiagnosticConsentChecked = true;
    Check(pointVm.AcceptV4DiagnosticPlanCommand.CanExecute(null), "separate diagnostic consent enables only the point-diagnostic authorization command");
    pointVm.AcceptV4DiagnosticPlanCommand.Execute(null);
    pointWindow.UpdateLayout();
    var point5Consent = pointVm.V4ConsentReceipt;
    Check(point5Consent is { Accepted: true } && point5Consent.PlanFingerprint == pointVm.V4PlanFingerprint && pointConsentPanel.Visibility != Visibility.Visible && pointRunPanel.Visibility == Visibility.Visible && pointStartButton.Visibility == Visibility.Visible && pointStartButton.IsEnabled, "accept hides consent and reveals an active start button bound to the exact 5 ms receipt");
    pointStartButton.Command!.Execute(null);
    Check(PumpUntil(() => !pointVm.V4ScanRunning && pointVm.V4ResultState == "DIAGNOSTIC_ONLY", TimeSpan.FromSeconds(3)), "5 ms diagnostic reaches terminal result");
    pointWindow.UpdateLayout();
    Check(pointWorkflow.Runs.Count == 1 && pointWorkflow.Runs[0].Plan.Candidates.Count == 1 && pointVm.V4ResultSummary.Contains("DIAGNOSTIC_ONLY", StringComparison.Ordinal) && pointVm.V4ResultSummary.Contains("998", StringComparison.Ordinal) && pointVm.V4ResultSummary.Contains("240f", StringComparison.Ordinal) && pointVm.V4ConsentReceipt is null && pointRunPanel.Visibility == Visibility.Visible && pointStartButton.Visibility != Visibility.Visible && pointResultsText.Visibility == Visibility.Visible && pointProgressText.Visibility == Visibility.Visible, "5 ms launch consumes its receipt while progress and result stay visible and no second start is offered");
    pointVm.V4DiagnosticPeriodMilliseconds = 10;
    pointMmcssSelector.SelectedValue = "PRO_AUDIO";
    pointWindow.UpdateLayout();
    Check(pointVm.V4DiagnosticMmcssMode == "PRO_AUDIO" && Equals(pointMmcssSelector.SelectedValue, "PRO_AUDIO"), $"diagnostic MMCSS selector updates the plan source [vm={pointVm.V4DiagnosticMmcssMode}; selected={pointMmcssSelector.SelectedValue}; busy={pointVm.AutomaticScanBusy}]");
    Check(!pointVm.V4PlanReady && !pointVm.V4DiagnosticConsentPending && pointVm.V4ConsentReceipt is null, "changing 5 ms to 10 ms invalidates the previous plan and its one-use receipt");
    pointPrepareButton.Command!.Execute(null);
    Check(PumpUntil(() => pointVm.V4DiagnosticConsentPending, TimeSpan.FromSeconds(3)), "10 ms point plan obtains new passive bounds before consent");
    pointWindow.UpdateLayout();
    Check(pointConsentPanel.Visibility == Visibility.Visible && pointStartButton.Visibility != Visibility.Visible, "10 ms requires a new visible consent after 5 ms completion");
    var point10Plan = (WorkerScanV2ContractV4)pointPlanField.GetValue(pointVm)!;
    var point10Fingerprint = WorkerScanV2ContractV4Fingerprint.Compute("local-windows-id", "local-render-id", point10Plan, 21);
    var point5Fingerprint = WorkerScanV2ContractV4Fingerprint.Compute("local-windows-id", "local-render-id", point5Plan, 21);
    Check(point10Plan.Candidates.Count == 1 && point10Plan.Candidates[0].CaptureFrames == 480 && point10Plan.Candidates[0].RenderFrames == 480 && point10Plan.MmcssModes.SequenceEqual(["PRO_AUDIO"]) && point10Plan.MaximumOperations == 3 && point10Plan.MaximumActiveDuplexSeconds == 5 && pointVm.V4ConsentSummary.Contains("MMCSS PRO_AUDIO", StringComparison.Ordinal) && point10Fingerprint != point5Fingerprint,
        $"10 ms GUI diagnostic binds only C480/R480 and PRO_AUDIO into a fresh distinct fingerprint [frames={point10Plan.Candidates.Single().CaptureFrames}/{point10Plan.Candidates.Single().RenderFrames}; mode={string.Join(',', point10Plan.MmcssModes)}; ops={point10Plan.MaximumOperations}; seconds={point10Plan.MaximumActiveDuplexSeconds}; summary={pointVm.V4ConsentSummary}; fp10={point10Fingerprint}; fp5={point5Fingerprint}]");
    pointVm.CancelV4CustomPlanCommand.Execute(null);
    pointWindow.UpdateLayout();
    Check(!pointVm.V4DiagnosticConsentPending && pointVm.V4ConsentReceipt is null && !pointVm.StartV4DiagnosticCommand.CanExecute(null) && pointConsentPanel.Visibility != Visibility.Visible && pointRunPanel.Visibility != Visibility.Visible, "cancelling the 10 ms consent clears both panels and leaves diagnostic dispatch disabled");
    pointPrepareButton.Command!.Execute(null);
    Check(PumpUntil(() => pointVm.V4DiagnosticConsentPending, TimeSpan.FromSeconds(3)), "cancelled consent can be prepared again only through a fresh plan");
    pointVm.V4DiagnosticConsentChecked = true; pointVm.AcceptV4DiagnosticPlanCommand.Execute(null);
    pointWindow.UpdateLayout();
    Check(pointVm.V4ConsentReceipt is { Accepted: true } receipt10 && receipt10.ConfirmationId != point5Consent!.ConfirmationId, "10 ms requires a fresh, non-replayable consent receipt");
    pointWorker.DiscoveryGeneration = 22;
    pointWorker.SetState(new(WorkerConnectionState.ConnectedFake, "Discovery generation changed."));
    pointWindow.UpdateLayout();
    Check(pointVm.V4ConsentReceipt is null && !pointVm.StartV4DiagnosticCommand.CanExecute(null) && pointRunPanel.Visibility != Visibility.Visible, "discovery-generation change invalidates the accepted point receipt before dispatch");
    pointVm.AutomaticScanV2Mode = "STEREO_TO_STEREO";
    Check(!pointVm.V4DiagnosticConsentPending && pointVm.V4ConsentReceipt is null && !pointVm.StartV4DiagnosticCommand.CanExecute(null), "route change invalidates the diagnostic plan and receipt before dispatch");
    pointVm.AutomaticScanV2Mode = "MONO_TO_STEREO";
    pointWindow.Close();
    Environment.SetEnvironmentVariable("KLIDAN_BUILD_SHA", previousDiagnosticBuildSha);
}
finally { if (Directory.Exists(bundleUiRoot)) Directory.Delete(bundleUiRoot, true); }
Console.WriteLine($"All GUI Core checks passed ({checks}).");
}

sealed class FakeBundleExportInteraction : ILaboratoryEvidenceBundleExportInteraction
{
    public string? NextDestination { get; set; }
    public bool ConfirmResult { get; set; }
    public int PickCalls { get; private set; }
    public Action? MutateOnPick { get; set; }
    public Task<string?> PickDestinationAsync(string suggestedFileName) { PickCalls++; MutateOnPick?.Invoke(); return Task.FromResult(NextDestination); }
    public Task<bool> ConfirmAsync(string message) => Task.FromResult(ConfirmResult);
}

sealed class EmptyProbeClient : IProbeClient
{
    public IReadOnlyList<EndpointDto> ListEndpoints(SessionMode mode) => [];
    public IReadOnlyList<FormatDto> QueryFormatMatrix() => [];
    public IReadOnlyList<ReportDto> ListReports() => [];
}

sealed class QueuedSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
    public void Drain()
    {
        while (_queue.Count > 0)
        {
            var item = _queue.Dequeue();
            item.Callback(item.State);
        }
    }
}

sealed class GuiStateWorkerClient(IReadOnlyList<WorkerEndpointDto> endpoints) : IWorkerClient
{
    public bool AdvertiseWindowsDiscovery { get; set; }
    public bool AdvertiseRealFormatProbe { get; set; }
    public bool AdvertiseV2 { get; set; }
    public bool AdvertisePassiveFormatQuery { get; set; }
    public bool AdvertiseCustomPeriod { get; set; }
    public WindowsEndpointDiscoveryDto? WindowsDiscovery { get; set; }
    public int WindowsDiscoveryCalls { get; private set; }
    public int RealQueryCalls { get; private set; }
    public int RealQueryCompletions { get; private set; }
    public int RealInitializeCalls { get; private set; }
    public int RealStreamCalls { get; private set; }
    public RealFormatMatrixDto? RealMatrixResponse { get; set; }
    public Func<string, string, IReadOnlyList<int>?, CancellationToken, Task<RealFormatMatrixDto>>? RealQueryFactory { get; set; }
    public ulong DiscoveryGeneration { get; set; }
    public RealFormatMatrixDto? RealInitializeResponse { get; set; }
    public Func<string, int, string, string, RealFormatMatrixDto>? RealInitializeFactory { get; set; }
    public Func<string, string, string, CancellationToken, Task<RealStreamValidationDto>>? RealStreamHandler { get; set; }
    public Func<string, string, string, string, CancellationToken, Task<DuplexValidationDto>>? DuplexHandler { get; set; }
    public int DuplexCalls { get; private set; }
    public int ConnectCalls { get; private set; }
    public IReadOnlyList<WorkerStateDto>? ConnectStates { get; set; }
    public DuplexConsentDto? LastDuplexConsent { get; private set; }
    public List<Guid> DuplexConsentIds { get; } = [];
    public Action? StreamCancelRequested { get; set; }
    public TaskCompletionSource? QueryGate { get; set; }
    public string? LastRealEndpointId { get; private set; }
    public string? LastRealDirection { get; private set; }
    public RealFormatConsentDto? LastRealConsent { get; private set; }
    public IReadOnlyList<string> Capabilities => (AdvertiseWindowsDiscovery ? new[] { "ListWindowsEndpoints" } : []).Concat(AdvertiseRealFormatProbe ? ["RealExclusiveFormatQuery", "RealExclusiveFormatInitialize", "RealExclusiveStreamValidation", "RealExclusiveDuplexValidation", "ScanSessionAuthorizationV1"] : []).Concat(AdvertisePassiveFormatQuery ? ["RealExclusiveFormatQuery"] : []).Concat(AdvertiseV2 ? ["RealExclusiveDuplexValidationV2"] : []).Concat(AdvertiseCustomPeriod ? ["RealExclusiveCustomPeriodInitialize"] : []).ToArray();
    public WorkerStateDto State { get; private set; } = new(WorkerConnectionState.Disconnected, "Not started.");
    ulong IWorkerClient.DiscoveryGeneration => DiscoveryGeneration;
    public WorkerSessionSettingsDto? LastStartSettings { get; private set; }
    public ConsentReceiptDto? LastConsent { get; private set; }
    public event Action<WorkerStateDto>? StateChanged;
    public event Action<WorkerSessionAcceptedDto>? SessionAccepted { add { } remove { } }
    public event Action<WorkerEventDto>? EventReceived { add { } remove { } }
    public event Action<WorkerTransportFaultDto>? TransportFault { add { } remove { } }
    public void SetState(WorkerStateDto state) { State = state; StateChanged?.Invoke(state); }
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectCalls++;
        foreach (var state in ConnectStates ?? [new(WorkerConnectionState.ConnectedFake, "Connected (fake).")]) SetState(state);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<WorkerEndpointDto>> ListEndpointsAsync(CancellationToken cancellationToken = default) => Task.FromResult(endpoints);
    public Task<WindowsEndpointDiscoveryDto> ListWindowsEndpointsAsync(CancellationToken cancellationToken = default)
    {
        WindowsDiscoveryCalls++;
        return WindowsDiscovery is null ? Task.FromException<WindowsEndpointDiscoveryDto>(new NotSupportedException()) : Task.FromResult(WindowsDiscovery);
    }
    public async Task<RealFormatMatrixDto> QueryFormatMatrixAsync(string endpointId, string direction, IReadOnlyList<int>? channelCounts = null, CancellationToken cancellationToken = default)
    {
        RealQueryCalls++; LastRealEndpointId = endpointId; LastRealDirection = direction;
        if (QueryGate is not null) await QueryGate.Task.WaitAsync(cancellationToken);
        if (RealQueryFactory is not null) { var result = await RealQueryFactory(endpointId, direction, channelCounts, cancellationToken); RealQueryCompletions++; return result; }
        return RealMatrixResponse ?? throw new NotSupportedException();
    }
    public Task<RealFormatMatrixDto> ValidateFormatMatrixAsync(string endpointId, string direction, int sampleRate, int channels, string encoding, string period, RealFormatConsentDto consent, CancellationToken cancellationToken = default)
    {
        RealInitializeCalls++; LastRealEndpointId = endpointId; LastRealDirection = direction; LastRealConsent = consent;
        return RealInitializeFactory is not null ? Task.FromResult(RealInitializeFactory(endpointId, channels, encoding, period) with { Direction = direction }) : RealInitializeResponse is null ? Task.FromException<RealFormatMatrixDto>(new NotSupportedException()) : Task.FromResult(RealInitializeResponse);
    }
    public Task<RealStreamValidationDto> ValidateStreamAsync(string operationId, string endpointId, string direction, string period, int durationSeconds, string mmcss, RealStreamConsentDto consent, CancellationToken cancellationToken = default, string? testScenario = null)
    {
        RealStreamCalls++;
        if (RealStreamHandler is not null) return RealStreamHandler(operationId, endpointId, period, cancellationToken);
        using var report = JsonDocument.Parse($"{{\"schemaVersion\":1,\"reportType\":\"wasapi-stream-validation\",\"reportId\":\"{Guid.NewGuid():D}\",\"timestampUtc\":\"2026-10-31T00:00:00Z\",\"provenance\":\"TEST_FAKE_STREAM_BACKEND\",\"probeVersion\":\"fixture\",\"direction\":\"{direction}\",\"format\":{{\"sampleRateHz\":48000,\"encoding\":\"PCM16\",\"channels\":2}},\"period\":\"{period}\",\"durationSeconds\":{durationSeconds},\"activationHresult\":\"0x00000000\",\"devicePeriodHresult\":\"0x00000000\",\"formatSupported\":true,\"initializeSucceeded\":true,\"firstInitializeHresult\":\"0x00000000\",\"finalInitializeHresult\":\"0x00000000\",\"requestedPeriod100ns\":30000,\"actualBufferFrames\":144,\"actualBufferPeriod100ns\":30000,\"actualBufferFramesHresult\":\"0x00000000\",\"setEventHandleHresult\":\"0x00000000\",\"eventCreateHresult\":\"0x00000000\",\"eventReady\":true,\"startSucceeded\":true,\"startHresult\":\"0x00000000\",\"events\":1667,\"packets\":1667,\"framesProcessed\":240000,\"expectedFrames\":240000,\"frameCoveragePercent\":100,\"elapsedMilliseconds\":5000,\"durationReached\":true,\"discontinuities\":0,\"timestampErrors\":0,\"unavailableCaptureBuffers\":0,\"bufferServiceFaults\":0,\"eventWaitTimeouts\":0,\"watchdogTimedOut\":false,\"cancelled\":false,\"deviceInvalidated\":false,\"streamQuality\":\"CLEAN\",\"stopSucceeded\":true,\"stopHresult\":\"0x00000000\",\"cleanup\":\"STOP_SUCCEEDED;EVENT_RELEASE_ATTEMPTED;CLIENT_RELEASE_ATTEMPTED\",\"physicalPlayback\":\"NOT_MEASURED\",\"physicalRouting\":\"UNKNOWN\"}}");
        return Task.FromResult(new RealStreamValidationDto(operationId, endpointId, direction, report.RootElement.Clone()));
    }
    public Task CancelStreamValidationAsync(string operationId, CancellationToken cancellationToken = default) { StreamCancelRequested?.Invoke(); return Task.CompletedTask; }
    public Task<DuplexValidationDto> ValidateDuplexAsync(string operationId, string captureEndpointId, string renderEndpointId, string period, int durationSeconds, string mmcss, DuplexConsentDto consent, CancellationToken cancellationToken = default, string? testScenario = null)
    {
        DuplexCalls++; LastDuplexConsent = consent; DuplexConsentIds.Add(consent.ConfirmationId);
        return DuplexHandler is not null ? DuplexHandler(operationId, captureEndpointId, renderEndpointId, period, cancellationToken) : Task.FromException<DuplexValidationDto>(new NotSupportedException());
    }
    public Task CancelDuplexValidationAsync(string operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkerSessionAcceptedDto> StartSessionAsync(WorkerSessionSettingsDto settings, ConsentReceiptDto consent, CancellationToken cancellationToken = default)
    {
        LastStartSettings = settings;
        LastConsent = consent;
        return Task.FromResult(new WorkerSessionAcceptedDto(Guid.NewGuid(), Guid.NewGuid(), settings));
    }
    public Task StopSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ShutdownAsync(CancellationToken cancellationToken = default) { SetState(new(WorkerConnectionState.Disconnected, "Worker exited.")); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class AvailabilityDuplexWorkflow(WindowsEndpointDiscoveryDto discovery) : IDuplexRepeatabilityWorkflow, ICustomDuplexRepeatabilityWorkflow
{
    public Task<WindowsEndpointDiscoveryDto> RefreshEndpointsAsync(CancellationToken cancellationToken) => Task.FromResult(discovery);
    public DuplexRepeatabilityPreparation Prepare(WindowsEndpointDiscoveryDto current, WindowsEndpointDto capture, WindowsEndpointDto render)
    {
        var plan = ScanPlan.Build(new(capture, render, ScanPairIdentity.OperatorSelectedPair), new(true, false, false, false, true));
        var workerPlan = new WorkerScanPlan([new(capture.EndpointId!, "CAPTURE"), new(render.EndpointId!, "RENDER")], ["ValidateDuplex"], [new(48_000, "PCM16", 2, ["MINIMUM", "DEFAULT"], ["OFF"])], 600, 5, 6);
        return new(current, plan, workerPlan, DuplexRepeatabilityPreparation.Fingerprint(current), 1);
    }
    public Task<DuplexRepeatabilityResult> RunApprovedAsync(DuplexRepeatabilityPreparation preparation, Guid confirmationId, Action<ScanProgressEvent>? progress = null, CancellationToken cancellationToken = default) => Task.FromException<DuplexRepeatabilityResult>(new NotSupportedException());
    public DuplexRepeatabilityPreparation PrepareCustom(WindowsEndpointDiscoveryDto current, WindowsEndpointDto capture, WindowsEndpointDto render, PeriodDiscoverySeriesResult series, int frames) => Prepare(current, capture, render) with { Periods = Enumerable.Repeat("CUSTOM:50000:240", 5).ToArray() };
    public Task<DuplexRepeatabilityResult> RunCustomApprovedAsync(DuplexRepeatabilityPreparation preparation, Guid confirmationId, Action<ScanProgressEvent>? progress = null, CancellationToken cancellationToken = default) => Task.FromException<DuplexRepeatabilityResult>(new NotSupportedException());
}

sealed class AvailabilityPeriodWorkflow : IExclusivePeriodDiscoveryWorkflow
{
    public Task<PeriodDiscoveryPreparation> PrepareAsync(WindowsEndpointDiscoveryDto discovery, WindowsEndpointDto capture, WindowsEndpointDto render, CancellationToken cancellationToken = default) => Task.FromException<PeriodDiscoveryPreparation>(new NotSupportedException());
    public Task<PeriodDiscoverySeriesResult> RunApprovedAsync(PeriodDiscoveryPreparation preparation, Guid confirmationId, Action<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromException<PeriodDiscoverySeriesResult>(new NotSupportedException());
}

sealed class AvailabilityCustomPeriodWorkflow : ICustomPeriodDuplexWorkflow
{
    public CustomPeriodDuplexPreparation Prepare(WindowsEndpointDiscoveryDto discovery, WindowsEndpointDto capture, WindowsEndpointDto render, PeriodDiscoverySeriesResult series, int frames) => throw new NotSupportedException();
    public Task<CustomPeriodDuplexResult> RunApprovedAsync(CustomPeriodDuplexPreparation preparation, Guid confirmationId, CancellationToken cancellationToken = default) => Task.FromException<CustomPeriodDuplexResult>(new NotSupportedException());
}

sealed class GuiAutomaticScanWorkflow(WindowsEndpointDiscoveryDto discovery) : IAutomaticScanWorkflow
{
    public WindowsEndpointDiscoveryDto Discovery { get; set; } = discovery;
    public AutomaticScanSelection? LastSelection { get; private set; }
    public int RefreshCalls { get; private set; }
    public int RunCalls { get; private set; }
    public int RunV2Calls { get; private set; }
    public string? LastV2CaptureId { get; private set; }
    public string? LastV2RenderId { get; private set; }
    public WorkerScanV2Route? LastV2Route { get; private set; }
    public string? LastV2Period { get; private set; }
    public string? LastV2QaMode { get; private set; }
    public Guid LastV2ConfirmationId { get; private set; }
    public TaskCompletionSource<WindowsEndpointDiscoveryDto>? RefreshGate { get; init; }
    public bool HoldRunUntilReleased { get; init; }
    public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Guid LastConfirmationId { get; private set; }
    public ScanPlan? PreparedPlan { get; private set; }

    public async Task<WindowsEndpointDiscoveryDto> RefreshEndpointsAsync(CancellationToken cancellationToken)
    {
        RefreshCalls++;
        return RefreshGate is null ? Discovery : await RefreshGate.Task.WaitAsync(cancellationToken);
    }

    public AutomaticScanPreparation Prepare(WindowsEndpointDiscoveryDto fresh, AutomaticScanSelection selection)
    {
        LastSelection = selection;
        var target = selection.ExplicitPair
            ? new ScanTarget(
                fresh.Endpoints.Single(e => e.EndpointId == selection.CaptureEndpointId && e.Direction == "CAPTURE" && e.DeviceState == "ACTIVE"),
                fresh.Endpoints.Single(e => e.EndpointId == selection.RenderEndpointId && e.Direction == "RENDER" && e.DeviceState == "ACTIVE"),
                ScanPairIdentity.OperatorSelectedPair)
            : fresh.Endpoints.Single(e => e.EndpointId == selection.EndpointId && e.DeviceState == "ACTIVE") is { } endpoint
                ? endpoint.Direction == "CAPTURE" ? new ScanTarget(endpoint, null) : new ScanTarget(null, endpoint)
                : throw new InvalidOperationException("Fixture endpoint missing.");
        var capabilities = new ScanCapabilities(true, true, true, true, true);
        var plan = ScanPlan.Build(target, capabilities);
        PreparedPlan = plan;
        var workerPlan = new WorkerScanPlan(target.Capture is { } capture && target.Render is { } render
                ? [new(capture.EndpointId!, capture.Direction), new(render.EndpointId!, render.Direction)]
                : [new((target.Capture ?? target.Render)!.EndpointId!, (target.Capture ?? target.Render)!.Direction)], ["QueryFormatMatrix", "ValidateFormatMatrix", "ValidateStream"],
            [new(48000, "PCM16", 2, ["MINIMUM", "DEFAULT"], ["OFF"])], 600, 5, 24);
        return new(fresh, plan, workerPlan, 10, selection.AllowUnconfirmedPair) { StagedPlan = AutomaticScanPlanner.Create(fresh, plan, workerPlan) };
    }

    public async Task<ScanRunResult> RunApprovedAsync(AutomaticScanPreparation preparation, Guid confirmationId,
        Action<ScanProgressEvent>? progress, CancellationToken cancellationToken)
    {
        RunCalls++;
        LastConfirmationId = confirmationId;
        var operation = new ScanOperation(ScanStage.Query, preparation.Plan.Target.Capture?.EndpointId ?? preparation.Plan.Target.Render?.EndpointId,
            preparation.Plan.Target.Capture is not null ? "CAPTURE" : "RENDER", OperationId: Guid.NewGuid().ToString("D"));
        var events = new List<ScanProgressEvent>();
        void Emit(ScanOperationLifecycle state, string action)
        {
            var evt = new ScanProgressEvent(events.Count + 1, DateTimeOffset.UtcNow, ScanStage.Query, state, action, operation.EndpointId, operation.OperationId);
            events.Add(evt); progress?.Invoke(evt);
        }
        Emit(ScanOperationLifecycle.Pending, "Проверочный адаптер подготовил план; реальная операция отсутствует.");
        Emit(ScanOperationLifecycle.Running, "Ожидается тестовое подтверждение завершения.");
        RunStarted.TrySetResult();
        if (HoldRunUntilReleased)
        {
            try { await ReleaseRun.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { await ReleaseRun.Task; }
        }
        var cancelled = cancellationToken.IsCancellationRequested;
        Emit(ScanOperationLifecycle.Skipped, cancelled ? "Тестовый адаптер получил отмену; аудиоопераций не было." : "Тестовый адаптер завершился без аудиоопераций.");
        var recommendation = DeviceConfigurationEvaluator.Recommend([]);
        var operations = new[] { new ScanOperationResult(operation, ScanOperationLifecycle.Skipped, "Injected UI fixture; no hardware operation was performed.", "NOT_ATTEMPTED", null) };
        return new(Guid.NewGuid().ToString("D"), cancelled ? ScanRunLifecycle.Cancelled : ScanRunLifecycle.Incomplete,
            preparation.Plan.Target, operations, events, [], recommendation,
            "Injected UI fixture · hardware operations intentionally omitted.", false, "NOT_CERTIFIED");
    }

    public Task<V2AutomaticScanResult> RunV2Async(string captureEndpointId, string renderEndpointId, WorkerScanV2Route route,
        string period, string qaMode, Guid confirmationId, Action<V2AutomaticScanProgress>? progress, CancellationToken cancellationToken)
    {
        RunV2Calls++;
        LastV2CaptureId = captureEndpointId; LastV2RenderId = renderEndpointId; LastV2Route = route;
        LastV2Period = period; LastV2QaMode = qaMode; LastV2ConfirmationId = confirmationId;
        RunStarted.TrySetResult();
        progress?.Invoke(new("SHORT_DUPLEX", "RUNNING", "Synthetic GUI workflow", Guid.NewGuid().ToString("D"), 1));
        IReadOnlyList<V2AutomaticScanOperation> operations = [new("SHORT_DUPLEX", Guid.NewGuid().ToString("D"), null, null, "COMPLETED", period, 1, "CONFIRMED", "TEST_FAKE_GUI_WORKFLOW", null)];
        IReadOnlyList<AutomaticScanStageResult> stages = [new(AutomaticScanStage.SHORT_DUPLEX, AutomaticScanStageState.COMPLETED, "Synthetic GUI workflow; no audio device operation.")];
        return Task.FromResult(new V2AutomaticScanResult(Guid.NewGuid().ToString("D"), "COMPLETED", captureEndpointId,
            renderEndpointId, WorkerScanPlanV2Fingerprint.Route(captureEndpointId, renderEndpointId, route), route, period,
            operations, stages, null, null)
            { CaptureFriendlyName = "Fixture Capture", RenderFriendlyName = "Fixture Render", QaMode = qaMode, RepeatabilityStatus = qaMode == "FULL" ? "COMPLETED" : "NOT_REQUESTED", SustainedStatus = qaMode == "FULL" ? "COMPLETED" : "NOT_REQUESTED" });
    }
}

sealed class FragmentReadStream(byte[] content, int maximumRead) : MemoryStream(content)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, maximumRead));
}

sealed class PointDiagnosticWorkflowFake : IV4AutomaticSearchWorkflow
{
    public List<WorkerScanV2V4PreparedPlan> Runs { get; } = [];
    public Task<V4AutomaticSearchResult> RunAsync(WorkerScanV2V4PreparedPlan prepared, WindowsEndpointDto capture, WindowsEndpointDto render,
        string evidenceRoot, string profileDirectory, bool runRepeatability, IProgress<V4AutomaticSearchProgress>? progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Point diagnostic fixture must not dispatch full search.");

    public Task<V4AutomaticSearchResult> RunDiagnosticAsync(WorkerScanV2V4PreparedPlan prepared, WindowsEndpointDto capture, WindowsEndpointDto render,
        string evidenceRoot, IProgress<V4AutomaticSearchProgress>? progress, CancellationToken cancellationToken)
    {
        Runs.Add(prepared);
        var candidate = prepared.Plan.Candidates.Single();
        var frames = candidate.CaptureFrames;
        var captureEvents = candidate.RequestedDuration100ns == 50_000 ? 998 : 500;
        var renderEvents = candidate.RequestedDuration100ns == 50_000 ? 500 : 500;
        using var report = JsonDocument.Parse($"{{\"report\":{{\"duplex\":{{\"status\":\"DUPLEX_CLEAN\"}},\"capture\":{{\"events\":{captureEvents},\"eventIntervalMedianMilliseconds\":5,\"eventIntervalP95Milliseconds\":5.1,\"eventIntervalP99Milliseconds\":5.2,\"firstEventDelayMilliseconds\":2,\"eventTimeoutCount\":0,\"eventFailureCount\":0,\"framesPerEventMean\":{frames},\"frameCoveragePercent\":100,\"discontinuities\":0}},\"render\":{{\"events\":{renderEvents},\"eventIntervalMedianMilliseconds\":10,\"eventIntervalP95Milliseconds\":10.1,\"eventIntervalP99Milliseconds\":10.2,\"firstEventDelayMilliseconds\":2,\"eventTimeoutCount\":0,\"eventFailureCount\":0,\"framesPerEventMean\":{frames},\"frameCoveragePercent\":100,\"discontinuities\":0}}}}}}");
        var row = new V2CandidateMatrixEntry(candidate, "INITIALIZED", "INITIALIZED", "CLEAN", "RELEASE_ATTEMPTED", "RELEASE_ATTEMPTED",
            ["DIAGNOSTIC_ONLY", "CLEAN"], false, null, "COMPLETE", "NOT_RUN", "TEST_FAKE_WASAPI_V4_DUPLEX")
        {
            ActualCaptureFrames = candidate.CaptureFrames, ActualCapturePeriod100ns = candidate.CapturePeriod100ns,
            ActualRenderFrames = candidate.RenderFrames, ActualRenderPeriod100ns = candidate.RenderPeriod100ns,
            DuplexEvidence = report.RootElement.Clone(), NativeDuplexDisposition = "CLEAN", EvaluatorReasonCode = "CLEAN"
        };
        var evidence = new V2CandidateMatrixEvidence(1, prepared.Consent.PlanFingerprint,
            WorkerScanPlanV2Fingerprint.Route(prepared.CaptureEndpointId, prepared.RenderEndpointId, prepared.Plan.Route), "COMPLETED",
            prepared.Plan.CaptureBounds, prepared.Plan.RenderBounds, prepared.Plan.Candidates, [row], "DIAGNOSTIC_ONLY", "TEST_FAKE_WASAPI_V4_DUPLEX")
        {
            EvidenceVersion = 4, DiagnosticOnly = true, BuildSha = Environment.GetEnvironmentVariable("KLIDAN_BUILD_SHA")!, DiagnosticTimestampUtc = DateTimeOffset.UtcNow,
            DiscoveryGeneration = prepared.Consent.DiscoveryGeneration, SelectedCandidateIndex = 0, GlobalMinimumProven = false,
            UsedOperations = 3, MaximumOperations = 3, UsedActiveDuplexSeconds = 5, MaximumActiveDuplexSeconds = 5,
            StopReason = "DIAGNOSTIC_CANDIDATE_COMPLETED", RepeatabilityPending = false, RepeatabilityRequested = 0,
            RepeatabilityCompleted = 0, SessionCleanup = "CLOSE_CONFIRMED"
        };
        V2CandidateMatrixEvidenceJson.Validate(evidence);
        return Task.FromResult(new V4AutomaticSearchResult(evidence, Path.Combine(evidenceRoot, $"diagnostic-{frames}.json")));
    }
}

sealed class DelegateScanExecutionAdapter(
    ScanCapabilities Capabilities,
    Func<ScanOperation, ScanPlan, ScanSessionAuthorization, CancellationToken, Task<ScanAdapterResult>> execute,
    Func<ScanOperation, ScanPlan, CancellationToken, Task<ScanAdapterResult>>? cancel = null) : IScanExecutionAdapter
{
    public ScanExecutionMode Mode => ScanExecutionMode.DeterministicFake;
    ScanCapabilities IScanExecutionAdapter.Capabilities => Capabilities;
    public Task<ScanAdapterResult> ExecuteAsync(ScanOperation operation, ScanPlan plan, ScanSessionAuthorization authorization, CancellationToken cancellationToken) => execute(operation, plan, authorization, cancellationToken);
    public Task<ScanAdapterResult> CancelAsync(ScanOperation operation, ScanPlan plan, CancellationToken cancellationToken) => cancel is null
        ? Task.FromResult(new ScanAdapterResult(ScanOperationLifecycle.Unknown, Message: "No cancellation handler.", CleanupStatus: "UNKNOWN", CanSafelyContinue: false))
        : cancel(operation, plan, cancellationToken);
}

sealed class RepeatabilityFixtureAdapter(Func<ScanOperation, int, DuplexValidationDto> reportFactory, int unknownCleanupAt = -1, int exceptionAt = -1, bool finishFailure = false, bool finishNeverCompletes = false) : IDuplexRepeatabilityAdapter
{
    private int _index;
    public ScanExecutionMode Mode => ScanExecutionMode.RealWorker;
    public bool IsBoundTo(ScanPlan plan, ScanSessionAuthorization authorization) => true;
    public Task<ScanAdapterResult> ExecuteAsync(ScanOperation operation, ScanPlan plan, ScanSessionAuthorization authorization, CancellationToken cancellationToken)
    {
        var index = _index++;
        if (index == exceptionAt) return Task.FromException<ScanAdapterResult>(new TimeoutException("native operation timeout"));
        var validation = reportFactory(operation, index);
        return Task.FromResult(new ScanAdapterResult(index == unknownCleanupAt ? ScanOperationLifecycle.Unknown : ScanOperationLifecycle.Completed,
            new(Duplex: validation), "fixture Duplex", index == unknownCleanupAt ? "CAPTURE:UNKNOWN;RENDER:UNKNOWN" : "CAPTURE:RELEASE_ATTEMPTED;RENDER:RELEASE_ATTEMPTED", CanSafelyContinue: index != unknownCleanupAt));
    }
    public Task<ScanAdapterResult> CancelAsync(ScanOperation operation, ScanPlan plan, CancellationToken cancellationToken) => Task.FromResult(new ScanAdapterResult(ScanOperationLifecycle.Unknown, Message: "fixture cancel", CleanupStatus: "UNKNOWN", CanSafelyContinue: false));
    public Task FinishSessionAsync(bool cancel, CancellationToken cancellationToken) => finishFailure ? Task.FromException(new InvalidOperationException("fixture cleanup failure")) : finishNeverCompletes ? new TaskCompletionSource().Task : Task.CompletedTask;
}
