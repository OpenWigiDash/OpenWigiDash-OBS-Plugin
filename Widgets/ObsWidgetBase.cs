using System.Drawing;
using Microsoft.Extensions.Logging;
using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Settings;

// Conexões com o obs-websocket (nome, endereço e senha): na página do plugin no manager. Cada widget escolhe uma.
[assembly: OpenWigiPluginSettings(typeof(ObsPluginSettings))]

namespace OpenWigiDash.ObsPlugin.Widgets;

/// <summary>
/// Base dos widgets do OBS. Cada função (Gravar, Live, Cena...) é um widget próprio no catálogo; os widgets da mesma
/// conexão usam a mesma <see cref="ObsSession"/>.
/// </summary>
public abstract class ObsWidgetBase : IWidget
{
    protected const string Website = "https://github.com/OpenWigiDash/OpenWigiDash-OBS-Plugin";

    private IWidgetHost? _host;

    public abstract WidgetManifest Manifest { get; }

    /// <summary>O tile como aparece no catálogo "Adicionar widget".</summary>
    protected abstract TileVisual Preview { get; }

    /// <summary>A prévia do catálogo, para os testes.</summary>
    internal TileVisual PreviewVisual => Preview;

    /// <summary>Nome curto embaixo do ícone do catálogo. Padrão: o nome sem o "OBS: ".</summary>
    protected virtual string CatalogLabel => Manifest.Name.StartsWith("OBS: ", StringComparison.Ordinal) ? Manifest.Name[5..] : Manifest.Name;

    /// <summary>Ícone do catálogo. Padrão: o da prévia.</summary>
    protected virtual ObsIcon CatalogIcon => Preview.Icon;

    /// <summary>
    /// O que o catálogo mostra: em todos os tamanhos, o ícone do widget e o nome curto sobre o fundo com o símbolo
    /// do OBS. Assim todos os ícones seguem o mesmo padrão, em vez de alguns virarem miniatura do tile.
    /// </summary>
    internal TileVisual CatalogVisual => new(CatalogIcon) { Layout = TileLayout.Catalog, Big = CatalogLabel };

    protected abstract ObsTileInstance CreateInstance(IWidgetContext context);

