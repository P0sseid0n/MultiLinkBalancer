// Mantém a lista de links do Balancer em dia com os adaptadores do Windows e guarda as escolhas
// de cada link (ativo, peso, velocidade medida) no config.ini.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Links;
using MultiLink.Monitoring;
using MultiLink.Platform;
using MultiLink.Storage;

namespace MultiLink.App
{
    public class LinkManager
    {
        // chaves do config.ini por link
        const string PreferencesPrefix = "link.";  // "ativo;peso", ex.: "1;72"
        const string KnownPrefix = "known.";       // "nome|descrição|cabeado": lembra adaptadores desativados
        const string SpeedPrefix = "speed.";       // última velocidade medida, em Mbps

        const int HealthCheckIntervalMs = 3000;
        const int SpeedTestSeconds = 8;
        const int MaxWeight = 1000;

        readonly Balancer balancer;
        readonly AppSettings settings;
        readonly ActivityLog log;
        volatile bool networkChanged;

        public LinkManager(Balancer balancer, AppSettings settings, ActivityLog log)
        {
            this.balancer = balancer;
            this.settings = settings;
            this.log = log;
        }

        public bool Measuring { get; private set; }

        public List<Link> All() { return balancer.Snapshot(); }

        public Link Find(string id)
        {
            var link = balancer.Snapshot().FirstOrDefault(candidate => candidate.Id == id);
            if (link == null) throw new Exception("Link não encontrado.");
            return link;
        }

        public bool AnyHasIpv6() { return balancer.Snapshot().Any(link => link.Present && link.Enabled && link.AddressV6 != null); }

        // Links que o usuário usa e estão fora do ar (sem contar os desativados por ele).
        public List<string> DownLinkNames()
        {
            return balancer.Snapshot().Where(link => link.Enabled && LinkHealth.IsDown(link.Health) && !link.UserDisabled).Select(link => link.Name).ToList();
        }

        // Links em condições de baixar agora.
        public List<Link> Usable()
        {
            return balancer.Snapshot().Where(link => link.Enabled && link.Present && link.InternetUp && link.Weight > 0).ToList();
        }

        // ------------------------------------------------------------ descoberta
        public void Start()
        {
            LoadKnownAdapters();
            Refresh();
            NetworkChange.NetworkAddressChanged += (sender, e) => networkChanged = true;
            NetworkChange.NetworkAvailabilityChanged += (sender, e) => networkChanged = true;
            Task.Run(() => HealthCheckLoop());
        }

        // Chamado a cada segundo: relê os adaptadores se o Windows avisou de alguma mudança.
        public void RefreshIfNetworkChanged()
        {
            if (!networkChanged) return;
            networkChanged = false;
            Refresh();
        }

        async Task HealthCheckLoop()
        {
            while (true)
            {
                await Task.WhenAll(balancer.Snapshot().Select(HealthChecker.Check));
                await Task.Delay(HealthCheckIntervalMs);
            }
        }

        public void CheckAllNow()
        {
            foreach (var link in balancer.Snapshot()) { var ignored = HealthChecker.Check(link); }
        }

        // Adaptadores desativados no Windows somem da lista do sistema: lembramos deles para poder reativá-los.
        void LoadKnownAdapters()
        {
            balancer.Mutate(links =>
            {
                foreach (var key in settings.KeysStartingWith(KnownPrefix))
                {
                    var fields = settings.Get(key, "").Split('|');
                    var link = new Link
                    {
                        Id = key.Substring(KnownPrefix.Length), Name = fields[0], Description = fields.Length > 1 ? fields[1] : "",
                        Wired = fields.Length > 2 && fields[2] == "1",
                        Address = IPAddress.None, Gateway = "", Present = false, AdapterState = LinkHealth.Disabled
                    };
                    ApplySavedPreferences(link);
                    links.Add(link);
                }
            });
        }

        public void Refresh()
        {
            var found = AdapterDiscovery.Find(TrafficCapture.AdapterName);
            var statusById = new Dictionary<string, OperationalStatus>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()) statusById[adapter.Id] = adapter.OperationalStatus;
            balancer.Mutate(links =>
            {
                var wasPresent = links.ToDictionary(link => link, link => link.Present);
                foreach (var link in links) link.Present = false;
                foreach (var discovered in found)
                {
                    var existing = links.FirstOrDefault(link => link.Id == discovered.Id);
                    if (existing == null)
                    {
                        ApplySavedPreferences(discovered);
                        links.Add(discovered);
                        log.Add(string.Format("Adaptador detectado: {0} ({1}), IP {2}, gateway {3}", discovered.Name, discovered.Description, discovered.Address, discovered.Gateway));
                    }
                    else UpdateAdapter(existing, discovered, wasPresent[existing]);
                    settings.Set(KnownPrefix + discovered.Id, discovered.Name + "|" + discovered.Description + "|" + (discovered.Wired ? "1" : "0"));
                }
                // por que o adaptador sumiu: desativado no Windows, sem IP/gateway ou cabo desconectado
                foreach (var missing in links.Where(link => !link.Present))
                {
                    OperationalStatus status;
                    missing.AdapterState = !statusById.TryGetValue(missing.Id, out status) ? LinkHealth.Disabled
                                         : status == OperationalStatus.Up ? LinkHealth.NoConfig : LinkHealth.Unplugged;
                }
            });
            settings.Save();
            CheckAllNow();
        }

