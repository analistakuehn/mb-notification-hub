using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NotificationHub.Platform.GoLiveChecks;

internal sealed class FileReceiptWriter : IReceiptWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // Receipt values are controlled and the document is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async ValueTask WriteAsync(
        string path,
        GoLiveReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(receipt);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(receipt, SerializerOptions)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            + "\n";
        await File.WriteAllTextAsync(
            fullPath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }
}
