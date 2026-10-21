// Enquanto a captura está ligada, desliga o "weak host send" e o "forwarding" nos links (store=active: nada fica
// salvo no Windows). Com qualquer um dos dois ligado, o Windows escolhe a saída de um socket só pela rota, ignorando o
// IP e até o IP_UNICAST_IF a que ele está preso (testado nos links, IPv4 e IPv6). As conexões do próprio MultiLink
// seguiriam as rotas da captura e voltariam para ela: o link pareceria sem internet. Ligam esses ajustes: o próprio
// Windows (weak host no IPv6), o Hotspot Móvel, o Compartilhamento de Internet, o Hyper-V e algumas VPNs.
// O net-merge chegou ao mesmo problema e à mesma solução.
// Os valores originais ficam no config.ini ("stronghost.<família>.<índice>" = "nome|weakhostsend=X forwarding=Y")
// até serem devolvidos: ao desligar a captura ou, depois de um fechamento inesperado, na abertura seguinte.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Storage;

namespace MultiLink.Platform
{
    public class StrongHost
    {
        const string KeyPrefix = "stronghost.";
        // O Windows volta a ligar o weak host do IPv6 sozinho (visto depois de recriar o adaptador da captura), e um
        // link novo pode chegar com a captura ligada: a verificação se repete. A consulta é pelo WMI, sem abrir processo.
        static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

        readonly AppSettings settings;
        readonly ActivityLog log;
        DateTime lastCheck = DateTime.MinValue;
        bool applying;
        bool queryFailed; // o erro da consulta aparece uma vez só

        public StrongHost(AppSettings settings, ActivityLog log) { this.settings = settings; this.log = log; }

        class Interface
        {
            public int Index;
            public string Alias, Family; // Family: "ipv4" ou "ipv6", como o netsh espera
            public bool WeakHostSend, Forwarding;
        }

        // interfaceIndexes: os índices IPv4 e IPv6 dos links. force: verifica já (ao ligar a captura, antes das rotas);
        // sem ele, no máximo a cada CheckInterval.
        public async Task Apply(IEnumerable<int> interfaceIndexes, bool force)
        {
            if (applying || (!force && DateTime.UtcNow - lastCheck < CheckInterval)) return;
            var indexes = interfaceIndexes.Where(index => index > 0).Distinct().ToList();
            if (indexes.Count == 0) return;
            applying = true;
            lastCheck = DateTime.UtcNow;
            try
            {
                List<Interface> enabled;
                try { enabled = await Task.Run(() => FindEnabled(indexes)); queryFailed = false; }
                catch (Exception ex)
                {
                    if (!queryFailed) log.Add("Não foi possível verificar o weak host send dos links: " + ex.Message, ActivityLog.Error);
                    queryFailed = true;
                    return;
                }

                var changed = new List<string>();
                bool forwardingOff = false;
                foreach (var found in enabled)
                {
                    var key = KeyPrefix + found.Family + "." + found.Index;
                    var original = "weakhostsend=" + (found.WeakHostSend ? "enabled" : "disabled") + " forwarding=" + (found.Forwarding ? "enabled" : "disabled");
                    // grava antes de mudar: se o app cair no meio, a abertura seguinte ainda sabe o que devolver.
                    // Se o Windows religou depois de o MultiLink desligar, o original continua o primeiro que foi gravado.
                    bool saved = settings.Has(key);
                    if (!saved) { settings.Set(key, found.Alias + "|" + original); settings.Save(); }
                    var error = await Task.Run(() => SetInterface(found.Family, "interface=" + found.Index, "weakhostsend=disabled forwarding=disabled"));
                    var family = found.Family == "ipv4" ? "IPv4" : "IPv6";
                    if (error != null)
                    {
                        if (!saved) { settings.Remove(key); settings.Save(); }
                        log.Add("[" + found.Alias + "] não foi possível desligar o weak host send no " + family + ": " + error, ActivityLog.Error);
                        continue;
                    }
                    var what = new List<string>();
                    if (found.WeakHostSend) what.Add("weak host send");
                    if (found.Forwarding) { what.Add("forwarding"); forwardingOff = true; }
                    changed.Add(found.Alias + " (" + family + ": " + string.Join(" e ", what) + ")");
                }
                if (changed.Count > 0)
                    log.Add("Desligado enquanto a captura está ligada, para as conexões do MultiLink saírem pela placa certa: " + string.Join(", ", changed) + "." +
                            (forwardingOff ? " O compartilhamento da internet por esse link (Hotspot Móvel) fica pausado até a captura desligar." : ""));
            }
            finally { applying = false; }
        }

        static List<Interface> FindEnabled(List<int> indexes)
        {
            var query = "SELECT InterfaceIndex, InterfaceAlias, AddressFamily, WeakHostSend, Forwarding FROM MSFT_NetIPInterface WHERE " +
                        string.Join(" OR ", indexes.Select(index => "InterfaceIndex=" + index));
            using (var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", query))
            {
                // AddressFamily: 2 = IPv4, 23 = IPv6. WeakHostSend e Forwarding: 0 = desligado, 1 = ligado.
                return searcher.Get().Cast<ManagementObject>()
                    .Select(row => new Interface
                    {
                        Index = Convert.ToInt32(row["InterfaceIndex"]),
                        Alias = row["InterfaceAlias"] as string,
                        Family = Convert.ToInt32(row["AddressFamily"]) == 23 ? "ipv6" : "ipv4",
                        WeakHostSend = Convert.ToInt32(row["WeakHostSend"]) == 1,
                        Forwarding = Convert.ToInt32(row["Forwarding"]) == 1
                    })
                    .Where(found => found.WeakHostSend || found.Forwarding)
                    .ToList();
            }
        }

        // Ao desligar a captura e na abertura (depois de um fechamento inesperado). Devolve pelo nome do adaptador: depois
        // de reiniciar o Windows o mesmo índice pode ser de outro adaptador (e os valores originais já voltaram sozinhos).
        public void RestoreAll()
        {
            lastCheck = DateTime.MinValue;
            var keys = settings.KeysStartingWith(KeyPrefix);
            if (keys.Count == 0) return;
            var restored = new List<string>();
            foreach (var key in keys)
            {
                var saved = settings.Get(key, "");
                settings.Remove(key);
                int separator = saved.LastIndexOf('|');
                var family = key.Substring(KeyPrefix.Length).Split('.')[0];
                if (separator <= 0 || (family != "ipv4" && family != "ipv6")) continue;
                var name = saved.Substring(0, separator);
                var error = SetInterface(family, "interface=\"" + name + "\"", saved.Substring(separator + 1));
                if (error == null) restored.Add(name + " (" + (family == "ipv4" ? "IPv4" : "IPv6") + ")");
                else log.Add("[" + name + "] não foi possível devolver o weak host send e o forwarding: " + error, ActivityLog.Error);
            }
            settings.Save();
            if (restored.Count > 0) log.Add("Weak host send e forwarding devolvidos ao que eram em " + string.Join(", ", restored) + ".");
        }

        // null se deu certo; senão, a mensagem do netsh.
        static string SetInterface(string family, string target, string values)
        {
            var result = ProcessRunner.Run("netsh.exe", "interface " + family + " set interface " + target + " " + values + " store=active");
            return result.ExitCode == 0 ? null : (result.StandardOutput + result.StandardError).Trim();
        }
    }
}
