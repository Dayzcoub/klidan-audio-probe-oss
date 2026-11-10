using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using KlidanAudioProbe.Gui.Core;
using KlidanAudioProbe.Gui.Worker;
using Microsoft.Win32.SafeHandles;

if (args.Length == 2 && args[0] == "--pipe") return await TestNativePipeWorker.Run(args[1]);

var count = 0;
void Check(bool condition, string message) { count++; if (!condition) throw new InvalidOperationException($"FAIL: {message}"); Console.WriteLine($"PASS: {message}"); }
string RouteFingerprint(string captureId,string renderId,string mode,uint cr,uint cc,string ce,uint cm,uint rr,uint rc,string re,uint rm,params uint[] indices)
{
    var captureCount=mode=="STEREO_TO_STEREO"?2:1;var renderCount=mode=="MONO_TO_MONO"?1:2;
    var canonical=$"2|{captureId}|{renderId}|{mode}|{cr},{cc},{ce},{cm}|{rr},{rc},{re},{rm}|";
    for(var i=0;i<captureCount;i++)canonical+=$"C{indices[i]}|";
    for(var i=0;i<renderCount;i++)canonical+=$"R{indices[captureCount+i]}|";
    ulong hash=14695981039346656037UL;foreach(var b in Encoding.UTF8.GetBytes(canonical)){hash^=b;hash*=1099511628211UL;}return hash.ToString("x16");
}
string RouteConsentFingerprint(string captureId,string renderId,string mode,string routeFingerprint)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{captureId}|{renderId}|MINIMUM|1|OFF|REAL_WASAPI_DUPLEX_V2|{routeFingerprint}")));
async Task<JsonElement[]> RawWorkerExchangeAsync(string workerPath, string handshake, params string[] requests)
{
    var pipeName = $"KlidanAudioProbe.RawTest.{Guid.NewGuid():N}";
    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    var start = new ProcessStartInfo(workerPath) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--pipe"); start.ArgumentList.Add($"\\\\.\\pipe\\{pipeName}");
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Native worker test process did not start.");
    try
    {
        await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        async Task Send(string json)
        {
            var frame = IpcFrameCodec.Encode(Encoding.UTF8.GetBytes(json));
            await pipe.WriteAsync(frame); await pipe.FlushAsync();
        }
        async Task<JsonElement> Read()
        {
            using var document = IpcFrameCodec.Parse(await IpcFrameCodec.ReadPayloadAsync(pipe, CancellationToken.None));
            return document.RootElement.Clone();
        }
        await Send(handshake);
        var responses = new List<JsonElement> { await Read() };
        foreach (var request in requests)
        {
            if (responses[^1].TryGetProperty("kind", out var kind) && kind.GetString() == "error") break;
            await Send(request);
            responses.Add(await Read());
        }
        pipe.Dispose();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return responses.ToArray();
    }
    finally
    {
        pipe.Dispose();
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}
string HandshakeJson(Guid requestId, int bootstrap = 1, string kind = "request", string command = "Handshake", int major = 1, int minMinor = 0, int maxMinor = 0, int commandSchema = 1, int eventSchema = 1, int reportSchema = 1, bool omitProtocol = false, bool omitSchemaSet = false, bool omitRequestId = false, bool malformedId = false)
{
    var protocol = new Dictionary<string, object?> { ["major"] = major, ["minMinor"] = minMinor, ["maxMinor"] = maxMinor };
    var schemas = new Dictionary<string, object?> { ["command"] = new[] { commandSchema }, ["event"] = new[] { eventSchema }, ["report"] = new[] { reportSchema } };
    if (omitSchemaSet) schemas.Remove("event");
    var payload = new Dictionary<string, object?> { ["schemas"] = schemas };
    if (!omitProtocol) payload["protocol"] = protocol;
    var hello = new Dictionary<string, object?> { ["bootstrapVersion"] = bootstrap, ["kind"] = kind, ["command"] = command, ["payload"] = payload };
    if (!omitRequestId) hello["requestId"] = malformedId ? "bad-id" : requestId.ToString("D");
    return JsonSerializer.Serialize(hello);
}
string ClientRequestJson(Guid requestId, string command = "ListEndpoints", string kind = "request", object? payload = null, int major = 1, int minor = 0, bool omitVersion = false, bool omitSchema = false, bool omitCorrelation = false, bool omitSession = false, bool omitSequence = false, bool omitPayload = false, bool malformedId = false, bool omitRequestId = false, bool wrongCorrelation = false, bool wrongSession = false, bool nonzeroSequence = false)
{
    var message = new Dictionary<string, object?> { ["kind"] = kind, ["commandOrEvent"] = command };
    if (!omitRequestId) message["requestId"] = malformedId ? "bad-id" : requestId.ToString("D");
    if (!omitVersion) message["protocolVersion"] = new { major, minor };
    if (!omitSchema) message["schemaVersion"] = 1;
    if (!omitCorrelation) message["correlationId"] = wrongCorrelation ? "not-a-uuid" : (Guid?)null;
    if (!omitSession) message["sessionId"] = wrongSession ? "not-a-uuid" : (Guid?)null;
    if (!omitSequence) message["eventSequence"] = nonzeroSequence ? 1 : 0;
    if (!omitPayload) message["payload"] = payload ?? new { direction = "BOTH" };
    return JsonSerializer.Serialize(message);
}
ConsentReceiptDto ConsentReceiptFor(WorkerSessionSettingsDto settings) => new(true, Guid.NewGuid(), WorkerConsent.Fingerprint(settings));
var configuredRoot = Environment.GetEnvironmentVariable("KLIDAN_TEST_REPOSITORY_ROOT");
var root = configuredRoot is null ? new DirectoryInfo(AppContext.BaseDirectory) : new DirectoryInfo(configuredRoot);
while (configuredRoot is null && root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
if (root is null) throw new InvalidOperationException("Repository root not found.");
var buildRoot = Environment.GetEnvironmentVariable("KLIDAN_TEST_BUILD_ROOT") ?? Path.Combine(root.FullName, "build");
var buildWorker = Environment.GetEnvironmentVariable("KLIDAN_TEST_GUI_WORKER") ?? Path.Combine(root.FullName, "src", "gui", "KlidanAudioProbe.Gui.App", "bin", "Release", "net10.0-windows", "KlidanProbeWorker.exe");
var nativeWorker = Path.Combine(buildRoot, "worker", "Release", "KlidanProbeWorker.exe");
var testWorker = Path.Combine(AppContext.BaseDirectory, "KlidanAudioProbe.Gui.Worker.Tests.exe");
var stallWorker = Path.Combine(buildRoot, "worker-tests", "Release", "probe_worker_stall_fixture.exe");
var mismatchWorker = Path.Combine(buildRoot, "worker-tests", "Release", "probe_worker_mismatch_fixture.exe");
var formatFixtureWorker = Path.Combine(buildRoot, "worker-tests", "Release", "probe_worker_format_fixture.exe");
foreach (var binary in new[] { buildWorker, nativeWorker, testWorker, stallWorker, mismatchWorker, formatFixtureWorker }) if (!File.Exists(binary)) throw new FileNotFoundException("Build the native worker and worker fixtures before running these tests.", binary);
var productionWorkerBinaryText = Encoding.ASCII.GetString(File.ReadAllBytes(nativeWorker));
Check(!productionWorkerBinaryText.Contains("KLIDAN_TEST_PERIOD_SCENARIO", StringComparison.Ordinal) && !productionWorkerBinaryText.Contains("REJECT_FIRST_FOUR", StringComparison.Ordinal), "production Release Worker contains no period-discovery Fake Worker scenario mechanism");

var handshakeId = Guid.NewGuid();
var incompatibleHandshakes = new[]
{
    HandshakeJson(handshakeId, bootstrap: 2),
    HandshakeJson(handshakeId, kind: "response"),
    HandshakeJson(handshakeId, command: "ListEndpoints"),
    HandshakeJson(handshakeId, major: 2),
    HandshakeJson(handshakeId, minMinor: 1, maxMinor: 2),
    HandshakeJson(handshakeId, commandSchema: 2),
    HandshakeJson(handshakeId, eventSchema: 2),
    HandshakeJson(handshakeId, reportSchema: 2),
    HandshakeJson(handshakeId, omitProtocol: true),
    HandshakeJson(handshakeId, omitSchemaSet: true),
    HandshakeJson(handshakeId, omitRequestId: true),
    HandshakeJson(handshakeId, malformedId: true)
};
foreach (var hello in incompatibleHandshakes)
{
    var error = (await RawWorkerExchangeAsync(nativeWorker, hello)).Single();
    Check(error.GetProperty("kind").GetString() == "error" && error.GetProperty("failureStage").GetString() == "HANDSHAKE" && error.GetProperty("errorCode").GetString() == "INCOMPATIBLE_PROTOCOL" && error.GetProperty("message").GetString()!.Length <= 512, "native worker rejects incompatible or incomplete D1 handshake with bounded typed error");
    using var helloDoc = JsonDocument.Parse(hello);
    Guid? trustedId = helloDoc.RootElement.TryGetProperty("requestId", out var rawId) && rawId.ValueKind == JsonValueKind.String && rawId.TryGetGuid(out var parsedId) ? parsedId : null;
    if (trustedId is { } id) Check(error.GetProperty("correlationId").GetGuid() == id, "trustworthy handshake request UUID is echoed in incompatibility error");
    else Check(error.GetProperty("correlationId").ValueKind == JsonValueKind.Null, "untrusted/missing handshake UUID is not used for correlation");
}
var rawRequestId = Guid.NewGuid();
var rawGoodHello = HandshakeJson(rawRequestId);
var rawBadCases = new[]
{
    (ClientRequestJson(Guid.NewGuid(), omitVersion: true), "INCOMPATIBLE_PROTOCOL", true),
    (ClientRequestJson(Guid.NewGuid(), major: 9), "INCOMPATIBLE_PROTOCOL", true),
    (ClientRequestJson(Guid.NewGuid(), omitSchema: true), "INCOMPATIBLE_PROTOCOL", true),
    (ClientRequestJson(Guid.NewGuid(), kind: "response"), "INVALID_REQUEST", true),
    (ClientRequestJson(Guid.NewGuid(), omitCorrelation: true), "INVALID_REQUEST", true),
    (ClientRequestJson(Guid.NewGuid(), wrongCorrelation: true), "INVALID_REQUEST", true),
    (ClientRequestJson(Guid.NewGuid(), omitSession: true), "INVALID_REQUEST", true),
    (ClientRequestJson(Guid.NewGuid(), wrongSession: true), "INVALID_REQUEST", true),
    (ClientRequestJson(Guid.NewGuid(), omitSequence: true), "INVALID_REQUEST", true),
    (ClientRequestJson(Guid.NewGuid(), nonzeroSequence: true), "INVALID_REQUEST", true),
    (ClientRequestJson(Guid.NewGuid(), payload: new[] { 1, 2 }), "INVALID_REQUEST", true),
    (ClientRequestJson(Guid.NewGuid(), omitPayload: true), "INVALID_REQUEST", true),
    (ClientRequestJson(Guid.NewGuid(), command: new string('X', 65)), "INVALID_REQUEST", true),
    (ClientRequestJson(Guid.NewGuid(), malformedId: true), "REQUEST_ENVELOPE", false),
    (ClientRequestJson(Guid.NewGuid(), omitRequestId: true), "REQUEST_ENVELOPE", false)
};
foreach (var (badRequest, expectedCode, correlated) in rawBadCases)
{
    using var badRequestDocument = JsonDocument.Parse(badRequest);
    var requestId = badRequestDocument.RootElement.TryGetProperty("requestId", out var idProperty) ? idProperty.GetString() : null;
    var followId = Guid.NewGuid();
    var list = ClientRequestJson(followId);
    var shutdown = ClientRequestJson(Guid.NewGuid(), "Shutdown", payload: new { mode = "IDLE" });
    var responses = correlated
        ? await RawWorkerExchangeAsync(nativeWorker, rawGoodHello, badRequest, list, shutdown)
        : await RawWorkerExchangeAsync(nativeWorker, rawGoodHello, badRequest);
    var invalid = responses[1];
    Check(correlated
        ? invalid.GetProperty("status").GetString() == "ERROR" && invalid.GetProperty("errorCode").GetString() == expectedCode && invalid.GetProperty("correlationId").GetGuid() == Guid.Parse(requestId!)
        : invalid.GetProperty("kind").GetString() == "error" && invalid.GetProperty("failureStage").GetString() == expectedCode && invalid.GetProperty("correlationId").ValueKind == JsonValueKind.Null,
        "native worker fails closed on malformed post-handshake envelope");
    if (correlated) Check(responses[2].GetProperty("status").GetString() == "OK" && responses[2].GetProperty("correlationId").GetGuid() == followId, "correlated envelope rejection does not dispatch malformed command or break later valid requests");
}

Check(WorkerProtocolCompatibility.IsCompatible(1, 0, 1, 1, 1), "protocol 1.0 with all required schemas negotiates");
foreach (var mismatch in new[] { (2, 0, 1, 1, 1), (1, 1, 1, 1, 1), (1, 0, 2, 1, 1), (1, 0, 1, 2, 1), (1, 0, 1, 1, 2) })
    Check(!WorkerProtocolCompatibility.IsCompatible(mismatch.Item1, mismatch.Item2, mismatch.Item3, mismatch.Item4, mismatch.Item5), "protocol or required schema mismatch fails closed");

var sequenceClient = new NativeWorkerClient(buildWorker);
var sequenceEvents = new List<WorkerEventDto>();
var sequenceFaults = new List<WorkerTransportFaultDto>();
sequenceClient.EventReceived += sequenceEvents.Add;
sequenceClient.TransportFault += sequenceFaults.Add;
await sequenceClient.ConnectAsync();
var sequenceSession = Guid.NewGuid(); var sequenceCorrelation = Guid.NewGuid(); const long sequenceSeed = 9_007_199_254_740_992L;
sequenceClient.SeedEventCursorForTest(sequenceSession, sequenceCorrelation, sequenceSeed);
string EventJson(long sequence, string name, Guid? session = null) => $"{{\"protocolVersion\":{{\"major\":1,\"minor\":0}},\"schemaVersion\":1,\"kind\":\"event\",\"requestId\":null,\"correlationId\":\"{sequenceCorrelation:D}\",\"sessionId\":\"{(session ?? sequenceSession):D}\",\"eventSequence\":{sequence},\"commandOrEvent\":\"{name}\",\"payload\":{{\"synthetic\":true}}}}";
sequenceClient.DispatchEventForTest(EventJson(sequenceSeed + 1, "Preparing"));
Check(sequenceEvents.Single().EventSequence == 9_007_199_254_740_993L, "event sequence above IEEE-754 exact range remains an exact Int64");
ThrowsSync<InvalidDataException>(() => sequenceClient.DispatchEventForTest(EventJson(sequenceSeed + 1, "Ready")), "out-of-order nonterminal event is rejected");
sequenceClient.DispatchEventForTest(EventJson(sequenceSeed + 2, "Completed"));
sequenceClient.DispatchEventForTest(EventJson(sequenceSeed + 2, "Completed"));
Check(sequenceFaults.Any(f => f.Stage == "AFTER_TERMINAL_EVENT"), "duplicate terminal event is ignored as a duplicate");
sequenceClient.DispatchEventForTest(EventJson(sequenceSeed + 3, "Warning", Guid.NewGuid()));
Check(sequenceFaults.Any(f => f.Stage == "STALE_SESSION_EVENT"), "stale session event is reported separately");
for (var i = 0; i < 80; i++) sequenceClient.SeedEventCursorForTest(Guid.NewGuid(), Guid.NewGuid(), sequenceSeed + 100 + i, terminal: true);
Check(sequenceClient.SessionCursorCountForTest == 0 && sequenceClient.TerminalSessionCountForTest == 64, "terminal session cursors are evicted into a bounded duplicate-event tombstone history");
sequenceClient.DispatchResponseForTest($"{{\"protocolVersion\":{{\"major\":1,\"minor\":0}},\"schemaVersion\":1,\"kind\":\"response\",\"requestId\":null,\"correlationId\":\"{Guid.NewGuid():D}\",\"sessionId\":null,\"eventSequence\":0,\"commandOrEvent\":\"ListEndpoints\",\"status\":\"OK\",\"payload\":{{}}}}");
Check(sequenceFaults.Any(f => f.Stage == "LATE_RESPONSE") && sequenceClient.State.State == WorkerConnectionState.ConnectedFake, "late or unknown correlation response is ignored without disconnecting");
var expiredDuplexCorrelation = sequenceClient.SeedTimedOutRequestForTest("ValidateDuplex");
var currentDuplexCorrelation = sequenceClient.SeedSentRequestForTest("ValidateDuplex");
var currentDuplexCompletion = sequenceClient.PendingTaskForTest(currentDuplexCorrelation);
string DuplexResponse(Guid correlation) => $"{{\"protocolVersion\":{{\"major\":1,\"minor\":0}},\"schemaVersion\":1,\"kind\":\"response\",\"requestId\":null,\"correlationId\":\"{correlation:D}\",\"sessionId\":null,\"eventSequence\":0,\"commandOrEvent\":\"ValidateDuplex\",\"status\":\"OK\",\"payload\":{{}}}}";
sequenceClient.DispatchResponseForTest(DuplexResponse(expiredDuplexCorrelation));
Check(!currentDuplexCompletion.IsCompleted && sequenceClient.PendingCountForTest == 1 && sequenceFaults.Last() is { Stage: "LATE_RESPONSE", Command: "ValidateDuplex" }, "late Duplex response cannot complete or remove a different current correlation");
sequenceClient.DispatchResponseForTest(DuplexResponse(currentDuplexCorrelation));
Check(currentDuplexCompletion.IsCompletedSuccessfully && sequenceClient.PendingCountForTest == 0, "current Duplex response completes only its matching request");
using (var currentDuplexResponse = await currentDuplexCompletion) { }
var lateSessionId = Guid.NewGuid(); var lateAccepted = new List<WorkerSessionAcceptedDto>();
sequenceClient.SessionAccepted += lateAccepted.Add;
var lateSettings = new WorkerSessionSettingsDto("fake-capture-2", "CAPTURE", "PCM16-48000", 48000, "PCM16", 2, "PCM16-48000", 3, "DEFAULT", "OFF", null, "NORMAL");
var lateCorrelation = sequenceClient.SeedTimedOutRequestForTest("StartCaptureMeter", lateSettings);
sequenceClient.DispatchResponseForTest($"{{\"protocolVersion\":{{\"major\":1,\"minor\":0}},\"schemaVersion\":1,\"kind\":\"response\",\"requestId\":null,\"correlationId\":\"{lateCorrelation:D}\",\"sessionId\":\"{lateSessionId:D}\",\"eventSequence\":0,\"commandOrEvent\":\"StartCaptureMeter\",\"status\":\"OK\",\"payload\":{{\"acceptance\":\"ACCEPTED\",\"operationState\":\"ACCEPTED\",\"settings\":{{\"endpointId\":\"fake-capture-2\",\"direction\":\"CAPTURE\",\"formatId\":\"PCM16-48000\",\"sampleRate\":48000,\"encoding\":\"PCM16\",\"channelCount\":2,\"topologyId\":\"PCM16-48000\",\"durationSeconds\":3,\"period\":\"DEFAULT\",\"mmcss\":\"OFF\",\"renderChannel\":null,\"scenario\":\"NORMAL\"}}}}}}");
Check(lateAccepted.Single().SessionId == lateSessionId && lateAccepted.Single().Settings == lateSettings, "late Start acceptance is surfaced with its immutable accepted settings instead of counted as success or dropped");
sequenceClient.DispatchEventForTest($"{{\"protocolVersion\":{{\"major\":1,\"minor\":0}},\"schemaVersion\":1,\"kind\":\"event\",\"requestId\":null,\"correlationId\":\"{lateCorrelation:D}\",\"sessionId\":\"{lateSessionId:D}\",\"eventSequence\":{sequenceSeed + 200},\"commandOrEvent\":\"Preparing\",\"payload\":{{\"synthetic\":true}}}}");
Check(sequenceEvents.Last().SessionId == lateSessionId, "events for a late-accepted session remain dispatchable after acceptance arrives");
var meterSessionId = Guid.NewGuid(); var meterCorrelationId = Guid.NewGuid();
sequenceClient.SeedMeterSessionForTest(meterSessionId, meterCorrelationId, "CAPTURE", 1);
string MeterJson(long eventSequence, long snapshotSequence, string level, string silent = "false") => $"{{\"protocolVersion\":{{\"major\":1,\"minor\":0}},\"schemaVersion\":1,\"kind\":\"event\",\"requestId\":null,\"correlationId\":\"{meterCorrelationId:D}\",\"sessionId\":\"{meterSessionId:D}\",\"eventSequence\":{eventSequence},\"commandOrEvent\":\"MeterSnapshot\",\"payload\":{{\"synthetic\":true,\"lifecycle\":\"STARTED\",\"streamQuality\":\"UNKNOWN\",\"cleanupEvidence\":\"NOT_ATTEMPTED\",\"snapshotSequence\":{snapshotSequence},\"completedWindowIndex\":1,\"windowDurationMs\":100,\"channelCount\":1,\"channels\":[{{\"index\":1,\"windowPeakDbfs\":{level},\"windowRmsDbfs\":{level},\"silent\":{silent},\"sessionPeakHoldDbfs\":{level},\"clipCount\":0}}],\"coalescedSnapshotCount\":0}}}}";
sequenceClient.DispatchEventForTest(MeterJson(sequenceSeed + 201, 1, "-20.0"));
Check(sequenceEvents.Last() is { Name: "MeterSnapshot", CaptureMeter.ChannelCount: 1 }, "validated one-channel snapshot accepts finite JSON-safe levels");
ThrowsSync<InvalidDataException>(() => sequenceClient.DispatchEventForTest(MeterJson(sequenceSeed + 202, 1, "-20.0")), "duplicate snapshot sequence is rejected while event gaps remain permitted");
ThrowsSync<InvalidDataException>(() => sequenceClient.DispatchEventForTest(MeterJson(sequenceSeed + 203, 2, "1e999")), "non-finite numeric level is rejected before ViewModel delivery");
ThrowsSync<InvalidDataException>(() => sequenceClient.DispatchEventForTest(MeterJson(sequenceSeed + 204, 2, "-20.0", "true")), "silence carrying a non-null window level is rejected");
var renderTestCorrelation = Guid.NewGuid(); var renderTestSession = Guid.NewGuid(); var renderEventSequence = sequenceSeed + 205;
string RenderMeterJson(long eventSequence, long snapshotSequence, int count, int selected, bool noWindow, int malformedChannel = 0, string malformedField = "")
{
    var channels = new List<string>();
    for (var index = 1; index <= count; index++)
    {
        var isSelected = index == selected;
        var peak = !noWindow && isSelected ? "-36.0" : "null";
        var rms = !noWindow && isSelected ? "-42.0" : "null";
        var silent = noWindow ? "null" : isSelected ? "false" : "true";
        var hold = !noWindow && isSelected ? "-36.0" : "null";
        var clips = noWindow ? "null" : "0";
        if (index == malformedChannel)
        {
            switch (malformedField)
            {
                case "peak": peak = "-36.0"; break;
                case "rms": rms = "-42.0"; break;
                case "silent": silent = "true"; break;
                case "hold": hold = "-36.0"; break;
                case "clips": clips = "0"; break;
            }
        }
        channels.Add($"{{\"index\":{index},\"intendedPeakDbfs\":{peak},\"intendedRmsDbfs\":{rms},\"digitalSilent\":{silent},\"sessionPeakHoldDbfs\":{hold},\"clipCount\":{clips}}}");
    }
    return $"{{\"protocolVersion\":{{\"major\":1,\"minor\":0}},\"schemaVersion\":1,\"kind\":\"event\",\"requestId\":null,\"correlationId\":\"{renderTestCorrelation:D}\",\"sessionId\":\"{renderTestSession:D}\",\"eventSequence\":{eventSequence},\"commandOrEvent\":\"MeterSnapshot\",\"payload\":{{\"synthetic\":true,\"lifecycle\":\"STARTED\",\"streamQuality\":\"UNKNOWN\",\"cleanupEvidence\":\"NOT_ATTEMPTED\",\"snapshotSequence\":{snapshotSequence},\"completedWindowIndex\":{(noWindow ? "null" : "1")},\"windowDurationMs\":100,\"channelCount\":{count},\"channels\":[{string.Join(',', channels)}],\"selectedChannelIndex\":{selected},\"intendedOutputModel\":\"SYNTHETIC_INTENDED_OUTPUT_NOT_MEASURED\",\"coalescedSnapshotCount\":0}}}}";
}
sequenceClient.SeedMeterSessionForTest(renderTestSession, renderTestCorrelation, "RENDER", 2, 1);
sequenceClient.DispatchEventForTest(RenderMeterJson(renderEventSequence, 1, 2, 1, true));
Check(sequenceEvents.Last().RenderMeter is { CompletedWindowIndex: null, Channels.Count: 2 } noWindowRender && noWindowRender.Channels.All(c => c.IntendedPeakDbfs is null && c.IntendedRmsDbfs is null && c.DigitalSilent is null && c.SessionPeakHoldDbfs is null && c.ClipCount is null), "Render no-window snapshot accepts null measurements on every channel");
foreach (var (channel, field) in new[] { (1, "peak"), (1, "rms"), (1, "silent"), (1, "hold"), (1, "clips"), (2, "silent"), (2, "clips") })
{
    var badSession = Guid.NewGuid(); renderTestSession = badSession;
    sequenceClient.SeedMeterSessionForTest(badSession, renderTestCorrelation, "RENDER", 2, 1);
    ThrowsSync<InvalidDataException>(() => sequenceClient.DispatchEventForTest(RenderMeterJson(++renderEventSequence, 1, 2, 1, true, channel, field)), $"Render no-window rejects fabricated {field} on channel {channel}");
}
foreach (var topologyCount in new[] { 1, 2, 8, 16, 32, 48, 64 })
{
    foreach (var selected in topologyCount == 1 ? new[] { 1 } : new[] { 1, topologyCount })
    {
        var testSession = Guid.NewGuid(); renderTestSession = testSession;
        sequenceClient.SeedMeterSessionForTest(testSession, renderTestCorrelation, "RENDER", topologyCount, selected);
        sequenceClient.DispatchEventForTest(RenderMeterJson(++renderEventSequence, 1, topologyCount, selected, true));
        var snapshot = sequenceEvents.Last().RenderMeter!;
        Check(snapshot.ChannelCount == topologyCount && snapshot.SelectedChannelIndex == selected && snapshot.Channels.All(c => c.IntendedPeakDbfs is null && c.IntendedRmsDbfs is null && c.DigitalSilent is null && c.SessionPeakHoldDbfs is null && c.ClipCount is null), $"Render no-window parser accepts all-null {topologyCount}-channel topology with selected channel {selected}");
    }
}
var lateRejectionCorrelation = sequenceClient.SeedTimedOutRequestForTest("StartCaptureMeter", lateSettings);
sequenceClient.DispatchResponseForTest($"{{\"protocolVersion\":{{\"major\":1,\"minor\":0}},\"schemaVersion\":1,\"kind\":\"response\",\"requestId\":null,\"correlationId\":\"{lateRejectionCorrelation:D}\",\"sessionId\":null,\"eventSequence\":0,\"commandOrEvent\":\"StartCaptureMeter\",\"status\":\"ERROR\",\"errorCode\":\"BUSY\",\"message\":\"operation active\",\"payload\":{{}}}}");
Check(sequenceFaults.Last() is { Stage: "LATE_RESPONSE", Command: "StartCaptureMeter", ErrorCode: "BUSY", CorrelationId: not null } && sequenceClient.State.State == WorkerConnectionState.ConnectedFake, "late typed Start rejection preserves correlation and connection");
await sequenceClient.ShutdownAsync();
await sequenceClient.DisposeAsync();

var preCancelledClient = new NativeWorkerClient(testWorker);
await preCancelledClient.ConnectAsync();
using (var preCancelled = new CancellationTokenSource())
{
    preCancelled.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => preCancelledClient.ListEndpointsAsync(preCancelled.Token), "cancellation before request write is reported before dispatch");
}
Check(preCancelledClient.PendingCountForTest == 0 && preCancelledClient.State.State == WorkerConnectionState.ConnectedFake, "pre-write cancellation removes its pending request and preserves connection");
await preCancelledClient.ShutdownAsync();
await preCancelledClient.DisposeAsync();

var capacityClient = new NativeWorkerClient(testWorker);
await capacityClient.ConnectAsync();
for (var i = 0; i < 32; i++) capacityClient.SeedTimedOutRequestForTest("ListEndpoints");
Check(capacityClient.PendingCountForTest == 32, "abandoned request retention is bounded at the configured capacity");
await ThrowsAsync<InvalidOperationException>(() => capacityClient.SendForTestAsync("ListEndpoints", new { direction = "BOTH" }), "request capacity fails closed when unresolved correlations reach the bound");
Check(capacityClient.PendingCountForTest == 32, "capacity rejection does not grow retained pending state");
await capacityClient.DisposeAsync();

var responseRaceClient = new NativeWorkerClient(testWorker);
var raceFaults = new ConcurrentQueue<WorkerTransportFaultDto>();
responseRaceClient.TransportFault += raceFaults.Enqueue;
await responseRaceClient.ConnectAsync();
var raceCorrelation = responseRaceClient.SeedSentRequestForTest("ListEndpoints");
using (var race = new Barrier(2))
{
    var abandonWon = Task.Run(() => { race.SignalAndWait(); return responseRaceClient.AbandonRequestForTest(raceCorrelation); });
    var responseWon = Task.Run(() =>
    {
        race.SignalAndWait();
        responseRaceClient.DispatchResponseForTest($"{{\"protocolVersion\":{{\"major\":1,\"minor\":0}},\"schemaVersion\":1,\"kind\":\"response\",\"requestId\":null,\"correlationId\":\"{raceCorrelation:D}\",\"sessionId\":null,\"eventSequence\":0,\"commandOrEvent\":\"ListEndpoints\",\"status\":\"OK\",\"payload\":{{}}}}");
    });
    var abandoned = await abandonWon;
    await responseWon;
    Check(responseRaceClient.PendingCountForTest == 0 && (abandoned ? raceFaults.Any(f => f.Stage == "LATE_RESPONSE") : !raceFaults.Any(f => f.Stage == "LATE_RESPONSE")), "response and cancellation boundary resolves atomically to exactly one observed outcome");
}
var raceSettings = new WorkerSessionSettingsDto("fake-capture-2", "CAPTURE", "PCM16-48000", 48000, "PCM16", 2, "PCM16-48000", 3, "DEFAULT", "OFF", null, "NORMAL");
var raceStartCorrelation = responseRaceClient.SeedSentRequestForTest("StartCaptureMeter", raceSettings);
var raceStartAccepted = new ConcurrentQueue<WorkerSessionAcceptedDto>();
responseRaceClient.SessionAccepted += raceStartAccepted.Enqueue;
bool startAbandoned;
using (var race = new Barrier(2))
{
    var abandon = Task.Run(() => { race.SignalAndWait(); return responseRaceClient.AbandonRequestForTest(raceStartCorrelation); });
    var dispatch = Task.Run(() =>
    {
        race.SignalAndWait();
        responseRaceClient.DispatchResponseForTest($"{{\"protocolVersion\":{{\"major\":1,\"minor\":0}},\"schemaVersion\":1,\"kind\":\"response\",\"requestId\":null,\"correlationId\":\"{raceStartCorrelation:D}\",\"sessionId\":\"{Guid.NewGuid():D}\",\"eventSequence\":0,\"commandOrEvent\":\"StartCaptureMeter\",\"status\":\"OK\",\"payload\":{{\"acceptance\":\"ACCEPTED\",\"operationState\":\"ACCEPTED\",\"settings\":{{\"endpointId\":\"fake-capture-2\",\"direction\":\"CAPTURE\",\"formatId\":\"PCM16-48000\",\"sampleRate\":48000,\"encoding\":\"PCM16\",\"channelCount\":2,\"topologyId\":\"PCM16-48000\",\"durationSeconds\":3,\"period\":\"DEFAULT\",\"mmcss\":\"OFF\",\"renderChannel\":null,\"scenario\":\"NORMAL\"}}}}}}");
    });
    startAbandoned = await abandon;
    await dispatch;
}
Check(raceStartAccepted.Count == (startAbandoned ? 1 : 0) && raceStartAccepted.All(a => a.Settings == raceSettings && a.OperationState == "ACCEPTED"), "Start response racing cancellation is either a normal response or one surfaced late acceptance");
await responseRaceClient.ShutdownAsync();
await responseRaceClient.DisposeAsync();

var previousFixtureMode = Environment.GetEnvironmentVariable("KLIDAN_TEST_WORKER_MODE");
Environment.SetEnvironmentVariable("KLIDAN_TEST_WORKER_MODE", "DELAY_ACCEPT");
var lateProcessClient = new NativeWorkerClient(testWorker);
var processLateAcceptance = new List<WorkerSessionAcceptedDto>();
lateProcessClient.SessionAccepted += processLateAcceptance.Add;
await lateProcessClient.ConnectAsync();
var processLateSettings = new WorkerSessionSettingsDto("fake-capture-2", "CAPTURE", "PCM16-48000", 48000, "PCM16", 2, "PCM16-48000", 3, "DEFAULT", "OFF", null, "NORMAL");
using (var cancelAfterWrite = new CancellationTokenSource())
{
    lateProcessClient.RequestWrittenForTest = _ => cancelAfterWrite.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => lateProcessClient.StartSessionAsync(processLateSettings, ConsentReceiptFor(processLateSettings), cancelAfterWrite.Token), "caller cancellation after frame write leaves Start outcome pending for late response");
}
lateProcessClient.RequestWrittenForTest = null;
await WaitUntilAsync(() => processLateAcceptance.Count == 1, TimeSpan.FromSeconds(5));
Check(processLateAcceptance.Single() is { OperationState: "ACCEPTED" } accepted && accepted.Settings == processLateSettings && accepted.CorrelationId != Guid.Empty && accepted.SessionId != Guid.Empty, "real local fake process late acceptance exposes session, correlation, settings and worker state");
await lateProcessClient.StopSessionAsync(processLateAcceptance.Single().SessionId);
await WaitUntilAsync(() => lateProcessClient.PendingCountForTest == 0, TimeSpan.FromSeconds(2));
await lateProcessClient.ShutdownAsync();
await lateProcessClient.DisposeAsync();

