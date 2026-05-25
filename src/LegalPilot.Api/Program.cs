using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using LegalPilot.Api.Application;
using LegalPilot.Api.Domain;
using LegalPilot.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
DotEnv.Load(builder.Environment.ContentRootPath, Directory.GetCurrentDirectory());
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddInMemoryCollection(LegalPilotEnvironmentVariables.Build());
var configuredUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? builder.Configuration["Urls"];
if (!string.IsNullOrWhiteSpace(configuredUrls))
{
    builder.WebHost.UseUrls(configuredUrls);
}

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
builder.Services.Configure<LegalPilotCloudOAuthOptions>(builder.Configuration.GetSection("LegalPilot"));
builder.Services.AddSingleton<ICloudOAuthWebhookService, CloudOAuthWebhookService>();
builder.Services.AddSingleton<IEmailConnector, GmailEmailConnector>();
builder.Services.AddSingleton<IEmailConnector, MicrosoftGraphEmailConnector>();
builder.Services.AddSingleton<EmailConnectorRegistry>();
builder.Services.AddSingleton<LegalIntelligenceService>();
builder.Services.AddSingleton<LegalAiPipelineService>();
builder.Services.AddSingleton<EcuadorDeadlineEngine>();
builder.Services.AddSingleton<LegalWorkflowService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<CalendarService>();
builder.Services.AddSingleton<ExternalCalendarSyncService>();
builder.Services.AddSingleton<WhatsAppService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddHttpClient("gemini", client => client.Timeout = TimeSpan.FromSeconds(180));
builder.Services.AddHttpClient<OpenWaClient>();
builder.Services.AddHostedService<ReminderDispatcher>();
builder.Services.AddHostedService<MailboxSyncWorker>();
builder.Services.AddHostedService<DeadlineMonitorWorker>();
builder.Services.AddHostedService<CalendarExternalSyncWorker>();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
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
app.MapControllers();

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

app.MapGet("/api/auth/status", (LegalPilotStore store, IConfiguration configuration, EmailConnectorRegistry connectors, OpenWaClient openWa) =>
{
    var aiApiKeyConfigured = !string.IsNullOrWhiteSpace(configuration["LegalPilot:AI:ApiKey"]) ||
                             !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));
    var aiModel = string.IsNullOrWhiteSpace(configuration["LegalPilot:AI:Model"])
        ? "gemini-2.5-flash"
        : configuration["LegalPilot:AI:Model"];
    var aiProvider = string.IsNullOrWhiteSpace(configuration["LegalPilot:AI:Provider"])
        ? aiApiKeyConfigured ? "gemini" : "not-configured"
        : configuration["LegalPilot:AI:Provider"];
    var bootstrapMissing = ConfigurationDiagnostics.Missing(configuration,
        "LegalPilot:Bootstrap:AdminEmail",
        "LegalPilot:Bootstrap:AdminPassword");

    return Results.Ok(store.Read(() => new
    {
        auth = new
        {
            ready = store.Users.Any(u => u.IsActive),
            activeUsers = store.Users.Count(u => u.IsActive),
            tenantCount = store.Tenants.Count,
            bootstrapConfigured = bootstrapMissing.Length == 0,
            bootstrapMissing
        },
        storage = new
        {
            provider = store.PersistenceProvider,
            dataSource = store.DataFilePath
        },
        integrations = new
        {
            mail = connectors.Status().Select(item => new
            {
                item.Provider,
                item.Configured,
                item.Status,
                item.Message,
                item.RequiredSettings
            }).ToArray(),
            openWa = openWa.GetReadiness(),
            ai = new
            {
                provider = aiProvider,
                model = aiModel,
                configured = aiApiKeyConfigured,
                status = aiApiKeyConfigured ? "GeminiReady" : "GeminiApiKeyMissing",
                message = aiApiKeyConfigured
                    ? "Gemini esta configurado para analisis real."
                    : "Configure GEMINI_API_KEY o LEGALPILOT_AI_API_KEY para usar Gemini."
            }
        }
    }));
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
        emails = store.Read(() => store.Emails.Where(e => e.CaseId == id && e.TenantId == principal.TenantId && e.ProcessingStatus != "IgnoredNonLegal").OrderByDescending(e => e.ReceivedAt).ToArray())
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
    return Results.Ok(await mailboxes.Sync(principal, id, cancellationToken));
});

app.MapPatch("/api/mailboxes/{id:guid}/calendar", (HttpRequest request, TokenService tokens, MailboxService mailboxes, Guid id, UpdateMailboxCalendarRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(mailboxes.UpdateCalendarPreference(principal, id, payload));
});

