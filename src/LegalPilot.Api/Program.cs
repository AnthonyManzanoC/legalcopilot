using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using LegalPilot.Api.Application;
using LegalPilot.Api.Domain;
using LegalPilot.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Services.AddSingleton<LegalPilotStore>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<CaseService>();
builder.Services.AddSingleton<ClientService>();
builder.Services.AddSingleton<MailboxService>();
builder.Services.AddSingleton<OAuthService>();
builder.Services.AddSingleton<IEmailConnector, GmailEmailConnector>();
builder.Services.AddSingleton<IEmailConnector, MicrosoftGraphEmailConnector>();
builder.Services.AddSingleton<EmailConnectorRegistry>();
builder.Services.AddSingleton<LegalIntelligenceService>();
builder.Services.AddSingleton<LegalAiPipelineService>();
builder.Services.AddSingleton<EcuadorDeadlineEngine>();
builder.Services.AddSingleton<LegalWorkflowService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<CalendarService>();
builder.Services.AddSingleton<WhatsAppService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddHttpClient<OpenWaClient>();
builder.Services.AddHostedService<ReminderDispatcher>();
builder.Services.AddHostedService<MailboxSyncWorker>();
builder.Services.AddHostedService<DeadlineMonitorWorker>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

SeedData.Seed(
    app.Services.GetRequiredService<LegalPilotStore>(),
    app.Services.GetRequiredService<PasswordHasher>(),
    app.Configuration,
    app.Environment);

app.UseDefaultFiles();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("LegalPilot.Errors");
    try
    {
        await next();
    }
    catch (UnauthorizedAccessException ex)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message, traceId = context.TraceIdentifier });
    }
    catch (ForbiddenOperationException ex)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message, traceId = context.TraceIdentifier });
    }
    catch (KeyNotFoundException ex)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message, traceId = context.TraceIdentifier });
    }
    catch (ConflictException ex)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message, traceId = context.TraceIdentifier });
    }
    catch (ArgumentException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message, traceId = context.TraceIdentifier });
    }
    catch (InvalidOperationException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message, traceId = context.TraceIdentifier });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled request error {Method} {Path}.", context.Request.Method, context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "Error interno controlado. Revise logs del servidor.", traceId = context.TraceIdentifier });
    }
});

app.MapGet("/health", (LegalPilotStore store) => Results.Ok(new
{
    status = "ok",
    app = "LegalPilot Ecuador",
    utc = DateTimeOffset.UtcNow,
    persistence = store.PersistenceProvider,
    dataSource = store.DataFilePath,
    tenants = store.Read(() => store.Tenants.Count)
}));

app.MapGet("/openapi.json", () => Results.Json(OpenApiContract.Build()));

app.MapPost("/api/auth/login", (HttpContext context, LoginRequest request, AuthService auth) =>
{
    return Results.Ok(auth.Login(request.Email, request.Password, context.Connection.RemoteIpAddress?.ToString()));
});

app.MapPost("/api/auth/refresh", (HttpContext context, RefreshTokenRequest request, AuthService auth) =>
{
    return Results.Ok(auth.Refresh(request.RefreshToken, context.Connection.RemoteIpAddress?.ToString()));
});

app.MapPost("/api/auth/logout", (HttpContext context, HttpRequest request, TokenService tokens, AuthService auth, LogoutRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(auth.Logout(principal.UserId, principal.TenantId, payload.RefreshToken, context.Connection.RemoteIpAddress?.ToString()));
});

app.MapPost("/api/auth/forgot-password", (ForgotPasswordRequest request, AuthService auth) =>
{
    return Results.Ok(auth.CreatePasswordReset(request.Email));
});

app.MapPost("/api/auth/reset-password", (ResetPasswordRequest request, AuthService auth) =>
{
    return Results.Ok(auth.ResetPassword(request.Token, request.NewPassword));
});

