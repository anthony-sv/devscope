using Devscope.Core.Database;
using Microsoft.Extensions.Logging.Abstractions;

namespace Devscope.Core.Tests.Database;

public sealed class ConnectionStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SaveAndLoad_RoundTripsProfiles()
    {
        var store = new JsonConnectionStore(_temp.Combine("connections.json"), NullLogger<JsonConnectionStore>.Instance);
        var profile = new DbConnectionProfile
        {
            Name = "Billing PROD",
            Server = @"sql01\PROD",
            Database = "Billing",
            Authentication = DbAuthentication.SqlLogin,
            UserName = "reader",
            ProtectedPassword = "encrypted",
            TrustServerCertificate = true,
        };

        await store.SaveAsync([profile], TestContext.Current.CancellationToken);
        var loaded = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(profile.Id, loaded.Id);
        Assert.Equal((profile.Name, profile.Server, profile.Database), (loaded.Name, loaded.Server, loaded.Database));
        Assert.Equal((DbAuthentication.SqlLogin, "reader", "encrypted"), (loaded.Authentication, loaded.UserName, loaded.ProtectedPassword));
        Assert.True(loaded.TrustServerCertificate);
        Assert.Contains("\"SqlLogin\"", File.ReadAllText(_temp.Combine("connections.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_CorruptFile_KeepsACopyAndReturnsNothing()
    {
        var path = _temp.WriteFile("connections.json", "{ not json");
        var store = new JsonConnectionStore(path, NullLogger<JsonConnectionStore>.Instance);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(loaded);
        Assert.True(File.Exists(path + ".corrupt"));
    }
}
