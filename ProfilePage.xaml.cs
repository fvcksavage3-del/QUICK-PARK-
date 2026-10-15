using System.Net.Http;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Maui.Networking;

namespace QuickPark;

public partial class ProfilePage : ContentPage
{
    private const string DisplayNameKeyPrefix = "QuickPark.DisplayName.";
    private const string PhoneNumberKeyPrefix = "QuickPark.Profile.Phone.";
    private const string PhoneCountryKeyPrefix = "QuickPark.PhoneCountry.";
    private static readonly DialingCountry[] DialingCountries =
    [
        new("🇵🇭", "Philippines", "63"),
        new("🇺🇸", "United States", "1"),
        new("🇨🇦", "Canada", "1"),
        new("🇬🇧", "United Kingdom", "44"),
        new("🇦🇺", "Australia", "61"),
        new("🇳🇿", "New Zealand", "64"),
        new("🇸🇬", "Singapore", "65"),
        new("🇲🇾", "Malaysia", "60"),
        new("🇮🇩", "Indonesia", "62"),
        new("🇹🇭", "Thailand", "66"),
        new("🇻🇳", "Vietnam", "84"),
        new("🇯🇵", "Japan", "81"),
        new("🇰🇷", "South Korea", "82"),
        new("🇨🇳", "China", "86"),
        new("🇭🇰", "Hong Kong", "852"),
        new("🇮🇳", "India", "91"),
        new("🇵🇰", "Pakistan", "92"),
        new("🇧🇩", "Bangladesh", "880"),
        new("🇦🇪", "United Arab Emirates", "971"),
        new("🇸🇦", "Saudi Arabia", "966"),
        new("🇶🇦", "Qatar", "974"),
        new("🇰🇼", "Kuwait", "965"),
        new("🇧🇭", "Bahrain", "973"),
        new("🇴🇲", "Oman", "968"),
        new("🇿🇦", "South Africa", "27"),
        new("🇩🇪", "Germany", "49"),
        new("🇫🇷", "France", "33"),
        new("🇮🇹", "Italy", "39"),
        new("🇪🇸", "Spain", "34"),
        new("🇳🇱", "Netherlands", "31"),
        new("🇧🇪", "Belgium", "32"),
        new("🇨🇭", "Switzerland", "41"),
        new("🇸🇪", "Sweden", "46"),
        new("🇳🇴", "Norway", "47"),
        new("🇩🇰", "Denmark", "45"),
        new("🇫🇮", "Finland", "358"),
        new("🇮🇪", "Ireland", "353"),
        new("🇵🇱", "Poland", "48"),
        new("🇹🇷", "Türkiye", "90"),
        new("🇧🇷", "Brazil", "55"),
        new("🇲🇽", "Mexico", "52"),
        new("🇦🇷", "Argentina", "54"),
        new("🇨🇱", "Chile", "56"),
        new("🇪🇬", "Egypt", "20"),
        new("🇳🇬", "Nigeria", "234"),
        new("🇰🇪", "Kenya", "254"),
        new("🇮🇱", "Israel", "972"),
        new("🇷🇺", "Russia", "7")
    ];
    private static readonly HashSet<string> CountriesWithNationalTrunkZero =
    [
        "20", "27", "30", "31", "32", "33", "34", "36",
        "41", "43", "44", "45", "46", "47", "48", "49",
        "52", "54", "55", "56", "60", "61", "63", "64", "65",
        "66", "81", "82", "84", "86", "90", "91", "92",
        "234", "254", "353", "358", "880", "966", "971"
    ];
    private readonly FirebaseAuthService _authService = new();
    private readonly AuthApiClient _profileApi = new();
    private readonly BookingApiClient _bookingApi = new();
    private readonly string _email;
    private readonly string _displayNamePreferenceKey;
    private readonly string _phoneCountryPreferenceKey;
    private bool _isLoadingBookingSummary;
    private bool _isSavingProfile;

