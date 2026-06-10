using System.Text.Json;
using Api.Auth;
using Api.Services;

namespace Api.Data;

// Seeds default data on startup, ported from the seeding parts of server/db/init.ts
// (the live-activity simulator is intentionally dropped). All steps are guarded by
// "is this table empty?" so it is safe to run on every boot.
public static class Seeder
{
    public static async Task RunAsync(Db db, IConfiguration config, IHostEnvironment env)
    {
        var isProduction = env.IsProduction();

        await EnsureDefaultTermAsync(db);
        var termId = await ActiveTermIdAsync(db);

        // Terms exist but none is active (e.g. an admin deactivated them all): skip the
        // term-scoped seeds rather than writing rows with a dangling term_id of 0.
        if (termId > 0)
            await SeedChecklistAsync(db, termId);
        await StepKeys.EnsureAllAsync(db);

        await SeedDefaultAdminAsync(db, config, isProduction);
        await SeedDefaultIntegrationClientAsync(db, config, isProduction);

        if (!isProduction && termId > 0)
            await SeedSampleStudentsAsync(db, termId);
    }

    private static async Task EnsureDefaultTermAsync(Db db)
    {
        var count = await db.QueryOneAsync<int>("SELECT COUNT(*) FROM terms");
        if (count == 0)
        {
            await db.ExecuteAsync(
                "INSERT INTO terms (name, start_date, end_date, is_active) VALUES ('Fall 2026', '2026-08-01', '2026-12-31', 1)");
        }
    }

    private static Task<int> ActiveTermIdAsync(Db db) =>
        db.QueryOneAsync<int>("SELECT TOP 1 id FROM terms WHERE is_active = 1 ORDER BY id");

