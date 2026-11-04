using System.Globalization;
using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public enum EvidenceLibraryRecordType
{
    ExclusiveFormat,
    PeriodDiscovery,
    DuplexValidation,
    SustainedDuplex,
    CustomPeriodDuplex,
    DuplexRepeatability,
    DeviceConfigurationProfile,
    DeviceConfigurationScanSummary,
    Unsupported
}

public sealed record EvidenceLibraryEntry(
    EvidenceLibraryRecordType RecordType,
    string ReportType,
    int SchemaVersion,
    string Path,
    string Provenance,
    string Lifecycle,
    string Quality,
    DateTimeOffset? TimestampUtc,
    string? ReportId,
    string? OperationId,
    string? CaptureEndpointId,
    string? RenderEndpointId,
    string? EndpointId,
    string? Direction,
    string? Period,
    string? Format,
    string? Mode,
    bool IsHistorical,
    bool IsValid,
    string Status,
    string Details,
    JsonElement Payload)
{
    public string? SessionId { get; init; }
    public string? SeriesIdentity { get; init; }
    public string TimestampLocalText => TimestampUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "неизвестно";
    public string TimestampUtcText => TimestampUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "неизвестно";
    public string ProvenanceLabel => Provenance switch { "REAL" => "REAL", "SYNTHETIC" => "SYNTHETIC", "MIXED" => "MIXED", _ => "UNKNOWN" };
    public string IdentityText => CaptureEndpointId is not null || RenderEndpointId is not null
        ? $"Capture: {CaptureEndpointId ?? "?"}; Render: {RenderEndpointId ?? "?"}"
        : EndpointId is not null ? $"{Direction ?? "?"}: {EndpointId}" : "Identity unknown";
}

public sealed record EvidenceLibrarySnapshot(IReadOnlyList<EvidenceLibraryEntry> Entries, DateTimeOffset RefreshedAtUtc)
{
    public IReadOnlyList<EvidenceLibraryEntry> ValidEntries => Entries.Where(e => e.IsValid).ToArray();
    public IReadOnlyList<EvidenceLibraryEntry> InvalidEntries => Entries.Where(e => !e.IsValid).ToArray();
}

public sealed record DuplexEvidenceComparison(string ConfigurationKey, IReadOnlyList<EvidenceLibraryEntry> Runs, string Summary);

