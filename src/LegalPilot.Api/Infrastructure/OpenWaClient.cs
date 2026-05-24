using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LegalPilot.Api.Infrastructure;

public sealed record OpenWaSendResult(bool Success, string ProviderStatus, string? ProviderMessageId);

public sealed record OpenWaReadiness(
    bool Configured,
    string Status,
    string Message,
    string[] RequiredSettings);

public sealed class OpenWaClient(HttpClient httpClient, IConfiguration configuration, ILogger<OpenWaClient> logger)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public OpenWaReadiness GetReadiness()
    {
        var missing = new[]
        {
            "LegalPilot:OpenWa:BaseUrl",
            "LegalPilot:OpenWa:ApiKey",
            "LegalPilot:OpenWa:SessionId",
            "LegalPilot:OpenWa:WebhookSecret"
        }.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();

        return missing.Length == 0
            ? new OpenWaReadiness(true, "Configured", "OpenWA listo para envio/recepcion con sesion, API key y secreto de webhook.", missing)
            : new OpenWaReadiness(false, "ConfigurationMissing", "OpenWA necesita URL, API key, sessionId y secreto de webhook para operar en produccion.", missing);
    }

    public async Task<OpenWaSendResult> SendMessageAsync(string to, string body, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["LegalPilot:OpenWa:BaseUrl"];
        var apiKey = configuration["LegalPilot:OpenWa:ApiKey"];
        var sessionId = configuration["LegalPilot:OpenWa:SessionId"];

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("OpenWA is not configured. WhatsApp message to {To} was not sent.", to);
            return new OpenWaSendResult(false, "ProviderNotConfigured", null);
        }

        var url = string.IsNullOrWhiteSpace(sessionId)
            ? $"{baseUrl.TrimEnd('/')}/api/messages/send"
            : $"{baseUrl.TrimEnd('/')}/api/sessions/{Uri.EscapeDataString(sessionId)}/messages/send-text";
        var payloadJson = string.IsNullOrWhiteSpace(sessionId)
            ? JsonSerializer.Serialize(new { to, message = body }, _json)
            : JsonSerializer.Serialize(new { chatId = ToChatId(to), text = body }, _json);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-API-Key", apiKey);
        request.Content = new StringContent(payloadJson, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("OpenWA send failed with {Status}: {Content}", response.StatusCode, content);
            return new OpenWaSendResult(false, response.StatusCode.ToString(), null);
        }

        return new OpenWaSendResult(true, "Sent", content);
    }

    private static string ToChatId(string to)
    {
        if (to.Contains('@'))
        {
            return to;
        }

        var digits = new StringBuilder();
        foreach (var c in to)
        {
            if (char.IsDigit(c))
            {
                digits.Append(c);
            }
        }

        return $"{digits}@c.us";
    }
}