app.MapGet("/api/me", (HttpRequest request, TokenService tokens, LegalPilotStore store) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    var user = store.Read(() => store.Users.First(u => u.Id == principal.UserId));
    var tenant = store.Read(() => store.Tenants.First(t => t.Id == principal.TenantId));
    store.Audit(principal.TenantId, principal.UserId, AuditAction.View, nameof(UserAccount), user.Id.ToString(), "Perfil consultado.");
    return Results.Ok(new
    {
        user = new
        {
            user.Id,
            user.Email,
            user.DisplayName,
            Roles = user.Roles.Select(r => r.ToString()).ToArray(),
            user.TenantId,
            user.MfaEnabled
        },
        tenant = new { tenant.Id, tenant.Name }
    });
});

app.MapGet("/api/users", (HttpRequest request, TokenService tokens, LegalPilotStore store) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
    return Results.Ok(store.Read(() => store.Users
        .Where(u => u.TenantId == principal.TenantId && u.IsActive)
        .Select(u => new
        {
            u.Id,
            u.Email,
            u.DisplayName,
            Roles = u.Roles.Select(r => r.ToString()).ToArray()
        })
        .ToArray()));
});

app.MapGet("/api/cases", (HttpRequest request, TokenService tokens, CaseService cases, string? search, int? take) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(cases.ListCases(principal, search, take));
});

app.MapGet("/api/cases/{id:guid}", (HttpRequest request, TokenService tokens, LegalPilotStore store, Guid id) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    var legalCase = store.Read(() => store.Cases.FirstOrDefault(c => c.Id == id && c.TenantId == principal.TenantId))
        ?? throw new KeyNotFoundException("Caso no encontrado.");
    return Results.Ok(new
    {
        legalCase,
        client = legalCase.ClientId.HasValue ? store.Read(() => store.Clients.FirstOrDefault(c => c.Id == legalCase.ClientId.Value)) : null,
        deadlines = store.Read(() => store.Deadlines.Where(d => d.CaseId == id && d.TenantId == principal.TenantId).OrderBy(d => d.DueDate).ToArray()),
        events = store.Read(() => store.CalendarEvents.Where(e => e.CaseId == id && e.TenantId == principal.TenantId).OrderBy(e => e.StartsAt).ToArray()),
        emails = store.Read(() => store.Emails.Where(e => e.CaseId == id && e.TenantId == principal.TenantId).OrderByDescending(e => e.ReceivedAt).ToArray())
    });
});

app.MapPost("/api/cases", (HttpRequest request, TokenService tokens, CaseService cases, CreateCaseRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Created("/api/cases", cases.CreateCase(principal, payload));
});

app.MapGet("/api/clients", (HttpRequest request, TokenService tokens, ClientService clients, string? search, int? take) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(clients.List(principal, search, take));
});

app.MapGet("/api/clients/{id:guid}", (HttpRequest request, TokenService tokens, LegalPilotStore store, Guid id) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    var client = store.Read(() => store.Clients.FirstOrDefault(c => c.Id == id && c.TenantId == principal.TenantId))
        ?? throw new KeyNotFoundException("Cliente no encontrado.");
    return Results.Ok(new
    {
        client,
        cases = store.Read(() => store.Cases.Where(c => c.ClientId == id && c.TenantId == principal.TenantId).OrderByDescending(c => c.UpdatedAt).ToArray()),
        messages = store.Read(() => store.ChatMessages.Where(m => m.ClientId == id && m.TenantId == principal.TenantId).OrderByDescending(m => m.CreatedAt).Take(50).ToArray())
    });
});

app.MapPost("/api/clients", (HttpRequest request, TokenService tokens, ClientService clients, CreateClientRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Created("/api/clients", clients.Create(principal, payload));
});

app.MapGet("/api/mailboxes", (HttpRequest request, TokenService tokens, MailboxService mailboxes) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(mailboxes.List(principal));
});

app.MapGet("/api/mailboxes/sync-states", (HttpRequest request, TokenService tokens, MailboxService mailboxes) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(mailboxes.SyncStates(principal));
});

app.MapPost("/api/mailboxes/connect", (HttpRequest request, TokenService tokens, MailboxService mailboxes, ConnectMailboxRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Created("/api/mailboxes", mailboxes.Connect(principal, payload));
});

app.MapPost("/api/mailboxes/{id:guid}/sync", async (HttpRequest request, TokenService tokens, MailboxService mailboxes, Guid id, CancellationToken cancellationToken) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Accepted($"/api/mailboxes/{id}/sync", await mailboxes.Sync(principal, id, cancellationToken));
});

