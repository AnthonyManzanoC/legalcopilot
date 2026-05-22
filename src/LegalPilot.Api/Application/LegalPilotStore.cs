using LegalPilot.Api.Domain;
using LegalPilot.Api.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegalPilot.Api.Application;

public sealed class LegalPilotStore
{
    private readonly object _gate = new();
    private readonly ILegalPilotPersistence _persistence;
    private readonly ILogger<LegalPilotStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public List<Tenant> Tenants { get; } = [];
    public List<UserAccount> Users { get; } = [];
    public List<ClientProfile> Clients { get; } = [];
    public List<LegalCase> Cases { get; } = [];
    public List<MailboxConnection> Mailboxes { get; } = [];
    public List<MailboxSyncState> MailboxSyncStates { get; } = [];
    public List<OAuthStateTicket> OAuthStateTickets { get; } = [];
    public List<OAuthTokenCredential> OAuthTokenCredentials { get; } = [];
    public List<LegalEmail> Emails { get; } = [];
    public List<DocumentAttachment> Attachments { get; } = [];
    public List<Holiday> Holidays { get; } = [];
    public List<Deadline> Deadlines { get; } = [];
    public List<CalendarEvent> CalendarEvents { get; } = [];
    public List<Reminder> Reminders { get; } = [];
    public List<Notification> Notifications { get; } = [];
    public List<WhatsAppTemplate> WhatsAppTemplates { get; } = [];
    public List<WhatsAppMessage> WhatsAppMessages { get; } = [];
    public List<ChatMessage> ChatMessages { get; } = [];
    public List<AuditEntry> AuditEntries { get; } = [];
    public List<PasswordResetTicket> PasswordResetTickets { get; } = [];
    public List<RefreshTokenSession> RefreshTokenSessions { get; } = [];
    public List<AiKnowledgeDocument> AiKnowledgeDocuments { get; } = [];
    public List<AiProcessingRun> AiProcessingRuns { get; } = [];
    public List<AiFeedbackEntry> AiFeedbackEntries { get; } = [];

    public LegalPilotStore(IConfiguration configuration, IWebHostEnvironment environment, ILogger<LegalPilotStore> logger)
    {
        _logger = logger;
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var configuredPath = configuration["LegalPilot:Storage:Path"];
        var dataFile = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "legalpilot-store.json")
            : Path.GetFullPath(configuredPath);

