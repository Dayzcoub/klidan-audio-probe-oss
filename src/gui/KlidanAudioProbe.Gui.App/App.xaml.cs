using KlidanAudioProbe.Gui.Core;
using System.IO;

namespace KlidanAudioProbe.Gui.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
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
