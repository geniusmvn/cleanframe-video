using Microsoft.UI.Xaml;

namespace Erasa.Video2.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();

        var workerFailureSmokePath = Environment.GetEnvironmentVariable("ERASA_WORKER_FAILURE_SMOKE_PATH");
        if (!string.IsNullOrWhiteSpace(workerFailureSmokePath))
        {
            MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                await MainWindow.RunWorkerFailureSmokeAsync(workerFailureSmokePath);
            });
            return;
        }

        var smokePath = Environment.GetEnvironmentVariable("ERASA_SMOKE_TEST_PATH");
        if (!string.IsNullOrWhiteSpace(smokePath))
        {
            MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(smokePath) ?? ".");
                    File.WriteAllText(smokePath, "ERASA_VIDEO_UI_STARTED");
                }
                finally
                {
                    MainWindow.Close();
                }
            });
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Services.AppLog.Write("Unhandled UI exception", e.Exception);
        e.Handled = true;
        if (MainWindow is not null) MainWindow.ShowFatalError(e.Exception.Message);
    }
}
