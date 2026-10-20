// Registro de eventos mostrado na interface (faixa do topo). Qualquer thread registra; a cada
// segundo o AppController recolhe as entradas novas e as envia para a interface.
// Também vai para um arquivo (com data e hora), para investigar o que aconteceu depois de o app fechar
// ou antes de a interface abrir, como a partida junto com o Windows.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MultiLink.Common
{
    public class ActivityLog
    {
        // tipos de evento: definem a cor do ponto na interface
        public const string Info = "info", Error = "error", LinkUp = "up", LinkDown = "down";

        const int MaxBacklog = 500;
        const long MaxFileBytes = 1 << 20;

        readonly ConcurrentQueue<Dictionary<string, object>> pending = new ConcurrentQueue<Dictionary<string, object>>();
        readonly ConcurrentQueue<string> pendingLines = new ConcurrentQueue<string>();
        readonly List<Dictionary<string, object>> backlog = new List<Dictionary<string, object>>();
        readonly string file;
        long sequence;

        // file: null = só na memória
        public ActivityLog(string file) { this.file = file; }

        public void Add(string text) { Add(text, Info); }

        public void Add(string text, string kind)
        {
            var now = DateTime.Now;
            pending.Enqueue(new Dictionary<string, object>
            {
                { "seq", Interlocked.Increment(ref sequence) }, { "time", now.ToString("HH:mm:ss") },
                { "kind", kind }, { "text", text }
            });
            if (file != null) pendingLines.Enqueue(now.ToString("yyyy-MM-dd HH:mm:ss.fff") + (kind == Error ? " [erro] " : " ") + text);
        }

        // Entradas registradas desde a última chamada (também guardadas no histórico e no arquivo).
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
            Flush();
            return fresh;
        }

        // Grava no arquivo o que ainda não foi gravado. Também chamado ao sair.
        public void Flush()
        {
            if (file == null || pendingLines.IsEmpty) return;
            var text = new StringBuilder();
            string line;
            while (pendingLines.TryDequeue(out line)) text.AppendLine(line);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                if (File.Exists(file) && new FileInfo(file).Length > MaxFileBytes) { File.Copy(file, file + ".old", true); File.Delete(file); }
                File.AppendAllText(file, text.ToString());
            }
            catch { }
        }

        // Histórico enviado quando a interface abre.
        public List<Dictionary<string, object>> Backlog() { return backlog.ToList(); }
    }
}
