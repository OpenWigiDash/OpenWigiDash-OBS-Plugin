using System.Diagnostics;
using Microsoft.Win32;

namespace OpenWigiDash.ObsPlugin.Obs;

/// <summary>
/// Acha e abre o OBS instalado neste PC. Só serve quando a conexão é local: um OBS em outra máquina não tem como
/// ser aberto daqui.
/// </summary>
public static class ObsLauncher
{
    private static readonly Lazy<string?> Installed = new(FindInstalled);

    /// <summary>Os testes trocam a busca e a abertura (não abrem programa de verdade).</summary>
    internal static Func<string?>? FindOverride { get; set; }
    internal static Action<string>? StartOverride { get; set; }

    /// <summary>Caminho do <c>obs64.exe</c>, ou <c>null</c> se o OBS não está instalado.</summary>
    public static string? Executable => FindOverride is { } find ? find() : Installed.Value;

    /// <summary>Abre o OBS. A pasta de trabalho tem que ser a do executável, senão ele não acha os próprios arquivos.</summary>
    public static bool Start()
    {
        if (Executable is not { } exe)
            return false;
        if (StartOverride is { } start)
        {
            start(exe);
            return true;
        }
        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
        return process is not null;
    }

    private static string? FindInstalled()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        foreach (var root in Candidates())
        {
            var exe = Path.Combine(root, "bin", "64bit", "obs64.exe");
            if (File.Exists(exe))
                return exe;
        }
        return null;
    }

    /// <summary>A pasta que o instalador grava no registro e, se faltar, a pasta padrão.</summary>
    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            string? registered = null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OBS Studio");
                registered = key?.GetValue(null) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
            if (!string.IsNullOrWhiteSpace(registered))
                yield return registered;
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio");
    }
}
