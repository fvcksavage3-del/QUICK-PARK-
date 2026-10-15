using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Media;
using System.Text.Json;

namespace QuickPark;

internal sealed class MapNavigationPage : ContentPage
{
    private static readonly Color Navy = Color.FromArgb("#0B1D38");
    private static readonly Color Blue = Color.FromArgb("#1769D2");
    private static readonly TimeSpan LocationInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RerouteInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BookingStatusCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumGpsFixAge = TimeSpan.FromSeconds(15);
    private const double MaximumRouteAccuracyMeters = 25;
    private readonly AdminLocation _destination;
    private readonly string _reservationId;
    private readonly IReadOnlyList<AdminLocation> _parkingLocations;
    private readonly NavigationRouteService _routeService = new();
    private readonly BookingApiClient _bookingApi = new();
    private readonly WebView _map;
    private readonly Label _instructionLabel;
    private readonly Label _turnDistanceLabel;
    private readonly Label _statusLabel;
    private readonly Label _distanceLabel;
    private readonly Label _travelTimeLabel;
    private readonly Label _etaLabel;
    private readonly Label _destinationNameLabel;
    private readonly Border _instructionCard;
    private readonly Button _startButton;
    private readonly Button _refreshRouteButton;
    private readonly Button _endButton;
    private readonly Button _recenterButton;
    private readonly Grid _bottomNavigation;
    private readonly CancellationTokenSource _lifetime = new();
    private NavigationRoute? _route;
    private Location? _lastLocation;
    private Location? _lastMapCenter;
    private DateTime _lastRouteAt = DateTime.MinValue;
    private DateTime _lastRouteAttemptAt = DateTime.MinValue;
    private DateTime _lastBookingStatusCheckAt = DateTime.MinValue;
    private int _currentStepIndex;
    private int _offRouteReadings;
    private bool _isFollowingUser;
    private bool _isStarting;
    private bool _isNavigationActive;
    private bool _mapLoaded;
    private bool _voiceGuidanceEnabled = true;
    private bool _hasAnnouncedInitialGuidance;
    private int _lastAnnouncedStepIndex = -1;
    private int _lastAnnouncedApproachStage = -1;
    private readonly SemaphoreSlim _speechLock = new(1, 1);
    private CancellationTokenSource _speechCancellation = new();
    private readonly Button _voiceGuidanceButton;

    public MapNavigationPage(
        string reservationId,
        AdminLocation destination,
        IReadOnlyList<AdminLocation>? parkingLocations = null)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
        {
            throw new ArgumentException("A confirmed booking is required for navigation.", nameof(reservationId));
        }
        if (destination.Latitude is not { } latitude ||
            destination.Longitude is not { } longitude ||
            !double.IsFinite(latitude) ||
            !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 ||
            longitude is < -180 or > 180)
        {
            throw new ArgumentException("The parking location needs valid entrance coordinates.", nameof(destination));
        }

        _reservationId = reservationId;
        _destination = destination;
        _parkingLocations = (parkingLocations ?? [destination])
            .Append(destination)
            .DistinctBy(location => location.Id, StringComparer.Ordinal)
            .ToArray();
        Title = "QuickPark navigation";
        BackgroundColor = Colors.White;

