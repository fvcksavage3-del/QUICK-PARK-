using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace QuickPark;

public partial class AdminPage : ContentPage
{
    private static readonly string[] SlotStatuses = ["Available", "Occupied", "Maintenance"];
    private readonly AdminApiClient _apiClient = new();
    private readonly FirebaseAuthService _authService = new();
    private AdminOverview? _overview;
    private bool? _isCompactLayout;
    private bool _isPageActive;
    private bool _overviewTimerScheduled;
    private bool _isLoadingOverview;
    private bool _isEditingLocation;

    public AdminPage()
    {
        InitializeComponent();
        AdminEmailLabel.Text = AuthSession.CurrentUser?.Email ?? "Administrator";
        ReservationStatusPicker.SelectedIndex = 0;
        UserRolePicker.SelectedIndex = 0;
        UserAuthStatusPicker.SelectedIndex = 0;
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        var isCompact = width < 900;
        if (_isCompactLayout == isCompact)
        {
            return;
        }

        _isCompactLayout = isCompact;
        AdminLayout.ColumnDefinitions.Clear();
        AdminLayout.ColumnDefinitions.Add(new ColumnDefinition(isCompact ? new GridLength(0) : new GridLength(220)));
        AdminLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        AdminSidebar.IsVisible = !isCompact;
        CompactNavigation.IsVisible = isCompact;
        CompactLogoutButton.IsVisible = isCompact;
        WorkspaceTitleLabel.IsVisible = !isCompact;

        DashboardColumns.ColumnDefinitions.Clear();
        DashboardColumns.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        DashboardColumns.RowDefinitions.Clear();

        if (isCompact)
        {
            for (var row = 0; row < 8; row++)
            {
                DashboardColumns.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            SetDashboardSection(ParkingSection, 0, 0);
            SetDashboardSection(UsageSection, 1, 0);
            SetDashboardSection(ReservationsSection, 2, 0);
            SetDashboardSection(QuickActionsAndSlots, 3, 0);
            SetDashboardSection(ReportsSection, 4, 0);
            SetDashboardSection(UsersSection, 5, 0);
            SetDashboardSection(PaymentsSection, 6, 0);
            SetDashboardSection(SettingsAndAnnouncements, 7, 0);
            MetricsGrid.ColumnDefinitions.Clear();
            for (var column = 0; column < 2; column++)
            {
                MetricsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            }
            Grid.SetColumn(OccupiedMetricCard, 0);
            Grid.SetRow(OccupiedMetricCard, 1);
            Grid.SetColumn(BookingsMetricCard, 1);
            Grid.SetRow(BookingsMetricCard, 1);
            return;
        }

        DashboardColumns.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(310)));
        for (var row = 0; row < 4; row++)
        {
            DashboardColumns.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        SetDashboardSection(ParkingSection, 0, 0);
        SetDashboardSection(UsageSection, 0, 1);
        SetDashboardSection(ReservationsSection, 1, 0);
        SetDashboardSection(QuickActionsAndSlots, 1, 1);
        SetDashboardSection(ReportsSection, 2, 0);
        SetDashboardSection(UsersSection, 2, 1);
        SetDashboardSection(PaymentsSection, 3, 0);
        SetDashboardSection(SettingsAndAnnouncements, 3, 1);
        MetricsGrid.ColumnDefinitions.Clear();
        for (var column = 0; column < 4; column++)
        {
            MetricsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        Grid.SetColumn(OccupiedMetricCard, 2);
        Grid.SetRow(OccupiedMetricCard, 0);
        Grid.SetColumn(BookingsMetricCard, 3);
        Grid.SetRow(BookingsMetricCard, 0);
    }

    private static void SetDashboardSection(VisualElement section, int row, int column)
    {
        Grid.SetRow(section, row);
        Grid.SetColumn(section, column);
    }

    private async void AdminNavigationButton_Clicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not string section)
        {
            return;
        }

