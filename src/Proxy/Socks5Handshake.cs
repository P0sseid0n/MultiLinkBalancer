// SOCKS5 (RFC 1928): CONNECT e UDP ASSOCIATE. Sem autenticação; o usuário e senha (RFC 1929) só servem para
// a captura se identificar (CaptureUser), sem verificar senha.
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
        public const string CaptureUser = "captura"; // usuário com que o tun2socks se conecta (TrafficCapture)

        const byte Version = 5;
        const byte NoAuthentication = 0, UserPassword = 2, NoAcceptableMethod = 0xFF;
        const byte UserPasswordVersion = 1;
        const byte CommandConnect = 1, CommandUdpAssociate = 3;
        const byte AddressIPv4 = 1, AddressDomain = 3, AddressIPv6 = 4;
        const byte ReplySucceeded = 0, ReplyHostUnreachable = 4, ReplyCommandNotSupported = 7;

        // Chamado depois de ler o primeiro byte (a versão). Devolve null se o pedido for inválido ou recusado.
        public static async Task<ProxyRequest> Read(NetworkStream client)
        {
            var user = await NegotiateAuthentication(client);
            if (user == null) return null;
            bool fromCapture = user == CaptureUser;

            // VER CMD RSV ATYP
            var header = new byte[4];
            if (!await client.ReadExactAsync(header, 4)) return null;
            byte command = header[1], addressType = header[3];
            var host = await ReadAddress(client, addressType);
            if (host == null) return null;
            var portBytes = new byte[2];
            if (!await client.ReadExactAsync(portBytes, 2)) return null;
            int port = (portBytes[0] << 8) | portBytes[1];

            if (command == CommandUdpAssociate) return new ProxyRequest { Host = host, Port = port, IsUdpAssociate = true, FromCapture = fromCapture };
            if (command != CommandConnect) { await client.WriteAllAsync(Reply(ReplyCommandNotSupported)); return null; }
            return new ProxyRequest
            {
                Host = host, Port = port, FromCapture = fromCapture,
                ReplySuccess = () => client.WriteAllAsync(Reply(ReplySucceeded)),
                ReplyFailure = () => client.WriteAllAsync(Reply(ReplyHostUnreachable))
            };
        }

        // Devolve o usuário informado ("" sem autenticação) ou null se a negociação falhou.
        static async Task<string> NegotiateAuthentication(NetworkStream client)
        {
            var count = new byte[1];
            if (!await client.ReadExactAsync(count, 1)) return null;
            var methods = new byte[count[0]];
            if (!await client.ReadExactAsync(methods, methods.Length)) return null;
            if (methods.Contains(UserPassword)) { await client.WriteAllAsync(new byte[] { Version, UserPassword }); return await ReadUserPassword(client); }
            if (!methods.Contains(NoAuthentication)) { await client.WriteAllAsync(new byte[] { Version, NoAcceptableMethod }); return null; }
            await client.WriteAllAsync(new byte[] { Version, NoAuthentication });
            return "";
        }

        // VER ULEN USUÁRIO PLEN SENHA: qualquer senha é aceita.
        static async Task<string> ReadUserPassword(NetworkStream client)
        {
            var header = new byte[2];
            if (!await client.ReadExactAsync(header, 2)) return null;
            var user = new byte[header[1]];
            if (!await client.ReadExactAsync(user, user.Length)) return null;
            var passwordLength = new byte[1];
            if (!await client.ReadExactAsync(passwordLength, 1)) return null;
            if (!await client.ReadExactAsync(new byte[passwordLength[0]], passwordLength[0])) return null;
            await client.WriteAllAsync(new byte[] { UserPasswordVersion, 0 });
            return Encoding.ASCII.GetString(user);
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
