using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LocalChat
{
    /// The release is a single LocalChat.exe: on start it creates its folders and unpacks the engines next to itself.
    static class Setup
    {
        static Stream Resource(string name) => typeof(Setup).Assembly.GetManifestResourceStream(name);

        static string EmbeddedEngineVersion()
        {
            using (var s = Resource("engine-version.txt"))
            {
                if (s == null) return null;
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd().Trim();
            }
        }

        /// Creates the folders and the models note, and unpacks (or updates) the engines. Returns an error, or null.
        public static string EnsureLayout()
        {
            Directory.CreateDirectory(Program.ModelsDir);
            Directory.CreateDirectory(Path.Combine(Program.ModelsDir, "תמונות"));
            Directory.CreateDirectory(Program.DataDir);

            var note = Path.Combine(Program.ModelsDir, "קרא אותי.txt");
            if (!File.Exists(note))
                using (var s = Resource("models-readme.txt"))
                    if (s != null) using (var f = File.Create(note)) s.CopyTo(f);

            var want = EmbeddedEngineVersion();
            var engine = Program.EngineDir;
            var versionFile = Path.Combine(engine, "version.txt");
            var have = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;
            bool present = File.Exists(Path.Combine(engine, "llama-server.exe"));

            if (want == null)   // a build without embedded engines (development)
                return present ? null : "חסרה התיקייה engine עם llama-server.exe ליד התוכנה.\nהורידו את LocalChat.exe מדף ה-Releases.";
            if (present && have == want) return null;

            string error = null;
            using (var splash = new Splash(present ? "מעדכן את מנועי התוכנה…" : "מכין את התוכנה לשימוש ראשון…"))
            {
                var t = new Thread(() =>
                {
                    try { Unpack(engine, want); }
                    catch (Exception ex) { error = ex.Message; }
                    try { splash.BeginInvoke((Action)splash.Close); } catch { }
                }) { IsBackground = true };
                splash.Shown += (o, e) => t.Start();
                Application.Run(splash);
                t.Join();
            }
            if (error == null) return null;
            Log.Write("Engine unpack failed: " + error);
            return present ? null   // keep using the old engines
                : "לא הצלחתי לפרוס את המנועים לתיקייה engine:\n" + error + "\n\nאולי התיקייה מוגנת מכתיבה. נסו להעביר את LocalChat.exe לתיקייה אחרת (למשל במסמכים).";
        }

        static void Unpack(string engine, string version)
        {
            var tmp = engine + ".new";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            using (var s = Resource("engine.zip"))
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                zip.ExtractToDirectory(tmp);
            File.WriteAllText(Path.Combine(tmp, "version.txt"), version);
            // swap in the new folder; the old one may be held briefly by an antivirus scan
            if (Directory.Exists(engine))
            {
                var old = engine + ".old";
                if (Directory.Exists(old)) Directory.Delete(old, true);
                for (int i = 0; ; i++)
                {
                    try { Directory.Move(engine, old); break; }
                    catch when (i < 10) { Thread.Sleep(500); }
                }
                try { Directory.Delete(old, true); } catch { }
            }
            Directory.Move(tmp, engine);
        }

        sealed class Splash : Form
        {
            public Splash(string text)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterScreen;
                Size = new Size(380, 110);
                BackColor = Color.FromArgb(24, 24, 27);
                ShowInTaskbar = true;
                TopMost = true;
                RightToLeft = RightToLeft.Yes;
                Text = "צ'אט מקומי";
                try { using (var s = typeof(Setup).Assembly.GetManifestResourceStream("icon.ico")) Icon = new Icon(s); } catch { }
                var title = new Label { Text = "צ'אט מקומי", ForeColor = Color.White, Font = new Font("Segoe UI", 14f, FontStyle.Bold), AutoSize = false, Dock = DockStyle.Top, Height = 46, TextAlign = ContentAlignment.BottomCenter };
                var msg = new Label { Text = text, ForeColor = Color.FromArgb(180, 180, 190), Font = new Font("Segoe UI", 10.5f), AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
                var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Bottom, Height = 6, MarqueeAnimationSpeed = 30 };
                Controls.Add(msg); Controls.Add(bar); Controls.Add(title);
            }
        }
    }
}
