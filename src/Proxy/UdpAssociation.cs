// SOCKS5 UDP ASSOCIATE (RFC 1928, seção 7).
// O cliente manda datagramas com um cabeçalho SOCKS para o relé local. Cada destino novo (fluxo)
// sai por um link escolhido pelo Balancer, num socket compartilhado do link (UdpPorts); as respostas
// voltam com o cabeçalho do remetente. A associação dura enquanto a conexão TCP de controle estiver aberta.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Links;

namespace MultiLink.Proxy
{
    class UdpAssociation
    {
        const int IdleFlowSeconds = 120;
        const int IdleCheckMs = 15000;
        const byte AddressIPv4 = 1, AddressDomain = 3, AddressIPv6 = 4;

        readonly Balancer balancer;
        readonly UdpPorts ports;
        readonly UdpBurstLog bursts;
        readonly UdpOversizeLog oversize;
        readonly LocalCaptureLog localCapture; // só nas associações da captura
        readonly NetworkStream control;
        readonly IPAddress clientAddress;
        readonly IPAddress localAddress;
        readonly ConcurrentDictionary<string, UdpPorts.Flow> flows = new ConcurrentDictionary<string, UdpPorts.Flow>();
        Socket relay;
        EndPoint clientEndPoint; // de onde o cliente mandou o último datagrama: as respostas vão para lá

        public UdpAssociation(Balancer balancer, UdpPorts ports, UdpBurstLog bursts, UdpOversizeLog oversize,
                              LocalCaptureLog localCapture, NetworkStream control, Socket controlSocket)
        {
            this.balancer = balancer;
            this.ports = ports;
            this.bursts = bursts;
            this.oversize = oversize;
            this.localCapture = localCapture;
            this.control = control;
            clientAddress = ((IPEndPoint)controlSocket.RemoteEndPoint).Address;
            localAddress = ((IPEndPoint)controlSocket.LocalEndPoint).Address;
        }

        public async Task Run()
        {
            relay = new Socket(localAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            var buffer = BufferPool.Datagram.Rent();
            try
            {
                relay.IgnoreUdpConnectionReset();
                relay.Bind(new IPEndPoint(localAddress, 0));
                await ReplyRelayAddress();

                var closed = new TaskCompletionSource<bool>();
                var watchControl = WatchControlConnection(closed);
                var closeIdle = CloseIdleFlows(closed.Task);
                await ReceiveFromClient(buffer, closed.Task);
            }
            catch { }
            finally
            {
                relay.CloseQuietly();
                BufferPool.Datagram.Return(buffer); // depois de fechar o relé: a leitura pendente não escreve mais nele
                foreach (var flow in flows.Values) CloseFlow(flow);
            }
        }

        // Responde ao cliente para onde mandar os datagramas.
        Task ReplyRelayAddress()
        {
            var bound = (IPEndPoint)relay.LocalEndPoint;
            var address = bound.Address.MapToIPv4().GetAddressBytes();
            return control.WriteAllAsync(new byte[] { 5, 0, 0, AddressIPv4, address[0], address[1], address[2], address[3], (byte)(bound.Port >> 8), (byte)bound.Port });
        }

        // Fim da associação: o cliente fecha a conexão de controle.
        Task WatchControlConnection(TaskCompletionSource<bool> closed)
        {
            return Task.Run(async () =>
            {
                var discard = new byte[64];
                try { while (await control.ReadAsync(discard, 0, discard.Length) > 0) { } } catch { }
                closed.TrySetResult(true);
            });
        }

        Task CloseIdleFlows(Task closed)
        {
            return Task.Run(async () =>
            {
                while (!closed.IsCompleted)
                {
                    await Task.WhenAny(closed, Task.Delay(IdleCheckMs));
                    long cutoff = Stopwatch.GetTimestamp() - Stopwatch.Frequency * IdleFlowSeconds;
                    foreach (var idle in flows.Where(pair => Interlocked.Read(ref pair.Value.LastActivity) < cutoff).ToList())
                    {
                        UdpPorts.Flow removed;
                        if (flows.TryRemove(idle.Key, out removed)) CloseFlow(removed);
                    }
                }
            });
        }

        async Task ReceiveFromClient(byte[] buffer, Task closed)
        {
            while (!closed.IsCompleted)
            {
                EndPoint sender = AnyEndPoint(localAddress.AddressFamily);
                var receive = Task.Factory.FromAsync((callback, state) => relay.BeginReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref sender, callback, state),
                                                     result => relay.EndReceiveFrom(result, ref sender), null);
                if (await Task.WhenAny(receive, closed) != receive) { receive.ContinueWith(task => { var ignored = task.Exception; }); break; }
                int length;
                try { length = await receive; }
                catch (SocketException ex) { if (ex.SocketErrorCode == SocketError.MessageSize) oversize.Dropped(false); continue; }
                // só o dono da associação pode usar o relé
                var senderAddress = ((IPEndPoint)sender).Address;
                if (!senderAddress.Equals(clientAddress) && !(IPAddress.IsLoopback(senderAddress) && IPAddress.IsLoopback(clientAddress))) continue;
                clientEndPoint = sender;
                await ForwardToDestination(buffer, length);
            }
        }

