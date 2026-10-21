// Estado publicado para a interface a cada segundo e após cada comando.
// As chaves deste JSON são o contrato com a interface (ui/src) e com o simulador (ui/src/api/mock.js).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using MultiLink.Common;
using MultiLink.Links;
using MultiLink.Platform;

namespace MultiLink.App
{
    static class UiState
    {
        public static Dictionary<string, object> Build(AppController app)
        {
            var settings = app.Settings;
            var capture = app.Capture;
            return new Dictionary<string, object>
            {
                { "admin", app.IsAdmin },
                { "proxy", new Dictionary<string, object> {
                    { "running", app.Proxy.Running }, { "port", settings.Port }, { "lan", settings.AllowLan },
                    { "system", settings.UseSystemProxy }, { "systemApplied", settings.SystemProxyApplied } } },
                { "settings", new Dictionary<string, object> {
                    { "mode", (int)app.Balancer.Mode }, { "affinity", app.Balancer.Affinity }, { "quality", app.Balancer.QualityAware },
                    { "ipv6", (int)app.Balancer.Ipv6 }, { "ipv6Allowed", app.Balancer.Ipv6Allowed() },
                    { "autoRoute", app.RouteFailover.Enabled }, { "startup", app.StartupEnabled },
                    { "notify", settings.Notify }, { "notifySound", settings.NotifySound },
                    { "hotkeys", settings.Hotkeys }, { "autoUpdate", settings.AutoUpdate } } },
                { "capture", new Dictionary<string, object> {
                    { "on", settings.CaptureAllPrograms }, { "active", capture.Active }, { "starting", capture.Starting },
                    { "error", capture.Error }, { "ipv6", capture.Ipv6 } } },
                { "measuring", app.Links.Measuring },
                { "measurePausedUntil", app.Links.MeasurePausedUntil.HasValue ? (object)UnixTime.ToMs(app.Links.MeasurePausedUntil.Value) : null },
                { "sumTest", new Dictionary<string, object> {
                    { "running", app.Links.TestingSum }, { "result", app.Links.LastSumTest == null ? null : app.Links.LastSumTest.ToState() } } },
                { "update", app.Updater.ToState() },
                { "dev", DevBuild.Available ? new Dictionary<string, object> {
                    { "builtAt", UnixTime.ToMs(DevBuild.BuiltAt) }, { "building", DevBuild.Running } } : null },
                { "links", LinksToJson(app.Links.All()) },
                { "download", app.Downloads.ToState() }
            };
        }

        // Ordem estável por nome: a cor de cada link na interface segue a posição, então não pode mudar entre execuções.
        static List<object> LinksToJson(List<Link> links)
        {
            return links.OrderBy(link => link.Name, StringComparer.CurrentCultureIgnoreCase).Select(LinkToJson).ToList();
        }

        static object LinkToJson(Link link)
        {
            var health = LinkHealth.Of(link);
            var traffic = link.Traffic;
            return new Dictionary<string, object>
            {
                { "id", link.Id }, { "name", link.Name }, { "description", link.Description ?? "" }, { "wired", link.Wired },
                { "present", link.Present }, { "health", health }, { "healthText", LinkHealth.ShortText(health) },
                { "diagnosis", health == LinkHealth.Ok ? "" : LinkHealth.Describe(health) },
                { "enabled", link.Enabled }, { "weight", link.Weight },
                { "ip", link.Address == null || link.Address.Equals(IPAddress.None) ? "" : link.Address.ToString() },
                { "gateway", link.Gateway ?? "" },
                { "latency", link.LatencyMs }, { "measuredMbps", Math.Round(link.MeasuredMbps) },
                { "loss", link.Quality.LossPct }, { "jitter", link.Quality.JitterMs },
                { "linkSpeed", link.AdapterSpeedBps },
                { "rateDown", Math.Round(traffic.RateDown) }, { "rateUp", Math.Round(traffic.RateUp) },
                { "active", Thread.VolatileRead(ref traffic.ActiveConnections) }, { "total", Interlocked.Read(ref traffic.TotalConnections) },
                { "bytesDown", Interlocked.Read(ref traffic.BytesDown) }, { "bytesUp", Interlocked.Read(ref traffic.BytesUp) },
                { "metricRaised", link.MetricRaised }, { "userDisabled", link.UserDisabled },
                { "downSince", LinkHealth.IsDown(link.Health) ? (object)UnixTime.ToMs(link.DownSince) : null }
            };
        }
    }
}
