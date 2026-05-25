using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using LegalPilot.Api.Domain;
using LegalPilot.Api.Infrastructure;

namespace LegalPilot.Api.Application;

public sealed class AuthService(LegalPilotStore store, PasswordHasher hasher, TokenService tokens, IWebHostEnvironment environment)
{
    public object Login(string email, string password, string? ipAddress = null)
    {
        email = InputGuard.Email(email);
        password = InputGuard.Required(password, "Contrasena", 256);
        if (store.Read(() => store.Users.All(u => !u.IsActive)))
        {
            throw new UnauthorizedAccessException("No hay usuario administrador inicial. Configure LEGALPILOT_BOOTSTRAP_ADMIN_EMAIL y LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD, reinicie la API y vuelva a entrar.");
        }

        var user = store.Read(() => store.Users.FirstOrDefault(u =>
            u.Email.Equals(email, StringComparison.OrdinalIgnoreCase) && u.IsActive));

        if (user is null || !hasher.Verify(password, user.PasswordHash, user.PasswordSalt))
        {
            if (store.Tenants.FirstOrDefault() is { } tenant)
            {
                store.Audit(tenant.Id, null, AuditAction.SecurityEvent, nameof(UserAccount), email, "Intento de login fallido.");
            }

            throw new UnauthorizedAccessException("Credenciales invalidas.");
        }

        var principal = new AuthPrincipal(user.Id, user.TenantId, user.Email, user.Roles);
        var token = tokens.Create(principal, TimeSpan.FromHours(10));
        var refresh = CreateRefreshSession(user, ipAddress);
        store.Audit(user.TenantId, user.Id, AuditAction.Login, nameof(UserAccount), user.Id.ToString(), "Login exitoso.");

        return new
        {
            accessToken = token,
            refreshToken = refresh.RawToken,
            expiresInSeconds = (int)TimeSpan.FromHours(10).TotalSeconds,
            refreshExpiresAt = refresh.Session.ExpiresAt,
            user = new
            {
                user.Id,
                user.Email,
                user.DisplayName,
                Roles = user.Roles.Select(r => r.ToString()).ToArray(),
                user.TenantId,
                user.MfaEnabled
            }
        };
    }

    public object Refresh(string refreshToken, string? ipAddress = null)
    {
        refreshToken = InputGuard.Required(refreshToken, "Refresh token", 512);
        var hash = TokenService.Sha256(refreshToken);
        return store.Write(() =>
        {
            var index = store.RefreshTokenSessions.FindIndex(t =>
                t.TokenHash == hash &&
                t.RevokedAt is null &&
                t.ExpiresAt > DateTimeOffset.UtcNow);

            if (index < 0)
            {
                throw new UnauthorizedAccessException("Refresh token invalido o expirado.");
            }

            var session = store.RefreshTokenSessions[index];
            var user = store.Users.FirstOrDefault(u => u.Id == session.UserId && u.TenantId == session.TenantId && u.IsActive)
                ?? throw new UnauthorizedAccessException("Usuario inactivo o no encontrado.");

            store.RefreshTokenSessions[index] = session with
            {
                RevokedAt = DateTimeOffset.UtcNow,
                RevokedByIp = ipAddress
            };

            var principal = new AuthPrincipal(user.Id, user.TenantId, user.Email, user.Roles);
            var accessToken = tokens.Create(principal, TimeSpan.FromHours(10));
            var nextRefresh = CreateRefreshSessionUnsafe(user, ipAddress);
            store.Audit(user.TenantId, user.Id, AuditAction.SecurityEvent, nameof(RefreshTokenSession), nextRefresh.Session.Id.ToString(), "Refresh token rotado.");

            return new
            {
                accessToken,
                refreshToken = nextRefresh.RawToken,
                expiresInSeconds = (int)TimeSpan.FromHours(10).TotalSeconds,
                refreshExpiresAt = nextRefresh.Session.ExpiresAt
            };
        });
    }

    public object Logout(Guid userId, Guid tenantId, string? refreshToken, string? ipAddress = null)
    {
        store.Write(() =>
        {
            var sessions = store.RefreshTokenSessions
                .Where(t => t.UserId == userId && t.TenantId == tenantId && t.RevokedAt is null)
                .ToArray();

            var tokenHash = string.IsNullOrWhiteSpace(refreshToken) ? null : TokenService.Sha256(refreshToken);
            foreach (var session in sessions)
            {
                if (tokenHash is not null && session.TokenHash != tokenHash)
                {
                    continue;
                }

                var index = store.RefreshTokenSessions.FindIndex(t => t.Id == session.Id);
                store.RefreshTokenSessions[index] = session with
                {
                    RevokedAt = DateTimeOffset.UtcNow,
                    RevokedByIp = ipAddress
                };
            }
        });
        store.Audit(tenantId, userId, AuditAction.Logout, nameof(UserAccount), userId.ToString(), "Sesion cerrada.");
        return new { message = "Sesion cerrada." };
    }

    public object CreatePasswordReset(string email)
    {
        email = InputGuard.Email(email);
        var user = store.Read(() => store.Users.FirstOrDefault(u =>
            u.Email.Equals(email, StringComparison.OrdinalIgnoreCase) && u.IsActive));

        if (user is null)
        {
            return new { message = "Si el correo existe, se enviaran instrucciones de recuperacion." };
        }

        var rawToken = TokenService.RandomToken();
        var ticket = new PasswordResetTicket(
            Guid.NewGuid(),
            user.TenantId,
            user.Id,
            TokenService.Sha256(rawToken),
            DateTimeOffset.UtcNow.AddMinutes(30),
            false,
            DateTimeOffset.UtcNow);

        store.Write(() => store.PasswordResetTickets.Add(ticket));
        store.Audit(user.TenantId, user.Id, AuditAction.PasswordReset, nameof(PasswordResetTicket), ticket.Id.ToString(), "Token de recuperacion generado.");

        return new
        {
            message = environment.IsProduction()
                ? "Si el correo existe, se enviaran instrucciones de recuperacion."
                : "Token de recuperacion generado para desarrollo local.",
            devResetToken = environment.IsProduction() ? null : rawToken,
            expiresAt = ticket.ExpiresAt
        };
    }

    public object ResetPassword(string token, string newPassword)
    {
        token = InputGuard.Required(token, "Token", 512);
        ValidatePassword(newPassword);
        var hash = TokenService.Sha256(token);
        return store.Write(() =>
        {
            var ticketIndex = store.PasswordResetTickets.FindIndex(t => t.TokenHash == hash && !t.Used && t.ExpiresAt > DateTimeOffset.UtcNow);
            if (ticketIndex < 0)
            {
                throw new UnauthorizedAccessException("Token invalido o expirado.");
            }

            var ticket = store.PasswordResetTickets[ticketIndex];
            var userIndex = store.Users.FindIndex(u => u.Id == ticket.UserId);
            if (userIndex < 0)
            {
                throw new InvalidOperationException("Usuario no encontrado.");
            }

            var user = store.Users[userIndex];
            var (passwordHash, salt) = hasher.HashPassword(newPassword);
            store.Users[userIndex] = user with { PasswordHash = passwordHash, PasswordSalt = salt };
            store.PasswordResetTickets[ticketIndex] = ticket with { Used = true };
            store.RefreshTokenSessions.RemoveAll(t => t.UserId == user.Id);
            store.Audit(user.TenantId, user.Id, AuditAction.PasswordReset, nameof(UserAccount), user.Id.ToString(), "Contrasena actualizada por recuperacion.");
            return new { message = "Contrasena actualizada." };
        });
    }

    private (string RawToken, RefreshTokenSession Session) CreateRefreshSession(UserAccount user, string? ipAddress)
    {
        var refresh = BuildRefreshSession(user, ipAddress);
        store.AddRefreshTokenSession(refresh.Session);
        return refresh;
    }

    private (string RawToken, RefreshTokenSession Session) CreateRefreshSessionUnsafe(UserAccount user, string? ipAddress)
    {
        var refresh = BuildRefreshSession(user, ipAddress);
        store.RefreshTokenSessions.Add(refresh.Session);
        return refresh;
    }

    private static (string RawToken, RefreshTokenSession Session) BuildRefreshSession(UserAccount user, string? ipAddress)
    {
        var raw = TokenService.RandomToken(48);
        return (raw, new RefreshTokenSession(
            Guid.NewGuid(),
            user.TenantId,
            user.Id,
            TokenService.Sha256(raw),
            DateTimeOffset.UtcNow.AddDays(14),
            DateTimeOffset.UtcNow,
            ipAddress,
            null,
            null));
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
        {
            throw new ArgumentException("La contrasena debe tener al menos 10 caracteres.");
        }
    }
}

