using Microsoft.Maui.Networking;

namespace QuickPark;

public partial class BookingPage : ContentPage
{
    private readonly BookingApiClient _bookingApi = new();
    private List<AdminLocation> _locations = [];
    private List<AdminSlot> _availableSlots = [];
    private List<AdminSlot> _selectedLocationSlots = [];
    private readonly string? _initialLocationId;
    private readonly string? _initialSlotId;
    private bool _hasLoadedOptions;
    private bool _isRefreshingSchedule;

    public BookingPage(string? initialLocationId = null, string? initialSlotId = null)
    {
        InitializeComponent();
        _initialLocationId = initialLocationId;
        _initialSlotId = initialSlotId;

        BookingDatePicker.MinimumDate = DateTime.Today;
        var defaultStartTime = GetNextStartTime(DateTime.Now);
        BookingDatePicker.Date = defaultStartTime.Date;
        BookingTimePicker.Time = defaultStartTime.TimeOfDay;
        PaymentMethodPicker.ItemsSource = new[]
        {
            "Pay at parking facility",
            "GCash (preview only)",
            "Card (preview only)"
        };
        PaymentMethodPicker.SelectedIndex = 0;
        UpdateSummary();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ReservationPrivacyLabel.Text = Connectivity.Current.NetworkAccess == NetworkAccess.Internet
            ? "Your request is saved securely. Payment is not processed in the app."
            : "Offline bookings are saved on this device and submitted when internet returns. Parking approval is still required. Payment is not processed.";
        if (!_hasLoadedOptions)
        {
            await LoadBookingOptionsAsync(GetSelectedStartTime());
        }
    }

