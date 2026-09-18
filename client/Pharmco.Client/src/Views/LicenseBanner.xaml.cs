using System.Windows;
using System.Windows.Controls;
using Pharmco.Client.Services;

namespace Pharmco.Client.Views;

public partial class LicenseBanner : UserControl
{
    private LicenseEnforcementService? _enforcementService;

    public static readonly DependencyProperty LicenseStateProperty =
        DependencyProperty.Register(
            nameof(LicenseState),
            typeof(LicenseEnforcementState),
            typeof(LicenseBanner),
            new PropertyMetadata(null, OnLicenseStateChanged));

    public LicenseEnforcementState? LicenseState
    {
        get => (LicenseEnforcementState?)GetValue(LicenseStateProperty);
        set => SetValue(LicenseStateProperty, value);
    }

    public LicenseBanner()
    {
        InitializeComponent();
        DataContext = this;
    }

    private static void OnLicenseStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LicenseBanner banner)
        {
            banner.UpdateBannerVisibility();
        }
    }

    private void UpdateBannerVisibility()
    {
        if (LicenseState == null || LicenseState.IsValid && LicenseState.Tier == LicenseEnforcementService.LicenseTier.Normal)
        {
            BannerBorder.Visibility = Visibility.Collapsed;
            return;
        }

        BannerBorder.Visibility = Visibility.Visible;
        WarningText.Text = LicenseState.GetWarningMessage();

        // Show renew button only for warning/grace tiers
        RenewButton.Visibility = (LicenseState.Tier == LicenseEnforcementService.LicenseTier.WarningYellow
            || LicenseState.Tier == LicenseEnforcementService.LicenseTier.WarningRed
            || LicenseState.Tier == LicenseEnforcementService.LicenseTier.GracePeriod)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RenewButton_Click(object sender, RoutedEventArgs e)
    {
        // Open renewal dialog
        var dialog = new RenewDialog
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}
