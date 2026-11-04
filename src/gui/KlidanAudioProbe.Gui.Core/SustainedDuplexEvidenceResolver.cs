using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public static class SustainedDuplexEvidenceResolver
{
    public const int MaximumReportBytes = 8 * 1024 * 1024;

    public static IReadOnlyList<SustainedDuplexEvidence> Resolve(IReadOnlyList<SustainedDuplexEvidence> evidence, string captureEndpointId, string renderEndpointId, string? approvedRootDirectory = null)
    {
        if (evidence is null || string.IsNullOrWhiteSpace(captureEndpointId) || string.IsNullOrWhiteSpace(renderEndpointId)) throw new InvalidDataException("Sustained evidence resolver requires an exact endpoint pair.");
        var root = Path.GetFullPath(approvedRootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "SustainedDuplex"));
        return evidence.Select(item => ResolveOne(item, captureEndpointId, renderEndpointId, root)).ToArray();
    }

    private static SustainedDuplexEvidence ResolveOne(SustainedDuplexEvidence source, string captureEndpointId, string renderEndpointId, string root)
    {
        if (!Guid.TryParse(source.OperationId, out var operationId) || operationId == Guid.Empty) throw new InvalidDataException("Sustained evidence operation identity is invalid.");
        var expectedPath = Path.Combine(root, $"sustained-duplex-{operationId:D}.json");
        try
        {
            if (source.ReportPath is { } hint && Path.GetFullPath(hint) != expectedPath) throw new InvalidDataException("Sustained evidence path is outside the approved derived location.");
        }
        catch (ArgumentException ex) { throw new InvalidDataException("Sustained evidence path is invalid.", ex); }
        var info = new FileInfo(expectedPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumReportBytes) throw new InvalidDataException("The derived sustained evidence report is missing or exceeds the size limit.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(expectedPath), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The persisted sustained evidence report is not valid JSON.", ex);
        }
        using (document)
        {
            RejectDuplicateProperties(document.RootElement);
            var rootElement = document.RootElement;
            RequireEnvelope(rootElement, source, operationId);
            var report = rootElement.GetProperty("report");
            if (source.Report.ValueKind != JsonValueKind.Object || !JsonElement.DeepEquals(source.Report, report)) throw new InvalidDataException("Persisted sustained report content differs from the in-memory report.");
            ValidateReport(source, report, operationId, captureEndpointId, renderEndpointId);
            return source with { OperationId = operationId.ToString("D"), Report = report.Clone(), ReportPath = expectedPath };
        }
    }

    private static void RequireEnvelope(JsonElement root, SustainedDuplexEvidence source, Guid operationId)
    {
        Need(root.ValueKind == JsonValueKind.Object, "Sustained evidence envelope is not an object.");
        var allowed = new[] { "schemaVersion", "reportType", "lifecycle", "classification", "operationId", "period", "report" };
        Need(root.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal)) && root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("reportType").GetString() == "wasapi-sustained-duplex", "Sustained evidence envelope is invalid.");
        Need(Guid.TryParse(root.GetProperty("operationId").GetString(), out var envelopeOperation) && envelopeOperation == operationId && root.GetProperty("period").GetString() == source.Period && root.GetProperty("lifecycle").GetString() == source.Lifecycle && root.GetProperty("classification").GetString() == source.Classification, "Persisted sustained envelope does not match the expected operation.");
    }

    private static void ValidateReport(SustainedDuplexEvidence source, JsonElement report, Guid operationId, string captureEndpointId, string renderEndpointId)
    {
        Need(report.ValueKind == JsonValueKind.Object && report.GetProperty("schemaVersion").GetInt32() == 1 && report.GetProperty("reportType").GetString() == "wasapi-sustained-duplex-validation" && report.GetProperty("provenance").GetString() == "REAL_WASAPI_SUSTAINED_DUPLEX" && report.GetProperty("physicalRtl").GetString() == "NOT_MEASURED", "Persisted sustained report provenance or schema is invalid.");
        Need(Guid.TryParse(report.GetProperty("operationId").GetString(), out var reportOperation) && reportOperation == operationId && Guid.TryParse(report.GetProperty("reportId").GetString(), out var reportId) && reportId != Guid.Empty, "Persisted sustained report identity is invalid.");
        var requested = report.GetProperty("requested");
        Need(requested.GetProperty("captureEndpointId").GetString() == captureEndpointId && requested.GetProperty("renderEndpointId").GetString() == renderEndpointId && requested.GetProperty("period").GetString() == source.Period && requested.GetProperty("mmcss").GetString() == source.Mmcss && requested.GetProperty("durationSeconds").GetInt32() == source.DurationSeconds && source.DurationSeconds is 60 or 300 or 600 && requested.GetProperty("sampleRateHz").GetInt32() == 48_000 && requested.GetProperty("encoding").GetString() == "PCM16" && requested.GetProperty("channels").GetInt32() == 2, "Persisted sustained requested settings differ from the expected evidence.");
        var status = report.GetProperty("duplex").GetProperty("status").GetString();
        Need((source.Classification == "SUSTAINED_CLEAN" && status == "DUPLEX_CLEAN") || (source.Classification == "SUSTAINED_WARNINGS" && status == "DUPLEX_WARNINGS"), "Persisted sustained classification contradicts the report.");
        foreach (var (name, direction, endpointId, expectedFrames) in new[] { ("capture", "CAPTURE", captureEndpointId, source.CaptureFrames), ("render", "RENDER", renderEndpointId, source.RenderFrames) })
        {
            Need(expectedFrames is > 0, $"Independent {name} geometry is missing.");
            var side = report.GetProperty(name);
            Need(side.GetProperty("endpointId").GetString() == endpointId && side.GetProperty("direction").GetString() == direction && side.GetProperty("actualBufferFrames").GetInt32() == expectedFrames && side.GetProperty("durationReached").ValueKind == JsonValueKind.True && side.GetProperty("stopSucceeded").ValueKind == JsonValueKind.True && side.GetProperty("cleanup").GetString() == "RELEASE_ATTEMPTED" && !True(side, "deviceInvalidated") && CountersAreNonnegative(side), $"Persisted sustained {name} geometry, cleanup or quality counters are invalid.");
        }
    }

    private static bool True(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool CountersAreNonnegative(JsonElement side) => new[] { "framesProcessed", "expectedFrames", "events", "packets", "discontinuities", "timestampErrors", "serviceFaults", "eventIntervalsObserved", "packetDrainLimitHits", "intermediateWaitTimeouts", "longEventIntervals" }.All(name => side.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) && count >= 0);
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
