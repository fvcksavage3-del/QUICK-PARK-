using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace QuickPark;

internal static class CustomerSupportService
{
    private const string SupportEmail = "fvcksavage3@gmail.com";

    public static async Task ContactAsync(ContentPage page, string accountEmail)
    {
        var subject = "QuickPark customer support";
        var body = $"Hello QuickPark Support,\n\nI need help with:\n\n\nAccount email: {accountEmail}";
        try
        {
            await Email.Default.ComposeAsync(new EmailMessage
            {
                Subject = subject,
                Body = body,
                To = [SupportEmail]
            });
            return;
        }
        catch (FeatureNotSupportedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        if (await TryOpenMailLinkAsync(subject, body))
        {
            return;
        }

        var copyAddress = await page.DisplayAlertAsync(
            "Email app unavailable",
            $"QuickPark could not open an email app. Copy the support address to contact us another way?\n\n{SupportEmail}",
            "Copy email",
            "Close");
        if (!copyAddress)
        {
            return;
        }

        try
        {
            await Clipboard.Default.SetTextAsync(SupportEmail);
            await page.DisplayAlertAsync("Email copied", "The support email address is on your clipboard.", "OK");
        }
        catch (FeatureNotSupportedException)
        {
            await page.DisplayAlertAsync(
                "Contact QuickPark Support",
                $"Email: {SupportEmail}",
                "OK");
        }
        catch (InvalidOperationException)
        {
            await page.DisplayAlertAsync(
                "Contact QuickPark Support",
                $"Email: {SupportEmail}",
                "OK");
        }
    }

    private static async Task<bool> TryOpenMailLinkAsync(string subject, string body)
    {
        var mailto = new Uri(
            $"mailto:{SupportEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}");
        try
        {
            return await Launcher.Default.OpenAsync(mailto);
        }
        catch (FeatureNotSupportedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
