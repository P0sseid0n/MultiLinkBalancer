using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;

namespace MultiLink.Platform
{
    public static class Elevation
    {
        public static bool IsAdmin
        {
            get
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        // Argumento que faz a nova instância esperar esta fechar (e liberar a instância única).
        public static string WaitForThisProcessArgument
        {
            get { return "/waitpid:" + Process.GetCurrentProcess().Id; }
        }

        // Abre outra instância como administrador. Quem chama deve fechar esta em seguida.
        public static void StartElevatedInstance()
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, WaitForThisProcessArgument) { UseShellExecute = true, Verb = "runas" }); }
            catch (Win32Exception) { throw new Exception("Reinício como administrador cancelado."); }
        }
    }
}
