using LegalPilot.Api.Domain;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LegalPilot.Api.Application;

namespace LegalPilot.Api.Infrastructure;

public sealed record IntegrationReadiness(
    MailProvider Provider,
    bool Configured,
    string Status,
    string Message,
    string[] RequiredSettings);

public sealed record MailboxSyncResult(
    bool Success,
    string Status,
    string Message,
    DateTimeOffset? NextAttemptAt);

public interface IEmailConnector
{
    MailProvider Provider { get; }
    IntegrationReadiness GetReadiness();
    Task<MailboxSyncResult> SyncAsync(MailboxConnection mailbox, CancellationToken cancellationToken);
}

public sealed class EmailConnectorRegistry(IEnumerable<IEmailConnector> connectors)
{
    private readonly IReadOnlyDictionary<MailProvider, IEmailConnector> _connectors =
        connectors.ToDictionary(c => c.Provider);

    public IEmailConnector Get(MailProvider provider)
    {
        return _connectors.TryGetValue(provider, out var connector)
            ? connector
            : throw new InvalidOperationException($"No existe conector para {provider}.");
    }

    public IReadOnlyList<IntegrationReadiness> Status()
    {
        return _connectors.Values
            .OrderBy(c => c.Provider.ToString())
            .Select(c => c.GetReadiness())
            .ToArray();
    }
}

