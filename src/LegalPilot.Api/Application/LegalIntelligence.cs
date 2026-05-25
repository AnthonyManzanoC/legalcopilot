using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LegalPilot.Api.Domain;

namespace LegalPilot.Api.Application;

public sealed class LegalIntelligenceService
{
    private static readonly TimeSpan GeminiTimeout = TimeSpan.FromSeconds(180);
    private static readonly Regex CaseNumberRegex = new(@"(?:(?:causa|proceso|juicio|expediente(?:\s+fiscal)?|investigacion previa|no\.?|nro\.?|n[°º])\s*[:#.-]?\s*)?(?<case>\d{5}-\d{4}-\d{5}|\d{13,16})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NumericDateRegex = new(@"\b(?<day>\d{1,2})[/-](?<month>\d{1,2})[/-](?<year>\d{4})\b|\b(?<year2>\d{4})-(?<month2>\d{1,2})-(?<day2>\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex SpanishDateRegex = new(@"\b(?<day>\d{1,2})\s+de\s+(?<month>enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|setiembre|octubre|noviembre|diciembre)\s+(?:de|del)\s+(?<year>\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeRegex = new(@"\b(?<hour>\d{1,2})(?::|h|H)(?<minute>\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex FiscalEventRegex = new(@"(?<type>VERSIONES?\s+FISCALIA|PERICIA\s+[A-ZÁÉÍÓÚÜÑ\s]+?|REQUERIMIENTO\s+DE\s+INFORMACION|RECOPILACION\s+DE\s+ELEMENTOS\s+DE\s+CONVICCION).*?FECHA\.-\s*(?<date>\d{4}-\d{1,2}-\d{1,2})\s+HORA\.-\s*(?<time>\d{1,2}:\d{2})", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DeadlineRegex = new(@"(?<context>.{0,180}?(?:termino|plazo).{0,120}?(?:(?<num>\d{1,3})|(?<words>[A-ZÁÉÍÓÚÜÑ\s]+?)\s*\((?<numParen>\d{1,3})\))\s*(?<unit>dias?|días?|horas?)[^.\n;]{0,220})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

        var hearings = ExtractHearings(text).ToArray();
        var deadlines = ExtractDeadlines(text).ToArray();
        var termDays = FirstLegacyTermDays(deadlines);
        if (termDays.HasValue)
        {
            signals.Add("plazo");
            if (actType == LegalActType.Unknown)
            {
                actType = LegalActType.Deadline;
            }
        }

        if (hearings.Length > 0)
        {
            signals.Add("agenda");
            if (actType == LegalActType.Unknown || actType == LegalActType.JudicialNotification || actType == LegalActType.Ruling)
            {
                actType = hearings.Any(h => ContainsAny(RemoveDiacritics(h.Type ?? string.Empty).ToLowerInvariant(), "pericia", "perito"))
                    ? LegalActType.ExpertReview
                    : LegalActType.Hearing;
            }
        }

        var caseNumber = ExtractCaseNumber(text);
        var firstHearing = hearings.FirstOrDefault(h => h.Date.HasValue);
        var eventDate = firstHearing?.Date ?? ExtractDate(text);
        var eventTime = firstHearing?.Time ?? ExtractTime(text);
        var court = ExtractCourtOrOffice(text);
        var location = firstHearing?.Location ?? firstHearing?.LinkZoom ?? ExtractLocation(text);
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
            BuildLawyerSummary(actType, caseNumber, court, eventDate, eventTime, termDays, obligation, priority, hearings, deadlines),
            BuildClientSummary(actType, eventDate, eventTime, obligation),
            BuildDraft(actType, caseNumber, termDays, eventDate),
            signals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            hearings,
            deadlines);
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
        using var timeout = new CancellationTokenSource(GeminiTimeout);
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

        var hearings = MergeHearings(dto.Hearings, local.Hearings).ToArray();
        var deadlines = MergeDeadlines(dto.Deadlines, local.Deadlines).ToArray();
        var actType = ParseActType(dto.ActType) ?? InferActType(hearings, deadlines, local.ActType);
        var firstHearing = hearings.FirstOrDefault(h => h.Date.HasValue);
        var eventDate = ParseDate(dto.EventDate) ?? firstHearing?.Date ?? local.EventDate;
        var eventTime = ParseTime(dto.EventTime) ?? firstHearing?.Time ?? local.EventTime;
        var termDays = dto.TermDays is >= 1 and <= 180 ? dto.TermDays : FirstLegacyTermDays(deadlines) ?? local.TermDays;
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
            string.IsNullOrWhiteSpace(dto.Location) ? firstHearing?.Location ?? firstHearing?.LinkZoom ?? local.Location : dto.Location,
            termDays,
            string.IsNullOrWhiteSpace(dto.Obligation) ? local.Obligation : dto.Obligation,
            dto.RequiresResponse ?? local.RequiresResponse,
            string.IsNullOrWhiteSpace(dto.Priority) ? local.Priority : dto.Priority,
            confidence,
            string.IsNullOrWhiteSpace(dto.LawyerSummary) ? BuildLawyerSummary(actType, dto.CaseNumber ?? local.CaseNumber, dto.CourtOrOffice ?? local.CourtOrOffice, eventDate, eventTime, termDays, dto.Obligation ?? local.Obligation, string.IsNullOrWhiteSpace(dto.Priority) ? local.Priority : dto.Priority, hearings, deadlines) : dto.LawyerSummary,
            string.IsNullOrWhiteSpace(dto.ClientSummary) ? local.ClientSummary : dto.ClientSummary,
            string.IsNullOrWhiteSpace(dto.SuggestedDraft) ? local.SuggestedDraft : dto.SuggestedDraft,
            signals,
            hearings,
            deadlines);
    }

    private static string BuildGeminiPrompt(string subject, string body)
    {
        var analysisBody = HtmlSanitizer.ClipForAnalysis(HtmlSanitizer.ToLegalInnerText(body));
        return $$"""
        Eres un asistente legal en Ecuador. Analiza el texto y extrae TODAS las audiencias y TODOS los plazos (dias u horas). Entiende formatos como SATJE y Fiscalia. Si hay diferimientos, captura la NUEVA fecha. Si es via Zoom, extrae la palabra Zoom como ubicacion.
        Devuelve exclusivamente JSON valido. No calcules vencimientos ni fechas limite: solo extrae los dias u horas concedidos. El motor deterministico EcuadorDeadlineEngine calculara cualquier vencimiento.

        Campos requeridos:
        {
          "actType": "Unknown|JudicialNotification|ProsecutorNotification|Hearing|ExpertReview|Ruling|Summons|OfficialLetter|Deadline|Diligence|ClientMessage",
          "caseNumber": string|null,
          "courtOrOffice": string|null,
          "eventDate": "yyyy-MM-dd"|null,
          "eventTime": "HH:mm"|null,
          "location": string|null,
          "termDays": number|null,
          "hearings": [
            { "date": "yyyy-MM-dd"|null, "time": "HH:mm"|null, "type": string|null, "location": string|null, "linkZoom": string|null }
          ],
          "deadlines": [
            { "grantedDays": number|null, "grantedHours": number|null, "condition": string|null }
          ],
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
        {{analysisBody}}
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
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : ExtractDate(value);
    }

    private static TimeOnly? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace('H', ':').Replace('h', ':');
        return TimeOnly.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private static bool ContainsAny(string value, params string[] needles) => needles.Any(value.Contains);

    private static string? ExtractCaseNumber(string text)
    {
        var match = CaseNumberRegex.Match(RemoveDiacritics(text));
        return match.Success ? match.Groups["case"].Value : null;
    }

    private static IEnumerable<ExtractedHearing> ExtractHearings(string text)
    {
        var results = new List<ExtractedHearing>();
        var location = ExtractZoomOrLocation(text);
        foreach (Match match in FiscalEventRegex.Matches(RemoveDiacritics(text).ToUpperInvariant()))
        {
            results.Add(new ExtractedHearing(
                ParseDate(match.Groups["date"].Value),
                ParseTime(match.Groups["time"].Value),
                CleanLabel(match.Groups["type"].Value),
                location,
                ExtractZoomLink(text)));
        }

        var normalized = RemoveDiacritics(text);
        foreach (Match match in Regex.Matches(normalized, @"\bAUDIENCIA\s+(?:DE\s+)?", RegexOptions.IgnoreCase))
        {
            var contextStart = Math.Max(0, match.Index - 180);
            var context = normalized.Substring(contextStart, Math.Min(830, normalized.Length - contextStart));
            var window = normalized.Substring(match.Index, Math.Min(650, normalized.Length - match.Index));
            if (window.StartsWith("audiencia convocada", StringComparison.OrdinalIgnoreCase) &&
                RemoveDiacritics(context).Contains("deja sin efecto", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsCancelledEventWindow(context))
            {
                continue;
            }

            var date = ExtractDate(window);
            var time = ExtractTime(window);
            if (!date.HasValue)
            {
                continue;
            }

            var type = ExtractHearingType(window);
            results.Add(new ExtractedHearing(date, time, type, location ?? ExtractLocation(window), ExtractZoomLink(text)));
        }

        return results
            .Where(h => h.Date.HasValue || h.Time.HasValue)
            .GroupBy(h => $"{DateKey(h.Date)}|{TimeKey(h.Time)}|{NormalizeKey(h.Type)}")
            .Select(g => g.First());
    }

    private static bool IsCancelledEventWindow(string window)
    {
        var normalized = RemoveDiacritics(window).ToLowerInvariant();
        var numeric = NumericDateRegex.Match(normalized);
        var spanish = SpanishDateRegex.Match(normalized);
        var indexes = new[] { numeric.Success ? numeric.Index : -1, spanish.Success ? spanish.Index : -1 }
            .Where(index => index >= 0)
            .ToArray();
        var firstDate = indexes.Length == 0 ? normalized.Length : indexes.Min();
        var beforeDate = normalized[..firstDate];
        return ContainsAny(beforeDate, "deja sin efecto", "se revoca", "revoca la providencia anterior") &&
               !beforeDate.Contains("nueva fecha", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ExtractedDeadline> ExtractDeadlines(string text)
    {
        var normalized = RemoveDiacritics(text);
        foreach (Match match in DeadlineRegex.Matches(normalized))
        {
            var numberText = GroupValue(match, "num", "numParen");
            if (!int.TryParse(numberText, out var amount) || amount <= 0)
            {
                continue;
            }

            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            var condition = CleanSentence(match.Groups["context"].Value);
            yield return unit.StartsWith("hora", StringComparison.OrdinalIgnoreCase)
                ? new ExtractedDeadline(null, amount, condition)
                : new ExtractedDeadline(amount, null, condition);
        }
    }

    private static int? FirstLegacyTermDays(IEnumerable<ExtractedDeadline>? deadlines)
    {
        foreach (var deadline in deadlines ?? [])
        {
            if (deadline.GrantedDays is >= 1 and <= 180)
            {
                return deadline.GrantedDays;
            }

            if (deadline.GrantedHours is >= 1 and <= 4320)
            {
                return Math.Clamp((int)Math.Ceiling(deadline.GrantedHours.Value / 24m), 1, 180);
            }
        }

        return null;
    }

    private static DateOnly? ExtractDate(string text)
    {
        var numeric = NumericDateRegex.Match(text);
        if (numeric.Success)
        {
            var day = GroupValue(numeric, "day", "day2");
            var month = GroupValue(numeric, "month", "month2");
            var year = GroupValue(numeric, "year", "year2");

            return int.TryParse(day, out var d) &&
                   int.TryParse(month, out var m) &&
                   int.TryParse(year, out var y) &&
                   DateOnly.TryParseExact($"{y:D4}-{m:D2}-{d:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
        }

        var spanish = SpanishDateRegex.Match(RemoveDiacritics(text));
        if (!spanish.Success)
        {
            return null;
        }

        return int.TryParse(spanish.Groups["day"].Value, out var sd) &&
               int.TryParse(spanish.Groups["year"].Value, out var sy) &&
               MonthNumber(spanish.Groups["month"].Value) is { } sm &&
               DateOnly.TryParseExact($"{sy:D4}-{sm:D2}-{sd:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedSpanish)
            ? parsedSpanish
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

    private static int? MonthNumber(string value)
    {
        return RemoveDiacritics(value).ToLowerInvariant() switch
        {
            "enero" => 1,
            "febrero" => 2,
            "marzo" => 3,
            "abril" => 4,
            "mayo" => 5,
            "junio" => 6,
            "julio" => 7,
            "agosto" => 8,
            "septiembre" or "setiembre" => 9,
            "octubre" => 10,
            "noviembre" => 11,
            "diciembre" => 12,
            _ => null
        };
    }

    private static string ExtractHearingType(string window)
    {
        var endMarkers = new[] { ",", " para el", " para que", " la cual", " a realizarse", " diligencia" };
        var type = window;
        foreach (var marker in endMarkers)
        {
            var index = type.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                type = type[..index];
            }
        }

        return CleanLabel(type);
    }

    private static string? ExtractZoomOrLocation(string text)
    {
        if (text.Contains("zoom", StringComparison.OrdinalIgnoreCase))
        {
            return "Zoom";
        }

        return ExtractLocation(text);
    }

    private static string? ExtractZoomLink(string text)
    {
        var match = Regex.Match(text, @"https?://\S*zoom\S*", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.TrimEnd('.', ',', ';') : null;
    }

    private static string CleanLabel(string value)
    {
        value = Regex.Replace(value, @"\s+", " ").Trim(' ', '.', ',', '-', ':');
        return value.Length > 160 ? value[..160] : value;
    }

    private static string CleanSentence(string value)
    {
        value = Regex.Replace(value, @"\s+", " ").Trim(' ', '.', ',', '-', ':');
        return value.Length > 240 ? value[..240] : value;
    }

    private static string NormalizeKey(string? value)
    {
        return RemoveDiacritics(value ?? string.Empty).ToLowerInvariant().Trim();
    }

    private static string DateKey(DateOnly? value) => value.HasValue ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

    private static string TimeKey(TimeOnly? value) => value.HasValue ? value.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty;

    private static LegalActType InferActType(IReadOnlyList<ExtractedHearing> hearings, IReadOnlyList<ExtractedDeadline> deadlines, LegalActType fallback)
    {
        if (hearings.Count > 0)
        {
            return hearings.Any(h => ContainsAny(NormalizeKey(h.Type), "pericia", "perito"))
                ? LegalActType.ExpertReview
                : LegalActType.Hearing;
        }

        return deadlines.Count > 0 && fallback == LegalActType.Unknown ? LegalActType.Deadline : fallback;
    }

    private static IEnumerable<ExtractedHearing> MergeHearings(GeminiHearingDto[]? gemini, IReadOnlyList<ExtractedHearing>? local)
    {
        var items = new List<ExtractedHearing>();
        foreach (var item in gemini ?? [])
        {
            items.Add(new ExtractedHearing(
                ParseDate(item.Date),
                ParseTime(item.Time),
                string.IsNullOrWhiteSpace(item.Type) ? null : CleanLabel(item.Type),
                string.IsNullOrWhiteSpace(item.Location) ? null : CleanLabel(item.Location),
                string.IsNullOrWhiteSpace(item.LinkZoom) ? null : item.LinkZoom));
        }

        items.AddRange(local ?? []);
        return items
            .Where(h => h.Date.HasValue || h.Time.HasValue)
            .GroupBy(h => $"{DateKey(h.Date)}|{TimeKey(h.Time)}|{NormalizeKey(h.Type)}")
            .Select(g => g.First());
    }

    private static IEnumerable<ExtractedDeadline> MergeDeadlines(GeminiDeadlineDto[]? gemini, IReadOnlyList<ExtractedDeadline>? local)
    {
        var items = new List<ExtractedDeadline>();
        foreach (var item in gemini ?? [])
        {
            var days = item.GrantedDays is >= 1 and <= 180 ? item.GrantedDays : null;
            var hours = item.GrantedHours is >= 1 and <= 4320 ? item.GrantedHours : null;
            if (days.HasValue || hours.HasValue)
            {
                items.Add(new ExtractedDeadline(days, hours, string.IsNullOrWhiteSpace(item.Condition) ? null : CleanSentence(item.Condition)));
            }
        }

        items.AddRange(local ?? []);
        return items
            .Where(d => d.GrantedDays.HasValue || d.GrantedHours.HasValue)
            .GroupBy(d => $"{d.GrantedDays}|{d.GrantedHours}|{NormalizeKey(d.Condition)}")
            .Select(g => g.First());
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

    private static string BuildLawyerSummary(
        LegalActType type,
        string? caseNumber,
        string? court,
        DateOnly? eventDate,
        TimeOnly? eventTime,
        int? termDays,
        string? obligation,
        string priority,
        IReadOnlyList<ExtractedHearing>? hearings,
        IReadOnlyList<ExtractedDeadline>? deadlines)
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
        if (hearings is { Count: > 1 }) parts.Add($"Audiencias/diligencias detectadas: {hearings.Count}.");
        if (deadlines is { Count: > 1 }) parts.Add($"Plazos detectados: {deadlines.Count}.");
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
        GeminiHearingDto[]? Hearings,
        GeminiDeadlineDto[]? Deadlines,
        string? Obligation,
        bool? RequiresResponse,
        string? Priority,
        decimal? Confidence,
        string? LawyerSummary,
        string? ClientSummary,
        string? SuggestedDraft,
        string[]? Signals);

    private sealed record GeminiHearingDto(
        string? Date,
        string? Time,
        string? Type,
        string? Location,
        string? LinkZoom);

    private sealed record GeminiDeadlineDto(
        int? GrantedDays,
        int? GrantedHours,
        string? Condition);
}