        _map = new WebView
        {
            Source = new HtmlWebViewSource
            {
                BaseUrl = "https://quickpark-offline-download.web.app/",
                Html = BuildNavigationMapHtml(latitude, longitude, _destination, _parkingLocations)
            },
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        _map.Navigated += NavigationMap_Navigated;

        _instructionLabel = new Label
        {
            Text = "Ready when you are",
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            LineBreakMode = LineBreakMode.WordWrap,
            MaxLines = 2
        };
        _turnDistanceLabel = new Label
        {
            Text = "Start navigation to request a real driving route",
            FontSize = 12,
            TextColor = Color.FromArgb("#D2E0F4"),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };
        _statusLabel = new Label
        {
            Text = "GPS starts when you begin",
            FontSize = 12,
            TextColor = Color.FromArgb("#86B7FF")
        };
        _distanceLabel = new Label
        {
            Text = "—",
            FontSize = 24,
            FontAttributes = FontAttributes.Bold,
            TextColor = Navy
        };
        _travelTimeLabel = new Label
        {
            Text = "—",
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = Navy
        };
        _etaLabel = new Label
        {
            Text = "ETA is not available until a route is calculated",
            FontSize = 12,
            TextColor = Color.FromArgb("#526782")
        };
        _destinationNameLabel = new Label
        {
            Text = destination.Name,
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            TextColor = Navy,
            LineBreakMode = LineBreakMode.TailTruncation
        };

        _voiceGuidanceButton = new Button
        {
            Text = "🔊",
            FontSize = 18,
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#263F62"),
            WidthRequest = 42,
            HeightRequest = 42,
            CornerRadius = 14,
            Padding = 0,
            AutomationId = "ToggleVoiceGuidance"
        };
        SemanticProperties.SetHint(_voiceGuidanceButton, "Turn off spoken navigation instructions");
        _voiceGuidanceButton.Clicked += VoiceGuidanceButton_Clicked;

        _instructionCard = new Border
        {
            BackgroundColor = Navy,
            Stroke = Color.FromArgb("#263F62"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 22 },
            Padding = new Thickness(17, 15),
            Shadow = new Shadow
            {
                Brush = new SolidColorBrush(Color.FromArgb("#550B1D38")),
                Offset = new Point(0, 7),
                Radius = 18,
                Opacity = 0.3f
            },
            Content = new VerticalStackLayout
            {
                Spacing = 5,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition(GridLength.Auto),
                            new ColumnDefinition(GridLength.Star),
                            new ColumnDefinition(GridLength.Auto)
                        },
                        ColumnSpacing = 11,
                        Children =
                        {
                            new Border
                            {
                                WidthRequest = 42,
                                HeightRequest = 42,
                                BackgroundColor = Color.FromArgb("#263F62"),
                                StrokeThickness = 0,
                                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                                Content = new Label
                                {
                                    Text = "↱",
                                    FontSize = 27,
                                    FontAttributes = FontAttributes.Bold,
                                    TextColor = Color.FromArgb("#73B7FF"),
                                    HorizontalTextAlignment = TextAlignment.Center,
                                    VerticalTextAlignment = TextAlignment.Center
                                }
                            },
                            new VerticalStackLayout
                            {
                                Spacing = 2,
                                VerticalOptions = LayoutOptions.Center,
                                Children =
                                {
                                    new Label
                                    {
                                        Text = "NEXT TURN",
                                        FontSize = 9,
                                        CharacterSpacing = 1.3,
                                        FontAttributes = FontAttributes.Bold,
                                        TextColor = Color.FromArgb("#8BBFFF")
                                    },
                                    _instructionLabel
                                }
                            },
                            _voiceGuidanceButton
                        }
                    },
                    _turnDistanceLabel,
                    new Label
                    {
                        Text = string.IsNullOrWhiteSpace(destination.Address)
                            ? "PARKING DESTINATION"
                            : destination.Address,
                        FontSize = 10,
                        TextColor = Color.FromArgb("#AABAD0"),
                        LineBreakMode = LineBreakMode.TailTruncation
                    },
                    _statusLabel
                }
            }
        };
        Grid.SetColumn(_voiceGuidanceButton, 2);

        _recenterButton = new Button
        {
            Text = "⌖",
            FontSize = 25,
            FontAttributes = FontAttributes.Bold,
            TextColor = Blue,
            BackgroundColor = Colors.White,
            WidthRequest = 54,
            HeightRequest = 54,
            CornerRadius = 27,
            Padding = 0,
            AutomationId = "RecenterNavigationMap"
        };
        SemanticProperties.SetHint(_recenterButton, "Recenter map on your live location");
        _recenterButton.Clicked += RecenterButton_Clicked;
        _recenterButton.Shadow = new Shadow
        {
            Brush = new SolidColorBrush(Color.FromArgb("#400B1D38")),
            Offset = new Point(0, 4),
            Radius = 12,
            Opacity = 0.24f
        };

        _startButton = new Button
        {
            Text = "Start Navigation",
            TextColor = Colors.White,
            BackgroundColor = Blue,
            FontAttributes = FontAttributes.Bold,
            FontSize = 16,
            HeightRequest = 54,
            CornerRadius = 17
        };
        SemanticProperties.SetDescription(_startButton, "Start in-app navigation to the parking entrance");
        _startButton.Clicked += StartNavigationButton_Clicked;
        _refreshRouteButton = new Button
        {
            Text = "Refresh route",
            TextColor = Navy,
            BackgroundColor = Color.FromArgb("#EAF2FC"),
            FontAttributes = FontAttributes.Bold,
            FontSize = 14,
            HeightRequest = 48,
            CornerRadius = 16,
            IsVisible = false
        };
        _refreshRouteButton.Clicked += RefreshRouteButton_Clicked;
        _endButton = new Button
        {
            Text = "End Navigation",
            TextColor = Navy,
            BackgroundColor = Color.FromArgb("#EAF2FC"),
            FontAttributes = FontAttributes.Bold,
            FontSize = 15,
            HeightRequest = 50,
            CornerRadius = 16,
            IsVisible = false
        };
        _endButton.Clicked += EndNavigationButton_Clicked;

        var destinationInfo = new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label
                {
                    Text = "PARKING DESTINATION",
                    FontSize = 9,
                    CharacterSpacing = 1.2,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#7B8DA4")
                },
                _destinationNameLabel,
                _etaLabel
            }
        };
        var destinationIcon = new Border
        {
            WidthRequest = 46,
            HeightRequest = 46,
            BackgroundColor = Color.FromArgb("#EAF3FF"),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 15 },
            Content = new Label
            {
                Text = "P",
                FontSize = 23,
                FontAttributes = FontAttributes.Bold,
                TextColor = Blue,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center
            }
        };
        var destinationRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            Children = { destinationInfo, destinationIcon }
        };
        Grid.SetColumn(destinationIcon, 1);

        var metricsRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 10
        };
        metricsRow.Children.Add(new VerticalStackLayout
        {
            Spacing = 1,
            Children =
            {
                new Label { Text = "ROUTE TIME", FontSize = 9, CharacterSpacing = 1, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#7B8DA4") },
                _travelTimeLabel
            }
        });
        var distanceInfo = new VerticalStackLayout
        {
            Spacing = 1,
            HorizontalOptions = LayoutOptions.End,
            Children =
            {
                new Label { Text = "DISTANCE LEFT", FontSize = 9, CharacterSpacing = 1, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#7B8DA4"), HorizontalTextAlignment = TextAlignment.End },
                _distanceLabel
            }
        };
        Grid.SetColumn(distanceInfo, 1);
        metricsRow.Children.Add(distanceInfo);

        _bottomNavigation = CreateBottomNavigation();
        var bottomPanel = new Border
        {
            BackgroundColor = Colors.White,
            Stroke = Color.FromArgb("#E8EDF4"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(26, 26, 0, 0) },
            Padding = new Thickness(19, 17, 19, 5),
            Shadow = new Shadow
            {
                Brush = new SolidColorBrush(Color.FromArgb("#300B1D38")),
                Offset = new Point(0, -5),
                Radius = 18,
                Opacity = 0.22f
            },
            Content = new VerticalStackLayout
            {
                Spacing = 11,
                Children =
                {
                    destinationRow,
                    metricsRow,
                    _startButton,
                    _refreshRouteButton,
                    _endButton,
                    new BoxView { HeightRequest = 1, Color = Color.FromArgb("#EDF1F6"), Margin = new Thickness(0, 1) },
                    _bottomNavigation
                }
            }
        };

        var recenterContainer = new VerticalStackLayout
        {
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 16, 50),
            Children = { _recenterButton }
        };

        Content = new Grid
        {
            Children =
            {
                _map,
                new Grid
                {
                    RowDefinitions =
                    {
                        new RowDefinition(GridLength.Auto),
                        new RowDefinition(GridLength.Star),
                        new RowDefinition(GridLength.Auto)
                    },
                    Children =
                    {
                        _instructionCard,
                        recenterContainer,
                        bottomPanel
                    }
                }
            }
        };
        Grid.SetRow(_instructionCard, 0);
        _instructionCard.Margin = new Thickness(16, 54, 16, 0);
        Grid.SetRow(recenterContainer, 1);
        Grid.SetRow(bottomPanel, 2);
        bottomPanel.VerticalOptions = LayoutOptions.End;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = _instructionCard.FadeToAsync(1, 220, Easing.CubicOut);
    }

    private async void StartNavigationButton_Clicked(object? sender, EventArgs e)
    {
        if (_isStarting || _isNavigationActive)
        {
            return;
        }

        _isStarting = true;
        _startButton.IsEnabled = false;
        _startButton.Text = "Checking location…";
        try
        {
            var permission = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (permission != PermissionStatus.Granted)
            {
                permission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }

            if (permission != PermissionStatus.Granted)
            {
                SetStatus("Allow location access to start navigation.");
                await DisplayAlertAsync(
                    "Location permission required",
                    "QuickPark needs GPS access to calculate a real driving route and update your position.",
                    "OK");
                return;
            }

            _isNavigationActive = true;
            _isFollowingUser = true;
            _recenterButton.Text = "➤";
            SemanticProperties.SetHint(_recenterButton, "Pause automatic map following");
            _startButton.IsVisible = false;
            _refreshRouteButton.IsVisible = true;
            _endButton.IsVisible = true;
            _bottomNavigation.IsVisible = false;
            _instructionLabel.Text = "Finding your location…";
            _turnDistanceLabel.Text = "GPS";
            await _endButton.FadeToAsync(1, 200, Easing.CubicOut);
            await FollowRouteAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (FeatureNotEnabledException)
        {
            SetStatus("Turn on device location to start navigation.");
        }
        catch (FeatureNotSupportedException)
        {
            SetStatus("GPS is not available on this device.");
        }
        catch (PermissionException exception)
        {
            SetStatus($"Location permission error: {exception.Message}");
        }
        catch (HttpRequestException)
        {
            SetStatus("Internet connection unavailable. QuickPark will retry; no route is simulated.");
        }
        catch (FirebaseAuthException exception)
        {
            SetStatus(exception.Message);
            _instructionLabel.Text = "Sign-in required";
        }
        finally
        {
            _isStarting = false;
            if (!_isNavigationActive)
            {
                _startButton.IsEnabled = true;
                _startButton.Text = "Start Navigation";
            }
        }
    }

    private async void RefreshRouteButton_Clicked(object? sender, EventArgs e)
    {
        if (!_isNavigationActive || _lastLocation is null || !_refreshRouteButton.IsEnabled)
        {
            return;
        }

        _refreshRouteButton.IsEnabled = false;
        _refreshRouteButton.Text = "Refreshing…";
        try
        {
            var current = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(15)),
                _lifetime.Token);
            if (current is null || !IsFreshGpsFix(current, out var accuracy))
            {
                SetStatus("No fresh GPS fix is available. QuickPark kept the last confirmed route.");
                return;
            }
            if (accuracy > MaximumRouteAccuracyMeters)
            {
                SetStatus($"GPS accuracy is ±{accuracy:0} m. Move to an open area and wait for ±{MaximumRouteAccuracyMeters:0} m or better.");
                return;
            }

            _lastLocation = current;
            await CalculateRouteAsync(current, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (FeatureNotEnabledException)
        {
            SetStatus("Turn on device location to refresh the route.");
        }
        catch (FeatureNotSupportedException)
        {
            SetStatus("This device does not provide GPS location.");
        }
        catch (PermissionException)
        {
            SetStatus("Location permission was revoked. Allow location access to refresh the route.");
        }
        catch (TimeoutException)
        {
            SetStatus("GPS timed out. Try refreshing the route again when location is available.");
        }
        finally
        {
            _refreshRouteButton.IsEnabled = _isNavigationActive;
            _refreshRouteButton.Text = "Refresh route";
        }
    }

    protected override void OnDisappearing()
    {
        _lifetime.Cancel();
        _speechCancellation.Cancel();
        base.OnDisappearing();
    }

    private async Task FollowRouteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (DateTime.UtcNow - _lastBookingStatusCheckAt >= BookingStatusCheckInterval)
            {
                try
                {
                    await _bookingApi.GetNavigationLocationAsync(_reservationId, cancellationToken);
                    _lastBookingStatusCheckAt = DateTime.UtcNow;
                }
                catch (InvalidOperationException exception)
                {
                    await StopForInactiveBookingAsync(exception.Message);
                    return;
                }
                catch (FirebaseAuthException exception)
                {
                    await StopForInactiveBookingAsync(exception.Message);
                    return;
                }
                catch (HttpRequestException)
                {
                    _lastBookingStatusCheckAt = DateTime.UtcNow;
                    SetStatus("Could not verify the booking status. Navigation will retry when the connection returns.");
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _lastBookingStatusCheckAt = DateTime.UtcNow;
                    SetStatus("Booking status check timed out. Navigation will retry.");
                }
            }

            Location? current;
            try
            {
                current = await Geolocation.Default.GetLocationAsync(
                    new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(15)),
                    cancellationToken);
            }
            catch (FeatureNotEnabledException)
            {
                SetStatus("Turn on device location. QuickPark will resume when GPS is available.");
                await Task.Delay(LocationInterval, cancellationToken);
                continue;
            }
            catch (FeatureNotSupportedException)
            {
                SetStatus("This device does not provide GPS location.");
                await Task.Delay(LocationInterval, cancellationToken);
                continue;
            }
            catch (PermissionException)
            {
                SetStatus("Location permission was revoked. Allow location access to continue.");
                await Task.Delay(LocationInterval, cancellationToken);
                continue;
            }
            catch (TimeoutException)
            {
                SetStatus("Weak GPS signal. Waiting for a more accurate location…");
                await Task.Delay(LocationInterval, cancellationToken);
                continue;
            }

            if (current is null)
            {
                SetStatus("Waiting for a fresh GPS fix. Move to an area with clearer sky.");
                await Task.Delay(LocationInterval, cancellationToken);
                continue;
            }
            if (!IsFreshGpsFix(current, out var accuracy))
            {
                SetStatus("GPS fix is stale or has no accuracy reading. Waiting for a fresh, reliable fix.");
                await Task.Delay(LocationInterval, cancellationToken);
                continue;
            }

            _lastLocation = current;
            if (_mapLoaded && _isFollowingUser)
            {
                var userPanned = await _map.EvaluateJavaScriptAsync("window.quickParkConsumeUserPan()");
                if (string.Equals(userPanned, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(userPanned, "\"true\"", StringComparison.OrdinalIgnoreCase))
                {
                    _isFollowingUser = false;
                    _recenterButton.Text = "◎";
                    SemanticProperties.SetHint(_recenterButton, "Resume automatic map following and center on your location");
                    SetStatus("Map moved manually. Tap the location button to follow your GPS again.");
                }
            }
            if (_mapLoaded && _isFollowingUser &&
                current.Accuracy is not > 100 &&
                (_lastMapCenter is null ||
                 Location.CalculateDistance(
                     _lastMapCenter,
                     current,
                     DistanceUnits.Kilometers) * 1000 >= 35))
            {
                await UpdateMapLocationAsync(current, centerOnUser: true);
                _lastMapCenter = current;
            }
            else if (_mapLoaded)
            {
                await UpdateMapLocationAsync(current, centerOnUser: false);
            }

            if (accuracy > MaximumRouteAccuracyMeters)
            {
                SetStatus($"GPS accuracy is ±{accuracy:0} m. Waiting for ±{MaximumRouteAccuracyMeters:0} m or better before updating your route.");
            }
            else if (_route is not null)
            {
                await UpdateRouteProgressAsync(current);
            }

            if (accuracy <= MaximumRouteAccuracyMeters &&
                ((_route is null && DateTime.UtcNow - _lastRouteAttemptAt >= RerouteInterval) ||
                 ShouldReroute(current)))
            {
                await CalculateRouteAsync(current, cancellationToken);
            }

            await Task.Delay(LocationInterval, cancellationToken);
        }
    }

    private bool ShouldReroute(Location current)
    {
        if (_route is null ||
            DateTime.UtcNow - _lastRouteAt < RerouteInterval ||
            DateTime.UtcNow - _lastRouteAttemptAt < RerouteInterval ||
            current.Accuracy is > MaximumRouteAccuracyMeters)
        {
            return false;
        }

        var nearestDistance = _route.Points.Min(point => Location.CalculateDistance(
            current,
            point,
            DistanceUnits.Kilometers)) * 1000;
        _offRouteReadings = nearestDistance > Math.Max(60, current.Accuracy.GetValueOrDefault(0) * 1.5)
            ? _offRouteReadings + 1
            : 0;
        return _offRouteReadings >= 2;
    }

    private async Task CalculateRouteAsync(Location current, CancellationToken cancellationToken)
    {
        _lastRouteAttemptAt = DateTime.UtcNow;
        SetStatus("Finding a real road route…");
        try
        {
            var route = await _routeService.GetDrivingRouteAsync(
                _reservationId,
                current,
                _destination,
                cancellationToken);
            _route = route;
            _lastRouteAt = DateTime.UtcNow;
            _offRouteReadings = 0;
            _currentStepIndex = 0;
            await RenderRouteAsync(route);
            await UpdateRouteProgressAsync(current);
            if (!string.IsNullOrWhiteSpace(route.TravelAdvisory))
            {
                SetStatus(route.TravelAdvisory);
            }

            else
            {
                SetStatus(route.Steps.Count == 0
                    ? "Driving route loaded; turn-by-turn steps are unavailable."
                    : "Driving route calculated by Google Routes.");
            }
        }
        catch (NavigationRouteException exception)
        {
            SetStatus(exception.Message);
            if (_route is null)
            {
                _instructionLabel.Text = "No route available";
                _distanceLabel.Text = "—";
                _etaLabel.Text = "—";
            }
        }
        catch (HttpRequestException)
        {
            SetStatus(_route is null
                ? "Connection lost. QuickPark will retry; it will not invent a route."
                : "Connection weak. Following the last confirmed route; live rerouting will resume online.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetStatus("Route request timed out. Following the last confirmed route and retrying.");
        }
    }

    private static bool IsFreshGpsFix(Location? location, out double accuracy)
    {
        accuracy = 0;
        if (location is null ||
            location.Accuracy is not { } reportedAccuracy ||
            !double.IsFinite(reportedAccuracy) ||
            reportedAccuracy <= 0 ||
            location.Timestamp > DateTimeOffset.UtcNow.AddSeconds(5) ||
            DateTimeOffset.UtcNow - location.Timestamp > MaximumGpsFixAge)
        {
            return false;
        }

        accuracy = reportedAccuracy;
        return true;
    }

    private async Task RenderRouteAsync(NavigationRoute route)
    {
        if (_mapLoaded)
        {
            await UpdateMapRouteAsync(route);
        }
    }

    private static string BuildNavigationMapHtml(
        double destinationLatitude,
        double destinationLongitude,
        AdminLocation destinationLocation,
        IReadOnlyList<AdminLocation> parkingLocations)
    {
        var webJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var destinationName = JsonSerializer.Serialize(destinationLocation.Name);
        var destinationAddress = JsonSerializer.Serialize(destinationLocation.Address);
        var parkingPins = JsonSerializer.Serialize(parkingLocations
            .Where(location =>
                location.Latitude is { } latitude &&
                location.Longitude is { } longitude &&
                double.IsFinite(latitude) &&
                double.IsFinite(longitude) &&
                latitude is >= -90 and <= 90 &&
                longitude is >= -180 and <= 180)
            .Select(location => new
            {
                location.Name,
                location.Address,
                Latitude = location.Latitude!.Value,
                Longitude = location.Longitude!.Value,
                IsDestination = location.Id == destinationLocation.Id
            }), webJsonOptions);
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
                body { background: #e9edf0; font-family: Arial, sans-serif; }
                #map { background: #e9edf0; }
                #map-status { position: absolute; z-index: 1000; left: 50%; top: 50%;
                  transform: translate(-50%, -50%); width: max-content; max-width: 80%;
                  padding: 11px 15px; border-radius: 12px; background: rgba(255,255,255,.96);
                  color: #243d5c; box-shadow: 0 2px 12px #102b4c28; font-size: 13px; text-align: center; }
                #map-status.error { color: #a32929; }
                .leaflet-control-attribution { max-width: min(82vw, 340px); margin: 0 7px 7px 0 !important;
                  padding: 3px 6px !important; border-radius: 6px; background: rgba(255,255,255,.88) !important;
                  font-size: 8px !important; line-height: 1.2; }
                .leaflet-control-attribution a { color: #425b78 !important; }
                .leaflet-control-zoom { border: 0 !important; box-shadow: 0 2px 12px #102b4c24 !important; }
                .leaflet-control-zoom a { color: #243d5c !important; }
                .qp-destination { display: grid; place-items: center; width: 34px; height: 42px;
                  color: white; background: #1769d2; border: 3px solid white; border-radius: 50% 50% 50% 4px;
                  box-shadow: 0 2px 9px #102b4c65; font-size: 16px; font-weight: bold; transform: rotate(-45deg); }
                .qp-destination span { transform: rotate(45deg); }
                .qp-user { position: relative; width: 22px; height: 22px; border: 3px solid white;
                  border-radius: 50%; background: #1683ff; box-shadow: 0 1px 7px #102b4c88; }
                .qp-user:after { position: absolute; content: ''; inset: -9px; border-radius: 50%;
                  background: #1683ff30; border: 1px solid #1683ff40; }
                .qp-user:before { position: absolute; content: ''; left: 50%; top: -12px; width: 0; height: 0;
                  border-left: 6px solid transparent; border-right: 6px solid transparent;
                  border-bottom: 12px solid #1683ff; transform: translateX(-50%) rotate(var(--bearing, 0deg));
                  transform-origin: 50% 17px; }
                .qp-popup { color: #243d5c; font-size: 12px; line-height: 1.4; }
                .qp-popup strong { display: block; margin-bottom: 3px; color: #102b4c; font-size: 14px; }
              </style>
              <script src="https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.js"
                      onerror="this.onerror=null;this.src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js';"
                      integrity="sha256-20nQCchB9co0qIjJZRGuk2/Z9VM+kNiyxNV1lvTlZBo=" crossorigin=""></script>
            </head>
            <body>
              <div id="map" aria-label="Live street map and driving route"></div>
              <div id="map-status">Loading live street map…</div>
              <script>
                const status = document.getElementById('map-status');
                const destination = [{{destinationLatitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                                     {{destinationLongitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}];
                const destinationName = {{destinationName}};
                const destinationAddress = {{destinationAddress}};
                const parkingPins = {{parkingPins}};
                if (typeof L === 'undefined') {
                  status.textContent = 'Map controls could not load. Check your internet connection.';
                  status.className = 'error';
                } else {
                  const map = L.map('map', { zoomControl: false, attributionControl: false, preferCanvas: true })
                    .setView(destination, 16);
                  L.control.zoom({ position: 'topright' }).addTo(map);
                  L.control.attribution({ position: 'bottomright', prefix: false }).addTo(map);
                  let loadedTiles = 0;
                  let failedTiles = 0;
                  let usingBackupTiles = false;
                  let backupTileErrors = 0;
                  const backupTiles = L.tileLayer('https://tile.openstreetmap.de/{z}/{x}/{y}.png', {
                    maxZoom: 19, minZoom: 3, updateWhenIdle: true, keepBuffer: 2,
                    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>'
                  });
                  backupTiles.on('tileload', () => {
                    loadedTiles++;
                    status.style.display = 'none';
                  });
                  backupTiles.on('tileerror', () => {
                    backupTileErrors++;
                    if (usingBackupTiles && loadedTiles === 0 && backupTileErrors >= 3) {
                      status.textContent = 'Street tiles are unavailable. Check your internet connection and try again.';
                      status.className = 'error';
                      status.style.display = 'block';
                    }
                  });
                  const tiles = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
                    maxZoom: 19, minZoom: 3, updateWhenIdle: true, keepBuffer: 2,
                    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>'
                  }).addTo(map);
                  const refreshMapSize = () => map.invalidateSize({ pan: false });
                  window.addEventListener('resize', refreshMapSize);
                  window.addEventListener('load', () => {
                    setTimeout(refreshMapSize, 100);
                    setTimeout(refreshMapSize, 500);
                  });
                  setTimeout(refreshMapSize, 100);
                  setTimeout(refreshMapSize, 500);
                  tiles.on('tileload', () => {
                    loadedTiles++;
                    status.style.display = 'none';
                  });
                  tiles.on('tileerror', () => {
                    failedTiles++;
                    if (loadedTiles === 0 && failedTiles >= 3 && !usingBackupTiles) {
                      usingBackupTiles = true;
                      failedTiles = 0;
                      status.textContent = 'Trying an alternate street map…';
                      status.className = '';
                      status.style.display = 'block';
                      map.removeLayer(tiles);
                      backupTiles.addTo(map);
                    }
                  });
                  const destinationIcon = L.divIcon({
                    className: '', html: '<div class="qp-destination"><span>P</span></div>',
                    iconSize: [40, 46], iconAnchor: [10, 40]
                  });
                  const userIcon = L.divIcon({
                    className: '', html: '<div class="qp-user"></div>',
                    iconSize: [28, 28], iconAnchor: [14, 14]
                  });
                  const destinationPopup = document.createElement('div');
                  destinationPopup.className = 'qp-popup';
                  const destinationTitle = document.createElement('strong');
                  destinationTitle.textContent = destinationName;
                  destinationPopup.appendChild(destinationTitle);
                  if (destinationAddress) {
                    const address = document.createElement('div');
                    address.textContent = destinationAddress;
                    destinationPopup.appendChild(address);
                  }
                  for (const pin of parkingPins) {
                    const point = [pin.latitude, pin.longitude];
                    if (pin.isDestination) {
                      L.marker(point, { icon: destinationIcon, zIndexOffset: 500 })
                        .bindPopup(destinationPopup).addTo(map);
                    } else {
                      const marker = L.marker(point).addTo(map);
                      const popup = document.createElement('div');
                      popup.className = 'qp-popup';
                      const title = document.createElement('strong');
                      title.textContent = pin.name || 'QuickPark parking';
                      popup.appendChild(title);
                      if (pin.address) {
                        const address = document.createElement('div');
                        address.textContent = pin.address;
                        popup.appendChild(address);
                      }
                      marker.bindPopup(popup);
                    }
                  }
                  const routeOutline = L.polyline([], {
                    color: '#fff', weight: 13, opacity: 0.96, lineCap: 'round', lineJoin: 'round'
                  }).addTo(map);
                  const routeLine = L.polyline([], {
                    color: '#1678f5', weight: 7, opacity: 0.96, lineCap: 'round', lineJoin: 'round'
                  }).addTo(map);
                  let userMarker = null;
                  let userAccuracy = null;
                  let userPanned = false;
                  map.on('dragstart', () => { userPanned = true; });
                  window.quickParkMapReady = true;
                  window.quickParkSetRoute = (points, active, follow) => {
                    routeOutline.setLatLngs(points);
                    routeLine.setLatLngs(points);
                    if (follow && userMarker) {
                      map.setView(userMarker.getLatLng(), Math.max(map.getZoom(), 16), { animate: true });
                      userPanned = false;
                    } else if (!active && points.length > 1 && !userPanned) {
                      map.fitBounds(routeLine.getBounds(), { padding: [55, 45], maxZoom: 16 });
                    }
                  };
                  window.quickParkSetLocation = (latitude, longitude, accuracy, course, follow) => {
                    const point = [latitude, longitude];
                    if (!userMarker) {
                      userMarker = L.marker(point, { icon: userIcon, zIndexOffset: 1000 })
                        .bindPopup('Your current GPS location').addTo(map);
                      userAccuracy = L.circle(point, {
                        radius: Math.max(accuracy || 0, 8), color: '#1683ff', weight: 1,
                        fillColor: '#1683ff', fillOpacity: 0.08, interactive: false
                      }).addTo(map);
                    } else {
                      userMarker.setLatLng(point);
                      userAccuracy.setLatLng(point);
                      userAccuracy.setRadius(Math.max(accuracy || 0, 8));
                    }
                    if (Number.isFinite(course) && course >= 0) {
                      userMarker.getElement()?.style.setProperty('--bearing', `${course}deg`);
                    }
                    if (follow) {
                      map.setView(point, Math.max(map.getZoom(), 16), { animate: true });
                      userPanned = false;
                    }
                  };
                  window.quickParkRecenter = () => {
                    if (userMarker) {
                      map.setView(userMarker.getLatLng(), Math.max(map.getZoom(), 16), { animate: true });
                      userPanned = false;
                    } else {
                      map.setView(destination, 16, { animate: true });
                    }
                  };
                  window.quickParkConsumeUserPan = () => {
                    const wasPanned = userPanned;
                    userPanned = false;
                    return wasPanned;
                  };
                  window.quickParkFitRoute = () => {
                    if (routeLine.getLatLngs().length > 1) {
                      map.fitBounds(routeLine.getBounds(), { padding: [55, 45], maxZoom: 16 });
                    } else {
                      map.setView(destination, 16, { animate: true });
                    }
                    userPanned = false;
                  };
                }
              </script>
            </body>
            </html>
            """;
    }

    private async void NavigationMap_Navigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success)
        {
            SetStatus("Street map failed to load. Check your internet connection.");
            return;
        }

        var mapReady = await _map.EvaluateJavaScriptAsync("window.quickParkMapReady === true");
        if (!string.Equals(mapReady, "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mapReady, "\"true\"", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Street map controls could not start. Check your internet connection.");
            return;
        }

        _mapLoaded = true;
        if (_lastLocation is not null)
        {
            await UpdateMapLocationAsync(_lastLocation, centerOnUser: _isFollowingUser);
        }
        if (_route is not null)
        {
            await UpdateMapRouteAsync(_route);
        }
    }

    private Task UpdateMapRouteAsync(NavigationRoute route)
    {
        var points = JsonSerializer.Serialize(route.Points.Select(point => new[] { point.Latitude, point.Longitude }));
        var active = _isNavigationActive ? "true" : "false";
        var follow = _isNavigationActive && _isFollowingUser ? "true" : "false";
        return _map.EvaluateJavaScriptAsync($"window.quickParkSetRoute({points}, {active}, {follow})");
    }

    private Task UpdateMapLocationAsync(Location location, bool centerOnUser)
    {
        var latitude = location.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var longitude = location.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var accuracy = Math.Max(location.Accuracy.GetValueOrDefault(0), 0)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var follow = centerOnUser ? "true" : "false";
        var course = location.Course is { } heading && double.IsFinite(heading) && heading >= 0
            ? heading.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "null";
        return _map.EvaluateJavaScriptAsync(
            $"window.quickParkSetLocation({latitude}, {longitude}, {accuracy}, {course}, {follow})");
    }

    private async Task UpdateRouteProgressAsync(Location current)
    {
        if (_route is null)
        {
            return;
        }

        var nearestIndex = 0;
        var nearestDistance = double.MaxValue;
        for (var index = 0; index < _route.Points.Count; index++)
        {
            var distance = Location.CalculateDistance(current, _route.Points[index], DistanceUnits.Kilometers) * 1000;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = index;
            }
        }

        var remainingMeters = 0d;
        for (var index = nearestIndex; index < _route.Points.Count - 1; index++)
        {
            remainingMeters += Location.CalculateDistance(
                _route.Points[index],
                _route.Points[index + 1],
                DistanceUnits.Kilometers) * 1000;
        }
        var totalRouteMeters = _route.DistanceMeters;
        if (totalRouteMeters > 0)
        {
            remainingMeters = Math.Min(remainingMeters, totalRouteMeters);
        }
        _distanceLabel.Text = FormatDistance(remainingMeters);
        var remainingDuration = totalRouteMeters > 0
            ? _route.DurationSeconds * Math.Clamp(remainingMeters / totalRouteMeters, 0, 1)
            : 0;
        _travelTimeLabel.Text = FormatDuration(remainingDuration);
        var arrivalTime = DateTime.Now.AddSeconds(remainingDuration);
        var arrivalText = arrivalTime.Date == DateTime.Today
            ? arrivalTime.ToString("h:mm tt")
            : arrivalTime.ToString("MMM d, h:mm tt");
        _etaLabel.Text = $"Arrive around {arrivalText} · Google driving-time estimate";
        if (_route.Steps.Count == 0)
        {
            _instructionLabel.Text = "Turn-by-turn guidance unavailable";
            _turnDistanceLabel.Text = "Follow the route line";
            if (current.Accuracy is { } routeAccuracy && routeAccuracy > 50)
            {
                SetStatus($"Weak GPS signal (±{routeAccuracy:0} m). Route updates may be delayed.");
            }
            return;
        }

        while (_currentStepIndex < _route.Steps.Count - 1 &&
            _route.Steps[_currentStepIndex].EndLocation is { } stepEnd &&
            nearestIndex >= FindNearestRoutePointIndex(stepEnd))
        {
            _currentStepIndex++;
        }

        if (_currentStepIndex != _lastAnnouncedStepIndex)
        {
            _lastAnnouncedStepIndex = _currentStepIndex;
            _lastAnnouncedApproachStage = -1;
        }

        _instructionLabel.Text = _route.Steps[Math.Min(_currentStepIndex, _route.Steps.Count - 1)].Instruction;
        var nextStep = _route.Steps[Math.Min(_currentStepIndex, _route.Steps.Count - 1)];
        var nextTurnMeters = nextStep.EndLocation is { } turnLocation
            ? CalculateDistanceAlongRoute(nearestIndex, FindNearestRoutePointIndex(turnLocation))
            : nextStep.DistanceMeters;
        _turnDistanceLabel.Text = FormatDistance(nextTurnMeters);

        if (_voiceGuidanceEnabled && !_hasAnnouncedInitialGuidance && _currentStepIndex == 0)
        {
            _hasAnnouncedInitialGuidance = true;
            _ = SpeakGuidanceAsync($"Start navigation. {_instructionLabel.Text}");
        }
        else if (_voiceGuidanceEnabled)
        {
            var approachStage = nextTurnMeters <= 75
                ? 2
                : nextTurnMeters <= 300 ? 1 : 0;
            if (approachStage > _lastAnnouncedApproachStage)
            {
                _lastAnnouncedApproachStage = approachStage;
                var prompt = approachStage == 2
                    ? $"Turn soon. {_instructionLabel.Text}"
                    : $"In {FormatDistance(nextTurnMeters)}, {_instructionLabel.Text}";
                _ = SpeakGuidanceAsync(prompt);
            }
        }

        if (current.Accuracy is { } accuracy && accuracy > 50)
        {
            SetStatus($"Weak GPS signal (±{accuracy:0} m). Route updates may be delayed.");
        }
    }

    private void VoiceGuidanceButton_Clicked(object? sender, EventArgs e)
    {
        _voiceGuidanceEnabled = !_voiceGuidanceEnabled;
        _voiceGuidanceButton.Text = _voiceGuidanceEnabled ? "🔊" : "🔇";
        SemanticProperties.SetHint(
            _voiceGuidanceButton,
            _voiceGuidanceEnabled ? "Turn off spoken navigation instructions" : "Turn on spoken navigation instructions");
        if (!_voiceGuidanceEnabled)
        {
            _speechCancellation.Cancel();
            return;
        }

        _speechCancellation = new CancellationTokenSource();
        _lastAnnouncedStepIndex = -1;
        _lastAnnouncedApproachStage = -1;
        SetStatus("Voice guidance is on.");
    }

    private async Task SpeakGuidanceAsync(string instruction)
    {
        if (!_voiceGuidanceEnabled || string.IsNullOrWhiteSpace(instruction))
        {
            return;
        }

        var speechCancellation = _speechCancellation;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            speechCancellation.Token);
        try
        {
            await _speechLock.WaitAsync(linkedCancellation.Token);
            try
            {
                if (_voiceGuidanceEnabled && !_lifetime.IsCancellationRequested)
                {
                    await TextToSpeech.Default.SpeakAsync(instruction, cancelToken: linkedCancellation.Token);
                }
            }
            finally
            {
                _speechLock.Release();
            }
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
        }
        catch (FeatureNotSupportedException exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark voice navigation is unavailable: {exception.Message}");
            SetStatus("Voice guidance is unavailable on this device. Follow the map and turn instructions.");
        }
        catch (InvalidOperationException exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark could not speak navigation guidance: {exception.Message}");
            SetStatus("Voice guidance could not start. Follow the map and turn instructions.");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark voice guidance failed: {exception}");
            SetStatus("Voice guidance failed. Follow the map and turn instructions.");
        }
    }

    private int FindNearestRoutePointIndex(Location location)
    {
        if (_route is null)
        {
            return 0;
        }

        var nearestIndex = 0;
        var nearestDistance = double.MaxValue;
        for (var index = 0; index < _route.Points.Count; index++)
        {
            var distance = Location.CalculateDistance(
                location,
                _route.Points[index],
                DistanceUnits.Kilometers);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = index;
            }
        }

        return nearestIndex;
    }

    private double CalculateDistanceAlongRoute(int startIndex, int endIndex)
    {
        if (_route is null || endIndex <= startIndex)
        {
            return 0;
        }

        var distanceMeters = 0d;
        for (var index = startIndex; index < Math.Min(endIndex, _route.Points.Count - 1); index++)
        {
            distanceMeters += Location.CalculateDistance(
                _route.Points[index],
                _route.Points[index + 1],
                DistanceUnits.Kilometers) * 1000;
        }

        return distanceMeters;
    }

    private void SetStatus(string message)
    {
        MainThread.BeginInvokeOnMainThread(() => _statusLabel.Text = message);
    }

    private static string FormatDistance(double meters)
    {
        return meters >= 1000
            ? $"{meters / 1000:0.0} km"
            : $"{Math.Max(0, meters):0} m";
    }

    private static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1
            ? $"~{(int)duration.TotalHours} hr {duration.Minutes:00} min"
            : $"~{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))} min";
    }

    private async void EndNavigationButton_Clicked(object? sender, EventArgs e)
    {
        _isNavigationActive = false;
        _endButton.IsEnabled = false;
        _refreshRouteButton.IsVisible = false;
        _lifetime.Cancel();
        _speechCancellation.Cancel();
        if (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }
        else if (Navigation.ModalStack.Count > 0)
        {
            await Navigation.PopModalAsync();
        }
    }

    private async Task StopForInactiveBookingAsync(string reason)
    {
        _isNavigationActive = false;
        _refreshRouteButton.IsVisible = false;
        _endButton.IsEnabled = false;
        _bottomNavigation.IsVisible = true;
        _lifetime.Cancel();
        _speechCancellation.Cancel();
        await DisplayAlertAsync(
            "Navigation stopped",
            $"This booking is no longer eligible for navigation. {reason}",
            "OK");
        if (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }
        else if (Navigation.ModalStack.Count > 0)
        {
            await Navigation.PopModalAsync();
        }
    }

    private async void RecenterButton_Clicked(object? sender, EventArgs e)
    {
        if (_isFollowingUser)
        {
            _isFollowingUser = false;
            _recenterButton.Text = "◎";
            SemanticProperties.SetHint(_recenterButton, "Resume automatic map following and center on your location");
            SetStatus("Automatic map following paused. Tap the map button again to resume.");
            return;
        }

        if (_lastLocation is null)
        {
            SetStatus("Waiting for a GPS fix before centering the map.");
            return;
        }

        _isFollowingUser = true;
        _recenterButton.Text = "➤";
        SemanticProperties.SetHint(_recenterButton, "Pause automatic map following");
        if (_mapLoaded)
        {
            await _map.EvaluateJavaScriptAsync("window.quickParkRecenter()");
        }
        _lastMapCenter = _lastLocation;
    }

    private Grid CreateBottomNavigation()
    {
        var navigation = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 2,
            HeightRequest = 43
        };
        var tabs = new[] { "Home", "Map", "Bookings", "Profile" };
        for (var index = 0; index < tabs.Length; index++)
        {
            var tab = tabs[index];
            var button = new Button
            {
                Text = tab,
                FontSize = 10,
                FontAttributes = tab == "Map" ? FontAttributes.Bold : FontAttributes.None,
                TextColor = tab == "Map" ? Blue : Color.FromArgb("#77869A"),
                BackgroundColor = Colors.Transparent,
                Padding = 0,
                CornerRadius = 12,
                AutomationId = $"NavigationTab{tab}"
            };
            button.Clicked += async (_, _) => await NavigateFromBottomBarAsync(tab);
            Grid.SetColumn(button, index);
            navigation.Children.Add(button);
        }

        return navigation;
    }

    private async Task NavigateFromBottomBarAsync(string tab)
    {
        if (tab == "Map")
        {
            RecenterButton_Clicked(this, EventArgs.Empty);
            return;
        }

        var rootPage = Application.Current?.Windows.FirstOrDefault()?.Page;
        await Navigation.PopModalAsync();
        if (tab == "Home" || rootPage is null)
        {
            return;
        }

        Page destinationPage = tab switch
        {
            "Bookings" => new MyBookingPage(),
            "Profile" => new ProfilePage(AuthSession.CurrentUser?.Email ?? string.Empty),
            _ => throw new InvalidOperationException($"Unsupported navigation tab '{tab}'.")
        };
        await rootPage.Navigation.PushModalAsync(new NavigationPage(destinationPage));
    }
}
