using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace QuickPark;

internal static class OfflineDataStore
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static BookingOptionsResponse? GetCachedOptions(string userId, DateTime requestedStartTime)
    {
        var options = ReadSnapshot(userId).Options;
        if (options is null)
        {
            return null;
        }

        var endTime = requestedStartTime.AddHours(1);
        var reservedSlotIds = ReadSnapshot(userId).Reservations
            .Where(item => item.Status is "Pending" or "Confirmed" or "Pending sync" or "Cancellation pending sync")
            .Where(item => item.StartTime < endTime && item.StartTime.AddHours(1) > requestedStartTime)
            .Select(item => item.SlotId)
            .ToHashSet(StringComparer.Ordinal);
        return new BookingOptionsResponse
        {
            Locations = options.Locations,
            Slots = options.Slots.Where(slot => !reservedSlotIds.Contains(slot.Id)).ToList()
        };
    }

    public static AdminLocation? GetCachedLocation(string userId, string locationId) =>
        ReadSnapshot(userId).Options?.Locations.FirstOrDefault(location => location.Id == locationId);

    public static void CacheOptions(string userId, BookingOptionsResponse options) =>
        UpdateSnapshot(userId, snapshot => snapshot with { Options = options });

    public static void CacheLocation(string userId, AdminLocation location) =>
        UpdateSnapshot(userId, snapshot =>
        {
            var options = snapshot.Options ?? new BookingOptionsResponse();
            var locations = options.Locations.Where(item => item.Id != location.Id).Append(location).ToList();
            return snapshot with { Options = new BookingOptionsResponse { Locations = locations, Slots = options.Slots } };
        });

    public static OfflineProfileUpdate? GetPendingProfileUpdate(string userId) =>
        ReadSnapshot(userId).PendingProfileUpdate;

    public static void QueueProfileUpdate(string userId, string displayName, string phoneNumber) =>
        UpdateSnapshot(userId, snapshot => snapshot with
        {
            PendingProfileUpdate = new OfflineProfileUpdate
            {
                DisplayName = displayName,
                PhoneNumber = phoneNumber
            }
        });

    public static void ClearPendingProfileUpdate(string userId) =>
        UpdateSnapshot(userId, snapshot => snapshot with { PendingProfileUpdate = null });

    public static void CacheRemoteHistory(string userId, IEnumerable<MyBookingDetails> reservations) =>
        UpdateSnapshot(userId, snapshot =>
        {
            var localChanges = snapshot.Reservations.Where(item => item.SyncAction.Length > 0).ToList();
            var remoteReservations = reservations
                .Select(StoredReservation.FromRemote)
                .Where(remote => localChanges.All(local => local.Id != remote.Id))
                .ToList();
            return snapshot with { Reservations = remoteReservations.Concat(localChanges).ToList() };
        });

    public static void CacheRemoteBooking(string userId, MyBookingDetails reservation) =>
        UpdateSnapshot(userId, snapshot => Upsert(snapshot, StoredReservation.FromRemote(reservation)));

    public static MyBookingDetails? GetCurrentBooking(string userId) => ReadSnapshot(userId).Reservations
        .Where(item => item.Status is "Pending" or "Confirmed" or "Pending sync" or "Cancellation pending sync")
        .OrderByDescending(item => item.CreatedAt)
        .Select(item => item.ToBookingDetails())
        .FirstOrDefault();

    public static List<MyBookingDetails> GetHistory(string userId) => ReadSnapshot(userId).Reservations
        .OrderByDescending(item => item.CreatedAt)
        .Select(item => item.ToBookingDetails())
        .ToList();

    public static StoredReservation QueueReservation(
        string userId,
        string locationId,
        string slotId,
        DateTime startTime,
        string paymentMethod,
        string? requestId = null)
    {
        var snapshot = ReadSnapshot(userId);
        var location = snapshot.Options?.Locations.FirstOrDefault(item => item.Id == locationId)
            ?? throw new InvalidOperationException("No saved details are available for this parking location. Connect to the internet and refresh parking first.");
        var slot = snapshot.Options?.Slots.FirstOrDefault(item =>
            item.Id == slotId && item.LocationId == locationId && item.Status == "Available")
            ?? throw new InvalidOperationException("This slot is not in the saved availability. Connect to the internet and refresh parking before booking.");
        if (startTime <= DateTime.Now.AddMinutes(1))
        {
            throw new InvalidOperationException("Select a valid future reservation start time.");
        }

        var overlaps = snapshot.Reservations.Any(item =>
            item.SlotId == slot.Id &&
            item.Status is "Pending" or "Confirmed" or "Pending sync" or "Cancellation pending sync" &&
            item.StartTime < startTime.AddHours(1) &&
            item.StartTime.AddHours(1) > startTime);
        if (overlaps)
        {
            throw new InvalidOperationException("You already have a booking for this slot and time.");
        }

        var localId = requestId ?? $"local-{Guid.NewGuid():N}";
        var now = DateTime.Now;
        var reservation = new StoredReservation
        {
            Id = localId,
            LocationId = location.Id,
            SlotId = slot.Id,
            LocationName = location.Name,
            SlotLabel = slot.Label,
            StartTime = startTime,
            Fee = location.FeePerHour,
            Status = "Pending sync",
            PaymentMethod = paymentMethod,
            PaymentStatus = "Not processed",
            ReceiptNumber = $"LOCAL-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}",
            CreatedAt = now,
            SyncAction = "Create",
            SyncLocationId = location.Id,
            SyncSlotId = slot.Id,
            SyncPaymentMethod = paymentMethod
        };
        UpdateSnapshot(userId, current => current with
        {
            Reservations = [.. current.Reservations, reservation]
        });
        return reservation;
    }

    public static void QueueCancellation(string userId, string reservationId)
    {
        UpdateSnapshot(userId, snapshot =>
        {
            var index = snapshot.Reservations.FindIndex(item => item.Id == reservationId);
            if (index < 0)
            {
                throw new InvalidOperationException("This booking is not available in the saved booking list.");
            }

            var reservation = snapshot.Reservations[index];
            if (reservation.StartTime <= DateTime.Now ||
                reservation.Status is not ("Pending" or "Confirmed" or "Pending sync" or "Cancellation pending sync"))
            {
                throw new InvalidOperationException("This reservation can no longer be cancelled.");
            }

            var cancelledBeforeSync = reservation.SyncAction == "Create";
            snapshot.Reservations[index] = reservation with
            {
                Status = cancelledBeforeSync ? "Cancelled" : "Cancellation pending sync",
                SyncAction = cancelledBeforeSync ? string.Empty : "Cancel"
            };
            return snapshot;
        });
    }

    public static List<StoredReservation> GetPendingCreates(string userId) => ReadSnapshot(userId)
        .Reservations.Where(item => item.SyncAction == "Create").ToList();

    public static List<StoredReservation> GetPendingCancellations(string userId) => ReadSnapshot(userId)
        .Reservations.Where(item => item.SyncAction == "Cancel").ToList();

    public static void CompleteQueuedCreation(string userId, string localId, CreateReservationResponse response) =>
        UpdateSnapshot(userId, snapshot =>
        {
            var index = snapshot.Reservations.FindIndex(item => item.Id == localId);
            if (index < 0)
            {
                return snapshot;
            }

            var queued = snapshot.Reservations[index];
            snapshot.Reservations[index] = queued with
            {
                Id = response.ReservationId,
                Status = response.Status,
                ReceiptNumber = response.ReceiptNumber,
                SyncAction = string.Empty,
                SyncLocationId = string.Empty,
                SyncSlotId = string.Empty,
                SyncPaymentMethod = string.Empty
            };
            return snapshot;
        });

    public static void CompleteQueuedCancellation(string userId, string reservationId) =>
        UpdateSnapshot(userId, snapshot =>
        {
            var index = snapshot.Reservations.FindIndex(item => item.Id == reservationId);
            if (index >= 0)
            {
                snapshot.Reservations[index] = snapshot.Reservations[index] with
                {
                    Status = "Cancelled",
                    SyncAction = string.Empty
                };
            }

            return snapshot;
        });

    private static LocalSnapshot ReadSnapshot(string userId)
    {
        lock (SyncRoot)
        {
            try
            {
                var path = GetPath(userId);
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<LocalSnapshot>(File.ReadAllText(path), JsonOptions) ?? new LocalSnapshot()
                    : new LocalSnapshot();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine($"QuickPark could not read its local cache: {exception.Message}");
                return new LocalSnapshot();
            }
        }
    }

    private static void UpdateSnapshot(string userId, Func<LocalSnapshot, LocalSnapshot> update)
    {
        lock (SyncRoot)
        {
            var snapshot = update(ReadSnapshot(userId));
            try
            {
                var path = GetPath(userId);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("QuickPark could not save data on this device. Check available storage and try again.", exception);
            }
        }
    }

    private static LocalSnapshot Upsert(LocalSnapshot snapshot, StoredReservation reservation)
    {
        var index = snapshot.Reservations.FindIndex(item => item.Id == reservation.Id);
        if (index < 0)
        {
            snapshot.Reservations.Add(reservation);
        }
        else if (snapshot.Reservations[index].SyncAction.Length == 0)
        {
            snapshot.Reservations[index] = reservation;
        }

        return snapshot;
    }

    private static string GetPath(string userId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)));
        return Path.Combine(FileSystem.AppDataDirectory, "offline", $"{hash}.json");
    }

    private sealed record LocalSnapshot
    {
        public LocalSnapshot()
        {
        }

        public BookingOptionsResponse? Options { get; init; }
        public List<StoredReservation> Reservations { get; init; } = [];
        public OfflineProfileUpdate? PendingProfileUpdate { get; init; }
    }
}

