using System.Net;
using System.Text.Json;

namespace SistemskoProjekat;

public static class Utils
{
    public static string BuildApiUrl(string searchType, string searchValue)
    {
        string encodedValue = Uri.EscapeDataString(searchValue);
        if (searchType == "author")
        {
            return $"https://api.artic.edu/api/v1/artworks/search?query[term][artist_title]={encodedValue}";
        }

        return $"https://api.artic.edu/api/v1/artworks/search?q={encodedValue}";
    }

    public static string NormalizeInput(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    public static bool IsEmptyArtworkResult(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("data", out var dataElement))
            {
                return false;
            }

            return dataElement.ValueKind == JsonValueKind.Array && dataElement.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task SendResponseAsync(HttpListenerContext context, string body, HttpStatusCode status)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";

        using var writer = new StreamWriter(context.Response.OutputStream);

        await writer.WriteAsync(body);
    }
}
