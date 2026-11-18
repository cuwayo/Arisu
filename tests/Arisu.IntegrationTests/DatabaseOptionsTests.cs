using Arisu.Configuration;
using Npgsql;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class DatabaseOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a connection string")]
    [InlineData("postgresql://user:fake-secret@localhost/db?sslmode=invalid")]
    [InlineData("postgresql://user:fake-secret@localhost/db?channel_binding=invalid")]
    [InlineData("postgresql://user:fake-secret@localhost/db?sslmode=require&sslmode=disable")]
    [InlineData("postgresql://user:fake-secret@localhost/db?unknown_option=true")]
    [InlineData("postgresql://user:fake-secret@localhost/db?connect_timeout=0")]
    [InlineData("postgresql://user:fake-secret@localhost/db?command_timeout=0")]
    [InlineData("postgresql://user:fake-secret@localhost/db#fragment")]
    [InlineData("postgresql://user:fake-secret@localhost/")]
    [InlineData("postgresql://localhost/db")]
    [InlineData("postgresql://user:fake%XXsecret@localhost/db")]
    [InlineData("postgresql://user:fake-secret@localhost:99999/db")]
    [InlineData("Host=localhost;Database=arisu_test;Username=arisu_test;Unknown Option=true")]
    [InlineData("Database=arisu_test;Username=arisu_test")]
    [InlineData("Host=localhost;Username=arisu_test")]
    [InlineData("Host=localhost;Database=arisu_test")]
    [InlineData("Host=;Database=arisu_test;Username=arisu_test")]
    [InlineData("Host=localhost;Database=;Username=arisu_test")]
    [InlineData("Host=localhost;Database=arisu_test;Username=")]
    [InlineData("Host=localhost;Database=arisu_test;Username=arisu_test;Timeout=0")]
    [InlineData("Host=localhost;Database=arisu_test;Username=arisu_test;Command Timeout=0")]
    [InlineData("Host=localhost;Database=arisu_test;Username=arisu_test;Log Parameters=true")]
    [InlineData("Host=localhost;Database=arisu_test;Username=arisu_test;Include Error Detail=true")]
    public void IsValidConnectionStringRejectsIncompleteMalformedOrUnsafeOptions(string? connectionString)
    {
        Assert.False(DatabaseOptions.IsValidConnectionString(connectionString));
    }

    [Theory]
    [InlineData("Host=localhost;Database=arisu_test;Username=arisu_test;SSL Mode=Disable")]
    [InlineData("Host=/tmp/arisu-test-postgres;Database=arisu_test;Username=arisu_test;SSL Mode=Disable;Timeout=15;Command Timeout=30")]
    [InlineData("Host=arisu-test.abcdefghij.ap-northeast-1.rds.amazonaws.com;Database=arisu_test;Username=arisu_test;SSL Mode=VerifyFull;Root Certificate=/path/to/rds-ca-bundle.pem;Timeout=15;Command Timeout=30")]
    public void IsValidConnectionStringAcceptsLocalAndVerifiedRdsConnections(string connectionString)
    {
        Assert.True(DatabaseOptions.IsValidConnectionString(connectionString));
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("postgres")]
    public void NeonUrlPreservesTlsAndChannelBindingOptions(string scheme)
    {
        var value = $"{scheme}://test_owner:fake-password@ep-example-pooler.aws.neon.tech/neondb?sslmode=require&channel_binding=require";
        Assert.True(DatabaseOptions.IsValidConnectionString(value));
        var options = new NpgsqlConnectionStringBuilder(
            new DatabaseOptions { ConnectionString = value }.GetNpgsqlConnectionString());
        Assert.Equal("ep-example-pooler.aws.neon.tech", options.Host);
        Assert.Equal(5432, options.Port);
        Assert.Equal("test_owner", options.Username);
        Assert.Equal("fake-password", options.Password);
        Assert.Equal("neondb", options.Database);
        Assert.Equal(SslMode.Require, options.SslMode);
        Assert.Equal(ChannelBinding.Require, options.ChannelBinding);
        using var dataSource = NpgsqlDataSource.Create(options);
    }

    [Fact]
    public void UrlDecodesCredentialsWithoutTreatingPasswordAsConnectionOptions()
    {
        const string value = "postgresql://test%40owner:fake%3BPassword%3Dvalue%2F%3F%23%40%3A+%25@[::1]:6543/my%20db?sslmode=verify-full&connect_timeout=20";
        var options = new NpgsqlConnectionStringBuilder(
            new DatabaseOptions { ConnectionString = value }.GetNpgsqlConnectionString());
        Assert.Equal("::1", options.Host);
        Assert.Equal(6543, options.Port);
        Assert.Equal("test@owner", options.Username);
        Assert.Equal("fake;Password=value/?#@:+%", options.Password);
        Assert.Equal("my db", options.Database);
        Assert.Equal(SslMode.VerifyFull, options.SslMode);
        Assert.Equal(20, options.Timeout);
    }

    [Theory]
    [InlineData("postgresql://user:sentinel-password@localhost/db?bad-option=sentinel-password")]
    [InlineData("Host=localhost;Database=db;Username=user;Password=sentinel-password;bad-option=true")]
    public void InvalidConnectionErrorsDoNotExposeCredentials(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new DatabaseOptions { ConnectionString = value }.GetNpgsqlConnectionString());
        Assert.DoesNotContain("sentinel-password", exception.ToString());
        Assert.Null(exception.InnerException);
    }
}
