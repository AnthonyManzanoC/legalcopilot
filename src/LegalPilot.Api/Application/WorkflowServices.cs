using System.Text;
using System.Text.Json;
using LegalPilot.Api.Domain;
using LegalPilot.Api.Infrastructure;

namespace LegalPilot.Api.Application;

public sealed class AuthService(LegalPilotStore store, PasswordHasher hasher, TokenService tokens, IWebHostEnvironment environment)
{
    public object Login(string email, string password, string? ipAddress = null)
    {
        email = InputGuard.Email(email);
        password = InputGuard.Required(password, "Contrasena", 256);
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
        return store.Write(() => CreateRefreshSessionUnsafe(user, ipAddress));
    }

    private (string RawToken, RefreshTokenSession Session) CreateRefreshSessionUnsafe(UserAccount user, string? ipAddress)
    {
        var raw = TokenService.RandomToken(48);
        var session = new RefreshTokenSession(
            Guid.NewGuid(),
            user.TenantId,
            user.Id,
            TokenService.Sha256(raw),
            DateTimeOffset.UtcNow.AddDays(14),
            DateTimeOffset.UtcNow,
            ipAddress,
            null,
            null);
        store.RefreshTokenSessions.Add(session);
        return (raw, session);
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
        store.Audit(principal.TenantId, principal.UserId, AuditAction.Create, nameof(MailboxConnection), mailbox.Id.ToString(), $"Buzon registrado: {mailbox.Email}. Estado: {mailbox.Status}");
        return mailbox;
    }

    public async Task<MailboxSyncState> Sync(AuthPrincipal principal, Guid mailboxId, CancellationToken cancellationToken)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        var mailbox = store.Read(() => store.Mailboxes.FirstOrDefault(m => m.Id == mailboxId && m.TenantId == principal.TenantId))
            ?? throw new KeyNotFoundException("Buzon no encontrado.");
        var connector = connectors.Get(mailbox.Provider);
        var result = await connector.SyncAsync(mailbox, cancellationToken);

        return store.Write(() =>
        {
            var previousFailures = store.MailboxSyncStates
                .Where(s => s.MailboxConnectionId == mailbox.Id)
                .OrderByDescending(s => s.CheckedAt)
                .FirstOrDefault()?.FailureCount ?? 0;

            var state = new MailboxSyncState(
                Guid.NewGuid(),
                principal.TenantId,
                mailbox.Id,
                mailbox.Provider,
                result.Status,
                result.Message,
                DateTimeOffset.UtcNow,
                result.NextAttemptAt,
                result.Success ? 0 : previousFailures + 1);

            store.MailboxSyncStates.Insert(0, state);
            var index = store.Mailboxes.FindIndex(m => m.Id == mailbox.Id);
            store.Mailboxes[index] = mailbox with
            {
                Status = result.Status,
                LastSyncAt = DateTimeOffset.UtcNow
            };
            store.Audit(principal.TenantId, principal.UserId, AuditAction.SyncAttempt, nameof(MailboxConnection), mailbox.Id.ToString(), result.Message);
            return state;
        });
    }
}

