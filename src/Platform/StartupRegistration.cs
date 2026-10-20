// Iniciar com o Windows. Como administrador usa uma tarefa agendada elevada (necessária para o
// failover pelo roteamento e a captura); sem administrador usa a chave "Run" do registro, que não eleva.
// Com a tarefa, a chave "Run" também recebe uma entrada "/startup": é ela que faz o app aparecer em
// "Aplicativos de inicialização" do Gerenciador de Tarefas. Quem abre o app continua sendo a tarefa (no
// logon, sem o atraso que o Windows impõe à chave "Run"); a entrada só confere e, se ele não estiver
// aberto, dispara a tarefa. "Desativar" no Gerenciador de Tarefas é respeitado pelo próprio app.
using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MultiLink.Platform
{
    public static class StartupRegistration
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string Name = "MultiLinkBalancer";
        public const string TrayArgument = "/tray";       // aberto junto com o Windows: fica só na bandeja
        public const string LauncherArgument = "/startup"; // entrada da chave "Run" ao lado da tarefa agendada

        static string RunValue
        {
            get { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key == null ? null : key.GetValue(Name) as string; }
        }

        // Entrada "Run" que abre o app sem elevação (instalação sem administrador).
        public static bool UsesUnelevatedRunKey
        {
            get { var value = RunValue; return value != null && value.IndexOf(LauncherArgument, StringComparison.OrdinalIgnoreCase) < 0; }
        }

        // Desativado em "Aplicativos de inicialização": o Windows grava 12 bytes, o primeiro ímpar quando desativado.
        public static bool DisabledInTaskManager
        {
            get
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                    {
                        var flags = key == null ? null : key.GetValue(Name) as byte[];
                        return flags != null && flags.Length > 0 && (flags[0] & 1) == 1;
                    }
                }
                catch { return false; }
            }
        }

        // Registrado (pela chave "Run" ou, nas versões anteriores, só pela tarefa), sem contar o Gerenciador de Tarefas.
        public static bool IsRegistered { get { return RunValue != null || ScheduledTaskExists(); } }

        public static async Task Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) key.DeleteValue(Name, false);
            // ligar pelos Ajustes desfaz o "Desativar" do Gerenciador de Tarefas
            using (var key = Registry.CurrentUser.CreateSubKey(ApprovedKey)) key.DeleteValue(Name, false);
            if (Elevation.IsAdmin) await UnregisterTask();
            if (!enabled) return;
            if (Elevation.IsAdmin)
            {
                var result = await RegisterElevatedTask(Application.ExecutablePath);
                if (!result.Ok) throw new Exception(result.Output);
                WriteRunValue(LauncherArgument);
            }
            else
                WriteRunValue(TrayArgument);
        }

        // Versões anteriores registravam só a tarefa: acrescenta a entrada "/startup" sem mexer no "Desativar".
        public static void AddLauncherIfMissing()
        {
            if (RunValue == null && ScheduledTaskExists()) WriteRunValue(LauncherArgument);
        }

        static void WriteRunValue(string argument)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) key.SetValue(Name, "\"" + Application.ExecutablePath + "\" " + argument);
        }

        // Abre o app pela tarefa agendada (elevado, sem pedir permissão). Falso se a tarefa não existe ou não rodou.
        public static bool RunTask()
        {
            try { return ProcessRunner.Run("schtasks.exe", "/Run /TN " + Name).ExitCode == 0; }
            catch { return false; }
        }

        static bool ScheduledTaskExists()
        {
            try { return ProcessRunner.Run("schtasks.exe", "/Query /TN " + Name).ExitCode == 0; }
            catch { return false; }
        }

        // Tarefa no logon com privilégio máximo, sem limite de tempo e também na bateria.
        static Task<CommandResult> RegisterElevatedTask(string exePath)
        {
            return PowerShell.Run("$a = New-ScheduledTaskAction -Execute " + PowerShell.Quote(exePath) + " -Argument '" + TrayArgument + "'; " +
                                  "$t = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; " +
                                  "$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries; " +
                                  "$p = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive; " +
                                  "Register-ScheduledTask -TaskName " + PowerShell.Quote(Name) + " -Action $a -Trigger $t -Settings $s -Principal $p -Force | Out-Null", false);
        }

        static Task<CommandResult> UnregisterTask()
        {
            return PowerShell.Run("Unregister-ScheduledTask -TaskName " + PowerShell.Quote(Name) + " -Confirm:$false -ErrorAction SilentlyContinue", false);
        }
    }
}
