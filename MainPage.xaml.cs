using System.Net.Http;
using Microsoft.Maui.Networking;

namespace QuickPark;

public partial class MainPage : ContentPage
{
    private const string RememberedEmailPreference = "QuickPark.RememberedEmail";
    private const string DisplayNamePreferencePrefix = "QuickPark.DisplayName.";
    private readonly FirebaseAuthService _authService = new();
    private readonly AuthApiClient _authApiClient = new();
    private bool _isRegistering;
    private bool _isAuthenticating;
    private bool _isPageVisible;
    private bool _backgroundAnimationsStarted;
    private bool _entranceAnimationShown;
    private bool _offlineRestoreChecked;
    private readonly Grid _brandHeader;
    private readonly Border _loginCard;
    private readonly Label _authSubtitleLabel;
    private readonly Image _ambientGlow;
    private readonly Border _particleOne;
    private readonly Border _particleTwo;
    private readonly Border _particleThree;
    private readonly Border _particleFour;
    private readonly Grid _loginContent;
    private readonly VerticalStackLayout _brandStory;
    private readonly VerticalStackLayout _loginPanel;
    private bool? _isCompactLayout;

    public MainPage()
    {
        InitializeComponent();

        _brandHeader = GetRequiredView<Grid>("BrandHeader");
        _loginCard = GetRequiredView<Border>("LoginCard");
        _authSubtitleLabel = GetRequiredView<Label>("AuthSubtitleLabel");
        _ambientGlow = GetRequiredView<Image>("AmbientGlow");
        _particleOne = GetRequiredView<Border>("ParticleOne");
        _particleTwo = GetRequiredView<Border>("ParticleTwo");
        _particleThree = GetRequiredView<Border>("ParticleThree");
        _particleFour = GetRequiredView<Border>("ParticleFour");
        _loginContent = GetRequiredView<Grid>("LoginContent");
        _brandStory = GetRequiredView<VerticalStackLayout>("BrandStory");
        _loginPanel = GetRequiredView<VerticalStackLayout>("LoginPanel");

        var rememberedEmail = Preferences.Default.Get(RememberedEmailPreference, string.Empty);
        if (!string.IsNullOrWhiteSpace(rememberedEmail))
        {
            EmailEntry.Text = rememberedEmail;
            RememberMeCheckBox.IsChecked = true;
        }
    }

