// Perfis de rede do Windows (NLA) do adaptador MultiLink. O Windows cria um perfil para cada rede
// nova e, se o nome já existe, acrescenta " 2", " 3"... Antes do GUID fixo (ver TrafficCapture) o
// adaptador nascia com GUID novo a cada vez e esses perfis se acumulavam. Aqui apaga os que sobraram
// e devolve o nome "MultiLink" ao perfil em uso (o Windows mostra o nome novo na próxima conexão).
using System;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MultiLink.Platform
{
    static class NetworkProfiles
    {
        const string ProfilesKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles";
        const string SignaturesKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Signatures\Unmanaged";

        // Devolve quantos perfis antigos foram apagados (0 se o perfil atual não apareceu a tempo).
        public static async Task<int> Tidy(string adapterName)
        {
            var pattern = new Regex("^" + Regex.Escape(adapterName) + "( \\d+)?$");
            // logo depois de o adaptador subir o perfil ainda está sendo identificado (outro nome)
            for (int attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(1000);
                var current = await Task.Run(() => CurrentProfileName(adapterName));
                if (current != null && pattern.IsMatch(current))
                    return await Task.Run(() => RemoveStale(pattern, current, adapterName));
            }
            return 0;
        }

        static string CurrentProfileName(string adapterName)
        {
            using (var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT Name FROM MSFT_NetConnectionProfile WHERE InterfaceAlias='" + adapterName + "'"))
                return searcher.Get().Cast<ManagementObject>().Select(profile => profile["Name"] as string).FirstOrDefault();
        }

        static int RemoveStale(Regex pattern, string current, string adapterName)
        {
            int removed = 0;
            using (var profiles = Registry.LocalMachine.OpenSubKey(ProfilesKey, true))
            {
                if (profiles == null) return 0;
                string currentGuid = null;
                foreach (var guid in profiles.GetSubKeyNames())
                {
                    string name;
                    using (var profile = profiles.OpenSubKey(guid)) name = profile == null ? null : profile.GetValue("ProfileName") as string;
                    if (name == null || !pattern.IsMatch(name)) continue;
                    if (name == current) { currentGuid = guid; continue; }
                    profiles.DeleteSubKeyTree(guid, false);
                    RemoveSignatures(guid);
                    removed++;
                }
                if (currentGuid != null && current != adapterName)
                    using (var profile = profiles.OpenSubKey(currentGuid, true))
                    {
                        profile.SetValue("ProfileName", adapterName);
                        profile.SetValue("Description", adapterName);
                    }
            }
            return removed;
        }

        // A assinatura liga a rede ao perfil; sem o perfil ela só ficaria sobrando.
        static void RemoveSignatures(string profileGuid)
        {
            using (var signatures = Registry.LocalMachine.OpenSubKey(SignaturesKey, true))
            {
                if (signatures == null) return;
                foreach (var id in signatures.GetSubKeyNames())
                {
                    string owner;
                    using (var signature = signatures.OpenSubKey(id)) owner = signature == null ? null : signature.GetValue("ProfileGuid") as string;
                    if (string.Equals(owner, profileGuid, StringComparison.OrdinalIgnoreCase))
                        signatures.DeleteSubKeyTree(id, false);
                }
            }
        }
    }
}
