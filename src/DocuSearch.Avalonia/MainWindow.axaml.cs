using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DocuSearch.Avalonia.ViewModels;

namespace DocuSearch.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (ViewModel != null)
        {
            ViewModel.MainWindow = this;
            ViewModel.Initialize();
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel != null)
        {
            ViewModel.SearchCommand.Execute(null);
        }
    }
}