        // Datagrama do cliente: RSV(2) FRAG(1) ATYP(1) DST.ADDR DST.PORT DADOS. Fragmentação não é suportada (FRAG != 0 é descartado).
        async Task ForwardToDestination(byte[] datagram, int length)
        {
            if (length < 10 || datagram[2] != 0) return;
            int offset;
            IPAddress destination;
            switch (datagram[3])
            {
                case AddressIPv4:
                    destination = new IPAddress(new[] { datagram[4], datagram[5], datagram[6], datagram[7] });
                    offset = 8;
                    break;
                case AddressIPv6:
                    if (length < 22) return;
                    var address = new byte[16];
                    Buffer.BlockCopy(datagram, 4, address, 0, 16);
                    destination = new IPAddress(address);
                    offset = 20;
                    break;
                case AddressDomain:
                    int nameLength = datagram[4];
                    if (length < 7 + nameLength) return;
                    var name = Encoding.ASCII.GetString(datagram, 5, nameLength);
                    offset = 5 + nameLength;
                    try { destination = (await Dns.GetHostAddressesAsync(name)).OrderBy(resolved => resolved.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First(); }
                    catch { return; }
                    break;
                default:
                    return;
            }
            int port = (datagram[offset] << 8) | datagram[offset + 1];
            offset += 2;

            var target = new IPEndPoint(destination, port);
            var flow = FlowFor(target);
            if (flow == null) return;
            Interlocked.Exchange(ref flow.LastActivity, Stopwatch.GetTimestamp());
            try { Interlocked.Add(ref flow.Port.Link.Traffic.BytesUp, flow.Port.Socket.SendTo(datagram, offset, length - offset, SocketFlags.None, target)); }
            catch { }
        }

        // Fluxo existente para o destino ou um novo, pelo link que o Balancer escolher. null se não houver link.
        UdpPorts.Flow FlowFor(IPEndPoint target)
        {
            var key = target.ToString();
            UdpPorts.Flow flow;
            if (flows.TryGetValue(key, out flow)) return flow;

            bool ipv6 = target.AddressFamily == AddressFamily.InterNetworkV6;
            if (ipv6 && !balancer.Ipv6Allowed()) return null; // descartado: sem resposta, o programa passa a usar o IPv4
            var exclude = new List<Link>();
            if (ipv6) exclude.AddRange(balancer.Snapshot().Where(link => !link.Ipv6.Works));
            var chosen = ipv6 ? null : balancer.Snapshot().FirstOrDefault(link => link.Present && link.OnLocalNetwork(target.Address)); // rede local: só esse link alcança
            if (chosen != null && localCapture != null) localCapture.ArrivedUdp(target, chosen);
            if (chosen == null) chosen = balancer.Pick(target.Address.ToString(), exclude);
            if (chosen == null) return null;

            flow = ports.Open(chosen, target, this);
            if (flow == null) return null;
            if (!flows.TryAdd(key, flow)) { ports.Close(flow); return flows[key]; } // outro datagrama criou o fluxo antes
            Interlocked.Increment(ref chosen.Traffic.ActiveConnections);
            Interlocked.Increment(ref chosen.Traffic.TotalConnections);
            bursts.NewFlow(target);
            return flow;
        }

        // Resposta de um destino (UdpPorts): devolve ao cliente com o cabeçalho SOCKS do remetente.
        public void DeliverReply(IPEndPoint sender, byte[] data, int length)
        {
            var client = clientEndPoint;
            if (client == null) return;
            try { relay.SendTo(WithSocksHeader(sender, data, length), client); }
            catch { } // associação encerrada
        }

        static byte[] WithSocksHeader(IPEndPoint sender, byte[] data, int length)
        {
            var address = sender.Address.GetAddressBytes();
            var packet = new byte[6 + address.Length + length];
            packet[3] = address.Length == 4 ? AddressIPv4 : AddressIPv6;
            Buffer.BlockCopy(address, 0, packet, 4, address.Length);
            packet[4 + address.Length] = (byte)(sender.Port >> 8);
            packet[5 + address.Length] = (byte)sender.Port;
            Buffer.BlockCopy(data, 0, packet, 6 + address.Length, length);
            return packet;
        }

        static EndPoint AnyEndPoint(AddressFamily family)
        {
            return new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        }

        void CloseFlow(UdpPorts.Flow flow)
        {
            if (ports.Close(flow)) Interlocked.Decrement(ref flow.Port.Link.Traffic.ActiveConnections);
        }
    }
}