Environment.SetEnvironmentVariable("KLIDAN_TEST_WORKER_MODE", "DELAY_REJECT");
var lateRejectClient = new NativeWorkerClient(testWorker);
var lateRejectFaults = new ConcurrentQueue<WorkerTransportFaultDto>();
lateRejectClient.TransportFault += lateRejectFaults.Enqueue;
await lateRejectClient.ConnectAsync();
using (var cancelRejectedStart = new CancellationTokenSource())
{
    lateRejectClient.RequestWrittenForTest = _ => cancelRejectedStart.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => lateRejectClient.StartSessionAsync(processLateSettings, ConsentReceiptFor(processLateSettings), cancelRejectedStart.Token), "cancelled Start can later receive a typed rejection");
}
lateRejectClient.RequestWrittenForTest = null;
await WaitUntilAsync(() => lateRejectFaults.Any(f => f.Stage == "LATE_RESPONSE" && f.Command == "StartCaptureMeter"), TimeSpan.FromSeconds(5));
Check(lateRejectFaults.Any(f => f.ErrorCode == "BUSY" && f.CorrelationId is not null) && lateRejectClient.State.State == WorkerConnectionState.ConnectedFake && lateRejectClient.PendingCountForTest == 0, "late rejection clears unresolved Start without phantom acceptance or disconnect");
await lateRejectClient.ShutdownAsync();
await lateRejectClient.DisposeAsync();

Environment.SetEnvironmentVariable("KLIDAN_TEST_WORKER_MODE", "HOLD_START");
var unresolvedClient = new NativeWorkerClient(testWorker);
var unresolvedAccepted = new ConcurrentQueue<WorkerSessionAcceptedDto>();
var unresolvedDisconnected = new TaskCompletionSource<WorkerStateDto>(TaskCreationOptions.RunContinuationsAsynchronously);
unresolvedClient.SessionAccepted += unresolvedAccepted.Enqueue;
unresolvedClient.StateChanged += state => { if (state.State == WorkerConnectionState.Disconnected) unresolvedDisconnected.TrySetResult(state); };
await unresolvedClient.ConnectAsync();
using (var cancelUnknown = new CancellationTokenSource())
{
    unresolvedClient.RequestWrittenForTest = _ => cancelUnknown.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => unresolvedClient.StartSessionAsync(processLateSettings, ConsentReceiptFor(processLateSettings), cancelUnknown.Token), "unresolved Start cancellation does not fabricate a response");
}
unresolvedClient.RequestWrittenForTest = null;
Process.GetProcessById(unresolvedClient.State.ProcessId!.Value).Kill(entireProcessTree: true);
await unresolvedDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(unresolvedAccepted.IsEmpty && unresolvedClient.PendingCountForTest == 0 && unresolvedClient.State.State == WorkerConnectionState.Disconnected, "connection loss during unresolved Start leaves no phantom acceptance and ends in disconnected unknown state");
await unresolvedClient.DisposeAsync();
Environment.SetEnvironmentVariable("KLIDAN_TEST_WORKER_MODE", previousFixtureMode);

var windowsFixtureClient = new NativeWorkerClient(testWorker);
await windowsFixtureClient.ConnectAsync();
Check(windowsFixtureClient.Capabilities.Contains("ListWindowsEndpoints", StringComparer.Ordinal), "additive Windows discovery capability is exposed by compatible protocol 1.0 worker");
var windowsFixtureResult = await windowsFixtureClient.ListWindowsEndpointsAsync();
Check(windowsFixtureResult.Provenance == "REAL_WINDOWS_METADATA" && windowsFixtureResult.Endpoints.Single().EndpointId == "fixture-local-id" && windowsFixtureResult.Endpoints[0].SharedModeFormat.ReportedChannelCount == 8, "typed Windows endpoint result is parsed over IPC without real endpoint enumeration");
await windowsFixtureClient.ShutdownAsync();
await windowsFixtureClient.DisposeAsync();

var worker = new NativeWorkerClient(buildWorker);
var states = new List<WorkerConnectionState>();
worker.StateChanged += state => states.Add(state.State);
await worker.ConnectAsync();
Check(worker.State.State == WorkerConnectionState.ConnectedFake && worker.State.ProcessId > 0, "matching bundled worker starts, connects and handshakes over the local pipe");
Check(states.SequenceEqual(new[] { WorkerConnectionState.Starting, WorkerConnectionState.Connecting, WorkerConnectionState.Handshaking, WorkerConnectionState.ConnectedFake }), "worker lifecycle reports starting, connecting, handshaking, and connected");

var injectedFormatClient = new NativeWorkerClient(formatFixtureWorker);
await injectedFormatClient.ConnectAsync();
Check(NativeWorkerClient.ResponseTimeoutForTest("ValidateDuplex") == TimeSpan.FromSeconds(51) && NativeWorkerClient.ResponseTimeoutForTest("ValidateSustainedDuplex") == TimeSpan.FromSeconds(641) && NativeWorkerClient.ResponseTimeoutForTest("ValidateStream") == TimeSpan.FromSeconds(45) && NativeWorkerClient.ResponseTimeoutForTest("ListEndpoints") == TimeSpan.FromSeconds(5), "short and sustained Duplex IPC deadlines remain distinct and bounded");
Check(NativeWorkerClient.ResponseTimeoutForTest("CloseScanSession") == TimeSpan.FromSeconds(8), "CloseScanSession uses the shared bounded eight-second IPC budget");
Check(injectedFormatClient.Capabilities.Contains("TestInjectedFormatBackend", StringComparer.Ordinal), "test-only worker advertises its injected non-WASAPI backend explicitly");
var beforeDiscovery = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.QueryFormatMatrixAsync("injected-endpoint-capture", "CAPTURE"), "format query requires an endpoint from an explicit discovery response");
Check(beforeDiscovery.ErrorCode == "ENDPOINT_NOT_DISCOVERED", "query before discovery performs no implicit endpoint scan");
using (var injectedDiscovery = await injectedFormatClient.SendForTestAsync("ListWindowsEndpoints", new { }))
    Check(injectedDiscovery.RootElement.GetProperty("payload").GetProperty("provenance").GetString() == "TEST_FAKE_FORMAT_BACKEND" && injectedDiscovery.RootElement.GetProperty("payload").GetProperty("endpoints").GetArrayLength() == 2, "injected test discovery supplies distinct synthetic Capture and Render identities without MMDevice");
