// Limite de conexões abertas no proxy, que não pede senha. Sem ele, um aparelho da rede (com "Aceitar outros aparelhos
// da rede") ou um programa abria conexões sem fim, cada uma com memória e uma tarefa, até o MultiLink travar.
// Os programas deste PC só têm o limite total: a captura leva todo o tráfego do PC pelo 127.0.0.1.
// Cada recusa aparece no registro uma vez a cada 10 min por aparelho.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using MultiLink.Common;

namespace MultiLink.Proxy
{
    class ClientLimits
    {
        const int MaxConnections = 16384;          // todas juntas
        const int MaxConnectionsPerLanClient = 256; // por aparelho da rede
        const int RepeatMinutes = 10;

        readonly ActivityLog log;
        readonly ConcurrentDictionary<IPAddress, int> perLanClient = new ConcurrentDictionary<IPAddress, int>();
        readonly ConcurrentDictionary<IPAddress, DateTime> lastLogged = new ConcurrentDictionary<IPAddress, DateTime>();
        int connections;

        public ClientLimits(ActivityLog log) { this.log = log; }

        // Conexão nova: false se passou do limite (a conexão deve ser fechada sem resposta). Quem recebe true chama Release.
        public bool Admit(IPAddress client, bool lanClient)
        {
            if (Interlocked.Increment(ref connections) > MaxConnections)
            {
                Interlocked.Decrement(ref connections);
                Refused(client, "o proxy já tem " + MaxConnections + " conexões abertas");
                return false;
            }
            if (lanClient && perLanClient.AddOrUpdate(client, 1, (key, count) => count + 1) > MaxConnectionsPerLanClient)
            {
                Release(client, true);
                Refused(client, "esse aparelho da rede já tem " + MaxConnectionsPerLanClient + " conexões abertas");
                return false;
            }
            return true;
        }

        public void Release(IPAddress client, bool lanClient)
        {
            Interlocked.Decrement(ref connections);
            if (!lanClient) return;
            int count = perLanClient.AddOrUpdate(client, 0, (key, current) => current - 1);
            // tira o aparelho do dicionário só se ninguém somou nesse meio-tempo
            if (count <= 0) ((ICollection<KeyValuePair<IPAddress, int>>)perLanClient).Remove(new KeyValuePair<IPAddress, int>(client, count));
        }

        void Refused(IPAddress client, string reason)
        {
            var now = DateTime.UtcNow;
            DateTime previous;
            if (lastLogged.TryGetValue(client, out previous) && now - previous < TimeSpan.FromMinutes(RepeatMinutes)) return;
            lastLogged[client] = now;
            log.Add("Conexão de " + client + " recusada: " + reason + ".", ActivityLog.Error);
        }
    }
}
