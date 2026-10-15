using System.Text.Json;

namespace QuickPark;

internal static class AuthSession
{
    private const string CachedUserKey = "QuickPark.Auth.CachedUser";

    public static FirebaseUser? CurrentUser { get; private set; }

    public static void SignIn(FirebaseUser user)
    {
        CurrentUser = user;
    }

    public static async Task SaveCachedUserAsync()
    {
        if (CurrentUser is null)
        {
            return;
        }

        await SecureStorage.Default.SetAsync(CachedUserKey, JsonSerializer.Serialize(CurrentUser));
    }

    public static async Task<FirebaseUser?> RestoreCachedUserAsync(string? email = null)
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(CachedUserKey);
            var cachedUser = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<FirebaseUser>(json);
            if (cachedUser is null ||
                (email is not null && !string.Equals(cachedUser.Email, email, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            CurrentUser = cachedUser;
            return cachedUser;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark could not restore the cached sign-in: {exception.Message}");
            return null;
        }
    }

    public static void Update(FirebaseUser user)
    {
        CurrentUser = user;
        _ = SaveCachedUserBestEffortAsync();
    }

    public static void SignOut()
    {
        CurrentUser = null;
        try
        {
            SecureStorage.Default.Remove(CachedUserKey);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark could not clear the cached sign-in: {exception.Message}");
        }
    }

    private static async Task SaveCachedUserBestEffortAsync()
    {
        try
        {
            await SaveCachedUserAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"QuickPark could not save the cached sign-in: {exception.Message}");
        }
    }
}