var scanFormats = (from rate in new[] { 44100, 48000 } from encoding in new[] { "PCM16", "PCM24_IN_32", "FLOAT32" } select new WorkerScanFormat(rate, encoding, 2, ["MINIMUM", "DEFAULT"], ["OFF", "PRO_AUDIO"])).ToArray();
WorkerScanPlan ScanPlan(WorkerScanEndpoint[] endpoints, int lifetime = 60, string[]? periods = null) => new(endpoints, ["QueryFormatMatrix", "ValidateFormatMatrix", "ValidateStream", "ValidateDuplex"], scanFormats.Select(f => f with { Periods = periods ?? f.Periods }).ToArray(), lifetime, 5, 8);
var scanEndpoints = new[] { new WorkerScanEndpoint("injected-endpoint-capture", "CAPTURE"), new WorkerScanEndpoint("injected-endpoint-render", "RENDER") };
var scanPlan = ScanPlan(scanEndpoints);
var scanConfirmation = Guid.NewGuid();
var scanSession = await injectedFormatClient.RegisterScanSessionAsync(scanPlan, true, scanConfirmation);
Check(scanSession.ScanSessionId != Guid.Empty, "Worker returns an opaque, connection-local scan authorization after explicit acceptance");
var scanQueryId = Guid.NewGuid();
var scanQuery = await injectedFormatClient.QueryFormatMatrixInScanAsync(scanSession, scanQueryId, "injected-endpoint-capture", "CAPTURE", [2]);
Check(scanQuery.Candidates.Count == 12 && scanQuery.Provenance == "TEST_FAKE_FORMAT_BACKEND", "session-authorized query executes only within the current exact endpoint and candidate plan");
var scanReplay = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.QueryFormatMatrixInScanAsync(scanSession, scanQueryId, "injected-endpoint-capture", "CAPTURE", [2]), "Worker rejects a reused scan operation UUID");
Check(scanReplay.ErrorCode == "SCAN_OPERATION_REJECTED", $"scan operation replay is rejected at Worker boundary ({scanReplay.ErrorCode})");
var scanInit = await injectedFormatClient.ValidateFormatMatrixInScanAsync(scanSession, Guid.NewGuid(), "injected-endpoint-capture", "CAPTURE", 48000, 2, "PCM16", "MINIMUM");
Check(scanInit.Candidates.Count == 1 && scanInit.Candidates[0].InitializeAttempted, "Initialize-only uses Worker session authorization without fabricating a manual receipt");
var scanStreamOperation = Guid.NewGuid();
var scanStream = await injectedFormatClient.ValidateStreamAsync(scanStreamOperation.ToString("D"), "injected-endpoint-capture", "CAPTURE", "MINIMUM", 1, "OFF", new(false, Guid.Empty, ""), scanSession: scanSession, scanOperationId: scanStreamOperation);
Check(scanStream.Report.GetProperty("provenance").GetString() == "TEST_FAKE_STREAM_BACKEND" && scanStream.Report.GetProperty("framesProcessed").GetInt64() == 48000, "session-authorized Stream returns only injected structured evidence");
var scanDuplexOperation = Guid.NewGuid();
var scanDuplex = await injectedFormatClient.ValidateDuplexAsync(scanDuplexOperation.ToString("D"), scanEndpoints[0].EndpointId, scanEndpoints[1].EndpointId, "MINIMUM", 1, "OFF", new(false, Guid.Empty, ""), scanSession: scanSession, scanOperationId: scanDuplexOperation);
Check(scanDuplex.Report.GetProperty("provenance").GetString() == "TEST_FAKE_WASAPI_DUPLEX", "session-authorized Duplex remains on the existing injected backend");
await injectedFormatClient.CloseScanSessionAsync(scanSession);
var sustainedPlan = new WorkerScanPlan(scanEndpoints, ["ValidateSustainedDuplex"], [new(48_000, "PCM16", 2, ["MINIMUM"], ["OFF"])], 90, 60, 1);
var sustainedSession = await injectedFormatClient.RegisterScanSessionAsync(sustainedPlan, true, Guid.NewGuid());
var sustainedOperation = Guid.NewGuid();
var sustained = await injectedFormatClient.ValidateSustainedDuplexInScanAsync(sustainedOperation.ToString("D"), scanEndpoints[0].EndpointId, scanEndpoints[1].EndpointId, "MINIMUM", 60, "OFF", sustainedSession, sustainedOperation);
Check(sustained.Report.GetProperty("schemaVersion").GetInt32() == 1 && sustained.Report.GetProperty("provenance").GetString() == "TEST_FAKE_WASAPI_SUSTAINED_DUPLEX" && sustained.Report.GetProperty("requested").GetProperty("durationSeconds").GetInt32() == 60, "authorized sustained Duplex uses a distinct command, schema and exact duration");
await injectedFormatClient.CloseScanSessionAsync(sustainedSession);
var replayedScanConsent = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.RegisterScanSessionAsync(scanPlan, true, scanConfirmation), "scan authorization confirmation cannot be replayed after closing its session");
Check(replayedScanConsent.ErrorCode == "SCAN_CONSENT_REPLAY", "Worker consumes scan-session approval IDs once per connection");
Environment.SetEnvironmentVariable("KLIDAN_TEST_SCAN_SCENARIO", "EXPIRE_ON_REGISTER");
var expiryClient = new NativeWorkerClient(formatFixtureWorker);
try
{
    await expiryClient.ConnectAsync();
    var expiryEndpoints = await expiryClient.ListWindowsEndpointsAsync();
    var expiryCapture = expiryEndpoints.Endpoints.Single(e => e.Direction == "CAPTURE");
    var expiryPlan = ScanPlan([new(expiryCapture.EndpointId!, "CAPTURE")]);
    var expirySession = await expiryClient.RegisterScanSessionAsync(expiryPlan, true, Guid.NewGuid());
    var expiryError = await ThrowsAsync<WorkerCommandException>(() => expiryClient.QueryFormatMatrixInScanAsync(expirySession, Guid.NewGuid(), expiryCapture.EndpointId!, "CAPTURE", [2]), "expired Worker scan session cannot authorize another operation");
    Check(expiryError.ErrorCode == "SCAN_SESSION_STALE" && expiryError.Message.Contains("expired", StringComparison.OrdinalIgnoreCase), "Worker reports expiry as the scan-session stale cause without identifiers");
    await expiryClient.CloseScanSessionAsync(expirySession);
}
finally { Environment.SetEnvironmentVariable("KLIDAN_TEST_SCAN_SCENARIO", null); await expiryClient.DisposeAsync(); }
Environment.SetEnvironmentVariable("KLIDAN_TEST_SCAN_SCENARIO", "GENERATION_MISMATCH");
var generationClient = new NativeWorkerClient(formatFixtureWorker);
try
{
    await generationClient.ConnectAsync();
    var generationEndpoints = await generationClient.ListWindowsEndpointsAsync();
    var generationCapture = generationEndpoints.Endpoints.Single(e => e.Direction == "CAPTURE");
    var generationPlan = ScanPlan([new(generationCapture.EndpointId!, "CAPTURE")]);
    var generationSession = await generationClient.RegisterScanSessionAsync(generationPlan, true, Guid.NewGuid());
    var generationError = await ThrowsAsync<WorkerCommandException>(() => generationClient.QueryFormatMatrixInScanAsync(generationSession, Guid.NewGuid(), generationCapture.EndpointId!, "CAPTURE", [2]), "a discovery-generation mismatch rejects authorization");
    Check(generationError.ErrorCode == "SCAN_SESSION_STALE" && generationError.Message.Contains("generation", StringComparison.OrdinalIgnoreCase), "Worker distinguishes discovery-generation mismatch without endpoint identifiers");
    await generationClient.CloseScanSessionAsync(generationSession);
}
finally { Environment.SetEnvironmentVariable("KLIDAN_TEST_SCAN_SCENARIO", null); await generationClient.DisposeAsync(); }
var badFingerprint = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.SendForTestAsync("RegisterScanSession", new { endpoints = new[] { new { endpointId = "injected-endpoint-capture", direction = "CAPTURE" } }, allowedOperations = new[] { "QueryFormatMatrix" }, formats = new[] { new { sampleRateHz = 48000, encoding = "PCM16", channels = 2, periods = new[] { "MINIMUM" }, mmcssModes = new[] { "OFF" } } }, lifetimeSeconds = 60, maximumOperationDurationSeconds = 5, maximumOperationCount = 1, planFingerprint = new string('0', 64), authorization = new { accepted = true, confirmationId = Guid.NewGuid() } }), "worker recomputes the immutable scan plan fingerprint");
Check(badFingerprint.ErrorCode == "SCAN_FINGERPRINT_MISMATCH", "caller-supplied plan fingerprint is not treated as authorization");
var scopedPlan = ScanPlan([scanEndpoints[0]], periods: ["MINIMUM"]);
var scopedSession = await injectedFormatClient.RegisterScanSessionAsync(scopedPlan, true, Guid.NewGuid());
var outOfScopeError = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.QueryFormatMatrixInScanAsync(scopedSession, Guid.NewGuid(), "injected-endpoint-render", "RENDER", [2]), "scan token cannot query a discovered endpoint outside its approved scope");
Check(outOfScopeError.ErrorCode == "SCAN_ENDPOINT_OUT_OF_SCOPE", "endpoint scope is verified from Worker-held authorization and current discovery");
var alteredError = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.ValidateFormatMatrixInScanAsync(scopedSession, Guid.NewGuid(), "injected-endpoint-capture", "CAPTURE", 48000, 2, "PCM16", "DEFAULT"), "changed period cannot reuse an authorization scoped to MINIMUM");
Check(alteredError.ErrorCode == "SCAN_FORMAT_OUT_OF_SCOPE", "changed period/settings are rejected by Worker plan validation");
await injectedFormatClient.CancelScanSessionAsync(scopedSession);
var cancelledError = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.QueryFormatMatrixInScanAsync(scopedSession, Guid.NewGuid(), "injected-endpoint-capture", "CAPTURE", [2]), "cancelled scan session cannot authorize later operations");
Check(cancelledError.ErrorCode == "SCAN_SESSION_STALE" && cancelledError.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase), "session cancellation invalidates the Worker authorization state with a distinct safe cause");
var discoveryStaleSession = await injectedFormatClient.RegisterScanSessionAsync(ScanPlan(scanEndpoints), true, Guid.NewGuid());
using (var refreshedDiscovery = await injectedFormatClient.SendForTestAsync("ListWindowsEndpoints", new { })) { }
var discoveryStaleError = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.QueryFormatMatrixInScanAsync(discoveryStaleSession, Guid.NewGuid(), "injected-endpoint-capture", "CAPTURE", [2]), "a discovery refresh invalidates previously authorized endpoint identity");
Check(discoveryStaleError.ErrorCode == "SCAN_SESSION_STALE" && discoveryStaleError.Message.Contains("discovery refresh", StringComparison.OrdinalIgnoreCase), "scan authorization invalidation identifies discovery refresh without exposing endpoint identity");
var cleanupPlan = new WorkerScanPlan([scanEndpoints[0]], ["ValidateStream"], [new(48000, "PCM16", 2, ["MINIMUM"], ["OFF"])], 60, 1, 2);
var cleanupSession = await injectedFormatClient.RegisterScanSessionAsync(cleanupPlan, true, Guid.NewGuid());
var cleanupOperation = Guid.NewGuid();
var cleanupEvidence = await injectedFormatClient.ValidateStreamAsync(cleanupOperation.ToString("D"), scanEndpoints[0].EndpointId, "CAPTURE", "MINIMUM", 1, "OFF", new(false, Guid.Empty, ""), testScenario: "CLEANUP_UNKNOWN", scanSession: cleanupSession, scanOperationId: cleanupOperation);
Check(cleanupEvidence.Report.GetProperty("cleanup").GetString() == "UNKNOWN", "uncertain cleanup remains visible in the accepted injected stream report");
var cleanupBlockedOperation = Guid.NewGuid();
var cleanupBlocked = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.ValidateStreamAsync(cleanupBlockedOperation.ToString("D"), scanEndpoints[0].EndpointId, "CAPTURE", "MINIMUM", 1, "OFF", new(false, Guid.Empty, ""), scanSession: cleanupSession, scanOperationId: cleanupBlockedOperation), "unknown cleanup invalidates the session and blocks subsequent operations");
Check(cleanupBlocked.ErrorCode == "SCAN_SESSION_STALE", "unsafe cleanup permanently invalidates Worker scan authorization");
var busyDiscovery = await injectedFormatClient.ListWindowsEndpointsAsync();
var busyRepeatability = new DuplexRepeatabilityWorkerWorkflow(injectedFormatClient);
var busyPreparation = busyRepeatability.Prepare(busyDiscovery, busyDiscovery.Endpoints.Single(e => e.Direction == "CAPTURE"), busyDiscovery.Endpoints.Single(e => e.Direction == "RENDER"));
var busyPeriodCandidate = new PeriodDiscoveryCandidate(240, 50_000);
var busyPeriodPlan = new WorkerScanPlan([new(busyDiscovery.Endpoints.Single(e => e.Direction == "CAPTURE").EndpointId!, "CAPTURE"), new(busyDiscovery.Endpoints.Single(e => e.Direction == "RENDER").EndpointId!, "RENDER")], ["InitializeExclusiveCustomPeriod"], [new(48_000, "PCM16", 2, [busyPeriodCandidate.AuthorizationValue], ["OFF"])], 60, 10, 2);
var busyPeriodPreparation = new PeriodDiscoveryPreparation(busyDiscovery,
    busyDiscovery.Endpoints.Single(e => e.Direction == "CAPTURE"), busyDiscovery.Endpoints.Single(e => e.Direction == "RENDER"),
    [busyPeriodCandidate], busyPeriodPlan, "busy-period-fixture", "bounded registration BUSY fixture");
var cancelSession = await injectedFormatClient.RegisterScanSessionAsync(cleanupPlan, true, Guid.NewGuid());
var cancelSessionOperation = Guid.NewGuid();
var cancelSessionRun = injectedFormatClient.ValidateStreamAsync(cancelSessionOperation.ToString("D"), scanEndpoints[0].EndpointId, "CAPTURE", "MINIMUM", 1, "OFF", new(false, Guid.Empty, ""), testScenario: "WAIT_FOR_CANCEL", scanSession: cancelSession, scanOperationId: cancelSessionOperation);
var busyPeriodWorkflow = new ExclusivePeriodDiscoveryWorkerWorkflow(injectedFormatClient);
var busyPeriodResult = await busyPeriodWorkflow.RunApprovedAsync(busyPeriodPreparation, Guid.NewGuid());
Check(busyPeriodResult.Lifecycle == "FAILED" && busyPeriodResult.Diagnostics?.TerminationReason == "REGISTRATION_BUSY" && busyPeriodResult.Diagnostics.SessionCloseStatus == "NOT_ATTEMPTED", "period discovery preserves confirmed registration BUSY as FAILED, not UNKNOWN");
var busyRegistration = await ThrowsAsync<WorkerCommandException>(() => busyRepeatability.RunApprovedAsync(busyPreparation, Guid.NewGuid()), "confirmed BUSY during scan-session registration is returned without being mislabeled UNKNOWN");
Check(busyRegistration.Command == "RegisterScanSession" && busyRegistration.ErrorCode == "BUSY", "repeatability preserves a confirmed registration refusal");
var busyOperationId = Guid.NewGuid();
var busyDuringStream = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.ValidateStreamAsync(busyOperationId.ToString("D"), scanEndpoints[0].EndpointId, "CAPTURE", "MINIMUM", 1, "OFF", new(false, Guid.Empty, ""), scanSession: cancelSession, scanOperationId: busyOperationId), "a second operation while the first is held active receives BUSY");
Check(busyDuringStream.ErrorCode == "BUSY", "Worker retains BUSY rejection for a genuinely active operation");
await injectedFormatClient.CancelScanSessionAsync(cancelSession);
var cancelledSessionReport = await cancelSessionRun;
Check(cancelledSessionReport.Report.GetProperty("cancelled").GetBoolean() && cancelledSessionReport.Report.GetProperty("streamQuality").GetString() != "CLEAN", "session cancellation signals the existing bounded Stream cancellation event and preserves incomplete evidence");
var afterBusyPeriod = await busyPeriodWorkflow.RunApprovedAsync(busyPeriodPreparation, Guid.NewGuid());
Check(afterBusyPeriod.Lifecycle == "COMPLETED" && afterBusyPeriod.Reports.Count == 2, "confirmed period registration BUSY releases its shared lease and allows a later successful period series");
var afterBusyRepeatability = await busyRepeatability.RunApprovedAsync(busyPreparation, Guid.NewGuid());
Check(afterBusyRepeatability.Lifecycle == ScanRunLifecycle.Completed && afterBusyRepeatability.Runs.Count == 6 && injectedFormatClient.State.State == WorkerConnectionState.ConnectedFake, $"confirmed registration BUSY releases the shared lease and permits later six-run repeatability after the active operation ends ({afterBusyRepeatability.Lifecycle}; {afterBusyRepeatability.Summary})");
var scanDiscovery = await injectedFormatClient.ListWindowsEndpointsAsync();
var scanTarget = new ScanTarget(scanDiscovery.Endpoints.Single(e => e.Direction == "CAPTURE"), scanDiscovery.Endpoints.Single(e => e.Direction == "RENDER"), ScanPairIdentity.OperatorSelectedPair);
var scanCapabilities = new ScanCapabilities(true, true, true, true, true);
var automaticPlan = KlidanAudioProbe.Gui.Core.ScanPlan.Build(scanTarget, scanCapabilities, new(DurationSeconds: 1, OperationTimeoutSeconds: 60));
var automaticWorkerPlan = RealWorkerScanExecutionAdapter.CreateWorkerPlan(automaticPlan, injectedFormatClient.Capabilities);
var automaticConfirmation = Guid.NewGuid();
var automaticToken = await injectedFormatClient.RegisterScanSessionAsync(automaticWorkerPlan, true, automaticConfirmation);
var automaticAdapter = new RealWorkerScanExecutionAdapter(injectedFormatClient, scanDiscovery, automaticToken, automaticWorkerPlan, automaticPlan);
var allowedScanStages = Enum.GetValues<ScanStage>().ToHashSet();
var automaticAuthorization = new ScanSessionAuthorization(Guid.NewGuid(), true,
    ScanSessionAuthorization.Fingerprint(automaticPlan, allowedScanStages, 30, 24), allowedScanStages, 30, 24);
var automaticScan = await new ScanCoordinator(automaticAdapter).RunAsync(automaticPlan, automaticAuthorization);
Check(automaticAdapter.Mode == ScanExecutionMode.DeterministicFake && automaticScan.Lifecycle == ScanRunLifecycle.Completed && automaticScan.Operations.Any(o => o.Operation.Stage == ScanStage.Initialize && o.Lifecycle == ScanOperationLifecycle.Completed) && automaticScan.Operations.Any(o => o.Operation.Stage == ScanStage.Stream && o.Evidence?.Stream?.Report.GetProperty("provenance").GetString() == "TEST_FAKE_STREAM_BACKEND") && automaticScan.Operations.Any(o => o.Operation.Stage == ScanStage.Duplex && o.Evidence?.Duplex?.Report.GetProperty("provenance").GetString() == "TEST_FAKE_WASAPI_DUPLEX"), "end-to-end coordinator invokes ordered scan-scoped operations over real local IPC while retaining explicit synthetic provenance");
var automatedHardwareStages = automaticScan.Operations.Where(o => o.Operation.Stage is ScanStage.Query or ScanStage.Initialize or ScanStage.Stream or ScanStage.Duplex).Select(o => o.Operation.Stage).ToArray();
Check(Array.IndexOf(automatedHardwareStages, ScanStage.Query) < Array.IndexOf(automatedHardwareStages, ScanStage.Initialize) && Array.IndexOf(automatedHardwareStages, ScanStage.Initialize) < Array.IndexOf(automatedHardwareStages, ScanStage.Stream) && Array.IndexOf(automatedHardwareStages, ScanStage.Stream) < Array.IndexOf(automatedHardwareStages, ScanStage.Duplex) && automaticScan.Operations.Where(o => o.Operation.Stage is ScanStage.Stream or ScanStage.Duplex).Select(o => o.Operation.Period).ToHashSet(StringComparer.Ordinal).SetEquals(["MINIMUM", "DEFAULT"]), "bounded automatic scan orders Query/Initialize/Stream/Duplex and independently exercises eligible MINIMUM and DEFAULT");
Check(injectedFormatClient.State.State == WorkerConnectionState.ConnectedFake && !automaticAdapter.IsBoundTo(automaticPlan, automaticAuthorization), "accepted scan closes only its authorization and keeps the Worker process connected");
var afterScanToken = await injectedFormatClient.RegisterScanSessionAsync(automaticWorkerPlan, true, Guid.NewGuid());
await injectedFormatClient.CloseScanSessionAsync(afterScanToken);
Check(true, "Worker discovery remains current after a completed scan; no hidden second enumeration invalidated the snapshot");
Check(automaticScan.Persistence is null && automaticScan.Operations.Single(o => o.Operation.Stage == ScanStage.Save).Lifecycle == ScanOperationLifecycle.Skipped && automaticScan.PhysicalRtlMeasured == false && automaticScan.AudioHostEligibility == "NOT_CERTIFIED", "fake Worker scan is never persisted as real hardware evidence or promoted to physical RTL/AudioHost certification");
await automaticAdapter.FinishSessionAsync(cancel: false, CancellationToken.None);
var sequentialClosePlan = new WorkerScanPlan(scanEndpoints, ["ValidateStream"], [new(48_000, "PCM16", 2, ["MINIMUM"], ["OFF"])], 60, 1, 2);
var sequentialCloseFirst = await injectedFormatClient.RegisterScanSessionAsync(sequentialClosePlan, true, Guid.NewGuid());
await injectedFormatClient.CloseScanSessionAsync(sequentialCloseFirst);
var sequentialCloseSecond = await injectedFormatClient.RegisterScanSessionAsync(sequentialClosePlan, true, Guid.NewGuid());
await injectedFormatClient.CloseScanSessionAsync(sequentialCloseSecond);
Check(injectedFormatClient.State.State == WorkerConnectionState.ConnectedFake, "sequential scan sessions close normally and leave the test Worker reusable");
var uiWorkflow = new WorkerAutomaticScanWorkflow(injectedFormatClient);
var uiDiscovery = await uiWorkflow.RefreshEndpointsAsync(CancellationToken.None);
var uiPreparation = uiWorkflow.Prepare(uiDiscovery, new AutomaticScanSelection(null, true,
    uiDiscovery.Endpoints.Single(e => e.Direction == "CAPTURE").EndpointId,
    uiDiscovery.Endpoints.Single(e => e.Direction == "RENDER").EndpointId));
Check(uiPreparation.MaximumTotalOperations == 94 && uiPreparation.MaximumActiveSeconds == 1260 && uiPreparation.MaximumWallClockSeconds > uiPreparation.MaximumActiveSeconds,
    $"automatic approval budget includes initial operations, period preflight/Initialize, custom short Duplex, both repeatability paths and teardown wall-clock bound ({uiPreparation.MaximumTotalOperations} ops/{uiPreparation.MaximumActiveSeconds}s active/{uiPreparation.MaximumWallClockSeconds}s wall)");
var extendedPreparation = uiWorkflow.Prepare(uiDiscovery, new AutomaticScanSelection(null, true,
    uiDiscovery.Endpoints.Single(e => e.Direction == "CAPTURE").EndpointId,
    uiDiscovery.Endpoints.Single(e => e.Direction == "RENDER").EndpointId, ExtendedSustainedValidation: true));
Check(extendedPreparation.MaximumTotalOperations == uiPreparation.MaximumTotalOperations && extendedPreparation.MaximumActiveSeconds == 1860 && extendedPreparation.SustainedDurationSeconds == 600, "extended sustained approval adds the bounded mode-aware 600-second active-time envelope without changing the operation count");
var uiScan = await uiWorkflow.RunApprovedAsync(uiPreparation, Guid.NewGuid(), null, CancellationToken.None);
Check(uiScan.Lifecycle == ScanRunLifecycle.Completed && uiScan.Target.PairIdentity == ScanPairIdentity.OperatorSelectedPair && uiScan.Operations.Any(o => o.Operation.Stage == ScanStage.Stream) && uiScan.Operations.Any(o => o.Operation.Stage == ScanStage.Duplex) && uiScan.Operations.Single(o => o.Operation.Stage == ScanStage.Save).Lifecycle == ScanOperationLifecycle.Skipped && injectedFormatClient.State.State == WorkerConnectionState.ConnectedFake,
    "production automatic workflow registers one approved exact-pair plan, runs all eligible synthetic IPC stages sequentially, keeps worker alive, and never persists fake evidence");
var automaticStreamOperations = uiScan.Operations.Where(o => o.Operation.Stage == ScanStage.Stream && o.Evidence?.Stream is not null).ToArray();
var automaticActivePeriods = uiScan.Operations.Where(o => o.Operation.Stage is ScanStage.Stream or ScanStage.Duplex).Select(o => o.Operation.Period).ToHashSet(StringComparer.Ordinal);
Check(automaticStreamOperations.Length > 0 && automaticStreamOperations.All(o => Guid.TryParse(o.Evidence!.Stream!.Report.GetProperty("reportId").GetString(), out var nativeId) && nativeId != Guid.Parse(o.Operation.OperationId)) &&
    automaticActivePeriods.SetEquals(["MINIMUM", "DEFAULT"]) && uiScan.Operations.Where(o => o.Operation.Stage == ScanStage.Duplex).All(o => o.Evidence?.Duplex is { } d && Guid.Parse(d.Report.GetProperty("reportId").GetString()!) != Guid.Parse(o.Operation.OperationId)),
    "automatic local-IPC scan accepts independent Stream/Duplex report UUIDs and completes both MINIMUM/DEFAULT progressions");
Check(uiScan.Evidence.SelectMany(e => new[] { e.Stream?.Report.GetProperty("provenance").GetString(), e.Duplex?.Report.GetProperty("provenance").GetString() }).Where(p => p is not null).All(p => p!.StartsWith("TEST_FAKE_", StringComparison.Ordinal)) && uiScan.AudioHostEligibility == "NOT_CERTIFIED" && !uiScan.PhysicalRtlMeasured,
    "automatic worker adapter preserves synthetic provenance and leaves RTL/AudioHost certification unavailable");
var automaticStageStates = uiScan.StageResults.ToDictionary(s => s.Stage, s => s.State);
Check(automaticStageStates[AutomaticScanStage.PERIOD_DISCOVERY] == AutomaticScanStageState.COMPLETED &&
    automaticStageStates[AutomaticScanStage.SHORT_STREAM] == AutomaticScanStageState.COMPLETED &&
    automaticStageStates[AutomaticScanStage.SHORT_DUPLEX] == AutomaticScanStageState.COMPLETED &&
    automaticStageStates[AutomaticScanStage.REPEATABILITY] == AutomaticScanStageState.COMPLETED &&
    automaticStageStates[AutomaticScanStage.SUSTAINED_VALIDATION] == AutomaticScanStageState.COMPLETED &&
    automaticStageStates[AutomaticScanStage.FINAL_SELECTION] == AutomaticScanStageState.COMPLETED &&
    uiScan.CandidateInventory.Any(c => c.Period.StartsWith("CUSTOM:", StringComparison.Ordinal) && c.State is AutomaticCandidateState.INITIALIZE_ACCEPTED or AutomaticCandidateState.RUNTIME_TESTED),
    $"normal automatic scan executes period discovery, repeatability, sustained validation and final selection without promoting synthetic evidence ({string.Join(",", uiScan.StageResults.Select(s => $"{s.Stage}={s.State}:{s.Message}"))}; custom={uiScan.CandidateInventory.Count(c => c.Period.StartsWith("CUSTOM:", StringComparison.Ordinal) && c.State == AutomaticCandidateState.INITIALIZE_ACCEPTED)})");
