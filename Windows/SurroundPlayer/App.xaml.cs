using System;
using System.Linq;
using Microsoft.UI.Xaml;

namespace SurroundPlayer;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        Native.timeBeginPeriod(1);   // 1 ms sleep granularity for the engine's threads
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
        // --vis-snapshot <dir>: render every visualiser preset to PNG with synthetic music, then quit
        int snap = Array.IndexOf(argv, "--vis-snapshot");
        if (snap >= 0 && snap + 1 < argv.Length)
        {
            int rc = 0;
            try
            {
                await Visualizer.VisualizerRenderer.WriteSnapshotsAsync(argv[snap + 1]);
            }
            catch (Exception e)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(argv[snap + 1], "error.txt"), e.ToString());
                rc = 1;
            }
            Environment.Exit(rc);
            return;
        }
        // files passed on the command line / "Open with" are added and played
        var files = argv.Where(a => !a.StartsWith("--")).ToArray();
        _window = new MainWindow(files);
        _window.Activate();
    }
}