public sealed class GmailEmailConnector(
    IConfiguration configuration,
    ILogger<GmailEmailConnector> logger,
    LegalPilotStore store,
    LegalWorkflowService workflow,
    SecretProtector secretProtector,
    IHttpClientFactory httpClientFactory) : IEmailConnector
{
    public MailProvider Provider => MailProvider.Gmail;

    public IntegrationReadiness GetReadiness()
    {
        var missing = Missing("LegalPilot:Gmail:ClientId", "LegalPilot:Gmail:ClientSecret", "LegalPilot:Gmail:RedirectUri");
        if (!secretProtector.Configured)
        {
            missing = [.. missing, "LEGALPILOT_DATA_PROTECTION_KEY"];
        }

        return missing.Length == 0
            ? new IntegrationReadiness(Provider, true, "OAuthReady", "Gmail listo para OAuth, refresh token cifrado y sincronizacion de mensajes via Gmail API.", missing)
            : new IntegrationReadiness(Provider, false, "ConfigurationMissing", "Gmail requiere credenciales OAuth y clave de cifrado antes de descargar mensajes reales.", missing);
    }

    public async Task<MailboxSyncResult> SyncAsync(MailboxConnection mailbox, CancellationToken cancellationToken)
    {
        var readiness = GetReadiness();
        if (!readiness.Configured)
        {
            return new MailboxSyncResult(false, readiness.Status, readiness.Message, DateTimeOffset.UtcNow.AddHours(1));
        }

        var access = await ResolveAccessTokenAsync(mailbox, cancellationToken);
        if (!access.Success)
        {
            return new MailboxSyncResult(false, access.Status, access.Message, DateTimeOffset.UtcNow.AddMinutes(30));
        }

        var client = httpClientFactory.CreateClient("gmail");
        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults=10&q=newer_than:14d");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        using var listResponse = await client.SendAsync(listRequest, cancellationToken);
        var listPayload = await listResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!listResponse.IsSuccessStatusCode)
        {
            logger.LogWarning("Gmail list failed for {Mailbox} with {Status}.", mailbox.Email, listResponse.StatusCode);
            return new MailboxSyncResult(false, "ProviderSyncFailed", $"Gmail rechazo messages.list: {listResponse.StatusCode}.", DateTimeOffset.UtcNow.AddMinutes(15));
        }

        using var listDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(listPayload) ? "{}" : listPayload);
        if (!listDocument.RootElement.TryGetProperty("messages", out var messages))
        {
            return new MailboxSyncResult(true, "Synced", "Gmail no devolvio mensajes nuevos.", DateTimeOffset.UtcNow.AddMinutes(15));
        }

        var ingested = 0;
        var skipped = 0;
        foreach (var message in messages.EnumerateArray())
        {
            var id = message.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var exists = store.Read(() => store.Emails.Any(e =>
                e.TenantId == mailbox.TenantId &&
                e.Provider == Provider &&
                e.ExternalMessageId.Equals(id, StringComparison.OrdinalIgnoreCase)));
            if (exists)
            {
                skipped++;
                continue;
            }

            using var detailRequest = new HttpRequestMessage(HttpMethod.Get, $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(id)}?format=full");
            detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
            using var detailResponse = await client.SendAsync(detailRequest, cancellationToken);
            var detailPayload = await detailResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!detailResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Gmail message.get failed for {Mailbox} message {MessageId} with {Status}.", mailbox.Email, id, detailResponse.StatusCode);
                continue;
            }

            using var detailDocument = JsonDocument.Parse(detailPayload);
            var envelope = GmailMessageToEnvelope(id, detailDocument.RootElement);
            workflow.IngestWebhook(mailbox.TenantId, Provider, envelope);
            ingested++;
        }

        return new MailboxSyncResult(true, "Synced", $"Gmail sincronizado. Nuevos: {ingested}. Duplicados omitidos: {skipped}.", DateTimeOffset.UtcNow.AddMinutes(15));
    }

    private async Task<TokenResolution> ResolveAccessTokenAsync(MailboxConnection mailbox, CancellationToken cancellationToken)
    {
        var credential = store.Read(() => store.OAuthTokenCredentials
            .Where(t => t.TenantId == mailbox.TenantId && t.MailboxConnectionId == mailbox.Id && t.Provider == Provider && t.Status != "Revoked")
            .OrderByDescending(t => t.UpdatedAt)
            .FirstOrDefault());

        if (credential is null)
        {
            return new TokenResolution(false, "OAuthTokenMissing", "Conecte OAuth para este buzon antes de sincronizar.", null);
        }

        if (credential.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return new TokenResolution(true, "OAuthTokenActive", "Token OAuth activo.", secretProtector.Unprotect(credential.AccessTokenCiphertext));
        }

        if (string.IsNullOrWhiteSpace(credential.RefreshTokenCiphertext))
        {
            return new TokenResolution(false, "OAuthRefreshRequired", "El access token expiro y no hay refresh token guardado.", null);
        }

        try
        {
            var refreshToken = secretProtector.Unprotect(credential.RefreshTokenCiphertext);
            var refreshed = await RefreshAsync(refreshToken, cancellationToken);
            var updated = credential with
            {
                AccessTokenCiphertext = secretProtector.Protect(refreshed.AccessToken),
                RefreshTokenCiphertext = string.IsNullOrWhiteSpace(refreshed.RefreshToken) ? credential.RefreshTokenCiphertext : secretProtector.Protect(refreshed.RefreshToken),
                TokenType = refreshed.TokenType,
                Scopes = refreshed.Scopes.Length == 0 ? credential.Scopes : refreshed.Scopes,
                ExpiresAt = refreshed.ExpiresAt,
                Status = "Active",
                UpdatedAt = DateTimeOffset.UtcNow
            };

            store.Write(() =>
            {
                var index = store.OAuthTokenCredentials.FindIndex(t => t.Id == credential.Id);
                if (index >= 0)
                {
                    store.OAuthTokenCredentials[index] = updated;
                }
            });

            return new TokenResolution(true, "OAuthTokenRefreshed", "Token OAuth renovado.", refreshed.AccessToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gmail refresh failed for mailbox {MailboxId}.", mailbox.Id);
            return new TokenResolution(false, "OAuthRefreshFailed", "No se pudo renovar el token Gmail. Revise consentimiento y credenciales.", null);
        }
    }

    private async Task<RefreshedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = Required("LegalPilot:Gmail:ClientId"),
            ["client_secret"] = Required("LegalPilot:Gmail:ClientSecret"),
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };

        var client = httpClientFactory.CreateClient("gmail-oauth");
        using var response = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(values), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gmail refresh rejected: {response.StatusCode}");
        }

        return EmailConnectorHelpers.ParseRefreshedToken(payload);
    }

    private WebhookEmailEnvelope GmailMessageToEnvelope(string id, JsonElement root)
    {
        var payload = root.TryGetProperty("payload", out var payloadElement) ? payloadElement : default;
        var subject = Header(payload, "Subject") ?? "(sin asunto)";
        var sender = Header(payload, "From") ?? "gmail";
        var recipients = EmailConnectorHelpers.SplitRecipients(Header(payload, "To"));
        var body = ExtractGmailBody(payload);
        if (string.IsNullOrWhiteSpace(body) && root.TryGetProperty("snippet", out var snippet))
        {
            body = snippet.GetString() ?? string.Empty;
        }

        var receivedAt = DateTimeOffset.UtcNow;
        if (root.TryGetProperty("internalDate", out var internalDate) &&
            long.TryParse(internalDate.GetString(), out var milliseconds))
        {
            receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }

        return new WebhookEmailEnvelope(id, subject, sender, recipients, string.IsNullOrWhiteSpace(body) ? subject : body, "gmail-api", receivedAt);
    }

    private static string? Header(JsonElement payload, string name)
    {
        if (payload.ValueKind == JsonValueKind.Undefined ||
            !payload.TryGetProperty("headers", out var headers))
        {
            return null;
        }

        foreach (var header in headers.EnumerateArray())
        {
            if (header.TryGetProperty("name", out var headerName) &&
                string.Equals(headerName.GetString(), name, StringComparison.OrdinalIgnoreCase) &&
                header.TryGetProperty("value", out var value))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static string ExtractGmailBody(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (payload.TryGetProperty("body", out var body) &&
            body.TryGetProperty("data", out var data))
        {
            return EmailConnectorHelpers.DecodeBase64Url(data.GetString());
        }

        if (payload.TryGetProperty("parts", out var parts))
        {
            var builder = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                var mimeType = part.TryGetProperty("mimeType", out var mime) ? mime.GetString() : null;
                if (mimeType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    builder.AppendLine(ExtractGmailBody(part));
                }
                else if (part.TryGetProperty("parts", out _))
                {
                    builder.AppendLine(ExtractGmailBody(part));
                }
            }

            return builder.ToString().Trim();
        }

        return string.Empty;
    }

    private string[] Missing(params string[] keys)
    {
        return keys.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();
    }

    private string Required(string key)
    {
        return string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"Configuracion faltante: {key}.")
            : configuration[key]!;
    }
}

