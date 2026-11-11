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
        // Prioridade da tarefa: 4 é a prioridade normal. O padrão do Agendador (7) roda o app abaixo do normal, e o
        // WebView2 herda isso: com o PC ocupado, o painel demorava a aparecer.
        const int TaskPriority = 4;

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
                var unsafeReason = ProtectedLocation.WhyNotProtected(Application.ExecutablePath);
                if (unsafeReason != null)
                {
                    WriteRunValue(TrayArgument); // inicia com o Windows, mas sem administrador
                    throw new Exception(UnsafeLocationMessage(unsafeReason));
                }
                var result = await RegisterElevatedTask(Application.ExecutablePath);
                if (!result.Ok) throw new Exception(result.Output);
                WriteRunValue(LauncherArgument);
            }
            else
                WriteRunValue(TrayArgument);
        }

        // A tarefa elevada roda o .exe como administrador sem aviso do UAC, no logon e quando a entrada "Run" (sem
        // elevação) a dispara. Só vale com o .exe numa pasta que usuários comuns não alteram (ProtectedLocation).
        public static bool ElevatedTaskAllowed { get { return ProtectedLocation.WhyNotProtected(Application.ExecutablePath) == null; } }

        static string UnsafeLocationMessage(string reason)
        {
            return "a inicialização como administrador exige o MultiLink numa pasta que só administradores alteram, como " +
                   @"C:\Program Files\MultiLinkBalancer (" + reason + "). Ele vai iniciar com o Windows sem administrador.";
        }

        // Tarefa elevada criada antes desta verificação, para um .exe que qualquer programa do usuário pode trocar: vira
        // a entrada "Run" sem elevação. Devolve a mensagem para o registro de eventos, ou null se não precisou mexer.
        public static async Task<string> DowngradeUnsafeTask()
        {
            if (!Elevation.IsAdmin || !ScheduledTaskExists()) return null;
            var reason = ProtectedLocation.WhyNotProtected(Application.ExecutablePath);
            if (reason == null) return null;
            await UnregisterTask();
            WriteRunValue(TrayArgument); // o "Desativar" do Gerenciador de Tarefas continua valendo (mesmo nome)
            return "Tarefa de inicialização como administrador removida: " + UnsafeLocationMessage(reason);
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

        // Tarefa das versões anteriores, criada com a prioridade padrão do Agendador: passa para a normal. Vale a partir
        // da próxima vez que a tarefa abrir o app. true se mudou.
        public static async Task<bool> FixTaskPriority()
        {
            if (!Elevation.IsAdmin || !ScheduledTaskExists()) return false;
            var result = await PowerShell.Run("$t = Get-ScheduledTask -TaskName " + PowerShell.Quote(Name) + " -ErrorAction Stop; " +
                                              "if ($t.Settings.Priority -ne " + TaskPriority + ") { $t.Settings.Priority = " + TaskPriority + "; " +
                                              "Set-ScheduledTask -InputObject $t -ErrorAction Stop | Out-Null; 'mudou' }", false);
            return result.Ok && result.Output != null && result.Output.Contains("mudou");
        }

        // Tarefa no logon com privilégio máximo, prioridade normal, sem limite de tempo e também na bateria.
        static Task<CommandResult> RegisterElevatedTask(string exePath)
        {
            return PowerShell.Run("$a = New-ScheduledTaskAction -Execute " + PowerShell.Quote(exePath) + " -Argument '" + TrayArgument + "'; " +
                                  "$t = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; " +
                                  "$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Priority " + TaskPriority + "; " +
                                  "$p = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive; " +
                                  "Register-ScheduledTask -TaskName " + PowerShell.Quote(Name) + " -Action $a -Trigger $t -Settings $s -Principal $p -Force | Out-Null", false);
        }

        static Task<CommandResult> UnregisterTask()
        {
            return PowerShell.Run("Unregister-ScheduledTask -TaskName " + PowerShell.Quote(Name) + " -Confirm:$false -ErrorAction SilentlyContinue", false);
        }
    }
}