    private async Task LoadBookingOptionsAsync(
        DateTime? requestedStartTime = null,
        bool preserveSelection = false)
    {
        var previousLocationId = preserveSelection ? GetSelectedLocation()?.Id : null;
        var previousSlotId = preserveSelection &&
            SlotPicker.SelectedIndex >= 0 &&
            SlotPicker.SelectedIndex < _selectedLocationSlots.Count
                ? _selectedLocationSlots[SlotPicker.SelectedIndex].Id
                : null;
        _isRefreshingSchedule = true;
        SetLoading(true);
        try
        {
            await _bookingApi.SyncPendingChangesAsync();
            var options = await _bookingApi.GetOptionsAsync(requestedStartTime ?? GetSelectedStartTime());
            _locations = options.Locations;
            _availableSlots = options.Slots;
            LocationPicker.ItemsSource = _locations.Select(location => location.Name).ToList();
            var preferredLocationId = previousLocationId ?? _initialLocationId;
            var preferredLocationIndex = preferredLocationId is null
                ? -1
                : _locations.FindIndex(location => location.Id == preferredLocationId);
            LocationPicker.SelectedIndex = preferredLocationIndex >= 0
                ? preferredLocationIndex
                : _locations.Count > 0 ? 0 : -1;
            _hasLoadedOptions = true;
            BookingStatusLabel.Text = _locations.Count == 0
                ? "No saved parking locations are available. Connect and refresh parking first."
                : Connectivity.Current.NetworkAccess == NetworkAccess.Internet
                    ? $"{_availableSlots.Count} slots available for this time."
                    : $"Saved availability: {_availableSlots.Count} slots. Actual availability may have changed.";
            BookingStatusLabel.TextColor = _availableSlots.Count == 0
                ? Color.FromArgb("#9A6500")
                : Color.FromArgb("#23834B");
            UpdateSelectedLocationSlots();
            var initialSlotIndex = _initialSlotId is null
                ? -1
                : _selectedLocationSlots.FindIndex(slot => slot.Id == _initialSlotId);
            if (initialSlotIndex >= 0)
            {
                SlotPicker.SelectedIndex = initialSlotIndex;
            }
            else if (previousSlotId is not null)
            {
                var previousSlotIndex = _selectedLocationSlots.FindIndex(slot => slot.Id == previousSlotId);
                if (previousSlotIndex >= 0)
                {
                    SlotPicker.SelectedIndex = previousSlotIndex;
                }
            }
            UpdateSummary();
        }
        catch (InvalidOperationException exception)
        {
            ShowBookingError(exception.Message);
        }
        catch (FirebaseAuthException exception)
        {
            ShowBookingError(exception.Message);
        }
        catch (System.Net.Http.HttpRequestException exception)
        {
            ShowBookingError($"QuickPark could not reach Firebase: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            ShowBookingError("The Firestore request timed out. Please try again.");
        }
        finally
        {
            _isRefreshingSchedule = false;
            SetLoading(false);
        }
    }

    private async void Selection_Changed(object? sender, EventArgs e)
    {
        if (sender == LocationPicker)
        {
            UpdateSelectedLocationSlots();
            if (!_isRefreshingSchedule)
            {
                SetLoading(false);
            }
        }

        UpdateSummary();
        if (_isRefreshingSchedule || !_hasLoadedOptions ||
            (sender != BookingDatePicker && sender != BookingTimePicker))
        {
            return;
        }

        var requestedStartTime = GetSelectedStartTime();
        if (requestedStartTime <= DateTime.Now.AddMinutes(1))
        {
            _availableSlots = [];
            UpdateSelectedLocationSlots();
            ShowBookingError("Select a future start time to check slot availability.");
            SetLoading(false);
            return;
        }

        await LoadBookingOptionsAsync(requestedStartTime, preserveSelection: true);
    }

    private void UpdateSelectedLocationSlots()
    {
        SlotPicker.SelectedIndex = -1;
        var selectedLocation = GetSelectedLocation();
        _selectedLocationSlots = selectedLocation is null
            ? []
            : _availableSlots.Where(slot => slot.LocationId == selectedLocation.Id).ToList();
        SlotPicker.ItemsSource = _selectedLocationSlots.Select(slot => slot.Label).ToList();
        SlotAvailabilityLabel.Text = selectedLocation is null
            ? "Choose a parking location."
            : _selectedLocationSlots.Count == 0
                ? "No available slots at this location. Choose another parking area."
                : $"{_selectedLocationSlots.Count} available slots.";
    }

    private async void BackButton_Clicked(object? sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }

    private async void ConfirmReservationButton_Clicked(object? sender, EventArgs e)
    {
        var location = GetSelectedLocation();
        if (location is null ||
            SlotPicker.SelectedIndex < 0 ||
            SlotPicker.SelectedIndex >= _selectedLocationSlots.Count ||
            PaymentMethodPicker.SelectedItem is not string paymentMethod)
        {
            await DisplayAlertAsync(
                "Complete booking details",
                "Select a parking location, available slot, and payment method to continue.",
                "OK");
            return;
        }

        var slot = _selectedLocationSlots[SlotPicker.SelectedIndex];
        var selectedSlot = slot.Label;
        var selectedDate = BookingDatePicker.Date ?? DateTime.Today;
        var selectedTime = BookingTimePicker.Time ?? TimeSpan.Zero;
        var dateTime = selectedDate.Date + selectedTime;
        if (dateTime <= DateTime.Now.AddMinutes(1))
        {
            await DisplayAlertAsync(
                "Choose a future time",
                "The reservation start time must be at least one minute from now. Update the date or start time and try again.",
                "OK");
            return;
        }

        var confirmation = await DisplayAlertAsync(
            "Review reservation",
            $"{location.Name}\n{dateTime:MMM d, yyyy 'at' h:mm tt} for 1 hour\nSlot {selectedSlot}\nParking fee: ₱{location.FeePerHour:N2} / hour\nPayment method: {paymentMethod}\n\nThe slot will be reserved in QuickPark. Payment is not processed.",
            "Reserve slot",
            "Cancel");

        if (confirmation)
        {
            SetLoading(true);
            var reservationCreated = false;
            try
            {
                var result = await _bookingApi.CreateReservationAsync(
                    location.Id,
                    slot.Id,
                    dateTime,
                    paymentMethod);
                const string paymentStatus = "Not processed";
                var fee = $"₱{result.Fee:N2} / hour";
                BookingReservationStore.Save(new BookingReservation(
                    result.ReservationId,
                    location.Id,
                    slot.Id,
                    result.LocationName,
                    result.SlotLabel,
                    dateTime,
                    fee,
                    result.Status,
                    paymentMethod,
                    paymentStatus,
                    result.ReceiptNumber,
                    DateTime.Now));
                reservationCreated = true;
                await DisplayAlertAsync(
                    result.Status == "Pending sync" ? "Booking saved on this device" : "Reservation request submitted",
                    $"Request: {result.ReservationId}\n{result.LocationName}\nSlot {result.SlotLabel}\n{dateTime:MMM d, yyyy 'at' h:mm tt}\nStatus: {result.Status}{(result.Status == "Pending sync" ? ". It will be sent when internet returns; parking approval is still required." : " approval")}\nFee: {fee}\nPayment: {paymentStatus}\nReceipt: {result.ReceiptNumber}",
                    "Done");
                await Navigation.PushAsync(new MyBookingPage());
            }
            catch (InvalidOperationException exception)
            {
                if (reservationCreated)
                {
                    await DisplayAlertAsync(
                        "Reservation saved",
                        $"Your reservation was saved, but QuickPark could not open My Booking. Select the Bookings tab to view it. {exception.Message}",
                        "OK");
                }
                else
                {
                    await DisplayAlertAsync("Could not reserve slot", exception.Message, "OK");
                    await LoadBookingOptionsAsync();
                }
            }
            catch (FirebaseAuthException exception)
            {
                await DisplayAlertAsync(
                    reservationCreated ? "Reservation saved" : "Sign-in required",
                    reservationCreated
                        ? $"Your reservation was saved. Select the Bookings tab to view it. {exception.Message}"
                        : exception.Message,
                    "OK");
            }
            catch (System.Net.Http.HttpRequestException exception)
            {
                await DisplayAlertAsync(
                    reservationCreated ? "Reservation saved" : "Connection error",
                    reservationCreated
                        ? $"Your reservation was saved. Select the Bookings tab to view it. {exception.Message}"
                        : $"QuickPark could not reach Firebase: {exception.Message}",
                    "OK");
            }
            catch (TaskCanceledException)
            {
                await DisplayAlertAsync(
                    reservationCreated ? "Reservation saved" : "Request timed out",
                    reservationCreated
                        ? "Your reservation was saved. Select the Bookings tab to view it."
                        : "The reservation request timed out. Check My Booking before retrying.",
                    "OK");
            }
            finally
            {
                SetLoading(false);
            }
        }
    }

    private AdminLocation? GetSelectedLocation()
    {
        var index = LocationPicker.SelectedIndex;
        return index >= 0 && index < _locations.Count
            ? _locations[index]
            : null;
    }

    private void UpdateSummary()
    {
        var location = GetSelectedLocation();
        ParkingFeeLabel.Text = location is null
            ? "Select a location"
            : $"₱{location.FeePerHour:N2} / hour";
        SelectedLocationAddressLabel.Text = location is null
            ? "Select a parking area to view its street address."
            : string.IsNullOrWhiteSpace(location.Address)
                ? "Street address not provided."
                : location.Address;
        SummaryLocationLabel.Text = location is null
            ? "Parking location: Not selected"
            : $"Parking location: {location.Name} · ₱{location.FeePerHour:N2}/hour";
        var selectedDate = BookingDatePicker.Date ?? DateTime.Today;
        var selectedTime = BookingTimePicker.Time ?? TimeSpan.Zero;
        SummaryDateTimeLabel.Text =
            $"Date and time: {selectedDate:MMM d, yyyy} at {selectedTime:h\\:mm}";
        SummarySlotLabel.Text = SlotPicker.SelectedIndex >= 0 && SlotPicker.SelectedIndex < _selectedLocationSlots.Count
            ? $"Parking slot: {_selectedLocationSlots[SlotPicker.SelectedIndex].Label}"
            : "Parking slot: Not selected";
        SummaryPaymentLabel.Text = PaymentMethodPicker.SelectedItem is string method
            ? $"Payment: {method} · Not processed"
            : "Payment: Not selected";
    }

    private DateTime GetSelectedStartTime()
    {
        var selectedDate = BookingDatePicker.Date ?? DateTime.Today;
        var selectedTime = BookingTimePicker.Time ?? TimeSpan.Zero;
        return selectedDate.Date + selectedTime;
    }

    private void ShowBookingError(string message)
    {
        BookingStatusLabel.Text = message;
        BookingStatusLabel.TextColor = Color.FromArgb("#C43B3B");
    }

    private void SetLoading(bool isLoading)
    {
        BookingStatusLabel.IsVisible = true;
        if (isLoading)
        {
            BookingStatusLabel.Text = "Loading booking service…";
        }
        ConfirmReservationButton.IsEnabled =
            !isLoading &&
            _hasLoadedOptions &&
            _locations.Count > 0 &&
            _selectedLocationSlots.Count > 0;
        ConfirmReservationButton.Opacity = ConfirmReservationButton.IsEnabled ? 1 : 0.6;
    }

    private static DateTime GetNextStartTime(DateTime now)
    {
        var nextQuarter = now.AddMinutes(15);
        var roundedMinutes = (nextQuarter.Minute + 14) / 15 * 15;
        return new DateTime(
            nextQuarter.Year,
            nextQuarter.Month,
            nextQuarter.Day,
            nextQuarter.Hour,
            0,
            0,
            nextQuarter.Kind).AddMinutes(roundedMinutes);
    }
}