app.MapPost("/api/oauth/start", (HttpRequest request, TokenService tokens, OAuthService oauth, StartOAuthRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(oauth.Start(principal, payload));
});

app.MapGet("/api/oauth/{provider}/callback", async (string provider, string? state, string? code, string? error, OAuthService oauth, CancellationToken cancellationToken) =>
{
    if (!MailProviderParser.TryParse(provider, out var mailProvider))
    {
        throw new ArgumentException("Proveedor OAuth no soportado.");
    }

    var result = await oauth.CompleteAsync(mailProvider, state, code, error, cancellationToken);
    var title = result.Accepted ? "OAuth validado" : "OAuth rechazado";
    var html = $$"""
        <!doctype html>
        <html lang="es">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>LegalPilot OAuth</title>
          <style>
            body { font-family: system-ui, sans-serif; background: #0f1418; color: #f7f3ea; display: grid; min-height: 100vh; place-items: center; margin: 0; }
            main { width: min(560px, calc(100vw - 32px)); border: 1px solid #2f3a42; border-radius: 10px; padding: 28px; background: #172027; }
            h1 { margin: 0 0 12px; font-size: 24px; }
            p { color: #b8c3ca; line-height: 1.5; }
          </style>
        </head>
        <body>
          <main>
            <h1>{{WebUtility.HtmlEncode(title)}}</h1>
            <p><strong>{{WebUtility.HtmlEncode(result.Provider.ToString())}}</strong> / {{WebUtility.HtmlEncode(result.Email)}}</p>
            <p>{{WebUtility.HtmlEncode(result.Message)}}</p>
          </main>
        </body>
        </html>
        """;
    return Results.Content(html, "text/html");
});

app.MapGet("/api/integrations/status", (HttpRequest request, TokenService tokens, EmailConnectorRegistry connectors, OpenWaClient openWa) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
    var openWaReadiness = openWa.GetReadiness();
    return Results.Ok(new
    {
        mail = connectors.Status(),
        openWa = new
        {
            openWaReadiness.Configured,
            openWaReadiness.Status,
            openWaReadiness.Message,
            openWaReadiness.RequiredSettings
        }
    });
});

app.MapPost("/api/inbox/manual", (HttpRequest request, TokenService tokens, LegalWorkflowService workflow, ManualEmailRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Created("/api/inbox/manual", workflow.IngestManual(principal, payload));
});

app.MapPost("/api/webhooks/gmail", (HttpRequest request, GmailWebhookRequest payload, LegalWorkflowService workflow, LegalPilotStore store, IConfiguration configuration) =>
{
    WebhookSecurity.RequireSharedSecret(request, configuration, "LegalPilot:Gmail:WebhookSecret", "Gmail");
    var tenantId = store.Read(() => store.Tenants.First().Id);
    var envelope = WebhookParsers.FromGmailPubSub(payload);
    var email = workflow.IngestWebhook(tenantId, MailProvider.Gmail, envelope);
    return Results.Accepted("/api/webhooks/gmail", new { accepted = true, email.Id });
});

app.MapMethods("/api/webhooks/microsoft", ["GET", "POST"], async (HttpRequest request, LegalWorkflowService workflow, LegalPilotStore store) =>
{
    if (request.Query.TryGetValue("validationToken", out var validationToken))
    {
        return Results.Text(validationToken.ToString(), "text/plain");
    }

    using var document = await JsonDocument.ParseAsync(request.Body);
    WebhookSecurity.RequireMicrosoftClientState(document.RootElement, request.HttpContext.RequestServices.GetRequiredService<IConfiguration>());
    var tenantId = store.Read(() => store.Tenants.First().Id);
    var email = workflow.IngestWebhook(tenantId, MailProvider.Outlook, WebhookParsers.FromMicrosoftNotification(document.RootElement));
    return Results.Accepted("/api/webhooks/microsoft", new { accepted = true, email.Id });
});

