using System.Net;
using System.Net.NetworkInformation;
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

    /// <summary>
    /// O OBS roda neste PC: loopback, <c>localhost</c>, o nome deste PC ou um IP de uma placa de rede dele. Só aí faz
    /// sentido dizer "OBS fechado", abrir o OBS daqui e ler o arquivo de configuração dele.
    /// </summary>
    public bool IsLocal => LocalHost.Is(Host);
}

/// <summary>Uma conexão com nome, para os widgets escolherem (ex.: "Este PC", "PC da live").</summary>
public sealed record ObsConnection(string Id, string Name, ObsEndpoint Endpoint);

/// <summary>Todas as conexões salvas. Sempre tem pelo menos uma, e uma delas é a padrão.</summary>
public sealed record ObsConnectionList(IReadOnlyList<ObsConnection> Connections, string DefaultId)
{
    public ObsConnection Default => Connections.FirstOrDefault(c => c.Id == DefaultId) ?? Connections[0];

    public ObsConnection? Find(string? id) => id is null ? null : Connections.FirstOrDefault(c => c.Id == id);

    /// <summary>A conexão escolhida pelo widget; sem escolha, ou se ela foi apagada, a padrão.</summary>
    public ObsConnection Resolve(string? id) => Find(id) ?? Default;
}

/// <summary>
/// Conexões do plugin, em <c>%LOCALAPPDATA%\OpenWigiDash\WidgetData\OBS\connections.json</c>. Cada widget escolhe uma
/// (ou fica na padrão). A senha vai criptografada por DPAPI para o usuário do Windows, como o token do plugin do
/// Discord. Ficam fora das configurações da instância porque estas vão junto ao duplicar e não são o lugar de senha.
/// </summary>
/// <remarks>
/// Até a versão com uma conexão só, o arquivo era <c>connection.json</c> (host, porta e senha). Enquanto a lista
/// nova não foi salva, aquela conexão aparece como a padrão, com o id <see cref="LocalId"/>. O arquivo antigo fica
/// como está, para quem voltar à versão anterior.
/// </remarks>
public sealed class ObsConnectionStore(string path, ISecretProtector protector, string? legacyPath = null)
{
    /// <summary>Id da conexão que veio da configuração única (e da primeira conexão de quem nunca configurou).</summary>
    public const string LocalId = "local";

    public const string LocalName = "Este PC";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _lock = new();

    public static ObsConnectionStore Default { get; } = Create(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenWigiDash", "WidgetData", "OBS"));

    private static ObsConnectionStore Create(string folder) => new(
        System.IO.Path.Combine(folder, "connections.json"), DpapiProtector.Instance, System.IO.Path.Combine(folder, "connection.json"));

    public string FilePath { get; } = path;

    /// <summary>A lista mudou na página do plugin (os widgets conferem se a conexão deles mudou).</summary>
    public event Action? Saved;

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public ObsConnectionList Load()
    {
        var file = Read<ConnectionsFile>(FilePath);
        var connections = (file?.Connections ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.Id))
            .DistinctBy(c => c.Id)
            .Select(c => new ObsConnection(c.Id!, string.IsNullOrWhiteSpace(c.Name) ? c.Host ?? c.Id! : c.Name.Trim(), ToEndpoint(c)))
            .ToList();
        if (connections.Count > 0)
        {
            var defaultId = connections.Any(c => c.Id == file!.DefaultId) ? file!.DefaultId! : connections[0].Id;
            return new ObsConnectionList(connections, defaultId);
        }

        // Nunca salvou a lista: a conexão única de antes (ou 127.0.0.1:4455) vira a padrão.
        var legacy = ToEndpoint(legacyPath is null ? null : Read<ConnectionEntry>(legacyPath));
        return new ObsConnectionList([new ObsConnection(LocalId, legacy.IsLocal ? LocalName : legacy.Host, legacy)], LocalId);
    }

    /// <summary>Endereço da conexão <paramref name="id"/> (ou da padrão, se ela não existe mais).</summary>
    public ObsEndpoint Endpoint(string? id) => Load().Resolve(id).Endpoint;

    public void Save(ObsConnectionList list)
    {
        if (list.Connections.Count == 0)
            throw new ArgumentException("Precisa de pelo menos uma conexão.", nameof(list));
        lock (_lock)
        {
            var file = new ConnectionsFile
            {
                DefaultId = list.Default.Id,
                Connections = [.. list.Connections.Select(c => new ConnectionEntry
                {
                    Id = c.Id,
                    Name = c.Name.Trim(),
                    Host = c.Endpoint.Host.Trim(),
                    Port = c.Endpoint.Port,
                    Password = string.IsNullOrEmpty(c.Endpoint.Password) ? null : protector.Protect(c.Endpoint.Password),
                })],
            };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, Json));
        }
        Saved?.Invoke();
    }

    private ObsEndpoint ToEndpoint(ConnectionEntry? entry)
    {
        string? password = null;
        if (!string.IsNullOrEmpty(entry?.Password))
        {
            try
            {
                password = protector.Unprotect(entry.Password);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
            {
                password = null; // de outro usuário ou PC: pede de novo
            }
        }
        return new ObsEndpoint(string.IsNullOrWhiteSpace(entry?.Host) ? ObsEndpoint.Default.Host : entry.Host.Trim(),
            entry?.Port is > 0 and < 65536 ? entry.Port : ObsEndpoint.DefaultPort, password);
    }

    private static T? Read<T>(string file) where T : class
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record ConnectionsFile
    {
        [JsonPropertyName("defaultId")] public string? DefaultId { get; init; }
        [JsonPropertyName("connections")] public List<ConnectionEntry>? Connections { get; init; }
    }

    /// <summary>Uma conexão no arquivo novo; no <c>connection.json</c> antigo, só host, porta e senha.</summary>
    private sealed record ConnectionEntry
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("host")] public string? Host { get; init; }
        [JsonPropertyName("port")] public int Port { get; init; }
        [JsonPropertyName("password")] public string? Password { get; init; }
    }
}

/// <summary>Se um endereço é deste PC. Os IPs das placas de rede são lidos no máximo uma vez por minuto.</summary>
internal static class LocalHost
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(1);
    private static (HashSet<IPAddress> Addresses, DateTimeOffset At)? _cache;

    public static bool Is(string host)
    {
        var name = host.Trim().TrimStart('[').TrimEnd(']');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(name, out var ip))
            return IPAddress.IsLoopback(ip) || OwnAddresses().Contains(ip);
        var machine = Environment.MachineName;
        return name.Equals(machine, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(machine + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<IPAddress> OwnAddresses()
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache is { } cache && now - cache.At < Refresh)
            return cache.Addresses;
        HashSet<IPAddress> addresses;
        try
        {
            addresses = [.. NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)];
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            addresses = [];
        }
        _cache = (addresses, now);
        return addresses;
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
