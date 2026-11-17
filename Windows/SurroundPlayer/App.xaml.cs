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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // files passed on the command line / "Open with" are added and played
        var files = Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith("--")).ToArray();
        _window = new MainWindow(files);
        _window.Activate();
    }
}
