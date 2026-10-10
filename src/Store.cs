using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace LocalChat
{
    /// Chats and settings as plain JSON files in the data folder (portable, survives updates).
    class Store
    {
        readonly string _chats, _settings, _index;
        readonly object _lock = new object();
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        static readonly Regex IdRx = new Regex("^[A-Za-z0-9_-]{1,64}$");
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public Store(string dataDir)
        {
            _chats = Path.Combine(dataDir, "שיחות");
            Directory.CreateDirectory(_chats);
            _settings = Path.Combine(dataDir, "settings.json");
            _index = Path.Combine(_chats, "index.json");
            if (!File.Exists(_index)) RebuildIndex();
        }

        public static bool ValidId(string id) => id != null && IdRx.IsMatch(id);

        public string ReadSettings() => File.Exists(_settings) ? File.ReadAllText(_settings, Encoding.UTF8) : "{}";

        public Dictionary<string, object> ReadSettingsDict()
        {
            try { return Json.Deserialize<Dictionary<string, object>>(ReadSettings()); } catch { return new Dictionary<string, object>(); }
        }

        public void WriteSettings(string json)
        {
            Json.DeserializeObject(json); // validate
            lock (_lock) AtomicWrite(_settings, json);
        }

        public void MergeSettings(Dictionary<string, object> changes)
        {
            lock (_lock)
            {
                var d = ReadSettingsDict();
                foreach (var kv in changes) d[kv.Key] = kv.Value;
                AtomicWrite(_settings, Json.Serialize(d));
            }
        }

        public string ListChats()
        {
            lock (_lock) return File.Exists(_index) ? File.ReadAllText(_index, Encoding.UTF8) : "[]";
        }

        public string ReadChat(string id)
        {
            var f = Path.Combine(_chats, id + ".json");
            return File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8) : null;
        }

        public void SaveChat(string id, string json)
        {
            var d = Json.Deserialize<Dictionary<string, object>>(json);
            lock (_lock)
            {
                AtomicWrite(Path.Combine(_chats, id + ".json"), json);
                var idx = LoadIndex();
                idx.RemoveAll(x => (x["id"] as string) == id);
                idx.Add(Meta(id, d));
                SaveIndex(idx);
            }
        }

        public void DeleteChat(string id)
        {
            lock (_lock)
            {
                var f = Path.Combine(_chats, id + ".json");
                if (File.Exists(f)) File.Delete(f);
                var idx = LoadIndex();
                idx.RemoveAll(x => (x["id"] as string) == id);
                SaveIndex(idx);
            }
        }

        static Dictionary<string, object> Meta(string id, Dictionary<string, object> d)
        {
            d.TryGetValue("title", out var title);
            d.TryGetValue("updated", out var updated);
            d.TryGetValue("pinned", out var pinned);
            return new Dictionary<string, object>
            {
                ["id"] = id,
                ["title"] = title ?? "שיחה חדשה",
                ["updated"] = updated ?? 0,
                ["pinned"] = pinned is bool b && b,
            };
        }

        List<Dictionary<string, object>> LoadIndex()
        {
            try { return Json.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(_index, Encoding.UTF8)) ?? new List<Dictionary<string, object>>(); }
            catch { return RebuildIndex(); }
        }

        void SaveIndex(List<Dictionary<string, object>> idx)
        {
            var sorted = idx.OrderByDescending(x => Convert.ToDouble(x["updated"] ?? 0)).ToList();
            AtomicWrite(_index, Json.Serialize(sorted));
        }

        List<Dictionary<string, object>> RebuildIndex()
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var f in Directory.GetFiles(_chats, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (id == "index" || !ValidId(id)) continue;
                try { list.Add(Meta(id, Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(f, Encoding.UTF8)))); } catch { }
            }
            SaveIndex(list);
            return list;
        }

        static void AtomicWrite(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, Utf8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
