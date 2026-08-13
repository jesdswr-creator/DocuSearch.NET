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
            var crashPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DocuSearch", "crash.txt");
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashPath)!);
                System.IO.File.WriteAllText(crashPath,
                    $"{DateTime.Now}\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
            }
            catch { }
        }
    }

    static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
