using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace DocuSearch.Avalonia;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DocuSearch", "crash.txt"),
                $"{DateTime.Now}\n{ex}");
        }
    }

    static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
