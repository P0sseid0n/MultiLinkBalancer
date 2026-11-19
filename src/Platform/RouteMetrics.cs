using System;
using System.Collections.Generic;
using System.Globalization;
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
        // Se a interface já está com a métrica alta, ela não é a original (é a do próprio MultiLink, que o Windows pode
        // regravar depois de uma devolução: o HNS do Hyper-V e o ICS regravam a interface inteira, com o que leram antes).
        // Gravada como original, cada devolução seguinte a punha de volta, e o link nunca mais voltava ao roteamento.
        public static Task<CommandResult> Raise(string adapterName)
        {
            var alias = PowerShell.Quote(adapterName);
            return PowerShell.Run("$i = Get-NetIPInterface -InterfaceAlias " + alias + " -AddressFamily IPv4 -PolicyStore ActiveStore; " +
                                  "if ($i.InterfaceMetric -eq " + HighMetric + ") { Write-Output 'Enabled;0' } " +
                                  "else { Write-Output ('{0};{1}' -f $i.AutomaticMetric, $i.InterfaceMetric) }; " +
                                  "Get-NetIPInterface -InterfaceAlias " + alias + " -PolicyStore ActiveStore | Set-NetIPInterface -PolicyStore ActiveStore -InterfaceMetric " + HighMetric, false);
        }

        const string StuckPrefix = "presa:";

        // saved: o que Raise devolveu (null = volta à métrica automática).
        // Confere depois de aplicar (IPv4 e IPv6): o Set-NetIPInterface dá certo e, às vezes, a interface continua com a
        // métrica alta (regravada pelo Windows logo depois). Tenta mais uma vez e, se ainda não pegou, devolve falha.
        public static async Task<CommandResult> Restore(string adapterName, string saved)
        {
            // saved vem do config.ini, que qualquer programa do usuário pode alterar: a métrica só entra no script como
            // número; qualquer outra coisa volta à métrica automática. A métrica alta também: é a do MultiLink, nunca a
            // original (gravada assim por versões anteriores).
            var parts = (saved ?? "Enabled;0").Split(';');
            int metric = 0;
            bool manual = parts[0] != "Enabled" && parts.Length >= 2 &&
                          int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out metric) && metric >= 1 && metric <= 9999 &&
                          metric != HighMetric;
            var alias = PowerShell.Quote(adapterName);
            var setting = manual ? "-InterfaceMetric " + metric : "-AutomaticMetric Enabled";
            var wrong = manual ? "$_.InterfaceMetric -ne " + metric
                               : "$_.AutomaticMetric -ne 'Enabled' -or $_.InterfaceMetric -eq " + HighMetric;
            var script =
                "function Set-Original { Get-NetIPInterface -InterfaceAlias " + alias + " -PolicyStore ActiveStore | Set-NetIPInterface -PolicyStore ActiveStore " + setting + " }; " +
                "function Get-Stuck { @(Get-NetIPInterface -InterfaceAlias " + alias + " -PolicyStore ActiveStore | Where-Object { " + wrong + " }) }; " +
                "Set-Original; $stuck = Get-Stuck; " +
                "if ($stuck.Count -gt 0) { Start-Sleep -Milliseconds 1500; Set-Original; $stuck = Get-Stuck }; " +
                "if ($stuck.Count -gt 0) { Write-Output ('" + StuckPrefix + "' + (($stuck | ForEach-Object { '{0}={1}' -f $_.AddressFamily, $_.InterfaceMetric }) -join ', ')) }";
            // sem voltar à thread da janela: RestoreAll espera este Task nela (com Wait), e a continuação travaria
            var result = await PowerShell.Run(script, false).ConfigureAwait(false);
            if (result.Ok && result.Output.StartsWith(StuckPrefix, StringComparison.Ordinal))
                return CommandResult.Failure("o Windows aceitou, mas a interface continuou com a métrica alta (" +
                                             result.Output.Substring(StuckPrefix.Length).Replace("=", ": ") + ")");
            return result;
        }

        // Limpa o que versões antigas deixaram gravado: a métrica 9000 no registro (que o DHCP usa na rota padrão) e
        // a rota padrão que já ficou com 9000. Devolve os nomes dos adaptadores corrigidos, um por linha.
        // adapters: id do adaptador ("{guid}") -> nome.
        // notRaised: nomes dos adaptadores que o MultiLink não retirou (sem devolução pendente). Se um deles está com a
        // métrica alta no estado ativo, ela sobrou de uma sessão anterior (devolução que não pegou, ou uma versão que
        // gravava a métrica alta como original) e volta à automática; o nome sai com o prefixo ActiveLeftover.
        public const string ActiveLeftover = "ativa:";

        public static Task<CommandResult> RepairLeftovers(IDictionary<string, string> adapters, ICollection<string> notRaised)
        {
            var script = string.Join(" ", adapters.Select(adapter =>
                "$fixed = $false; " +
                "$key = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\" + adapter.Key.Replace("'", "") + "'; " +
                "if ((Get-ItemProperty $key -Name InterfaceMetric -ErrorAction SilentlyContinue).InterfaceMetric -eq " + HighMetric + ") { Remove-ItemProperty $key -Name InterfaceMetric; $fixed = $true }; " +
                "Get-NetRoute -InterfaceAlias " + PowerShell.Quote(adapter.Value) + " -DestinationPrefix 0.0.0.0/0 -PolicyStore ActiveStore -ErrorAction SilentlyContinue | " +
                "Where-Object RouteMetric -eq " + HighMetric + " | ForEach-Object { $_ | Set-NetRoute -RouteMetric 0; $fixed = $true }; " +
                "if ($fixed) { Write-Output " + PowerShell.Quote(adapter.Value) + " };" +
                (notRaised.Contains(adapter.Value)
                    ? " $high = @(Get-NetIPInterface -InterfaceAlias " + PowerShell.Quote(adapter.Value) + " -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object InterfaceMetric -eq " + HighMetric + "); " +
                      "if ($high.Count -gt 0) { $high | Set-NetIPInterface -PolicyStore ActiveStore -AutomaticMetric Enabled; Write-Output (" + PowerShell.Quote(ActiveLeftover) + " + " + PowerShell.Quote(adapter.Value) + ") };"
                    : "")));
            return PowerShell.Run(script, false);
        }
    }
}