app.MapPost("/api/webhooks/openwa", (HttpRequest request, OpenWaWebhookRequest payload, WhatsAppService whatsApp, IConfiguration configuration) =>
{
    var secret = configuration["LegalPilot:OpenWa:WebhookSecret"];
    if (!string.IsNullOrWhiteSpace(secret))
    {
        var supplied = request.Headers["X-OpenWA-Webhook-Secret"].FirstOrDefault()
            ?? request.Headers["X-Webhook-Secret"].FirstOrDefault();
        if (!string.Equals(secret, supplied, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Webhook OpenWA no autorizado.");
        }
    }

    return Results.Accepted("/api/webhooks/openwa", whatsApp.ReceiveWebhook(payload));
});

app.MapGet("/api/inbox", (HttpRequest request, TokenService tokens, LegalPilotStore store) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(store.Read(() => store.Emails.Where(e => e.TenantId == principal.TenantId).Take(100).ToArray()));
});

app.MapGet("/api/deadlines", (HttpRequest request, TokenService tokens, LegalPilotStore store) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(store.Read(() => store.Deadlines.Where(d => d.TenantId == principal.TenantId).OrderBy(d => d.DueDate).ToArray()));
});

app.MapPost("/api/deadlines/calculate", (HttpRequest request, TokenService tokens, LegalWorkflowService workflow, DeadlineRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
    return Results.Ok(workflow.Calculate(principal.TenantId, payload));
});

app.MapPost("/api/deadlines", (HttpRequest request, TokenService tokens, LegalWorkflowService workflow, CreateDeadlineRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Created("/api/deadlines", workflow.CreateDeadline(principal, payload));
});

app.MapPatch("/api/deadlines/{id:guid}/review", (HttpRequest request, TokenService tokens, LegalPilotStore store, Guid id, ReviewDeadlineRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
    var deadline = store.Write(() =>
    {
        var index = store.Deadlines.FindIndex(d => d.Id == id && d.TenantId == principal.TenantId);
        if (index < 0)
        {
            throw new KeyNotFoundException("Plazo no encontrado.");
        }

        var updated = store.Deadlines[index] with
        {
            Status = payload.Approved ? DeadlineStatus.Confirmed : DeadlineStatus.Cancelled,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        store.Deadlines[index] = updated;
        return updated;
    });
    store.Audit(principal.TenantId, principal.UserId, payload.Approved ? AuditAction.Approve : AuditAction.Reject, nameof(Deadline), id.ToString(), payload.Comment ?? "Revision de plazo.");
    return Results.Ok(deadline);
});

app.MapGet("/api/calendar/events", (HttpRequest request, TokenService tokens, CalendarService calendar) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(calendar.List(principal));
});

app.MapPost("/api/calendar/events", (HttpRequest request, TokenService tokens, CalendarService calendar, CreateCalendarEventRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Created("/api/calendar/events", calendar.Create(principal, payload));
});

app.MapPost("/api/calendar/events/{id:guid}/confirm", (HttpRequest request, TokenService tokens, CalendarService calendar, Guid id) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(calendar.Confirm(principal, id));
});

app.MapGet("/api/alerts", (HttpRequest request, TokenService tokens, NotificationService notifications) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(notifications.List(principal));
});

app.MapGet("/api/reminders", (HttpRequest request, TokenService tokens, LegalPilotStore store) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(store.Read(() => store.Reminders
        .Where(r => r.TenantId == principal.TenantId)
        .OrderBy(r => r.SendAt)
        .Take(100)
        .ToArray()));
});

app.MapPost("/api/alerts/{id:guid}/ack", (HttpRequest request, TokenService tokens, NotificationService notifications, Guid id) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(notifications.Acknowledge(principal, id));
});

app.MapGet("/api/whatsapp/templates", (HttpRequest request, TokenService tokens, WhatsAppService whatsApp) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(whatsApp.Templates(principal));
});

app.MapGet("/api/whatsapp/messages", (HttpRequest request, TokenService tokens, LegalPilotStore store) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
    return Results.Ok(store.Read(() => store.WhatsAppMessages
        .Where(m => m.TenantId == principal.TenantId)
        .OrderByDescending(m => m.CreatedAt)
        .Take(100)
        .ToArray()));
});