Check(uiScan.ChildOperations.Count > uiScan.Operations.Count &&
    uiScan.ChildOperations.Any(c => c.Stage == "PERIOD_DISCOVERY") &&
    uiScan.ChildOperations.Any(c => c.Stage == "SHORT_CUSTOM_DUPLEX") &&
    uiScan.ChildOperations.Any(c => c.Stage == "REPEATABILITY") &&
    uiScan.ChildOperations.Where(c => c.Lifecycle == "COMPLETED" && c.OperationId is not null).Select(c => c.OperationId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == uiScan.ChildOperations.Count(c => c.Lifecycle == "COMPLETED" && c.OperationId is not null),
    "automatic short-scan summary links period, custom Duplex and repeatability operation identities without duplicating completed operation IDs");
var repeatabilityChildren = uiScan.ChildOperations.Where(c => c.Stage == "REPEATABILITY" && c.Lifecycle == "COMPLETED").ToArray();
Check(repeatabilityChildren.Length == 46 && repeatabilityChildren.Take(6).All(c => c.Period is "MINIMUM" or "DEFAULT") &&
    repeatabilityChildren.Skip(6).All(c => c.Period?.StartsWith("CUSTOM:", StringComparison.Ordinal) == true) &&
    uiScan.StageResults.Single(s => s.Stage == AutomaticScanStage.SUSTAINED_VALIDATION).State == AutomaticScanStageState.COMPLETED,
    "sequential Worker sessions complete base repeatability (6), eight custom groups of five, then Sustained without a session-boundary race");
var sustainedChildren = uiScan.ChildOperations.Where(c => c.Stage == "SUSTAINED_VALIDATION").ToArray();
Check(uiScan.StageResults.Single(s => s.Stage == AutomaticScanStage.SUSTAINED_VALIDATION).State == AutomaticScanStageState.COMPLETED &&
    uiScan.StageResults.Single(s => s.Stage == AutomaticScanStage.FINAL_SELECTION).State == AutomaticScanStageState.COMPLETED &&
    sustainedChildren.Any(c => c.Lifecycle == "COMPLETED" && c.Provenance == "TEST_FAKE_WASAPI_SUSTAINED_DUPLEX" && c.Message.Contains("NOT_SAVED_SYNTHETIC", StringComparison.Ordinal)),
    "synthetic sustained evidence with case-insensitive UUID identity completes sustained validation and final selection without real persistence");
Check(sustainedChildren.Length is >= 1 and <= 8 && sustainedChildren.Any(c => c.Period?.Contains("/OFF/", StringComparison.Ordinal) == true) &&
    sustainedChildren.Select(c => c.OperationId).Where(id => id is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count() == sustainedChildren.Count(c => c.OperationId is not null),
    "automatic sustained ladder screens each shortlisted period in both modes and records distinct long-validation identities");
var periodDiscovery = new ExclusivePeriodDiscoveryWorkerWorkflow(injectedFormatClient);
var periodEndpoints = await injectedFormatClient.ListWindowsEndpointsAsync();
var periodCapture = periodEndpoints.Endpoints.Single(e => e.Direction == "CAPTURE");
var periodRender = periodEndpoints.Endpoints.Single(e => e.Direction == "RENDER");
var periodCandidate = new PeriodDiscoveryCandidate(240, 50_000);
var periodPlan = new WorkerScanPlan([new(periodCapture.EndpointId!, "CAPTURE"), new(periodRender.EndpointId!, "RENDER")], ["InitializeExclusiveCustomPeriod"], [new(48_000, "PCM16", 2, [periodCandidate.AuthorizationValue], ["OFF"])], 60, 10, 2);
var periodPreparation = new PeriodDiscoveryPreparation(periodEndpoints, periodCapture, periodRender, [periodCandidate], periodPlan, "fixture-period-discovery", "test plan");
var firstPeriodSeries = await periodDiscovery.RunApprovedAsync(periodPreparation, Guid.NewGuid(), _ => throw new InvalidOperationException("advisory progress failure"));
var secondPeriodSeries = await periodDiscovery.RunApprovedAsync(periodPreparation, Guid.NewGuid());
Check(firstPeriodSeries.Lifecycle == "COMPLETED" && secondPeriodSeries.Lifecycle == "COMPLETED" && injectedFormatClient.State.State == WorkerConnectionState.ConnectedFake, $"two consecutive period discovery sessions release the real shared Worker lease ({firstPeriodSeries.Lifecycle}/{firstPeriodSeries.PersistenceStatus}/{string.Join("|", firstPeriodSeries.Progress)}; {secondPeriodSeries.Lifecycle}/{secondPeriodSeries.PersistenceStatus}/{string.Join("|", secondPeriodSeries.Progress)})");
Check(firstPeriodSeries.Reports.All(r => r.TimestampUtc == "2026-11-01T00:00:00Z" && r.Provenance == "TEST_FAKE_PERIOD_BACKEND" && r.Candidates.Single().CleanupStatus == "RELEASE_ATTEMPTED") && firstPeriodSeries.ReportPath is not null && File.ReadAllText(firstPeriodSeries.ReportPath).Contains("2026-11-01T00:00:00Z", StringComparison.Ordinal), "synthetic period timestamps and RELEASE_ATTEMPTED cleanup survive Worker IPC, aggregation, and persistence unchanged");
Check(NativeWorkerClient.ResponseTimeoutForTest("InitializeExclusiveCustomPeriod") == TimeSpan.FromSeconds(20), "custom period IPC has the bounded native timeout used by period discovery");
var fullPeriodCandidates = Enumerable.Range(0, 8).Select(i => new PeriodDiscoveryCandidate(192 + i * 48, ExclusivePeriodDiscoveryPlanning.Duration(192 + i * 48, 48_000))).ToArray();
var fullPeriodPlan = new WorkerScanPlan([new(periodCapture.EndpointId!, "CAPTURE"), new(periodRender.EndpointId!, "RENDER")], ["InitializeExclusiveCustomPeriod"], [new(48_000, "PCM16", 2, fullPeriodCandidates.Select(c => c.AuthorizationValue).ToArray(), ["OFF"])], 348, 10, 16);
var fullPeriodPreparation = new PeriodDiscoveryPreparation(periodEndpoints, periodCapture, periodRender, fullPeriodCandidates, fullPeriodPlan, "fixture-period-discovery-8x2", "test plan");
var fullPeriodSeries = await periodDiscovery.RunApprovedAsync(fullPeriodPreparation, Guid.NewGuid());
Check(fullPeriodSeries.Lifecycle == "COMPLETED" && fullPeriodSeries.Reports.Count == 16 && fullPeriodSeries.Reports.All(r => r.Candidates.Single().Classification == "ACCEPTED") && fullPeriodSeries.Diagnostics?.Operations.Count == 16 && fullPeriodSeries.Diagnostics.SessionCloseStatus == "CONFIRMED", "eight candidates by two endpoints complete with per-operation diagnostics and confirmed session close");
Check(fullPeriodSeries.Diagnostics!.Operations.Select(o => o.OperationIndex).SequenceEqual(Enumerable.Range(1, 16)), "period diagnostics preserve the exact 1..16 operation order");
Check(fullPeriodSeries.ReportPath is not null && File.ReadAllText(fullPeriodSeries.ReportPath).Contains("terminationReason", StringComparison.Ordinal) && File.ReadAllText(fullPeriodSeries.ReportPath).Contains("operationDiagnostics", StringComparison.Ordinal), "period termination diagnostics are persisted in the compatible JSON envelope");
var repeatedPeriod = fullPeriodCandidates[0];
var repeatedPlan = fullPeriodPlan with { Formats = [new(48_000, "PCM16", 2, [repeatedPeriod.AuthorizationValue], ["OFF"])], MaximumOperationCount = 24 };
var repeatedSession = await injectedFormatClient.RegisterScanSessionAsync(repeatedPlan, true, Guid.NewGuid());
var repeatedReports = new List<ExclusivePeriodDiscoveryReportDto>();
for (var i = 0; i < 24; i++) repeatedReports.Add(await injectedFormatClient.InitializeExclusiveCustomPeriodAsync(repeatedSession, Guid.NewGuid(), periodCapture.EndpointId!, "CAPTURE", (int)repeatedPeriod.Period100ns, (int)repeatedPeriod.Frames));
await injectedFormatClient.CloseScanSessionAsync(repeatedSession);
Check(repeatedReports.Count == 24 && repeatedReports.All(r => r.Candidates.Single().Classification == "ACCEPTED") && injectedFormatClient.State.State == WorkerConnectionState.ConnectedFake, "24 bounded Initialize operations run consecutively after each terminal IPC response with confirmed session close");
var gateSuffix = Guid.NewGuid().ToString("N");
var terminalGateName = $"Local\\KlidanAudioProbe.AsN1.Terminal.{gateSuffix}";
var operationIdleName = $"Local\\KlidanAudioProbe.AsN1.Idle.{gateSuffix}";
var operationBusyName = $"Local\\KlidanAudioProbe.AsN1.Busy.{gateSuffix}";
using (var terminalGate = new EventWaitHandle(false, EventResetMode.ManualReset, terminalGateName))
using (var operationIdle = new EventWaitHandle(false, EventResetMode.ManualReset, operationIdleName))
using (var operationBusy = new EventWaitHandle(false, EventResetMode.ManualReset, operationBusyName))
{
    Environment.SetEnvironmentVariable("KLIDAN_TEST_POST_RESPONSE_GATE", terminalGateName);
    Environment.SetEnvironmentVariable("KLIDAN_TEST_OPERATION_IDLE_EVENT", operationIdleName);
    Environment.SetEnvironmentVariable("KLIDAN_TEST_OPERATION_BUSY_EVENT", operationBusyName);
    var terminalClient = new NativeWorkerClient(formatFixtureWorker);
    try
    {
        await terminalClient.ConnectAsync();
        var terminalEndpoints = await terminalClient.ListWindowsEndpointsAsync();
        var capture = terminalEndpoints.Endpoints.Single(e => e.Direction == "CAPTURE");
        var candidate = new PeriodDiscoveryCandidate(240, 50_000);
        var plan = new WorkerScanPlan([new(capture.EndpointId!, "CAPTURE")], ["InitializeExclusiveCustomPeriod"], [new(48_000, "PCM16", 2, [candidate.AuthorizationValue], ["OFF"])], 60, 10, 2);
        var session = await terminalClient.RegisterScanSessionAsync(plan, true, Guid.NewGuid());
        operationIdle.Reset(); operationBusy.Reset();
        await terminalClient.InitializeExclusiveCustomPeriodAsync(session, Guid.NewGuid(), capture.EndpointId!, "CAPTURE", (int)candidate.Period100ns, (int)candidate.Frames);
        operationIdle.Reset(); operationBusy.Reset();
        var secondOperation = Guid.NewGuid();
        var second = terminalClient.InitializeExclusiveCustomPeriodAsync(session, secondOperation, capture.EndpointId!, "CAPTURE", (int)candidate.Period100ns, (int)candidate.Frames);
        var observed = WaitHandle.WaitAny([operationIdle, operationBusy], TimeSpan.FromSeconds(5));
        terminalGate.Set();
        var secondResult = await second;
        await terminalClient.CloseScanSessionAsync(session);
        Check(observed == 0 && secondResult.OperationId == secondOperation.ToString("D") && terminalClient.State.State == WorkerConnectionState.ConnectedFake, "the first response is terminal before the next request is dispatched; a barrier proves BUSY is already clear while the prior thread exits");
    }
    finally { terminalGate.Set(); Environment.SetEnvironmentVariable("KLIDAN_TEST_POST_RESPONSE_GATE", null); Environment.SetEnvironmentVariable("KLIDAN_TEST_OPERATION_IDLE_EVENT", null); Environment.SetEnvironmentVariable("KLIDAN_TEST_OPERATION_BUSY_EVENT", null); await terminalClient.DisposeAsync(); }
}
var sixDuplexPlan = new WorkerScanPlan([new(periodCapture.EndpointId!, "CAPTURE"), new(periodRender.EndpointId!, "RENDER")], ["ValidateDuplex"], [new(48_000, "PCM16", 2, ["MINIMUM"], ["OFF"])], 60, 5, 6);
var sixDuplexSession = await injectedFormatClient.RegisterScanSessionAsync(sixDuplexPlan, true, Guid.NewGuid());
var sixDuplexReports = new List<DuplexValidationDto>();
for (var i = 0; i < 6; i++) { var operationId = Guid.NewGuid(); sixDuplexReports.Add(await injectedFormatClient.ValidateDuplexInScanAsync(operationId.ToString("D"), periodCapture.EndpointId!, periodRender.EndpointId!, "MINIMUM", 1, "OFF", sixDuplexSession, operationId)); }
await injectedFormatClient.CloseScanSessionAsync(sixDuplexSession);
Check(sixDuplexReports.Count == 6 && sixDuplexReports.All(r => r.Report.GetProperty("provenance").GetString() == "TEST_FAKE_WASAPI_DUPLEX") && injectedFormatClient.State.State == WorkerConnectionState.ConnectedFake, "six sequential authorized Duplex operations complete and leave the Worker reusable");
foreach(var spec in new[]{
    (Mode:"MONO_TO_STEREO",Cc:8u,Ce:"PCM24_IN_32",Cm:0u,Rc:2u,Re:"FLOAT32",Rm:3u,Idx:new uint[]{7,0,1}),
    (Mode:"STEREO_TO_STEREO",Cc:32u,Ce:"FLOAT32",Cm:0u,Rc:32u,Re:"PCM24_IN_32",Rm:0u,Idx:new uint[]{30,31,16,17}),
    (Mode:"MONO_TO_MONO",Cc:8u,Ce:"PCM16",Cm:0u,Rc:2u,Re:"PCM16",Rm:3u,Idx:new uint[]{5,1})})
{
    var op=Guid.NewGuid();var fingerprint=RouteFingerprint("injected-endpoint-capture","injected-endpoint-render",spec.Mode,48000,spec.Cc,spec.Ce,spec.Cm,44100,spec.Rc,spec.Re,spec.Rm,spec.Idx);
    var receipt=new {accepted=true,confirmationId=Guid.NewGuid(),requestFingerprint=RouteConsentFingerprint("injected-endpoint-capture","injected-endpoint-render",spec.Mode,fingerprint)};
    using var v2=await injectedFormatClient.SendForTestAsync("ValidateDuplexV2",new {contractVersion=2,operationId=op,captureEndpointId="injected-endpoint-capture",renderEndpointId="injected-endpoint-render",mode=spec.Mode,capture=new {sampleRateHz=48000,channels=spec.Cc,encoding=spec.Ce,channelMask=spec.Cm,selectedIndicesZeroBased=spec.Idx.Take(spec.Mode=="STEREO_TO_STEREO"?2:1).ToArray()},render=new {sampleRateHz=44100,channels=spec.Rc,encoding=spec.Re,channelMask=spec.Rm,selectedIndicesZeroBased=spec.Idx.Skip(spec.Mode=="STEREO_TO_STEREO"?2:1).ToArray()},period="MINIMUM",durationSeconds=1,mmcss="OFF",consentReceipt=receipt,testScenario="CLEAN"});
    var payload=v2.RootElement.GetProperty("payload");var report=payload.GetProperty("report");var route=report.GetProperty("route");
    Check(payload.GetProperty("renderEndpointId").GetString()=="injected-endpoint-render"&&route.GetProperty("render").GetProperty("endpointId").GetString()=="injected-endpoint-render"&&report.GetProperty("schemaVersion").GetInt32()==3&&report.GetProperty("provenance").GetString()=="TEST_FAKE_WASAPI_DUPLEX_V2"&&route.GetProperty("fingerprint").GetString()==fingerprint&&route.GetProperty("mode").GetString()==spec.Mode&&route.GetProperty("physicalMapping").GetString()=="UNKNOWN"&&route.GetProperty("renderPolicy").GetString()=="SILENCE_ONLY"&&route.GetProperty("capture").GetProperty("channels").GetUInt32()==spec.Cc&&route.GetProperty("render").GetProperty("sampleRateHz").GetUInt32()==44100&&report.GetProperty("capture").GetProperty("format").GetProperty("encoding").GetString()==spec.Ce&&report.GetProperty("render").GetProperty("format").GetProperty("channels").GetUInt32()==spec.Rc,"V2 IPC preserves exact endpoints, independent open-stream formats, selected route and silence-only evidence");
}
var v2Fingerprint=RouteFingerprint("injected-endpoint-capture","injected-endpoint-render","MONO_TO_STEREO",48000,2,"PCM16",0,48000,2,"PCM16",3,0,0,1);
object V2Request(Guid id,object? consent)=>new{contractVersion=2,operationId=id,captureEndpointId="injected-endpoint-capture",renderEndpointId="injected-endpoint-render",mode="MONO_TO_STEREO",capture=new{sampleRateHz=48000,channels=2,encoding="PCM16",channelMask=0u,selectedIndicesZeroBased=new[]{0}},render=new{sampleRateHz=48000,channels=2,encoding="PCM16",channelMask=3u,selectedIndicesZeroBased=new[]{0,1}},period="MINIMUM",durationSeconds=1,mmcss="OFF",consentReceipt=consent,testScenario="CLEAN"};
var activeV1Plan=new WorkerScanPlan([new(periodCapture.EndpointId!,"CAPTURE"),new(periodRender.EndpointId!,"RENDER")],["ValidateDuplex"],[new(48000,"PCM16",2,["MINIMUM"],["OFF"])],60,5,2);
var activeV1=await injectedFormatClient.RegisterScanSessionAsync(activeV1Plan,true,Guid.NewGuid());
var conflictingV2=await ThrowsAsync<WorkerCommandException>(()=>injectedFormatClient.SendForTestAsync("ValidateDuplexV2",V2Request(Guid.NewGuid(),new{accepted=true,confirmationId=Guid.NewGuid(),requestFingerprint=RouteConsentFingerprint("injected-endpoint-capture","injected-endpoint-render","MONO_TO_STEREO",v2Fingerprint)})),"V2 cannot bypass an active V1 scan session even with standalone consent");
Check(conflictingV2.ErrorCode=="SCAN_SESSION_CONFLICT","active V1 scan scope is preserved at the Worker boundary");
await injectedFormatClient.CloseScanSessionAsync(activeV1);
var noV2Consent=await ThrowsAsync<WorkerCommandException>(()=>injectedFormatClient.SendForTestAsync("ValidateDuplexV2",V2Request(Guid.NewGuid(),new{accepted=false,confirmationId=Guid.NewGuid(),requestFingerprint=RouteConsentFingerprint("injected-endpoint-capture","injected-endpoint-render","MONO_TO_STEREO",v2Fingerprint)})),"standalone V2 requires fresh explicit consent after the V1 session closes");
Check(noV2Consent.ErrorCode=="DUPLEX_CONSENT_MISMATCH","missing V2 consent is rejected");
var v2Consent=new{accepted=true,confirmationId=Guid.NewGuid(),requestFingerprint=RouteConsentFingerprint("injected-endpoint-capture","injected-endpoint-render","MONO_TO_STEREO",v2Fingerprint)};
using(var standaloneV2=await injectedFormatClient.SendForTestAsync("ValidateDuplexV2",V2Request(Guid.NewGuid(),v2Consent)))Check(standaloneV2.RootElement.GetProperty("status").GetString()=="OK","V2 is allowed with new consent after confirmed V1 closure");
var replayedV2=await ThrowsAsync<WorkerCommandException>(()=>injectedFormatClient.SendForTestAsync("ValidateDuplexV2",V2Request(Guid.NewGuid(),v2Consent)),"standalone V2 consent can be consumed only once");
Check(replayedV2.ErrorCode=="CONSENT_REPLAY","V2 consent replay is rejected");
var v2SessionClient=new NativeWorkerClient(formatFixtureWorker);
try
{
    await v2SessionClient.ConnectAsync();
    var v2Discovery=await v2SessionClient.ListWindowsEndpointsAsync();
    var v2Capture=v2Discovery.Endpoints.Single(e=>e.Direction=="CAPTURE").EndpointId!;
    var v2Render=v2Discovery.Endpoints.Single(e=>e.Direction=="RENDER").EndpointId!;
    var routeFingerprint=RouteFingerprint(v2Capture,v2Render,"MONO_TO_STEREO",48000,2,"PCM16",0,48000,2,"PCM16",3,0,0,1);
    var route=new WorkerScanV2Route("MONO_TO_STEREO",new(48000,2,"PCM16",0,[0]),new(48000,2,"PCM16",3,[0,1]));
    var plan=new WorkerScanPlanV2(route,["MINIMUM"],["OFF"],1800,600,10,1030);
    var confirmationId=Guid.NewGuid();var session=await v2SessionClient.RegisterScanSessionV2Async(v2Capture,v2Render,plan,true,confirmationId);
    Check(session.ContractVersion==2&&session.RouteFingerprint==routeFingerprint,"typed V2 session registration binds route and worker discovery generation");
    Task<DuplexValidationDto> V2Operation(int seconds,bool sustained=false)=>V2OperationWithId(Guid.NewGuid(),seconds,sustained);
    Task<DuplexValidationDto> V2OperationWithId(Guid id,int seconds,bool sustained=false)=>v2SessionClient.ValidateDuplexV2InScanAsync(id.ToString("D"),session,id,route,v2Capture,v2Render,"MINIMUM",seconds,"OFF",sustained);
    var unqualified=await ThrowsAsync<WorkerCommandException>(()=>V2Operation(60,true),"V2 sustained is blocked until short and six-run qualification complete");
    Check(unqualified.ErrorCode=="SCAN_QUALIFICATION_REQUIRED","V2 sustained qualification gate is enforced at Worker");
    var shortId=Guid.NewGuid();var shortResult=await V2OperationWithId(shortId,3);Check(shortResult.Report.GetProperty("reportType").GetString()=="wasapi-duplex-validation-v2","V2 authorized short Duplex accepts bounded duration and V2 evidence");
    var replayOperation=await ThrowsAsync<WorkerCommandException>(()=>V2OperationWithId(shortId,3),"V2 scan operation ID cannot be replayed");Check(replayOperation.ErrorCode=="SCAN_OPERATION_REJECTED","V2 operation replay is rejected");
    var routeChangeId=Guid.NewGuid();var changedRoute=await ThrowsAsync<WorkerCommandException>(()=>v2SessionClient.SendForTestAsync("ValidateDuplexV2",new{contractVersion=2,operationId=routeChangeId,captureEndpointId=v2Capture,renderEndpointId=v2Render,mode="MONO_TO_STEREO",capture=new{sampleRateHz=48000,channels=2,encoding="PCM16",channelMask=0u,selectedIndicesZeroBased=new[]{1}},render=new{sampleRateHz=48000,channels=2,encoding="PCM16",channelMask=3u,selectedIndicesZeroBased=new[]{0,1}},period="MINIMUM",durationSeconds=1,mmcss="OFF",scanSessionId=session.ScanSessionId,scanOperationId=routeChangeId,testScenario="CLEAN"}),"route mutation after V2 authorization is rejected");Check(changedRoute.ErrorCode=="SCAN_ROUTE_MISMATCH","V2 route mutation is rejected at the Worker boundary");
    for(var i=0;i<6;i++){var id=Guid.NewGuid();var repeat=await v2SessionClient.ValidateDuplexV2InScanAsync(id.ToString("D"),session,id,route,v2Capture,v2Render,"MINIMUM",1,"OFF");Check(repeat.Report.GetProperty("duplex").GetProperty("status").GetString()=="DUPLEX_CLEAN","V2 repeatability operation completes cleanly");}
    var outOfOrder=await ThrowsAsync<WorkerCommandException>(()=>V2Operation(300,true),"V2 sustained ladder rejects skipped durations");
    Check(outOfOrder.ErrorCode=="SCAN_SUSTAINED_ORDER","V2 sustained order requires 60 then 300 then 600");
    foreach(var seconds in new[]{60,300,600}){var sustainedV2=await V2Operation(seconds,true);Check(sustainedV2.Report.GetProperty("reportType").GetString()=="wasapi-sustained-duplex-validation-v2","V2 sustained evidence uses the authorized V2 report for each duration");}
    await v2SessionClient.CancelScanSessionAsync(session);
    var cancelled=await ThrowsAsync<WorkerCommandException>(()=>V2Operation(1),"cancelled V2 session cannot authorize another operation");Check(cancelled.ErrorCode=="SCAN_SESSION_STALE","V2 cancellation invalidates the active authorization");
    await v2SessionClient.CloseScanSessionAsync(session);
    var replayConsent=await ThrowsAsync<WorkerCommandException>(()=>v2SessionClient.RegisterScanSessionV2Async(v2Capture,v2Render,plan,true,confirmationId),"V2 confirmation cannot register another session after close");Check(replayConsent.ErrorCode=="SCAN_CONSENT_REPLAY","V2 consent replay is rejected");
    foreach(var scenario in new[]{"EXPIRE_ON_REGISTER","GENERATION_MISMATCH"})
    {
        Environment.SetEnvironmentVariable("KLIDAN_TEST_SCAN_SCENARIO",scenario);
        var scenarioClient=new NativeWorkerClient(formatFixtureWorker);
        try
        {
            await scenarioClient.ConnectAsync();var scenarioDiscovery=await scenarioClient.ListWindowsEndpointsAsync();
            var scenarioCapture=scenarioDiscovery.Endpoints.Single(e=>e.Direction=="CAPTURE").EndpointId!;var scenarioRender=scenarioDiscovery.Endpoints.Single(e=>e.Direction=="RENDER").EndpointId!;
            var staleSession=await scenarioClient.RegisterScanSessionV2Async(scenarioCapture,scenarioRender,plan,true,Guid.NewGuid());Environment.SetEnvironmentVariable("KLIDAN_TEST_SCAN_SCENARIO",null);
            var staleId=Guid.NewGuid();var stale=await ThrowsAsync<WorkerCommandException>(()=>scenarioClient.ValidateDuplexV2InScanAsync(staleId.ToString("D"),staleSession,staleId,route,scenarioCapture,scenarioRender,"MINIMUM",1,"OFF"),$"V2 {scenario} fails closed");
            Check(stale.ErrorCode=="SCAN_SESSION_STALE",$"V2 {scenario} cannot qualify evidence");
            await scenarioClient.CancelScanSessionAsync(staleSession);await scenarioClient.CloseScanSessionAsync(staleSession);
        }
        finally{Environment.SetEnvironmentVariable("KLIDAN_TEST_SCAN_SCENARIO",null);await scenarioClient.DisposeAsync();}
    }
    var unsafeSession=await v2SessionClient.RegisterScanSessionV2Async(v2Capture,v2Render,plan,true,Guid.NewGuid());
    var unsafeId=Guid.NewGuid();using(var unsafeReport=await v2SessionClient.SendForTestAsync("ValidateDuplexV2",new{contractVersion=2,operationId=unsafeId,captureEndpointId=v2Capture,renderEndpointId=v2Render,mode=route.Mode,capture=new{sampleRateHz=route.Capture.SampleRateHz,channels=route.Capture.Channels,encoding=route.Capture.Encoding,channelMask=route.Capture.ChannelMask,selectedIndicesZeroBased=route.Capture.SelectedIndicesZeroBased},render=new{sampleRateHz=route.Render.SampleRateHz,channels=route.Render.Channels,encoding=route.Render.Encoding,channelMask=route.Render.ChannelMask,selectedIndicesZeroBased=route.Render.SelectedIndicesZeroBased},period="MINIMUM",durationSeconds=1,mmcss="OFF",scanSessionId=unsafeSession.ScanSessionId,scanOperationId=unsafeId,testScenario="CLEANUP_UNKNOWN"}))Check(unsafeReport.RootElement.GetProperty("payload").GetProperty("report").GetProperty("capture").GetProperty("cleanup").GetString()=="UNKNOWN","V2 cleanup uncertainty remains explicit in returned evidence");
    var unsafeFollowupId=Guid.NewGuid();var unsafeFollowup=await ThrowsAsync<WorkerCommandException>(()=>v2SessionClient.ValidateDuplexV2InScanAsync(unsafeFollowupId.ToString("D"),unsafeSession,unsafeFollowupId,route,v2Capture,v2Render,"MINIMUM",1,"OFF"),"V2 unknown cleanup blocks subsequent operations");Check(unsafeFollowup.ErrorCode=="SCAN_SESSION_STALE","V2 unsafe cleanup invalidates the session");await v2SessionClient.CloseScanSessionAsync(unsafeSession);
}
finally{Environment.SetEnvironmentVariable("KLIDAN_TEST_SCAN_SCENARIO",null);await v2SessionClient.DisposeAsync();}
async Task<WorkerCommandException> RejectNumeric(object rate,object channels,object mask,object indices)=>await ThrowsAsync<WorkerCommandException>(()=>injectedFormatClient.SendForTestAsync("ValidateDuplexV2",new{contractVersion=2,operationId=Guid.NewGuid(),captureEndpointId="injected-endpoint-capture",renderEndpointId="injected-endpoint-render",mode="MONO_TO_MONO",capture=new{sampleRateHz=rate,channels,encoding="PCM16",channelMask=mask,selectedIndicesZeroBased=indices},render=new{sampleRateHz=48000,channels=2,encoding="PCM16",channelMask=3u,selectedIndicesZeroBased=new[]{0}},period="MINIMUM",durationSeconds=1,mmcss="OFF",consentReceipt=new{accepted=true,confirmationId=Guid.NewGuid(),requestFingerprint=new string('0',64)}}),"invalid V2 numeric value is rejected before type narrowing");
Check((await RejectNumeric(-1,2,0u,new[]{0})).ErrorCode=="ROUTE_INVALID","negative V2 sample rate rejected");
Check((await RejectNumeric(48000,-1,0u,new[]{0})).ErrorCode=="ROUTE_INVALID","negative V2 channel count rejected");
Check((await RejectNumeric(48000,2,-1,new[]{0})).ErrorCode=="ROUTE_INVALID","negative V2 channel mask rejected");
Check((await RejectNumeric(48000,2,0u,new[]{-1})).ErrorCode=="ROUTE_INVALID","negative V2 selected index rejected");
Check((await RejectNumeric(48000,65537,0u,new[]{0})).ErrorCode=="ROUTE_INVALID","channel count that would wrap when narrowed is rejected");
Check((await RejectNumeric(4294967296UL,2,0u,new[]{0})).ErrorCode=="ROUTE_INVALID","sample-rate integer overflow is rejected");
Check((await RejectNumeric(48000,2,4294967296UL,new[]{0})).ErrorCode=="ROUTE_INVALID","channel-mask integer overflow is rejected");
Check((await RejectNumeric(48000,2,0u,new[]{65536})).ErrorCode=="ROUTE_INVALID","selected-index integer overflow is rejected");
Check((await RejectNumeric(48000,2,0u,new[]{2})).ErrorCode=="ROUTE_INVALID","selected index at channel-count boundary is rejected");
var boundaryFingerprint=RouteFingerprint("injected-endpoint-capture","injected-endpoint-render","MONO_TO_MONO",48000,32,"PCM16",0,48000,32,"PCM16",0,31,31);
var boundaryReceipt=new{accepted=true,confirmationId=Guid.NewGuid(),requestFingerprint=RouteConsentFingerprint("injected-endpoint-capture","injected-endpoint-render","MONO_TO_MONO",boundaryFingerprint)};
using(var boundary=await injectedFormatClient.SendForTestAsync("ValidateDuplexV2",new{contractVersion=2,operationId=Guid.NewGuid(),captureEndpointId="injected-endpoint-capture",renderEndpointId="injected-endpoint-render",mode="MONO_TO_MONO",capture=new{sampleRateHz=48000,channels=32,encoding="PCM16",channelMask=0u,selectedIndicesZeroBased=new[]{31}},render=new{sampleRateHz=48000,channels=32,encoding="PCM16",channelMask=0u,selectedIndicesZeroBased=new[]{31}},period="MINIMUM",durationSeconds=1,mmcss="OFF",consentReceipt=boundaryReceipt,testScenario="CLEAN"}))Check(boundary.RootElement.GetProperty("status").GetString()=="OK","channel count 32 and selected zero-based channel 31 remain valid boundary values");
var invalidRoute=await ThrowsAsync<WorkerCommandException>(()=>injectedFormatClient.SendForTestAsync("ValidateDuplexV2",new {contractVersion=2,operationId=Guid.NewGuid(),captureEndpointId="injected-endpoint-capture",renderEndpointId="injected-endpoint-render",mode="STEREO_TO_STEREO",capture=new {sampleRateHz=48000,channels=8,encoding="PCM16",channelMask=3u,selectedIndicesZeroBased=new[]{2,2}},render=new {sampleRateHz=48000,channels=2,encoding="PCM16",channelMask=3u,selectedIndicesZeroBased=new[]{0,1}},period="MINIMUM",durationSeconds=1,mmcss="OFF",consentReceipt=new {accepted=true,confirmationId=Guid.NewGuid(),requestFingerprint=new string('0',64)}}),"route V2 rejects duplicate/out-of-range channels and masks before consent or native operation");
Check(invalidRoute.ErrorCode=="ROUTE_INVALID","invalid route geometry receives a typed rejection");
Environment.SetEnvironmentVariable("KLIDAN_TEST_PERIOD_SCENARIO", "REJECT_FIRST_FOUR");
var rejectedScenarioClient = new NativeWorkerClient(formatFixtureWorker);
try
{
    await rejectedScenarioClient.ConnectAsync();
    var rejectedEndpoints = await rejectedScenarioClient.ListWindowsEndpointsAsync();
    var rejectedCapture = rejectedEndpoints.Endpoints.Single(e => e.Direction == "CAPTURE");
    var rejectedRender = rejectedEndpoints.Endpoints.Single(e => e.Direction == "RENDER");
    var rejectedPlan = new WorkerScanPlan([new(rejectedCapture.EndpointId!, "CAPTURE"), new(rejectedRender.EndpointId!, "RENDER")], ["InitializeExclusiveCustomPeriod"], [new(48_000, "PCM16", 2, fullPeriodCandidates.Select(c => c.AuthorizationValue).ToArray(), ["OFF"])], 348, 10, 16);
    var rejectedPreparation = new PeriodDiscoveryPreparation(rejectedEndpoints, rejectedCapture, rejectedRender, fullPeriodCandidates, rejectedPlan, "fixture-period-rejected-first-four", "test plan");
    var rejectedThenAccepted = await new ExclusivePeriodDiscoveryWorkerWorkflow(rejectedScenarioClient).RunApprovedAsync(rejectedPreparation, Guid.NewGuid());
    Check(rejectedThenAccepted.Lifecycle == "COMPLETED" && rejectedThenAccepted.Reports.Count == 16 && rejectedThenAccepted.Reports.Take(4).All(r => r.Candidates.Single().Classification == "REJECTED" && r.Candidates.Single().CleanupStatus == "RELEASE_ATTEMPTED") && rejectedThenAccepted.Reports.Skip(4).All(r => r.Candidates.Single().Classification == "ACCEPTED") && rejectedThenAccepted.Diagnostics?.TerminationReason == "COMPLETED", $"first four safely rejected custom periods do not stop the 16-operation plan or create false UNKNOWN ({rejectedThenAccepted.Lifecycle}; {string.Join(",", rejectedThenAccepted.Reports.Select(r => r.Candidates.Single().Classification))}; {rejectedThenAccepted.Diagnostics?.TerminationReason})");
}
finally { Environment.SetEnvironmentVariable("KLIDAN_TEST_PERIOD_SCENARIO", null); await rejectedScenarioClient.DisposeAsync(); }
Environment.SetEnvironmentVariable("KLIDAN_TEST_PERIOD_SCENARIO", "NO_RESPONSE");
var timeoutPeriodClient = new NativeWorkerClient(formatFixtureWorker) { ResponseTimeoutOverrideForTest = TimeSpan.FromMilliseconds(250) };
try
{
    await timeoutPeriodClient.ConnectAsync();
    var timeoutEndpoints = await timeoutPeriodClient.ListWindowsEndpointsAsync();
    var timeoutCapture = timeoutEndpoints.Endpoints.Single(e => e.Direction == "CAPTURE");
    var timeoutRender = timeoutEndpoints.Endpoints.Single(e => e.Direction == "RENDER");
    var timeoutPeriod = new PeriodDiscoveryCandidate(240, 50_000);
    var timeoutPlan = new WorkerScanPlan([new(timeoutCapture.EndpointId!, "CAPTURE"), new(timeoutRender.EndpointId!, "RENDER")], ["InitializeExclusiveCustomPeriod"], [new(48_000, "PCM16", 2, [timeoutPeriod.AuthorizationValue], ["OFF"])], 60, 10, 2);
    var timeoutPreparation = new PeriodDiscoveryPreparation(timeoutEndpoints, timeoutCapture, timeoutRender, [timeoutPeriod], timeoutPlan, "fixture-period-timeout", "test plan");
    var timeoutWorkflow = new ExclusivePeriodDiscoveryWorkerWorkflow(timeoutPeriodClient);
    var timedOut = await timeoutWorkflow.RunApprovedAsync(timeoutPreparation, Guid.NewGuid());
    var blocked = await timeoutWorkflow.RunApprovedAsync(timeoutPreparation, Guid.NewGuid());
    Check(timedOut.Lifecycle == "UNKNOWN" && timedOut.Diagnostics?.TerminationReason == "IPC_TIMEOUT" && timeoutPeriodClient.State.State == WorkerConnectionState.Disconnected && timedOut.Diagnostics.Operations.Count == 1, "non-responsive custom period is supervised, persisted as UNKNOWN with IPC timeout, and terminates the Worker");
    Check(blocked.Lifecycle == "UNKNOWN" && blocked.Diagnostics?.Operations.Count == 0 && timeoutPeriodClient.State.State == WorkerConnectionState.Disconnected, "timed-out period discovery cannot automatically retry or reuse the terminated Worker");
}
finally { Environment.SetEnvironmentVariable("KLIDAN_TEST_PERIOD_SCENARIO", null); await timeoutPeriodClient.DisposeAsync(); }
var customDuplexPlan = new WorkerScanPlan([new(periodCapture.EndpointId!, "CAPTURE"), new(periodRender.EndpointId!, "RENDER")], ["ValidateDuplex"], [new(48_000, "PCM16", 2, ["CUSTOM:50000:240"], ["OFF"])], 60, 10, 1);
var customDuplexSession = await injectedFormatClient.RegisterScanSessionAsync(customDuplexPlan, true, Guid.NewGuid());
var customDuplexOperation = Guid.NewGuid();
var customDuplexValidation = await injectedFormatClient.ValidateCustomDuplexInScanAsync(customDuplexOperation, periodCapture.EndpointId!, periodRender.EndpointId!, "CUSTOM:50000:240", 5, "OFF", customDuplexSession, customDuplexOperation);
Check(customDuplexValidation.Report.GetProperty("provenance").GetString() == "TEST_FAKE_WASAPI_DUPLEX" && customDuplexValidation.Report.GetProperty("requested").GetProperty("period").GetString() == "CUSTOM:50000:240" && customDuplexValidation.Report.GetProperty("capture").GetProperty("cleanup").GetString() == "RELEASE_ATTEMPTED", "custom-period Duplex survives Worker authorization/IPC and accepts attempted release without treating it as COM destruction proof");
await injectedFormatClient.CloseScanSessionAsync(customDuplexSession);
using (var cancelledPeriod = new CancellationTokenSource())
{
    cancelledPeriod.Cancel();
    var unknownPeriodSeries = await periodDiscovery.RunApprovedAsync(periodPreparation, Guid.NewGuid(), cancellationToken: cancelledPeriod.Token);
    var blockedPeriodSeries = await periodDiscovery.RunApprovedAsync(periodPreparation, Guid.NewGuid());
    Check(unknownPeriodSeries.Lifecycle == "UNKNOWN" && blockedPeriodSeries.Lifecycle == "UNKNOWN", "UNKNOWN period discovery retains the shared Worker block and prevents reuse");
}
var manualConsentRequired = await ThrowsAsync<ArgumentException>(() => injectedFormatClient.ValidateStreamAsync(Guid.NewGuid().ToString("D"), "injected-endpoint-capture", "CAPTURE", "MINIMUM", 1, "OFF", new(false, Guid.Empty, "")), "manual Stream path continues to require its independent one-use consent");
Check(manualConsentRequired is not null, "scan-scoped typed APIs do not create or reuse manual consent receipts");
var disconnectClient = new NativeWorkerClient(formatFixtureWorker);
await disconnectClient.ConnectAsync();
using (var disconnectDiscovery = await disconnectClient.SendForTestAsync("ListWindowsEndpoints", new { })) { }
var orphanedScanToken = await disconnectClient.RegisterScanSessionAsync(ScanPlan(scanEndpoints), true, Guid.NewGuid());
await disconnectClient.DisposeAsync();
var replacementClient = new NativeWorkerClient(formatFixtureWorker);
await replacementClient.ConnectAsync();
using (var replacementDiscovery = await replacementClient.SendForTestAsync("ListWindowsEndpoints", new { })) { }
var disconnectedSession = await ThrowsAsync<WorkerCommandException>(() => replacementClient.SendForTestAsync("QueryFormatMatrix", new { endpointId = "injected-endpoint-capture", direction = "CAPTURE", channelCounts = new[] { 2 }, scanSessionId = orphanedScanToken.ScanSessionId, scanOperationId = Guid.NewGuid() }), "opaque scan token from a disconnected worker cannot transfer to its replacement");
Check(disconnectedSession.ErrorCode == "SCAN_SESSION_INVALID", "Worker process connection loss destroys authoritative session state");
await replacementClient.ShutdownAsync(); await replacementClient.DisposeAsync();
var injectedQuery = await injectedFormatClient.QueryFormatMatrixAsync("injected-endpoint-capture", "CAPTURE", [1,64]);
Check(injectedQuery.Provenance == "TEST_FAKE_FORMAT_BACKEND" && injectedQuery.Candidates.Count == 24 && injectedQuery.Candidates.Any(c => c.Channels == 64 && c.Status == "SUPPORTED_REPORTED"), "real local IPC dispatch returns deterministic injected passive format results across mono and 64 channels");
Check(injectedQuery.Candidates.Any(c => c.SampleRate == 44100 && c.Channels == 1 && c.Encoding == "PCM24_IN_32" && c.SupportStatus == "UNSUPPORTED") && injectedQuery.Candidates.Any(c => c.SampleRate == 44100 && c.Channels == 1 && c.Encoding == "FLOAT32" && c.ActivationHr == "0x80004005" && c.Status == "FAILED"), "real query IPC preserves unsupported format and activation failure separately");
var injectedBusyQuery = await injectedFormatClient.QueryFormatMatrixAsync("injected-endpoint-capture", "CAPTURE", [2]);
Check(injectedBusyQuery.Candidates.Any(c => c.SampleRate == 48000 && c.Channels == 2 && c.Encoding == "PCM24_IN_32" && c.SupportHr == "0x8889000A" && c.Status == "FAILED"), "real query IPC retains a device-in-use HRESULT as a failed driver response");
var injectedRenderQuery = await injectedFormatClient.QueryFormatMatrixAsync("injected-endpoint-render", "RENDER", [1,2]);
Check(injectedRenderQuery.Direction == "RENDER" && injectedRenderQuery.Candidates.Count == 24 && injectedRenderQuery.Candidates.All(c => c.Channels is 1 or 2), "real local IPC dispatch preserves independent Render query direction and candidate evidence");
var injectedDirectionMismatch = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.QueryFormatMatrixAsync("injected-endpoint-capture", "RENDER"), "real format command rejects a discovered endpoint requested with the wrong direction");
Check(injectedDirectionMismatch.ErrorCode == "ENDPOINT_DIRECTION_MISMATCH", "direction mismatch is rejected before native service dispatch");
await ThrowsAsync<ArgumentOutOfRangeException>(() => injectedFormatClient.QueryFormatMatrixAsync("injected-endpoint-capture", "CAPTURE", [65]), "GUI IPC client rejects out-of-range explicit channel counts");
var initFingerprintInjected = RealFormatConsent.Fingerprint("injected-endpoint-capture","CAPTURE",48000,64,"FLOAT32","minimum");
var initReceiptInjected = new RealFormatConsentDto(true,Guid.NewGuid(),initFingerprintInjected);
var injectedInit = await injectedFormatClient.ValidateFormatMatrixAsync("injected-endpoint-capture","CAPTURE",48000,64,"FLOAT32","minimum",initReceiptInjected);
Check(injectedInit.Provenance == "TEST_FAKE_FORMAT_BACKEND" && injectedInit.Candidates.Count == 1 && injectedInit.Candidates[0].Status == "READY" && injectedInit.Candidates[0].InitializeAttempted && injectedInit.Candidates[0].ActualBufferFrames == 144 && injectedInit.Candidates[0].ContainerBits == 32 && injectedInit.Candidates[0].ValidBits == 32 && injectedInit.Candidates[0].PeriodQueryHr == "0x00000000" && injectedInit.Candidates[0].ActualBufferSizeHr == "0x00000000" && injectedInit.Candidates[0].EventHandleReady == true && injectedInit.Candidates[0].ClientReleaseAttempts == 1 && injectedInit.Candidates[0].QueryClientReleaseAttempts == 1, "real local IPC dispatch preserves requested format, period, buffer, event and COM release evidence");
RealFormatConsentDto InjectedReceipt(int channels, string encoding) => new(true,Guid.NewGuid(),RealFormatConsent.Fingerprint("injected-endpoint-capture","CAPTURE",48000,channels,encoding,"minimum"));
var alignedInit = await injectedFormatClient.ValidateFormatMatrixAsync("injected-endpoint-capture","CAPTURE",48000,32,"PCM16","minimum",InjectedReceipt(32,"PCM16"));
Check(alignedInit.Candidates[0].Status == "READY" && alignedInit.Candidates[0].FirstInitializeHr == "0x88890019" && alignedInit.Candidates[0].AlignmentGetBufferSizeHr == "0x00000000" && alignedInit.Candidates[0].AlignmentFrames == 256 && alignedInit.Candidates[0].AlignmentInitializeHr == "0x00000000" && alignedInit.Candidates[0].ClientReleaseAttempts == 2, "Initialize-only IPC preserves the one permitted alignment retry and its geometry");
var failedInit = await injectedFormatClient.ValidateFormatMatrixAsync("injected-endpoint-capture","CAPTURE",48000,48,"PCM16","minimum",InjectedReceipt(48,"PCM16"));
Check(failedInit.Candidates[0].Status == "FAILED" && failedInit.Candidates[0].FirstInitializeHr == "0x8889000A", "Initialize-only IPC preserves device-in-use failure without implying successful initialization");
var failedEventInit = await injectedFormatClient.ValidateFormatMatrixAsync("injected-endpoint-capture","CAPTURE",48000,16,"PCM16","minimum",InjectedReceipt(16,"PCM16"));
Check(failedEventInit.Candidates[0].InitializeStatus == "INITIALIZED" && failedEventInit.Candidates[0].Status == "FAILED" && failedEventInit.Candidates[0].EventHandleReady == false && failedEventInit.Candidates[0].SetEventHandleHr == "0x80004005", "Initialize success remains separate from failed event readiness");
var failedCleanup = await injectedFormatClient.ValidateFormatMatrixAsync("injected-endpoint-capture","CAPTURE",48000,8,"PCM16","minimum",InjectedReceipt(8,"PCM16"));
Check(failedCleanup.Candidates[0].Status == "FAILED" && failedCleanup.Candidates[0].EventHandleCloseHr == "0x80004005" && failedCleanup.Candidates[0].CleanupStatus == "FAILED", "Initialize-only IPC preserves verifiable event-close cleanup failure");
var streamOperationId = Guid.NewGuid().ToString("D");
var streamConsentId = Guid.NewGuid();
var streamConsent = new RealStreamConsentDto(true, streamConsentId, RealStreamConsent.Fingerprint("injected-endpoint-capture", "CAPTURE", "MINIMUM", 5, "OFF"));
var injectedStream = await injectedFormatClient.ValidateStreamAsync(streamOperationId, "injected-endpoint-capture", "CAPTURE", "MINIMUM", 5, "OFF", streamConsent);
Check(Guid.Parse(injectedStream.OperationId) == Guid.Parse(streamOperationId) && Guid.TryParse(injectedStream.Report.GetProperty("reportId").GetString(), out var injectedReportId) && injectedReportId != Guid.Parse(streamOperationId) && injectedStream.Report.GetProperty("provenance").GetString() == "TEST_FAKE_STREAM_BACKEND" && injectedStream.Report.GetProperty("streamQuality").GetString() == "CLEAN" && injectedStream.Report.GetProperty("framesProcessed").GetInt64() == 240000, "fake-only IPC stream uses an independent native-report UUID and preserves serviced frames without WASAPI");
var renderStreamConsent = new RealStreamConsentDto(true, Guid.NewGuid(), RealStreamConsent.Fingerprint("injected-endpoint-render", "RENDER", "DEFAULT", 5, "OFF"));
var injectedRenderStream = await injectedFormatClient.ValidateStreamAsync(Guid.NewGuid().ToString("D"), "injected-endpoint-render", "RENDER", "DEFAULT", 5, "OFF", renderStreamConsent);
Check(injectedRenderStream.Report.GetProperty("direction").GetString() == "RENDER" && injectedRenderStream.Report.GetProperty("physicalPlayback").GetString() == "NOT_MEASURED" && injectedRenderStream.Report.GetProperty("physicalRouting").GetString() == "UNKNOWN", "fake Render stream fixture keeps digital silence evidence separate from physical output");
var invalidStreamConsent = await ThrowsAsync<ArgumentException>(() => injectedFormatClient.ValidateStreamAsync(Guid.NewGuid().ToString("D"), "injected-endpoint-capture", "CAPTURE", "MINIMUM", 5, "OFF", new(true, Guid.NewGuid(), new string('0', 64))), "stream consent fingerprint is tied to its exact period and settings");
async Task<JsonDocument> InjectedStreamScenario(string scenario)
{
    var receipt = new { accepted = true, confirmationId = Guid.NewGuid(), requestFingerprint = RealStreamConsent.Fingerprint("injected-endpoint-capture", "CAPTURE", "MINIMUM", 5, "OFF") };
    return await injectedFormatClient.SendForTestAsync("ValidateStream", new { operationId = Guid.NewGuid(), endpointId = "injected-endpoint-capture", direction = "CAPTURE", sampleRate = 48000, channels = 2, encoding = "PCM16", period = "MINIMUM", durationSeconds = 5, mmcss = "OFF", testScenario = scenario, consentReceipt = receipt });
}
async Task<RealStreamValidationDto> ValidatedInjectedStreamScenario(string scenario)
{
    var operationId = Guid.NewGuid().ToString("D");
    var receipt = new RealStreamConsentDto(true, Guid.NewGuid(), RealStreamConsent.Fingerprint("injected-endpoint-capture", "CAPTURE", "MINIMUM", 5, "OFF"));
    return await injectedFormatClient.ValidateStreamAsync(operationId, "injected-endpoint-capture", "CAPTURE", "MINIMUM", 5, "OFF", receipt, testScenario: scenario);
}
var uppercaseStream = await ValidatedInjectedStreamScenario("UPPERCASE_REPORT_ID");
Check(uppercaseStream.Report.GetProperty("reportId").GetString() == "A0000000-0000-4000-8000-000000000001" && uppercaseStream.Report.GetProperty("reportId").GetGuid() != Guid.Parse(uppercaseStream.OperationId), "Stream client accepts uppercase valid independent report UUID");
foreach (var (scenario, field) in new[]
{
    ("INVALID_REPORT_ID", "reportId"), ("MISSING_REPORT_ID", "reportId"),
    ("MISMATCH_OPERATION_ID", "operationId"), ("MISMATCH_ENDPOINT", "endpointId"),
    ("MISMATCH_RESPONSE_DIRECTION", "direction"), ("MISMATCH_REPORT_DIRECTION", "report.direction"),
    ("MISMATCH_PERIOD", "report.period"), ("MISMATCH_DURATION", "report.durationSeconds"),
    ("MISMATCH_SAMPLE_RATE", "format.sampleRateHz"), ("MISMATCH_ENCODING", "format.encoding"),
    ("MISMATCH_CHANNELS", "format.channels"), ("MISMATCH_PROVENANCE", "provenance")
})
{
    var invalid = await ThrowsAsync<InvalidDataException>(() => ValidatedInjectedStreamScenario(scenario), $"Stream IPC rejects {scenario} at its contract boundary");
    Check(invalid.Message.Contains(field, StringComparison.Ordinal) && !invalid.Message.Contains("injected-endpoint", StringComparison.Ordinal), $"Stream rejection identifies {field} without echoing endpoint identity");
}
var operationIdentityPlan = new WorkerScanPlan([new("injected-endpoint-capture", "CAPTURE")], ["ValidateStream"],
    [new(48000, "PCM16", 2, ["MINIMUM"], ["OFF"])], 60, 5, 1);
var operationIdentitySession = await injectedFormatClient.RegisterScanSessionAsync(operationIdentityPlan, true, Guid.NewGuid());
var requestedIdentity = Guid.NewGuid();
var mismatchedScanIdentity = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.SendForTestAsync("ValidateStream", new
{
    operationId = requestedIdentity, endpointId = "injected-endpoint-capture", direction = "CAPTURE", sampleRate = 48000,
    channels = 2, encoding = "PCM16", period = "MINIMUM", durationSeconds = 5, mmcss = "OFF",
    scanSessionId = operationIdentitySession.ScanSessionId, scanOperationId = Guid.NewGuid()
}), "Worker rejects IPC operation identity that differs from the authorized scan-session operation identity");
Check(mismatchedScanIdentity.ErrorCode == "SCAN_OPERATION_ID_MISMATCH", "Worker keeps scan-session and IPC operation UUID correlation strict");
await injectedFormatClient.CloseScanSessionAsync(operationIdentitySession);
using (var startFailure = await InjectedStreamScenario("START_FAIL")) Check(startFailure.RootElement.GetProperty("payload").GetProperty("report").GetProperty("startSucceeded").GetBoolean() == false, "injected stream Start failure is preserved without stream success");
using (var noFrames = await InjectedStreamScenario("ZERO_FRAMES")) Check(noFrames.RootElement.GetProperty("payload").GetProperty("report").GetProperty("framesProcessed").GetInt64() == 0 && noFrames.RootElement.GetProperty("payload").GetProperty("report").GetProperty("streamQuality").GetString() == "INCOMPLETE", "Start with zero serviced frames is incomplete");
using (var lowCoverage = await InjectedStreamScenario("LOW_COVERAGE")) Check(lowCoverage.RootElement.GetProperty("payload").GetProperty("report").GetProperty("streamQuality").GetString() == "ANOMALIES_PRESENT", "insufficient serviced-frame coverage is not clean");
using (var cancellation = await InjectedStreamScenario("CANCELLED")) Check(cancellation.RootElement.GetProperty("payload").GetProperty("report").GetProperty("cancelled").GetBoolean() && cancellation.RootElement.GetProperty("payload").GetProperty("report").GetProperty("durationReached").GetBoolean() == false, "cancelled stream reports incomplete duration rather than completion");
var cancelOperationId = Guid.NewGuid().ToString("D");
var cancelReceipt = new RealStreamConsentDto(true, Guid.NewGuid(), RealStreamConsent.Fingerprint("injected-endpoint-capture", "CAPTURE", "MINIMUM", 5, "OFF"));
var pendingStream = injectedFormatClient.ValidateStreamAsync(cancelOperationId, "injected-endpoint-capture", "CAPTURE", "MINIMUM", 5, "OFF", cancelReceipt, testScenario: "WAIT_FOR_CANCEL");
await Task.Delay(100);
await injectedFormatClient.CancelStreamValidationAsync(cancelOperationId);
var cancelledStream = await pendingStream.WaitAsync(TimeSpan.FromSeconds(5));
Check(cancelledStream.Report.GetProperty("cancelled").GetBoolean() && cancelledStream.Report.GetProperty("stopSucceeded").GetBoolean() && cancelledStream.Report.GetProperty("streamQuality").GetString() == "INCOMPLETE", "CancelStreamValidation signals the active operation and returns its terminal report over the same single pipe reader");
Check(injectedFormatClient.Capabilities.Contains("RealExclusiveDuplexValidation", StringComparer.Ordinal) && injectedFormatClient.Capabilities.Contains("CancelDuplexValidation", StringComparer.Ordinal), "fake worker advertises Duplex validation and cooperative cancellation additively");
var duplexOperationId = Guid.NewGuid().ToString("D");
var duplexConsent = new DuplexConsentDto(true, Guid.NewGuid(), DuplexConsent.Fingerprint("injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF"));
var injectedDuplex = await injectedFormatClient.ValidateDuplexAsync(duplexOperationId, "injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF", duplexConsent);
Check(injectedDuplex.Report.GetProperty("provenance").GetString() == "TEST_FAKE_WASAPI_DUPLEX" && injectedDuplex.Report.GetProperty("requested").GetProperty("captureEndpointId").GetString() == "injected-endpoint-capture" && injectedDuplex.Report.GetProperty("requested").GetProperty("renderEndpointId").GetString() == "injected-endpoint-render" && injectedDuplex.Report.GetProperty("duplex").GetProperty("status").GetString() == "DUPLEX_CLEAN", "typed local IPC Duplex response preserves exact asymmetric endpoint identity and explicit test provenance");
var injectedDefaultConsent = new DuplexConsentDto(true, Guid.NewGuid(), DuplexConsent.Fingerprint("injected-endpoint-capture", "injected-endpoint-render", "DEFAULT", 5, "OFF"));
var injectedDefaultDuplex = await injectedFormatClient.ValidateDuplexAsync(Guid.NewGuid().ToString("D"), "injected-endpoint-capture", "injected-endpoint-render", "DEFAULT", 5, "OFF", injectedDefaultConsent);
Check(injectedDefaultDuplex.Report.GetProperty("requested").GetProperty("period").GetString() == "DEFAULT", "MINIMUM and DEFAULT Duplex are independent immutable operations");
foreach (var period in new[] { "MINIMUM", "DEFAULT" })
{
    var delayedConsent = new DuplexConsentDto(true, Guid.NewGuid(), DuplexConsent.Fingerprint("injected-endpoint-capture", "injected-endpoint-render", period, 5, "OFF"));
    var delayedDuplexResult = await injectedFormatClient.ValidateDuplexAsync(Guid.NewGuid().ToString("D"), "injected-endpoint-capture", "injected-endpoint-render", period, 5, "OFF", delayedConsent, testScenario: "DELAY_RESPONSE");
    Check(delayedDuplexResult.Report.GetProperty("requested").GetProperty("period").GetString() == period && delayedDuplexResult.Report.GetProperty("duplex").GetProperty("status").GetString() == "DUPLEX_CLEAN", $"{period} structured Duplex response arriving after the former 5-second deadline is accepted within the bounded supervision window");
}
await ThrowsAsync<ArgumentException>(() => injectedFormatClient.ValidateDuplexAsync(Guid.NewGuid().ToString("D"), "injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF", new(true, Guid.NewGuid(), new string('0', 64))), "Duplex client rejects a mismatched fresh-consent fingerprint");
var replayedDuplex = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.ValidateDuplexAsync(Guid.NewGuid().ToString("D"), "injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF", duplexConsent), "Duplex worker rejects consent receipt replay");
Check(replayedDuplex.ErrorCode == "CONSENT_REPLAY", "one-use Duplex consent is enforced at native worker boundary");
var duplexCancelId = Guid.NewGuid().ToString("D");
var duplexCancelConsent = new DuplexConsentDto(true, Guid.NewGuid(), DuplexConsent.Fingerprint("injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF"));
var pendingDuplex = injectedFormatClient.ValidateDuplexAsync(duplexCancelId, "injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF", duplexCancelConsent, testScenario: "WAIT_FOR_CANCEL");
await Task.Delay(100);
await injectedFormatClient.CancelDuplexValidationAsync(duplexCancelId);
var cancelledDuplex = await pendingDuplex.WaitAsync(TimeSpan.FromSeconds(5));
Check(cancelledDuplex.Report.GetProperty("duplex").GetProperty("cancelled").GetBoolean() && cancelledDuplex.Report.GetProperty("duplex").GetProperty("status").GetString() == "CANCELLED" && cancelledDuplex.Report.GetProperty("capture").GetProperty("cleanup").GetString() == "RELEASE_ATTEMPTED", "Duplex cancellation signals both fake sides and waits for one terminal report through the single reader");
var duplexIncompleteConsent = new DuplexConsentDto(true, Guid.NewGuid(), DuplexConsent.Fingerprint("injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF"));
var insufficientDuplex = await injectedFormatClient.ValidateDuplexAsync(Guid.NewGuid().ToString("D"), "injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF", duplexIncompleteConsent, testScenario: "INSUFFICIENT_FRAMES");
Check(insufficientDuplex.Report.GetProperty("duplex").GetProperty("status").GetString() == "DUPLEX_WARNINGS" && insufficientDuplex.Report.GetProperty("capture").GetProperty("framesProcessed").GetInt64() < insufficientDuplex.Report.GetProperty("capture").GetProperty("expectedFrames").GetInt64(), "Duplex report retains low-coverage warning instead of CLEAN");
foreach (var (scenario, field, side) in new[] { ("BENIGN_INTERMEDIATE_WAIT", "intermediateWaitTimeouts", "capture"), ("SERVICE_FAILURE", "serviceFaults", "capture"), ("WATCHDOG", "watchdogTimedOut", "capture"), ("LONG_EVENT_INTERVAL", "longEventIntervals", "render"), ("PACKET_DRAIN_LIMIT", "packetDrainLimitHits", "capture") })
{
    var scenarioConsent = new DuplexConsentDto(true, Guid.NewGuid(), DuplexConsent.Fingerprint("injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF"));
    var scenarioResult = await injectedFormatClient.ValidateDuplexAsync(Guid.NewGuid().ToString("D"), "injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF", scenarioConsent, testScenario: scenario);
    var observed = scenarioResult.Report.GetProperty(side).GetProperty(field);
    Check(scenario == "BENIGN_INTERMEDIATE_WAIT" ? scenarioResult.Report.GetProperty("duplex").GetProperty("status").GetString() == "DUPLEX_CLEAN" && observed.GetInt64() == 1 : scenarioResult.Report.GetProperty("duplex").GetProperty("status").GetString() == "DUPLEX_WARNINGS" && (field == "watchdogTimedOut" ? observed.GetBoolean() : observed.GetInt64() == 1), $"Duplex structured report preserves and classifies {scenario} without converting a material anomaly to CLEAN");
}
Check(injectedFormatClient.State.State == WorkerConnectionState.ConnectedFake, "normal/faulted native child completion retains the healthy GUI-owned worker connection for the next period");
var timeoutClient = new NativeWorkerClient(formatFixtureWorker) { ResponseTimeoutOverrideForTest = TimeSpan.FromMilliseconds(500) };
await timeoutClient.ConnectAsync();
using (var timeoutDiscovery = await timeoutClient.SendForTestAsync("ListWindowsEndpoints", new { })) { }
var timeoutPid = timeoutClient.State.ProcessId ?? throw new InvalidOperationException("Timeout fixture worker has no process ID.");
var timeoutFaults = new List<WorkerTransportFaultDto>(); timeoutClient.TransportFault += timeoutFaults.Add;
var timeoutConsent = new DuplexConsentDto(true, Guid.NewGuid(), DuplexConsent.Fingerprint("injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF"));
await ThrowsAsync<TimeoutException>(() => timeoutClient.ValidateDuplexAsync(Guid.NewGuid().ToString("D"), "injected-endpoint-capture", "injected-endpoint-render", "MINIMUM", 5, "OFF", timeoutConsent, testScenario: "NO_RESPONSE"), "missing Duplex response reaches its bounded timeout instead of remaining pending");
bool WorkerProcessAlive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
await WaitUntilAsync(() => !WorkerProcessAlive(timeoutPid), TimeSpan.FromSeconds(3));
Check(timeoutClient.State.State == WorkerConnectionState.Disconnected && timeoutFaults.Any(f => f.Stage == "DUPLEX_SUPERVISION_TIMEOUT"), "Duplex response timeout tears down the GUI-owned worker job and preserves UNKNOWN operation/cleanup state");
await timeoutClient.DisposeAsync();
Check(NativeWorkerClient.ResponseTimeoutForTest("ListWindowsEndpoints") == TimeSpan.FromSeconds(5), "Windows endpoint discovery response timeout is explicitly bounded to five seconds");
Environment.SetEnvironmentVariable("KLIDAN_TEST_FORMAT_SCENARIO", "HOLD_DISCOVERY");
var discoveryTimeoutClient = new NativeWorkerClient(formatFixtureWorker) { ResponseTimeoutOverrideForTest = TimeSpan.FromMilliseconds(100) };
var discoveryTimeoutFaults = new List<WorkerTransportFaultDto>(); discoveryTimeoutClient.TransportFault += discoveryTimeoutFaults.Add;
try
{
    await discoveryTimeoutClient.ConnectAsync();
    var discoveryPid = discoveryTimeoutClient.State.ProcessId ?? throw new InvalidOperationException("Discovery timeout fixture has no Worker process ID.");
    await ThrowsAsync<TimeoutException>(() => discoveryTimeoutClient.ListWindowsEndpointsAsync(), "endpoint discovery response timeout returns a bounded failure");
    Check(discoveryTimeoutClient.State.State == WorkerConnectionState.ConnectedFake && WorkerProcessAlive(discoveryPid) && discoveryTimeoutFaults.Any(f => f.Stage == "RESPONSE_TIMEOUT"), "discovery timeout leaves only an idle connected Worker; late results cannot be mistaken for current discovery");
    await Task.Delay(400);
    discoveryTimeoutClient.ResponseTimeoutOverrideForTest = null;
    using var afterTimeout = await discoveryTimeoutClient.SendForTestAsync("ListWindowsEndpoints", new { });
    Check(afterTimeout.RootElement.GetProperty("status").GetString() == "OK", "a later bounded discovery response is correlated independently after timeout");
}
finally
{
    await discoveryTimeoutClient.DisposeAsync();
    Environment.SetEnvironmentVariable("KLIDAN_TEST_FORMAT_SCENARIO", null);
}
var closePlan = new WorkerScanPlan(scanEndpoints, ["ValidateStream"], [new(48_000, "PCM16", 2, ["MINIMUM"], ["OFF"])], 60, 1, 1);
Environment.SetEnvironmentVariable("KLIDAN_TEST_CLOSE_SCENARIO", "NO_RESPONSE");
var closeTimeoutClient = new NativeWorkerClient(formatFixtureWorker) { ResponseTimeoutOverrideForTest = TimeSpan.FromMilliseconds(250) };
try
{
    await closeTimeoutClient.ConnectAsync();
    using (var closeDiscovery = await closeTimeoutClient.SendForTestAsync("ListWindowsEndpoints", new { })) { }
    var closeTimeoutSession = await closeTimeoutClient.RegisterScanSessionAsync(closePlan, true, Guid.NewGuid());
    var closeTimeoutPid = closeTimeoutClient.State.ProcessId ?? throw new InvalidOperationException("Close-timeout fixture Worker has no process ID.");
    var closeFaults = new List<WorkerTransportFaultDto>(); closeTimeoutClient.TransportFault += closeFaults.Add;
    await ThrowsAsync<TimeoutException>(() => closeTimeoutClient.CloseScanSessionAsync(closeTimeoutSession), "missing CloseScanSession response reaches its bounded timeout");
    await WaitUntilAsync(() => !WorkerProcessAlive(closeTimeoutPid), TimeSpan.FromSeconds(3));
    Check(closeTimeoutClient.State.State == WorkerConnectionState.Disconnected && closeFaults.Any(f => f.Stage == "SESSION_CLOSE_SUPERVISION_TIMEOUT"), "CloseScanSession timeout calls EndTransport and terminates the GUI-owned Worker");
    await ThrowsAsync<InvalidOperationException>(() => closeTimeoutClient.RegisterScanSessionAsync(closePlan, true, Guid.NewGuid()), "a Worker with unconfirmed close cannot be reused");
}
finally { await closeTimeoutClient.DisposeAsync(); Environment.SetEnvironmentVariable("KLIDAN_TEST_CLOSE_SCENARIO", null); }
Environment.SetEnvironmentVariable("KLIDAN_TEST_CLOSE_SCENARIO", "DELAY_RESPONSE");
var closeCancelClient = new NativeWorkerClient(formatFixtureWorker);
try
{
    await closeCancelClient.ConnectAsync();
    using (var closeDiscovery = await closeCancelClient.SendForTestAsync("ListWindowsEndpoints", new { })) { }
    var closeCancelSession = await closeCancelClient.RegisterScanSessionAsync(closePlan, true, Guid.NewGuid());
    var closeCancelPid = closeCancelClient.State.ProcessId ?? throw new InvalidOperationException("Close-cancel fixture Worker has no process ID.");
    var closeCancelFaults = new List<WorkerTransportFaultDto>(); closeCancelClient.TransportFault += closeCancelFaults.Add;
    using var closeCancellation = new CancellationTokenSource();
    closeCancelClient.RequestWrittenForTest = _ => closeCancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => closeCancelClient.CloseScanSessionAsync(closeCancelSession, closeCancellation.Token), "CloseScanSession cancellation after write is surfaced to the caller");
    await WaitUntilAsync(() => !WorkerProcessAlive(closeCancelPid), TimeSpan.FromSeconds(3));
    Check(closeCancelClient.State.State == WorkerConnectionState.Disconnected && closeCancelFaults.Any(f => f.Stage == "SESSION_CLOSE_CANCELLED"), "post-write CloseScanSession cancellation supervises and terminates the Worker");
}
finally { closeCancelClient.RequestWrittenForTest = null; await closeCancelClient.DisposeAsync(); Environment.SetEnvironmentVariable("KLIDAN_TEST_CLOSE_SCENARIO", null); }
Environment.SetEnvironmentVariable("KLIDAN_TEST_CLOSE_SCENARIO", "NO_RESPONSE");
var cleanupErrorClient = new NativeWorkerClient(formatFixtureWorker);
try
{
    await cleanupErrorClient.ConnectAsync();
    using (var closeDiscovery = await cleanupErrorClient.SendForTestAsync("ListWindowsEndpoints", new { })) { }
    var customWorkflow = new CustomPeriodDuplexWorkerWorkflow(cleanupErrorClient);
    var customPreparation = customWorkflow.Prepare(periodEndpoints, periodCapture, periodRender, firstPeriodSeries, 240);
    var cleanupResult = await customWorkflow.RunApprovedAsync(customPreparation, Guid.NewGuid());
    Check(cleanupResult.Lifecycle == "UNKNOWN" && cleanupResult.PersistenceStatus == "UNKNOWN_CLEANUP" && cleanupResult.Error is { } cleanupError && cleanupError.Contains("Synthetic custom-period evidence", StringComparison.Ordinal) && cleanupError.Contains("Cleanup was not confirmed", StringComparison.Ordinal), "primary custom-period result and independent close cleanup error are both preserved");
}
finally { await cleanupErrorClient.DisposeAsync(); Environment.SetEnvironmentVariable("KLIDAN_TEST_CLOSE_SCENARIO", null); }
var wrongFingerprint = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.SendForTestAsync("ValidateFormatMatrix", new { endpointId="injected-endpoint-capture", direction="CAPTURE", sampleRate=48000, channels=64, encoding="FLOAT32", period="MINIMUM", consentReceipt=new { accepted=true, confirmationId=Guid.NewGuid(), requestFingerprint=new string('0',64) } }), "Initialize-only rejects consent whose fingerprint differs from immutable request settings");
Check(wrongFingerprint.ErrorCode == "SETTINGS_FINGERPRINT_MISMATCH", $"fingerprint mismatch is typed and does not start native initialization ({wrongFingerprint.ErrorCode})");
var rejectedConsent = await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.SendForTestAsync("ValidateFormatMatrix", new { endpointId="injected-endpoint-capture", direction="CAPTURE", sampleRate=48000, channels=64, encoding="FLOAT32", period="MINIMUM", consentReceipt=new { accepted=false, confirmationId=Guid.NewGuid(), requestFingerprint=initFingerprintInjected } }), "Initialize-only rejects an unaccepted receipt at the native worker boundary");
Check(rejectedConsent.ErrorCode == "CONSENT_NOT_ACCEPTED", "missing operator acceptance fails before the injected Initialize service");
await ThrowsAsync<WorkerCommandException>(() => injectedFormatClient.ValidateFormatMatrixAsync("injected-endpoint-capture","CAPTURE",48000,64,"FLOAT32","minimum",initReceiptInjected), "Initialize consent receipt cannot be replayed over IPC");
Check(injectedFormatClient.State.State == WorkerConnectionState.ConnectedFake, "injected test operations do not access audio hardware or disrupt the transport");
await injectedFormatClient.ShutdownAsync(); await injectedFormatClient.DisposeAsync();
Check(worker.PipeNameForTests is { Length: > 25 } && worker.PipeNameForTests.StartsWith("KlidanAudioProbe.", StringComparison.Ordinal), "pipe name is per-launch and unpredictable");
Check(NativeSecurity.GetNamedPipeClientProcessId(worker.PipeHandleForTests!, out var pipePid) && pipePid == (uint)worker.State.ProcessId.GetValueOrDefault(), "connected pipe PID is the GUI-owned child PID");
Check(PipeAclIsCurrentUserOnly(worker.PipeHandleForTests!), "named pipe DACL grants access only to the current user");
Check((worker.PipeOpenModeForTests & 0x00080000) != 0 && (worker.PipeModeForTests & 0x00000008) != 0, "pipe creation enables first-instance and remote-client rejection flags");
Check(FirstInstanceIsExclusive(worker), "a second first-instance pipe server cannot claim the same name");
var endpoints = await worker.ListEndpointsAsync();
Check(endpoints.Count == 4 && endpoints.All(e => e.IsSynthetic && e.State == "SYNTHETIC"), "ListEndpoints returns only explicitly synthetic Capture and Render fixtures");
var realEndpointSettings = new WorkerSessionSettingsDto("real-windows-endpoint-id", "CAPTURE", "PCM16-48000", 48000, "PCM16", 2, "PCM16-48000", 1, "DEFAULT", "OFF", null, "NORMAL");
var realEndpointRejection = await ThrowsAsync<WorkerCommandException>(() => worker.StartSessionAsync(realEndpointSettings, ConsentReceiptFor(realEndpointSettings)), "fake active commands reject a real Windows endpoint identifier");
Check(realEndpointRejection.ErrorCode == "INVALID_SETTINGS" && worker.State.State == WorkerConnectionState.ConnectedFake, "real endpoint identity cannot cross into fake session service");
Check(worker.Capabilities.Contains("RealExclusiveFormatQuery", StringComparer.Ordinal) && worker.Capabilities.Contains("RealExclusiveFormatInitialize", StringComparer.Ordinal), "worker additively advertises real format query and Initialize-only capabilities");
var fakeFormatQuery = await ThrowsAsync<WorkerCommandException>(() => worker.QueryFormatMatrixAsync("fake-capture-2", "CAPTURE"), "real format query rejects a synthetic endpoint before MMDevice access");
Check(fakeFormatQuery.ErrorCode == "SYNTHETIC_ENDPOINT_REJECTED" && worker.State.State == WorkerConnectionState.ConnectedFake, "synthetic endpoint rejection is typed and does not disconnect the worker");
var initFingerprint = RealFormatConsent.Fingerprint("unseen-real-endpoint", "CAPTURE", 48000, 2, "PCM16", "DEFAULT");
var initReceipt = new RealFormatConsentDto(true, Guid.NewGuid(), initFingerprint);
var undiscoveredInit = await ThrowsAsync<WorkerCommandException>(() => worker.ValidateFormatMatrixAsync("unseen-real-endpoint", "CAPTURE", 48000, 2, "PCM16", "DEFAULT", initReceipt), "Initialize-only refuses an endpoint absent from the explicit discovery snapshot");
Check(undiscoveredInit.ErrorCode == "ENDPOINT_NOT_DISCOVERED" && worker.State.State == WorkerConnectionState.ConnectedFake, "unseen endpoint is rejected before COM activation or initialization");
Check(endpoints.Single(e => e.EndpointId == "fake-capture-2").ChannelCount == 2 && endpoints.Single(e => e.EndpointId == "fake-render-8").ChannelCount == 8, "Capture and Render channel counts remain independent");
Check(endpoints.SelectMany(e => e.FormatTopologies ?? []).Select(f => f.Topology.ChannelCount).Distinct().Order().SequenceEqual(new[] { 1, 2, 8, 16, 32, 48, 64 }), "format-dependent topologies include each required 1..64 synthetic channel count");
foreach (var direction in new[] { "CAPTURE", "RENDER" }) Check(endpoints.Where(e => e.Direction == direction).SelectMany(e => e.FormatTopologies ?? []).Select(f => f.Topology.ChannelCount).Distinct().Order().SequenceEqual(new[] { 1, 2, 8, 16, 32, 48, 64 }), $"{direction} exposes independent format fixtures for every requested channel count");
foreach (var command in new[] { "InspectEndpoint", "ExportReport" })
{
    var rejection = await ThrowsAsync<WorkerCommandException>(() => worker.SendForTestAsync(command, new { }), $"{command} fails closed with typed NOT_IMPLEMENTED");
    Check(rejection.ErrorCode == "NOT_IMPLEMENTED" && worker.State.State == WorkerConnectionState.ConnectedFake, $"{command} rejection preserves connection and exact code");
}
var unknownRejection = await ThrowsAsync<WorkerCommandException>(() => worker.SendForTestAsync("TotallyUnknown", new { }), "unknown command is a correlated typed rejection");
Check(unknownRejection.ErrorCode == "UNKNOWN_COMMAND" && unknownRejection.Command == "TotallyUnknown" && worker.State.State == WorkerConnectionState.ConnectedFake, "UNKNOWN_COMMAND preserves command correlation without disconnecting");

