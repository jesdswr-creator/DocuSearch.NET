using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DocuSearch.Avalonia.ViewModels;

namespace DocuSearch.Avalonia;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm };
            vm.MainWindow = window;
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
