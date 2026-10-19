// Sockets UDP de saída compartilhados entre as associações. Um socket por link serve a vários destinos; outro só
// é aberto quando duas associações falam com o mesmo destino pelo mesmo link (as respostas são separadas pelo
// remetente). Com um socket por destino, um programa que fala com milhares de destinos ao ligar a captura
// esgotava as portas do Windows e derrubava a internet de todos os programas.
// As respostas só são aceitas do próprio destino (endereço e porta), como num NAT que restringe por porta.
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Links;

namespace MultiLink.Proxy
{
    class UdpPorts
    {
        public class Port
        {
            public Socket Socket;
            public Link Link;
            public readonly ConcurrentDictionary<string, Flow> Flows = new ConcurrentDictionary<string, Flow>(); // por destino
        }

        // um destino (endereço:porta) de uma associação
        public class Flow
        {
            public Port Port;
            public string Key;
            public UdpAssociation Owner;
            public long LastActivity;
        }

        readonly object sync = new object();
        readonly List<Port> open = new List<Port>();
        readonly UdpOversizeLog oversize;

        public UdpPorts(UdpOversizeLog oversize) { this.oversize = oversize; }

        // null se não deu para abrir um socket no link.
        public Flow Open(Link link, IPEndPoint target, UdpAssociation owner)
        {
            var key = target.ToString();
            lock (sync)
            {
                var port = open.FirstOrDefault(candidate => candidate.Link == link && candidate.Socket.AddressFamily == target.AddressFamily &&
                                                            !candidate.Flows.ContainsKey(key));
                bool created = port == null;
                if (created)
                {
                    port = Create(link, target.AddressFamily);
                    if (port == null) return null;
                    open.Add(port);
                }
                var flow = new Flow { Port = port, Key = key, Owner = owner, LastActivity = Stopwatch.GetTimestamp() };
                port.Flows[key] = flow;
                if (created) { var ignored = Receive(port); }
                return flow;
            }
        }

        // O socket fecha junto com o último fluxo dele. false se o fluxo já estava fechado.
        public bool Close(Flow flow)
        {
            lock (sync)
            {
                var port = flow.Port;
                Flow removed;
                if (!port.Flows.TryGetValue(flow.Key, out removed) || removed != flow) return false;
                port.Flows.TryRemove(flow.Key, out removed);
                if (port.Flows.IsEmpty)
                {
                    open.Remove(port);
                    port.Socket.CloseQuietly();
                }
                return true;
            }
        }

        static Port Create(Link link, AddressFamily family)
        {
            bool ipv6 = family == AddressFamily.InterNetworkV6;
            var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.IgnoreUdpConnectionReset();
                socket.Bind(new IPEndPoint(ipv6 ? link.AddressV6 : link.Address, 0));
                if (ipv6) socket.UseInterfaceV6(link.InterfaceIndexV6);
            }
            catch { socket.Close(); return null; }
            return new Port { Socket = socket, Link = link };
        }

        // Respostas dos destinos: cada uma vai para a associação que mandou para aquele remetente.
        async Task Receive(Port port)
        {
            var buffer = BufferPool.Datagram.Rent();
            var family = port.Socket.AddressFamily;
            try
            {
                while (true)
                {
                    EndPoint sender = new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    int length;
                    try
                    {
                        length = await Task.Factory.FromAsync((callback, state) => port.Socket.BeginReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref sender, callback, state),
                                                              result => port.Socket.EndReceiveFrom(result, ref sender), null);
                    }
                    catch (SocketException ex) // maior que o buffer: só ele se perde
                    {
                        if (ex.SocketErrorCode != SocketError.MessageSize) throw;
                        oversize.Dropped(true);
                        continue;
                    }
                    Flow flow;
                    if (!port.Flows.TryGetValue(sender.ToString(), out flow)) continue; // remetente que nenhuma associação procurou
                    Interlocked.Add(ref port.Link.Traffic.BytesDown, length);
                    Interlocked.Exchange(ref flow.LastActivity, Stopwatch.GetTimestamp());
                    flow.Owner.DeliverReply((IPEndPoint)sender, buffer, length);
                }
            }
            catch { } // socket fechado: o último fluxo dele terminou
            BufferPool.Datagram.Return(buffer);
        }
    }
}
