using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record DuplexCandidateAssessment(string CaptureEndpointId, string RenderEndpointId, int SampleRate, string Encoding, int Channels, string Period, string Mmcss, int DurationSeconds, string Classification, string Confidence, string? CaptureBufferFrames, string? RenderBufferFrames, string? CaptureCoverage, string? RenderCoverage, string Reason, JsonElement Report);
public sealed record DuplexRecommendation(string Scope, string AudioHostEligibility, DuplexCandidateAssessment? Preferred, DuplexCandidateAssessment? Fallback, IReadOnlyList<string> Reasons);
public sealed record DuplexResultCard(string Period, string FinalStatus, string CaptureBuffer, string RenderBuffer, string CaptureInitialize, string RenderInitialize, string CaptureStart, string RenderStart, string CaptureFrames, string RenderFrames, string CaptureCoverage, string RenderCoverage, string CaptureQuality, string RenderQuality, string DuplexQuality, string Faults, string Runtime, string StopsCleanup, string Details)
{
    public string BufferSummary => $"{CaptureBuffer} / {RenderBuffer}";
    public string InitializeSummary => $"{CaptureInitialize} / {RenderInitialize}";
    public string StartSummary => $"{CaptureStart} / {RenderStart}";
    public string FramesSummary => $"{CaptureFrames} / {RenderFrames}";
    public string CoverageSummary => $"{CaptureCoverage} / {RenderCoverage}";
    public string QualitySummary => $"{CaptureQuality} / {RenderQuality} / {DuplexQuality}";
}

