using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LocalChat
{
    static class Program
    {
        public static string BaseDir, ModelsDir, DataDir, EngineDir;
        public static Engine Engine, ImageEngine;
        public static WebServer Server;
        public static Store Store;
        static Mutex _mutex;

        [STAThread]
        static void Main(string[] args)
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            ModelsDir = Path.Combine(BaseDir, "מודלים");
            if (!Directory.Exists(ModelsDir) && Directory.Exists(Path.Combine(BaseDir, "models")))
                ModelsDir = Path.Combine(BaseDir, "models");
            DataDir = Path.Combine(BaseDir, "נתונים");
            EngineDir = Path.Combine(BaseDir, "engine");

            var key = "LocalChat_" + BaseDir.ToLowerInvariant().GetHashCode().ToString("X8");
            _mutex = new Mutex(true, key, out bool first);
            if (!first)
            {
                // Already running from this folder: just bring up a window.
                try
                {
                    var port = File.ReadAllText(Path.Combine(DataDir, "port.txt")).Trim();
                    Window.Open("http://localhost:" + port + "/", false);
                }
                catch { }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal: " + e.ExceptionObject);

            try
            {
                Directory.CreateDirectory(ModelsDir);
                Directory.CreateDirectory(DataDir);
                Log.Init(Path.Combine(DataDir, "log.txt"));

                if (!File.Exists(Path.Combine(EngineDir, "llama-server.exe")))
                {
                    MessageBox.Show("חסרה התיקייה engine עם llama-server.exe ליד התוכנה.\nהורידו את הגרסה המלאה (ZIP) מדף ה-Releases.",
                        "צ'אט מקומי", MessageBoxButtons.OK, MessageBoxIcon.Error,
                        MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                    return;
                }

                Store = new Store(DataDir);
                Engine = new Engine(Path.Combine(EngineDir, "llama-server.exe"), EngineKind.Chat);
                ImageEngine = new Engine(Path.Combine(EngineDir, "sd", "sd-server.exe"), EngineKind.Image);
                Server = new WebServer();
                Server.Start();
                File.WriteAllText(Path.Combine(DataDir, "port.txt"), Server.Port.ToString());

                var settings = Store.ReadSettingsDict();
                Engine.AutoLoad(settings);
                ImageEngine.AutoLoad(settings);

                Application.Run(new TrayContext("http://localhost:" + Server.Port + "/", !args.Contains("--no-window")));
            }
            catch (Exception ex)
            {
                Log.Write("Startup failed: " + ex);
                MessageBox.Show("שגיאה בהפעלה:\n" + ex.Message, "צ'אט מקומי", MessageBoxButtons.OK, MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            }
            finally
            {
                try { Engine?.Stop(); } catch { }
                try { ImageEngine?.Stop(); } catch { }
                try { Server?.Stop(); } catch { }
            }
        }

        public static Icon AppIcon()
        {
            using (var s = typeof(Program).Assembly.GetManifestResourceStream("icon.ico"))
                return s != null ? new Icon(s) : SystemIcons.Application;
        }
    }

    class TrayContext : ApplicationContext
    {
        readonly NotifyIcon _tray;
        readonly string _url;
        readonly SynchronizationContext _ui;

        public TrayContext(string url, bool openWindow)
        {
            _url = url;
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
            menu.Items.Add("פתח את הצ'אט", null, (s, e) => OpenWindow());
            menu.Items.Add("פתח את תיקיית המודלים", null, (s, e) => Process.Start("explorer.exe", "\"" + Program.ModelsDir + "\""));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("יציאה", null, (s, e) => Quit());
            _tray = new NotifyIcon
            {
                Icon = Program.AppIcon(),
                Text = "צ'אט מקומי",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => OpenWindow();
            if (openWindow) OpenWindow();
        }

        System.Windows.Forms.Timer _watch;
        int _seen, _missing;

        void OpenWindow()
        {
            var p = Window.Open(_url, true);
            if (p == null)
            {
                _tray.ShowBalloonTip(4000, "צ'אט מקומי", "הצ'אט נפתח בדפדפן. התוכנה ממשיכה לרוץ כאן ליד השעון.", ToolTipIcon.Info);
                return;
            }
            // Browsers often hand the window to another process, so watch for our private profile instead of the PID.
            _seen = 0; _missing = 0;
            if (_watch == null)
            {
                _watch = new System.Windows.Forms.Timer { Interval = 2500 };
                _watch.Tick += (s, e) => CheckWindow();
            }
            _watch.Start();
        }

        bool _checking;
        async void CheckWindow()
        {
            if (_checking) return;
            _checking = true;
            try
            {
                var open = await System.Threading.Tasks.Task.Run(() => Window.AnyOpen());
                if (open) { _seen++; _missing = 0; return; }
                _missing++;
                if (_seen == 0 && _missing > 12) { _watch.Stop(); return; } // never saw it: stay in tray
                if (_seen > 0 && _missing >= 2) { _watch.Stop(); Quit(); }
            }
            finally { _checking = false; }
        }

        void Quit()
        {
            _tray.Visible = false;
            try { Program.Engine.Stop(); } catch { }
            try { Program.ImageEngine.Stop(); } catch { }
            ExitThread();
        }
    }

    static class Window
    {
        static string ProfileDir => Path.Combine(Program.DataDir ?? Path.GetTempPath(), "window");

        static string FindBrowser()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        /// Opens the UI as a standalone app window. Returns the browser process, or null if it fell back to the default browser.
        public static Process Open(string url, bool tracked)
        {
            var exe = FindBrowser();
            if (exe != null)
            {
                try
                {
                    var args = "--app=\"" + url + "\" --user-data-dir=\"" + ProfileDir + "\" --no-first-run --no-default-browser-check " +
                               "--disable-features=Translate --window-size=1280,860 --lang=he";
                    return Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
                }
                catch (Exception ex) { Log.Write("Browser launch failed: " + ex.Message); }
            }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            return null;
        }

        /// True if a browser process using our private profile is still alive.
        public static bool AnyOpen()
        {
            try
            {
                var marker = ProfileDir.ToLowerInvariant();
                foreach (var cmd in ProcessCmd.Browsers())
                    if (cmd != null && cmd.ToLowerInvariant().Contains(marker) && !cmd.Contains("--type=")) return true;
            }
            catch { }
            return false;
        }
    }

    static class Log
    {
        static string _path;
        static readonly object _lock = new object();
        public static void Init(string path)
        {
            _path = path;
            try { if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path); } catch { }
        }
        public static void Write(string msg)
        {
            if (_path == null) return;
            lock (_lock)
            {
                try { File.AppendAllText(_path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine, Encoding.UTF8); } catch { }
            }
        }
    }
}
