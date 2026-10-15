using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuickPark;

internal sealed class AdminApiClient
{
    private static readonly HashSet<string> AllowedSlotStatuses =
        ["Available", "Occupied", "Reserved", "Maintenance"];
    private readonly FirestoreRestClient _firestore = new();
    private readonly FirebaseFunctionsApiClient _functions = new();

    public async Task<AdminOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var locationDocuments = await _firestore.GetCollectionAsync(
            "locations", limit: 500, cancellationToken: cancellationToken);
        var optionalCollections = await Task.WhenAll(
            TryLoadCollectionAsync("slots", 1000, cancellationToken),
            TryLoadCollectionAsync("reservations", 500, cancellationToken, "createdAt", true),
            TryLoadCollectionAsync("payments", 500, cancellationToken),
            TryLoadCollectionAsync("userProfiles", 1000, cancellationToken));
        var locations = locationDocuments.Select(FirestoreRestClient.Deserialize<AdminLocation>).ToList();
        var slotsResult = optionalCollections[0];
        var reservationsResult = optionalCollections[1];
        var paymentsResult = optionalCollections[2];
        var profilesResult = optionalCollections[3];
        var slots = slotsResult.Documents.Select(FirestoreRestClient.Deserialize<AdminSlot>).ToList();
        var reservations = reservationsResult.Documents;
        var payments = paymentsResult.Documents;
        var profiles = profilesResult.Documents;

        UserDirectoryResponse? userDirectory = null;
        var userDirectoryError = profilesResult.Error;
        try
        {
            userDirectory = await _functions.CallAsync<UserDirectoryResponse>(
                "userDirectory", new { }, cancellationToken);
        }
        catch (FirebaseFunctionNotDeployedException exception)
        {
            userDirectoryError = exception.Message;
        }
        catch (FirebaseAuthException exception)
        {
            userDirectoryError = exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            userDirectoryError = exception.Message;
        }
        catch (HttpRequestException exception)
        {
            userDirectoryError = exception.Message;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            userDirectoryError = "The Firebase Authentication status request timed out.";
        }

