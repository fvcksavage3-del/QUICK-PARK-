using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickPark;

public sealed class FirebaseAuthService
{
    private const string FirebaseAuthUrl = "https://identitytoolkit.googleapis.com/v1/accounts:";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<string> GetValidIdTokenAsync(CancellationToken cancellationToken = default)
    {
        var user = AuthSession.CurrentUser
            ?? throw new FirebaseAuthException("Sign in again before using admin tools.");
        if (TokenHasAtLeastFiveMinutesRemaining(user.IdToken))
        {
            return user.IdToken;
        }

        var config = FirebaseProjectConfig.Load();
        var requestUri =
            $"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(config.ApiKey)}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = user.RefreshToken
        });
        using var response = await HttpClient.PostAsync(requestUri, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new FirebaseAuthException("Your sign-in expired. Sign in again before using admin tools.");
        }

        var refreshed = JsonSerializer.Deserialize<SecureTokenResponse>(responseBody, JsonOptions)
            ?? throw new FirebaseAuthException("Firebase returned an empty token refresh response.");
        if (string.IsNullOrWhiteSpace(refreshed.IdToken) || string.IsNullOrWhiteSpace(refreshed.RefreshToken))
        {
            throw new FirebaseAuthException("Firebase returned an invalid token refresh response.");
        }

        var updatedUser = user with
        {
            IdToken = refreshed.IdToken,
            RefreshToken = refreshed.RefreshToken
        };
        AuthSession.Update(updatedUser);
        return updatedUser.IdToken;
    }

    public async Task<string> GetAccountRoleAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetValidIdTokenAsync(cancellationToken);
        var segments = token.Split('.');
        if (segments.Length != 3)
        {
            throw new FirebaseAuthException("Firebase returned an invalid sign-in token.");
        }

        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("admin", out var adminClaim) &&
                adminClaim.ValueKind == JsonValueKind.True
                    ? "admin"
                    : "user";
        }
        catch (FormatException exception)
        {
            throw new FirebaseAuthException($"Firebase returned an invalid sign-in token: {exception.Message}");
        }
        catch (JsonException exception)
        {
            throw new FirebaseAuthException($"Firebase returned an unreadable sign-in token: {exception.Message}");
        }
    }

    public async Task<FirebaseUser> GetCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        var currentUser = AuthSession.CurrentUser
            ?? throw new FirebaseAuthException("Sign in again before loading your profile.");
        var token = await GetValidIdTokenAsync(cancellationToken);
        currentUser = AuthSession.CurrentUser
            ?? throw new FirebaseAuthException("Your sign-in session expired. Sign in again.");
        var response = await PostAsync<AccountLookupResponse>(
            "lookup",
            new AccountLookupRequest(token),
            cancellationToken);
        var account = response.Users.FirstOrDefault(candidate =>
            string.Equals(candidate.LocalId, currentUser.UserId, StringComparison.Ordinal));
        if (account is null)
        {
            throw new FirebaseAuthException("Firebase could not find the signed-in account. Sign in again.");
        }

        var updatedUser = currentUser with
        {
            Email = string.IsNullOrWhiteSpace(account.Email) ? currentUser.Email : account.Email,
            DisplayName = account.DisplayName ?? string.Empty,
            PhoneNumber = account.PhoneNumber ?? string.Empty
        };
        AuthSession.Update(updatedUser);
        return updatedUser;
    }

    public async Task<FirebaseUser> UpdateCurrentAccountProfileAsync(
        string displayName,
        string? phoneNumber = null,
        CancellationToken cancellationToken = default)
    {
        var currentUser = AuthSession.CurrentUser
            ?? throw new FirebaseAuthException("Sign in again before saving your profile.");
        var token = await GetValidIdTokenAsync(cancellationToken);
        currentUser = AuthSession.CurrentUser
            ?? throw new FirebaseAuthException("Your sign-in session expired. Sign in again.");
        var clearPhoneNumber = phoneNumber is not null && string.IsNullOrWhiteSpace(phoneNumber);
        var updatePhoneNumber = phoneNumber is not null && !clearPhoneNumber;
        var update = new UpdateProfileRequest(
            token,
            displayName,
            updatePhoneNumber ? phoneNumber : null,
            clearPhoneNumber ? ["PHONE_NUMBER"] : null);
        var updatedUser = await PostAsync<FirebaseUser>("update", update, cancellationToken);
        if (!string.IsNullOrWhiteSpace(updatedUser.UserId) &&
            !string.Equals(updatedUser.UserId, currentUser.UserId, StringComparison.Ordinal))
        {
            throw new FirebaseAuthException("Firebase returned a profile for a different account. Sign in again.");
        }

        updatedUser = updatedUser with
        {
            UserId = currentUser.UserId,
            Email = string.IsNullOrWhiteSpace(updatedUser.Email) ? currentUser.Email : updatedUser.Email,
            IdToken = string.IsNullOrWhiteSpace(updatedUser.IdToken) ? currentUser.IdToken : updatedUser.IdToken,
            RefreshToken = string.IsNullOrWhiteSpace(updatedUser.RefreshToken)
                ? currentUser.RefreshToken
                : updatedUser.RefreshToken,
            DisplayName = string.IsNullOrWhiteSpace(updatedUser.DisplayName)
                ? displayName
                : updatedUser.DisplayName,
            PhoneNumber = phoneNumber is null
                ? currentUser.PhoneNumber
                : clearPhoneNumber
                    ? string.Empty
                    : string.IsNullOrWhiteSpace(updatedUser.PhoneNumber) ? phoneNumber : updatedUser.PhoneNumber
        };
        AuthSession.Update(updatedUser);
        return updatedUser;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Task<FirebaseUser> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        return PostAsync<FirebaseUser>(
            "signInWithPassword",
            new AuthRequest(email, password),
            cancellationToken);
    }

    public Task<FirebaseUser> CreateAccountAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        return PostAsync<FirebaseUser>(
            "signUp",
            new AuthRequest(email, password),
            cancellationToken);
    }

    public async Task SendPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        await PostAsync<PasswordResetResponse>(
            "sendOobCode",
            new PasswordResetRequest(email),
            cancellationToken);
    }

    public async Task SendEmailChangeVerificationAsync(
        string newEmail,
        string currentPassword,
        CancellationToken cancellationToken = default)
    {
        var user = AuthSession.CurrentUser
            ?? throw new FirebaseAuthException("Sign in again before changing your email address.");
        var reauthenticatedUser = await SignInAsync(user.Email, currentPassword, cancellationToken);
        if (!string.Equals(reauthenticatedUser.UserId, user.UserId, StringComparison.Ordinal))
        {
            throw new FirebaseAuthException("Could not verify this account. Sign in again and try once more.");
        }

        AuthSession.Update(reauthenticatedUser);
        var request = new EmailChangeVerificationRequest(
            "VERIFY_AND_CHANGE_EMAIL",
            reauthenticatedUser.IdToken,
            newEmail);
        await PostAsync<JsonElement>("sendOobCode", request, cancellationToken);
    }

    public Task<FirebaseUser> SignInWithGoogleIdTokenAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        var payload = new GoogleIdTokenRequest(
            $"id_token={Uri.EscapeDataString(idToken)}&providerId=google.com",
            "http://localhost");

        return PostAsync<FirebaseUser>("signInWithIdp", payload, cancellationToken);
    }

    public async Task<FirebaseUser> ChangePasswordAsync(
        string email,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var authenticatedUser = await SignInAsync(email, currentPassword, cancellationToken);
        return await PostAsync<FirebaseUser>(
            "update",
            new UpdatePasswordRequest(authenticatedUser.IdToken, newPassword),
            cancellationToken);
    }

    private async Task<TResponse> PostAsync<TResponse>(
        string action,
        object payload,
        CancellationToken cancellationToken)
    {
        var config = FirebaseProjectConfig.Load();
        var requestUri = $"{FirebaseAuthUrl}{action}?key={Uri.EscapeDataString(config.ApiKey)}";
        using var response = await HttpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var code = ReadFirebaseErrorCode(responseBody);
            throw new FirebaseAuthException(GetFriendlyErrorMessage(code), code);
        }

        try
        {
            var result = JsonSerializer.Deserialize<TResponse>(responseBody, JsonOptions);
            return result ?? throw new FirebaseAuthException("Firebase returned an empty response.");
        }
        catch (JsonException)
        {
            throw new FirebaseAuthException("Firebase returned an unreadable response. Please try again.");
        }
    }

    private static string ReadFirebaseErrorCode(string responseBody)
    {
        try
        {
            return JsonSerializer.Deserialize<FirebaseErrorResponse>(responseBody, JsonOptions)?.Error?.Message
                ?? "UNKNOWN_ERROR";
        }
        catch (JsonException)
        {
            return "UNKNOWN_ERROR";
        }
    }

    private static bool TokenHasAtLeastFiveMinutesRemaining(string token)
    {
        try
        {
            var segments = token.Split('.');
            if (segments.Length != 3)
            {
                return false;
            }

            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("exp", out var expiration)
                && expiration.TryGetInt64(out var expirationUnixSeconds)
                && DateTimeOffset.FromUnixTimeSeconds(expirationUnixSeconds) > DateTimeOffset.UtcNow.AddMinutes(5);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string GetFriendlyErrorMessage(string code)
    {
        return code switch
        {
            "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" or "INVALID_LOGIN_CREDENTIALS" =>
                "The email or password is incorrect.",
            "EMAIL_EXISTS" or "EMAIL_ALREADY_IN_USE" =>
                "That email address is already linked to another account.",
            "CREDENTIAL_TOO_OLD_LOGIN_AGAIN" =>
                "For your security, sign out and sign in again before changing your email.",
            "INVALID_ID_TOKEN" =>
                "Your sign-in session expired. Sign in again before changing your email.",
            "USER_DISABLED" => "This account has been disabled.",
            "WEAK_PASSWORD" => "Choose a stronger password with at least 6 characters.",
            "INVALID_EMAIL" => "Enter a valid email address.",
            "OPERATION_NOT_ALLOWED" => "Email and password sign-in is not enabled in Firebase Authentication.",
            "TOO_MANY_ATTEMPTS_TRY_LATER" => "Too many attempts. Please wait a while and try again.",
            "INVALID_PHONE_NUMBER" => "Enter a phone number in international format, such as +639171234567.",
            "PHONE_NUMBER_EXISTS" => "That phone number is already linked to another account.",
            "API_KEY_INVALID" => "The Firebase Web API key is invalid. Check FirebaseAuthService.cs.",
            "PROJECT_NOT_FOUND" => "The Firebase project was not found. Check its Web API key.",
            _ => $"Firebase authentication failed ({code}). Check your Firebase setup and try again."
        };
    }

    private sealed record AuthRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("password")] string Password,
        [property: JsonPropertyName("returnSecureToken")] bool ReturnSecureToken = true);

    private sealed record PasswordResetRequest(
        [property: JsonPropertyName("requestType")] string RequestType,
        [property: JsonPropertyName("email")] string Email)
    {
        public PasswordResetRequest(string email) : this("PASSWORD_RESET", email)
        {
        }
    }

    private sealed record PasswordResetResponse(
        [property: JsonPropertyName("email")] string Email);

    private sealed record EmailChangeVerificationRequest(
        [property: JsonPropertyName("requestType")] string RequestType,
        [property: JsonPropertyName("idToken")] string IdToken,
        [property: JsonPropertyName("newEmail")] string NewEmail);

    private sealed record AccountLookupRequest(
        [property: JsonPropertyName("idToken")] string IdToken);

    private sealed record UpdateProfileRequest(
        [property: JsonPropertyName("idToken")] string IdToken,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("phoneNumber")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PhoneNumber,
        [property: JsonPropertyName("deleteAttribute")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string[]? DeleteAttribute,
        [property: JsonPropertyName("returnSecureToken")] bool ReturnSecureToken = true);

    private sealed class AccountLookupResponse
    {
        public List<FirebaseAccountInfo> Users { get; set; } = [];
    }

    private sealed class FirebaseAccountInfo
    {
        public string LocalId { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? DisplayName { get; set; }
        public string? PhoneNumber { get; set; }
    }

    private sealed record SecureTokenResponse(
        [property: JsonPropertyName("id_token")] string IdToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken);

    private sealed record UpdatePasswordRequest(
        [property: JsonPropertyName("idToken")] string IdToken,
        [property: JsonPropertyName("password")] string Password,
        [property: JsonPropertyName("returnSecureToken")] bool ReturnSecureToken = true);

    private sealed record GoogleIdTokenRequest(
        [property: JsonPropertyName("postBody")] string PostBody,
        [property: JsonPropertyName("requestUri")] string RequestUri,
        [property: JsonPropertyName("returnIdpCredential")] bool ReturnIdpCredential = true,
        [property: JsonPropertyName("returnSecureToken")] bool ReturnSecureToken = true);

    private sealed class FirebaseErrorResponse
    {
        public FirebaseError? Error { get; set; }
    }

    private sealed class FirebaseError
    {
        public string? Message { get; set; }
    }
}

internal sealed class FirebaseProjectConfig
{
    private static readonly Lazy<FirebaseProjectConfig> CurrentConfig = new(LoadConfig);

    public required string ApiKey { get; init; }
    public required string ProjectId { get; init; }
    public required string GoogleWebClientId { get; init; }

    public static FirebaseProjectConfig Load() => CurrentConfig.Value;

    private static FirebaseProjectConfig LoadConfig()
    {
        using var stream = typeof(FirebaseProjectConfig).Assembly
            .GetManifestResourceStream("QuickPark.google-services.json")
            ?? throw new FirebaseAuthException("Firebase Android config is missing from the app.");

        try
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var apiKey = root.GetProperty("client")[0]
                .GetProperty("api_key")[0]
                .GetProperty("current_key")
                .GetString();
            var projectId = root.GetProperty("project_info")
                .GetProperty("project_id")
                .GetString();
            var oauthClients = root.GetProperty("client")[0].GetProperty("oauth_client");
            var webClient = oauthClients.ValueKind == JsonValueKind.Array
                ? oauthClients.EnumerateArray()
                    .FirstOrDefault(client =>
                        client.TryGetProperty("client_type", out var clientType) &&
                        clientType.TryGetInt32(out var clientTypeValue) &&
                        clientTypeValue == 3)
                : default;
            var googleWebClientId = webClient.ValueKind == JsonValueKind.Object &&
                webClient.TryGetProperty("client_id", out var clientId)
                    ? clientId.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(projectId))
            {
                throw new FirebaseAuthException("Firebase config is missing the project ID or API key.");
            }

            return new FirebaseProjectConfig
            {
                ApiKey = apiKey,
                ProjectId = projectId,
                GoogleWebClientId = googleWebClientId ?? string.Empty
            };
        }
        catch (JsonException exception)
        {
            throw new FirebaseAuthException($"Firebase Android config could not be read: {exception.Message}");
        }
        catch (KeyNotFoundException exception)
        {
            throw new FirebaseAuthException($"Firebase Android config is missing a required field: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            throw new FirebaseAuthException($"Firebase Android config contains an invalid field: {exception.Message}");
        }
    }
}

public sealed record FirebaseUser(
    [property: JsonPropertyName("localId")] string UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("idToken")] string IdToken,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("displayName")] string DisplayName = "",
    [property: JsonPropertyName("phoneNumber")] string PhoneNumber = "");

public sealed class FirebaseAuthException(string message, string? errorCode = null) : Exception(message)
{
    public string? ErrorCode { get; } = errorCode;
}
