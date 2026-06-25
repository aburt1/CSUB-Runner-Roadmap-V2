namespace Api.Services;

// Integer parsing with JavaScript parseInt(value, 10) semantics, used to keep
// query-string number handling consistent with the public API contract.
// See IntegrationsController and Admin/StepsController for call sites.
public static class JsParse
{
    // Mimic JS parseInt(value, 10): skip leading whitespace, accept an optional sign,
    // then read leading digits. Returns null when no digits are present (NaN in JS).
    // Returns null on overflow (values that don't fit in int) rather than wrapping,
    // so the caller can treat overflow the same as a missing value.
    public static int? LeadingInt(string value)
    {
        var s = value.TrimStart();
        var index = 0;
        var sign = 1;
        if (index < s.Length && (s[index] == '+' || s[index] == '-'))
        {
            if (s[index] == '-') sign = -1;
            index++;
        }

        var start = index;
        while (index < s.Length && s[index] >= '0' && s[index] <= '9')
            index++;

        if (index == start)
            return null; // no digits -> NaN in JS

        // int.TryParse the digit span: returns false on overflow (null -> caller treats as NaN).
        if (!int.TryParse(s.AsSpan(start, index - start), out var digits))
            return null;

        return sign * digits;
    }
}
