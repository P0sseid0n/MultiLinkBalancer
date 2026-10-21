// Recusa o IPv6 dos programas capturados já no início da conexão, pelo Firewall do Windows.
// Recusar no proxy não basta: o tun2socks completa a conexão TCP antes de pedir o destino ao proxy, então o programa vê
// uma conexão aberta que cai, e não tenta o IPv4 (o curl, por exemplo, falha no TLS). Bloqueada no firewall, a conexão
// falha na hora e o programa usa o IPv4. A regra vale só para a saída pelo adaptador da captura: as conexões do próprio
// MultiLink saem pela placa de cada link e não são afetadas (o teste de IPv6 dos links continua). No UDP o firewall
// descarta os pacotes, como o proxy já fazia.
// Com o adaptador fora do ar a regra não faz nada, mas é removida ao desligar a captura e, depois de uma queda, na abertura.
using System;
using System.Threading.Tasks;
using MultiLink.Common;

namespace MultiLink.Platform
{
    public class Ipv6Block
    {
        const string RuleName = "MultiLinkBalancer-Ipv6";

        readonly ActivityLog log;
        bool? blocked; // null: não se sabe se a regra existe (início, ou o último comando falhou)
        bool busy;
        DateTime retryAt; // depois de uma falha, espera antes de tentar de novo (sem encher o registro)

        public Ipv6Block(ActivityLog log) { this.log = log; }

        public bool Blocked { get { return blocked == true; } }

        // block: a regra deve existir. Não repete o comando se nada mudou; uma falha é avisada e tentada de novo 1 min depois.
        public async Task Set(bool block, string adapterName)
        {
            if (busy || blocked == block || DateTime.UtcNow < retryAt) return;
            busy = true;
            try
            {
                // sem a regra, o Get falha (mesmo calado) e o PowerShell sairia com código 1: o "exit 0" do fim resolve, e
                // um erro de verdade (com $ErrorActionPreference = Stop) interrompe o script antes dele
                var script = "Get-NetFirewallRule -Name " + RuleName + " -ErrorAction SilentlyContinue | Remove-NetFirewallRule; " +
                    (block ? "New-NetFirewallRule -Name " + RuleName + " -DisplayName 'MultiLink Balancer: IPv6 recusado na captura' " +
                             "-Direction Outbound -Action Block -InterfaceAlias " + PowerShell.Quote(adapterName) +
                             " -RemoteAddress 2000::/3 | Out-Null; " : "") + "exit 0";
                var result = await PowerShell.Run(script, false);
                if (result.Ok) { blocked = block; return; }
                blocked = null;
                retryAt = DateTime.UtcNow.AddMinutes(1);
                log.Add((block ? "Não foi possível recusar o IPv6 no firewall: " : "Não foi possível tirar a regra de IPv6 do firewall: ") + result.Output, ActivityLog.Error);
            }
            finally { busy = false; }
        }
    }
}
