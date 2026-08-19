namespace OptimizacionCostos.Api.Auth;

public static class BrowserSession
{
    public const string SessionCookieName = "__Host-bit_session";
    public const string CsrfCookieName = "__Host-bit_csrf";
    public const string CsrfHeaderName = "X-CSRF-Token";
    public static CookieOptions SessionCookie(TimeSpan lifetime) => new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.None, Path = "/", MaxAge = lifetime, IsEssential = true };
    public static CookieOptions CsrfCookie(TimeSpan lifetime) => new() { HttpOnly = false, Secure = true, SameSite = SameSiteMode.None, Path = "/", MaxAge = lifetime, IsEssential = true };
}
