// SOCKS4 e SOCKS4a (nome do destino no lugar do IP), só CONNECT.
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MultiLink.Common;

namespace MultiLink.Proxy
{
    static class Socks4Handshake
    {
        const byte CommandConnect = 1;
        const byte Granted = 0x5A, Rejected = 0x5B;
        const int MaxFieldLength = 512;

        // Chamado depois de ler o primeiro byte (a versão). Devolve null se o pedido for inválido ou recusado.
        public static async Task<ProxyRequest> Read(NetworkStream client)
        {
            // CMD(1) PORTA(2) IP(4)
            var header = new byte[7];
            if (!await client.ReadExactAsync(header, 7)) return null;
            int port = (header[1] << 8) | header[2];
            await client.ReadNullTerminatedAsync(MaxFieldLength); // identificação do usuário, ignorada
            // SOCKS4a: IP 0.0.0.x (x != 0) indica que o nome do destino vem em seguida
            bool hasDomain = header[3] == 0 && header[4] == 0 && header[5] == 0 && header[6] != 0;
            string host = hasDomain ? await client.ReadNullTerminatedAsync(MaxFieldLength)
                                    : new IPAddress(new[] { header[3], header[4], header[5], header[6] }).ToString();
            if (host == null) return null;

            var rejected = new byte[] { 0, Rejected, 0, 0, 0, 0, 0, 0 };
            if (header[0] != CommandConnect) { await client.WriteAllAsync(rejected); return null; }
            return new ProxyRequest
            {
                Host = host, Port = port,
                ReplySuccess = () => client.WriteAllAsync(new byte[] { 0, Granted, header[1], header[2], header[3], header[4], header[5], header[6] }),
                ReplyFailure = () => client.WriteAllAsync(rejected)
            };
        }
    }
}
