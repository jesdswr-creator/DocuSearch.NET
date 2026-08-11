using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace DocuSearch.Avalonia;

class Program
{
    // STAThread needed for Windows COM interop (clipboard, file dialogs)
    [STAThread]
    static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
