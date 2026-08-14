using Microsoft.AspNetCore.Authentication.Cookies;
using TelemetryGuard.Portal.Api;
using TelemetryGuard.Portal.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z' ";
});

builder.Services.AddOptions<PortalOptions>()
    .Bind(builder.Configuration.GetSection(PortalOptions.SectionName))
    .Validate(o => Uri.TryCreate(o.ApiBaseUrl, UriKind.Absolute, out _),
        "Portal:ApiBaseUrl must be an absolute URL (e.g. http://localhost:5120).")
    .ValidateOnStart();

builder.Services.AddMemoryCache();          // sign-in throttle
builder.Services.AddHttpContextAccessor();  // PortalApiClient reads the session key
builder.Services.AddAdminApiClient(builder.Configuration);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "tg_portal";
        o.Cookie.HttpOnly = true;                 // the API key is NEVER reachable from script
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        o.LoginPath = "/signin";
        o.LogoutPath = "/signout";
        o.AccessDeniedPath = "/signin";
        o.ExpireTimeSpan = TimeSpan.FromHours(
            builder.Configuration.GetValue("Portal:SessionHours", 8));
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");                 // every page requires a session…
    o.Conventions.AllowAnonymousToPage("/SignIn");      // …except these two
    o.Conventions.AllowAnonymousToPage("/Error");
});

var app = builder.Build();

app.UseExceptionHandler("/Error");
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

// Security headers. script-src 'none' is enforceable because this app ships ZERO
// JavaScript — keep it that way (see the guardrails section in the task doc).
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'none'; style-src 'self'; img-src 'self'; " +
        "form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h.CacheControl = "no-store";   // tenant data must never be cached by a proxy
    await next();
});

app.UseStaticFiles();   // wwwroot/css/portal.css only
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.Run();