public static class DeviceConfigurationEvaluator
{
    public static DuplexResultCard ProjectCard(string period, DuplexValidationDto? validation)
    {
        if (validation is null) return new(period, "NOT_RUN", "UNKNOWN", "UNKNOWN", "NOT_RUN", "NOT_RUN", "NOT_RUN", "NOT_RUN", "UNKNOWN", "UNKNOWN", "UNKNOWN", "UNKNOWN", "NOT_RUN", "NOT_RUN", "NOT_RUN", "UNKNOWN", "UNKNOWN", "UNKNOWN", "Проверка этого периода не запускалась.");
        var report = validation.Report; var c = report.GetProperty("capture"); var r = report.GetProperty("render"); var d = report.GetProperty("duplex");
        string Text(JsonElement e, string n) => e.GetProperty(n).ValueKind == JsonValueKind.Null ? "UNKNOWN" : e.GetProperty(n).ToString();
        string Init(JsonElement e) => Text(e, "initializeHresult") == "UNKNOWN" ? "UNKNOWN" : Text(e, "initializeHresult");
        string Buffer(JsonElement e) => Text(e, "actualBufferFrames");
        string Frames(JsonElement e) => $"{Text(e, "framesProcessed")} / {Text(e, "expectedFrames")}";
        var faults = string.Join(" · ", new[] { "serviceFaults", "packetDrainLimitHits", "intermediateWaitTimeouts", "longEventIntervals", "discontinuities", "timestampErrors", "devicePositionAnomalies", "unavailableCaptureBuffers" }.Select(n => $"{n} C:{Text(c,n)} R:{Text(r,n)}")) + $" · Render SILENT buffers={Text(r,"silentBuffersServiced")}";
        var stops = $"C Stop={Text(c,"stopHresult")} / {Text(c,"cleanup")} · R Stop={Text(r,"stopHresult")} / {Text(r,"cleanup")}";
        var details = $"operationId={report.GetProperty("operationId").GetString()} · provenance={report.GetProperty("provenance").GetString()} · events C/R={Text(c,"events")}/{Text(r,"events")} · interval median/P95/P99 C={Text(c,"eventIntervalMedianMilliseconds")}/{Text(c,"eventIntervalP95Milliseconds")}/{Text(c,"eventIntervalP99Milliseconds")} ms R={Text(r,"eventIntervalMedianMilliseconds")}/{Text(r,"eventIntervalP95Milliseconds")}/{Text(r,"eventIntervalP99Milliseconds")} ms · Capture/Render watchdog={Text(c,"watchdogTimedOut")}/{Text(r,"watchdogTimedOut")} device-loss={Text(c,"deviceInvalidated")}/{Text(r,"deviceInvalidated")} · peer-stop={Text(d,"peerStopRequested")} · errors C:{Text(c,"errorStage")} ({Text(c,"errorHresult")}), R:{Text(r,"errorStage")} ({Text(r,"errorHresult")}) · AudioHost=NOT_CERTIFIED · Physical RTL=NOT_MEASURED";
        return new(period, Text(d,"status"), Buffer(c), Buffer(r), Init(c), Init(r), Text(c,"startHresult"), Text(r,"startHresult"), Frames(c), Frames(r), Coverage(c.GetProperty("framesProcessed").GetInt64(), c.GetProperty("expectedFrames").GetInt64()), Coverage(r.GetProperty("framesProcessed").GetInt64(), r.GetProperty("expectedFrames").GetInt64()), Text(c,"quality"), Text(r,"quality"), Text(d,"status"), faults, Text(d,"simultaneousRuntimeMilliseconds"), stops, details);
    }
    public static DuplexCandidateAssessment Evaluate(string period, JsonElement report, string captureId, string renderId, string mmcss)
    {
        if (period is not ("MINIMUM" or "DEFAULT") && !IsCustomPeriod(period)) throw new ArgumentOutOfRangeException(nameof(period));
        if (report.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Duplex report must be an object.");
        var requested = report.GetProperty("requested");
        if (requested.GetProperty("captureEndpointId").GetString() != captureId || requested.GetProperty("renderEndpointId").GetString() != renderId || requested.GetProperty("period").GetString() != period || requested.GetProperty("mmcss").GetString() != mmcss || requested.GetProperty("sampleRateHz").GetInt32() != 48000 || requested.GetProperty("channels").GetInt32() != 2 || requested.GetProperty("encoding").GetString() != "PCM16")
            return Result("INCOMPLETE", "Низкая", "Отчёт не совпадает с точной парой и настройками кандидата.");
        if (report.GetProperty("provenance").GetString() != "REAL_WASAPI_DUPLEX") return Result("INCOMPLETE", "Низкая", "Синтетическое или неизвестное происхождение не является реальным Duplex evidence.");
        var cap = report.GetProperty("capture"); var ren = report.GetProperty("render"); var duplex = report.GetProperty("duplex");
        var status = duplex.GetProperty("status").GetString();
        var capFrames = cap.GetProperty("framesProcessed").GetInt64(); var renFrames = ren.GetProperty("framesProcessed").GetInt64();
        var capExpected = cap.GetProperty("expectedFrames").GetInt64(); var renExpected = ren.GetProperty("expectedFrames").GetInt64();
        var capCoverage = Coverage(capFrames, capExpected); var renCoverage = Coverage(renFrames, renExpected);
        var capBuffer = NullableLong(cap, "actualBufferFrames"); var renBuffer = NullableLong(ren, "actualBufferFrames");
        var reason = "";
        var classification = "INCOMPLETE";
        if (status is "FAILED") { classification = "FAILED"; reason = "Worker прислал FAILED."; }
        else if (status is "DUPLEX_WARNINGS") { classification = "DUPLEX_WARNINGS"; reason = "Duplex завершён с предупреждениями; кандидат не допускается к clean рекомендации."; }
        else if (status == "DUPLEX_CLEAN" && CleanSide(cap, capExpected) && CleanSide(ren, renExpected) && duplex.GetProperty("bothReadyBeforeStart").GetBoolean() && duplex.GetProperty("coordinatedStartAuthorized").GetBoolean() && !duplex.GetProperty("cancelled").GetBoolean() && !duplex.GetProperty("peerStopRequested").GetBoolean() && duplex.GetProperty("simultaneousRuntimeMilliseconds").ValueKind == JsonValueKind.Number && duplex.GetProperty("simultaneousRuntimeMilliseconds").GetDouble() >= requested.GetProperty("durationSeconds").GetInt32() * 900d)
        { classification = "DUPLEX_CLEAN_SHORT_RUN"; reason = "Обе стороны обслуживали потоки в течение ограниченного теста; длительная стабильность и физическая синхронизация не проверялись."; }
        else if (status == "CANCELLED") { classification = "INCOMPLETE"; reason = "Операция отменена; исход незавершённых стадий неизвестен."; }
        else { reason = "Не хватает успешного Start, достаточной подачи кадров, Duplex overlap или завершения/cleanup."; }
        return new(captureId, renderId, 48000, "PCM16", 2, period, mmcss, requested.GetProperty("durationSeconds").GetInt32(), classification, classification == "DUPLEX_CLEAN_SHORT_RUN" ? "PROVISIONAL" : "LOW", capBuffer?.ToString() ?? "UNKNOWN", renBuffer?.ToString() ?? "UNKNOWN", capCoverage, renCoverage, reason, report.Clone());

        DuplexCandidateAssessment Result(string kind, string confidence, string why) => new(captureId, renderId, 48000, "PCM16", 2, period, mmcss, 0, kind, confidence, "UNKNOWN", "UNKNOWN", "UNKNOWN", "UNKNOWN", why, report.Clone());
    }

    public static DuplexRecommendation Recommend(IEnumerable<DuplexCandidateAssessment> candidates)
    {
        var candidateList = candidates.ToArray();
        if (candidateList.GroupBy(c => c.Period, StringComparer.Ordinal).Any(g => g.Count() > 1))
            return new("NONE", "NOT_CERTIFIED", null, null, ["Для одного периода получено несколько кандидатов; автоматический выбор из неоднозначных записей заблокирован."]);
        var byPeriod = candidateList.ToDictionary(c => c.Period, StringComparer.Ordinal);
        byPeriod.TryGetValue("MINIMUM", out var minimum); byPeriod.TryGetValue("DEFAULT", out var standard);
        var selected = new[] { minimum, standard }.Where(c => c is not null).Cast<DuplexCandidateAssessment>().ToArray();
        if (selected.Any(c => c.SampleRate != 48000 || c.Encoding != "PCM16" || c.Channels != 2 || c.DurationSeconds is < 1 or > 10) ||
            selected.Select(c => (c.CaptureEndpointId, c.RenderEndpointId, c.SampleRate, c.Encoding, c.Channels, c.Mmcss, c.DurationSeconds)).Distinct().Count() > 1)
            return new("NONE", "NOT_CERTIFIED", null, null, ["Кандидаты относятся к разным точным парам или несовместимым настройкам; они не ранжируются совместно."]);
        var minimumClean = minimum?.Classification == "DUPLEX_CLEAN_SHORT_RUN";
        var defaultClean = standard?.Classification == "DUPLEX_CLEAN_SHORT_RUN";
        if (minimumClean)
            return new("PROBE_PROVISIONAL", "NOT_CERTIFIED", minimum, defaultClean ? standard : null,
                ["Кандидат MINIMUM выбран по чистому короткому Duplex evidence для этой точной пары.", "Рекомендация не разрешает активацию AudioHost; требуется длительная и production QA."]);
        if (defaultClean)
            return new("PROBE_PROVISIONAL", "NOT_CERTIFIED", standard, null,
                ["DEFAULT выбран: MINIMUM не имеет независимого чистого Duplex подтверждения.", "Проверка ограничена коротким запуском; AudioHost не сертифицирован."]);
        return new("NONE", "NOT_CERTIFIED", null, null,
            ["Нет кандидата с полным чистым Duplex evidence для этой точной пары.", "Формат support и отдельные Capture/Render результаты не заменяют совместную Duplex проверку."]);
    }

    private static bool CleanSide(JsonElement side, long expected) => expected > 0 && side.GetProperty("ready").GetBoolean() && side.GetProperty("startSucceeded").GetBoolean() && side.GetProperty("stopSucceeded").GetBoolean() && side.GetProperty("durationReached").GetBoolean() && !side.GetProperty("cancelled").GetBoolean() && !side.GetProperty("watchdogTimedOut").GetBoolean() && !side.GetProperty("deviceInvalidated").GetBoolean() && Success(side, "isFormatSupportedHresult") && Success(side, "initializeHresult") && Success(side, "startHresult") && Success(side, "stopHresult") && side.GetProperty("actualBufferFrames").ValueKind == JsonValueKind.Number && side.GetProperty("actualBufferFrames").GetInt64() > 0 && side.GetProperty("quality").GetString() == "CLEAN" && side.GetProperty("cleanup").GetString() == "RELEASE_ATTEMPTED" && side.GetProperty("events").GetInt64() > 0 && side.GetProperty("framesProcessed").GetInt64() >= expected * 9 / 10 && side.GetProperty("discontinuities").GetInt64() == 0 && side.GetProperty("timestampErrors").GetInt64() == 0 && side.GetProperty("serviceFaults").GetInt64() == 0 && side.GetProperty("packetDrainLimitHits").GetInt64() == 0 && side.GetProperty("longEventIntervals").GetInt64() == 0 && (side.GetProperty("devicePositionAnomalies").ValueKind == JsonValueKind.Null || side.GetProperty("devicePositionAnomalies").GetInt64() == 0) && (side.GetProperty("unavailableCaptureBuffers").ValueKind == JsonValueKind.Null || side.GetProperty("unavailableCaptureBuffers").GetInt64() == 0) && (side.GetProperty("silentBuffersServiced").ValueKind != JsonValueKind.Null || side.GetProperty("direction").GetString() != "RENDER") && (side.GetProperty("direction").GetString() != "RENDER" || side.GetProperty("silentBuffersServiced").GetInt64() > 0) && side.GetProperty("frameCoveragePercent").ValueKind == JsonValueKind.Number && side.GetProperty("frameCoveragePercent").GetDouble() >= 90;
    private static bool Success(JsonElement side, string name) => side.GetProperty(name).ValueKind == JsonValueKind.String && side.GetProperty(name).GetString() is "S_OK" or "0x00000000";
    private static string Coverage(long frames, long expected) => expected <= 0 ? "UNKNOWN" : $"{Math.Clamp(frames * 100d / expected, 0, 1000):0.00}%";
    private static long? NullableLong(JsonElement parent, string name) => parent.GetProperty(name).ValueKind == JsonValueKind.Number ? parent.GetProperty(name).GetInt64() : null;
    private static bool IsCustomPeriod(string value) => value.Split(':') is ["CUSTOM", var duration, var frames] && long.TryParse(duration, out var period100ns) && uint.TryParse(frames, out var requestedFrames) && period100ns > 0 && requestedFrames > 0 && requestedFrames <= 48_000 && ((period100ns >= ((long)requestedFrames * 10_000_000 + 24_000) / 48_000 ? period100ns - ((long)requestedFrames * 10_000_000 + 24_000) / 48_000 : ((long)requestedFrames * 10_000_000 + 24_000) / 48_000 - period100ns) <= 1);
}
