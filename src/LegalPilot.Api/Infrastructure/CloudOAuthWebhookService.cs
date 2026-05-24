using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using LegalPilot.Api.Application;
using LegalPilot.Api.Domain;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace LegalPilot.Api.Infrastructure;

public sealed class LegalPilotCloudOAuthOptions
{
    public string Issuer { get; set; } = "legalpilot-ecuador";
    public GmailCloudOAuthOptions Gmail { get; set; } = new();
    public MicrosoftCloudOAuthOptions Microsoft { get; set; } = new();
}

public sealed class GmailCloudOAuthOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string PubSubTopicName { get; set; } = "projects/legalcopilot-497022/topics/notificaciones-gmail";
}

public sealed class MicrosoftCloudOAuthOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string TenantId { get; set; } = "common";
    public string RedirectUri { get; set; } = string.Empty;
    public string WebhookClientState { get; set; } = string.Empty;
    public string WebhookNotificationUrl { get; set; } = "https://legalcopilot-ryhr.onrender.com/api/webhooks/microsoft";
}

public sealed record CloudOAuthStartResult(MailProvider Provider, string Email, string AuthorizationUrl, string State, DateTimeOffset ExpiresAt, string[] Scopes);

public sealed record CloudOAuthCallbackResult(
    bool Accepted,
    MailProvider Provider,
    string Email,
    string Status,
    string Message,
    Guid? MailboxId,
    DateTimeOffset? TokenExpiresAt,
    string? SubscriptionId,
    DateTimeOffset? SubscriptionExpiresAt);

public interface ICloudOAuthWebhookService
{
    CloudOAuthStartResult Start(AuthPrincipal principal, MailProvider provider, string email);
    Task<CloudOAuthCallbackResult> CompleteAsync(MailProvider provider, string? state, string? code, string? error, CancellationToken cancellationToken);
}

