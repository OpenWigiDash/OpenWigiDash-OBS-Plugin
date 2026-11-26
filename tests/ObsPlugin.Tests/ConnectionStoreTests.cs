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

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "owd-obs-" + Guid.NewGuid().ToString("N"), "connections.json");

    private static string LegacyFile(string file) => Path.Combine(Path.GetDirectoryName(file)!, "connection.json");

    private static void WriteLegacy(string file, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(LegacyFile(file), json);
    }

    [Fact]
    public void NoFile_IsTheLocalDefault()
    {
        var list = new ObsConnectionStore(TempFile(), new ReverseProtector()).Load();
        var only = Assert.Single(list.Connections);
        Assert.Equal(ObsConnectionStore.LocalId, list.DefaultId);
        Assert.Equal("Este PC", only.Name);
        Assert.Equal(ObsEndpoint.Default, only.Endpoint);
        Assert.Equal(new Uri("ws://127.0.0.1:4455"), only.Endpoint.Uri);
        Assert.True(only.Endpoint.IsLocal);
    }

    [Fact]
    public void LegacySingleConnection_BecomesTheDefault()
    {
        var path = TempFile();
        var protector = new ReverseProtector();
        WriteLegacy(path, $$"""{"host":"127.0.0.1","port":4460,"password":"{{protector.Protect("antiga")}}"}""");

        var list = new ObsConnectionStore(path, protector, LegacyFile(path)).Load();
        var only = Assert.Single(list.Connections);
        Assert.Equal(new ObsConnection(ObsConnectionStore.LocalId, "Este PC", new ObsEndpoint("127.0.0.1", 4460, "antiga")), only);
        Assert.Same(only, list.Default);

        // Um widget sem escolha, ou com uma conexão que não existe, cai na padrão.
        Assert.Equal(only, list.Resolve(null));
        Assert.Equal(only, list.Resolve("apagada"));
    }

    [Fact]
    public void LegacyRemote_IsNamedByHost()
    {
        var path = TempFile();
        WriteLegacy(path, """{"host":"192.168.0.20","port":4455}""");
        var only = Assert.Single(new ObsConnectionStore(path, new ReverseProtector(), LegacyFile(path)).Load().Connections);
        Assert.Equal("192.168.0.20", only.Name);
        Assert.False(only.Endpoint.IsLocal);
    }

    [Fact]
    public void SaveAndLoad_SeveralConnections_PasswordNeverInPlainText()
    {
        var path = TempFile();
        WriteLegacy(path, """{"host":"10.0.0.1","port":1}""");
        var store = new ObsConnectionStore(path, new ReverseProtector(), LegacyFile(path));
        var saved = 0;
        store.Saved += () => saved++;
        store.Save(new ObsConnectionList(
        [
            new ObsConnection(ObsConnectionStore.LocalId, "Este PC", new ObsEndpoint("127.0.0.1", 4455, "s3nh4-local")),
            new ObsConnection("live", " PC da live ", new ObsEndpoint(" 192.168.0.20 ", 4460, "s3nh4-de-teste")),
        ], "live"));

        Assert.Equal(1, saved);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("s3nh4-de-teste", text);
        Assert.DoesNotContain("s3nh4-local", text);

        // A lista salva vale mais que o arquivo antigo.
        var loaded = store.Load();
        Assert.Equal(2, loaded.Connections.Count);
        Assert.Equal("live", loaded.DefaultId);
        Assert.Equal(new ObsConnection("live", "PC da live", new ObsEndpoint("192.168.0.20", 4460, "s3nh4-de-teste")), loaded.Default);
        Assert.Equal(new ObsEndpoint("127.0.0.1", 4455, "s3nh4-local"), store.Endpoint(ObsConnectionStore.LocalId));
        Assert.Equal(loaded.Default.Endpoint, store.Endpoint("nao-existe"));
        Assert.False(loaded.Default.Endpoint.IsLocal);
    }

    [Fact]
    public void DefaultMissing_IsTheFirst()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"defaultId":"sumiu","connections":[{"id":"a","name":"A","host":"127.0.0.1","port":4455},{"id":"a","name":"Repetida"},{"name":"Sem id"}]}""");
        var list = new ObsConnectionStore(path, new ReverseProtector()).Load();
        Assert.Equal("a", list.DefaultId);
        Assert.Equal("A", Assert.Single(list.Connections).Name);
    }

    [Fact]
    public void SavingEmpty_IsRefused() =>
        Assert.Throws<ArgumentException>(() => new ObsConnectionStore(TempFile(), new ReverseProtector()).Save(new ObsConnectionList([], "x")));

    [Fact]
    public void PasswordFromAnotherUser_IsDropped()
    {
        var path = TempFile();
        new ObsConnectionStore(path, new ReverseProtector()).Save(new ObsConnectionList(
            [new ObsConnection("local", "Este PC", new ObsEndpoint("127.0.0.1", 4455, "abc"))], "local"));
        Assert.Null(new ObsConnectionStore(path, new BrokenProtector()).Load().Default.Endpoint.Password);
    }

    [Fact]
    public void BrokenFile_IsTheDefault()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ nope");
        Assert.Equal(ObsEndpoint.Default, new ObsConnectionStore(path, new ReverseProtector()).Load().Default.Endpoint);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("192.0.2.55", false)]
    [InlineData("pc-da-live-que-nao-existe", false)]
    public void IsLocal(string host, bool local) => Assert.Equal(local, new ObsEndpoint(host, 4455, null).IsLocal);

    [Fact]
    public void ThisMachineName_IsLocal() => Assert.True(new ObsEndpoint(Environment.MachineName.ToLowerInvariant(), 4455, null).IsLocal);

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
