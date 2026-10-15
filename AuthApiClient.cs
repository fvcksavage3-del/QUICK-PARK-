namespace QuickPark;

internal sealed class AuthApiClient
{
    private readonly FirebaseAuthService _authService = new();
    private readonly FirestoreRestClient _firestore = new();

    public async Task<AccountRoleResponse> GetAccountRoleAsync(CancellationToken cancellationToken = default)
    {
        return new AccountRoleResponse
        {
            Role = await _authService.GetAccountRoleAsync(cancellationToken)
        };
    }

    public async Task<string> EnsureOwnProfileAsync(
        string? preferredDisplayName = null,
        CancellationToken cancellationToken = default)
    {
        var user = AuthSession.CurrentUser
            ?? throw new FirebaseAuthException("Sign in again before saving your profile.");
        var existing = await _firestore.GetDocumentAsync("userProfiles", user.UserId, cancellationToken);
        if (existing is not null)
        {
            var displayName = existing["displayName"]?.GetValue<string>();
            var profileEmail = existing["email"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(displayName) &&
                string.Equals(profileEmail, user.Email, StringComparison.OrdinalIgnoreCase))
            {
                return displayName;
            }

            var repairedName = string.IsNullOrWhiteSpace(displayName)
                ? NormalizeDisplayName(preferredDisplayName, user.Email)
                : displayName.Trim();
            if (repairedName.Length > 60)
            {
                repairedName = repairedName[..60];
            }

            await UpdateOwnProfileAsync(
                repairedName,
                cancellationToken,
                existing["phoneNumber"]?.GetValue<string>());
            return repairedName;
        }

        var fallbackName = NormalizeDisplayName(preferredDisplayName, user.Email);
        await _firestore.CommitAsync(
        [
            _firestore.BuildUpdateWrite("userProfiles", user.UserId, new
            {
                email = user.Email,
                displayName = fallbackName,
                phoneNumber = user.PhoneNumber,
                role = "user",
                createdAt = DateTime.UtcNow,
                updatedAt = DateTime.UtcNow
            }, createOnly: true)
        ], cancellationToken);
        return fallbackName;
    }

    public async Task<OwnUserProfile> GetOwnProfileAsync(
        string? preferredDisplayName = null,
        CancellationToken cancellationToken = default)
    {
        var displayName = await EnsureOwnProfileAsync(preferredDisplayName, cancellationToken);
        var user = AuthSession.CurrentUser
            ?? throw new FirebaseAuthException("Sign in again before loading your profile.");
        var existing = await _firestore.GetDocumentAsync("userProfiles", user.UserId, cancellationToken)
            ?? throw new InvalidOperationException("Your QuickPark profile could not be loaded. Please try again.");
        if (!existing.ContainsKey("phoneNumber") && !string.IsNullOrWhiteSpace(user.PhoneNumber))
        {
            await UpdateOwnProfileAsync(
                displayName,
                cancellationToken,
                user.PhoneNumber);
            existing = await _firestore.GetDocumentAsync("userProfiles", user.UserId, cancellationToken)
                ?? throw new InvalidOperationException("Your QuickPark profile could not be loaded. Please try again.");
        }

        var phoneNumber = existing["phoneNumber"]?.GetValue<string>() ?? string.Empty;
        return new OwnUserProfile(displayName, phoneNumber);
    }

    public async Task UpdateOwnProfileAsync(
        string displayName,
        CancellationToken cancellationToken = default,
        string? phoneNumber = null)
    {
        var user = AuthSession.CurrentUser
            ?? throw new FirebaseAuthException("Sign in again before saving your profile.");
        var existing = await _firestore.GetDocumentAsync(
            "userProfiles",
            user.UserId,
            cancellationToken);
        if (existing is null)
        {
            await EnsureOwnProfileAsync(displayName, cancellationToken);
            existing = await _firestore.GetDocumentAsync("userProfiles", user.UserId, cancellationToken)
                ?? throw new InvalidOperationException("Your QuickPark profile could not be created. Please try again.");
        }

        var profileFields = new Dictionary<string, object>
        {
            ["displayName"] = displayName,
            ["email"] = user.Email,
            ["updatedAt"] = DateTime.UtcNow
        };
        if (phoneNumber is not null)
        {
            profileFields["phoneNumber"] = phoneNumber;
        }

        await _firestore.CommitAsync(
        [
            _firestore.BuildUpdateWrite(
                "userProfiles",
                user.UserId,
                profileFields,
                updateTime: existing["__updateTime"]?.GetValue<string>())
        ], cancellationToken);
    }

    private static string NormalizeDisplayName(string? preferredDisplayName, string email)
    {
        var displayName = string.IsNullOrWhiteSpace(preferredDisplayName)
            ? email.Split('@')[0]
            : preferredDisplayName.Trim();
        return displayName.Length > 60 ? displayName[..60] : displayName;
    }
}

internal sealed record OwnUserProfile(string DisplayName, string PhoneNumber);

internal sealed class AccountRoleResponse
{
    public string Role { get; set; } = string.Empty;
}
