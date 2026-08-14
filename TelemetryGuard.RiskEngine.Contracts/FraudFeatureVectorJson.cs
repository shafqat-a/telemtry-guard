using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>
/// RSK-08: the ONE canonical JSON (de)serialization contract for
/// <see cref="FraudFeatureVector"/>, shared by every writer (API-06's
/// VerdictFinalizer, populating tg_events.features at scoring time) and every reader
/// (TelemetryGuard.Training's Trainer, deserializing tg_events.features back for
/// model training). Both sides MUST use these exact options — a drift here silently
/// corrupts every NaN round-trip.
///
/// <see cref="JsonNumberHandling.AllowNamedFloatingPointLiterals"/> is mandatory:
/// FraudFeatureVector's float members default to NaN (spec §7, "missing ≠ zero") and
/// System.Text.Json throws on NaN/Infinity without this flag. System.Text.Json ships
/// in the shared framework — referencing it adds no NuGet package (this project stays
/// a zero-package-dependency POCO library).
/// </summary>
public static class FraudFeatureVectorJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}
