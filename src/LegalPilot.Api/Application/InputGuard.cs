using System.Text.RegularExpressions;

namespace LegalPilot.Api.Application;

public static partial class InputGuard
{
    public static string Required(string? value, string fieldName, int maxLength)
    {
        var normalized = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException($"{fieldName} es obligatorio.");
        }

        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{fieldName} supera {maxLength} caracteres.");
        }

        return normalized;
    }

    public static string Optional(string? value, int maxLength)
    {
        var normalized = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"El valor supera {maxLength} caracteres.");
        }

        return normalized;
    }

    public static string TextBlock(string? value, string fieldName, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException($"{fieldName} es obligatorio.");
        }

        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{fieldName} supera {maxLength} caracteres.");
        }

        return normalized;
    }

    public static string Email(string? value, string fieldName = "Correo")
    {
        var email = Required(value, fieldName, 254).ToLowerInvariant();
        if (!EmailRegex().IsMatch(email))
        {
            throw new ArgumentException($"{fieldName} no tiene formato valido.");
        }

        return email;
    }

    public static string Phone(string? value, string fieldName = "Telefono")
    {
        var phone = Required(value, fieldName, 32);
        if (!PhoneRegex().IsMatch(phone))
        {
            throw new ArgumentException($"{fieldName} no tiene formato valido.");
        }

        return phone;
    }

    public static string EcuadorWhatsApp(string? value, string fieldName = "WhatsApp")
    {
        var phone = Required(value, fieldName, 32);
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (!EcuadorWhatsAppRegex().IsMatch(digits))
        {
            throw new ArgumentException($"{fieldName} debe usar formato Ecuador 593, por ejemplo +593999000111.");
        }

        return $"+{digits}";
    }

    public static string CaseNumber(string? value)
    {
        var caseNumber = Required(value, "Numero de causa", 64);
        if (!CaseNumberRegex().IsMatch(caseNumber))
        {
            throw new ArgumentException("Numero de causa no tiene formato valido.");
        }

        return caseNumber;
    }

    public static int TermDays(int value)
    {
        if (value is < 1 or > 180)
        {
            throw new ArgumentException("El plazo debe estar entre 1 y 180 dias habiles.");
        }

        return value;
    }

    public static void DateRange(DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        if (endsAt <= startsAt)
        {
            throw new ArgumentException("La fecha de fin debe ser posterior al inicio.");
        }

        if (endsAt - startsAt > TimeSpan.FromDays(30))
        {
            throw new ArgumentException("El evento no puede superar 30 dias de duracion.");
        }
    }

    public static int Take(int? take, int defaultValue = 100, int max = 500)
    {
        if (!take.HasValue)
        {
            return defaultValue;
        }

        return Math.Clamp(take.Value, 1, max);
    }

    public static string Search(string? value, int maxLength = 80)
    {
        return Optional(value, maxLength);
    }

    public static string SafeMessage(string? value, int maxLength)
    {
        var text = Required(value, "Mensaje", maxLength)
            .Replace("contrasena", "[dato protegido]", StringComparison.OrdinalIgnoreCase)
            .Replace("password", "[dato protegido]", StringComparison.OrdinalIgnoreCase)
            .Replace("token", "[dato protegido]", StringComparison.OrdinalIgnoreCase);

        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return WhitespaceRegex().Replace(value.Trim(), " ");
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^\+?[0-9 ()-]{7,32}$", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^593[0-9]{8,9}$", RegexOptions.Compiled)]
    private static partial Regex EcuadorWhatsAppRegex();

    [GeneratedRegex(@"^[0-9A-Za-z.-]{5,64}$", RegexOptions.Compiled)]
    private static partial Regex CaseNumberRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}

public sealed class ForbiddenOperationException(string message) : Exception(message);

public sealed class ConflictException(string message) : Exception(message);
