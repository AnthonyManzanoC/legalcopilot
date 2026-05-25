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
        var provider = AiProvider();
        var model = AiModel();
        var geminiConfigured = GeminiConfigured();
        return store.Read(() => new
        {
            provider,
            model,
            configured = geminiConfigured,
            status = geminiConfigured ? "GeminiReady" : "GeminiApiKeyMissing",
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
                status = "Prepared",
                exportEndpoint = "/api/ai/dataset.jsonl"
            },
            training = new
            {
                strategy = "RAG primero, fine-tuning ligero despues de dataset etiquetado.",
                compatibleFormat = "instruction-jsonl",
                fromScratchReference = "Use solo para experimentacion controlada; produccion debe iniciar con modelo base evaluado."
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
        var body = HtmlSanitizer.ToLegalInnerText(request.BodyText);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("Texto es obligatorio.");
        }

        body = HtmlSanitizer.ClipForAnalysis(body);
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
            AiProvider(),
            AiModel(),
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

    public string ExportDatasetJsonl(AuthPrincipal principal)
    {
        HttpAuth.RequireRole(principal, UserRole.SuperAdmin, UserRole.Lawyer);
        var rows = store.Read(() => store.Emails
            .Where(e => e.TenantId == principal.TenantId && e.Extraction is not null)
            .OrderByDescending(e => e.CreatedAt)
            .Take(500)
            .Select(e => new
            {
                instruction = "Clasifica y extrae datos legales de Ecuador. No calcules plazos; solo devuelve el termino mencionado si aparece.",
                input = new
                {
                    e.Subject,
                    e.Sender,
                    e.BodyText
                },
                output = new
                {
                    label = ToTrainingLabel(e.Extraction!.ActType),
                    e.Extraction.CaseNumber,
                    e.Extraction.CourtOrOffice,
                    e.Extraction.EventDate,
                    e.Extraction.EventTime,
                    e.Extraction.Location,
                    e.Extraction.TermDays,
                    e.Extraction.Hearings,
                    e.Extraction.Deadlines,
                    e.Extraction.Obligation,
                    e.Extraction.RequiresResponse,
                    e.Extraction.Priority,
                    e.Extraction.Signals,
                    summary = e.Extraction.LawyerSummary
                },
                metadata = new
                {
                    source = "legalpilot-email",
                    legalEmailId = e.Id,
                    createdAt = e.CreatedAt,
                    requiresHumanReview = e.Extraction.Confidence < 0.80m
                }
            })
            .ToArray());

        return string.Join('\n', rows.Select(row => JsonSerializer.Serialize(row, Json)));
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

    private static string ToTrainingLabel(LegalActType type)
    {
        return type switch
        {
            LegalActType.Hearing => "audiencia",
            LegalActType.Summons => "citacion",
            LegalActType.JudicialNotification => "notificacion",
            LegalActType.Ruling => "providencia",
            LegalActType.ExpertReview => "pericia",
            LegalActType.ProsecutorNotification => "fiscalia",
            LegalActType.Deadline => "plazo",
            LegalActType.OfficialLetter => "requerimiento",
            LegalActType.Diligence => "requerimiento",
            _ => "otro"
        };
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

    private string AiProvider()
    {
        var configured = configuration["LegalPilot:AI:Provider"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return GeminiConfigured() ? "gemini" : "not-configured";
    }

    private string AiModel()
    {
        var configured = configuration["LegalPilot:AI:Model"];
        return string.IsNullOrWhiteSpace(configured) ? "gemini-2.5-flash" : configured;
    }

    private bool GeminiConfigured()
    {
        return !string.IsNullOrWhiteSpace(configuration["LegalPilot:AI:ApiKey"]) ||
               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));
    }
}

public sealed record AiAnalyzeRequest(string Subject, string BodyText, Guid? LegalEmailId);
public sealed record AiKnowledgeDocumentRequest(string Title, string SourceType, string SourceReference, string[]? Tags, string? ContentHash, int ChunkCount, bool Indexed);
public sealed record AiFeedbackRequest(Guid? AiRunId, int Rating, string CorrectionJson);
