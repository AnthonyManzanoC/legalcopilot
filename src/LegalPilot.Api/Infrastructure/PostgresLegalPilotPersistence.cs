using System.Text.Json;
using LegalPilot.Api.Application;
using LegalPilot.Api.Domain;
using Npgsql;
using NpgsqlTypes;

namespace LegalPilot.Api.Infrastructure;

public sealed class PostgresLegalPilotPersistence : ILegalPilotPersistence
{
    private const int CurrentMigration = 2;
    private readonly string _connectionString;
    private readonly string _jsonMigrationPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger _logger;
    private readonly string _safeDataSource;

    public PostgresLegalPilotPersistence(
        string rawConnectionString,
        string jsonMigrationPath,
        JsonSerializerOptions jsonOptions,
        ILogger logger)
    {
        _connectionString = NormalizeConnectionString(rawConnectionString);
        _jsonMigrationPath = jsonMigrationPath;
        _jsonOptions = jsonOptions;
        _logger = logger;
        _safeDataSource = BuildSafeDataSource(_connectionString);
    }

    public string Provider => "postgresql";

    public string DataSource => _safeDataSource;

    public StoreSnapshot? Load()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        ApplyMigrations(connection);

        if (CountRows(connection, "legalpilot_tenants") == 0 && File.Exists(_jsonMigrationPath))
        {
            var migrated = TryLoadJsonSnapshot();
            if (migrated?.Tenants?.Count > 0)
            {
                Persist(migrated);
                _logger.LogInformation("Migrated LegalPilot JSON snapshot into PostgreSQL.");
                return migrated;
            }
        }