        var postgresConnection = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_DATABASE_URL"),
            configuration.GetConnectionString("LegalPilotPostgres"),
            Environment.GetEnvironmentVariable("DATABASE_URL"));

        var requirePostgres = environment.IsProduction() ||
                              string.Equals(configuration["LegalPilot:Storage:RequirePostgres"], "true", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(postgresConnection) && requirePostgres)
        {
            throw new InvalidOperationException("PostgreSQL es obligatorio en produccion. Configure LEGALPILOT_DATABASE_URL o ConnectionStrings:LegalPilotPostgres desde variables de entorno o Secret Manager.");
        }

        _persistence = string.IsNullOrWhiteSpace(postgresConnection)
            ? new JsonFileLegalPilotPersistence(dataFile, _jsonOptions, logger)
            : new PostgresLegalPilotPersistence(postgresConnection, dataFile, _jsonOptions, logger);

        Load();
    }

    public T Write<T>(Func<T> action)
    {
        lock (_gate)
        {
            var result = action();
            Persist();
            return result;
        }
    }

    public void Write(Action action)
    {
        lock (_gate)
        {
            action();
            Persist();
        }
    }

    public T Read<T>(Func<T> action)
    {
        lock (_gate)
        {
            return action();
        }
    }

    public void Audit(Guid tenantId, Guid? actorUserId, AuditAction action, string entityType, string entityId, string summary, IReadOnlyDictionary<string, string>? metadata = null)
    {
        Write(() =>
        {
            AuditEntries.Insert(0, new AuditEntry(
                Guid.NewGuid(),
                tenantId,
                actorUserId,
                action,
                entityType,
                entityId,
                summary,
                metadata ?? new Dictionary<string, string>(),
                DateTimeOffset.UtcNow));
        });
    }

    public string DataFilePath => _persistence.DataSource;

    public string PersistenceProvider => _persistence.Provider;

    public object PersistenceDiagnostics => _persistence.Diagnostics();

    public void Flush()
    {
        lock (_gate)
        {
            Persist();
        }
    }

    private void Load()
    {
        try
        {
            var snapshot = _persistence.Load();
            if (snapshot is null)
            {
                return;
            }

            Tenants.AddRange(snapshot.Tenants ?? []);
            Users.AddRange(snapshot.Users ?? []);
            Clients.AddRange(snapshot.Clients ?? []);
            Cases.AddRange(snapshot.Cases ?? []);
            Mailboxes.AddRange(snapshot.Mailboxes ?? []);
            MailboxSyncStates.AddRange(snapshot.MailboxSyncStates ?? []);
            OAuthStateTickets.AddRange(snapshot.OAuthStateTickets ?? []);
            OAuthTokenCredentials.AddRange(snapshot.OAuthTokenCredentials ?? []);
            Emails.AddRange(snapshot.Emails ?? []);
            Attachments.AddRange(snapshot.Attachments ?? []);
            Holidays.AddRange(snapshot.Holidays ?? []);
            Deadlines.AddRange(snapshot.Deadlines ?? []);
            CalendarEvents.AddRange(snapshot.CalendarEvents ?? []);
            Reminders.AddRange(snapshot.Reminders ?? []);
            Notifications.AddRange(snapshot.Notifications ?? []);
            WhatsAppTemplates.AddRange(snapshot.WhatsAppTemplates ?? []);
            WhatsAppMessages.AddRange(snapshot.WhatsAppMessages ?? []);
            ChatMessages.AddRange(snapshot.ChatMessages ?? []);
            AuditEntries.AddRange(snapshot.AuditEntries ?? []);
            PasswordResetTickets.AddRange(snapshot.PasswordResetTickets ?? []);
            RefreshTokenSessions.AddRange(snapshot.RefreshTokenSessions ?? []);
            AiKnowledgeDocuments.AddRange(snapshot.AiKnowledgeDocuments ?? []);
            AiProcessingRuns.AddRange(snapshot.AiProcessingRuns ?? []);
            AiFeedbackEntries.AddRange(snapshot.AiFeedbackEntries ?? []);
            _logger.LogInformation("LegalPilot store loaded from {Provider}: {Path}.", _persistence.Provider, _persistence.DataSource);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load LegalPilot store from {Provider}.", _persistence.Provider);
            throw;
        }
    }

    private void Persist()
    {
        var snapshot = new StoreSnapshot(
            4,
            Tenants,
            Users,
            Clients,
            Cases,
            Mailboxes,
            MailboxSyncStates,
            OAuthStateTickets,
            OAuthTokenCredentials,
            Emails,
            Attachments,
            Holidays,
            Deadlines,
            CalendarEvents,
            Reminders,
            Notifications,
            WhatsAppTemplates,
            WhatsAppMessages,
            ChatMessages,
            AuditEntries,
            PasswordResetTickets,
            RefreshTokenSessions,
            AiKnowledgeDocuments,
            AiProcessingRuns,
            AiFeedbackEntries);

        _persistence.Persist(snapshot);
    }

    private static string? FirstConfigured(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}

