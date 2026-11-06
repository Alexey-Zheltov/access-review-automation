using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using AccessReviewPortal.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace AccessReviewPortal.Services.Identity;

public sealed class DeviceLoginSession
{
    public required string Id { get; init; }
    public required string Binding { get; init; }
    public required string[] Scopes { get; init; }
    public required string ReturnUrl { get; init; }
    public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cancellation { get; } = new(TimeSpan.FromMinutes(16));

    public volatile string Status = "Starting";
    public string? UserCode { get; set; }
    public string? VerificationUrl { get; set; }
    public DateTimeOffset? ExpiresOn { get; set; }
    public string? Error { get; set; }
    public ClaimsPrincipal? Principal { get; set; }
}

/// <summary>
/// "Sign in with a code from another device" (OAuth 2.0 device authorization grant). The user enters the code on a
/// phone or another computer, so no credentials are typed on the machine that shows the portal.
/// The app registration needs "Allow public client flows" (isFallbackPublicClient = true).
/// Tokens are kept in the PostgreSQL distributed cache, encrypted with Data Protection.
/// </summary>
public sealed class DeviceCodeLoginService(
    IOptions<AzureAdOptions> azureAd,
    IDistributedCache cache,
    IDataProtectionProvider dataProtection,
    ILogger<DeviceCodeLoginService> logger)
{
    public static readonly string[] SignInScopes = ["https://management.azure.com/user_impersonation"];

    private readonly ConcurrentDictionary<string, DeviceLoginSession> _sessions = new();
    private readonly IDataProtector _protector = dataProtection.CreateProtector("AccessReviewPortal.DeviceCodeTokenCache");

    public DeviceLoginSession Start(string[] scopes, string returnUrl)
    {
        foreach (var old in _sessions.Values.Where(s => DateTimeOffset.UtcNow - s.CreatedUtc > TimeSpan.FromMinutes(20)).ToList())
        {
            _sessions.TryRemove(old.Id, out _);
            old.Cancellation.Dispose();
        }

        var session = new DeviceLoginSession
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            Binding = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Scopes = scopes.Length > 0 ? scopes : SignInScopes,
            ReturnUrl = returnUrl
        };
        _sessions[session.Id] = session;
        _ = Task.Run(() => RunAsync(session));
        return session;
    }

    public DeviceLoginSession? Get(string id, string binding)
    {
        if (!_sessions.TryGetValue(id, out var session)) return null;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(session.Binding), System.Text.Encoding.UTF8.GetBytes(binding ?? ""))
            ? session
            : null;
    }

    public void Remove(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            session.Cancellation.Dispose();
        }
    }

    /// <summary>Token for a user who signed in with a device code. Throws UserConsentRequiredException when a new code is needed.</summary>
    public async Task<string> AcquireTokenSilentAsync(ClaimsPrincipal user, string[] scopes, CancellationToken ct)
    {
        var accountId = user.FindFirst(PortalClaims.MsalAccountId)?.Value;
        if (string.IsNullOrEmpty(accountId))
        {
            throw new UserConsentRequiredException(scopes, isDeviceCode: true, "The session has no token cache entry. Sign in again.");
        }

        var app = BuildApp(accountId);
        var account = await app.GetAccountAsync(accountId);
        if (account is null)
        {
            throw new UserConsentRequiredException(scopes, isDeviceCode: true, "The cached tokens expired. Sign in again with a code.");
        }

        try
        {
            var result = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            throw new UserConsentRequiredException(scopes, isDeviceCode: true, ex.Message, ex);
        }
    }

    public async Task SignOutAsync(ClaimsPrincipal user)
    {
        var accountId = user.FindFirst(PortalClaims.MsalAccountId)?.Value;
        if (!string.IsNullOrEmpty(accountId))
        {
            await cache.RemoveAsync(CacheKey(accountId));
        }
    }

    private async Task RunAsync(DeviceLoginSession session)
    {
        try
        {
            // The account is known only after the sign-in: keep the new token cache in memory, then store it under the account id.
            byte[]? tokenCache = null;
            var app = BuildApp(null, bytes => tokenCache = bytes);
            var result = await app.AcquireTokenWithDeviceCode(session.Scopes, code =>
                {
                    session.UserCode = code.UserCode;
                    session.VerificationUrl = code.VerificationUrl;
                    session.ExpiresOn = code.ExpiresOn;
                    session.Status = "Waiting";
                    return Task.CompletedTask;
                })
                .ExecuteAsync(session.Cancellation.Token);

            if (tokenCache is not null)
            {
                await SaveAsync(result.Account.HomeAccountId.Identifier, tokenCache, CancellationToken.None);
            }
            session.Principal = BuildPrincipal(result);
            session.Status = "Completed";
            logger.LogInformation("Device code sign-in completed for {Upn}.", session.Principal.GetUpn());
        }
        catch (OperationCanceledException)
        {
            session.Status = "Expired";
            session.Error = "The code expired. Start again.";
        }
        catch (MsalException ex)
        {
            session.Status = "Failed";
            session.Error = ex.ErrorCode switch
            {
                "authorization_declined" => "Sign-in was declined on the other device.",
                "code_expired" or "expired_token" => "The code expired. Start again.",
                _ => $"{ex.ErrorCode}: {ex.Message.Split('\n')[0]}"
            };
            logger.LogWarning("Device code sign-in failed: {Error}", session.Error);
        }
        catch (Exception ex)
        {
            session.Status = "Failed";
            session.Error = ex.Message;
            logger.LogError(ex, "Device code sign-in failed");
        }
    }

    private static ClaimsPrincipal BuildPrincipal(AuthenticationResult result)
    {
        var source = result.ClaimsPrincipal?.Claims.ToList() ?? [];
        string? Claim(string type) => source.FirstOrDefault(c => c.Type == type)?.Value;

        var identity = new ClaimsIdentity("DeviceCode", "name", "roles");
        void Add(string type, string? value)
        {
            if (!string.IsNullOrEmpty(value)) identity.AddClaim(new Claim(type, value));
        }

        Add("oid", Claim("oid") ?? result.Account.HomeAccountId.ObjectId);
        Add("tid", Claim("tid") ?? result.TenantId);
        Add("preferred_username", Claim("preferred_username") ?? result.Account.Username);
        Add("name", Claim("name") ?? result.Account.Username);
        foreach (var role in source.Where(c => c.Type == "roles").Select(c => c.Value).Distinct())
        {
            Add("roles", role);
        }
        Add(PortalClaims.LoginMethod, PortalClaims.LoginDeviceCode);
        Add(PortalClaims.MsalAccountId, result.Account.HomeAccountId.Identifier);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Public client applications get no SuggestedCacheKey from MSAL, so the cache partition is bound here:
    /// one entry per account (home account id). Without an account (device code sign-in) the cache is handed to <paramref name="onChanged"/>.
    /// </summary>
    private IPublicClientApplication BuildApp(string? accountId, Action<byte[]>? onChanged = null)
    {
        var o = azureAd.Value;
        var app = PublicClientApplicationBuilder.Create(o.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, o.TenantId)
            .WithClientName("AccessReviewPortal")
            .Build();

        app.UserTokenCache.SetBeforeAccessAsync(async args =>
        {
            if (accountId is null) return;
            var protectedBytes = await cache.GetAsync(CacheKey(accountId), args.CancellationToken);
            if (protectedBytes is null) return;
            try
            {
                args.TokenCache.DeserializeMsalV3(_protector.Unprotect(protectedBytes));
            }
            catch (CryptographicException)
            {
                // Keys changed: the user signs in again.
            }
        });
        app.UserTokenCache.SetAfterAccessAsync(async args =>
        {
            if (!args.HasStateChanged) return;
            var bytes = args.TokenCache.SerializeMsalV3();
            if (accountId is null)
            {
                onChanged?.Invoke(bytes);
                return;
            }
            await SaveAsync(accountId, bytes, args.CancellationToken);
        });
        return app;
    }

    private Task SaveAsync(string accountId, byte[] tokenCache, CancellationToken ct) =>
        cache.SetAsync(CacheKey(accountId), _protector.Protect(tokenCache),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(14) }, ct);

    private static string CacheKey(string accountId) => "devicecode-msal:" + accountId;
}
