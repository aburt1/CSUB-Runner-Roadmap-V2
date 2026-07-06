namespace Api.Data;

// The one canonical source for the writable `steps` column set.
//
// Every full-row INSERT (admin create/duplicate, term clone) shares the same column
// order via InsertColumns/InsertValues so a new column can't be silently dropped from
// one copy (which would NULL it on that path). Update()'s whitelist is the same set
// minus step_key (regenerated separately, never patched from the request body).
//
// Callers must still supply matching Dapper parameters (@title, @description, ...).
// The Seeder deliberately does NOT use this: its INSERT reorders the columns and hard-codes
// NULL for excluded_tags/contact_info/links instead of binding params — if you add a column
// here, update Api/Data/Seeder.cs's steps INSERT to match.
public static class StepColumns
{
    // Ordered writable columns for a full-row steps INSERT.
    public static readonly string[] Insert =
    {
        "title", "description", "icon", "sort_order", "deadline", "deadline_date",
        "guide_content", "links", "required_tags", "required_tag_mode", "excluded_tags",
        "contact_info", "term_id", "step_key", "is_active", "is_public", "is_optional",
    };

    // Columns an admin PUT may patch: the INSERT set minus step_key (regenerated, not patched).
    public static readonly string[] UpdateWhitelist =
        Insert.Where(c => c != "step_key").ToArray();

    // "(title, description, ...)" — the INSERT column list.
    public static readonly string InsertColumns = "(" + string.Join(", ", Insert) + ")";

    // "(@title, @description, ...)" — the matching VALUES list.
    public static readonly string InsertValues = "(" + string.Join(", ", Insert.Select(c => "@" + c)) + ")";
}
