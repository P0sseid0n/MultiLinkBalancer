// multilink-vigia.exe: aberto pelo MultiLink (Platform/ProxyWatchdog) com o PID dele, só espera ele fechar. Se fechou
// sem devolver o proxy do Windows (encerrado à força, erro nativo, travamento que o Windows encerrou), devolve no
// lugar dele: sem isso os programas ficam sem internet até o MultiLink abrir de novo.
// Compilado à parte pelo build.ps1, só com os arquivos de src que usa, e embutido no MultiLinkBalancer.exe.
using System;
using System.Diagnostics;
using System.IO;
using MultiLink.Common;
using MultiLink.Platform;
using MultiLink.Storage;

[assembly: System.Reflection.AssemblyTitle("MultiLink Balancer - vigia do proxy")] // nome no Gerenciador de Tarefas
[assembly: System.Reflection.AssemblyProduct("MultiLink Balancer")]
[assembly: System.Reflection.AssemblyCompany("P0sseid0n")]

namespace MultiLink.Watchdog
{
    static class Vigia
    {
        static void Main(string[] args)
        {
            int processId;
            if (args.Length != 1 || !int.TryParse(args[0], out processId)) return;
            try { using (var watched = Process.GetProcessById(processId)) watched.WaitForExit(); }
            catch (ArgumentException) { return; } // já tinha fechado: a próxima abertura confere

            // outro MultiLink já abriu (reinício como administrador, atualização): o proxy é dele
            if (SingleInstance.Running()) return;
            var settings = AppSettings.Load();
            if (!settings.SystemProxyApplied) return; // fechou direito
            SystemProxy.Restore(settings);
            var log = new ActivityLog(Path.Combine(AppPaths.Local, "eventos.log"));
            log.Add("O MultiLink fechou sem devolver o proxy do Windows (encerrado à força ou por um erro): o vigia devolveu.", ActivityLog.Error);
            log.Flush();
        }
    }
}