app.MapDelete("/api/mailboxes/{id:guid}", (HttpRequest request, TokenService tokens, MailboxService mailboxes, Guid id) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(mailboxes.Disconnect(principal, id));
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

app.MapGet("/api/integrations/status", (HttpRequest request, TokenService tokens, EmailConnectorRegistry connectors, OpenWaClient openWa, ExternalCalendarSyncService calendars) =>
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
        },
        calendar = calendars.Status(principal)
    });
});

app.MapPost("/api/inbox/manual", (HttpRequest request, TokenService tokens, LegalWorkflowService workflow, ManualEmailRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Created("/api/inbox/manual", workflow.IngestManual(principal, payload));
});

app.MapPost("/api/webhooks/openwa", async (HttpRequest request, WhatsAppService whatsApp, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    using var reader = new StreamReader(request.Body);
    var rawBody = await reader.ReadToEndAsync(cancellationToken);
    WebhookSecurity.RequireOpenWaSignatureOrSecret(request, configuration, rawBody);
    var payload = OpenWaWebhookParser.Parse(rawBody);
    return Results.Accepted("/api/webhooks/openwa", await whatsApp.ReceiveWebhookAsync(payload, cancellationToken));
});

app.MapGet("/api/inbox", (HttpRequest request, TokenService tokens, LegalPilotStore store) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(store.Read(() => store.Emails
        .Where(e => e.TenantId == principal.TenantId)
        .Where(e => e.ProcessingStatus != "IgnoredNonLegal")
        .OrderByDescending(e => e.ReceivedAt)
        .Take(100)
        .Select(e => new
        {
            e.Id,
            e.TenantId,
            e.MailboxConnectionId,
            e.CaseId,
            e.Provider,
            e.ExternalMessageId,
            e.Subject,
            e.Sender,
            e.Recipients,
            e.BodyText,
            e.RawReference,
            e.ReceivedAt,
            e.ProcessingStatus,
            e.Extraction,
            e.CreatedAt,
            AttachmentCount = store.Attachments.Count(a => a.LegalEmailId == e.Id && a.TenantId == principal.TenantId)
        })
        .ToArray()));
});

app.MapGet("/api/inbox/{id:guid}/attachments", (HttpRequest request, TokenService tokens, LegalPilotStore store, Guid id) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    if (store.Read(() => store.Emails.All(e => e.Id != id || e.TenantId != principal.TenantId)))
    {
        throw new KeyNotFoundException("Correo no encontrado.");
    }

    return Results.Ok(store.Read(() => store.Attachments
        .Where(a => a.TenantId == principal.TenantId && a.LegalEmailId == id)
        .OrderBy(a => a.FileName)
        .ToArray()));
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

app.MapPatch("/api/calendar/events/{id:guid}", (HttpRequest request, TokenService tokens, CalendarService calendar, Guid id, UpdateCalendarEventRequest payload) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(calendar.Update(principal, id, payload));
});

app.MapDelete("/api/calendar/events/{id:guid}", async (HttpRequest request, TokenService tokens, CalendarService calendar, ExternalCalendarSyncService calendars, Guid id, CancellationToken cancellationToken) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    var cancelled = calendar.Cancel(principal, id);
    var sync = await calendars.SyncEventAsync(principal, id, cancellationToken);
    return Results.Ok(new { eventItem = cancelled, sync });
});

