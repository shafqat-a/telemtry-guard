---
id: FND-04
title: Core primitives (ITenantContext, TenantId, IClock)
phase: 0
workstream: foundation
depends_on: [FND-01]
size: S
spec_refs: [D11, "§4 System overview"]
detail_level: full
---

# FND-04: Core primitives (ITenantContext, TenantId, IClock)

## Objective
Implement the cross-cutting primitives in `TelemetryGuard.Core` that every other layer consumes: a strongly-typed `TenantId` value type, the `ITenantContext` abstraction with a set-once `TenantContext` implementation that throws when read before resolution, `TenantNotResolvedException`, and `IClock`/`SystemClock` for testable time. This project must have **zero dependency on ASP.NET Core** — it is referenced by Data, Analytics, RiskEngine, and Api alike.

## Spec context (self-contained)
- **Multi-tenancy correctness must not depend on developer discipline** (spec D11). The tenant identity flows through a scoped `ITenantContext`, populated exactly once per request by `TenantResolutionMiddleware` (task DAT-04, in the Api project) from either an API key (dashboard/API traffic) or the site key embedded in the JS snippet (beacon/click traffic). SQL (session-context stamping for Row-Level Security), Redis (`t:{tenantId}:…` key prefixes), and analytics (`tenant_id` injected into every query) all consume the same context.
- Because unresolved-tenant reads are a security bug, `TenantContext` must **fail loudly**: reading `TenantId` (or `SiteKey`) before resolution throws `TenantNotResolvedException` — never returns a default/empty value that would silently scope queries to nothing (or worse, to a zero GUID key shared by all tenants in Redis).
- `TenantId` wraps a `Guid` and rejects `Guid.Empty`, so a zero tenant can never be constructed via `Parse`/`TryParse`.
- `IClock` exists because verdict timestamps, velocity windows, grace-period workers, and retention math all need mockable time in tests; direct `DateTime.UtcNow` calls in domain code are forbidden from here on.
- No ASP.NET dependency: `TelemetryGuard.Core` stays a plain `net8.0` classlib (no `FrameworkReference`, no `Microsoft.AspNetCore.*` packages). DI registration of these types happens in the Api project (DAT-04/API-01), not here.

## Prerequisites
FND-01 is complete: `TelemetryGuard.Core` classlib exists at `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Core/` (net8.0, Nullable + TreatWarningsAsErrors on via `Directory.Build.props`), is referenced by RiskEngine.Contracts, RiskEngine, Analytics.Abstractions, Analytics.ClickHouse, Data, Integrations, Api, and the test projects. `tests/TelemetryGuard.Tests.Unit` exists with xunit 2.9.2.

## Implementation steps

1. **Create `TelemetryGuard.Core/Tenancy/TenantId.cs`:**

```csharp
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
```

2. **Create `TelemetryGuard.Core/Tenancy/ITenantContext.cs`:**

```csharp
namespace TelemetryGuard.Core.Tenancy;

/// <summary>
/// Ambient tenant identity for the current logical operation (one HTTP request,
/// one job execution). Populated exactly once by tenant resolution (DAT-04);
/// consumed by SQL session-context stamping (DAT-03), Redis key prefixing (RSK-03),
/// and analytics query scoping (ANA-04).
/// Reading TenantId or SiteKey before resolution throws TenantNotResolvedException.
/// </summary>
public interface ITenantContext
{
    TenantId TenantId { get; }

    /// <summary>The public site key the tenant embedded in their snippet/pixel,
    /// when resolution happened via site key; null when resolved via API key.</summary>
    string? SiteKey { get; }

    bool IsResolved { get; }
}
```

3. **Create `TelemetryGuard.Core/Tenancy/TenantNotResolvedException.cs`:**

```csharp
namespace TelemetryGuard.Core.Tenancy;

/// <summary>
/// Thrown when tenant-scoped state is read before tenant resolution ran.
/// Indicates a pipeline-ordering bug (e.g. an endpoint executing before
/// TenantResolutionMiddleware) — never a user error.
/// </summary>
public sealed class TenantNotResolvedException : InvalidOperationException
{
    public TenantNotResolvedException()
        : base("Tenant has not been resolved for this scope. " +
               "TenantResolutionMiddleware (DAT-04) must run before any tenant-scoped work.")
    {
    }
}
```

4. **Create `TelemetryGuard.Core/Tenancy/TenantContext.cs`:**

```csharp
namespace TelemetryGuard.Core.Tenancy;

/// <summary>
/// Set-once implementation of <see cref="ITenantContext"/>. Registered as a
/// scoped service (one instance per request/job scope) by the Api composition
/// root — this class itself is DI-framework-agnostic and NOT thread-safe:
/// a scope belongs to one logical operation.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private TenantId _tenantId;
    private string? _siteKey;

    public bool IsResolved { get; private set; }

    public TenantId TenantId
        => IsResolved ? _tenantId : throw new TenantNotResolvedException();

    public string? SiteKey
        => IsResolved ? _siteKey : throw new TenantNotResolvedException();

    /// <summary>
    /// Resolves the tenant for this scope. May be called exactly once;
    /// a second call throws InvalidOperationException. An empty tenantId throws
    /// ArgumentException — resolution with a zero tenant is always a bug.
    /// </summary>
    public void Resolve(TenantId tenantId, string? siteKey = null)
    {
        if (IsResolved)
        {
            throw new InvalidOperationException(
                "Tenant context is already resolved for this scope; it cannot be reassigned.");
        }

        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("TenantId must not be empty.", nameof(tenantId));
        }

        _tenantId = tenantId;
        _siteKey = siteKey;
        IsResolved = true;
    }
}
```

