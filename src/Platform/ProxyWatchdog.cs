// Vigia do proxy do Windows: o multilink-vigia.exe (watchdog/), embutido no .exe, recebe o PID do MultiLink e só
// espera ele fechar. Se fechou sem devolver o proxy (encerrado pelo Gerenciador de Tarefas, erro nativo, travamento
// que o Windows encerrou), devolve no lugar dele: sem isso os programas ficam sem internet até o MultiLink abrir de novo.
using System;
using System.Diagnostics;
using MultiLink.Common;

namespace MultiLink.Platform
{
    public static class ProxyWatchdog
    {
        const string ExeName = "multilink-vigia.exe";

        public static void Start(ActivityLog log)
        {
            try
            {
                var exe = EmbeddedFiles.ExtractWatchdog(ExeName);
                Process.Start(new ProcessStartInfo(exe, Process.GetCurrentProcess().Id.ToString()) { UseShellExecute = false, CreateNoWindow = true }).Dispose();
            }
            catch (Exception ex) { log.Add("Não foi possível abrir o vigia do proxy do Windows: " + ex.Message, ActivityLog.Error); }
        }
    }
}
