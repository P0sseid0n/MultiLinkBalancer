using System;
using System.Threading.Tasks;

namespace MultiLink.Proxy
{
    // O que o cliente pediu ao proxy, já lido do protocolo (SOCKS4, SOCKS5 ou HTTP).
    class ProxyRequest
    {
        public string Host;
        public int Port;
        public byte[] InitialData;            // bytes a mandar ao destino logo após conectar (HTTP)
        public Func<Task> ReplySuccess;       // responde ao cliente que a conexão foi aberta
        public Func<Task> ReplyFailure;
        public bool IsUdpAssociate;           // SOCKS5 UDP ASSOCIATE: a conexão TCP só mantém a associação viva
        public bool FromCapture;              // veio do tun2socks (Socks5Handshake.CaptureUser), não de um programa configurado

        public static readonly Func<Task> NoReply = () => Task.FromResult(0);
    }
}
