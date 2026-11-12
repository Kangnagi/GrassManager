// Grass Widget — 바탕화면에서 이번 주(일~토) GitHub 잔디를 보여 주는 가벼운 위젯
// 빌드: build.bat (Windows에 기본으로 있는 .NET Framework 4의 csc.exe 사용, 따로 설치할 것 없음)
//
// CPU를 아끼기 위한 원칙
// - 애니메이션·주기적 다시 그리기 없음: 데이터가 바뀌거나 날짜가 넘어갈 때만 그린다
// - 타이머는 하나뿐이고 30분(실패 시 5분)마다 또는 자정에 한 번 깨어난다
// - 네트워크 요청은 백그라운드 스레드에서 해서 화면이 멈추지 않게 한다
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "GrassWidget-single-instance", out created))
        {
            if (!created) return; // 이미 실행 중이면 하나만 띄운다
            SetProcessDPIAware();
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2 (GitHub 필수)
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new GrassWidget());
        }
    }
}

// 설정: %APPDATA%\GrassWidget\config.ini (user=아이디, x/y=위치, web=더블클릭 시 열 주소)
class Config
{
    public string User = "Kangnagi";
    public string Web = "https://kangnagi.github.io/GrassManager/";
    public int X = int.MinValue, Y = int.MinValue;

