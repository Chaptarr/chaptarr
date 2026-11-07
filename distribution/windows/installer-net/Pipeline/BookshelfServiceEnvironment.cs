using System;
using System.Collections;
using System.Collections.Generic;

namespace ChaptarrNG.Installer.Pipeline;

internal static class BookshelfServiceEnvironment
{
    public const string DefaultBookshelfPort = "8787";
    private static readonly string[] LegacyPrefixes = { "BOOKSHELF__", "AUDIOARR__", "READARR__" };

    public static IReadOnlyDictionary<string, string> GetEffectiveConfigurationEnvironment()
    {
        return GetEffectiveConfigurationEnvironment(
            ServiceLifecycle.BookshelfServiceName,
            ServiceLifecycle.GetBookshelfServerCommandLineOverrides());
    }

    public static IReadOnlyDictionary<string, string> GetEffectiveConfigurationEnvironment(
        string serviceName,
        IReadOnlyDictionary<string, string>? commandLineOverrides = null)
    {
        var machine = ReadMachineEnvironment();
        var service = ServiceLifecycle.GetServiceEnvironment(serviceName);
        var effective = Normalize(machine);
        Overlay(effective, Normalize(service));
        if (commandLineOverrides is not null)
        {
            Overlay(effective, commandLineOverrides);
        }

        return effective;
    }

    public static string? GetEffectiveDefaultPort(string serviceName)
    {
        var service = ServiceLifecycle.GetServiceEnvironment(serviceName);
        if (service.TryGetValue("CHAPTARR_DEFAULT_PORT", out var serviceDefaultPort) &&
            !string.IsNullOrWhiteSpace(serviceDefaultPort))
        {
            return serviceDefaultPort;
        }

        var machine = ReadMachineEnvironment();
        return machine.TryGetValue("CHAPTARR_DEFAULT_PORT", out var machineDefaultPort) &&
            !string.IsNullOrWhiteSpace(machineDefaultPort)
            ? machineDefaultPort
            : null;
    }

    public static IReadOnlyDictionary<string, string?> CreateMigrationOverrides(ProviderIdentityMode providerMode)
    {
        var machine = ReadMachineEnvironment();
        var service = ServiceLifecycle.GetBookshelfServiceEnvironment();
        var overrides = MaskInheritedApplicationEnvironment();
        foreach (var item in service)
        {
            overrides[item.Key] = item.Value;
        }

        var effective = Normalize(machine);
        Overlay(effective, Normalize(service));
        Overlay(effective, ServiceLifecycle.GetBookshelfServerCommandLineOverrides());
        foreach (var item in effective)
        {
            overrides[item.Key] = item.Value;
        }

        if (!TryGetEnvironmentValue(service, machine, "CHAPTARR_DEFAULT_PORT", out var configuredDefaultPort) ||
            string.IsNullOrWhiteSpace(configuredDefaultPort))
        {
            overrides["CHAPTARR_DEFAULT_PORT"] = DefaultBookshelfPort;
        }
        else
        {
            overrides["CHAPTARR_DEFAULT_PORT"] = configuredDefaultPort;
        }

        ApplyProviderModeToMigrationEnvironment(overrides, providerMode);
        return overrides;
    }

    public static IReadOnlyDictionary<string, string> CreateTargetServiceEnvironment(ProviderIdentityMode providerMode)
    {
        var machine = ReadMachineEnvironment();
        var sourceService = ServiceLifecycle.GetBookshelfServiceEnvironment();
        var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Overlay(target, sourceService);

        // ChaptarrNG understands the legacy prefixes itself, but adding canonical
        // aliases lets service-specific BookshelfNG values override machine-wide
        // ChaptarrNG settings just as the original service environment did.
        Overlay(target, Normalize(sourceService));
        Overlay(target, ServiceLifecycle.GetBookshelfServerCommandLineOverrides());

        if (!TryGetEnvironmentValue(sourceService, machine, "CHAPTARR_DEFAULT_PORT", out var configuredDefaultPort) ||
            string.IsNullOrWhiteSpace(configuredDefaultPort))
        {
            target["CHAPTARR_DEFAULT_PORT"] = DefaultBookshelfPort;
        }
        else if (sourceService.TryGetValue("CHAPTARR_DEFAULT_PORT", out var serviceDefaultPort) &&
            !string.IsNullOrWhiteSpace(serviceDefaultPort))
        {
            target["CHAPTARR_DEFAULT_PORT"] = serviceDefaultPort;
        }

        ApplyProviderModeToServiceEnvironment(target, providerMode);
        return target;
    }

