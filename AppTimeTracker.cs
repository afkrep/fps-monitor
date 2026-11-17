using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrackerForm());
    }
}

class TrackerForm : Form
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    const uint QUERY_LIMITED_INFORMATION = 0x1000;
    readonly string dataFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppTimeTracker", "activity.csv");
    readonly ListView ranking = new ListView();
    readonly ListView applications = new ListView();
    readonly ImageList applicationIcons = new ImageList();
    readonly HashSet<string> iconKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly Panel applicationsPage = new Panel();
    readonly Panel chart = new Panel();
    readonly Label status = new Label();
    readonly Label todayTotal = new Label();
    readonly Label topApp = new Label();
    readonly Label activeStatus = new Label();
    readonly Button toggle = new Button();
    readonly Timer timer = new Timer();
    bool tracking;
    string lastApp;
    DateTime lastTick;

    public TrackerForm()
    {
        Text = "Время в приложениях";
        Size = new Size(1120, 760);
        MinimumSize = new Size(960, 660);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 17, 31);
        Font = new Font("Segoe UI", 10F);

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 205, BackColor = Color.FromArgb(14, 21, 37), Padding = new Padding(16, 22, 14, 16) };
        var brand = new Label { Text = "◉  FOCUS\n     TRACKER", Dock = DockStyle.Top, Height = 72, ForeColor = Color.White, Font = new Font("Segoe UI", 14F, FontStyle.Bold) };
        var navCaption = new Label { Text = "МЕНЮ", Dock = DockStyle.Top, Height = 40, ForeColor = Color.FromArgb(105, 122, 151), Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(10, 0, 0, 7) };
        var navHome = NavButton("▦   Обзор", true);
        var navApps = NavButton("◷   Приложения", false);
        var navStack = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 92, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        navStack.Controls.AddRange(new Control[] { navHome, navApps });
        var sidebarBottom = new Label { Text = "●  Данные хранятся локально\n   на этом компьютере", Dock = DockStyle.Bottom, Height = 48, ForeColor = Color.FromArgb(116, 135, 160), Font = new Font("Segoe UI", 8.5F) };
        sidebar.Controls.Add(navStack); sidebar.Controls.Add(navCaption); sidebar.Controls.Add(brand); sidebar.Controls.Add(sidebarBottom);

        var workspace = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(12, 17, 31), Padding = new Padding(28, 24, 28, 18) };
        var header = new Panel { Dock = DockStyle.Top, Height = 79, BackColor = Color.Transparent };
        var title = new Label { Text = "Обзор активности", Font = new Font("Segoe UI", 22F, FontStyle.Bold), ForeColor = Color.FromArgb(241, 245, 249), AutoSize = true, Location = new Point(0, 0) };
        var subtitle = new Label { Text = "Ваше время за компьютером — в одном месте", ForeColor = Color.FromArgb(134, 148, 171), AutoSize = true, Location = new Point(2, 40) };
        toggle.Text = "▶  Начать учёт";
        toggle.Size = new Size(180, 40);
        toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        toggle.Location = new Point(600, 8);
        toggle.FlatStyle = FlatStyle.Flat; toggle.FlatAppearance.BorderSize = 0;
        toggle.BackColor = Color.FromArgb(103, 81, 245); toggle.ForeColor = Color.White;
        toggle.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold); toggle.Cursor = Cursors.Hand;
        toggle.Click += (s, e) => ToggleTracking();
        status.Text = "●  Учёт остановлен"; status.AutoSize = true; status.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        status.ForeColor = Color.FromArgb(138, 151, 173); status.Location = new Point(410, 55);
        header.Controls.AddRange(new Control[] { title, subtitle, toggle, status });

        var cards = new TableLayoutPanel { Dock = DockStyle.Top, Height = 124, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent, Padding = new Padding(0, 7, 0, 12) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
        cards.Controls.Add(CreateCard("ВРЕМЯ СЕГОДНЯ", todayTotal, Color.FromArgb(190, 94, 255), "◷"), 0, 0);
        cards.Controls.Add(CreateCard("ТОП ПРИЛОЖЕНИЕ", topApp, Color.FromArgb(25, 212, 207), "↗"), 1, 0);
        cards.Controls.Add(CreateCard("СТАТУС УЧЁТА", activeStatus, Color.FromArgb(85, 151, 255), "●"), 2, 0);

        var lower = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Padding = new Padding(0, 7, 0, 0), BackColor = Color.Transparent };
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); lower.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40)); lower.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0)); lower.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        var chartCard = DarkCard(); chartCard.Dock = DockStyle.Fill;
        var chartTitle = new Label { Text = "Активность за 7 дней", Dock = DockStyle.Top, Height = 48, Padding = new Padding(16, 15, 0, 0), ForeColor = Color.FromArgb(236, 241, 250), Font = new Font("Segoe UI", 11F, FontStyle.Bold) };
        chart = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        chart.Paint += DrawWeeklyChart;
        chartCard.Controls.Add(chart); chartCard.Controls.Add(chartTitle);
        var rankCard = DarkCard(); rankCard.Dock = DockStyle.Fill;
        var rankTitle = new Label { Text = "Часто используемые", Dock = DockStyle.Top, Height = 48, Padding = new Padding(16, 15, 0, 0), ForeColor = Color.FromArgb(236, 241, 250), Font = new Font("Segoe UI", 11F, FontStyle.Bold) };
        ranking.Dock = DockStyle.Fill; ranking.View = View.Details; ranking.FullRowSelect = true; ranking.BorderStyle = BorderStyle.None;
        ranking.BackColor = Color.FromArgb(20, 29, 49); ranking.ForeColor = Color.FromArgb(218, 226, 241); ranking.Font = new Font("Segoe UI", 9.5F);
        ranking.HeaderStyle = ColumnHeaderStyle.None; ranking.Columns.Add("Приложение", 190); ranking.Columns.Add("Время", 82, HorizontalAlignment.Right);
        rankCard.Controls.Add(ranking); rankCard.Controls.Add(rankTitle);
        lower.Controls.Add(chartCard, 0, 0); lower.Controls.Add(new Panel(), 1, 0); lower.Controls.Add(rankCard, 2, 0);
        applicationIcons.ColorDepth = ColorDepth.Depth32Bit;
        applicationIcons.ImageSize = new Size(24, 24);
        applicationsPage.Dock = DockStyle.Fill; applicationsPage.BackColor = Color.FromArgb(20, 29, 49); applicationsPage.Visible = false;
        var appPageTitle = new Label { Text = "Приложения за последние 7 дней", Dock = DockStyle.Top, Height = 52, Padding = new Padding(18, 16, 0, 0), ForeColor = Color.FromArgb(236, 241, 250), Font = new Font("Segoe UI", 11F, FontStyle.Bold) };
        applications.Dock = DockStyle.Fill; applications.View = View.Details; applications.FullRowSelect = true; applications.BorderStyle = BorderStyle.None;
        applications.BackColor = Color.FromArgb(20, 29, 49); applications.ForeColor = Color.FromArgb(218, 226, 241); applications.Font = new Font("Segoe UI", 10F);
        applications.SmallImageList = applicationIcons; applications.HeaderStyle = ColumnHeaderStyle.None;
        applications.Columns.Add("ПРИЛОЖЕНИЕ", 480); applications.Columns.Add("ВРЕМЯ ЗА 7 ДНЕЙ", 180, HorizontalAlignment.Right);
        applicationsPage.Controls.Add(applications); applicationsPage.Controls.Add(appPageTitle);
        navHome.Click += (s, e) => { lower.Visible = true; applicationsPage.Visible = false; title.Text = "Обзор активности"; subtitle.Text = "Ваше время за компьютером — в одном месте"; SelectNav(navHome, navApps); };
        navApps.Click += (s, e) => { lower.Visible = false; applicationsPage.Visible = true; title.Text = "Приложения"; subtitle.Text = "Список программ, их значки и время использования"; SelectNav(navApps, navHome); };
        var footer = new Label { Text = "УЧЁТ ЗАПУСКАЕТСЯ АВТОМАТИЧЕСКИ  •  ПЕРЕКЛЮЧИТЕСЬ НА ДРУГОЕ ПРИЛОЖЕНИЕ  •  ОБНОВЛЕНИЕ КАЖДЫЕ 5 СЕКУНД", Dock = DockStyle.Bottom, Height = 26, ForeColor = Color.FromArgb(91, 106, 130), Font = new Font("Segoe UI", 8F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        workspace.Controls.Add(lower); workspace.Controls.Add(applicationsPage); workspace.Controls.Add(cards); workspace.Controls.Add(header); workspace.Controls.Add(footer);
        Controls.Add(workspace); Controls.Add(sidebar);
        timer.Interval = 5000;
        timer.Tick += (s, e) => TickUsage();
        FormClosing += (s, e) => { SaveLast(); };
        tracking = true;
        lastTick = DateTime.Now;
        lastApp = null;
        toggle.Text = "■  Остановить учёт";
        status.Text = "●  Учёт активен";
        status.ForeColor = Color.FromArgb(45, 214, 160);
        activeStatus.Text = "Активен";
        timer.Start();
        RefreshLists();
    }

    Button NavButton(string text, bool selected)
    {
        return new Button { Text = text, Width = 165, Height = 38, TextAlign = ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, BackColor = selected ? Color.FromArgb(48, 54, 93) : Color.FromArgb(14, 21, 37), ForeColor = selected ? Color.White : Color.FromArgb(146, 160, 184), Font = new Font("Segoe UI", 9.5F, selected ? FontStyle.Bold : FontStyle.Regular), Padding = new Padding(10, 0, 0, 0), Margin = new Padding(0, 0, 0, 5), Cursor = Cursors.Hand };
    }

    void SelectNav(Button selected, params Button[] buttons)
    {
        foreach (Button b in buttons)
        {
            bool active = b == selected;
            b.BackColor = active ? Color.FromArgb(48, 54, 93) : Color.FromArgb(14, 21, 37);
            b.ForeColor = active ? Color.White : Color.FromArgb(146, 160, 184);
            b.Font = new Font("Segoe UI", 9.5F, active ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    Panel DarkCard()
    {
        return new Panel { BackColor = Color.FromArgb(20, 29, 49), Padding = new Padding(1) };
    }

    Panel CreateCard(string heading, Label value, Color accent, string symbol)
    {
        var card = new Panel { BackColor = Color.FromArgb(20, 29, 49), Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0), Padding = new Padding(15, 12, 10, 8) };
        var caption = new Label { Text = heading, Dock = DockStyle.Top, Height = 25, ForeColor = Color.FromArgb(139, 153, 178), Font = new Font("Segoe UI", 8F, FontStyle.Bold) };
        var icon = new Label { Text = symbol, Dock = DockStyle.Right, Width = 38, TextAlign = ContentAlignment.MiddleCenter, ForeColor = accent, Font = new Font("Segoe UI", 20F, FontStyle.Bold) };
        value.Text = "0 мин"; value.Dock = DockStyle.Fill; value.ForeColor = accent; value.Font = new Font("Segoe UI", 19F, FontStyle.Bold); value.AutoEllipsis = true; value.TextAlign = ContentAlignment.MiddleLeft;
        card.Controls.Add(value); card.Controls.Add(icon); card.Controls.Add(caption);
        return card;
    }

    void ToggleTracking()
    {
        if (tracking)
        {
            TickUsage();
            timer.Stop(); tracking = false; lastApp = null;
            toggle.Text = "▶  Начать учёт"; toggle.BackColor = Color.FromArgb(103, 81, 245);
            status.Text = "●  Учёт остановлен"; status.ForeColor = Color.FromArgb(138, 151, 173);
            activeStatus.Text = "Остановлен";
        }
        else
        {
            tracking = true; lastTick = DateTime.Now; lastApp = ForegroundApp(); timer.Start();
            toggle.Text = "■  Остановить учёт"; toggle.BackColor = Color.FromArgb(185, 55, 86);
            status.Text = "●  Учёт активен"; status.ForeColor = Color.FromArgb(45, 214, 160);
            activeStatus.Text = "Активен";
        }
    }

    string ForegroundApp()
    {
        uint pid;
        GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        if (pid == 0) return null;
        if (pid == (uint)Process.GetCurrentProcess().Id) return null;
        IntPtr process = OpenProcess(QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return "Процесс " + pid;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : "Процесс " + pid;
        }
        finally { CloseHandle(process); }
    }

    void TickUsage()
    {
        try
        {
            DateTime now = DateTime.Now;
            int seconds = Math.Max(0, Math.Min(15, (int)(now - lastTick).TotalSeconds));
            if (!String.IsNullOrEmpty(lastApp) && seconds > 0) AppendUsage(lastApp, now.AddSeconds(-seconds), seconds);
            lastApp = ForegroundApp();
            lastTick = now;
            RefreshLists();
        }
        catch (Exception ex)
        {
            timer.Stop(); tracking = false; lastApp = null;
            toggle.Text = "▶  Начать учёт"; toggle.BackColor = Color.FromArgb(103, 81, 245);
            status.Text = "●  Ошибка учёта"; status.ForeColor = Color.FromArgb(248, 113, 113);
            activeStatus.Text = "Ошибка";
            MessageBox.Show(this, "Не удалось сохранить или прочитать статистику.\n\n" + ex.Message + "\n\nПапка данных: " + Path.GetDirectoryName(dataFile), "Ошибка учёта", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void SaveLast()
    {
        if (tracking && !String.IsNullOrEmpty(lastApp))
        {
            int seconds = Math.Max(0, Math.Min(15, (int)(DateTime.Now - lastTick).TotalSeconds));
            AppendUsage(lastApp, DateTime.Now.AddSeconds(-seconds), seconds);
        }
    }

    void AppendUsage(string app, DateTime start, int seconds)
    {
        if (seconds <= 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dataFile));
        string safe = app.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        File.AppendAllText(dataFile, start.ToString("o") + "\t" + seconds + "\t" + safe + Environment.NewLine, Encoding.UTF8);
    }

    Dictionary<string, int> Totals(DateTime since)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(dataFile)) return result;
        foreach (string line in File.ReadAllLines(dataFile, Encoding.UTF8))
        {
            string[] p = line.Split(new char[] { '\t' }, 3);
            DateTime start; int seconds;
            if (p.Length != 3 || !DateTime.TryParse(p[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out start) || !Int32.TryParse(p[1], out seconds)) continue;
            DateTime end = start.AddSeconds(seconds);
            int overlap = Math.Max(0, (int)(end - (start > since ? start : since)).TotalSeconds);
            if (overlap > 0) { if (!result.ContainsKey(p[2])) result[p[2]] = 0; result[p[2]] += overlap; }
        }
        return result;
    }

    void RefreshLists()
    {
        var daily = Totals(DateTime.Today);
        int sum = 0; string best = "Пока нет данных"; int bestSeconds = 0;
        foreach (var row in daily) { sum += row.Value; if (row.Value > bestSeconds) { best = row.Key; bestSeconds = row.Value; } }
        todayTotal.Text = FormatTime(sum); topApp.Text = DisplayApp(best); activeStatus.Text = tracking ? "Активен" : "Остановлен";
        ranking.BeginUpdate(); ranking.Items.Clear();
        var rows = new List<KeyValuePair<string, int>>(Totals(DateTime.Now.AddDays(-7)));
        rows.Sort((a, b) => b.Value.CompareTo(a.Value));
        int limit = Math.Min(7, rows.Count);
        for (int i = 0; i < limit; i++) ranking.Items.Add(new ListViewItem(new string[] { "  " + DisplayApp(rows[i].Key), FormatTime(rows[i].Value) }));
        if (limit == 0) ranking.Items.Add(new ListViewItem(new string[] { "  Данных пока нет", "—" }));
        ranking.EndUpdate();
        RefreshApplications(rows);
        if (chart != null) chart.Invalidate();
    }

    string DisplayApp(string key)
    {
        if (String.IsNullOrEmpty(key)) return "Пока нет данных";
        if (key.StartsWith("Процесс ", StringComparison.OrdinalIgnoreCase)) return key;
        return Path.GetFileNameWithoutExtension(key);
    }

    void RefreshApplications(List<KeyValuePair<string, int>> rows)
    {
        applications.BeginUpdate(); applications.Items.Clear();
        foreach (var row in rows)
        {
            string key = row.Key;
            if (!iconKeys.Contains(key))
            {
                Image image = null;
                string exePath = ResolveExecutable(key);
                try { if (!String.IsNullOrEmpty(exePath)) { using (Icon icon = Icon.ExtractAssociatedIcon(exePath)) if (icon != null) image = icon.ToBitmap(); } } catch { }
                if (image == null) image = SystemIcons.Application.ToBitmap();
                applicationIcons.Images.Add(key, image); iconKeys.Add(key);
            }
            var item = new ListViewItem(new string[] { "  " + DisplayApp(key), FormatTime(row.Value) });
            item.ImageKey = key;
            applications.Items.Add(item);
        }
        if (rows.Count == 0) applications.Items.Add(new ListViewItem(new string[] { "Пока нет данных", "—" }));
        applications.EndUpdate();
    }

    string ResolveExecutable(string key)
    {
        if (File.Exists(key)) return key;
        string exeName = Path.GetFileNameWithoutExtension(key);
        try
        {
            foreach (Process process in Process.GetProcessesByName(exeName))
            {
                try { string path = process.MainModule.FileName; if (File.Exists(path)) return path; }
                catch { }
                finally { process.Dispose(); }
            }
        }
        catch { }
        string fileName = exeName + ".exe";
        RegistryView[] views = { RegistryView.Registry64, RegistryView.Registry32 };
        RegistryHive[] hives = { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        foreach (RegistryView view in views)
        foreach (RegistryHive hive in hives)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey app = root.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\" + fileName))
                {
                    if (app == null) continue;
                    string value = Convert.ToString(app.GetValue(null));
                    if (String.IsNullOrEmpty(value)) continue;
                    value = value.Trim().Trim('"');
                    if (File.Exists(value)) return value;
                }
            }
            catch { }
        }
        return null;
    }

    void DrawWeeklyChart(object sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(20, 29, 49));
        int left = 44, right = 18, top = 18, bottom = 34;
        int w = Math.Max(1, chart.ClientSize.Width - left - right), h = Math.Max(1, chart.ClientSize.Height - top - bottom);
        var daily = new int[7]; var labels = new string[7]; DateTime today = DateTime.Today;
        for (int i = 0; i < 7; i++) { DateTime d = today.AddDays(i - 6); labels[i] = d.ToString("ddd"); daily[i] = TotalForDay(d); }
        int max = 3600; foreach (int n in daily) if (n > max) max = n;
        int ceilHours = Math.Max(1, (int)Math.Ceiling(max / 3600.0)); max = ceilHours * 3600;
        using (var grid = new Pen(Color.FromArgb(43, 55, 78)))
        using (var text = new SolidBrush(Color.FromArgb(125, 141, 166)))
        using (var bar = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, top, 1, h), Color.FromArgb(32, 220, 210), Color.FromArgb(110, 84, 248), 90F))
        using (var font = new Font("Segoe UI", 8F))
        {
            for (int line = 0; line <= 4; line++)
            {
                int y = top + h - (h * line / 4); g.DrawLine(grid, left, y, left + w, y);
                string label = (ceilHours * line / 4) + "ч"; g.DrawString(label, font, text, 4, y - 7);
            }
            float slot = (float)w / 7F; int bw = Math.Max(14, (int)(slot * .48F));
            for (int i = 0; i < 7; i++)
            {
                int bh = (int)(h * daily[i] / (float)max); int x = left + (int)(i * slot + (slot - bw) / 2); int y = top + h - bh;
                if (bh > 0) { var rect = new Rectangle(x, y, bw, bh); using (var path = RoundedRect(rect, 6)) g.FillPath(bar, path); }
                g.DrawString(labels[i], font, text, x - 3, top + h + 9);
            }
        }
    }

    System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2; var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
    }

    int TotalForDay(DateTime day)
    {
        int result = 0;
        if (!File.Exists(dataFile)) return 0;
        DateTime dayEnd = day.AddDays(1);
        foreach (string line in File.ReadAllLines(dataFile, Encoding.UTF8))
        {
            string[] p = line.Split(new char[] { '\t' }, 3);
            DateTime start; int seconds;
            if (p.Length != 3 || !DateTime.TryParse(p[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out start) || !Int32.TryParse(p[1], out seconds)) continue;
            DateTime end = start.AddSeconds(seconds);
            DateTime overlapStart = start > day ? start : day;
            DateTime overlapEnd = end < dayEnd ? end : dayEnd;
            if (overlapEnd > overlapStart) result += (int)(overlapEnd - overlapStart).TotalSeconds;
        }
        return result;
    }

    string FormatTime(int seconds)
    {
        int hours = seconds / 3600, minutes = (seconds % 3600) / 60;
        return hours > 0 ? hours + " ч " + minutes.ToString("00") + " мин" : minutes + " мин";
    }
}

