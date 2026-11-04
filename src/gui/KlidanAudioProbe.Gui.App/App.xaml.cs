using KlidanAudioProbe.Gui.Core;

namespace KlidanAudioProbe.Gui.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        Localization.Initialize();
        var developerTools = e.Args.Any(a => string.Equals(a, "--developer-tools", StringComparison.OrdinalIgnoreCase));
        MainWindow = new MainWindow(null, null, developerTools);
        MainWindow.Show();
    }
}