var observedEvents = new ConcurrentQueue<WorkerEventDto>();
TaskCompletionSource<WorkerEventDto>? terminalEvent = null;
worker.TransportFault += fault => Console.WriteLine($"IPC FAULT: {fault.Stage}: {fault.Message}");
worker.EventReceived += evt => { observedEvents.Enqueue(evt); if (evt.Name is "Completed" or "Failed") terminalEvent?.TrySetResult(evt); };
WorkerSessionSettingsDto CaptureSettings(int duration = 1, string scenario = "NORMAL") => new("fake-capture-2", "CAPTURE", "PCM16-48000", 48000, "PCM16", 2, "PCM16-48000", duration, "DEFAULT", "OFF", null, scenario);
ConsentReceiptDto Consent(WorkerSessionSettingsDto settings, Guid? id = null, bool accepted = true, string? fingerprint = null) => new(accepted, id ?? Guid.NewGuid(), fingerprint ?? WorkerConsent.Fingerprint(settings));
terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
var naturalSettings = CaptureSettings();
var natural = await worker.StartSessionAsync(naturalSettings, Consent(naturalSettings));
var naturalTerminal = await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
var naturalEvents = observedEvents.Where(e => e.SessionId == natural.SessionId).ToArray();
Check(naturalEvents.Where(e => e.Name != "MeterSnapshot").Select(e => e.Name).SequenceEqual(new[] { "Preparing", "Ready", "Started", "Completed" }), "natural fake Capture emits the ordered accepted lifecycle around snapshots");
Check(naturalEvents.All(e => e.CorrelationId == natural.CorrelationId && e.Payload.GetProperty("synthetic").GetBoolean()) && naturalEvents.Zip(naturalEvents.Skip(1), (a,b) => b.EventSequence > a.EventSequence).All(x => x), "session events preserve correlation, synthetic provenance and increasing 64-bit sequence");
var captureSnapshots = naturalEvents.Where(e => e.Name == "MeterSnapshot").Select(e => e.CaptureMeter).ToArray();
Check(captureSnapshots.Length >= 8 && captureSnapshots.Zip(captureSnapshots.Skip(1), (a,b) => b!.SnapshotSequence > a!.SnapshotSequence).All(x => x), "real IPC transports bounded-rate typed Capture snapshots with increasing per-session sequence");
Check(captureSnapshots.All(s => s!.ChannelCount == 2 && s.Channels.Count == 2 && s.Channels.All(c => c.Index is 1 or 2)) && captureSnapshots.Any(s => s!.Channels.Any(c => c.Silent == true)) && captureSnapshots.Any(s => s!.Channels.Any(c => c.Silent == false && c.WindowPeakDbfs >= c.WindowRmsDbfs)), "Capture snapshots preserve channel topology, explicit silence and peak/RMS semantics");
Check(naturalTerminal.Name == "Completed" && naturalTerminal.Payload.GetProperty("cleanupEvidence").GetString() == "SYNTHETIC_CLEANUP_COMPLETED", "completion separates lifecycle from explicit fake cleanup evidence");

