using KlidanAudioProbe.Gui.Core;
using System.IO;

namespace KlidanAudioProbe.Gui.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        var identityIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--write-build-identity", StringComparison.OrdinalIgnoreCase));
        if (identityIndex >= 0)
        {
            if (identityIndex + 1 >= e.Args.Length || !Path.IsPathFullyQualified(e.Args[identityIndex + 1])) throw new ArgumentException("--write-build-identity requires an absolute output file path.");
            var entry = System.Reflection.Assembly.GetEntryAssembly()!;
            var metadata = entry.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).Cast<System.Reflection.AssemblyMetadataAttribute>();
            var commit = metadata.SingleOrDefault(a => a.Key == "KlidanSourceCommit")?.Value ?? string.Empty;
            var clean = metadata.SingleOrDefault(a => a.Key == "KlidanSourceTreeClean")?.Value == "true";
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(executable)));
            File.WriteAllText(e.Args[identityIndex + 1], System.Text.Json.JsonSerializer.Serialize(new { sourceCommit = commit, sourceTreeClean = clean, executableSha256 = sha }));
            Shutdown(0);
            return;
        }
        EndpointDiscoveryQaTrace.StartFromEnvironment();
        base.OnStartup(e);
        Localization.Initialize();
        var developerTools = e.Args.Any(a => string.Equals(a, "--developer-tools", StringComparison.OrdinalIgnoreCase));
        string? qaRoot = null;
        var qaIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--qa-root", StringComparison.OrdinalIgnoreCase));
        if (qaIndex >= 0)
        {
            if (qaIndex + 1 >= e.Args.Length || !Path.IsPathFullyQualified(e.Args[qaIndex + 1])) throw new ArgumentException("--qa-root requires an absolute directory path.");
            qaRoot = Path.GetFullPath(e.Args[qaIndex + 1]);
            Directory.CreateDirectory(Path.Combine(qaRoot, "evidence"));
            Directory.CreateDirectory(Path.Combine(qaRoot, "profiles"));
        }
        MainWindow = new MainWindow(null, qaRoot is null ? null : Path.Combine(qaRoot, "profiles"), developerTools, qaRoot is null ? null : Path.Combine(qaRoot, "evidence"));
        MainWindow.Show();
    }
}