public sealed class CaseService(LegalPilotStore store)
{
    public IReadOnlyList<LegalCase> ListCases(AuthPrincipal principal, string? search = null, int? take = null)
    {
        var query = InputGuard.Search(search);
        var limit = InputGuard.Take(take);
        return store.Read(() =>
        {
            var items = store.Cases.Where(c => c.TenantId == principal.TenantId);
            if (!string.IsNullOrWhiteSpace(query))
            {
                items = items.Where(c =>
                    c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.CaseNumber.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Matter.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            return items.OrderByDescending(c => c.UpdatedAt).Take(limit).ToArray();
        });
    }

    public LegalCase CreateCase(AuthPrincipal principal, CreateCaseRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        var title = InputGuard.Required(request.Title, "Titulo", 180);
        var caseNumber = InputGuard.CaseNumber(request.CaseNumber);
        var matter = InputGuard.Required(request.Matter, "Materia", 80);
        var court = InputGuard.Required(request.CourtOrOffice, "Dependencia", 160);

        store.Read(() =>
        {
            if (store.Cases.Any(c => c.TenantId == principal.TenantId && c.CaseNumber.Equals(caseNumber, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConflictException("Ya existe un caso con ese numero de causa.");
            }

            if (request.ClientId.HasValue && store.Clients.All(c => c.Id != request.ClientId.Value || c.TenantId != principal.TenantId))
            {
                throw new ArgumentException("Cliente no pertenece al tenant actual.");
            }

            if (request.ResponsibleUserId.HasValue && store.Users.All(u => u.Id != request.ResponsibleUserId.Value || u.TenantId != principal.TenantId || !u.IsActive))
            {
                throw new ArgumentException("Responsable no pertenece al tenant actual.");
            }

            return true;
        });

        var now = DateTimeOffset.UtcNow;
        var legalCase = new LegalCase(
            Guid.NewGuid(),
            principal.TenantId,
            title,
            caseNumber,
            matter,
            court,
            request.ClientId,
            request.ResponsibleUserId ?? principal.UserId,
            "Activo",
            now,
            now);

        store.Write(() => store.Cases.Add(legalCase));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.Create, nameof(LegalCase), legalCase.Id.ToString(), $"Caso creado: {legalCase.Title}");
        return legalCase;
    }
}

public sealed class ClientService(LegalPilotStore store)
{
    public IReadOnlyList<ClientProfile> List(AuthPrincipal principal, string? search = null, int? take = null)
    {
        var query = InputGuard.Search(search);
        var limit = InputGuard.Take(take);
        return store.Read(() =>
        {
            var items = store.Clients.Where(c => c.TenantId == principal.TenantId);
            if (!string.IsNullOrWhiteSpace(query))
            {
                items = items.Where(c =>
                    c.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Email.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Identification.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            return items.OrderBy(c => c.FullName).Take(limit).ToArray();
        });
    }

    public ClientProfile Create(AuthPrincipal principal, CreateClientRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        var fullName = InputGuard.Required(request.FullName, "Nombre completo", 160);
        var email = InputGuard.Email(request.Email);
        var phone = InputGuard.Phone(request.Phone);
        var identification = InputGuard.Required(request.Identification, "Identificacion", 32);

        store.Read(() =>
        {
            if (store.Clients.Any(c => c.TenantId == principal.TenantId && c.Identification.Equals(identification, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConflictException("Ya existe un cliente con esa identificacion.");
            }

            return true;
        });

        var client = new ClientProfile(
            Guid.NewGuid(),
            principal.TenantId,
            fullName,
            email,
            phone,
            identification,
            DateTimeOffset.UtcNow);

        store.Write(() => store.Clients.Add(client));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.Create, nameof(ClientProfile), client.Id.ToString(), $"Cliente creado: {client.FullName}");
        return client;
    }
}

public sealed class MailboxService(LegalPilotStore store, EmailConnectorRegistry connectors)
{
    public IReadOnlyList<MailboxConnection> List(AuthPrincipal principal)
    {
        return store.Read(() => store.Mailboxes.Where(m => m.TenantId == principal.TenantId).OrderBy(m => m.Email).ToArray());
    }

    public IReadOnlyList<MailboxSyncState> SyncStates(AuthPrincipal principal)
    {
        return store.Read(() => store.MailboxSyncStates
            .Where(s => s.TenantId == principal.TenantId)
            .OrderByDescending(s => s.CheckedAt)
            .Take(100)
            .ToArray());
    }

    public MailboxConnection Connect(AuthPrincipal principal, ConnectMailboxRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
        var email = InputGuard.Email(request.Email);
        store.Read(() =>
        {
            if (store.Mailboxes.Any(m =>
                m.TenantId == principal.TenantId &&
                m.Provider == request.Provider &&
                m.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConflictException("Ya existe un buzon registrado para ese proveedor y correo.");
            }

            return true;
        });

        var scopes = OAuthService.Scopes(request.Provider);
        var readiness = connectors.Get(request.Provider).GetReadiness();

        var mailbox = new MailboxConnection(
            Guid.NewGuid(),
            principal.TenantId,
            principal.UserId,
            request.Provider,
            email,
            InputGuard.Optional(request.ExternalAccountId, 160) is { Length: > 0 } externalId ? externalId : email,
            readiness.Configured ? "PendingOAuth" : "ConfigurationMissing",
            scopes,
            DateTimeOffset.UtcNow,
            null,
            null,
            null);

        store.Write(() => store.Mailboxes.Add(mailbox));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.ConnectIntegration, nameof(MailboxConnection), mailbox.Id.ToString(), $"Buzon registrado: {mailbox.Email}. Estado: {mailbox.Status}");
        return mailbox;
    }

    public MailboxConnection UpdateCalendarPreference(AuthPrincipal principal, Guid mailboxId, UpdateMailboxCalendarRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
        var calendarId = InputGuard.Optional(request.CalendarId, 180);
        return store.Write(() =>
        {
            var index = store.Mailboxes.FindIndex(m => m.Id == mailboxId && m.TenantId == principal.TenantId);
            if (index < 0)
            {
                throw new KeyNotFoundException("Buzon no encontrado.");
            }

            var mailbox = store.Mailboxes[index] with
            {
                DefaultCalendarId = string.IsNullOrWhiteSpace(calendarId) ? null : calendarId,
                LastError = null
            };
            store.Mailboxes[index] = mailbox;
            store.Audit(principal.TenantId, principal.UserId, AuditAction.Update, nameof(MailboxConnection), mailbox.Id.ToString(), $"Calendario predeterminado actualizado para {mailbox.Provider}.");
            return mailbox;
        });
    }

    public MailboxConnection Disconnect(AuthPrincipal principal, Guid mailboxId)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
        return store.Write(() =>
        {
            var index = store.Mailboxes.FindIndex(m => m.Id == mailboxId && m.TenantId == principal.TenantId);
            if (index < 0)
            {
                throw new KeyNotFoundException("Buzon no encontrado.");
            }

            var mailbox = store.Mailboxes[index] with
            {
                Status = "Disconnected",
                Cursor = null,
                WatchExpiresAt = null,
                WebhookSubscriptionId = null,
                WebhookRenewedAt = DateTimeOffset.UtcNow,
                LastError = null
            };
            store.Mailboxes[index] = mailbox;

            for (var i = 0; i < store.OAuthTokenCredentials.Count; i++)
            {
                var token = store.OAuthTokenCredentials[i];
                if (token.TenantId == principal.TenantId && token.MailboxConnectionId == mailbox.Id && token.Status != "Revoked")
                {
                    store.OAuthTokenCredentials[i] = token with
                    {
                        Status = "Revoked",
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                }
            }

            store.Audit(principal.TenantId, principal.UserId, AuditAction.DisconnectIntegration, nameof(MailboxConnection), mailbox.Id.ToString(), $"Buzon desconectado localmente: {mailbox.Email}.");
            return mailbox;
        });
    }

    public async Task<MailboxSyncState> Sync(AuthPrincipal principal, Guid mailboxId, CancellationToken cancellationToken)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        var mailbox = store.Read(() => store.Mailboxes.FirstOrDefault(m => m.Id == mailboxId && m.TenantId == principal.TenantId))
            ?? throw new KeyNotFoundException("Buzon no encontrado.");
        var connector = connectors.Get(mailbox.Provider);
        MailboxSyncResult result;
        try
        {
            result = await connector.SyncAsync(mailbox, cancellationToken);
        }
        catch (Exception ex)
        {
            result = new MailboxSyncResult(false, "SyncError", $"Sincronizacion registrada con error controlado: {ex.Message}", DateTimeOffset.UtcNow.AddMinutes(15), mailbox.Cursor, mailbox.WatchExpiresAt, mailbox.WebhookSubscriptionId);
        }

        return RecordSyncState(mailbox, result, principal.UserId);
    }

    public async Task<MailboxSyncState?> SyncProviderFromWebhook(Guid tenantId, MailProvider provider, string? emailAddress, CancellationToken cancellationToken)
    {
        var mailbox = store.Read(() =>
        {
            var query = store.Mailboxes.Where(m => m.TenantId == tenantId && m.Provider == provider);
            if (!string.IsNullOrWhiteSpace(emailAddress))
            {
                var normalized = emailAddress.Trim();
                return query.FirstOrDefault(m => m.Email.Equals(normalized, StringComparison.OrdinalIgnoreCase)) ?? query.FirstOrDefault();
            }

            return query.FirstOrDefault();
        });

        if (mailbox is null)
        {
            return null;
        }

        var result = await connectors.Get(provider).SyncAsync(mailbox, cancellationToken);
        return RecordSyncState(mailbox, result, null);
    }

    private MailboxSyncState RecordSyncState(MailboxConnection mailbox, MailboxSyncResult result, Guid? actorUserId)
    {
        return store.Write(() =>
        {
            var previousFailures = store.MailboxSyncStates
                .Where(s => s.MailboxConnectionId == mailbox.Id)
                .OrderByDescending(s => s.CheckedAt)
                .FirstOrDefault()?.FailureCount ?? 0;

            var state = new MailboxSyncState(
                Guid.NewGuid(),
                mailbox.TenantId,
                mailbox.Id,
                mailbox.Provider,
                result.Status,
                result.Message,
                DateTimeOffset.UtcNow,
                result.NextAttemptAt,
                result.Success ? 0 : previousFailures + 1);

            store.MailboxSyncStates.Insert(0, state);
            var index = store.Mailboxes.FindIndex(m => m.Id == mailbox.Id);
            var current = index >= 0 ? store.Mailboxes[index] : mailbox;
            store.Mailboxes[index] = current with
            {
                Status = result.Status,
                LastSyncAt = DateTimeOffset.UtcNow,
                Cursor = result.Cursor ?? current.Cursor,
                WatchExpiresAt = result.WatchExpiresAt ?? current.WatchExpiresAt,
                WebhookSubscriptionId = result.SubscriptionId ?? current.WebhookSubscriptionId,
                WebhookRenewedAt = result.SubscriptionId is null ? current.WebhookRenewedAt : DateTimeOffset.UtcNow,
                LastError = result.Success ? null : result.Message
            };
            store.Audit(mailbox.TenantId, actorUserId, AuditAction.SyncAttempt, nameof(MailboxConnection), mailbox.Id.ToString(), result.Message);
            return state;
        });
    }
}

public sealed class LegalWorkflowService(
    LegalPilotStore store,
    LegalIntelligenceService intelligence,
    EcuadorDeadlineEngine deadlineEngine,
    IConfiguration configuration,
    ExternalCalendarSyncService? externalCalendars = null,
    ILogger<LegalWorkflowService>? logger = null)
{
    public LegalEmail IngestManual(AuthPrincipal principal, ManualEmailRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        var provider = request.Provider;
        var subject = InputGuard.Required(request.Subject, "Asunto", 240);
        var sender = InputGuard.Email(request.Sender, "Remitente");
        var bodyText = PrepareBodyOrThrow(request.BodyText);
        EnsureCaseAndMailbox(principal.TenantId, request.CaseId, request.MailboxConnectionId);
        var externalMessageId = InputGuard.Optional(request.ExternalMessageId, 180);
        var messageHash = BuildMessageHash(provider, externalMessageId, subject, sender, bodyText, request.ReceivedAt);
        if (!string.IsNullOrWhiteSpace(externalMessageId))
        {
            var existing = FindExistingEmail(principal.TenantId, provider, externalMessageId);
            if (existing is not null)
            {
                store.Audit(principal.TenantId, principal.UserId, AuditAction.IngestEmail, nameof(LegalEmail), existing.Id.ToString(), "Correo duplicado omitido por idempotencia.");
                return existing;
            }
        }
        else if (FindExistingEmailByHash(principal.TenantId, provider, messageHash) is { } existing)
        {
            store.Audit(principal.TenantId, principal.UserId, AuditAction.IngestEmail, nameof(LegalEmail), existing.Id.ToString(), "Correo duplicado omitido por huella de mensaje.");
            return existing;
        }

        var emailId = Guid.NewGuid();
        var attachments = BuildAttachments(principal.TenantId, emailId, request.Attachments);
        var analysisBody = AppendAttachmentText(bodyText, attachments);
        var storedBody = HtmlSanitizer.ClipForStorage(analysisBody);
        var extraction = intelligence.Extract(subject, HtmlSanitizer.ClipForAnalysis(analysisBody));
        var caseId = ResolveCaseId(principal.TenantId, extraction.CaseNumber, request.CaseId);
        var processedWithFallback = ExtractionUsedFallback(extraction);
        var canAutoProcess = CanAutoConfirmExtraction(caseId, extraction);
        var now = DateTimeOffset.UtcNow;

        var email = new LegalEmail(
            emailId,
            principal.TenantId,
            request.MailboxConnectionId,
            caseId,
            provider,
            string.IsNullOrWhiteSpace(externalMessageId) ? $"manual-{Guid.NewGuid():N}" : externalMessageId,
            subject,
            sender,
            request.Recipients ?? [],
            storedBody,
            InputGuard.Optional(request.RawReference, 200) is { Length: > 0 } rawReference ? rawReference : "manual",
            request.ReceivedAt ?? now,
            processedWithFallback && !canAutoProcess ? "RequiresManualReview" : "Processed",
            extraction,
            now,
            messageHash,
            processedWithFallback,
            processedWithFallback ? "LLM externo no disponible o salida invalida." : null);

        store.Write(() =>
        {
            store.Emails.Insert(0, email);
            store.Attachments.AddRange(attachments);
            store.MailProcessingLogs.Insert(0, new MailProcessingLog(Guid.NewGuid(), email.TenantId, email.MailboxConnectionId, email.Id, email.Provider, "ingest", "Completed", "Correo manual persistido y clasificado.", 1, now, null));
        });
        store.Audit(principal.TenantId, principal.UserId, AuditAction.IngestEmail, nameof(LegalEmail), email.Id.ToString(), $"Correo legal ingerido: {email.Subject}");
        store.Audit(principal.TenantId, principal.UserId, AuditAction.ClassifyEmail, nameof(LegalEmail), email.Id.ToString(), extraction.LawyerSummary);

        CreateDerivedWork(email, principal.UserId);
        return email;
    }

    public LegalEmail IngestWebhook(Guid tenantId, MailProvider provider, WebhookEmailEnvelope envelope)
    {
        try
        {
            return IngestWebhookCore(tenantId, provider, envelope);
        }
        catch (Exception ex)
        {
            return RecordFailedWebhookEmail(tenantId, provider, envelope, "Error", ex.Message);
        }
    }

    private LegalEmail IngestWebhookCore(Guid tenantId, MailProvider provider, WebhookEmailEnvelope envelope)
    {
        var externalMessageId = InputGuard.Optional(envelope.ExternalMessageId, 180);
        var subject = SafeSubject(envelope.Subject);
        var sender = SafeSender(envelope.Sender, provider);
        var bodyText = PrepareBodyOrThrow(envelope.BodyText);
        var messageHash = BuildMessageHash(provider, externalMessageId, subject, sender, bodyText, envelope.ReceivedAt);
        if (!string.IsNullOrWhiteSpace(externalMessageId))
        {
            var existing = FindExistingEmail(tenantId, provider, externalMessageId);
            if (existing is not null)
            {
                store.Audit(tenantId, null, AuditAction.WebhookReceived, nameof(LegalEmail), existing.Id.ToString(), $"Webhook {provider} duplicado omitido.");
                return existing;
            }
        }
        else if (FindExistingEmailByHash(tenantId, provider, messageHash) is { } existing)
        {
            store.Audit(tenantId, null, AuditAction.WebhookReceived, nameof(LegalEmail), existing.Id.ToString(), $"Webhook {provider} duplicado omitido por huella.");
            return existing;
        }

        var emailId = Guid.NewGuid();
        var attachments = BuildAttachments(tenantId, emailId, envelope.Attachments);
        var analysisBody = AppendAttachmentText(bodyText, attachments);
        var storedBody = HtmlSanitizer.ClipForStorage(analysisBody);
        if (!LooksLikeLegalNotification(subject, sender, analysisBody))
        {
            return RecordIgnoredWebhookEmail(tenantId, provider, envelope, externalMessageId, subject, sender, storedBody, messageHash);
        }

        var extraction = intelligence.Extract(subject, HtmlSanitizer.ClipForAnalysis(analysisBody));
        var caseId = ResolveCaseId(tenantId, extraction.CaseNumber, null);
        var processedWithFallback = ExtractionUsedFallback(extraction);
        var canAutoProcess = CanAutoConfirmExtraction(caseId, extraction);
        var now = DateTimeOffset.UtcNow;
        var email = new LegalEmail(
            emailId,
            tenantId,
            envelope.MailboxConnectionId,
            caseId,
            provider,
            string.IsNullOrWhiteSpace(externalMessageId) ? $"webhook-{Guid.NewGuid():N}" : externalMessageId,
            subject,
            sender,
            envelope.Recipients ?? [],
            storedBody,
            InputGuard.Optional(envelope.RawReference, 200) is { Length: > 0 } rawReference ? rawReference : provider.ToString(),
            envelope.ReceivedAt ?? now,
            processedWithFallback && !canAutoProcess ? "RequiresManualReview" : "ProcessedFromWebhook",
            extraction,
            now,
            messageHash,
            processedWithFallback,
            processedWithFallback ? "LLM externo no disponible o salida invalida." : null);

        store.Write(() =>
        {
            store.Emails.Insert(0, email);
            store.Attachments.AddRange(attachments);
            store.MailProcessingLogs.Insert(0, new MailProcessingLog(Guid.NewGuid(), tenantId, envelope.MailboxConnectionId, email.Id, provider, "webhook-ingest", "Completed", $"Correo persistido desde {provider}. Adjuntos: {attachments.Count}.", 1, now, null));
        });
        store.Audit(tenantId, null, AuditAction.WebhookReceived, nameof(LegalEmail), email.Id.ToString(), $"Webhook {provider} procesado. Adjuntos: {attachments.Count}.");
        CreateDerivedWork(email, null);
        return email;
    }

    private LegalEmail RecordFailedWebhookEmail(Guid tenantId, MailProvider provider, WebhookEmailEnvelope envelope, string status, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var externalMessageId = SafeExternalMessageId(envelope.ExternalMessageId) ?? $"webhook-error-{Guid.NewGuid():N}";
        var subject = SafeSubject(envelope.Subject);
        var sender = SafeSender(envelope.Sender, provider);
        var bodyText = HtmlSanitizer.ToLegalInnerText(envelope.BodyText);
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            bodyText = subject;
        }

        var storedBody = HtmlSanitizer.ClipForStorage(bodyText);
        var messageHash = BuildMessageHash(provider, externalMessageId, subject, sender, storedBody, envelope.ReceivedAt);
        if (FindExistingEmail(tenantId, provider, externalMessageId) is { } existingById)
        {
            return existingById;
        }

        if (FindExistingEmailByHash(tenantId, provider, messageHash) is { } existingByHash)
        {
            return existingByHash;
        }

        var errorSummary = Truncate($"No se pudo procesar automaticamente este correo. Motivo: {reason}", 360);
        var extraction = new LegalExtraction(
            LegalActType.Unknown,
            null,
            null,
            null,
            null,
            null,
            null,
            "Revision manual requerida antes de crear plazos o agenda.",
            true,
            "Alta",
            0.35m,
            errorSummary,
            "El estudio revisara esta notificacion manualmente.",
            "Borrador: revisar el correo original y registrar manualmente la actuacion necesaria.",
            ["processing-error", "requires-manual-review"],
            [],
            []);

        var email = new LegalEmail(
            Guid.NewGuid(),
            tenantId,
            envelope.MailboxConnectionId,
            null,
            provider,
            externalMessageId,
            subject,
            sender,
            envelope.Recipients ?? [],
            storedBody,
            SafeRawReference(envelope.RawReference) ?? provider.ToString(),
            envelope.ReceivedAt ?? now,
            status,
            extraction,
            now,
            messageHash,
            true,
            errorSummary);

        store.Write(() =>
        {
            store.Emails.Insert(0, email);
            store.MailProcessingLogs.Insert(0, new MailProcessingLog(Guid.NewGuid(), tenantId, envelope.MailboxConnectionId, email.Id, provider, "webhook-ingest", status, errorSummary, 1, now, null));
        });
        store.Audit(tenantId, null, AuditAction.ProcessMail, nameof(LegalEmail), email.Id.ToString(), errorSummary);
        NotifyProcessingIssue(email, errorSummary);
        return email;
    }

    private LegalEmail RecordIgnoredWebhookEmail(Guid tenantId, MailProvider provider, WebhookEmailEnvelope envelope, string? externalMessageId, string subject, string sender, string bodyText, string messageHash)
    {
        externalMessageId = string.IsNullOrWhiteSpace(externalMessageId) ? $"ignored-{Guid.NewGuid():N}" : externalMessageId;
        if (FindExistingEmail(tenantId, provider, externalMessageId) is { } existingById)
        {
            return existingById;
        }

        if (FindExistingEmailByHash(tenantId, provider, messageHash) is { } existingByHash)
        {
            return existingByHash;
        }

        var now = DateTimeOffset.UtcNow;
        var extraction = new LegalExtraction(
            LegalActType.Unknown,
            null,
            null,
            null,
            null,
            null,
            null,
            "Correo no legal omitido por filtro de inbox.",
            false,
            "Normal",
            0.90m,
            "Correo omitido: no contiene senales suficientes de SATJE, Fiscalia o actuacion judicial.",
            string.Empty,
            string.Empty,
            ["ignored-non-legal"],
            [],
            []);

        var email = new LegalEmail(
            Guid.NewGuid(),
            tenantId,
            envelope.MailboxConnectionId,
            null,
            provider,
            externalMessageId,
            subject,
            sender,
            envelope.Recipients ?? [],
            bodyText,
            SafeRawReference(envelope.RawReference) ?? provider.ToString(),
            envelope.ReceivedAt ?? now,
            "IgnoredNonLegal",
            extraction,
            now,
            messageHash,
            false,
            null);

        store.Write(() =>
        {
            store.Emails.Insert(0, email);
            store.MailProcessingLogs.Insert(0, new MailProcessingLog(Guid.NewGuid(), tenantId, envelope.MailboxConnectionId, email.Id, provider, "webhook-ingest", "IgnoredNonLegal", "Correo omitido por filtro legal.", 1, now, null));
        });
        return email;
    }

    public DeadlineCalculation Calculate(Guid tenantId, DeadlineRequest request)
    {
        _ = InputGuard.TermDays(request.TermDays);
        var holidays = store.Read(() => store.Holidays.Where(h => h.TenantId == tenantId).ToArray());
        return deadlineEngine.Calculate(request, holidays);
    }

    public Deadline CreateDeadline(AuthPrincipal principal, CreateDeadlineRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        var title = InputGuard.Required(request.Title, "Titulo", 180);
        var matter = InputGuard.Required(request.Matter, "Materia", 80);
        var termDays = InputGuard.TermDays(request.TermDays);
        EnsureCaseAndResponsible(principal.TenantId, request.CaseId, request.ResponsibleUserId);
        var holidays = store.Read(() => store.Holidays.Where(h => h.TenantId == principal.TenantId).ToArray());
        var calculation = deadlineEngine.Calculate(
            new DeadlineRequest(
                request.NotificationDate,
                termDays,
                matter,
                InputGuard.Optional(request.Province, 80),
                InputGuard.Optional(request.Canton, 80),
                InputGuard.Optional(request.RuleCode, 80) is { Length: > 0 } ruleCode ? ruleCode : "EC-COGEP-TERM-BUSINESS-DAYS-V1"),
            holidays);

        var responsible = request.ResponsibleUserId ?? principal.UserId;
        var deadline = new Deadline(
            Guid.NewGuid(),
            principal.TenantId,
            request.CaseId,
            null,
            title,
            LegalActType.Deadline,
            request.NotificationDate,
            termDays,
            calculation.DueDate,
            request.Confirmed ? DeadlineStatus.Confirmed : DeadlineStatus.PendingReview,
            responsible,
            1.00m,
            calculation,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        store.Write(() => store.Deadlines.Insert(0, deadline));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.CalculateDeadline, nameof(Deadline), deadline.Id.ToString(), calculation.Explanation);

        var starts = ToQuitoDateTimeOffset(calculation.DueDate, new TimeOnly(9, 0));
        var calendarEvent = new CalendarEvent(
            Guid.NewGuid(),
            principal.TenantId,
            request.CaseId,
            deadline.Id,
            CalendarEventType.Deadline,
            deadline.Title,
            null,
            starts,
            starts.AddMinutes(30),
            responsible,
            !request.Confirmed,
            request.Confirmed,
            null,
            null,
            DateTimeOffset.UtcNow,
            null,
            "Scheduled",
            request.Confirmed ? "Pending" : "NeedsConfirmation");
        store.Write(() => store.CalendarEvents.Insert(0, calendarEvent));
        CreateReminderSet(principal.TenantId, calendarEvent, deadline.Title, holidays);
        QueueExternalCalendarSync(calendarEvent, responsible);
        return deadline;
    }

    private LegalEmail? FindExistingEmail(Guid tenantId, MailProvider? provider, string externalMessageId)
    {
        return store.Read(() => store.Emails.FirstOrDefault(e =>
            e.TenantId == tenantId &&
            e.Provider == provider &&
            e.ExternalMessageId.Equals(externalMessageId, StringComparison.OrdinalIgnoreCase)));
    }

    private LegalEmail? FindExistingEmailByHash(Guid tenantId, MailProvider? provider, string messageHash)
    {
        return store.Read(() => store.Emails.FirstOrDefault(e =>
            e.TenantId == tenantId &&
            e.Provider == provider &&
            !string.IsNullOrWhiteSpace(e.MessageHash) &&
            e.MessageHash.Equals(messageHash, StringComparison.OrdinalIgnoreCase)));
    }

    private static string BuildMessageHash(MailProvider? provider, string? externalMessageId, string subject, string sender, string body, DateTimeOffset? receivedAt)
    {
        var basis = string.IsNullOrWhiteSpace(externalMessageId)
            ? $"{provider}|{sender}|{subject}|{receivedAt:O}|{body[..Math.Min(body.Length, 400)]}"
            : $"{provider}|{externalMessageId}";
        return TokenService.Sha256(basis);
    }

    private static bool ExtractionUsedFallback(LegalExtraction extraction)
    {
        return extraction.Signals.Any(s => s.Equals("fallback-local-heuristic", StringComparison.OrdinalIgnoreCase));
    }

    private static string PrepareBodyOrThrow(string? body)
    {
        var text = HtmlSanitizer.ToLegalInnerText(body);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Cuerpo es obligatorio.");
        }

        if (text.Length > HtmlSanitizer.MaxStoredBodyCharacters)
        {
            throw new ArgumentException($"Cuerpo saneado supera {HtmlSanitizer.MaxStoredBodyCharacters} caracteres.");
        }

        return text;
    }

    private static string SafeSubject(string? value)
    {
        var text = CollapseInlineWhitespace(HtmlSanitizer.ToLegalInnerText(value));
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "(sin asunto)";
        }

        return Truncate(text, 240);
    }

    private static string SafeSender(string? value, MailProvider provider)
    {
        var text = CollapseInlineWhitespace(HtmlSanitizer.ToLegalInnerText(value));
        if (string.IsNullOrWhiteSpace(text))
        {
            text = provider.ToString();
        }

        return Truncate(text, 254);
    }

    private static string? SafeExternalMessageId(string? value)
    {
        value = CollapseInlineWhitespace(value ?? string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : Truncate(value, 180);
    }

    private static string? SafeRawReference(string? value)
    {
        value = CollapseInlineWhitespace(value ?? string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : Truncate(value, 200);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static string CollapseInlineWhitespace(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool LooksLikeLegalNotification(string subject, string sender, string body)
    {
        var normalized = RemoveDiacritics($"{sender}\n{subject}\n{body}").ToLowerInvariant();
        var score = 0;
        string[] strong =
        [
            "funcion judicial",
            "satje",
            "fiscalia general",
            "expediente fiscal",
            "investigacion previa",
            "juicio no",
            "proceso numero",
            "casillero judicial",
            "unidad judicial",
            "codigo organico integral penal",
            " coip",
            "providencia",
            "oficiese",
            "cumplase"
        ];
        string[] weak =
        [
            "notificacion",
            "audiencia",
            "plazo",
            "termino",
            "pericia",
            "versiones fiscalia",
            "diligencia",
            "juez",
            "secretario"
        ];

        score += strong.Count(normalized.Contains) * 2;
        score += weak.Count(normalized.Contains);
        return score >= 2;
    }

    private void NotifyProcessingIssue(LegalEmail email, string message)
    {
        var responsible = store.Read(() => store.Users.FirstOrDefault(u =>
            u.TenantId == email.TenantId &&
            u.IsActive &&
            (u.Roles.Contains(UserRole.Lawyer) || u.Roles.Contains(UserRole.SuperAdmin)))?.Id);
        if (responsible is null)
        {
            return;
        }

        store.Write(() => store.Notifications.Insert(0, new Notification(
            Guid.NewGuid(),
            email.TenantId,
            responsible.Value,
            NotificationChannel.Panel,
            "Correo requiere revision manual",
            message,
            NotificationStatus.Sent,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null)));
    }

    private void EnsureCaseAndMailbox(Guid tenantId, Guid? caseId, Guid? mailboxConnectionId)
    {
        store.Read(() =>
        {
            if (caseId.HasValue && store.Cases.All(c => c.Id != caseId.Value || c.TenantId != tenantId))
            {
                throw new ArgumentException("Caso no pertenece al tenant actual.");
            }

            if (mailboxConnectionId.HasValue && store.Mailboxes.All(m => m.Id != mailboxConnectionId.Value || m.TenantId != tenantId))
            {
                throw new ArgumentException("Buzon no pertenece al tenant actual.");
            }

            return true;
        });
    }

    private void EnsureCaseAndResponsible(Guid tenantId, Guid? caseId, Guid? responsibleUserId)
    {
        store.Read(() =>
        {
            if (caseId.HasValue && store.Cases.All(c => c.Id != caseId.Value || c.TenantId != tenantId))
            {
                throw new ArgumentException("Caso no pertenece al tenant actual.");
            }

            if (responsibleUserId.HasValue && store.Users.All(u => u.Id != responsibleUserId.Value || u.TenantId != tenantId || !u.IsActive))
            {
                throw new ArgumentException("Responsable no pertenece al tenant actual.");
            }

            return true;
        });
    }

    private Guid? ResolveCaseId(Guid tenantId, string? caseNumber, Guid? explicitCaseId)
    {
        if (explicitCaseId.HasValue)
        {
            return explicitCaseId;
        }

        if (string.IsNullOrWhiteSpace(caseNumber))
        {
            return null;
        }

        return store.Read(() => store.Cases.FirstOrDefault(c =>
            c.TenantId == tenantId &&
            c.CaseNumber.Equals(caseNumber, StringComparison.OrdinalIgnoreCase))?.Id);
    }

    private void CreateDerivedWork(LegalEmail email, Guid? actorUserId)
    {
        if (email.Extraction is null)
        {
            return;
        }

        var responsible = ResolveResponsible(email);
        var holidays = store.Read(() => store.Holidays.Where(h => h.TenantId == email.TenantId).ToArray());
        var autoConfirmDerivedWork = CanAutoConfirmDerivedWork(email);
        CreateInboxNotification(email, responsible);

        var extractedDeadlines = (email.Extraction.Deadlines is { Count: > 0 }
                ? email.Extraction.Deadlines
                : email.Extraction.TermDays.HasValue
                    ? [new ExtractedDeadline(email.Extraction.TermDays.Value, null, email.Extraction.Obligation)]
                    : [])
            .Where(d => d.GrantedDays.HasValue || d.GrantedHours.HasValue)
            .ToArray();

        foreach (var extractedDeadline in extractedDeadlines)
        {
            var termDays = extractedDeadline.GrantedDays ?? Math.Clamp((int)Math.Ceiling((extractedDeadline.GrantedHours ?? 24) / 24m), 1, 180);
            var notificationDate = DateOnly.FromDateTime(email.ReceivedAt.ToOffset(TimeSpan.FromHours(-5)).Date);
            var title = BuildDeadlineTitle(email.Subject, extractedDeadline);
            var exists = store.Read(() => store.Deadlines.Any(d =>
                d.TenantId == email.TenantId &&
                d.LegalEmailId == email.Id &&
                d.TermDays == termDays &&
                d.Title.Equals(title, StringComparison.OrdinalIgnoreCase)));
            if (exists)
            {
                continue;
            }

            var calculation = deadlineEngine.Calculate(
                new DeadlineRequest(notificationDate, InputGuard.TermDays(termDays), "general"),
                holidays);

            var status = autoConfirmDerivedWork ? DeadlineStatus.Confirmed : DeadlineStatus.PendingReview;
            var deadline = new Deadline(
                Guid.NewGuid(),
                email.TenantId,
                email.CaseId,
                email.Id,
                title,
                email.Extraction.ActType,
                notificationDate,
                termDays,
                calculation.DueDate,
                status,
                responsible,
                email.Extraction.Confidence,
                calculation,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);

            store.Write(() => store.Deadlines.Insert(0, deadline));
            store.Audit(email.TenantId, actorUserId, AuditAction.CalculateDeadline, nameof(Deadline), deadline.Id.ToString(), calculation.Explanation);

            var starts = ToQuitoDateTimeOffset(calculation.DueDate, new TimeOnly(9, 0));
            var calendarEvent = CreateCalendarEvent(
                email,
                deadline.Id,
                CalendarEventType.Deadline,
                deadline.Title,
                null,
                starts,
                starts.AddMinutes(30),
                responsible,
                !autoConfirmDerivedWork);
            CreateReminderSet(email.TenantId, calendarEvent, deadline.Title, holidays);
            QueueExternalCalendarSync(calendarEvent, responsible);
        }

        var extractedHearings = (email.Extraction.Hearings is { Count: > 0 }
                ? email.Extraction.Hearings
                : email.Extraction.EventDate.HasValue
                    ? [new ExtractedHearing(email.Extraction.EventDate, email.Extraction.EventTime, email.Extraction.ActType.ToString(), email.Extraction.Location, null)]
                    : [])
            .Where(h => h.Date.HasValue)
            .ToArray();

        foreach (var hearing in extractedHearings)
        {
            var time = hearing.Time ?? new TimeOnly(9, 0);
            var starts = ToQuitoDateTimeOffset(hearing.Date!.Value, time);
            var type = ResolveCalendarEventType(email.Extraction.ActType, hearing.Type);

            var title = BuildHearingTitle(email.Subject, hearing, type);
            var exists = store.Read(() => store.CalendarEvents.Any(e =>
                e.TenantId == email.TenantId &&
                e.CaseId == email.CaseId &&
                e.StartsAt == starts &&
                e.Title.Equals(title, StringComparison.OrdinalIgnoreCase)));

            if (exists)
            {
                continue;
            }

            var calendarEvent = CreateCalendarEvent(
                email,
                null,
                type,
                title,
                hearing.LinkZoom ?? hearing.Location ?? email.Extraction.Location,
                starts,
                starts.AddHours(1),
                responsible,
                !autoConfirmDerivedWork);
            CreateReminderSet(email.TenantId, calendarEvent, title, holidays);
            QueueExternalCalendarSync(calendarEvent, responsible);
        }
    }

    private static CalendarEventType ResolveCalendarEventType(LegalActType actType, string? hearingType)
    {
        var normalized = RemoveDiacritics(hearingType ?? string.Empty).ToLowerInvariant();
        if (normalized.Contains("pericia") || actType == LegalActType.ExpertReview)
        {
            return CalendarEventType.ExpertReview;
        }

        if (normalized.Contains("version") || normalized.Contains("fiscalia") || actType == LegalActType.ProsecutorNotification)
        {
            return CalendarEventType.Diligence;
        }

        return actType switch
            {
                LegalActType.Hearing => CalendarEventType.Hearing,
                LegalActType.ExpertReview => CalendarEventType.ExpertReview,
                _ => CalendarEventType.Diligence
            };
    }

    private static string BuildDeadlineTitle(string subject, ExtractedDeadline deadline)
    {
        var condition = string.IsNullOrWhiteSpace(deadline.Condition)
            ? subject
            : deadline.Condition;
        var unit = deadline.GrantedDays.HasValue
            ? $"{deadline.GrantedDays.Value} dias"
            : $"{deadline.GrantedHours ?? 0} horas";
        return Truncate($"Plazo {unit}: {condition}", 180);
    }

    private static string BuildHearingTitle(string subject, ExtractedHearing hearing, CalendarEventType type)
    {
        var label = string.IsNullOrWhiteSpace(hearing.Type) ? type.ToString() : hearing.Type;
        return Truncate($"{label}: {subject}", 180);
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private bool CanAutoConfirmDerivedWork(LegalEmail email)
    {
        return email.Extraction is not null && CanAutoConfirmExtraction(email.CaseId, email.Extraction);
    }

    private bool CanAutoConfirmExtraction(Guid? caseId, LegalExtraction extraction)
    {
        var configured = configuration["LegalPilot:Automation:AutoConfirmDerivedCalendar"];
        var enabled = string.IsNullOrWhiteSpace(configured) ||
                      string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase);
        var thresholdText = configuration["LegalPilot:Automation:AutoConfirmMinConfidence"];
        var threshold = decimal.TryParse(thresholdText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.70m;
        return enabled &&
               extraction.Confidence >= threshold &&
               HasCompleteAutomationEvidence(caseId, extraction);
    }

    private static bool HasCompleteAutomationEvidence(Guid? caseId, LegalExtraction extraction)
    {
        var hasCase = caseId.HasValue || !string.IsNullOrWhiteSpace(extraction.CaseNumber);
        var hasType = extraction.ActType != LegalActType.Unknown;
        var hasEventDate = extraction.EventDate.HasValue ||
                           extraction.Hearings?.Any(h => h.Date.HasValue) == true;
        var hasDeadlineTerm = extraction.TermDays is > 0 ||
                              extraction.Deadlines?.Any(d => d.GrantedDays is > 0 || d.GrantedHours is > 0) == true;

        return hasCase && hasType && (hasEventDate || hasDeadlineTerm);
    }

    private Guid ResolveResponsible(LegalEmail email)
    {
        if (email.CaseId.HasValue)
        {
            var legalCase = store.Read(() => store.Cases.FirstOrDefault(c => c.Id == email.CaseId.Value && c.TenantId == email.TenantId));
            if (legalCase is not null)
            {
                return legalCase.ResponsibleUserId;
            }
        }

        return store.Read(() => store.Users.First(u => u.TenantId == email.TenantId && u.Roles.Contains(UserRole.Lawyer)).Id);
    }

    private CalendarEvent CreateCalendarEvent(
        LegalEmail email,
        Guid? deadlineId,
        CalendarEventType type,
        string title,
        string? location,
        DateTimeOffset starts,
        DateTimeOffset ends,
        Guid responsible,
        bool requiresConfirmation)
    {
        var calendarEvent = new CalendarEvent(
            Guid.NewGuid(),
            email.TenantId,
            email.CaseId,
            deadlineId,
            type,
            title,
            location,
            starts,
            ends,
            responsible,
            requiresConfirmation,
            !requiresConfirmation,
            null,
            null,
            DateTimeOffset.UtcNow,
            null,
            "Scheduled",
            requiresConfirmation ? "NeedsConfirmation" : "Pending");

        store.Write(() => store.CalendarEvents.Insert(0, calendarEvent));
        store.Audit(email.TenantId, responsible, AuditAction.CreateCalendarEvent, nameof(CalendarEvent), calendarEvent.Id.ToString(), $"Evento creado: {title}");
        return calendarEvent;
    }

    private void QueueExternalCalendarSync(CalendarEvent calendarEvent, Guid actorUserId)
    {
        if (externalCalendars is null ||
            !calendarEvent.Confirmed ||
            calendarEvent.SyncStatus is not ("Pending" or "PendingUpdate"))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var principal = ResolveAutomationPrincipal(calendarEvent.TenantId, actorUserId);
                if (principal is null)
                {
                    logger?.LogWarning("Calendar event {EventId} could not sync automatically because no active automation user exists.", calendarEvent.Id);
                    return;
                }

                var result = await externalCalendars.SyncEventAsync(principal, calendarEvent.Id, CancellationToken.None);
                if (!result.Success)
                {
                    logger?.LogWarning("Calendar event {EventId} external sync finished with {Status}: {Message}", calendarEvent.Id, result.Status, result.Message);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Calendar event {EventId} failed during zero-touch external sync.", calendarEvent.Id);
            }
        });
    }

    private AuthPrincipal? ResolveAutomationPrincipal(Guid tenantId, Guid preferredUserId)
    {
        return store.Read(() =>
        {
            var user = store.Users.FirstOrDefault(u =>
                u.Id == preferredUserId &&
                u.TenantId == tenantId &&
                u.IsActive);

            user ??= store.Users.FirstOrDefault(u =>
                u.TenantId == tenantId &&
                u.IsActive &&
                (u.Roles.Contains(UserRole.Lawyer) ||
                 u.Roles.Contains(UserRole.SuperAdmin) ||
                 u.Roles.Contains(UserRole.Assistant)));

            return user is null ? null : new AuthPrincipal(user.Id, tenantId, user.Email, user.Roles);
        });
    }

    private void CreateReminderSet(Guid tenantId, CalendarEvent calendarEvent, string title, IReadOnlyList<Holiday> holidays)
    {
        int[] offsets = [30, 15, 7, 3, 1, 0];
        var reminders = new List<Reminder>();
        foreach (var offset in offsets)
        {
            var targetDate = DateOnly.FromDateTime(calendarEvent.StartsAt.AddDays(-offset).Date);
            var businessDate = offset == 0 ? targetDate : deadlineEngine.MoveBackToBusinessDay(targetDate, holidays);
            var sendAt = ToQuitoDateTimeOffset(businessDate, offset == 0 ? new TimeOnly(7, 30) : new TimeOnly(8, 0));
            if (sendAt < DateTimeOffset.UtcNow.AddMinutes(-1))
            {
                continue;
            }

            var message = offset == 0 ? $"Hoy: {title}" : $"Recordatorio T-{offset}: {title}";
            var channels = ReminderChannels(calendarEvent, offset);
            foreach (var channel in channels)
            {
                var duplicate = store.Read(() => store.Reminders.Any(r =>
                    r.TenantId == tenantId &&
                    r.CalendarEventId == calendarEvent.Id &&
                    r.Channel == channel &&
                    r.SendAt == sendAt &&
                    r.Message == message));

                if (duplicate)
                {
                    continue;
                }

                reminders.Add(new Reminder(
                    Guid.NewGuid(),
                    tenantId,
                    calendarEvent.Id,
                    channel,
                    sendAt,
                    message,
                    NotificationStatus.Pending,
                    DateTimeOffset.UtcNow));
            }
        }

        store.Write(() => store.Reminders.AddRange(reminders));
    }

    private NotificationChannel[] ReminderChannels(CalendarEvent calendarEvent, int offset)
    {
        if (offset is 7 or 1 or 0 && ClientForEvent(calendarEvent) is { Phone.Length: > 0 })
        {
            return [NotificationChannel.Panel, NotificationChannel.WhatsApp];
        }

        return [NotificationChannel.Panel];
    }

    private ClientProfile? ClientForEvent(CalendarEvent calendarEvent)
    {
        if (!calendarEvent.CaseId.HasValue)
        {
            return null;
        }

        return store.Read(() =>
        {
            var legalCase = store.Cases.FirstOrDefault(c => c.Id == calendarEvent.CaseId.Value && c.TenantId == calendarEvent.TenantId);
            return legalCase?.ClientId is { } clientId
                ? store.Clients.FirstOrDefault(c => c.Id == clientId && c.TenantId == calendarEvent.TenantId)
                : null;
        });
    }

    private void CreateInboxNotification(LegalEmail email, Guid responsible)
    {
        var title = email.Extraction?.Priority.Equals("Alta", StringComparison.OrdinalIgnoreCase) == true
            ? "Correo legal urgente"
            : "Correo legal procesado";
        var message = email.Extraction?.LawyerSummary ?? email.Subject;
        store.Write(() =>
        {
            var duplicate = store.Notifications.Any(n =>
                n.TenantId == email.TenantId &&
                n.UserId == responsible &&
                n.Title == title &&
                n.Message == message &&
                n.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
            if (duplicate)
            {
                return;
            }

            store.Notifications.Insert(0, new Notification(
                Guid.NewGuid(),
                email.TenantId,
                responsible,
                NotificationChannel.Panel,
                title,
                message,
                NotificationStatus.Sent,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null));
        });
    }

    private static List<DocumentAttachment> BuildAttachments(Guid tenantId, Guid legalEmailId, IEnumerable<EmailAttachmentInput>? inputs)
    {
        var attachments = new List<DocumentAttachment>();
        foreach (var input in inputs ?? [])
        {
            var fileName = InputGuard.Optional(input.FileName, 180);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "adjunto";
            }

            var contentType = InputGuard.Optional(input.ContentType, 120);
            if (string.IsNullOrWhiteSpace(contentType))
            {
                contentType = "application/octet-stream";
            }

            var bytes = DecodeAttachmentBytes(input);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            var ocrText = DocumentTextExtractor.ExtractText(fileName, contentType, bytes, input.TextContent);
            attachments.Add(new DocumentAttachment(
                Guid.NewGuid(),
                tenantId,
                legalEmailId,
                fileName,
                contentType,
                bytes.LongLength,
                $"emails/{legalEmailId:N}/attachments/{sha[..16]}-{fileName}",
                sha,
                ocrText,
                DateTimeOffset.UtcNow));
        }

        return attachments
            .GroupBy(a => a.Sha256)
            .Select(g => g.First())
            .ToList();
    }

    private static byte[] DecodeAttachmentBytes(EmailAttachmentInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.ContentBase64))
        {
            try
            {
                return Convert.FromBase64String(input.ContentBase64);
            }
            catch (FormatException)
            {
                throw new ArgumentException($"Adjunto {input.FileName} no tiene base64 valido.");
            }
        }

        return Encoding.UTF8.GetBytes(input.TextContent ?? string.Empty);
    }

    private static string AppendAttachmentText(string bodyText, IReadOnlyList<DocumentAttachment> attachments)
    {
        var readable = attachments
            .Where(a => !string.IsNullOrWhiteSpace(a.OcrText))
            .Select(a => $"[Adjunto: {a.FileName}]\n{a.OcrText}")
            .ToArray();

        if (readable.Length == 0)
        {
            return bodyText;
        }

        var combined = $"{bodyText}\n\n--- Texto extraido de adjuntos ---\n{string.Join("\n\n", readable)}";
        return HtmlSanitizer.ClipForAnalysis(combined);
    }

    private static DateTimeOffset ToQuitoDateTimeOffset(DateOnly date, TimeOnly time)
    {
        return new DateTimeOffset(date.ToDateTime(time), TimeSpan.FromHours(-5));
    }
}

public static class DocumentTextExtractor
{
    public static string? ExtractText(string fileName, string contentType, byte[] bytes, string? providedText)
    {
        if (!string.IsNullOrWhiteSpace(providedText))
        {
            return HtmlSanitizer.ClipForAnalysis(HtmlSanitizer.ToLegalInnerText(providedText));
        }

        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return TrimForIndex(Encoding.UTF8.GetString(bytes));
        }

        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = ExtractPrintablePdfText(bytes);
            return string.IsNullOrWhiteSpace(candidate)
                ? "[OCR pendiente: PDF recibido y persistido con hash. Configure un proveedor OCR para texto completo.]"
                : TrimForIndex(candidate);
        }

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return "[OCR pendiente: imagen recibida y persistida con hash. Configure un proveedor OCR para texto completo.]";
        }

        return null;
    }

