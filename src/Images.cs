using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LocalChat
{
    /// Polls sd-server jobs and stores finished images as files, so chats keep small links instead of base64.
    static class Images
    {
        static string Dir => Path.Combine(Program.DataDir, "תמונות");
        static readonly ConcurrentDictionary<string, string> Done = new ConcurrentDictionary<string, string>();
        static readonly Regex NameRx = new Regex(@"^[A-Za-z0-9_-]{1,80}\.(png|jpg|jpeg|webp)$");

        public static string FilePath(string name)
        {
            if (!NameRx.IsMatch(name)) return null;
            var f = Path.Combine(Dir, name);
            return File.Exists(f) ? f : null;
        }

        public static string Job(int port, string id)
        {
            if (Done.TryGetValue(id, out var cached)) return cached;
            var (code, text) = WebServer.Forward("GET", port, "/sdcpp/v1/jobs/" + id, null);
            if (code != 200) return Store.Json.Serialize(new Dictionary<string, object> { ["status"] = "failed", ["error"] = new Dictionary<string, object> { ["message"] = "העבודה לא נמצאה (" + code + ")" } });
            var job = Store.Json.Deserialize<Dictionary<string, object>>(text);
            if (job.TryGetValue("status", out var st) && (st as string) == "completed" &&
                job.TryGetValue("result", out var ro) && ro is Dictionary<string, object> result)
            {
                Directory.CreateDirectory(Dir);
                var fmt = result.TryGetValue("output_format", out var fo) && fo is string fs && fs.Length > 0 ? fs : "png";
                var urls = new List<object>();
                if (result.TryGetValue("images", out var io) && io is System.Collections.ArrayList arr)
                {
                    int i = 0;
                    foreach (Dictionary<string, object> im in arr)
                    {
                        if (!(im.TryGetValue("b64_json", out var b) && b is string b64)) continue;
                        var name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + id.GetHashCode().ToString("x8") + "-" + (i++) + "." + (fmt == "jpeg" ? "jpg" : fmt);
                        File.WriteAllBytes(Path.Combine(Dir, name), Convert.FromBase64String(b64));
                        urls.Add("/api/image-file/" + name);
                    }
                }
                var outJob = new Dictionary<string, object> { ["status"] = "completed", ["images"] = urls, ["started"] = job["started"], ["completed"] = job["completed"] };
                var json = Store.Json.Serialize(outJob);
                Done[id] = json;
                return json;
            }
            return text;
        }
    }
}
