using System.Text.Json;
using KlidanAudioProbe.Gui.Core;

var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[i][2..], args[i + 1]))
    {
        Console.Error.WriteLine("Invalid or duplicate command-line option.");
        return 64;
    }
}
string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException($"Missing --{name}.");
try
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Historical DEVICE profile import requires Windows endpoint metadata.");
    if (!values.TryGetValue("confirm-operator-profile-write", out var confirmed) || confirmed != "I_CONFIRM_DEVICE_PROFILE_V5")
        throw new InvalidDataException("Explicit operator confirmation is required.");
    var output = HistoricalAudioHostProfileImporter.ImportToLocalProfile(new(
        Path.GetFullPath(Required("scanner-run")), Path.GetFullPath(Required("evidence-index")), Path.GetFullPath(Required("minimum-audit")),
        Required("minimum-audit-sha256"), Path.GetFullPath(Required("audiohost-manifest")), Required("audiohost-manifest-sha256"),
        Path.GetFullPath(Required("audiohost-run")), Path.GetFullPath(Required("output-directory"))));
    Console.WriteLine($"DEVICE_PROFILE_V5_CREATED={output}");
    Console.WriteLine($"PROFILE_SHA256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(output)))}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"HISTORICAL_PROFILE_IMPORT_BLOCKED={ex.GetType().Name}: {ex.Message}");
    return 1;
}