    private static async Task SeedChecklistAsync(Db db, int termId)
    {
        var stepCount = await db.QueryOneAsync<int>("SELECT COUNT(*) FROM steps");
        if (stepCount > 0) return;

        var path = Path.Combine(AppContext.BaseDirectory, "Data", "seed", "fall2026-onboarding-checklist.json");
        var items = JsonSerializer.Deserialize<List<ManifestItem>>(
            await File.ReadAllTextAsync(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        await db.TransactionAsync(async tx =>
        {
            foreach (var item in items)
            {
                var requiredTags = item.required_tags is { Count: > 0 }
                    ? JsonSerializer.Serialize(item.required_tags)
                    : null;

                await tx.ExecuteAsync(
                    @"INSERT INTO steps
                        (title, description, icon, sort_order, deadline, deadline_date, guide_content,
                         required_tags, required_tag_mode, excluded_tags, contact_info, links,
                         term_id, step_key, is_active, is_public, is_optional)
                      VALUES
                        (@title, @description, @icon, @sort_order, @deadline, @deadline_date, @guide_content,
                         @required_tags, @required_tag_mode, NULL, NULL, NULL,
                         @term_id, @step_key, 1, @is_public, @is_optional)",
                    new
                    {
                        item.title,
                        item.description,
                        item.icon,
                        item.sort_order,
                        item.deadline,
                        item.deadline_date,
                        item.guide_content,
                        required_tags = requiredTags,
                        required_tag_mode = item.required_tag_mode == "all" ? "all" : "any",
                        term_id = termId,
                        item.step_key,
                        item.is_public,
                        item.is_optional,
                    });
            }
        });
    }

    // A production admin password is unacceptable if it is missing, too short, the
    // committed dev default, or a placeholder from .env.example.
    public static bool IsWeakAdminPassword(string? password) =>
        string.IsNullOrEmpty(password)
        || password.Length < 12
        || password == "admin123"
        || password.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);

    private static async Task SeedDefaultAdminAsync(Db db, IConfiguration config, bool isProduction)
    {
        var count = await db.QueryOneAsync<int>("SELECT COUNT(*) FROM admin_users");
        if (count > 0) return;

        var email = config["Admin:DefaultEmail"] ?? "admin@csub.edu";
        var password = config["Admin:DefaultPassword"];
        // Fail safe in production: refuse to seed a missing/weak/default admin password.
        if (isProduction && IsWeakAdminPassword(password))
            throw new InvalidOperationException("Admin:DefaultPassword must be set to a strong, non-default value in Production. Refusing to seed default credentials.");
        password ??= "admin123";

        await db.ExecuteAsync(
            "INSERT INTO admin_users (email, password_hash, role, display_name) VALUES (@email, @hash, 'sysadmin', 'Admin')",
            new { email, hash = Passwords.Hash(password) });
    }

    private static async Task SeedDefaultIntegrationClientAsync(Db db, IConfiguration config, bool isProduction)
    {
        var count = await db.QueryOneAsync<int>("SELECT COUNT(*) FROM integration_clients");
        var defaultKey = config["Integration:DefaultKey"];
        if (count > 0 || (isProduction && string.IsNullOrEmpty(defaultKey))) return;

        var name = config["Integration:DefaultName"] ?? "PeopleSoft Dev";
        var key = defaultKey ?? "dev-integration-key";

        await db.ExecuteAsync(
            "INSERT INTO integration_clients (name, key_hash, is_active) VALUES (@name, @keyHash, 1)",
            new { name, keyHash = Passwords.Hash(key) });
    }

    // 50 deterministic sample students with realistic progress (dev only),
    // ported from the seed loop in server/db/init.ts. Uses a fixed RNG seed so
    // reseeding a fresh database is reproducible.
    private static async Task SeedSampleStudentsAsync(Db db, int termId)
    {
        var count = await db.QueryOneAsync<int>("SELECT COUNT(*) FROM students");
        if (count > 0) return;

        var stepRows = await db.QueryAllAsync<SeedStepRow>(
            "SELECT id, sort_order FROM steps WHERE term_id = @termId AND COALESCE(is_optional, 0) = 0 ORDER BY sort_order",
            new { termId });

        string[] firstNames =
        [
            "Sofia", "Miguel", "Emily", "Jose", "Maria", "David", "Isabella", "Carlos",
            "Ashley", "Angel", "Jasmine", "Luis", "Alyssa", "Diego", "Samantha", "Juan",
            "Brianna", "Daniel", "Gabriela", "Andres", "Maya", "Kevin", "Priya", "Omar",
            "Rachel", "Alejandro", "Destiny", "Marco", "Chloe", "Eduardo", "Fatima", "Ethan",
            "Lucia", "Ryan", "Vanessa", "Jorge", "Mia", "Anthony", "Karina", "Tyler",
            "Andrea", "Nathan", "Rosa", "Brandon", "Jessica", "Victor", "Lauren", "Adrian",
            "Natalie", "Christian",
        ];
        string[] lastNames =
        [
            "Garcia", "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Perez",
            "Sanchez", "Rivera", "Torres", "Flores", "Ramirez", "Morales", "Cruz", "Reyes",
            "Nguyen", "Patel", "Singh", "Chen", "Kim", "Johnson", "Williams", "Brown",
            "Davis", "Miller", "Wilson", "Moore", "Taylor", "Anderson", "Thomas",
            "Jackson", "White", "Harris", "Clark", "Lewis", "Walker", "Hall", "Allen",
            "Young", "King", "Wright", "Scott", "Adams", "Baker", "Nelson", "Carter",
            "Mitchell", "Campbell", "Roberts", "Phillips",
        ];
        string[] applicantTypes = ["First-Time Freshman", "Transfer", "First-Time Freshman", "Transfer", "Readmit"];
        string[] majors =
        [
            "Business Administration", "Computer Science", "Psychology", "Nursing",
            "Mechanical Engineering", "Biology", "Criminal Justice", "Kinesiology",
            "Sociology", "Liberal Studies",
        ];
        string[] residencies = ["In-State", "In-State", "In-State", "Out-of-State"];
        string[][] manualTagOptions =
        [
            ["first-gen"], ["honors"], ["eop"], ["athlete"], ["veteran"],
            ["first-gen", "honors"], [], [],
        ];

        var progressionWeights = new (int StepsCompleted, int Weight)[]
        {
            (0, 2), (1, 3), (2, 5), (3, 6), (4, 7), (5, 8), (6, 7), (7, 5), (8, 4), (9, 3),
        };
        var progressionPool = new List<int>();
        foreach (var (stepsCompleted, weight) in progressionWeights)
            for (var i = 0; i < weight; i++) progressionPool.Add(stepsCompleted);

        var rng = new Random(20260608);
        var now = DateTime.UtcNow;

        await db.TransactionAsync(async tx =>
        {
            for (var i = 0; i < 50; i++)
            {
                var first = firstNames[i];
                var last = lastNames[i % lastNames.Length];
                var name = $"{first} {last}";
                var email = $"{first.ToLowerInvariant()}{char.ToLowerInvariant(last[0])}@csub.edu";
                var id = $"seed-student-{(i + 1):D3}";
                var azureId = $"azure-{id}";
                var applicantType = applicantTypes[i % applicantTypes.Length];
                var major = majors[i % majors.Length];
                var residency = residencies[i % residencies.Length];
                var emplid = $"00{1000000 + i}";
                var preferredName = i % 6 == 0 ? first : null;
                var phone = $"(661) 654-{1200 + i:D4}";
                var lastSyncedAt = now.AddHours(-(i % 10));
                var manualTags = manualTagOptions[i % manualTagOptions.Length];

                var daysAgo = rng.Next(1, 61);
                var createdAt = now.AddDays(-daysAgo);

                await tx.ExecuteAsync(
                    @"INSERT INTO students
                        (id, display_name, email, azure_id, tags, term_id, created_at, emplid,
                         preferred_name, phone, applicant_type, major, residency, admit_term, last_synced_at)
                      VALUES
                        (@id, @name, @email, @azureId, @tags, @termId, @createdAt, @emplid,
                         @preferredName, @phone, @applicantType, @major, @residency, 'Fall 2026', @lastSyncedAt)",
                    new
                    {
                        id, name, email, azureId,
                        tags = manualTags.Length > 0 ? JsonSerializer.Serialize(manualTags) : null,
                        termId, createdAt, emplid, preferredName, phone,
                        applicantType, major, residency, lastSyncedAt,
                    });

                var stepsCompleted = progressionPool[i % progressionPool.Count];
                for (var j = 0; j < stepsCompleted && j < stepRows.Count; j++)
                {
                    var completionDaysAgo = Math.Max(daysAgo - j * 2 - rng.Next(0, 3), 1);
                    var completedAt = now.AddDays(-completionDaysAgo);
                    var status = rng.NextDouble() < 0.05 ? "waived" : "completed";

                    await tx.ExecuteAsync(
                        "INSERT INTO student_progress (student_id, step_id, completed_at, status) VALUES (@id, @stepId, @completedAt, @status)",
                        new { id, stepId = stepRows[j].id, completedAt, status });
                }
            }
        });
    }

    private sealed class ManifestItem
    {
        public string step_key { get; set; } = "";
        public string title { get; set; } = "";
        public string? description { get; set; }
        public string? icon { get; set; }
        public int sort_order { get; set; }
        public string? deadline { get; set; }
        public string? deadline_date { get; set; }
        public string? guide_content { get; set; }
        public List<string>? required_tags { get; set; }
        public string? required_tag_mode { get; set; }
        public int is_public { get; set; }
        public int is_optional { get; set; }
    }

    private sealed class SeedStepRow
    {
        public int id { get; set; }
        public int sort_order { get; set; }
    }
}
