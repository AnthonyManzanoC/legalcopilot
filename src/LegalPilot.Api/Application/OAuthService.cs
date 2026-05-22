using LegalPilot.Api.Domain;
using LegalPilot.Api.Infrastructure;
using System.Text.Json;

namespace LegalPilot.Api.Application;

public sealed record StartOAuthRequest(MailProvider Provider, string Email);

public sealed record OAuthStartResponse(
    MailProvider Provider,
    string Email,
    string AuthorizationUrl,
    string State,
    DateTimeOffset ExpiresAt,
    string[] Scopes);

public sealed record OAuthCallbackResponse(
    bool Accepted,
    MailProvider Provider,
    string Email,
    string Status,
    string Message,
    Guid? MailboxId = null,
    DateTimeOffset? TokenExpiresAt = null);

public sealed class OAuthService(
    LegalPilotStore store,
    EmailConnectorRegistry connectors,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    SecretProtector secretProtector,
    ILogger<OAuthService> logger)
{
    public OAuthStartResponse Start(AuthPrincipal principal, StartOAuthRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);

        var email = InputGuard.Email(request.Email);
        var connector = connectors.Get(request.Provider);
        var readiness = connector.GetReadiness();
        if (!readiness.Configured)
        {
            throw new InvalidOperationException($"{request.Provider} no esta configurado. Faltan: {string.Join(", ", readiness.RequiredSettings)}.");
        }

        if (!secretProtector.Configured)
        {
            throw new InvalidOperationException("Configure LEGALPILOT_DATA_PROTECTION_KEY antes de iniciar OAuth para guardar tokens cifrados.");
        }

        var state = TokenService.RandomToken(32);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var ticket = new OAuthStateTicket(
            Guid.NewGuid(),
            principal.TenantId,
            principal.UserId,
            request.Provider,
            email,
            TokenService.Sha256(state),
            expiresAt,
            false,
            DateTimeOffset.UtcNow);

        store.Write(() => store.OAuthStateTickets.Add(ticket));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.SecurityEvent, nameof(OAuthStateTicket), ticket.Id.ToString(), $"OAuth iniciado para {request.Provider} / {email}.");

        var scopes = Scopes(request.Provider);
        return new OAuthStartResponse(
            request.Provider,
            email,
            BuildAuthorizationUrl(request.Provider, state, scopes),
            state,
            expiresAt,
            scopes);
    }

    public async Task<OAuthCallbackResponse> CompleteAsync(MailProvider provider, string? state, string? code, string? error, CancellationToken cancellationToken)
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

            var ticket = store.OAuthStateTickets[index];
            store.OAuthStateTickets[index] = ticket with { Used = true };
            return ticket;
        });

        if (!string.IsNullOrWhiteSpace(error))
        {
            store.Audit(ticket.TenantId, ticket.UserId, AuditAction.SecurityEvent, nameof(OAuthStateTicket), ticket.Id.ToString(), $"OAuth rechazado por proveedor: {error}.");
            return new OAuthCallbackResponse(false, provider, ticket.Email, "OAuthRejected", $"Proveedor devolvio error: {error}.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Codigo OAuth ausente.");
        }

        OAuthTokenExchange tokenExchange;
        try
        {
            tokenExchange = await ExchangeCodeAsync(provider, code, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OAuth token exchange failed for {Provider} / {Email}.", provider, ticket.Email);
            store.Audit(ticket.TenantId, ticket.UserId, AuditAction.SecurityEvent, nameof(OAuthStateTicket), ticket.Id.ToString(), $"OAuth token exchange fallo para {provider}: {ex.Message}");
            return new OAuthCallbackResponse(false, provider, ticket.Email, "OAuthTokenExchangeFailed", "Callback validado, pero el proveedor rechazo el intercambio de token. Revise credenciales, redirect URI y scopes.");
        }

        var stored = store.Write(() =>
        {
            var mailbox = store.Mailboxes.FirstOrDefault(m =>
                m.TenantId == ticket.TenantId &&
                m.Provider == provider &&
                m.Email.Equals(ticket.Email, StringComparison.OrdinalIgnoreCase));

            if (mailbox is null)
            {
                mailbox = new MailboxConnection(
                    Guid.NewGuid(),
                    ticket.TenantId,
                    ticket.UserId,
                    provider,
                    ticket.Email,
                    ticket.Email,
                    "OAuthCallbackReceived",
                    Scopes(provider),
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
                    LastSyncAt = DateTimeOffset.UtcNow
                };
                mailbox = store.Mailboxes[mailboxIndex];
            }

            var now = DateTimeOffset.UtcNow;
            var existingIndex = store.OAuthTokenCredentials.FindIndex(t =>
                t.TenantId == ticket.TenantId &&
                t.MailboxConnectionId == mailbox.Id &&
                t.Provider == provider);
            var previous = existingIndex >= 0 ? store.OAuthTokenCredentials[existingIndex] : null;
            var refreshCiphertext = !string.IsNullOrWhiteSpace(tokenExchange.RefreshToken)
                ? secretProtector.Protect(tokenExchange.RefreshToken)
                : previous?.RefreshTokenCiphertext;
            var credential = new OAuthTokenCredential(
                previous?.Id ?? Guid.NewGuid(),
                ticket.TenantId,
                mailbox.Id,
                provider,
                ticket.Email,
                secretProtector.Protect(tokenExchange.AccessToken),
                refreshCiphertext,
                tokenExchange.TokenType,
                tokenExchange.Scopes.Length == 0 ? Scopes(provider) : tokenExchange.Scopes,
                tokenExchange.ExpiresAt,
                refreshCiphertext is null ? "AccessTokenOnly" : "Active",
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

            store.Audit(ticket.TenantId, ticket.UserId, AuditAction.SecurityEvent, nameof(MailboxConnection), mailbox.Id.ToString(), $"OAuth conectado para {provider}. Tokens almacenados cifrados.");
            return (mailbox, credential);
        });

        return new OAuthCallbackResponse(
            true,
            provider,
            ticket.Email,
            stored.credential.Status,
            "OAuth validado, token intercambiado y credencial guardada cifrada.",
            stored.mailbox.Id,
            stored.credential.ExpiresAt);
    }

    private string BuildAuthorizationUrl(MailProvider provider, string state, string[] scopes)
    {
        return provider switch
        {
            MailProvider.Gmail => BuildQuery("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string?>
            {
                ["client_id"] = configuration["LegalPilot:Gmail:ClientId"],
                ["redirect_uri"] = configuration["LegalPilot:Gmail:RedirectUri"],
                ["response_type"] = "code",
                ["scope"] = string.Join(' ', scopes),
                ["access_type"] = "offline",
                ["prompt"] = "consent",
                ["state"] = state
            }),
            MailProvider.Outlook => BuildQuery($"https://login.microsoftonline.com/{configuration["LegalPilot:Microsoft:TenantId"] ?? "common"}/oauth2/v2.0/authorize", new Dictionary<string, string?>
            {
                ["client_id"] = configuration["LegalPilot:Microsoft:ClientId"],
                ["redirect_uri"] = configuration["LegalPilot:Microsoft:RedirectUri"],
                ["response_type"] = "code",
                ["response_mode"] = "query",
                ["scope"] = string.Join(' ', scopes),
                ["prompt"] = "consent",
                ["state"] = state
            }),
            _ => throw new InvalidOperationException($"Proveedor no soportado: {provider}.")
        };
    }

    public static string[] Scopes(MailProvider provider)
    {
        return provider == MailProvider.Gmail
            ? ["https://www.googleapis.com/auth/gmail.readonly", "https://www.googleapis.com/auth/gmail.modify", "https://www.googleapis.com/auth/calendar.events"]
            : ["offline_access", "User.Read", "Mail.Read", "Calendars.ReadWrite"];
    }

    private static string BuildQuery(string url, IReadOnlyDictionary<string, string?> values)
    {
        var query = string.Join("&", values
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));

        return $"{url}?{query}";
    }

    private async Task<OAuthTokenExchange> ExchangeCodeAsync(MailProvider provider, string code, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("oauth");
        var values = provider switch
        {
            MailProvider.Gmail => new Dictionary<string, string?>
            {
                ["code"] = code,
                ["client_id"] = RequiredConfig("LegalPilot:Gmail:ClientId"),
                ["client_secret"] = RequiredConfig("LegalPilot:Gmail:ClientSecret"),
                ["redirect_uri"] = RequiredConfig("LegalPilot:Gmail:RedirectUri"),
                ["grant_type"] = "authorization_code"
            },
            MailProvider.Outlook => new Dictionary<string, string?>
            {
                ["code"] = code,
                ["client_id"] = RequiredConfig("LegalPilot:Microsoft:ClientId"),
                ["client_secret"] = RequiredConfig("LegalPilot:Microsoft:ClientSecret"),
                ["redirect_uri"] = RequiredConfig("LegalPilot:Microsoft:RedirectUri"),
                ["grant_type"] = "authorization_code",
                ["scope"] = string.Join(' ', Scopes(MailProvider.Outlook))
            },
            _ => throw new InvalidOperationException($"Proveedor no soportado: {provider}.")
        };

        var endpoint = provider == MailProvider.Gmail
            ? "https://oauth2.googleapis.com/token"
            : $"https://login.microsoftonline.com/{configuration["LegalPilot:Microsoft:TenantId"] ?? "common"}/oauth2/v2.0/token";

        using var response = await client.PostAsync(
            endpoint,
            new FormUrlEncodedContent(values.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value!))),
            cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token endpoint rejected the request ({response.StatusCode}): {ProviderError(payload)}");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("El proveedor no devolvio access_token.");
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

        return new OAuthTokenExchange(
            accessToken,
            refreshToken,
            tokenType,
            scopes,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)));
    }

    private string RequiredConfig(string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Configuracion faltante: {key}.")
            : value;
    }

    private static string ProviderError(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("error_description", out var description))
            {
                return description.GetString() ?? "sin descripcion";
            }

            if (root.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? "error";
            }
        }
        catch (JsonException)
        {
            // Keep provider payload out of logs/responses when it is not JSON.
        }

        return "respuesta no aceptada por el proveedor";
    }

    private sealed record OAuthTokenExchange(
        string AccessToken,
        string? RefreshToken,
        string TokenType,
        string[] Scopes,
        DateTimeOffset ExpiresAt);
}
