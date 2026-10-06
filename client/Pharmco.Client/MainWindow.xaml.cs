using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Pharmco.Client.Models;
using Pharmco.Client.Services;

namespace Pharmco.Client;

public partial class MainWindow : Window
{
    private readonly SyncEngine _syncEngine;
    private readonly LicenseEnforcementService _licenseService;

    public MainWindow()
    {
        InitializeComponent();

        _syncEngine = App.Services.GetRequiredService<SyncEngine>();
        _licenseService = App.Services.GetRequiredService<LicenseEnforcementService>();

        // Bind license state to banner
        LicenseBannerView.LicenseState = _licenseService.State;
        _licenseService.StateChanged += (state) =>
        {
            Dispatcher.Invoke(() => LicenseBannerView.LicenseState = state);
        };

        // Subscribe to sync status changes
        _syncEngine.StatusChanged += (status) =>
        {
            Dispatcher.Invoke(() =>
            {
                SyncIndicator.DataContext = new { Status = status.ToString() };
                FooterText.Text = status switch
                {
                    SyncStatus.Synced => "All changes synced",
                    SyncStatus.Pending => "Pending sync...",
                    SyncStatus.Offline => "Offline mode",
                    SyncStatus.Error => "Sync error",
                    _ => "Ready"
                };
            });
        };

        // Initial status
        SyncIndicator.DataContext = new { Status = _syncEngine.Status.ToString() };
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        // Navigate to login window
        var loginWindow = new LoginWindow();
        loginWindow.Owner = this;
        loginWindow.ShowDialog();
    }
}
