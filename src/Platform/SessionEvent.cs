// Evento nomeado que outros programas do mesmo usuário disparam (segunda execução do .exe, build.ps1 -Restart).
// Com o app aberto como administrador, um evento criado com a segurança padrão só aceita processos também
// elevados; este libera o usuário atual e rebaixa o rótulo de integridade para médio.
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MultiLink.Platform
{
    public static class SessionEvent
    {
        const int SDDL_REVISION_1 = 1;
        const string AllAccess = "0x1F0003"; // EVENT_ALL_ACCESS

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, int revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeWaitHandle CreateEvent(ref SECURITY_ATTRIBUTES attributes, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

        class NamedEvent : WaitHandle { public NamedEvent(SafeWaitHandle handle) { SafeWaitHandle = handle; } }

        // Evento de reset automático: cada Set libera uma espera.
        public static WaitHandle Create(string name)
        {
            string user;
            using (var identity = WindowsIdentity.GetCurrent()) user = identity.User.Value;
            var sddl = "D:(A;;" + AllAccess + ";;;" + user + ")(A;;" + AllAccess + ";;;BA)(A;;" + AllAccess + ";;;SY)S:(ML;;NW;;;ME)";
            IntPtr descriptor;
            uint size;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SDDL_REVISION_1, out descriptor, out size)) throw new Win32Exception();
            try
            {
                var attributes = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), lpSecurityDescriptor = descriptor };
                var handle = CreateEvent(ref attributes, false, false, name);
                if (handle.IsInvalid) throw new Win32Exception();
                return new NamedEvent(handle);
            }
            finally { LocalFree(descriptor); }
        }
    }
}
