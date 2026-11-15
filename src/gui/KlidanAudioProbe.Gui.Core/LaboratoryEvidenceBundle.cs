using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record LaboratoryEvidenceBundleDirectories(string Scans, string Profiles, string PeriodDiscovery,
    string CustomDuplex, string Repeatability, string SustainedDuplex);

public sealed record LaboratoryEvidenceBundleLimits(int MaximumFiles = 128, long MaximumBytes = 64 * 1024 * 1024,
    long MaximumFileBytes = 8 * 1024 * 1024);

public sealed record LaboratoryEvidenceBundleFile(string Role, string ArchivePath, long ByteLength, string Sha256,
    string? OperationId = null, string? ReportId = null);

public sealed record LaboratoryEvidenceBundleSelection(string Role, string Period, string Mmcss, int DurationSeconds,
    string Quality, string Confidence, string OperationId, string ReportId);

public sealed record LaboratoryEvidenceBundleProfile(string Role, Guid ProfileId, int SchemaVersion, string ArchivePath);

public sealed record LaboratoryEvidenceBundleManifest(int ManifestSchemaVersion, string BundleId, string SourceScanSessionId,
    DateTimeOffset OriginalScanTimestampUtc, DateTimeOffset BundleCreatedUtc, string ToolName, string? ToolVersion,
    string ScanLifecycle, string PersistenceStatus, IReadOnlyList<LaboratoryEvidenceBundleProfile> Profiles,
    LaboratoryEvidenceBundleSelection? Preferred, LaboratoryEvidenceBundleSelection? Fallback,
    IReadOnlyList<LaboratoryEvidenceBundleFile> Files, string MembershipCompleteness, string AudioHostEligibility,
    string PhysicalRtlStatus, string Scope, IReadOnlyList<string> Limitations);

public sealed class LaboratoryEvidenceBundlePreparation
{
    internal LaboratoryEvidenceBundlePreparation(LaboratoryEvidenceBundleManifest manifest, string fingerprint,
        IReadOnlyList<SourceFile> files, LaboratoryEvidenceBundleDirectories directories, LaboratoryEvidenceBundleLimits limits,
        IReadOnlyList<string> errors)
    { Manifest = manifest; SourceFingerprint = fingerprint; Files = files; Directories = directories; Limits = limits; Errors = errors; }
    public LaboratoryEvidenceBundleManifest Manifest { get; }
    public string SourceFingerprint { get; }
    public bool IsComplete => Manifest.MembershipCompleteness == "COMPLETE";
    public IReadOnlyList<string> Errors { get; }
    internal IReadOnlyList<SourceFile> Files { get; }
    internal LaboratoryEvidenceBundleDirectories Directories { get; }
    internal LaboratoryEvidenceBundleLimits Limits { get; }
}

public sealed record LaboratoryEvidenceBundleVerificationResult(bool IsValid, IReadOnlyList<string> Errors,
    string? SourceScanSessionId = null, string? BundleId = null);

internal sealed record SourceFile(string Role, string ArchivePath, string Path, byte[] Bytes, LaboratoryEvidenceBundleFile Manifest);

