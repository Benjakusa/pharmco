using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Pharmco.Client.Services;

namespace Pharmco.Client;

public partial class LoginWindow : Window
{
    private readonly SyncEngine _syncEngine;
    private readonly LicenseEnforcementService _licenseService;
    private readonly LocalDatabase _localDb;

    public LoginWindow()
    {
        InitializeComponent();

        _syncEngine = App.Services.GetRequiredService<SyncEngine>();
        _licenseService = App.Services.GetRequiredService<LicenseEnforcementService>();
        _localDb = App.Services.GetRequiredService<LocalDatabase>();
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

        try
        {
            // Validate offline first using cached credentials
            var result = await _licenseService.LoadCachedLicenseAsync();

            if (!result.IsValid)
            {
                MessageBox.Show(
                    $"License error: {result.Error}\n\nPlease contact Pharmco to renew.",
                    "License Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // In production, this would call the auth API
            // For now, simulate successful login
            MessageBox.Show(
                $"Login successful! Welcome to Pharmco POS.\n\nTenant: {result.TenantCode}",
                "Welcome",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // Trigger sync
            await _syncEngine.ForceSyncAsync();

            // Close login and show main window
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Login failed: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