app.MapPost("/api/whatsapp/send-client-message", async (HttpRequest request, TokenService tokens, WhatsAppService whatsApp, SendWhatsAppRequest payload, CancellationToken cancellationToken) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    var message = await whatsApp.SendClientMessage(principal, payload, cancellationToken);
    return Results.Created("/api/whatsapp/messages", message);
});

app.MapGet("/api/chat/messages", (HttpRequest request, TokenService tokens, ChatService chat) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(chat.List(principal));
});

app.MapPost("/api/chat/messages", (HttpRequest request, TokenService tokens, ChatService chat, CreateChatMessageRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Created("/api/chat/messages", chat.Create(principal, payload));
});

app.MapGet("/api/audit", (HttpRequest request, TokenService tokens, LegalPilotStore store) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
    return Results.Ok(store.Read(() => store.AuditEntries.Where(a => a.TenantId == principal.TenantId).Take(200).ToArray()));
});

app.MapGet("/api/settings/holidays", (HttpRequest request, TokenService tokens, LegalPilotStore store) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(store.Read(() => store.Holidays.Where(h => h.TenantId == principal.TenantId).OrderBy(h => h.Date).ToArray()));
});

app.MapGet("/api/diagnostics", (HttpRequest request, TokenService tokens, LegalPilotStore store, EmailConnectorRegistry connectors) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
    return Results.Ok(store.Read(() => new
    {
        utc = DateTimeOffset.UtcNow,
        store.DataFilePath,
        counts = new
        {
            tenants = store.Tenants.Count,
            users = store.Users.Count,
            clients = store.Clients.Count,
            cases = store.Cases.Count,
            mailboxes = store.Mailboxes.Count,
            oauthTokenCredentials = store.OAuthTokenCredentials.Count,
            emails = store.Emails.Count,
            deadlines = store.Deadlines.Count,
            calendarEvents = store.CalendarEvents.Count,
            reminders = store.Reminders.Count,
            notifications = store.Notifications.Count,
            chatMessages = store.ChatMessages.Count,
            auditEntries = store.AuditEntries.Count,
            aiKnowledgeDocuments = store.AiKnowledgeDocuments.Count,
            aiProcessingRuns = store.AiProcessingRuns.Count
        },
        integrations = connectors.Status()
    }));
});

app.MapGet("/api/status", (HttpRequest request, TokenService tokens, LegalPilotStore store, EmailConnectorRegistry connectors, OpenWaClient openWa, IConfiguration configuration) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
    var postgresConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEGALPILOT_DATABASE_URL")) ||
                             !string.IsNullOrWhiteSpace(configuration.GetConnectionString("LegalPilotPostgres")) ||
                             !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATABASE_URL"));

    return Results.Ok(store.Read(() => new
    {
        product = "LegalPilot Ecuador",
        utc = DateTimeOffset.UtcNow,
        storage = new
        {
            provider = store.PersistenceProvider,
            durable = true,
            multiInstanceSafe = store.PersistenceProvider == "postgresql",
            dataSource = store.DataFilePath,
            diagnostics = store.PersistenceDiagnostics,
            postgres = new
            {
                configured = postgresConfigured || store.PersistenceProvider == "postgresql",
                status = store.PersistenceProvider == "postgresql" ? "Active" : postgresConfigured ? "ConfigurationDetected" : "NotConfigured",
                message = store.PersistenceProvider == "postgresql"
                    ? "PostgreSQL activo como fuente de verdad. Migraciones aplicadas al iniciar."
                    : "Configure LEGALPILOT_DATABASE_URL o ConnectionStrings:LegalPilotPostgres para activar Supabase/PostgreSQL."
            }
        },
        security = new
        {
            auth = "AccessToken+RefreshRotation",
            roles = store.Users.Count(u => u.TenantId == principal.TenantId && u.IsActive),
            activeRefreshSessions = store.RefreshTokenSessions.Count(t => t.TenantId == principal.TenantId && t.RevokedAt is null && t.ExpiresAt > DateTimeOffset.UtcNow)
        },
        jobs = new
        {
            remindersPending = store.Reminders.Count(r => r.TenantId == principal.TenantId && r.Status == NotificationStatus.Pending),
            mailboxSyncStates = store.MailboxSyncStates.Count(s => s.TenantId == principal.TenantId)
        },
        integrations = new
        {
            mail = connectors.Status(),
            openWa = openWa.GetReadiness(),
            ai = new
            {
                provider = configuration["LegalPilot:AI:Provider"] ?? "local-deterministic",
                model = configuration["LegalPilot:AI:Model"] ?? "rules-v1",
                knowledgeDocuments = store.AiKnowledgeDocuments.Count(d => d.TenantId == principal.TenantId),
                processingRuns = store.AiProcessingRuns.Count(r => r.TenantId == principal.TenantId)
            }
        },
        counts = new
        {
            tenants = store.Tenants.Count,
            users = store.Users.Count(u => u.TenantId == principal.TenantId),
            clients = store.Clients.Count(c => c.TenantId == principal.TenantId),
            cases = store.Cases.Count(c => c.TenantId == principal.TenantId),
            emails = store.Emails.Count(e => e.TenantId == principal.TenantId),
            deadlines = store.Deadlines.Count(d => d.TenantId == principal.TenantId),
            calendarEvents = store.CalendarEvents.Count(e => e.TenantId == principal.TenantId),
            auditEntries = store.AuditEntries.Count(a => a.TenantId == principal.TenantId),
            oauthTokenCredentials = store.OAuthTokenCredentials.Count(t => t.TenantId == principal.TenantId),
            aiRuns = store.AiProcessingRuns.Count(r => r.TenantId == principal.TenantId)
        }
    }));
});

