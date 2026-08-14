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
        ViewModel?.Initialize();

        // Wire selection change manually (no source generator)
        var resultsList = this.FindControl<ListBox>("ResultsList");
        if (resultsList != null)
        {
            resultsList.SelectionChanged += (s, ev) =>
            {
                ViewModel!.SelectedResultIndex = resultsList.SelectedIndex;
                ViewModel.OnSelectedResultIndexChanged();
            };
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ViewModel?.SearchCommand.Execute(null);
    }
}