public sealed record StoreSnapshot(
    int SchemaVersion,
    List<Tenant>? Tenants,
    List<UserAccount>? Users,
    List<ClientProfile>? Clients,
    List<LegalCase>? Cases,
    List<MailboxConnection>? Mailboxes,
    List<MailboxSyncState>? MailboxSyncStates,
    List<OAuthStateTicket>? OAuthStateTickets,
    List<OAuthTokenCredential>? OAuthTokenCredentials,
    List<LegalEmail>? Emails,
    List<DocumentAttachment>? Attachments,
    List<Holiday>? Holidays,
    List<Deadline>? Deadlines,
    List<CalendarEvent>? CalendarEvents,
    List<Reminder>? Reminders,
    List<Notification>? Notifications,
    List<WhatsAppTemplate>? WhatsAppTemplates,
    List<WhatsAppMessage>? WhatsAppMessages,
    List<ChatMessage>? ChatMessages,
    List<AuditEntry>? AuditEntries,
    List<PasswordResetTicket>? PasswordResetTickets,
    List<RefreshTokenSession>? RefreshTokenSessions,
    List<AiKnowledgeDocument>? AiKnowledgeDocuments,
    List<AiProcessingRun>? AiProcessingRuns,
    List<AiFeedbackEntry>? AiFeedbackEntries);

public interface ILegalPilotPersistence
{
    string Provider { get; }
    string DataSource { get; }
    StoreSnapshot? Load();
    void Persist(StoreSnapshot snapshot);
    object Diagnostics();
}

public sealed class JsonFileLegalPilotPersistence(
    string dataFile,
    JsonSerializerOptions jsonOptions,
    ILogger logger) : ILegalPilotPersistence
{
    public string Provider => "json-file";

    public string DataSource => dataFile;

    public StoreSnapshot? Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dataFile)!);
        if (!File.Exists(dataFile))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(dataFile);
            return JsonSerializer.Deserialize<StoreSnapshot>(json, jsonOptions);
        }
        catch (Exception ex)
        {
            var quarantinePath = $"{dataFile}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.invalid";
            File.Move(dataFile, quarantinePath, overwrite: true);
            logger.LogError(ex, "Could not load LegalPilot JSON store. Invalid file moved to {Path}.", quarantinePath);
            return null;
        }
    }

    public void Persist(StoreSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dataFile)!);
        var tempFile = $"{dataFile}.tmp";
        File.WriteAllText(tempFile, JsonSerializer.Serialize(snapshot, jsonOptions));
        File.Move(tempFile, dataFile, overwrite: true);
    }

    public object Diagnostics()
    {
        return new
        {
            provider = Provider,
            dataSource = DataSource,
            exists = File.Exists(dataFile)
        };
    }
}