5. **Create `TelemetryGuard.Core/Time/IClock.cs`:**

```csharp
namespace TelemetryGuard.Core.Time;

/// <summary>Testable time source. Domain code must use this instead of
/// DateTime.UtcNow / DateTimeOffset.UtcNow.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
```

6. **Create `TelemetryGuard.Core/Time/SystemClock.cs`:**

```csharp
namespace TelemetryGuard.Core.Time;

/// <summary>Production clock. Register as a singleton.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
```

7. **Add unit tests** in `tests/TelemetryGuard.Tests.Unit/Core/` (namespace `TelemetryGuard.Tests.Unit.Core`), covering at minimum:
   - `TenantIdTests`: `Parse` round-trips a valid GUID; `Parse` throws `FormatException` on garbage, null-ish empty string, and `Guid.Empty.ToString()`; `TryParse` returns false for null / malformed / `Guid.Empty` and true (with correct value) for a valid GUID; `ToString()` returns the "D" format lowercase form; two `TenantId`s with the same Guid are equal (record-struct value equality); `default(TenantId).IsEmpty` is true.
   - `TenantContextTests`: fresh context has `IsResolved == false`; reading `TenantId` unresolved throws `TenantNotResolvedException`; reading `SiteKey` unresolved throws `TenantNotResolvedException`; after `Resolve(id, "sk_live_x")` all three properties return the set values; second `Resolve` throws `InvalidOperationException`; `Resolve(default(TenantId))` throws `ArgumentException`; `Resolve` with null siteKey leaves `SiteKey` null but `IsResolved` true.
   - `SystemClockTests`: `SystemClock.Instance.UtcNow` is within a few seconds of `DateTimeOffset.UtcNow` and has `Offset == TimeSpan.Zero`.

8. **Verify**: `dotnet build TelemetryGuard.sln -c Release` (zero warnings) and `dotnet test tests/TelemetryGuard.Tests.Unit -c Release` green. Confirm no ASP.NET reference: `grep -i aspnet TelemetryGuard.Core/TelemetryGuard.Core.csproj` returns nothing.

## Files to create or modify
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Core/Tenancy/TenantId.cs`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Core/Tenancy/ITenantContext.cs`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Core/Tenancy/TenantContext.cs`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Core/Tenancy/TenantNotResolvedException.cs`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Core/Time/IClock.cs`
- `/home/shafqat/git/shafqat/telemtry-guard/TelemetryGuard.Core/Time/SystemClock.cs`
- `/home/shafqat/git/shafqat/telemtry-guard/tests/TelemetryGuard.Tests.Unit/Core/TenantIdTests.cs`
- `/home/shafqat/git/shafqat/telemtry-guard/tests/TelemetryGuard.Tests.Unit/Core/TenantContextTests.cs`
- `/home/shafqat/git/shafqat/telemtry-guard/tests/TelemetryGuard.Tests.Unit/Core/SystemClockTests.cs`

## Acceptance criteria
- `dotnet build TelemetryGuard.sln -c Release` exits 0 with no warnings.
- `dotnet test tests/TelemetryGuard.Tests.Unit -c Release` passes with all the tests from step 7 present.
- `TelemetryGuard.Core.csproj` contains no `FrameworkReference` and no package references (plain classlib).
- Public API surface exactly as specified: `TenantId` (readonly record struct with `Value`, `IsEmpty`, `Parse`, `TryParse`, `ToString`), `ITenantContext` (`TenantId`, `SiteKey`, `IsResolved` — getters only), `TenantContext` (adds `Resolve`), `TenantNotResolvedException`, `IClock`, `SystemClock`.
- `TenantId.TryParse(Guid.Empty.ToString(), out _)` returns `false`.
- `new TenantContext().TenantId` throws `TenantNotResolvedException`.

## Testing
Unit tests only (step 7); no containers, no I/O. Keep tests in the `Core/` subfolder of the Unit test project so later workstreams can add sibling folders.

## Out of scope / guardrails
- **No ASP.NET Core dependency** in `TelemetryGuard.Core` — no middleware, no `HttpContext`, no DI registration extension for the Api here. Tenant resolution itself is DAT-04's job; connection stamping is DAT-03's.
- **Do not add a settable TenantId or a Reset/Clear method** — set-once is the contract; mutability would reopen the cross-tenant-leak class of bugs D11 exists to kill. **tenant_id is never optional** anywhere downstream; this type system is the root of that guarantee.
- **The set-once method is named `Resolve(TenantId, string? siteKey = null)` — there is no `Set` method, and none may be added as an alias.** Some consumer task snippets (API-04 step 3, API-05 step 2.0, API-06 worker step 5) drifted to `tenantContext.Set(new TenantId(...))`; those call sites must read `tenantContext.Resolve(new TenantId(...))` — fix the snippet, never this contract. DAT-04 and ANA-07 show the correct usage.
- **Do not add an "unresolved returns Guid.Empty" convenience path** — throwing is the design.
- No `AsyncLocal` ambient-context pattern — the context flows via scoped DI, explicitly.
- Do not put `FraudFeatureVector`, verdict DTOs (RSK-01), or analytics DTOs (ANA-01) in Core — they have owning projects.
- No EF Core, no data access of any kind in this project (Dapper-only rule lives in TelemetryGuard.Data, D9).