public sealed class LegalWorkflowService(
    LegalPilotStore store,
    LegalIntelligenceService intelligence,
    EcuadorDeadlineEngine deadlineEngine)
{
    public LegalEmail IngestManual(AuthPrincipal principal, ManualEmailRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        var provider = request.Provider;
        var subject = InputGuard.Required(request.Subject, "Asunto", 240);
        var sender = InputGuard.Email(request.Sender, "Remitente");
        var bodyText = InputGuard.TextBlock(request.BodyText, "Cuerpo", 12000);
        EnsureCaseAndMailbox(principal.TenantId, request.CaseId, request.MailboxConnectionId);
        var externalMessageId = InputGuard.Optional(request.ExternalMessageId, 180);
        if (!string.IsNullOrWhiteSpace(externalMessageId))
        {
            var existing = FindExistingEmail(principal.TenantId, provider, externalMessageId);
            if (existing is not null)
            {
                store.Audit(principal.TenantId, principal.UserId, AuditAction.IngestEmail, nameof(LegalEmail), existing.Id.ToString(), "Correo duplicado omitido por idempotencia.");
                return existing;
            }
        }

        var extraction = intelligence.Extract(subject, bodyText);
        var caseId = ResolveCaseId(principal.TenantId, extraction.CaseNumber, request.CaseId);
        var now = DateTimeOffset.UtcNow;

        var email = new LegalEmail(
            Guid.NewGuid(),
            principal.TenantId,
            request.MailboxConnectionId,
            caseId,
            provider,
            string.IsNullOrWhiteSpace(externalMessageId) ? $"manual-{Guid.NewGuid():N}" : externalMessageId,
            subject,
            sender,
            request.Recipients ?? [],
            bodyText,
            InputGuard.Optional(request.RawReference, 200) is { Length: > 0 } rawReference ? rawReference : "manual",
            request.ReceivedAt ?? now,
            "Processed",
            extraction,
            now);

        store.Write(() => store.Emails.Insert(0, email));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.IngestEmail, nameof(LegalEmail), email.Id.ToString(), $"Correo legal ingerido: {email.Subject}");
        store.Audit(principal.TenantId, principal.UserId, AuditAction.ClassifyEmail, nameof(LegalEmail), email.Id.ToString(), extraction.LawyerSummary);

        CreateDerivedWork(email, principal.UserId);
        return email;
    }

    public LegalEmail IngestWebhook(Guid tenantId, MailProvider provider, WebhookEmailEnvelope envelope)
    {
        var externalMessageId = InputGuard.Optional(envelope.ExternalMessageId, 180);
        if (!string.IsNullOrWhiteSpace(externalMessageId))
        {
            var existing = FindExistingEmail(tenantId, provider, externalMessageId);
            if (existing is not null)
            {
                store.Audit(tenantId, null, AuditAction.WebhookReceived, nameof(LegalEmail), existing.Id.ToString(), $"Webhook {provider} duplicado omitido.");
                return existing;
            }
        }

        var subject = InputGuard.Required(envelope.Subject, "Asunto", 240);
        var sender = InputGuard.Required(envelope.Sender, "Remitente", 254);
        var bodyText = InputGuard.TextBlock(envelope.BodyText, "Cuerpo", 12000);
        var extraction = intelligence.Extract(subject, bodyText);
        var caseId = ResolveCaseId(tenantId, extraction.CaseNumber, null);
        var now = DateTimeOffset.UtcNow;
        var email = new LegalEmail(
            Guid.NewGuid(),
            tenantId,
            null,
            caseId,
            provider,
            string.IsNullOrWhiteSpace(externalMessageId) ? $"webhook-{Guid.NewGuid():N}" : externalMessageId,
            subject,
            sender,
            envelope.Recipients ?? [],
            bodyText,
            InputGuard.Optional(envelope.RawReference, 200) is { Length: > 0 } rawReference ? rawReference : provider.ToString(),
            envelope.ReceivedAt ?? now,
            "ProcessedFromWebhook",
            extraction,
            now);

        store.Write(() => store.Emails.Insert(0, email));
        store.Audit(tenantId, null, AuditAction.WebhookReceived, nameof(LegalEmail), email.Id.ToString(), $"Webhook {provider} procesado.");
        CreateDerivedWork(email, null);
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
            true,
            false,
            null,
            null,
            DateTimeOffset.UtcNow);
        store.Write(() => store.CalendarEvents.Insert(0, calendarEvent));
        CreateReminderSet(principal.TenantId, calendarEvent, deadline.Title, holidays);
        return deadline;
    }

    private LegalEmail? FindExistingEmail(Guid tenantId, MailProvider? provider, string externalMessageId)
    {
        return store.Read(() => store.Emails.FirstOrDefault(e =>
            e.TenantId == tenantId &&
            e.Provider == provider &&
            e.ExternalMessageId.Equals(externalMessageId, StringComparison.OrdinalIgnoreCase)));
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

        if (email.Extraction.TermDays.HasValue && !store.Read(() => store.Deadlines.Any(d => d.TenantId == email.TenantId && d.LegalEmailId == email.Id)))
        {
            var notificationDate = DateOnly.FromDateTime(email.ReceivedAt.ToOffset(TimeSpan.FromHours(-5)).Date);
            var calculation = deadlineEngine.Calculate(
                new DeadlineRequest(notificationDate, InputGuard.TermDays(email.Extraction.TermDays.Value), "general"),
                holidays);

            var status = email.Extraction.Confidence >= 0.70m ? DeadlineStatus.Confirmed : DeadlineStatus.PendingReview;
            var deadline = new Deadline(
                Guid.NewGuid(),
                email.TenantId,
                email.CaseId,
                email.Id,
                $"Plazo: {email.Subject}",
                email.Extraction.ActType,
                notificationDate,
                email.Extraction.TermDays.Value,
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
            var calendarEvent = CreateCalendarEvent(email, deadline.Id, CalendarEventType.Deadline, deadline.Title, null, starts, starts.AddMinutes(30), responsible, true);
            CreateReminderSet(email.TenantId, calendarEvent, deadline.Title, holidays);
        }

        if (email.Extraction.EventDate.HasValue && email.Extraction.ActType is LegalActType.Hearing or LegalActType.ExpertReview or LegalActType.ProsecutorNotification)
        {
            var time = email.Extraction.EventTime ?? new TimeOnly(9, 0);
            var starts = ToQuitoDateTimeOffset(email.Extraction.EventDate.Value, time);
            var type = email.Extraction.ActType switch
            {
                LegalActType.Hearing => CalendarEventType.Hearing,
                LegalActType.ExpertReview => CalendarEventType.ExpertReview,
                _ => CalendarEventType.Diligence
            };

            var title = $"{email.Extraction.ActType}: {email.Subject}";
            var exists = store.Read(() => store.CalendarEvents.Any(e =>
                e.TenantId == email.TenantId &&
                e.CaseId == email.CaseId &&
                e.StartsAt == starts &&
                e.Title.Equals(title, StringComparison.OrdinalIgnoreCase)));

            if (!exists)
            {
                var calendarEvent = CreateCalendarEvent(email, null, type, title, email.Extraction.Location, starts, starts.AddHours(1), responsible, true);
                CreateReminderSet(email.TenantId, calendarEvent, title, holidays);
            }
        }
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
            false,
            null,
            null,
            DateTimeOffset.UtcNow);

        store.Write(() => store.CalendarEvents.Insert(0, calendarEvent));
        store.Audit(email.TenantId, responsible, AuditAction.CreateCalendarEvent, nameof(CalendarEvent), calendarEvent.Id.ToString(), $"Evento creado: {title}");
        return calendarEvent;
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

            var duplicate = store.Read(() => store.Reminders.Any(r =>
                r.TenantId == tenantId &&
                r.CalendarEventId == calendarEvent.Id &&
                r.SendAt == sendAt &&
                r.Message == (offset == 0 ? $"Hoy: {title}" : $"Recordatorio T-{offset}: {title}")));

            if (duplicate)
            {
                continue;
            }

            reminders.Add(new Reminder(
                Guid.NewGuid(),
                tenantId,
                calendarEvent.Id,
                NotificationChannel.Panel,
                sendAt,
                offset == 0 ? $"Hoy: {title}" : $"Recordatorio T-{offset}: {title}",
                NotificationStatus.Pending,
                DateTimeOffset.UtcNow));
        }

        store.Write(() => store.Reminders.AddRange(reminders));
    }

    private static DateTimeOffset ToQuitoDateTimeOffset(DateOnly date, TimeOnly time)
    {
        return new DateTimeOffset(date.ToDateTime(time), TimeSpan.FromHours(-5));
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
    ILogger<ReminderDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DispatchDueReminders();
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private void DispatchDueReminders()
    {
        try
        {
            store.Write(() =>
            {
                var due = store.Reminders
                    .Where(r => r.Status == NotificationStatus.Pending && r.SendAt <= DateTimeOffset.UtcNow)
                    .Take(25)
                    .ToArray();

                foreach (var reminder in due)
                {
                    var eventItem = store.CalendarEvents.FirstOrDefault(e => e.Id == reminder.CalendarEventId);
                    if (eventItem is null)
                    {
                        continue;
                    }

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
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error dispatching reminders.");
        }
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
            .Where(m => m.LastSyncAt is null || m.LastSyncAt < DateTimeOffset.UtcNow.AddMinutes(-15))
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
                        store.Mailboxes[index] = mailbox with
                        {
                            Status = result.Status,
                            LastSyncAt = DateTimeOffset.UtcNow
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
            DateTimeOffset.UtcNow);
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

            var updated = store.CalendarEvents[index] with { Confirmed = true };
            store.CalendarEvents[index] = updated;
            store.Audit(principal.TenantId, principal.UserId, AuditAction.Approve, nameof(CalendarEvent), id.ToString(), "Evento confirmado.");
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

    public object ReceiveWebhook(OpenWaWebhookRequest request)
    {
        var tenantId = store.Read(() => store.Tenants.First().Id);
        var body = request.Body ?? string.Empty;
        store.Write(() =>
        {
            store.ChatMessages.Insert(0, new ChatMessage(
                Guid.NewGuid(),
                tenantId,
                null,
                null,
                ChatDirection.Inbound,
                NotificationChannel.WhatsApp,
                null,
                request.From ?? "OpenWA",
                body,
                true,
                "Received",
                DateTimeOffset.UtcNow));

            var lawyer = store.Users.FirstOrDefault(u => u.TenantId == tenantId && u.Roles.Contains(UserRole.Lawyer));
            if (lawyer is not null)
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
        store.Audit(tenantId, null, AuditAction.WebhookReceived, "OpenWA", request.SessionId ?? "default", $"Mensaje OpenWA recibido de {request.From}");
        return new { received = true, action = "stored-for-routing" };
    }

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
                emails = store.Emails.Count(e => e.TenantId == principal.TenantId),
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
public sealed record ManualEmailRequest(MailProvider? Provider, Guid? MailboxConnectionId, Guid? CaseId, string? ExternalMessageId, string Subject, string Sender, string[]? Recipients, string BodyText, string? RawReference, DateTimeOffset? ReceivedAt);
public sealed record CreateDeadlineRequest(string Title, Guid? CaseId, DateOnly NotificationDate, int TermDays, string Matter, string? Province, string? Canton, string? RuleCode, Guid? ResponsibleUserId, bool Confirmed);
public sealed record WebhookEmailEnvelope(string? ExternalMessageId, string Subject, string Sender, string[]? Recipients, string BodyText, string? RawReference, DateTimeOffset? ReceivedAt);
public sealed record CreateCalendarEventRequest(Guid? CaseId, CalendarEventType Type, string Title, string? Location, DateTimeOffset StartsAt, DateTimeOffset EndsAt, Guid? ResponsibleUserId, bool RequiresConfirmation);
public sealed record SendWhatsAppRequest(Guid? ClientId, Guid? CaseId, string? To, string Body, bool Approved);
public sealed record CreateChatMessageRequest(Guid? ClientId, Guid? CaseId, ChatDirection Direction, NotificationChannel Channel, string Body, bool RequiresHumanReview);
public sealed record OpenWaWebhookRequest(string? SessionId, string? From, string? Body, string? Event);
public sealed record GmailWebhookRequest(PubSubMessage Message);
public sealed record PubSubMessage(string? Data, string? MessageId, Dictionary<string, string>? Attributes);

public static class WebhookParsers
{
    public static WebhookEmailEnvelope FromGmailPubSub(GmailWebhookRequest request)
    {
        var decoded = DecodeBase64Json(request.Message.Data);
        var email = decoded.RootElement.TryGetProperty("emailAddress", out var emailAddress)
            ? emailAddress.GetString() ?? "gmail-notification"
            : "gmail-notification";
        var history = decoded.RootElement.TryGetProperty("historyId", out var historyId)
            ? historyId.GetString()
            : request.Message.MessageId;

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
