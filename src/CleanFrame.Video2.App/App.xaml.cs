using Microsoft.UI.Xaml;

namespace CleanFrame.Video2.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var details = e.Exception?.ToString() ?? e.Message;
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CleanFrameVideo2", "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, $"ui-{DateTime.UtcNow:yyyyMMdd}.log"),
                $"[{DateTimeOffset.Now:O}] {details}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
        (_window as MainWindow)?.ShowUnhandledError(e.Message);
    }
}
