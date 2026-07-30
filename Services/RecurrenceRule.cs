using System.Globalization;

namespace BackendApi.Services;

// Events redesign: a minimal RFC 5545-flavored recurrence rule - "RRULE-lite". This
// validates syntax only (FREQ=DAILY|WEEKLY|MONTHLY, optional INTERVAL/COUNT/UNTIL) so a
// malformed rule can't be stored; it deliberately does NOT expand a rule into individual
// calendar occurrences - full occurrence expansion for calendar display is a materially
// larger, separate feature and is not implemented this round (see events.recurrence_rule's
// schema comment).
public static class RecurrenceRule
{
    private static readonly HashSet<string> ValidFrequencies = ["DAILY", "WEEKLY", "MONTHLY"];

    public static bool IsValid(string rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return false;
        }

        var parts = rule.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var seenKeys = new HashSet<string>();
        var hasFreq = false;

        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                return false;
            }
            var key = kv[0].Trim().ToUpperInvariant();
            var value = kv[1].Trim();

            if (!seenKeys.Add(key))
            {
                return false; // duplicate key
            }

            switch (key)
            {
                case "FREQ":
                    if (!ValidFrequencies.Contains(value.ToUpperInvariant()))
                    {
                        return false;
                    }
                    hasFreq = true;
                    break;
                case "INTERVAL":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var interval) || interval < 1)
                    {
                        return false;
                    }
                    break;
                case "COUNT":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
                    {
                        return false;
                    }
                    break;
                case "UNTIL":
                    if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    {
                        return false;
                    }
                    break;
                default:
                    return false; // unrecognized key - reject rather than silently ignore
            }
        }

        if (seenKeys.Contains("COUNT") && seenKeys.Contains("UNTIL"))
        {
            return false; // RFC 5545: COUNT and UNTIL are mutually exclusive
        }

        return hasFreq;
    }
}
