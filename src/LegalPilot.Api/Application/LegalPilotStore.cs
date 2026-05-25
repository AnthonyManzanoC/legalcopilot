using LegalPilot.Api.Domain;
using LegalPilot.Api.Infrastructure;
using Npgsql;
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
    public List<MailWebhookEvent> MailWebhookEvents { get; } = [];
    public List<MailProcessingLog> MailProcessingLogs { get; } = [];
    public List<Holiday> Holidays { get; } = [];
    public List<Deadline> Deadlines { get; } = [];
    public List<CalendarEvent> CalendarEvents { get; } = [];
    public List<CalendarSyncLog> CalendarSyncLogs { get; } = [];
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

        var migrateLocalJson = string.Equals(configuration["LegalPilot:Storage:MigrateLocalJson"], "true", StringComparison.OrdinalIgnoreCase);

        _persistence = string.IsNullOrWhiteSpace(postgresConnection)
            ? new JsonFileLegalPilotPersistence(dataFile, _jsonOptions, logger)
            : new PostgresLegalPilotPersistence(postgresConnection, dataFile, _jsonOptions, logger, migrateLocalJson);

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
        var entry = new AuditEntry(
            Guid.NewGuid(),
            tenantId,
            actorUserId,
            action,
            entityType,
            entityId,
            summary,
            metadata ?? new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);

        lock (_gate)
        {
            AuditEntries.Insert(0, entry);
            if (_persistence is ILegalPilotIncrementalPersistence incremental)
            {
                incremental.PersistAuditEntry(entry);
                return;
            }

            Persist();
        }
    }

    public void AddRefreshTokenSession(RefreshTokenSession session)
    {
        lock (_gate)
        {
            RefreshTokenSessions.Add(session);
            if (_persistence is ILegalPilotIncrementalPersistence incremental)
            {
                incremental.PersistRefreshTokenSession(session);
                return;
            }

            Persist();
        }
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
            MailWebhookEvents.AddRange(snapshot.MailWebhookEvents ?? []);
            MailProcessingLogs.AddRange(snapshot.MailProcessingLogs ?? []);
            Holidays.AddRange(snapshot.Holidays ?? []);
            Deadlines.AddRange(snapshot.Deadlines ?? []);
            CalendarEvents.AddRange(snapshot.CalendarEvents ?? []);
            CalendarSyncLogs.AddRange(snapshot.CalendarSyncLogs ?? []);
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
            5,
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
            MailWebhookEvents,
            MailProcessingLogs,
            Holidays,
            Deadlines,
            CalendarEvents,
            CalendarSyncLogs,
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
    List<MailWebhookEvent>? MailWebhookEvents,
    List<MailProcessingLog>? MailProcessingLogs,
    List<Holiday>? Holidays,
    List<Deadline>? Deadlines,
    List<CalendarEvent>? CalendarEvents,
    List<CalendarSyncLog>? CalendarSyncLogs,
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

public interface ILegalPilotIncrementalPersistence
{
    void PersistAuditEntry(AuditEntry entry);
    void PersistRefreshTokenSession(RefreshTokenSession session);
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
            EnsureConfiguredBootstrap(store, hasher, configuration, environment);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var production = environment?.IsProduction() == true;
        var tenantName = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_BOOTSTRAP_TENANT_NAME"),
            configuration?["LegalPilot:Bootstrap:TenantName"]) ?? "LegalPilot Ecuador";
        var adminEmail = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_BOOTSTRAP_ADMIN_EMAIL"),
            configuration?["LegalPilot:Bootstrap:AdminEmail"]);
        var adminPassword = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD"),
            configuration?["LegalPilot:Bootstrap:AdminPassword"]);
        var superAdminEmail = ResolveSuperAdminEmail(configuration) ?? adminEmail;

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            if (production)
            {
                throw new InvalidOperationException("Base vacia en produccion: configure LEGALPILOT_BOOTSTRAP_ADMIN_EMAIL y LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD para crear el primer usuario.");
            }

            return;
        }

        if (adminPassword.Length < 10)
        {
            throw new InvalidOperationException("LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD debe tener al menos 10 caracteres.");
        }

        var tenantId = Guid.Parse("a3eb2579-63c9-4e34-9f13-d9f5f67ad001");
        var adminId = Guid.Parse("91b2fd19-2334-403a-a77a-2b907e8ad001");
        var (adminHash, adminSalt) = hasher.HashPassword(adminPassword);

        store.Tenants.Add(new Tenant(tenantId, tenantName, now));
        var adminRoles = BuildBootstrapRoles(adminEmail, superAdminEmail);
        store.Users.Add(new UserAccount(adminId, tenantId, adminEmail, "Admin LegalPilot", adminHash, adminSalt, adminRoles, false, now));

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

        store.Audit(tenantId, adminId, AuditAction.Create, nameof(Tenant), tenantId.ToString(), "Tenant inicializado por bootstrap configurado.");
    }

    private static void EnsureConfiguredBootstrap(LegalPilotStore store, PasswordHasher hasher, IConfiguration? configuration, IWebHostEnvironment? environment)
    {
        var tenantName = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_BOOTSTRAP_TENANT_NAME"),
            configuration?["LegalPilot:Bootstrap:TenantName"]);
        var adminEmail = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_BOOTSTRAP_ADMIN_EMAIL"),
            configuration?["LegalPilot:Bootstrap:AdminEmail"]);
        var adminPassword = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD"),
            configuration?["LegalPilot:Bootstrap:AdminPassword"]);
        var superAdminEmail = ResolveSuperAdminEmail(configuration) ?? adminEmail;

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            return;
        }

        if (adminPassword.Length < 10)
        {
            throw new InvalidOperationException("LEGALPILOT_BOOTSTRAP_ADMIN_PASSWORD debe tener al menos 10 caracteres.");
        }

        var tenant = store.Tenants.First();
        if (!string.IsNullOrWhiteSpace(tenantName) &&
            !tenant.Name.Equals(tenantName, StringComparison.Ordinal))
        {
            var tenantIndex = store.Tenants.FindIndex(t => t.Id == tenant.Id);
            tenant = tenant with { Name = tenantName };
            store.Tenants[tenantIndex] = tenant;
        }

        var now = DateTimeOffset.UtcNow;
        var (adminHash, adminSalt) = hasher.HashPassword(adminPassword);
        if (store.PersistenceProvider == "postgresql")
        {
            var admin = UpsertBootstrapAdminInMemory(store, tenant.Id, adminEmail, adminHash, adminSalt, now, superAdminEmail);
            var postgresPruned = RemoveKnownDemoRows(store, tenant.Id, adminEmail);
            RepairTenantReferences(store, tenant.Id, admin.Id);
            var audit = new AuditEntry(
                Guid.NewGuid(),
                tenant.Id,
                null,
                AuditAction.SecurityEvent,
                nameof(UserAccount),
                adminEmail,
                postgresPruned
                    ? "Bootstrap real aplicado y datos demo/QA removidos de PostgreSQL."
                    : "Bootstrap real aplicado sobre PostgreSQL existente.",
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow);
            store.AuditEntries.Insert(0, audit);
            PersistPostgresBootstrap(configuration, tenant, admin, audit);
            return;
        }

        var demoRemoved = false;
        store.Write(() =>
        {
            var admin = UpsertBootstrapAdminInMemory(store, tenant.Id, adminEmail, adminHash, adminSalt, now, superAdminEmail);
            demoRemoved = RemoveKnownDemoRows(store, tenant.Id, adminEmail);
            RepairTenantReferences(store, tenant.Id, admin.Id);
            store.AuditEntries.Insert(0, new AuditEntry(
                Guid.NewGuid(),
                tenant.Id,
                null,
                AuditAction.SecurityEvent,
                nameof(UserAccount),
                adminEmail,
                demoRemoved
                    ? "Bootstrap real aplicado y datos demo conocidos removidos."
                    : "Bootstrap real aplicado sobre store existente.",
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow));
        });
    }

    private static UserAccount UpsertBootstrapAdminInMemory(LegalPilotStore store, Guid tenantId, string adminEmail, string adminHash, string adminSalt, DateTimeOffset now, string? superAdminEmail)
    {
        var existingIndex = store.Users.FindIndex(u => u.Email.Equals(adminEmail, StringComparison.OrdinalIgnoreCase));
        var roles = BuildBootstrapRoles(adminEmail, superAdminEmail);
        if (existingIndex >= 0)
        {
            var existing = store.Users[existingIndex];
            var updated = existing with
            {
                TenantId = tenantId,
                PasswordHash = adminHash,
                PasswordSalt = adminSalt,
                Roles = roles,
                IsActive = true
            };
            store.Users[existingIndex] = updated;
            return updated;
        }

        var admin = new UserAccount(
            Guid.NewGuid(),
            tenantId,
            adminEmail,
            "Admin LegalPilot",
            adminHash,
            adminSalt,
            roles,
            false,
            now);
        store.Users.Add(admin);
        return admin;
    }

    private static IReadOnlyList<UserRole> BuildBootstrapRoles(string adminEmail, string? superAdminEmail)
    {
        var roles = new List<UserRole> { UserRole.Admin, UserRole.Lawyer };
        if (!string.IsNullOrWhiteSpace(superAdminEmail) &&
            adminEmail.Equals(superAdminEmail, StringComparison.OrdinalIgnoreCase))
        {
            roles.Insert(0, UserRole.SuperAdmin);
        }

        return roles;
    }

    private static string? ResolveSuperAdminEmail(IConfiguration? configuration)
    {
        return FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_SUPERADMIN_EMAIL"),
            configuration?["LegalPilot:SuperAdmin:Email"],
            Environment.GetEnvironmentVariable("LEGALPILOT_BOOTSTRAP_ADMIN_EMAIL"),
            configuration?["LegalPilot:Bootstrap:AdminEmail"]);
    }

    private static void PersistPostgresBootstrap(IConfiguration? configuration, Tenant tenant, UserAccount admin, AuditEntry audit)
    {
        var rawConnection = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_DATABASE_URL"),
            configuration?.GetConnectionString("LegalPilotPostgres"),
            Environment.GetEnvironmentVariable("DATABASE_URL"));
        if (string.IsNullOrWhiteSpace(rawConnection))
        {
            return;
        }

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        using var connection = new NpgsqlConnection(NormalizePostgresConnectionString(rawConnection));
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var command = new NpgsqlCommand("""
                INSERT INTO legalpilot_tenants
                    (id, tenant_id, created_at, updated_at, status, name, payload)
                VALUES
                    (@id, @tenant_id, @created_at, NULL, @status, @name, CAST(@payload AS jsonb))
                ON CONFLICT (id) DO UPDATE SET
                    tenant_id = EXCLUDED.tenant_id,
                    updated_at = now(),
                    status = EXCLUDED.status,
                    name = EXCLUDED.name,
                    payload = EXCLUDED.payload;
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("id", tenant.Id);
                command.Parameters.AddWithValue("tenant_id", tenant.Id);
                command.Parameters.AddWithValue("created_at", tenant.CreatedAt.ToUniversalTime());
                command.Parameters.AddWithValue("status", tenant.IsActive ? "Active" : "Inactive");
                command.Parameters.AddWithValue("name", tenant.Name);
                command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(tenant, jsonOptions));
                command.ExecuteNonQuery();
            }

            using (var command = new NpgsqlCommand("""
                INSERT INTO legalpilot_users
                    (id, tenant_id, created_at, updated_at, user_id, email, status, name, payload)
                VALUES
                    (@id, @tenant_id, @created_at, NULL, @user_id, @email, @status, @name, CAST(@payload AS jsonb))
                ON CONFLICT (id) DO UPDATE SET
                    tenant_id = EXCLUDED.tenant_id,
                    updated_at = now(),
                    user_id = EXCLUDED.user_id,
                    email = EXCLUDED.email,
                    status = EXCLUDED.status,
                    name = EXCLUDED.name,
                    payload = EXCLUDED.payload;
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("id", admin.Id);
                command.Parameters.AddWithValue("tenant_id", admin.TenantId);
                command.Parameters.AddWithValue("created_at", admin.CreatedAt.ToUniversalTime());
                command.Parameters.AddWithValue("user_id", admin.Id);
                command.Parameters.AddWithValue("email", admin.Email);
                command.Parameters.AddWithValue("status", admin.IsActive ? "Active" : "Inactive");
                command.Parameters.AddWithValue("name", admin.DisplayName);
                command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(admin, jsonOptions));
                command.ExecuteNonQuery();
            }

            using (var command = new NpgsqlCommand("""
                INSERT INTO legalpilot_audit_entries
                    (id, tenant_id, created_at, updated_at, user_id, external_id, status, name, payload)
                VALUES
                    (@id, @tenant_id, @created_at, NULL, NULL, @external_id, @status, @name, CAST(@payload AS jsonb))
                ON CONFLICT (id) DO NOTHING;
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("id", audit.Id);
                command.Parameters.AddWithValue("tenant_id", audit.TenantId);
                command.Parameters.AddWithValue("created_at", audit.CreatedAt.ToUniversalTime());
                command.Parameters.AddWithValue("external_id", audit.EntityId);
                command.Parameters.AddWithValue("status", audit.Action.ToString());
                command.Parameters.AddWithValue("name", audit.EntityType);
                command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(audit, jsonOptions));
                command.ExecuteNonQuery();
            }

            PrunePostgresDeadData(connection, transaction, tenant.Id, admin.Id, admin.Email);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void PrunePostgresDeadData(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId, Guid adminId, string adminEmail)
    {
        using var command = new NpgsqlCommand("""
            CREATE TEMP TABLE lp_dead_users ON COMMIT DROP AS
            SELECT id
            FROM legalpilot_users
            WHERE tenant_id = @tenant_id
              AND lower(coalesce(email, '') || ' ' || payload::text) LIKE ANY (ARRAY[
                '%abogado@legalpilot.ec%',
                '%admin@legalpilot.ec%',
                '%cliente@example.com%',
                '%cliente demo%',
                '%abogado demo%'
              ])
              AND lower(coalesce(email, '')) <> lower(@admin_email);

            CREATE TEMP TABLE lp_dead_clients ON COMMIT DROP AS
            SELECT id
            FROM legalpilot_clients
            WHERE tenant_id = @tenant_id
              AND lower(coalesce(name, '') || ' ' || coalesce(email, '') || ' ' || coalesce(external_id, '') || ' ' || payload::text) LIKE ANY (ARRAY[
                '%cliente demo%',
                '%cliente@example.com%',
                '%0999999999%'
              ]);

            CREATE TEMP TABLE lp_dead_cases ON COMMIT DROP AS
            SELECT id
            FROM legalpilot_cases
            WHERE tenant_id = @tenant_id
              AND (
                client_id IN (SELECT id FROM lp_dead_clients)
                OR lower(coalesce(name, '') || ' ' || coalesce(case_number, '') || ' ' || payload::text) LIKE ANY (ARRAY[
                  '%cliente demo%',
                  '%17230-2026-00001%',
                  '%qa supabase%',
                  '%providencia qa%'
                ])
              );

            CREATE TEMP TABLE lp_dead_emails ON COMMIT DROP AS
            SELECT id
            FROM legalpilot_emails
            WHERE tenant_id = @tenant_id
              AND (
                case_id IN (SELECT id FROM lp_dead_cases)
                OR lower(coalesce(name, '') || ' ' || coalesce(email, '') || ' ' || coalesce(external_id, '') || ' ' || payload::text) LIKE ANY (ARRAY[
                  '%cliente demo%',
                  '%qa supabase%',
                  '%providencia qa%',
                  '%17230-2026-00001%'
                ])
              );

            CREATE TEMP TABLE lp_dead_deadlines ON COMMIT DROP AS
            SELECT id
            FROM legalpilot_deadlines
            WHERE tenant_id = @tenant_id
              AND (
                case_id IN (SELECT id FROM lp_dead_cases)
                OR legal_email_id IN (SELECT id FROM lp_dead_emails)
                OR lower(coalesce(name, '') || ' ' || payload::text) LIKE ANY (ARRAY[
                  '%cliente demo%',
                  '%qa supabase%',
                  '%providencia qa%',
                  '%17230-2026-00001%'
                ])
              );

            CREATE TEMP TABLE lp_dead_events ON COMMIT DROP AS
            SELECT id
            FROM legalpilot_calendar_events
            WHERE tenant_id = @tenant_id
              AND (
                case_id IN (SELECT id FROM lp_dead_cases)
                OR deadline_id IN (SELECT id FROM lp_dead_deadlines)
                OR lower(coalesce(name, '') || ' ' || coalesce(external_id, '') || ' ' || payload::text) LIKE ANY (ARRAY[
                  '%cliente demo%',
                  '%qa supabase%',
                  '%providencia qa%',
                  '%17230-2026-00001%'
                ])
              );

            CREATE TEMP TABLE lp_dead_ai_runs ON COMMIT DROP AS
            SELECT id
            FROM legalpilot_ai_processing_runs
            WHERE tenant_id = @tenant_id
              AND (
                legal_email_id IN (SELECT id FROM lp_dead_emails)
                OR lower(coalesce(name, '') || ' ' || coalesce(external_id, '') || ' ' || payload::text) LIKE ANY (ARRAY[
                  '%cliente demo%',
                  '%qa supabase%',
                  '%providencia qa%',
                  '%17230-2026-00001%'
                ])
              );

            UPDATE legalpilot_mailboxes
            SET user_id = @admin_id,
                updated_at = now(),
                payload = jsonb_set(payload, '{ownerUserId}', to_jsonb(CAST(@admin_id AS text)), false)
            WHERE tenant_id = @tenant_id
              AND user_id IN (SELECT id FROM lp_dead_users)
              AND NOT (lower(coalesce(email, '') || ' ' || coalesce(name, '') || ' ' || payload::text) LIKE ANY (ARRAY[
                '%cliente demo%',
                '%qa supabase%',
                '%providencia qa%',
                '%abogado@legalpilot.ec%',
                '%admin@legalpilot.ec%'
              ]));

            UPDATE legalpilot_cases
            SET user_id = @admin_id,
                updated_at = now(),
                payload = jsonb_set(payload, '{responsibleUserId}', to_jsonb(CAST(@admin_id AS text)), false)
            WHERE tenant_id = @tenant_id
              AND user_id IN (SELECT id FROM lp_dead_users)
              AND id NOT IN (SELECT id FROM lp_dead_cases);

            UPDATE legalpilot_deadlines
            SET user_id = @admin_id,
                updated_at = now(),
                payload = jsonb_set(payload, '{responsibleUserId}', to_jsonb(CAST(@admin_id AS text)), false)
            WHERE tenant_id = @tenant_id
              AND user_id IN (SELECT id FROM lp_dead_users)
              AND id NOT IN (SELECT id FROM lp_dead_deadlines);

            UPDATE legalpilot_calendar_events
            SET user_id = @admin_id,
                updated_at = now(),
                payload = jsonb_set(payload, '{responsibleUserId}', to_jsonb(CAST(@admin_id AS text)), false)
            WHERE tenant_id = @tenant_id
              AND user_id IN (SELECT id FROM lp_dead_users)
              AND id NOT IN (SELECT id FROM lp_dead_events);

            DELETE FROM legalpilot_calendar_sync_logs
            WHERE tenant_id = @tenant_id
              AND (calendar_event_id IN (SELECT id FROM lp_dead_events)
                   OR lower(coalesce(name, '') || ' ' || coalesce(external_id, '') || ' ' || payload::text) LIKE ANY (ARRAY['%qa supabase%', '%providencia qa%', '%cliente demo%', '%17230-2026-00001%']));

            DELETE FROM legalpilot_reminders
            WHERE tenant_id = @tenant_id
              AND (calendar_event_id IN (SELECT id FROM lp_dead_events)
                   OR lower(coalesce(name, '') || ' ' || payload::text) LIKE ANY (ARRAY['%qa supabase%', '%providencia qa%', '%cliente demo%', '%17230-2026-00001%']));

            DELETE FROM legalpilot_notifications
            WHERE tenant_id = @tenant_id
              AND (user_id IN (SELECT id FROM lp_dead_users)
                   OR lower(coalesce(name, '') || ' ' || payload::text) LIKE ANY (ARRAY['%qa supabase%', '%providencia qa%', '%cliente demo%', '%17230-2026-00001%', '%abogado demo%']));

            DELETE FROM legalpilot_whatsapp_messages
            WHERE tenant_id = @tenant_id
              AND (client_id IN (SELECT id FROM lp_dead_clients)
                   OR case_id IN (SELECT id FROM lp_dead_cases)
                   OR lower(coalesce(name, '') || ' ' || coalesce(external_id, '') || ' ' || payload::text) LIKE ANY (ARRAY['%qa supabase%', '%providencia qa%', '%cliente demo%', '%17230-2026-00001%']));

            DELETE FROM legalpilot_chat_messages
            WHERE tenant_id = @tenant_id
              AND (client_id IN (SELECT id FROM lp_dead_clients)
                   OR case_id IN (SELECT id FROM lp_dead_cases)
                   OR user_id IN (SELECT id FROM lp_dead_users)
                   OR lower(coalesce(name, '') || ' ' || payload::text) LIKE ANY (ARRAY['%qa supabase%', '%providencia qa%', '%cliente demo%', '%17230-2026-00001%', '%abogado demo%']));

            DELETE FROM legalpilot_ai_feedback_entries
            WHERE tenant_id = @tenant_id
              AND (user_id IN (SELECT id FROM lp_dead_users)
                   OR external_id IN (SELECT id::text FROM lp_dead_ai_runs)
                   OR lower(payload::text) LIKE ANY (ARRAY['%qa supabase%', '%providencia qa%', '%cliente demo%', '%17230-2026-00001%']));

            DELETE FROM legalpilot_ai_processing_runs
            WHERE tenant_id = @tenant_id
              AND id IN (SELECT id FROM lp_dead_ai_runs);

            DELETE FROM legalpilot_calendar_events
            WHERE tenant_id = @tenant_id
              AND id IN (SELECT id FROM lp_dead_events);

            DELETE FROM legalpilot_deadlines
            WHERE tenant_id = @tenant_id
              AND id IN (SELECT id FROM lp_dead_deadlines);

            DELETE FROM legalpilot_mail_processing_logs
            WHERE tenant_id = @tenant_id
              AND (mailbox_connection_id IN (
                    SELECT id FROM legalpilot_mailboxes
                    WHERE tenant_id = @tenant_id
                      AND lower(coalesce(email, '') || ' ' || coalesce(name, '') || ' ' || payload::text) LIKE ANY (ARRAY['%abogado@legalpilot.ec%', '%admin@legalpilot.ec%', '%cliente demo%', '%qa supabase%', '%providencia qa%'])
                  )
                  OR legal_email_id IN (SELECT id FROM lp_dead_emails)
                  OR lower(coalesce(name, '') || ' ' || payload::text) LIKE ANY (ARRAY['%qa supabase%', '%providencia qa%', '%cliente demo%', '%17230-2026-00001%']));

            DELETE FROM legalpilot_mail_webhook_events
            WHERE tenant_id = @tenant_id
              AND lower(coalesce(name, '') || ' ' || coalesce(external_id, '') || ' ' || payload::text) LIKE ANY (ARRAY['%qa supabase%', '%providencia qa%', '%cliente demo%', '%17230-2026-00001%']);

            DELETE FROM legalpilot_attachments
            WHERE tenant_id = @tenant_id
              AND (legal_email_id IN (SELECT id FROM lp_dead_emails)
                   OR lower(coalesce(name, '') || ' ' || coalesce(external_id, '') || ' ' || payload::text) LIKE ANY (ARRAY['%qa supabase%', '%providencia qa%', '%cliente demo%', '%17230-2026-00001%']));

            DELETE FROM legalpilot_emails
            WHERE tenant_id = @tenant_id
              AND id IN (SELECT id FROM lp_dead_emails);

            DELETE FROM legalpilot_cases
            WHERE tenant_id = @tenant_id
              AND id IN (SELECT id FROM lp_dead_cases);

            DELETE FROM legalpilot_clients
            WHERE tenant_id = @tenant_id
              AND id IN (SELECT id FROM lp_dead_clients);

            DELETE FROM legalpilot_mailbox_sync_states
            WHERE tenant_id = @tenant_id
              AND mailbox_connection_id IN (
                SELECT id FROM legalpilot_mailboxes
                WHERE tenant_id = @tenant_id
                  AND lower(coalesce(email, '') || ' ' || coalesce(name, '') || ' ' || payload::text) LIKE ANY (ARRAY['%abogado@legalpilot.ec%', '%admin@legalpilot.ec%', '%cliente demo%', '%qa supabase%', '%providencia qa%'])
              );

            DELETE FROM legalpilot_oauth_token_credentials
            WHERE tenant_id = @tenant_id
              AND mailbox_connection_id IN (
                SELECT id FROM legalpilot_mailboxes
                WHERE tenant_id = @tenant_id
                  AND lower(coalesce(email, '') || ' ' || coalesce(name, '') || ' ' || payload::text) LIKE ANY (ARRAY['%abogado@legalpilot.ec%', '%admin@legalpilot.ec%', '%cliente demo%', '%qa supabase%', '%providencia qa%'])
              );

            DELETE FROM legalpilot_mailboxes
            WHERE tenant_id = @tenant_id
              AND lower(coalesce(email, '') || ' ' || coalesce(name, '') || ' ' || payload::text) LIKE ANY (ARRAY['%abogado@legalpilot.ec%', '%admin@legalpilot.ec%', '%cliente demo%', '%qa supabase%', '%providencia qa%']);

            DELETE FROM legalpilot_password_reset_tickets
            WHERE tenant_id = @tenant_id
              AND user_id IN (SELECT id FROM lp_dead_users);

            DELETE FROM legalpilot_refresh_token_sessions
            WHERE tenant_id = @tenant_id
              AND user_id IN (SELECT id FROM lp_dead_users);

            DELETE FROM legalpilot_audit_entries
            WHERE tenant_id = @tenant_id
              AND (user_id IN (SELECT id FROM lp_dead_users)
                   OR external_id IN (SELECT id::text FROM lp_dead_users)
                   OR external_id IN (SELECT id::text FROM lp_dead_clients)
                   OR external_id IN (SELECT id::text FROM lp_dead_cases)
                   OR external_id IN (SELECT id::text FROM lp_dead_emails)
                   OR external_id IN (SELECT id::text FROM lp_dead_deadlines)
                   OR external_id IN (SELECT id::text FROM lp_dead_events)
                   OR lower(coalesce(name, '') || ' ' || coalesce(external_id, '') || ' ' || payload::text) LIKE ANY (ARRAY['%qa supabase%', '%providencia qa%', '%cliente demo%', '%abogado demo%', '%17230-2026-00001%', '%abogado@legalpilot.ec%', '%admin@legalpilot.ec%']));

            DELETE FROM legalpilot_users
            WHERE tenant_id = @tenant_id
              AND id IN (SELECT id FROM lp_dead_users);
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("admin_id", adminId);
        command.Parameters.AddWithValue("admin_email", adminEmail);
        command.ExecuteNonQuery();
    }

    private static string NormalizePostgresConnectionString(string raw)
    {
        if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(raw);
            var userInfo = uri.UserInfo.Split(':', 2);
            return new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 5432,
                Database = uri.AbsolutePath.Trim('/'),
                Username = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(0) ?? string.Empty),
                Password = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(1) ?? string.Empty),
                SslMode = SslMode.Require,
                Pooling = true,
                Timeout = 15,
                CommandTimeout = 30,
                IncludeErrorDetail = false
            }.ConnectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder(raw);
        if (builder.SslMode is SslMode.Disable or SslMode.Prefer)
        {
            builder.SslMode = SslMode.Require;
        }

        builder.Pooling = true;
        builder.IncludeErrorDetail = false;
        return builder.ConnectionString;
    }

    private static bool RemoveKnownDemoRows(LegalPilotStore store, Guid tenantId, string adminEmail)
    {
        var demoUserEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "abogado@legalpilot.ec"
        };
        if (!adminEmail.Equals("admin@legalpilot.ec", StringComparison.OrdinalIgnoreCase))
        {
            demoUserEmails.Add("admin@legalpilot.ec");
        }

        var demoClientIds = store.Clients
            .Where(c => c.TenantId == tenantId &&
                        (c.FullName.Equals("Cliente Demo", StringComparison.OrdinalIgnoreCase) ||
                         c.Email.Equals("cliente@example.com", StringComparison.OrdinalIgnoreCase) ||
                         c.Identification.Equals("0999999999", StringComparison.OrdinalIgnoreCase) ||
                         LooksLikeDeadData(c.FullName, c.Email, c.Phone, c.Identification)))
            .Select(c => c.Id)
            .ToHashSet();
        var demoCaseIds = store.Cases
            .Where(c => c.TenantId == tenantId &&
                        (c.Title.Contains("Cliente Demo", StringComparison.OrdinalIgnoreCase) ||
                         c.CaseNumber.Equals("17230-2026-00001", StringComparison.OrdinalIgnoreCase) ||
                         LooksLikeDeadData(c.Title, c.CaseNumber, c.Matter, c.CourtOrOffice) ||
                         (c.ClientId.HasValue && demoClientIds.Contains(c.ClientId.Value))))
            .Select(c => c.Id)
            .ToHashSet();
        var demoEmailIds = store.Emails
            .Where(e => e.TenantId == tenantId &&
                        ((e.CaseId.HasValue && demoCaseIds.Contains(e.CaseId.Value)) ||
                         LooksLikeDeadData(e.Subject, e.Sender, e.BodyText, e.RawReference, e.ExternalMessageId, e.Extraction?.LawyerSummary, e.Extraction?.SuggestedDraft)))
            .Select(e => e.Id)
            .ToHashSet();
        var demoDeadlineIds = store.Deadlines
            .Where(d => d.TenantId == tenantId &&
                        ((d.CaseId.HasValue && demoCaseIds.Contains(d.CaseId.Value)) ||
                         (d.LegalEmailId.HasValue && demoEmailIds.Contains(d.LegalEmailId.Value)) ||
                         LooksLikeDeadData(d.Title, d.Calculation.Explanation)))
            .Select(d => d.Id)
            .ToHashSet();
        var demoEventIds = store.CalendarEvents
            .Where(e => e.TenantId == tenantId &&
                        ((e.CaseId.HasValue && demoCaseIds.Contains(e.CaseId.Value)) ||
                         (e.DeadlineId.HasValue && demoDeadlineIds.Contains(e.DeadlineId.Value)) ||
                         LooksLikeDeadData(e.Title, e.Location, e.Description, e.SyncError, e.ExternalEventId)))
            .Select(e => e.Id)
            .ToHashSet();
        var demoAiRunIds = store.AiProcessingRuns
            .Where(r => r.TenantId == tenantId &&
                        ((r.LegalEmailId.HasValue && demoEmailIds.Contains(r.LegalEmailId.Value)) ||
                         LooksLikeDeadData(r.OutputJson, r.InputHash, r.Purpose)))
            .Select(r => r.Id)
            .ToHashSet();
        var before = CountDomainRows(store);

        store.Users.RemoveAll(u => u.TenantId == tenantId && demoUserEmails.Contains(u.Email));
        store.Clients.RemoveAll(c => c.TenantId == tenantId && demoClientIds.Contains(c.Id));
        store.Cases.RemoveAll(c => c.TenantId == tenantId && demoCaseIds.Contains(c.Id));
        store.Emails.RemoveAll(e => e.TenantId == tenantId && demoEmailIds.Contains(e.Id));
        store.Attachments.RemoveAll(a => a.TenantId == tenantId && demoEmailIds.Contains(a.LegalEmailId));
        store.MailProcessingLogs.RemoveAll(l => l.TenantId == tenantId && l.LegalEmailId.HasValue && demoEmailIds.Contains(l.LegalEmailId.Value));
        store.MailWebhookEvents.RemoveAll(e => e.TenantId == tenantId && LooksLikeDeadData(e.Message, e.ExternalEventId));
        store.Deadlines.RemoveAll(d => d.TenantId == tenantId && demoDeadlineIds.Contains(d.Id));
        store.CalendarEvents.RemoveAll(e => e.TenantId == tenantId && demoEventIds.Contains(e.Id));
        store.CalendarSyncLogs.RemoveAll(l => l.TenantId == tenantId && demoEventIds.Contains(l.CalendarEventId));
        store.Reminders.RemoveAll(r => r.TenantId == tenantId && (demoEventIds.Contains(r.CalendarEventId) || LooksLikeDeadData(r.Message)));
        store.Notifications.RemoveAll(n => n.TenantId == tenantId && LooksLikeDeadData(n.Title, n.Message));
        store.WhatsAppMessages.RemoveAll(m => m.TenantId == tenantId &&
                                             ((m.ClientId.HasValue && demoClientIds.Contains(m.ClientId.Value)) ||
                                              (m.CaseId.HasValue && demoCaseIds.Contains(m.CaseId.Value)) ||
                                              LooksLikeDeadData(m.Body, m.To)));
        store.ChatMessages.RemoveAll(m => m.TenantId == tenantId &&
                                          ((m.ClientId.HasValue && demoClientIds.Contains(m.ClientId.Value)) ||
                                           (m.CaseId.HasValue && demoCaseIds.Contains(m.CaseId.Value)) ||
                                           LooksLikeDeadData(m.Body, m.AuthorName)));
        store.PasswordResetTickets.RemoveAll(t => t.TenantId == tenantId && store.Users.All(u => u.Id != t.UserId));
        store.RefreshTokenSessions.RemoveAll(t => t.TenantId == tenantId && store.Users.All(u => u.Id != t.UserId));
        store.AiProcessingRuns.RemoveAll(r => r.TenantId == tenantId && demoAiRunIds.Contains(r.Id));
        store.AiFeedbackEntries.RemoveAll(f => f.TenantId == tenantId && ((f.AiRunId.HasValue && demoAiRunIds.Contains(f.AiRunId.Value)) || LooksLikeDeadData(f.CorrectionJson)));
        store.AuditEntries.RemoveAll(a => a.TenantId == tenantId &&
                                          (demoUserEmails.Contains(a.EntityId) ||
                                           demoClientIds.Contains(ParseGuidOrEmpty(a.EntityId)) ||
                                           demoCaseIds.Contains(ParseGuidOrEmpty(a.EntityId)) ||
                                           demoEmailIds.Contains(ParseGuidOrEmpty(a.EntityId)) ||
                                           demoDeadlineIds.Contains(ParseGuidOrEmpty(a.EntityId)) ||
                                           demoEventIds.Contains(ParseGuidOrEmpty(a.EntityId)) ||
                                           LooksLikeDeadData(a.Summary, a.EntityId, a.EntityType)));

        var after = CountDomainRows(store);
        return after < before;
    }

    private static int CountDomainRows(LegalPilotStore store)
    {
        return store.Users.Count + store.Clients.Count + store.Cases.Count + store.Emails.Count + store.Attachments.Count +
               store.MailWebhookEvents.Count + store.MailProcessingLogs.Count + store.Deadlines.Count + store.CalendarEvents.Count +
               store.CalendarSyncLogs.Count + store.Reminders.Count + store.Notifications.Count + store.WhatsAppMessages.Count +
               store.ChatMessages.Count + store.AuditEntries.Count + store.AiProcessingRuns.Count + store.AiFeedbackEntries.Count;
    }

    private static Guid ParseGuidOrEmpty(string value)
    {
        return Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;
    }

    private static bool LooksLikeDeadData(params string?[] values)
    {
        var joined = string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
        if (joined.Length == 0)
        {
            return false;
        }

        return joined.Contains("cliente demo", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("abogado demo", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("cliente@example.com", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("0999999999", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("17230-2026-00001", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("qa supabase", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("providencia qa", StringComparison.OrdinalIgnoreCase);
    }

    private static void RepairTenantReferences(LegalPilotStore store, Guid tenantId, Guid fallbackUserId)
    {
        var validUsers = store.Users.Where(u => u.TenantId == tenantId && u.IsActive).Select(u => u.Id).ToHashSet();
        if (!validUsers.Contains(fallbackUserId))
        {
            fallbackUserId = validUsers.FirstOrDefault();
        }

        var validClients = store.Clients.Where(c => c.TenantId == tenantId).Select(c => c.Id).ToHashSet();
        var validCases = store.Cases.Where(c => c.TenantId == tenantId).Select(c => c.Id).ToHashSet();
        var validEmails = store.Emails.Where(e => e.TenantId == tenantId).Select(e => e.Id).ToHashSet();
        var validDeadlines = store.Deadlines.Where(d => d.TenantId == tenantId).Select(d => d.Id).ToHashSet();

        for (var i = 0; i < store.Cases.Count; i++)
        {
            var item = store.Cases[i];
            if (item.TenantId != tenantId)
            {
                continue;
            }

            store.Cases[i] = item with
            {
                ClientId = item.ClientId.HasValue && validClients.Contains(item.ClientId.Value) ? item.ClientId : null,
                ResponsibleUserId = validUsers.Contains(item.ResponsibleUserId) ? item.ResponsibleUserId : fallbackUserId
            };
        }

        for (var i = 0; i < store.Mailboxes.Count; i++)
        {
            var item = store.Mailboxes[i];
            if (item.TenantId == tenantId && !validUsers.Contains(item.OwnerUserId))
            {
                store.Mailboxes[i] = item with { OwnerUserId = fallbackUserId };
            }
        }

        for (var i = 0; i < store.Deadlines.Count; i++)
        {
            var item = store.Deadlines[i];
            if (item.TenantId != tenantId)
            {
                continue;
            }

            store.Deadlines[i] = item with
            {
                CaseId = item.CaseId.HasValue && validCases.Contains(item.CaseId.Value) ? item.CaseId : null,
                LegalEmailId = item.LegalEmailId.HasValue && validEmails.Contains(item.LegalEmailId.Value) ? item.LegalEmailId : null,
                ResponsibleUserId = validUsers.Contains(item.ResponsibleUserId) ? item.ResponsibleUserId : fallbackUserId
            };
        }

        for (var i = 0; i < store.CalendarEvents.Count; i++)
        {
            var item = store.CalendarEvents[i];
            if (item.TenantId != tenantId)
            {
                continue;
            }

            store.CalendarEvents[i] = item with
            {
                CaseId = item.CaseId.HasValue && validCases.Contains(item.CaseId.Value) ? item.CaseId : null,
                DeadlineId = item.DeadlineId.HasValue && validDeadlines.Contains(item.DeadlineId.Value) ? item.DeadlineId : null,
                ResponsibleUserId = validUsers.Contains(item.ResponsibleUserId) ? item.ResponsibleUserId : fallbackUserId
            };
        }

        for (var i = 0; i < store.Notifications.Count; i++)
        {
            var item = store.Notifications[i];
            if (item.TenantId == tenantId && !validUsers.Contains(item.UserId))
            {
                store.Notifications[i] = item with { UserId = fallbackUserId };
            }
        }

        for (var i = 0; i < store.AiFeedbackEntries.Count; i++)
        {
            var item = store.AiFeedbackEntries[i];
            if (item.TenantId == tenantId && !validUsers.Contains(item.UserId))
            {
                store.AiFeedbackEntries[i] = item with { UserId = fallbackUserId };
            }
        }

        store.OAuthStateTickets.RemoveAll(t => t.TenantId == tenantId && !validUsers.Contains(t.UserId));
        store.PasswordResetTickets.RemoveAll(t => t.TenantId == tenantId && !validUsers.Contains(t.UserId));
        store.RefreshTokenSessions.RemoveAll(t => t.TenantId == tenantId && !validUsers.Contains(t.UserId));
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
