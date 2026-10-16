// SOCKS5 (RFC 1928) sem autenticação: CONNECT e UDP ASSOCIATE.
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MultiLink.Common;

namespace MultiLink.Proxy
{
    static class Socks5Handshake
    {
        const byte Version = 5;
        const byte NoAuthentication = 0, NoAcceptableMethod = 0xFF;
        const byte CommandConnect = 1, CommandUdpAssociate = 3;
        const byte AddressIPv4 = 1, AddressDomain = 3, AddressIPv6 = 4;
        const byte ReplySucceeded = 0, ReplyHostUnreachable = 4, ReplyCommandNotSupported = 7;

        // Chamado depois de ler o primeiro byte (a versão). Devolve null se o pedido for inválido ou recusado.
        public static async Task<ProxyRequest> Read(NetworkStream client)
        {
            if (!await NegotiateAuthentication(client)) return null;

            // VER CMD RSV ATYP
            var header = new byte[4];
            if (!await client.ReadExactAsync(header, 4)) return null;
            byte command = header[1], addressType = header[3];
            var host = await ReadAddress(client, addressType);
            if (host == null) return null;
            var portBytes = new byte[2];
            if (!await client.ReadExactAsync(portBytes, 2)) return null;
            int port = (portBytes[0] << 8) | portBytes[1];

            if (command == CommandUdpAssociate) return new ProxyRequest { Host = host, Port = port, IsUdpAssociate = true };
            if (command != CommandConnect) { await client.WriteAllAsync(Reply(ReplyCommandNotSupported)); return null; }
            return new ProxyRequest
            {
                Host = host, Port = port,
                ReplySuccess = () => client.WriteAllAsync(Reply(ReplySucceeded)),
                ReplyFailure = () => client.WriteAllAsync(Reply(ReplyHostUnreachable))
            };
        }

        static async Task<bool> NegotiateAuthentication(NetworkStream client)
        {
            var count = new byte[1];
            if (!await client.ReadExactAsync(count, 1)) return false;
            var methods = new byte[count[0]];
            if (!await client.ReadExactAsync(methods, methods.Length)) return false;
            if (!methods.Contains(NoAuthentication)) { await client.WriteAllAsync(new byte[] { Version, NoAcceptableMethod }); return false; }
            await client.WriteAllAsync(new byte[] { Version, NoAuthentication });
            return true;
        }

        static async Task<string> ReadAddress(NetworkStream client, byte addressType)
        {
            switch (addressType)
            {
                case AddressIPv4:
                case AddressIPv6:
                    var address = new byte[addressType == AddressIPv4 ? 4 : 16];
                    if (!await client.ReadExactAsync(address, address.Length)) return null;
                    return new IPAddress(address).ToString();
                case AddressDomain:
                    var length = new byte[1];
                    if (!await client.ReadExactAsync(length, 1)) return null;
                    var domain = new byte[length[0]];
                    if (!await client.ReadExactAsync(domain, domain.Length)) return null;
                    return Encoding.ASCII.GetString(domain);
                default:
                    return null;
            }
        }

        // Resposta com endereço vinculado 0.0.0.0:0 (o cliente não precisa dele no CONNECT).
        static byte[] Reply(byte status) { return new byte[] { Version, status, 0, AddressIPv4, 0, 0, 0, 0, 0, 0 }; }
    }
}
