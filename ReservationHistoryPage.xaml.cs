using Microsoft.Maui.Controls.Shapes;
using System.Net.Http;

namespace QuickPark;

public partial class ReservationHistoryPage : ContentPage
{
    private readonly BookingApiClient _bookingApi = new();
    private bool _isLoading;

    public ReservationHistoryPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadHistoryAsync();
    }

    private async void BackButton_Clicked(object? sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }

    private async void RefreshButton_Clicked(object? sender, EventArgs e)
    {
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        HistoryStatusLabel.IsVisible = false;
        EmptyHistoryLabel.IsVisible = false;
        HistoryItems.Children.Clear();
        RefreshButton.IsEnabled = false;
        try
        {
            await _bookingApi.SyncPendingChangesAsync();
            var reservations = await _bookingApi.GetMyReservationHistoryAsync();
            foreach (var reservation in reservations)
            {
                AddHistoryItem(reservation);
            }

            EmptyHistoryLabel.IsVisible = reservations.Count == 0;
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
        catch (FirebaseAuthException exception)
        {
            ShowError(exception.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not connect to the reservation service. Check your connection and try again.");
        }
        catch (TaskCanceledException)
        {
            ShowError("Loading reservation history timed out. Please try again.");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _isLoading = false;
        }
    }

    private void AddHistoryItem(MyBookingDetails reservation)
    {
        var statusColor = reservation.Status switch
        {
            "Pending" or "Pending sync" or "Cancellation pending sync" => Color.FromArgb("#9A6500"),
            "Cancelled" or "Expired" => Color.FromArgb("#B42318"),
            _ => Color.FromArgb("#23834B")
        };
        var statusBackground = reservation.Status switch
        {
            "Pending" or "Pending sync" or "Cancellation pending sync" => Color.FromArgb("#FFF4DB"),
            "Cancelled" or "Expired" => Color.FromArgb("#FCE8E8"),
            _ => Color.FromArgb("#E8F7EF")
        };
        var card = new Border
        {
            BackgroundColor = Colors.White,
            Stroke = Color.FromArgb("#E1EAF4"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(17) },
            Padding = new Thickness(14)
        };
        var content = new VerticalStackLayout { Spacing = 8 };
        var heading = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8
        };
        heading.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(reservation.LocationName)
                ? "QuickPark reservation"
                : reservation.LocationName,
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#172E4E"),
            VerticalOptions = LayoutOptions.Center
        });
        heading.Add(new Border
        {
            BackgroundColor = statusBackground,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
            Padding = new Thickness(9, 4),
            Content = new Label
            {
                Text = string.IsNullOrWhiteSpace(reservation.Status) ? "Unknown" : reservation.Status,
                FontSize = 9,
                FontAttributes = FontAttributes.Bold,
                TextColor = statusColor
            }
        }, 1);
        content.Children.Add(heading);
        content.Children.Add(new Label
        {
            Text = $"Slot {reservation.SlotLabel} · {reservation.StartTime.ToLocalTime():MMM d, yyyy h:mm tt}",
            FontSize = 11,
            TextColor = Color.FromArgb("#526579")
        });
        content.Children.Add(new Label
        {
            Text = $"Fee: ₱{reservation.Fee:N2} · Reservation ID: {reservation.Id}",
            FontSize = 10,
            TextColor = Color.FromArgb("#78899D")
        });

        var canCancel = (reservation.Status is "Pending" or "Confirmed" or "Pending sync") &&
            reservation.StartTime.ToUniversalTime() > DateTime.UtcNow;
        if (reservation.Status == "Confirmed")
        {
            var navigateButton = new Button
            {
                Text = "Navigate to parking",
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                TextColor = Colors.White,
                BackgroundColor = Color.FromArgb("#1769D2"),
                HeightRequest = 42,
                CornerRadius = 13,
                AutomationId = $"NavigateReservation_{reservation.Id}"
            };
            navigateButton.Clicked += async (_, _) =>
                await NavigateToReservationAsync(reservation, navigateButton);
            content.Children.Add(navigateButton);
        }

        if (canCancel)
        {
            var cancelButton = new Button
            {
                Text = "Cancel booking",
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#B42318"),
                BackgroundColor = Color.FromArgb("#FCE8E8"),
                HeightRequest = 42,
                CornerRadius = 13,
                AutomationId = $"CancelReservation_{reservation.Id}"
            };
            cancelButton.Clicked += async (_, _) =>
                await CancelReservationAsync(reservation, cancelButton);
            content.Children.Add(cancelButton);
        }
        else
        {
            content.Children.Add(new Label
            {
                Text = reservation.Status is "Pending" or "Confirmed"
                    ? "Cancellation is unavailable because the reservation start time has passed."
                    : $"Cancellation is unavailable for {reservation.Status.ToLowerInvariant()} reservations.",
                FontSize = 10,
                TextColor = Color.FromArgb("#78899D")
            });
        }

        card.Content = content;
        HistoryItems.Children.Add(card);
    }

    private async Task CancelReservationAsync(MyBookingDetails reservation, Button button)
    {
        if (!await DisplayAlertAsync(
                "Cancel booking",
                $"Cancel this booking before its start time ({reservation.StartTime.ToLocalTime():MMM d, yyyy h:mm tt})? Payment has not been processed and no cancellation fee is configured.",
                "Cancel booking",
                "Keep booking"))
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await _bookingApi.CancelReservationAsync(reservation.Id);
            await DisplayAlertAsync(
                "Booking cancelled",
                "The booking status and parking availability have been refreshed.",
                "OK");
            await LoadHistoryAsync();
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
        catch (FirebaseAuthException exception)
        {
            ShowError(exception.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not connect to the reservation service. Try again.");
        }
        catch (TaskCanceledException)
        {
            ShowError("The cancellation request timed out. Refresh the history before retrying.");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task NavigateToReservationAsync(MyBookingDetails reservation, Button button)
    {
        button.IsEnabled = false;
        try
        {
            var location = await _bookingApi.GetNavigationLocationAsync(reservation.Id);
            if (location.Id != reservation.LocationId)
            {
                throw new InvalidOperationException(
                    "The verified parking location does not match this reservation.");
            }

            await Navigation.PushAsync(new MapNavigationPage(reservation.Id, location));
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
        catch (FirebaseAuthException exception)
        {
            ShowError(exception.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not load the parking entrance. Check your connection and try again.");
        }
        catch (TaskCanceledException)
        {
            ShowError("Loading the parking entrance timed out. Please try again.");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        HistoryStatusLabel.Text = message;
        HistoryStatusLabel.IsVisible = true;
    }
}
