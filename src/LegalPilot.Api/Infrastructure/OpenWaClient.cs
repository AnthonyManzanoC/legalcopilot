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
            "LegalPilot:OpenWa:WebhookSecret"
        }.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();

        return missing.Length == 0
            ? new OpenWaReadiness(true, "Configured", "OpenWA listo para envio y recepcion con secreto de webhook.", missing)
            : new OpenWaReadiness(false, "ConfigurationMissing", "OpenWA necesita URL, API key y secreto de webhook para operar en produccion.", missing);
    }

    public async Task<OpenWaSendResult> SendMessageAsync(string to, string body, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["LegalPilot:OpenWa:BaseUrl"];
        var apiKey = configuration["LegalPilot:OpenWa:ApiKey"];

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("OpenWA is not configured. WhatsApp message to {To} was not sent.", to);
            return new OpenWaSendResult(false, "ProviderNotConfigured", null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/messages/send");
        request.Headers.Add("X-API-Key", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new { to, message = body }, _json), Encoding.UTF8, "application/json");
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
}
