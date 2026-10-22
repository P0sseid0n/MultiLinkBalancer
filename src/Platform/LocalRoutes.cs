// Endereços do próprio PC e a rota que o Windows usaria para um destino: o proxy usa os dois para reconhecer o que
// não sai por um link (ver Proxy.DirectDestination).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace MultiLink.Platform
{
    public static class LocalRoutes
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MIB_IPFORWARDROW
        {
            public uint Dest, Mask, Policy, NextHop, IfIndex, Type, Proto, Age, NextHopAS, Metric1, Metric2, Metric3, Metric4, Metric5;
        }

        [DllImport("iphlpapi.dll")] static extern int GetBestRoute(uint destAddr, uint sourceAddr, out MIB_IPFORWARDROW bestRoute);

        class Snapshot
        {
            public HashSet<IPAddress> OwnAddresses = new HashSet<IPAddress>();
            public int CaptureIndex = -1; // índice IPv4 do adaptador da captura
        }

        static volatile Snapshot current; // null: refazer na próxima consulta

        static LocalRoutes() { NetworkChange.NetworkAddressChanged += (sender, e) => current = null; }

        // IP de um adaptador deste PC (inclusive dos que não são links e o da captura).
        public static bool IsOwnAddress(IPAddress address) { return Current().OwnAddresses.Contains(address); }

        // Interface do adaptador da captura: o que o Windows manda para ela volta para o proxy.
        public static bool IsCaptureInterface(int interfaceIndex) { return interfaceIndex == Current().CaptureIndex; }

        static Snapshot Current()
        {
            var snapshot = current;
            if (snapshot != null) return snapshot;
            snapshot = new Snapshot();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var properties = adapter.GetIPProperties();
                    snapshot.OwnAddresses.UnionWith(properties.UnicastAddresses.Select(unicast => unicast.Address));
                    if (adapter.Name == TrafficCapture.AdapterName && adapter.Supports(NetworkInterfaceComponent.IPv4))
                        snapshot.CaptureIndex = properties.GetIPv4Properties().Index;
                }
            }
            catch { }
            current = snapshot;
            return snapshot;
        }

        // Interface e tamanho do prefixo (ex.: 24 para /24, 0 na rota padrão) da rota IPv4 que o Windows usaria. false se não houver rota.
        public static bool BestRouteV4(IPAddress target, out int interfaceIndex, out int prefixLength)
        {
            interfaceIndex = 0; prefixLength = 0;
            MIB_IPFORWARDROW row;
            if (GetBestRoute(BitConverter.ToUInt32(target.GetAddressBytes(), 0), 0, out row) != 0) return false;
            interfaceIndex = (int)row.IfIndex;
            for (uint mask = row.Mask; mask != 0; mask &= mask - 1) prefixLength++;
            return true;
        }
    }
}
