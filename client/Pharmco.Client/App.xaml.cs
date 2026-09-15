using System.Windows;

namespace Pharmco.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // TODO(phase 1):
        //   - read local settings (pharmacy code, last device id, sync state)
        //   - open local SQLite (file-backed SQLite, single writer on the HUB PC)
        //   - self-update check against https://{BASE_DOMAIN}/client/version.json
        //   - show Login window before MainWindow (cached refresh token path)
    }
}