    public ProfilePage(string email)
    {
        InitializeComponent();
        _email = email;
        _displayNamePreferenceKey = $"{DisplayNameKeyPrefix}{email}";
        var userId = AuthSession.CurrentUser?.UserId ?? email;
        _phoneCountryPreferenceKey = $"{PhoneCountryKeyPrefix}{userId}";
        PhoneCountryPicker.ItemsSource = DialingCountries;
        PhoneCountryPicker.ItemDisplayBinding = new Binding(nameof(DialingCountry.DisplayName));
        SelectSavedPhoneCountry();
        var authenticatedEmail = AuthSession.CurrentUser?.Email;
        EmailLabel.Text = string.IsNullOrWhiteSpace(authenticatedEmail)
            ? string.IsNullOrWhiteSpace(email) ? "Email unavailable" : email
            : authenticatedEmail;
        DisplayNameLabel.Text = Preferences.Default.Get(
            _displayNamePreferenceKey,
            string.IsNullOrWhiteSpace(email) ? "QuickPark Driver" : email.Split('@')[0]);
        UpdateProfileInitial();
        LoadProfilePhoto();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadProfileAsync();
        await LoadBookingSummaryAsync();
    }

    private async Task LoadProfileAsync()
    {
        StatusLabel.IsVisible = false;
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            var cachedName = Preferences.Default.Get(
                _displayNamePreferenceKey,
                AuthSession.CurrentUser?.DisplayName ?? _email.Split('@')[0]);
            var cachedPhone = Preferences.Default.Get(
                $"{PhoneNumberKeyPrefix}{AuthSession.CurrentUser?.UserId}",
                AuthSession.CurrentUser?.PhoneNumber ?? string.Empty);
            DisplayNameLabel.Text = cachedName;
            DisplayNameEntry.Text = cachedName;
            EmailLabel.Text = _email;
            PhoneNumberLabel.Text = string.IsNullOrWhiteSpace(cachedPhone)
                ? "Not added"
                : FormatPhoneForDisplay(cachedPhone);
            SetPhoneNumberEditor(cachedPhone);
            UpdateProfileInitial();
            ShowProfileError("Showing profile information saved on this device.");
            return;
        }