    private static string ExtractPrintablePdfText(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            if (c is >= ' ' and <= '~' or '\n' or '\r' or '\t')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }

    private static string TrimForIndex(string value)
    {
        value = value.Trim();
        return HtmlSanitizer.ClipForAnalysis(value);
    }
}

public sealed class NotificationService(LegalPilotStore store)
{
    public IReadOnlyList<Notification> List(AuthPrincipal principal)
    {
        return store.Read(() => store.Notifications
            .Where(n => n.TenantId == principal.TenantId && (principal.IsSuperAdmin || n.UserId == principal.UserId))
            .OrderByDescending(n => n.CreatedAt)
            .Take(100)
            .ToArray());
    }

    public Notification Acknowledge(AuthPrincipal principal, Guid id)
    {
        return store.Write(() =>
        {
            var index = store.Notifications.FindIndex(n => n.Id == id && n.TenantId == principal.TenantId);
            if (index < 0)
            {
                throw new KeyNotFoundException("Alerta no encontrada.");
            }

            var updated = store.Notifications[index] with
            {
                Status = NotificationStatus.Acknowledged,
                AcknowledgedAt = DateTimeOffset.UtcNow
            };
            store.Notifications[index] = updated;
            store.Audit(principal.TenantId, principal.UserId, AuditAction.Update, nameof(Notification), id.ToString(), "Alerta confirmada.");
            return updated;
        });
    }
}