        void UpdateAdapter(Link existing, Link discovered, bool wasPresent)
        {
            if (!existing.Address.Equals(discovered.Address) && !existing.Address.Equals(IPAddress.None))
                log.Add(string.Format("[{0}] IP mudou para {1}", existing.Name, discovered.Address));
            existing.Address = discovered.Address;
            existing.AddressV6 = discovered.AddressV6;
            existing.Gateway = discovered.Gateway;
            existing.Name = discovered.Name;
            existing.Description = discovered.Description;
            existing.AdapterSpeedBps = discovered.AdapterSpeedBps;
            if (!wasPresent) { existing.InternetUp = true; existing.ConsecutiveFailures = 0; } // voltou: começa como online
            existing.Present = true;
        }

        // ------------------------------------------------------------ escolhas do usuário
        void ApplySavedPreferences(Link link)
        {
            var saved = settings.Get(PreferencesPrefix + link.Id, null);
            if (saved != null)
            {
                var fields = saved.Split(';');
                link.Enabled = fields[0] == "1";
                int weight;
                if (fields.Length > 1 && int.TryParse(fields[1], out weight)) link.Weight = weight;
            }
            else
            {
                // primeira vez: só cabos entram no balanceamento (Wi-Fi e 4G o usuário liga)
                link.Enabled = link.Wired;
                link.Weight = 1;
            }
            link.MeasuredMbps = settings.GetInt(SpeedPrefix + link.Id, -1);
        }

        void SavePreferences(Link link)
        {
            settings.Set(PreferencesPrefix + link.Id, (link.Enabled ? "1" : "0") + ";" + link.Weight);
        }

        // enabled e weight: null = não mudar.
        public void SetPreferences(string id, bool? enabled, int? weight)
        {
            var link = Find(id);
            if (enabled.HasValue)
            {
                link.Enabled = enabled.Value;
                log.Add(string.Format("[{0}] {1} no balanceamento", link.Name, link.Enabled ? "incluído" : "retirado"));
            }
            if (weight.HasValue)
            {
                if (weight.Value < 0 || weight.Value > MaxWeight) throw new Exception("O peso deve estar entre 0 e 1000.");
                link.Weight = weight.Value;
            }
            SavePreferences(link);
            settings.Save();
        }

        // Ativa o adaptador desativado ou desativa o ativo, no Windows. Devolve o novo estado.
        public async Task<bool> ToggleAdapter(string id)
        {
            var link = Find(id);
            bool enable = !link.Present && link.AdapterState == LinkHealth.Disabled;
            log.Add(string.Format("[{0}] {1} o adaptador no Windows...", link.Name, enable ? "ativando" : "desativando"));
            if (!enable) link.UserDisabled = true;
            var result = await NetworkAdapters.SetEnabled(link.Name, enable);
            if (!result.Ok)
            {
                if (!enable) link.UserDisabled = false;
                var error = string.Format("Não foi possível {0} {1}: {2}", enable ? "ativar" : "desativar", link.Name, result.Output);
                log.Add(error, ActivityLog.Error);
                throw new Exception(error);
            }
            log.Add(string.Format("[{0}] adaptador {1} no Windows", link.Name, enable ? "ativado" : "desativado"));
            networkChanged = true;
            if (enable) { await Task.Delay(4000); networkChanged = true; } // o DHCP leva alguns segundos
            return enable;
        }

        // ------------------------------------------------------------ medição de velocidade
        // Primeira execução: mede os links para os pesos refletirem a velocidade real.
        public async void MeasureOnFirstRun()
        {
            if (settings.SpeedMeasured) return;
            await Task.Delay(1500);
            try { if (!settings.SpeedMeasured) await MeasureAndSetWeights(); }
            catch (Exception ex) { log.Add("Medição automática não concluída: " + ex.Message, ActivityLog.Error); }
        }

        // Mede todos os links online em paralelo e define o peso proporcional à velocidade.
        public async Task MeasureAndSetWeights()
        {
            var links = balancer.Snapshot().Where(link => link.Present && link.InternetUp).ToList();
            if (links.Count == 0) throw new Exception("Nenhum link online para medir.");
            if (Measuring) return;
            Measuring = true;
            log.Add("Medindo a velocidade de " + links.Count + " link(s) em paralelo (cerca de 8 s)...");
            try
            {
                var results = await Task.WhenAll(links.Select(link => SpeedTest.MeasureMbps(link, SpeedTestSeconds)));
                for (int i = 0; i < links.Count; i++)
                {
                    var link = links[i];
                    double mbps = results[i];
                    if (mbps < 1) { log.Add(string.Format("[{0}] a medição falhou; peso mantido em {1}", link.Name, link.Weight), ActivityLog.Error); continue; }
                    link.MeasuredMbps = mbps;
                    settings.Set(SpeedPrefix + link.Id, Math.Round(mbps));
                    // peso proporcional à velocidade: 700 Mbps -> 70, 95 Mbps -> 10
                    link.Weight = Math.Max(1, Math.Min(MaxWeight, (int)Math.Round(mbps / 10)));
                    SavePreferences(link);
                    log.Add(string.Format("[{0}] {1:0} Mbps, peso {2}", link.Name, mbps, link.Weight));
                }
                if (results.Any(mbps => mbps >= 1)) settings.SpeedMeasured = true;
                settings.Save();
            }
            finally { Measuring = false; }
        }
    }
}
