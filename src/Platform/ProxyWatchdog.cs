// Vigia do proxy do Windows: um segundo MultiLink.exe, aberto pelo principal com "/vigia:<pid>", só espera ele
// fechar. Se fechou sem devolver o proxy (encerrado pelo Gerenciador de Tarefas, erro nativo, travamento que o
// Windows encerrou), devolve no lugar dele: sem isso os programas ficam sem internet até o MultiLink abrir de novo.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MultiLink.Common;
using MultiLink.Storage;

namespace MultiLink.Platform
{
    public static class ProxyWatchdog
    {
        const string Argument = "/vigia:";

        public static void Start(ActivityLog log)
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, Argument + Process.GetCurrentProcess().Id) { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception ex) { log.Add("Não foi possível abrir o vigia do proxy do Windows: " + ex.Message, ActivityLog.Error); }
        }

        // No começo do Main: true quando este processo é o vigia (e o trabalho dele já acabou).
        // instanceRunning: se há um MultiLink aberto (a instância única).
        public static bool Run(string[] args, Func<bool> instanceRunning)
        {
            var argument = args.FirstOrDefault(arg => arg.StartsWith(Argument, StringComparison.OrdinalIgnoreCase));
            if (argument == null) return false;
            int processId;
            if (!int.TryParse(argument.Substring(Argument.Length), out processId)) return true;
            try { using (var watched = Process.GetProcessById(processId)) watched.WaitForExit(); }
            catch (ArgumentException) { return true; } // já tinha fechado: a próxima abertura confere

            // outro MultiLink já abriu (reinício como administrador, atualização): o proxy é dele
            if (instanceRunning()) return true;
            var settings = AppSettings.Load();
            if (!settings.SystemProxyApplied) return true; // fechou direito
            SystemProxy.Restore(settings);
            var log = new ActivityLog(Path.Combine(AppPaths.Local, "eventos.log"));
            log.Add("O MultiLink fechou sem devolver o proxy do Windows (encerrado à força ou por um erro): o vigia devolveu.", ActivityLog.Error);
            log.Flush();
            return true;
        }
    }
}