public sealed class ReminderDispatcher(
    LegalPilotStore store,
    OpenWaClient openWa,
    ILogger<ReminderDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await DispatchDueReminders(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task DispatchDueReminders(CancellationToken cancellationToken)
    {
        try
        {
            var due = store.Read(() => store.Reminders
                .Where(r => r.Status == NotificationStatus.Pending && r.SendAt <= DateTimeOffset.UtcNow)
                .Take(25)
                .ToArray());

            foreach (var reminder in due)
            {
                var eventItem = store.Read(() => store.CalendarEvents.FirstOrDefault(e => e.Id == reminder.CalendarEventId));
                if (eventItem is null)
                {
                    MarkReminder(reminder, NotificationStatus.Failed, null, "Evento asociado no encontrado.");
                    continue;
                }

                if (reminder.Channel == NotificationChannel.WhatsApp)
                {
                    await DispatchWhatsAppReminder(reminder, eventItem, cancellationToken);
                }
                else
                {
                    store.Write(() =>
                    {
                        store.Notifications.Insert(0, new Notification(
                            Guid.NewGuid(),
                            reminder.TenantId,
                            eventItem.ResponsibleUserId,
                            reminder.Channel,
                            eventItem.Type.ToString(),
                            reminder.Message,
                            NotificationStatus.Sent,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            null));

                        var index = store.Reminders.FindIndex(r => r.Id == reminder.Id);
                        store.Reminders[index] = reminder with { Status = NotificationStatus.Sent };
                        store.Audit(reminder.TenantId, eventItem.ResponsibleUserId, AuditAction.SendNotification, nameof(Reminder), reminder.Id.ToString(), reminder.Message);
                    });
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error dispatching reminders.");
        }
    }

    private async Task DispatchWhatsAppReminder(Reminder reminder, CalendarEvent eventItem, CancellationToken cancellationToken)
    {
        var context = store.Read(() =>
        {
            var legalCase = eventItem.CaseId.HasValue
                ? store.Cases.FirstOrDefault(c => c.Id == eventItem.CaseId.Value && c.TenantId == eventItem.TenantId)
                : null;
            var client = legalCase?.ClientId is { } clientId
                ? store.Clients.FirstOrDefault(c => c.Id == clientId && c.TenantId == eventItem.TenantId)
                : null;
            return (legalCase, client);
        });

        if (context.client is null || string.IsNullOrWhiteSpace(context.client.Phone))
        {
            MarkReminder(reminder, NotificationStatus.Failed, eventItem.ResponsibleUserId, "Cliente sin telefono para WhatsApp.");
            return;
        }

        var clientMessage = BuildClientReminderMessage(context.client, context.legalCase, eventItem, reminder);
        var result = await openWa.SendMessageAsync(context.client.Phone, clientMessage, cancellationToken);
        store.Write(() =>
        {
            store.WhatsAppMessages.Insert(0, new WhatsAppMessage(
                Guid.NewGuid(),
                reminder.TenantId,
                context.client.Id,
                context.legalCase?.Id,
                context.client.Phone,
                clientMessage,
                true,
                result.Success ? "Sent" : result.ProviderStatus,
                DateTimeOffset.UtcNow,
                result.Success ? DateTimeOffset.UtcNow : null));

            store.ChatMessages.Insert(0, new ChatMessage(
                Guid.NewGuid(),
                reminder.TenantId,
                context.client.Id,
                context.legalCase?.Id,
                ChatDirection.Outbound,
                NotificationChannel.WhatsApp,
                eventItem.ResponsibleUserId,
                "LegalPilot",
                clientMessage,
                false,
                result.Success ? "Sent" : result.ProviderStatus,
                DateTimeOffset.UtcNow));

            var index = store.Reminders.FindIndex(r => r.Id == reminder.Id);
            store.Reminders[index] = reminder with { Status = result.Success ? NotificationStatus.Sent : NotificationStatus.Failed };
            store.Audit(reminder.TenantId, eventItem.ResponsibleUserId, AuditAction.SendWhatsApp, nameof(Reminder), reminder.Id.ToString(), result.Success ? "Recordatorio WhatsApp enviado." : $"Recordatorio WhatsApp fallo: {result.ProviderStatus}.");
        });
    }

    private void MarkReminder(Reminder reminder, NotificationStatus status, Guid? actorUserId, string reason)
    {
        store.Write(() =>
        {
            var index = store.Reminders.FindIndex(r => r.Id == reminder.Id);
            if (index >= 0)
            {
                store.Reminders[index] = reminder with { Status = status };
            }

            store.Audit(reminder.TenantId, actorUserId, AuditAction.SendNotification, nameof(Reminder), reminder.Id.ToString(), reason);
        });
    }

    private static string BuildClientReminderMessage(ClientProfile client, LegalCase? legalCase, CalendarEvent eventItem, Reminder reminder)
    {
        var caseText = legalCase is null ? "su caso" : $"el caso {legalCase.CaseNumber}";
        return $"Estimado/a {client.FullName}, le recordamos {caseText}: {eventItem.Type} programado para {eventItem.StartsAt:yyyy-MM-dd HH:mm}. {reminder.Message}. Si necesita asesoria juridica, un abogado del estudio le contactara.";
    }
}

public sealed class MailboxSyncWorker(
    LegalPilotStore store,
    EmailConnectorRegistry connectors,
    ILogger<MailboxSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckRegisteredMailboxes(stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task CheckRegisteredMailboxes(CancellationToken cancellationToken)
    {
        var mailboxes = store.Read(() => store.Mailboxes
            .Where(m => m.Status != "Disconnected" && m.Status != "Revoked")
            .Where(m =>
            {
                var latest = store.MailboxSyncStates
                    .Where(s => s.MailboxConnectionId == m.Id)
                    .OrderByDescending(s => s.CheckedAt)
                    .FirstOrDefault();
                return (latest?.NextAttemptAt is null || latest.NextAttemptAt <= DateTimeOffset.UtcNow) &&
                       (m.LastSyncAt is null || m.LastSyncAt < DateTimeOffset.UtcNow.AddMinutes(-15) || m.WatchExpiresAt <= DateTimeOffset.UtcNow.AddHours(12));
            })
            .Take(10)
            .ToArray());

        foreach (var mailbox in mailboxes)
        {
            try
            {
                var result = await connectors.Get(mailbox.Provider).SyncAsync(mailbox, cancellationToken);
                store.Write(() =>
                {
                    var previousFailures = store.MailboxSyncStates
                        .Where(s => s.MailboxConnectionId == mailbox.Id)
                        .OrderByDescending(s => s.CheckedAt)
                        .FirstOrDefault()?.FailureCount ?? 0;

                    store.MailboxSyncStates.Insert(0, new MailboxSyncState(
                        Guid.NewGuid(),
                        mailbox.TenantId,
                        mailbox.Id,
                        mailbox.Provider,
                        result.Status,
                        result.Message,
                        DateTimeOffset.UtcNow,
                        result.NextAttemptAt,
                        result.Success ? 0 : previousFailures + 1));

                    var index = store.Mailboxes.FindIndex(m => m.Id == mailbox.Id);
                    if (index >= 0)
                    {
                        var current = store.Mailboxes[index];
                        store.Mailboxes[index] = current with
                        {
                            Status = result.Status,
                            LastSyncAt = DateTimeOffset.UtcNow,
                            Cursor = result.Cursor ?? current.Cursor,
                            WatchExpiresAt = result.WatchExpiresAt ?? current.WatchExpiresAt,
                            WebhookSubscriptionId = result.SubscriptionId ?? current.WebhookSubscriptionId,
                            WebhookRenewedAt = result.SubscriptionId is null ? current.WebhookRenewedAt : DateTimeOffset.UtcNow,
                            LastError = result.Success ? null : result.Message
                        };
                    }
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mailbox sync check failed for {MailboxId}.", mailbox.Id);
            }
        }
    }
}

public sealed class DeadlineMonitorWorker(
    LegalPilotStore store,
    ILogger<DeadlineMonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            MarkOverdueDeadlines();
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    private void MarkOverdueDeadlines()
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5)).Date);
            store.Write(() =>
            {
                var due = store.Deadlines
                    .Where(d => d.DueDate < today && d.Status is DeadlineStatus.Confirmed or DeadlineStatus.PendingReview)
                    .Take(50)
                    .ToArray();

                foreach (var deadline in due)
                {
                    var index = store.Deadlines.FindIndex(d => d.Id == deadline.Id);
                    if (index < 0)
                    {
                        continue;
                    }

                    var updated = deadline with
                    {
                        Status = DeadlineStatus.Overdue,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    store.Deadlines[index] = updated;
                    store.Notifications.Insert(0, new Notification(
                        Guid.NewGuid(),
                        deadline.TenantId,
                        deadline.ResponsibleUserId,
                        NotificationChannel.Panel,
                        "Plazo vencido",
                        $"{deadline.Title} vencio el {deadline.DueDate:yyyy-MM-dd}.",
                        NotificationStatus.Sent,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        null));
                    store.Audit(deadline.TenantId, deadline.ResponsibleUserId, AuditAction.DeadlineOverdue, nameof(Deadline), deadline.Id.ToString(), $"Plazo marcado como vencido: {deadline.DueDate:yyyy-MM-dd}.");
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error monitoring overdue deadlines.");
        }
    }
}

public sealed class CalendarService(LegalPilotStore store)
{
    public IReadOnlyList<CalendarEvent> List(AuthPrincipal principal)
    {
        return store.Read(() => store.CalendarEvents
            .Where(e => e.TenantId == principal.TenantId)
            .OrderBy(e => e.StartsAt)
            .ToArray());
    }

    public CalendarEvent Create(AuthPrincipal principal, CreateCalendarEventRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        var title = InputGuard.Required(request.Title, "Titulo", 180);
        var location = InputGuard.Optional(request.Location, 240);
        InputGuard.DateRange(request.StartsAt, request.EndsAt);
        store.Read(() =>
        {
            if (request.CaseId.HasValue && store.Cases.All(c => c.Id != request.CaseId.Value || c.TenantId != principal.TenantId))
            {
                throw new ArgumentException("Caso no pertenece al tenant actual.");
            }

            if (request.ResponsibleUserId.HasValue && store.Users.All(u => u.Id != request.ResponsibleUserId.Value || u.TenantId != principal.TenantId || !u.IsActive))
            {
                throw new ArgumentException("Responsable no pertenece al tenant actual.");
            }

            if (store.CalendarEvents.Any(e =>
                e.TenantId == principal.TenantId &&
                e.CaseId == request.CaseId &&
                e.StartsAt == request.StartsAt &&
                e.Title.Equals(title, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConflictException("Ya existe un evento equivalente en calendario.");
            }

            return true;
        });

        var item = new CalendarEvent(
            Guid.NewGuid(),
            principal.TenantId,
            request.CaseId,
            null,
            request.Type,
            title,
            string.IsNullOrWhiteSpace(location) ? null : location,
            request.StartsAt,
            request.EndsAt,
            request.ResponsibleUserId ?? principal.UserId,
            request.RequiresConfirmation,
            false,
            null,
            null,
            DateTimeOffset.UtcNow,
            null,
            "Scheduled",
            request.RequiresConfirmation ? "NeedsConfirmation" : "Pending",
            null,
            null,
            null);
        store.Write(() => store.CalendarEvents.Insert(0, item));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.CreateCalendarEvent, nameof(CalendarEvent), item.Id.ToString(), $"Evento manual creado: {item.Title}");
        return item;
    }

    public CalendarEvent Confirm(AuthPrincipal principal, Guid id)
    {
        return store.Write(() =>
        {
            var index = store.CalendarEvents.FindIndex(e => e.Id == id && e.TenantId == principal.TenantId);
            if (index < 0)
            {
                throw new KeyNotFoundException("Evento no encontrado.");
            }

            var updated = store.CalendarEvents[index] with
            {
                Confirmed = true,
                SyncStatus = string.IsNullOrWhiteSpace(store.CalendarEvents[index].ExternalEventId) ? "Pending" : "PendingUpdate",
                UpdatedAt = DateTimeOffset.UtcNow,
                SyncError = null
            };
            store.CalendarEvents[index] = updated;
            store.Audit(principal.TenantId, principal.UserId, AuditAction.Approve, nameof(CalendarEvent), id.ToString(), "Evento confirmado.");
            return updated;
        });
    }

    public CalendarEvent Update(AuthPrincipal principal, Guid id, UpdateCalendarEventRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        return store.Write(() =>
        {
            var index = store.CalendarEvents.FindIndex(e => e.Id == id && e.TenantId == principal.TenantId);
            if (index < 0)
            {
                throw new KeyNotFoundException("Evento no encontrado.");
            }

            var current = store.CalendarEvents[index];
            var title = string.IsNullOrWhiteSpace(request.Title)
                ? current.Title
                : InputGuard.Required(request.Title, "Titulo", 180);
            var startsAt = request.StartsAt ?? current.StartsAt;
            var endsAt = request.EndsAt ?? current.EndsAt;
            InputGuard.DateRange(startsAt, endsAt);

            var updated = current with
            {
                Type = request.Type ?? current.Type,
                Title = title,
                Location = request.Location is null ? current.Location : InputGuard.Optional(request.Location, 240),
                StartsAt = startsAt,
                EndsAt = endsAt,
                RequiresConfirmation = request.RequiresConfirmation ?? current.RequiresConfirmation,
                Confirmed = request.Confirmed ?? current.Confirmed,
                Description = request.Description is null ? current.Description : (InputGuard.Optional(request.Description, 2000) is { Length: > 0 } description ? description : null),
                UpdatedAt = DateTimeOffset.UtcNow,
                SyncStatus = current.Confirmed || request.Confirmed == true
                    ? (string.IsNullOrWhiteSpace(current.ExternalEventId) ? "Pending" : "PendingUpdate")
                    : "NeedsConfirmation",
                SyncError = null
            };
            store.CalendarEvents[index] = updated;
            store.Audit(principal.TenantId, principal.UserId, AuditAction.Update, nameof(CalendarEvent), id.ToString(), $"Evento actualizado: {updated.Title}");
            return updated;
        });
    }

    public CalendarEvent Cancel(AuthPrincipal principal, Guid id)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
        return store.Write(() =>
        {
            var index = store.CalendarEvents.FindIndex(e => e.Id == id && e.TenantId == principal.TenantId);
            if (index < 0)
            {
                throw new KeyNotFoundException("Evento no encontrado.");
            }

            var current = store.CalendarEvents[index];
            var updated = current with
            {
                Status = "Cancelled",
                SyncStatus = string.IsNullOrWhiteSpace(current.ExternalEventId) ? "Cancelled" : "PendingDelete",
                UpdatedAt = DateTimeOffset.UtcNow,
                SyncError = null
            };
            store.CalendarEvents[index] = updated;

            for (var i = 0; i < store.Reminders.Count; i++)
            {
                if (store.Reminders[i].CalendarEventId == id && store.Reminders[i].Status == NotificationStatus.Pending)
                {
                    store.Reminders[i] = store.Reminders[i] with { Status = NotificationStatus.Acknowledged };
                }
            }

            store.Audit(principal.TenantId, principal.UserId, AuditAction.Delete, nameof(CalendarEvent), id.ToString(), $"Evento cancelado: {updated.Title}");
            return updated;
        });
    }
}

public sealed class WhatsAppService(LegalPilotStore store, OpenWaClient openWa)
{
    public IReadOnlyList<WhatsAppTemplate> Templates(AuthPrincipal principal)
    {
        return store.Read(() => store.WhatsAppTemplates.Where(t => t.TenantId == principal.TenantId && t.IsActive).OrderBy(t => t.Name).ToArray());
    }

    public async Task<WhatsAppMessage> SendClientMessage(AuthPrincipal principal, SendWhatsAppRequest request, CancellationToken cancellationToken)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        var body = InputGuard.SafeMessage(request.Body, 900);
        var client = request.ClientId.HasValue
            ? store.Read(() => store.Clients.FirstOrDefault(c => c.Id == request.ClientId.Value && c.TenantId == principal.TenantId))
            : null;

        if (request.ClientId.HasValue && client is null)
        {
            throw new ArgumentException("Cliente no pertenece al tenant actual.");
        }

        if (request.CaseId.HasValue && store.Read(() => store.Cases.All(c => c.Id != request.CaseId.Value || c.TenantId != principal.TenantId)))
        {
            throw new ArgumentException("Caso no pertenece al tenant actual.");
        }

        var to = string.IsNullOrWhiteSpace(request.To) ? client?.Phone : InputGuard.Phone(request.To, "Destinatario");
        if (string.IsNullOrWhiteSpace(to))
        {
            throw new InvalidOperationException("Debe indicar destinatario o cliente con telefono.");
        }

        var approved = principal.HasAnyRole(UserRole.SuperAdmin, UserRole.Lawyer) && request.Approved;
        var message = new WhatsAppMessage(
            Guid.NewGuid(),
            principal.TenantId,
            request.ClientId,
            request.CaseId,
            to,
            body,
            approved,
            approved ? "Queued" : "PendingApproval",
            DateTimeOffset.UtcNow,
            null);

        store.Write(() => store.WhatsAppMessages.Insert(0, message));

        if (approved)
        {
            var result = await openWa.SendMessageAsync(to, body, cancellationToken);
            message = message with { Status = result.Success ? "Sent" : "Failed", SentAt = result.Success ? DateTimeOffset.UtcNow : null };
            store.Write(() =>
            {
                var index = store.WhatsAppMessages.FindIndex(m => m.Id == message.Id);
                store.WhatsAppMessages[index] = message;
            });
        }

        store.Write(() => store.ChatMessages.Insert(0, new ChatMessage(
            Guid.NewGuid(),
            principal.TenantId,
            request.ClientId,
            request.CaseId,
            ChatDirection.Outbound,
            NotificationChannel.WhatsApp,
            principal.UserId,
            principal.Email,
            body,
            !approved,
            message.Status,
            DateTimeOffset.UtcNow)));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.SendWhatsApp, nameof(WhatsAppMessage), message.Id.ToString(), $"WhatsApp {message.Status} a {to}");
        return message;
    }

    public async Task<object> ReceiveWebhookAsync(OpenWaWebhookRequest request, CancellationToken cancellationToken)
    {
        var tenantId = store.Read(() => store.Tenants.First().Id);
        var from = request.From ?? request.ChatId ?? "OpenWA";
        var body = request.Body ?? request.Text ?? string.Empty;
        var client = ResolveClientByPhone(tenantId, from);
        var legalCase = ResolveCaseForClient(tenantId, client, body);
        var assistant = BuildControlledClientReply(tenantId, client, legalCase, body);
        store.Write(() =>
        {
            store.ChatMessages.Insert(0, new ChatMessage(
                Guid.NewGuid(),
                tenantId,
                client?.Id,
                legalCase?.Id,
                ChatDirection.Inbound,
                NotificationChannel.WhatsApp,
                null,
                from,
                body,
                assistant.RequiresHumanReview,
                assistant.RequiresHumanReview ? "NeedsHumanReview" : "Received",
                DateTimeOffset.UtcNow));

            var lawyer = store.Users.FirstOrDefault(u => u.TenantId == tenantId && u.Roles.Contains(UserRole.Lawyer));
            if (lawyer is not null && assistant.RequiresHumanReview)
            {
                store.Notifications.Insert(0, new Notification(
                    Guid.NewGuid(),
                    tenantId,
                    lawyer.Id,
                    NotificationChannel.Panel,
                    "Mensaje WhatsApp recibido",
                    body.Length > 160 ? body[..160] : body,
                    NotificationStatus.Sent,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    null));
            }
        });

        string? outboundStatus = null;
        if (!string.IsNullOrWhiteSpace(assistant.Reply) && client is not null)
        {
            var result = await openWa.SendMessageAsync(from, assistant.Reply, cancellationToken);
            outboundStatus = result.Success ? "Sent" : result.ProviderStatus;
            store.Write(() =>
            {
                store.WhatsAppMessages.Insert(0, new WhatsAppMessage(
                    Guid.NewGuid(),
                    tenantId,
                    client.Id,
                    legalCase?.Id,
                    from,
                    assistant.Reply,
                    true,
                    outboundStatus,
                    DateTimeOffset.UtcNow,
                    result.Success ? DateTimeOffset.UtcNow : null));

                store.ChatMessages.Insert(0, new ChatMessage(
                    Guid.NewGuid(),
                    tenantId,
                    client.Id,
                    legalCase?.Id,
                    ChatDirection.Outbound,
                    NotificationChannel.WhatsApp,
                    null,
                    "LegalPilot",
                    assistant.Reply,
                    false,
                    outboundStatus,
                    DateTimeOffset.UtcNow));
            });
        }

        store.Audit(tenantId, null, AuditAction.WebhookReceived, "OpenWA", request.SessionId ?? "default", $"Mensaje OpenWA recibido de {from}. Accion: {assistant.Action}.");
        return new { received = true, action = assistant.Action, clientId = client?.Id, caseId = legalCase?.Id, outboundStatus };
    }

    private ClientProfile? ResolveClientByPhone(Guid tenantId, string from)
    {
        var incoming = PhoneKey(from);
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return null;
        }

        return store.Read(() => store.Clients.FirstOrDefault(c =>
        {
            var phone = PhoneKey(c.Phone);
            return phone.Length > 0 && (incoming.EndsWith(phone, StringComparison.Ordinal) || phone.EndsWith(incoming, StringComparison.Ordinal));
        }));
    }

    private LegalCase? ResolveCaseForClient(Guid tenantId, ClientProfile? client, string body)
    {
        if (client is null)
        {
            return null;
        }

        var caseNumber = store.Read(() => store.Cases
            .Where(c => c.TenantId == tenantId && c.ClientId == client.Id)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefault(c => body.Contains(c.CaseNumber, StringComparison.OrdinalIgnoreCase))?.CaseNumber);

        return store.Read(() => store.Cases
            .Where(c => c.TenantId == tenantId && c.ClientId == client.Id)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefault(c => caseNumber is null || c.CaseNumber == caseNumber));
    }

    private ClientAssistantDecision BuildControlledClientReply(Guid tenantId, ClientProfile? client, LegalCase? legalCase, string body)
    {
        if (client is null)
        {
            return new ClientAssistantDecision("unknown-client", null, true);
        }

        var normalized = body.ToLowerInvariant();
        if (ContainsAny(normalized, "que debo hacer", "que hago", "estrategia", "defensa", "demanda", "sentencia", "apelar", "recurso", "acuerdo"))
        {
            return new ClientAssistantDecision("handoff-sensitive", "Recibimos su mensaje. Por tratarse de una consulta que requiere criterio legal, un abogado del estudio le respondera directamente.", true);
        }

        if (ContainsAny(normalized, "estado", "caso", "proceso", "audiencia", "plazo", "vencimiento"))
        {
            var summary = store.Read(() =>
            {
                var cases = store.Cases.Where(c => c.TenantId == tenantId && c.ClientId == client.Id).OrderByDescending(c => c.UpdatedAt).ToArray();
                var selectedCase = legalCase ?? cases.FirstOrDefault();
                var nextEvent = selectedCase is null
                    ? null
                    : store.CalendarEvents.Where(e => e.TenantId == tenantId && e.CaseId == selectedCase.Id && e.StartsAt >= DateTimeOffset.UtcNow).OrderBy(e => e.StartsAt).FirstOrDefault();
                var nextDeadline = selectedCase is null
                    ? null
                    : store.Deadlines.Where(d => d.TenantId == tenantId && d.CaseId == selectedCase.Id && d.Status is DeadlineStatus.Confirmed or DeadlineStatus.PendingReview).OrderBy(d => d.DueDate).FirstOrDefault();
                return (selectedCase, nextEvent, nextDeadline);
            });

            if (summary.selectedCase is null)
            {
                return new ClientAssistantDecision("client-no-case", $"Estimado/a {client.FullName}, no encontramos un caso activo vinculado a su numero. El equipo revisara su mensaje.", true);
            }

            var parts = new List<string>
            {
                $"Estimado/a {client.FullName}, el caso {summary.selectedCase.CaseNumber} figura como {summary.selectedCase.Status}."
            };
            if (summary.nextEvent is not null)
            {
                parts.Add($"Proximo evento: {summary.nextEvent.Type} el {summary.nextEvent.StartsAt:yyyy-MM-dd HH:mm}.");
            }

            if (summary.nextDeadline is not null)
            {
                parts.Add($"Proximo plazo registrado: {summary.nextDeadline.DueDate:yyyy-MM-dd} ({summary.nextDeadline.Status}).");
            }

            parts.Add("Esta es informacion operativa; cualquier decision juridica sera confirmada por el abogado.");
            return new ClientAssistantDecision("auto-replied-status", string.Join(" ", parts), false);
        }

        return new ClientAssistantDecision("stored-for-routing", "Recibimos su mensaje. El equipo legal lo revisara y le respondera por este medio.", true);
    }

    private static bool ContainsAny(string value, params string[] items) => items.Any(value.Contains);

    private static string PhoneKey(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value)
        {
            if (char.IsDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private sealed record ClientAssistantDecision(string Action, string? Reply, bool RequiresHumanReview);
}

public sealed class ChatService(LegalPilotStore store)
{
    public IReadOnlyList<ChatMessage> List(AuthPrincipal principal)
    {
        return store.Read(() => store.ChatMessages
            .Where(m => m.TenantId == principal.TenantId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(100)
            .ToArray());
    }

    public ChatMessage Create(AuthPrincipal principal, CreateChatMessageRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        var body = InputGuard.SafeMessage(request.Body, 2000);
        store.Read(() =>
        {
            if (request.ClientId.HasValue && store.Clients.All(c => c.Id != request.ClientId.Value || c.TenantId != principal.TenantId))
            {
                throw new ArgumentException("Cliente no pertenece al tenant actual.");
            }

            if (request.CaseId.HasValue && store.Cases.All(c => c.Id != request.CaseId.Value || c.TenantId != principal.TenantId))
            {
                throw new ArgumentException("Caso no pertenece al tenant actual.");
            }

            return true;
        });

        var message = new ChatMessage(
            Guid.NewGuid(),
            principal.TenantId,
            request.ClientId,
            request.CaseId,
            request.Direction,
            request.Channel,
            principal.UserId,
            principal.Email,
            body,
            request.RequiresHumanReview,
            "Stored",
            DateTimeOffset.UtcNow);

        store.Write(() => store.ChatMessages.Insert(0, message));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.ChatMessage, nameof(ChatMessage), message.Id.ToString(), "Mensaje registrado en chat.");
        return message;
    }
}

public sealed class ReportService(LegalPilotStore store)
{
    public object Overview(AuthPrincipal principal)
    {
        return store.Read(() =>
        {
            var deadlines = store.Deadlines.Where(d => d.TenantId == principal.TenantId).ToArray();
            var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5)).Date);
            return new
            {
                cases = store.Cases.Count(c => c.TenantId == principal.TenantId),
                clients = store.Clients.Count(c => c.TenantId == principal.TenantId),
                mailboxes = store.Mailboxes.Count(m => m.TenantId == principal.TenantId),
                emails = store.Emails.Count(e => e.TenantId == principal.TenantId && e.ProcessingStatus != "IgnoredNonLegal"),
                deadlines = deadlines.Length,
                deadlinesDueSoon = deadlines.Count(d => d.DueDate <= today.AddDays(3) && d.Status is DeadlineStatus.Confirmed or DeadlineStatus.PendingReview),
                pendingReview = deadlines.Count(d => d.Status == DeadlineStatus.PendingReview),
                events = store.CalendarEvents.Count(e => e.TenantId == principal.TenantId),
                alerts = store.Notifications.Count(n => n.TenantId == principal.TenantId && n.Status != NotificationStatus.Acknowledged),
                chats = store.ChatMessages.Count(m => m.TenantId == principal.TenantId),
                syncIssues = store.MailboxSyncStates.Count(s => s.TenantId == principal.TenantId && s.FailureCount > 0),
                audit = store.AuditEntries.Count(a => a.TenantId == principal.TenantId)
            };
        });
    }
}

public sealed record LoginRequest(string Email, string Password);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string Token, string NewPassword);
public sealed record RefreshTokenRequest(string RefreshToken);
public sealed record LogoutRequest(string? RefreshToken);
public sealed record CreateCaseRequest(string Title, string CaseNumber, string Matter, string CourtOrOffice, Guid? ClientId, Guid? ResponsibleUserId);
public sealed record CreateClientRequest(string FullName, string Email, string Phone, string Identification);
public sealed record ConnectMailboxRequest(MailProvider Provider, string Email, string? ExternalAccountId);
public sealed record UpdateMailboxCalendarRequest(string? CalendarId);
public sealed record ManualEmailRequest(MailProvider? Provider, Guid? MailboxConnectionId, Guid? CaseId, string? ExternalMessageId, string Subject, string Sender, string[]? Recipients, string BodyText, string? RawReference, DateTimeOffset? ReceivedAt, EmailAttachmentInput[]? Attachments = null);
public sealed record CreateDeadlineRequest(string Title, Guid? CaseId, DateOnly NotificationDate, int TermDays, string Matter, string? Province, string? Canton, string? RuleCode, Guid? ResponsibleUserId, bool Confirmed);
public sealed record WebhookEmailEnvelope(string? ExternalMessageId, string Subject, string Sender, string[]? Recipients, string BodyText, string? RawReference, DateTimeOffset? ReceivedAt, EmailAttachmentInput[]? Attachments = null, Guid? MailboxConnectionId = null);
public sealed record EmailAttachmentInput(string FileName, string? ContentType, string? ContentBase64, string? TextContent, long? SizeBytes = null);
public sealed record CreateCalendarEventRequest(Guid? CaseId, CalendarEventType Type, string Title, string? Location, DateTimeOffset StartsAt, DateTimeOffset EndsAt, Guid? ResponsibleUserId, bool RequiresConfirmation);
public sealed record UpdateCalendarEventRequest(CalendarEventType? Type, string? Title, string? Location, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool? RequiresConfirmation, bool? Confirmed, string? Description);
public sealed record SendWhatsAppRequest(Guid? ClientId, Guid? CaseId, string? To, string Body, bool Approved);
public sealed record CreateChatMessageRequest(Guid? ClientId, Guid? CaseId, ChatDirection Direction, NotificationChannel Channel, string Body, bool RequiresHumanReview);
public sealed record OpenWaWebhookRequest(string? SessionId, string? From, string? Body, string? Event, string? ChatId = null, string? Text = null);
public sealed record GmailWebhookRequest(PubSubMessage Message);
public sealed record PubSubMessage(string? Data, string? MessageId, Dictionary<string, string>? Attributes);