    public ValueTask LoadAsync(IWidgetHost host, CancellationToken cancellationToken)
    {
        _host = host;
        ObsSession.UseLoggerForAll(host.LoggerFactory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IWidgetInstance> CreateInstanceAsync(IWidgetContext context, CancellationToken cancellationToken)
    {
        var instance = CreateInstance(context);
        await instance.LoadConnectionAsync(cancellationToken).ConfigureAwait(false);
        await instance.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        instance.Start();
        return instance;
    }

    public ValueTask<WidgetFrame?> GetPreviewAsync(WidgetSize size, PixelSize pixelSize, CancellationToken cancellationToken) =>
        ValueTask.FromResult<WidgetFrame?>(TileRenderer.Render(pixelSize, _host?.Theme ?? new WidgetTheme(), CatalogVisual));

    protected static WidgetManifest CreateManifest(Guid id, string name, string description, params WidgetSize[] sizes) => new()
    {
        Id = id,
        Name = name,
        Version = typeof(ObsWidgetBase).Assembly.GetName().Version ?? new Version(1, 0, 0),
        Author = "Victor Perez",
        Website = Website,
        Description = description,
        SupportedSizes = sizes,
    };
}

/// <summary>
/// Um tile do OBS na tela. Mostra o estado da conexão até o OBS ficar pronto e só então passa desenho e toques
/// para o widget concreto. Cuida também da barra do long-press e do aviso curto depois de um toque.
/// </summary>
public abstract class ObsTileInstance : IWidgetInstance
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FeedbackLength = TimeSpan.FromSeconds(2.5);

    /// <summary>Chave da conexão escolhida nas configurações da instância (sem ela, a padrão).</summary>
    internal const string ConnectionKey = "connection";

    private readonly Lock _sessionLock = new();
    private readonly ObsConnectionStore _store = Store;
    private readonly CancellationTokenSource _stop = new();
    private IDisposable? _lease;
    private bool _started;
    private double? _press;
    private (string Text, bool Ok, DateTimeOffset Until)? _feedback;
    private bool _asleep;

    protected ObsTileInstance(IWidgetContext context)
    {
        Context = context;
        Session = ResolveSession();
        _asleep = context.IsAsleep;
    }

    /// <summary>Os testes trocam pela sessão ligada ao OBS simulado (vale para qualquer conexão).</summary>
    internal static ObsSession? SessionOverride { get; set; }

    /// <summary>Os testes dão uma sessão para cada id de conexão (cada uma ligada a um OBS simulado).</summary>
    internal static Func<string, ObsSession>? SessionFactoryOverride { get; set; }

    /// <summary>Onde estão as conexões (os testes trocam por um arquivo temporário).</summary>
    internal static ObsConnectionStore? StoreOverride { get; set; }

    internal static ObsConnectionStore Store => StoreOverride ?? ObsConnectionStore.Default;

    protected IWidgetContext Context { get; }

    /// <summary>A sessão da conexão deste widget. Muda quando a pessoa escolhe outra conexão.</summary>
    public ObsSession Session { get; private set; }

    /// <summary>Id da conexão escolhida, ou <c>null</c> para seguir a padrão.</summary>
    public string? ConnectionId { get; private set; }

    /// <summary>As conexões salvas, para o seletor da tela de configurações.</summary>
    public ObsConnectionList Connections => _store.Load();

    /// <summary>A conexão que o widget usa agora (a escolhida, ou a padrão se não escolheu ou ela foi apagada).</summary>
    public ObsConnection Connection => Connections.Resolve(ConnectionId);

    protected ObsState Obs => Session.State;

    protected DateTimeOffset Now => Context.Host.Time.GetUtcNow();

    protected bool IsMini => Context.PixelSize.Height < 100;

    protected bool IsWide => Context.PixelSize.Width >= Context.PixelSize.Height * 2.2f;

    protected bool IsAsleep => _asleep;

    /// <summary>Algo que a tela de configurações mostra mudou (conexão, opções do widget).</summary>
    public event Action? Changed;

    /// <summary>Ícone usado nos estados de conexão.</summary>
    protected abstract ObsIcon Icon { get; }

    /// <summary>Componente Razor com as opções do widget (parâmetro <c>Instance</c>), ou <c>null</c>.</summary>
    public virtual Type? OptionsComponent => null;

    public Type? SettingsComponent => typeof(ObsSettings);

    /// <summary>O widget tem ação no long-press (desenha a barra enchendo).</summary>
    protected virtual bool HasLongPress => false;

    /// <summary>Precisa redesenhar sozinho (tempo correndo, ponto piscando).</summary>
    protected virtual bool Animating => false;

    protected internal virtual ValueTask LoadSettingsAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Lê a conexão escolhida antes de abrir qualquer uma.</summary>
    internal async ValueTask LoadConnectionAsync(CancellationToken cancellationToken)
    {
        ConnectionId = await Context.Settings.GetAsync(ConnectionKey, cancellationToken).ConfigureAwait(false);
        lock (_sessionLock)
            Session = ResolveSession();
    }

    /// <summary>A pessoa escolheu outra conexão (<c>null</c> = a padrão): salva e passa a usar a sessão dela.</summary>
    public async Task SetConnectionAsync(string? connectionId)
    {
        ConnectionId = string.IsNullOrEmpty(connectionId) ? null : connectionId;
        if (ConnectionId is null)
            await Context.Settings.RemoveAsync(ConnectionKey).ConfigureAwait(false);
        else
            await Context.Settings.SetAsync(ConnectionKey, ConnectionId).ConfigureAwait(false);
        SwitchSession();
    }

    private ObsSession ResolveSession()
    {
        if (SessionOverride is { } fixedSession)
            return fixedSession;
        var id = _store.Load().Resolve(ConnectionId).Id;
        return SessionFactoryOverride?.Invoke(id) ?? ObsSession.For(id);
    }

    /// <summary>
    /// Passa para a sessão da conexão atual, se mudou: solta medidor, miniatura e a conexão antiga e pega as da nova.
    /// </summary>
    private void SwitchSession()
    {
        lock (_sessionLock)
        {
            var next = ResolveSession();
            if (ReferenceEquals(next, Session))
            {
                NotifyChanged();
                return;
            }
            if (!_started)
            {
                Session = next;
                return;
            }
            OnAwakeChanged(false);
            Session.Changed -= OnSessionChanged;
            var old = _lease;
            Session = next;
            Session.Changed += OnSessionChanged;
            _lease = Session.Acquire();
            old?.Dispose();
            OnAwakeChanged(!_asleep);
        }
        Context.Logger.LogInformation("Widget do OBS passou para a conexão {Connection}.", Session.ConnectionId);
        NotifyChanged();
    }

    /// <summary>A lista de conexões mudou: a padrão pode ser outra, ou a escolhida pode ter sido apagada.</summary>
    private void OnConnectionsSaved()
    {
        try
        {
            SwitchSession();
        }
        catch (Exception ex)
        {
            Context.Logger.LogWarning(ex, "Falha ao trocar a conexão do widget do OBS.");
        }
    }

    /// <summary>Desenho com o OBS pronto.</summary>
    protected abstract TileVisual GetVisual();

    /// <summary>Toque com o OBS pronto (gestos e press/release).</summary>
    protected abstract Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken);

