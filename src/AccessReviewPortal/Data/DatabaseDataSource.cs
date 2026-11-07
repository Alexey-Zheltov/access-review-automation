using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AccessReviewPortal.Options;
using Azure.Core;
using Azure.Identity;
using Npgsql;

namespace AccessReviewPortal.Data;

/// <summary>
/// Builds the Npgsql data source of the portal. With <see cref="DatabaseAuthentication.ManagedIdentity"/> the connection string
/// has no password: Npgsql uses a Microsoft Entra token for Azure Database for PostgreSQL instead, and renews it in the background.
/// </summary>
public static class DatabaseDataSource
{
    public const string EntraScope = "https://ossrdbms-aad.database.windows.net/.default";

    public static NpgsqlDataSource Build(string connectionString, DatabaseOptions options, TokenCredential? credential = null)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        if (options.Authentication == DatabaseAuthentication.ManagedIdentity)
        {
            if (!string.IsNullOrEmpty(builder.ConnectionStringBuilder.Password))
            {
                throw new InvalidOperationException(
                    "Database:Authentication is ManagedIdentity, but the connection string has a password. Remove the password.");
            }

            credential ??= new ManagedIdentityCredential(string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));
            var request = new TokenRequestContext([EntraScope]);
            // Tokens live for at least an hour; open connections stay valid after the token expires.
            builder.UsePeriodicPasswordProvider(
                async (_, ct) => (await credential.GetTokenAsync(request, ct)).Token,
                successRefreshInterval: TimeSpan.FromMinutes(30),
                failureRefreshInterval: TimeSpan.FromSeconds(30));
        }

        if (!string.IsNullOrWhiteSpace(options.RootCertificate))
        {
            // The Npgsql keyword "Root Certificate" reads only PEM files; this setting also takes DER files and the certificate text.
            builder.UseRootCertificate(LoadCertificate(options.RootCertificate.Trim()));
        }

        return builder.Build();
    }

    public static X509Certificate2 LoadCertificate(string value)
    {
        try
        {
            if (value.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                return X509Certificate2.CreateFromPem(value);
            }

            if (File.Exists(value))
            {
                return X509CertificateLoader.LoadCertificateFromFile(value);
            }

            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new InvalidOperationException(
                $"Database:RootCertificate: not a certificate (PEM text, base64 or the path of a DER or PEM file). {ex.Message}", ex);
        }
    }
}
