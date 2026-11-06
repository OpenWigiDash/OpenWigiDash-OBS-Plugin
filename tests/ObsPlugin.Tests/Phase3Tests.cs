using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Widgets;

namespace ObsPlugin.Tests;

/// <summary>Fase 3: coleções de cenas e perfis.</summary>
public class ConfigStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Lists_AndSwitchEvents()
    {
        var state = new ObsState(new ManualTime(T0));
        state.ApplySceneCollectionList(Wait.Json("""{"currentSceneCollectionName":"Principal","sceneCollections":["Principal","Podcast"]}"""));
        state.ApplyProfileList(Wait.Json("""{"currentProfileName":"Twitch","profiles":["Twitch","YouTube"]}"""));
        Assert.Equal(["Principal", "Podcast"], state.SceneCollections);
        Assert.Equal("Twitch", state.CurrentProfile);

        state.ApplyEvent("CurrentSceneCollectionChanging", Wait.Json("""{"sceneCollectionName":"Principal"}"""));
        Assert.True(state.SwitchingSceneCollection);
        var changed = state.ApplyEvent("CurrentSceneCollectionChanged", Wait.Json("""{"sceneCollectionName":"Podcast"}"""));
        Assert.False(state.SwitchingSceneCollection);
        Assert.Equal("Podcast", state.CurrentSceneCollection);
        Assert.True(changed.NeedsScenes && changed.NeedsInputs);

        state.ApplyEvent("CurrentProfileChanging", Wait.Json("""{"profileName":"Twitch"}"""));
        Assert.True(state.SwitchingProfile);
        Assert.True(state.ApplyEvent("CurrentProfileChanged", Wait.Json("""{"profileName":"YouTube"}""")).NeedsOutputs);
        Assert.Equal("YouTube", state.CurrentProfile);
        Assert.False(state.SwitchingProfile);

        state.ApplyEvent("ProfileListChanged", Wait.Json("""{"profiles":["Twitch","YouTube","4K"]}"""));
        state.ApplyEvent("SceneCollectionListChanged", Wait.Json("""{"sceneCollections":["Podcast"]}"""));
        Assert.Equal(3, state.Profiles.Count);
        Assert.Equal(["Podcast"], state.SceneCollections);

        state.Reset();
        Assert.Empty(state.Profiles);
        Assert.Null(state.CurrentSceneCollection);
    }

    [Fact]
    public void Visual_FooterSaysWhatHappens()
    {
        var size = new PixelSize(400, 290);
        string[] items = ["A", "B", "C"];
        var rest = ConfigListInstance.Visual(items, "A", null, false, ConfigKind.SceneCollection, false, null, requireHold: true, size, 0);
        Assert.True(rest.GridFooter);
        Assert.Equal(TilePalette.Ok, rest.Cells![0].Fill);
        Assert.Equal("Segure para trocar", rest.Detail);
        Assert.Null(rest.Accent);
        Assert.Equal("Toque para trocar", ConfigListInstance.Visual(items, "A", null, false, ConfigKind.Profile, false, null, false, size, 0).Detail);

        var switching = ConfigListInstance.Visual(items, "A", "B", false, ConfigKind.SceneCollection, false, null, true, size, 0);
        Assert.Equal("Trocando…", switching.Detail);
        Assert.Equal(TilePalette.Preview, switching.Cells![1].Ring);
        Assert.Null(switching.Cells[0].Fill);

        var live = ConfigListInstance.Visual(items, "A", null, false, ConfigKind.SceneCollection, outputsActive: true, null, true, size, 0);
        Assert.Equal(TilePalette.Warn, live.Accent);
        Assert.False(live.Tinted);

        var blocked = ConfigListInstance.Visual(items, "A", null, false, ConfigKind.Profile, true, "Pare a live para trocar", true, size, 0);
        Assert.Equal(TilePalette.Record, blocked.Accent);
        Assert.Equal("Pare a live para trocar", blocked.Detail);

        Assert.Equal("Nenhum perfil", ConfigListInstance.Visual([], null, null, false, ConfigKind.Profile, false, null, true, size, 0).Detail);
    }

    [Fact]
    public void ProfileBlocked_NamesTheOutput()
    {
        Assert.Null(ProfileInstance.ProfileBlockedText(false, false, false, false));
        Assert.Equal("Pare a gravação para trocar", ProfileInstance.ProfileBlockedText(false, true, false, false));
        Assert.Equal("Pare a live e a gravação para trocar", ProfileInstance.ProfileBlockedText(true, true, false, false));
        Assert.Equal("Pare as saídas do OBS para trocar", ProfileInstance.ProfileBlockedText(true, true, true, false));
    }

    [Fact]
    public void ItemAt_UsesTheGridAboveTheFooter()
    {
        var size = new PixelSize(400, 290);
        var body = GridLayout.Body(size);
        var (columns, rows) = GridLayout.Shape(body);
        string[] items = ["A", "B", "C"];
        var second = GridLayout.Cell(body, columns, rows, 1);
        Assert.Equal("B", ConfigListInstance.ItemAt(items, size, 0, (int)(second.X + second.Width / 2), (int)(second.Y + second.Height / 2)));
        // A faixa do aviso não é célula.
        Assert.Null(ConfigListInstance.ItemAt(items, size, 0, 200, size.Height - 5));
    }
}