public static class LaboratoryEvidenceBundleService
{
    private const string ManifestName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static LaboratoryEvidenceBundleDirectories DefaultDirectories(string summaryPath, string? profileDirectory = null)
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe");
        return new(Path.GetDirectoryName(Path.GetFullPath(summaryPath)) ?? Path.Combine(local, "Scans"),
            profileDirectory ?? Path.Combine(local, "Profiles"), Path.Combine(local, "PeriodDiscovery"),
            Path.Combine(local, "CustomDuplex"), Path.Combine(local, "Repeatability"), Path.Combine(local, "SustainedDuplex"));
    }

    public static LaboratoryEvidenceBundlePreparation Prepare(string sessionId, LaboratoryEvidenceBundleDirectories directories,
        LaboratoryEvidenceBundleLimits? limits = null)
    {
        limits ??= new();
        if (!Guid.TryParse(sessionId, out var session) || session == Guid.Empty) throw new InvalidDataException("Scan session identity is invalid.");
        ValidateRoots(directories);
        var errors = new List<string>();
        var files = new List<SourceFile>();
        var summaryPath = Path.Combine(directories.Scans, $"scan-{session:D}.json");
        DeviceConfigurationScanSummary summary;
        try { summary = DeviceConfigurationScanStorage.LoadSummary(summaryPath); }
        catch (Exception ex) { throw new InvalidDataException($"The scan summary is invalid: {ex.Message}", ex); }
        if (!string.Equals(summary.SessionId, session.ToString("D"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Scan summary identity differs from the requested session.");
        Add(files, "scan.summary", "scan/summary.json", summaryPath, null, null, limits);
        if (summary.Lifecycle != "COMPLETED") errors.Add("The scan lifecycle is not COMPLETED.");
        var saveStage = summary.AutomaticStages.FirstOrDefault(s => s.Stage == AutomaticScanStage.SAVE);
        if (saveStage is not { State: AutomaticScanStageState.COMPLETED })
            errors.Add("The scan save stage is not COMPLETED.");

        var profilePaths = summary.ProfilePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (profilePaths.Length == 0) errors.Add("The scan has no persisted profile.");
        var profiles = new List<DeviceConfigurationProfile>();
        foreach (var reference in profilePaths)
        {
            var profilePath = ResolveReferencedProfile(reference, directories.Profiles);
            try
            {
                var profile = DeviceConfigurationProfileJson.Parse(ReadBounded(profilePath, limits.MaximumFileBytes));
                profiles.Add(profile);
                Add(files, profile.SchemaVersion switch { 4 => "profile.v4", 5 => "profile.v5", _ => "profile.legacy" }, $"profiles/{Path.GetFileName(profilePath)}", profilePath, null, null, limits);
            }
            catch (Exception ex) { errors.Add($"Profile {Path.GetFileName(profilePath)} is invalid: {ex.Message}"); }
        }
        var v4 = profiles.SingleOrDefault(p => p.SchemaVersion == 4 && p.SustainedSelection is not null);
        if (v4 is null) errors.Add("A completed automatic scan requires one validated v4 sustained profile.");
        if (profiles.Count != profilePaths.Length) errors.Add("One or more referenced profiles could not be loaded.");
        if (v4?.SustainedSelection?.SourceScanSessionId is not { } source || !source.Equals(session.ToString("D"), StringComparison.OrdinalIgnoreCase)) errors.Add("The v4 profile source session differs from the scan.");

        var standaloneChildren = summary.ChildOperations.Where(IsStandaloneChild).ToArray();
        foreach (var child in standaloneChildren)
            if (child.Lifecycle != "COMPLETED" || child.ReportId is null || (child.Stage != "PERIOD_DISCOVERY" && child.OperationId is null))
                errors.Add($"Required standalone {child.Stage} evidence has incomplete child identity or lifecycle.");
        var children = standaloneChildren.Where(c => c.Lifecycle == "COMPLETED" && c.ReportId is not null).ToArray();
        AddPeriodSeries(children, directories.PeriodDiscovery, files, errors, limits);
        AddRepeatability(children, directories.Repeatability, files, errors, limits);
        foreach (var child in children.Where(c => c.Stage == "SHORT_CUSTOM_DUPLEX" || c.Stage == "SUSTAINED_VALIDATION"))
        {
            if (!Guid.TryParse(child.OperationId, out var operation)) { errors.Add($"{child.Stage} child has no valid operation identity."); continue; }
            var (root, prefix) = child.Stage == "SUSTAINED_VALIDATION" ? (directories.SustainedDuplex, "sustained-duplex-") : (directories.CustomDuplex, "custom-duplex-");
            var path = Path.Combine(root, prefix + operation.ToString("D") + ".json");
            try { var bytes = ReadBounded(path, limits.MaximumFileBytes); ValidateOperationReport(bytes, child, child.Stage); Add(files, child.Stage == "SUSTAINED_VALIDATION" ? "sustained.report" : "custom-duplex.report", $"reports/{child.Stage.ToLowerInvariant()}/{Path.GetFileName(path)}", path, child.OperationId, child.ReportId, limits); }
            catch (Exception ex) { errors.Add($"{child.Stage} report {child.ReportId} is invalid or missing: {ex.Message}"); }
        }
        if (v4?.SustainedSelection is { } selection)
        {
            foreach (var reference in selection.Evidence)
                if (!files.Any(f => string.Equals(f.Manifest.OperationId, reference.OperationId.ToString("D"), StringComparison.OrdinalIgnoreCase) && string.Equals(f.Manifest.ReportId, reference.ReportId.ToString("D"), StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"Profile evidence {reference.ReportId:D} is not a persisted bundle member.");
        }
        var preferred = ToSelection(v4?.SustainedSelection?.Preferred, "PRIMARY");
        var fallback = ToSelection(v4?.SustainedSelection?.Fallback, "FALLBACK");
        if (preferred is not null && !files.Any(f => f.Manifest.OperationId == preferred.OperationId && f.Manifest.ReportId == preferred.ReportId)) errors.Add("Preferred evidence is not persisted.");
        if (fallback is not null && !files.Any(f => f.Manifest.OperationId == fallback.OperationId && f.Manifest.ReportId == fallback.ReportId)) errors.Add("Fallback evidence is not persisted.");
        if (files.Count > limits.MaximumFiles || files.Sum(f => f.Bytes.Length) > limits.MaximumBytes) errors.Add("Bundle source bounds were exceeded.");
        var manifest = new LaboratoryEvidenceBundleManifest(1, Guid.NewGuid().ToString("D"), session.ToString("D"), summary.TimestampUtc,
            DateTimeOffset.UtcNow, "KLIDAN Audio Probe", typeof(LaboratoryEvidenceBundleService).Assembly.GetName().Version?.ToString(), summary.Lifecycle,
            saveStage?.Message ?? "UNKNOWN", profiles.Select((p, i) => new LaboratoryEvidenceBundleProfile(i == 0 ? "profile" : "profile.additional", p.ProfileId, p.SchemaVersion, $"profiles/{Path.GetFileName(profilePaths[i])}")).ToArray(), preferred, fallback,
            files.Select(f => f.Manifest).ToArray(), errors.Count == 0 ? "COMPLETE" : "INCOMPLETE", summary.AudioHostEligibility, summary.PhysicalRtl,
            "LOCAL_FULL_EVIDENCE", ["Contains raw local endpoint identifiers and machine-specific evidence; not anonymized or suitable for public distribution.", "SHA-256 provides integrity checking, not authorship or certification proof."]);
        return new(manifest, Fingerprint(files), files, directories, limits, errors);
    }

    public static string Export(LaboratoryEvidenceBundlePreparation preparation, string destinationZipPath)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!preparation.IsComplete) throw new InvalidDataException("Incomplete evidence cannot be exported as a complete bundle: " + string.Join("; ", preparation.Errors));
        if (File.Exists(destinationZipPath)) throw new IOException("The destination bundle already exists.");
        var current = Prepare(preparation.Manifest.SourceScanSessionId, preparation.Directories, preparation.Limits);
        if (!current.IsComplete || current.SourceFingerprint != preparation.SourceFingerprint) throw new InvalidDataException("Evidence changed after preparation; prepare a new bundle.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationZipPath)) ?? throw new ArgumentException("Destination has no directory.", nameof(destinationZipPath));
        Directory.CreateDirectory(parent);
        if (new DirectoryInfo(parent).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Bundle destination directory is a reparse point.");
        var temp = Path.Combine(parent, "." + Path.GetFileName(destinationZipPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(current.Manifest, JsonOptions);
            if (manifestBytes.LongLength + current.Files.Sum(f => f.Bytes.LongLength) > current.Limits.MaximumBytes) throw new InvalidDataException("Bundle uncompressed content exceeds the configured aggregate limit.");
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                WriteEntry(archive, ManifestName, manifestBytes);
                foreach (var file in current.Files) WriteEntry(archive, file.Manifest.ArchivePath, ReadAndMatch(file));
            }
            var verified = Verify(temp);
            if (!verified.IsValid) throw new InvalidDataException("Bundle read-back verification failed: " + string.Join("; ", verified.Errors));
            if (File.Exists(destinationZipPath)) throw new IOException("The destination bundle already exists.");
            File.Move(temp, destinationZipPath);
            return destinationZipPath;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static LaboratoryEvidenceBundleVerificationResult Verify(string bundlePath)
    {
        var errors = new List<string>(); string? session = null; string? bundle = null;
        try
        {
            if (new FileInfo(bundlePath).Length > 64 * 1024 * 1024) throw new InvalidDataException("Bundle exceeds the bounded archive size.");
            using var archive = ZipFile.OpenRead(bundlePath);
            if (archive.Entries.Count is 0 or > 129) throw new InvalidDataException("Bundle entry count is outside bounds.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            long uncompressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName != entry.FullName.Replace('\\', '/') || entry.FullName.StartsWith('/') || entry.FullName.Split('/').Contains("..") || !names.Add(entry.FullName)) throw new InvalidDataException("Bundle contains an unsafe or duplicate ZIP path.");
                if (entry.Length < 0 || entry.Length > 8 * 1024 * 1024 || (uncompressedBytes = checked(uncompressedBytes + entry.Length)) > 64 * 1024 * 1024) throw new InvalidDataException("Bundle uncompressed content exceeds the aggregate limit.");
            }
            var manifestEntry = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("Bundle manifest is missing.");
            var manifestBytes = ReadEntry(manifestEntry, 1_048_576);
            RejectDuplicateJsonProperties(manifestBytes);
            var manifest = JsonSerializer.Deserialize<LaboratoryEvidenceBundleManifest>(manifestBytes, JsonOptions) ?? throw new InvalidDataException("Bundle manifest is invalid.");
            session = manifest.SourceScanSessionId; bundle = manifest.BundleId;
            if (manifest.ManifestSchemaVersion != 1 || manifest.Scope != "LOCAL_FULL_EVIDENCE" || manifest.MembershipCompleteness != "COMPLETE" || !Guid.TryParse(manifest.BundleId, out _) || !Guid.TryParse(manifest.SourceScanSessionId, out _)) throw new InvalidDataException("Bundle manifest contract is invalid.");
            var listed = manifest.Files.Select(f => f.ArchivePath).ToHashSet(StringComparer.Ordinal);
            if (listed.Count != manifest.Files.Count || listed.Any(p => p == ManifestName || p.StartsWith('/') || p.Split('/').Contains(".."))) throw new InvalidDataException("Bundle manifest file membership is invalid.");
            if (!names.SetEquals(listed.Append(ManifestName))) throw new InvalidDataException("Bundle contains missing or unexpected entries.");
            foreach (var file in manifest.Files)
            {
                var entry = archive.GetEntry(file.ArchivePath) ?? throw new InvalidDataException("Bundle entry is missing.");
                var bytes = ReadEntry(entry, 8 * 1024 * 1024);
                if (bytes.LongLength != file.ByteLength || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Bundle integrity failed for {file.ArchivePath}.");
            }
            ValidateBundleIdentities(archive, manifest);
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        return new(errors.Count == 0, errors, session, bundle);
    }

    private static void ValidateBundleIdentities(ZipArchive archive, LaboratoryEvidenceBundleManifest manifest)
    {
        var summary = DeviceConfigurationScanStorage.LoadSummary(ReadEntry(archive.GetEntry("scan/summary.json") ?? throw new InvalidDataException("Summary is missing."), 8 * 1024 * 1024));
        if (!summary.SessionId.Equals(manifest.SourceScanSessionId, StringComparison.OrdinalIgnoreCase) || summary.Lifecycle != manifest.ScanLifecycle) throw new InvalidDataException("Summary identity contradicts the manifest.");
        foreach (var profile in manifest.Profiles)
        {
            var p = DeviceConfigurationProfileJson.Parse(ReadEntry(archive.GetEntry(profile.ArchivePath) ?? throw new InvalidDataException("Profile is missing."), 8 * 1024 * 1024));
            if (p.ProfileId != profile.ProfileId || p.SchemaVersion != profile.SchemaVersion) throw new InvalidDataException("Profile identity contradicts the manifest.");
        }
        foreach (var child in summary.ChildOperations.Where(c => c.Lifecycle == "COMPLETED" && c.ReportId is not null))
        {
            var role = child.Stage switch { "PERIOD_DISCOVERY" => "period-discovery.series", "REPEATABILITY" => "repeatability.series", _ => null };
            if (role is null) continue;
            var file = manifest.Files.SingleOrDefault(f => f.Role == role) ?? throw new InvalidDataException($"Manifest is missing {role}.");
            var bytes = ReadEntry(archive.GetEntry(file.ArchivePath) ?? throw new InvalidDataException("Series evidence is missing."), 8 * 1024 * 1024);
            if (!ContainsIdentity(bytes, child, role == "period-discovery.series" ? "reports" : "runs")) throw new InvalidDataException("Series evidence identity contradicts the scan summary.");
        }
        foreach (var file in manifest.Files.Where(f => f.OperationId is not null))
        {
            var bytes = ReadEntry(archive.GetEntry(file.ArchivePath) ?? throw new InvalidDataException("Evidence is missing."), 8 * 1024 * 1024);
            ValidateOperationReport(bytes, file.OperationId!, file.ReportId);
        }
    }

    private static void ValidateOperationReport(byte[] bytes, AutomaticScanChildOperation child, string stage) => ValidateOperationReport(bytes, child.OperationId!, child.ReportId, stage);
    private static void ValidateOperationReport(byte[] bytes, string operationId, string? reportId, string? stage = null)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); var root = doc.RootElement;
        var op = root.TryGetProperty("operationId", out var opValue) ? opValue.GetString() : root.TryGetProperty("report", out var nested) && nested.TryGetProperty("operationId", out var nestedOp) ? nestedOp.GetString() : null;
        if (!string.Equals(op, operationId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Report operation identity differs from the structured child operation.");
        var report = root.TryGetProperty("report", out var reportValue) ? reportValue : root;
        var rid = report.TryGetProperty("reportId", out var id) ? id.GetString() : null;
        if (reportId is not null && !string.Equals(rid, reportId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Report identity differs from the structured child operation.");
        var provenance = report.TryGetProperty("provenance", out var p) ? p.GetString() : null;
        if (provenance is null || !provenance.StartsWith("REAL_WASAPI", StringComparison.Ordinal)) throw new InvalidDataException("Evidence provenance is not real WASAPI evidence.");
        if (stage == "SUSTAINED_VALIDATION" && report.GetProperty("reportType").GetString() != "wasapi-sustained-duplex-validation") throw new InvalidDataException("Sustained report type is invalid.");
    }

    private static bool IsStandaloneChild(AutomaticScanChildOperation child) => child.Stage switch
    {
        "SHORT_CUSTOM_DUPLEX" or "SUSTAINED_VALIDATION" => true,
        "PERIOD_DISCOVERY" => child.ReportId is not null || child.OperationId is not null,
        "REPEATABILITY" => child.Lifecycle == "COMPLETED" || child.ReportId is not null || child.OperationId is not null,
        _ => false
    };

    private static void AddPeriodSeries(IReadOnlyList<AutomaticScanChildOperation> children, string root, List<SourceFile> files, List<string> errors, LaboratoryEvidenceBundleLimits limits)
    {
        var ids = children.Where(c => c.Stage == "PERIOD_DISCOVERY" && c.ReportId is not null).Select(c => c.ReportId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return;
        if (!Directory.Exists(root)) { errors.Add("Required PeriodDiscovery directory is missing."); return; }
        var matches = Directory.EnumerateFiles(root, "period-discovery-*.json").Where(p => !IsReparse(p)).Select(p => (Path: p, Bytes: TryRead(p, limits.MaximumFileBytes))).Where(x => x.Bytes is not null && ContainsIds(x.Bytes!, ids, "reports")).ToArray();
        if (matches.Length != 1) { errors.Add("Period discovery evidence is missing or ambiguous."); return; }
        Add(files, "period-discovery.series", $"reports/period-discovery/{Path.GetFileName(matches[0].Path)}", matches[0].Path, null, null, limits);
    }

    private static void AddRepeatability(IReadOnlyList<AutomaticScanChildOperation> children, string root, List<SourceFile> files, List<string> errors, LaboratoryEvidenceBundleLimits limits)
    {
        var ids = children.Where(c => c.Stage == "REPEATABILITY" && c.OperationId is not null).Select(c => c.OperationId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return;
        if (!Directory.Exists(root)) { errors.Add("Required Repeatability directory is missing."); return; }
        var matches = Directory.EnumerateFiles(root, "duplex-repeatability-*.json").Where(p => !IsReparse(p)).Select(p => (Path: p, Bytes: TryRead(p, limits.MaximumFileBytes))).Where(x => x.Bytes is not null && ContainsIds(x.Bytes!, ids, "runs")).ToArray();
        if (matches.Length != 1) errors.Add("Repeatability evidence is missing or ambiguous."); else Add(files, "repeatability.series", $"reports/repeatability/{Path.GetFileName(matches[0].Path)}", matches[0].Path, null, null, limits);
    }

    private static bool ContainsIds(byte[] bytes, IEnumerable<string> ids, string arrayName)
    {
        try { using var d = JsonDocument.Parse(bytes); var array = d.RootElement.TryGetProperty(arrayName, out var a) ? a.EnumerateArray().ToArray() : []; return ids.All(id => array.Any(x => (x.TryGetProperty("reportId", out var r) ? r.GetString() : x.TryGetProperty("operationId", out var o) ? o.GetString() : null)?.Equals(id, StringComparison.OrdinalIgnoreCase) == true)); } catch { return false; }
    }

    private static bool ContainsIdentity(byte[] bytes, AutomaticScanChildOperation child, string arrayName)
    {
        try
        {
            using var d = JsonDocument.Parse(bytes); if (!d.RootElement.TryGetProperty(arrayName, out var array)) return false;
            return array.EnumerateArray().Any(x => x.TryGetProperty("operationId", out var op) && x.TryGetProperty("reportId", out var report) &&
                string.Equals(op.GetString(), child.OperationId, StringComparison.OrdinalIgnoreCase) && string.Equals(report.GetString(), child.ReportId, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static void RejectDuplicateJsonProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        static void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("Manifest contains duplicate JSON properties."); Visit(property.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item);
        }
        Visit(document.RootElement);
    }

    private static LaboratoryEvidenceBundleSelection? ToSelection(ProfileSustainedConfiguration? c, string role) => c is null ? null : new(role, c.Period, c.Mmcss, c.DurationSeconds, c.Quality, c.Confidence, c.OperationIds.Single().ToString("D"), c.ReportIds.Single().ToString("D"));
    private static void Add(List<SourceFile> files, string role, string archivePath, string path, string? operationId, string? reportId, LaboratoryEvidenceBundleLimits limits) { var bytes = ReadBounded(path, limits.MaximumFileBytes); files.Add(new(role, archivePath, path, bytes, new(role, archivePath, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)), operationId, reportId))); }
    private static byte[] ReadAndMatch(SourceFile file) { var bytes = ReadBounded(file.Path, file.Bytes.LongLength); if (!bytes.AsSpan().SequenceEqual(file.Bytes)) throw new InvalidDataException($"Source changed during export: {file.Path}"); return bytes; }
    private static byte[] ReadBounded(string path, long max) { var info = new FileInfo(path); if (!info.Exists || info.Length <= 0 || info.Length > max || IsReparse(path)) throw new InvalidDataException("Source file is missing, oversized or a reparse point."); return File.ReadAllBytes(path); }
    private static byte[]? TryRead(string path, long max) { try { return ReadBounded(path, max); } catch { return null; } }
    private static byte[] ReadEntry(ZipArchiveEntry entry, long max) { if (entry.Length < 0 || entry.Length > max) throw new InvalidDataException("ZIP entry exceeds the bounded size."); using var s = entry.Open(); using var m = new MemoryStream(); s.CopyTo(m); if (m.Length > max) throw new InvalidDataException("ZIP entry exceeds the bounded size."); return m.ToArray(); }
    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes) { if (name.StartsWith('/') || name.Split('/').Contains("..")) throw new InvalidDataException("Unsafe archive path."); using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open(); stream.Write(bytes); }
    private static string Fingerprint(IEnumerable<SourceFile> files) { using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); foreach (var file in files.OrderBy(f => f.Manifest.ArchivePath, StringComparer.Ordinal)) { hash.AppendData(Encoding.UTF8.GetBytes(file.Manifest.Role + "\0" + file.Manifest.ArchivePath + "\0")); hash.AppendData(file.Bytes); } return Convert.ToHexString(hash.GetHashAndReset()); }
    private static string ResolveReferencedProfile(string reference, string root) { var path = Path.GetFullPath(Path.IsPathRooted(reference) ? reference : Path.Combine(root, reference)); if (!IsWithin(path, root) || !File.Exists(path)) { var name = Path.GetFileName(reference); path = Path.Combine(root, name); } if (!IsWithin(path, root) || !File.Exists(path) || IsReparse(path)) throw new InvalidDataException("Profile reference is outside the approved Profiles directory."); return path; }
    private static void ValidateRoots(LaboratoryEvidenceBundleDirectories d) { foreach (var root in new[] { d.Scans, d.Profiles }) { if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("A required evidence directory is missing or unsafe."); } foreach (var root in new[] { d.PeriodDiscovery, d.CustomDuplex, d.Repeatability, d.SustainedDuplex }.Where(r => Directory.Exists(r))) if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("An evidence directory is a reparse point."); }
    private static bool IsWithin(string path, string root) { var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; return p.StartsWith(r, StringComparison.OrdinalIgnoreCase); }
    private static bool IsReparse(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
}
