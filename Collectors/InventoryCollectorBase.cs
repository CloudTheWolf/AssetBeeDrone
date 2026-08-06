using System.Text;
using AssetBeeDrone.Models;

namespace AssetBeeDrone.Collectors;

public abstract class InventoryCollectorBase
{
    /// <summary>
    /// Normalizes manufacturer/SKU values by replacing underscores (and other
    /// separators) and converting the result to camelCase. Example:
    /// "Dell_Inc." -> "dellInc", "Latitude_5520" -> "latitude5520".
    /// </summary>
    protected static string? ToCamelCaseIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value
            .Replace('_', ' ')
            .Split([' ', '-', '.', '/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        StringBuilder builder = new();
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (part.Length == 0)
            {
                continue;
            }

            if (index == 0)
            {
                builder.Append(part.ToLowerInvariant());
                continue;
            }

            builder.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
            {
                builder.Append(part[1..].ToLowerInvariant());
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    protected static ProbeValue<string> AvailableCamelCaseIdentifier(
        string? value,
        string unavailableDetail)
    {
        string? normalized = ToCamelCaseIdentifier(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? ProbeValue<string>.Unavailable(unavailableDetail)
            : ProbeValue<string>.Available(normalized);
    }

    protected static ProbeValue<IReadOnlyList<DiskInfo>> CollectMountedDisks()
    {
        try
        {
            List<DiskInfo> disks = [];
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                disks.Add(new DiskInfo(
                    drive.Name,
                    drive.RootDirectory.FullName,
                    (ulong)drive.TotalSize,
                    (ulong)drive.AvailableFreeSpace,
                    drive.DriveFormat));
            }

            return ProbeValue<IReadOnlyList<DiskInfo>>.Available(disks);
        }
        catch (UnauthorizedAccessException)
        {
            return new(ProbeStatus.AccessDenied, Detail: "Access to disk information was denied.");
        }
        catch (IOException exception)
        {
            return ProbeValue<IReadOnlyList<DiskInfo>>.Error(exception.Message);
        }
    }

    protected static string? ReadFirstExistingFile(params string[] paths)
    {
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    string value = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Try the next standard source.
            }
            catch (IOException)
            {
                // Try the next standard source.
            }
        }

        return null;
    }
}
