using System.Threading.Tasks;

namespace MultiLink.Platform
{
    // Métrica das interfaces no roteamento do Windows: com métrica alta, o sistema deixa de preferir a interface.
    public static class RouteMetrics
    {
        const int HighMetric = 9000;

        // Devolve, em caso de sucesso, a configuração original no formato "AutomaticMetric;InterfaceMetric".
        public static Task<CommandResult> Raise(string adapterName)
        {
            var alias = PowerShell.Quote(adapterName);
            return PowerShell.Run("$i = Get-NetIPInterface -InterfaceAlias " + alias + " -AddressFamily IPv4; " +
                                  "Write-Output ('{0};{1}' -f $i.AutomaticMetric, $i.InterfaceMetric); " +
                                  "Get-NetIPInterface -InterfaceAlias " + alias + " | Set-NetIPInterface -InterfaceMetric " + HighMetric, false);
        }

        // saved: o que Raise devolveu (null = volta à métrica automática).
        public static Task<CommandResult> Restore(string adapterName, string saved)
        {
            var parts = (saved ?? "Enabled;0").Split(';');
            var setting = parts[0] == "Enabled" || parts.Length < 2 ? "-AutomaticMetric Enabled" : "-InterfaceMetric " + parts[1];
            return PowerShell.Run("Get-NetIPInterface -InterfaceAlias " + PowerShell.Quote(adapterName) + " | Set-NetIPInterface " + setting, false);
        }
    }
}