public sealed class MicrosoftGraphEmailConnector(
    IConfiguration configuration,
    ILogger<MicrosoftGraphEmailConnector> logger,
    LegalPilotStore store,
    LegalWorkflowService workflow,
    SecretProtector secretProtector,
    IHttpClientFactory httpClientFactory) : IEmailConnector
{
    public MailProvider Provider => MailProvider.Outlook;

    public IntegrationReadiness GetReadiness()
    {
        var missing = Missing("LegalPilot:Microsoft:ClientId", "LegalPilot:Microsoft:ClientSecret", "LegalPilot:Microsoft:TenantId", "LegalPilot:Microsoft:RedirectUri");
        if (!secretProtector.Configured)
        {
            missing = [.. missing, "LEGALPILOT_DATA_PROTECTION_KEY"];
        }

        return missing.Length == 0
            ? new IntegrationReadiness(Provider, true, "OAuthReady", "Microsoft Graph listo para OAuth, refresh token cifrado y lectura de mensajes/calendario.", missing)
            : new IntegrationReadiness(Provider, false, "ConfigurationMissing", "Microsoft Graph requiere credenciales OAuth y clave de cifrado antes de descargar mensajes reales.", missing);
    }

    public async Task<MailboxSyncResult> SyncAsync(MailboxConnection mailbox, CancellationToken cancellationToken)
    {
        var readiness = GetReadiness();
        if (!readiness.Configured)
        {
            return new MailboxSyncResult(false, readiness.Status, readiness.Message, DateTimeOffset.UtcNow.AddHours(1));
        }

        var access = await ResolveAccessTokenAsync(mailbox, cancellationToken);
        if (!access.Success)
        {
            return new MailboxSyncResult(false, access.Status, access.Message, DateTimeOffset.UtcNow.AddMinutes(30));
        }

        var client = httpClientFactory.CreateClient("graph");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/messages?$top=10&$select=id,subject,from,toRecipients,bodyPreview,body,receivedDateTime&$orderby=receivedDateTime desc");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Graph messages failed for {Mailbox} with {Status}.", mailbox.Email, response.StatusCode);
            return new MailboxSyncResult(false, "ProviderSyncFailed", $"Microsoft Graph rechazo messages: {response.StatusCode}.", DateTimeOffset.UtcNow.AddMinutes(15));
        }

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
        if (!document.RootElement.TryGetProperty("value", out var messages))
        {
            return new MailboxSyncResult(true, "Synced", "Microsoft Graph no devolvio mensajes nuevos.", DateTimeOffset.UtcNow.AddMinutes(15));
        }

        var ingested = 0;
        var skipped = 0;
        foreach (var item in messages.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var exists = store.Read(() => store.Emails.Any(e =>
                e.TenantId == mailbox.TenantId &&
                e.Provider == Provider &&
                e.ExternalMessageId.Equals(id, StringComparison.OrdinalIgnoreCase)));
            if (exists)
            {
                skipped++;
                continue;
            }

            workflow.IngestWebhook(mailbox.TenantId, Provider, GraphMessageToEnvelope(id, item));
            ingested++;
        }

        return new MailboxSyncResult(true, "Synced", $"Microsoft Graph sincronizado. Nuevos: {ingested}. Duplicados omitidos: {skipped}.", DateTimeOffset.UtcNow.AddMinutes(15));
    }

    private async Task<TokenResolution> ResolveAccessTokenAsync(MailboxConnection mailbox, CancellationToken cancellationToken)
    {
        var credential = store.Read(() => store.OAuthTokenCredentials
            .Where(t => t.TenantId == mailbox.TenantId && t.MailboxConnectionId == mailbox.Id && t.Provider == Provider && t.Status != "Revoked")
            .OrderByDescending(t => t.UpdatedAt)
            .FirstOrDefault());

        if (credential is null)
        {
            return new TokenResolution(false, "OAuthTokenMissing", "Conecte OAuth para este buzon antes de sincronizar.", null);
        }

        if (credential.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return new TokenResolution(true, "OAuthTokenActive", "Token OAuth activo.", secretProtector.Unprotect(credential.AccessTokenCiphertext));
        }

        if (string.IsNullOrWhiteSpace(credential.RefreshTokenCiphertext))
        {
            return new TokenResolution(false, "OAuthRefreshRequired", "El access token expiro y no hay refresh token guardado.", null);
        }

        try
        {
            var refreshToken = secretProtector.Unprotect(credential.RefreshTokenCiphertext);
            var refreshed = await RefreshAsync(refreshToken, cancellationToken);
            var updated = credential with
            {
                AccessTokenCiphertext = secretProtector.Protect(refreshed.AccessToken),
                RefreshTokenCiphertext = string.IsNullOrWhiteSpace(refreshed.RefreshToken) ? credential.RefreshTokenCiphertext : secretProtector.Protect(refreshed.RefreshToken),
                TokenType = refreshed.TokenType,
                Scopes = refreshed.Scopes.Length == 0 ? credential.Scopes : refreshed.Scopes,
                ExpiresAt = refreshed.ExpiresAt,
                Status = "Active",
                UpdatedAt = DateTimeOffset.UtcNow
            };

            store.Write(() =>
            {
                var index = store.OAuthTokenCredentials.FindIndex(t => t.Id == credential.Id);
                if (index >= 0)
                {
                    store.OAuthTokenCredentials[index] = updated;
                }
            });

            return new TokenResolution(true, "OAuthTokenRefreshed", "Token OAuth renovado.", refreshed.AccessToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Graph refresh failed for mailbox {MailboxId}.", mailbox.Id);
            return new TokenResolution(false, "OAuthRefreshFailed", "No se pudo renovar el token Microsoft Graph. Revise consentimiento y credenciales.", null);
        }
    }

    private async Task<RefreshedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = Required("LegalPilot:Microsoft:ClientId"),
            ["client_secret"] = Required("LegalPilot:Microsoft:ClientSecret"),
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = string.Join(' ', OAuthService.Scopes(Provider))
        };

        var tenant = configuration["LegalPilot:Microsoft:TenantId"] ?? "common";
        var client = httpClientFactory.CreateClient("graph-oauth");
        using var response = await client.PostAsync($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token", new FormUrlEncodedContent(values), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Graph refresh rejected: {response.StatusCode}");
        }

        return EmailConnectorHelpers.ParseRefreshedToken(payload);
    }

    private static WebhookEmailEnvelope GraphMessageToEnvelope(string id, JsonElement item)
    {
        var subject = item.TryGetProperty("subject", out var subjectElement) ? subjectElement.GetString() ?? "(sin asunto)" : "(sin asunto)";
        var sender = "microsoft-graph";
        if (item.TryGetProperty("from", out var from) &&
            from.TryGetProperty("emailAddress", out var emailAddress) &&
            emailAddress.TryGetProperty("address", out var address))
        {
            sender = address.GetString() ?? sender;
        }

        var recipients = new List<string>();
        if (item.TryGetProperty("toRecipients", out var recipientArray))
        {
            foreach (var recipient in recipientArray.EnumerateArray())
            {
                if (recipient.TryGetProperty("emailAddress", out var recipientEmail) &&
                    recipientEmail.TryGetProperty("address", out var recipientAddress) &&
                    recipientAddress.GetString() is { Length: > 0 } value)
                {
                    recipients.Add(value);
                }
            }
        }

        var body = item.TryGetProperty("bodyPreview", out var preview)
            ? preview.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(body) &&
            item.TryGetProperty("body", out var bodyElement) &&
            bodyElement.TryGetProperty("content", out var content))
        {
            body = content.GetString() ?? string.Empty;
        }

        var receivedAt = item.TryGetProperty("receivedDateTime", out var received) &&
                         DateTimeOffset.TryParse(received.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        return new WebhookEmailEnvelope(id, subject, sender, recipients.ToArray(), string.IsNullOrWhiteSpace(body) ? subject : body, "microsoft-graph-api", receivedAt);
    }

    private string[] Missing(params string[] keys)
    {
        return keys.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();
    }

    private string Required(string key)
    {
        return string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"Configuracion faltante: {key}.")
            : configuration[key]!;
    }
}

