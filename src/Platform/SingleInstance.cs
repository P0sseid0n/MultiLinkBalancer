// Instância única do MultiLink: o mutex que o Program segura enquanto o app está aberto. Também usado pelo
// multilink-watchdog.exe (watchdog/), que é compilado com este arquivo.
using System;
using System.Security.AccessControl;
using System.Threading;

namespace MultiLink.Platform
{
    public static class SingleInstance
    {
        public const string MutexName = "Local\\MultiLinkBalancer.SingleInstance";

        // A instância aberta pela tarefa é elevada: sem administrador, abrir o mutex dela pode ser negado, o que também diz que existe.
        public static bool Running()
        {
            try
            {
                Mutex existing;
                if (!Mutex.TryOpenExisting(MutexName, MutexRights.Synchronize, out existing)) return false;
                existing.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; }
        }
    }
}