public sealed class CloudOAuthWebhookService(
    LegalPilotStore store,
    SecretProtector secretProtector,
    IOptions<LegalPilotCloudOAuthOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<CloudOAuthWebhookService> logger) : ICloudOAuthWebhookService
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public CloudOAuthStartResult Start(AuthPrincipal principal, MailProvider provider, string email)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
        email = InputGuard.Email(email);
        EnsureConfigured(provider);

        if (!secretProtector.Configured)
        {
            throw new InvalidOperationException("Configure LegalPilot:Security:DataProtectionKey o LEGALPILOT_DATA_PROTECTION_KEY antes de iniciar OAuth.");
        }

        var scopes = OAuthService.Scopes(provider);
        var state = TokenService.RandomToken(32);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var ticket = new OAuthStateTicket(
            Guid.NewGuid(),
            principal.TenantId,
            principal.UserId,
            provider,
            email,
            TokenService.Sha256(state),
            expiresAt,
            false,
            DateTimeOffset.UtcNow);

        store.Write(() => store.OAuthStateTickets.Add(ticket));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.SecurityEvent, nameof(OAuthStateTicket), ticket.Id.ToString(), $"OAuth cloud iniciado para {provider} / {email}.");

        return new CloudOAuthStartResult(provider, email, BuildAuthorizationUrl(provider, state, scopes), state, expiresAt, scopes);
    }

    public async Task<CloudOAuthCallbackResult> CompleteAsync(MailProvider provider, string? state, string? code, string? error, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new UnauthorizedAccessException("Estado OAuth ausente.");
        }

        var stateHash = TokenService.Sha256(state);
        var ticket = store.Write(() =>
        {
            var index = store.OAuthStateTickets.FindIndex(t =>
                t.Provider == provider &&
                t.StateHash == stateHash &&
                !t.Used &&
                t.ExpiresAt > DateTimeOffset.UtcNow);

            if (index < 0)
            {
                throw new UnauthorizedAccessException("Estado OAuth invalido o expirado.");
            }

            var found = store.OAuthStateTickets[index];
            store.OAuthStateTickets[index] = found with { Used = true };
            return found;
        });

        if (!string.IsNullOrWhiteSpace(error))
        {
            store.Audit(ticket.TenantId, ticket.UserId, AuditAction.SecurityEvent, nameof(OAuthStateTicket), ticket.Id.ToString(), $"OAuth rechazado por proveedor: {error}.");
            return new CloudOAuthCallbackResult(false, provider, ticket.Email, "OAuthRejected", $"Proveedor devolvio error: {error}.", null, null, null, null);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Codigo OAuth ausente.");
        }

        CloudTokenExchange token;
        try
        {
            token = provider == MailProvider.Gmail
                ? await ExchangeGoogleCodeAsync(ticket.Email, code, cancellationToken)
                : await ExchangeMicrosoftCodeAsync(code, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cloud OAuth token exchange failed for {Provider}.", provider);
            store.Audit(ticket.TenantId, ticket.UserId, AuditAction.SecurityEvent, nameof(OAuthStateTicket), ticket.Id.ToString(), $"OAuth token exchange fallo para {provider}.");
            return new CloudOAuthCallbackResult(false, provider, ticket.Email, "OAuthTokenExchangeFailed", "Callback validado, pero el proveedor rechazo el intercambio del code.", null, null, null, null);
        }

        var stored = StoreCredential(ticket, token);
        CloudSubscriptionResult subscription;
        try
        {
            subscription = provider == MailProvider.Gmail
                ? await StartGmailWatchAsync(token.AccessToken, cancellationToken)
                : await StartMicrosoftSubscriptionAsync(token.AccessToken, cancellationToken);
            UpdateMailboxSubscription(stored.Mailbox, subscription);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cloud webhook subscription failed for {Provider} mailbox {MailboxId}.", provider, stored.Mailbox.Id);
            store.Audit(ticket.TenantId, ticket.UserId, AuditAction.SecurityEvent, nameof(MailboxConnection), stored.Mailbox.Id.ToString(), $"Token OAuth guardado, pero la suscripcion webhook fallo para {provider}.");
            return new CloudOAuthCallbackResult(
                true,
                provider,
                ticket.Email,
                "OAuthConnectedWebhookPending",
                "OAuth conectado y tokens cifrados. No se pudo crear webhook automaticamente; revise permisos, Pub/Sub/Graph y URL publica.",
                stored.Mailbox.Id,
                stored.Credential.ExpiresAt,
                null,
                null);
        }

        store.Audit(ticket.TenantId, ticket.UserId, AuditAction.SecurityEvent, nameof(MailboxConnection), stored.Mailbox.Id.ToString(), $"OAuth conectado y webhook creado para {provider}: {subscription.SubscriptionId}.");
        return new CloudOAuthCallbackResult(
            true,
            provider,
            ticket.Email,
            "OAuthConnectedWebhookActive",
            "OAuth conectado, tokens cifrados y suscripcion webhook creada.",
            stored.Mailbox.Id,
            stored.Credential.ExpiresAt,
            subscription.SubscriptionId,
            subscription.ExpiresAt);
    }

    private async Task<CloudTokenExchange> ExchangeGoogleCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        var gmail = options.Value.Gmail;
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = Required(gmail.ClientId, "LegalPilot:Gmail:ClientId"),
                ClientSecret = Required(gmail.ClientSecret, "LegalPilot:Gmail:ClientSecret")
            },
            Scopes = OAuthService.Scopes(MailProvider.Gmail)
        });

        var token = await flow.ExchangeCodeForTokenAsync(email, code, Required(gmail.RedirectUri, "LegalPilot:Gmail:RedirectUri"), cancellationToken);
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Google no devolvio access token.");
        }

        var expiresIn = token.ExpiresInSeconds ?? 3600;
        return new CloudTokenExchange(
            token.AccessToken,
            token.RefreshToken,
            "Bearer",
            OAuthService.Scopes(MailProvider.Gmail),
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)));
    }

    private async Task<CloudTokenExchange> ExchangeMicrosoftCodeAsync(string code, CancellationToken cancellationToken)
    {
        var microsoft = options.Value.Microsoft;
        var tenant = string.IsNullOrWhiteSpace(microsoft.TenantId) ? "common" : microsoft.TenantId;
        var values = new Dictionary<string, string>
        {
            ["client_id"] = Required(microsoft.ClientId, "LegalPilot:Microsoft:ClientId"),
            ["client_secret"] = Required(microsoft.ClientSecret, "LegalPilot:Microsoft:ClientSecret"),
            ["redirect_uri"] = Required(microsoft.RedirectUri, "LegalPilot:Microsoft:RedirectUri"),
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["scope"] = string.Join(' ', OAuthService.Scopes(MailProvider.Outlook))
        };

        using var response = await httpClientFactory.CreateClient("graph-oauth").PostAsync(
            $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
            new FormUrlEncodedContent(values),
            cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Microsoft token endpoint rejected authorization code: {response.StatusCode}");
        }

        var parsed = EmailConnectorHelpers.ParseRefreshedToken(payload);
        return new CloudTokenExchange(parsed.AccessToken, parsed.RefreshToken, parsed.TokenType, parsed.Scopes.Length == 0 ? OAuthService.Scopes(MailProvider.Outlook) : parsed.Scopes, parsed.ExpiresAt);
    }

    private (MailboxConnection Mailbox, OAuthTokenCredential Credential) StoreCredential(OAuthStateTicket ticket, CloudTokenExchange token)
    {
        return store.Write(() =>
        {
            var mailbox = store.Mailboxes.FirstOrDefault(m =>
                m.TenantId == ticket.TenantId &&
                m.Provider == ticket.Provider &&
                m.Email.Equals(ticket.Email, StringComparison.OrdinalIgnoreCase));

            if (mailbox is null)
            {
                mailbox = new MailboxConnection(
                    Guid.NewGuid(),
                    ticket.TenantId,
                    ticket.UserId,
                    ticket.Provider,
                    ticket.Email,
                    ticket.Email,
                    "Connected",
                    OAuthService.Scopes(ticket.Provider),
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null);
                store.Mailboxes.Add(mailbox);
            }
            else
            {
                var mailboxIndex = store.Mailboxes.FindIndex(m => m.Id == mailbox.Id);
                store.Mailboxes[mailboxIndex] = mailbox with
                {
                    Status = "Connected",
                    LastSyncAt = null,
                    LastError = null
                };
                mailbox = store.Mailboxes[mailboxIndex];
            }

            var now = DateTimeOffset.UtcNow;
            var existingIndex = store.OAuthTokenCredentials.FindIndex(t =>
                t.TenantId == ticket.TenantId &&
                t.MailboxConnectionId == mailbox.Id &&
                t.Provider == ticket.Provider);
            var previous = existingIndex >= 0 ? store.OAuthTokenCredentials[existingIndex] : null;
            var refreshCipher = !string.IsNullOrWhiteSpace(token.RefreshToken)
                ? secretProtector.Protect(token.RefreshToken)
                : previous?.RefreshTokenCiphertext;

            var credential = new OAuthTokenCredential(
                previous?.Id ?? Guid.NewGuid(),
                ticket.TenantId,
                mailbox.Id,
                ticket.Provider,
                ticket.Email,
                secretProtector.Protect(token.AccessToken),
                refreshCipher,
                token.TokenType,
                token.Scopes,
                token.ExpiresAt,
                refreshCipher is null ? "AccessTokenOnly" : "Active",
                previous?.CreatedAt ?? now,
                now);

            if (existingIndex >= 0)
            {
                store.OAuthTokenCredentials[existingIndex] = credential;
            }
            else
            {
                store.OAuthTokenCredentials.Add(credential);
            }

            return (mailbox, credential);
        });
    }

    private async Task<CloudSubscriptionResult> StartGmailWatchAsync(string accessToken, CancellationToken cancellationToken)
    {
        var gmail = options.Value.Gmail;
        var payload = new
        {
            topicName = Required(gmail.PubSubTopicName, "LegalPilot:Gmail:PubSubTopicName"),
            labelIds = new[] { "INBOX" },
            labelFilterAction = "include"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/watch");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
        using var response = await httpClientFactory.CreateClient("gmail-watch").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gmail users.watch rejected: {response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        var historyId = document.RootElement.TryGetProperty("historyId", out var history)
            ? history.GetString()
            : null;
        var expiresAt = document.RootElement.TryGetProperty("expiration", out var expiration) &&
                        long.TryParse(expiration.GetString(), out var millis)
            ? DateTimeOffset.FromUnixTimeMilliseconds(millis)
            : DateTimeOffset.UtcNow.AddDays(6);

        return new CloudSubscriptionResult(historyId ?? "gmail-watch", expiresAt);
    }

    private async Task<CloudSubscriptionResult> StartMicrosoftSubscriptionAsync(string accessToken, CancellationToken cancellationToken)
    {
        var microsoft = options.Value.Microsoft;
        var expiresAt = DateTimeOffset.UtcNow.AddHours(68);
        var payload = new
        {
            changeType = "created,updated",
            notificationUrl = Required(microsoft.WebhookNotificationUrl, "LegalPilot:Microsoft:WebhookNotificationUrl"),
            resource = "me/messages",
            expirationDateTime = expiresAt.UtcDateTime.ToString("O"),
            clientState = Required(microsoft.WebhookClientState, "LegalPilot:Microsoft:WebhookClientState")
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/subscriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
        using var response = await httpClientFactory.CreateClient("graph-subscriptions").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Microsoft Graph subscriptions rejected: {response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        var id = document.RootElement.TryGetProperty("id", out var idElement)
            ? idElement.GetString() ?? "graph-subscription"
            : "graph-subscription";
        var graphExpiresAt = document.RootElement.TryGetProperty("expirationDateTime", out var expirationElement) &&
                             DateTimeOffset.TryParse(expirationElement.GetString(), out var parsed)
            ? parsed
            : expiresAt;
        return new CloudSubscriptionResult(id, graphExpiresAt);
    }

    private void UpdateMailboxSubscription(MailboxConnection mailbox, CloudSubscriptionResult subscription)
    {
        store.Write(() =>
        {
            var index = store.Mailboxes.FindIndex(m => m.Id == mailbox.Id);
            if (index >= 0)
            {
                store.Mailboxes[index] = store.Mailboxes[index] with
                {
                    Status = "WebhookActive",
                    Cursor = mailbox.Provider == MailProvider.Gmail ? subscription.SubscriptionId : store.Mailboxes[index].Cursor,
                    WebhookSubscriptionId = mailbox.Provider == MailProvider.Gmail ? "gmail-watch" : subscription.SubscriptionId,
                    WatchExpiresAt = subscription.ExpiresAt,
                    WebhookRenewedAt = DateTimeOffset.UtcNow,
                    LastSyncAt = null,
                    LastError = null
                };
            }
        });
    }

    private string BuildAuthorizationUrl(MailProvider provider, string state, string[] scopes)
    {
        return provider switch
        {
            MailProvider.Gmail => Query("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string?>
            {
                ["client_id"] = options.Value.Gmail.ClientId,
                ["redirect_uri"] = options.Value.Gmail.RedirectUri,
                ["response_type"] = "code",
                ["scope"] = string.Join(' ', scopes),
                ["access_type"] = "offline",
                ["prompt"] = "consent",
                ["state"] = state
            }),
            MailProvider.Outlook => Query($"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(options.Value.Microsoft.TenantId) ? "common" : options.Value.Microsoft.TenantId)}/oauth2/v2.0/authorize", new Dictionary<string, string?>
            {
                ["client_id"] = options.Value.Microsoft.ClientId,
                ["redirect_uri"] = options.Value.Microsoft.RedirectUri,
                ["response_type"] = "code",
                ["response_mode"] = "query",
                ["scope"] = string.Join(' ', scopes),
                ["prompt"] = "consent",
                ["state"] = state
            }),
            _ => throw new InvalidOperationException($"Proveedor no soportado: {provider}.")
        };
    }

    private void EnsureConfigured(MailProvider provider)
    {
        if (provider == MailProvider.Gmail)
        {
            var gmail = options.Value.Gmail;
            _ = Required(gmail.ClientId, "LegalPilot:Gmail:ClientId");
            _ = Required(gmail.ClientSecret, "LegalPilot:Gmail:ClientSecret");
            _ = Required(gmail.RedirectUri, "LegalPilot:Gmail:RedirectUri");
            _ = Required(gmail.PubSubTopicName, "LegalPilot:Gmail:PubSubTopicName");
            return;
        }

        var microsoft = options.Value.Microsoft;
        _ = Required(microsoft.ClientId, "LegalPilot:Microsoft:ClientId");
        _ = Required(microsoft.ClientSecret, "LegalPilot:Microsoft:ClientSecret");
        _ = Required(microsoft.RedirectUri, "LegalPilot:Microsoft:RedirectUri");
        _ = Required(microsoft.WebhookClientState, "LegalPilot:Microsoft:WebhookClientState");
        _ = Required(microsoft.WebhookNotificationUrl, "LegalPilot:Microsoft:WebhookNotificationUrl");
    }

    private static string Query(string url, IReadOnlyDictionary<string, string?> values)
    {
        var query = string.Join("&", values
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        return $"{url}?{query}";
    }

    private static string Required(string value, string name)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Configuracion faltante: {name}.")
            : value;
    }

    private sealed record CloudTokenExchange(string AccessToken, string? RefreshToken, string TokenType, string[] Scopes, DateTimeOffset ExpiresAt);

    private sealed record CloudSubscriptionResult(string SubscriptionId, DateTimeOffset ExpiresAt);
}