app.MapPost("/api/calendar/events/{id:guid}/sync", async (HttpRequest request, TokenService tokens, ExternalCalendarSyncService calendars, Guid id, CancellationToken cancellationToken) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Ok(await calendars.SyncEventAsync(principal, id, cancellationToken));
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

app.MapGet("/api/status", (HttpRequest request, TokenService tokens, LegalPilotStore store, EmailConnectorRegistry connectors, OpenWaClient openWa, ExternalCalendarSyncService calendars, IConfiguration configuration) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
    var postgresConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEGALPILOT_DATABASE_URL")) ||
                             !string.IsNullOrWhiteSpace(configuration.GetConnectionString("LegalPilotPostgres")) ||
                             !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATABASE_URL"));
    var calendarStatus = calendars.Status(principal);

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
            calendar = calendarStatus,
            ai = new
            {
                provider = string.IsNullOrWhiteSpace(configuration["LegalPilot:AI:Provider"])
                    ? (!string.IsNullOrWhiteSpace(configuration["LegalPilot:AI:ApiKey"]) ? "gemini" : "not-configured")
                    : configuration["LegalPilot:AI:Provider"],
                model = string.IsNullOrWhiteSpace(configuration["LegalPilot:AI:Model"]) ? "gemini-2.5-flash" : configuration["LegalPilot:AI:Model"],
                configured = !string.IsNullOrWhiteSpace(configuration["LegalPilot:AI:ApiKey"]),
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
            emails = store.Emails.Count(e => e.TenantId == principal.TenantId && e.ProcessingStatus != "IgnoredNonLegal"),
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

app.MapGet("/api/ai/dataset.jsonl", (HttpRequest request, TokenService tokens, LegalAiPipelineService ai) =>
{
    var principal = HttpAuth.RequirePrincipal(request, tokens);
    return Results.Text(ai.ExportDatasetJsonl(principal), "application/x-jsonlines; charset=utf-8");
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

static class LegalPilotEnvironmentVariables
{
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["LEGALPILOT_TOKEN_SIGNING_KEY"] = "LegalPilot:TokenSigningKey",
        ["LEGALPILOT_DATA_PROTECTION_KEY"] = "LegalPilot:Security:DataProtectionKey",
        ["LEGALPILOT_BOOTSTRAP_TENANT_NAME"] = "LegalPilot:Bootstrap:TenantName",
        ["LEGALPILOT_BOOTSTRAP_ADMIN_EMAIL"] = "LegalPilot:Bootstrap:AdminEmail",
        ["LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD"] = "LegalPilot:Bootstrap:AdminPassword",
        ["LEGALPILOT_BOOTSTRAP_LAWYER_PASSWORD"] = "LegalPilot:Bootstrap:LawyerPassword",
        ["LEGALPILOT_STORAGE_PATH"] = "LegalPilot:Storage:Path",
        ["LEGALPILOT_STORAGE_REQUIRE_POSTGRES"] = "LegalPilot:Storage:RequirePostgres",
        ["LEGALPILOT_MIGRATE_LOCAL_JSON"] = "LegalPilot:Storage:MigrateLocalJson",
        ["LEGALPILOT_GMAIL_CLIENT_ID"] = "LegalPilot:Gmail:ClientId",
        ["LEGALPILOT_GMAIL_CLIENT_SECRET"] = "LegalPilot:Gmail:ClientSecret",
        ["LEGALPILOT_GMAIL_REDIRECT_URI"] = "LegalPilot:Gmail:RedirectUri",
        ["LEGALPILOT_GMAIL_WEBHOOK_SECRET"] = "LegalPilot:Gmail:WebhookSecret",
        ["LEGALPILOT_GMAIL_PUBSUB_TOPIC_NAME"] = "LegalPilot:Gmail:PubSubTopicName",
        ["LEGALPILOT_MICROSOFT_CLIENT_ID"] = "LegalPilot:Microsoft:ClientId",
        ["LEGALPILOT_MICROSOFT_CLIENT_SECRET"] = "LegalPilot:Microsoft:ClientSecret",
        ["LEGALPILOT_MICROSOFT_TENANT_ID"] = "LegalPilot:Microsoft:TenantId",
        ["LEGALPILOT_MICROSOFT_REDIRECT_URI"] = "LegalPilot:Microsoft:RedirectUri",
        ["LEGALPILOT_MICROSOFT_WEBHOOK_CLIENT_STATE"] = "LegalPilot:Microsoft:WebhookClientState",
        ["LEGALPILOT_MICROSOFT_WEBHOOK_NOTIFICATION_URL"] = "LegalPilot:Microsoft:WebhookNotificationUrl",
        ["LEGALPILOT_OPENWA_BASE_URL"] = "LegalPilot:OpenWa:BaseUrl",
        ["LEGALPILOT_OPENWA_API_KEY"] = "LegalPilot:OpenWa:ApiKey",
        ["LEGALPILOT_OPENWA_SESSION_ID"] = "LegalPilot:OpenWa:SessionId",
        ["LEGALPILOT_OPENWA_WEBHOOK_SECRET"] = "LegalPilot:OpenWa:WebhookSecret",
        ["LEGALPILOT_CALENDAR_PREFERRED_PROVIDER"] = "LegalPilot:Calendar:PreferredProvider",
        ["LEGALPILOT_AI_PROVIDER"] = "LegalPilot:AI:Provider",
        ["LEGALPILOT_AI_MODEL"] = "LegalPilot:AI:Model",
        ["LEGALPILOT_AI_EMBEDDING_MODEL"] = "LegalPilot:AI:EmbeddingModel",
        ["LEGALPILOT_AI_API_KEY"] = "LegalPilot:AI:ApiKey",
        ["GEMINI_API_KEY"] = "LegalPilot:AI:ApiKey"
    };

    public static IReadOnlyDictionary<string, string?> Build()
    {
        return Map
            .Select(pair => new KeyValuePair<string, string?>(pair.Value, Environment.GetEnvironmentVariable(pair.Key)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
}

static class DotEnv
{
    public static void Load(params string[] roots)
    {
        var candidates = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .SelectMany(root => new[]
            {
                Path.Combine(root, ".env"),
                Path.Combine(root, ".env.local"),
                Path.Combine(root, "..", ".env"),
                Path.Combine(root, "..", ".env.local"),
                Path.Combine(root, "..", "..", ".env"),
                Path.Combine(root, "..", "..", ".env.local")
            })
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToArray();

        var processValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                processValues[key] = entry.Value?.ToString();
            }
        }

        var loadedByDotEnv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in candidates)
        {
            var localFile = Path.GetFileName(file).Equals(".env.local", StringComparison.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(file))
            {
                var parsed = ParseLine(line);
                if (parsed is null)
                {
                    continue;
                }

                var (key, value) = parsed.Value;
                var alreadyConfiguredByProcess = processValues.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing);
                if (alreadyConfiguredByProcess && !loadedByDotEnv.Contains(key))
                {
                    continue;
                }

                if (!localFile && loadedByDotEnv.Contains(key))
                {
                    continue;
                }

                Environment.SetEnvironmentVariable(key, value);
                loadedByDotEnv.Add(key);
            }
        }
    }

    private static (string Key, string Value)? ParseLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return null;
        }

        if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["export ".Length..].TrimStart();
        }

        var index = trimmed.IndexOf('=');
        if (index <= 0)
        {
            return null;
        }

        var key = trimmed[..index].Trim();
        var value = trimmed[(index + 1)..].Trim();
        if (key.Length == 0)
        {
            return null;
        }

        value = Unquote(value);
        return (key, value);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        return value
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);
    }
}

