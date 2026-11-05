using System.Text;
using OpenWigiDash.ObsPlugin.Obs;

namespace ObsPlugin.Tests;

public class ConnectionStoreTests
{
    /// <summary>No lugar do DPAPI (só Windows): inverte e codifica, o bastante para ver que não fica em claro.</summary>
    private sealed class ReverseProtector : ISecretProtector
    {
        public string Protect(string secret) => Convert.ToBase64String(Encoding.UTF8.GetBytes(new string([.. secret.Reverse()])));
        public string Unprotect(string stored) => new([.. Encoding.UTF8.GetString(Convert.FromBase64String(stored)).Reverse()]);
    }

    private sealed class BrokenProtector : ISecretProtector
    {
        public string Protect(string secret) => "x";
        public string Unprotect(string stored) => throw new System.Security.Cryptography.CryptographicException("outro usuário");
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "owd-obs-" + Guid.NewGuid().ToString("N"), "connection.json");

    [Fact]
    public void NoFile_IsTheLocalDefault()
    {
        var endpoint = new ObsConnectionStore(TempFile(), new ReverseProtector()).Load();
        Assert.Equal(ObsEndpoint.Default, endpoint);
        Assert.Equal(new Uri("ws://127.0.0.1:4455"), endpoint.Uri);
        Assert.True(endpoint.IsLocal);
    }

    [Fact]
    public void SaveAndLoad_PasswordNeverInPlainText()
    {
        var path = TempFile();
        var store = new ObsConnectionStore(path, new ReverseProtector());
        store.Save(new ObsEndpoint(" 192.168.0.20 ", 4460, "s3nh4-de-teste"));

        Assert.DoesNotContain("s3nh4-de-teste", File.ReadAllText(path));
        var loaded = store.Load();
        Assert.Equal(new ObsEndpoint("192.168.0.20", 4460, "s3nh4-de-teste"), loaded);
        Assert.False(loaded.IsLocal);
    }

    [Fact]
    public void PasswordFromAnotherUser_IsDropped()
    {
        var path = TempFile();
        new ObsConnectionStore(path, new ReverseProtector()).Save(new ObsEndpoint("127.0.0.1", 4455, "abc"));
        Assert.Null(new ObsConnectionStore(path, new BrokenProtector()).Load().Password);
    }

    [Fact]
    public void BrokenFile_IsTheDefault()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ nope");
        Assert.Equal(ObsEndpoint.Default, new ObsConnectionStore(path, new ReverseProtector()).Load());
    }

    [Fact]
    public void Ipv6_GoesInBrackets() =>
        Assert.Equal(new Uri("ws://[::1]:4455"), new ObsEndpoint("::1", 4455, null).Uri);

    [Fact]
    public void LocalConfig_ReadsObsWebsocketFile()
    {
        var config = ObsLocalConfig.Parse("""
            {"alerts_enabled":false,"auth_required":true,"first_load":false,"server_enabled":true,
             "server_password":"senha-do-arquivo","server_port":4466}
            """);
        Assert.Equal(new ObsLocalConfig(true, 4466, true, "senha-do-arquivo"), config);

        Assert.Equal(new ObsLocalConfig(false, 4455, false, null), ObsLocalConfig.Parse("""{"server_enabled":false,"auth_required":false}"""));
        Assert.Null(ObsLocalConfig.Parse("not json"));
        Assert.Null(ObsLocalConfig.TryRead(TempFile()));
    }
}
