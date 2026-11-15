using System.Security.Cryptography;
using System.Text.Json;

namespace KlidanAudioProbe.Gui.Core;

public sealed record VerifiedBuildMetadata(string SourceCommit, string ExecutableSha256);

public static class VerifiedBuildMetadataReader
{
    public static void ValidateDeclaredCommits(VerifiedBuildMetadata metadata, string? sourceCommit, string? buildSha)
    {
        if (sourceCommit is not null && !string.Equals(sourceCommit, metadata.SourceCommit, StringComparison.OrdinalIgnoreCase) ||
            buildSha is not null && !string.Equals(buildSha, metadata.SourceCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Declared source/build commit does not match verified executable metadata.");
    }

    public static VerifiedBuildMetadata Read(string executablePath, string manifestPath, string embeddedSourceCommit, bool embeddedSourceTreeClean)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = manifest.RootElement;
        var commit = root.GetProperty("sourceCommit").GetString();
        if (commit is null || commit.Length != 40 || !commit.All(Uri.IsHexDigit)) throw new InvalidDataException("Build manifest source commit is invalid.");
        if (!embeddedSourceTreeClean || embeddedSourceCommit.Length != 40 || !embeddedSourceCommit.All(Uri.IsHexDigit) ||
            !string.Equals(commit, embeddedSourceCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package source commit does not match the clean source identity embedded in this executable.");
        var relative = Path.GetRelativePath(Path.GetDirectoryName(manifestPath)!, executablePath);
        var entry = root.GetProperty("files").EnumerateArray().SingleOrDefault(item =>
            string.Equals(item.GetProperty("path").GetString()?.Replace('\\', '/'), relative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Build manifest does not identify this executable.");
        var expected = entry.GetProperty("sha256").GetString();
        var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executablePath)));
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidDataException("Executable SHA-256 does not match its build manifest.");
        return new(commit.ToLowerInvariant(), actual);
    }
}
