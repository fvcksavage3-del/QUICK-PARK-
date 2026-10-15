using System.Net.Http;
using Microsoft.Maui.Networking;

namespace QuickPark;

public partial class MyBookingPage : ContentPage
{
    private readonly BookingApiClient _bookingApi = new();
    private AdminLocation? _currentLocation;
    private bool _isCancelling;

    public MyBookingPage()
    {
        InitializeComponent();
        DisplayCurrentReservation();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _bookingApi.SyncPendingChangesAsync();
        await LoadCurrentReservationAsync();
    }

    private async void BackButton_Clicked(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }
        else if (Navigation.ModalStack.Count > 0)
        {
            await Navigation.PopModalAsync();
        }
    }

    private async void NavigateToParkingButton_Clicked(object? sender, EventArgs e)
    {
        var cachedReservation = BookingReservationStore.Current;
        if (cachedReservation is null)
        {
            return;
        }

        try
        {
            var response = await _bookingApi.GetMyBookingAsync();
            if (response.Reservation is not { } reservation ||
                reservation.Id != cachedReservation.ReservationId ||
                reservation.LocationId != cachedReservation.LocationId)
            {
                await DisplayAlertAsync(
                    "Booking unavailable",
                    "The current booking could not be verified. Refresh your bookings and try again.",
                    "OK");
                return;
            }

            if (!string.Equals(reservation.Status, "Confirmed", StringComparison.Ordinal))
            {
                await DisplayAlertAsync(
                    "Booking not confirmed",
                    "Navigation is available only after the parking administrator confirms this booking.",
                    "OK");
                return;
            }

            var location = await _bookingApi.GetNavigationLocationAsync(reservation.Id);
            if (location.Id != reservation.LocationId)
            {
                throw new InvalidOperationException("The verified parking location does not match this booking.");
            }

            await Navigation.PushAsync(new MapNavigationPage(reservation.Id, location));
        }
        catch (InvalidOperationException exception)
        {
            await DisplayAlertAsync("Could not load parking location", exception.Message, "OK");
        }
        catch (FirebaseAuthException exception)
        {
            await DisplayAlertAsync("Sign-in required", exception.Message, "OK");
        }
        catch (HttpRequestException)
        {
            await DisplayAlertAsync("Connection error", "Could not load the parking entrance details. Check your connection and try again.", "OK");
        }
        catch (TaskCanceledException)
        {
            await DisplayAlertAsync("Request timed out", "Loading the parking entrance details timed out. Please try again.", "OK");
        }
    }

    private async void CancelBookingButton_Clicked(object? sender, EventArgs e)
    {
        var reservation = BookingReservationStore.Current;
        if (_isCancelling || !CanCancel(reservation))
        {
            DisplayCurrentReservation();
            return;
        }

        var confirmed = await DisplayAlertAsync(
            "Cancel booking",
            $"Cancel this reservation before its start time ({reservation!.StartTime.ToLocalTime():MMM d, yyyy h:mm tt})? Payment has not been processed and no cancellation fee is configured. The slot will be released only if it is still reserved for this booking.",
            "Cancel booking",
            "Keep booking");

        if (confirmed)
        {
            _isCancelling = true;
            CancelBookingButton.IsEnabled = false;
            try
            {
                await _bookingApi.CancelReservationAsync(reservation!.ReservationId);
                BookingReservationStore.Cancel();
                _currentLocation = null;
                await DisplayAlertAsync(
                    "Booking cancelled",
                    "Your reservation was cancelled and your booking list has been refreshed.",
                    "OK");
                await LoadCurrentReservationAsync();
            }
            catch (InvalidOperationException exception)
            {
                await DisplayAlertAsync("Could not cancel reservation", exception.Message, "OK");
            }
            catch (FirebaseAuthException exception)
            {
                await DisplayAlertAsync("Sign-in required", exception.Message, "OK");
            }
            catch (HttpRequestException)
            {
                await DisplayAlertAsync("Connection error", "Could not connect to the reservation service. Try again.", "OK");
            }
            catch (TaskCanceledException)
            {
                await DisplayAlertAsync("Request timed out", "The cancellation request timed out. Check the reservation status before retrying.", "OK");
            }
            finally
            {
                _isCancelling = false;
                DisplayCurrentReservation();
            }
        }
    }

    private async Task LoadCurrentReservationAsync()
    {
        try
        {
            var response = await _bookingApi.GetMyBookingAsync();
            if (response.Reservation is not { } details)
            {
                BookingReservationStore.Cancel();
                _currentLocation = null;
                DisplayCurrentReservation();
                return;
            }

            _currentLocation = await _bookingApi.GetLocationAsync(details.LocationId);
            BookingReservationStore.Save(new BookingReservation(
                details.Id,
                details.LocationId,
                details.SlotId,
                details.LocationName,
                details.SlotLabel,
                details.StartTime,
                $"₱{details.Fee:N2} / hour",
                details.Status,
                details.PaymentMethod,
                details.PaymentStatus,
                details.ReceiptNumber,
                DateTime.Now));
            DisplayCurrentReservation();
        }
        catch (InvalidOperationException exception)
        {
            await DisplayAlertAsync("Could not load booking", exception.Message, "OK");
        }
        catch (FirebaseAuthException exception)
        {
            await DisplayAlertAsync("Sign-in required", exception.Message, "OK");
        }
        catch (HttpRequestException)
        {
            await DisplayAlertAsync("Connection error", "Could not connect to the reservation service.", "OK");
        }
        catch (TaskCanceledException)
        {
            await DisplayAlertAsync("Request timed out", "The reservation lookup timed out. Please try again.", "OK");
        }
    }

    private void DisplayCurrentReservation()
    {
        var reservation = BookingReservationStore.Current;
        ReservationContent.IsVisible = reservation is not null;
        EmptyState.IsVisible = reservation is null;
        if (reservation is null)
        {
            _currentLocation = null;
            NavigateToParkingButton.IsEnabled = false;
            CancelBookingButton.IsVisible = false;
            CancellationPolicyLabel.Text = string.Empty;
            return;
        }

        LocationLabel.Text = reservation.Location;
        LocationAddressLabel.Text = _currentLocation?.Address is { Length: > 0 } address
            ? address
            : "Parking address unavailable";
        DateTimeLabel.Text = reservation.StartTime.ToString("MMM d, yyyy h:mm tt");
        SlotLabel.Text = reservation.Slot;
        FeeLabel.Text = reservation.Fee;
        StatusLabel.Text = reservation.Status;
        var reservationColor = reservation.Status switch
        {
            "Pending" or "Pending sync" or "Cancellation pending sync" => Color.FromArgb("#9A6500"),
            "Cancelled" or "Expired" => Color.FromArgb("#C43B3B"),
            _ => Color.FromArgb("#23834B")
        };
        StatusBorder.BackgroundColor = reservation.Status switch
        {
            "Pending" or "Pending sync" or "Cancellation pending sync" => Color.FromArgb("#FFF4DB"),
            "Cancelled" or "Expired" => Color.FromArgb("#FCE8E8"),
            _ => Color.FromArgb("#E8F7EF")
        };
        StatusLabel.TextColor = reservationColor;
        ReservationStatusLabel.TextColor = reservationColor;
        ReservationStatusLabel.Text = reservation.Status;
        PaymentMethodLabel.Text = reservation.PaymentMethod;
        PaymentStatusLabel.Text = reservation.PaymentStatus;
        ReceiptNumberLabel.Text = $"{reservation.ReceiptNumber} · ID {reservation.ReservationId}";
        var latitude = _currentLocation?.Latitude;
        var longitude = _currentLocation?.Longitude;
        var hasCoordinates = latitude is { } validLatitude &&
            longitude is { } validLongitude &&
            double.IsFinite(validLatitude) &&
            double.IsFinite(validLongitude) &&
            validLatitude is >= -90 and <= 90 &&
            validLongitude is >= -180 and <= 180;
        var hasInternet = Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
        NavigateToParkingButton.IsEnabled = hasInternet && reservation.Status == "Confirmed" && hasCoordinates;
        NavigateToParkingButton.Text = !hasInternet
            ? "Directions require internet"
            : reservation.Status == "Confirmed"
                ? hasCoordinates ? "Navigate to Parking" : "Parking entrance not mapped"
                : "Available after booking confirmation";
        var cancellationEligible = CanCancel(reservation);
        CancelBookingButton.IsVisible = cancellationEligible;
        CancelBookingButton.IsEnabled = cancellationEligible && !_isCancelling;
        CancellationPolicyLabel.Text = cancellationEligible
            ? reservation.Status == "Pending sync"
                ? "This booking is saved on this device and will be sent when internet returns. You can cancel it before its start time."
                : "Cancellation is allowed before the reservation start time. No cancellation fee is configured; payment is not processed."
            : reservation.Status is "Pending" or "Confirmed"
                ? "Cancellation is unavailable because the reservation start time has passed."
                : $"Cancellation is unavailable for {reservation.Status.ToLowerInvariant()} reservations.";
    }

    private static bool CanCancel(BookingReservation? reservation) =>
        reservation is not null &&
        (reservation.Status is "Pending" or "Confirmed" or "Pending sync") &&
        reservation.StartTime.ToUniversalTime() > DateTime.UtcNow;
}
