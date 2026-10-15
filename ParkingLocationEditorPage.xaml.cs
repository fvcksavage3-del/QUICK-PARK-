using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Maps;

namespace QuickPark;

public partial class ParkingLocationEditorPage : ContentPage
{
    private readonly AdminApiClient _apiClient = new();
    private readonly AdminLocation? _location;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _saved;
    private bool _isSaving;
    private bool _mapReady;
    private int _addressLookupVersion;

    internal ParkingLocationEditorPage(AdminLocation? location)
    {
        InitializeComponent();
        _location = location;
        StatusPicker.SelectedIndex = 0;
        var hasValidSavedCoordinates =
            location is not null &&
            location.Latitude is >= -90 and <= 90 &&
            location.Longitude is >= -180 and <= 180;
        var initialLatitude = hasValidSavedCoordinates ? location!.Latitude!.Value : 12.8797;
        var initialLongitude = hasValidSavedCoordinates ? location!.Longitude!.Value : 121.7740;
        if (location is not null)
        {
            PageTitleLabel.Text = "Edit parking location";
            SaveButton.Text = "Save changes";
            NameEntry.Text = location.Name;
            AddressEntry.Text = location.Address;
            HoursEntry.Text = location.OperatingHours;
            FeeEntry.Text = location.FeePerHour.ToString(CultureInfo.InvariantCulture);
            CapacityEntry.Text = Math.Max(location.TotalCapacity, 1).ToString(CultureInfo.InvariantCulture);
            StatusPicker.SelectedIndex = location.IsActive ? 0 : 1;
        }

        LocationPickerMap.Source = new HtmlWebViewSource
        {
            BaseUrl = "https://quickpark-offline-download.web.app/",
            Html = BuildLocationPickerHtml(
                initialLatitude,
                initialLongitude,
                hasValidSavedCoordinates)
        };
        if (hasValidSavedCoordinates)
        {
            SetCoordinateEntries(initialLatitude, initialLongitude);
        }
    }