    private T GetRequiredView<T>(string name) where T : VisualElement
    {
        if (FindByName(name) is T view)
        {
            return view;
        }

        throw new InvalidOperationException($"The login page is missing the required view '{name}'.");
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        var isCompact = width < 900;
        if (_isCompactLayout == isCompact)
        {
            return;
        }

        _isCompactLayout = isCompact;
        _loginContent.ColumnDefinitions.Clear();
        _loginContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        if (isCompact)
        {
            _loginContent.ColumnSpacing = 0;
            _loginContent.Padding = new Thickness(18, 24);
            _brandStory.IsVisible = false;
            Grid.SetColumn(_loginPanel, 0);
            _loginPanel.WidthRequest = -1;
            _loginPanel.HorizontalOptions = LayoutOptions.Fill;
            return;
        }

        _loginContent.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(430)));
        _loginContent.ColumnSpacing = 64;
        _loginContent.Padding = new Thickness(28, 36);
        _brandStory.IsVisible = true;
        Grid.SetColumn(_loginPanel, 1);
        _loginPanel.WidthRequest = 430;
        _loginPanel.HorizontalOptions = LayoutOptions.Center;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _isPageVisible = true;

        if (!_offlineRestoreChecked && Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            _offlineRestoreChecked = true;
            var user = await AuthSession.RestoreCachedUserAsync();
            if (user is not null && Application.Current?.Windows.FirstOrDefault() is { } window)
            {
                window.Page = new HomePage(user.Email);
                return;
            }

            ShowStatus("Connect to the internet and sign in once on this device to use QuickPark offline later.", isError: true);
        }

        StartBackgroundAnimations();

        if (!_entranceAnimationShown)
        {
            _entranceAnimationShown = true;
            await Task.WhenAll(
                _brandHeader.FadeToAsync(1, 500, Easing.CubicOut),
                _loginCard.FadeToAsync(1, 650, Easing.CubicOut));
        }
    }

    protected override void OnDisappearing()
    {
        _isPageVisible = false;
        this.AbortAnimation("QuickParkBackground");
        _backgroundAnimationsStarted = false;
        base.OnDisappearing();
    }

    private void StartBackgroundAnimations()
    {
        if (!_isPageVisible || _backgroundAnimationsStarted)
        {
            return;
        }

        _backgroundAnimationsStarted = true;

        var backgroundAnimation = new Animation();
        backgroundAnimation.Add(0, 1, new Animation(value =>
        {
            var wave = Math.Sin(value * Math.PI * 2);
            _ambientGlow.TranslationY = wave * 8;
            _ambientGlow.Opacity = 0.58 + (wave + 1) * 0.025;
        }));
        AddFloatingParticle(backgroundAnimation, _particleOne, 12, 0.25, 0.00);
        AddFloatingParticle(backgroundAnimation, _particleTwo, 18, 0.20, 0.18);
        AddFloatingParticle(backgroundAnimation, _particleThree, 14, 0.18, 0.42);
        AddFloatingParticle(backgroundAnimation, _particleFour, 10, 0.22, 0.68);
        backgroundAnimation.Commit(this, "QuickParkBackground", 33, 9000, Easing.Linear, repeat: () => _isPageVisible);
    }

    private static void AddFloatingParticle(Animation backgroundAnimation, VisualElement particle, double travel, double minimumOpacity, double phase)
    {
        backgroundAnimation.Add(0, 1, new Animation(value =>
        {
            var wave = (Math.Sin((value + phase) * Math.PI * 2) + 1) / 2;
            particle.TranslationY = -travel * wave;
            particle.Opacity = minimumOpacity + (1 - minimumOpacity) * wave;
        }));
    }

    private async void LoginButton_Clicked(object sender, EventArgs e)
    {
        if (_isAuthenticating)
        {
            return;
        }

        var email = EmailEntry.Text?.Trim() ?? string.Empty;
        var password = PasswordEntry.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(email))
        {
            ShowStatus("Enter your email address.", isError: true);
            EmailEntry.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            ShowStatus("Enter your password.", isError: true);
            PasswordEntry.Focus();
            return;
        }

        _isAuthenticating = true;
        SetBusy(true, _isRegistering ? "Creating account..." : "Signing in...");
        try
        {
            var user = _isRegistering
                ? await _authService.CreateAccountAsync(email, password)
                : await _authService.SignInAsync(email, password);
            if (_isRegistering)
            {
                AuthSession.SignIn(user);
                await SyncFirestoreProfileBestEffortAsync(user, user.Email.Split('@')[0]);
                AuthSession.SignOut();
                SaveRememberedEmail(email);
                PasswordEntry.Text = string.Empty;
                ShowStatus($"Account created for {user.Email}.", isError: false);
                await DisplayAlertAsync("Account created", $"Your QuickPark account for {user.Email} is ready.", "Continue");
                SetRegistrationMode(false);
            }
            else
            {
                if (await ContinueAsCustomerAsync(user))
                {
                    PasswordEntry.Text = string.Empty;
                }
            }
        }
        catch (FirebaseAuthException exception)
        {
            if (await TryResumeOfflineAccountAsync(email))
            {
                PasswordEntry.Text = string.Empty;
                return;
            }

            AuthSession.SignOut();
            ShowStatus(exception.Message, isError: true);
        }
        catch (InvalidOperationException exception)
        {
            if (await TryResumeOfflineAccountAsync(email))
            {
                PasswordEntry.Text = string.Empty;
                return;
            }

            AuthSession.SignOut();
            ShowStatus(exception.Message, isError: true);
        }
        catch (HttpRequestException)
        {
            if (await TryResumeOfflineAccountAsync(email))
            {
                PasswordEntry.Text = string.Empty;
                return;
            }

            AuthSession.SignOut();
            ShowStatus("Could not connect to Firebase. Check your internet connection and try again.", isError: true);
        }
        catch (TaskCanceledException)
        {
            if (await TryResumeOfflineAccountAsync(email))
            {
                PasswordEntry.Text = string.Empty;
                return;
            }

            AuthSession.SignOut();
            ShowStatus("The Firebase request timed out. Please try again.", isError: true);
        }
        finally
        {
            _isAuthenticating = false;
            SetBusy(false, _isRegistering ? "Create Account" : "Log In");
        }
    }

    private void PasswordEntry_Completed(object sender, EventArgs e)
    {
        LoginButton_Clicked(sender, e);
    }

    private void ShowPasswordButton_Clicked(object sender, EventArgs e)
    {
        PasswordEntry.IsPassword = !PasswordEntry.IsPassword;
        ShowPasswordButton.Text = PasswordEntry.IsPassword ? "◉" : "◌";
    }

    private async void ForgotPasswordButton_Clicked(object sender, EventArgs e)
    {
        var email = EmailEntry.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
        {
            email = await DisplayPromptAsync(
                "Reset password",
                "Enter the email address registered to your QuickPark account.",
                "Send",
                "Cancel",
                "Email address",
                keyboard: Keyboard.Email) ?? string.Empty;
        }

        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        SetBusy(true, "Sending...");
        try
        {
            await _authService.SendPasswordResetAsync(email);
            ShowStatus("Password reset email sent. Check your inbox.", isError: false);
            await DisplayAlertAsync("Check your email", "If this email is registered, Firebase has sent a password reset link.", "OK");
        }
        catch (FirebaseAuthException exception)
        {
            ShowStatus(exception.Message, isError: true);
        }
        catch (HttpRequestException)
        {
            ShowStatus("Could not connect to Firebase. Check your internet connection and try again.", isError: true);
        }
        catch (TaskCanceledException)
        {
            ShowStatus("The Firebase request timed out. Please try again.", isError: true);
        }
        finally
        {
            SetBusy(false, _isRegistering ? "Create Account" : "Log In");
        }
    }

    private void RegisterButton_Clicked(object sender, EventArgs e)
    {
        SetRegistrationMode(!_isRegistering);
    }

    private async void AdminLoginButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new NavigationPage(new AdminLoginPage()));
    }

    private async Task<bool> TryResumeOfflineAccountAsync(string email)
    {
        if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
        {
            return false;
        }

        var user = await AuthSession.RestoreCachedUserAsync(email);
        if (user is null)
        {
            return false;
        }

        var window = Application.Current?.Windows.FirstOrDefault();
        if (window is null)
        {
            return false;
        }

        window.Page = new HomePage(email);
        return true;
    }

    private async void GoogleSignInButton_Clicked(object sender, EventArgs e)
    {
        await SignInWithGoogleAsync();
    }

    private async void FacebookSignInButton_Clicked(object sender, EventArgs e)
    {
        await ShowProviderSetupMessageAsync("Facebook");
    }

    private async Task ShowProviderSetupMessageAsync(string provider)
    {
        await DisplayAlertAsync(
            $"{provider} sign-in isn't connected yet",
            $"To use {provider} sign-in, connect a Firebase project and enable this provider in Firebase Authentication.",
            "OK");
    }

    private async Task SignInWithGoogleAsync()
    {
#if ANDROID
        if (_isAuthenticating)
        {
            return;
        }

        _isAuthenticating = true;
        SetBusy(true, "Opening Google...");
        try
        {
            if (Platform.CurrentActivity is not MainActivity activity)
            {
                throw new GoogleSignInException("The Android sign-in activity is unavailable.");
            }

            var idToken = await GoogleSignInFlow.SignInAsync(activity);
            if (string.IsNullOrWhiteSpace(idToken))
            {
                return;
            }

            SetBusy(true, "Signing in...");
            var user = await _authService.SignInWithGoogleIdTokenAsync(idToken);
            await ContinueAsCustomerAsync(user);
        }
        catch (GoogleSignInException exception)
        {
            ShowStatus(exception.Message, isError: true);
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
            ShowStatus("Could not connect to Firebase. Check your internet connection and try again.", isError: true);
        }
        catch (TaskCanceledException)
        {
            AuthSession.SignOut();
            ShowStatus("The Firebase request timed out. Please try again.", isError: true);
        }
        finally
        {
            _isAuthenticating = false;
            SetBusy(false, "Log In");
        }
#else
        await DisplayAlertAsync(
            "Google sign-in unavailable",
            "Google sign-in is available in the Android app.",
            "OK");
#endif
    }

    private async Task<bool> ContinueAsCustomerAsync(FirebaseUser user)
    {
        AuthSession.SignIn(user);
        var role = await _authApiClient.GetAccountRoleAsync();

        if (role.Role == "admin")
        {
            AuthSession.SignOut();
            ShowStatus("This is an admin-only account. Use Admin sign in.", isError: true);
            return false;
        }

        if (role.Role != "user")
        {
            AuthSession.SignOut();
            throw new InvalidOperationException("Could not verify this account type. Please try again.");
        }

        await AuthSession.SaveCachedUserAsync();

        var displayName = string.IsNullOrWhiteSpace(user.DisplayName)
            ? Preferences.Default.Get($"{DisplayNamePreferencePrefix}{user.Email}", user.Email.Split('@')[0])
            : user.DisplayName;
        try
        {
            user = await _authService.GetCurrentAccountAsync();
            if (!string.IsNullOrWhiteSpace(user.DisplayName))
            {
                displayName = user.DisplayName;
            }
        }
        catch (FirebaseAuthException exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark profile details could not be loaded: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark profile details could not be loaded: {exception.Message}");
        }
        catch (TaskCanceledException exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark profile details timed out: {exception.Message}");
        }
        Preferences.Default.Set($"{DisplayNamePreferencePrefix}{user.Email}", displayName);
        await SyncFirestoreProfileBestEffortAsync(user, displayName);
        SaveRememberedEmail(user.Email);
        var window = Application.Current?.Windows.FirstOrDefault()
            ?? throw new InvalidOperationException("The QuickPark window is unavailable. Restart the app and try again.");
        var homePage = new HomePage(user.Email);
        window.Page = homePage;

        return true;
    }

    private async Task SyncFirestoreProfileBestEffortAsync(FirebaseUser user, string displayName)
    {
        try
        {
            await _authApiClient.EnsureOwnProfileAsync(displayName);
            await _authApiClient.UpdateOwnProfileAsync(displayName);
        }
        catch (FirestorePermissionException exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark profile sync was denied: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark profile sync failed: {exception.Message}");
        }
        catch (TaskCanceledException exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark profile sync timed out: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark profile sync failed: {exception.Message}");
        }
    }

    private void SaveRememberedEmail(string email)
    {
        if (RememberMeCheckBox.IsChecked)
        {
            Preferences.Default.Set(RememberedEmailPreference, email);
        }
        else
        {
            Preferences.Default.Remove(RememberedEmailPreference);
        }
    }

    private void SetBusy(bool isBusy, string buttonText)
    {
        LoginButton.IsEnabled = !isBusy;
        RegisterButton.IsEnabled = !isBusy;
        AdminLoginButton.IsEnabled = !isBusy;
        ForgotPasswordButton.IsEnabled = !isBusy && !_isRegistering;
        GoogleSignInButton.IsEnabled = !isBusy;
        EmailEntry.IsEnabled = !isBusy;
        PasswordEntry.IsEnabled = !isBusy;
        LoadingIndicator.IsRunning = isBusy;
        LoadingIndicator.IsVisible = isBusy;
        LoginButton.Text = buttonText;
    }

    private void SetRegistrationMode(bool isRegistering)
    {
        _isRegistering = isRegistering;
        AuthTitleLabel.Text = isRegistering ? "Create your account" : "Welcome back";
        _authSubtitleLabel.Text = isRegistering
            ? "Create an account to get started with QuickPark."
            : "Log in to your QuickPark account";
        LoginButton.Text = isRegistering ? "Create Account" : "Log In";
        LoginOptionsGrid.IsVisible = !isRegistering;
        RegisterPromptLabel.Text = isRegistering ? "Already have an account?" : "Don't have an account?";
        RegisterButton.Text = isRegistering ? "Sign In" : "Sign Up";
        ForgotPasswordButton.IsVisible = !isRegistering;
        if (isRegistering)
        {
            ShowStatus(string.Empty, isError: false);
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusLabel.Text = message;
        StatusLabel.TextColor = isError ? Color.FromArgb("#C62828") : Color.FromArgb("#16A34A");
        StatusLabel.IsVisible = !string.IsNullOrWhiteSpace(message);
    }
}
