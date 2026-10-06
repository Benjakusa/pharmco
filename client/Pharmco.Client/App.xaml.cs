using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pharmco.Client.Services;

namespace Pharmco.Client;

public partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Configure services
        var builder = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                // Local database (SQLite with SQLCipher)
                var dbPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Pharmco", "pharmco.db");
                var encryptionKey = Environment.GetEnvironmentVariable("Pharmco__DatabaseKey");
                services.AddSingleton(new LocalDatabase(dbPath, encryptionKey));

                // Sync engine
                var apiBaseUrl = Environment.GetEnvironmentVariable("PHARMCO_API_URL")
                    ?? "https://api.pharmco.co.ke";
                services.AddSingleton(sp => new SyncEngine(
                    sp.GetRequiredService<LocalDatabase>(),
                    sp.GetRequiredService<ILogger<SyncEngine>>(),
                    apiBaseUrl));
                services.AddSingleton<LicenseValidator>();
                services.AddSingleton<LicenseEnforcementService>();

                // HTTP client for API calls
                services.AddHttpClient();
            });

        _host = builder.Build();
        await _host.StartAsync();

        Services = _host.Services;

        // Start sync engine
        var syncEngine = Services.GetRequiredService<SyncEngine>();
        var syncCts = new System.Threading.CancellationTokenSource();
        await syncEngine.StartAsync(syncCts.Token);

        // Load cached license
        var licenseService = Services.GetRequiredService<LicenseEnforcementService>();
        licenseService.LoadCachedLicenseAsync().Wait();

        // Check license tier and show appropriate UI
        var state = licenseService.State;
        if (!state.IsValid || state.Tier >= LicenseEnforcementService.LicenseTier.HardLock)
        {
            MessageBox.Show(
                $"License error: {state.Error}\n\nPlease contact Pharmco to renew your license.",
                "License Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Show main window
        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
