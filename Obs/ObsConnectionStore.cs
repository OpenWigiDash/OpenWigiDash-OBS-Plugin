using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenWigiDash.ObsPlugin.Obs;

/// <summary>Para onde conectar: host, porta e a senha do servidor WebSocket do OBS.</summary>
public sealed record ObsEndpoint(string Host, int Port, string? Password)
{
    public const int DefaultPort = 4455;

    public static ObsEndpoint Default { get; } = new("127.0.0.1", DefaultPort, null);

    public Uri Uri => new($"ws://{(Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host)}:{Port}");

    public bool IsLocal => Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
}

/// <summary>
/// Conexão do plugin inteiro (um OBS para todos os widgets), em
/// <c>%LOCALAPPDATA%\OpenWigiDash\WidgetData\OBS\connection.json</c>. A senha vai criptografada por DPAPI para o
/// usuário do Windows, como o token do plugin do Discord. Fica fora das configurações da instância porque estas são
/// por widget e vão junto ao duplicar.
/// </summary>
public sealed class ObsConnectionStore(string path, ISecretProtector protector)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _lock = new();

    public static ObsConnectionStore Default { get; } = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenWigiDash", "WidgetData", "OBS", "connection.json"), DpapiProtector.Instance);

    public string FilePath { get; } = path;

    public ObsEndpoint Load()
    {
        var file = Read();
        string? password = null;
        if (!string.IsNullOrEmpty(file.Password))
        {
            try
            {
                password = protector.Unprotect(file.Password);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
            {
                password = null; // de outro usuário ou PC: pede de novo
            }
        }
        return new ObsEndpoint(string.IsNullOrWhiteSpace(file.Host) ? ObsEndpoint.Default.Host : file.Host,
            file.Port is > 0 and < 65536 ? file.Port : ObsEndpoint.DefaultPort, password);
    }

    public void Save(ObsEndpoint endpoint)
    {
        lock (_lock)
        {
            var file = new ConnectionFile
            {
                Host = endpoint.Host.Trim(),
                Port = endpoint.Port,
                Password = string.IsNullOrEmpty(endpoint.Password) ? null : protector.Protect(endpoint.Password),
            };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, Json));
        }
    }

    private ConnectionFile Read()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<ConnectionFile>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private sealed record ConnectionFile
    {
        [JsonPropertyName("host")] public string? Host { get; init; }
        [JsonPropertyName("port")] public int Port { get; init; }
        [JsonPropertyName("password")] public string? Password { get; init; }
    }
}

/// <summary>Guarda a senha em disco sem deixá-la legível.</summary>
public interface ISecretProtector
{
    string Protect(string secret);
    string Unprotect(string stored);
}

/// <summary>DPAPI do Windows, escopo do usuário.</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "OpenWigiDash.ObsPlugin"u8.ToArray();

    public static DpapiProtector Instance { get; } = new();

    public string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string stored) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
}

/// <summary>
/// O que o próprio OBS diz do servidor WebSocket, lido de
/// <c>%APPDATA%\obs-studio\plugin_config\obs-websocket\config.json</c>. Só roda quando a pessoa clica em
/// "Detectar" e só vale para o OBS deste PC.
/// </summary>
public sealed record ObsLocalConfig(bool Enabled, int Port, bool AuthRequired, string? Password)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "plugin_config", "obs-websocket", "config.json");

    /// <summary>Lê o arquivo; <c>null</c> se não existe (OBS antigo, não instalado ou portátil) ou não é JSON.</summary>
    public static ObsLocalConfig? TryRead(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static ObsLocalConfig? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var port = (int)(ObsProtocol.Num(root, "server_port") ?? ObsEndpoint.DefaultPort);
            return new ObsLocalConfig(
                ObsProtocol.Bool(root, "server_enabled") ?? false,
                port is > 0 and < 65536 ? port : ObsEndpoint.DefaultPort,
                ObsProtocol.Bool(root, "auth_required") ?? true,
                ObsProtocol.Str(root, "server_password"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
