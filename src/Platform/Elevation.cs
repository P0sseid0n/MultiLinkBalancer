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
        // extraArgument: o que a nova instância faz ao abrir (Installation.InstallArgument, UpdateArgument).
        public static void StartElevatedInstance(string extraArgument = null)
        {
            var arguments = WaitForThisProcessArgument + (extraArgument != null ? " " + extraArgument : "");
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, arguments) { UseShellExecute = true, Verb = "runas" }); }
            catch (Win32Exception) { throw new Exception("Reinício como administrador cancelado."); }
        }
    }
}
