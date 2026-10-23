using Avalonia;
using ReactiveUI.Avalonia;
using Velopack;

namespace VRCFaceTracking;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Has to run first. Handles the installer's hooks and applies an update downloaded last time
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI();
}