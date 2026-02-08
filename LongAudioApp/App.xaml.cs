using System.Windows;

namespace LongAudioApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AnalyticsService.Initialize();
        AnalyticsService.TrackEvent("app_launch");
    }
}
