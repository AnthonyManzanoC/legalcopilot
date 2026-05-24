using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LegalPilot.Api.Application;
using LegalPilot.Api.Domain;

namespace LegalPilot.Api.Infrastructure;

public sealed record ExternalCalendarSyncResult(bool Success, string Status, string Message, string? Provider, string? ExternalEventId);

public sealed class ExternalCalendarSyncService(
    LegalPilotStore store,
    SecretProtector secretProtector,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<ExternalCalendarSyncService> logger)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public object Status(AuthPrincipal principal)
    {
        return store.Read(() =>
        {
            var tokens = store.OAuthTokenCredentials
                .Where(t => t.TenantId == principal.TenantId && t.Status != "Revoked")
                .GroupBy(t => t.Provider)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());
            var unsynced = store.CalendarEvents.Count(e =>
                e.TenantId == principal.TenantId &&
                e.Confirmed &&
                string.IsNullOrWhiteSpace(e.ExternalEventId));

            return new
            {
                configured = secretProtector.Configured && tokens.Count > 0,
                status = tokens.Count > 0 ? "OAuthTokensAvailable" : "OAuthCalendarNotConnected",
                preferredProvider = configuration["LegalPilot:Calendar:PreferredProvider"] ?? "auto",
                tokens,
                confirmedUnsyncedEvents = unsynced,
                policy = "Solo se sincronizan eventos confirmados por el abogado."
            };
        });
    }

    public async Task<ExternalCalendarSyncResult> SyncEventAsync(AuthPrincipal principal, Guid eventId, CancellationToken cancellationToken)
    {
        var eventItem = store.Read(() => store.CalendarEvents.FirstOrDefault(e => e.Id == eventId && e.TenantId == principal.TenantId))
            ?? throw new KeyNotFoundException("Evento no encontrado.");
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        return await SyncEventAsync(eventItem, principal.UserId, cancellationToken);
    }

    public async Task SyncPendingConfirmedEventsAsync(CancellationToken cancellationToken)
    {
        var events = store.Read(() => store.CalendarEvents
            .Where(e => e.Confirmed && string.IsNullOrWhiteSpace(e.ExternalEventId))
            .OrderBy(e => e.StartsAt)
            .Take(5)
            .ToArray());

        foreach (var eventItem in events)
        {
            await SyncEventAsync(eventItem, eventItem.ResponsibleUserId, cancellationToken);
        }
    }

    private async Task<ExternalCalendarSyncResult> SyncEventAsync(CalendarEvent eventItem, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (!eventItem.Confirmed)
        {
            return new ExternalCalendarSyncResult(false, "NeedsConfirmation", "El evento debe confirmarse antes de sincronizar calendario externo.", null, null);
        }

        if (!string.IsNullOrWhiteSpace(eventItem.ExternalEventId))
        {
            return new ExternalCalendarSyncResult(true, "AlreadySynced", "El evento ya tiene id externo.", eventItem.ExternalProvider, eventItem.ExternalEventId);
        }

        var credential = FindCredential(eventItem);
        if (credential is null)
        {
            return new ExternalCalendarSyncResult(false, "OAuthCalendarNotConnected", "No hay token OAuth activo para sincronizar calendario.", null, null);
        }

        var access = await ResolveAccessTokenAsync(credential, cancellationToken);
        if (!access.Success || string.IsNullOrWhiteSpace(access.Token))
        {
            return new ExternalCalendarSyncResult(false, access.Status, access.Message, credential.Mailbox.Provider.ToString(), null);
        }

        try
        {
            var externalId = credential.Mailbox.Provider == MailProvider.Gmail
                ? await CreateGoogleEventAsync(access.Token, eventItem, cancellationToken)
                : await CreateGraphEventAsync(access.Token, eventItem, cancellationToken);

            store.Write(() =>
            {
                var index = store.CalendarEvents.FindIndex(e => e.Id == eventItem.Id);
                if (index >= 0)
                {
                    store.CalendarEvents[index] = eventItem with
                    {
                        ExternalProvider = credential.Mailbox.Provider == MailProvider.Gmail ? "GoogleCalendar" : "OutlookCalendar",
                        ExternalEventId = externalId
                    };
                }

                store.Audit(eventItem.TenantId, actorUserId, AuditAction.CreateCalendarEvent, nameof(CalendarEvent), eventItem.Id.ToString(), $"Evento sincronizado con calendario externo: {externalId}.");
            });

            return new ExternalCalendarSyncResult(true, "Synced", "Evento creado en calendario externo.", credential.Mailbox.Provider.ToString(), externalId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "External calendar sync failed for event {EventId}.", eventItem.Id);
            return new ExternalCalendarSyncResult(false, "ProviderCalendarFailed", "El proveedor rechazo la creacion del evento. Revise scopes, token y calendario.", credential.Mailbox.Provider.ToString(), null);
        }
    }

    private CalendarCredential? FindCredential(CalendarEvent eventItem)
    {
        return store.Read(() =>
        {
            var preferred = configuration["LegalPilot:Calendar:PreferredProvider"];
            var candidates = store.OAuthTokenCredentials
                .Where(t => t.TenantId == eventItem.TenantId && t.Status != "Revoked")
                .Select(t => new
                {
                    Credential = t,
                    Mailbox = store.Mailboxes.FirstOrDefault(m => m.Id == t.MailboxConnectionId && m.TenantId == eventItem.TenantId)
                })
                .Where(c => c.Mailbox is not null)
                .Select(c => new CalendarCredential(c.Credential, c.Mailbox!))
                .OrderByDescending(c => c.Mailbox.OwnerUserId == eventItem.ResponsibleUserId)
                .ThenBy(c => PreferredRank(c.Credential.Provider, preferred))
                .ThenByDescending(c => c.Credential.UpdatedAt)
                .FirstOrDefault();

            return candidates;
        });
    }

    private async Task<TokenResolution> ResolveAccessTokenAsync(CalendarCredential credential, CancellationToken cancellationToken)
    {
        if (credential.Credential.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return new TokenResolution(true, "OAuthTokenActive", "Token OAuth activo.", secretProtector.Unprotect(credential.Credential.AccessTokenCiphertext));
        }

        if (string.IsNullOrWhiteSpace(credential.Credential.RefreshTokenCiphertext))
        {
            return new TokenResolution(false, "OAuthRefreshRequired", "El token expiro y no existe refresh token para calendario.", null);
        }

        var refreshToken = secretProtector.Unprotect(credential.Credential.RefreshTokenCiphertext);
        var refreshed = credential.Credential.Provider == MailProvider.Gmail
            ? await RefreshGoogleAsync(refreshToken, cancellationToken)
            : await RefreshMicrosoftAsync(refreshToken, cancellationToken);

        var updated = credential.Credential with
        {
            AccessTokenCiphertext = secretProtector.Protect(refreshed.AccessToken),
            RefreshTokenCiphertext = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
                ? credential.Credential.RefreshTokenCiphertext
                : secretProtector.Protect(refreshed.RefreshToken),
            TokenType = refreshed.TokenType,
            Scopes = refreshed.Scopes.Length == 0 ? credential.Credential.Scopes : refreshed.Scopes,
            ExpiresAt = refreshed.ExpiresAt,
            Status = "Active",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        store.Write(() =>
        {
            var index = store.OAuthTokenCredentials.FindIndex(t => t.Id == credential.Credential.Id);
            if (index >= 0)
            {
                store.OAuthTokenCredentials[index] = updated;
            }
        });

        return new TokenResolution(true, "OAuthTokenRefreshed", "Token OAuth renovado.", refreshed.AccessToken);
    }

    private async Task<RefreshedToken> RefreshGoogleAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = Required("LegalPilot:Gmail:ClientId"),
            ["client_secret"] = Required("LegalPilot:Gmail:ClientSecret"),
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };
        using var response = await httpClientFactory.CreateClient("gmail-oauth").PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(values), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Calendar refresh rejected: {response.StatusCode}");
        }

        return EmailConnectorHelpers.ParseRefreshedToken(payload);
    }

    private async Task<RefreshedToken> RefreshMicrosoftAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = Required("LegalPilot:Microsoft:ClientId"),
            ["client_secret"] = Required("LegalPilot:Microsoft:ClientSecret"),
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = string.Join(' ', OAuthService.Scopes(MailProvider.Outlook))
        };
        var tenant = string.IsNullOrWhiteSpace(configuration["LegalPilot:Microsoft:TenantId"])
            ? "common"
            : configuration["LegalPilot:Microsoft:TenantId"];
        using var response = await httpClientFactory.CreateClient("graph-oauth").PostAsync($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token", new FormUrlEncodedContent(values), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Outlook Calendar refresh rejected: {response.StatusCode}");
        }

        return EmailConnectorHelpers.ParseRefreshedToken(payload);
    }

    private async Task<string> CreateGoogleEventAsync(string accessToken, CalendarEvent eventItem, CancellationToken cancellationToken)
    {
        var payload = new
        {
            summary = eventItem.Title,
            location = eventItem.Location,
            description = "Creado por LegalPilot Ecuador. Revise el expediente antes de actuaciones sensibles.",
            start = new { dateTime = eventItem.StartsAt.ToString("O"), timeZone = "America/Guayaquil" },
            end = new { dateTime = eventItem.EndsAt.ToString("O"), timeZone = "America/Guayaquil" },
            reminders = new
            {
                useDefault = false,
                overrides = new[] { new { method = "popup", minutes = 60 }, new { method = "popup", minutes = 1440 } }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/calendar/v3/calendars/primary/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
        using var response = await httpClientFactory.CreateClient("google-calendar").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Calendar rejected event: {response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? body.GetHashCode().ToString() : body.GetHashCode().ToString();
    }

    private async Task<string> CreateGraphEventAsync(string accessToken, CalendarEvent eventItem, CancellationToken cancellationToken)
    {
        var payload = new
        {
            subject = eventItem.Title,
            body = new { contentType = "Text", content = "Creado por LegalPilot Ecuador. Revise el expediente antes de actuaciones sensibles." },
            location = new { displayName = eventItem.Location ?? string.Empty },
            start = new { dateTime = eventItem.StartsAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
            end = new { dateTime = eventItem.EndsAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
            isReminderOn = true,
            reminderMinutesBeforeStart = 60
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
        using var response = await httpClientFactory.CreateClient("graph-calendar").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Microsoft Graph rejected event: {response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? body.GetHashCode().ToString() : body.GetHashCode().ToString();
    }

    private string Required(string key)
    {
        return string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"Configuracion faltante: {key}.")
            : configuration[key]!;
    }

    private static int PreferredRank(MailProvider provider, string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred) || preferred.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return provider == MailProvider.Gmail ? 0 : 1;
        }

        return provider.ToString().Equals(preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 10;
    }

    private sealed record CalendarCredential(OAuthTokenCredential Credential, MailboxConnection Mailbox);
}

public sealed class CalendarExternalSyncWorker(
    ExternalCalendarSyncService calendars,
    ILogger<CalendarExternalSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await calendars.SyncPendingConfirmedEventsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "External calendar sync worker failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}
