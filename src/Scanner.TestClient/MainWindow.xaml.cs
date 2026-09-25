using System.ComponentModel;
using System.Windows;
namespace Scanner.TestClient;
public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _closing, _closed;
    public MainWindow(string? settingsPath = null)
    {
        InitializeComponent(); DataContext = _model = new(Dispatcher, settingsPath); Closing += OnClosing;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return; e.Cancel = true; if (_closing) return; _closing = true;
        try { await _model.DisposeAsync(); }
        finally { _ = Dispatcher.BeginInvoke(() => { _closed = true; Close(); }); }
    }
}