    /// <summary>O widget faz algo com o OBS fora do ar (o Status abre o OBS no long-press).</summary>
    protected virtual bool OfflineLongPress => false;

    /// <summary>Toque com o OBS fora do ar (só chega se <see cref="OfflineLongPress"/>).</summary>
    protected virtual Task OnOfflineTouchAsync(TouchEvent touch, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Texto do tile com o OBS fora do ar (o Status acrescenta "segure para abrir").</summary>
    protected virtual string OfflineText(ObsConnectionState state, string text) => text;

    /// <summary>A tela acordou ou dormiu (ligar e desligar medidor e miniatura).</summary>
    protected virtual void OnAwakeChanged(bool awake)
    {
    }

    internal void Start()
    {
        lock (_sessionLock)
        {
            Session.Changed += OnSessionChanged;
            _lease = Session.Acquire();
            _started = true;
        }
        _store.Saved += OnConnectionsSaved;
        OnAwakeChanged(!_asleep);
        _ = TickLoopAsync(_stop.Token);
    }

    public ValueTask<WidgetFrame> RenderAsync(RenderReason reason, CancellationToken cancellationToken) =>
        ValueTask.FromResult(TileRenderer.Render(Context.PixelSize, Context.Theme, CurrentVisual()));

    /// <summary>O que o tile mostra agora (os testes conferem isto sem desenhar).</summary>
    public TileVisual CurrentVisual()
    {
        (string Text, Color Dot)? offline = Session.ConnectionState switch
        {
            ObsConnectionState.Ready => null,
            // Fora deste PC não dá para saber se é o OBS fechado ou o PC desligado.
            ObsConnectionState.Unavailable => (Session.IsLocal ? "OBS fechado" : "Sem resposta", TilePalette.Record),
            ObsConnectionState.NeedsPassword => ("Falta a senha", TilePalette.Warn),
            ObsConnectionState.AuthFailed => ("Senha errada", TilePalette.Record),
            ObsConnectionState.Unsupported => ("Atualize o OBS", TilePalette.Warn),
            _ => ("Conectando…", TilePalette.Preview),
        };
        if (offline is var (text, dot))
        {
            var detail = _feedback is { } shown && Now < shown.Until ? shown.Text : OfflineText(Session.ConnectionState, text);
            var visual0 = new TileVisual(Icon) { Layout = TileLayout.Offline, Detail = detail, StatusDot = dot };
            return _press is { } held ? visual0 with { Press = held } : visual0;
        }

        var visual = GetVisual();
        if (_feedback is { } feedback && Now < feedback.Until)
        {
            visual = feedback.Ok
                ? visual with { Detail = feedback.Text, MiniText = visual.MiniText }
                : visual with { Accent = TilePalette.Record, Tinted = true, Detail = feedback.Text, Ring = null };
        }
        return _press is { } press ? visual with { Press = press } : visual;
    }

    public async ValueTask OnTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        var ready = Session.ConnectionState == ObsConnectionState.Ready;
        if (!ready && !OfflineLongPress)
            return;
        if (ready ? HasLongPress : OfflineLongPress)
        {
            switch (touch.Kind)
            {
                case TouchKind.PressProgress when touch.Progress > 0.15:
                    _press = touch.Progress;
                    Context.RequestRender();
                    break;
                case TouchKind.LongPress or TouchKind.PressEnded or TouchKind.Tap:
                    if (_press is not null)
                    {
                        _press = null;
                        Context.RequestRender();
                    }
                    break;
            }
        }
        try
        {
            if (ready)
                await OnReadyTouchAsync(touch, cancellationToken).ConfigureAwait(false);
            else
                await OnOfflineTouchAsync(touch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Context.Logger.LogWarning(ex, "Falha ao mandar o comando ao OBS.");
            Flash("Falhou", ok: false);
        }
    }

    /// <summary>Confere a resposta do OBS; se falhou, mostra o motivo no tile por alguns segundos.</summary>
    protected bool Check(ObsResponse response, string? failure = null)
    {
        if (response.Ok)
            return true;
        Context.Logger.LogInformation("OBS recusou: {Code} {Comment}", response.Code, response.Comment);
        Flash(failure ?? "OBS recusou", ok: false);
        return false;
    }

    /// <summary>Aviso curto no lugar do detalhe (verde = deu certo, vermelho = falhou).</summary>
    protected void Flash(string text, bool ok = true)
    {
        _feedback = (text, ok, Now + FeedbackLength);
        Context.RequestRender();
    }

    public ValueTask OnSleepAsync(CancellationToken cancellationToken)
    {
        _asleep = true;
        OnAwakeChanged(false);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnWakeAsync(CancellationToken cancellationToken)
    {
        _asleep = false;
        OnAwakeChanged(true);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnSizeChangedAsync(PixelSize pixelSize, CancellationToken cancellationToken)
    {
        OnAwakeChanged(!_asleep); // miniatura e medidor dependem do tamanho
        return ValueTask.CompletedTask;
    }

    /// <summary>Entrou ou saiu da tela cheia (o painel de cenas muda a grade).</summary>
    public virtual ValueTask OnFullscreenChangedAsync(bool fullscreen, CancellationToken cancellationToken)
    {
        OnAwakeChanged(!_asleep);
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _store.Saved -= OnConnectionsSaved;
        OnAwakeChanged(false);
        lock (_sessionLock)
        {
            Session.Changed -= OnSessionChanged;
            _lease?.Dispose();
            _lease = null;
            _started = false;
        }
        _stop.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Salva uma opção da instância, avisa a tela de configurações e redesenha.</summary>
    protected async Task SaveAsync(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            await Context.Settings.RemoveAsync(key).ConfigureAwait(false);
        else
            await Context.Settings.SetAsync(key, value).ConfigureAwait(false);
        NotifyChanged();
    }

    protected void NotifyChanged()
    {
        Changed?.Invoke();
        Context.RequestRender();
    }

    private void OnSessionChanged() => NotifyChanged();

    /// <summary>Tempo correndo, ponto piscando e o fim do aviso: redesenha a cada meio segundo enquanto precisa.</summary>
    private async Task TickLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TickInterval, Context.Host.Time);
            var feedbackShown = false;
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (_asleep)
                    continue;
                var feedback = _feedback is { } f && Now < f.Until;
                if (Session.ConnectionState != ObsConnectionState.Ready && !feedback && !feedbackShown)
                    continue;
                if ((Animating && Session.ConnectionState == ObsConnectionState.Ready) || feedback || feedbackShown)
                    Context.RequestRender();
                feedbackShown = feedback;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- formatos ----

    /// <summary>"01:02:03".</summary>
    protected static string Clock(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>Mini: "02:03" até uma hora, depois "1:02:03".</summary>
    protected static string ShortClock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    protected static string Number(double value, int decimals) =>
        value.ToString("N" + decimals, System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));

    protected static Color? Accent(bool on, Color color) => on ? color : null;
}
