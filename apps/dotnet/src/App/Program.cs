using Avalonia;

namespace Qwen3TtsStudio.App;

internal static class Program
{
    // Avalonia desktop entry point. Runs on Windows and Linux.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
