using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Networking;
using System.Net.Http;
using System.Text.Json;

namespace QuickPark;

public partial class HomePage : ContentPage
{
    private const string DisplayNameKeyPrefix = "QuickPark.DisplayName.";
#if !WINDOWS
    private static readonly TimeSpan MapLocationRefreshInterval = TimeSpan.FromSeconds(15);
#endif
    private string _selectedCategory = "All locations";
    private readonly string _email;
    private readonly BookingApiClient _bookingApi = new();
    private readonly NotificationsApiClient _notificationsApi = new();
    private List<AdminLocation> _locations = [];
    private List<AdminSlot> _availableSlots = [];
    private List<JsonElement> _notifications = [];
    private Location? _currentLocation;
    private AdminLocation? _selectedMapLocation;
    private bool _parkingDataLoadError;
    private bool _isLoadingMapLocation;
#if !WINDOWS
    private bool _isPageActive;
    private bool _mapLocationTimerScheduled;
#endif
    private bool _sortParkingByDistance;
    private bool _isRefreshingDashboard;
    private double _mapRadiusKilometers = 2;
    private bool _fallbackMapLoaded;
    private DateTime _lastGpsFixAt = DateTime.MinValue;
    private DateTime _lastDashboardRefreshAt = DateTime.MinValue;
    private bool? _isCompactLayout;
    private bool? _isSingleColumnLayout;
    private bool _connectivityHandlerAttached;

    public HomePage(string email)
    {
        InitializeComponent();
        _email = email;
#if ANDROID
        LiveParkingMap.IsVisible = false;
#endif
        FallbackParkingMap.Navigated += FallbackParkingMap_Navigated;
        WelcomeBanner.SizeChanged += WelcomeBanner_SizeChanged;
        UpdateWelcomeLabel();
    }

    private void WelcomeBanner_SizeChanged(object? sender, EventArgs e)
    {
        var bannerWidth = WelcomeBanner.Width;
        if (bannerWidth <= 0)
        {
            return;
        }

        var isCompactBanner = bannerWidth < 780;
        WelcomeBannerArtImage.Aspect = isCompactBanner ? Aspect.AspectFit : Aspect.AspectFill;
        var artHeight = Math.Min(260, bannerWidth * (670d / 2000d));
        if (Math.Abs(WelcomeBannerArtImage.HeightRequest - artHeight) > 1)
        {
            WelcomeBannerArtImage.HeightRequest = artHeight;
        }

        WelcomeHeadline.FontSize = bannerWidth < 440 ? 17 : 20;
    }

    private void RenderParkingSpotPreview(string searchQuery)
    {
        ParkingSpotPreview.Children.Clear();
        var matchingLocations = _locations
            .Where(location =>
                string.IsNullOrWhiteSpace(searchQuery) ||
                location.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
                location.Address.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
            .Where(location =>
            {
                var availableCount = _availableSlots.Count(slot => slot.LocationId == location.Id);
                return _selectedCategory switch
                {
                    "Available" => availableCount > 0,
                    "No open slots" => availableCount == 0,
                    _ => true
                };
            })
            .Select(location => location.Id)
            .ToHashSet(StringComparer.Ordinal);
        var locationsById = _locations.ToDictionary(location => location.Id, StringComparer.Ordinal);
        var availableSlots = _availableSlots
            .Where(slot => matchingLocations.Contains(slot.LocationId) && locationsById.ContainsKey(slot.LocationId))
            .OrderBy(slot => locationsById[slot.LocationId].Name)
            .ThenBy(slot => slot.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ParkingSpotPreviewStatusLabel.Text = availableSlots.Count == 0
            ? _locations.Count == 0
                ? "Live spaces will appear when parking locations are available."
                : "No available spaces match this search. Try another location or filter."
            : $"{availableSlots.Count} live spaces · Choose one to continue booking.";
        ParkingSpotPreviewStatusLabel.TextColor = availableSlots.Count == 0
            ? Color.FromArgb("#63778E")
            : Color.FromArgb("#18794E");

        foreach (var slot in availableSlots.Take(12))
        {
            var location = locationsById[slot.LocationId];
            var spotButton = new Button
            {
                Text = $"P\n{slot.Label}",
                FontSize = 10,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#18794E"),
                BackgroundColor = Color.FromArgb("#DDF5E6"),
                BorderColor = Color.FromArgb("#B8E7C9"),
                BorderWidth = 1,
                HeightRequest = 56,
                WidthRequest = 76,
                CornerRadius = 11,
                Padding = new Thickness(3, 4),
                Margin = new Thickness(0, 0, 7, 7),
                CommandParameter = slot,
                AutomationId = $"AvailableSpot_{slot.Id}"
            };
            spotButton.Clicked += BookingLocationButton_Clicked;
            SemanticProperties.SetDescription(spotButton, $"Book slot {slot.Label} at {location.Name}");
            ParkingSpotPreview.Children.Add(spotButton);
        }

        if (availableSlots.Count > 12)
        {
            ParkingSpotPreview.Children.Add(new Label
            {
                Text = $"+{availableSlots.Count - 12} more",
                FontSize = 10,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#40556F"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(4, 0)
            });
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if !WINDOWS
        _isPageActive = true;
#endif
        UpdateNavigationSelection("Home");
        UpdateWelcomeLabel();
        if (!_connectivityHandlerAttached)
        {
            Connectivity.Current.ConnectivityChanged += Connectivity_ConnectivityChanged;
            _connectivityHandlerAttached = true;
        }
        UpdateOfflineStatus(Connectivity.Current.NetworkAccess);
        if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
        {
            await _bookingApi.SyncPendingChangesAsync();
        }
        await RefreshDashboardAsync();
        _lastDashboardRefreshAt = DateTime.UtcNow;

#if WINDOWS
        ParkingMapStatusLabel.Text =
            "Parking availability can be refreshed here. Live map controls are available on Android.";
        return;
#else
        _ = LoadMapCurrentLocationAsync();
        if (!_mapLocationTimerScheduled)
        {
            _mapLocationTimerScheduled = true;
            Dispatcher.StartTimer(MapLocationRefreshInterval, () =>
            {
                if (!_isPageActive)
                {
                    _mapLocationTimerScheduled = false;
                    return false;
                }

                _ = LoadMapCurrentLocationAsync();
                if (DateTime.UtcNow - _lastDashboardRefreshAt >= TimeSpan.FromMinutes(1))
                {
                    _lastDashboardRefreshAt = DateTime.UtcNow;
                    _ = RefreshDashboardAsync(fitMapRegion: false);
                }
                return true;
            });
        }
#endif
    }

    protected override void OnDisappearing()
    {
        if (_connectivityHandlerAttached)
        {
            Connectivity.Current.ConnectivityChanged -= Connectivity_ConnectivityChanged;
            _connectivityHandlerAttached = false;
        }
#if !WINDOWS
        _isPageActive = false;
        LiveParkingMap.IsShowingUser = false;
#endif
        base.OnDisappearing();
    }

    private void Connectivity_ConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            UpdateOfflineStatus(e.NetworkAccess);
            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                await _bookingApi.SyncPendingChangesAsync();
                await RefreshDashboardAsync();
            }
        });
    }

    private void UpdateOfflineStatus(NetworkAccess networkAccess)
    {
        var isOffline = networkAccess != NetworkAccess.Internet;
        OfflineStatusNotice.IsVisible = isOffline;
        MapNavigateButton.IsEnabled = !isOffline;
        if (isOffline)
        {
            OfflineStatusLabel.Text = "Offline: showing saved parking information. Availability may have changed. Bookings and profile changes sync when internet returns; live maps and directions need internet.";
            OfflineStatusLabel.TextColor = Color.FromArgb("#624B16");
        }
        else
        {
            ParkingMapStatusLabel.Text = "Connected. Parking information is being refreshed.";
        }
    }

    private async Task LoadMapCurrentLocationAsync(bool requestPermission = false)
    {
        if (_isLoadingMapLocation)
        {
            return;
        }

        _isLoadingMapLocation = true;
        try
        {
            var permission = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (permission != PermissionStatus.Granted && requestPermission)
            {
                permission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }

            if (permission != PermissionStatus.Granted)
            {
                LiveParkingMap.IsShowingUser = false;
                _currentLocation = null;
#if ANDROID
                if (_fallbackMapLoaded)
                {
                    await FallbackParkingMap.EvaluateJavaScriptAsync("window.quickParkClearUserLocation()");
                }
#endif
                ParkingMapStatusLabel.Text = "Allow location access to show your live position. Parking pins remain available.";
                return;
            }

            var location = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10)));
            if (location is null ||
                location.Latitude is < -90 or > 90 ||
                location.Longitude is < -180 or > 180)
            {
                ParkingMapStatusLabel.Text = "Your current location is not available. Parking pins remain visible.";
                return;
            }

            _currentLocation = location;
            _lastGpsFixAt = DateTime.UtcNow;
