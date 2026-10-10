using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LocalChat
{
    enum EngineKind { Chat, Image }

    /// Runs one backend process (llama-server for chat, sd-server for images) and tracks its state.
    class Engine
    {
        readonly string _exe;
        readonly EngineKind _kind;
        readonly object _lock = new object();
        Process _proc;
        int _generation;
        readonly LinkedList<string> _log = new LinkedList<string>();

        public string State { get; private set; } = "idle"; // idle | loading | ready | error
        public string Model { get; private set; }
        public string Error { get; private set; }
        public int Port { get; private set; }
        public int Ctx { get; private set; }
        public int Threads { get; private set; }
        public bool Vision { get; private set; }
        public int Progress { get; private set; }
        public bool Available => File.Exists(_exe);

        public Engine(string exe, EngineKind kind) { _exe = exe; _kind = kind; }

        /* ---------------- model discovery ---------------- */

        public static string ImagesDir => Path.Combine(Program.ModelsDir, "תמונות");
        static readonly Regex SplitRest = new Regex(@"-0*([2-9]|\d{2,})-of-\d+\.gguf$", RegexOptions.IgnoreCase);
        static readonly string[] ImageExt = { ".gguf", ".safetensors", ".ckpt", ".sft" };
        // Helper files that sit next to an image model and are passed with their own flag.
        static readonly (string flag, Regex rx)[] Companions =
        {
            ("--vae", new Regex(@"(^|[^a-z])(vae|ae)([^a-z]|$)", RegexOptions.IgnoreCase)),
            ("--clip_l", new Regex("clip[_-]?l", RegexOptions.IgnoreCase)),
            ("--clip_g", new Regex("clip[_-]?g", RegexOptions.IgnoreCase)),
            ("--t5xxl", new Regex("t5", RegexOptions.IgnoreCase)),
            ("--llm", new Regex(@"qwen|mistral|gemma|llama|umt5|text[_-]?encoder|(^|[^a-z])te[_-]", RegexOptions.IgnoreCase)),
            ("--taesd", new Regex("taesd|tae[_-]", RegexOptions.IgnoreCase)),
        };

        static bool IsImagePath(string f) =>
            f.StartsWith(ImagesDir + "\\", StringComparison.OrdinalIgnoreCase) ||
            f.IndexOf("\\images\\", StringComparison.OrdinalIgnoreCase) >= 0;

        public static List<Dictionary<string, object>> ListModels()
        {
            var list = new List<Dictionary<string, object>>();
            if (!Directory.Exists(Program.ModelsDir)) return list;
            foreach (var f in Directory.EnumerateFiles(Program.ModelsDir, "*.gguf", SearchOption.AllDirectories))
            {
                if (IsImagePath(f)) continue;
                var name = Path.GetFileName(f);
                if (name.IndexOf("mmproj", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (SplitRest.IsMatch(name)) continue;
                long size = 0;
                try
                {
                    var m = Regex.Match(name, @"^(.*)-0*1-of-(\d+)\.gguf$", RegexOptions.IgnoreCase);
                    if (m.Success)
                        size = Directory.GetFiles(Path.GetDirectoryName(f), m.Groups[1].Value + "-*-of-" + m.Groups[2].Value + ".gguf").Sum(x => new FileInfo(x).Length);
                    else size = new FileInfo(f).Length;
                }
                catch { }
                list.Add(new Dictionary<string, object>
                {
                    ["file"] = Rel(f),
                    ["name"] = Pretty(name),
                    ["size"] = size,
                    ["vision"] = FindMmproj(f) != null,
                });
            }
            return list.OrderBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// Image models live in מודלים\תמונות. A file there is a model; a sub folder is one model plus its helper files.
        public static List<Dictionary<string, object>> ListImageModels()
        {
            var list = new List<Dictionary<string, object>>();
            var roots = new[] { ImagesDir, Path.Combine(Program.ModelsDir, "images") }.Where(Directory.Exists);
            foreach (var root in roots)
            {
                foreach (var f in Directory.GetFiles(root).Where(IsImageFile))
                {
                    var len = new FileInfo(f).Length;
                    // Loose helper files (a VAE etc.) are small; a big file is a model even if its name says "vae".
                    if (CompanionFlag(Path.GetFileName(f)) != null && len < 1_000_000_000) continue;
                    list.Add(ImageEntry(f, len, Path.GetFileNameWithoutExtension(f)));
                }
                foreach (var d in Directory.GetDirectories(root))
                {
                    var main = MainImageFile(d);
                    if (main == null) continue;
                    var size = Directory.GetFiles(d, "*", SearchOption.AllDirectories).Where(IsImageFile).Sum(x => new FileInfo(x).Length);
                    list.Add(ImageEntry(main, size, Path.GetFileName(d)));
                }
            }
            return list.OrderBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase).ToList();
        }

        static Dictionary<string, object> ImageEntry(string f, long size, string name) => new Dictionary<string, object>
        {
            ["file"] = Rel(f),
            ["name"] = name,
            ["size"] = size,
            ["fast"] = Regex.IsMatch(name + Path.GetFileName(f), "turbo|lightning|lcm|schnell|hyper|flash", RegexOptions.IgnoreCase),
            ["xl"] = Regex.IsMatch(name + Path.GetFileName(f), "xl|flux|sd3|z-image|qwen", RegexOptions.IgnoreCase),
        };

        static bool IsImageFile(string f) => ImageExt.Contains(Path.GetExtension(f).ToLowerInvariant());

        static string CompanionFlag(string fileName)
        {
            var n = Path.GetFileNameWithoutExtension(fileName);
            foreach (var c in Companions) if (c.rx.IsMatch(n)) return c.flag;
            return null;
        }

        static string MainImageFile(string dir)
        {
            var files = Directory.GetFiles(dir).Where(IsImageFile).ToList();
            var mains = files.Where(f => CompanionFlag(Path.GetFileName(f)) == null).ToList();
            if (mains.Count == 0) mains = files; // nothing looks like a helper: take the biggest
            return mains.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }

        static string Rel(string f) => f.Substring(Program.ModelsDir.Length).TrimStart('\\');

        static string Pretty(string file)
        {
            var n = Regex.Replace(file, @"\.gguf$", "", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"-0*1-of-\d+$", "");
            return n;
        }

        static string FindMmproj(string modelPath)
        {
            try
            {
                return Directory.GetFiles(Path.GetDirectoryName(modelPath), "*.gguf")
                    .FirstOrDefault(x => Path.GetFileName(x).IndexOf("mmproj", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return null; }
        }

        /* ---------------- lifecycle ---------------- */

        public void AutoLoad(Dictionary<string, object> settings)
        {
            try
            {
                var models = _kind == EngineKind.Chat ? ListModels() : ListImageModels();
                if (models.Count == 0) return;
                var key = _kind == EngineKind.Chat ? "lastModel" : "lastImageModel";
                string want = settings != null && settings.TryGetValue(key, out var lm) ? lm as string : null;
                if (want == null || !models.Any(m => (string)m["file"] == want))
                {
                    if (_kind == EngineKind.Image || models.Count != 1) return; // image models load only on request
                    want = (string)models[0]["file"];
                }
                Load(want, GetInt(settings, "ctx", 8192), GetInt(settings, "threads", 0));
            }
            catch (Exception ex) { Log.Write("AutoLoad: " + ex.Message); }
        }

        public static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return def;
            try { return Convert.ToInt32(v); } catch { return def; }
        }

        public void Load(string relFile, int ctx, int threads)
        {
            if (!Available) throw new FileNotFoundException("רכיב המנוע חסר: " + Path.GetFileName(_exe));
            var path = Path.GetFullPath(Path.Combine(Program.ModelsDir, relFile));
            if (!path.StartsWith(Program.ModelsDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new FileNotFoundException("המודל לא נמצא: " + relFile);
            if (ctx < 512) ctx = 512;

            int gen;
            lock (_lock)
            {
                KillLocked();
                gen = ++_generation;
                Model = relFile; Ctx = ctx; Threads = threads; Error = null; Progress = 0;
                State = "loading";
                Port = FreePort();
                lock (_log) _log.Clear();

                var args = _kind == EngineKind.Chat ? ChatArgs(path, ctx, threads) : ImageArgs(path, threads);
                var psi = new ProcessStartInfo(_exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(_exe),
                };
                Log.Write("Starting " + _kind + " engine: " + args);
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => AddLog(e.Data, gen);
                p.ErrorDataReceived += (s, e) => AddLog(e.Data, gen);
                p.Exited += (s, e) => OnExit(gen, p);
                p.Start();
                KillOnExitJob.Add(p);
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _proc = p;
            }
            new Thread(() => WaitReady(gen)) { IsBackground = true }.Start();
        }

        string ChatArgs(string path, int ctx, int threads)
        {
            var mmproj = FindMmproj(path);
            Vision = mmproj != null;
            var a = new StringBuilder();
            a.Append("-m ").Append(Q(path));
            if (mmproj != null) a.Append(" --mmproj ").Append(Q(mmproj));
            a.Append(" --host 127.0.0.1 --port ").Append(Port);
            a.Append(" -c ").Append(ctx);
            if (threads > 0) a.Append(" -t ").Append(threads);
            a.Append(" -np 1 --no-webui --jinja -ngl 0 --metrics");
            return a.ToString();
        }

        string ImageArgs(string path, int threads)
        {
            var a = new StringBuilder();
            var dir = Path.GetDirectoryName(path);
            var helpers = new List<(string flag, string file)>();
            // Helper files are only picked up when the model has its own sub folder.
            if (!string.Equals(dir.TrimEnd('\\'), ImagesDir, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFileName(dir), "images", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var f in Directory.GetFiles(dir).Where(IsImageFile))
                {
                    if (string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) continue;
                    var flag = CompanionFlag(Path.GetFileName(f));
                    if (flag != null && !helpers.Any(h => h.flag == flag)) helpers.Add((flag, f));
                }
            }
            // Split models (FLUX, SD3, Z-Image…) come with text encoders: the main file is then a bare diffusion model.
            bool split = helpers.Any(h => h.flag == "--clip_l" || h.flag == "--t5xxl" || h.flag == "--llm");
            a.Append(split ? "--diffusion-model " : "-m ").Append(Q(path));
            foreach (var h in helpers) a.Append(' ').Append(h.flag).Append(' ').Append(Q(h.file));
            a.Append(" --listen-ip 127.0.0.1 --listen-port ").Append(Port);
            if (threads > 0) a.Append(" -t ").Append(threads);
            a.Append(" --mmap --diffusion-fa");
            return a.ToString();
        }

        static string Q(string s) => "\"" + s + "\"";

        void AddLog(string line, int gen)
        {
            if (line == null || gen != _generation) return;
            lock (_log)
            {
                _log.AddLast(line);
                while (_log.Count > 400) _log.RemoveFirst();
            }
            if (State != "loading") return;
            if (line.Contains("load_tensors") || line.Contains("loading tensors")) Progress = Math.Max(Progress, 30);
            if (line.Contains("llama_context") || line.Contains("total params memory")) Progress = Math.Max(Progress, 70);
            if (line.Contains("warming up")) Progress = Math.Max(Progress, 90);
        }

        void OnExit(int gen, Process p)
        {
            lock (_lock)
            {
                if (gen != _generation || p != _proc) return;
                if (State == "idle") return;
                State = "error";
                Error = ExtractError() ?? "המנוע נסגר באופן לא צפוי.";
                Log.Write(_kind + " engine exited: " + Error);
            }
        }

        string ExtractError()
        {
            lock (_log)
            {
                var errs = _log.Where(l => Regex.IsMatch(l, @"(^|\s)E\s|\[ERROR|error|failed", RegexOptions.IgnoreCase)).ToList();
                if (errs.Count == 0) return null;
                var all = string.Join("\n", errs);
                if (Regex.IsMatch(all, "unknown model architecture|unknown pre-tokenizer|wrong number of tensors|tensor .* not found|get sd version|unknown version", RegexOptions.IgnoreCase))
                    return "המודל הזה לא נתמך בגרסת המנוע הנוכחית או שהקובץ פגום/חסרים לו קבצים נלווים.\n" + errs.Last();
                if (Regex.IsMatch(all, "alloc|out of memory|failed to allocate", RegexOptions.IgnoreCase))
                    return "אין מספיק זיכרון למודל הזה. נסו גודל הקשר קטן יותר או מודל קטן יותר.\n" + errs.Last();
                return errs.Last();
            }
        }

        void WaitReady(int gen)
        {
            var url = "http://127.0.0.1:" + Port + (_kind == EngineKind.Chat ? "/health" : "/sdcpp/v1/capabilities");
            var deadline = DateTime.UtcNow.AddMinutes(20);
            while (DateTime.UtcNow < deadline)
            {
                if (gen != _generation || State != "loading") return;
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 3000; req.Proxy = null;
                    using (var r = (HttpWebResponse)req.GetResponse())
                        if (r.StatusCode == HttpStatusCode.OK)
                        {
                            lock (_lock) if (gen == _generation && State == "loading") { State = "ready"; Progress = 100; }
                            Log.Write(_kind + " engine ready on port " + Port);
                            return;
                        }
                }
                catch { }
                Thread.Sleep(400);
            }
            lock (_lock) if (gen == _generation && State == "loading") { State = "error"; Error = "טעינת המודל לקחה יותר מדי זמן."; KillLocked(); }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _generation++;
                KillLocked();
                State = "idle"; Model = null; Error = null;
            }
        }

        void KillLocked()
        {
            var p = _proc; _proc = null;
            if (p == null) return;
            // Note: Process.WaitForExit can block forever on .NET Framework when output is redirected, so poll instead.
            try
            {
                if (!p.HasExited) p.Kill();
                for (int i = 0; i < 50 && !p.HasExited; i++) Thread.Sleep(100);
            }
            catch { }
        }

        public string LogText()
        {
            lock (_log) return string.Join("\n", _log);
        }

        public Dictionary<string, object> Info() => new Dictionary<string, object>
        {
            ["state"] = State,
            ["model"] = Model,
            ["error"] = Error,
            ["port"] = Port,
            ["ctx"] = Ctx,
            ["threads"] = Threads,
            ["vision"] = Vision,
            ["progress"] = Progress,
            ["available"] = Available,
        };

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
