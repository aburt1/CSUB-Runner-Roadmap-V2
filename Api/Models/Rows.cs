namespace Api.Models;

// Plain row models matching the SQL tables, 1:1 with the old server/types/models.ts.
//
// Property names are snake_case on purpose: they match the database columns
// (so Dapper maps them with no configuration) AND the JSON wire format the
// existing React client expects (so responses that return rows verbatim need
// no renaming). Hand-built responses (e.g. auth) use anonymous objects instead.

public sealed class Student
{
    public string id { get; set; } = "";
    public string? display_name { get; set; }
    public string? email { get; set; }
    public string? azure_id { get; set; }
    public string? tags { get; set; }
    public string? emplid { get; set; }
    public string? preferred_name { get; set; }
    public string? phone { get; set; }
    public string? applicant_type { get; set; }
    public string? major { get; set; }
    public string? residency { get; set; }
    public int? term_id { get; set; }
    public DateTime? last_synced_at { get; set; }
    public DateTime? last_api_check_at { get; set; }
    public DateTime created_at { get; set; }
}

public sealed class Step
{
    public int id { get; set; }
    public string title { get; set; } = "";
    public string? description { get; set; }
    public string? icon { get; set; }
    public int sort_order { get; set; }
    public string? deadline { get; set; }
    public string? deadline_date { get; set; }
    public string? guide_content { get; set; }
    public string? links { get; set; }
    public string? required_tags { get; set; }
    public string? required_tag_mode { get; set; }
    public string? excluded_tags { get; set; }
    public string? contact_info { get; set; }
    public int? term_id { get; set; }
    public string? step_key { get; set; }
    public int? is_public { get; set; }
    public int? is_optional { get; set; }
    public int? is_active { get; set; }
}

public sealed class AdminUser
{
    public int id { get; set; }
    public string email { get; set; } = "";
    public string password_hash { get; set; } = "";
    public string role { get; set; } = "viewer";
    public string display_name { get; set; } = "";
    public int is_active { get; set; }
    public string? azure_id { get; set; }
    public DateTime created_at { get; set; }
}

public sealed class Term
{
    public int id { get; set; }
    public string name { get; set; } = "";
    public string? start_date { get; set; }
    public string? end_date { get; set; }
    public int is_active { get; set; }
    public DateTime created_at { get; set; }
}

public sealed class AuditLogEntry
{
    public int id { get; set; }
    public string entity_type { get; set; } = "";
    public string entity_id { get; set; } = "";
    public string action { get; set; } = "";
    public string changed_by { get; set; } = "";
    public string? details { get; set; }
    public DateTime created_at { get; set; }
}

public sealed class IntegrationClient
{
    public int id { get; set; }
    public string name { get; set; } = "";
    public string key_hash { get; set; } = "";
    public int is_active { get; set; }
    public DateTime created_at { get; set; }
}

public sealed class StepApiCheck
{
    public int id { get; set; }
    public int step_id { get; set; }
    public bool is_enabled { get; set; }
    public string http_method { get; set; } = "GET";
    public string url { get; set; } = "";
    public string? auth_type { get; set; }
    public string? auth_credentials { get; set; }
    public string? headers { get; set; }
    public string student_param_name { get; set; } = "studentId";
    public string student_param_source { get; set; } = "emplid";
    public string response_field_path { get; set; } = "";
    public DateTime created_at { get; set; }
    public DateTime updated_at { get; set; }
}
