using System.Globalization;
using System.Text;

namespace TeamsCallRecords.Infrastructure.Data;

/// <summary>Opaque page cursor "startUtc|cdrSeq" in URL-safe Base64 without padding (TRP 3.11.8).</summary>
public static class Cursor
{
    public static string Encode(string startUtc, long cdrSeq)
    {
        if (!IsCanonical(startUtc)) throw new ArgumentException("startUtc must be canonical UtcText", nameof(startUtc));
        if (cdrSeq < 1) throw new ArgumentException("cdrSeq must be >= 1", nameof(cdrSeq));
        var text = startUtc + "|" + cdrSeq.ToString(CultureInfo.InvariantCulture);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static (string StartUtc, long CdrSeq) Decode(string cursor)
    {
        try
        {
            if (string.IsNullOrEmpty(cursor)) throw new FormatException();
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            var text = strict.GetString(Convert.FromBase64String(b64));
            var parts = text.Split('|');
            if (parts.Length != 2) throw new FormatException();
            if (!IsCanonical(parts[0])) throw new FormatException();
            var digits = parts[1];
            if (digits.Length == 0 || !digits.All(c => c is >= '0' and <= '9')) throw new FormatException();
            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) || seq < 1)
                throw new FormatException();
            return (parts[0], seq);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            throw new ArgumentException("cursor is malformed", "cursor");
        }
    }

    private static bool IsCanonical(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        try { return UtcText.Format(UtcText.Parse(s)) == s; }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) { return false; }
    }
}
