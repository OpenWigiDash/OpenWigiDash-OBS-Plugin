using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWigi.Sdk;

namespace ObsPlugin.Tests;

/// <summary>Contexto mínimo de instância: configurações em memória e contagem de pedidos de quadro.</summary>
internal sealed class FakeContext(WidgetSize size, bool asleep = false) : IWidgetContext
{
    private int _renders;

    public Guid InstanceId { get; } = Guid.NewGuid();
    public IWidgetHost Host { get; } = new FakeHost();
    public WidgetSize Size { get; } = size;
    /// <summary>Área de conteúdo com a moldura padrão da página (no SDK 3.0, sem respiro nem borda).</summary>
    public PixelSize PixelSize => Frame.ContentSize(Size.CellSpan);
    public TileFrame Frame { get; } = TileFrame.Default;
    public bool IsFullscreen => false;
    public bool IsAsleep { get; } = asleep;
    public WidgetTheme Theme { get; } = new();
    public IWidgetSettings Settings { get; } = new MemorySettings();
    public IWidgetFiles Files { get; } = new TempFiles();
    public IWidgetActions Actions => throw new NotSupportedException();
    public ILogger Logger => NullLogger.Instance;
    public CancellationToken Stopping => CancellationToken.None;

    public int Renders => Volatile.Read(ref _renders);

    public ValueTask PresentAsync(WidgetFrame frame, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public void RequestRender() => Interlocked.Increment(ref _renders);
    public ValueTask<bool> RequestFullscreenAsync(bool fullscreen, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    private sealed class FakeHost : IWidgetHost
    {
        public string ResourceDirectory => AppContext.BaseDirectory;
        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
        public TimeProvider Time => TimeProvider.System;
        public WidgetTheme Theme { get; } = new();
    }

    /// <summary>Arquivos da instância numa pasta temporária.</summary>
    internal sealed class TempFiles : IWidgetFiles
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "owd-tests-" + Guid.NewGuid().ToString("N"));

        public ValueTask<string> ImportAsync(string fileId, string sourcePath, CancellationToken cancellationToken = default)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, fileId + Path.GetExtension(sourcePath));
            File.Copy(sourcePath, path, overwrite: true);
            return ValueTask.FromResult(path);
        }

        public ValueTask<string> CreateAsync(string fileId, string fileName, CancellationToken cancellationToken = default)
        {
            System.IO.Directory.CreateDirectory(Directory);
            return ValueTask.FromResult(Path.Combine(Directory, fileId + Path.GetExtension(fileName)));
        }

        public ValueTask<string?> GetPathAsync(string fileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(System.IO.Directory.Exists(Directory)
                ? System.IO.Directory.GetFiles(Directory, fileId + ".*").FirstOrDefault()
                : null);

        public ValueTask<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
        {
            var found = false;
            if (System.IO.Directory.Exists(Directory))
            {
                foreach (var file in System.IO.Directory.GetFiles(Directory, fileId + ".*"))
                {
                    File.Delete(file);
                    found = true;
                }
            }
            return ValueTask.FromResult(found);
        }
    }

    private sealed class MemorySettings : IWidgetSettings
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.TryGetValue(key, out var v) ? v : null);

        public ValueTask SetAsync(string key, string value, bool cloneable = true, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.TryRemove(key, out _));

        public ValueTask<IReadOnlyCollection<string>> GetKeysAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyCollection<string>>(_values.Keys.ToList());
    }
}
