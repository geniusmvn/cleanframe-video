using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Erasa.Video.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();
            await Task.Delay(900);
            var main = new MainWindow();
            desktop.MainWindow = main;
            main.Show();
            splash.Close();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
