using System.Text.Json;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;

/// <summary>Projection helpers for JSON documents stored as raw text.</summary>
internal static class JsonProjections
{
    internal static JsonElement? ParseOrNull(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