        return new StoreSnapshot(
            CurrentMigration,
            LoadTable<Tenant>(connection, "legalpilot_tenants"),
            LoadTable<UserAccount>(connection, "legalpilot_users"),
            LoadTable<ClientProfile>(connection, "legalpilot_clients"),
            LoadTable<LegalCase>(connection, "legalpilot_cases"),
            LoadTable<MailboxConnection>(connection, "legalpilot_mailboxes"),
            LoadTable<MailboxSyncState>(connection, "legalpilot_mailbox_sync_states", "created_at DESC NULLS LAST"),
            LoadTable<OAuthStateTicket>(connection, "legalpilot_oauth_state_tickets", "created_at DESC NULLS LAST"),
            LoadTable<OAuthTokenCredential>(connection, "legalpilot_oauth_token_credentials", "updated_at DESC NULLS LAST"),
            LoadTable<LegalEmail>(connection, "legalpilot_emails", "created_at DESC NULLS LAST"),
            LoadTable<DocumentAttachment>(connection, "legalpilot_attachments", "created_at DESC NULLS LAST"),
            LoadTable<Holiday>(connection, "legalpilot_holidays", "due_date ASC NULLS LAST"),
            LoadTable<Deadline>(connection, "legalpilot_deadlines", "due_date ASC NULLS LAST"),
            LoadTable<CalendarEvent>(connection, "legalpilot_calendar_events", "starts_at ASC NULLS LAST"),
            LoadTable<Reminder>(connection, "legalpilot_reminders", "starts_at ASC NULLS LAST"),
            LoadTable<Notification>(connection, "legalpilot_notifications", "created_at DESC NULLS LAST"),
            LoadTable<WhatsAppTemplate>(connection, "legalpilot_whatsapp_templates"),
            LoadTable<WhatsAppMessage>(connection, "legalpilot_whatsapp_messages", "created_at DESC NULLS LAST"),
            LoadTable<ChatMessage>(connection, "legalpilot_chat_messages", "created_at DESC NULLS LAST"),
            LoadTable<AuditEntry>(connection, "legalpilot_audit_entries", "created_at DESC NULLS LAST"),
            LoadTable<PasswordResetTicket>(connection, "legalpilot_password_reset_tickets", "created_at DESC NULLS LAST"),
            LoadTable<RefreshTokenSession>(connection, "legalpilot_refresh_token_sessions", "created_at DESC NULLS LAST"),
            LoadTable<AiKnowledgeDocument>(connection, "legalpilot_ai_knowledge_documents", "updated_at DESC NULLS LAST"),
            LoadTable<AiProcessingRun>(connection, "legalpilot_ai_processing_runs", "created_at DESC NULLS LAST"),
            LoadTable<AiFeedbackEntry>(connection, "legalpilot_ai_feedback_entries", "created_at DESC NULLS LAST"));
    }

    public void Persist(StoreSnapshot snapshot)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        ApplyMigrations(connection);

        using var transaction = connection.BeginTransaction();
        try
        {
            using var batch = new NpgsqlBatch(connection, transaction);
            foreach (var table in DeleteOrder)
            {
                batch.BatchCommands.Add(new NpgsqlBatchCommand($"DELETE FROM {table};"));
            }

            AppendTable(batch, "legalpilot_tenants", snapshot.Tenants ?? [], Row);
            AppendTable(batch, "legalpilot_users", snapshot.Users ?? [], Row);
            AppendTable(batch, "legalpilot_clients", snapshot.Clients ?? [], Row);
            AppendTable(batch, "legalpilot_cases", snapshot.Cases ?? [], Row);
            AppendTable(batch, "legalpilot_mailboxes", snapshot.Mailboxes ?? [], Row);
            AppendTable(batch, "legalpilot_mailbox_sync_states", snapshot.MailboxSyncStates ?? [], Row);
            AppendTable(batch, "legalpilot_oauth_state_tickets", snapshot.OAuthStateTickets ?? [], Row);
            AppendTable(batch, "legalpilot_oauth_token_credentials", snapshot.OAuthTokenCredentials ?? [], Row);
            AppendTable(batch, "legalpilot_emails", snapshot.Emails ?? [], Row);
            AppendTable(batch, "legalpilot_attachments", snapshot.Attachments ?? [], Row);
            AppendTable(batch, "legalpilot_holidays", snapshot.Holidays ?? [], Row);
            AppendTable(batch, "legalpilot_deadlines", snapshot.Deadlines ?? [], Row);
            AppendTable(batch, "legalpilot_calendar_events", snapshot.CalendarEvents ?? [], Row);
            AppendTable(batch, "legalpilot_reminders", snapshot.Reminders ?? [], Row);
            AppendTable(batch, "legalpilot_notifications", snapshot.Notifications ?? [], Row);
            AppendTable(batch, "legalpilot_whatsapp_templates", snapshot.WhatsAppTemplates ?? [], Row);
            AppendTable(batch, "legalpilot_whatsapp_messages", snapshot.WhatsAppMessages ?? [], Row);
            AppendTable(batch, "legalpilot_chat_messages", snapshot.ChatMessages ?? [], Row);
            AppendTable(batch, "legalpilot_audit_entries", snapshot.AuditEntries ?? [], Row);
            AppendTable(batch, "legalpilot_password_reset_tickets", snapshot.PasswordResetTickets ?? [], Row);
            AppendTable(batch, "legalpilot_refresh_token_sessions", snapshot.RefreshTokenSessions ?? [], Row);
            AppendTable(batch, "legalpilot_ai_knowledge_documents", snapshot.AiKnowledgeDocuments ?? [], Row);
            AppendTable(batch, "legalpilot_ai_processing_runs", snapshot.AiProcessingRuns ?? [], Row);
            AppendTable(batch, "legalpilot_ai_feedback_entries", snapshot.AiFeedbackEntries ?? [], Row);

            batch.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
                // The server can dispose the transaction after a protocol/write error.
            }

            throw;
        }
    }

    public object Diagnostics()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        using var tablesCommand = new NpgsqlCommand("""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_name LIKE 'legalpilot_%'
            ORDER BY table_name;
            """, connection);
        using var reader = tablesCommand.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        reader.Close();

        using var migrationCommand = new NpgsqlCommand("SELECT COALESCE(MAX(version), 0) FROM legalpilot_schema_migrations;", connection);
        var migrationVersion = (int)migrationCommand.ExecuteScalar()!;

        return new
        {
            provider = Provider,
            dataSource = DataSource,
            migrationVersion,
            tableCount = tables.Count,
            tables
        };
    }

    private StoreSnapshot? TryLoadJsonSnapshot()
    {
        try
        {
            var json = File.ReadAllText(_jsonMigrationPath);
            return JsonSerializer.Deserialize<StoreSnapshot>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            var quarantinePath = $"{_jsonMigrationPath}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.invalid";
            File.Move(_jsonMigrationPath, quarantinePath, overwrite: true);
            _logger.LogError(ex, "Could not migrate invalid LegalPilot JSON snapshot. File moved to {Path}.", quarantinePath);
            return null;
        }
    }

    private static void ApplyMigrations(NpgsqlConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS legalpilot_schema_migrations (
                version integer PRIMARY KEY,
                name text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            );
            """);

        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var table in InsertOrder)
            {
                Execute(connection, transaction, $$"""
                    CREATE TABLE IF NOT EXISTS {{table}} (
                        id uuid PRIMARY KEY,
                        tenant_id uuid NULL,
                        created_at timestamptz NULL,
                        updated_at timestamptz NULL,
                        user_id uuid NULL,
                        case_id uuid NULL,
                        client_id uuid NULL,
                        mailbox_connection_id uuid NULL,
                        legal_email_id uuid NULL,
                        deadline_id uuid NULL,
                        calendar_event_id uuid NULL,
                        email text NULL,
                        case_number text NULL,
                        external_id text NULL,
                        due_date date NULL,
                        starts_at timestamptz NULL,
                        status text NULL,
                        provider text NULL,
                        name text NULL,
                        payload jsonb NOT NULL
                    );
                    """);
            }

            foreach (var statement in ConstraintStatements)
            {
                Execute(connection, transaction, statement);
            }

            foreach (var statement in IndexStatements)
            {
                Execute(connection, transaction, statement);
            }

            using var baselineCommand = new NpgsqlCommand(
                "INSERT INTO legalpilot_schema_migrations (version, name) VALUES (@version, @name) ON CONFLICT (version) DO NOTHING;",
                connection,
                transaction);
            baselineCommand.Parameters.AddWithValue("version", 1);
            baselineCommand.Parameters.AddWithValue("name", "jsonb_domain_tables_with_indexes");
            baselineCommand.ExecuteNonQuery();

            using var command = new NpgsqlCommand(
                "INSERT INTO legalpilot_schema_migrations (version, name) VALUES (@version, @name) ON CONFLICT (version) DO NOTHING;",
                connection,
                transaction);
            command.Parameters.AddWithValue("version", CurrentMigration);
            command.Parameters.AddWithValue("name", "oauth_ai_and_idempotency_indexes");
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static bool MigrationApplied(NpgsqlConnection connection, int version)
    {
        using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM legalpilot_schema_migrations WHERE version = @version);", connection);
        command.Parameters.AddWithValue("version", version);
        return (bool)command.ExecuteScalar()!;
    }

    private static long CountRows(NpgsqlConnection connection, string table)
    {
        using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM {table};", connection);
        return (long)command.ExecuteScalar()!;
    }

    private List<T> LoadTable<T>(NpgsqlConnection connection, string table, string orderBy = "created_at ASC NULLS LAST")
    {
        using var command = new NpgsqlCommand($"SELECT payload::text FROM {table} ORDER BY {orderBy}, id;", connection);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            var payload = reader.GetString(0);
            if (JsonSerializer.Deserialize<T>(payload, _jsonOptions) is { } entity)
            {
                rows.Add(entity);
            }
        }

        return rows;
    }

    private void AppendTable<T>(
        NpgsqlBatch batch,
        string table,
        IEnumerable<T> items,
        Func<T, PgRow> map)
    {
        const string columns = "id, tenant_id, created_at, updated_at, user_id, case_id, client_id, mailbox_connection_id, legal_email_id, deadline_id, calendar_event_id, email, case_number, external_id, due_date, starts_at, status, provider, name, payload";
        var sql = $"""
            INSERT INTO {table} ({columns})
            VALUES (@id, @tenant_id, @created_at, @updated_at, @user_id, @case_id, @client_id, @mailbox_connection_id, @legal_email_id, @deadline_id, @calendar_event_id, @email, @case_number, @external_id, @due_date, @starts_at, @status, @provider, @name, CAST(@payload AS jsonb));
            """;

        foreach (var item in items)
        {
            var row = map(item);
            var command = new NpgsqlBatchCommand(sql);
            Add(command.Parameters, "id", NpgsqlDbType.Uuid, row.Id);
            Add(command.Parameters, "tenant_id", NpgsqlDbType.Uuid, row.TenantId);
            Add(command.Parameters, "created_at", NpgsqlDbType.TimestampTz, row.CreatedAt);
            Add(command.Parameters, "updated_at", NpgsqlDbType.TimestampTz, row.UpdatedAt);
            Add(command.Parameters, "user_id", NpgsqlDbType.Uuid, row.UserId);
            Add(command.Parameters, "case_id", NpgsqlDbType.Uuid, row.CaseId);
            Add(command.Parameters, "client_id", NpgsqlDbType.Uuid, row.ClientId);
            Add(command.Parameters, "mailbox_connection_id", NpgsqlDbType.Uuid, row.MailboxConnectionId);
            Add(command.Parameters, "legal_email_id", NpgsqlDbType.Uuid, row.LegalEmailId);
            Add(command.Parameters, "deadline_id", NpgsqlDbType.Uuid, row.DeadlineId);
            Add(command.Parameters, "calendar_event_id", NpgsqlDbType.Uuid, row.CalendarEventId);
            Add(command.Parameters, "email", NpgsqlDbType.Text, row.Email);
            Add(command.Parameters, "case_number", NpgsqlDbType.Text, row.CaseNumber);
            Add(command.Parameters, "external_id", NpgsqlDbType.Text, row.ExternalId);
            Add(command.Parameters, "due_date", NpgsqlDbType.Date, row.DueDate);
            Add(command.Parameters, "starts_at", NpgsqlDbType.TimestampTz, row.StartsAt);
            Add(command.Parameters, "status", NpgsqlDbType.Text, row.Status);
            Add(command.Parameters, "provider", NpgsqlDbType.Text, row.Provider);
            Add(command.Parameters, "name", NpgsqlDbType.Text, row.Name);
            Add(command.Parameters, "payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(item, _jsonOptions));
            batch.BatchCommands.Add(command);
        }
    }

    private static PgRow Row(Tenant item) => new(item.Id, item.Id, item.CreatedAt, null, null, null, null, null, null, null, null, null, null, null, null, null, item.IsActive ? "Active" : "Inactive", null, item.Name);

    private static PgRow Row(UserAccount item) => new(item.Id, item.TenantId, item.CreatedAt, null, item.Id, null, null, null, null, null, null, item.Email, null, null, null, null, item.IsActive ? "Active" : "Inactive", null, item.DisplayName);

    private static PgRow Row(ClientProfile item) => new(item.Id, item.TenantId, item.CreatedAt, null, null, null, item.Id, null, null, null, null, item.Email, null, item.Identification, null, null, null, null, item.FullName);

    private static PgRow Row(LegalCase item) => new(item.Id, item.TenantId, item.CreatedAt, item.UpdatedAt, item.ResponsibleUserId, item.Id, item.ClientId, null, null, null, null, null, item.CaseNumber, null, null, null, item.Status, null, item.Title);

    private static PgRow Row(MailboxConnection item) => new(item.Id, item.TenantId, item.ConnectedAt, item.LastSyncAt, item.OwnerUserId, null, null, item.Id, null, null, null, item.Email, null, item.ExternalAccountId, null, null, item.Status, item.Provider.ToString(), item.Email);

    private static PgRow Row(MailboxSyncState item) => new(item.Id, item.TenantId, item.CheckedAt, null, null, null, null, item.MailboxConnectionId, null, null, null, null, null, null, null, null, item.Status, item.Provider.ToString(), null);

    private static PgRow Row(OAuthStateTicket item) => new(item.Id, item.TenantId, item.CreatedAt, null, item.UserId, null, null, null, null, null, null, item.Email, null, null, null, null, item.Used ? "Used" : "Pending", item.Provider.ToString(), item.Email);

    private static PgRow Row(OAuthTokenCredential item) => new(item.Id, item.TenantId, item.CreatedAt, item.UpdatedAt, null, null, null, item.MailboxConnectionId, null, null, null, item.Email, null, null, null, item.ExpiresAt, item.Status, item.Provider.ToString(), item.Email);

    private static PgRow Row(LegalEmail item) => new(item.Id, item.TenantId, item.CreatedAt, null, null, item.CaseId, null, item.MailboxConnectionId, item.Id, null, null, item.Sender, item.Extraction?.CaseNumber, item.ExternalMessageId, null, item.ReceivedAt, item.ProcessingStatus, item.Provider?.ToString(), item.Subject);

    private static PgRow Row(DocumentAttachment item) => new(item.Id, item.TenantId, item.CreatedAt, null, null, null, null, null, item.LegalEmailId, null, null, null, null, item.StorageKey, null, null, null, null, item.FileName);

    private static PgRow Row(Holiday item) => new(item.Id, item.TenantId, item.CreatedAt, null, null, null, null, null, null, null, null, null, null, null, item.Date, null, item.Scope.ToString(), null, item.Name);

    private static PgRow Row(Deadline item) => new(item.Id, item.TenantId, item.CreatedAt, item.UpdatedAt, item.ResponsibleUserId, item.CaseId, null, null, item.LegalEmailId, item.Id, null, null, null, null, item.DueDate, null, item.Status.ToString(), null, item.Title);

    private static PgRow Row(CalendarEvent item) => new(item.Id, item.TenantId, item.CreatedAt, null, item.ResponsibleUserId, item.CaseId, null, null, null, item.DeadlineId, item.Id, null, null, item.ExternalEventId, null, item.StartsAt, item.Confirmed ? "Confirmed" : "Pending", item.ExternalProvider, item.Title);

    private static PgRow Row(Reminder item) => new(item.Id, item.TenantId, item.CreatedAt, null, null, null, null, null, null, null, item.CalendarEventId, null, null, null, null, item.SendAt, item.Status.ToString(), item.Channel.ToString(), item.Message);

    private static PgRow Row(Notification item) => new(item.Id, item.TenantId, item.CreatedAt, item.AcknowledgedAt, item.UserId, null, null, null, null, null, null, null, null, null, null, item.SentAt, item.Status.ToString(), item.Channel.ToString(), item.Title);

    private static PgRow Row(WhatsAppTemplate item) => new(item.Id, item.TenantId, item.CreatedAt, null, null, null, null, null, null, null, null, null, null, null, null, null, item.IsActive ? "Active" : "Inactive", "WhatsApp", item.Name);

    private static PgRow Row(WhatsAppMessage item) => new(item.Id, item.TenantId, item.CreatedAt, null, null, item.CaseId, item.ClientId, null, null, null, null, null, null, item.To, null, item.SentAt, item.Status, "WhatsApp", item.To);

    private static PgRow Row(ChatMessage item) => new(item.Id, item.TenantId, item.CreatedAt, null, item.AuthorUserId, item.CaseId, item.ClientId, null, null, null, null, null, null, null, null, null, item.Status, item.Channel.ToString(), item.AuthorName);

    private static PgRow Row(AuditEntry item) => new(item.Id, item.TenantId, item.CreatedAt, null, item.ActorUserId, null, null, null, null, null, null, null, null, item.EntityId, null, null, item.Action.ToString(), null, item.EntityType);

    private static PgRow Row(PasswordResetTicket item) => new(item.Id, item.TenantId, item.CreatedAt, null, item.UserId, null, null, null, null, null, null, null, null, null, null, item.ExpiresAt, item.Used ? "Used" : "Pending", null, null);

    private static PgRow Row(RefreshTokenSession item) => new(item.Id, item.TenantId, item.CreatedAt, item.RevokedAt, item.UserId, null, null, null, null, null, null, null, null, null, null, item.ExpiresAt, item.RevokedAt is null ? "Active" : "Revoked", null, null);

    private static PgRow Row(AiKnowledgeDocument item) => new(item.Id, item.TenantId, item.CreatedAt, item.UpdatedAt, null, null, null, null, null, null, null, null, null, item.SourceReference, null, null, item.Status, item.SourceType, item.Title);

    private static PgRow Row(AiProcessingRun item) => new(item.Id, item.TenantId, item.CreatedAt, null, null, null, null, null, item.LegalEmailId, null, null, null, null, item.InputHash, null, null, item.Status, item.Provider, item.Purpose);

    private static PgRow Row(AiFeedbackEntry item) => new(item.Id, item.TenantId, item.CreatedAt, null, item.UserId, null, null, null, null, null, null, null, null, item.AiRunId?.ToString(), null, null, "Stored", "AI", null);

    private static void Add(NpgsqlParameterCollection parameters, string name, NpgsqlDbType type, object? value)
    {
        if (type == NpgsqlDbType.TimestampTz && value is DateTimeOffset dto)
        {
            value = dto.ToUniversalTime();
        }

        parameters.Add(new NpgsqlParameter(name, type)
        {
            Value = value ?? DBNull.Value
        });
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private static void Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        command.ExecuteNonQuery();
    }

    private static string NormalizeConnectionString(string raw)
    {
        if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(raw);
            var userInfo = uri.UserInfo.Split(':', 2);
            var builder = new NpgsqlConnectionStringBuilder
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
            };

            return builder.ConnectionString;
        }

        var parsed = new NpgsqlConnectionStringBuilder(raw);
        if (parsed.SslMode is SslMode.Disable or SslMode.Prefer)
        {
            parsed.SslMode = SslMode.Require;
        }

        parsed.Pooling = true;
        parsed.IncludeErrorDetail = false;
        return parsed.ConnectionString;
    }

    private static string BuildSafeDataSource(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return $"postgresql://{builder.Username}@{builder.Host}:{builder.Port}/{builder.Database}";
    }

    private sealed record PgRow(
        Guid Id,
        Guid? TenantId,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? UpdatedAt,
        Guid? UserId,
        Guid? CaseId,
        Guid? ClientId,
        Guid? MailboxConnectionId,
        Guid? LegalEmailId,
        Guid? DeadlineId,
        Guid? CalendarEventId,
        string? Email,
        string? CaseNumber,
        string? ExternalId,
        DateOnly? DueDate,
        DateTimeOffset? StartsAt,
        string? Status,
        string? Provider,
        string? Name);

    private static readonly string[] InsertOrder =
    [
        "legalpilot_tenants",
        "legalpilot_users",
        "legalpilot_clients",
        "legalpilot_cases",
        "legalpilot_mailboxes",
        "legalpilot_mailbox_sync_states",
        "legalpilot_oauth_state_tickets",
        "legalpilot_oauth_token_credentials",
        "legalpilot_emails",
        "legalpilot_attachments",
        "legalpilot_holidays",
        "legalpilot_deadlines",
        "legalpilot_calendar_events",
        "legalpilot_reminders",
        "legalpilot_notifications",
        "legalpilot_whatsapp_templates",
        "legalpilot_whatsapp_messages",
        "legalpilot_chat_messages",
        "legalpilot_audit_entries",
        "legalpilot_password_reset_tickets",
        "legalpilot_refresh_token_sessions",
        "legalpilot_ai_knowledge_documents",
        "legalpilot_ai_processing_runs",
        "legalpilot_ai_feedback_entries"
    ];

    private static readonly string[] DeleteOrder = InsertOrder.Reverse().ToArray();

    private static readonly string[] ConstraintStatements =
    [
        Constraint("legalpilot_users", "fk_users_tenant", "tenant_id", "legalpilot_tenants", "id"),
        Constraint("legalpilot_clients", "fk_clients_tenant", "tenant_id", "legalpilot_tenants", "id"),
        Constraint("legalpilot_cases", "fk_cases_tenant", "tenant_id", "legalpilot_tenants", "id"),
        Constraint("legalpilot_cases", "fk_cases_client", "client_id", "legalpilot_clients", "id"),
        Constraint("legalpilot_cases", "fk_cases_responsible", "user_id", "legalpilot_users", "id"),
        Constraint("legalpilot_mailboxes", "fk_mailboxes_tenant", "tenant_id", "legalpilot_tenants", "id"),
        Constraint("legalpilot_mailboxes", "fk_mailboxes_owner", "user_id", "legalpilot_users", "id"),
        Constraint("legalpilot_mailbox_sync_states", "fk_sync_mailbox", "mailbox_connection_id", "legalpilot_mailboxes", "id"),
        Constraint("legalpilot_oauth_state_tickets", "fk_oauth_user", "user_id", "legalpilot_users", "id"),
        Constraint("legalpilot_oauth_token_credentials", "fk_oauth_tokens_mailbox", "mailbox_connection_id", "legalpilot_mailboxes", "id"),
        Constraint("legalpilot_emails", "fk_emails_mailbox", "mailbox_connection_id", "legalpilot_mailboxes", "id"),
        Constraint("legalpilot_emails", "fk_emails_case", "case_id", "legalpilot_cases", "id"),
        Constraint("legalpilot_attachments", "fk_attachments_email", "legal_email_id", "legalpilot_emails", "id"),
        Constraint("legalpilot_holidays", "fk_holidays_tenant", "tenant_id", "legalpilot_tenants", "id"),
        Constraint("legalpilot_deadlines", "fk_deadlines_case", "case_id", "legalpilot_cases", "id"),
        Constraint("legalpilot_deadlines", "fk_deadlines_email", "legal_email_id", "legalpilot_emails", "id"),
        Constraint("legalpilot_deadlines", "fk_deadlines_responsible", "user_id", "legalpilot_users", "id"),
        Constraint("legalpilot_calendar_events", "fk_events_case", "case_id", "legalpilot_cases", "id"),
        Constraint("legalpilot_calendar_events", "fk_events_deadline", "deadline_id", "legalpilot_deadlines", "id"),
        Constraint("legalpilot_calendar_events", "fk_events_responsible", "user_id", "legalpilot_users", "id"),
        Constraint("legalpilot_reminders", "fk_reminders_event", "calendar_event_id", "legalpilot_calendar_events", "id"),
        Constraint("legalpilot_notifications", "fk_notifications_user", "user_id", "legalpilot_users", "id"),
        Constraint("legalpilot_whatsapp_messages", "fk_whatsapp_client", "client_id", "legalpilot_clients", "id"),
        Constraint("legalpilot_whatsapp_messages", "fk_whatsapp_case", "case_id", "legalpilot_cases", "id"),
        Constraint("legalpilot_chat_messages", "fk_chat_client", "client_id", "legalpilot_clients", "id"),
        Constraint("legalpilot_chat_messages", "fk_chat_case", "case_id", "legalpilot_cases", "id"),
        Constraint("legalpilot_audit_entries", "fk_audit_tenant", "tenant_id", "legalpilot_tenants", "id"),
        Constraint("legalpilot_password_reset_tickets", "fk_password_reset_user", "user_id", "legalpilot_users", "id"),
        Constraint("legalpilot_refresh_token_sessions", "fk_refresh_user", "user_id", "legalpilot_users", "id"),
        Constraint("legalpilot_ai_knowledge_documents", "fk_ai_docs_tenant", "tenant_id", "legalpilot_tenants", "id"),
        Constraint("legalpilot_ai_processing_runs", "fk_ai_runs_tenant", "tenant_id", "legalpilot_tenants", "id"),
        Constraint("legalpilot_ai_processing_runs", "fk_ai_runs_email", "legal_email_id", "legalpilot_emails", "id"),
        Constraint("legalpilot_ai_feedback_entries", "fk_ai_feedback_tenant", "tenant_id", "legalpilot_tenants", "id"),
        Constraint("legalpilot_ai_feedback_entries", "fk_ai_feedback_user", "user_id", "legalpilot_users", "id")
    ];

    private static readonly string[] IndexStatements =
    [
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_users_tenant_email ON legalpilot_users (tenant_id, email);",
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_legalpilot_users_tenant_email_active ON legalpilot_users (tenant_id, lower(email)) WHERE status = 'Active';",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_clients_tenant_name ON legalpilot_clients (tenant_id, name);",
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_legalpilot_clients_tenant_identification ON legalpilot_clients (tenant_id, external_id) WHERE external_id IS NOT NULL;",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_cases_tenant_case_number ON legalpilot_cases (tenant_id, case_number);",
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_legalpilot_cases_tenant_case_number ON legalpilot_cases (tenant_id, lower(case_number)) WHERE case_number IS NOT NULL;",
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_legalpilot_mailboxes_tenant_provider_email ON legalpilot_mailboxes (tenant_id, provider, lower(email)) WHERE email IS NOT NULL;",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_oauth_tokens_mailbox ON legalpilot_oauth_token_credentials (mailbox_connection_id, status, starts_at);",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_emails_tenant_external ON legalpilot_emails (tenant_id, provider, external_id);",
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_legalpilot_emails_tenant_provider_external ON legalpilot_emails (tenant_id, provider, external_id) WHERE external_id IS NOT NULL;",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_deadlines_tenant_due ON legalpilot_deadlines (tenant_id, due_date, status);",
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_legalpilot_deadlines_email ON legalpilot_deadlines (tenant_id, legal_email_id) WHERE legal_email_id IS NOT NULL;",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_calendar_tenant_start ON legalpilot_calendar_events (tenant_id, starts_at);",
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_legalpilot_calendar_dedupe ON legalpilot_calendar_events (tenant_id, case_id, starts_at, lower(name)) WHERE starts_at IS NOT NULL;",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_reminders_tenant_start ON legalpilot_reminders (tenant_id, starts_at, status);",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_notifications_tenant_status ON legalpilot_notifications (tenant_id, status, created_at DESC);",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_audit_tenant_created ON legalpilot_audit_entries (tenant_id, created_at DESC);",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_ai_docs_tenant_status ON legalpilot_ai_knowledge_documents (tenant_id, status, updated_at DESC);",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_ai_runs_tenant_created ON legalpilot_ai_processing_runs (tenant_id, created_at DESC);",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_ai_feedback_tenant_created ON legalpilot_ai_feedback_entries (tenant_id, created_at DESC);",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_payload_gin_emails ON legalpilot_emails USING gin (payload jsonb_path_ops);",
        "CREATE INDEX IF NOT EXISTS ix_legalpilot_payload_gin_cases ON legalpilot_cases USING gin (payload jsonb_path_ops);"
    ];

    private static string Constraint(string table, string name, string column, string targetTable, string targetColumn)
    {
        return $$"""
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conname = '{{name}}'
                ) THEN
                    ALTER TABLE {{table}}
                    ADD CONSTRAINT {{name}}
                    FOREIGN KEY ({{column}}) REFERENCES {{targetTable}} ({{targetColumn}})
                    ON DELETE SET NULL;
                END IF;
            END
            $$;
            """;
    }
}
