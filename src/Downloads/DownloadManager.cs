// O download acelerado em andamento (um por vez) e o que a interface mostra dele: progresso,
// velocidade total e por link, e o resultado do último download.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Links;

namespace MultiLink.Downloads
{
    public class DownloadManager
    {
        // estados enviados à interface
        const string Probing = "probing", Running = "running", Completed = "done", Canceled = "canceled", Failed = "error";

        readonly ActivityLog log;
        SegmentedDownload current;
        CancellationTokenSource cancellation;
        string state, fileName, filePath, message;
        long totalBytes = -1, doneBytes, lastDoneBytes;
        double rate, averageMbps;
        readonly Dictionary<Link, long> lastBytesPerLink = new Dictionary<Link, long>();
        readonly Dictionary<Link, double> ratePerLink = new Dictionary<Link, double>();
        Dictionary<Link, long> finalBytesPerLink;

        public DownloadManager(ActivityLog log) { this.log = log; }

        // links: os links online e ativos. pickSavePath: pergunta onde salvar (null = cancelado).
        // notify: aviso do Windows (título, mensagem, é alerta?).
        public async Task Start(string url, List<Link> links, Func<string, string> pickSavePath, Action<string, string, bool> notify)
        {
            if (current != null) throw new Exception("Já existe um download em andamento.");
            Uri uri;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new Exception("Informe um link que comece com http:// ou https://");
            if (links.Count == 0) throw new Exception("Nenhum link online e ativo para baixar.");

            state = Probing; message = "Verificando o arquivo..."; fileName = null; filePath = null; finalBytesPerLink = null;
            DownloadProbe probe;
            try { probe = await SegmentedDownload.Probe(uri.ToString()); }
            catch (Exception ex) { state = null; throw new Exception("Não foi possível acessar o link: " + ex.Message); }

            var path = pickSavePath(SuggestedFileName(probe.FinalUri));
            if (path == null) { state = null; return; }

            current = new SegmentedDownload(log);
            cancellation = new CancellationTokenSource();
            fileName = Path.GetFileName(path); filePath = path;
            totalBytes = probe.Size; doneBytes = 0; lastDoneBytes = 0; rate = 0;
            lastBytesPerLink.Clear(); ratePerLink.Clear();
            state = Running; message = null;
            try { await Download(probe, path, links, notify); }
            finally { current = null; }
        }

        async Task Download(DownloadProbe probe, string path, List<Link> links, Action<string, string, bool> notify)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (probe.SupportsRanges)
                {
                    log.Add(string.Format("Download acelerado: {0} por {1} link(s)", fileName, links.Count));
                    await current.Run(probe.FinalUri, probe.Size, path, links, cancellation.Token);
                }
                else
                {
                    log.Add("O servidor não aceita download em partes; baixando por um único link.");
                    message = "O servidor não aceita download em partes: baixando por um único link.";
                    await current.RunSingle(probe.FinalUri, path, cancellation.Token);
                }
                doneBytes = current.Done;
                totalBytes = current.Total > 0 ? current.Total : current.Done;
                averageMbps = current.Done * 8 / Math.Max(0.1, stopwatch.Elapsed.TotalSeconds) / 1e6;
                finalBytesPerLink = current.BytesPerLink.ToDictionary(pair => pair.Key, pair => pair.Value);
                state = Completed;
                log.Add(string.Format("Download concluído: {0} em {1:0.0} s, média de {2:0} Mbps", fileName, stopwatch.Elapsed.TotalSeconds, averageMbps), ActivityLog.LinkUp);
                notify("Download concluído", string.Format("{0}, média de {1:0} Mbps", fileName, averageMbps), false);
            }
            catch (Exception ex)
            {
                bool canceled = cancellation.IsCancellationRequested;
                state = canceled ? Canceled : Failed;
                message = canceled ? "Download cancelado." : "O download falhou: " + ex.Message;
                log.Add(message, canceled ? ActivityLog.Info : ActivityLog.Error);
                if (!canceled) notify("Download falhou", message, true);
                try { File.Delete(path); } catch { }
            }
        }

        static string SuggestedFileName(Uri uri)
        {
            string name = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath));
            if (string.IsNullOrWhiteSpace(name)) name = "download.bin";
            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return name;
        }

        public void Cancel() { if (cancellation != null) cancellation.Cancel(); }

        // Mostra o arquivo baixado no Explorer.
        public void Reveal() { if (filePath != null && File.Exists(filePath)) Process.Start("explorer.exe", "/select,\"" + filePath + "\""); }

        // Some com o resultado do último download (não mexe num download em andamento).
        public void Clear() { if (current == null) state = null; }

        // Chamado a cada segundo.
        public void UpdateRates(double seconds)
        {
            var download = current;
            if (download == null) return;
            doneBytes = Interlocked.Read(ref download.Done);
            if (download.Total > 0) totalBytes = download.Total;
            rate = (doneBytes - lastDoneBytes) / seconds;
            lastDoneBytes = doneBytes;
            foreach (var pair in download.BytesPerLink)
            {
                long last;
                lastBytesPerLink.TryGetValue(pair.Key, out last);
                ratePerLink[pair.Key] = (pair.Value - last) / seconds;
                lastBytesPerLink[pair.Key] = pair.Value;
            }
        }

        // Estado para a interface; null se não há download para mostrar.
        public object ToState()
        {
            if (state == null) return null;
            var download = current;
            var bytesPerLink = download != null ? download.BytesPerLink.ToDictionary(pair => pair.Key, pair => pair.Value)
                                                : (finalBytesPerLink ?? new Dictionary<Link, long>());
            var perLink = new List<object>();
            foreach (var pair in bytesPerLink)
            {
                double linkRate;
                ratePerLink.TryGetValue(pair.Key, out linkRate);
                perLink.Add(new Dictionary<string, object>
                {
                    { "id", pair.Key.Id }, { "name", pair.Key.Name }, { "bytes", pair.Value }, { "rate", download != null ? Math.Round(linkRate) : 0 }
                });
            }
            return new Dictionary<string, object>
            {
                { "state", state }, { "name", fileName }, { "path", filePath }, { "message", message },
                { "total", totalBytes }, { "done", doneBytes }, { "rate", download != null ? Math.Round(rate) : 0 },
                { "avgMbps", Math.Round(averageMbps) }, { "perLink", perLink }
            };
        }
    }
}
