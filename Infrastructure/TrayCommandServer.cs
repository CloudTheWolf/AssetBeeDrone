using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AssetBeeDrone.Services;

namespace AssetBeeDrone.Infrastructure;

/// <summary>
/// Windows-only named-pipe server for the user-session tray app (status + Sync Now).
/// </summary>
public sealed class TrayCommandServer(
    IInventorySyncController syncController,
    ILogger<TrayCommandServer> logger) : BackgroundService
{
    public const string PipeName = "AssetBeeDrone";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        logger.LogInformation("Tray command pipe listening on {PipeName}", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                await server.WaitForConnectionAsync(stoppingToken);
                await HandleClientAsync(server, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Tray command pipe connection failed");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync();
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateServer()
    {
        PipeSecurity security = new();
        SecurityIdentifier authenticatedUsers =
            new(WellKnownSidType.AuthenticatedUserSid, null);
        SecurityIdentifier network = new(WellKnownSidType.NetworkSid, null);
        security.AddAccessRule(new PipeAccessRule(
            authenticatedUsers,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            network,
            PipeAccessRights.FullControl,
            AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using StreamWriter writer = new(server, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        string? line = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line))
        {
            await WriteStatusAsync(writer, syncController.GetStatus(), "Empty request.", cancellationToken);
            return;
        }

        TrayCommand? command;
        try
        {
            command = JsonSerializer.Deserialize(line, TrayJsonContext.Default.TrayCommand);
        }
        catch (JsonException)
        {
            await WriteStatusAsync(writer, syncController.GetStatus(), "Invalid JSON request.", cancellationToken);
            return;
        }

        if (command is null || string.IsNullOrWhiteSpace(command.Op))
        {
            await WriteStatusAsync(writer, syncController.GetStatus(), "Missing op.", cancellationToken);
            return;
        }

        if (command.Op.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStatusAsync(writer, syncController.GetStatus(), null, cancellationToken);
            return;
        }

        if (command.Op.Equals("sync", StringComparison.OrdinalIgnoreCase))
        {
            InventorySyncStatus status = await syncController.RequestSyncAsync(cancellationToken);
            string? message = status.Busy
                ? "Sync already in progress."
                : status.LastError is null
                    ? "Sync completed."
                    : "Sync finished with errors.";
            await WriteStatusAsync(writer, status, message, cancellationToken);
            return;
        }

        await WriteStatusAsync(writer, syncController.GetStatus(), $"Unknown op '{command.Op}'.", cancellationToken);
    }

    private static async Task WriteStatusAsync(
        StreamWriter writer,
        InventorySyncStatus status,
        string? message,
        CancellationToken cancellationToken)
    {
        TrayStatusResponse response = new(
            status.LastRunUtc,
            status.LastSuccessUtc,
            status.LastError,
            status.Running,
            status.Busy,
            message);
        string json = JsonSerializer.Serialize(response, TrayJsonContext.Default.TrayStatusResponse);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
    }
}
