using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AccessReviewPortal.Data;
using AccessReviewPortal.Options;
using Azure.Core;

namespace AccessReviewPortal.Tests;

public class DatabaseDataSourceTests
{
    private const string WithoutPassword = "Host=psql-test.postgres.database.azure.com;Database=accessreview;Username=id-test;Ssl Mode=VerifyFull";

    [Fact]
    public async Task Managed_identity_uses_an_entra_token_for_postgresql()
    {
        var credential = new FakeCredential();
        await using var dataSource = DatabaseDataSource.Build(WithoutPassword,
            new DatabaseOptions { Authentication = DatabaseAuthentication.ManagedIdentity }, credential);

        // Npgsql asks for the first token in the background when the data source is built.
        var scopes = await credential.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([DatabaseDataSource.EntraScope], scopes);
    }

    [Fact]
    public void Managed_identity_rejects_a_password_in_the_connection_string()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseDataSource.Build(WithoutPassword + ";Password=secret",
            new DatabaseOptions { Authentication = DatabaseAuthentication.ManagedIdentity }, new FakeCredential()));
        Assert.Contains("Remove the password", ex.Message);
    }

    [Fact]
    public async Task Password_authentication_does_not_ask_for_tokens()
    {
        var credential = new FakeCredential();
        await using var dataSource = DatabaseDataSource.Build(WithoutPassword + ";Password=secret", new DatabaseOptions(), credential);

        await Task.Delay(200);
        Assert.False(credential.FirstRequest.Task.IsCompleted);
    }

    [Fact]
    public async Task Root_certificate_can_be_a_der_file()
    {
        using var ca = CreateCa();
        var der = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.der");
        await File.WriteAllBytesAsync(der, ca.RawData);
        try
        {
            await using var dataSource = DatabaseDataSource.Build(WithoutPassword + ";Password=secret", new DatabaseOptions { RootCertificate = der });
            Assert.Equal(ca.Thumbprint, DatabaseDataSource.LoadCertificate(der).Thumbprint);
        }
        finally
        {
            File.Delete(der);
        }
    }

    [Fact]
    public void Root_certificate_can_be_base64_or_pem_text()
    {
        using var ca = CreateCa();
        Assert.Equal(ca.Thumbprint, DatabaseDataSource.LoadCertificate(Convert.ToBase64String(ca.RawData)).Thumbprint);
        Assert.Equal(ca.Thumbprint, DatabaseDataSource.LoadCertificate(ca.ExportCertificatePem()).Thumbprint);
    }

    [Fact]
    public void Wrong_root_certificate_names_the_setting()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseDataSource.Build(WithoutPassword + ";Password=secret",
            new DatabaseOptions { RootCertificate = "not-a-certificate" }));
        Assert.StartsWith("Database:RootCertificate", ex.Message);
    }

    private static X509Certificate2 CreateCa()
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest("CN=Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class FakeCredential : TokenCredential
    {
        public TaskCompletionSource<string[]> FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            FirstRequest.TrySetResult(requestContext.Scopes);
            return new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
