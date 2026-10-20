using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MultiLink.Platform
{
    // Métrica das interfaces no roteamento do Windows: com métrica alta, o sistema deixa de preferir a interface.
    // Só no estado ativo (ActiveStore): gravada também no registro, a métrica sobrevivia ao "devolver" e o cliente DHCP
    // passava a usá-la na rota padrão a cada renovação (rota com métrica 9000 mesmo com o link saudável).
    public static class RouteMetrics
    {
        const int HighMetric = 9000;

        // Devolve, em caso de sucesso, a configuração original no formato "AutomaticMetric;InterfaceMetric".
        public static Task<CommandResult> Raise(string adapterName)
        {
            var alias = PowerShell.Quote(adapterName);
            return PowerShell.Run("$i = Get-NetIPInterface -InterfaceAlias " + alias + " -AddressFamily IPv4 -PolicyStore ActiveStore; " +
                                  "Write-Output ('{0};{1}' -f $i.AutomaticMetric, $i.InterfaceMetric); " +
                                  "Get-NetIPInterface -InterfaceAlias " + alias + " -PolicyStore ActiveStore | Set-NetIPInterface -PolicyStore ActiveStore -InterfaceMetric " + HighMetric, false);
        }

        // saved: o que Raise devolveu (null = volta à métrica automática).
        public static Task<CommandResult> Restore(string adapterName, string saved)
        {
            var parts = (saved ?? "Enabled;0").Split(';');
            var setting = parts[0] == "Enabled" || parts.Length < 2 ? "-AutomaticMetric Enabled" : "-InterfaceMetric " + parts[1];
            return PowerShell.Run("Get-NetIPInterface -InterfaceAlias " + PowerShell.Quote(adapterName) + " -PolicyStore ActiveStore | Set-NetIPInterface -PolicyStore ActiveStore " + setting, false);
        }

        // Limpa o que versões antigas deixaram gravado: a métrica 9000 no registro (que o DHCP usa na rota padrão) e
        // a rota padrão que já ficou com 9000. Devolve os nomes dos adaptadores corrigidos, um por linha.
        // adapters: id do adaptador ("{guid}") -> nome.
        public static Task<CommandResult> RepairLeftovers(IDictionary<string, string> adapters)
        {
            var script = string.Join(" ", adapters.Select(adapter =>
                "$fixed = $false; " +
                "$key = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\" + adapter.Key.Replace("'", "") + "'; " +
                "if ((Get-ItemProperty $key -Name InterfaceMetric -ErrorAction SilentlyContinue).InterfaceMetric -eq " + HighMetric + ") { Remove-ItemProperty $key -Name InterfaceMetric; $fixed = $true }; " +
                "Get-NetRoute -InterfaceAlias " + PowerShell.Quote(adapter.Value) + " -DestinationPrefix 0.0.0.0/0 -PolicyStore ActiveStore -ErrorAction SilentlyContinue | " +
                "Where-Object RouteMetric -eq " + HighMetric + " | ForEach-Object { $_ | Set-NetRoute -RouteMetric 0; $fixed = $true }; " +
                "if ($fixed) { Write-Output " + PowerShell.Quote(adapter.Value) + " };"));
            return PowerShell.Run(script, false);
        }
    }
}