    private static Dictionary<string, string> Normalize(IEnumerable<KeyValuePair<string, string>> source)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Overlay(values, source);
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in values)
        {
            if (item.Key.StartsWith("CHAPTARR__", StringComparison.OrdinalIgnoreCase))
            {
                normalized[item.Key] = item.Value;
            }
        }

        var fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in LegacyPrefixes)
        {
            foreach (var item in values)
            {
                if (item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    fallback["CHAPTARR__" + item.Key[prefix.Length..]] = item.Value;
                }
            }
        }

        if (values.TryGetValue("BOOKSHELF_PORT", out var port) &&
            !fallback.ContainsKey("CHAPTARR__SERVER__PORT"))
        {
            fallback["CHAPTARR__SERVER__PORT"] = port;
        }

        if (values.TryGetValue("BOOKSHELF_URLBASE", out var urlBase) &&
            !fallback.ContainsKey("CHAPTARR__SERVER__URLBASE"))
        {
            fallback["CHAPTARR__SERVER__URLBASE"] = urlBase;
        }

        foreach (var item in fallback)
        {
            if (!normalized.ContainsKey(item.Key))
            {
                normalized[item.Key] = item.Value;
            }
        }

        return normalized;
    }

    private static Dictionary<string, string?> MaskInheritedApplicationEnvironment()
    {
        var masked = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = entry.Key?.ToString();
            if (!string.IsNullOrWhiteSpace(name) && IsApplicationEnvironmentName(name))
            {
                masked[name] = null;
            }
        }

        return masked;
    }

    private static bool IsApplicationEnvironmentName(string name)
    {
        return name.StartsWith("CHAPTARR__", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("BOOKSHELF__", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("AUDIOARR__", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("READARR__", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("BOOKSHELF_PORT", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("BOOKSHELF_URLBASE", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CHAPTARR_DEFAULT_PORT", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("HARDCOVER", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("HARDCOVER_NATIVE", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadMachineEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Machine))
        {
            var name = entry.Key?.ToString();
            var value = entry.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(name) && value is not null)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static bool TryGetEnvironmentValue(
        IReadOnlyDictionary<string, string> service,
        IReadOnlyDictionary<string, string> machine,
        string name,
        out string value)
    {
        if (service.TryGetValue(name, out var serviceValue))
        {
            value = serviceValue;
            return true;
        }

        if (machine.TryGetValue(name, out var machineValue))
        {
            value = machineValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static void Overlay(
        IDictionary<string, string> destination,
        IEnumerable<KeyValuePair<string, string>> values)
    {
        foreach (var item in values)
        {
            destination[item.Key] = item.Value;
        }
    }

    private static void ApplyProviderModeToMigrationEnvironment(
        IDictionary<string, string?> destination,
        ProviderIdentityMode providerMode)
    {
        if (providerMode == ProviderIdentityMode.NativeHardcover)
        {
            destination["HARDCOVER"] = "true";
            destination["HARDCOVER_NATIVE"] = null;
        }
        else
        {
            destination["HARDCOVER"] = "false";
            destination["HARDCOVER_NATIVE"] = "false";
        }
    }

    private static void ApplyProviderModeToServiceEnvironment(
        IDictionary<string, string> destination,
        ProviderIdentityMode providerMode)
    {
        if (providerMode == ProviderIdentityMode.NativeHardcover)
        {
            destination["HARDCOVER"] = "true";
            destination["HARDCOVER_NATIVE"] = "true";
        }
        else
        {
            destination["HARDCOVER"] = "false";
            destination["HARDCOVER_NATIVE"] = "false";
        }
    }
}
