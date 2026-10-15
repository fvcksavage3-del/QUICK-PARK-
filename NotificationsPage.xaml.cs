using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Networking;

namespace QuickPark;

public partial class NotificationsPage : ContentPage
{
    private bool _isPageActive;
    private bool _isLoading;
    private readonly NotificationsApiClient _notificationsApi = new();

    public NotificationsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _isPageActive = true;
        _ = LoadNotificationsAsync();
        Dispatcher.StartTimer(TimeSpan.FromMinutes(1), () =>
        {
            if (!_isPageActive)
            {
                return false;
            }

            _ = LoadNotificationsAsync();
            return true;
        });
    }

    protected override void OnDisappearing()
    {
        _isPageActive = false;
        base.OnDisappearing();
    }

    private async void BackButton_Clicked(object? sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }

    private async Task LoadNotificationsAsync()
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        NotificationList.Children.Clear();
        RemoteStatusLabel.IsVisible = false;
        try
        {
            var notifications = (await _notificationsApi.GetMyNotificationsAsync()).Notifications;
            foreach (var notification in notifications)
            {
                AddNotification(notification);
            }

            if (notifications.Count == 0)
            {
                NotificationList.Children.Add(new Label
                {
                    Text = Connectivity.Current.NetworkAccess == NetworkAccess.Internet
                        ? "No notifications yet. Booking requests, decisions, cancellations, and announcements will appear here."
                        : "Notifications need an internet connection. Saved parking and bookings remain available offline.",
                    FontSize = 14,
                    TextColor = Color.FromArgb("#7B8490"),
                    HorizontalTextAlignment = TextAlignment.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(8, 36)
                });
            }
        }
        catch (InvalidOperationException exception)
        {
            RemoteStatusLabel.Text = exception.Message;
            RemoteStatusLabel.IsVisible = true;
        }
        catch (FirebaseAuthException exception)
        {
            RemoteStatusLabel.Text = exception.Message;
            RemoteStatusLabel.IsVisible = true;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            RemoteStatusLabel.Text = "Could not load account notifications. Check your connection and try again.";
            RemoteStatusLabel.IsVisible = true;
        }
        catch (TaskCanceledException)
        {
            RemoteStatusLabel.Text = "Loading notifications timed out. Please try again.";
            RemoteStatusLabel.IsVisible = true;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private static string GetString(System.Text.Json.JsonElement document, string propertyName)
    {
        return document.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    private async Task MarkAsReadAsync(string notificationId, Button button)
    {
        button.IsEnabled = false;
        try
        {
            await _notificationsApi.MarkAsReadAsync(notificationId);
            await LoadNotificationsAsync();
        }
        catch (InvalidOperationException exception)
        {
            RemoteStatusLabel.Text = exception.Message;
            RemoteStatusLabel.IsVisible = true;
        }
        catch (FirebaseAuthException exception)
        {
            RemoteStatusLabel.Text = exception.Message;
            RemoteStatusLabel.IsVisible = true;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            RemoteStatusLabel.Text = "Could not update this notification. Check your connection and try again.";
            RemoteStatusLabel.IsVisible = true;
        }
        catch (TaskCanceledException)
        {
            RemoteStatusLabel.Text = "Updating this notification timed out. Try again.";
            RemoteStatusLabel.IsVisible = true;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void AddNotification(System.Text.Json.JsonElement notification)
    {
        var title = GetString(notification, "title");
        var message = GetString(notification, "message");
        var notificationId = GetString(notification, "id");
        var isRead = notification.TryGetProperty("read", out var readValue) &&
            readValue.ValueKind == System.Text.Json.JsonValueKind.True;
        var createdAt = GetString(notification, "createdAt");
        var time = DateTime.TryParse(
            createdAt,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var timestamp)
                ? timestamp.ToLocalTime().ToString("MMM d, yyyy h:mm tt")
                : "Recent update";

        var card = new Border
        {
            BackgroundColor = isRead ? Colors.White : Color.FromArgb("#F3F8FF"),
            Stroke = isRead ? Colors.Transparent : Color.FromArgb("#B8D4F5"),
            StrokeThickness = isRead ? 0 : 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(18) },
            Padding = new Thickness(16)
        };

        var content = new VerticalStackLayout { Spacing = 8 };
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };
        header.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(title) ? "QuickPark update" : title,
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#151B24"),
            VerticalOptions = LayoutOptions.Center
        });
        if (!isRead)
        {
            header.Add(new Border
            {
                BackgroundColor = Color.FromArgb("#EAF3FF"),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
                Padding = new Thickness(8, 4),
                Content = new Label
                {
                    Text = "UNREAD",
                    FontSize = 9,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#1769D2")
                }
            }, 1);
        }
        content.Children.Add(header);
        content.Children.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(message) ? "Open QuickPark for booking details." : message,
            FontSize = 12,
            TextColor = Color.FromArgb("#52606D")
        });
        content.Children.Add(new Label
        {
            Text = time,
            FontSize = 10,
            TextColor = Color.FromArgb("#929BA6")
        });
        if (!isRead && !string.IsNullOrWhiteSpace(notificationId))
        {
            var markReadButton = new Button
            {
                Text = "Mark as read",
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#1769D2"),
                BackgroundColor = Color.FromArgb("#EAF3FF"),
                HeightRequest = 38,
                CornerRadius = 12,
                AutomationId = $"MarkNotificationRead_{notificationId}"
            };
            markReadButton.Clicked += async (_, _) =>
                await MarkAsReadAsync(notificationId, markReadButton);
            content.Children.Add(markReadButton);
        }
        card.Content = content;
        NotificationList.Children.Add(card);
    }
}
