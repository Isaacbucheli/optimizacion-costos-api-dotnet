using System.Security.Cryptography;
using System.Text;

namespace OptimizacionCostos.Api.Features.Boletin;

/// <summary>Fila normalizada de un aviso de retiro: (anuncio × recurso impactado).
/// Los avisos de Service Health no traen recurso → AzureResourceId null (nivel suscripción).</summary>
public sealed record RetirementRow(
    string Source, string AnnouncementKey, string SubscriptionId, string? AzureResourceId,
    string ResourceName, string ResourceType, string RetiringFeature, DateOnly? RetirementDate,
    string Title, string? Summary, string? RecommendedAction, string? LearnMoreUrl,
    bool Derived = false)
{
    public const string SourceAdvisor = "advisor";
    public const string SourceServiceHealth = "service_health";

    /// <summary>SHA256 de "{clientId}|{source}|{subscriptionId}|{clave}|{resourceId.lower}" → 32 bytes.
    /// NOTA: Derived no entra al fingerprint — distingue origen (info de Microsoft vs. inferido de inventario),
    /// nunca afecta la identidad de la fila para deduplicación.</summary>
    public byte[] Fingerprint(int clientId)
    {
        var raw = $"{clientId}|{Source}|{SubscriptionId}|{AnnouncementKey}|{(AzureResourceId ?? "").ToLowerInvariant()}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }
}

/// <summary>Urgencia calculada al LEER (no se persiste): así nunca queda desactualizada.</summary>
public static class BoletinUrgency
{
    public const string Retirado = "retirado";
    public const string Proximo = "proximo";       // vence en menos de 183 días
    public const string Programado = "programado";
    public const string SinFecha = "sin_fecha";

    public static string Classify(DateOnly? retirementDate, DateOnly today)
    {
        if (retirementDate is not DateOnly d) return SinFecha;
        if (d < today) return Retirado;
        return d.DayNumber - today.DayNumber < 183 ? Proximo : Programado;
    }
}
