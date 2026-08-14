using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DocuSearch.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ViewModel?.Initialize();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ViewModel?.SearchCommand.Execute(null);
    }
}
