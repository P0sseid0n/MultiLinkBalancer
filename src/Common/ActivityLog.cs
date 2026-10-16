// Registro de eventos mostrado na interface (faixa do topo). Qualquer thread registra; a cada
// segundo o AppController recolhe as entradas novas e as envia para a interface.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MultiLink.Common
{
    public class ActivityLog
    {
        // tipos de evento: definem a cor do ponto na interface
        public const string Info = "info", Error = "error", LinkUp = "up", LinkDown = "down";

        const int MaxBacklog = 500;

        readonly ConcurrentQueue<Dictionary<string, object>> pending = new ConcurrentQueue<Dictionary<string, object>>();
        readonly List<Dictionary<string, object>> backlog = new List<Dictionary<string, object>>();
        long sequence;

        public void Add(string text) { Add(text, Info); }

        public void Add(string text, string kind)
        {
            pending.Enqueue(new Dictionary<string, object>
            {
                { "seq", Interlocked.Increment(ref sequence) }, { "time", DateTime.Now.ToString("HH:mm:ss") },
                { "kind", kind }, { "text", text }
            });
        }

        // Entradas registradas desde a última chamada (também guardadas no histórico).
        public List<Dictionary<string, object>> TakeNew()
        {
            var fresh = new List<Dictionary<string, object>>();
            Dictionary<string, object> entry;
            while (pending.TryDequeue(out entry)) fresh.Add(entry);
            if (fresh.Count > 0)
            {
                backlog.AddRange(fresh);
                if (backlog.Count > MaxBacklog) backlog.RemoveRange(0, backlog.Count - MaxBacklog);
            }
            return fresh;
        }

        // Histórico enviado quando a interface abre.
        public List<Dictionary<string, object>> Backlog() { return backlog.ToList(); }
    }
}
