using System.Globalization;

namespace KlidanAudioProbe.Gui.Core;

public sealed record HistoricalSustainedScanEntry(
    string SummaryPath,
    string SessionId,
    string DeviceName,
    DateTimeOffset TimestampUtc,
    string Pair,
    bool HasSustainedEvidence,
    bool IsRecovered,
    bool IsAvailable,
    string AvailabilityReason)
{
    public string TimestampLabel => TimestampUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
    public string EvidenceLabel => HasSustainedEvidence ? "Sustained evidence: есть" : "Sustained evidence: нет";
    public string RecoveryLabel => IsRecovered ? "Уже восстановлен" : IsAvailable ? "Проверка доступна" : AvailabilityReason;
    public string TechnicalDetails => $"Session: {SessionId}\nSummary: {SummaryPath}";
}

public static class HistoricalSustainedScanCatalog
{
    private const int MaximumFiles = 128;
    private const long MaximumBytes = 16 * 1024 * 1024;

    public static IReadOnlyList<HistoricalSustainedScanEntry> Discover(string scansDirectory, string profilesDirectory)
    {
        if (!Directory.Exists(scansDirectory)) return [];
        var profiles = DeviceConfigurationProfileStorage.DiscoverLocal(profilesDirectory);
        var result = new List<HistoricalSustainedScanEntry>();
        long totalBytes = 0;
        foreach (var path in Directory.EnumerateFiles(scansDirectory, "scan-*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(MaximumFiles))
        {
            try
            {
                var length = new FileInfo(path).Length;
                if (length <= 0 || totalBytes + length > MaximumBytes)
                {
                    result.Add(Unavailable(path, "Файл не прочитан: превышен лимит исторического каталога."));
                    continue;
                }
                totalBytes += length;
                var summary = DeviceConfigurationScanStorage.LoadSummary(path);
                var profile = summary.ProfilePaths.Select(p => Path.GetFullPath(p))
                    .Select(p => profiles.FirstOrDefault(x => string.Equals(Path.GetFullPath(x.Path), p, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(p => p?.Profile is not null);
                var sustained = summary.ChildOperations.Any(c => c.Stage == "SUSTAINED_VALIDATION");
                var recovered = profiles.Any(p => p.IsSustained && string.Equals(p.Profile?.SustainedSelection?.SourceScanSessionId, summary.SessionId, StringComparison.OrdinalIgnoreCase));
                var pair = string.IsNullOrWhiteSpace(summary.RenderEndpointId) ? "Capture / Render: неполная пара" : "Capture / Render: точная пара сохранена";
                var available = summary.Lifecycle == "COMPLETED" && sustained && profile?.Profile?.SchemaVersion == 3;
                var reason = available ? "" : summary.Lifecycle != "COMPLETED" ? "Сканирование не завершено." : !sustained ? "Sustained evidence не найдена." : profile is null ? "Исходный v3 профиль недоступен." : "Исходный профиль не является v3.";
                result.Add(new(path, summary.SessionId, profile?.OperatorDisplayName ?? "Историческое устройство", summary.TimestampUtc, pair, sustained, recovered, available, reason));
            }
            catch (Exception ex) { result.Add(Unavailable(path, $"Исторический файл отклонён: {ex.Message}")); }
        }
        return result.OrderByDescending(x => x.TimestampUtc).ThenBy(x => x.DeviceName, StringComparer.CurrentCulture).ThenBy(x => x.SummaryPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static HistoricalSustainedScanEntry Unavailable(string path, string reason) =>
        new(path, "UNKNOWN", "Историческое измерение недоступно", DateTimeOffset.MinValue, "Capture / Render: неизвестно", false, false, false, reason);
}
