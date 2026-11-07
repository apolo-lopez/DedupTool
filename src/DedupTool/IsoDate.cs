using System.Globalization;
using System.Text.RegularExpressions;

namespace DedupTool;

/// <summary>
/// UTC instant formatting (design D7): parse an ISO 8601 timestamp *with* a
/// required offset, convert to UTC, and format exactly
/// <c>yyyy-MM-ddTHH:mm:ssZ</c>. Returns null for anything unparseable —
/// callers treat that as invalid (the Validator already rejects offset-less
/// and non-ISO sentDates upstream; task 5.2 reuses this helper for the
/// SentDate column). Fractional seconds are accepted but truncated, since the
/// output format carries no fraction.
///
/// Implementation note (deviation from D7's format list): the
/// <c>yyyy-MM-ddTHH:mm:ssK</c> format silently *accepts* timestamps with no
/// offset at all, interpreting them in the machine's local zone — the exact
/// §6.1 nondeterminism trap D7 exists to avoid. So the offset requirement is
/// enforced by the same regex shape the Validator (task 1.x) uses, and only
/// then is the string parsed. Deterministic on any machine.
/// </summary>
public static partial class IsoDate
{
    /// <summary>
    /// ISO 8601 date-time with a required offset: <c>yyyy-MM-ddTHH:mm:ss[.f…](Z|±hh:mm)</c>.
    /// Same shape as the Validator's gate (design D4: offset-less timestamps
    /// are rejected as invalid, never silently zone-assumed).
    /// </summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex IsoWithOffsetRegex();

    public static string? FormatUtc(string isoWithOffset)
    {
        if (!IsoWithOffsetRegex().IsMatch(isoWithOffset))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                isoWithOffset,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var value))
        {
            return null;
        }

        return value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
