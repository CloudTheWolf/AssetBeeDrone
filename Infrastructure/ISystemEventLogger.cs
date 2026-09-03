namespace AssetBeeDrone.Infrastructure;

/// <summary>
/// Writes notable failures to the host OS event facility
/// (Windows Event Viewer, Linux journal/syslog, macOS syslog).
/// </summary>
public interface ISystemEventLogger
{
    void WriteError(string title, string details);
}