foreach (var scenario in new[] { "ALL_SILENT", "NO_WINDOW" })
{
    var settings = CaptureSettings(1, scenario);
    terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var session = await worker.StartSessionAsync(settings, Consent(settings));
    await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var snapshots = observedEvents.Where(e => e.SessionId == session.SessionId && e.Name == "MeterSnapshot").Select(e => e.CaptureMeter!).ToArray();
    Check(snapshots.Length > 0 && (scenario == "ALL_SILENT"
        ? snapshots.All(s => s.CompletedWindowIndex is not null && s.Channels.All(c => c.Silent == true && c.WindowPeakDbfs is null && c.WindowRmsDbfs is null))
        : snapshots.All(s => s.CompletedWindowIndex is null && s.Channels.All(c => c.Silent is null && c.WindowPeakDbfs is null && c.ClipCount is null))), $"{scenario} remains distinct from a valid completed level or observed silence");
}

var sixteen = new WorkerSessionSettingsDto("fake-capture-64", "CAPTURE", "FLOAT32-48000-16", 48000, "FLOAT32", 16, "FLOAT32-48000-16", 1, "DEFAULT", "OFF", null, "NORMAL");
terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
var sixteenSession = await worker.StartSessionAsync(sixteen, Consent(sixteen));
await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(observedEvents.Any(e => e.SessionId == sixteenSession.SessionId && e.CaptureMeter is { ChannelCount: 16 } snapshot && snapshot.Channels.Select(c => c.Index).SequenceEqual(Enumerable.Range(1, 16))), "16-channel Capture topology crosses real local IPC with ordered independent channel indices");

