using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Serialization;

// Serializes DateTime as ISO-8601 UTC with a trailing 'Z' and millisecond precision.
// SQL Server datetimes come back from Dapper as DateTimeKind.Unspecified; we store UTC,
// so we treat unspecified/local as UTC on the way out. Also covers DateTime? automatically.
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        writer.WriteStringValue(utc.ToString(Format, CultureInfo.InvariantCulture));
    }
}
