namespace TelemetryGuard.Core.Tenancy;

/// <summary>
/// Strongly-typed tenant identifier. Wraps a non-empty <see cref="Guid"/>.
/// A default(TenantId) has Value == Guid.Empty and is only ever valid as
/// an uninitialized placeholder — Parse/TryParse never produce it.
/// </summary>
public readonly record struct TenantId(Guid Value)
{
    /// <summary>True when this is the uninitialized/default value.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Parses a GUID string into a TenantId. Throws FormatException on
    /// malformed input or Guid.Empty.</summary>
    public static TenantId Parse(string input)
    {
        if (!TryParse(input, out var id))
        {
            throw new FormatException(
                $"'{input}' is not a valid TenantId (expected a non-empty GUID).");
        }

        return id;
    }

    /// <summary>Returns false for null, malformed input, and Guid.Empty.</summary>
    public static bool TryParse(string? input, out TenantId tenantId)
    {
        if (Guid.TryParse(input, out var guid) && guid != Guid.Empty)
        {
            tenantId = new TenantId(guid);
            return true;
        }

        tenantId = default;
        return false;
    }

    /// <summary>Lowercase hyphenated GUID, e.g. "6f9619ff-8b86-d011-b42d-00cf4fc964ff".
    /// This is the canonical wire/storage/Redis-key form.</summary>
    public override string ToString() => Value.ToString("D");
}
