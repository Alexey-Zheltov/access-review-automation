using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace AccessReviewPortal.Endpoints;

/// <summary>Sign-in pages: Microsoft Entra ID (OpenID Connect), sign-in with a code from another device, access key fallback.</summary>
public static class AccountEndpoints
{
    private const string DeviceCookie = "arp_device_login";

    public static void MapAccountEndpoints(this WebApplication app)
    {
        var auth = app.Services.GetRequiredService<IOptions<PortalAuthOptions>>().Value;
        var group = app.MapGroup("/account").AllowAnonymous();

        group.MapGet("/signin", (string? returnUrl, string? error) =>
        {
            var target = SafeReturnUrl(returnUrl);
            if (!auth.IsEntraId) return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString(target)}");
            var body = new StringBuilder();
            body.Append("<p>Sign in with your Microsoft Entra ID account. The portal uses your own rights for any change.</p>");
            if (error is not null) body.Append($"<div class=\"err\">{Html(error)}</div>");
            body.Append($"<p><a class=\"btn\" href=\"/account/signin/microsoft?returnUrl={Uri.EscapeDataString(target)}\">Sign in with Microsoft</a></p>");
            if (auth.EnableDeviceCodeSignIn)
            {
                body.Append($"<p><a class=\"btn secondary\" href=\"/account/device-login?returnUrl={Uri.EscapeDataString(target)}\">Sign in with a code from another device</a></p>");
                body.Append("<p class=\"hint\">Use the code option when you do not want to type your credentials on this computer: " +
                            "you enter a short code on your phone or another device.</p>");
            }
            return Html("Access Review Portal - sign in", body.ToString());
        });

