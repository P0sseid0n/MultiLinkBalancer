using System.Threading.Tasks;

namespace MultiLink.Platform
{
    public static class NetworkAdapters
    {
        // Ativa ou desativa o adaptador no Windows (pede confirmação de administrador se preciso).
        public static Task<CommandResult> SetEnabled(string adapterName, bool enable)
        {
            return PowerShell.Run((enable ? "Enable" : "Disable") + "-NetAdapter -Name " + PowerShell.Quote(adapterName) + " -Confirm:$false", true);
        }
    }
}
