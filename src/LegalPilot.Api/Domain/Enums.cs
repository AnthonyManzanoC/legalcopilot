namespace LegalPilot.Api.Domain;

public enum UserRole
{
    SuperAdmin,
    Lawyer,
    Assistant,
    Client
}

public enum MailProvider
{
    Gmail,
    Outlook
}

public enum LegalActType
{
    Unknown,
    JudicialNotification,
    ProsecutorNotification,
    Hearing,
    ExpertReview,
    Ruling,
    Summons,
    OfficialLetter,
    Deadline,
    Diligence,
    ClientMessage
}

public enum DeadlineStatus
{
    Draft,
    PendingReview,
    Confirmed,
    Completed,
    Cancelled,
    Overdue
}

public enum CalendarEventType
{
    Deadline,
    Hearing,
    Diligence,
    ExpertReview,
    Meeting,
    Task
}

public enum NotificationChannel
{
    Panel,
    Email,
    Push,
    WhatsApp
}

public enum NotificationStatus
{
    Pending,
    Sent,
    Failed,
    Acknowledged
}

public enum ChatDirection
{
    Inbound,
    Outbound,
    Internal
}

public enum AuditAction
{
    Login,
    Logout,
    Create,
    Update,
    Delete,
    View,
    IngestEmail,
    ClassifyEmail,
    CalculateDeadline,
    CreateCalendarEvent,
    SendNotification,
    SendWhatsApp,
    Approve,
    Reject,
    ConnectIntegration,
    DisconnectIntegration,
    RenewSubscription,
    ProcessMail,
    CalendarSync,
    WebhookReceived,
    SecurityEvent,
    SyncAttempt,
    PasswordReset,
    ChatMessage,
    AiRun,
    AiKnowledgeRegistered,
    AiFeedback,
    DeadlineOverdue
}

public enum HolidayScope
{
    National,
    Province,
    Canton,
    Court,
    TenantException
}
