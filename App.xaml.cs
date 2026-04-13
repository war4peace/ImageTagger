using System.Windows;
using Wpf.Ui.Appearance;

namespace ImageTagger;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Apply dark Fluent theme before any window is shown
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        var window = new MainWindow();
        window.Show();
    }
}