        var target = section switch
        {
            "Dashboard" => (VisualElement)WelcomeBanner,
            "Parking" => ParkingSection,
            "Bookings" => ReservationsSection,
            "Users" => UsersSection,
            "Reports" => ReportsSection,
            "Settings" => SettingsSection,
            _ => throw new InvalidOperationException($"Unknown admin section '{section}'.")
        };

        await DashboardScrollView.ScrollToAsync(target, ScrollToPosition.Start, animated: true);
        UpdateNavigationSelection(section);
    }

    private void UpdateNavigationSelection(string selectedSection)
    {
        foreach (var navButton in SidebarNavigation.Children.OfType<Button>()
                     .Concat(CompactNavigation.Content is HorizontalStackLayout compactButtons
                         ? compactButtons.Children.OfType<Button>()
                         : Enumerable.Empty<Button>()))
        {
            var isSelected = string.Equals(navButton.CommandParameter as string, selectedSection, StringComparison.Ordinal);
            navButton.BackgroundColor = isSelected
                ? Color.FromArgb("#1469E8")
                : navButton.Parent is HorizontalStackLayout
                    ? Colors.White
                    : Colors.Transparent;
            navButton.TextColor = isSelected
                ? Colors.White
                : navButton.Parent is HorizontalStackLayout
                    ? Color.FromArgb("#365372")
                    : Color.FromArgb("#CED9E9");
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _isPageActive = true;
        await LoadOverviewAsync();
        if (!_overviewTimerScheduled)
        {
            _overviewTimerScheduled = true;
            Dispatcher.StartTimer(TimeSpan.FromSeconds(30), () =>
            {
                if (!_isPageActive)
                {
                    _overviewTimerScheduled = false;
                    return false;
                }

                _ = LoadOverviewAsync();
                return true;
            });
        }
    }

    protected override void OnDisappearing()
    {
        _isPageActive = false;
        base.OnDisappearing();
    }

    private async void LogoutButton_Clicked(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("Admin sign out", "Are you sure you want to sign out?", "OK", "Cancel"))
        {
            return;
        }

        AuthSession.SignOut();
        Application.Current?.Windows.FirstOrDefault()?.Page = new MainPage();
    }

    private async void RefreshButton_Clicked(object? sender, EventArgs e)
    {
        await LoadOverviewAsync();
    }

    private async Task LoadOverviewAsync()
    {
        if (_isLoadingOverview)
        {
            return;
        }

        _isLoadingOverview = true;
        SetBusy(true);
        StatusLabel.IsVisible = false;
        try
        {
            _overview = await _apiClient.GetOverviewAsync();
            RenderOverview(_overview);
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
            ShowError("Could not connect to Firestore. Check your connection and try again.");
        }
        catch (TaskCanceledException)
        {
            ShowError("The Firestore request timed out. Please try again.");
        }
        finally
        {
            _isLoadingOverview = false;
            SetBusy(false);
        }
    }

    private void RenderOverview(AdminOverview overview)
    {
        LocationsMetricLabel.Text = $"{overview.Report.LocationCount} locations";
        SlotsMetricLabel.Text = FormatCount(overview.Report.SlotCount);
        AvailableMetricLabel.Text = FormatCount(overview.Report.AvailableSlots);
        OccupiedMetricLabel.Text = FormatCount(overview.Report.OccupiedSlots);
        ReservationsMetricLabel.Text = FormatCount(overview.Report.ReservationCount);
        PaymentsMetricLabel.Text = FormatCount(overview.Report.PaymentCount);
        DailyReservationsLabel.Text = $"Today: {FormatCount(overview.Report.DailyReservations)}";
        MonthlyReservationsLabel.Text = FormatCount(overview.Report.MonthlyReservations);
        ParkingUsageLabel.Text = overview.Report.SlotsUnavailable
            ? "Usage: unavailable"
            : $"Usage: {overview.Report.ParkingUsagePercent:N1}%";
        ReportUsageLabel.Text = overview.Report.SlotsUnavailable
            ? "Unavailable"
            : $"{overview.Report.ParkingUsagePercent:N1}%";
        UsagePercentLabel.Text = overview.Report.SlotsUnavailable
            ? "—"
            : $"{overview.Report.ParkingUsagePercent:N0}%";
        ParkingUsageProgress.Progress = overview.Report.SlotsUnavailable
            ? 0
            : Math.Clamp((double)(overview.Report.ParkingUsagePercent / 100), 0, 1);
        UsageAvailableDetailLabel.Text = $"Available  {FormatCount(overview.Report.AvailableSlots)}";
        UsageOccupiedDetailLabel.Text = $"Occupied  {FormatCount(overview.Report.OccupiedSlots)}";
        UsageTotalDetailLabel.Text = $"Total slots  {FormatCount(overview.Report.SlotCount)}";
        PaymentAmountMetricLabel.Text = overview.Report.PaymentsUnavailable
            ? "Revenue: unavailable"
            : $"Revenue: ₱{overview.Report.PaymentAmount:N2}";
        ReservationStatusReportLabel.Text = overview.Report.ReservationsUnavailable
            ? "Reservation totals unavailable"
            : $"Pending {overview.Report.PendingReservations} · Confirmed {overview.Report.ConfirmedReservations} · " +
              $"Completed {overview.Report.CompletedReservations} · Cancelled {overview.Report.CancelledReservations}";
        UsersLimitLabel.Text = overview.Report.UsersTruncated
            ? $"Showing {overview.Users.Count}+ profiles"
            : $"{overview.Users.Count} profiles";
        UsersSearchBar.IsEnabled = true;

        var warnings = new List<string>();
        if (overview.Report.SlotsUnavailable)
        {
            warnings.Add($"Parking slots unavailable: {overview.Report.SlotsError}");
        }
        if (overview.Report.ReservationsUnavailable)
        {
            warnings.Add($"Bookings unavailable: {overview.Report.ReservationsError}");
        }
        if (overview.Report.PaymentsUnavailable)
        {
            warnings.Add($"Payments unavailable: {overview.Report.PaymentsError}");
        }
        StatusLabel.Text = string.Join(Environment.NewLine, warnings);
        StatusLabel.IsVisible = warnings.Count > 0;

        LocationsList.Children.Clear();
        foreach (var location in overview.Locations)
        {
            var isActive = location.IsActive;
            var slotCount = overview.Slots.Count(slot => slot.LocationId == location.Id);
            var slotsDescription = overview.Report.SlotsUnavailable
                ? "Slots unavailable"
                : $"Slots: {slotCount}";
            var row = CreateRow(
                $"{location.Name} · {(isActive ? "Active" : "Inactive")}\n{location.Address}\n₱{location.FeePerHour:N2}/hour · {location.OperatingHours}\nCapacity: {location.TotalCapacity} · {slotsDescription}",
                ("Edit", async () => await EditLocationAsync(location)),
                (isActive ? "Deactivate" : "Activate", async () => await SetLocationActiveAsync(location, !isActive)),
                ("Delete", async () => await DeleteLocationAsync(location)));
            LocationsList.Children.Add(row);
        }
        AddEmptyMessage(LocationsList, overview.Locations.Count, "No parking locations yet.");

        SlotsList.Children.Clear();
        foreach (var slot in overview.Slots)
        {
            var locationName = overview.Locations.FirstOrDefault(item => item.Id == slot.LocationId)?.Name
                ?? "Unknown location";
            var nextStatus = NextSlotStatus(slot.Status);
            var row = slot.Status == "Reserved"
                ? CreateRow(
                    $"{slot.Label} · {locationName}\nStatus: {slot.Status} · manage from Reservations",
                    ("Edit", async () => await EditSlotAsync(slot)))
                : CreateRow(
                    $"{slot.Label} · {locationName}\nStatus: {slot.Status}",
                    ("Edit", async () => await EditSlotAsync(slot)),
                    ($"Set {nextStatus}", async () => await ChangeSlotStatusAsync(slot, nextStatus)),
                    ("Delete", async () => await DeleteSlotAsync(slot)));
            SlotsList.Children.Add(row);
        }
        AddEmptyMessage(
            SlotsList,
            overview.Slots.Count,
            overview.Report.SlotsUnavailable ? "Parking slot records could not be loaded." : "No parking slots yet.");

        RenderReservations(overview);

        RenderUsers(overview);

        PaymentsList.Children.Clear();
        foreach (var payment in overview.Payments)
        {
            PaymentsList.Children.Add(CreateDataRow(FormatPayment(payment, overview.Users)));
        }
        AddEmptyMessage(
            PaymentsList,
            overview.Payments.Count,
            overview.Report.PaymentsUnavailable ? "Payment records could not be loaded." : "No payment records found.");
    }

    private void UsersSearchBar_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_overview is not null)
        {
            RenderUsers(_overview);
        }
    }

    private void UserRolePicker_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_overview is not null)
        {
            RenderUsers(_overview);
        }
    }

    private void RenderUsers(AdminOverview overview)
    {
        UsersList.Children.Clear();
        UsersDirectoryStatusLabel.Text = overview.Report.AuthenticationDirectoryMessage;
        if (overview.Report.UserDirectoryUnavailable)
        {
            UsersList.Children.Add(new Label
            {
                Text = "No application profile records could be loaded. Firebase Authentication accounts are not shown as profiles.",
                FontSize = 12,
                TextColor = Color.FromArgb("#75849A"),
                Margin = new Thickness(2, 4)
            });
            return;
        }
        var query = UsersSearchBar.Text?.Trim() ?? string.Empty;
        var roleFilter = UserRolePicker.SelectedItem as string ?? "All roles";
        var authStatusFilter = UserAuthStatusPicker.SelectedItem as string ?? "All account states";
        var users = overview.Users
            .Where(user => string.IsNullOrWhiteSpace(query) ||
                user.Email.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                user.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                user.Uid.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(user => roleFilter == "All roles" ||
                (roleFilter == "Administrator" && user.Admin) ||
                (roleFilter == "Customer" && !user.Admin))
            .Where(user => authStatusFilter == "All account states" ||
                (!overview.Report.AuthenticationDirectoryAvailable && authStatusFilter == "Not verified") ||
                (overview.Report.AuthenticationDirectoryAvailable &&
                    (user.FirebaseAuthStatus == authStatusFilter ||
                     (authStatusFilter == "Not verified" &&
                      user.FirebaseAuthStatus == "Missing from Firebase Authentication"))))
            .OrderBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var user in users)
        {
            var role = user.Admin || string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase)
                ? "Administrator"
                : "Customer";
            var registrationDate = user.RegisteredAt?.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
                ?? "Date unavailable";
            var authStatus = overview.Report.AuthenticationDirectoryAvailable
                ? user.FirebaseAuthStatus
                : "Not verified";
            var description = $"{(string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName)}\n" +
                $"{user.Email} · {role}\nProfile registered: {registrationDate} · Firebase Authentication: {authStatus}\nUID: {user.Uid}";
            var canManageAccount = overview.Report.AuthenticationDirectoryAvailable &&
                !user.Admin &&
                user.FirebaseAuthStatus is "Active" or "Disabled";
            if (!canManageAccount)
            {
                UsersList.Children.Add(CreateDataRow(description));
                continue;
            }

            var newDisabledState = !user.Disabled;
            UsersList.Children.Add(CreateRow(
                description,
                (user.Disabled ? "Enable" : "Disable", async () => await SetUserDisabledAsync(user, newDisabledState))));
        }

        AddEmptyMessage(
            UsersList,
            users.Count,
            string.IsNullOrWhiteSpace(query) ? "No user accounts found." : "No users match this search.");
    }

    private async Task SetUserDisabledAsync(AdminUser user, bool disabled)
    {
        var action = disabled ? "disable" : "enable";
        if (!await DisplayAlertAsync(
                $"{char.ToUpperInvariant(action[0])}{action[1..]} user",
                $"Are you sure you want to {action} {user.Email}?",
                char.ToUpperInvariant(action[0]) + action[1..],
                "Cancel"))
        {
            return;
        }

        await RunAdminActionAsync(() => _apiClient.SetUserDisabledAsync(user.Uid, disabled));
    }

    private async void AddLocationButton_Clicked(object? sender, EventArgs e)
    {
        await EditLocationAsync(null);
    }

    private async Task EditLocationAsync(AdminLocation? location)
    {
        if (_isEditingLocation)
        {
            return;
        }

        _isEditingLocation = true;
        try
        {
            if (location is not null && _overview is not null)
            {
                location.TotalCapacity = Math.Max(
                    location.TotalCapacity,
                    _overview.Slots.Count(slot => slot.LocationId == location.Id));
            }

            var editor = new ParkingLocationEditorPage(location);
            await Navigation.PushModalAsync(new NavigationPage(editor));
            if (await editor.Completion)
            {
                await LoadOverviewAsync();
            }
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
        catch (FirebaseAuthException exception)
        {
            ShowError(exception.Message);
        }
        catch (HttpRequestException exception)
        {
            ShowError($"Could not open or save the parking location: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            ShowError("The parking location request timed out. Please try again.");
        }
        finally
        {
            _isEditingLocation = false;
        }
    }

    private async Task DeleteLocationAsync(AdminLocation location)
    {
        if (!await DisplayAlertAsync("Delete location", $"Delete {location.Name}?", "Delete", "Cancel"))
        {
            return;
        }

        await RunAdminActionAsync(() => _apiClient.DeleteLocationAsync(location.Id));
    }

    private async Task SetLocationActiveAsync(AdminLocation location, bool isActive)
    {
        var action = isActive ? "activate" : "deactivate";
        if (!await DisplayAlertAsync(
                $"{char.ToUpperInvariant(action[0])}{action[1..]} location",
                $"{(isActive ? "Make" : "Remove")} {location.Name} {(isActive ? "available" : "unavailable")} for new customer bookings? Existing reservations will remain unchanged.",
                char.ToUpperInvariant(action[0]) + action[1..],
                "Cancel"))
        {
            return;
        }

        await RunAdminActionAsync(() => _apiClient.SetLocationActiveAsync(location.Id, isActive));
    }

    private async void AddSlotButton_Clicked(object? sender, EventArgs e)
    {
        if (_overview?.Locations.Count is not > 0)
        {
            await DisplayAlertAsync("No locations", "Add a parking location before creating slots.", "OK");
            return;
        }

        var locationsByChoice = _overview.Locations
            .Select((location, index) => (
                Choice: $"{location.Name} — {location.Address} (#{index + 1})",
                Location: location))
            .ToDictionary(item => item.Choice, item => item.Location, StringComparer.Ordinal);
        var selectedChoice = await DisplayActionSheetAsync(
            "Choose parking location",
            "Cancel",
            null,
            locationsByChoice.Keys.ToArray());
        if (string.IsNullOrWhiteSpace(selectedChoice) ||
            selectedChoice == "Cancel" ||
            !locationsByChoice.TryGetValue(selectedChoice, out var selectedLocation))
        {
            return;
        }

        var label = await DisplayPromptAsync("Add parking slot", "Enter the slot label.", "Save", "Cancel", "Slot label", maxLength: 32);
        if (label is null)
        {
            return;
        }

        var status = await DisplayActionSheetAsync("Initial availability", "Cancel", null, SlotStatuses);
        if (string.IsNullOrWhiteSpace(status) || status == "Cancel")
        {
            return;
        }

        await RunAdminActionAsync(() => _apiClient.UpsertSlotAsync(
            null,
            selectedLocation.Id,
            label.Trim(),
            status));
    }

    private async Task ChangeSlotStatusAsync(AdminSlot slot, string status)
    {
        await RunAdminActionAsync(() => _apiClient.SetSlotAvailabilityAsync(slot.Id, status));
    }

    private async Task EditSlotAsync(AdminSlot slot)
    {
        var label = await DisplayPromptAsync(
            "Edit parking slot",
            "Update the slot label.",
            "Save",
            "Cancel",
            "Slot label",
            initialValue: slot.Label,
            maxLength: 32);
        if (label is null)
        {
            return;
        }

        await RunAdminActionAsync(() => _apiClient.UpsertSlotAsync(
            slot.Id,
            slot.LocationId,
            label.Trim(),
            slot.Status));
    }

    private async Task DeleteSlotAsync(AdminSlot slot)
    {
        if (!await DisplayAlertAsync("Delete slot", $"Delete parking slot {slot.Label}?", "Delete", "Cancel"))
        {
            return;
        }

        await RunAdminActionAsync(() => _apiClient.DeleteSlotAsync(slot.Id));
    }

    private async Task SetReservationStatusAsync(string reservationId, string status)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
        {
            ShowError("This reservation is missing its ID and cannot be updated.");
            return;
        }

        if (!await DisplayAlertAsync(
                $"{status} reservation",
                $"Change this reservation to {status.ToLowerInvariant()}?",
                status,
                "Keep"))
        {
            return;
        }

        await RunAdminActionAsync(() => _apiClient.SetReservationStatusAsync(reservationId, status));
    }

    private async void ReservationStatusPicker_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_overview is not null)
        {
            RenderReservations(_overview);
        }
    }

    private void RenderReservations(AdminOverview overview)
    {
        ReservationsList.Children.Clear();
        var selectedStatus = ReservationStatusPicker.SelectedItem as string ?? "All";
        var filteredReservations = overview.Reservations
            .Where(reservation => selectedStatus == "All" || GetJsonString(reservation, "status") == selectedStatus)
            .ToList();
        foreach (var reservation in filteredReservations)
        {
            var id = GetJsonString(reservation, "id");
            var status = GetJsonString(reservation, "status");
            var userId = GetJsonString(reservation, "userId");
            var userEmail = GetJsonString(reservation, "userEmail");
            var location = GetJsonString(reservation, "locationName");
            var slot = GetJsonString(reservation, "slotLabel");
            var start = GetJsonString(reservation, "startTime");
            var fee = GetJsonDecimal(reservation, "fee");
            var userDetails = string.IsNullOrWhiteSpace(userEmail) ? userId : userEmail;
            var details = $"{location} · Slot {slot}\nUser: {userDetails}\n{start} · ₱{fee:N2} · {status}\nID: {id}";
            if (status == "Pending")
            {
                ReservationsList.Children.Add(CreateRow(
                    details,
                    ("Approve", async () => await SetReservationStatusAsync(id, "Confirmed")),
                    ("Reject", async () => await SetReservationStatusAsync(id, "Cancelled"))));
            }
            else if (status == "Confirmed")
            {
                ReservationsList.Children.Add(CreateRow(
                    details,
                    ("Complete", async () => await SetReservationStatusAsync(id, "Completed")),
                    ("Cancel", async () => await SetReservationStatusAsync(id, "Cancelled"))));
            }
            else
            {
                ReservationsList.Children.Add(CreateDataRow(details));
            }
        }

        AddEmptyMessage(
            ReservationsList,
            filteredReservations.Count,
            overview.Report.ReservationsUnavailable
                ? "Reservation records could not be loaded."
                : "No reservations for this status.");
    }

    private async void SendNotificationButton_Clicked(object? sender, EventArgs e)
    {
        if (_overview is null)
        {
            await DisplayAlertAsync("Dashboard unavailable", "Load the dashboard before sending an announcement.", "OK");
            return;
        }

        var title = await DisplayPromptAsync("Send announcement", "Announcement title", "Next", "Cancel", "Title", maxLength: 100);
        if (title is null)
        {
            return;
        }
        var message = await DisplayPromptAsync("Send announcement", "Announcement message", "Next", "Cancel", "Message", maxLength: 1000);
        if (message is null)
        {
            return;
        }

        SetBusy(true);
        StatusLabel.IsVisible = false;
        try
        {
            await _apiClient.SendNotificationAsync(title.Trim(), message.Trim(), null);
            await DisplayAlertAsync(
                "Announcement sent",
                "A global in-app announcement was published. Signed-in users will see it when they open Notifications.",
                "OK");
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
            ShowError("Could not connect to Firestore. Check your connection and try again.");
        }
        catch (TaskCanceledException)
        {
            ShowError("The Firestore request timed out. Please try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ChangeAdminPasswordButton_Clicked(object? sender, EventArgs e)
    {
        ChangeAdminPasswordButton.IsVisible = false;
        AdminPasswordForm.IsVisible = true;
        AdminCurrentPasswordEntry.Focus();
    }

    private void CancelAdminPasswordButton_Clicked(object? sender, EventArgs e)
    {
        ClearAdminPasswordFields();
        AdminPasswordForm.IsVisible = false;
        ChangeAdminPasswordButton.IsVisible = true;
    }

    private async void SaveAdminPasswordButton_Clicked(object? sender, EventArgs e)
    {
        var email = AuthSession.CurrentUser?.Email ?? string.Empty;
        var currentPassword = AdminCurrentPasswordEntry.Text ?? string.Empty;
        var newPassword = AdminNewPasswordEntry.Text ?? string.Empty;
        var confirmation = AdminConfirmPasswordEntry.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(currentPassword) ||
            string.IsNullOrEmpty(newPassword) || string.IsNullOrEmpty(confirmation))
        {
            await DisplayAlertAsync("Complete all fields", "Enter the current password and the new password twice.", "OK");
            return;
        }
        if (newPassword.Length < 6)
        {
            await DisplayAlertAsync("Password too short", "Choose a password with at least 6 characters.", "OK");
            return;
        }
        if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
        {
            await DisplayAlertAsync("Passwords don't match", "Re-enter the same new password in both fields.", "OK");
            return;
        }

        SetBusy(true);
        try
        {
            var updatedUser = await _authService.ChangePasswordAsync(email, currentPassword, newPassword);
            AuthSession.Update(updatedUser);
            ClearAdminPasswordFields();
            AdminPasswordForm.IsVisible = false;
            ChangeAdminPasswordButton.IsVisible = true;
            await DisplayAlertAsync("Password updated", "Your admin password was changed.", "OK");
        }
        catch (FirebaseAuthException exception)
        {
            ShowError(exception.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not connect to Firebase Authentication. Try again.");
        }
        catch (TaskCanceledException)
        {
            ShowError("The password update request timed out. Please try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ClearAdminPasswordFields()
    {
        AdminCurrentPasswordEntry.Text = string.Empty;
        AdminNewPasswordEntry.Text = string.Empty;
        AdminConfirmPasswordEntry.Text = string.Empty;
    }

    private async Task RunAdminActionAsync(Func<Task> action)
    {
        SetBusy(true);
        StatusLabel.IsVisible = false;
        try
        {
            await action();
            await LoadOverviewAsync();
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
        catch (FirebaseAuthException exception)
        {
            ShowError(exception.Message);
        }
        catch (HttpRequestException exception)
        {
            ShowError($"QuickPark could not reach Firebase: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            ShowError("The QuickPark Firebase request timed out. Please try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static View CreateRow(string details, params (string Text, Func<Task> Action)[] actions)
    {
        var content = new VerticalStackLayout
        {
            Spacing = 10
        };
        content.Children.Add(new Label
        {
            Text = details,
            FontSize = 12,
            TextColor = Color.FromArgb("#263A57"),
            VerticalOptions = LayoutOptions.Center,
            LineHeight = 1.2
        });

        var actionButtons = new HorizontalStackLayout
        {
            Spacing = 7,
            HorizontalOptions = LayoutOptions.End
        };
        for (var index = 0; index < actions.Length; index++)
        {
            var action = actions[index];
            var button = new Button
            {
                Text = action.Text,
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = action.Text == "Delete" || action.Text == "Reject" || action.Text == "Cancel"
                    ? Color.FromArgb("#A83A45")
                    : Color.FromArgb("#205F9F"),
                BackgroundColor = action.Text == "Delete" || action.Text == "Reject" || action.Text == "Cancel"
                    ? Color.FromArgb("#FFF0F0")
                    : Color.FromArgb("#EAF3FF"),
                Padding = new Thickness(10, 0),
                HeightRequest = 38,
                CornerRadius = 13,
                Command = new Command(async () => await action.Action())
            };
            actionButtons.Children.Add(button);
        }
        if (actionButtons.Children.Count > 0)
        {
            content.Children.Add(actionButtons);
        }

        return new Border
        {
            BackgroundColor = Color.FromArgb("#F7F9FC"),
            Stroke = Color.FromArgb("#E5ECF4"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(16) },
            Padding = new Thickness(12, 10),
            Content = content
        };
    }

    private static View CreateDataRow(string text)
    {
        return new Border
        {
            BackgroundColor = Color.FromArgb("#F7F9FC"),
            Stroke = Color.FromArgb("#E5ECF4"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(16) },
            Padding = new Thickness(13, 11),
            Content = new Label
            {
                Text = text,
                FontSize = 11,
                TextColor = Color.FromArgb("#263A57"),
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 4
            }
        };
    }

    private static string GetJsonString(JsonElement document, string propertyName)
    {
        if (!document.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                ? value.ToString()
                : string.Empty;
    }

    private static decimal GetJsonDecimal(JsonElement document, string propertyName)
    {
        return document.TryGetProperty(propertyName, out var value) && value.TryGetDecimal(out var number)
            ? number
            : 0;
    }

    private static string FormatPayment(JsonElement payment, IReadOnlyCollection<AdminUser> users)
    {
        var userId = GetJsonString(payment, "userId");
        var userEmail = users.FirstOrDefault(user => user.Uid == userId)?.Email ?? userId;
        var amount = GetJsonDecimal(payment, "amount");
        return
            $"Receipt {GetJsonString(payment, "receiptNumber")}\n" +
            $"₱{amount:N2} {GetJsonString(payment, "currency")} · {GetJsonString(payment, "method")}\n" +
            $"Status: {GetJsonString(payment, "status")}\n" +
            $"User: {userEmail} · Reservation: {GetJsonString(payment, "reservationId")}";
    }

    private static void AddEmptyMessage(VerticalStackLayout list, int count, string message)
    {
        if (count == 0)
        {
            list.Children.Add(new Label
            {
                Text = message,
                FontSize = 12,
                TextColor = Color.FromArgb("#75849A"),
                Margin = new Thickness(2, 4)
            });
        }
    }

    private static string FormatCount(int count) =>
        count < 0 ? "—" : count.ToString(CultureInfo.InvariantCulture);

    private static string NextSlotStatus(string current)
    {
        var currentIndex = Array.IndexOf(SlotStatuses, current);
        return SlotStatuses[(currentIndex + 1 + SlotStatuses.Length) % SlotStatuses.Length];
    }

    private void ShowError(string message)
    {
        StatusLabel.Text = message;
        StatusLabel.IsVisible = true;
    }

    private void SetBusy(bool isBusy)
    {
        LoadingIndicator.IsVisible = isBusy;
        LoadingIndicator.IsRunning = isBusy;
    }
}
