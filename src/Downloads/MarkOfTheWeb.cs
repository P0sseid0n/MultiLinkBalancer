// Marca da internet (Mark-of-the-Web) no arquivo baixado, como fazem os navegadores: o fluxo alternativo
// Zone.Identifier com ZoneId=3. Sem ela, um .exe, .msi, .lnk, .iso ou documento do Office baixado pelo MultiLink abria
// sem o aviso do SmartScreen, sem o Modo de Exibição Protegido e sem o bloqueio de macros, e a marca não passava para
// o que fosse extraído de um .zip.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiLink.Downloads
{
    static class MarkOfTheWeb
    {
        const uint GENERIC_WRITE = 0x40000000, CREATE_ALWAYS = 2, FILE_ATTRIBUTE_NORMAL = 0x80;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        // source: de onde o arquivo veio (a URL final, depois dos redirecionamentos), sem usuário e senha.
        // Lança erro onde não há fluxos alternativos (pendrive em FAT32/exFAT), que também não guardam a marca dos navegadores.
        public static void Apply(string path, Uri source)
        {
            var host = source.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
            var content = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=" + host + "\r\n";
            // o FileStream do .NET Framework recusa o ':' do fluxo alternativo no caminho: abre pela API do Windows
            using (var handle = CreateFile(path + ":Zone.Identifier", GENERIC_WRITE, 0, IntPtr.Zero, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                using (var stream = new FileStream(handle, FileAccess.Write))
                {
                    var bytes = Encoding.ASCII.GetBytes(content);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
        }
    }
}