        var authUsers = userDirectory?.Users.ToDictionary(user => user.Uid, StringComparer.Ordinal)
            ?? new Dictionary<string, AdminUser>(StringComparer.Ordinal);
        var profilesUnavailable = profilesResult.IsUnavailable && userDirectory is null;
        var users = profilesResult.IsUnavailable && userDirectory is not null
            ? userDirectory.Users
            : profiles
                .Select(FirestoreRestClient.Deserialize<UserProfile>)
                .Select(profile =>
                {
                    var verified = authUsers.GetValueOrDefault(profile.Id);
                    return new AdminUser
                    {
                        Uid = profile.Id,
                        Email = verified?.Email ?? profile.Email,
                        DisplayName = verified?.DisplayName is { Length: > 0 } displayName
                            ? displayName
                            : profile.DisplayName,
                        Role = profile.Role,
                        RegisteredAt = profile.CreatedAt,
                        FirebaseAuthStatus = verified is null
                            ? "Not verified"
                            : verified.Disabled ? "Disabled" : "Active",
                        Admin = verified?.Admin ?? false,
                        Disabled = verified?.Disabled ?? false
                    };
                })
                .OrderBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
                .ToList();
        var usersById = users.ToDictionary(user => user.Uid, StringComparer.Ordinal);
        var reservationRows = reservations
            .Select(reservation =>
            {
                var row = (JsonObject)reservation.DeepClone();
                var user = usersById.GetValueOrDefault(ReadString(reservation, "userId"));
                row["userEmail"] = user?.Email ?? string.Empty;
                return FirestoreRestClient.ToJsonElement(row);
            })
            .ToList();
        var paymentRows = payments
            .Select(FirestoreRestClient.ToJsonElement)
            .ToList();
        var now = DateTime.UtcNow;
        var todayStart = now.Date;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var reservationDates = reservations
            .Select(item => ReadDate(item, "createdAt"))
            .Where(date => date.HasValue)
            .Select(date => date!.Value)
            .ToList();
        var occupiedSlots = slots.Count(slot => slot.Status == "Occupied");
        var confirmed = CountStatus(reservations, "Confirmed");
        var pending = CountStatus(reservations, "Pending");
        var completed = CountStatus(reservations, "Completed");
        var cancelled = CountStatus(reservations, "Cancelled");
        var report = new AdminReport
        {
            LocationCount = locations.Count,
            SlotCount = slotsResult.IsUnavailable ? -1 : slots.Count,
            AvailableSlots = slotsResult.IsUnavailable ? -1 : slots.Count(slot => slot.Status == "Available"),
            OccupiedSlots = slotsResult.IsUnavailable ? -1 : occupiedSlots,
            ReservationCount = reservationsResult.IsUnavailable ? -1 : reservations.Count,
            PendingReservations = reservationsResult.IsUnavailable ? -1 : pending,
            ConfirmedReservations = reservationsResult.IsUnavailable ? -1 : confirmed,
            CompletedReservations = reservationsResult.IsUnavailable ? -1 : completed,
            CancelledReservations = reservationsResult.IsUnavailable ? -1 : cancelled,
            DailyReservations = reservationsResult.IsUnavailable ? -1 : reservationDates.Count(date => date >= todayStart),
            MonthlyReservations = reservationsResult.IsUnavailable ? -1 : reservationDates.Count(date => date >= monthStart),
            ParkingUsagePercent = slotsResult.IsUnavailable
                ? -1
                : slots.Count == 0 ? 0 : (decimal)occupiedSlots / slots.Count * 100,
            PaymentCount = paymentsResult.IsUnavailable ? -1 : payments.Count,
            PaymentAmount = paymentsResult.IsUnavailable ? -1 : payments
                .Where(IsProcessedPayment)
                .Sum(item => ReadDecimal(item, "amount")),
            UsersTruncated = userDirectory?.UsersTruncated ?? profiles.Count >= 1000,
            UserDirectoryUnavailable = profilesUnavailable,
            AuthenticationDirectoryAvailable = userDirectory is not null,
            AuthenticationDirectoryMessage = userDirectory is not null
                ? profilesResult.IsUnavailable
                    ? $"Application profiles and Firebase Authentication status are loaded by the secure Admin API. {profilesResult.Error}"
                    : "Firebase Authentication status is verified by the secure Admin API."
                : profilesUnavailable
                    ? $"User profiles are unavailable: {userDirectoryError}"
                    : $"Profile records are shown, but Firebase Authentication status is not verified. {userDirectoryError}",
            SlotsUnavailable = slotsResult.IsUnavailable,
            ReservationsUnavailable = reservationsResult.IsUnavailable,
            PaymentsUnavailable = paymentsResult.IsUnavailable,
            SlotsError = slotsResult.Error,
            ReservationsError = reservationsResult.Error,
            PaymentsError = paymentsResult.Error
        };
        return new AdminOverview
        {
            Locations = locations,
            Slots = slots,
            Reservations = reservationRows,
            Payments = paymentRows,
            Users = users,
            Report = report
        };
    }

    private async Task<CollectionLoadResult> TryLoadCollectionAsync(
        string collection,
        int limit,
        CancellationToken cancellationToken,
        string? orderByField = null,
        bool descendingOrder = false)
    {
        try
        {
            var documents = await _firestore.GetCollectionAsync(
                collection,
                limit: limit,
                cancellationToken: cancellationToken,
                orderByField: orderByField,
                descendingOrder: descendingOrder);
            return new CollectionLoadResult(documents, string.Empty);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or FirebaseAuthException or HttpRequestException)
        {
            return new CollectionLoadResult([], exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CollectionLoadResult([], $"Loading the {collection} collection timed out.");
        }
    }

    public async Task UpsertLocationAsync(
        string? id,
        string name,
        string address,
        decimal feePerHour,
        string operatingHours,
        int totalCapacity,
        bool isActive,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(address) ||
            string.IsNullOrWhiteSpace(operatingHours))
        {
            throw new InvalidOperationException("Location name, address, and operating hours are required.");
        }
        if (feePerHour < 0)
        {
            throw new InvalidOperationException("Hourly fee must be a non-negative number.");
        }
        if (totalCapacity is < 1 or > 400)
        {
            throw new InvalidOperationException("Total capacity must be between 1 and 400 parking slots.");
        }
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 ||
            longitude is < -180 or > 180)
        {
            throw new InvalidOperationException("Select or enter valid latitude and longitude coordinates.");
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            id = FirestoreRestClient.NormalizeDocumentId("locations", id);
        }

        var locationId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        List<JsonObject> existingSlots = string.IsNullOrWhiteSpace(id)
            ? []
            : await _firestore.GetCollectionAsync("slots", "locationId", locationId, 1000, cancellationToken);
        if (totalCapacity < existingSlots.Count)
        {
            throw new InvalidOperationException(
                $"Capacity cannot be less than the {existingSlots.Count} existing parking slots. Remove eligible slots first.");
        }

        var locations = await _firestore.GetCollectionAsync("locations", limit: 500, cancellationToken: cancellationToken);
        if (locations.Any(location =>
        {
            var existingId = ReadString(location, "id");
            return existingId != locationId &&
                string.Equals(ReadString(location, "name").Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ReadString(location, "address").Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase);
        }))
        {
            throw new InvalidOperationException("A parking area with the same name and address already exists.");
        }

        var locationData = new Dictionary<string, object?>
        {
            ["name"] = name.Trim(),
            ["address"] = address.Trim(),
            ["feePerHour"] = feePerHour,
            ["operatingHours"] = operatingHours.Trim(),
            ["totalCapacity"] = totalCapacity,
            ["latitude"] = latitude,
            ["longitude"] = longitude,
            ["isActive"] = isActive,
            ["updatedAt"] = DateTime.UtcNow
        };
        if (string.IsNullOrWhiteSpace(id))
        {
            locationData["createdAt"] = DateTime.UtcNow;
        }
        else
        {
            if (await _firestore.GetDocumentAsync("locations", locationId, cancellationToken) is null)
            {
                throw new InvalidOperationException("Parking location not found.");
            }

            locationData["isActive"] = isActive;
        }

        var writes = new List<JsonObject>
        {
            _firestore.BuildUpdateWrite(
                "locations", locationId, locationData, createOnly: string.IsNullOrWhiteSpace(id))
        };
        var usedLabels = existingSlots.Select(slot => ReadString(slot, "label"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextSlotNumber = 1;
        while (existingSlots.Count + writes.Count - 1 < totalCapacity)
        {
            string label;
            do
            {
                label = $"P-{nextSlotNumber++:D3}";
            } while (usedLabels.Contains(label));

            usedLabels.Add(label);
            var slotId = Guid.NewGuid().ToString("N");
            writes.Add(_firestore.BuildUpdateWrite("slots", slotId, new
            {
                locationId,
                label,
                status = "Available",
                createdAt = DateTime.UtcNow,
                updatedAt = DateTime.UtcNow
            }, createOnly: true));
        }

        await _firestore.CommitAsync(writes, cancellationToken);
    }

    public async Task SetLocationActiveAsync(
        string id,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var location = await _firestore.GetDocumentAsync("locations", id, cancellationToken)
            ?? throw new InvalidOperationException("Parking location not found.");
        if (ReadBoolean(location, "isActive", defaultValue: true) == isActive)
        {
            return;
        }

        await _firestore.CommitAsync(
            [_firestore.BuildUpdateWrite("locations", id, new
            {
                isActive,
                updatedAt = DateTime.UtcNow
            }, updateTime: ReadString(location, "__updateTime"))],
            cancellationToken);
    }

    public async Task DeleteLocationAsync(string id, CancellationToken cancellationToken = default)
    {
        var slots = await _firestore.GetCollectionAsync(
            "slots", "locationId", id, 1, cancellationToken);
        if (slots.Count > 0)
        {
            throw new InvalidOperationException("Remove or reassign this location's slots before deleting it.");
        }
        var reservations = await _firestore.GetCollectionAsync(
            "reservations", "locationId", id, 1, cancellationToken);
        if (reservations.Count > 0)
        {
            throw new InvalidOperationException("This location has reservation history and cannot be deleted. Deactivate it instead.");
        }
        var document = await _firestore.GetDocumentAsync("locations", id, cancellationToken)
            ?? throw new InvalidOperationException("Parking location not found.");
        await _firestore.CommitAsync(
            [_firestore.BuildDeleteWrite("locations", id, ReadString(document, "__updateTime"))],
            cancellationToken);
    }

    public async Task UpsertSlotAsync(
        string? id,
        string locationId,
        string label,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new InvalidOperationException("Slot label is required.");
        }
        if (!AllowedSlotStatuses.Contains(status))
        {
            throw new InvalidOperationException("The selected parking slot status is invalid.");
        }
        if (string.IsNullOrWhiteSpace(locationId))
        {
            throw new InvalidOperationException("The selected parking location does not exist.");
        }
        var location = await _firestore.GetDocumentAsync("locations", locationId, cancellationToken)
            ?? throw new InvalidOperationException("The selected parking location does not exist.");
        var slotId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        var locationSlots = await _firestore.GetCollectionAsync(
            "slots", "locationId", locationId, 1000, cancellationToken);
        if (locationSlots.Any(slot =>
            ReadString(slot, "id") != slotId &&
            string.Equals(ReadString(slot, "label").Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A parking slot with this label already exists at this location.");
        }
        if (string.IsNullOrWhiteSpace(id) &&
            location["totalCapacity"] is JsonValue capacityValue &&
            capacityValue.TryGetValue<int>(out var capacity) &&
            locationSlots.Count >= capacity)
        {
            throw new InvalidOperationException(
                "This area has reached its saved capacity. Edit the parking area capacity before adding more slots.");
        }
        var existingSlot = string.IsNullOrWhiteSpace(id)
            ? null
            : await _firestore.GetDocumentAsync("slots", slotId, cancellationToken);
        if (existingSlot is not null && ReadString(existingSlot, "status") == "Reserved" &&
            (ReadString(existingSlot, "locationId") != locationId ||
             ReadString(existingSlot, "label") != label.Trim() ||
             status != "Reserved"))
        {
            throw new InvalidOperationException("Cancel or complete the active reservation before editing its reserved slot.");
        }
        if (existingSlot is not null &&
            (ReadString(existingSlot, "locationId") != locationId ||
             ReadString(existingSlot, "label") != label.Trim()))
        {
            var reservations = await _firestore.GetCollectionAsync(
                "reservations", "slotId", slotId, 1, cancellationToken);
            if (reservations.Count > 0)
            {
                throw new InvalidOperationException("A slot with reservation history cannot be renamed or moved.");
            }
        }
        var slotData = new Dictionary<string, object?>
        {
            ["locationId"] = locationId,
            ["label"] = label.Trim(),
            ["status"] = status,
            ["updatedAt"] = DateTime.UtcNow
        };
        if (string.IsNullOrWhiteSpace(id))
        {
            slotData["createdAt"] = DateTime.UtcNow;
        }
        await _firestore.CommitAsync(
            [_firestore.BuildUpdateWrite(
                "slots", slotId, slotData, createOnly: string.IsNullOrWhiteSpace(id))],
            cancellationToken);
    }

    public async Task SetSlotAvailabilityAsync(
        string id,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (!AllowedSlotStatuses.Contains(status))
        {
            throw new InvalidOperationException("The selected parking slot status is invalid.");
        }
        var slot = await _firestore.GetDocumentAsync("slots", id, cancellationToken)
            ?? throw new InvalidOperationException("Parking slot not found.");
        if (ReadString(slot, "status") == "Reserved" && status != "Reserved")
        {
            throw new InvalidOperationException("Cancel or complete the active reservation before changing this reserved slot.");
        }
        await _firestore.CommitAsync(
        [
            _firestore.BuildUpdateWrite("slots", id, new
            {
                status,
                updatedAt = DateTime.UtcNow
            }, updateTime: ReadString(slot, "__updateTime"))
        ], cancellationToken);
    }

    public async Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default)
    {
        var slot = await _firestore.GetDocumentAsync("slots", id, cancellationToken)
            ?? throw new InvalidOperationException("Parking slot not found.");
        if (ReadString(slot, "status") == "Reserved")
        {
            throw new InvalidOperationException("Cancel or complete the active reservation before deleting this slot.");
        }
        var reservations = await _firestore.GetCollectionAsync(
            "reservations", "slotId", id, 1, cancellationToken);
        if (reservations.Count > 0)
        {
            throw new InvalidOperationException("This slot has reservation history and cannot be deleted. Mark it as maintenance instead.");
        }
        await _firestore.CommitAsync(
            [_firestore.BuildDeleteWrite("slots", id, ReadString(slot, "__updateTime"))],
            cancellationToken);
    }

    public async Task SetReservationStatusAsync(
        string reservationId,
        string status,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _functions.CallAsync<JsonElement>("setReservationStatus", new
            {
                reservationId,
                status
            }, cancellationToken);
            return;
        }
        catch (FirebaseFunctionNotDeployedException)
        {
            // Keep the existing Admin Firestore workflow available until Functions are deployed.
        }

        var reservation = await _firestore.GetDocumentAsync("reservations", reservationId, cancellationToken)
            ?? throw new InvalidOperationException("Reservation not found.");
        var currentStatus = ReadString(reservation, "status");
        var validTransition =
            (currentStatus == "Pending" && status is "Confirmed" or "Cancelled") ||
            (currentStatus == "Confirmed" && status is "Completed" or "Cancelled");
        if (!validTransition)
        {
            throw new InvalidOperationException($"Cannot change reservation from {currentStatus} to {status}.");
        }
        if (status == "Confirmed" && ReadDate(reservation, "endTime") <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("This reservation has expired and cannot be approved.");
        }
        var reservationEndTime = ReadDate(reservation, "endTime");
        if (status == "Completed" &&
            (!reservationEndTime.HasValue || reservationEndTime.Value > DateTime.UtcNow))
        {
            throw new InvalidOperationException("A reservation can only be completed after its scheduled end time.");
        }
        var reservationStartTime = ReadDate(reservation, "startTime");
        if (status == "Cancelled" &&
            (!reservationStartTime.HasValue || reservationStartTime.Value <= DateTime.UtcNow))
        {
            throw new InvalidOperationException("A reservation cannot be cancelled after its scheduled start time.");
        }

        var slotId = ReadString(reservation, "slotId");
        var slot = await _firestore.GetDocumentAsync("slots", slotId, cancellationToken)
            ?? throw new InvalidOperationException("The reservation is missing its parking slot.");
        var activeReservationId = ReadString(slot, "activeReservationId");
        if (status == "Confirmed" &&
            (ReadString(slot, "status") != "Reserved" ||
             (!string.IsNullOrEmpty(activeReservationId) && activeReservationId != reservationId)))
        {
            throw new InvalidOperationException("The reserved slot is no longer available for this reservation.");
        }

        var now = DateTime.UtcNow;
        var reservationUpdate = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["updatedAt"] = now
        };
        if (status == "Cancelled")
        {
            reservationUpdate["cancelledAt"] = now;
        }
        if (status == "Completed")
        {
            reservationUpdate["completedAt"] = now;
        }
        var writes = new List<JsonObject>
        {
            _firestore.BuildUpdateWrite(
                "reservations", reservationId, reservationUpdate,
                updateTime: ReadString(reservation, "__updateTime"))
        };
        var shouldReleaseSlot = status is "Cancelled" or "Completed" &&
            ReadString(slot, "status") == "Reserved" &&
            activeReservationId == reservationId;
        if (status == "Confirmed" && string.IsNullOrEmpty(activeReservationId))
        {
            writes.Add(_firestore.BuildUpdateWrite("slots", slotId, new
            {
                activeReservationId = reservationId,
                updatedAt = now
            }, updateTime: ReadString(slot, "__updateTime")));
        }
        else if (shouldReleaseSlot)
        {
            writes.Add(_firestore.BuildUpdateWrite(
                "slots", slotId, new { status = "Available", updatedAt = now },
                updateTime: ReadString(slot, "__updateTime"),
                additionalMaskFields: ["activeReservationId"]));
        }

        var paymentId = ReadString(reservation, "paymentId");
        if (status == "Cancelled" && !string.IsNullOrWhiteSpace(paymentId))
        {
            var payment = await _firestore.GetDocumentAsync("payments", paymentId, cancellationToken);
            if (payment is not null)
            {
                writes.Add(_firestore.BuildUpdateWrite("payments", paymentId, new
                {
                    status = "Cancelled — not processed",
                    updatedAt = now
                }, updateTime: ReadString(payment, "__updateTime")));
            }
        }
        var wasRejected = status == "Cancelled" && currentStatus == "Pending";
        var reservationDateTime = reservationStartTime?.ToLocalTime().ToString("MMM d, yyyy h:mm tt")
            ?? "time unavailable";
        writes.Add(_firestore.CreateNotificationWrite(
            ReadString(reservation, "userId"),
            wasRejected ? "Reservation rejected" : $"Reservation {status.ToLowerInvariant()}",
            $"{ReadString(reservation, "locationName")} · Slot {ReadString(reservation, "slotLabel")} · {reservationDateTime} is now {(wasRejected ? "rejected by the parking administrator" : status.ToLowerInvariant())}.",
            "Booking",
            now));
        await _firestore.CommitAsync(writes, cancellationToken);
    }

    public async Task<int> SendNotificationAsync(
        string title,
        string message,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 100 ||
            string.IsNullOrWhiteSpace(message) || message.Length > 1000)
        {
            throw new InvalidOperationException("Enter a title (up to 100 characters) and message (up to 1,000 characters).");
        }
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var notification = _firestore.CreateNotificationWrite(
                userId, title.Trim(), message.Trim(), "Announcement", DateTime.UtcNow);
            await _firestore.CommitAsync([notification], cancellationToken);
            return 1;
        }

        var globalNotification = _firestore.CreateNotificationWrite(
            "*", title.Trim(), message.Trim(), "Announcement", DateTime.UtcNow);
        await _firestore.CommitAsync([globalNotification], cancellationToken);
        return 1;
    }

    public async Task SetUserDisabledAsync(
        string userId,
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        await _functions.CallAsync<JsonElement>("setUserDisabled", new
        {
            uid = userId,
            disabled
        }, cancellationToken);
    }

    private static int CountStatus(IEnumerable<JsonObject> reservations, string status) =>
        reservations.Count(reservation => ReadString(reservation, "status") == status);

    private static bool IsProcessedPayment(JsonObject payment) =>
        ReadString(payment, "status").ToLowerInvariant() is "paid" or "processed" or "succeeded";

    private static string ReadString(JsonObject document, string field) =>
        document[field]?.ToString() ?? string.Empty;

    private static bool ReadBoolean(JsonObject document, string field, bool defaultValue) =>
        document[field] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : defaultValue;

    private static DateTime? ReadDate(JsonObject document, string field) =>
        DateTime.TryParse(ReadString(document, field), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
                ? value
                : null;

    private static decimal ReadDecimal(JsonObject document, string field) =>
        decimal.TryParse(ReadString(document, field), NumberStyles.Number,
            CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
}

internal sealed class AdminOverview
{
    public List<AdminLocation> Locations { get; set; } = [];
    public List<AdminSlot> Slots { get; set; } = [];
    public List<JsonElement> Reservations { get; set; } = [];
    public List<JsonElement> Payments { get; set; } = [];
    public List<AdminUser> Users { get; set; } = [];
    public AdminReport Report { get; set; } = new();
}

internal sealed record CollectionLoadResult(List<JsonObject> Documents, string Error)
{
    public bool IsUnavailable => Error.Length > 0;
}

internal sealed class UserDirectoryResponse
{
    public List<AdminUser> Users { get; set; } = [];
    public bool UsersTruncated { get; set; }
}

internal sealed class UserProfile
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "user";
    public DateTime? CreatedAt { get; set; }
}

