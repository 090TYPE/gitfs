using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Gitfs.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            // монтирование снимается при выходе, иначе том остаётся висеть
            desktop.ShutdownRequested += (_, _) => MountService.Instance.UnmountAll();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