[Collection(nameof(TileTests))]
public sealed class ConfigTileTests : IAsyncLifetime
{
    private FakeObs _obs = null!;
    private ObsSession _session = null!;

    public async Task InitializeAsync()
    {
        _obs = FakeObs.Start();
        _session = new ObsSession(TimeProvider.System, () => _obs.Endpoint, [TimeSpan.FromMilliseconds(100)]);
        ObsTileInstance.SessionOverride = _session;
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        ObsTileInstance.SessionOverride = null;
        await _session.DisposeAsync();
        await _obs.DisposeAsync();
    }

    private async Task<T> Create<T>(ObsWidgetBase widget, FakeContext context) where T : ConfigListInstance
    {
        var instance = (T)await widget.CreateInstanceAsync(context, CancellationToken.None);
        await Wait.Until(() => _session.ConnectionState == ObsConnectionState.Ready && instance.CurrentVisual().Cells?.Count == 2, "OBS pronto");
        return instance;
    }

    private static TouchEvent TouchCell(FakeContext context, TouchKind kind, int index)
    {
        var body = GridLayout.Body(context.PixelSize);
        var (columns, rows) = GridLayout.Shape(body);
        var cell = GridLayout.Cell(body, columns, rows, index);
        return new TouchEvent(kind, (int)(cell.X + cell.Width / 2), (int)(cell.Y + cell.Height / 2), TimeSpan.Zero, kind == TouchKind.LongPress ? 1 : 0);
    }

    private int Count(string request) => _obs.Requests.Count(r => r == request);

    [Fact]
    public async Task Collection_TapOnlyHints_HoldSwitches()
    {
        var context = new FakeContext(WidgetSize.Large);
        var tile = await Create<SceneCollectionInstance>(new SceneCollectionWidget(), context);
        Assert.Equal(TilePalette.Ok, tile.CurrentVisual().Cells![0].Fill);

        await tile.OnTouchAsync(TouchCell(context, TouchKind.Tap, 1), CancellationToken.None);
        Assert.Equal("Segure para trocar", tile.CurrentVisual().Detail);
        Assert.Equal(0, Count("SetCurrentSceneCollection"));

        await tile.OnTouchAsync(TouchCell(context, TouchKind.LongPress, 1), CancellationToken.None);
        await Wait.Until(() => _session.State.CurrentSceneCollection == "Podcast", "trocou a coleção");
        Assert.Equal(1, Count("SetCurrentSceneCollection"));
        await Wait.Until(() => tile.CurrentVisual().Cells![1].Fill == TilePalette.Ok);
        // A coleção nova relê as cenas.
        await Wait.Until(() => Count("GetSceneList") >= 2, "releu as cenas");
        await tile.DisposeAsync();
    }

    [Fact]
    public async Task Profile_BlockedWhileRecording_AndTapSwitchesWithoutHold()
    {
        _obs.Recording = true;
        var context = new FakeContext(WidgetSize.Large);
        var tile = await Create<ProfileInstance>(new ProfileWidget(), context);
        await Wait.Until(() => _session.State.Record.Active, "gravando");
        Assert.Equal("Pare a gravação para trocar", tile.CurrentVisual().Detail);

        await tile.OnTouchAsync(TouchCell(context, TouchKind.LongPress, 1), CancellationToken.None);
        Assert.Equal(0, Count("SetCurrentProfile"));
        Assert.Equal(TilePalette.Record, tile.CurrentVisual().Accent);

        await _session.ToggleRecordAsync();
        await Wait.Until(() => !_session.State.Record.Active, "parou a gravação");
        await tile.SetRequireHoldAsync(false);
        await tile.OnTouchAsync(TouchCell(context, TouchKind.Tap, 1), CancellationToken.None);
        await Wait.Until(() => _session.State.CurrentProfile == "YouTube", "trocou o perfil");
        Assert.Equal(1, Count("SetCurrentProfile"));
        // Tocar no atual não manda nada.
        await tile.OnTouchAsync(TouchCell(context, TouchKind.Tap, 1), CancellationToken.None);
        Assert.Equal(1, Count("SetCurrentProfile"));
        await tile.DisposeAsync();
    }
}