        try
        {
            var account = await _authService.GetCurrentAccountAsync();
            var profile = await _profileApi.GetOwnProfileAsync(account.DisplayName);
            var displayName = string.IsNullOrWhiteSpace(profile.DisplayName)
                ? string.IsNullOrWhiteSpace(account.DisplayName)
                    ? Preferences.Default.Get(_displayNamePreferenceKey, account.Email.Split('@')[0])
                    : account.DisplayName
                : profile.DisplayName;
            var phoneNumber = profile.PhoneNumber;
            Preferences.Default.Set($"{PhoneNumberKeyPrefix}{AuthSession.CurrentUser?.UserId}", phoneNumber);
            DisplayNameLabel.Text = displayName;
            DisplayNameEntry.Text = displayName;
            EmailLabel.Text = account.Email;
            PhoneNumberLabel.Text = string.IsNullOrWhiteSpace(phoneNumber)
                ? "Not added"
                : FormatPhoneForDisplay(phoneNumber);
            SetPhoneNumberEditor(phoneNumber);
            Preferences.Default.Set(_displayNamePreferenceKey, displayName);
            UpdateProfileInitial();
        }
        catch (FirebaseAuthException exception)
        {
            ShowProfileError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            ShowProfileError(exception.Message);
        }
        catch (HttpRequestException)
        {
            ShowProfileError("Could not load your saved profile. Check your connection and try again.");
        }
        catch (TaskCanceledException)
        {
            ShowProfileError("Loading your saved profile timed out. Please try again.");
        }
    }

    private async Task LoadBookingSummaryAsync()
    {
        if (_isLoadingBookingSummary)
        {
            return;
        }

        _isLoadingBookingSummary = true;
        BookingSummaryStatusLabel.IsVisible = false;
        try
        {
            var reservations = await _bookingApi.GetMyReservationHistoryAsync();
            TotalBookingsLabel.Text = reservations.Count.ToString();
            ActiveBookingsLabel.Text = reservations.Count(reservation =>
                string.Equals(reservation.Status, "Pending", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservation.Status, "Confirmed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservation.Status, "Active", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservation.Status, "InProgress", StringComparison.OrdinalIgnoreCase))
                .ToString();
            CompletedBookingsLabel.Text = reservations.Count(reservation =>
                string.Equals(reservation.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                .ToString();
        }
        catch (FirebaseAuthException exception)
        {
            SetBookingSummaryUnavailable(exception.Message);
        }
        catch (HttpRequestException)
        {
            SetBookingSummaryUnavailable("Could not load your booking summary. Check your connection and try again.");
        }
        catch (TaskCanceledException)
        {
            SetBookingSummaryUnavailable("Loading your booking summary timed out. Please try again.");
        }
        finally
        {
            _isLoadingBookingSummary = false;
        }
    }

    private void SetBookingSummaryUnavailable(string message)
    {
        TotalBookingsLabel.Text = "—";
        ActiveBookingsLabel.Text = "—";
        CompletedBookingsLabel.Text = "—";
        BookingSummaryStatusLabel.Text = message;
        BookingSummaryStatusLabel.IsVisible = true;
    }

    private void ShowProfileError(string message)
    {
        StatusLabel.Text = message;
        StatusLabel.IsVisible = true;
    }

    private void SelectSavedPhoneCountry()
    {
        var savedCode = Preferences.Default.Get(_phoneCountryPreferenceKey, "63");
        var savedCountry = DialingCountries.FirstOrDefault(country =>
            string.Equals(country.DialCode, savedCode, StringComparison.Ordinal));
        PhoneCountryPicker.SelectedItem = savedCountry ?? DialingCountries[0];
    }

    private void PhoneCountryPicker_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (PhoneCountryPicker.SelectedItem is DialingCountry country)
        {
            Preferences.Default.Set(_phoneCountryPreferenceKey, country.DialCode);
        }
    }

    private void SetPhoneNumberEditor(string? e164Number)
    {
        if (string.IsNullOrWhiteSpace(e164Number))
        {
            SelectSavedPhoneCountry();
            PhoneNumberEntry.Text = string.Empty;
            return;
        }

        var digits = string.Concat(e164Number.Where(char.IsDigit));
        var matchingCountries = DialingCountries
            .Where(country => digits.StartsWith(country.DialCode, StringComparison.Ordinal))
            .OrderByDescending(country => country.DialCode.Length)
            .ToArray();
        if (matchingCountries.Length > 0)
        {
            var savedCode = Preferences.Default.Get(_phoneCountryPreferenceKey, string.Empty);
            var country = matchingCountries.FirstOrDefault(item =>
                string.Equals(item.DialCode, savedCode, StringComparison.Ordinal))
                ?? matchingCountries[0];
            PhoneCountryPicker.SelectedItem = country;
            PhoneNumberEntry.Text = digits[country.DialCode.Length..];
            return;
        }

        SelectSavedPhoneCountry();
        PhoneNumberEntry.Text = digits;
    }

    private static string FormatPhoneForDisplay(string phoneNumber)
    {
        var digits = string.Concat(phoneNumber.Where(char.IsDigit));
        var country = DialingCountries
            .Where(item => digits.StartsWith(item.DialCode, StringComparison.Ordinal))
            .OrderByDescending(item => item.DialCode.Length)
            .FirstOrDefault();
        return country is null
            ? phoneNumber
            : $"+{country.DialCode} {digits[country.DialCode.Length..]}";
    }

    private string? BuildE164PhoneNumber(out string validationMessage)
    {
        validationMessage = string.Empty;
        var country = PhoneCountryPicker.SelectedItem as DialingCountry;
        if (country is null)
        {
            validationMessage = "Select your country or region for the phone number.";
            return null;
        }

        var rawNumber = PhoneNumberEntry.Text?.Trim() ?? string.Empty;
        if (rawNumber.Length == 0)
        {
            return string.Empty;
        }

        if (rawNumber.Any(character =>
                !char.IsDigit(character) && character is not ('+' or '-' or '(' or ')' or ' ')))
        {
            validationMessage = "Enter a phone number using digits, spaces, parentheses, or hyphens.";
            return null;
        }

        var digits = string.Concat(rawNumber.Where(char.IsDigit));
        var isInternationalNumber = rawNumber.StartsWith('+') ||
            rawNumber.StartsWith("00", StringComparison.Ordinal);
        if (isInternationalNumber)
        {
            var internationalDigits = rawNumber.StartsWith("00", StringComparison.Ordinal)
                ? digits[2..]
                : digits;
            var selectedPrefix = country.DialCode;
            if (internationalDigits.StartsWith(selectedPrefix, StringComparison.Ordinal))
            {
                digits = internationalDigits[selectedPrefix.Length..];
            }
            else
            {
                var pastedCountry = DialingCountries
                    .Where(item => internationalDigits.StartsWith(item.DialCode, StringComparison.Ordinal))
                    .OrderByDescending(item => item.DialCode.Length)
                    .FirstOrDefault();
                if (pastedCountry is null)
                {
                    validationMessage = "The pasted international number has an unsupported country calling code.";
                    return null;
                }

                PhoneCountryPicker.SelectedItem = pastedCountry;
                country = pastedCountry;
                digits = internationalDigits[pastedCountry.DialCode.Length..];
            }
        }

        if (!isInternationalNumber && CountriesWithNationalTrunkZero.Contains(country.DialCode))
        {
            digits = digits.TrimStart('0');
        }
        var fullNumber = country.DialCode + digits;
        if (digits.Length == 0 || fullNumber.Length is < 7 or > 15)
        {
            validationMessage = "Enter a valid phone number for the selected country (7–15 digits including country code).";
            return null;
        }

        return $"+{fullNumber}";
    }

    private void UpdateProfileInitial()
    {
        var initial = ProfilePhotoStore.GetInitial(DisplayNameLabel.Text);
        ProfileInitialLabel.Text = initial;
        PersonalInfoInitialLabel.Text = initial;
    }

    private void LoadProfilePhoto()
    {
        var photoPath = ProfilePhotoStore.GetSavedPhotoPath();
        if (photoPath is null)
        {
            ProfilePhotoImage.IsVisible = false;
            return;
        }

        ProfilePhotoImage.Source = ImageSource.FromFile(photoPath);
        ProfilePhotoImage.IsVisible = true;
    }

    private async void ChangePhotoButton_Clicked(object? sender, EventArgs e)
    {
        ChangePhotoButton.IsEnabled = false;
        try
        {
            var photos = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions
            {
                Title = "Choose your QuickPark profile photo"
            });
            var photo = photos?.FirstOrDefault();
            if (photo is null)
            {
                return;
            }

            var photoPath = await ProfilePhotoStore.SaveAsync(photo);
            ProfilePhotoImage.Source = ImageSource.FromFile(photoPath);
            ProfilePhotoImage.IsVisible = true;
            ShowProfileError("Profile photo saved on this device.");
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlertAsync(
                "Photo selection unavailable",
                "This device does not support selecting a profile photo.",
                "OK");
        }
        catch (PermissionException)
        {
            await DisplayAlertAsync(
                "Photo access needed",
                "Allow QuickPark to access a photo you choose, then try again.",
                "OK");
        }
        catch (InvalidOperationException exception)
        {
            await DisplayAlertAsync("Could not use this photo", exception.Message, "OK");
        }
        catch (IOException)
        {
            await DisplayAlertAsync(
                "Could not save photo",
                "QuickPark could not save this photo on your device. Choose another photo and try again.",
                "OK");
        }
        catch (UnauthorizedAccessException)
        {
            await DisplayAlertAsync(
                "Photo storage unavailable",
                "QuickPark could not access its private photo storage. Please try again.",
                "OK");
        }
        finally
        {
            ChangePhotoButton.IsEnabled = true;
        }
    }

    private async void BackButton_Clicked(object? sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }

    private async void BookingHistoryButton_Clicked(object? sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new NavigationPage(new ReservationHistoryPage()));
    }

    private async void NotificationsButton_Clicked(object? sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new NavigationPage(new NotificationsPage()));
    }

    private async void SupportButton_Clicked(object? sender, EventArgs e)
    {
        await CustomerSupportService.ContactAsync(this, _email);
    }

    private void EditProfileButton_Clicked(object? sender, EventArgs e)
    {
        StatusLabel.IsVisible = false;
        DisplayNameEntry.Text = DisplayNameLabel.Text;
        SetPhoneNumberEditor(AuthSession.CurrentUser?.PhoneNumber);
        EmailAddressEntry.Text = EmailLabel.Text == "Email unavailable"
            ? string.Empty
            : EmailLabel.Text;
        EmailChangePasswordEntry.Text = string.Empty;
        UpdateEmailChangePrompt();
        EditProfileForm.IsVisible = true;
        DisplayNameEntry.Focus();
    }

    private void CancelEditProfileButton_Clicked(object? sender, EventArgs e)
    {
        DisplayNameEntry.Text = DisplayNameLabel.Text;
        SetPhoneNumberEditor(AuthSession.CurrentUser?.PhoneNumber);
        EmailAddressEntry.Text = EmailLabel.Text == "Email unavailable"
            ? string.Empty
            : EmailLabel.Text;
        EmailChangePasswordEntry.Text = string.Empty;
        UpdateEmailChangePrompt();
        EditProfileForm.IsVisible = false;
        StatusLabel.IsVisible = false;
    }

    private void EmailAddressEntry_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateEmailChangePrompt();
    }

    private void UpdateEmailChangePrompt()
    {
        var currentEmail = AuthSession.CurrentUser?.Email ?? EmailLabel.Text;
        var emailChanged = !string.Equals(
            EmailAddressEntry.Text?.Trim(),
            currentEmail,
            StringComparison.OrdinalIgnoreCase);
        EmailChangeHintLabel.IsVisible = emailChanged;
        EmailChangePasswordEntry.IsVisible = emailChanged;
        if (!emailChanged)
        {
            EmailChangePasswordEntry.Text = string.Empty;
        }
    }

    private void DisplayNameEntry_Completed(object? sender, EventArgs e)
    {
        SaveDisplayNameButton_Clicked(sender, e);
    }

    private async void SaveDisplayNameButton_Clicked(object? sender, EventArgs e)
    {
        if (_isSavingProfile)
        {
            return;
        }

        var updatedName = DisplayNameEntry.Text?.Trim() ?? string.Empty;
        var updatedEmail = EmailAddressEntry.Text?.Trim() ?? string.Empty;
        var currentEmail = AuthSession.CurrentUser?.Email ?? string.Empty;
        var emailChanged = !string.Equals(updatedEmail, currentEmail, StringComparison.OrdinalIgnoreCase);
        var currentPassword = EmailChangePasswordEntry.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(updatedName))
        {
            ShowProfileError("Enter your display name before saving.");
            DisplayNameEntry.Focus();
            return;
        }

        if (updatedName.Length > 60)
        {
            ShowProfileError("Your display name must be 60 characters or fewer.");
            return;
        }

        if (!MailAddress.TryCreate(updatedEmail, out var parsedEmail) ||
            !string.Equals(parsedEmail.Address, updatedEmail, StringComparison.OrdinalIgnoreCase))
        {
            ShowProfileError("Enter a valid email address.");
            EmailAddressEntry.Focus();
            return;
        }

        if (emailChanged && string.IsNullOrWhiteSpace(currentPassword))
        {
            ShowProfileError("Enter your current password to verify an email change.");
            EmailChangePasswordEntry.Focus();
            return;
        }

        var e164PhoneNumber = BuildE164PhoneNumber(out var phoneValidationMessage);
        if (e164PhoneNumber is null)
        {
            ShowProfileError(phoneValidationMessage);
            PhoneNumberEntry.Focus();
            return;
        }

        if (AuthSession.CurrentUser is null)
        {
            ShowProfileError("Your sign-in session expired. Sign in again before saving your profile.");
            return;
        }

        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            if (emailChanged)
            {
                ShowProfileError("Email changes require an internet connection.");
                return;
            }

            try
            {
                OfflineDataStore.QueueProfileUpdate(
                    AuthSession.CurrentUser.UserId,
                    updatedName,
                    e164PhoneNumber ?? string.Empty);
            }
            catch (InvalidOperationException exception)
            {
                ShowProfileError(exception.Message);
                return;
            }

            Preferences.Default.Set(_displayNamePreferenceKey, updatedName);
            Preferences.Default.Set($"{PhoneNumberKeyPrefix}{AuthSession.CurrentUser.UserId}", e164PhoneNumber);
            AuthSession.Update(AuthSession.CurrentUser with
            {
                DisplayName = updatedName,
                PhoneNumber = e164PhoneNumber ?? string.Empty
            });
            DisplayNameLabel.Text = updatedName;
            PhoneNumberLabel.Text = string.IsNullOrWhiteSpace(e164PhoneNumber)
                ? "Not added"
                : FormatPhoneForDisplay(e164PhoneNumber);
            UpdateProfileInitial();
            EditProfileForm.IsVisible = false;
            ShowProfileError("Profile saved on this device and will sync when internet returns.");
            return;
        }

        StatusLabel.IsVisible = false;
        _isSavingProfile = true;
        SaveDisplayNameButton.IsEnabled = false;
        EditProfileButton.IsEnabled = false;
        var emailVerificationSent = false;
        try
        {
            if (emailChanged)
            {
                await _authService.SendEmailChangeVerificationAsync(updatedEmail, currentPassword);
                emailVerificationSent = true;
            }

            await _profileApi.UpdateOwnProfileAsync(updatedName, phoneNumber: e164PhoneNumber);

            Preferences.Default.Set(_displayNamePreferenceKey, updatedName);
            Preferences.Default.Set($"{PhoneNumberKeyPrefix}{AuthSession.CurrentUser.UserId}", e164PhoneNumber);
            DisplayNameLabel.Text = updatedName;
            PhoneNumberLabel.Text = string.IsNullOrWhiteSpace(e164PhoneNumber)
                ? "Not added"
                : FormatPhoneForDisplay(e164PhoneNumber);
            SetPhoneNumberEditor(e164PhoneNumber);
            UpdateProfileInitial();

            string? authSyncWarning = null;
            try
            {
                await _authService.UpdateCurrentAccountProfileAsync(updatedName);
            }
            catch (FirebaseAuthException exception)
            {
                authSyncWarning = exception.Message;
            }
            catch (HttpRequestException exception)
            {
                authSyncWarning = exception.Message;
            }
            catch (TaskCanceledException exception)
            {
                authSyncWarning = exception.Message;
            }
            catch (InvalidOperationException exception)
            {
                authSyncWarning = exception.Message;
            }

            if (emailChanged)
            {
                EmailAddressEntry.Text = currentEmail;
                EmailChangePasswordEntry.Text = string.Empty;
                UpdateEmailChangePrompt();
                EditProfileForm.IsVisible = false;
                ShowProfileError(authSyncWarning is null
                    ? $"Profile saved. A verification link was sent to {updatedEmail}. Your sign-in email changes only after you verify it; then sign in again."
                    : $"Phone and profile were saved. A verification link was sent to {updatedEmail}, but the sign-in display name could not be synced. {authSyncWarning}");
                return;
            }

            EditProfileForm.IsVisible = false;
            ShowProfileError(authSyncWarning is null
                ? "Profile information saved to your QuickPark account."
                : $"Phone and profile were saved to QuickPark, but the sign-in display name could not be synced. {authSyncWarning}");
        }
        catch (FirebaseAuthException exception)
        {
            ShowProfileError(emailVerificationSent
                ? $"A verification link was sent to {updatedEmail}, but your profile could not be saved. {exception.Message}"
                : exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            ShowProfileError(emailVerificationSent
                ? $"A verification link was sent to {updatedEmail}, but your profile could not be saved. {exception.Message}"
                : exception.Message);
        }
        catch (HttpRequestException)
        {
            ShowProfileError(emailVerificationSent
                ? $"A verification link was sent to {updatedEmail}, but QuickPark could not save your profile. Check your connection and try again."
                : "Could not connect to QuickPark. Check your connection and try saving again.");
        }
        catch (TaskCanceledException)
        {
            ShowProfileError(emailVerificationSent
                ? $"A verification link was sent to {updatedEmail}, but saving your profile timed out. Check your connection and reload the profile."
                : "Saving your profile timed out. Your changes were not saved; please try again.");
        }
        finally
        {
            EmailChangePasswordEntry.Text = string.Empty;
            _isSavingProfile = false;
            SaveDisplayNameButton.IsEnabled = true;
            EditProfileButton.IsEnabled = true;
        }
    }

    private async void ChangePasswordButton_Clicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_email))
        {
            await DisplayAlertAsync("Password unavailable", "An email address is not available for this account.", "OK");
            return;
        }

        ChangePasswordButton.IsVisible = false;
        ChangePasswordForm.IsVisible = true;
        CurrentPasswordEntry.Focus();
    }

    private void CancelPasswordChangeButton_Clicked(object? sender, EventArgs e)
    {
        ClearPasswordFields();
        ChangePasswordForm.IsVisible = false;
        ChangePasswordButton.IsVisible = true;
    }

    private async void SavePasswordChangeButton_Clicked(object? sender, EventArgs e)
    {
        var currentPassword = CurrentPasswordEntry.Text ?? string.Empty;
        var newPassword = NewPasswordEntry.Text ?? string.Empty;
        var confirmation = ConfirmPasswordEntry.Text ?? string.Empty;
        if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword) || string.IsNullOrEmpty(confirmation))
        {
            await DisplayAlertAsync("Complete all fields", "Enter your current password and the new password twice.", "OK");
            return;
        }

        if (newPassword.Length < 6)
        {
            await DisplayAlertAsync("Password too short", "Choose a password with at least 6 characters.", "OK");
            return;
        }

        if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
        {
            await DisplayAlertAsync("Passwords don't match", "Re-enter the same new password in both fields.", "OK");
            return;
        }

        ChangePasswordButton.IsEnabled = false;
        try
        {
            await _authService.ChangePasswordAsync(_email, currentPassword, newPassword);
            StatusLabel.Text = "Password updated successfully.";
            StatusLabel.IsVisible = true;
            ClearPasswordFields();
            ChangePasswordForm.IsVisible = false;
            ChangePasswordButton.IsVisible = true;
        }
        catch (FirebaseAuthException exception)
        {
            await DisplayAlertAsync("Could not change password", exception.Message, "OK");
        }
        catch (HttpRequestException)
        {
            await DisplayAlertAsync(
                "Connection error",
                "Could not connect to Firebase. Check your internet connection and try again.",
                "OK");
        }
        catch (TaskCanceledException)
        {
            await DisplayAlertAsync("Request timed out", "The password update request timed out. Please try again.", "OK");
        }
        finally
        {
            ChangePasswordButton.IsEnabled = true;
        }
    }

    private void ClearPasswordFields()
    {
        CurrentPasswordEntry.Text = string.Empty;
        NewPasswordEntry.Text = string.Empty;
        ConfirmPasswordEntry.Text = string.Empty;
    }

    private async void LogoutButton_Clicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync(
            "Log out",
            "Are you sure you want to log out?",
            "Log out",
            "Stay");
        if (!confirmed)
        {
            return;
        }

        AuthSession.SignOut();
        Application.Current?.Windows.FirstOrDefault()?.Page = new MainPage();
    }

    private sealed record DialingCountry(string Flag, string Name, string DialCode)
    {
        public string DisplayName => $"{Flag}  {Name}  (+{DialCode})";
    }
}

