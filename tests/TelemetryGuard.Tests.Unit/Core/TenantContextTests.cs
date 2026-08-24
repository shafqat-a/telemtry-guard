using TelemetryGuard.Core.Tenancy;

namespace TelemetryGuard.Tests.Unit.Core;

public class TenantContextTests
{
    [Fact]
    public void FreshContext_IsNotResolved()
    {
        var context = new TenantContext();

        Assert.False(context.IsResolved);
    }

    [Fact]
    public void Reading_TenantId_Unresolved_Throws_TenantNotResolvedException()
    {
        var context = new TenantContext();

        Assert.Throws<TenantNotResolvedException>(() => context.TenantId);
    }

    [Fact]
    public void Reading_SiteKey_Unresolved_Throws_TenantNotResolvedException()
    {
        var context = new TenantContext();

        Assert.Throws<TenantNotResolvedException>(() => context.SiteKey);
    }

    [Fact]
    public void Resolve_Sets_AllProperties()
    {
        var context = new TenantContext();
        var id = new TenantId(Guid.NewGuid());

        context.Resolve(id, "sk_live_x");

        Assert.True(context.IsResolved);
        Assert.Equal(id, context.TenantId);
        Assert.Equal("sk_live_x", context.SiteKey);
        Assert.Empty(context.Scopes);   // site-key resolutions carry no scopes
    }

    [Fact]
    public void Resolve_CopiesScopes_AndUnresolvedScopesThrow()
    {
        var context = new TenantContext();
        Assert.Throws<TenantNotResolvedException>(() => context.Scopes);

        var granted = new List<string> { ApiKeyScopes.Admin, ApiKeyScopes.Report };
        context.Resolve(new TenantId(Guid.NewGuid()), siteKey: null, scopes: granted);
        granted.Clear();   // the context must not alias the caller's list

        Assert.Equal(new[] { ApiKeyScopes.Admin, ApiKeyScopes.Report }, context.Scopes);
        Assert.Null(context.SiteKey);
    }

    [Fact]
    public void SecondResolve_Throws_InvalidOperationException()
    {
        var context = new TenantContext();
        context.Resolve(new TenantId(Guid.NewGuid()), "sk_live_x");

        Assert.Throws<InvalidOperationException>(
            () => context.Resolve(new TenantId(Guid.NewGuid())));
    }

    [Fact]
    public void Resolve_WithDefaultTenantId_Throws_ArgumentException()
    {
        var context = new TenantContext();

        Assert.Throws<ArgumentException>(() => context.Resolve(default));
    }

    [Fact]
    public void Resolve_WithNullSiteKey_LeavesSiteKeyNull_ButResolved()
    {
        var context = new TenantContext();
        var id = new TenantId(Guid.NewGuid());

        context.Resolve(id, siteKey: null);

        Assert.True(context.IsResolved);
        Assert.Equal(id, context.TenantId);
        Assert.Null(context.SiteKey);
    }
}
