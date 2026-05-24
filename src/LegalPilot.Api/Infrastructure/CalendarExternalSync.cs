using System.Net;
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
    private static readonly TimeSpan EcuadorOffset = TimeSpan.FromHours(-5);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public object Status(AuthPrincipal principal)
    {
        return store.Read(() =>
        {
            var tokens = store.OAuthTokenCredentials
                .Where(t => t.TenantId == principal.TenantId && t.Status != "Revoked")
                .GroupBy(t => t.Provider)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());
            var pending = store.CalendarEvents.Count(e =>
                e.TenantId == principal.TenantId &&
                e.Confirmed &&
                e.Status != "Cancelled" &&
                e.SyncStatus is "Pending" or "PendingUpdate" or "Error");
            var pendingDelete = store.CalendarEvents.Count(e =>
                e.TenantId == principal.TenantId &&
                e.Status == "Cancelled" &&
                e.SyncStatus == "PendingDelete");
            var accounts = store.Mailboxes
                .Where(m => m.TenantId == principal.TenantId && m.Status != "Disconnected")
                .Select(m => new
                {
                    m.Id,
                    m.Provider,
                    m.Email,
                    m.Status,
                    m.DefaultCalendarId,
                    m.WatchExpiresAt,
                    m.WebhookSubscriptionId,
                    m.LastError
                })
                .ToArray();

            return new
            {
                configured = secretProtector.Configured && tokens.Count > 0,
                status = tokens.Count > 0 ? "OAuthTokensAvailable" : "OAuthCalendarNotConnected",
                preferredProvider = configuration["LegalPilot:Calendar:PreferredProvider"] ?? "auto",
                tokens,
                accounts,
                confirmedUnsyncedEvents = pending,
                pendingDeletes = pendingDelete,
                recentLogs = store.CalendarSyncLogs
                    .Where(l => l.TenantId == principal.TenantId)
                    .OrderByDescending(l => l.CreatedAt)
                    .Take(8)
                    .ToArray(),
                policy = "El calendario interno es la fuente de verdad; los proveedores externos son sincronizaciones auditadas."
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
            .Where(IsDueForExternalSync)
            .OrderBy(e => e.StartsAt)
            .Take(10)
            .ToArray());

        foreach (var eventItem in events)
        {
            await SyncEventAsync(eventItem, eventItem.ResponsibleUserId, cancellationToken);
        }
    }

    private bool IsDueForExternalSync(CalendarEvent eventItem)
    {
        if (eventItem.Status == "Cancelled")
        {
            return eventItem.SyncStatus == "PendingDelete";
        }

        if (!eventItem.Confirmed)
        {
            return false;
        }

        if (eventItem.SyncStatus is not ("Pending" or "PendingUpdate" or "Error") &&
            !string.IsNullOrWhiteSpace(eventItem.ExternalEventId))
        {
            return false;
        }

        var lastFailure = store.CalendarSyncLogs
            .Where(l => l.CalendarEventId == eventItem.Id && l.Status == "Error")
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefault();
        return lastFailure?.NextAttemptAt is null || lastFailure.NextAttemptAt <= DateTimeOffset.UtcNow;
    }

    private async Task<ExternalCalendarSyncResult> SyncEventAsync(CalendarEvent eventItem, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (eventItem.Status == "Cancelled" && string.IsNullOrWhiteSpace(eventItem.ExternalEventId))
        {
            MarkEvent(eventItem, "Cancelled", null, null);
            return new ExternalCalendarSyncResult(true, "Cancelled", "Evento cancelado internamente sin id externo.", eventItem.ExternalProvider, null);
        }

        if (eventItem.Status != "Cancelled" && !eventItem.Confirmed)
        {
            return new ExternalCalendarSyncResult(false, "NeedsConfirmation", "El evento debe confirmarse antes de sincronizar calendario externo.", eventItem.ExternalProvider, eventItem.ExternalEventId);
        }

        var credential = FindCredential(eventItem);
        if (credential is null)
        {
            return RecordFailure(eventItem, null, "OAuthCalendarNotConnected", "No hay token OAuth activo para sincronizar calendario.", actorUserId, null);
        }

        var access = await ResolveAccessTokenAsync(credential, cancellationToken);
        if (!access.Success || string.IsNullOrWhiteSpace(access.Token))
        {
            return RecordFailure(eventItem, credential.Mailbox.Provider, access.Status, access.Message, actorUserId, null);
        }

        var provider = credential.Mailbox.Provider;
        var operation = DetermineOperation(eventItem);
        var calendarId = CalendarId(credential.Mailbox, provider);

        try
        {
            var externalId = operation switch
            {
                "delete" when provider == MailProvider.Gmail => await DeleteGoogleEventAsync(access.Token, calendarId, eventItem.ExternalEventId!, cancellationToken),
                "delete" => await DeleteGraphEventAsync(access.Token, eventItem.ExternalEventId!, cancellationToken),
                "update" when provider == MailProvider.Gmail => await UpdateGoogleEventAsync(access.Token, calendarId, eventItem, cancellationToken),
                "update" => await UpdateGraphEventAsync(access.Token, eventItem, cancellationToken),
                "create" when provider == MailProvider.Gmail => await CreateGoogleEventAsync(access.Token, calendarId, eventItem, cancellationToken),
                _ => await CreateGraphEventAsync(access.Token, credential.Mailbox.DefaultCalendarId, eventItem, cancellationToken)
            };

            var status = operation == "delete" ? "Deleted" : "Synced";
            store.Write(() =>
            {
                var index = store.CalendarEvents.FindIndex(e => e.Id == eventItem.Id);
                if (index >= 0)
                {
                    var current = store.CalendarEvents[index];
                    store.CalendarEvents[index] = current with
                    {
                        ExternalProvider = operation == "delete" ? current.ExternalProvider : ProviderName(provider),
                        ExternalEventId = operation == "delete" ? null : externalId,
                        ExternalCalendarId = operation == "delete" ? current.ExternalCalendarId : calendarId,
                        SyncStatus = status,
                        SyncError = null,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                }

                store.CalendarSyncLogs.Insert(0, new CalendarSyncLog(
                    Guid.NewGuid(),
                    eventItem.TenantId,
                    eventItem.Id,
                    provider,
                    operation,
                    status,
                    operation == "delete" ? "Evento eliminado del calendario externo." : "Evento sincronizado con calendario externo.",
                    operation == "delete" ? eventItem.ExternalEventId : externalId,
                    1,
                    DateTimeOffset.UtcNow,
                    null));
                store.Audit(eventItem.TenantId, actorUserId, AuditAction.CalendarSync, nameof(CalendarEvent), eventItem.Id.ToString(), $"{operation} {provider}: {externalId}.");
            });

            return new ExternalCalendarSyncResult(true, status, operation == "delete" ? "Evento eliminado en calendario externo." : "Evento sincronizado en calendario externo.", provider.ToString(), externalId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "External calendar {Operation} failed for event {EventId}.", operation, eventItem.Id);
            return RecordFailure(eventItem, provider, "ProviderCalendarFailed", "El proveedor rechazo la operacion de calendario. Revise scopes, token, calendario y permisos.", actorUserId, operation);
        }
    }

    private CalendarCredential? FindCredential(CalendarEvent eventItem)
    {
        return store.Read(() =>
        {
            var preferred = configuration["LegalPilot:Calendar:PreferredProvider"];
            return store.OAuthTokenCredentials
                .Where(t => t.TenantId == eventItem.TenantId && t.Status != "Revoked")
                .Select(t => new
                {
                    Credential = t,
                    Mailbox = store.Mailboxes.FirstOrDefault(m => m.Id == t.MailboxConnectionId && m.TenantId == eventItem.TenantId && m.Status != "Disconnected")
                })
                .Where(c => c.Mailbox is not null)
                .Select(c => new CalendarCredential(c.Credential, c.Mailbox!))
                .OrderByDescending(c => c.Mailbox.OwnerUserId == eventItem.ResponsibleUserId)
                .ThenBy(c => PreferredRank(c.Credential.Provider, preferred))
                .ThenByDescending(c => c.Credential.UpdatedAt)
                .FirstOrDefault();
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

    private async Task<string> CreateGoogleEventAsync(string accessToken, string calendarId, CalendarEvent eventItem, CancellationToken cancellationToken)
    {
        var payload = GooglePayload(eventItem);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent(payload);
        using var response = await httpClientFactory.CreateClient("google-calendar").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Calendar rejected event create: {response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? body.GetHashCode().ToString() : body.GetHashCode().ToString();
    }

    private async Task<string> UpdateGoogleEventAsync(string accessToken, string calendarId, CalendarEvent eventItem, CancellationToken cancellationToken)
    {
        var payload = GooglePayload(eventItem);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventItem.ExternalEventId!)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent(payload);
        using var response = await httpClientFactory.CreateClient("google-calendar").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Calendar rejected event update: {response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? eventItem.ExternalEventId! : eventItem.ExternalEventId!;
    }

    private async Task<string> DeleteGoogleEventAsync(string accessToken, string calendarId, string externalEventId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(externalEventId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClientFactory.CreateClient("google-calendar").SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
        {
            return externalEventId;
        }

        throw new InvalidOperationException($"Google Calendar rejected event delete: {response.StatusCode}");
    }

    private async Task<string> CreateGraphEventAsync(string accessToken, string? calendarId, CalendarEvent eventItem, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(calendarId) || calendarId.Equals("primary", StringComparison.OrdinalIgnoreCase) || calendarId.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? "https://graph.microsoft.com/v1.0/me/events"
            : $"https://graph.microsoft.com/v1.0/me/calendars/{Uri.EscapeDataString(calendarId)}/events";
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent(GraphPayload(eventItem));
        using var response = await httpClientFactory.CreateClient("graph-calendar").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Microsoft Graph rejected event create: {response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? body.GetHashCode().ToString() : body.GetHashCode().ToString();
    }

    private async Task<string> UpdateGraphEventAsync(string accessToken, CalendarEvent eventItem, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"https://graph.microsoft.com/v1.0/me/events/{Uri.EscapeDataString(eventItem.ExternalEventId!)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent(GraphPayload(eventItem));
        using var response = await httpClientFactory.CreateClient("graph-calendar").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Microsoft Graph rejected event update: {response.StatusCode}");
        }

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? eventItem.ExternalEventId! : eventItem.ExternalEventId!;
    }

    private async Task<string> DeleteGraphEventAsync(string accessToken, string externalEventId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/me/events/{Uri.EscapeDataString(externalEventId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClientFactory.CreateClient("graph-calendar").SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
        {
            return externalEventId;
        }

        throw new InvalidOperationException($"Microsoft Graph rejected event delete: {response.StatusCode}");
    }

    private object GooglePayload(CalendarEvent eventItem)
    {
        return new
        {
            summary = eventItem.Title,
            location = eventItem.Location,
            description = eventItem.Description ?? "Creado por LegalPilot Ecuador. Revise el expediente antes de actuaciones sensibles.",
            start = new { dateTime = eventItem.StartsAt.ToOffset(EcuadorOffset).ToString("yyyy-MM-ddTHH:mm:sszzz"), timeZone = "America/Guayaquil" },
            end = new { dateTime = eventItem.EndsAt.ToOffset(EcuadorOffset).ToString("yyyy-MM-ddTHH:mm:sszzz"), timeZone = "America/Guayaquil" },
            reminders = new
            {
                useDefault = false,
                overrides = new[] { new { method = "popup", minutes = 60 }, new { method = "popup", minutes = 1440 } }
            },
            extendedProperties = new
            {
                @private = new Dictionary<string, string>
                {
                    ["legalPilotEventId"] = eventItem.Id.ToString(),
                    ["legalPilotTenantId"] = eventItem.TenantId.ToString()
                }
            }
        };
    }

    private object GraphPayload(CalendarEvent eventItem)
    {
        var start = eventItem.StartsAt.ToOffset(EcuadorOffset).DateTime.ToString("yyyy-MM-ddTHH:mm:ss");
        var end = eventItem.EndsAt.ToOffset(EcuadorOffset).DateTime.ToString("yyyy-MM-ddTHH:mm:ss");
        return new
        {
            subject = eventItem.Title,
            body = new { contentType = "Text", content = eventItem.Description ?? "Creado por LegalPilot Ecuador. Revise el expediente antes de actuaciones sensibles." },
            location = new { displayName = eventItem.Location ?? string.Empty },
            start = new { dateTime = start, timeZone = "SA Pacific Standard Time" },
            end = new { dateTime = end, timeZone = "SA Pacific Standard Time" },
            isReminderOn = true,
            reminderMinutesBeforeStart = 60,
            singleValueExtendedProperties = new[]
            {
                new
                {
                    id = "String {00020329-0000-0000-C000-000000000046} Name LegalPilotEventId",
                    value = eventItem.Id.ToString()
                }
            }
        };
    }

    private ExternalCalendarSyncResult RecordFailure(CalendarEvent eventItem, MailProvider? provider, string status, string message, Guid? actorUserId, string? operation)
    {
        var attempt = store.Read(() => store.CalendarSyncLogs.Count(l => l.CalendarEventId == eventItem.Id && l.Status == "Error") + 1);
        var next = DateTimeOffset.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(attempt, 5)) * 5));
        store.Write(() =>
        {
            var index = store.CalendarEvents.FindIndex(e => e.Id == eventItem.Id);
            if (index >= 0)
            {
                store.CalendarEvents[index] = store.CalendarEvents[index] with
                {
                    SyncStatus = "Error",
                    SyncError = message,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }

            if (provider.HasValue)
            {
                store.CalendarSyncLogs.Insert(0, new CalendarSyncLog(
                    Guid.NewGuid(),
                    eventItem.TenantId,
                    eventItem.Id,
                    provider.Value,
                    operation ?? DetermineOperation(eventItem),
                    "Error",
                    message,
                    eventItem.ExternalEventId,
                    attempt,
                    DateTimeOffset.UtcNow,
                    next));
            }

            store.Audit(eventItem.TenantId, actorUserId, AuditAction.CalendarSync, nameof(CalendarEvent), eventItem.Id.ToString(), message);
        });

        return new ExternalCalendarSyncResult(false, status, message, provider?.ToString(), eventItem.ExternalEventId);
    }

    private void MarkEvent(CalendarEvent eventItem, string syncStatus, string? syncError, string? externalEventId)
    {
        store.Write(() =>
        {
            var index = store.CalendarEvents.FindIndex(e => e.Id == eventItem.Id);
            if (index >= 0)
            {
                store.CalendarEvents[index] = store.CalendarEvents[index] with
                {
                    SyncStatus = syncStatus,
                    SyncError = syncError,
                    ExternalEventId = externalEventId,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
        });
    }

    private StringContent JsonContent(object payload)
    {
        return new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
    }

    private static string DetermineOperation(CalendarEvent eventItem)
    {
        if (eventItem.Status == "Cancelled" && !string.IsNullOrWhiteSpace(eventItem.ExternalEventId))
        {
            return "delete";
        }

        return string.IsNullOrWhiteSpace(eventItem.ExternalEventId) ? "create" : "update";
    }

    private static string CalendarId(MailboxConnection mailbox, MailProvider provider)
    {
        if (!string.IsNullOrWhiteSpace(mailbox.DefaultCalendarId))
        {
            return mailbox.DefaultCalendarId;
        }

        return provider == MailProvider.Gmail ? "primary" : "default";
    }

    private static string ProviderName(MailProvider provider) => provider == MailProvider.Gmail ? "GoogleCalendar" : "OutlookCalendar";

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
