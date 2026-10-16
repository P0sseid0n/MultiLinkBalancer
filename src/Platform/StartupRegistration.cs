// Iniciar com o Windows. Como administrador usa uma tarefa agendada elevada (necessária para o
// failover pelo roteamento e a captura); sem administrador usa a chave "Run" do registro, que não eleva.
using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MultiLink.Platform
{
    public static class StartupRegistration
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "MultiLinkBalancer";

        public static bool UsesRunKey
        {
            get { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue(Name) != null; }
        }

        public static bool IsEnabled { get { return UsesRunKey || ScheduledTaskExists(); } }

        public static async Task Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) key.DeleteValue(Name, false);
            if (Elevation.IsAdmin) await UnregisterTask();
            if (!enabled) return;
            if (Elevation.IsAdmin)
            {
                var result = await RegisterElevatedTask(Application.ExecutablePath);
                if (!result.Ok) throw new Exception(result.Output);
            }
            else
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) key.SetValue(Name, "\"" + Application.ExecutablePath + "\" /tray");
        }

        static bool ScheduledTaskExists()
        {
            try { return ProcessRunner.Run("schtasks.exe", "/Query /TN " + Name).ExitCode == 0; }
            catch { return false; }
        }

        // Tarefa no logon com privilégio máximo, sem limite de tempo e também na bateria.
        static Task<CommandResult> RegisterElevatedTask(string exePath)
        {
            return PowerShell.Run("$a = New-ScheduledTaskAction -Execute " + PowerShell.Quote(exePath) + " -Argument '/tray'; " +
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
