// Modo de desenvolvimento: o .exe está ao lado do build.ps1 (no repositório) ou é a cópia instalada (Installation) de
// um build local, que guarda a pasta do repositório (AssemblyMetadata "DevRepository", gravado pelo build.ps1; o build
// da release não leva). Nesse modo o app não se atualiza pelo GitHub (trocaria o build local pelo da release), libera
// o DevTools e ganha o "Recompilar e reiniciar": roda o build.ps1 numa janela do PowerShell (-Restart no repositório,
// -Install na cópia instalada). O script fecha este MultiLink pelo evento de saída, troca o .exe e reabre; se a
// compilação falhar, a janela fica aberta com o erro e o MultiLink continua rodando.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace MultiLink.Platform
{
    public static class DevBuild
    {
        static Process running;

        static readonly string ScriptPath = FindScript();

        static string FindScript()
        {
            var besideExe = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "build.ps1");
            if (File.Exists(besideExe)) return besideExe;
            if (!Installation.IsInstalled) return null;
            var repository = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
                .Cast<AssemblyMetadataAttribute>().Where(meta => meta.Key == "DevRepository").Select(meta => meta.Value).FirstOrDefault();
            if (string.IsNullOrEmpty(repository)) return null;
            var script = Path.Combine(repository, "build.ps1");
            return File.Exists(script) ? script : null;
        }

        public static readonly bool Available = ScriptPath != null;

        // quando este .exe foi compilado (o build.ps1 -Restart move o arquivo e o -Install copia, os dois mantêm a data)
        public static readonly DateTime BuiltAt = File.GetLastWriteTimeUtc(Application.ExecutablePath);

        public static bool Running { get { return running != null && !running.HasExited; } }

        public static void Start()
        {
            if (!Available || Running) return;
            var script = ScriptPath.Replace("'", "''");
            var mode = Installation.IsInstalled ? "-Install" : "-Restart";
            var command = "try { & '" + script + "' " + mode + " } catch { Write-Host $_ -ForegroundColor Red; Read-Host 'Enter para fechar' }";
            var arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + command + "\"";
            if (Installation.IsInstalled && Elevation.IsAdmin) { StartUnelevated(arguments); return; }
            running = Process.Start(new ProcessStartInfo(PowerShell.Exe, arguments)
                { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(ScriptPath) });
        }

        // O repositório é do usuário: a cópia instalada, como administrador, não roda o build.ps1 dele como
        // administrador (qualquer programa poderia ter mexido no script). O Explorer abre um .cmd com o usuário comum,
        // e o próprio build.ps1 -Install pede a confirmação de administrador só para copiar o .exe.
        static void StartUnelevated(string arguments)
        {
            var launcher = Path.Combine(Path.GetTempPath(), "multilink-rebuild.cmd");
            File.WriteAllText(launcher, "@echo off\r\ncd /d \"" + Path.GetDirectoryName(ScriptPath).Replace("%", "%%") + "\"\r\n\"" +
                                        PowerShell.Exe + "\" " + arguments.Replace("%", "%%") + "\r\n");
            Process.Start(ProcessRunner.Explorer, "\"" + launcher + "\"").Dispose();
        }
    }
}