public static class SeedData
{
    public static void Seed(LegalPilotStore store, PasswordHasher hasher, IConfiguration? configuration = null, IWebHostEnvironment? environment = null)
    {
        if (store.Tenants.Count > 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var production = environment?.IsProduction() == true;
        var tenantName = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_BOOTSTRAP_TENANT_NAME"),
            configuration?["LegalPilot:Bootstrap:TenantName"]) ?? "LegalPilot Demo Ecuador";
        var adminEmail = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_BOOTSTRAP_ADMIN_EMAIL"),
            configuration?["LegalPilot:Bootstrap:AdminEmail"]) ?? (production ? null : "admin@legalpilot.ec");
        var adminPassword = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD"),
            configuration?["LegalPilot:Bootstrap:AdminPassword"]) ?? (production ? null : "LegalPilot#2026");

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            throw new InvalidOperationException("Base vacia en produccion: configure LEGALPILOT_BOOTSTRAP_ADMIN_EMAIL y LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD para crear el primer usuario.");
        }

        if (adminPassword.Length < 10)
        {
            throw new InvalidOperationException("LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD debe tener al menos 10 caracteres.");
        }

        var tenantId = Guid.Parse("a3eb2579-63c9-4e34-9f13-d9f5f67ad001");
        var adminId = Guid.Parse("91b2fd19-2334-403a-a77a-2b907e8ad001");
        var lawyerId = Guid.Parse("91b2fd19-2334-403a-a77a-2b907e8ad002");
        var clientId = Guid.Parse("91b2fd19-2334-403a-a77a-2b907e8ad003");
        var caseId = Guid.Parse("b86d4f8b-93bd-48d5-beca-26b9528ad001");
        var (adminHash, adminSalt) = hasher.HashPassword(adminPassword);

        store.Tenants.Add(new Tenant(tenantId, tenantName, now));
        store.Users.Add(new UserAccount(adminId, tenantId, adminEmail, "Admin LegalPilot", adminHash, adminSalt, [UserRole.SuperAdmin, UserRole.Lawyer], false, now));

        if (!production)
        {
            var lawyerPassword = FirstConfigured(
                Environment.GetEnvironmentVariable("LEGALPILOT_BOOTSTRAP_LAWYER_PASSWORD"),
                configuration?["LegalPilot:Bootstrap:LawyerPassword"]) ?? "Abogado#2026";
            var (lawyerHash, lawyerSalt) = hasher.HashPassword(lawyerPassword);
            store.Users.Add(new UserAccount(lawyerId, tenantId, "abogado@legalpilot.ec", "Abogado Demo", lawyerHash, lawyerSalt, [UserRole.Lawyer], false, now));
            store.Clients.Add(new ClientProfile(clientId, tenantId, "Cliente Demo", "cliente@example.com", "+593999000111", "0999999999", now));
            store.Cases.Add(new LegalCase(caseId, tenantId, "Juicio de cobro - Cliente Demo", "17230-2026-00001", "Civil", "Unidad Judicial Civil de Quito", clientId, lawyerId, "Activo", now, now));
        }

        foreach (var holiday in EcuadorHolidaySeed.National2026(tenantId))
        {
            store.Holidays.Add(holiday);
        }

        store.WhatsAppTemplates.Add(new WhatsAppTemplate(
            Guid.NewGuid(),
            tenantId,
            "recordatorio-audiencia",
            "Estimado/a {{cliente}}, le recordamos su audiencia del caso {{caso}} el {{fecha}} a las {{hora}}. Por favor confirme recepcion.",
            true,
            true,
            now));

        store.WhatsAppTemplates.Add(new WhatsAppTemplate(
            Guid.NewGuid(),
            tenantId,
            "documentos-pendientes",
            "Estimado/a {{cliente}}, necesitamos que remita los documentos pendientes del caso {{caso}}. Si tiene dudas, un abogado del estudio le contactara.",
            true,
            true,
            now));

        store.Audit(tenantId, adminId, AuditAction.Create, nameof(Tenant), tenantId.ToString(), production ? "Tenant inicializado por bootstrap productivo." : "Tenant demo inicializado.");
    }

    private static string? FirstConfigured(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}

public static class EcuadorHolidaySeed
{
    public static IEnumerable<Holiday> National2026(Guid tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        var source = "Seed configurable 2026. Verificar contra fuente oficial vigente antes de uso productivo.";

        yield return Holiday("2026-01-01", "Ano Nuevo", source);
        yield return Holiday("2026-01-02", "Descanso obligatorio Ano Nuevo 2026", source);
        yield return Holiday("2026-02-16", "Carnaval", source);
        yield return Holiday("2026-02-17", "Carnaval", source);
        yield return Holiday("2026-04-03", "Viernes Santo", source);
        yield return Holiday("2026-05-01", "Dia del Trabajo", source);
        yield return Holiday("2026-05-25", "Batalla de Pichincha trasladada", source);
        yield return Holiday("2026-08-10", "Primer Grito de Independencia", source);
        yield return Holiday("2026-10-09", "Independencia de Guayaquil", source);
        yield return Holiday("2026-11-02", "Dia de Difuntos", source);
        yield return Holiday("2026-11-03", "Independencia de Cuenca", source);
        yield return Holiday("2026-12-25", "Navidad", source);

        Holiday Holiday(string date, string name, string holidaySource)
        {
            return new Holiday(
                Guid.NewGuid(),
                tenantId,
                DateOnly.Parse(date),
                name,
                HolidayScope.National,
                null,
                null,
                holidaySource,
                false,
                now);
        }
    }
}
