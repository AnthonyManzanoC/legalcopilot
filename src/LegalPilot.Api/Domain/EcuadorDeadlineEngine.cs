namespace LegalPilot.Api.Domain;

public sealed record DeadlineRequest(
    DateOnly NotificationDate,
    int TermDays,
    string Matter,
    string? Province = null,
    string? Canton = null,
    string RuleCode = "EC-COGEP-TERM-BUSINESS-DAYS-V1");

public sealed class EcuadorDeadlineEngine
{
    public DeadlineCalculation Calculate(DeadlineRequest request, IEnumerable<Holiday> holidays)
    {
        if (request.TermDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.TermDays), "Term days must be greater than zero.");
        }

        var relevantHolidays = holidays
            .Where(h => AppliesToRequest(h, request))
            .GroupBy(h => h.Date)
            .ToDictionary(g => g.Key, g => g.ToArray());

        var steps = new List<DeadlineCalculationStep>();
        var applied = new SortedSet<string>();
        var cursor = request.NotificationDate.AddDays(1);
        var counted = 0;

        while (counted < request.TermDays)
        {
            var reason = GetExclusionReason(cursor, relevantHolidays, applied);
            if (reason is null)
            {
                counted++;
                steps.Add(new DeadlineCalculationStep(cursor, true, "Dia habil contado.", counted));
            }
            else
            {
                steps.Add(new DeadlineCalculationStep(cursor, false, reason, counted));
            }

            if (counted < request.TermDays)
            {
                cursor = cursor.AddDays(1);
            }
        }

        var dueDate = cursor;
        var excluded = steps.Count(s => !s.Included);
        var explanation = $"Se contaron {request.TermDays} dias habiles desde el dia habil siguiente a {request.NotificationDate:yyyy-MM-dd}. Se excluyeron {excluded} dias por fin de semana, feriado o regla especial. Vence el {dueDate:yyyy-MM-dd}.";

        return new DeadlineCalculation(
            Guid.NewGuid(),
            request.RuleCode,
            request.NotificationDate,
            request.TermDays,
            dueDate,
            steps,
            applied.ToArray(),
            explanation,
            DateTimeOffset.UtcNow);
    }

    public bool IsBusinessDay(DateOnly date, IEnumerable<Holiday> holidays)
    {
        var request = new DeadlineRequest(date, 1, "general");
        var relevant = holidays
            .Where(h => AppliesToRequest(h, request))
            .GroupBy(h => h.Date)
            .ToDictionary(g => g.Key, g => g.ToArray());

        return GetExclusionReason(date, relevant, new SortedSet<string>()) is null;
    }

    public DateOnly MoveBackToBusinessDay(DateOnly date, IEnumerable<Holiday> holidays)
    {
        var cursor = date;
        while (!IsBusinessDay(cursor, holidays))
        {
            cursor = cursor.AddDays(-1);
        }

        return cursor;
    }

    private static bool AppliesToRequest(Holiday holiday, DeadlineRequest request)
    {
        if (holiday.Scope is HolidayScope.National or HolidayScope.TenantException)
        {
            return true;
        }

        if (holiday.Scope == HolidayScope.Province && !string.IsNullOrWhiteSpace(request.Province))
        {
            return string.Equals(holiday.Province, request.Province, StringComparison.OrdinalIgnoreCase);
        }

        if (holiday.Scope == HolidayScope.Canton && !string.IsNullOrWhiteSpace(request.Canton))
        {
            return string.Equals(holiday.Canton, request.Canton, StringComparison.OrdinalIgnoreCase);
        }

        if (holiday.Scope == HolidayScope.Court)
        {
            return true;
        }

        return false;
    }

    private static string? GetExclusionReason(
        DateOnly date,
        IReadOnlyDictionary<DateOnly, Holiday[]> holidays,
        ISet<string> applied)
    {
        if (holidays.TryGetValue(date, out var holidaySet) && holidaySet.Any(h => h.IsBusinessDayOverride))
        {
            foreach (var holiday in holidaySet.Where(h => h.IsBusinessDayOverride))
            {
                applied.Add($"{holiday.Date:yyyy-MM-dd}: {holiday.Name} (habilitado)");
            }

            return null;
        }

        if (date.DayOfWeek == DayOfWeek.Saturday)
        {
            return "Sabado excluido.";
        }

        if (date.DayOfWeek == DayOfWeek.Sunday)
        {
            return "Domingo excluido.";
        }

        if (holidaySet is not null && holidaySet.Any(h => !h.IsBusinessDayOverride))
        {
            var blockingHolidays = holidaySet.Where(h => !h.IsBusinessDayOverride).ToArray();
            foreach (var holiday in blockingHolidays)
            {
                applied.Add($"{holiday.Date:yyyy-MM-dd}: {holiday.Name}");
            }

            return "Feriado o descanso obligatorio excluido: " + string.Join(", ", blockingHolidays.Select(h => h.Name));
        }

        return null;
    }
}