#if ANDROID
            if (_fallbackMapLoaded)
            {
                var latitude = location.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var longitude = location.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await FallbackParkingMap.EvaluateJavaScriptAsync(
                    $"window.quickParkUpdateUserLocation({latitude}, {longitude})");
            }
#else
            LiveParkingMap.IsShowingUser = true;
#endif
            if (!_parkingDataLoadError && _locations.Count > 0)
            {
                ParkingMapStatusLabel.Text = location.Accuracy is { } accuracy
                    ? $"Live GPS position · accuracy ±{accuracy:0} m. Parking data is refreshed from Firebase."
                    : "Live GPS position. Parking data is refreshed from Firebase.";
                ParkingMapStatusLabel.TextColor = Color.FromArgb("#18794E");
            }
        }
        catch (FeatureNotEnabledException)
        {
            ParkingMapStatusLabel.Text = "Turn on device location to show your position.";
        }
        catch (FeatureNotSupportedException)
        {
            ParkingMapStatusLabel.Text = "Current location is not supported on this device.";
        }
        catch (PermissionException)
        {
            ParkingMapStatusLabel.Text = "Location permission is unavailable. Parking pins remain visible.";
        }
        catch (TimeoutException)
        {
            ParkingMapStatusLabel.Text = "GPS timed out. Parking pins remain visible.";
        }
        catch (TaskCanceledException)
        {
            ParkingMapStatusLabel.Text = "GPS lookup timed out. Parking pins remain visible.";
        }
        catch (InvalidOperationException exception)
        {
            ParkingMapStatusLabel.Text = $"Could not read current location: {exception.Message}";
        }
        finally
        {
            _isLoadingMapLocation = false;
        }
    }

    private async void RecenterParkingMapButton_Clicked(object? sender, EventArgs e)
    {
#if WINDOWS
        await DisplayAlertAsync(
            "Map unavailable",
            "Live map controls are temporarily unavailable on Windows. Use QuickPark on Android for live map navigation.",
            "OK");
        return;
#else
        await LoadMapCurrentLocationAsync(requestPermission: true);
        if (_currentLocation is not null &&
            DateTime.UtcNow - _lastGpsFixAt <= TimeSpan.FromSeconds(45))
        {
#if ANDROID
            if (_fallbackMapLoaded)
            {
                var latitude = _currentLocation.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var longitude = _currentLocation.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await FallbackParkingMap.EvaluateJavaScriptAsync(
                    $"window.quickParkRecenter({latitude}, {longitude})");
            }
#else
            LiveParkingMap.MoveToRegion(MapSpan.FromCenterAndRadius(
                _currentLocation,
                Distance.FromKilometers(_mapRadiusKilometers)));
#endif
        }
        else
        {
            await DisplayAlertAsync(
                "Location unavailable",
                "QuickPark could not get a recent GPS fix. Turn on device location and try again.",
                "OK");
        }
#endif
    }

    private void ZoomInParkingMapButton_Clicked(object? sender, EventArgs e)
    {
#if !WINDOWS
        _mapRadiusKilometers = Math.Max(0.15, _mapRadiusKilometers / 1.7);
        MoveParkingMapToCurrentRegion();
#if ANDROID
        if (_fallbackMapLoaded)
        {
            _ = FallbackParkingMap.EvaluateJavaScriptAsync("window.quickParkZoomIn()");
        }
#endif
#endif
    }

    private void ZoomOutParkingMapButton_Clicked(object? sender, EventArgs e)
    {
#if !WINDOWS
        _mapRadiusKilometers = Math.Min(100, _mapRadiusKilometers * 1.7);
        MoveParkingMapToCurrentRegion();
#if ANDROID
        if (_fallbackMapLoaded)
        {
            _ = FallbackParkingMap.EvaluateJavaScriptAsync("window.quickParkZoomOut()");
        }
    #endif
#endif
    }

    private void MoveParkingMapToCurrentRegion()
    {
        var center = _currentLocation;
        if (center is null && _locations.FirstOrDefault(location =>
                location.Latitude is >= -90 and <= 90 &&
                location.Longitude is >= -180 and <= 180) is { } parking)
        {
            center = new Location(parking.Latitude!.Value, parking.Longitude!.Value);
        }

        if (center is not null)
        {
#if ANDROID
            if (_fallbackMapLoaded)
            {
                var latitude = center.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var longitude = center.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _ = FallbackParkingMap.EvaluateJavaScriptAsync(
                    $"window.quickParkRecenter({latitude}, {longitude})");
            }
            return;
#else
            LiveParkingMap.MoveToRegion(MapSpan.FromCenterAndRadius(
                center,
                Distance.FromKilometers(_mapRadiusKilometers)));
#endif
        }
    }

    private void FallbackParkingMap_Navigated(object? sender, WebNavigatedEventArgs e)
    {
        _fallbackMapLoaded = e.Result == WebNavigationResult.Success;
    }

    private void UpdateWelcomeLabel()
    {
        var defaultName = string.IsNullOrWhiteSpace(_email)
            ? "QuickPark Driver"
            : _email.Split('@')[0];
        ProfileNameLabel.Text = Preferences.Default.Get(
            $"{DisplayNameKeyPrefix}{_email}",
            defaultName);
        var hour = DateTime.Now.Hour;
        var greeting = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        WelcomeHeadline.Text = $"{greeting}, {GetFirstName(ProfileNameLabel.Text)}";
        ProfileEmailLabel.Text = _email;
        ProfileAvatarFallback.Text = ProfilePhotoStore.GetInitial(ProfileNameLabel.Text);
        var profilePhotoPath = ProfilePhotoStore.GetSavedPhotoPath();
        if (profilePhotoPath is null)
        {
            DashboardProfilePhotoImage.IsVisible = false;
            return;
        }

        DashboardProfilePhotoImage.Source = ImageSource.FromFile(profilePhotoPath);
        DashboardProfilePhotoImage.IsVisible = true;
    }

    private static string GetFirstName(string displayName)
    {
        var firstName = displayName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstName) ? "driver" : firstName;
    }

    private void UpdateReservationSummary()
    {
        var reservation = BookingReservationStore.Current;
        ReservationSummaryLabel.Text = reservation is null
            ? "No active reservation"
            : $"{reservation.Location} · {reservation.Status}";
        ReservationDetailLabel.Text = reservation is null
            ? "Your current reservation appears here."
            : $"{reservation.StartTime.ToLocalTime():MMM d · h:mm tt} · Slot {reservation.Slot}";
        BookingsMetricLabel.Text = reservation is null ? "0" : "1";
    }

    private async Task RefreshDashboardAsync(bool fitMapRegion = true)
    {
        if (_isRefreshingDashboard)
        {
            return;
        }

        _isRefreshingDashboard = true;
        try
        {
            await Task.WhenAll(
                LoadParkingOptionsAsync(fitMapRegion),
                LoadCurrentReservationAsync(),
                LoadNotificationCountAsync());
        }
        finally
        {
            _isRefreshingDashboard = false;
        }
    }

    private async Task LoadParkingOptionsAsync(bool fitMapRegion = true)
    {
        ParkingMapLoadingIndicator.IsVisible = true;
        ParkingMapLoadingIndicator.IsRunning = true;
        ParkingMapStatusLabel.Text = Connectivity.Current.NetworkAccess == NetworkAccess.Internet
            ? "Loading parking locations…"
            : "Showing the last saved parking information. Availability may have changed.";
        ParkingMapStatusLabel.TextColor = Color.FromArgb("#63778E");
        ParkingStatusLabel.Text = Connectivity.Current.NetworkAccess == NetworkAccess.Internet
            ? "Loading live locations and slot availability…"
            : "Loading saved locations and availability…";
        ParkingStatusLabel.TextColor = Color.FromArgb("#7B8DA4");
        try
        {
            var options = await _bookingApi.GetOptionsAsync();
            _locations = options.Locations
                .Where(location => location.IsActive)
                .OrderBy(location => location.Name)
                .ToList();
            _availableSlots = options.Slots;
            _parkingDataLoadError = false;
            if (_sortParkingByDistance && _currentLocation is null)
            {
                _sortParkingByDistance = false;
                FindNearbyParkingButton.Text = "Near me";
            }
            AvailableSlotsMetricLabel.Text = _availableSlots.Count.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            ParkingStatusLabel.Text = Connectivity.Current.NetworkAccess == NetworkAccess.Internet
                ? $"{_locations.Count} locations · {_availableSlots.Count} available slots · refreshed {DateTime.Now:h:mm tt}"
                : $"{_locations.Count} saved locations · {_availableSlots.Count} saved available slots · availability may have changed";
            RenderParkingLocations(fitMapRegion);
        }
        catch (InvalidOperationException exception)
        {
            ShowParkingLoadError(exception.Message);
        }
        catch (FirebaseAuthException exception)
        {
            ShowParkingLoadError(exception.Message);
        }
        catch (HttpRequestException exception)
        {
            ShowParkingLoadError($"QuickPark could not load parking availability: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            ShowParkingLoadError("Loading parking availability timed out. Please refresh.");
        }
        catch (JsonException)
        {
            ShowParkingLoadError("QuickPark received unreadable parking data. Refresh to try again.");
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
            }
            else
            {
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
            }

            UpdateReservationSummary();
        }
        catch (InvalidOperationException exception)
        {
            ShowReservationLoadError(exception.Message);
        }
        catch (FirebaseAuthException exception)
        {
            ShowReservationLoadError(exception.Message);
        }
        catch (HttpRequestException)
        {
            ShowReservationLoadError("Could not connect to the reservation service. Refresh to try again.");
        }
        catch (TaskCanceledException)
        {
            ShowReservationLoadError("Loading your reservation timed out. Refresh to try again.");
        }
    }

    private async Task LoadNotificationCountAsync()
    {
        try
        {
            var response = await _notificationsApi.GetMyNotificationsAsync();
            _notifications = response.Notifications
                .OrderByDescending(GetNotificationTimestamp)
                .ToList();
            var unreadCount = _notifications.Count(notification => !GetNotificationRead(notification));
            NotificationsMetricLabel.Text = unreadCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            NotificationsDetailLabel.Text = unreadCount == 1
                ? "1 unread update"
                : unreadCount > 1
                    ? $"{unreadCount} unread updates"
                    : $"{_notifications.Count} updates in inbox";
            NotificationsPreviewStatusLabel.Text = _notifications.Count == 0
                ? "Your latest parking updates"
                : unreadCount == 0
                    ? "All updates have been read"
                    : $"{unreadCount} unread · {_notifications.Count} in your inbox";
            RenderNotificationPreview();
        }
        catch (InvalidOperationException)
        {
            SetNotificationLoadError("Sign in to view inbox");
        }
        catch (FirebaseAuthException)
        {
            SetNotificationLoadError("Sign in to view inbox");
        }
        catch (HttpRequestException)
        {
            SetNotificationLoadError("Refresh to retry");
        }
        catch (TaskCanceledException)
        {
            SetNotificationLoadError("Refresh to retry");
        }
    }

    private void SetNotificationLoadError(string message)
    {
        _notifications = [];
        NotificationsMetricLabel.Text = "—";
        NotificationsDetailLabel.Text = message;
        NotificationsPreviewStatusLabel.Text = message;
        RenderNotificationPreview();
    }

    private void RenderNotificationPreview()
    {
        NotificationPreviewList.Children.Clear();
        if (_notifications.Count == 0)
        {
            NotificationPreviewList.Children.Add(new Label
            {
                Text = NotificationsMetricLabel.Text == "—"
                    ? "Parking updates couldn't be loaded. Open notifications to retry."
                    : "No parking updates yet. Booking requests and announcements will appear here.",
                FontSize = 10,
                TextColor = Color.FromArgb("#74869C"),
                Margin = new Thickness(2, 4)
            });
            return;
        }

        foreach (var notification in _notifications.Take(3))
        {
            var title = GetNotificationValue(notification, "title");
            var message = GetNotificationValue(notification, "message");
            var type = GetNotificationValue(notification, "type");
            var icon = type.Equals("Booking", StringComparison.OrdinalIgnoreCase) ? "▦" : "♧";
            var iconColor = type.Equals("Booking", StringComparison.OrdinalIgnoreCase)
                ? "#7046E8"
                : "#1769D2";
            var timestamp = GetNotificationTimestamp(notification);
            var dateText = timestamp == DateTime.MinValue
                ? "QuickPark update"
                : timestamp.Date == DateTime.Today
                    ? $"Today, {timestamp:h:mm tt}"
                    : timestamp.ToString("MMM d, h:mm tt");

            var previewRow = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(34)),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 9,
            };
            var iconContainer = new Border
            {
                WidthRequest = 32,
                HeightRequest = 32,
                BackgroundColor = Color.FromArgb(type.Equals("Booking", StringComparison.OrdinalIgnoreCase)
                    ? "#F0ECFF"
                    : "#EAF2FF"),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(16) },
                Content = new Label
                {
                    Text = icon,
                    FontSize = 15,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb(iconColor),
                    HorizontalTextAlignment = TextAlignment.Center,
                    VerticalTextAlignment = TextAlignment.Center
                }
            };
            var notificationContent = new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    new Label
                    {
                        Text = string.IsNullOrWhiteSpace(title) ? "QuickPark update" : title,
                        FontSize = 10,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Color.FromArgb("#243D5C"),
                        LineBreakMode = LineBreakMode.TailTruncation,
                        MaxLines = 1
                    },
                    new Label
                    {
                        Text = string.IsNullOrWhiteSpace(message) ? "Open notifications for details." : message,
                        FontSize = 9,
                        TextColor = Color.FromArgb("#74869C"),
                        LineBreakMode = LineBreakMode.TailTruncation,
                        MaxLines = 2
                    },
                    new Label
                    {
                        Text = dateText,
                        FontSize = 8,
                        TextColor = Color.FromArgb("#8A9AAF")
                    }
                }
            };
            Grid.SetColumn(notificationContent, 1);
            previewRow.Children.Add(iconContainer);
            previewRow.Children.Add(notificationContent);
            var unreadIndicator = new Label
            {
                Text = GetNotificationRead(notification) ? string.Empty : "●",
                FontSize = 10,
                TextColor = Color.FromArgb("#1769D2"),
                VerticalOptions = LayoutOptions.Center,
                AutomationId = $"NotificationUnreadIndicator_{GetNotificationValue(notification, "id")}"
            };
            Grid.SetColumn(unreadIndicator, 2);
            previewRow.Children.Add(unreadIndicator);
            NotificationPreviewList.Children.Add(previewRow);
        }
    }

    private static string GetNotificationValue(JsonElement notification, string propertyName)
    {
        return notification.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool GetNotificationRead(JsonElement notification) =>
        notification.TryGetProperty("read", out var read) &&
        read.ValueKind == JsonValueKind.True;

    private static DateTime GetNotificationTimestamp(JsonElement notification)
    {
        return DateTime.TryParse(
            GetNotificationValue(notification, "createdAt"),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var timestamp)
                ? timestamp.ToLocalTime()
                : DateTime.MinValue;
    }

    private void ShowParkingLoadError(string message)
    {
        _locations = [];
        _availableSlots = [];
        AvailableSlotsMetricLabel.Text = "—";
        ParkingStatusLabel.Text = message;
        ParkingStatusLabel.TextColor = Color.FromArgb("#B42318");
        _parkingDataLoadError = true;
        RenderParkingLocations();
    }

    private void ShowReservationLoadError(string message)
    {
        ReservationSummaryLabel.Text = "Reservation unavailable";
        ReservationDetailLabel.Text = message;
        BookingsMetricLabel.Text = "—";
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0)
        {
            return;
        }

        var isCompact = width < 900;
        var isSingleColumn = width < 1120;
        if (_isCompactLayout == isCompact && _isSingleColumnLayout == isSingleColumn)
        {
            return;
        }

        _isCompactLayout = isCompact;
        _isSingleColumnLayout = isSingleColumn;

        DashboardLayout.ColumnDefinitions.Clear();
        DashboardLayout.ColumnDefinitions.Add(new ColumnDefinition(isCompact ? new GridLength(0) : new GridLength(210)));
        DashboardLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        DashboardSidebar.IsVisible = !isCompact;
        CompactNavigation.IsVisible = isCompact;
        CompactBottomNavigation.IsVisible = isCompact;

        DashboardColumns.ColumnDefinitions.Clear();
        DashboardColumns.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        DashboardColumns.RowDefinitions.Clear();
        if (isSingleColumn)
        {
            for (var row = 0; row < 5; row++)
            {
                DashboardColumns.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
            Grid.SetRow(ParkingSection, 0);
            Grid.SetColumn(ParkingSection, 0);
            Grid.SetRowSpan(ParkingSection, 1);
            Grid.SetRow(NotificationsSection, 1);
            Grid.SetColumn(NotificationsSection, 0);
            Grid.SetRow(ReservationSection, 2);
            Grid.SetColumn(ReservationSection, 0);
            Grid.SetRow(ProfileSection, 3);
            Grid.SetColumn(ProfileSection, 0);
            Grid.SetRow(QuickActionsSection, 4);
            Grid.SetColumn(QuickActionsSection, 0);
        }
        else
        {
            DashboardColumns.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(300)));
            for (var row = 0; row < 4; row++)
            {
                DashboardColumns.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
            Grid.SetRow(ParkingSection, 0);
            Grid.SetColumn(ParkingSection, 0);
            Grid.SetRowSpan(ParkingSection, 4);
            Grid.SetRow(NotificationsSection, 0);
            Grid.SetColumn(NotificationsSection, 1);
            Grid.SetRow(ReservationSection, 1);
            Grid.SetColumn(ReservationSection, 1);
            Grid.SetRow(ProfileSection, 2);
            Grid.SetColumn(ProfileSection, 1);
            Grid.SetRow(QuickActionsSection, 3);
            Grid.SetColumn(QuickActionsSection, 1);
        }

        SummaryCards.ColumnDefinitions.Clear();
        SummaryCards.RowDefinitions.Clear();
        var metricColumns = width < 550 ? 1 : width < 1120 ? 2 : 4;
        for (var column = 0; column < metricColumns; column++)
        {
            SummaryCards.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        var metricCards = new[] { NearbyMetricCard, BookingsMetricCard, MapMetricCard, NotificationsMetricCard };
        for (var row = 0; row < (metricCards.Length + metricColumns - 1) / metricColumns; row++)
        {
            SummaryCards.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        for (var index = 0; index < metricCards.Length; index++)
        {
            Grid.SetRow(metricCards[index], index / metricColumns);
            Grid.SetColumn(metricCards[index], index % metricColumns);
        }
    }

    private void SearchEntry_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderParkingLocations();
    }

    private async void QuickBookButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new NavigationPage(new BookingPage()));
    }

    private async void BookingLocationButton_Clicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is null)
        {
            await DisplayAlertAsync("Location unavailable", "Refresh the parking list and try again.", "OK");
            return;
        }

        var locationId = button.CommandParameter switch
        {
            string id => id,
            AdminSlot slot => slot.LocationId,
            _ => string.Empty
        };
        var slotId = button.CommandParameter is AdminSlot selectedSlot ? selectedSlot.Id : null;
        if (string.IsNullOrWhiteSpace(locationId) ||
            !_locations.Any(location => location.Id == locationId))
        {
            await DisplayAlertAsync("Location unavailable", "Refresh the parking list and try again.", "OK");
            return;
        }

        await Navigation.PushModalAsync(new NavigationPage(new BookingPage(locationId, slotId)));
    }

    private async void LocationDirectionsButton_Clicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string locationId })
        {
            return;
        }

        var location = _locations.FirstOrDefault(item => item.Id == locationId);
        if (location is null)
        {
            await DisplayAlertAsync("Location unavailable", "Refresh the parking list and try again.", "OK");
            return;
        }

        await OpenInAppNavigationAsync(location);
    }

    private async void SearchButton_Clicked(object sender, EventArgs e)
    {
        RenderParkingLocations();
        await DashboardScrollView.ScrollToAsync(ParkingSection, ScrollToPosition.Start, animated: true);
        if (ParkingListings.Children.Count == 1 &&
            ParkingListings.Children[0] is Label { Text: "No locations match this search and filter." })
        {
            await DisplayAlertAsync("No matching locations", "Try a different location name or parking availability filter.", "OK");
        }
    }

    private async void NearbyMetricCard_Tapped(object? sender, TappedEventArgs e)
    {
        SearchEntry.Text = string.Empty;
        SelectParkingFilter("Available");
        await DashboardScrollView.ScrollToAsync(ParkingSection, ScrollToPosition.Start, animated: true);
    }

    private void CategoryButton_Clicked(object sender, EventArgs e)
    {
        if (sender is not Button selectedButton)
        {
            return;
        }

        SelectParkingFilter(selectedButton.Text);
    }

    private void SelectParkingFilter(string filter)
    {
        _selectedCategory = filter;
        foreach (var child in CategoryList.Children)
        {
            if (child is Button categoryButton)
            {
                var isSelected = string.Equals(categoryButton.Text, filter, StringComparison.Ordinal);
                categoryButton.BackgroundColor = isSelected ? Color.FromArgb("#1769D2") : Color.FromArgb("#F0F4F9");
                categoryButton.TextColor = isSelected ? Colors.White : Color.FromArgb("#40556F");
            }
        }

        RenderParkingLocations();
    }

    private void RenderParkingLocations(bool fitMapRegion = true)
    {
        var query = SearchEntry.Text?.Trim() ?? string.Empty;
        var matchingLocations = _locations
            .Where(location =>
                string.IsNullOrWhiteSpace(query) ||
                location.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                location.Address.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(location =>
            {
                var availableCount = _availableSlots.Count(slot => slot.LocationId == location.Id);
                return _selectedCategory switch
                {
                    "Available" => availableCount > 0,
                    "No open slots" => availableCount == 0,
                    _ => true
                };
            })
            .OrderBy(location => _sortParkingByDistance
                ? GetDistanceMeters(location) ?? double.MaxValue
                : 0d)
            .ThenBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RenderParkingMap(matchingLocations, fitMapRegion);
        RenderParkingSpotPreview(query);
        ParkingListings.Children.Clear();
        if (_locations.Count == 0)
        {
            ParkingListings.Children.Add(new Label
            {
                Text = "No parking locations are currently available. Refresh the list or check again later.",
                FontSize = 11,
                TextColor = Color.FromArgb("#74869C")
            });
            return;
        }

        if (matchingLocations.Count == 0)
        {
            ParkingListings.Children.Add(new Label
            {
                Text = "No locations match this search and filter.",
                FontSize = 11,
                TextColor = Color.FromArgb("#74869C")
            });
            return;
        }

        foreach (var location in matchingLocations)
        {
            AddParkingLocationCard(location);
        }
    }

    private void RenderParkingMap(
        IReadOnlyCollection<AdminLocation> locations,
        bool fitMapRegion = true)
    {
        ParkingMapLoadingIndicator.IsRunning = false;
        ParkingMapLoadingIndicator.IsVisible = false;
#if WINDOWS
        MapPlaceholder.IsVisible = true;
        MapPlaceholderLabel.Text =
            "Map refresh is unavailable on Windows. Use QuickPark on Android to refresh live map pins.";
        ParkingMapStatusLabel.Text =
            "Parking availability refreshed. The Windows map is left unchanged to prevent a map-control crash.";
        ParkingMapStatusLabel.TextColor = Color.FromArgb("#63778E");
        return;
#else
        var mappedLocations = locations
            .Where(location =>
                location.Latitude is >= -90 and <= 90 &&
                location.Longitude is >= -180 and <= 180)
            .ToList();

#if ANDROID
        LiveParkingMap.IsVisible = false;
        FallbackParkingMap.IsVisible = true;
        MapPlaceholder.IsVisible = false;
        ParkingMapStatusLabel.Text = mappedLocations.Count == 0
            ? "Street map powered by OpenStreetMap. No parking locations with valid coordinates match this search yet."
            : "Street map powered by OpenStreetMap. Tap pins to inspect saved parking locations.";
        ParkingMapStatusLabel.TextColor = Color.FromArgb("#63778E");
        _fallbackMapLoaded = false;
        FallbackParkingMap.Source = new HtmlWebViewSource
        {
            BaseUrl = "https://quickpark-offline-download.web.app/",
            Html = BuildFallbackParkingMapHtml(mappedLocations)
        };
        return;
#else
        FallbackParkingMap.IsVisible = false;
        LiveParkingMap.IsVisible = true;
        LiveParkingMap.Pins.Clear();
        LiveParkingMap.IsShowingUser = _currentLocation is not null;
        foreach (var location in mappedLocations)
        {
            var availableCount = _availableSlots.Count(slot => slot.LocationId == location.Id);
            var pin = new Microsoft.Maui.Controls.Maps.Pin
            {
                Label = $"{location.Name} · {availableCount} available",
                Address = $"{(string.IsNullOrWhiteSpace(location.Address) ? "Parking entrance" : location.Address)} · ₱{location.FeePerHour:N2}/hour",
                Type = Microsoft.Maui.Controls.Maps.PinType.Place,
                Location = new Location(location.Latitude!.Value, location.Longitude!.Value)
            };
            pin.MarkerClicked += (_, args) =>
            {
                args.HideInfoWindow = true;
                SelectMapLocation(location);
            };
            LiveParkingMap.Pins.Add(pin);
        }

        if (_selectedMapLocation is not null &&
            !mappedLocations.Any(location => location.Id == _selectedMapLocation.Id))
        {
            _selectedMapLocation = null;
            MapSelectedLocationPanel.IsVisible = false;
        }
        else if (_selectedMapLocation is not null)
        {
            SelectMapLocation(mappedLocations.First(location => location.Id == _selectedMapLocation.Id));
        }

        if (mappedLocations.Count == 0 && _currentLocation is null)
        {
            MapPlaceholder.IsVisible = true;
            MapPlaceholderLabel.Text = _parkingDataLoadError
                ? "Parking data could not be loaded. Check your connection and refresh."
                : locations.Count == 0
                    ? "No parking locations match this search."
                    : "Matching locations need valid entrance coordinates before they can appear on the map.";
            ParkingMapStatusLabel.Text = _parkingDataLoadError
                ? "Map data unavailable. Refresh to try again."
                : "No matching parking pins to show.";
            ParkingMapStatusLabel.TextColor = _parkingDataLoadError
                ? Color.FromArgb("#B42318")
                : Color.FromArgb("#63778E");
            return;
        }

        MapPlaceholder.IsVisible = false;
        ParkingMapStatusLabel.Text = _parkingDataLoadError
            ? "Parking data could not be loaded. Only your current position may be shown."
            : mappedLocations.Count == 0
                ? locations.Count == 0
                    ? "No parking locations match this search and filter."
                    : "Matching parking locations have no valid entrance coordinates."
                : $"{mappedLocations.Count} matching parking area(s) shown. Tap a pin for details and booking.";
        ParkingMapStatusLabel.TextColor = _parkingDataLoadError
            ? Color.FromArgb("#B42318")
            : Color.FromArgb("#63778E");
        if (fitMapRegion && mappedLocations.Count > 0)
        {
            var center = new Location(
                mappedLocations.Average(location => location.Latitude!.Value),
                mappedLocations.Average(location => location.Longitude!.Value));
            var radiusKilometers = mappedLocations.Max(location => Location.CalculateDistance(
                center,
                new Location(location.Latitude!.Value, location.Longitude!.Value),
                DistanceUnits.Kilometers));
            if (_currentLocation is not null)
            {
                radiusKilometers = Math.Max(
                    radiusKilometers,
                    Location.CalculateDistance(center, _currentLocation, DistanceUnits.Kilometers));
            }

            LiveParkingMap.MoveToRegion(MapSpan.FromCenterAndRadius(
                center,
                Distance.FromKilometers(Math.Clamp(radiusKilometers * 1.4, 0.4, 100))));
        }
        else if (fitMapRegion && _currentLocation is not null)
        {
            LiveParkingMap.MoveToRegion(MapSpan.FromCenterAndRadius(
                _currentLocation,
                Distance.FromKilometers(1.5)));
        }
#endif
    #endif
    }

#if ANDROID
        private string BuildFallbackParkingMapHtml(IReadOnlyCollection<AdminLocation> locations)
        {
                var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                var pins = JsonSerializer.Serialize(locations.Select(location => new
                {
                        location.Name,
                        location.Address,
                        Latitude = location.Latitude!.Value,
                        Longitude = location.Longitude!.Value,
                        location.FeePerHour,
                        AvailableSlots = _availableSlots.Count(slot => slot.LocationId == location.Id)
                }), jsonOptions);
                var currentLocation = _currentLocation is null
                        ? "null"
                        : JsonSerializer.Serialize(new
                        {
                                Latitude = _currentLocation.Latitude,
                                Longitude = _currentLocation.Longitude
                        }, jsonOptions);

                return $$"""
                        <!doctype html>
                        <html lang="en">
                        <head>
                            <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
                            <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.css"
                                        onerror="this.onerror=null;this.href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css';"
                                        integrity="sha256-p4NxAoJBhIIN+hmNHrzRCf9tD/miZyoHS5obTRR9BMY=" crossorigin="">
                            <style>
                                html, body, #map { width: 100%; height: 100%; margin: 0; padding: 0; }
                                body, #map { background: #e8edf1; font-family: Arial, sans-serif; }
                                .map-status { position: absolute; z-index: 1000; left: 50%; top: 50%; transform: translate(-50%, -50%);
                                    max-width: 84%; padding: 10px 13px; border-radius: 10px; background: #fff; color: #243d5c;
                                    box-shadow: 0 2px 10px #102b4c30; font-size: 12px; text-align: center; }
                                .pin { background: #1769d2; border: 3px solid #fff; border-radius: 50% 50% 50% 4px;
                                    box-shadow: 0 2px 8px #102b4c70; color: #fff; font: bold 13px Arial; padding-top: 5px;
                                    text-align: center; transform: rotate(-45deg); }
                                .pin span { display: block; transform: rotate(45deg); }
                                .user-pin { width: 20px; height: 20px; border: 3px solid #fff; border-radius: 50%;
                                    background: #1683ff; box-shadow: 0 1px 7px #102b4c88; }
                                .leaflet-control-attribution { font-size: 8px !important; }
                            </style>
                            <script src="https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.js"
                                            onerror="this.onerror=null;this.src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js';"
                                            integrity="sha256-20nQCchB9co0qIjJZRGuk2/Z9VM+kNiyxNV1lvTlZBo=" crossorigin=""></script>
                        </head>
                        <body>
                            <div id="map" aria-label="OpenStreetMap parking locations"></div>
                            <div id="status" class="map-status">Loading street map…</div>
                            <script>
                                const status = document.getElementById('status');
                                const pins = {{pins}};
                                const current = {{currentLocation}};
                                if (typeof L === 'undefined') {
                                    status.textContent = 'Map controls need an internet connection. Saved parking locations remain available below.';
                                } else {
                                    const center = current ? [current.latitude, current.longitude]
                                        : pins.length ? [pins[0].latitude, pins[0].longitude] : [12.8797, 121.7740];
                                    const map = L.map('map', { zoomControl: true }).setView(center, pins.length || current ? 14 : 6);
                                    const tiles = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
                                        maxZoom: 19,
                                        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>'
                                    }).addTo(map);
                                    let loaded = false;
                                    let tileErrors = 0;
                                    let usingBackupTiles = false;
                                    const backupTiles = L.tileLayer('https://tile.openstreetmap.de/{z}/{x}/{y}.png', {
                                        maxZoom: 19,
                                        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>'
                                    });
                                    backupTiles.on('tileload', () => { loaded = true; status.style.display = 'none'; });
                                    backupTiles.on('tileerror', () => {
                                        tileErrors++;
                                        if (usingBackupTiles && tileErrors >= 3 && !loaded) {
                                            status.textContent = 'Street tiles are unavailable. Parking pins are still shown; check your internet connection.';
                                            status.style.display = 'block';
                                        }
                                    });
                                    tiles.on('tileload', () => { loaded = true; status.style.display = 'none'; });
                                    tiles.on('tileerror', () => {
                                        if (loaded || usingBackupTiles) return;
                                        tileErrors++;
                                        if (tileErrors >= 3) {
                                            usingBackupTiles = true;
                                            tileErrors = 0;
                                            status.textContent = 'Trying an alternate street map…';
                                            status.style.display = 'block';
                                            map.removeLayer(tiles);
                                            backupTiles.addTo(map);
                                        }
                                    });
                                    for (const pin of pins) {
                                        const icon = L.divIcon({ className: '', html: '<div class="pin"><span>P</span></div>',
                                            iconSize: [34, 42], iconAnchor: [10, 38] });
                                        L.marker([pin.latitude, pin.longitude], { icon }).bindPopup(
                                            `<strong>${escapeHtml(pin.name)}</strong><br>${escapeHtml(pin.address)}<br>₱${pin.feePerHour.toFixed(2)}/hour · ${pin.availableSlots} available`)
                                            .addTo(map);
                                    }
                                    const userIcon = L.divIcon({ className: '', html: '<div class="user-pin"></div>',
                                        iconSize: [26, 26], iconAnchor: [13, 13] });
                                    let userMarker = null;
                                    if (current) {
                                        userMarker = L.marker([current.latitude, current.longitude],
                                            { icon: userIcon, zIndexOffset: 1000 })
                                            .bindPopup('Your current location').addTo(map);
                                    }
                                    if (pins.length > 1) {
                                        map.fitBounds(L.latLngBounds(pins.map(pin => [pin.latitude, pin.longitude])), { padding: [20, 20] });
                                    }
                                    const refreshMapSize = () => map.invalidateSize({ pan: false });
                                    window.addEventListener('resize', refreshMapSize);
                                    window.addEventListener('load', () => {
                                        setTimeout(refreshMapSize, 100);
                                        setTimeout(refreshMapSize, 500);
                                    });
                                    setTimeout(refreshMapSize, 100);
                                    setTimeout(refreshMapSize, 500);
                                    window.quickParkZoomIn = () => map.zoomIn();
                                    window.quickParkZoomOut = () => map.zoomOut();
                                    window.quickParkUpdateUserLocation = (latitude, longitude) => {
                                        const position = [latitude, longitude];
                                        if (!userMarker) {
                                            userMarker = L.marker(position, { icon: userIcon, zIndexOffset: 1000 })
                                                .bindPopup('Your current location').addTo(map);
                                        } else {
                                            userMarker.setLatLng(position);
                                        }
                                    };
                                    window.quickParkClearUserLocation = () => {
                                        if (userMarker) {
                                            map.removeLayer(userMarker);
                                            userMarker = null;
                                        }
                                    };
                                    window.quickParkRecenter = (latitude, longitude) => {
                                        window.quickParkUpdateUserLocation(latitude, longitude);
                                        map.setView([latitude, longitude], 15);
                                    };
                                    window.quickParkReady = true;
                                }
                                function escapeHtml(value) {
                                    const element = document.createElement('span');
                                    element.textContent = value || '';
                                    return element.innerHTML;
                                }
                            </script>
                        </body>
                        </html>
                        """;
        }
#endif

    private void SelectMapLocation(AdminLocation location)
    {
        _selectedMapLocation = location;
        var availableSlots = _availableSlots.Count(slot => slot.LocationId == location.Id);
        MapSelectedLocationNameLabel.Text = location.Name;
        MapSelectedLocationAddressLabel.Text = string.IsNullOrWhiteSpace(location.Address)
            ? "Address not provided"
            : location.Address;
        MapSelectedLocationDetailsLabel.Text = $"₱{location.FeePerHour:N2}/hour · {availableSlots} available slot(s)";
        MapSelectedLocationBookButton.CommandParameter = location.Id;
        MapSelectedLocationBookButton.IsEnabled = availableSlots > 0;
        MapSelectedLocationBookButton.Text = availableSlots > 0 ? "Book now" : "Full";
        MapSelectedLocationPanel.IsVisible = true;
    }

    private async void MapSelectedLocationBookButton_Clicked(object? sender, EventArgs e)
    {
        if (_selectedMapLocation is not { } location)
        {
            return;
        }

        if (!_availableSlots.Any(slot => slot.LocationId == location.Id))
        {
            await DisplayAlertAsync(
                "No spaces available",
                "This parking area has no available slots right now. Refresh availability before booking.",
                "OK");
            await LoadParkingOptionsAsync();
            return;
        }

        await Navigation.PushModalAsync(new NavigationPage(new BookingPage(location.Id)));
    }

    private static string BuildParkingMapHtml(string parkingPinsJson, string userLocationJson)
    {
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
              <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.css"
                    onerror="this.onerror=null;this.href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css';"
                    integrity="sha256-p4NxAoJBhIIN+hmNHrzRCf9tD/miZyoHS5obTRR9BMY=" crossorigin="">
              <style>
                html, body, #map { height: 100%; width: 100%; margin: 0; padding: 0; }
                #map { background: #eaf0f6; }
                #map-status { position: absolute; z-index: 1000; left: 50%; top: 12px; transform: translateX(-50%);
                  max-width: 88%; padding: 9px 13px; border: 1px solid #dbe5f0; border-radius: 12px;
                  background: rgba(255,255,255,.96); color: #344f6e; box-shadow: 0 3px 14px #21374b24;
                  font: 12px Arial, sans-serif; text-align: center; }
                .leaflet-popup-content-wrapper { border-radius: 14px; box-shadow: 0 5px 20px #21374b30; }
                .leaflet-popup-content { margin: 13px 15px; font: 13px/1.5 Arial, sans-serif; color: #344f6e; }
                .leaflet-popup-content strong { display: block; margin-bottom: 4px; color: #172e4e; font-size: 15px; }
                .leaflet-control-attribution { padding: 2px 5px !important; border-radius: 7px 0 0 0;
                  background: rgba(255,255,255,.84); color: #526579; font-size: 9px !important; }
                .leaflet-control-attribution a { color: #365b83; }
                .leaflet-control-zoom a { color: #294967 !important; }
                .parking-pin { width: 24px; height: 24px; border: 3px solid white; border-radius: 50%;
                  background: #1769d2; box-shadow: 0 2px 8px #172e4e70; }
                .user-pin { width: 16px; height: 16px; border: 3px solid white; border-radius: 50%;
                  background: #20a464; box-shadow: 0 2px 8px #172e4e70; }
                .map-action { margin: 7px 5px 0 0; padding: 8px 11px; border: 0; border-radius: 9px;
                  color: white; background: #1769d2; font-weight: bold; }
                .map-action:disabled { background: #8797aa; }
              </style>
              <script src="https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.js"
                      onerror="this.onerror=null;this.src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js';"
                      integrity="sha256-20nQCchB9co0qIjJZRGuk2/Z9VM+kNiyxNV1lvTlZBo=" crossorigin=""></script>
            </head>
            <body>
              <div id="map" aria-label="Map showing QuickPark parking locations"></div>
              <div id="map-status" role="status" aria-live="polite">Loading map…</div>
              <script>
                const mapStatus = document.getElementById('map-status');
                if (typeof L === 'undefined') {
                  mapStatus.textContent = 'Map library could not load. Check your internet connection.';
                } else {
                const parkingPins = {{parkingPinsJson}};
                const userLocation = {{userLocationJson}};
                const map = L.map('map', { zoomControl: false, attributionControl: true });
                L.control.zoom({ position: 'topright' }).addTo(map);
                const tileLayer = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
                  maxZoom: 19,
                  attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>'
                }).addTo(map);
                let firstTileLoaded = false;
                tileLayer.on('tileload', () => {
                  firstTileLoaded = true;
                  mapStatus.style.display = 'none';
                });
                tileLayer.on('tileerror', () => {
                  if (!firstTileLoaded) {
                    mapStatus.textContent = 'Map tiles could not load. Check your internet connection.';
                    mapStatus.style.display = 'block';
                  }
                });
                const bounds = [];
                const parkingIcon = L.divIcon({ className: '', html: '<div class="parking-pin"></div>',
                  iconSize: [30, 30], iconAnchor: [15, 15] });
                const userIcon = L.divIcon({ className: '', html: '<div class="user-pin"></div>',
                  iconSize: [22, 22], iconAnchor: [11, 11] });
                const goToQuickPark = (action, id) => {
                  window.location.href = 'quickpark://' + action +
                    (id ? '?locationId=' + encodeURIComponent(id) : '');
                };
                for (const pin of parkingPins) {
                  if (!Number.isFinite(pin.latitude) || !Number.isFinite(pin.longitude) ||
                      pin.latitude < -90 || pin.latitude > 90 ||
                      pin.longitude < -180 || pin.longitude > 180) {
                    continue;
                  }
                  const marker = L.marker([pin.latitude, pin.longitude], { icon: parkingIcon }).addTo(map);
                  const popup = document.createElement('div');
                  popup.className = 'quickpark-popup';
                  const title = document.createElement('strong');
                  title.textContent = pin.name;
                  popup.appendChild(title);
                  if (pin.address) {
                    const address = document.createElement('div');
                    address.textContent = pin.address;
                    popup.appendChild(address);
                  }
                  const details = document.createElement('div');
                  details.textContent = '₱' + Number(pin.hourlyRate).toFixed(2) +
                    ' / hour · ' + pin.availableSlots + ' available spaces';
                  popup.appendChild(details);
                  const selectButton = document.createElement('button');
                  selectButton.className = 'map-action';
                  selectButton.textContent = 'Show in search';
                  selectButton.addEventListener('click', () => goToQuickPark('select', pin.id));
                  popup.appendChild(selectButton);
                  const bookButton = document.createElement('button');
                  bookButton.className = 'map-action';
                  bookButton.textContent = 'Book now';
                  bookButton.disabled = pin.availableSlots < 1;
                  bookButton.addEventListener('click', () => goToQuickPark('book', pin.id));
                  popup.appendChild(bookButton);
                  marker.bindPopup(popup);
                  bounds.push([pin.latitude, pin.longitude]);
                }
                if (userLocation) {
                  L.marker([userLocation.latitude, userLocation.longitude], { icon: userIcon })
                    .bindPopup('Your current location')
                    .addTo(map);
                  bounds.push([userLocation.latitude, userLocation.longitude]);
                }
                const locateControl = L.control({ position: 'topleft' });
                locateControl.onAdd = () => {
                  const container = L.DomUtil.create('div', 'leaflet-bar');
                  const button = L.DomUtil.create('a', '', container);
                  button.href = '#';
                  button.title = 'Center on my location';
                  button.textContent = '◎';
                  button.style.fontSize = '20px';
                  button.style.textAlign = 'center';
                  L.DomEvent.disableClickPropagation(container);
                  L.DomEvent.on(button, 'click', (event) => {
                    L.DomEvent.preventDefault(event);
                    if (userLocation) {
                      map.setView([userLocation.latitude, userLocation.longitude], 15);
                    } else {
                      goToQuickPark('locate');
                    }
                  });
                  return container;
                };
                locateControl.addTo(map);
                if (bounds.length === 0) {
                  map.setView([12.8797, 121.7740], 6);
                  mapStatus.textContent = 'No parking pins yet. An administrator must save parking entrance coordinates.';
                  mapStatus.style.display = 'block';
                } else if (bounds.length === 1) {
                  map.setView(bounds[0], 15);
                } else {
                  map.fitBounds(bounds, { padding: [24, 24], maxZoom: 15 });
                }
                window.setTimeout(() => map.invalidateSize(false), 250);
                window.setTimeout(() => map.invalidateSize(false), 900);
                window.setTimeout(() => {
                  if (!firstTileLoaded && mapStatus.textContent === 'Loading map…') {
                    mapStatus.textContent = 'Map is taking longer than expected. Check your internet connection.';
                  }
                }, 12000);
                }
              </script>
            </body>
            </html>
            """;
    }

    private double? GetDistanceMeters(AdminLocation location)
    {
        if (_currentLocation is null ||
            location.Latitude is not { } latitude ||
            location.Longitude is not { } longitude ||
            latitude is < -90 or > 90 ||
            longitude is < -180 or > 180)
        {
            return null;
        }

        return _currentLocation.CalculateDistance(
            new Location(latitude, longitude),
            DistanceUnits.Kilometers) * 1000;
    }

    private async void FindNearbyParkingButton_Clicked(object? sender, EventArgs e)
    {
        FindNearbyParkingButton.IsEnabled = false;
        FindNearbyParkingButton.Text = "Finding…";
        try
        {
            await LoadMapCurrentLocationAsync(requestPermission: true);
            if (_currentLocation is null ||
                DateTime.UtcNow - _lastGpsFixAt > TimeSpan.FromSeconds(45))
            {
                await DisplayAlertAsync(
                    "Location unavailable",
                    "QuickPark could not get a recent GPS fix. Turn on device location and try again.",
                    "OK");
                return;
            }

            _sortParkingByDistance = true;
            FindNearbyParkingButton.Text = "Nearest first";
            RenderParkingLocations();
        }
        catch (FeatureNotEnabledException)
        {
            await DisplayAlertAsync("Location is off", "Turn on device location and try again.", "OK");
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlertAsync("Location unavailable", "This device does not support location services.", "OK");
        }
        catch (PermissionException exception)
        {
            await DisplayAlertAsync("Location permission error", exception.Message, "OK");
        }
        catch (TimeoutException)
        {
            await DisplayAlertAsync("Location timed out", "QuickPark could not get a GPS fix. Try again outdoors.", "OK");
        }
        finally
        {
            FindNearbyParkingButton.IsEnabled = true;
            if (!_sortParkingByDistance)
            {
                FindNearbyParkingButton.Text = "Near me";
            }
        }
    }

    private void AddParkingLocationCard(AdminLocation location)
    {
        var availableCount = _availableSlots.Count(slot => slot.LocationId == location.Id);
        var card = new Border
        {
            BackgroundColor = Color.FromArgb("#F7F9FC"),
            Stroke = Color.FromArgb("#E4EAF2"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(15) },
            Padding = new Thickness(12)
        };
        var content = new VerticalStackLayout { Spacing = 7 };
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
            Text = location.Name,
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#172E4E"),
            VerticalOptions = LayoutOptions.Center
        });
        var directionsButton = new Button
        {
            Text = "Directions",
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#1769D2"),
            HeightRequest = 32,
            CornerRadius = 10,
            Padding = new Thickness(9, 0),
            CommandParameter = location.Id
        };
        directionsButton.Clicked += LocationDirectionsButton_Clicked;
        heading.Add(directionsButton, 1);
        content.Children.Add(heading);

        if (!string.IsNullOrWhiteSpace(location.Address))
        {
            content.Children.Add(new Label
            {
                Text = location.Address,
                FontSize = 10,
                TextColor = Color.FromArgb("#74869C")
            });
        }

        if (GetDistanceMeters(location) is { } distanceMeters)
        {
            content.Children.Add(new Label
            {
                Text = distanceMeters < 1000
                    ? $"{distanceMeters:0} m away"
                    : $"{distanceMeters / 1000:0.0} km away",
                FontSize = 9,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#1769D2")
            });
        }

        var availability = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8
        };
        availability.Add(new Label
        {
            Text = availableCount == 0 ? "No available slots" : $"{availableCount} slots available",
            FontSize = 10,
            FontAttributes = FontAttributes.Bold,
            TextColor = availableCount == 0
                ? Color.FromArgb("#B42318")
                : Color.FromArgb("#23834B"),
            VerticalOptions = LayoutOptions.Center
        });
        availability.Add(new Label
        {
            Text = $"₱{location.FeePerHour:N2} / hour",
            FontSize = 10,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#344F6E"),
            VerticalOptions = LayoutOptions.Center
        }, 1);
        content.Children.Add(availability);

        if (!string.IsNullOrWhiteSpace(location.OperatingHours))
        {
            content.Children.Add(new Label
            {
                Text = $"Hours: {location.OperatingHours}",
                FontSize = 9,
                TextColor = Color.FromArgb("#74869C")
            });
        }

        var bookButton = new Button
        {
            Text = availableCount == 0 ? "No slots available" : "Book at this location",
            FontSize = 10,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#1769D2"),
            HeightRequest = 38,
            CornerRadius = 11,
            IsEnabled = availableCount > 0,
            CommandParameter = location.Id
        };
        bookButton.Clicked += BookingLocationButton_Clicked;
        content.Children.Add(bookButton);
        card.Content = content;
        ParkingListings.Children.Add(card);
    }

    private async void RefreshDashboardButton_Clicked(object sender, EventArgs e)
    {
        if (_isRefreshingDashboard || sender is not Button refreshButton)
        {
            return;
        }

        refreshButton.IsEnabled = false;
        try
        {
            await RefreshDashboardAsync();
        }
        finally
        {
            refreshButton.IsEnabled = true;
        }
    }

    private async void NotificationsButton_Clicked(object sender, EventArgs e)
    {
        UpdateNavigationSelection("Notifications");
        await Navigation.PushModalAsync(new NavigationPage(new NotificationsPage()));
    }

    private async void MapNavigateButton_Clicked(object sender, EventArgs e)
    {
        await DashboardScrollView.ScrollToAsync(ParkingSection, ScrollToPosition.Start, animated: true);
        await OpenInAppNavigationAsync();
    }

    private async Task OpenInAppNavigationAsync(AdminLocation? expectedLocation = null)
    {
        var reservation = BookingReservationStore.Current;
        if (reservation is null)
        {
            await DisplayAlertAsync(
                "Booking required",
                "Book a parking space first. QuickPark navigation is tied to the exact parking location on your confirmed booking.",
                "OK");
            return;
        }
        if (expectedLocation is not null && expectedLocation.Id != reservation.LocationId)
        {
            await DisplayAlertAsync(
                "Different parking location",
                "Navigation can start only for the parking area linked to your current booking.",
                "OK");
            return;
        }
        if (reservation.Status != "Confirmed")
        {
            await DisplayAlertAsync(
                "Booking not confirmed",
                "Navigation is available only after the parking administrator confirms your booking.",
                "OK");
            return;
        }

        try
        {
            var destination = await _bookingApi.GetNavigationLocationAsync(reservation.ReservationId);
            if (destination.Id != reservation.LocationId)
            {
                throw new InvalidOperationException("The verified parking location does not match this booking.");
            }

            await Navigation.PushAsync(new MapNavigationPage(reservation.ReservationId, destination));
        }
        catch (InvalidOperationException exception)
        {
            await DisplayAlertAsync(
                "Could not start navigation",
                exception.Message,
                "OK");
        }
        catch (FirebaseAuthException exception)
        {
            await DisplayAlertAsync(
                "Sign-in required",
                exception.Message,
                "OK");
        }
        catch (HttpRequestException)
        {
            await DisplayAlertAsync("Connection error", "Could not verify your booking and parking entrance. Check your connection and try again.", "OK");
        }
        catch (TaskCanceledException)
        {
            await DisplayAlertAsync("Request timed out", "Verifying the booking timed out. Please try again.", "OK");
        }
    }

    private async void HomeTab_Clicked(object sender, EventArgs e)
    {
        await DashboardScrollView.ScrollToAsync(WelcomeBanner, ScrollToPosition.Start, animated: true);
        UpdateNavigationSelection("Home");
    }

    private async void BookingsTab_Clicked(object? sender, EventArgs e)
    {
        UpdateNavigationSelection("Bookings");
        await Navigation.PushModalAsync(new NavigationPage(new MyBookingPage()));
    }

    private async void ProfileTab_Clicked(object? sender, EventArgs e)
    {
        UpdateNavigationSelection("Profile");
        await Navigation.PushModalAsync(
            new NavigationPage(new ProfilePage(AuthSession.CurrentUser?.Email ?? _email)));
    }

    private async void CustomerServiceButton_Clicked(object sender, EventArgs e)
    {
        await CustomerSupportService.ContactAsync(this, _email);
    }

    private async void ScrollToParkingButton_Clicked(object? sender, EventArgs e)
    {
        await DashboardScrollView.ScrollToAsync(ParkingSection, ScrollToPosition.Start, animated: true);
    }

    private async void DashboardNavigationButton_Clicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not string section)
        {
            return;
        }

        switch (section)
        {
            case "Home":
                UpdateNavigationSelection(section);
                await DashboardScrollView.ScrollToAsync(WelcomeBanner, ScrollToPosition.Start, animated: true);
                break;
            case "Bookings":
                UpdateNavigationSelection(section);
                await Navigation.PushModalAsync(new NavigationPage(new MyBookingPage()));
                break;
            case "History":
                UpdateNavigationSelection(section);
                await Navigation.PushModalAsync(new NavigationPage(new ReservationHistoryPage()));
                break;
            case "Parking":
                UpdateNavigationSelection("Home");
                await DashboardScrollView.ScrollToAsync(ParkingSection, ScrollToPosition.Start, animated: true);
                break;
            case "Profile":
            case "Settings":
                UpdateNavigationSelection("Profile");
                await Navigation.PushModalAsync(
                    new NavigationPage(new ProfilePage(AuthSession.CurrentUser?.Email ?? string.Empty)));
                break;
            case "Notifications":
                UpdateNavigationSelection(section);
                await Navigation.PushModalAsync(new NavigationPage(new NotificationsPage()));
                break;
            default:
                throw new InvalidOperationException($"Unknown dashboard section '{section}'.");
        }
    }

    private async void LogoutButton_Clicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync("Log out", "Are you sure you want to log out of QuickPark?", "OK", "Cancel");
        if (!confirmed)
        {
            return;
        }

        AuthSession.SignOut();
        Application.Current?.Windows.FirstOrDefault()?.Page = new MainPage();
    }

    private void UpdateNavigationSelection(string selectedSection)
    {
        foreach (var button in SidebarNavigation.Children.OfType<Button>()
                     .Concat(CompactNavigation.Content is HorizontalStackLayout compactButtons
                         ? compactButtons.Children.OfType<Button>()
                         : Enumerable.Empty<Button>())
                     .Concat(CompactBottomNavigation.Children.OfType<Button>()))
        {
            var isSelected = string.Equals(button.CommandParameter as string, selectedSection, StringComparison.Ordinal);
            var isCompactTopButton = button.Parent is HorizontalStackLayout;
            var isBottomButton = button.Parent == CompactBottomNavigation;
            button.BackgroundColor = isSelected
                ? isBottomButton || isCompactTopButton
                    ? Color.FromArgb("#EAF2FF")
                    : Color.FromArgb("#1469E8")
                : isCompactTopButton
                    ? Colors.White
                    : Colors.Transparent;
            button.TextColor = isSelected
                ? isBottomButton || isCompactTopButton
                    ? Color.FromArgb("#1769D2")
                    : Colors.White
                : isCompactTopButton
                    ? Color.FromArgb("#365372")
                    : isBottomButton
                        ? Color.FromArgb("#526579")
                        : Color.FromArgb("#CED9E9");
        }
    }
}