    static string Dir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrassWidget"); }
    }

    public static Config Load()
    {
        var c = new Config();
        try
        {
            foreach (var line in File.ReadAllLines(Path.Combine(Dir, "config.ini"), Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i < 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                int n;
                if (k == "user" && v.Length > 0) c.User = v;
                else if (k == "web" && v.Length > 0) c.Web = v;
                else if (k == "x" && int.TryParse(v, out n)) c.X = n;
                else if (k == "y" && int.TryParse(v, out n)) c.Y = n;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return c;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "config.ini"),
                "user=" + User + "\r\nweb=" + Web + "\r\nx=" + X + "\r\ny=" + Y + "\r\n", Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

class Day
{
    public int Level, Count;
}

// GitHub 공개 잔디 페이지(github.com/users/{아이디}/contributions)에서 날짜별 단계·기여 수를 읽는다.
// 토큰이 필요 없고, 프로필의 "Private contributions" 설정을 켜 두면 비공개 저장소 기여도 숫자로 잡힌다.
static class GitHub
{
    static readonly Regex CellRe = new Regex("<td[^>]*\\bdata-date=\"[^>]*>");
    static readonly Regex TipRe = new Regex("<tool-tip[^>]*\\bfor=\"([^\"]+)\"[^>]*>([^<]*)</tool-tip>");
    static readonly Regex NumRe = new Regex("^\\d[\\d,]*");

    static string Attr(string tag, string name)
    {
        var m = Regex.Match(tag, "\\b" + name + "=\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static Dictionary<DateTime, Day> Fetch(string user)
    {
        string html;
        using (var wc = new WebClient())
        {
            wc.Encoding = Encoding.UTF8;
            wc.Headers[HttpRequestHeader.UserAgent] = "GrassWidget";
            html = wc.DownloadString("https://github.com/users/" + Uri.EscapeDataString(user) + "/contributions");
        }

        // 툴팁 문장("3 contributions on …", "No contributions on …")에서 칸 id별 기여 수
        var counts = new Dictionary<string, int>();
        foreach (Match m in TipRe.Matches(html))
        {
            var num = NumRe.Match(m.Groups[2].Value.Trim());
            int n = 0;
            if (num.Success) int.TryParse(num.Value.Replace(",", ""), out n);
            counts[m.Groups[1].Value] = n;
        }

        var days = new Dictionary<DateTime, Day>();
        foreach (Match m in CellRe.Matches(html))
        {
            DateTime d;
            if (!DateTime.TryParseExact(Attr(m.Value, "data-date"), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) continue;
            var day = new Day();
            int.TryParse(Attr(m.Value, "data-level") ?? "0", out day.Level);
            string id = Attr(m.Value, "id");
            int c;
            day.Count = id != null && counts.TryGetValue(id, out c) ? c : (day.Level > 0 ? 1 : 0);
            days[d] = day;
        }
        if (days.Count == 0) throw new InvalidDataException("잔디 칸을 찾지 못함");
        return days;
    }
}

class GrassWidget : Form
{
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int RefreshMs = 30 * 60 * 1000, RetryMs = 5 * 60 * 1000;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    const int GWL_HWNDPARENT = -8;

    [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLongPtr64(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLong32(IntPtr h, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);

    // GitHub 다크 테마 색
    static readonly Color Bg = Color.FromArgb(13, 17, 23);
    static readonly Color Line = Color.FromArgb(48, 54, 61);
    static readonly Color Fg = Color.FromArgb(230, 237, 243);
    static readonly Color Muted = Color.FromArgb(125, 133, 144);
    static readonly Color Ok = Color.FromArgb(57, 211, 83);
    static readonly Color Warn = Color.FromArgb(240, 136, 62);
    static readonly Color FutureCell = Color.FromArgb(22, 27, 34);
    static readonly Color[] Levels =
    {
        Color.FromArgb(33, 38, 45), Color.FromArgb(14, 68, 41), Color.FromArgb(0, 109, 50),
        Color.FromArgb(38, 166, 65), Color.FromArgb(57, 211, 83)
    };
    static readonly string[] DayNames = { "일", "월", "화", "수", "목", "금", "토" };
    static readonly Day Empty = new Day();

    readonly Config cfg;
    readonly float scale;
    readonly Font fTitle, fSmall, fSmallBold, fCell;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly ToolTip tip = new ToolTip();
    readonly ToolStripMenuItem autoStartItem;
    Dictionary<DateTime, Day> data;
    DateTime lastOk;
    string error;
    bool loading, drawOwnBorder = true;
    bool dragging, moved;
    Point dragOffset;
    string tipText;

    int S(float v) { return (int)Math.Round(v * scale); }

    public GrassWidget()
    {
        cfg = Config.Load();
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

        Text = "Grass Widget";
        FormBorderStyle = FormBorderStyle.None;
        // ShowInTaskbar는 기본값(true) 그대로 둔다: false면 WinForms가 숨은 소유 창을 붙여 바탕화면 고정이 풀린다.
        // 작업 표시줄에는 WS_EX_TOOLWINDOW 덕분에 어차피 나타나지 않는다.
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        DoubleBuffered = true;
        ClientSize = new Size(S(236), S(106));
        Location = StartLocation();

        fTitle = new Font("Malgun Gothic", 9.5f, FontStyle.Bold);
        fSmall = new Font("Malgun Gothic", 8f);
        fSmallBold = new Font("Malgun Gothic", 8f, FontStyle.Bold);
        fCell = new Font("Segoe UI", 7.5f, FontStyle.Bold);

        var menu = new ContextMenuStrip();
        menu.Items.Add("새로고침", null, delegate { RefreshData(); });
        menu.Items.Add("웹에서 열기", null, delegate { OpenWeb(); });
        menu.Items.Add("GitHub 아이디 변경…", null, delegate { ChangeUser(); });
        menu.Items.Add(new ToolStripSeparator());
        autoStartItem = new ToolStripMenuItem("Windows 시작 시 자동 실행");
        autoStartItem.Click += delegate { AutoStart = !AutoStart; };
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, delegate { Close(); });
        menu.Opening += delegate { autoStartItem.Checked = AutoStart; };
        ContextMenuStrip = menu;

        tip.ShowAlways = true; // 활성 창이 아니어도 툴팁 표시
        tip.InitialDelay = 300;
        timer.Tick += delegate { RefreshData(); };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    // 바탕화면(Progman) 소유 창 + 도구 창 + 포커스 안 뺏기:
    // 작업 표시줄·Alt+Tab에 안 뜨고, 다른 창 위로 튀어나오지 않는다
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    // 소유자를 바탕화면으로 바꿔 Win+D(바탕화면 보기)에도 숨지 않게 한다.
    // WinForms가 창을 보여 줄 때 소유자를 다시 정하므로 보인 뒤(OnShown)에 호출한다.
    void PinToDesktop()
    {
        var desktop = FindWindow("Progman", null);
        if (desktop == IntPtr.Zero) return;
        if (IntPtr.Size == 8) SetWindowLongPtr64(Handle, GWL_HWNDPARENT, desktop);
        else SetWindowLong32(Handle, GWL_HWNDPARENT, desktop.ToInt32());
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    Point StartLocation()
    {
        var p = new Point(cfg.X, cfg.Y);
        foreach (var sc in Screen.AllScreens)
            if (sc.WorkingArea.Contains(p.X + S(20), p.Y + S(20))) return p;
        var wa = Screen.PrimaryScreen.WorkingArea; // 처음이거나 모니터가 바뀌면 오른쪽 위
        return new Point(wa.Right - Width - S(24), wa.Top + S(24));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Windows 11: 둥근 모서리 + 테두리 색 (Windows 10에서는 실패하고 직접 테두리를 그림)
        int round = 2, border = Line.R | (Line.G << 8) | (Line.B << 16);
        if (DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0 &&
            DwmSetWindowAttribute(Handle, 34, ref border, 4) == 0) drawOwnBorder = false;
        PinToDesktop();
        SetWindowPos(Handle, (IntPtr)1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // 맨 아래로 (NOSIZE|NOMOVE|NOACTIVATE)
        RefreshData();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged; // 정적 이벤트라 꼭 해제
        timer.Dispose();
        base.OnFormClosed(e);
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        // 절전에서 깨어나면 네트워크가 붙을 시간을 조금 두고 새로고침
        BeginInvoke((MethodInvoker)delegate { Schedule(10 * 1000); });
    }

    // ── 데이터 ─────────────────────────────────────────

    void RefreshData()
    {
        timer.Stop();
        if (loading) return;
        loading = true;
        string user = cfg.User;
        ThreadPool.QueueUserWorkItem(delegate
        {
            Dictionary<DateTime, Day> result = null;
            string err = null;
            try { result = GitHub.Fetch(user); }
            catch (WebException ex)
            {
                var res = ex.Response as HttpWebResponse;
                err = res != null && res.StatusCode == HttpStatusCode.NotFound ? "아이디를 찾을 수 없음" : "연결 실패";
            }
            catch (Exception) { err = "읽기 실패"; }
            try { BeginInvoke((MethodInvoker)delegate { Apply(result, err); }); }
            catch (InvalidOperationException) { } // 그 사이 창이 닫힘
        });
    }

    void Apply(Dictionary<DateTime, Day> result, string err)
    {
        loading = false;
        if (result != null)
        {
            data = result;
            lastOk = DateTime.Now;
            error = null;
        }
        else error = err;
        Invalidate();
        Schedule(result != null ? RefreshMs : RetryMs);
        // 받아 온 HTML(약 200KB)을 정리하고 작업 메모리를 돌려준다
        GC.Collect();
        EmptyWorkingSet(Process.GetCurrentProcess().Handle);
    }

    // 다음 새로고침: 정해진 간격과 자정 중 빠른 쪽 (자정이 지나면 오늘 칸이 바뀌므로)
    void Schedule(int ms)
    {
        var now = DateTime.Now;
        double toMidnight = (now.Date.AddDays(1) - now).TotalMilliseconds + 5000;
        timer.Stop();
        timer.Interval = (int)Math.Max(1000, Math.Min(ms, toMidnight));
        timer.Start();
    }

    Day Get(DateTime d)
    {
        Day day;
        return data != null && data.TryGetValue(d, out day) ? day : Empty;
    }

    // 연속 기록: 오늘 아직 없으면 어제부터 센다
    int Streak()
    {
        var d = DateTime.Today;
        if (Get(d).Count == 0) d = d.AddDays(-1);
        int n = 0;
        while (Get(d).Count > 0) { n++; d = d.AddDays(-1); }
        return n;
    }

    // ── 그리기 ─────────────────────────────────────────

    int CellSize { get { return S(24); } }
    int CellGap { get { return S(6); } }
    int CellLeft { get { return (ClientSize.Width - (7 * CellSize + 6 * CellGap)) / 2; } }
    int CellTop { get { return S(34); } }

    Rectangle CellRect(int i)
    {
        return new Rectangle(CellLeft + i * (CellSize + CellGap), CellTop, CellSize, CellSize);
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void DrawText(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags align)
    {
        TextRenderer.DrawText(g, text, font, r, color,
            align | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = ClientSize.Width, pad = S(12);
        var today = DateTime.Today;
        var sunday = today.AddDays(-(int)today.DayOfWeek);

        // 제목 줄: 이번 주 잔디 | 연속 N일
        DrawText(g, "이번 주 잔디", fTitle, Fg, new Rectangle(pad, S(9), w - pad * 2, S(18)), TextFormatFlags.Left);
        if (data != null)
            DrawText(g, "연속 " + Streak() + "일", fSmallBold, Ok, new Rectangle(pad, S(11), w - pad * 2, S(16)), TextFormatFlags.Right);

        // 일~토 7칸
        int active = 0;
        for (int i = 0; i < 7; i++)
        {
            var d = sunday.AddDays(i);
            var r = CellRect(i);
            var day = Get(d);
            bool future = d > today, isToday = d == today;
            if (!future && day.Count > 0) active++;

            using (var path = RoundRect(r, S(5)))
            {
                using (var b = new SolidBrush(future ? FutureCell : Levels[Math.Max(0, Math.Min(4, day.Level))]))
                    g.FillPath(b, path);
                if (isToday)
                    using (var pen = new Pen(Fg, Math.Max(1, S(1.5f))))
                        g.DrawPath(pen, path);
            }
            if (!future && day.Count > 0)
                DrawText(g, day.Count > 99 ? "99+" : day.Count.ToString(), fCell,
                    day.Level >= 3 ? Bg : Fg, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            DrawText(g, DayNames[i], isToday ? fSmallBold : fSmall, isToday ? Fg : Muted,
                new Rectangle(r.X - S(4), r.Bottom + S(3), r.Width + S(8), S(15)), TextFormatFlags.HorizontalCenter);
        }

        // 상태 줄: 오늘 기여 여부 | 이번 주 활동일
        string status;
        Color color;
        if (data == null)
        {
            status = error ?? "불러오는 중…";
            color = error != null ? Warn : Muted;
        }
        else
        {
            int t = Get(today).Count;
            status = t > 0 ? "오늘 " + t + "개 기여 완료" : "오늘 아직 커밋 없음";
            color = t > 0 ? Ok : Warn;
            if (error != null) { status += " · " + error; color = Muted; }
        }
        var bottom = new Rectangle(pad, S(83), w - pad * 2, S(16));
        DrawText(g, status, fSmallBold, color, new Rectangle(bottom.X, bottom.Y, bottom.Width - S(44), bottom.Height), TextFormatFlags.Left);
        if (data != null)
            DrawText(g, active + "/7일", fSmall, Muted, bottom, TextFormatFlags.Right);

        if (drawOwnBorder)
            using (var pen = new Pen(Line))
                g.DrawRectangle(pen, 0, 0, w - 1, ClientSize.Height - 1);
    }

    // ── 마우스: 끌어서 이동, 더블클릭으로 웹 열기, 칸 위에 올리면 날짜·기여 수 ──

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        dragging = true;
        moved = false;
        dragOffset = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging && (MouseButtons & MouseButtons.Left) != 0)
        {
            if (!moved && Math.Abs(e.X - dragOffset.X) + Math.Abs(e.Y - dragOffset.Y) < S(4)) return;
            moved = true;
            var p = Cursor.Position;
            Location = new Point(p.X - dragOffset.X, p.Y - dragOffset.Y);
            return;
        }
        UpdateTip(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragging && moved)
        {
            cfg.X = Left;
            cfg.Y = Top;
            cfg.Save();
        }
        dragging = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && !moved) OpenWeb();
    }

    void UpdateTip(Point pt)
    {
        string text = null;
        var sunday = DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek);
        for (int i = 0; i < 7; i++)
        {
            if (!CellRect(i).Contains(pt)) continue;
            var d = sunday.AddDays(i);
            text = d.Month + "월 " + d.Day + "일 (" + DayNames[i] + ") · " +
                (d > DateTime.Today ? "아직" : Get(d).Count + "개 기여");
        }
        if (text == null)
            text = (data != null ? "업데이트 " + lastOk.ToString("HH:mm") + " · " : "") + "더블클릭: 웹 열기 · 우클릭: 메뉴";
        if (text == tipText) return; // 같은 내용이면 툴팁을 다시 만들지 않음
        tipText = text;
        tip.SetToolTip(this, text);
    }

    // ── 메뉴 동작 ───────────────────────────────────────

    void OpenWeb()
    {
        try { Process.Start(cfg.Web); }
        catch (Exception) { }
    }

    void ChangeUser()
    {
        string v = Microsoft.VisualBasic.Interaction.InputBox("잔디를 볼 GitHub 아이디", "Grass Widget", cfg.User).Trim();
        if (v.Length == 0 || v == cfg.User) return;
        cfg.User = v;
        cfg.Save();
        data = null;
        error = null;
        Invalidate();
        RefreshData();
    }

    static bool AutoStart
    {
        get
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("GrassWidget") != null;
        }
        set
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (value) k.SetValue("GrassWidget", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("GrassWidget", false);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            fTitle.Dispose(); fSmall.Dispose(); fSmallBold.Dispose(); fCell.Dispose();
            tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
