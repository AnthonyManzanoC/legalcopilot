using System.Text.Json.Serialization;

namespace LegalPilot.Api.Domain;

public sealed record Tenant(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    bool IsActive = true);

public sealed record UserAccount(
    Guid Id,
    Guid TenantId,
    string Email,
    string DisplayName,
    string PasswordHash,
    string PasswordSalt,
    IReadOnlyList<UserRole> Roles,
    bool MfaEnabled,
    DateTimeOffset CreatedAt,
    bool IsActive = true);

public sealed record ClientProfile(
    Guid Id,
    Guid TenantId,
    string FullName,
    string Email,
    string Phone,
    string Identification,
    DateTimeOffset CreatedAt);

public sealed record LegalCase(
    Guid Id,
    Guid TenantId,
    string Title,
    string CaseNumber,
    string Matter,
    string CourtOrOffice,
    Guid? ClientId,
    Guid ResponsibleUserId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MailboxConnection(
    Guid Id,
    Guid TenantId,
    Guid OwnerUserId,
    MailProvider Provider,
    string Email,
    string ExternalAccountId,
    string Status,
    string[] Scopes,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? LastSyncAt,
    string? Cursor,
    DateTimeOffset? WatchExpiresAt,
    string? WebhookSubscriptionId = null,
    DateTimeOffset? WebhookRenewedAt = null,
    string? DefaultCalendarId = null,
    string? LastError = null);

public sealed record MailboxSyncState(
    Guid Id,
    Guid TenantId,
    Guid MailboxConnectionId,
    MailProvider Provider,
    string Status,
    string Message,
    DateTimeOffset CheckedAt,
    DateTimeOffset? NextAttemptAt,
    int FailureCount);

public sealed record OAuthStateTicket(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    MailProvider Provider,
    string Email,
    string StateHash,
    DateTimeOffset ExpiresAt,
    bool Used,
    DateTimeOffset CreatedAt);

public sealed record OAuthTokenCredential(
    Guid Id,
    Guid TenantId,
    Guid MailboxConnectionId,
    MailProvider Provider,
    string Email,
    string AccessTokenCiphertext,
    string? RefreshTokenCiphertext,
    string TokenType,
    string[] Scopes,
    DateTimeOffset ExpiresAt,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DocumentAttachment(
    Guid Id,
    Guid TenantId,
    Guid LegalEmailId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string StorageKey,
    string Sha256,
    string? OcrText,
    DateTimeOffset CreatedAt);

public sealed record LegalEmail(
    Guid Id,
    Guid TenantId,
    Guid? MailboxConnectionId,
    Guid? CaseId,
    MailProvider? Provider,
    string ExternalMessageId,
    string Subject,
    string Sender,
    string[] Recipients,
    string BodyText,
    string RawReference,
    DateTimeOffset ReceivedAt,
    string ProcessingStatus,
    LegalExtraction? Extraction,
    DateTimeOffset CreatedAt,
    string MessageHash = "",
    bool ProcessedWithFallback = false,
    string? FallbackReason = null);

public sealed record LegalExtraction(
    LegalActType ActType,
    string? CaseNumber,
    string? CourtOrOffice,
    DateOnly? EventDate,
    TimeOnly? EventTime,
    string? Location,
    int? TermDays,
    string? Obligation,
    bool RequiresResponse,
    string Priority,
    decimal Confidence,
    string LawyerSummary,
    string ClientSummary,
    string SuggestedDraft,
    string[] Signals);

public sealed record Holiday(
    Guid Id,
    Guid TenantId,
    DateOnly Date,
    string Name,
    HolidayScope Scope,
    string? Province,
    string? Canton,
    string Source,
    bool IsBusinessDayOverride,
    DateTimeOffset CreatedAt);

public sealed record DeadlineCalculationStep(
    DateOnly Date,
    bool Included,
    string Reason,
    int BusinessDayNumber);

public sealed record DeadlineCalculation(
    Guid Id,
    string RuleCode,
    DateOnly NotificationDate,
    int TermDays,
    DateOnly DueDate,
    IReadOnlyList<DeadlineCalculationStep> Steps,
    IReadOnlyList<string> HolidaysApplied,
    string Explanation,
    DateTimeOffset CalculatedAt);

public sealed record Deadline(
    Guid Id,
    Guid TenantId,
    Guid? CaseId,
    Guid? LegalEmailId,
    string Title,
    LegalActType SourceActType,
    DateOnly NotificationDate,
    int TermDays,
    DateOnly DueDate,
    DeadlineStatus Status,
    Guid ResponsibleUserId,
    decimal Confidence,
    DeadlineCalculation Calculation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CalendarEvent(
    Guid Id,
    Guid TenantId,
    Guid? CaseId,
    Guid? DeadlineId,
    CalendarEventType Type,
    string Title,
    string? Location,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    Guid ResponsibleUserId,
    bool RequiresConfirmation,
    bool Confirmed,
    string? ExternalProvider,
    string? ExternalEventId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt = null,
    string Status = "Scheduled",
    string SyncStatus = "Pending",
    string? SyncError = null,
    string? ExternalCalendarId = null,
    string? Description = null);

public sealed record MailWebhookEvent(
    Guid Id,
    Guid TenantId,
    MailProvider Provider,
    Guid? MailboxConnectionId,
    string ExternalEventId,
    string PayloadHash,
    string Status,
    string Message,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt);

public sealed record MailProcessingLog(
    Guid Id,
    Guid TenantId,
    Guid? MailboxConnectionId,
    Guid? LegalEmailId,
    MailProvider? Provider,
    string Stage,
    string Status,
    string Message,
    int Attempt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt);

public sealed record CalendarSyncLog(
    Guid Id,
    Guid TenantId,
    Guid CalendarEventId,
    MailProvider Provider,
    string Operation,
    string Status,
    string Message,
    string? ExternalEventId,
    int Attempt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt);

public sealed record Reminder(
    Guid Id,
    Guid TenantId,
    Guid CalendarEventId,
    NotificationChannel Channel,
    DateTimeOffset SendAt,
    string Message,
    NotificationStatus Status,
    DateTimeOffset CreatedAt);

public sealed record Notification(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    NotificationChannel Channel,
    string Title,
    string Message,
    NotificationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? AcknowledgedAt);

public sealed record WhatsAppTemplate(
    Guid Id,
    Guid TenantId,
    string Name,
    string Body,
    bool RequiresApproval,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed record WhatsAppMessage(
    Guid Id,
    Guid TenantId,
    Guid? ClientId,
    Guid? CaseId,
    string To,
    string Body,
    bool Approved,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt);

public sealed record ChatMessage(
    Guid Id,
    Guid TenantId,
    Guid? ClientId,
    Guid? CaseId,
    ChatDirection Direction,
    NotificationChannel Channel,
    Guid? AuthorUserId,
    string AuthorName,
    string Body,
    bool RequiresHumanReview,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record AuditEntry(
    Guid Id,
    Guid TenantId,
    Guid? ActorUserId,
    AuditAction Action,
    string EntityType,
    string EntityId,
    string Summary,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt);

public sealed record PasswordResetTicket(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    string TokenHash,
    DateTimeOffset ExpiresAt,
    bool Used,
    DateTimeOffset CreatedAt);

public sealed record RefreshTokenSession(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    string TokenHash,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    string? CreatedByIp,
    DateTimeOffset? RevokedAt,
    string? RevokedByIp);

public sealed record AiKnowledgeDocument(
    Guid Id,
    Guid TenantId,
    string Title,
    string SourceType,
    string SourceReference,
    string[] Tags,
    string ContentHash,
    string EmbeddingModel,
    int ChunkCount,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AiProcessingRun(
    Guid Id,
    Guid TenantId,
    Guid? LegalEmailId,
    string Purpose,
    string Provider,
    string Model,
    string Status,
    string InputHash,
    string OutputJson,
    bool RequiresHumanReview,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);

public sealed record AiFeedbackEntry(
    Guid Id,
    Guid TenantId,
    Guid? AiRunId,
    Guid UserId,
    int Rating,
    string CorrectionJson,
    DateTimeOffset CreatedAt);

public sealed record AuthPrincipal(
    Guid UserId,
    Guid TenantId,
    string Email,
    IReadOnlyList<UserRole> Roles)
{
    [JsonIgnore]
    public bool IsSuperAdmin => Roles.Contains(UserRole.SuperAdmin);

    public bool HasAnyRole(params UserRole[] roles) => IsSuperAdmin || roles.Any(Roles.Contains);
}
