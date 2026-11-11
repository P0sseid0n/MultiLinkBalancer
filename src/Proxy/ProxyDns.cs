// Consultas DNS dos pedidos ao proxy. No .NET Framework cada consulta ocupa uma thread do pool até responder, e o nome
// vem de quem usa o proxy: nomes que demoram a responder, pedidos aos milhares, esgotariam o pool que todo o app usa
// (o proxy inteiro, a interface, os testes dos links). No máximo MaxConcurrent ao mesmo tempo; as outras esperam a vez.
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MultiLink.Proxy
{
    static class ProxyDns
    {
        const int MaxConcurrent = 32; // abaixo das 64 threads mínimas do pool (Program.cs)

        static readonly SemaphoreSlim slots = new SemaphoreSlim(MaxConcurrent);

        public static async Task<IPAddress[]> Resolve(string host)
        {
            await slots.WaitAsync();
            try { return await Dns.GetHostAddressesAsync(host); }
            finally { slots.Release(); }
        }
    }
}
