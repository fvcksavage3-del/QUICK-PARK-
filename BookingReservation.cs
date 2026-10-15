namespace QuickPark;

internal sealed record BookingReservation(
    string ReservationId,
    string LocationId,
    string SlotId,
    string Location,
    string Slot,
    DateTime StartTime,
    string Fee,
    string Status,
    string PaymentMethod,
    string PaymentStatus,
    string ReceiptNumber,
    DateTime ConfirmedAt)
{
    public DateTime EndTime => StartTime.AddHours(1);
}

internal static class BookingReservationStore
{
    public static BookingReservation? Current { get; private set; }

    public static void Save(BookingReservation reservation)
    {
        Current = reservation;
    }

    public static void Cancel()
    {
        Current = null;
    }
}
