// Instalação no computador, feita pelo próprio .exe (não há instalador separado): copia o app para
// C:\Program Files\MultiLinkBalancer, cria o atalho no Menu Iniciar e o item em "Aplicativos instalados" do Windows.
// Só lá a tarefa de "Iniciar com o Windows" pode rodar como administrador (ver ProtectedLocation): nenhum usuário
// comum consegue trocar o .exe dessa pasta. Os ajustes e os logs continuam em %APPDATA% e %LOCALAPPDATA%; os dados da
// interface da instância elevada ficam em %ProgramData% (ElevatedWebViewData).
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using MultiLink.Common;
using MultiLink.Storage;

namespace MultiLink.Platform
{
    public static class Installation
    {
        public const string InstallArgument = "/install";     // instância elevada que só instala e abre a cópia instalada
        public const string UninstallArgument = "/uninstall"; // chamado por "Aplicativos instalados"
        public const string UpdateArgument = "/update";       // instância elevada que atualiza a cópia instalada
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MultiLinkBalancer";
        const string DisplayName = "MultiLink Balancer";

        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MultiLinkBalancer"); } }
        public static string Exe { get { return Path.Combine(Folder, "MultiLinkBalancer.exe"); } }
        static string Shortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), DisplayName + ".lnk"); } }

        // Este .exe é a cópia instalada?
        public static bool IsInstalled
        {
            get { return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase); }
        }

        // A instância elevada aberta com /update atualiza assim que abrir (AppController).
        public static bool UpdateRequested;

        // Pedido pela interface. Sem administrador, abre este .exe elevado com /install (o Windows pede confirmação);
        // quem chama fecha esta instância em seguida, nos dois casos.
        public static async Task Start(ActivityLog log)
        {
            if (!Elevation.IsAdmin) { Elevation.StartElevatedInstance(InstallArgument); return; }
            await Task.Run(() => Install(log));
            OpenInstalled();
        }

        // Abertura com /install (já elevada, depois que a instância anterior fechou). false se não deu: o app abre normal.
        public static bool InstallFromArgument()
        {
            var log = new ActivityLog(Path.Combine(AppPaths.Local, "eventos.log"));
            try { Install(log); OpenInstalled(); return true; }
            catch (Exception ex)
            {
                MessageBox.Show("Não foi possível instalar o MultiLink: " + ex.Message, DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally { log.Flush(); }
        }

        static void Install(ActivityLog log)
        {
            var source = Path.GetFullPath(Application.ExecutablePath);
            if (IsInstalled) return;
            Directory.CreateDirectory(Folder); // herda as permissões do Arquivos de Programas
            string target = Exe, copy = target + ".new", previous = target + ".old";
            File.Copy(source, copy, true);
            if (File.Exists(target))
            {
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(target, previous); // o Updater.Cleanup da cópia instalada apaga
            }
            File.Move(copy, target);
            var unsafeReason = ProtectedLocation.WhyNotProtected(target);
            if (unsafeReason != null) throw new Exception("a pasta de instalação não está protegida: " + unsafeReason);
            var shortcut = CreateShortcut(target).Result;
            if (!shortcut.Ok) log.Add("Atalho no Menu Iniciar não criado: " + shortcut.Output, ActivityLog.Error);
            WriteUninstallEntry();
            log.Add("Instalado em " + Folder + ". O arquivo usado até agora (" + source + ") pode ser apagado.", ActivityLog.LinkUp);
        }

        static void OpenInstalled()
        {
            // a cópia instalada espera esta instância fechar (instância única) e herda o administrador
            Process.Start(new ProcessStartInfo(Exe, Elevation.WaitForThisProcessArgument) { UseShellExecute = false }).Dispose();
        }

        static Task<CommandResult> CreateShortcut(string target)
        {
            return PowerShell.Run("$s = (New-Object -ComObject WScript.Shell).CreateShortcut(" + PowerShell.Quote(Shortcut) + "); " +
                                  "$s.TargetPath = " + PowerShell.Quote(target) + "; $s.WorkingDirectory = " + PowerShell.Quote(Folder) + "; " +
                                  "$s.Description = 'Balanceia a internet entre várias conexões'; $s.Save()", false);
        }

        // Item em "Aplicativos instalados". Também na abertura da cópia instalada como administrador, para a versão
        // mostrada acompanhar as atualizações.
        public static void WriteUninstallEntry()
        {
            using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", DisplayName);
                key.SetValue("DisplayVersion", Updates.Updater.Current.ToString());
                key.SetValue("Publisher", "P0sseid0n");
                key.SetValue("DisplayIcon", Exe + ",0");
                key.SetValue("InstallLocation", Folder);
                key.SetValue("UninstallString", "\"" + Exe + "\" " + UninstallArgument);
                key.SetValue("URLInfoAbout", "https://github.com/P0sseid0n/MultiLinkBalancer");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                try { key.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord); } catch { }
            }
        }

        // Abertura com /uninstall. Pede administrador se precisar, confirma, fecha o MultiLink aberto (que devolve o
        // proxy e as rotas do Windows), tira a inicialização com o Windows, o atalho e o item, e apaga a pasta e os dados
        // da interface da instância elevada (%ProgramData%\MultiLinkBalancer, que só administradores apagam).
        public static void UninstallFromArgument()
        {
            if (!Elevation.IsAdmin)
            {
                try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, UninstallArgument) { UseShellExecute = true, Verb = "runas" }).Dispose(); }
                catch { } // cancelado no aviso do UAC
                return;
            }
            if (MessageBox.Show("Desinstalar o MultiLink Balancer?\n\nO app fecha, a inicialização com o Windows é removida e as pastas " + Folder +
                                " e " + ElevatedWebViewData.Root + " são apagadas. Os ajustes e os logs ficam em %APPDATA% e %LOCALAPPDATA%.", DisplayName,
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { Task.Run(() => Uninstall()).Wait(); }
            catch (AggregateException ex)
            {
                MessageBox.Show("A desinstalação não terminou: " + ex.InnerException.Message, DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show("MultiLink Balancer desinstalado.", DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (IsInstalled) DeleteFolderAfterExit(); // por último: este processo fecha logo em seguida
        }

        static async Task Uninstall()
        {
            CloseRunningInstance();
            await StartupRegistration.Set(false);
            try { ElevatedWebViewData.Delete(); } catch { } // só administradores apagam depois
            try { File.Delete(Shortcut); } catch { }
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        }

        // O .exe em uso não pode ser apagado por ele mesmo: um cmd.exe apaga a pasta logo depois de este processo fechar.
        static void DeleteFolderAfterExit()
        {
            var command = "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Folder + "\"";
            Process.Start(new ProcessStartInfo(ProcessRunner.SystemExe("cmd.exe"), command)
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory }).Dispose();
        }

        // Pede para o MultiLink aberto fechar como pelo "Sair" (devolvendo proxy e rotas) e espera até 30 s.
        static void CloseRunningInstance()
        {
            EventWaitHandle exitRequest;
            if (!EventWaitHandle.TryOpenExisting(Ui.MainWindow.ExitEventName, out exitRequest)) return;
            using (exitRequest) exitRequest.Set();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (SingleInstance.Running() && DateTime.UtcNow < deadline) Thread.Sleep(200);
            if (SingleInstance.Running()) throw new Exception("o MultiLink aberto não fechou; feche pelo \"Sair\" e tente de novo");
        }
    }
}