internal sealed class AdminLocation
{
    public AdminLocation()
    {
    }

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string OperatingHours { get; set; } = string.Empty;
    public decimal FeePerHour { get; set; }
    public int TotalCapacity { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedAt { get; set; }
}

internal sealed class AdminSlot
{
    public AdminSlot()
    {
    }

    public string Id { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

internal sealed class AdminUser
{
    public string Uid { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "user";
    public DateTime? RegisteredAt { get; set; }
    public string FirebaseAuthStatus { get; set; } = "Not verified";
    public bool Disabled { get; set; }
    public bool Admin { get; set; }
}

internal sealed class AdminReport
{
    public int LocationCount { get; set; }
    public int SlotCount { get; set; }
    public int AvailableSlots { get; set; }
    public int OccupiedSlots { get; set; }
    public int ReservationCount { get; set; }
    public int PendingReservations { get; set; }
    public int ConfirmedReservations { get; set; }
    public int CompletedReservations { get; set; }
    public int CancelledReservations { get; set; }
    public int PaymentCount { get; set; }
    public decimal PaymentAmount { get; set; }
    public int DailyReservations { get; set; }
    public int MonthlyReservations { get; set; }
    public decimal ParkingUsagePercent { get; set; }
    public bool UsersTruncated { get; set; }
    public bool UserDirectoryUnavailable { get; set; }
    public bool AuthenticationDirectoryAvailable { get; set; }
    public string AuthenticationDirectoryMessage { get; set; } = string.Empty;
    public bool SlotsUnavailable { get; set; }
    public bool ReservationsUnavailable { get; set; }
    public bool PaymentsUnavailable { get; set; }
    public string SlotsError { get; set; } = string.Empty;
    public string ReservationsError { get; set; } = string.Empty;
    public string PaymentsError { get; set; } = string.Empty;
}