internal static class ProfilePhotoStore
{
    private const string PreferenceKeyPrefix = "QuickPark.ProfilePhoto.";
    private const long MaximumPhotoSizeBytes = 8 * 1024 * 1024;

    public static string GetInitial(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? "Q"
            : name.Trim()[0].ToString().ToUpperInvariant();
    }

    public static string? GetSavedPhotoPath()
    {
        var userId = AuthSession.CurrentUser?.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var photoPath = Preferences.Default.Get(PreferenceKeyPrefix + userId, string.Empty);
        return !string.IsNullOrWhiteSpace(photoPath) && File.Exists(photoPath)
            ? photoPath
            : null;
    }

    public static async Task<string> SaveAsync(FileResult photo)
    {
        var userId = AuthSession.CurrentUser?.UserId
            ?? throw new InvalidOperationException("Sign in again before changing your profile photo.");

        var extension = Path.GetExtension(photo.FileName).ToLowerInvariant();
        if (extension is not ".jpg" and not ".jpeg" and not ".png")
        {
            extension = photo.ContentType?.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                _ => string.Empty
            };
        }

        if (extension.Length == 0)
        {
            throw new InvalidOperationException("Choose a JPG or PNG image.");
        }

        var userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)));
        var destinationPath = Path.Combine(FileSystem.AppDataDirectory, $"profile-{userHash}{extension}");
        var temporaryPath = destinationPath + ".tmp";

        try
        {
            await using var source = await photo.OpenReadAsync();
            if (source.CanSeek && source.Length > MaximumPhotoSizeBytes)
            {
                throw new InvalidOperationException("Choose a photo smaller than 8 MB.");
            }

            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await source.CopyToAsync(destination);
                if (destination.Length > MaximumPhotoSizeBytes)
                {
                    throw new InvalidOperationException("Choose a photo smaller than 8 MB.");
                }
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            Preferences.Default.Set(PreferenceKeyPrefix + userId, destinationPath);
            return destinationPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
