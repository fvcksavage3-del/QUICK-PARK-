using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.Networking;

namespace QuickPark;

internal sealed class BookingApiClient
{
    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    private static readonly HashSet<string> PaymentMethods =
    [
        "Pay at parking facility",
        "GCash (preview only)",
        "Card (preview only)"
    ];

    private readonly FirestoreRestClient _firestore = new();
    private readonly FirebaseFunctionsApiClient _functions = new();

    public async Task<BookingOptionsResponse> GetOptionsAsync(
        DateTime? requestedStartTime = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = requestedStartTime ?? DateTime.Now.AddMinutes(15);
        var userId = GetCurrentUserId("view parking availability");
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            return GetCachedOptionsOrThrow(userId, startTime);
        }

        try
        {
            var options = await GetRemoteOptionsAsync(startTime, cancellationToken);
            OfflineDataStore.CacheOptions(userId, options);
            return options;
        }
        catch (HttpRequestException)
        {
            return GetCachedOptionsOrThrow(userId, startTime);
        }
        catch (TaskCanceledException)
        {
            return GetCachedOptionsOrThrow(userId, startTime);
        }
    }

    private async Task<BookingOptionsResponse> GetRemoteOptionsAsync(
        DateTime startTime,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _functions.CallAsync<BookingOptionsResponse>("bookingOptions", new
            {
                startTime = startTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            }, cancellationToken);
        }
        catch (FirebaseFunctionNotDeployedException)
        {
            // Keep the current Spark-plan flow usable until the transactional Function is deployed.
        }

        var results = await Task.WhenAll(
            _firestore.GetCollectionAsync("locations", limit: 500, cancellationToken: cancellationToken),
            _firestore.GetCollectionAsync("slots", "status", "Available", 1000, cancellationToken));
        var locations = results[0]
            .Select(FirestoreRestClient.Deserialize<AdminLocation>)
            .Where(location => location.IsActive)
            .ToList();
        var activeLocationIds = locations.Select(location => location.Id).ToHashSet(StringComparer.Ordinal);
        return new BookingOptionsResponse
        {
            Locations = locations,
            Slots = results[1]
                .Select(FirestoreRestClient.Deserialize<AdminSlot>)
                .Where(slot => activeLocationIds.Contains(slot.LocationId))
                .ToList()
        };
    }

    public async Task<MyBookingResponse> GetMyBookingAsync(CancellationToken cancellationToken = default)
    {
        var userId = AuthSession.CurrentUser?.UserId
            ?? throw new FirebaseAuthException("Sign in again before viewing your booking.");
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            return new MyBookingResponse { Reservation = OfflineDataStore.GetCurrentBooking(userId) };
        }

        try
        {
        var reservations = await _firestore.GetCollectionAsync(
            "reservations", "userId", userId, 50, cancellationToken,
            orderByField: "createdAt", descendingOrder: true);
        var reservation = reservations
            .Where(item => ReadString(item, "status") is "Pending" or "Confirmed")
            .OrderByDescending(item => ReadDate(item, "createdAt"))
            .FirstOrDefault();
        var remoteHistory = reservations
            .Select(FirestoreRestClient.Deserialize<MyBookingDetails>)
            .ToList();
        OfflineDataStore.CacheRemoteHistory(userId, remoteHistory);
        if (reservation is null)
        {
            return new MyBookingResponse { Reservation = OfflineDataStore.GetCurrentBooking(userId) };
        }

        var paymentId = ReadString(reservation, "paymentId");
        var payment = string.IsNullOrWhiteSpace(paymentId)
            ? null
            : await _firestore.GetDocumentAsync("payments", paymentId, cancellationToken);
        reservation["paymentMethod"] = ReadString(payment, "method");
        reservation["paymentStatus"] = ReadString(payment, "status");
        reservation["receiptNumber"] = ReadString(payment, "receiptNumber");
        var details = FirestoreRestClient.Deserialize<MyBookingDetails>(reservation);
        OfflineDataStore.CacheRemoteBooking(userId, details);
        return new MyBookingResponse
        {
            Reservation = OfflineDataStore.GetCurrentBooking(userId)
        };
        }
        catch (HttpRequestException)
        {
            return new MyBookingResponse { Reservation = OfflineDataStore.GetCurrentBooking(userId) };
        }
        catch (TaskCanceledException)
        {
            return new MyBookingResponse { Reservation = OfflineDataStore.GetCurrentBooking(userId) };
        }
    }

    public async Task<AdminLocation?> GetLocationAsync(
        string locationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(locationId))
        {
            throw new InvalidOperationException("This reservation is missing its parking location.");
        }

        var userId = GetCurrentUserId("view saved parking details");
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            return OfflineDataStore.GetCachedLocation(userId, locationId);
        }

        try
        {
            var document = await _firestore.GetDocumentAsync("locations", locationId, cancellationToken);
            if (document is null)
            {
                return OfflineDataStore.GetCachedLocation(userId, locationId);
            }

            var location = FirestoreRestClient.Deserialize<AdminLocation>(document);
            OfflineDataStore.CacheLocation(userId, location);
            return location;
        }
        catch (HttpRequestException)
        {
            return OfflineDataStore.GetCachedLocation(userId, locationId);
        }
        catch (TaskCanceledException)
        {
            return OfflineDataStore.GetCachedLocation(userId, locationId);
        }
    }

    public async Task<AdminLocation> GetNavigationLocationAsync(
        string reservationId,
        CancellationToken cancellationToken = default)
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            throw new InvalidOperationException("Turn-by-turn directions require an internet connection. Saved parking details remain available offline.");
        }

        var userId = AuthSession.CurrentUser?.UserId
            ?? throw new FirebaseAuthException("Sign in again before starting navigation.");
        if (string.IsNullOrWhiteSpace(reservationId))
        {
            throw new InvalidOperationException("This booking is missing its reference.");
        }

        var reservation = await _firestore.GetDocumentAsync("reservations", reservationId, cancellationToken)
            ?? throw new InvalidOperationException("The booking linked to navigation could not be found.");
        if (ReadString(reservation, "userId") != userId)
        {
            throw new InvalidOperationException("This booking does not belong to the signed-in user.");
        }
        if (ReadString(reservation, "status") != "Confirmed")
        {
            throw new InvalidOperationException("Navigation is available only for confirmed bookings.");
        }
        if (ReadDate(reservation, "endTime") is not { } endTime || endTime <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("This booking has no valid end time or has expired, so navigation is unavailable.");
        }

        var locationId = ReadString(reservation, "locationId");
        var slotId = ReadString(reservation, "slotId");
        if (string.IsNullOrWhiteSpace(locationId) || string.IsNullOrWhiteSpace(slotId))
        {
            throw new InvalidOperationException("This booking is missing its linked parking location or slot.");
        }

        var slot = await _firestore.GetDocumentAsync("slots", slotId, cancellationToken)
            ?? throw new InvalidOperationException("The parking slot linked to this booking could not be found.");
        if (ReadString(slot, "locationId") != locationId)
        {
            throw new InvalidOperationException("The parking slot does not belong to this booking's location.");
        }

        var location = await GetLocationAsync(locationId, cancellationToken)
            ?? throw new InvalidOperationException("The parking location linked to this booking could not be found.");
        if (location.Latitude is not { } latitude ||
            location.Longitude is not { } longitude ||
            !double.IsFinite(latitude) ||
            !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 ||
            longitude is < -180 or > 180)
        {
            throw new InvalidOperationException(
                "The parking location linked to this booking has no valid entrance coordinates.");
        }

        return location;
    }

    public async Task<List<MyBookingDetails>> GetMyReservationHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        var userId = AuthSession.CurrentUser?.UserId
            ?? throw new FirebaseAuthException("Sign in again before viewing reservation history.");
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            return OfflineDataStore.GetHistory(userId);
        }

        try
        {
        var reservations = await _firestore.GetCollectionAsync(
            "reservations", "userId", userId, 100, cancellationToken,
            orderByField: "createdAt", descendingOrder: true);

        var history = reservations
            .Select(FirestoreRestClient.Deserialize<MyBookingDetails>)
            .ToList();
        OfflineDataStore.CacheRemoteHistory(userId, history);
        return OfflineDataStore.GetHistory(userId);
        }
        catch (HttpRequestException)
        {
            return OfflineDataStore.GetHistory(userId);
        }
        catch (TaskCanceledException)
        {
            return OfflineDataStore.GetHistory(userId);
        }
    }

    public async Task SyncPendingChangesAsync(CancellationToken cancellationToken = default)
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet ||
            !await SyncGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var userId = AuthSession.CurrentUser?.UserId;
            if (userId is null)
            {
                return;
            }

            if (OfflineDataStore.GetPendingProfileUpdate(userId) is { } profileUpdate)
            {
                try
                {
                    await new AuthApiClient().UpdateOwnProfileAsync(
                        profileUpdate.DisplayName,
                        cancellationToken,
                        profileUpdate.PhoneNumber);
                    if (AuthSession.CurrentUser is { } user)
                    {
                        AuthSession.Update(user with
                        {
                            DisplayName = profileUpdate.DisplayName,
                            PhoneNumber = profileUpdate.PhoneNumber
                        });
                    }
                    OfflineDataStore.ClearPendingProfileUpdate(userId);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or FirebaseAuthException)
                {
                    System.Diagnostics.Debug.WriteLine($"QuickPark could not sync the saved profile: {exception.Message}");
                }
            }

            foreach (var queued in OfflineDataStore.GetPendingCreates(userId))
            {
                try
                {
                    var response = await CreateReservationOnlineAsync(
                        queued.SyncLocationId,
                        queued.SyncSlotId,
                        queued.StartTime,
                        queued.SyncPaymentMethod,
                        queued.Id,
                        cancellationToken);
                    OfflineDataStore.CompleteQueuedCreation(userId, queued.Id, response);
                }
                catch (HttpRequestException)
                {
                    return;
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception exception) when (exception is InvalidOperationException or FirebaseAuthException)
                {
                    System.Diagnostics.Debug.WriteLine($"QuickPark could not sync a saved reservation: {exception.Message}");
                    return;
                }
            }

            foreach (var queued in OfflineDataStore.GetPendingCancellations(userId))
            {
                try
                {
                    await CancelReservationOnlineAsync(queued.Id, cancellationToken);
                    OfflineDataStore.CompleteQueuedCancellation(userId, queued.Id);
                }
                catch (HttpRequestException)
                {
                    return;
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception exception) when (exception is InvalidOperationException or FirebaseAuthException)
                {
                    System.Diagnostics.Debug.WriteLine($"QuickPark could not sync a saved cancellation: {exception.Message}");
                    return;
                }
            }
        }
        finally
        {
            SyncGate.Release();
        }
    }

    public async Task<CreateReservationResponse> CreateReservationAsync(
        string locationId,
        string slotId,
        DateTime startTime,
        string paymentMethod,
        string? clientRequestId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId("make a reservation");
        var requestId = clientRequestId ?? $"local-{Guid.NewGuid():N}";
        if (startTime.ToUniversalTime() < DateTime.UtcNow.AddMinutes(-1))
        {
            throw new InvalidOperationException("Select a valid future reservation start time.");
        }
        if (!PaymentMethods.Contains(paymentMethod))
        {
            throw new InvalidOperationException("The selected payment method is invalid.");
        }

        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            var queued = OfflineDataStore.QueueReservation(userId, locationId, slotId, startTime, paymentMethod, requestId);
            return new CreateReservationResponse
            {
                ReservationId = queued.Id,
                LocationName = queued.LocationName,
                SlotLabel = queued.SlotLabel,
                Fee = queued.Fee,
                EndTime = queued.StartTime.AddHours(1),
                ReceiptNumber = queued.ReceiptNumber,
                Status = queued.Status
            };
        }

        CreateReservationResponse response;
        try
        {
            response = await CreateReservationOnlineAsync(
                locationId,
                slotId,
                startTime,
                paymentMethod,
                requestId,
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            return CreateQueuedResponse(OfflineDataStore.QueueReservation(
                userId, locationId, slotId, startTime, paymentMethod, requestId));
        }
        catch (TaskCanceledException)
        {
            return CreateQueuedResponse(OfflineDataStore.QueueReservation(
                userId, locationId, slotId, startTime, paymentMethod, requestId));
        }
        OfflineDataStore.CacheRemoteBooking(userId, new MyBookingDetails
        {
            Id = response.ReservationId,
            LocationId = locationId,
            SlotId = slotId,
            LocationName = response.LocationName,
            SlotLabel = response.SlotLabel,
            StartTime = startTime,
            Fee = response.Fee,
            Status = response.Status,
            PaymentMethod = paymentMethod,
            PaymentStatus = "Not processed",
            ReceiptNumber = response.ReceiptNumber,
            EndTime = response.EndTime
        });
        return response;
    }

    private async Task<CreateReservationResponse> CreateReservationOnlineAsync(
        string locationId,
        string slotId,
        DateTime startTime,
        string paymentMethod,
        string clientRequestId,
        CancellationToken cancellationToken = default)
    {
        var userId = AuthSession.CurrentUser?.UserId
            ?? throw new FirebaseAuthException("Sign in again before making a reservation.");
        if (startTime.ToUniversalTime() < DateTime.UtcNow.AddMinutes(-1))
        {
            throw new InvalidOperationException("Select a valid future reservation start time.");
        }
        if (!PaymentMethods.Contains(paymentMethod))
        {
            throw new InvalidOperationException("The selected payment method is invalid.");
        }

        if (string.IsNullOrWhiteSpace(clientRequestId) ||
            clientRequestId.Length > 128 ||
            clientRequestId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
        {
            throw new InvalidOperationException("The reservation request ID is invalid.");
        }

        try
        {
            return await _functions.CallAsync<CreateReservationResponse>("createReservation", new
            {
                locationId,
                slotId,
                startTime = startTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                paymentMethod,
                clientRequestId
            }, cancellationToken);
        }
        catch (FirebaseFunctionNotDeployedException)
        {
            // Preserve the existing atomic Firestore booking flow until Functions are deployed.
        }

        var location = await _firestore.GetDocumentAsync("locations", locationId, cancellationToken)
            ?? throw new InvalidOperationException("The selected parking location does not exist.");
        if (location["isActive"]?.GetValue<bool>() == false)
        {
            throw new InvalidOperationException("This parking location is inactive and is not accepting new reservations.");
        }
        var slot = await _firestore.GetDocumentAsync("slots", slotId, cancellationToken)
            ?? throw new InvalidOperationException("The selected parking slot does not exist.");
        if (ReadString(slot, "locationId") != locationId)
        {
            throw new InvalidOperationException("The selected parking slot does not belong to this location.");
        }
        if (ReadString(slot, "status") != "Available")
        {
            throw new InvalidOperationException("This parking slot is no longer available. Refresh and choose another.");
        }

        var fee = ReadDecimal(location, "feePerHour");
        if (fee < 0)
        {
            throw new InvalidOperationException("The selected parking location has an invalid hourly fee.");
        }
        var reservationId = clientRequestId;
        var paymentId = $"payment-{clientRequestId}";
        var existingReservation = (await _firestore.GetCollectionAsync(
            "reservations",
            "userId",
            userId,
            limit: 1000,
            cancellationToken: cancellationToken))
            .FirstOrDefault(item => ReadString(item, "id") == reservationId);
        if (existingReservation is not null)
        {
            if (ReadString(existingReservation, "userId") != userId ||
                ReadString(existingReservation, "locationId") != locationId ||
                ReadString(existingReservation, "slotId") != slotId)
            {
                throw new InvalidOperationException("This reservation request ID is already in use.");
            }

            var existingPaymentId = ReadString(existingReservation, "paymentId");
            var existingPayment = string.IsNullOrWhiteSpace(existingPaymentId)
                ? null
                : await _firestore.GetDocumentAsync("payments", existingPaymentId, cancellationToken);
            return new CreateReservationResponse
            {
                ReservationId = reservationId,
                PaymentId = existingPaymentId,
                LocationName = ReadString(existingReservation, "locationName"),
                SlotLabel = ReadString(existingReservation, "slotLabel"),
                Fee = ReadDecimal(existingReservation, "fee"),
                EndTime = ReadDate(existingReservation, "endTime") ?? startTime.ToUniversalTime().AddHours(1),
                ReceiptNumber = ReadString(existingPayment, "receiptNumber"),
                Status = ReadString(existingReservation, "status")
            };
        }

        var now = DateTime.UtcNow;
        var startUtc = startTime.ToUniversalTime();
        var endTime = startUtc.AddHours(1);
        var locationName = ReadString(location, "name");
        var slotLabel = ReadString(slot, "label");
        var receiptNumber = $"QP-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{reservationId[..6]}";

        await _firestore.CommitAsync(
        [
            _firestore.BuildUpdateWrite("slots", slotId, new Dictionary<string, object?>
            {
                ["status"] = "Reserved",
                ["activeReservationId"] = reservationId,
                ["updatedAt"] = now
            }, updateTime: ReadString(slot, "__updateTime")),
            _firestore.BuildUpdateWrite("reservations", reservationId, new
            {
                userId,
                locationId,
                locationName,
                slotId,
                slotLabel,
                startTime = startUtc,
                endTime,
                durationHours = 1,
                fee,
                status = "Pending",
                paymentId,
                createdAt = now
            }, createOnly: true),
            _firestore.BuildUpdateWrite("payments", paymentId, new
            {
                reservationId,
                userId,
                amount = fee,
                currency = "PHP",
                method = paymentMethod,
                status = "Not processed",
                receiptNumber,
                createdAt = now
            }, createOnly: true),
            _firestore.CreateNotificationWrite(
                userId,
                "Reservation request received",
                $"{locationName} · Slot {slotLabel} · {startUtc.ToLocalTime():MMM d, yyyy h:mm tt}. Your booking is pending admin approval.",
                "Booking",
                now)
        ], cancellationToken);

        return new CreateReservationResponse
        {
            ReservationId = reservationId,
            PaymentId = paymentId,
            LocationName = locationName,
            SlotLabel = slotLabel,
            Fee = fee,
            EndTime = endTime,
            ReceiptNumber = receiptNumber
        };
    }

    public async Task CancelReservationAsync(
        string reservationId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId("cancel a reservation");
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            OfflineDataStore.QueueCancellation(userId, reservationId);
            return;
        }

        try
        {
            await CancelReservationOnlineAsync(reservationId, cancellationToken);
            OfflineDataStore.CompleteQueuedCancellation(userId, reservationId);
        }
        catch (HttpRequestException)
        {
            OfflineDataStore.QueueCancellation(userId, reservationId);
        }
        catch (TaskCanceledException)
        {
            OfflineDataStore.QueueCancellation(userId, reservationId);
        }
    }

    private async Task CancelReservationOnlineAsync(
        string reservationId,
        CancellationToken cancellationToken = default)
    {
        var userId = AuthSession.CurrentUser?.UserId
            ?? throw new FirebaseAuthException("Sign in again before cancelling a reservation.");
        try
        {
            await _functions.CallAsync<JsonElement>("cancelReservation", new
            {
                reservationId
            }, cancellationToken);
            return;
        }
        catch (FirebaseFunctionNotDeployedException)
        {
            // Preserve the existing Firestore cancellation flow until Functions are deployed.
        }

        var reservation = await _firestore.GetDocumentAsync("reservations", reservationId, cancellationToken)
            ?? throw new InvalidOperationException("Reservation not found.");
        if (ReadString(reservation, "userId") != userId)
        {
            throw new InvalidOperationException("You cannot cancel another user's reservation.");
        }
        if (ReadString(reservation, "status") == "Cancelled")
        {
            return;
        }
        if (ReadString(reservation, "status") is not ("Pending" or "Confirmed"))
        {
            throw new InvalidOperationException("This reservation is no longer active.");
        }
        if (ReadDate(reservation, "startTime") <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("A reservation cannot be cancelled after its start time.");
        }

        var slotId = ReadString(reservation, "slotId");
        var slot = await _firestore.GetDocumentAsync("slots", slotId, cancellationToken)
            ?? throw new InvalidOperationException("The reservation is missing its parking slot.");
        var slotStatus = ReadString(slot, "status");
        var activeReservationId = ReadString(slot, "activeReservationId");
        if (slotStatus == "Reserved" && activeReservationId != reservationId)
        {
            throw new InvalidOperationException(
                "This parking slot is linked to a different active reservation. Refresh the booking status before trying again.");
        }
        var paymentId = ReadString(reservation, "paymentId");
        var payment = string.IsNullOrWhiteSpace(paymentId)
            ? null
            : await _firestore.GetDocumentAsync("payments", paymentId, cancellationToken);
        var now = DateTime.UtcNow;
        var writes = new List<JsonObject>
        {
            _firestore.BuildUpdateWrite("reservations", reservationId, new
            {
                status = "Cancelled",
                cancelledAt = now
            }, updateTime: ReadString(reservation, "__updateTime")),
        };
        if (slotStatus == "Reserved" && activeReservationId == reservationId)
        {
            writes.Add(_firestore.BuildUpdateWrite("slots", slotId,
                new { status = "Available", updatedAt = now },
                updateTime: ReadString(slot, "__updateTime"),
                additionalMaskFields: ["activeReservationId"]));
        }
        if (payment is not null)
        {
            writes.Add(_firestore.BuildUpdateWrite("payments", paymentId, new
            {
                status = "Cancelled — not processed",
                updatedAt = now
            }, updateTime: ReadString(payment, "__updateTime")));
        }
        writes.Add(_firestore.CreateNotificationWrite(
            userId,
            "Reservation cancelled",
            $"{ReadString(reservation, "locationName")} · Slot {ReadString(reservation, "slotLabel")} · {ReadDate(reservation, "startTime")?.ToLocalTime():MMM d, yyyy h:mm tt} was cancelled.",
            "Booking",
            now));
        await _firestore.CommitAsync(writes, cancellationToken);
    }

    private static string ReadString(JsonObject? document, string propertyName)
    {
        return document?[propertyName]?.GetValue<string>() ?? string.Empty;
    }

    private static string GetCurrentUserId(string action) =>
        AuthSession.CurrentUser?.UserId
        ?? throw new FirebaseAuthException($"Sign in again before you {action}.");

    private static BookingOptionsResponse GetCachedOptionsOrThrow(string userId, DateTime startTime) =>
        OfflineDataStore.GetCachedOptions(userId, startTime)
        ?? throw new InvalidOperationException("No saved parking information is available yet. Connect to the internet and refresh parking once to use it offline.");

    private static CreateReservationResponse CreateQueuedResponse(StoredReservation reservation) => new()
    {
        ReservationId = reservation.Id,
        LocationName = reservation.LocationName,
        SlotLabel = reservation.SlotLabel,
        Fee = reservation.Fee,
        EndTime = reservation.StartTime.AddHours(1),
        ReceiptNumber = reservation.ReceiptNumber,
        Status = reservation.Status
    };

    private static decimal ReadDecimal(JsonObject document, string propertyName)
    {
        return decimal.TryParse(document[propertyName]?.ToJsonString(), NumberStyles.Number,
            CultureInfo.InvariantCulture, out var value)
                ? value
                : -1;
    }

    private static DateTime? ReadDate(JsonObject document, string propertyName)
    {
        return DateTime.TryParse(ReadString(document, propertyName), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
                ? value
                : null;
    }
}

internal sealed class BookingOptionsResponse
{
    public BookingOptionsResponse()
    {
    }

    public List<AdminLocation> Locations { get; set; } = [];
    public List<AdminSlot> Slots { get; set; } = [];
}

internal sealed class CreateReservationResponse
{
    public string ReservationId { get; set; } = string.Empty;
    public string PaymentId { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public string SlotLabel { get; set; } = string.Empty;
    public decimal Fee { get; set; }
    public DateTime EndTime { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
}

internal sealed class MyBookingResponse
{
    public MyBookingDetails? Reservation { get; set; }
}

internal sealed class MyBookingDetails
{
    public string Id { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public string SlotLabel { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public decimal Fee { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public string ReceiptNumber { get; set; } = string.Empty;
    public DateTime? EndTime { get; set; }
}
