namespace KlidanAudioProbe.Gui.Core;

public static class WorkerScanV2PlanPreparation
{
    public static WorkerScanV2ContractV4 Prepare(string captureId, string renderId, WorkerScanV2Route route,
        RealFormatMatrixDto captureMatrix, RealFormatMatrixDto renderMatrix, ulong discoveryGeneration, int repeatabilityRuns,
        int? diagnosticPeriodMilliseconds = null, string diagnosticMmcssMode = "OFF")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderId);
        ArgumentNullException.ThrowIfNull(route);
        if (captureId == renderId || discoveryGeneration == 0) throw new InvalidDataException("Fresh distinct endpoints and a current Worker discovery generation are required.");
        var captureBounds = ReadBounds(captureMatrix, captureId, "CAPTURE", route.Capture);
        var renderBounds = ReadBounds(renderMatrix, renderId, "RENDER", route.Render);
        IReadOnlyList<WorkerScanV2Candidate> candidates;
        if (diagnosticPeriodMilliseconds is { } diagnosticMs)
        {
            if (diagnosticMs is not (5 or 10) || repeatabilityRuns != 0 || diagnosticMmcssMode is not ("OFF" or "PRO_AUDIO") || route.Mode != "MONO_TO_STEREO" ||
                route.Capture.SampleRateHz != 48_000 || route.Render.SampleRateHz != 48_000 ||
                route.Capture.Encoding != "PCM16" || route.Render.Encoding != "PCM16")
                throw new InvalidDataException("Point diagnostics require the reviewed 48 kHz PCM16 MONO_TO_STEREO route, 5/10 ms, and R=0.");
            static int Frames(long period, int rate) => checked((int)(((decimal)period * rate + 5_000_000m) / 10_000_000m));
            var candidate = WorkerScanV2CandidatePlanning.Make(0, diagnosticMs * 10_000L, route,
                Frames(captureBounds.MinimumPeriod100ns, route.Capture.SampleRateHz), Frames(captureBounds.DefaultPeriod100ns, route.Capture.SampleRateHz),
                Frames(renderBounds.MinimumPeriod100ns, route.Render.SampleRateHz), Frames(renderBounds.DefaultPeriod100ns, route.Render.SampleRateHz));
            if (candidate.RejectionReason.Length != 0) throw new InvalidDataException("The exact diagnostic candidate is outside confirmed device bounds or geometry.");
            candidates = [candidate];
        }
        else candidates = WorkerScanV2CandidatePlanning.GenerateFromBounds(route, captureBounds, renderBounds);
        if (candidates.Count == 0) throw new InvalidDataException("Capture and Render reported period ranges do not produce any unique candidate periods.");
        var budget = WorkerScanV2ContractV4Fingerprint.Budget(candidates.Count, repeatabilityRuns);
        return new(route, candidates, [diagnosticPeriodMilliseconds is null ? "PRO_AUDIO" : diagnosticMmcssMode], repeatabilityRuns, budget.MaximumOperations, budget.MaximumActiveDuplexSeconds,
            30, budget.SessionLifetimeSeconds, 8, captureBounds, renderBounds);
    }

    private static WorkerScanV2PeriodBounds ReadBounds(RealFormatMatrixDto matrix, string endpointId, string direction, WorkerScanV2Side side)
    {
        if (matrix is null || matrix.Provenance != "REAL_WASAPI_QUERY" || matrix.ReportLevel != "PASSIVE_QUERY" || matrix.EndpointId != endpointId || matrix.Direction != direction || matrix.Candidates is null)
            throw new InvalidDataException($"{direction} passive-query identity, provenance, or report level is not current and exact.");
        var format = matrix.Candidates.Where(c => c.SampleRate == side.SampleRateHz && c.Channels == side.Channels && c.Encoding == side.Encoding).ToArray();
        if (format.Length != 2 || format.Count(c => c.Period == "minimum") != 1 || format.Count(c => c.Period == "default") != 1)
            throw new InvalidDataException($"{direction} query is missing a unique minimum/default row for the selected rate, channels, and encoding.");
        foreach (var candidate in format)
            if (candidate.Status != "SUPPORTED_REPORTED" || candidate.SupportStatus != "SUPPORTED_REPORTED" || candidate.ActivationHr != "0x00000000" || candidate.SupportHr != "0x00000000" || candidate.DevicePeriodHr != "0x00000000" || candidate.MinimumPeriod100ns is not > 0 || candidate.DefaultPeriod100ns is not > 0 || candidate.MinimumPeriod100ns > candidate.DefaultPeriod100ns || candidate.QueryCleanupStatus != "RELEASE_ATTEMPTED" || candidate.QueryClientReleaseAttempts == 0 || candidate.InitializeAttempted || candidate.InitializeStatus != "NOT_TESTED")
                throw new InvalidDataException($"{direction} bounds are unsupported, conflicting, incomplete, or lack confirmed passive-query cleanup.");
        var first = format[0]; var second = format[1];
        if (first.MinimumPeriod100ns != second.MinimumPeriod100ns || first.DefaultPeriod100ns != second.DefaultPeriod100ns || first.ActivationHr != second.ActivationHr || first.DevicePeriodHr != second.DevicePeriodHr || first.QueryCleanupStatus != second.QueryCleanupStatus)
            throw new InvalidDataException($"{direction} minimum/default rows conflict on reported device bounds or cleanup.");
        return new(first.MinimumPeriod100ns!.Value, first.DefaultPeriod100ns!.Value, null);
    }
}