public static class WebhookParsers
{
    public static WebhookEmailEnvelope FromGmailPubSub(GmailWebhookRequest request)
    {
        var decoded = DecodeBase64Json(request.Message?.Data);
        var email = decoded.RootElement.TryGetProperty("emailAddress", out var emailAddress)
            ? emailAddress.GetString() ?? "gmail-notification"
            : "gmail-notification";
        var history = decoded.RootElement.TryGetProperty("historyId", out var historyId)
            ? historyId.GetString()
            : request.Message?.MessageId;

        return new WebhookEmailEnvelope(
            history,
            "Gmail Pub/Sub notification",
            email,
            [],
            $"Gmail watch notification received. historyId={history}. A production connector must call history.list and messages.get.",
            "gmail-pubsub",
            DateTimeOffset.UtcNow);
    }

    public static WebhookEmailEnvelope FromMicrosoftNotification(JsonElement body)
    {
        return new WebhookEmailEnvelope(
            body.ToString().GetHashCode().ToString(),
            "Microsoft Graph change notification",
            "microsoft-graph",
            [],
            "Graph webhook received. A production connector must validate clientState and fetch the message resource.",
            "microsoft-graph",
            DateTimeOffset.UtcNow);
    }

    private static JsonDocument DecodeBase64Json(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return JsonDocument.Parse("{}");
        }

        var padded = data.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        return JsonDocument.Parse(json);
    }
}
