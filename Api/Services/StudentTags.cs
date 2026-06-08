using System.Text.RegularExpressions;

namespace Api.Services;

// Manual + derived student tags, ported from server/utils/studentTags.ts.
// Manual tags are stored as a JSON array in students.tags; derived tags come
// from applicant_type / residency / major. Step visibility is filtered on these.
public static class StudentTags
{
    private static string Slugify(string? value)
    {
        var s = (value ?? "").Trim().ToLowerInvariant();
        s = Regex.Replace(s, "[^a-z0-9]+", "-");
        s = Regex.Replace(s, "^-+|-+$", "");
        return s;
    }

    public static List<string> Manual(string? tagsJson) => Json.SafeParse<List<string>>(tagsJson, []);

    public static List<string> Derived(string? applicantType, string? residency, string? major)
    {
        var tags = new List<string>();
        var type = (applicantType ?? "").ToLowerInvariant();
        var res = (residency ?? "").ToLowerInvariant();
        var majorSlug = string.IsNullOrEmpty(major) ? "" : Slugify(major);

        if (type.Contains("transfer")) tags.Add("transfer");
        if (type.Contains("freshman")) tags.Add("freshman");
        if (type.Contains("readmit")) tags.Add("readmit");
        if (res.Contains("out-of-state")) tags.Add("out-of-state");
        if (res.Contains("in-state")) tags.Add("in-state");
        if (!string.IsNullOrEmpty(majorSlug)) tags.Add($"major:{majorSlug}");

        return tags.Distinct().ToList();
    }

    public static List<string> Merged(string? tagsJson, string? applicantType, string? residency, string? major) =>
        Manual(tagsJson).Concat(Derived(applicantType, residency, major)).Distinct().ToList();
}
