using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LocalChat
{
    /// Downloads model files into the models folder, with resume (.part files), retries and progress.
    static class Downloader
    {
        class FileItem { public string Url, Dest; public long Size; }

        class Job
        {
            public string Id, Name, Kind, State = "queued", Error, Folder;
            public List<FileItem> Files = new List<FileItem>();
            public long Total, Done, Current;
            public double Speed;
            public volatile bool Cancel;
        }

        static readonly object Lock = new object();
        static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();
        static string _remoteCatalog;
        static DateTime _remoteAt = DateTime.MinValue;

        static Downloader()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // + TLS 1.3
            ServicePointManager.DefaultConnectionLimit = 8;
        }

        /* ---------------- catalog ---------------- */

        /// The catalog shipped inside the exe, or a newer one from the repo (via jsDelivr, which filtered networks usually allow).
        public static string CatalogJson()
        {
            string local;
            using (var s = typeof(Downloader).Assembly.GetManifestResourceStream("ui/catalog.json"))
            using (var r = new StreamReader(s, Encoding.UTF8)) local = r.ReadToEnd();
            if ((DateTime.UtcNow - _remoteAt).TotalHours > 6)
            {
                // refresh in the background; this request uses what we have now
                _remoteAt = DateTime.UtcNow;
                ThreadPool.QueueUserWorkItem(_ => FetchRemoteCatalog());
            }
            try
            {
                if (_remoteCatalog != null && Version(_remoteCatalog) >= Version(local)) return _remoteCatalog;
            }
            catch { }
            return local;
        }

        public static void FetchRemoteCatalog()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://cdn.jsdelivr.net/gh/tsoolgee/local-chat@main/ui/catalog.json");
                req.Timeout = 8000; req.ReadWriteTimeout = 8000;
                using (var res = req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                {
                    var text = r.ReadToEnd();
                    var d = Store.Json.Deserialize<Dictionary<string, object>>(text);
                    if (d.ContainsKey("models")) _remoteCatalog = text;
                }
            }
            catch { }
        }

        static int Version(string json) => Convert.ToInt32(Store.Json.Deserialize<Dictionary<string, object>>(json)["version"]);

        static string KindRoot(string kind) => kind == "image" ? Engine.ImagesDir : Program.ModelsDir;

        /// Catalog plus, per model, which files are already on disk.
        public static string CatalogWithStatus()
        {
            var cat = Store.Json.Deserialize<Dictionary<string, object>>(CatalogJson());
            foreach (Dictionary<string, object> m in (ArrayList)cat["models"])
            {
                var dir = Path.Combine(KindRoot(m["kind"] as string), SafeName(m["folder"] as string));
                bool all = true, any = false;
                foreach (Dictionary<string, object> f in (ArrayList)m["files"])
                {
                    var p = Path.Combine(dir, SafeName(f["name"] as string));
                    var have = File.Exists(p) && new FileInfo(p).Length == Convert.ToInt64(f["size"]);
                    f["installed"] = have;
                    f["partial"] = !have && File.Exists(p + ".part") ? new FileInfo(p + ".part").Length : 0L;
                    if (have) any = true;
                    else if (!f.ContainsKey("optional")) all = false;
                }
                m["installed"] = all && any;
                // the file the engine will load (first required file)
                var main = ((ArrayList)m["files"]).Cast<Dictionary<string, object>>().First(f => !f.ContainsKey("optional"));
                m["path"] = Path.Combine(dir, SafeName(main["name"] as string)).Substring(Program.ModelsDir.Length).TrimStart('\\');
            }
            cat["freeDisk"] = FreeBytes(Program.ModelsDir);
            return Store.Json.Serialize(cat);
        }

        static string SafeName(string n)
        {
            n = n ?? "";
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim().Trim('.');
            if (n.Length == 0) throw new Exception("שם קובץ לא תקין");
            return n;
        }

        static double FreeBytes(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace; } catch { return -1; }
        }

        /* ---------------- starting jobs ---------------- */

        public static string StartCatalog(string id, bool withOptional)
        {
            var cat = Store.Json.Deserialize<Dictionary<string, object>>(CatalogJson());
            var m = ((ArrayList)cat["models"]).Cast<Dictionary<string, object>>().FirstOrDefault(x => (x["id"] as string) == id)
                    ?? throw new Exception("המודל לא נמצא בקטלוג");
            var kind = m["kind"] as string;
            var dir = Path.Combine(KindRoot(kind), SafeName(m["folder"] as string));
            var job = new Job { Id = id, Name = m["name"] as string, Kind = kind, Folder = dir };
            foreach (Dictionary<string, object> f in (ArrayList)m["files"])
            {
                if (f.ContainsKey("optional") && !withOptional) continue;
                job.Files.Add(new FileItem { Url = f["url"] as string, Dest = Path.Combine(dir, SafeName(f["name"] as string)), Size = Convert.ToInt64(f["size"]) });
            }
            return Start(job);
        }

        static readonly Regex HfUrl = new Regex(@"^https://(huggingface\.co|hf\.co)/([^/]+/[^/]+)/(blob|resolve)/([^/]+)/(.+?)(\?.*)?$", RegexOptions.IgnoreCase);

        /// Any direct link to a model file. Hugging Face "blob" page links are turned into download links.
        public static string StartUrl(string url, string kind)
        {
            url = (url ?? "").Trim();
            var m = HfUrl.Match(url);
            if (m.Success) url = "https://huggingface.co/" + m.Groups[2].Value + "/resolve/" + m.Groups[4].Value + "/" + m.Groups[5].Value;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
                throw new Exception("הקישור לא תקין");
            var name = SafeName(Uri.UnescapeDataString(Path.GetFileName(u.AbsolutePath)));
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (!new[] { ".gguf", ".safetensors", ".ckpt", ".sft" }.Contains(ext))
                throw new Exception("צריך קישור ישיר לקובץ מודל (.gguf או .safetensors)");
            if (kind != "image" && ext != ".gguf") kind = "image";
            var dir = KindRoot(kind);
            var job = new Job { Id = "url-" + Math.Abs(url.GetHashCode()).ToString("x"), Name = name, Kind = kind, Folder = dir };
            job.Files.Add(new FileItem { Url = url, Dest = Path.Combine(dir, name), Size = -1 });
            return Start(job);
        }

        static string Start(Job job)
        {
            lock (Lock)
            {
                if (Jobs.TryGetValue(job.Id, out var old) && (old.State == "downloading" || old.State == "queued"))
                    return job.Id;
                Jobs[job.Id] = job;
            }
            new Thread(() => Run(job)) { IsBackground = true, Name = "download " + job.Id }.Start();
            return job.Id;
        }

        public static void Cancel(string id)
        {
            lock (Lock) if (Jobs.TryGetValue(id, out var j)) j.Cancel = true;
        }

        public static void Forget(string id)
        {
            lock (Lock) if (Jobs.TryGetValue(id, out var j) && j.State != "downloading" && j.State != "queued") Jobs.Remove(id);
        }

        /// Deletes a catalog model's folder (only folders the catalog would have created).
        public static void DeleteCatalog(string id)
        {
            var cat = Store.Json.Deserialize<Dictionary<string, object>>(CatalogJson());
            var m = ((ArrayList)cat["models"]).Cast<Dictionary<string, object>>().FirstOrDefault(x => (x["id"] as string) == id)
                    ?? throw new Exception("המודל לא נמצא בקטלוג");
            var kind = m["kind"] as string;
            var dir = Path.Combine(KindRoot(kind), SafeName(m["folder"] as string));
            var eng = kind == "image" ? Program.ImageEngine : Program.Engine;
            if (eng.Model != null && Path.Combine(Program.ModelsDir, eng.Model).StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
                eng.Stop();
            lock (Lock) if (Jobs.TryGetValue(id, out var j) && j.State == "downloading") throw new Exception("ההורדה עדיין פעילה");
            foreach (Dictionary<string, object> f in (ArrayList)m["files"])
            {
                var p = Path.Combine(dir, SafeName(f["name"] as string));
                foreach (var x in new[] { p, p + ".part" }) if (File.Exists(x)) File.Delete(x);
            }
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }

        static string _update;
        static DateTime _updateAt = DateTime.MinValue;

        /// Newest release on GitHub vs. this build: {current, latest, url, newer}. Cached for 6 hours.
        public static string UpdateInfo()
        {
            var current = typeof(Downloader).Assembly.GetName().Version;
            var cur = current.Major + "." + current.Minor + "." + current.Build;
            if (_update == null || (DateTime.UtcNow - _updateAt).TotalHours > 6)
            {
                _updateAt = DateTime.UtcNow;
                try
                {
                    var req = NewRequest("https://api.github.com/repos/tsoolgee/local-chat/releases/latest");
                    req.Timeout = 8000; req.ReadWriteTimeout = 8000;
                    req.Accept = "application/vnd.github+json";
                    using (var res = req.GetResponse())
                    using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    {
                        var d = Store.Json.Deserialize<Dictionary<string, object>>(r.ReadToEnd());
                        var tag = ((d["tag_name"] as string) ?? "").TrimStart('v', 'V');
                        var newer = System.Version.TryParse(tag, out var lv) && lv > new System.Version(cur);
                        _update = Store.Json.Serialize(new Dictionary<string, object>
                        {
                            ["current"] = cur, ["latest"] = tag, ["url"] = d["html_url"], ["newer"] = newer,
                        });
                    }
                }
                catch { _update = null; }
            }
            return _update ?? Store.Json.Serialize(new Dictionary<string, object> { ["current"] = cur, ["newer"] = false });
        }

        public static string Status()
        {
            lock (Lock)
            {
                return Store.Json.Serialize(Jobs.Values.Select(j => new Dictionary<string, object>
                {
                    ["id"] = j.Id, ["name"] = j.Name, ["kind"] = j.Kind, ["state"] = j.State, ["error"] = j.Error,
                    ["total"] = j.Total, ["done"] = j.Done + j.Current, ["speed"] = j.Speed,
                    ["path"] = j.Files.Count > 0 ? j.Files[0].Dest.Substring(Program.ModelsDir.Length).TrimStart('\\') : null,
                }).ToList());
            }
        }

        /* ---------------- the actual download ---------------- */

        static void Run(Job job)
        {
            try
            {
                job.State = "downloading";
                Directory.CreateDirectory(job.Folder);
                foreach (var f in job.Files.Where(f => f.Size < 0)) f.Size = ProbeSize(f.Url);
                job.Total = job.Files.Sum(f => Math.Max(0, f.Size));

                long need = job.Files.Sum(f => File.Exists(f.Dest) && new FileInfo(f.Dest).Length == f.Size ? 0
                    : Math.Max(0, f.Size - (File.Exists(f.Dest + ".part") ? new FileInfo(f.Dest + ".part").Length : 0)));
                var free = FreeBytes(job.Folder);
                if (free >= 0 && need + 200_000_000 > free)
                    throw new Exception($"אין מספיק מקום בדיסק: צריך {need / 1e9:0.0}GB ויש {free / 1e9:0.0}GB פנויים.");

                foreach (var f in job.Files)
                {
                    if (File.Exists(f.Dest) && (f.Size <= 0 || new FileInfo(f.Dest).Length == f.Size)) { job.Done += new FileInfo(f.Dest).Length; continue; }
                    DownloadFile(job, f);
                    job.Done += f.Size;
                    job.Current = 0;
                }
                job.State = "done";
                Log.Write("Downloaded " + job.Name);
            }
            catch (OperationCanceledException) { job.State = "cancelled"; }
            catch (Exception ex)
            {
                job.State = "error";
                job.Error = ex.Message;
                Log.Write("Download " + job.Name + " failed: " + ex.Message);
            }
            job.Speed = 0;
        }

        static HttpWebRequest NewRequest(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "LocalChat/1.0";
            req.AllowAutoRedirect = true;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            var proxy = Program.Store.ReadSettingsDict().TryGetValue("proxy", out var p) ? (p as string ?? "").Trim() : "";
            if (proxy.Length > 0)
            {
                if (!proxy.Contains("://")) proxy = "http://" + proxy;
                req.Proxy = new WebProxy(proxy);
            }
            return req;
        }

        static long ProbeSize(string url)
        {
            var req = NewRequest(url);
            req.Method = "HEAD";
            try { using (var r = (HttpWebResponse)req.GetResponse()) return r.ContentLength; }
            catch (WebException we) { throw Friendly(we); }
        }

        static void DownloadFile(Job job, FileItem f)
        {
            var part = f.Dest + ".part";
            int failures = 0;
            var buf = new byte[1 << 20];
            while (true)
            {
                if (job.Cancel) throw new OperationCanceledException();
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (f.Size > 0 && have > f.Size) { File.Delete(part); have = 0; }
                if (f.Size > 0 && have == f.Size) break;
                job.Current = have;
                try
                {
                    var req = NewRequest(f.Url);
                    if (have > 0) req.AddRange(have);
                    using (var res = (HttpWebResponse)req.GetResponse())
                    {
                        if (have > 0 && res.StatusCode != HttpStatusCode.PartialContent) have = 0; // server ignored the range: start over
                        if (f.Size <= 0) f.Size = res.ContentLength + have;
                        using (var input = res.GetResponseStream())
                        using (var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            long windowStart = have, windowAt = 0;
                            int n;
                            while ((n = input.Read(buf, 0, buf.Length)) > 0)
                            {
                                if (job.Cancel) throw new OperationCanceledException();
                                output.Write(buf, 0, n);
                                have += n;
                                job.Current = have;
                                var ms = sw.ElapsedMilliseconds;
                                if (ms - windowAt >= 1000)
                                {
                                    var speed = (have - windowStart) * 1000.0 / (ms - windowAt);
                                    job.Speed = job.Speed <= 0 ? speed : job.Speed * 0.7 + speed * 0.3;
                                    windowStart = have; windowAt = ms;
                                }
                                failures = 0;
                            }
                        }
                    }
                    if (f.Size <= 0 || have >= f.Size) break;
                }
                catch (OperationCanceledException) { throw; }
                catch (WebException we) when (we.Response is HttpWebResponse r && (int)r.StatusCode == 416 && f.Size > 0 && have == f.Size) { break; }
                catch (WebException we) when (we.Response is HttpWebResponse r && (int)r.StatusCode >= 400 && (int)r.StatusCode < 500 && (int)r.StatusCode != 408 && (int)r.StatusCode != 429)
                {
                    throw Friendly(we);
                }
                catch (Exception ex)
                {
                    // network hiccup: wait and resume from where we stopped
                    if (++failures > 30) throw new Exception("ההורדה נכשלה שוב ושוב: " + ex.Message);
                    job.Speed = 0;
                    Thread.Sleep(Math.Min(30, 2 * failures) * 1000);
                }
            }
            if (f.Size > 0 && new FileInfo(part).Length != f.Size) throw new Exception("הקובץ שהורד לא שלם");
            if (File.Exists(f.Dest)) File.Delete(f.Dest);
            File.Move(part, f.Dest);
        }

        static Exception Friendly(WebException we)
        {
            if (we.Response is HttpWebResponse r)
            {
                var code = (int)r.StatusCode;
                if (code == 418 || code == 403 && (r.Headers["Server"] ?? "").IndexOf("netfree", StringComparison.OrdinalIgnoreCase) >= 0)
                    return new Exception("ההורדה נחסמה על ידי סינון האינטרנט (למשל נטפרי). אפשר להגדיר פרוקסי בהגדרות, או להוריד את הקובץ בדרך אחרת ולשים אותו בתיקיית המודלים.");
                if (code == 401 || code == 403) return new Exception("האתר דורש הרשאה לקובץ הזה (מודל נעול). הורידו אותו ידנית אחרי אישור התנאים באתר.");
                if (code == 404) return new Exception("הקובץ לא נמצא בכתובת הזו.");
                return new Exception("השרת החזיר שגיאה " + code);
            }
            return new Exception("אין חיבור לאינטרנט או לשרת: " + we.Message);
        }
    }
}
