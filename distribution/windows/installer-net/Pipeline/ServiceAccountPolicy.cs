using System;

namespace ChaptarrNG.Installer.Pipeline;

public static class ServiceAccountPolicy
{
    public const string LocalService = @"NT AUTHORITY\LocalService";
    public const string NetworkService = @"NT AUTHORITY\NetworkService";
    public const string LocalSystem = "LocalSystem";

    public static string? GetUnsupportedReason(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return "The BookshelfNG service account could not be identified.";
        }

        if (account.Trim().StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase))
        {
            return $"The Readarr service uses the service-specific virtual account '{account}'. Windows binds that identity to the Readarr service name, so it cannot be reused by the Chaptarr service. Configure BookshelfNG to use a shared Windows account before migrating.";
        }

        return null;
    }

    public static bool RequiresPassword(string account)
    {
        var normalized = account.Trim();
        if (IsAlias(normalized, LocalSystem, "NT AUTHORITY\\LocalSystem", "NT AUTHORITY\\SYSTEM") ||
            IsAlias(normalized, LocalService, "LocalService") ||
            IsAlias(normalized, NetworkService, "NetworkService"))
        {
            return false;
        }

        return !IsManagedServiceAccount(normalized);
    }

    public static bool IsManagedServiceAccount(string account) => account.Trim().EndsWith('$');

    public static bool UsesEmptyPassword(string account)
    {
        var normalized = account.Trim();
        return IsAlias(normalized, LocalSystem, "NT AUTHORITY\\LocalSystem", "NT AUTHORITY\\SYSTEM") ||
            IsAlias(normalized, LocalService, "LocalService") ||
            IsAlias(normalized, NetworkService, "NetworkService");
    }

    public static bool AreEquivalent(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var leftNormalized = NormalizeAlias(left);
        var rightNormalized = NormalizeAlias(right);
        return string.Equals(leftNormalized, rightNormalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAlias(string value, string canonical, params string[] aliases)
    {
        return string.Equals(value, canonical, StringComparison.OrdinalIgnoreCase) ||
            Array.Exists(aliases, alias => string.Equals(value, alias, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeAlias(string account)
    {
        var value = account.Trim();
        if (IsAlias(value, LocalSystem, "NT AUTHORITY\\LocalSystem", "NT AUTHORITY\\SYSTEM"))
        {
            return "LocalSystem";
        }
        if (IsAlias(value, LocalService, "LocalService"))
        {
            return LocalService;
        }
        if (IsAlias(value, NetworkService, "NetworkService"))
        {
            return NetworkService;
        }

        return value;
    }
}