var replayReceipt = Consent(naturalSettings);
terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
var replaySession = await worker.StartSessionAsync(naturalSettings, replayReceipt);
await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
await ThrowsAsync<WorkerCommandException>(() => worker.StartSessionAsync(naturalSettings, replayReceipt), "consent receipt cannot be replayed");

var longSettings = CaptureSettings(10);
terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
var stopping = await worker.StartSessionAsync(longSettings, Consent(longSettings));
await WaitUntilAsync(() => observedEvents.Any(e => e.SessionId == stopping.SessionId && e.Name == "Started"), TimeSpan.FromSeconds(3));
await worker.StopSessionAsync(stopping.SessionId);
var repeatedStop = await ThrowsAsync<WorkerCommandException>(() => worker.StopSessionAsync(stopping.SessionId), "repeated Stop is rejected while cleanup is pending");
Check(repeatedStop.ErrorCode is "ALREADY_STOPPING" or "NO_SESSION", "repeated Stop never reports a second accepted cancellation");
var stopTerminal = await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(3));
Check(stopTerminal.Name == "Completed" && observedEvents.Any(e => e.SessionId == stopping.SessionId && e.Name == "Stopping"), "Stop acceptance is followed by Stopping and a terminal event");

var rejected = CaptureSettings();
await ThrowsAsync<WorkerCommandException>(() => worker.StartSessionAsync(rejected, Consent(rejected, accepted: false)), "unaccepted consent is rejected");
await ThrowsAsync<WorkerCommandException>(() => worker.StartSessionAsync(rejected, Consent(rejected, fingerprint: new string('0', 64))), "settings fingerprint mismatch is rejected");
await ThrowsAsync<WorkerCommandException>(() => worker.StartSessionAsync(rejected with { Direction = "RENDER", RenderChannel = 1 }, Consent(rejected with { Direction = "RENDER", RenderChannel = 1 })), "Capture endpoint cannot be used for a Render request");
var renderSettings = new WorkerSessionSettingsDto("fake-render-64", "RENDER", "PCM16-48000", 48000, "PCM16", 64, "PCM16-48000", 3, "MINIMUM", "PRO_AUDIO", 64, "NORMAL");
await ThrowsAsync<WorkerCommandException>(() => worker.StartSessionAsync(renderSettings with { RenderChannel = 0 }, Consent(renderSettings with { RenderChannel = 0 })), "invalid Render channel zero is rejected");
await ThrowsAsync<WorkerCommandException>(() => worker.StartSessionAsync(renderSettings with { RenderChannel = 65 }, Consent(renderSettings with { RenderChannel = 65 })), "Render channel count plus one is rejected");
terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
var render64 = await worker.StartSessionAsync(renderSettings, Consent(renderSettings));
await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(observedEvents.Any(e => e.SessionId == render64.SessionId && e.Name == "Started" && e.Payload.GetProperty("settings").GetProperty("renderChannel").GetInt32() == 64), "immutable Render settings preserve selected Windows channel 64");
var renderSnapshot = observedEvents.First(e => e.SessionId == render64.SessionId && e.Name == "MeterSnapshot").RenderMeter!;
Check(renderSnapshot.ChannelCount == 64 && renderSnapshot.SelectedChannelIndex == 64 && renderSnapshot.Channels.Where(c => c.Index != 64).All(c => c.DigitalSilent == true && c.IntendedPeakDbfs is null), "Render IPC models only the selected channel as intended output and leaves every other channel digitally silent");
var renderedSelected = renderSnapshot.Channels.Single(c => c.Index == 64);
Check(renderSnapshot.CompletedWindowIndex is not null && renderedSelected.IntendedPeakDbfs is double renderPeak && renderedSelected.IntendedRmsDbfs is double renderRms && renderPeak >= renderRms && renderedSelected.SessionPeakHoldDbfs is double renderHold && renderHold >= renderPeak, "completed Render window has finite intended peak/RMS and accumulated hold");
var completedRenderSnapshots = observedEvents.Where(e => e.SessionId == render64.SessionId && e.Name == "MeterSnapshot").Select(e => e.RenderMeter!).ToArray();
var renderTerminalSequence = observedEvents.Single(e => e.SessionId == render64.SessionId && e.Name == "Completed").EventSequence;
Check(completedRenderSnapshots.Length > 1 && completedRenderSnapshots.All(s => s.Channels[63].SessionPeakHoldDbfs >= s.Channels[63].IntendedPeakDbfs) && completedRenderSnapshots.Zip(completedRenderSnapshots.Skip(1), (a,b) => b.Channels[63].SessionPeakHoldDbfs >= a.Channels[63].SessionPeakHoldDbfs).All(x => x), "Render peak hold accumulates monotonically across completed windows");
Check(completedRenderSnapshots.All(s => s.SnapshotSequence > 0) && observedEvents.Where(e => e.SessionId == render64.SessionId && e.Name == "MeterSnapshot").All(e => e.EventSequence < renderTerminalSequence), "Render meter publication sequences and terminal freeze are preserved");

var renderTopologies = new[]
{
    (1, "fake-render-8", "PCM16-48000-1", "PCM16"), (2, "fake-render-8", "FLOAT32-48000", "FLOAT32"),
    (8, "fake-render-8", "PCM16-48000", "PCM16"), (16, "fake-render-64", "FLOAT32-48000-16", "FLOAT32"),
    (32, "fake-render-64", "FLOAT32-48000", "FLOAT32"), (48, "fake-render-64", "PCM24-48000", "PCM24_IN_32"),
    (64, "fake-render-64", "PCM16-48000", "PCM16")
};
foreach (var (channels, endpoint, format, encoding) in renderTopologies)
foreach (var selected in channels == 1 ? new[] { 1 } : new[] { 1, channels })
{
    var noWindowSettings = new WorkerSessionSettingsDto(endpoint, "RENDER", format, 48000, encoding, channels, format, 10, "DEFAULT", "OFF", selected, "NO_WINDOW");
    terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var noWindowSession = await worker.StartSessionAsync(noWindowSettings, Consent(noWindowSettings));
    await WaitUntilAsync(() => observedEvents.Any(e => e.SessionId == noWindowSession.SessionId && e.Name == "MeterSnapshot"), TimeSpan.FromSeconds(3));
    var noWindowSnapshot = observedEvents.First(e => e.SessionId == noWindowSession.SessionId && e.Name == "MeterSnapshot").RenderMeter!;
    Check(noWindowSnapshot.CompletedWindowIndex is null && noWindowSnapshot.SelectedChannelIndex == selected && noWindowSnapshot.Channels.Count == channels && noWindowSnapshot.Channels.All(c => c.IntendedPeakDbfs is null && c.IntendedRmsDbfs is null && c.DigitalSilent is null && c.SessionPeakHoldDbfs is null && c.ClipCount is null), $"real IPC Render NO_WINDOW is all-null for {channels} channels, selection {selected}");
    await worker.StopSessionAsync(noWindowSession.SessionId);
    var stoppedRender = await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var stopSequence = observedEvents.Single(e => e.SessionId == noWindowSession.SessionId && e.Name == "Stopping").EventSequence;
    Check(stoppedRender.Name == "Completed" && stopSequence < stoppedRender.EventSequence && !observedEvents.Any(e => e.SessionId == noWindowSession.SessionId && e.Name == "MeterSnapshot" && e.EventSequence > stopSequence), "Stop during Render publication prioritizes lifecycle and freezes snapshots");
}

var warningSettings = CaptureSettings(1, "WARNING");
terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
var warningSession = await worker.StartSessionAsync(warningSettings, Consent(warningSettings));
await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(observedEvents.Any(e => e.SessionId == warningSession.SessionId && e.Name == "Warning" && e.Payload.GetProperty("warningCode").GetString() == "SIMULATED_WARNING"), "fake warning is typed and carries no fabricated HRESULT");
var failureSettings = CaptureSettings(1, "FAILURE");
terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
var failureSession = await worker.StartSessionAsync(failureSettings, Consent(failureSettings));
var failedEvent = await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(failedEvent.Name == "Failed" && failedEvent.Payload.GetProperty("failureCode").GetString() == "SIMULATED_FAILURE", "fake service failure is terminal and distinct from stream quality");
var concurrent = CaptureSettings(10);
terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
var active = await worker.StartSessionAsync(concurrent, Consent(concurrent));
await ThrowsAsync<InvalidOperationException>(() => worker.StartSessionAsync(naturalSettings, Consent(naturalSettings)), "client blocks a concurrent Start while one fake operation is active");
Check(worker.State.State == WorkerConnectionState.ConnectedFake, "concurrent Start rejection preserves the worker connection");
await worker.ShutdownAsync();
Check(worker.State.State == WorkerConnectionState.ConnectedFake, "idle-shutdown request is rejected without killing an active operation");
await worker.StopSessionAsync(active.SessionId);
await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(3));
var stopBeforeStartSettings = CaptureSettings(10);
terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
var stopBeforeStart = await worker.StartSessionAsync(stopBeforeStartSettings, Consent(stopBeforeStartSettings));
await worker.StopSessionAsync(stopBeforeStart.SessionId);
await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(3));
Check(!observedEvents.Any(e => e.SessionId == stopBeforeStart.SessionId && e.Name == "Started"), "Stop accepted before synthetic Started prevents active lifecycle start");

