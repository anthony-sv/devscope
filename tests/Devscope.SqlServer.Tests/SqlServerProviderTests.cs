using Devscope.Core.Database;
using Microsoft.Data.SqlClient;

namespace Devscope.SqlServer.Tests;

public sealed class SqlServerProviderTests
{
    private readonly SqlServerProvider _provider = new();

    [Fact]
    public void ParseConnectionString_SqlLogin_FillsProfileAndKeepsOtherOptions()
    {
        var profile = _provider.ParseConnectionString(
            "Server=sql01\\PROD,1433;Database=Billing;User ID=reader;Password=s3cret;TrustServerCertificate=True;MultiSubnetFailover=True;Command Timeout=300",
            out var password);

        Assert.Equal((@"sql01\PROD,1433", "Billing"), (profile.Server, profile.Database));
        Assert.Equal((DbAuthentication.SqlLogin, "reader", "s3cret"), (profile.Authentication, profile.UserName, password));
        Assert.True(profile.TrustServerCertificate);
        Assert.Equal(300, profile.CommandTimeoutSeconds);
        Assert.Equal("Multi Subnet Failover=True", profile.AdditionalOptions);
        Assert.Equal(@"Billing (sql01\PROD,1433)", profile.Name);
    }

    [Fact]
    public void ParseConnectionString_IntegratedSecurity_IsWindowsAuthentication()
    {
        var profile = _provider.ParseConnectionString("Data Source=.;Initial Catalog=App;Integrated Security=SSPI", out var password);

        Assert.Equal(DbAuthentication.Windows, profile.Authentication);
        Assert.Null(password);
        Assert.Equal(120, profile.CommandTimeoutSeconds);
    }

    [Fact]
    public void ParseConnectionString_MicrosoftJdbcUrl_BecomesAProfile()
    {
        var profile = _provider.ParseConnectionString(
            @"jdbc:sqlserver://sql01\PROD:1433;databaseName=Billing;user=app;password={p;w=d};encrypt=true;trustServerCertificate=true",
            out var password);

        Assert.Equal((@"sql01\PROD,1433", "Billing"), (profile.Server, profile.Database));
        Assert.Equal((DbAuthentication.SqlLogin, "app", "p;w=d"), (profile.Authentication, profile.UserName, password));
        Assert.True(profile.Encrypt);
        Assert.True(profile.TrustServerCertificate);
    }

    [Fact]
    public void ParseConnectionString_JtdsUrlWithoutLogin_UsesWindowsAuthentication()
    {
        var profile = _provider.ParseConnectionString("jdbc:jtds:sqlserver://sql02:1433/Orders;loginTimeout=20", out var password);

        Assert.Equal(("sql02,1433", "Orders"), (profile.Server, profile.Database));
        Assert.Equal(DbAuthentication.Windows, profile.Authentication);
        Assert.Null(password);
        Assert.Equal("Connect Timeout=20", profile.AdditionalOptions);
    }

    [Theory]
    [InlineData("not a connection string")]
    [InlineData("jdbc:sqlserver://;databaseName=x")]
    [InlineData("Database=NoServer")]
    public void ParseConnectionString_Invalid_ThrowsArgumentException(string text) =>
        Assert.Throws<ArgumentException>(() => _provider.ParseConnectionString(text, out _));

    [Fact]
    public void BuildConnectionString_AppliesProfileOverAdditionalOptions()
    {
        var profile = new DbConnectionProfile
        {
            Server = "sql01",
            Database = "Billing",
            Authentication = DbAuthentication.SqlLogin,
            UserName = "reader",
            AdditionalOptions = "Database=Other;Connect Timeout=5",
        };

        var builder = new SqlConnectionStringBuilder(SqlServerProvider.BuildConnectionString(profile, "pw"));

        Assert.Equal(("sql01", "Billing", "reader", "pw"), (builder.DataSource, builder.InitialCatalog, builder.UserID, builder.Password));
        Assert.False(builder.IntegratedSecurity);
        Assert.Equal(5, builder.ConnectTimeout);
        Assert.Equal(ApplicationIntent.ReadOnly, builder.ApplicationIntent);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.Equal("Devscope", builder.ApplicationName);
    }

    [Fact]
    public void BuildConnectionString_ExplicitAuthenticationOption_ReplacesWindowsAuthentication()
    {
        var profile = new DbConnectionProfile { Server = "x.database.windows.net", AdditionalOptions = "Authentication=Active Directory Default" };

        var builder = new SqlConnectionStringBuilder(SqlServerProvider.BuildConnectionString(profile, null));

        Assert.False(builder.IntegratedSecurity);
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryDefault, builder.Authentication);
    }

    [Fact]
    public void BuildSelect_OrdersTextKeysUnderABinaryCollation()
    {
        var columns = new[]
        {
            new ColumnDefinition("Code", "varchar(20)", false) { Kind = DbValueKind.Text },
            new ColumnDefinition("Name", "nvarchar(max)", true) { Kind = DbValueKind.Text },
            new ColumnDefinition("Id", "int", false) { Kind = DbValueKind.Number },
            new ColumnDefinition("Shape", "geography", true) { Kind = DbValueKind.Clr },
        };

        var sql = SqlServerSource.BuildSelect(new RowQuery("dbo", "T", columns, [0, 1, 2], "Id > 5"));

        Assert.Equal(
            "SELECT [Code], [Name], [Id], CAST([Shape] AS varbinary(max)) AS [Shape] FROM [dbo].[T] WHERE (Id > 5) "
            + "ORDER BY CAST([Code] AS nvarchar(4000)) COLLATE Latin1_General_BIN2, [Name] COLLATE Latin1_General_BIN2, [Id]",
            sql);
    }
}
