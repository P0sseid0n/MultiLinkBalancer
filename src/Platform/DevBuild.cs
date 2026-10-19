// Modo de desenvolvimento: o .exe está ao lado do build.ps1 (no repositório). Nesse modo o app não se atualiza
// pelo GitHub (trocaria o build local pelo da release), libera o DevTools e ganha o "Recompilar e reiniciar":
// roda o build.ps1 -Restart numa janela do PowerShell. O script fecha este MultiLink pelo evento de saída, troca
// o .exe e reabre; se a compilação falhar, a janela fica aberta com o erro e o MultiLink continua rodando.
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace MultiLink.Platform
{
    public static class DevBuild
    {
        static Process running;

        static string ScriptPath { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "build.ps1"); } }

        public static readonly bool Available = File.Exists(ScriptPath);

        // quando este .exe foi compilado (o build.ps1 -Restart move o arquivo, o que mantém a data)
        public static readonly DateTime BuiltAt = File.GetLastWriteTimeUtc(Application.ExecutablePath);

        public static bool Running { get { return running != null && !running.HasExited; } }

        public static void Start()
        {
            if (Running) return;
            var script = ScriptPath.Replace("'", "''");
            var command = "try { & '" + script + "' -Restart } catch { Write-Host $_ -ForegroundColor Red; Read-Host 'Enter para fechar' }";
            running = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + command + "\"")
                { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(ScriptPath) });
        }
    }
}
