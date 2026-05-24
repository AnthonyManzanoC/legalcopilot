using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LegalPilot.Api.Domain;

namespace LegalPilot.Api.Application;

public sealed class LegalIntelligenceService
{
    private static readonly Regex CaseNumberRegex = new(@"(?:(?:causa|proceso|juicio|expediente|no\.?|nro\.?)\s*[:#-]?\s*)?(?<case>\d{5}-\d{4}-\d{5})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TermRegex = new(@"(?:termino|plazo)\s+(?:de\s+)?(?<days>\d{1,3})\s+dias?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"\b(?<day>\d{1,2})[/-](?<month>\d{1,2})[/-](?<year>\d{4})\b|\b(?<year2>\d{4})-(?<month2>\d{1,2})-(?<day2>\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex TimeRegex = new(@"\b(?<hour>\d{1,2}):(?<minute>\d{2})\b", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IConfiguration? _configuration;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<LegalIntelligenceService>? _logger;

    public LegalIntelligenceService()
    {
    }

    public LegalIntelligenceService(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<LegalIntelligenceService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public LegalExtraction Extract(string subject, string body)
    {
        var local = ExtractLocal(subject, body, false);
        if (!ShouldUseGemini())
        {
            return WantsGemini() ? MarkFallback(local, "fallback-local-heuristic", "Gemini no esta configurado.") : local;
        }

        try
        {
            return TryExtractWithGemini(subject, body, local) ?? MarkFallback(local, "fallback-local-heuristic", "Gemini no devolvio JSON valido.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Gemini legal extraction failed; local heuristic fallback will be used.");
            return MarkFallback(local, "fallback-local-heuristic", "Gemini fallo o excedio timeout.");
        }
    }

    private LegalExtraction ExtractLocal(string subject, string body, bool fallback)
    {
        var text = $"{subject}\n{body}";
        var normalized = RemoveDiacritics(text).ToLowerInvariant();
        var signals = new List<string>();
        var actType = LegalActType.Unknown;

        if (ContainsAny(normalized, "audiencia", "convocatoria"))
        {
            actType = LegalActType.Hearing;
            signals.Add("audiencia");
        }
        else if (ContainsAny(normalized, "fiscalia", "fiscal", "version", "diligencia fiscal"))
        {
            actType = LegalActType.ProsecutorNotification;
            signals.Add("fiscalia");
        }
        else if (ContainsAny(normalized, "pericia", "perito", "informe pericial"))
        {
            actType = LegalActType.ExpertReview;
            signals.Add("pericia");
        }
        else if (ContainsAny(normalized, "providencia", "auto", "decreto"))
        {
            actType = LegalActType.Ruling;
            signals.Add("providencia");
        }
        else if (ContainsAny(normalized, "citacion", "citar", "comparezca"))
        {
            actType = LegalActType.Summons;
            signals.Add("citacion");
        }
        else if (ContainsAny(normalized, "oficio"))
        {
            actType = LegalActType.OfficialLetter;
            signals.Add("oficio");
        }
        else if (ContainsAny(normalized, "notificacion", "notifica"))
        {
            actType = LegalActType.JudicialNotification;
            signals.Add("notificacion");
        }

        var termDays = ExtractTermDays(text);
        if (termDays.HasValue)
        {
            signals.Add("plazo");
            if (actType == LegalActType.Unknown)
            {
                actType = LegalActType.Deadline;
            }
        }

        var caseNumber = CaseNumberRegex.Match(text) is { Success: true } match
            ? match.Groups["case"].Value
            : null;

        var eventDate = ExtractDate(text);
        var eventTime = ExtractTime(text);
        var court = ExtractCourtOrOffice(text);
        var location = ExtractLocation(text);
        var requiresResponse = ContainsAny(normalized, "conteste", "contestar", "presente", "remita", "cumpla", "comparezca", "subsanar");
        var priority = CalculatePriority(termDays, eventDate, normalized);
        var confidence = CalculateConfidence(actType, caseNumber, court, eventDate, termDays, signals.Count);
        var obligation = ExtractObligation(text, requiresResponse, actType);
        if (fallback)
        {
            signals.Add("fallback-local-heuristic");
            confidence = Math.Min(confidence, 0.72m);
        }

        return new LegalExtraction(
            actType,
            caseNumber,
            court,
            eventDate,
            eventTime,
            location,
            termDays,
            obligation,
            requiresResponse,
            priority,
            confidence,
            BuildLawyerSummary(actType, caseNumber, court, eventDate, eventTime, termDays, obligation, priority),
            BuildClientSummary(actType, eventDate, eventTime, obligation),
            BuildDraft(actType, caseNumber, termDays, eventDate),
            signals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private bool ShouldUseGemini()
    {
        if (!WantsGemini())
        {
            return false;
        }

        return _httpClientFactory is not null && !string.IsNullOrWhiteSpace(GeminiApiKey());
    }

    private bool WantsGemini()
    {
        var provider = _configuration?["LegalPilot:AI:Provider"] ?? string.Empty;
        var model = _configuration?["LegalPilot:AI:Model"] ?? string.Empty;
        return provider.Contains("gemini", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("gemini", StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrWhiteSpace(GeminiApiKey());
    }

    private string GeminiApiKey()
    {
        return _configuration?["LegalPilot:AI:ApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;
    }

    private LegalExtraction? TryExtractWithGemini(string subject, string body, LegalExtraction local)
    {
        var apiKey = GeminiApiKey();
        if (string.IsNullOrWhiteSpace(apiKey) || _httpClientFactory is null)
        {
            return null;
        }

        var model = string.IsNullOrWhiteSpace(_configuration?["LegalPilot:AI:Model"])
            ? "gemini-2.5-flash"
            : _configuration!["LegalPilot:AI:Model"]!;
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        var prompt = BuildGeminiPrompt(subject, body);
        var payload = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = prompt } }
                }
            },
            generationConfig = new
            {
                temperature = 0.1,
                responseMimeType = "application/json"
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = _httpClientFactory.CreateClient("gemini").Send(request, timeout.Token);
        var responseBody = response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gemini rechazo la solicitud: {response.StatusCode}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var text = document.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var dto = JsonSerializer.Deserialize<GeminiExtractionDto>(StripJsonFence(text), Json);
        if (dto is null)
        {
            return null;
        }

        var actType = ParseActType(dto.ActType) ?? local.ActType;
        var eventDate = ParseDate(dto.EventDate) ?? local.EventDate;
        var eventTime = ParseTime(dto.EventTime) ?? local.EventTime;
        var termDays = dto.TermDays is >= 1 and <= 180 ? dto.TermDays : local.TermDays;
        var confidence = Math.Clamp(dto.Confidence ?? local.Confidence, 0.35m, 0.95m);
        var signals = (dto.Signals ?? [])
            .Append("llm-gemini")
            .Concat(local.Signals)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LegalExtraction(
            actType,
            string.IsNullOrWhiteSpace(dto.CaseNumber) ? local.CaseNumber : dto.CaseNumber,
            string.IsNullOrWhiteSpace(dto.CourtOrOffice) ? local.CourtOrOffice : dto.CourtOrOffice,
            eventDate,
            eventTime,
            string.IsNullOrWhiteSpace(dto.Location) ? local.Location : dto.Location,
            termDays,
            string.IsNullOrWhiteSpace(dto.Obligation) ? local.Obligation : dto.Obligation,
            dto.RequiresResponse ?? local.RequiresResponse,
            string.IsNullOrWhiteSpace(dto.Priority) ? local.Priority : dto.Priority,
            confidence,
            string.IsNullOrWhiteSpace(dto.LawyerSummary) ? local.LawyerSummary : dto.LawyerSummary,
            string.IsNullOrWhiteSpace(dto.ClientSummary) ? local.ClientSummary : dto.ClientSummary,
            string.IsNullOrWhiteSpace(dto.SuggestedDraft) ? local.SuggestedDraft : dto.SuggestedDraft,
            signals);
    }

    private static string BuildGeminiPrompt(string subject, string body)
    {
        var clippedBody = body.Length <= 8000 ? body : body[..8000];
        return $$"""
        Eres un clasificador legal para Ecuador. Devuelve exclusivamente JSON valido.
        No calcules vencimientos ni fechas limite: si el texto menciona un termino, devuelve solo termDays.
        El motor deterministico EcuadorDeadlineEngine calculara cualquier plazo.

        Campos requeridos:
        {
          "actType": "Unknown|JudicialNotification|ProsecutorNotification|Hearing|ExpertReview|Ruling|Summons|OfficialLetter|Deadline|Diligence|ClientMessage",
          "caseNumber": string|null,
          "courtOrOffice": string|null,
          "eventDate": "yyyy-MM-dd"|null,
          "eventTime": "HH:mm"|null,
          "location": string|null,
          "termDays": number|null,
          "obligation": string|null,
          "requiresResponse": boolean,
          "priority": "Alta|Media|Normal",
          "confidence": number,
          "lawyerSummary": string,
          "clientSummary": string,
          "suggestedDraft": string,
          "signals": string[]
        }

        Asunto:
        {{subject}}

        Texto:
        {{clippedBody}}
        """;
    }

    private static LegalExtraction MarkFallback(LegalExtraction local, string signal, string reason)
    {
        var signals = local.Signals.Append(signal).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return local with
        {
            Signals = signals,
            Confidence = Math.Min(local.Confidence, 0.72m),
            LawyerSummary = $"{local.LawyerSummary} Procesado con fallback local: {reason}"
        };
    }

    private static string StripJsonFence(string text)
    {
        text = text.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline
            ? text[(firstNewline + 1)..lastFence].Trim()
            : text.Trim('`');
    }

    private static LegalActType? ParseActType(string? value)
    {
        if (Enum.TryParse<LegalActType>(value, true, out var parsed))
        {
            return parsed;
        }

        var normalized = RemoveDiacritics(value ?? string.Empty).ToLowerInvariant();
        if (normalized.Contains("audiencia")) return LegalActType.Hearing;
        if (normalized.Contains("pericia")) return LegalActType.ExpertReview;
        if (normalized.Contains("fiscal")) return LegalActType.ProsecutorNotification;
        if (normalized.Contains("providencia") || normalized.Contains("auto")) return LegalActType.Ruling;
        if (normalized.Contains("citacion")) return LegalActType.Summons;
        if (normalized.Contains("plazo") || normalized.Contains("termino")) return LegalActType.Deadline;
        if (normalized.Contains("notificacion")) return LegalActType.JudicialNotification;
        return null;
    }

    private static DateOnly? ParseDate(string? value)
    {
        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private static TimeOnly? ParseTime(string? value)
    {
        return TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private static bool ContainsAny(string value, params string[] needles) => needles.Any(value.Contains);

    private static int? ExtractTermDays(string text)
    {
        var match = TermRegex.Match(RemoveDiacritics(text).ToLowerInvariant());
        if (match.Success && int.TryParse(match.Groups["days"].Value, out var days))
        {
            return days;
        }

        return null;
    }

    private static DateOnly? ExtractDate(string text)
    {
        var match = DateRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var day = GroupValue(match, "day", "day2");
        var month = GroupValue(match, "month", "month2");
        var year = GroupValue(match, "year", "year2");

        return int.TryParse(day, out var d) &&
               int.TryParse(month, out var m) &&
               int.TryParse(year, out var y) &&
               DateOnly.TryParseExact($"{y:D4}-{m:D2}-{d:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static TimeOnly? ExtractTime(string text)
    {
        var match = TimeRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups["hour"].Value, out var hour) &&
               int.TryParse(match.Groups["minute"].Value, out var minute) &&
               hour is >= 0 and <= 23 &&
               minute is >= 0 and <= 59
            ? new TimeOnly(hour, minute)
            : null;
    }

    private static string? ExtractCourtOrOffice(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(line =>
        {
            var normalized = RemoveDiacritics(line).ToLowerInvariant();
            return normalized.Contains("unidad judicial") ||
                   normalized.Contains("juzgado") ||
                   normalized.Contains("fiscalia") ||
                   normalized.Contains("tribunal") ||
                   normalized.Contains("sala");
        });
    }

    private static string? ExtractLocation(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(line =>
        {
            var normalized = RemoveDiacritics(line).ToLowerInvariant();
            return normalized.Contains("sala") ||
                   normalized.Contains("direccion") ||
                   normalized.Contains("lugar") ||
                   normalized.Contains("link") ||
                   normalized.Contains("zoom");
        });
    }

    private static string ExtractObligation(string text, bool requiresResponse, LegalActType actType)
    {
        if (requiresResponse)
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var line = lines.FirstOrDefault(l =>
                l.Contains("conteste", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("presente", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("remita", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("cumpla", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("comparezca", StringComparison.OrdinalIgnoreCase));

            return line ?? "Requiere actuacion o respuesta del abogado.";
        }

        return actType switch
        {
            LegalActType.Hearing => "Comparecer a audiencia.",
            LegalActType.ExpertReview => "Revisar pericia o coordinar diligencia pericial.",
            LegalActType.ProsecutorNotification => "Revisar actuacion fiscal y preparar comparecencia si aplica.",
            _ => "Revisar notificacion y confirmar accion requerida."
        };
    }

    private static string CalculatePriority(int? termDays, DateOnly? eventDate, string normalized)
    {
        if (normalized.Contains("urgente") || termDays is <= 3)
        {
            return "Alta";
        }

        if (eventDate.HasValue && eventDate.Value <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)))
        {
            return "Alta";
        }

        if (termDays is <= 7)
        {
            return "Media";
        }

        return "Normal";
    }

    private static decimal CalculateConfidence(LegalActType type, string? caseNumber, string? court, DateOnly? eventDate, int? termDays, int signals)
    {
        decimal score = 0.30m;
        if (type != LegalActType.Unknown) score += 0.20m;
        if (!string.IsNullOrWhiteSpace(caseNumber)) score += 0.15m;
        if (!string.IsNullOrWhiteSpace(court)) score += 0.10m;
        if (eventDate.HasValue) score += 0.10m;
        if (termDays.HasValue) score += 0.10m;
        if (signals >= 2) score += 0.05m;
        return Math.Min(score, 0.95m);
    }

    private static string BuildLawyerSummary(LegalActType type, string? caseNumber, string? court, DateOnly? eventDate, TimeOnly? eventTime, int? termDays, string? obligation, string priority)
    {
        var parts = new List<string>
        {
            $"Tipo detectado: {type}.",
            $"Prioridad: {priority}."
        };

        if (!string.IsNullOrWhiteSpace(caseNumber)) parts.Add($"Causa: {caseNumber}.");
        if (!string.IsNullOrWhiteSpace(court)) parts.Add($"Dependencia: {court}.");
        if (eventDate.HasValue) parts.Add($"Fecha: {eventDate:yyyy-MM-dd}{(eventTime.HasValue ? $" {eventTime:HH:mm}" : string.Empty)}.");
        if (termDays.HasValue) parts.Add($"Termino/plazo detectado: {termDays} dias.");
        if (!string.IsNullOrWhiteSpace(obligation)) parts.Add($"Accion: {obligation}");
        return string.Join(" ", parts);
    }

    private static string BuildClientSummary(LegalActType type, DateOnly? eventDate, TimeOnly? eventTime, string obligation)
    {
        var when = eventDate.HasValue ? $" Fecha relevante: {eventDate:yyyy-MM-dd}{(eventTime.HasValue ? $" {eventTime:HH:mm}" : string.Empty)}." : string.Empty;
        return $"Se registro una novedad legal de tipo {type}.{when} El estudio revisara la accion necesaria.";
    }

    private static string BuildDraft(LegalActType type, string? caseNumber, int? termDays, DateOnly? eventDate)
    {
        return type switch
        {
            LegalActType.Hearing => $"Borrador: Se toma nota de la audiencia{(eventDate.HasValue ? $" del {eventDate:yyyy-MM-dd}" : string.Empty)}. Preparar comparecencia, anexos y coordinacion con cliente.",
            LegalActType.Deadline or LegalActType.Ruling or LegalActType.JudicialNotification => $"Borrador: Revisar providencia{(caseNumber is not null ? $" de la causa {caseNumber}" : string.Empty)} y preparar escrito dentro del termino detectado{(termDays.HasValue ? $" de {termDays} dias" : string.Empty)}.",
            LegalActType.ProsecutorNotification => "Borrador: Revisar disposicion fiscal, confirmar diligencia y preparar comparecencia o documentacion requerida.",
            _ => "Borrador: Revisar documento original, validar datos extraidos y definir siguiente actuacion."
        };
    }

    private static string GroupValue(Match match, string primary, string fallback)
    {
        return match.Groups[primary].Success ? match.Groups[primary].Value : match.Groups[fallback].Value;
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        Span<char> buffer = stackalloc char[normalized.Length];
        var index = 0;
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                buffer[index++] = c;
            }
        }

        return new string(buffer[..index]).Normalize(NormalizationForm.FormC);
    }

    private sealed record GeminiExtractionDto(
        string? ActType,
        string? CaseNumber,
        string? CourtOrOffice,
        string? EventDate,
        string? EventTime,
        string? Location,
        int? TermDays,
        string? Obligation,
        bool? RequiresResponse,
        string? Priority,
        decimal? Confidence,
        string? LawyerSummary,
        string? ClientSummary,
        string? SuggestedDraft,
        string[]? Signals);
}