    public Task<bool> Completion => _completion.Task;

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _completion.TrySetResult(_saved);
    }

    private async void BackButton_Clicked(object? sender, EventArgs e)
    {
        if (_isSaving)
        {
            return;
        }
        await Navigation.PopModalAsync();
    }

    private async void LocationPickerMap_Navigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "quickpark", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        if (!string.Equals(uri.Host, "select", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
        if (!query.TryGetValue("latitude", out var latitudeText) ||
            !query.TryGetValue("longitude", out var longitudeText) ||
            !TryParseCoordinate(latitudeText, out var latitude) ||
            !TryParseCoordinate(longitudeText, out var longitude) ||
            latitude is < -90 or > 90 ||
            longitude is < -180 or > 180)
        {
            ShowError("The map returned invalid coordinates. Tap or drag the marker again.");
            return;
        }

        SetCoordinateEntries(latitude, longitude);
        AddressEntry.Text = string.Empty;
        AddressLookupLabel.Text = "Looking up the street address…";
        await UpdateAddressFromCoordinatesAsync(latitude, longitude, ++_addressLookupVersion);
        StatusLabel.IsVisible = false;
    }

    private async Task UpdateAddressFromCoordinatesAsync(double latitude, double longitude, int lookupVersion)
    {
        try
        {
            var placemarks = await Geocoding.Default.GetPlacemarksAsync(latitude, longitude);
            if (lookupVersion != _addressLookupVersion)
            {
                return;
            }

            var placemark = placemarks?.FirstOrDefault();
            var street = string.Join(
                " ",
                new[] { placemark?.SubThoroughfare, placemark?.Thoroughfare }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            var addressParts = new[]
                {
                    street,
                    placemark?.SubLocality,
                    placemark?.Locality,
                    placemark?.SubAdminArea,
                    placemark?.AdminArea,
                    placemark?.PostalCode,
                    placemark?.CountryName
                }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (addressParts.Length == 0)
            {
                AddressLookupLabel.Text = "Street address not found. Enter it manually.";
                return;
            }

            AddressEntry.Text = string.Join(", ", addressParts);
            AddressLookupLabel.Text = "Street address added from the map. You can edit it if needed.";
        }
        catch (FeatureNotSupportedException)
        {
            if (lookupVersion == _addressLookupVersion)
            {
                AddressLookupLabel.Text = "Address lookup is unavailable on this device. Enter it manually.";
            }
        }
        catch (InvalidOperationException)
        {
            if (lookupVersion == _addressLookupVersion)
            {
                AddressLookupLabel.Text = "Could not find the street address. Enter it manually.";
            }
        }
        catch (HttpRequestException)
        {
            if (lookupVersion == _addressLookupVersion)
            {
                AddressLookupLabel.Text = "Address lookup needs internet. Enter the street address manually.";
            }
        }
        catch (TaskCanceledException)
        {
            if (lookupVersion == _addressLookupVersion)
            {
                AddressLookupLabel.Text = "Address lookup timed out. Enter the street address manually.";
            }
        }
    }

    private async void LocationPickerMap_Navigated(object? sender, WebNavigatedEventArgs e)
    {
        _mapReady = e.Result == WebNavigationResult.Success;
        if (!_mapReady || !TryReadCoordinates(out var latitude, out var longitude))
        {
            return;
        }

        try
        {
            await UpdateMapSelectionAsync(latitude, longitude);
        }
        catch (InvalidOperationException exception)
        {
            _mapReady = false;
            ShowError($"The map could not be initialized: {exception.Message} You can still enter coordinates manually.");
        }
    }

    private static string BuildLocationPickerHtml(
        double latitude,
        double longitude,
        bool hasSavedCoordinates)
    {
        var initialMarkerJson = hasSavedCoordinates
            ? JsonSerializer.Serialize(new { latitude, longitude })
            : "null";
        var centerJson = JsonSerializer.Serialize(new { latitude, longitude });
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
                .leaflet-control-attribution { padding: 2px 5px !important; border-radius: 7px 0 0 0;
                  background: rgba(255,255,255,.84); color: #526579; font-size: 9px !important; }
                .leaflet-control-attribution a { color: #365b83; }
                .leaflet-control-zoom a { color: #294967 !important; }
                .leaflet-marker-icon { filter: drop-shadow(0 2px 5px #172e4e50); }
              </style>
              <script src="https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.js"
                      onerror="this.onerror=null;this.src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js';"
                      integrity="sha256-20nQCchB9co0qIjJZRGuk2/Z9VM+kNiyxNV1lvTlZBo=" crossorigin=""></script>
            </head>
            <body>
              <div id="map" aria-label="Choose the parking entrance on the map"></div>
              <div id="map-status" role="status" aria-live="polite">Loading map…</div>
              <script>
                const status = document.getElementById('map-status');
                if (typeof L === 'undefined') {
                  status.textContent = 'Map unavailable. Check internet, or enter coordinates below.';
                } else {
                const center = {{centerJson}};
                const initialMarker = {{initialMarkerJson}};
                const map = L.map('map', { zoomControl: false }).setView(
                  [center.latitude, center.longitude], initialMarker ? 15 : 6);
                L.control.zoom({ position: 'topright' }).addTo(map);
                const tiles = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
                  maxZoom: 19,
                  attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>'
                }).addTo(map);
                let firstTileLoaded = false;
                let tileErrors = 0;
                let backupTileErrors = 0;
                let usingBackupTiles = false;
                const showTileError = () => {
                  if (!firstTileLoaded) {
                    status.textContent = 'Map tiles unavailable. Check internet, or enter coordinates below.';
                    status.style.display = 'block';
                  }
                };
                const backupTiles = L.tileLayer('https://tile.openstreetmap.de/{z}/{x}/{y}.png', {
                  maxZoom: 19,
                  attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>'
                });
                backupTiles.on('tileload', () => {
                  firstTileLoaded = true;
                  status.style.display = 'none';
                });
                backupTiles.on('tileerror', () => {
                  backupTileErrors++;
                  if (usingBackupTiles && backupTileErrors >= 3) showTileError();
                });
                tiles.on('tileload', () => {
                  firstTileLoaded = true;
                  status.style.display = 'none';
                });
                tiles.on('tileerror', () => {
                  if (firstTileLoaded || usingBackupTiles) return;
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
                let marker = null;
                function sendCoordinates(point) {
                  window.location.href = 'quickpark://select?latitude=' +
                    encodeURIComponent(point.lat.toFixed(6)) + '&longitude=' +
                    encodeURIComponent(point.lng.toFixed(6));
                }
                function setSelectedLocation(lat, lng, notify = true) {
                  const point = L.latLng(lat, lng);
                  if (!marker) {
                    marker = L.marker(point, { draggable: true }).addTo(map);
                    marker.on('dragend', () => sendCoordinates(marker.getLatLng()));
                  } else {
                    marker.setLatLng(point);
                  }
                  map.setView(point, Math.max(map.getZoom(), 15));
                  if (notify) sendCoordinates(point);
                }
                if (initialMarker) {
                  setSelectedLocation(initialMarker.latitude, initialMarker.longitude, false);
                } else {
                  marker = L.marker([center.latitude, center.longitude], { draggable: true }).addTo(map);
                  marker.on('dragend', () => sendCoordinates(marker.getLatLng()));
                }
                map.on('click', event => setSelectedLocation(event.latlng.lat, event.latlng.lng));
                window.setSelectedLocation = setSelectedLocation;
                window.setTimeout(() => map.invalidateSize(false), 250);
                window.setTimeout(() => map.invalidateSize(false), 900);
                window.setTimeout(() => {
                  if (!firstTileLoaded && status.textContent === 'Loading map…') {
                    status.textContent = 'Map is taking longer than expected. Enter coordinates manually if needed.';
                  }
                }, 12000);
                }
              </script>
            </body>
            </html>
            """;
    }

    private void SetCoordinateEntries(double latitude, double longitude)
    {
        LatitudeEntry.Text = latitude.ToString("F6", CultureInfo.InvariantCulture);
        LongitudeEntry.Text = longitude.ToString("F6", CultureInfo.InvariantCulture);
    }

    private async void ShowCoordinatesOnMapButton_Clicked(object? sender, EventArgs e)
    {
        if (!TryReadCoordinates(out var latitude, out var longitude))
        {
            ShowError("Enter valid latitude (-90 to 90) and longitude (-180 to 180) first.");
            return;
        }

        try
        {
            if (!_mapReady)
            {
                ShowError("The map is not ready yet. Wait for it to load, then try again.");
                return;
            }

            await UpdateMapSelectionAsync(latitude, longitude);
            StatusLabel.IsVisible = false;
        }
        catch (InvalidOperationException exception)
        {
            ShowError($"The map could not be updated: {exception.Message}");
        }
    }

    private async void UseCurrentLocationButton_Clicked(object? sender, EventArgs e)
    {
        try
        {
            var permission = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (permission != PermissionStatus.Granted)
            {
                permission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }

            if (permission != PermissionStatus.Granted)
            {
                ShowError("Location permission is required to use your current position. You can still tap the map to choose a location.");
                return;
            }

            var current = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10)));
            if (current is null)
            {
                ShowError("QuickPark could not get your current location. Turn on device location or tap the map.");
                return;
            }

            var selected = new Location(current.Latitude, current.Longitude);
            SetCoordinateEntries(selected.Latitude, selected.Longitude);
            await UpdateMapSelectionAsync(selected.Latitude, selected.Longitude);
            StatusLabel.IsVisible = false;
        }
        catch (FeatureNotEnabledException)
        {
            ShowError("Device location is turned off. Enable it or tap the map to choose a location.");
        }
        catch (FeatureNotSupportedException)
        {
            ShowError("This device does not support location services. Tap the map to choose a location.");
        }
        catch (PermissionException exception)
        {
            ShowError($"Location permission error: {exception.Message}");
        }
        catch (TimeoutException)
        {
            ShowError("Getting your location timed out. Try again outdoors or tap the map.");
        }
        catch (InvalidOperationException exception)
        {
            ShowError($"QuickPark could not get your current location: {exception.Message} You can still enter coordinates manually.");
        }
    }

    private async void SaveButton_Clicked(object? sender, EventArgs e)
    {
        if (_isSaving)
        {
            return;
        }

        StatusLabel.IsVisible = false;
        var name = NameEntry.Text?.Trim() ?? string.Empty;
        var address = AddressEntry.Text?.Trim() ?? string.Empty;
        var hours = HoursEntry.Text?.Trim() ?? string.Empty;
        if (name.Length == 0 || address.Length == 0 || hours.Length == 0)
        {
            ShowError("Enter the location name, street address, and operating hours.");
            return;
        }
        if (!TryParseFee(FeeEntry.Text, out var fee) || fee < 0)
        {
            ShowError("Enter a valid non-negative hourly rate in PHP, using your device's decimal separator.");
            return;
        }

        if (!int.TryParse(CapacityEntry.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var capacity) ||
            capacity is < 1 or > 400)
        {
            ShowError("Enter a whole-number capacity between 1 and 400.");
            return;
        }

        if (StatusPicker.SelectedIndex < 0)
        {
            ShowError("Choose whether this parking area is active or inactive.");
            return;
        }

        if (!TryReadCoordinates(out var latitude, out var longitude))
        {
            ShowError("Enter valid latitude (-90 to 90) and longitude (-180 to 180) coordinates.");
            return;
        }

        SetBusy(true);
        try
        {
            await _apiClient.UpsertLocationAsync(
                _location?.Id,
                name,
                address,
                fee,
                hours,
                capacity,
                StatusPicker.SelectedIndex == 0,
                latitude,
                longitude);
            _saved = true;
            await DisplayAlertAsync(
                "Location saved",
                "The parking area and its parking slots were saved to Firebase. Users can book it when the area is active.",
                "Done");
            await Navigation.PopModalAsync();
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
            ShowError($"QuickPark could not reach Firestore: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            ShowError("Saving the location timed out. Check the Admin parking list before trying again.");
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            ShowError($"The location could not be saved: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool TryReadCoordinates(out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        return TryParseCoordinate(LatitudeEntry.Text, out latitude) &&
            TryParseCoordinate(LongitudeEntry.Text, out longitude) &&
            double.IsFinite(latitude) &&
            double.IsFinite(longitude) &&
            latitude is >= -90 and <= 90 &&
            longitude is >= -180 and <= 180;
    }

    private static bool TryParseCoordinate(string? text, out double value)
    {
        const NumberStyles styles = NumberStyles.Float;
        var trimmed = text?.Trim();
        return double.TryParse(trimmed, styles, CultureInfo.CurrentCulture, out value) ||
            double.TryParse(trimmed, styles, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseFee(string? text, out decimal value)
    {
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        var trimmed = text?.Trim();
        return decimal.TryParse(trimmed, styles, CultureInfo.CurrentCulture, out value) ||
            decimal.TryParse(trimmed, styles, CultureInfo.InvariantCulture, out value);
    }

    private async Task UpdateMapSelectionAsync(double latitude, double longitude)
    {
        if (!_mapReady)
        {
            return;
        }

        await LocationPickerMap.EvaluateJavaScriptAsync(
            $"setSelectedLocation({latitude.ToString("R", CultureInfo.InvariantCulture)},{longitude.ToString("R", CultureInfo.InvariantCulture)});");
    }

    private void ShowError(string message)
    {
        StatusLabel.Text = message;
        StatusLabel.IsVisible = true;
    }

    private void SetBusy(bool isBusy)
    {
        _isSaving = isBusy;
        SaveButton.IsEnabled = !isBusy;
        LoadingIndicator.IsVisible = isBusy;
        LoadingIndicator.IsRunning = isBusy;
        SaveButton.Text = isBusy ? "Saving…" : _location is null ? "Save location" : "Save changes";
    }
}
