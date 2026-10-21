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
        const string PeakPrefix = "peak.";         // pico de vazão dos pesos automáticos, em bytes/s

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
        public bool TestingSum { get; private set; }
        public SumTestResult LastSumTest { get; private set; }
        // o servidor de medição bloqueou há pouco: as medições esperam até esta hora; null se não há pausa
        public DateTime? MeasurePausedUntil { get { return SpeedTest.PausedUntil; } }

        public List<Link> All() { return balancer.Snapshot(); }

        public Link Find(string id)
        {
            var link = balancer.Snapshot().FirstOrDefault(candidate => candidate.Id == id);
            if (link == null) throw new Exception("Link não encontrado.");
            return link;
        }

        public bool AnyHasIpv6() { return balancer.Snapshot().Any(link => link.Present && link.Enabled && link.Ipv6.Works); }

        // Índices IPv4 e IPv6 dos adaptadores no ar (inclusive os desativados no app: as sondas de saúde usam todos).
        public IEnumerable<int> InterfaceIndexes()
        {
            return balancer.Snapshot().Where(link => link.Present).SelectMany(link => new[] { link.InterfaceIndex, link.InterfaceIndexV6 });
        }

        // Links que o usuário usa e estão fora do ar (sem contar os desativados por ele).
        public List<string> DownLinkNames()
        {
            return balancer.Snapshot().Where(link => link.Enabled && LinkHealth.IsDown(link.Health) && !link.UserDisabled).Select(link => link.Name).ToList();
        }

        // Links em condições de baixar agora.
        public List<Link> Usable()
        {
            return balancer.Snapshot().Where(link => link.Enabled && link.Present && link.InternetUp && balancer.WeightOf(link) > 0).ToList();
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
            existing.SubnetMask = discovered.SubnetMask;
            existing.InterfaceIndex = discovered.InterfaceIndex;
            if (!Equals(existing.AddressV6, discovered.AddressV6) || discovered.AddressV6 == null)
                existing.Ipv6.AddressChanged(discovered.AddressV6 != null);
            existing.AddressV6 = discovered.AddressV6;
            existing.InterfaceIndexV6 = discovered.InterfaceIndexV6;
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
            // sem pico guardado, a última medição serve de ponto de partida
            long peak;
            if (long.TryParse(settings.Get(PeakPrefix + link.Id, null), out peak)) link.PeakBytesPerSecond = peak;
            else if (link.MeasuredMbps > 0) link.PeakBytesPerSecond = link.MeasuredMbps * 125000;
        }

        // ------------------------------------------------------------ pesos automáticos
        // Chamado a cada segundo, depois das taxas. O pico vai para o config.ini, gravado com o resto dos ajustes
        // (ao sair, por exemplo): a próxima abertura já começa com os pesos que valiam.
        public void UpdateAutoWeights()
        {
            var links = balancer.Snapshot();
            AutoWeights.Update(links);
            foreach (var link in links.Where(link => link.PeakBytesPerSecond > 0))
            {
                var value = Math.Round(link.PeakBytesPerSecond).ToString();
                if (settings.Get(PeakPrefix + link.Id, null) != value) settings.Set(PeakPrefix + link.Id, value);
            }
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
        // Primeira execução com pesos manuais: mede os links para os pesos refletirem a velocidade real.
        // Com os automáticos não precisa: eles se ajustam pelo próprio tráfego.
        public async void MeasureOnFirstRun()
        {
            if (settings.SpeedMeasured || balancer.UseAutoWeights) return;
            await Task.Delay(1500);
            try { if (!settings.SpeedMeasured) await MeasureAndSetWeights(); }
            catch (Exception ex) { log.Add("Medição automática não concluída: " + ex.Message, ActivityLog.Error); }
        }

        // Mede todos os links online em paralelo e define o peso proporcional à velocidade.
        public async Task MeasureAndSetWeights()
        {
            var links = balancer.Snapshot().Where(link => link.Present && link.InternetUp).ToList();
            if (links.Count == 0) throw new Exception("Nenhum link online para medir.");
            if (Measuring || TestingSum) return;
            SpeedTest.EnsureNotPaused();
            Measuring = true;
            log.Add("Medindo a velocidade de " + links.Count + " link(s) em paralelo (cerca de 8 s)...");
            try
            {
                var results = await Task.WhenAll(links.Select(link => SpeedTest.Measure(link, SpeedTestSeconds)));
                int refusedStatus = 0;
                for (int i = 0; i < links.Count; i++)
                {
                    var link = links[i];
                    double mbps = results[i].Mbps;
                    // recusa no meio do teste: as conexões que sobraram mediram menos que o link rende
                    if (results[i].Refused)
                    {
                        refusedStatus = results[i].RefusedStatus;
                        log.Add(string.Format("[{0}] medição descartada: {1}; peso mantido em {2}", link.Name, SpeedTest.RefusalText(refusedStatus), balancer.WeightOf(link)), ActivityLog.Error);
                        continue;
                    }
                    if (mbps < 1) { log.Add(string.Format("[{0}] a medição falhou; peso mantido em {1}", link.Name, balancer.WeightOf(link)), ActivityLog.Error); continue; }
                    link.MeasuredMbps = mbps;
                    settings.Set(SpeedPrefix + link.Id, Math.Round(mbps));
                    // peso manual proporcional à velocidade: 700 Mbps -> 70, 95 Mbps -> 10 (fica guardado para quando
                    // os automáticos forem desligados); nos automáticos, a medição vira o pico de onde eles partem
                    link.Weight = Math.Max(1, Math.Min(MaxWeight, (int)Math.Round(mbps / 10)));
                    link.PeakBytesPerSecond = mbps * 125000;
                    SavePreferences(link);
                    if (!balancer.UseAutoWeights) log.Add(string.Format("[{0}] {1:0} Mbps, peso {2}", link.Name, mbps, link.Weight));
                    else log.Add(string.Format("[{0}] {1:0} Mbps: ponto de partida dos pesos automáticos", link.Name, mbps));
                }
                if (results.Any(result => !result.Refused && result.Mbps >= 1)) settings.SpeedMeasured = true;
                settings.Save();
                // o aviso chega no painel ou na bandeja; os links medidos sem recusa já foram salvos
                if (refusedStatus != 0) throw new Exception("Medição incompleta: " + SpeedTest.RefusalText(refusedStatus) + ".");
            }
            finally { Measuring = false; }
        }

        // Teste da soma: mede todos os links ao mesmo tempo e, em seguida, o que mais rendeu sozinho.
        // O "sozinho" é medido de novo porque a medição dos pesos também roda os links em paralelo.
        public async Task<SumTestResult> TestSum()
        {
            var links = balancer.Snapshot().Where(link => link.Enabled && link.Present && link.InternetUp).ToList();
            if (links.Count < 2) throw new Exception("O teste da soma precisa de pelo menos dois links online no balanceamento.");
            if (Measuring || TestingSum) return null;
            SpeedTest.EnsureNotPaused();
            TestingSum = true;
            log.Add(string.Format("Teste da soma: {0} links baixando juntos, depois o melhor sozinho (cerca de {1} s)...", links.Count, 2 * SpeedTestSeconds));
            try
            {
                // com qualquer recusa o resultado inteiro é descartado: uma parte medida pela metade distorce a comparação
                var measured = await SpeedTest.MeasureTogether(links, SpeedTestSeconds);
                ThrowIfRefused(measured);
                var together = measured.Select(link => link.Mbps).ToArray();
                double total = together.Sum();
                if (total < 1) throw new Exception("O teste da soma falhou: nenhum link conseguiu baixar do servidor de teste.");
                int best = Array.IndexOf(together, together.Max());
                var aloneResult = await SpeedTest.Measure(links[best], SpeedTestSeconds);
                ThrowIfRefused(new[] { aloneResult });
                double alone = aloneResult.Mbps;
                var result = new SumTestResult
                {
                    At = DateTime.Now, TotalMbps = total, BestName = links[best].Name, BestAloneMbps = alone,
                    PerLink = links.Select((link, i) => new KeyValuePair<string, double>(link.Name, together[i])).ToList()
                };
                LastSumTest = result;
                log.Add(result.Summary() + " (juntos: " + string.Join(", ", result.PerLink.Select(pair => string.Format("{0} {1:0} Mbps", pair.Key, pair.Value))) + ")",
                        alone >= 1 ? ActivityLog.Info : ActivityLog.Error);
                return result;
            }
            finally { TestingSum = false; }
        }

        void ThrowIfRefused(SpeedResult[] results)
        {
            var refused = results.FirstOrDefault(result => result.Refused);
            if (refused == null) return;
            var error = "Teste da soma descartado: " + SpeedTest.RefusalText(refused.RefusedStatus) + ".";
            log.Add(error, ActivityLog.Error);
            throw new Exception(error);
        }
    }
}
