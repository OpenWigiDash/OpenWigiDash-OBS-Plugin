using System.Text;
using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Widgets;

namespace ObsPlugin.Tests;

/// <summary>
/// Várias conexões: cada widget usa a sua, e troca quando a pessoa escolhe outra ou apaga a dele. Mesma coleção dos
/// outros testes de tile, porque troca a sessão e as conexões estáticas.
/// </summary>
[Collection(nameof(TileTests))]
public sealed class ConnectionsTests : IAsyncLifetime
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string secret) => Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        public string Unprotect(string stored) => Encoding.UTF8.GetString(Convert.FromBase64String(stored));
    }

    private FakeObs _local = null!;
    private FakeObs _remote = null!;
    private ObsSession _localSession = null!;
    private ObsSession _remoteSession = null!;
    private ObsConnectionStore _store = null!;

    public Task InitializeAsync()
    {
        _local = FakeObs.Start();
        _remote = FakeObs.Start();
        _localSession = new ObsSession(TimeProvider.System, () => _local.Endpoint, [TimeSpan.FromMilliseconds(100)]) { ConnectionId = "local" };
        _remoteSession = new ObsSession(TimeProvider.System, () => _remote.Endpoint, [TimeSpan.FromMilliseconds(100)]) { ConnectionId = "live" };
        _store = new ObsConnectionStore(Path.Combine(Path.GetTempPath(), "owd-obs-" + Guid.NewGuid().ToString("N"), "connections.json"), new PlainProtector());
        _store.Save(new ObsConnectionList(
        [
            new ObsConnection("local", "Este PC", _local.Endpoint),
            new ObsConnection("live", "PC da live", _remote.Endpoint),
        ], "local"));
        ObsTileInstance.SessionOverride = null;
        ObsTileInstance.StoreOverride = _store;
        ObsTileInstance.SessionFactoryOverride = id => id == "live" ? _remoteSession : _localSession;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        ObsTileInstance.SessionFactoryOverride = null;
        ObsTileInstance.StoreOverride = null;
        await _localSession.DisposeAsync();
        await _remoteSession.DisposeAsync();
        await _local.DisposeAsync();
        await _remote.DisposeAsync();
    }

    private static TouchEvent Tap => new(TouchKind.Tap, 50, 50, TimeSpan.Zero, 0);

    [Fact]
    public async Task WithoutChoice_UsesTheDefault_AndChoosingAnotherSwitches()
    {
        var context = new FakeContext(WidgetSize.Small);
        var record = (RecordInstance)await new RecordWidget().CreateInstanceAsync(context, CancellationToken.None);
        try
        {
            Assert.Null(record.ConnectionId);
            Assert.Same(_localSession, record.Session);
            Assert.Equal("Este PC", record.Connection.Name);
            await Wait.Until(() => _localSession.ConnectionState == ObsConnectionState.Ready, "OBS local pronto");
            Assert.Equal(ObsConnectionState.Connecting, _remoteSession.ConnectionState); // o outro nem abriu

            await record.SetConnectionAsync("live");
            Assert.Equal("live", await context.Settings.GetAsync(ObsTileInstance.ConnectionKey));
            Assert.Same(_remoteSession, record.Session);
            await Wait.Until(() => _remoteSession.ConnectionState == ObsConnectionState.Ready, "OBS do outro PC pronto");
            await Wait.Until(() => _localSession.ConnectionState == ObsConnectionState.Connecting, "local fechado sem widget");

            await record.OnTouchAsync(Tap, CancellationToken.None);
            await Wait.Until(() => _remote.Recording, "gravou no OBS do outro PC");
            Assert.False(_local.Recording);

            // Voltar para "padrão" tira a escolha.
            await record.SetConnectionAsync(null);
            Assert.Null(await context.Settings.GetAsync(ObsTileInstance.ConnectionKey));
            Assert.Same(_localSession, record.Session);
        }
        finally
        {
            await record.DisposeAsync();
        }
    }

    [Fact]
    public async Task SavedChoice_IsUsedFromTheStart()
    {
        var context = new FakeContext(WidgetSize.Small);
        await context.Settings.SetAsync(ObsTileInstance.ConnectionKey, "live");
        var scene = (SceneInstance)await new SceneWidget().CreateInstanceAsync(context, CancellationToken.None);
        try
        {
            Assert.Same(_remoteSession, scene.Session);
            await Wait.Until(() => _remoteSession.ConnectionState == ObsConnectionState.Ready);
            Assert.Equal(ObsConnectionState.Connecting, _localSession.ConnectionState);
        }
        finally
        {
            await scene.DisposeAsync();
        }
    }

    [Fact]
    public async Task RemovedConnection_FallsBackToTheDefault()
    {
        var context = new FakeContext(WidgetSize.Small);
        await context.Settings.SetAsync(ObsTileInstance.ConnectionKey, "live");
        var record = (RecordInstance)await new RecordWidget().CreateInstanceAsync(context, CancellationToken.None);
        try
        {
            Assert.Same(_remoteSession, record.Session);
            var changed = 0;
            record.Changed += () => changed++;

            _store.Save(new ObsConnectionList([new ObsConnection("local", "Este PC", _local.Endpoint)], "local"));
            Assert.Same(_localSession, record.Session);
            Assert.Equal("Este PC", record.Connection.Name);
            Assert.True(changed > 0);
            await Wait.Until(() => record.CurrentVisual().Layout != TileLayout.Offline, "pronto no OBS local");
        }
        finally
        {
            await record.DisposeAsync();
        }
    }

    [Fact]
    public async Task NewDefault_MovesWidgetsWithoutChoice()
    {
        var record = (RecordInstance)await new RecordWidget().CreateInstanceAsync(new FakeContext(WidgetSize.Small), CancellationToken.None);
        try
        {
            Assert.Same(_localSession, record.Session);
            _store.Save(_store.Load() with { DefaultId = "live" });
            Assert.Same(_remoteSession, record.Session);
        }
        finally
        {
            await record.DisposeAsync();
        }
    }

    [Fact]
    public async Task RemoteOffline_SaysNoAnswer_NotObsClosed()
    {
        var unreachable = new ObsSession(TimeProvider.System, () => new ObsEndpoint("nao-existe.invalid", 4455, null), [TimeSpan.FromMilliseconds(100)]);
        ObsTileInstance.SessionOverride = unreachable;
        ObsLauncher.FindOverride = () => @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
        var status = (StatusInstance)await new StatusWidget().CreateInstanceAsync(new FakeContext(WidgetSize.Small), CancellationToken.None);
        try
        {
            await Wait.Until(() => unreachable.ConnectionState == ObsConnectionState.Unavailable, "sem resposta");
            Assert.False(unreachable.IsLocal);
            // Sem "segure para abrir": o OBS de outro PC não abre daqui.
            Assert.Equal("Sem resposta", status.CurrentVisual().Detail);
        }
        finally
        {
            ObsTileInstance.SessionOverride = null;
            ObsLauncher.FindOverride = null;
            await status.DisposeAsync();
            await unreachable.DisposeAsync();
        }
    }
}
