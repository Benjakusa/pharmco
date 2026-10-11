using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Pharmco.Client.Services;

namespace Pharmco.Client;

public partial class LoginWindow : Window
{
    private readonly SessionManager _sessionManager;
    private readonly SyncEngine _syncEngine;
    private readonly LicenseEnforcementService _licenseService;

    public LoginWindow()
    {
        InitializeComponent();

        _sessionManager = App.Services.GetRequiredService<SessionManager>();
        _syncEngine = App.Services.GetRequiredService<SyncEngine>();
        _licenseService = App.Services.GetRequiredService<LicenseEnforcementService>();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var pharmacyCode = PharmacyCodeInput.Text.Trim();
        var username = UsernameInput.Text.Trim();
        var password = PasswordInput.Password;

        if (string.IsNullOrEmpty(pharmacyCode)
            || string.IsNullOrEmpty(username)
            || string.IsNullOrEmpty(password))
        {
            MessageBox.Show("Please enter pharmacy code, username and password.",
                "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Online first (writes the offline cache as a side effect), offline as fallback.
        var outcome = await _sessionManager.LoginOnlineAsync(pharmacyCode, username, password);
        if (!outcome.Allowed)
            outcome = _sessionManager.LoginOffline(pharmacyCode, username, password);

        if (!outcome.Allowed)
        {
            var detail = outcome.Reason switch
            {
                "server_rejected" => "The server rejected these credentials.",
                "invalid_password" => "Wrong password (offline cache).",
                "cache_stale" => "Offline cache is older than 7 days — connect to the internet once.",
                _ => "No cached credentials for this pharmacy — connect to the internet for the first login.",
            };
            MessageBox.Show($"Login failed. {detail}", "Login Failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var session = outcome.Session!;
        MessageBox.Show(
            $"Login successful! Welcome to Pharmco POS.\n\nTenant: {session.User.TenantCode}\nRole: {session.User.Role}\nOffline: {session.Offline}",
            "Welcome", MessageBoxButton.OK, MessageBoxImage.Information);

        // Best-effort sync kick (fails harmlessly when the API is unreachable).
        try { await _syncEngine.ForceSyncAsync(); }
        catch { /* offline — the background engine retries on its schedule */ }

        DialogResult = true;
        Close();
    }
}
