using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QQNTLiteLoaderSetup
{
    internal class Paths
    {
        public string QQ;
        public string Version;
        public string Resources;
        public string AppDir;
        public string AppAsar;
        public string Host;
    }

    internal class AsarFile
    {
        public string Name;
        public byte[] Data;
        public AsarFile(string name, byte[] data) { Name = name; Data = data; }
    }

    internal class PluginInfo
    {
        public string Folder;
        public string Slug;
        public string Name;
        public string Version;
        public string Type;
        public string Desc;
        public bool Enabled;
    }

    public class MainForm : Form
    {
        private const string HostDirName = "LiteLoaderQQNT";
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "qqnt-litelloader-installer.log");

        private Label qqPathLabel;
        private Label versionLabel;
        private Label injectLabel;
        private Label hostLabel;
        private Label pluginCountLabel;
        private TextBox logBox;
        private ListView pluginList;
        private CheckBox pluginSwitch;
        private StatusStrip statusStrip;
        private bool suppressSwitch;
        private ToolStripStatusLabel statusText;

        private string qqRoot;
        private string currentVersion;
        private Paths paths;
        private bool silent;

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0].StartsWith("--"))
            {
                string output = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "qqnt-installer-cli.json");
                using (MainForm form = new MainForm())
                {
                    form.ExecuteCli(args[0], output);
                }
                return;
            }
            Application.Run(new MainForm());
        }

        public MainForm()
        {
            Text = "QQNT LiteLoader 一键安装器";
            ClientSize = new Size(980, 640);
            MinimumSize = new Size(880, 560);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(80, 60);
            WindowState = FormWindowState.Normal;
            Font = new Font("Microsoft YaHei UI", 9f);

            GroupBox status = new GroupBox();
            status.Text = "QQ 状态";
            status.Dock = DockStyle.Top;
            status.Height = 130;
            status.Padding = new Padding(10, 6, 10, 6);

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 2;
            grid.RowCount = 5;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 20));

            grid.Controls.Add(MakeCaption("安装目录："), 0, 0);
            qqPathLabel = MakeValue();
            grid.Controls.Add(qqPathLabel, 1, 0);
            grid.Controls.Add(MakeCaption("当前版本："), 0, 1);
            versionLabel = MakeValue();
            grid.Controls.Add(versionLabel, 1, 1);
            grid.Controls.Add(MakeCaption("注入状态："), 0, 2);
            injectLabel = MakeValue();
            grid.Controls.Add(injectLabel, 1, 2);
            grid.Controls.Add(MakeCaption("宿主状态："), 0, 3);
            hostLabel = MakeValue();
            grid.Controls.Add(hostLabel, 1, 3);
            grid.Controls.Add(MakeCaption("插件数量："), 0, 4);
            pluginCountLabel = MakeValue();
            grid.Controls.Add(pluginCountLabel, 1, 4);

            status.Controls.Add(grid);

            Panel buttons = new Panel();
            buttons.Dock = DockStyle.Top;
            buttons.Height = 52;
            buttons.Padding = new Padding(10, 8, 10, 8);

            Button installButton = MakeButton("一键安装 / 修复", 140, buttons.Left + 10);
            installButton.Click += delegate { DoInstall(); };
            Button restoreButton = MakeButton("恢复原生启动", 130, installButton.Right + 8);
            restoreButton.Click += delegate { DoRestore(); };
            Button refreshButton = MakeButton("刷新状态", 100, restoreButton.Right + 8);
            refreshButton.Click += delegate { RefreshStatus(); };
            Button openPluginDir = MakeButton("打开插件目录", 120, refreshButton.Right + 8);
            openPluginDir.Click += delegate { OpenPluginDir(); };
            Button launchQQ = MakeButton("启动 QQ", 100, 0);
            launchQQ.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            launchQQ.Left = buttons.Width - launchQQ.Width - 10;
            launchQQ.Click += delegate { LaunchQQ(); };
            Button browse = MakeButton("手动指定 QQ…", 120, openPluginDir.Right + 8);
            browse.Click += delegate { BrowseQQ(); };

            buttons.Controls.Add(installButton);
            buttons.Controls.Add(restoreButton);
            buttons.Controls.Add(refreshButton);
            buttons.Controls.Add(browse);
            buttons.Controls.Add(openPluginDir);
            buttons.Controls.Add(launchQQ);
            buttons.Resize += delegate
            {
                launchQQ.Left = buttons.Width - launchQQ.Width - 10;
            };

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;

            TabPage pluginPage = new TabPage("插件列表");
            pluginPage.Padding = new Padding(6);
            Panel pluginBar = new Panel();
            pluginBar.Dock = DockStyle.Top;
            pluginBar.Height = 40;
            pluginSwitch = new CheckBox();
            pluginSwitch.Text = "启用插件总开关";
            pluginSwitch.AutoSize = true;
            pluginSwitch.Location = new Point(6, 10);
            pluginSwitch.CheckedChanged += delegate { if (!suppressSwitch) ToggleAll(); };
            Button enableButton = new Button();
            enableButton.Text = "启用选中";
            enableButton.SetBounds(160, 6, 96, 28);
            enableButton.Click += delegate { ToggleSelected(true); };
            Button disableButton = new Button();
            disableButton.Text = "禁用选中";
            disableButton.SetBounds(262, 6, 96, 28);
            disableButton.Click += delegate { ToggleSelected(false); };
            Label hint = new Label();
            hint.Text = "双击插件可快速切换启用状态；修改后需重启 QQ 生效。";
            hint.AutoSize = true;
            hint.ForeColor = Color.DimGray;
            hint.Location = new Point(372, 13);

            pluginBar.Controls.Add(pluginSwitch);
            pluginBar.Controls.Add(enableButton);
            pluginBar.Controls.Add(disableButton);
            pluginBar.Controls.Add(hint);

            pluginList = new ListView();
            pluginList.Dock = DockStyle.Fill;
            pluginList.View = View.Details;
            pluginList.FullRowSelect = true;
            pluginList.GridLines = false;
            pluginList.MultiSelect = true;
            pluginList.Columns.Add("插件", 190);
            pluginList.Columns.Add("版本", 80);
            pluginList.Columns.Add("状态", 70);
            pluginList.Columns.Add("类型", 80);
            pluginList.Columns.Add("简介", 480);
            pluginList.DoubleClick += delegate { ToggleSelected(true); };

            pluginPage.Controls.Add(pluginList);
            pluginPage.Controls.Add(pluginBar);

            TabPage logPage = new TabPage("日志");
            logPage.Padding = new Padding(6);
            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.Dock = DockStyle.Fill;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.White;
            logBox.Font = new Font("Consolas", 9f);
            logPage.Controls.Add(logBox);

            tabs.TabPages.Add(pluginPage);
            tabs.TabPages.Add(logPage);

            statusStrip = new StatusStrip();
            statusText = new ToolStripStatusLabel("就绪");
            statusStrip.Items.Add(statusText);

            Controls.Add(tabs);
            Controls.Add(buttons);
            Controls.Add(status);
            Controls.Add(statusStrip);

            AppendLog("安装器已启动。");
            RefreshStatus();
        }

        private static Label MakeCaption(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = Color.FromArgb(90, 90, 90);
            return label;
        }

        private static Label MakeValue()
        {
            Label label = new Label();
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
            label.Text = "-";
            return label;
        }

        private Button MakeButton(string text, int width, int left)
        {
            Button button = new Button();
            button.Text = text;
            button.SetBounds(left, 8, width, 32);
            return button;
        }

        // ------------------------------------------------------------ 基础工具

        private void AppendLog(string text)
        {
            string line = string.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), text);
            if (logBox != null)
            {
                logBox.AppendText(line + Environment.NewLine);
                statusText.Text = text;
            }
            try
            {
                using (StreamWriter sw = new StreamWriter(LogPath, true, Encoding.UTF8))
                    sw.WriteLine(string.Format("{0} {1}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), text));
            }
            catch { }
            Application.DoEvents();
        }

        private static string ReadResourceText(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new Exception("内置资源缺失：" + name);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        private static string ResourceToTempFile(string name, string suffix)
        {
            string temp = Path.Combine(Path.GetTempPath(),
                "lq-res-" + Guid.NewGuid().ToString("N").Substring(0, 8) + suffix);
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new Exception("内置资源缺失：" + name);
                using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[8192];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        fs.Write(buffer, 0, read);
                }
            }
            return temp;
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                string dest = Path.Combine(target, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch { }
        }

        private static bool IsAdmin()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return true;
            }
        }


        private static string JsonEscape(string value)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append(string.Format("\\u{0:x4}", (int)ch));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string CompactJson(Dictionary<string, object> data)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            return serializer.Serialize(data);
        }

        // ------------------------------------------------------------ 定位 QQ

        private static void AddRegistryDirs(List<string> list, RegistryHive hive, RegistryView view)
        {
            string[] keys = new string[]
            {
                @"SOFTWARE\Tencent\QQNT",
                @"SOFTWARE\Tencent\QQ",
                @"SOFTWARE\WOW6432Node\Tencent\QQNT",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QQ",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\QQ"
            };
            string[] names = new string[] { "InstallDir", "InstallPath", "install_path", "Path", "DisplayIcon", "UninstallString" };
            foreach (string sub in keys)
            {
                try
                {
                    RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    RegistryKey key = baseKey.OpenSubKey(sub);
                    if (key == null) continue;
                    foreach (string name in names)
                    {
                        try
                        {
                            object value = key.GetValue(name);
                            if (value == null) continue;
                            string text = Convert.ToString(value).Trim().Trim('"');
                            if (text.Length == 0) continue;
                            if (text.ToLower().EndsWith(".exe"))
                                text = Path.GetDirectoryName(text);
                            if (text.Length > 0) list.Add(text);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        private static List<string> CandidateDirs()
        {
            List<string> list = new List<string>();
            AddRegistryDirs(list, RegistryHive.LocalMachine, RegistryView.Registry64);
            AddRegistryDirs(list, RegistryHive.LocalMachine, RegistryView.Registry32);
            AddRegistryDirs(list, RegistryHive.CurrentUser, RegistryView.Default);

            string[] programRoots = new string[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramW6432")
            };
            foreach (string root in programRoots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                list.Add(Path.Combine(root, "Tencent", "QQNT"));
                list.Add(Path.Combine(root, "Tencent", "QQ"));
                list.Add(Path.Combine(root, "QQNT"));
            }
            foreach (char drive in "CDEFGAB".ToCharArray())
            {
                string prefix = drive + ":\\";
                if (!Directory.Exists(prefix)) continue;
                list.Add(Path.Combine(prefix, "Program Files", "Tencent", "QQNT"));
                list.Add(Path.Combine(prefix, "Program Files (x86)", "Tencent", "QQNT"));
                list.Add(Path.Combine(prefix, "Tencent", "QQNT"));
                list.Add(Path.Combine(prefix, "App", "QQNT"));
                list.Add(Path.Combine(prefix, "QQNT"));
            }

            List<string> unique = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string item in list)
            {
                if (string.IsNullOrEmpty(item)) continue;
                string key = item.ToLowerInvariant();
                if (seen.Contains(key)) continue;
                seen.Add(key);
                unique.Add(item);
            }
            return unique;
        }

        private static string FindQQDir(List<string> extra)
        {
            List<string> candidates = new List<string>();
            if (extra != null) candidates.AddRange(extra);
            candidates.AddRange(CandidateDirs());
            foreach (string dir in candidates)
            {
                if (File.Exists(Path.Combine(dir, "QQ.exe")) && Directory.Exists(Path.Combine(dir, "versions")))
                    return dir;
            }
            return null;
        }

        private static List<string> VersionDirs(string qqRoot)
        {
            List<string> result = new List<string>();
            string root = Path.Combine(qqRoot, "versions");
            if (!Directory.Exists(root)) return result;
            foreach (string dir in Directory.GetDirectories(root))
            {
                if (Directory.Exists(Path.Combine(dir, "resources", "app")))
                    result.Add(Path.GetFileName(dir));
            }
            return result;
        }

        private static string PickVersion(string qqRoot)
        {
            List<string> versions = VersionDirs(qqRoot);
            if (versions.Count == 0) return null;
            string cur = null;
            string configPath = Path.Combine(qqRoot, "versions", "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    Dictionary<string, object> config = new JavaScriptSerializer()
                        .Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath, Encoding.UTF8));
                    if (config.ContainsKey("curVersion") && config["curVersion"] != null)
                        cur = Convert.ToString(config["curVersion"]);
                }
                catch { }
            }
            if (cur != null && versions.Contains(cur)) return cur;
            string best = versions[0];
            foreach (string name in versions)
                if (CompareVersion(name, best) > 0) best = name;
            return best;
        }

        private static int CompareVersion(string a, string b)
        {
            string[] pa = a.Split('-')[0].Split('.');
            string[] pb = b.Split('-')[0].Split('.');
            int len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                int va = i < pa.Length && pa[i].Length > 0 && char.IsDigit(pa[i][0]) ? int.Parse(pa[i]) : 0;
                int vb = i < pb.Length && pb[i].Length > 0 && char.IsDigit(pb[i][0]) ? int.Parse(pb[i]) : 0;
                if (va != vb) return va - vb;
            }
            return 0;
        }

        private static Paths BuildPaths(string qqRoot, string version)
        {
            Paths p = new Paths();
            p.QQ = qqRoot;
            p.Version = version;
            p.Resources = Path.Combine(qqRoot, "versions", version, "resources");
            p.AppDir = Path.Combine(p.Resources, "app");
            p.AppAsar = Path.Combine(p.Resources, "app.asar");
            p.Host = Path.Combine(qqRoot, HostDirName);
            return p;
        }

        private static bool QQRunning()
        {
            return Process.GetProcessesByName("QQ").Length > 0 ||
                   Process.GetProcessesByName("QQEX").Length > 0;
        }

        private static void KillQQ()
        {
            foreach (Process process in Process.GetProcessesByName("QQ")) TryKill(process);
            foreach (Process process in Process.GetProcessesByName("QQEX")) TryKill(process);
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch { }
        }

        // ------------------------------------------------------------ 状态

        private static string InjectionState(Paths p)
        {
            if (!File.Exists(p.AppAsar)) return "未注入";
            if (ContainsMarker(p.AppAsar, OurMarker)) return "已注入";
            if (ContainsMarker(p.AppAsar, "[LQ-Inject]")) return "其他版本的注入";
            return "未知 app.asar";
        }

        private const string OurMarker = "QQNT-LiteLoader-OneClick";

        private static bool ContainsMarker(string file, string marker)
        {
            try
            {
                byte[] head = new byte[16384];
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                {
                    int read = fs.Read(head, 0, head.Length);
                    string text = Encoding.UTF8.GetString(head, 0, read);
                    return text.IndexOf(marker, StringComparison.Ordinal) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string HostState(string hostDir)
        {
            string pkg = Path.Combine(hostDir, "package.json");
            if (!File.Exists(pkg)) return "未安装";
            try
            {
                Dictionary<string, object> data = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(pkg, Encoding.UTF8));
                if (Convert.ToString(data["name"]) != "liteloader-qqnt") return "目录被其他文件占用";
                string store = Path.Combine(hostDir, "src", "main", "store.js");
                bool patched = File.Exists(store) &&
                    File.ReadAllText(store, Encoding.UTF8).Contains("parentPath");
                return "LiteLoaderQQNT " + Convert.ToString(data["version"]) + (patched ? "（已打兼容补丁）" : "（缺少兼容补丁）");
            }
            catch
            {
                return "读取失败";
            }
        }

        // ------------------------------------------------------------ asar

        private static byte[] BuildAsar(List<AsarFile> files)
        {
            StringBuilder header = new StringBuilder();
            header.Append("{\"files\":{\"package.json\":{\"size\":").Append(files[0].Data.Length)
                .Append(",\"offset\":\"0\"},\"lq-build.json\":{\"size\":").Append(files[1].Data.Length)
                .Append(",\"offset\":\"").Append(files[0].Data.Length).Append("\"},\"app_launcher\":{\"files\":{\"index.js\":{\"size\":")
                .Append(files[2].Data.Length).Append(",\"offset\":\"").Append(files[0].Data.Length + files[1].Data.Length)
                .Append("\"}}},\"application\":{\"files\":{\"background.js\":{\"size\":").Append(files[3].Data.Length)
                .Append(",\"offset\":\"").Append(files[0].Data.Length + files[1].Data.Length + files[2].Data.Length)
                .Append("\"}}}}}");

            byte[] headerBytes = Encoding.UTF8.GetBytes(header.ToString());
            int total = 16 + headerBytes.Length;
            foreach (AsarFile file in files) total += file.Data.Length;

            MemoryStream ms = new MemoryStream(total);
            BinaryWriter writer = new BinaryWriter(ms);
            writer.Write((uint)4);
            writer.Write((uint)(headerBytes.Length + 8));
            writer.Write((uint)(headerBytes.Length + 4));
            writer.Write((uint)headerBytes.Length);
            writer.Write(headerBytes);
            foreach (AsarFile file in files) writer.Write(file.Data);
            writer.Flush();
            byte[] result = ms.ToArray();
            writer.Close();
            return result;
        }

        private static byte[] BuildInjectionAsar(Paths p)
        {
            string appPkgPath = Path.Combine(p.AppDir, "package.json");
            Dictionary<string, object> appPkg = new JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(File.ReadAllText(appPkgPath, Encoding.UTF8));
            appPkg["main"] = "./app_launcher/index.js";

            string launcher = ReadResourceText("app_launcher.js")
                .Replace("__LQ_LOADER_PATH__", "\"" + JsonEscape(p.Host) + "\"");
            string background = ReadResourceText("background.js");

            string buildInfo = "{\"builder\":{\"name\":\"QQNT-LiteLoader-OneClick\",\"version\":\"1.0.0\"}"
                + ",\"builtAt\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\""
                + ",\"qq\":{\"name\":\"" + JsonEscape(Convert.ToString(appPkg["name"]))
                + "\",\"version\":\"" + JsonEscape(Convert.ToString(appPkg["version"]))
                + "\",\"buildVersion\":\"" + JsonEscape(Convert.ToString(appPkg["buildVersion"])) + "\"}"
                + ",\"loader\":{\"path\":\"" + JsonEscape(p.Host) + "\",\"mode\":\"构建时固定\"}}";

            List<AsarFile> files = new List<AsarFile>();
            files.Add(new AsarFile("package.json", Encoding.UTF8.GetBytes(CompactJson(appPkg))));
            files.Add(new AsarFile("lq-build.json", Encoding.UTF8.GetBytes(buildInfo)));
            files.Add(new AsarFile("app_launcher/index.js", Encoding.UTF8.GetBytes(launcher)));
            files.Add(new AsarFile("application/background.js", Encoding.UTF8.GetBytes(background)));
            return BuildAsar(files);
        }

        // ------------------------------------------------------------ 安装 / 恢复

        private void DoInstall()
        {
            if (qqRoot == null)
            {
                Alert("没有找到 QQNT 安装目录，请点“手动指定 QQ…”选择 QQ 所在文件夹。", MessageBoxIcon.Warning);
                return;
            }
            if (!IsAdmin())
            {
                Alert("写入 QQ 安装目录需要管理员权限，请以管理员身份重新运行安装器。", MessageBoxIcon.Warning);
                return;
            }
            if (QQRunning() && !silent)
            {
                if (MessageBox.Show(this, "安装前需要结束正在运行的 QQ，是否继续？", Text,
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                AppendLog("=== 开始安装 ===");
                string version = PickVersion(qqRoot);
                if (version == null) throw new Exception("没有找到带 resources\\app 的 QQ 版本目录");
                Paths p = BuildPaths(qqRoot, version);

                if (QQRunning())
                {
                    AppendLog("结束 QQ 进程");
                    KillQQ();
                    System.Threading.Thread.Sleep(1500);
                }

                InstallHost(p);
                DeployAsar(p);
                SetPluginsEnabled(FindProfile(qqRoot), true);
                AppendLog("插件总开关已设为开启");
                AppendLog("安装完成：QQ " + version + "，重启 QQ 即生效");
                statusText.Text = "安装完成";
            }
            catch (Exception ex)
            {
                AppendLog("安装失败：" + ex.Message);
                if (silent) throw;
                MessageBox.Show(this, "安装失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                RefreshStatus();
            }
        }

        private void InstallHost(Paths p)
        {
            string zip = ResourceToTempFile("host.zip", ".zip");
            string tempDir = Path.Combine(Path.GetTempPath(), "lq-host-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                ZipFile.ExtractToDirectory(zip, tempDir);
                if (Directory.Exists(p.Host))
                {
                    string backup = p.Host + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    try { Directory.Move(p.Host, backup); }
                    catch { DeleteDirectory(p.Host); }
                }
                CopyDirectory(tempDir, p.Host);
                string store = Path.Combine(p.Host, "src", "main", "store.js");
                if (!File.Exists(store) || !File.ReadAllText(store, Encoding.UTF8).Contains("parentPath"))
                    throw new Exception("宿主兼容补丁未生效，安装包可能已损坏");
                AppendLog("宿主已安装到 " + p.Host);
            }
            finally
            {
                try { File.Delete(zip); } catch { }
                DeleteDirectory(tempDir);
            }
        }

        private void DeployAsar(Paths p)
        {
            if (File.Exists(p.AppAsar))
            {
                string bak = p.AppAsar + ".bak";
                try
                {
                    if (!File.Exists(bak)) File.Copy(p.AppAsar, bak, true);
                    else File.Copy(p.AppAsar, p.AppAsar + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                }
                catch { }
            }
            byte[] data = BuildInjectionAsar(p);
            File.WriteAllBytes(p.AppAsar, data);
            AppendLog("注入点已写入 " + p.AppAsar + "（" + data.Length + " 字节）");
        }

        private void DoRestore()
        {
            if (qqRoot == null) return;
            string version = PickVersion(qqRoot);
            if (version == null) return;
            Paths p = BuildPaths(qqRoot, version);
            if (!silent && MessageBox.Show(this, "将移除注入点并恢复 QQ 原生启动，继续？", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                if (QQRunning())
                {
                    AppendLog("结束 QQ 进程");
                    KillQQ();
                    System.Threading.Thread.Sleep(1500);
                }
                if (File.Exists(p.AppAsar)) File.Delete(p.AppAsar);
                string bak = p.AppAsar + ".bak";
                if (File.Exists(bak))
                {
                    if (ContainsMarker(bak, "[LQ-Inject]") || ContainsMarker(bak, OurMarker))
                    {
                        AppendLog("备份本身也是注入文件，已跳过（QQ 直接使用原生 resources\\app）");
                    }
                    else
                    {
                        File.Copy(bak, p.AppAsar, true);
                        AppendLog("已从备份还原原生 app.asar");
                    }
                }
                AppendLog("已恢复 QQ 原生启动");
            }
            catch (Exception ex)
            {
                AppendLog("恢复失败：" + ex.Message);
                if (silent) throw;
                MessageBox.Show(this, "恢复失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                RefreshStatus();
            }
        }

        private void Alert(string message, MessageBoxIcon icon)
        {
            if (silent) throw new Exception(message);
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, icon);
        }

        // ------------------------------------------------------------ 命令行模式

        public void ExecuteCli(string command, string outputPath)
        {
            silent = true;
            StringBuilder json = new StringBuilder();
            string error = null;
            try
            {
                RefreshStatus();
                if (command == "--install") DoInstall();
                else if (command == "--restore") DoRestore();
                else if (command != "--status") throw new Exception("未知参数：" + command);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            bool enableAll = false;
            List<PluginInfo> plugins = ScanPlugins(FindProfile(qqRoot), out enableAll);
            json.Append("{\"ok\":").Append(error == null ? "true" : "false");
            json.Append(",\"command\":\"").Append(JsonEscape(command)).Append("\"");
            if (error != null) json.Append(",\"error\":\"").Append(JsonEscape(error)).Append("\"");
            json.Append(",\"qqRoot\":\"").Append(JsonEscape(qqRoot == null ? "" : qqRoot)).Append("\"");
            json.Append(",\"version\":\"").Append(JsonEscape(currentVersion == null ? "" : currentVersion)).Append("\"");
            json.Append(",\"asar\":\"").Append(JsonEscape(paths == null ? "" : InjectionState(paths))).Append("\"");
            json.Append(",\"host\":\"").Append(JsonEscape(paths == null ? "" : HostState(paths.Host))).Append("\"");
            json.Append(",\"pluginsEnabled\":").Append(enableAll ? "true" : "false");
            json.Append(",\"plugins\":[");
            for (int i = 0; i < plugins.Count; i++)
            {
                if (i > 0) json.Append(",");
                json.Append("{\"name\":\"").Append(JsonEscape(plugins[i].Name))
                    .Append("\",\"slug\":\"").Append(JsonEscape(plugins[i].Slug))
                    .Append("\",\"version\":\"").Append(JsonEscape(plugins[i].Version))
                    .Append("\",\"enabled\":").Append(plugins[i].Enabled ? "true" : "false").Append("}");
            }
            json.Append("]}");
            File.WriteAllText(outputPath, json.ToString(), Encoding.UTF8);
        }

        // ------------------------------------------------------------ 插件

        private static string DocumentsDir()
        {
            try
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (key != null)
                {
                    object value = key.GetValue("Personal");
                    if (value != null)
                    {
                        string dir = Environment.ExpandEnvironmentVariables(Convert.ToString(value));
                        if (Directory.Exists(dir)) return dir;
                    }
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        }

        private static string FindProfile(string qqRoot)
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            List<string> candidates = new List<string>();
            candidates.Add(Path.Combine(DocumentsDir(), "LiteloaderQQNT"));
            candidates.Add(Path.Combine(userProfile, "LiteloaderQQNT"));
            if (qqRoot != null) candidates.Add(Path.Combine(qqRoot, HostDirName));
            foreach (string path in candidates)
                if (Directory.Exists(Path.Combine(path, "plugins"))) return path;
            return candidates[0];
        }

        private static string ConfigPath(string profile)
        {
            return Path.Combine(profile, "data", "LiteLoader", "config.json");
        }

        private static Dictionary<string, object> LoadConfig(string profile)
        {
            string path = ConfigPath(profile);
            Dictionary<string, object> config = null;
            if (File.Exists(path))
            {
                try
                {
                    config = new JavaScriptSerializer()
                        .Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                }
                catch { config = null; }
            }
            if (config == null) config = new Dictionary<string, object>();
            if (!config.ContainsKey("enable_plugins")) config["enable_plugins"] = true;
            if (!config.ContainsKey("disabled_plugins")) config["disabled_plugins"] = new List<object>();
            if (!config.ContainsKey("deleting_plugins")) config["deleting_plugins"] = new Dictionary<string, object>();
            return config;
        }

        private static void SaveConfig(string profile, Dictionary<string, object> config)
        {
            string path = ConfigPath(profile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            File.WriteAllText(path, serializer.Serialize(config), Encoding.UTF8);
        }

        private static List<string> DisabledSlugs(Dictionary<string, object> config)
        {
            List<string> result = new List<string>();
            object raw = config["disabled_plugins"];
            if (raw is List<object>)
            {
                foreach (object item in (List<object>)raw)
                    result.Add(Convert.ToString(item));
            }
            else if (raw is string)
            {
                string text = Convert.ToString(raw);
                if (text.Length > 0) result.Add(text);
            }
            return result;
        }

        private static bool EnableAll(string profile)
        {
            Dictionary<string, object> config = LoadConfig(profile);
            return config["enable_plugins"] is bool ? Convert.ToBoolean(config["enable_plugins"]) : true;
        }

        private static void SetPluginsEnabled(string profile, bool enabled)
        {
            Dictionary<string, object> config = LoadConfig(profile);
            config["enable_plugins"] = enabled;
            SaveConfig(profile, config);
        }

        private static List<PluginInfo> ScanPlugins(string profile, out bool enableAll)
        {
            Dictionary<string, object> config = LoadConfig(profile);
            enableAll = config["enable_plugins"] is bool ? Convert.ToBoolean(config["enable_plugins"]) : true;
            List<string> disabled = DisabledSlugs(config);
            List<PluginInfo> result = new List<PluginInfo>();
            string pluginsDir = Path.Combine(profile, "plugins");
            if (!Directory.Exists(pluginsDir)) return result;
            foreach (string dir in Directory.GetDirectories(pluginsDir))
            {
                string manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                Dictionary<string, object> manifest;
                try
                {
                    manifest = new JavaScriptSerializer()
                        .Deserialize<Dictionary<string, object>>(File.ReadAllText(manifestPath, Encoding.UTF8));
                }
                catch { continue; }
                PluginInfo info = new PluginInfo();
                info.Folder = Path.GetFileName(dir);
                info.Slug = manifest.ContainsKey("slug") && manifest["slug"] != null
                    ? Convert.ToString(manifest["slug"]) : info.Folder;
                info.Name = manifest.ContainsKey("name") && manifest["name"] != null
                    ? Convert.ToString(manifest["name"]) : info.Slug;
                info.Version = manifest.ContainsKey("version") && manifest["version"] != null
                    ? Convert.ToString(manifest["version"]) : "-";
                info.Type = manifest.ContainsKey("type") && manifest["type"] != null
                    ? Convert.ToString(manifest["type"]) : "";
                string desc = manifest.ContainsKey("description") && manifest["description"] != null
                    ? Convert.ToString(manifest["description"]) : "";
                info.Desc = desc.Replace("\r", " ").Replace("\n", " ");
                info.Enabled = enableAll && !disabled.Contains(info.Slug);
                result.Add(info);
            }
            return result;
        }

        private void ToggleAll()
        {
            string profile = FindProfile(qqRoot);
            SetPluginsEnabled(profile, pluginSwitch.Checked);
            AppendLog("插件总开关 -> " + (pluginSwitch.Checked ? "开启" : "关闭") + "（重启 QQ 生效）");
            RefreshStatus();
        }

        private void ToggleSelected(bool enable)
        {
            if (pluginList.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "请先在列表中选择插件。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string profile = FindProfile(qqRoot);
            Dictionary<string, object> config = LoadConfig(profile);
            List<string> disabled = DisabledSlugs(config);
            List<string> changed = new List<string>();
            foreach (ListViewItem item in pluginList.SelectedItems)
            {
                PluginInfo info = (PluginInfo)item.Tag;
                if (enable)
                {
                    if (disabled.Contains(info.Slug)) disabled.Remove(info.Slug);
                    config["enable_plugins"] = true;
                }
                else
                {
                    if (!disabled.Contains(info.Slug)) disabled.Add(info.Slug);
                }
                changed.Add(info.Slug);
            }
            List<object> disabledObjects = new List<object>();
            foreach (string slug in disabled) disabledObjects.Add(slug);
            config["disabled_plugins"] = disabledObjects;
            SaveConfig(profile, config);
            AppendLog((enable ? "启用" : "禁用") + "插件：" + string.Join(", ", changed.ToArray()) + "（重启 QQ 生效）");
            RefreshStatus();
        }

        private void OpenPluginDir()
        {
            string profile = FindProfile(qqRoot);
            string dir = Path.Combine(profile, "plugins");
            Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", dir);
        }

        private void OpenLogFile()
        {
            if (File.Exists(LogPath)) Process.Start("notepad.exe", LogPath);
        }

        // ------------------------------------------------------------ 界面刷新

        private void RefreshStatus()
        {
            List<string> extra = new List<string>();
            if (qqRoot != null) extra.Add(qqRoot);
            qqRoot = FindQQDir(extra);
            currentVersion = qqRoot != null ? PickVersion(qqRoot) : null;
            paths = currentVersion != null ? BuildPaths(qqRoot, currentVersion) : null;

            if (qqRoot == null)
            {
                qqPathLabel.Text = "未检测到 QQNT，可点“手动指定 QQ…”";
                versionLabel.Text = "-";
                injectLabel.Text = "无法安装";
                hostLabel.Text = "-";
                pluginCountLabel.Text = "-";
                statusText.Text = "未检测到 QQNT";
            }
            else
            {
                qqPathLabel.Text = qqRoot;
                versionLabel.Text = currentVersion ?? "未找到版本目录";
                injectLabel.Text = paths != null ? InjectionState(paths) : "-";
                hostLabel.Text = paths != null ? HostState(paths.Host) : "-";
            }

            string profile = FindProfile(qqRoot);
            bool enableAll = false;
            List<PluginInfo> plugins = ScanPlugins(profile, out enableAll);
            suppressSwitch = true;
            pluginSwitch.Checked = enableAll;
            suppressSwitch = false;
            pluginSwitch.Text = "启用插件总开关";
            pluginCountLabel.Text = plugins.Count + " 个（" + (enableAll ? "总开关开" : "总开关关") + "）";

            pluginList.BeginUpdate();
            pluginList.Items.Clear();
            foreach (PluginInfo info in plugins)
            {
                ListViewItem item = new ListViewItem(info.Name);
                item.SubItems.Add(info.Version);
                item.SubItems.Add(info.Enabled ? "启用" : "禁用");
                item.SubItems.Add(info.Type);
                item.SubItems.Add(info.Desc);
                item.Tag = info;
                if (!info.Enabled) item.ForeColor = Color.Gray;
                pluginList.Items.Add(item);
            }
            pluginList.EndUpdate();
        }

        private void BrowseQQ()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "请选择 QQNT 安装目录（包含 QQ.exe 与 versions 文件夹）";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    string dir = dialog.SelectedPath;
                    if (!File.Exists(Path.Combine(dir, "QQ.exe")))
                    {
                        MessageBox.Show(this, "该目录下没有 QQ.exe，请重新选择。", Text,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    qqRoot = dir;
                    RefreshStatus();
                }
            }
        }

        private void LaunchQQ()
        {
            if (qqRoot == null) return;
            string exe = Path.Combine(qqRoot, "QQ.exe");
            if (File.Exists(exe)) Process.Start(exe);
        }
    }
}




