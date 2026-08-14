using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AssetBeeDrone.MsiHelper;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("Usage: AssetBee.Drone.MsiHelper <command> [options]");
                return 1;
            }

            return args[0].ToLowerInvariant() switch
            {
                "save-pending" => SavePending(ParseOptions(args.AsSpan(1))),
                "load-existing" => LoadExisting(),
                "preserve" => Preserve(),
                "write-settings" => WriteSettings(ParseOptions(args.AsSpan(1))),
                "uninstall-related" => UninstallRelated(ParseOptions(args.AsSpan(1))),
                _ => Fail($"Unknown command: {args[0]}"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument: {key}");
            }

            key = key[2..];
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for --{key}");
            }

            options[key] = args[++i];
        }

        return options;
    }

    private static int SavePending(Dictionary<string, string> options)
    {
        string pendingPath = Required(options, "pending");
        string endpoint = Optional(options, "endpoint");
        string bearer = Optional(options, "bearer");
        string api = Optional(options, "api");

        string? dir = Path.GetDirectoryName(pendingPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var payload = new Dictionary<string, string?>
        {
            ["Endpoint"] = endpoint,
            ["BearerToken"] = bearer,
            ["ApiKey"] = api,
        };

        File.WriteAllText(pendingPath, JsonSerializer.Serialize(payload));
        return 0;
    }

    private static int LoadExisting()
    {
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "AssetBee", "Drone", "appsettings.json");

        if (!TryReadDroneConfig(settingsPath, out string endpoint, out string bearer, out string apiKey))
        {
            return 0;
        }

        WriteRegistryMirror(@"SOFTWARE\AssetBee\Drone", RegistryHive.LocalMachine, endpoint, bearer, apiKey);
        try
        {
            WriteRegistryMirror(@"SOFTWARE\AssetBee\Drone", RegistryHive.CurrentUser, endpoint, bearer, apiKey);
        }
        catch
        {
            // Unelevated UI may not write HKLM; HKCU is best-effort.
        }

        return 0;
    }

    private static int Preserve()
    {
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "AssetBee", "Drone", "appsettings.json");

        if (!TryReadDroneConfig(settingsPath, out string endpoint, out string bearer, out string apiKey))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return 0;
        }

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AssetBee", "Drone");
        Directory.CreateDirectory(dir);

        var payload = new Dictionary<string, string?>
        {
            ["Endpoint"] = endpoint,
            ["BearerToken"] = bearer,
            ["ApiKey"] = apiKey,
        };

        File.WriteAllText(Path.Combine(dir, "msi-pending.json"), JsonSerializer.Serialize(payload));
        return 0;
    }

    private static int WriteSettings(Dictionary<string, string> options)
    {
        string installDir = Required(options, "install").Trim().TrimEnd('.').TrimEnd('\\', '/');
        string endpoint = Optional(options, "endpoint");
        string bearer = Optional(options, "bearer");
        string api = Optional(options, "api");
        string pendingPath = Optional(options, "pending");
        string legacyPendingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AssetBee", "Drone", "msi-pending.json");
        string settingsPath = Path.Combine(installDir, "appsettings.json");

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            foreach (string path in new[] { pendingPath, legacyPendingPath })
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                    JsonElement root = doc.RootElement;
                    endpoint = GetString(root, "Endpoint");
                    if (string.IsNullOrWhiteSpace(bearer))
                    {
                        bearer = GetString(root, "BearerToken");
                    }

                    if (string.IsNullOrWhiteSpace(api))
                    {
                        api = GetString(root, "ApiKey");
                    }

                    if (!string.IsNullOrWhiteSpace(endpoint))
                    {
                        break;
                    }
                }
                catch
                {
                    // Try next source.
                }
            }
        }

        if (string.IsNullOrWhiteSpace(endpoint) ||
            (string.IsNullOrWhiteSpace(bearer) && string.IsNullOrWhiteSpace(api)))
        {
            if (TryReadDroneConfig(settingsPath, out string existingEndpoint, out string existingBearer, out string existingApi))
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    endpoint = existingEndpoint;
                }

                if (string.IsNullOrWhiteSpace(bearer) && string.IsNullOrWhiteSpace(api))
                {
                    bearer = existingBearer;
                    api = existingApi;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(endpoint) ||
            (string.IsNullOrWhiteSpace(bearer) && string.IsNullOrWhiteSpace(api)))
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\AssetBee\Drone");
            if (key is not null)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    endpoint = key.GetValue("Endpoint") as string ?? "";
                }

                if (string.IsNullOrWhiteSpace(bearer) && string.IsNullOrWhiteSpace(api))
                {
                    bearer = key.GetValue("BearerToken") as string ?? "";
                    api = key.GetValue("ApiKey") as string ?? "";
                }
            }
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException(
                "The inventory endpoint is missing from both the installer UI and existing settings.");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The inventory endpoint must be a valid HTTPS URL.");
        }

        bool hasBearer = !string.IsNullOrWhiteSpace(bearer) && bearer != "null";
        bool hasApiKey = !string.IsNullOrWhiteSpace(api) && api != "null";
        if (hasBearer == hasApiKey)
        {
            throw new InvalidOperationException("Provide exactly one of BearerToken or ApiKey.");
        }

        if (!Directory.Exists(installDir))
        {
            throw new DirectoryNotFoundException($"Install directory not found: {installDir}");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Drone");
            writer.WriteStartObject();
            writer.WriteString("Endpoint", endpointUri.AbsoluteUri);
            writer.WriteNumber("CollectionIntervalMinutes", 360);
            writer.WriteNumber("RequestTimeoutSeconds", 30);
            writer.WriteNumber("MaxRetryAttempts", 3);
            if (hasBearer)
            {
                writer.WriteString("BearerToken", bearer);
                writer.WriteNull("ApiKey");
            }
            else
            {
                writer.WriteNull("BearerToken");
                writer.WriteString("ApiKey", api);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        File.WriteAllBytes(settingsPath, stream.ToArray());
        HardenDirectoryAcl(installDir);
        HardenFileAcl(settingsPath);

        WriteRegistryMirror(
            @"SOFTWARE\AssetBee\Drone",
            RegistryHive.LocalMachine,
            endpointUri.AbsoluteUri,
            hasBearer ? bearer : "",
            hasApiKey ? api : "");
        try
        {
            WriteRegistryMirror(
                @"SOFTWARE\AssetBee\Drone",
                RegistryHive.CurrentUser,
                endpointUri.AbsoluteUri,
                hasBearer ? bearer : "",
                hasApiKey ? api : "");
        }
        catch
        {
        }

        foreach (string path in new[] { pendingPath, legacyPendingPath })
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try { File.Delete(path); } catch { /* best effort */ }
            }
        }

        return 0;
    }

    private static int UninstallRelated(Dictionary<string, string> options)
    {
        string products = Optional(options, "products");
        string endpoint = Optional(options, "endpoint");
        string ui = Optional(options, "ui");
        if (string.IsNullOrWhiteSpace(ui))
        {
            ui = "quiet";
        }

        string msiexec = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "msiexec.exe");

        foreach (string productCode in products.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var args = new StringBuilder();
            args.Append("/x ").Append(productCode).Append(' ');
            args.Append(ui.Equals("basic", StringComparison.OrdinalIgnoreCase) ? "/qb" : "/qn");
            args.Append(" /norestart");
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                args.Append(" ENDPOINT=").Append(endpoint);
            }

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = msiexec,
                Arguments = args.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Failed to start msiexec.");

            process.WaitForExit();
            if (process.ExitCode is not (0 or 1605 or 3010))
            {
                return process.ExitCode;
            }
        }

        return 0;
    }

    private static bool TryReadDroneConfig(
        string settingsPath,
        out string endpoint,
        out string bearer,
        out string apiKey)
    {
        endpoint = "";
        bearer = "";
        apiKey = "";

        if (!File.Exists(settingsPath))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!doc.RootElement.TryGetProperty("Drone", out JsonElement drone))
            {
                return false;
            }

            endpoint = GetString(drone, "Endpoint");
            bearer = GetString(drone, "BearerToken");
            apiKey = GetString(drone, "ApiKey");
            if (bearer == "null") bearer = "";
            if (apiKey == "null") apiKey = "";
            return !string.IsNullOrWhiteSpace(endpoint);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteRegistryMirror(
        string subKey,
        RegistryHive hive,
        string endpoint,
        string bearer,
        string apiKey)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey key = baseKey.CreateSubKey(subKey, writable: true);
        key.SetValue("Endpoint", endpoint);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            key.SetValue("BearerToken", bearer);
            key.DeleteValue("ApiKey", throwOnMissingValue: false);
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            key.SetValue("ApiKey", apiKey);
            key.DeleteValue("BearerToken", throwOnMissingValue: false);
        }
    }

    private static void HardenDirectoryAcl(string path)
    {
        var info = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static void HardenFileAcl(string path)
    {
        var info = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required --{name}");

    private static string Optional(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) ? value : "";

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