app.MapPost("/api/settings/holidays", (HttpRequest request, TokenService tokens, LegalPilotStore store, UpsertHolidayRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
    var name = InputGuard.Required(payload.Name, "Nombre", 120);
    var province = InputGuard.Optional(payload.Province, 80);
    var canton = InputGuard.Optional(payload.Canton, 80);
    var source = InputGuard.Optional(payload.Source, 160);
    var holiday = new Holiday(
        Guid.NewGuid(),
        principal.TenantId,
        payload.Date,
        name,
        payload.Scope,
        string.IsNullOrWhiteSpace(province) ? null : province,
        string.IsNullOrWhiteSpace(canton) ? null : canton,
        string.IsNullOrWhiteSpace(source) ? "Manual" : source,
        payload.IsBusinessDayOverride,
        DateTimeOffset.UtcNow);
    store.Write(() => store.Holidays.Add(holiday));
    store.Audit(principal.TenantId, principal.UserId, AuditAction.Create, nameof(Holiday), holiday.Id.ToString(), $"Feriado/excepcion creado: {holiday.Name}");
    return Results.Created("/api/settings/holidays", holiday);
});

app.MapGet("/api/ai/status", (HttpRequest request, TokenService tokens, LegalAiPipelineService ai) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(ai.Status(principal));
});

app.MapPost("/api/ai/analyze", (HttpRequest request, TokenService tokens, LegalAiPipelineService ai, AiAnalyzeRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Created("/api/ai/runs", ai.Analyze(principal, payload));
});

app.MapPost("/api/ai/knowledge", (HttpRequest request, TokenService tokens, LegalAiPipelineService ai, AiKnowledgeDocumentRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Created("/api/ai/knowledge", ai.RegisterKnowledge(principal, payload));
});

app.MapPost("/api/ai/feedback", (HttpRequest request, TokenService tokens, LegalAiPipelineService ai, AiFeedbackRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Created("/api/ai/feedback", ai.Feedback(principal, payload));
});

app.MapGet("/api/reports/overview", (HttpRequest request, TokenService tokens, ReportService reports) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(reports.Overview(principal));
});

app.MapFallbackToFile("index.html");

app.Run();

public sealed record ReviewDeadlineRequest(bool Approved, string? Comment);
public sealed record UpsertHolidayRequest(DateOnly Date, string Name, HolidayScope Scope, string? Province, string? Canton, string? Source, bool IsBusinessDayOverride);

