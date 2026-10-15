using System.Net.Http;

namespace QuickPark;

public partial class AdminLoginPage : ContentPage
{
    private const string RememberedEmailPreference = "QuickPark.RememberedAdminEmail";
    private readonly FirebaseAuthService _authService = new();
    private readonly AuthApiClient _authApiClient = new();
    private bool _isBusy;

    public AdminLoginPage()
    {
        InitializeComponent();
        var rememberedEmail = Preferences.Default.Get(RememberedEmailPreference, string.Empty);
        if (!string.IsNullOrWhiteSpace(rememberedEmail))
        {
            EmailEntry.Text = rememberedEmail;
            RememberAdminEmailCheckBox.IsChecked = true;
        }
    }

    private async void BackButton_Clicked(object? sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }

    private async void LoginButton_Clicked(object? sender, EventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var email = EmailEntry.Text?.Trim() ?? string.Empty;
        var password = PasswordEntry.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ShowStatus("Enter your admin email and password.", isError: true);
            return;
        }

        SetBusy(true);
        try
        {
            var user = await _authService.SignInAsync(email, password);
            AuthSession.SignIn(user);
            var role = await _authApiClient.GetAccountRoleAsync();

            if (role.Role != "admin")
            {
                AuthSession.SignOut();
                ShowStatus("This account does not have administrator access.", isError: true);
                return;
            }

            var window = Application.Current?.Windows.FirstOrDefault()
                ?? throw new InvalidOperationException("The QuickPark window is unavailable. Restart the app and try again.");
            var adminPage = new AdminPage();
            if (RememberAdminEmailCheckBox.IsChecked)
            {
                Preferences.Default.Set(RememberedEmailPreference, user.Email);
            }
            else
            {
                Preferences.Default.Remove(RememberedEmailPreference);
            }

            PasswordEntry.Text = string.Empty;
            await Navigation.PopModalAsync(animated: false);
            window.Page = adminPage;
        }
        catch (FirebaseAuthException exception)
        {
            AuthSession.SignOut();
            ShowStatus(exception.Message, isError: true);
        }
        catch (InvalidOperationException exception)
        {
            AuthSession.SignOut();
            ShowStatus(exception.Message, isError: true);
        }
        catch (HttpRequestException)
        {
            AuthSession.SignOut();
            ShowStatus("Could not connect to Firebase. Check your connection and try again.", isError: true);
        }
        catch (TaskCanceledException)
        {
            AuthSession.SignOut();
            ShowStatus("The Firebase request timed out. Please try again.", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowPasswordButton_Clicked(object? sender, EventArgs e)
    {
        PasswordEntry.IsPassword = !PasswordEntry.IsPassword;
        ShowPasswordButton.Text = PasswordEntry.IsPassword ? "◉" : "◌";
    }

    private async void ForgotPasswordButton_Clicked(object? sender, EventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var email = EmailEntry.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
        {
            email = await DisplayPromptAsync(
                "Reset password",
                "Enter the email address registered to your administrator account.",
                "Send",
                "Cancel",
                "Email address",
                keyboard: Keyboard.Email) ?? string.Empty;
            email = email.Trim();
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _authService.SendPasswordResetAsync(email);
            ShowStatus("If this email is registered, Firebase has sent a password reset link.", isError: false);
        }
        catch (FirebaseAuthException exception)
        {
            ShowStatus(exception.Message, isError: true);
        }
        catch (HttpRequestException)
        {
            ShowStatus("Could not connect to Firebase. Check your connection and try again.", isError: true);
        }
        catch (TaskCanceledException)
        {
            ShowStatus("The Firebase request timed out. Please try again.", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        EmailEntry.IsEnabled = !isBusy;
        PasswordEntry.IsEnabled = !isBusy;
        ShowPasswordButton.IsEnabled = !isBusy;
        RememberAdminEmailCheckBox.IsEnabled = !isBusy;
        ForgotPasswordButton.IsEnabled = !isBusy;
        LoginButton.IsEnabled = !isBusy;
        LoadingIndicator.IsVisible = isBusy;
        LoadingIndicator.IsRunning = isBusy;
        if (isBusy)
        {
            StatusLabel.IsVisible = false;
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusLabel.Text = message;
        StatusLabel.TextColor = isError ? Color.FromArgb("#B42336") : Color.FromArgb("#15803D");
        StatusLabel.IsVisible = true;
    }
}
