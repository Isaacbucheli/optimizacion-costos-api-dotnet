using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Configuration;

/// <summary>
/// Serializa todo DateTime saliente como UTC con sufijo 'Z'. La app guarda timestamps con
/// DateTime.UtcNow pero SqlDataReader.GetDateTime devuelve Kind=Unspecified, y System.Text.Json
/// los emitía sin zona ("2026-07-31T15:29:40"): el navegador los interpretaba como hora local
/// y mostraba la hora UTC tal cual (5 horas adelantada para Quito). Con el 'Z' el front puede
/// convertir correctamente a America/Guayaquil.
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // Unspecified: todo lo que sale de SQL; la app siempre persiste UTC.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        writer.WriteStringValue(utc);
    }
}
