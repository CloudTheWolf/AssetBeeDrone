using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetBeeDrone.Tray;

internal static class TrayPipeClient
{
    public const string PipeName = "AssetBeeDrone";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<TrayStatusResponse?> SendAsync(string op, CancellationToken cancellationToken)
    {
        await using NamedPipeClientStream client = new(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using CancellationTokenSource connectCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(2));
        await client.ConnectAsync(connectCts.Token);

        await using StreamWriter writer = new(client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        using StreamReader reader = new(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        string request = JsonSerializer.Serialize(new TrayCommand(op), JsonOptions);
        await writer.WriteLineAsync(request.AsMemory(), cancellationToken);
        string? line = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return JsonSerializer.Deserialize<TrayStatusResponse>(line, JsonOptions);
    }
}

internal sealed record TrayCommand(string Op);

internal sealed record TrayStatusResponse(
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? LastSuccessUtc,
    string? LastError,
    bool Running,
    bool Busy,
    string? Message);
