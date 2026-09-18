using System.Windows;
using Pharmco.Client.Services;

namespace Pharmco.Client.Views;

public partial class RenewDialog : Window
{
    private readonly LicenseEnforcementService _enforcementService;
    private readonly SyncEngine _syncEngine;
    private readonly LocalDatabase _localDb;

    public RenewDialog(
        LicenseEnforcementService enforcementService,
        SyncEngine syncEngine,
        LocalDatabase localDb)
    {
        InitializeComponent();
        _enforcementService = enforcementService;
        _syncEngine = syncEngine;
        _localDb = localDb;

        // Populate renewal info
        var info = _enforcementService.GetRenewalInfo();
        PaybillText.Text = info.PaybillNumber;
        AccountRefText.Text = info.AccountReference;
        AmountText.Text = $"KES {info.Amount:N0}";
        PhoneText.Text = info.Phone;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        var mpesaRef = MpesaRefInput.Text.Trim();
        if (string.IsNullOrEmpty(mpesaRef))
        {
            MessageBox.Show("Please enter your M-Pesa reference code.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            // Submit renewal request to server
            // POST /api/license/renew-request
            var request = new
            {
                mpesa_ref = mpesaRef,
                tenant_code = _enforcementService.State.TenantCode
            };

            var json = System.Text.Json.JsonSerializer.Serialize(request);
            using var content = new System.Net.Http.StringContent(
                json, System.Text.Encoding.UTF8, "application/json");

            using var client = new System.Net.Http.HttpClient
            {
                BaseAddress = new Uri("https://api.pharmco.co.ke")
            };

            var response = await client.PostAsync("/api/license/renew-request", content);

            if (response.IsSuccessStatusCode)
            {
                MessageBox.Show(
                    "Renewal request submitted successfully! Your license will be updated within 60 seconds after the Pharmco team processes your payment.",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                MessageBox.Show(
                    $"Failed to submit renewal request: {error}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Network error: {ex.Message}. Please try again when online.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