public static class WebhookSecurity
{
    public static void RequireSharedSecret(HttpRequest request, IConfiguration configuration, string key, string integrationName)
    {
        var expected = configuration[key];
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        var supplied = request.Headers["X-LegalPilot-Webhook-Secret"].FirstOrDefault()
            ?? request.Headers["X-Webhook-Secret"].FirstOrDefault();
        if (!string.Equals(expected, supplied, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Webhook {integrationName} no autorizado.");
        }
    }

    public static void RequireMicrosoftClientState(JsonElement body, IConfiguration configuration)
    {
        var expected = configuration["LegalPilot:Microsoft:WebhookClientState"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        if (!body.TryGetProperty("value", out var values))
        {
            throw new UnauthorizedAccessException("Webhook Microsoft Graph sin value.");
        }

        foreach (var notification in values.EnumerateArray())
        {
            var supplied = notification.TryGetProperty("clientState", out var clientState)
                ? clientState.GetString()
                : null;
            if (!string.Equals(expected, supplied, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Webhook Microsoft Graph clientState invalido.");
            }
        }
    }
}

internal sealed record TokenResolution(bool Success, string Status, string Message, string? Token);

internal sealed record RefreshedToken(
    string AccessToken,
    string? RefreshToken,
    string TokenType,
    string[] Scopes,
    DateTimeOffset ExpiresAt);

internal static class EmailConnectorHelpers
{
    public static RefreshedToken ParseRefreshedToken(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("El proveedor no devolvio access_token al renovar.");
        }

        var refreshToken = root.TryGetProperty("refresh_token", out var refreshTokenElement)
            ? refreshTokenElement.GetString()
            : null;
        var expiresIn = root.TryGetProperty("expires_in", out var expiresInElement) && expiresInElement.TryGetInt32(out var seconds)
            ? seconds
            : 3600;
        var tokenType = root.TryGetProperty("token_type", out var tokenTypeElement)
            ? tokenTypeElement.GetString() ?? "Bearer"
            : "Bearer";
        var scopes = root.TryGetProperty("scope", out var scopeElement)
            ? (scopeElement.GetString() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return new RefreshedToken(accessToken, refreshToken, tokenType, scopes, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)));
    }

    public static string DecodeBase64Url(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return string.Empty;
        }

        var padded = data.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    public static string[] SplitRecipients(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
