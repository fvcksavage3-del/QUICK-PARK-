using Microsoft.Maui.Networking;

namespace QuickPark;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        Connectivity.Current.ConnectivityChanged += Connectivity_ConnectivityChanged;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage());
    }

    private async void Connectivity_ConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess != NetworkAccess.Internet)
        {
            return;
        }

        try
        {
            await AuthSession.RestoreCachedUserAsync();
            await new BookingApiClient().SyncPendingChangesAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark reconnect sync failed: {exception.Message}");
        }
    }
}
