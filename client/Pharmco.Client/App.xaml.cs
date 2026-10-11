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

        // Dev convenience: PHARMCO_PLAINTEXT_SQLITE=1 skips the SQLCipher PRAGMA
        // (bare SQLite ignores it, but never ship pilots with this).
        var apiBaseUrl = Environment.GetEnvironmentVariable("PHARMCO_API_URL")
            ?? "http://localhost:8080";

        var builder = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();

                // Local database (SQLite with SQLCipher)
                var dbPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Pharmco", "pharmco.db");
                var encryptionKey = Environment.GetEnvironmentVariable("Pharmco__DatabaseKey");
                services.AddSingleton(new LocalDatabase(dbPath, encryptionKey));

                // Sync engine (baseUrl is config, not resolvable by DI)
                services.AddSingleton(sp => new SyncEngine(
                    sp.GetRequiredService<LocalDatabase>(),
                    sp.GetRequiredService<ILogger<SyncEngine>>(),
                    apiBaseUrl));

                services.AddSingleton<LicenseValidator>();
                services.AddSingleton(sp => new LicenseEnforcementService(
                    sp.GetRequiredService<LocalDatabase>(),
                    sp.GetRequiredService<LicenseValidator>(),
                    sp.GetRequiredService<ILogger<LicenseEnforcementService>>()));

                // Auth stack (Week 2)
                services.AddSingleton(new AuthService(apiBaseUrl));
                services.AddSingleton(CredentialCache.OpenDefault());
                services.AddSingleton(sp => new SessionManager(
                    sp.GetRequiredService<AuthService>(),
                    sp.GetRequiredService<CredentialCache>(),
                    new SessionManager.Options()));

                services.AddHttpClient();
            });

        _host = builder.Build();
        await _host.StartAsync();

        Services = _host.Services;

        // Start sync engine
        var syncEngine = Services.GetRequiredService<SyncEngine>();
        var syncCts = new System.Threading.CancellationTokenSource();
        await syncEngine.StartAsync(syncCts.Token);

        // Load cached license. A missing cache is a first-run state, not a fault:
        // the app launches with a banner (the license arrives via sync/login).
        // Invalid signatures or clock tampering still hard-lock (Tier=HardLock).
        var licenseService = Services.GetRequiredService<LicenseEnforcementService>();
        await licenseService.LoadCachedLicenseAsync();

        var state = licenseService.State;
        if (state.Tier >= LicenseEnforcementService.LicenseTier.HardLock)
        {
            MessageBox.Show(
                $"License error: {state.Error}\n\nPlease contact Pharmco to renew your license.",
                "License Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Show main window (StartupUri is intentionally not used — the license
        // gate above must run before any window appears).
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