internal sealed record OfflineProfileUpdate
{
    public OfflineProfileUpdate()
    {
    }

    public string DisplayName { get; init; } = string.Empty;
    public string PhoneNumber { get; init; } = string.Empty;
}

internal sealed record StoredReservation
{
    public StoredReservation()
    {
    }

    public string Id { get; init; } = string.Empty;
    public string LocationId { get; init; } = string.Empty;
    public string SlotId { get; init; } = string.Empty;
    public string LocationName { get; init; } = string.Empty;
    public string SlotLabel { get; init; } = string.Empty;
    public DateTime StartTime { get; init; }
    public decimal Fee { get; init; }
    public string Status { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = string.Empty;
    public string PaymentStatus { get; init; } = string.Empty;
    public string ReceiptNumber { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string SyncAction { get; init; } = string.Empty;
    public string SyncLocationId { get; init; } = string.Empty;
    public string SyncSlotId { get; init; } = string.Empty;
    public string SyncPaymentMethod { get; init; } = string.Empty;

    public MyBookingDetails ToBookingDetails() => new()
    {
        Id = Id,
        LocationId = LocationId,
        SlotId = SlotId,
        LocationName = LocationName,
        SlotLabel = SlotLabel,
        StartTime = StartTime,
        Fee = Fee,
        Status = Status,
        PaymentMethod = PaymentMethod,
        PaymentStatus = PaymentStatus,
        ReceiptNumber = ReceiptNumber,
        EndTime = StartTime.AddHours(1)
    };

    public static StoredReservation FromRemote(MyBookingDetails details) => new()
    {
        Id = details.Id,
        LocationId = details.LocationId,
        SlotId = details.SlotId,
        LocationName = details.LocationName,
        SlotLabel = details.SlotLabel,
        StartTime = details.StartTime,
        Fee = details.Fee,
        Status = details.Status,
        PaymentMethod = details.PaymentMethod,
        PaymentStatus = details.PaymentStatus,
        ReceiptNumber = details.ReceiptNumber,
        CreatedAt = details.StartTime
    };
}