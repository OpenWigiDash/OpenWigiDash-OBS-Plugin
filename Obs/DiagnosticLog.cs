using System.Globalization;

namespace OpenWigiDash.ObsPlugin.Obs;

/// <summary>
/// Log próprio do plugin em %LOCALAPPDATA%\OpenWigiDash\WidgetData\OBS\obs.log, porque o log do host não fica salvo
/// em arquivo. Pequeno: passou de 256 KB, recomeça (a versão anterior vira obs.old.log). Nunca grava a senha.
/// </summary>
internal sealed class DiagnosticLog
{
    private const long MaxBytes = 256 * 1024;
    private readonly Lock _lock = new();
    private readonly string? _path;

    public DiagnosticLog(string? path) => _path = path;

    public static DiagnosticLog Default { get; } = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenWigiDash", "WidgetData", "OBS", "obs.log"));

    public string? Path => _path;

    public void Write(string message, Exception? exception = null)
    {
        if (_path is null)
            return;
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(_path, System.IO.Path.ChangeExtension(_path, ".old.log"), overwrite: true);
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message;
                if (exception is not null)
                    line += Environment.NewLine + exception;
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Log é ajuda, não pode derrubar o widget.
        }
    }
}
