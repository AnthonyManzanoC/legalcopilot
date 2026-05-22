using System.Text.Json;
using LegalPilot.Api.Domain;

namespace LegalPilot.Api.Application;

public sealed class LegalAiPipelineService(
    LegalPilotStore store,
    LegalIntelligenceService intelligence,
    IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public object Status(AuthPrincipal principal)
    {
        return store.Read(() => new
        {
            provider = configuration["LegalPilot:AI:Provider"] ?? "local-deterministic",
            model = configuration["LegalPilot:AI:Model"] ?? "rules-v1",
            rag = new
            {
                documents = store.AiKnowledgeDocuments.Count(d => d.TenantId == principal.TenantId),
                ready = store.AiKnowledgeDocuments.Any(d => d.TenantId == principal.TenantId && d.Status == "Indexed"),
                embeddingModel = configuration["LegalPilot:AI:EmbeddingModel"] ?? "not-configured"
            },
            fineTuning = new
            {
                datasetFormat = "jsonl",
                labels = LegalLabels(),
                feedbackSamples = store.AiFeedbackEntries.Count(f => f.TenantId == principal.TenantId),
                status = "Prepared"
            },
            guardrails = Guardrails(),
            recentRuns = store.AiProcessingRuns
                .Where(r => r.TenantId == principal.TenantId)
                .OrderByDescending(r => r.CreatedAt)
                .Take(8)
                .ToArray()
        });
    }

    public object Analyze(AuthPrincipal principal, AiAnalyzeRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        var subject = InputGuard.Required(request.Subject, "Asunto", 240);
        var body = InputGuard.TextBlock(request.BodyText, "Texto", 12000);
        var extraction = intelligence.Extract(subject, body);
        var output = new
        {
            extraction,
            guardrails = Guardrails(),
            deadlinePolicy = "Los plazos se calculan exclusivamente con EcuadorDeadlineEngine; la IA solo detecta senales y terminos mencionados."
        };
        var run = new AiProcessingRun(
            Guid.NewGuid(),
            principal.TenantId,
            request.LegalEmailId,
            "classification-extraction-summary",
            configuration["LegalPilot:AI:Provider"] ?? "local-deterministic",
            configuration["LegalPilot:AI:Model"] ?? "rules-v1",
            "Completed",
            TokenService.Sha256($"{subject}\n{body}"),
            JsonSerializer.Serialize(output, Json),
            extraction.Confidence < 0.80m,
            null,
            DateTimeOffset.UtcNow);

        store.Write(() => store.AiProcessingRuns.Insert(0, run));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.AiRun, nameof(AiProcessingRun), run.Id.ToString(), "Clasificacion/extraccion IA registrada para revision humana.");
        return new
        {
            run,
            extraction,
            guardrails = Guardrails(),
            deadlinePolicy = "No se calculan vencimientos con IA."
        };
    }

    public AiKnowledgeDocument RegisterKnowledge(AuthPrincipal principal, AiKnowledgeDocumentRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
        var title = InputGuard.Required(request.Title, "Titulo", 180);
        var sourceType = InputGuard.Required(request.SourceType, "Tipo de fuente", 80);
        var sourceReference = InputGuard.Required(request.SourceReference, "Referencia", 240);
        var tags = request.Tags?.Select(tag => InputGuard.Optional(tag, 40)).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var contentHash = string.IsNullOrWhiteSpace(request.ContentHash)
            ? TokenService.Sha256($"{title}|{sourceType}|{sourceReference}|{string.Join(',', tags)}")
            : InputGuard.Required(request.ContentHash, "Hash", 128);
        var now = DateTimeOffset.UtcNow;
        var item = new AiKnowledgeDocument(
            Guid.NewGuid(),
            principal.TenantId,
            title,
            sourceType,
            sourceReference,
            tags,
            contentHash,
            configuration["LegalPilot:AI:EmbeddingModel"] ?? "pending-embedding-provider",
            Math.Max(0, request.ChunkCount),
            request.Indexed ? "Indexed" : "Registered",
            now,
            now);

        store.Write(() => store.AiKnowledgeDocuments.Insert(0, item));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.AiKnowledgeRegistered, nameof(AiKnowledgeDocument), item.Id.ToString(), $"Documento IA/RAG registrado: {item.Title}");
        return item;
    }

    public AiFeedbackEntry Feedback(AuthPrincipal principal, AiFeedbackRequest request)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer, UserRole.Assistant);
        if (request.AiRunId.HasValue && store.Read(() => store.AiProcessingRuns.All(r => r.Id != request.AiRunId.Value || r.TenantId != principal.TenantId)))
        {
            throw new ArgumentException("Ejecucion IA no pertenece al tenant actual.");
        }

        var rating = Math.Clamp(request.Rating, 1, 5);
        var correction = InputGuard.TextBlock(request.CorrectionJson, "Correccion", 6000);
        using var _ = JsonDocument.Parse(correction);
        var entry = new AiFeedbackEntry(
            Guid.NewGuid(),
            principal.TenantId,
            request.AiRunId,
            principal.UserId,
            rating,
            correction,
            DateTimeOffset.UtcNow);

        store.Write(() => store.AiFeedbackEntries.Insert(0, entry));
        store.Audit(principal.TenantId, principal.UserId, AuditAction.AiFeedback, nameof(AiFeedbackEntry), entry.Id.ToString(), "Feedback IA guardado para dataset futuro.");
        return entry;
    }

    private static string[] LegalLabels()
    {
        return
        [
            "audiencia",
            "citacion",
            "notificacion",
            "providencia",
            "pericia",
            "fiscalia",
            "plazo",
            "escrito_pendiente",
            "requerimiento",
            "otro"
        ];
    }

    private static object Guardrails()
    {
        return new
        {
            noDeadlineCalculationByLlm = true,
            deterministicDeadlineEngine = "EcuadorDeadlineEngine",
            humanReviewRequiredForCriticalActions = true,
            auditPromptsAndOutputs = true,
            piiProtection = "No enviar informacion sensible al cliente sin aprobacion del abogado."
        };
    }
}

public sealed record AiAnalyzeRequest(string Subject, string BodyText, Guid? LegalEmailId);
public sealed record AiKnowledgeDocumentRequest(string Title, string SourceType, string SourceReference, string[]? Tags, string? ContentHash, int ChunkCount, bool Indexed);
public sealed record AiFeedbackRequest(Guid? AiRunId, int Rating, string CorrectionJson);
