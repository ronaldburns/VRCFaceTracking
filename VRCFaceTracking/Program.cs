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
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI();

        /*if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null)
        {
            builder = builder.UseWayland();
        }*/

        return builder;
    }
}