        group.MapGet("/signin/microsoft", (string? returnUrl) =>
            Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, [OpenIdConnectDefaults.AuthenticationScheme]));

        group.MapGet("/device-login", (HttpContext http, string? returnUrl, string? scopes, DeviceCodeLoginService deviceCode, IDataProtectionProvider dp) =>
        {
            if (!auth.IsEntraId || !auth.EnableDeviceCodeSignIn) return Results.NotFound();
            var requested = (scopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s.StartsWith("https://management.azure.com/", StringComparison.OrdinalIgnoreCase)
                            || s.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var session = deviceCode.Start(requested, SafeReturnUrl(returnUrl));
            http.Response.Cookies.Append(DeviceCookie, dp.CreateProtector(DeviceCookie).Protect($"{session.Id}|{session.Binding}"), new CookieOptions
            {
                HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/account/device-login", MaxAge = TimeSpan.FromMinutes(20)
            });
            var purpose = requested.Length == 0
                ? "to sign in to the Access Review Portal"
                : "to give the portal access to " + string.Join(", ", requested.Select(s => s.Split('/').Last()));
            return Html("Sign in with a code", $$"""
                <p>On your phone or another device open <a id="url" href="https://microsoft.com/devicelogin" target="_blank" rel="noopener">https://microsoft.com/devicelogin</a>
                and enter this code {{Html(purpose)}}:</p>
                <div id="code" class="code">...</div>
                <p id="status" class="hint">Requesting a code...</p>
                <p><a href="/account/signin">Cancel</a></p>
                <script>
                  async function poll() {
                    try {
                      const r = await fetch('/account/device-login/status', { credentials: 'same-origin', cache: 'no-store' });
                      const s = await r.json();
                      if (s.userCode) document.getElementById('code').textContent = s.userCode;
                      if (s.verificationUrl) { const a = document.getElementById('url'); a.href = s.verificationUrl; a.textContent = s.verificationUrl; }
                      if (s.status === 'Completed') { document.getElementById('status').textContent = 'Signed in. Opening the portal...'; location.href = '/account/device-login/complete'; return; }
                      if (s.status === 'Failed' || s.status === 'Expired' || s.status === 'Unknown') { document.getElementById('status').textContent = (s.error || 'The sign-in did not complete.') + ' Reload the page to get a new code.'; return; }
                      document.getElementById('status').textContent = 'Waiting for you to enter the code' + (s.expiresOn ? ' (valid until ' + new Date(s.expiresOn).toLocaleTimeString() + ')' : '') + '...';
                    } catch (e) { }
                    setTimeout(poll, 2000);
                  }
                  poll();
                </script>
                """);
        });

        group.MapGet("/device-login/status", (HttpContext http, DeviceCodeLoginService deviceCode, IDataProtectionProvider dp) =>
        {
            var session = ReadSession(http, deviceCode, dp);
            return session is null
                ? Results.Json(new { status = "Unknown", error = "The sign-in session was not found." })
                : Results.Json(new { status = session.Status, userCode = session.UserCode, verificationUrl = session.VerificationUrl, expiresOn = session.ExpiresOn, error = session.Error });
        });

        group.MapGet("/device-login/complete", async (HttpContext http, DeviceCodeLoginService deviceCode, IDataProtectionProvider dp) =>
        {
            var session = ReadSession(http, deviceCode, dp);
            if (session is not { Status: "Completed", Principal: not null })
            {
                return Results.Redirect("/account/signin?error=" + Uri.EscapeDataString(session?.Error ?? "The sign-in did not complete."));
            }
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, session.Principal,
                new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(auth.SessionHours) });
            deviceCode.Remove(session.Id);
            http.Response.Cookies.Delete(DeviceCookie, new CookieOptions { Path = "/account/device-login" });
            return Results.Redirect(session.ReturnUrl);
        });

        group.MapGet("/signout", async (HttpContext http, DeviceCodeLoginService deviceCode) =>
        {
            var user = http.User;
            if (user.IsDeviceCodeLogin())
            {
                await deviceCode.SignOutAsync(user);
            }
            if (auth.IsEntraId && user.GetLoginMethod() == PortalClaims.LoginOidc && user.Identity?.IsAuthenticated == true)
            {
                return Results.SignOut(new AuthenticationProperties { RedirectUri = "/account/signed-out" },
                    [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
            }
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/account/signed-out");
        });

        group.MapGet("/signed-out", () => Html("Signed out", "<p>You are signed out.</p><p><a class=\"btn\" href=\"/account/signin\">Sign in again</a></p>"));

        group.MapGet("/denied", (HttpContext http, IOptions<PortalAuthorizationOptions> roles) => Html("No access",
            $"<p>You are signed in as <b>{Html(http.User.GetUpn())}</b>, but you do not have a role in the Access Review Portal.</p>" +
            $"<p>Ask an administrator to assign the app role <b>{Html(roles.Value.ReviewerRole)}</b> or <b>{Html(roles.Value.AdminRole)}</b> " +
            "to your account (Enterprise applications &gt; Access Review Portal &gt; Users and groups), then sign in again.</p>" +
            "<p><a class=\"btn secondary\" href=\"/account/signout\">Sign out</a></p>"));

        // Access key fallback (Authentication__Mode=AccessKey).
        app.MapGet("/login", (string? returnUrl, string? error) =>
        {
            if (auth.IsEntraId) return Results.Redirect("/account/signin?returnUrl=" + Uri.EscapeDataString(SafeReturnUrl(returnUrl)));
            return Html("Access Review Portal - sign in", $"""
                {(error is null ? "" : "<div class=\"err\">The access key is not correct.</div>")}
                <form method="post" action="/login">
                  <label for="key">Access key</label>
                  <input id="key" name="key" type="password" autofocus />
                  <input type="hidden" name="returnUrl" value="{Html(SafeReturnUrl(returnUrl))}" />
                  <button class="btn" type="submit">Sign in</button>
                </form>
                <p class="hint">Access key mode: review only. Revoke needs Microsoft Entra ID sign-in.</p>
                """);
        }).AllowAnonymous();

        app.MapPost("/login", async (HttpContext http, IOptions<PortalOptions> portal) =>
        {
            if (auth.IsEntraId) return Results.NotFound();
            var form = await http.Request.ReadFormAsync();
            var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());
            var expected = portal.Value.AccessKey ?? "";
            if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(form["key"].ToString()), Encoding.UTF8.GetBytes(expected)))
            {
                return Results.Redirect($"/login?error=1&returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            var identity = new ClaimsIdentity(
            [
                new Claim("name", "Access key user"),
                new Claim("preferred_username", "access-key"),
                new Claim(PortalClaims.LoginMethod, PortalClaims.LoginAccessKey)
            ], "AccessKey", "name", "roles");
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
                new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(auth.SessionHours) });
            return Results.Redirect(returnUrl);
        }).AllowAnonymous().DisableAntiforgery();
    }

    private static DeviceLoginSession? ReadSession(HttpContext http, DeviceCodeLoginService deviceCode, IDataProtectionProvider dp)
    {
        if (!http.Request.Cookies.TryGetValue(DeviceCookie, out var value)) return null;
        try
        {
            var parts = dp.CreateProtector(DeviceCookie).Unprotect(value).Split('|');
            return parts.Length == 2 ? deviceCode.Get(parts[0], parts[1]) : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    private static string Html(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static IResult Html(string title, string body) => Results.Content($$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
        <title>{{Html(title)}}</title>
        <style>
          :root { color-scheme: dark; }
          body { font-family: Segoe UI, Arial, sans-serif; color: #ccccdc; background: #111217; display: flex; justify-content: center; padding-top: 10vh; margin: 0; }
          .box { background: #181b1f; padding: 28px 32px; border: 1px solid #2c3036; border-radius: 4px; width: 440px; }
          h1 { font-size: 19px; margin: 0 0 16px 0; color: #e9e9f2; }
          a { color: #6e9fff; }
          .btn { display: inline-block; padding: 9px 18px; background: #3d71d9; color: #fff; border: 1px solid #3d71d9; border-radius: 4px; cursor: pointer; text-decoration: none; font-size: 14px; }
          .btn:hover { background: #3264c8; }
          .btn.secondary { background: transparent; color: #6e9fff; border: 1px solid #3d71d9; }
          .btn.secondary:hover { background: #22252b; }
          .err { color: #ff7383; margin-bottom: 10px; }
          .hint { color: #8e8e9f; font-size: 13px; }
          .code { font-family: Consolas, monospace; font-size: 30px; letter-spacing: 3px; padding: 10px 0; color: #e9e9f2; }
          input[type=password] { width: 100%; padding: 8px; margin: 6px 0 14px 0; box-sizing: border-box; color: #ccccdc; background: #111217; border: 1px solid #2c3036; border-radius: 4px; }
        </style></head>
        <body><div class="box"><h1>{{Html(title)}}</h1>{{body}}</div></body></html>
        """, "text/html");
}
