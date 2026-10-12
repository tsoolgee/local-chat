using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LocalChat
{
    /// Serves the UI and a small JSON API on localhost. Chat completions go straight to llama-server.
    class WebServer
    {
        HttpListener _http;
        public int Port { get; private set; }
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public void Start()
        {
            for (int port = 47800; port < 47900; port++)
            {
                var l = new HttpListener();
                l.Prefixes.Add("http://localhost:" + port + "/");
                try { l.Start(); _http = l; Port = port; break; }
                catch { try { l.Close(); } catch { } }
            }
            if (_http == null) throw new Exception("לא נמצא פורט פנוי לשרת המקומי.");
            new Thread(Loop) { IsBackground = true, Name = "http" }.Start();
        }

        public void Stop() { try { _http?.Stop(); } catch { } }

        void Loop()
        {
            while (_http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _http.GetContext(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            try
            {
                // Only our own UI may call the API (blocks other web pages from poking at it).
                var origin = req.Headers["Origin"];
                if (origin != null && origin != "http://localhost:" + Port) { res.StatusCode = 403; res.Close(); return; }

                var path = Uri.UnescapeDataString(req.Url.AbsolutePath);
                if (path.StartsWith("/api/")) Api(ctx, path.Substring(5), req.HttpMethod);
                else Static(ctx, path);
            }
            catch (Exception ex)
            {
                try { SendJson(res, new Dictionary<string, object> { ["error"] = ex.Message }, 500); } catch { }
            }
        }

        void Api(HttpListenerContext ctx, string route, string method)
        {
            var res = ctx.Response;
            var e = Program.Engine;
            var store = Program.Store;

            var img = Program.ImageEngine;
            if (route == "state")
            {
                var st = e.Info();
                st["ram"] = Memory.Info();
                st["cpus"] = Environment.ProcessorCount;
                st["image"] = img.Info();
                SendJson(res, st);
                return;
            }
            if (route == "image-models") { SendJson(res, Engine.ListImageModels()); return; }
            if (route == "extract-docx" && method == "POST")
            {
                var ms = new MemoryStream();
                ctx.Request.InputStream.CopyTo(ms);
                ms.Position = 0;
                SendJson(res, new Dictionary<string, object> { ["text"] = Documents.DocxText(ms) });
                return;
            }
            if (route == "update") { SendText(res, Downloader.UpdateInfo(), "application/json; charset=utf-8"); return; }
            if (route == "catalog") { SendText(res, Downloader.CatalogWithStatus(), "application/json; charset=utf-8"); return; }
            if (route == "downloads") { SendText(res, Downloader.Status(), "application/json; charset=utf-8"); return; }
            if (route.StartsWith("download") || route == "model-delete")
            {
                if (method != "POST") { res.StatusCode = 405; res.Close(); return; }
                var d = Store.Json.Deserialize<Dictionary<string, object>>(Body(ctx.Request));
                d.TryGetValue("id", out var idObj);
                var id = idObj as string;
                string jobId = id;
                if (route == "download")
                {
                    if (d.TryGetValue("url", out var u) && u is string url && url.Length > 0)
                        jobId = Downloader.StartUrl(url, d.TryGetValue("kind", out var k) ? k as string : "chat");
                    else jobId = Downloader.StartCatalog(id, d.TryGetValue("vision", out var v) && v is bool b && b);
                }
                else if (route == "download-cancel") Downloader.Cancel(id);
                else if (route == "download-forget") Downloader.Forget(id);
                else if (route == "download-resume") jobId = Downloader.Resume(id);
                else if (route == "download-discard") Downloader.Discard(id);
                else if (route == "model-delete") Downloader.DeleteCatalog(id);
                else { res.StatusCode = 404; res.Close(); return; }
                SendJson(res, new Dictionary<string, object> { ["ok"] = true, ["id"] = jobId });
                return;
            }
            if (route == "image-load" && method == "POST")
            {
                var d = Store.Json.Deserialize<Dictionary<string, object>>(Body(ctx.Request));
                var file = d["file"] as string;
                int t = Engine.GetInt(d, "threads", 0);
                img.Load(file, 0, t);
                store.MergeSettings(new Dictionary<string, object> { ["lastImageModel"] = file });
                SendJson(res, new Dictionary<string, object> { ["ok"] = true });
                return;
            }
            if (route == "image-unload" && method == "POST")
            {
                img.Stop();
                store.MergeSettings(new Dictionary<string, object> { ["lastImageModel"] = null });
                SendJson(res, new Dictionary<string, object> { ["ok"] = true });
                return;
            }
            if (route == "image-log") { SendText(res, img.LogText(), "text/plain; charset=utf-8"); return; }
            if (route == "img/generate" && method == "POST")
            {
                if (img.State != "ready") throw new Exception("מודל התמונות לא טעון.");
                var (code, text) = Forward("POST", img.Port, "/sdcpp/v1/img_gen", Body(ctx.Request));
                res.StatusCode = code;
                SendText(res, text, "application/json; charset=utf-8");
                return;
            }
            if (route.StartsWith("img/job/"))
            {
                var id = route.Substring(8);
                if (!Store.ValidId(id)) { res.StatusCode = 400; res.Close(); return; }
                SendText(res, Images.Job(img.Port, id), "application/json; charset=utf-8");
                return;
            }
            if (route.StartsWith("img/cancel/") && method == "POST")
            {
                var id = route.Substring(11);
                if (!Store.ValidId(id)) { res.StatusCode = 400; res.Close(); return; }
                try { Forward("POST", img.Port, "/sdcpp/v1/jobs/" + id + "/cancel", "{}"); } catch { }
                SendJson(res, new Dictionary<string, object> { ["ok"] = true });
                return;
            }
            if (route.StartsWith("image-file/"))
            {
                var name = route.Substring(11);
                var f = Images.FilePath(name);
                if (f == null) { res.StatusCode = 404; res.Close(); return; }
                res.ContentType = name.EndsWith(".png") ? "image/png" : name.EndsWith(".webp") ? "image/webp" : "image/jpeg";
                res.Headers["Cache-Control"] = "max-age=31536000, immutable";
                var bytes = File.ReadAllBytes(f);
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
                return;
            }
            if (route == "models") { SendJson(res, Engine.ListModels()); return; }
            if (route == "load" && method == "POST")
            {
                var d = Store.Json.Deserialize<Dictionary<string, object>>(Body(ctx.Request));
                var file = d["file"] as string;
                int c = Engine.GetInt(d, "ctx", 8192), t = Engine.GetInt(d, "threads", 0);
                e.Load(file, c, t);
                store.MergeSettings(new Dictionary<string, object> { ["lastModel"] = file, ["ctx"] = c, ["threads"] = t });
                SendJson(res, new Dictionary<string, object> { ["ok"] = true });
                return;
            }
            if (route == "unload" && method == "POST") { e.Stop(); SendJson(res, new Dictionary<string, object> { ["ok"] = true }); return; }
            if (route == "log") { SendText(res, e.LogText(), "text/plain; charset=utf-8"); return; }
            if (route == "open-models" && method == "POST")
            {
                Process.Start("explorer.exe", "\"" + Program.ModelsDir + "\"");
                SendJson(res, new Dictionary<string, object> { ["ok"] = true });
                return;
            }
            if (route == "settings")
            {
                if (method == "PUT")
                {
                    store.MergeSettings(Store.Json.Deserialize<Dictionary<string, object>>(Body(ctx.Request)));
                    SendJson(res, new Dictionary<string, object> { ["ok"] = true });
                }
                else SendText(res, store.ReadSettings(), "application/json; charset=utf-8");
                return;
            }
            if (route == "search") { SendText(res, store.Search(QueryParam(ctx.Request.Url, "q")), "application/json; charset=utf-8"); return; }
            if (route == "chats") { SendText(res, store.ListChats(), "application/json; charset=utf-8"); return; }
            if (route.StartsWith("chats/"))
            {
                var id = route.Substring(6);
                if (!Store.ValidId(id)) { res.StatusCode = 400; res.Close(); return; }
                if (method == "PUT") { store.SaveChat(id, Body(ctx.Request)); SendJson(res, new Dictionary<string, object> { ["ok"] = true }); }
                else if (method == "DELETE") { store.DeleteChat(id); SendJson(res, new Dictionary<string, object> { ["ok"] = true }); }
                else
                {
                    var json = store.ReadChat(id);
                    if (json == null) { res.StatusCode = 404; res.Close(); }
                    else SendText(res, json, "application/json; charset=utf-8");
                }
                return;
            }
            res.StatusCode = 404; res.Close();
        }

        // Embedded names may use '\' for sub folders; index them by a normalized '/' path.
        static readonly Dictionary<string, string> Resources = typeof(WebServer).Assembly.GetManifestResourceNames()
            .ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

        static readonly Dictionary<string, string> Types = new Dictionary<string, string>
        {
            [".html"] = "text/html; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".mjs"] = "text/javascript; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".ico"] = "image/x-icon",
            [".json"] = "application/json; charset=utf-8",
        };

        void Static(HttpListenerContext ctx, string path)
        {
            if (path == "/" || path == "") path = "/index.html";
            if (path == "/favicon.ico") { SendResource(ctx.Response, "icon.ico", "image/x-icon"); return; }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            Types.TryGetValue(ext, out var type);
            SendResource(ctx.Response, "ui" + path, type ?? "application/octet-stream");
        }

        void SendResource(HttpListenerResponse res, string name, string type)
        {
            // Dev override: a "ui" folder next to the exe wins over the embedded copy.
            var dev = Path.Combine(Program.BaseDir, name.Replace('/', '\\'));
            Stream s = File.Exists(dev) ? File.OpenRead(dev) : (Resources.TryGetValue(name, out var real) ? typeof(WebServer).Assembly.GetManifestResourceStream(real) : null);
            if (s == null) { res.StatusCode = 404; res.Close(); return; }
            using (s)
            {
                res.ContentType = type;
                res.Headers["Cache-Control"] = "no-cache";
                res.ContentLength64 = s.Length;
                s.CopyTo(res.OutputStream);
            }
            res.Close();
        }

        public static (int, string) Forward(string method, int port, string path, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
            req.Method = method; req.Proxy = null; req.Timeout = 60000; req.ReadWriteTimeout = 300000;
            if (body != null)
            {
                var b = Utf8.GetBytes(body);
                req.ContentType = "application/json";
                req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            HttpWebResponse r;
            try { r = (HttpWebResponse)req.GetResponse(); }
            catch (WebException we) when (we.Response is HttpWebResponse er) { r = er; }
            using (r)
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return ((int)r.StatusCode, sr.ReadToEnd());
        }

        static string Body(HttpListenerRequest req)
        {
            using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) return r.ReadToEnd();
        }

        static void SendJson(HttpListenerResponse res, object o, int status = 200)
        {
            res.StatusCode = status;
            SendText(res, Store.Json.Serialize(o), "application/json; charset=utf-8");
        }

        static void SendText(HttpListenerResponse res, string text, string type)
        {
            var b = Utf8.GetBytes(text ?? "");
            res.ContentType = type;
            res.Headers["Cache-Control"] = "no-store";
            res.ContentLength64 = b.Length;
            res.OutputStream.Write(b, 0, b.Length);
            res.Close();
        }

        /// A query-string value decoded as UTF-8 (HttpListener would use the system code page).
        static string QueryParam(Uri url, string name)
        {
            foreach (var part in url.Query.TrimStart('?').Split('&'))
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length == 2 && kv[0] == name) return Uri.UnescapeDataString(kv[1].Replace('+', ' '));
            }
            return null;
        }
    }

    static class Memory
    {
        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

        public static Dictionary<string, object> Info()
        {
            var m = new MEMORYSTATUSEX();
            GlobalMemoryStatusEx(m);
            return new Dictionary<string, object> { ["total"] = (double)m.ullTotalPhys, ["free"] = (double)m.ullAvailPhys };
        }
    }
}