static class MailProviderParser
{
    public static bool TryParse(string value, out MailProvider provider)
    {
        if (Enum.TryParse(value, true, out provider))
        {
            return true;
        }

        if (value.Equals("microsoft", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("graph", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("outlook", StringComparison.OrdinalIgnoreCase))
        {
            provider = MailProvider.Outlook;
            return true;
        }

        if (value.Equals("google", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("gmail", StringComparison.OrdinalIgnoreCase))
        {
            provider = MailProvider.Gmail;
            return true;
        }

        return false;
    }
}

static class OpenApiContract
{
    public static object Build()
    {
        var paths = new Dictionary<string, object>
        {
            ["/health"] = Path("get", "Health publico del backend"),
            ["/api/auth/login"] = Path("post", "Login con access token y refresh token rotativo"),
            ["/api/auth/refresh"] = Path("post", "Rotacion de refresh token"),
            ["/api/auth/logout"] = Path("post", "Revocacion de sesion"),
            ["/api/auth/forgot-password"] = Path("post", "Inicio de recuperacion de contrasena"),
            ["/api/auth/reset-password"] = Path("post", "Cambio de contrasena con token"),
            ["/api/me"] = Path("get", "Perfil autenticado"),
            ["/api/users"] = Path("get", "Usuarios del tenant"),
            ["/api/cases"] = Path("get", "Listado de casos", "post", "Crear caso"),
            ["/api/cases/{id}"] = Path("get", "Detalle de caso"),
            ["/api/clients"] = Path("get", "Listado de clientes", "post", "Crear cliente"),
            ["/api/mailboxes"] = Path("get", "Buzones conectados"),
            ["/api/mailboxes/connect"] = Path("post", "Registrar buzon para OAuth"),
            ["/api/mailboxes/{id}/sync"] = Path("post", "Sincronizar buzon"),
            ["/api/oauth/start"] = Path("post", "Iniciar OAuth Gmail/Microsoft"),
            ["/api/oauth/{provider}/callback"] = Path("get", "Callback OAuth con exchange y token cifrado"),
            ["/api/inbox"] = Path("get", "Inbox legal procesado"),
            ["/api/inbox/manual"] = Path("post", "Ingesta manual de correo legal"),
            ["/api/deadlines"] = Path("get", "Plazos", "post", "Crear plazo deterministico"),
            ["/api/deadlines/calculate"] = Path("post", "Calcular plazo Ecuador sin LLM"),
            ["/api/calendar/events"] = Path("get", "Eventos", "post", "Crear evento"),
            ["/api/alerts"] = Path("get", "Alertas"),
            ["/api/chat/messages"] = Path("get", "Mensajes", "post", "Crear mensaje"),
            ["/api/whatsapp/messages"] = Path("get", "WhatsApp enviados"),
            ["/api/whatsapp/send-client-message"] = Path("post", "Enviar WhatsApp por OpenWA"),
            ["/api/ai/status"] = Path("get", "Estado RAG/fine-tuning/guardrails"),
            ["/api/ai/analyze"] = Path("post", "Clasificacion y extraccion asistida sin calculo de plazos"),
            ["/api/ai/knowledge"] = Path("post", "Registrar fuente para RAG"),
            ["/api/ai/feedback"] = Path("post", "Guardar correccion humana para entrenamiento"),
            ["/api/audit"] = Path("get", "Bitacora auditada"),
            ["/api/status"] = Path("get", "Diagnostico operativo autenticado"),
            ["/api/webhooks/gmail"] = Path("post", "Webhook Gmail Pub/Sub"),
            ["/api/webhooks/microsoft"] = Path("get", "Validacion Graph", "post", "Webhook Microsoft Graph"),
            ["/api/webhooks/openwa"] = Path("post", "Webhook OpenWA")
        };

        return new
        {
            openapi = "3.0.3",
            info = new { title = "LegalPilot Ecuador API", version = "1.0.0" },
            paths
        };
    }

    private static object Path(params string[] methodsAndDescriptions)
    {
        var map = new Dictionary<string, object>();
        for (var i = 0; i + 1 < methodsAndDescriptions.Length; i += 2)
        {
            map[methodsAndDescriptions[i]] = new
            {
                summary = methodsAndDescriptions[i + 1],
                responses = new Dictionary<string, object>
                {
                    ["200"] = new { description = "OK" },
                    ["400"] = new { description = "Solicitud invalida" },
                    ["401"] = new { description = "No autenticado" },
                    ["403"] = new { description = "Sin permisos" }
                }
            };
        }

        return map;
    }
}