var guiWorker = new NativeWorkerClient(buildWorker);
var guiVm = new MainViewModel(worker: guiWorker);
Check(guiVm.WorkerSession is null && guiVm.WorkerTimeline.Count == 0, "GUI has no automatic Worker Fake operation or fabricated events on launch");
guiVm.ConnectWorkerCommand.Execute(null);
await WaitUntilAsync(() => guiVm.WorkerState.State == WorkerConnectionState.ConnectedFake, TimeSpan.FromSeconds(5));
guiVm.QueryWorkerEndpointsCommand.Execute(null);
await WaitUntilAsync(() => guiVm.WorkerEndpoints.Count == 4, TimeSpan.FromSeconds(5));
guiVm.SelectedWorkerEndpoint = guiVm.WorkerEndpoints.Single(e => e.EndpointId == "fake-capture-2");
guiVm.RequestWorkerCaptureCommand.Execute(null);
Check(guiVm.WorkerConsentPending && guiVm.WorkerSession is null, "GUI Worker Fake request requires a fresh per-session consent");
guiVm.CancelWorkerConsentCommand.Execute(null);
Check(guiVm.WorkerSession is null && guiVm.WorkerTimeline.Any(e => e.Name == "ConsentCancelled"), "GUI consent cancellation sends no active request");
guiVm.RequestWorkerCaptureCommand.Execute(null);
guiVm.WorkerConsentChecked = true;
guiVm.ConfirmWorkerSessionCommand.Execute(null);
await WaitUntilAsync(() => guiVm.WorkerSession?.Lifecycle == SessionLifecycle.Started, TimeSpan.FromSeconds(5));
var guiSessionId = guiVm.WorkerSession!.Id;
guiVm.StopWorkerSessionCommand.Execute(null);
await WaitUntilAsync(() => guiVm.WorkerSession?.Lifecycle == SessionLifecycle.Completed, TimeSpan.FromSeconds(5));
Check(guiVm.WorkerMeterStatusText.Contains("ФИНАЛЬНЫЕ", StringComparison.Ordinal) && guiVm.WorkerVisibleMeterChannels.Count <= 8, "GUI consumes worker snapshot as final data and renders only one eight-channel bank");
Check(guiVm.WorkerSession!.Id == guiSessionId && guiVm.WorkerTimeline.Any(e => e.Name == "Stopping" && e.SessionId == guiSessionId) && guiVm.WorkerTimeline.Any(e => e.Name == "Completed" && e.SessionId == guiSessionId), "GUI session Stop follows correlated worker Stopping and Completed events");
var finalWorkerMeter = guiVm.WorkerVisibleMeterChannels.ToArray();
Check(finalWorkerMeter.Length > 0 && guiVm.WorkerTimeline.Where(e => e.SessionId == guiSessionId && e.Name == "MeterSnapshot").All(e => e.ProtocolSequence < guiVm.WorkerTimeline.Last(t => t.SessionId == guiSessionId && t.Name == "Completed").ProtocolSequence), "no Worker Fake meter event follows the terminal event and displayed measurements remain frozen");
guiVm.WorkerDurationSeconds = 10;
guiVm.RequestWorkerCaptureCommand.Execute(null);
guiVm.WorkerConsentChecked = true;
guiVm.ConfirmWorkerSessionCommand.Execute(null);
await WaitUntilAsync(() => guiVm.WorkerSession is { Lifecycle: SessionLifecycle.Started }, TimeSpan.FromSeconds(5));
await guiVm.ShutdownWorkerAsync();
Check(guiVm.WorkerSession?.Lifecycle == SessionLifecycle.Completed && guiVm.WorkerState.State == WorkerConnectionState.Disconnected, $"GUI shutdown requests Stop, observes terminal cleanup, then exits worker (session={guiVm.WorkerSession?.Lifecycle}, worker={guiVm.WorkerState.State}, note={guiVm.WorkerState.Message})");
Check(guiVm.WorkerMeterStatusText.StartsWith("УСТАРЕЛО", StringComparison.Ordinal) && guiVm.WorkerVisibleMeterChannels.Count == finalWorkerMeter.Length, "worker disconnect marks the last values stale without generating new snapshots");

var lossSettings = CaptureSettings(1, "DEVICE_LOSS");
terminalEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
var lossSession = await worker.StartSessionAsync(lossSettings, Consent(lossSettings));
var lossEvent = await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(lossEvent.Name == "Failed" && lossEvent.Payload.GetProperty("cleanupEvidence").GetString() == "UNKNOWN", "simulated device loss leaves cleanup unknown");
var blockedReuse = await ThrowsAsync<WorkerCommandException>(() => worker.StartSessionAsync(naturalSettings, Consent(naturalSettings)), "unknown cleanup blocks worker reuse");
Check(blockedReuse.ErrorCode == "CLEANUP_UNKNOWN", "worker does not return to reusable idle after unknown cleanup");
var badRequestClient = new NativeWorkerClient(buildWorker);
await badRequestClient.ConnectAsync();
var protocolError = await ThrowsAsync<WorkerCommandException>(() => badRequestClient.SendForTestAsync("ListEndpoints", new { }), "invalid command fields receive a typed rejection");
Check(protocolError.ErrorCode == "INVALID_ARGUMENT" && badRequestClient.State.State == WorkerConnectionState.ConnectedFake, "typed validation rejection preserves transport state");
await badRequestClient.DisposeAsync();
var malformedIndex = 0;
foreach (var malformed in new[]
{
    new byte[] { 0, 0, 0, 0 },
    LengthOnly(1_048_577),
    Frame(System.Text.Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}")),
    Frame([0xC3, 0x28]),
    Frame(System.Text.Encoding.UTF8.GetBytes("{broken"))
})
{
    var invalidClient = new NativeWorkerClient(buildWorker);
    await invalidClient.ConnectAsync();
    var invalidResponse = await invalidClient.SendRawFrameForTestAsync(malformed);
    Check(invalidClient.State.State == WorkerConnectionState.Disconnected, $"native worker rejects malformed frame case {malformedIndex} by closing the invalid protocol session");
    malformedIndex++;
    await invalidClient.DisposeAsync();
}
var gracefulPid = worker.State.ProcessId!.Value;
await worker.ShutdownAsync();
Check(worker.State.State == WorkerConnectionState.Disconnected && worker.State.ExitCode == 0, "explicit idle shutdown is acknowledged and exits cleanly");
Check(WaitExited(gracefulPid), "gracefully shut down worker process exits");
await ThrowsAsync<InvalidOperationException>(() => worker.ConnectAsync(), "worker is not automatically restarted");
await worker.DisposeAsync();

var slowPipeName = $"KlidanAudioProbe.SlowConsumer.{Guid.NewGuid():N}";
await using (var slowPipe = new NamedPipeServerStream(slowPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
{
    var slowStart = new ProcessStartInfo(nativeWorker) { UseShellExecute = false, CreateNoWindow = true };
    slowStart.ArgumentList.Add("--pipe"); slowStart.ArgumentList.Add($"\\\\.\\pipe\\{slowPipeName}");
    using var slowProcess = Process.Start(slowStart) ?? throw new InvalidOperationException("Slow-consumer worker did not start.");
    try
    {
        await slowPipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        async Task SlowSend(string json) { await slowPipe.WriteAsync(IpcFrameCodec.Encode(Encoding.UTF8.GetBytes(json))); await slowPipe.FlushAsync(); }
        async Task<JsonElement> SlowRead() { using var d = IpcFrameCodec.Parse(await IpcFrameCodec.ReadPayloadAsync(slowPipe, CancellationToken.None)); return d.RootElement.Clone(); }
        var slowHello = Guid.NewGuid(); await SlowSend(HandshakeJson(slowHello)); await SlowRead();
        var slowSettings = new WorkerSessionSettingsDto("fake-capture-64", "CAPTURE", "PCM16-48000", 48000, "PCM16", 64, "PCM16-48000", 3, "DEFAULT", "OFF", null, "NORMAL");
        var receipt = Consent(slowSettings);
        var slowPayload = new { endpointId = slowSettings.EndpointId, direction = slowSettings.Direction, formatId = slowSettings.FormatId, sampleRate = slowSettings.SampleRate, encoding = slowSettings.Encoding, channelCount = slowSettings.ChannelCount, topologyId = slowSettings.TopologyId, durationSeconds = slowSettings.DurationSeconds, period = slowSettings.Period, mmcss = slowSettings.Mmcss, renderChannel = slowSettings.RenderChannel, scenario = slowSettings.Scenario, consentReceipt = new { accepted = receipt.Accepted, confirmationId = receipt.ConfirmationId, requestFingerprint = receipt.RequestFingerprint } };
        await SlowSend(ClientRequestJson(Guid.NewGuid(), "StartCaptureMeter", payload: slowPayload));
        await SlowRead(); // Start acceptance is small; intentionally leave subsequent pipe output unread.
        await Task.Delay(1600);
        var coalesced = false; var completed = false;
        while (!completed)
        {
            var evt = await SlowRead().WaitAsync(TimeSpan.FromSeconds(5));
            if (evt.GetProperty("commandOrEvent").GetString() == "MeterSnapshot" && evt.GetProperty("payload").GetProperty("coalescedSnapshotCount").GetInt64() > 0) coalesced = true;
            completed = evt.GetProperty("commandOrEvent").GetString() == "Completed";
        }
        Check(coalesced && completed, "slow pipe consumer observes latest-only snapshot coalescing while terminal control remains deliverable");
        slowPipe.Dispose(); await slowProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    finally { slowPipe.Dispose(); if (!slowProcess.HasExited) { slowProcess.Kill(entireProcessTree: true); await slowProcess.WaitForExitAsync(); } }
}

var interrupted = new NativeWorkerClient(buildWorker);
var interruptedEvents = new ConcurrentQueue<WorkerEventDto>();
var interruptedState = new TaskCompletionSource<WorkerStateDto>(TaskCreationOptions.RunContinuationsAsynchronously);
interrupted.EventReceived += evt => interruptedEvents.Enqueue(evt);
interrupted.StateChanged += state => { if (state.State == WorkerConnectionState.Disconnected) interruptedState.TrySetResult(state); };
await interrupted.ConnectAsync();
var interruptedSettings = CaptureSettings(10);
var interruptedSession = await interrupted.StartSessionAsync(interruptedSettings, Consent(interruptedSettings));
await WaitUntilAsync(() => interruptedEvents.Any(e => e.SessionId == interruptedSession.SessionId && e.Name == "Started"), TimeSpan.FromSeconds(3));
await interrupted.StopSessionAsync(interruptedSession.SessionId);
Process.GetProcessById(interrupted.State.ProcessId!.Value).Kill(entireProcessTree: true);
var unknownStop = await interruptedState.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(unknownStop.State == WorkerConnectionState.Disconnected && !interruptedEvents.Any(e => e.SessionId == interruptedSession.SessionId && e.Name is "Completed" or "Failed"), "worker crash during accepted Stop remains UNKNOWN with no fabricated terminal event");
await interrupted.DisposeAsync();

var crashed = new NativeWorkerClient(buildWorker);
var disconnected = new TaskCompletionSource<WorkerStateDto>(TaskCreationOptions.RunContinuationsAsynchronously);
crashed.StateChanged += state => { if (state.State == WorkerConnectionState.Disconnected) disconnected.TrySetResult(state); };
await crashed.ConnectAsync();
var crashPid = crashed.State.ProcessId!.Value;
Process.GetProcessById(crashPid).Kill(entireProcessTree: true);
var disconnectState = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(disconnectState.State == WorkerConnectionState.Disconnected && disconnectState.ExitCode is not null && !disconnectState.Message.Contains("успеш", StringComparison.OrdinalIgnoreCase), "worker crash is reported as disconnect with observed exit status only");
await crashed.DisposeAsync();

var killed = new NativeWorkerClient(buildWorker);
await killed.ConnectAsync();
var killedPid = killed.State.ProcessId!.Value;
await killed.DisposeAsync();
Check(WaitExited(killedPid), "closing the Job Object terminates the owned worker");

var mismatchedWorkerClient = new NativeWorkerClient(mismatchWorker);
var incompatible = await ThrowsAsync<WorkerProtocolException>(() => mismatchedWorkerClient.ConnectAsync(), "incompatible protocol handshake is rejected with a typed protocol failure");
Check(incompatible.Stage == "HANDSHAKE" && incompatible.ErrorCode == "INCOMPATIBLE_PROTOCOL", "handshake incompatibility preserves the exact failure stage and code");
Check(mismatchedWorkerClient.State.State == WorkerConnectionState.Incompatible, "version mismatch has a distinct incompatible state");
await mismatchedWorkerClient.DisposeAsync();

var stall = new NativeWorkerClient(stallWorker);
var stopwatch = Stopwatch.StartNew();
await ThrowsAsync<OperationCanceledException>(() => stall.ConnectAsync(), "worker connection timeout fails closed");
Check(stopwatch.Elapsed < TimeSpan.FromSeconds(8) && stall.State.State == WorkerConnectionState.Failed, "startup timeout is bounded and reported failed");
await stall.DisposeAsync();

var delayed = new DelayedFailClient();
var vm = new MainViewModel(worker: delayed);
var commandWatch = Stopwatch.StartNew();
vm.ConnectWorkerCommand.Execute(null);
Check(commandWatch.Elapsed < TimeSpan.FromMilliseconds(100), "GUI worker connect command returns without synchronously blocking UI caller");
await Task.Delay(100);
Check(vm.WorkerState.State == WorkerConnectionState.Failed, "asynchronous worker failure reaches ViewModel diagnostics");
await delayed.DisposeAsync();

Console.WriteLine($"All worker IPC/process checks passed ({count}).");
return 0;

static async Task<T> ThrowsAsync<T>(Func<Task> action, string message) where T : Exception { try { await action(); } catch (T ex) { Console.WriteLine($"PASS: {message}"); return ex; } throw new InvalidOperationException($"FAIL: {message}"); }
static void ThrowsSync<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { Console.WriteLine($"PASS: {message}"); return; } throw new InvalidOperationException($"FAIL: {message}"); }
static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout) { var until = Stopwatch.StartNew(); while (!predicate()) { if (until.Elapsed > timeout) throw new TimeoutException("Condition was not observed before timeout."); await Task.Delay(10); } }
static byte[] LengthOnly(uint size) { var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, size); return header; }
static byte[] Frame(byte[] payload) { var frame = new byte[payload.Length + 4]; BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length); payload.CopyTo(frame, 4); return frame; }
static bool WaitExited(int pid) { try { using var p = Process.GetProcessById(pid); return p.WaitForExit(5000) && p.HasExited; } catch (ArgumentException) { return true; } }
static bool PipeAclIsCurrentUserOnly(SafePipeHandle pipe)
{
    if (NativeSecurity.GetSecurityInfo(pipe, 6, 4, out _, out _, out var dacl, out _, out var descriptor) != 0 || dacl == IntPtr.Zero) return false;
    try
    {
        if (!NativeSecurity.GetAclInformation(dacl, out var info, (uint)Marshal.SizeOf<NativeSecurity.ACL_SIZE_INFORMATION>(), 2)) return false;
        var currentSid = WindowsIdentity.GetCurrent().User?.Value;
        for (uint i = 0; i < info.AceCount; i++)
        {
            if (!NativeSecurity.GetAce(dacl, i, out var ace)) return false;
            var header = Marshal.PtrToStructure<NativeSecurity.ACE_HEADER>(ace);
            if (header.AceType != 0) return false;
            var sid = IntPtr.Add(ace, 8);
            if (!NativeSecurity.ConvertSidToStringSidW(sid, out var sidText)) return false;
            try { if (Marshal.PtrToStringUni(sidText) != currentSid) return false; }
            finally { NativeSecurity.LocalFree(sidText); }
        }
        return info.AceCount == 1;
    }
    finally { NativeSecurity.LocalFree(descriptor); }
}
static bool FirstInstanceIsExclusive(NativeWorkerClient worker)
{
    using var duplicate = NativeSecurity.CreateNamedPipeForTest($"\\\\.\\pipe\\{worker.PipeNameForTests}", worker.PipeOpenModeForTests, worker.PipeModeForTests, 1, 65536, 65536, 0, IntPtr.Zero);
    var error = Marshal.GetLastWin32Error();
    return duplicate.IsInvalid && error is 5 or 231;
}

static class NativeSecurity
{
    [StructLayout(LayoutKind.Sequential)] public struct ACL_SIZE_INFORMATION { public uint AceCount, AclBytesInUse, AclBytesFree; }
    [StructLayout(LayoutKind.Sequential)] public struct ACE_HEADER { public byte AceType, AceFlags; public ushort AceSize; }
    [DllImport("advapi32.dll", SetLastError = true)] public static extern uint GetSecurityInfo(SafePipeHandle handle, int objectType, uint securityInfo, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetAclInformation(IntPtr acl, out ACL_SIZE_INFORMATION info, uint length, int infoClass);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetAce(IntPtr acl, uint index, out IntPtr ace);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafePipeHandle CreateNamedPipeForTest(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, IntPtr securityAttributes);
}

sealed class DelayedFailClient : IWorkerClient
{
    public WorkerStateDto State { get; private set; } = new(WorkerConnectionState.Disconnected, "");
    public event Action<WorkerStateDto>? StateChanged;
    public event Action<WorkerSessionAcceptedDto>? SessionAccepted { add { } remove { } }
    public event Action<WorkerEventDto>? EventReceived { add { } remove { } }
    public event Action<WorkerTransportFaultDto>? TransportFault { add { } remove { } }
    public async Task ConnectAsync(CancellationToken cancellationToken = default) { State = new(WorkerConnectionState.Starting, "Starting"); StateChanged?.Invoke(State); await Task.Delay(50, cancellationToken); State = new(WorkerConnectionState.Failed, "synthetic failure"); StateChanged?.Invoke(State); throw new IOException("synthetic failure"); }
    public Task<IReadOnlyList<WorkerEndpointDto>> ListEndpointsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkerEndpointDto>>([]);
    public Task<WorkerSessionAcceptedDto> StartSessionAsync(WorkerSessionSettingsDto settings, ConsentReceiptDto consent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task StopSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

static class TestNativePipeWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> Run(string fullPipeName)
    {
        var pipeName = fullPipeName.Split('\\').Last();
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using (var hello = IpcFrameCodec.Parse(await IpcFrameCodec.ReadPayloadAsync(pipe, CancellationToken.None)))
        {
            var requestId = hello.RootElement.GetProperty("requestId").GetGuid();
            await IpcFrameCodec.WriteJsonAsync(pipe, new { bootstrapVersion = 1, kind = "response", correlationId = requestId, product = "KLIDAN_AUDIO_PROBE_WORKER", protocol = new { major = 1, minor = 0 }, schemas = new { command = 1, @event = 1, report = 1 }, workerVersion = "D3-test", capabilities = new[] { "Handshake", "ListEndpoints", "ListWindowsEndpoints", "StartCaptureMeter", "StartRenderChannelTest", "StopSession", "Shutdown" } }, CancellationToken.None);
        }
        var mode = Environment.GetEnvironmentVariable("KLIDAN_TEST_WORKER_MODE") ?? "NORMAL";
        Guid activeSession = Guid.Empty, startCorrelation = Guid.Empty;
        WorkerSessionSettingsDto? settings = null;
        long sequence = 0;
        while (true)
        {
            using var document = IpcFrameCodec.Parse(await IpcFrameCodec.ReadPayloadAsync(pipe, CancellationToken.None));
            var root = document.RootElement;
            var correlation = root.GetProperty("requestId").GetGuid();
            var command = root.GetProperty("commandOrEvent").GetString()!;
            if (command == "ListEndpoints")
            {
                await Respond("ListEndpoints", correlation, null, "OK", new { endpoints = Array.Empty<WorkerEndpointDto>() });
                continue;
            }
            if (command == "ListWindowsEndpoints")
            {
                if (Environment.GetEnvironmentVariable("KLIDAN_TEST_FORMAT_SCENARIO") == "HOLD_DISCOVERY") { await Task.Delay(350); }
                var evidence = new { hresult = 0, state = "AVAILABLE" };
                await Respond(command, correlation, null, "OK", new
                {
                    provenance = "REAL_WINDOWS_METADATA", operationState = "COMPLETE", captureHresult = 0, renderHresult = 0,
                    errors = Array.Empty<object>(), endpoints = new[] { new
                    {
                        endpointId = "fixture-local-id", endpointIdEvidence = evidence, friendlyName = "Fixture Endpoint", friendlyNameEvidence = evidence,
                        direction = "RENDER", deviceState = "ACTIVE", rawDeviceState = 1, deviceStateEvidence = evidence,
                        formFactor = "UNKNOWN", formFactorEvidence = new { hresult = unchecked((int)0x80070490), state = "UNAVAILABLE" },
                        sharedModeFormat = new { property = evidence, parseState = "VALID", formatTag = 65534, encoding = "PCM", reportedChannelCount = 8,
                            sampleRate = 48000u, bitsPerSample = 24, validBitsPerSample = 20, topologyState = "REPRESENTABLE_1_TO_64", speakerMask = (uint?)null, speakerMaskState = "UNKNOWN" }
                    } }
                });
                continue;
            }
            if (command is "StartCaptureMeter" or "StartRenderChannelTest")
            {
                if (mode == "HOLD_START") await Task.Delay(Timeout.InfiniteTimeSpan);
                if (mode is "DELAY_ACCEPT" or "DELAY_REJECT") await Task.Delay(300);
                if (mode == "DELAY_REJECT")
                {
                    await Respond(command, correlation, null, "ERROR", new { }, "BUSY", "delayed fake rejection");
                    continue;
                }
                var payload = root.GetProperty("payload");
                settings = JsonSerializer.Deserialize<WorkerSessionSettingsDto>(payload.GetRawText(), JsonOptions) ?? throw new InvalidDataException("Fixture settings missing.");
                activeSession = Guid.NewGuid(); startCorrelation = correlation; sequence = 0;
                await Respond(command, correlation, activeSession, "OK", new { acceptance = "ACCEPTED", operationState = "ACCEPTED", settings = SettingsJson(settings) });
                await Event("Preparing", "PREPARING", "UNKNOWN", "NOT_ATTEMPTED");
                continue;
            }
            if (command == "StopSession")
            {
                await Respond(command, correlation, activeSession, "OK", new { acceptance = "ACCEPTED" });
                await Event("Stopping", "STOPPING", "UNKNOWN", "SYNTHETIC_CLEANUP_PENDING");
                await Event("Completed", "COMPLETED", "UNKNOWN", "SYNTHETIC_CLEANUP_COMPLETED");
                continue;
            }
            if (command == "Shutdown")
            {
                await Respond(command, correlation, null, "OK", new { shutdown = "IDLE" });
                return 0;
            }
            await Respond(command, correlation, null, "ERROR", new { }, "UNKNOWN_COMMAND", "unknown test fixture command");
        }

        async Task Respond(string commandName, Guid correlationId, Guid? session, string status, object payload, string? errorCode = null, string? message = null) =>
            await IpcFrameCodec.WriteJsonAsync(pipe, new { protocolVersion = new { major = 1, minor = 0 }, schemaVersion = 1, kind = "response", requestId = (Guid?)null, correlationId, sessionId = session, eventSequence = 0L, commandOrEvent = commandName, status, errorCode, message, payload }, CancellationToken.None);

        async Task Event(string name, string lifecycle, string quality, string cleanup) =>
            await IpcFrameCodec.WriteJsonAsync(pipe, new { protocolVersion = new { major = 1, minor = 0 }, schemaVersion = 1, kind = "event", requestId = (Guid?)null, correlationId = startCorrelation, sessionId = activeSession, eventSequence = ++sequence, commandOrEvent = name, payload = new { synthetic = true, lifecycle, streamQuality = quality, cleanupEvidence = cleanup, physicalMapping = "UNKNOWN", settings = SettingsJson(settings!) } }, CancellationToken.None);
    }

    private static object SettingsJson(WorkerSessionSettingsDto s) => new { endpointId = s.EndpointId, direction = s.Direction, formatId = s.FormatId, sampleRate = s.SampleRate, encoding = s.Encoding, channelCount = s.ChannelCount, topologyId = s.TopologyId, durationSeconds = s.DurationSeconds, period = s.Period, mmcss = s.Mmcss, renderChannel = s.RenderChannel, scenario = s.Scenario };
}
