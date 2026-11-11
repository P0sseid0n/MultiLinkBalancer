// Como administrador, o app e o vigia gravam, trocam e apagam arquivos em pastas do usuário (%APPDATA%,
// %LOCALAPPDATA%, a pasta escolhida no download). Um programa do usuário, sem administrador, pode trocar uma dessas
// pastas por uma junção que aponte para uma pasta do Windows, e a operação de administrador cairia lá.
// O Redirection Guard do Windows (Windows 10 21H2 e Windows 11) faz o processo não seguir junções criadas por quem não é
// administrador: a abertura falha em vez de seguir. Ligado no começo do processo, vale para todas as operações de
// arquivo dele (não passa para os processos filhos).
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MultiLink.Platform
{
    public static class RedirectionGuard
    {
        const int ProcessRedirectionTrustPolicy = 16; // PROCESS_MITIGATION_POLICY
        const int EnforceRedirectionTrust = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessMitigationPolicy(int policy, ref int value, IntPtr size);

        // false: Windows sem suporte (anterior ao 21H2) ou processo sem administrador (não precisa).
        public static bool EnableIfElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return false;
            int flags = EnforceRedirectionTrust;
            try { return SetProcessMitigationPolicy(ProcessRedirectionTrustPolicy, ref flags, new IntPtr(sizeof(int))); }
            catch (EntryPointNotFoundException) { return false; }
        }
    }
}
