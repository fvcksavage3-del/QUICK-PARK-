using System.Text.Json;
using Microsoft.Maui.Networking;

namespace QuickPark;

internal sealed class NotificationsApiClient
{
    private readonly FirestoreRestClient _firestore = new();
    private readonly FirebaseFunctionsApiClient _functions = new();

    public async Task<UserNotificationsResponse> GetMyNotificationsAsync(
        CancellationToken cancellationToken = default)
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            return new UserNotificationsResponse();
        }

        var userId = AuthSession.CurrentUser?.UserId
            ?? throw new FirebaseAuthException("Sign in again before viewing notifications.");
        try
        {
            return await _functions.CallAsync<UserNotificationsResponse>(
                "myNotifications",
                new { },
                cancellationToken);
        }
        catch (FirebaseFunctionNotDeployedException)
        {
            var results = await Task.WhenAll(
                _firestore.GetCollectionAsync("notifications", "userId", userId, 100, cancellationToken,
                    orderByField: "createdAt", descendingOrder: true),
                _firestore.GetCollectionAsync("notifications", "userId", "*", 100, cancellationToken,
                    orderByField: "createdAt", descendingOrder: true));
            return new UserNotificationsResponse
            {
                Notifications = results
                    .SelectMany(items => items)
                    .DistinctBy(item => item["id"]?.GetValue<string>())
                    .OrderByDescending(item => ReadCreatedAt(item))
                    .Take(100)
                    .Select(item => SetEffectiveReadState(item, userId))
                    .Select(FirestoreRestClient.ToJsonElement)
                    .ToList()
            };
        }
    }

    public async Task MarkAsReadAsync(
        string notificationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(notificationId))
        {
            throw new InvalidOperationException("This notification is missing its ID.");
        }

        try
        {
            await _functions.CallAsync<JsonElement>(
                "markNotificationRead",
                new { notificationId },
                cancellationToken);
        }
        catch (FirebaseFunctionNotDeployedException)
        {
            var userId = AuthSession.CurrentUser?.UserId
                ?? throw new FirebaseAuthException("Sign in again before updating notifications.");
            var notification = await _firestore.GetDocumentAsync(
                "notifications",
                notificationId,
                cancellationToken)
                ?? throw new InvalidOperationException("This notification no longer exists.");
            if (notification["userId"]?.GetValue<string>() != userId)
            {
                throw new InvalidOperationException(
                    "Marking shared announcements as read requires the deployed QuickPark notifications service.");
            }

            await _firestore.CommitAsync(
            [
                _firestore.BuildUpdateWrite(
                    "notifications",
                    notificationId,
                    new { read = true },
                    updateTime: notification["__updateTime"]?.GetValue<string>())
            ], cancellationToken);
        }
    }

    private static System.Text.Json.Nodes.JsonObject SetEffectiveReadState(
        System.Text.Json.Nodes.JsonObject notification,
        string userId)
    {
        var isShared = notification["userId"]?.GetValue<string>() == "*";
        var isRead = isShared
            ? notification["readBy"]?[userId]?.GetValue<bool>() == true
            : notification["read"]?.GetValue<bool>() == true;
        notification["read"] = isRead;
        return notification;
    }

    private static DateTime ReadCreatedAt(System.Text.Json.Nodes.JsonObject notification) =>
        DateTime.TryParse(
            notification["createdAt"]?.GetValue<string>(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var createdAt)
                ? createdAt
                : DateTime.MinValue;
}

internal sealed class UserNotificationsResponse
{
    public List<JsonElement> Notifications { get; set; } = [];
}