public static class EvidenceLibraryCatalog
{
    private const int MaximumFileBytes = 8 * 1024 * 1024;
    private const int MaximumFiles = 4096;
    private const long MaximumTotalBytes = 64L * 1024 * 1024;
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 };

    public static EvidenceLibrarySnapshot Refresh(string? rootDirectory = null)
    {
        string[] files;
        try { files = ApprovedFiles(rootDirectory).Take(MaximumFiles).ToArray(); }
        catch (Exception ex) { return new([Invalid(rootDirectory ?? "<approved-directories>", ex.GetType().Name + ": " + ex.Message)], DateTimeOffset.UtcNow); }
        var entries = new List<EvidenceLibraryEntry>(files.Length);
        long totalBytes = 0;
        foreach (var path in files)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("File is empty or exceeds the bounded catalog size.");
                if (totalBytes + info.Length > MaximumTotalBytes) throw new InvalidDataException("Catalog byte budget exceeded.");
                totalBytes += info.Length;
                using var document = JsonDocument.Parse(File.ReadAllBytes(path), DocumentOptions);
                entries.Add(Read(path, document.RootElement.Clone()));
            }
            catch (Exception ex)
            {
                entries.Add(Invalid(path, ex.GetType().Name + ": " + ex.Message));
            }
        }

        var identities = entries.Where(e => e.IsValid).GroupBy(IdentityKey, StringComparer.OrdinalIgnoreCase);
        foreach (var group in identities.Where(g => g.Key is not null && g.Count() > 1))
            foreach (var entry in group.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Skip(1))
                entries[entries.IndexOf(entry)] = entry with { IsValid = false, Status = "CONFLICT", Details = "Duplicate report or operation identity." };
        return new(entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray(), DateTimeOffset.UtcNow);
    }

    public static IReadOnlyList<DuplexEvidenceComparison> CompareDuplex(IReadOnlyList<EvidenceLibraryEntry> entries)
    {
        var valid = entries.Where(e => e.IsValid).ToArray();
        var normalized = NormalizedDuplexEvidence.FromEntries(valid);
        return normalized.GroupBy(o => o.Configuration.ExactKey, StringComparer.Ordinal)
            .Select(g => new DuplexEvidenceComparison(g.Key, valid.Where(e => g.Any(o => o.SourcePath.Equals(e.Path, StringComparison.OrdinalIgnoreCase))).OrderBy(e => e.TimestampUtc).ToArray(), $"{g.Count()} normalized Duplex observations; measurements remain per operation."))
            .OrderBy(g => g.ConfigurationKey, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> ApprovedFiles(string? rootDirectory)
    {
        if (rootDirectory is not null)
            return Directory.Exists(rootDirectory) ? Directory.EnumerateFiles(rootDirectory, "*.json", SearchOption.AllDirectories) : [];
        var baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe");
        return new[] { "Profiles", "Scans", "PeriodDiscovery", "CustomDuplex", "Repeatability", "SustainedDuplex" }
            .Select(name => Path.Combine(baseDirectory, name))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories));
    }

    private static EvidenceLibraryEntry Read(string path, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Root JSON value must be an object.");
        var reportType = String(root, "reportType");
        var schema = Int(root, "schemaVersion") ?? throw new InvalidDataException("schemaVersion is required.");
        var recordType = reportType switch
        {
            "wasapi-exclusive-format-matrix" => EvidenceLibraryRecordType.ExclusiveFormat,
            "wasapi-exclusive-period-discovery" or "wasapi-exclusive-period-discovery-series" => EvidenceLibraryRecordType.PeriodDiscovery,
            "wasapi-duplex-validation" => EvidenceLibraryRecordType.DuplexValidation,
            "wasapi-sustained-duplex" => EvidenceLibraryRecordType.SustainedDuplex,
            "wasapi-custom-period-duplex" => EvidenceLibraryRecordType.CustomPeriodDuplex,
            "wasapi-duplex-repeatability" => EvidenceLibraryRecordType.DuplexRepeatability,
            "wasapi-device-configuration-scan" => EvidenceLibraryRecordType.DeviceConfigurationScanSummary,
            null when root.TryGetProperty("profileId", out _) => EvidenceLibraryRecordType.DeviceConfigurationProfile,
            null when root.TryGetProperty("sessionId", out _) && root.TryGetProperty("operations", out _) => EvidenceLibraryRecordType.DeviceConfigurationScanSummary,
            null => throw new InvalidDataException("Unsupported report type.") ,
            _ => throw new InvalidDataException("Unsupported report type.")
        };
        ValidateKnown(recordType, schema, root, path);
        var payload = recordType is EvidenceLibraryRecordType.CustomPeriodDuplex or EvidenceLibraryRecordType.SustainedDuplex && root.TryGetProperty("report", out var nested) ? nested : root;
        var provenance = Provenance(payload);
        var timestamp = Date(payload, "timestampUtc") ?? (recordType == EvidenceLibraryRecordType.DeviceConfigurationProfile ? Date(root, "createdAtUtc") : null);
        var reportId = GuidText(payload, "reportId") ?? GuidText(root, "profileId");
        var operationId = GuidText(payload, "operationId");
        var capture = String(payload, "captureEndpointId") ?? String(payload, "capture", "endpointId");
        var render = String(payload, "renderEndpointId") ?? String(payload, "render", "endpointId");
        var endpoint = String(payload, "endpointId");
        var direction = String(payload, "direction");
        var period = PeriodText(payload);
        var format = Format(payload);
        var lifecycle = String(root, "lifecycle") ?? String(payload, "duplex", "status") ?? "HISTORICAL";
        var quality = String(payload, "duplex", "status") ?? String(payload, "streamQuality") ?? lifecycle;
        return new(recordType, reportType ?? recordType.ToString(), schema, path, provenance ?? "UNKNOWN", lifecycle, quality, timestamp, reportId?.ToString("D"), operationId?.ToString("D"), capture, render, endpoint, direction, period, format, String(payload, "requested", "mode") ?? (recordType is EvidenceLibraryRecordType.DuplexValidation or EvidenceLibraryRecordType.CustomPeriodDuplex or EvidenceLibraryRecordType.DuplexRepeatability ? "EXCLUSIVE" : String(payload, "mmcss")), true, true, "VALID", "Historical evidence; current discovery and operator consent are still required.", root)
        {
            SessionId = GuidText(root, "sessionId")?.ToString("D"),
            SeriesIdentity = recordType == EvidenceLibraryRecordType.DuplexRepeatability ? GuidText(root, "sessionId")?.ToString("D") : null
        };
    }

    private static void ValidateKnown(EvidenceLibraryRecordType type, int schema, JsonElement root, string path)
    {
        if (type == EvidenceLibraryRecordType.DeviceConfigurationProfile)
        {
            _ = DeviceConfigurationProfileJson.Parse(JsonSerializer.SerializeToUtf8Bytes(root));
            return;
        }
        if (type == EvidenceLibraryRecordType.DeviceConfigurationScanSummary)
        {
            _ = DeviceConfigurationScanStorage.LoadSummary(path);
            return;
        }
        if (type == EvidenceLibraryRecordType.PeriodDiscovery && root.TryGetProperty("reports", out _)) { ExclusivePeriodDiscoverySeriesJson.ValidateReadBack(path); return; }
        if (type == EvidenceLibraryRecordType.CustomPeriodDuplex)
        {
            CustomPeriodDuplexReportStorage.ValidateReadBack(path);
            NormalizedDuplexEvidence.ValidateReportContract(root.GetProperty("report"));
            return;
        }
        if (type == EvidenceLibraryRecordType.DuplexRepeatability)
        {
            var report = DuplexRepeatabilityReportStorage.Load(path);
            foreach (var run in report.Runs)
                if (run.Validation is { Report: var validation }) NormalizedDuplexEvidence.ValidateReportContract(validation);
            return;
        }
        if (type == EvidenceLibraryRecordType.SustainedDuplex)
        {
            Need(schema == 1 && GuidText(root, "operationId").HasValue && GuidText(root.GetProperty("report"), "reportId").HasValue && GuidText(root.GetProperty("report"), "operationId").HasValue && GuidText(root.GetProperty("report"), "operationId") == GuidText(root, "operationId") && String(root.GetProperty("report"), "provenance") == "REAL_WASAPI_SUSTAINED_DUPLEX" && String(root.GetProperty("report"), "reportType") == "wasapi-sustained-duplex-validation", "Sustained report envelope is invalid.");
            var report = root.GetProperty("report");
            var requested = report.GetProperty("requested");
            Need(String(requested, "period") is { Length: > 0 } && String(requested, "mmcss") is "OFF" or "PRO_AUDIO" && Int(requested, "durationSeconds") is 60 or 300 or 600 && Int(requested, "sampleRateHz") == 48_000 && String(requested, "encoding") == "PCM16" && Int(requested, "channels") == 2, "Sustained report requested configuration is invalid.");
            foreach (var name in new[] { "capture", "render" })
            {
                var side = report.GetProperty(name);
                Need(String(side, "endpointId") is { Length: > 0 } && String(side, "direction") == name.ToUpperInvariant() && side.GetProperty("actualBufferFrames").TryGetInt32(out var frames) && frames > 0 && side.GetProperty("durationReached").ValueKind == JsonValueKind.True && side.GetProperty("stopSucceeded").ValueKind == JsonValueKind.True && String(side, "cleanup") == "RELEASE_ATTEMPTED", $"Sustained {name} evidence is incomplete.");
            }
            return;
        }
        if (schema != 1 && !(type is EvidenceLibraryRecordType.DuplexValidation && schema == 2)) throw new InvalidDataException("Unsupported schema version.");
        if (type == EvidenceLibraryRecordType.ExclusiveFormat && (!GuidText(root, "reportId").HasValue || !root.TryGetProperty("candidates", out _))) throw new InvalidDataException("Format report identity or candidates are incomplete.");
        if (type == EvidenceLibraryRecordType.DuplexValidation) NormalizedDuplexEvidence.ValidateReportContract(root);
        if (type == EvidenceLibraryRecordType.PeriodDiscovery && !GuidText(root, "reportId").HasValue && !root.TryGetProperty("reports", out _)) throw new InvalidDataException("Period discovery identity is incomplete.");
        if (type == EvidenceLibraryRecordType.CustomPeriodDuplex && (!GuidText(root, "operationId").HasValue || !root.TryGetProperty("report", out _))) throw new InvalidDataException("Custom Duplex envelope is incomplete.");
        if (type == EvidenceLibraryRecordType.DuplexRepeatability && (!GuidText(root, "sessionId").HasValue || !root.TryGetProperty("runs", out _))) throw new InvalidDataException("Repeatability envelope is incomplete.");
    }

    private static void Need(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static EvidenceLibraryEntry Invalid(string path, string details) => new(EvidenceLibraryRecordType.Unsupported, "unsupported", 0, path, "UNKNOWN", "UNKNOWN", "UNKNOWN", null, null, null, null, null, null, null, null, null, null, true, false, "INVALID", details, default);
    private static string? IdentityKey(EvidenceLibraryEntry e) => e.SessionId is not null ? $"session:{e.SessionId}" : e.ReportId is not null ? $"report:{e.ReportId}" : e.OperationId is not null ? $"operation:{e.OperationId}" : e.RecordType == EvidenceLibraryRecordType.DeviceConfigurationProfile ? $"profile:{e.Path}" : null;
    private static string? Provenance(JsonElement e)
    {
        var values = new[] { String(e, "provenance"), String(e, "report", "provenance") }.Where(v => v is not null).Select(v => v!.StartsWith("REAL_", StringComparison.Ordinal) ? "REAL" : v.StartsWith("TEST_", StringComparison.Ordinal) ? "SYNTHETIC" : v).Distinct().ToArray();
        return values.Length == 1 ? values[0] : values.Length > 1 ? "MIXED" : "UNKNOWN";
    }
    private static string? PeriodText(JsonElement e)
    {
        if (String(e, "period") is { } period) return period;
        if (String(e, "requested", "period") is { } requested) return requested;
        if (e.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            if (candidate.TryGetProperty("requestedPeriod100ns", out var duration) && candidate.TryGetProperty("requestedFrames", out var frames))
                return $"{duration.GetInt64()}×100 ns / {frames.GetInt32()} frames";
        }
        return null;
    }
    private static string? Format(JsonElement e) => String(e, "format") ?? (String(e, "requested", "encoding") is { } encoding && Int(e, "requested", "sampleRateHz") is { } rate && Int(e, "requested", "channels") is { } channels ? $"{encoding} {rate} Hz {channels}ch" : Int(e, "sampleRateHz") is { } flatRate && String(e, "encoding") is { } flatEncoding && Int(e, "channels") is { } flatChannels ? $"{flatEncoding} {flatRate} Hz {flatChannels}ch" : null);
    private static Guid? GuidText(JsonElement e, params string[] path) => Guid.TryParse(String(e, path), out var value) ? value : null;
    private static int? Int(JsonElement e, params string[] path) => Try<int>(e, path, x => x.TryGetInt32(out var value) ? value : null);
    private static DateTimeOffset? Date(JsonElement e, params string[] path) => Try<DateTimeOffset>(e, path, x => DateTimeOffset.TryParse(x.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.ToUniversalTime() : null);
    private static T? Try<T>(JsonElement e, string[] path, Func<JsonElement, T?> read) where T : struct
    {
        foreach (var name in path) { if (!e.TryGetProperty(name, out e)) return null; }
        return read(e);
    }
    private static string? String(JsonElement e, params string[] path)
    {
        foreach (var name in path) { if (!e.TryGetProperty(name, out e)) return null; }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }
}