static class ConfigurationDiagnostics
{
    public static string[] Missing(IConfiguration configuration, params string[] keys)
    {
        return keys.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();
    }
}

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
            ["/api/auth/gmail/login"] = Path("get", "Iniciar OAuth Gmail y redirigir al consentimiento"),
            ["/api/auth/gmail/callback"] = Path("get", "Callback OAuth Gmail, exchange token y users.watch"),
            ["/api/auth/microsoft/login"] = Path("get", "Iniciar OAuth Microsoft y redirigir al consentimiento"),
            ["/api/auth/microsoft/callback"] = Path("get", "Callback OAuth Microsoft, MSAL y Graph subscriptions"),
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
            ["/api/inbox/{id}/attachments"] = Path("get", "Adjuntos normalizados de un correo"),
            ["/api/inbox/manual"] = Path("post", "Ingesta manual de correo legal"),
            ["/api/deadlines"] = Path("get", "Plazos", "post", "Crear plazo deterministico"),
            ["/api/deadlines/calculate"] = Path("post", "Calcular plazo Ecuador sin LLM"),
            ["/api/calendar/events"] = Path("get", "Eventos", "post", "Crear evento"),
            ["/api/alerts"] = Path("get", "Alertas"),
            ["/api/calendar/events/{id}/sync"] = Path("post", "Sincronizar evento confirmado con Google/Outlook Calendar"),
            ["/api/chat/messages"] = Path("get", "Mensajes", "post", "Crear mensaje"),
            ["/api/whatsapp/messages"] = Path("get", "WhatsApp enviados"),
            ["/api/whatsapp/send-client-message"] = Path("post", "Enviar WhatsApp por OpenWA"),
            ["/api/ai/status"] = Path("get", "Estado RAG/fine-tuning/guardrails"),
            ["/api/ai/analyze"] = Path("post", "Clasificacion y extraccion asistida sin calculo de plazos"),
            ["/api/ai/knowledge"] = Path("post", "Registrar fuente para RAG"),
            ["/api/ai/feedback"] = Path("post", "Guardar correccion humana para entrenamiento"),
            ["/api/ai/dataset.jsonl"] = Path("get", "Exportar dataset JSONL para entrenamiento/fine-tuning ligero"),
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

static class OpenWaWebhookParser
{
    public static OpenWaWebhookRequest Parse(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return new OpenWaWebhookRequest(null, null, null, "empty");
        }

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object
            ? dataElement
            : root;
        var message = data.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.Object
            ? messageElement
            : data;

        return new OpenWaWebhookRequest(
            Get(root, "sessionId") ?? Get(data, "sessionId") ?? Get(root, "session") ?? Get(data, "session"),
            Get(root, "from") ?? Get(data, "from") ?? Get(message, "from") ?? Get(root, "chatId") ?? Get(data, "chatId"),
            Get(root, "body") ?? Get(data, "body") ?? Get(message, "body") ?? Get(message, "text") ?? Get(root, "text"),
            Get(root, "event") ?? Get(root, "type") ?? Get(data, "event") ?? "message",
            Get(root, "chatId") ?? Get(data, "chatId") ?? Get(message, "chatId"),
            Get(root, "text") ?? Get(data, "text") ?? Get(message, "text"));
    }

    private static string? Get(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : null;
    }
}
