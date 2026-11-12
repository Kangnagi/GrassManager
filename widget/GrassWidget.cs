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

// 설정: %APPDATA%\GrassWidget\config.ini
// (user=아이디, web=홈페이지 주소, x/y=위치, zoom=크기 배율, hidden=숨김 상태)
class Config
{
    public const float MinZoom = 0.75f, MaxZoom = 2f;
    public string User = "Kangnagi";
    public string Web = "https://kangnagi.github.io/GrassManager/";
    public int X = int.MinValue, Y = int.MinValue;
    public float Zoom = 1f;
    public bool Hidden;

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
                float f;
                if (k == "user" && v.Length > 0) c.User = v;
                else if (k == "web" && v.Length > 0) c.Web = v;
                else if (k == "x" && int.TryParse(v, out n)) c.X = n;
                else if (k == "y" && int.TryParse(v, out n)) c.Y = n;
                else if (k == "zoom" && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                    c.Zoom = Math.Max(MinZoom, Math.Min(MaxZoom, f));
                else if (k == "hidden") c.Hidden = v == "1";
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
                "user=" + User + "\r\nweb=" + Web + "\r\nx=" + X + "\r\ny=" + Y +
                "\r\nzoom=" + Zoom.ToString("0.00", CultureInfo.InvariantCulture) +
                "\r\nhidden=" + (Hidden ? "1" : "0") + "\r\n", Encoding.UTF8);
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

// 메뉴 아이콘: 16칸 좌표에 선으로 그려 DPI에 맞게 비트맵으로 만든다 (이미지 파일 없이)
static class Icons
{
    static readonly Color Ink = Color.FromArgb(87, 96, 106);

    public static Bitmap Make(string kind, int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        using (var pen = new Pen(Ink, 1.4f))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(size / 16f, size / 16f);
            pen.StartCap = pen.EndCap = LineCap.Round;
            switch (kind)
            {
                case "move": // 사방 화살표
                    g.DrawLine(pen, 8, 2, 8, 14);
                    g.DrawLine(pen, 2, 8, 14, 8);
                    g.DrawLines(pen, new[] { new PointF(6, 4), new PointF(8, 2), new PointF(10, 4) });
                    g.DrawLines(pen, new[] { new PointF(6, 12), new PointF(8, 14), new PointF(10, 12) });
                    g.DrawLines(pen, new[] { new PointF(4, 6), new PointF(2, 8), new PointF(4, 10) });
                    g.DrawLines(pen, new[] { new PointF(12, 6), new PointF(14, 8), new PointF(12, 10) });
                    break;
                case "hide": // 눈에 빗금
                    g.DrawArc(pen, 1.5f, 4, 13, 12, 200, 140);
                    g.DrawArc(pen, 1.5f, 0, 13, 12, 20, 140);
                    g.DrawEllipse(pen, 6, 6, 4, 4);
                    g.DrawLine(pen, 3, 13, 13, 3);
                    break;
                case "show": // 눈
                    g.DrawArc(pen, 1.5f, 4, 13, 12, 200, 140);
                    g.DrawArc(pen, 1.5f, 0, 13, 12, 20, 140);
                    g.DrawEllipse(pen, 6, 6, 4, 4);
                    break;
                case "user": // 사람
                    g.DrawEllipse(pen, 5, 2, 6, 6);
                    g.DrawArc(pen, 2.5f, 9.5f, 11, 9, 180, 180);
                    break;
                case "refresh": // 원형 화살표
                    g.DrawArc(pen, 2.5f, 2.5f, 11, 11, -60, 300);
                    g.DrawLines(pen, new[] { new PointF(10.5f, 1.5f), new PointF(11.2f, 3.8f), new PointF(8.8f, 4.6f) });
                    break;
                case "power": // 전원
                    g.DrawArc(pen, 2.5f, 3, 11, 11, -50, 280);
                    g.DrawLine(pen, 8, 1.5f, 8, 7.5f);
                    break;
            }
        }
        return bmp;
    }
}

// 밝은 팝업 메뉴 색 (첨부 이미지처럼 흰 바탕 + 연한 회색 강조)
class LightMenuColors : ProfessionalColorTable
{
    static readonly Color White = Color.White, Hover = Color.FromArgb(240, 242, 245), Edge = Color.FromArgb(222, 225, 230);
    public override Color ToolStripDropDownBackground { get { return White; } }
    public override Color ImageMarginGradientBegin { get { return White; } }
    public override Color ImageMarginGradientMiddle { get { return White; } }
    public override Color ImageMarginGradientEnd { get { return White; } }
    public override Color MenuBorder { get { return Edge; } }
    public override Color MenuItemBorder { get { return Hover; } }
    public override Color MenuItemSelected { get { return Hover; } }
    public override Color SeparatorDark { get { return Edge; } }
    public override Color SeparatorLight { get { return White; } }
    public override Color CheckBackground { get { return Hover; } }
    public override Color CheckSelectedBackground { get { return Edge; } }
    public override Color CheckPressedBackground { get { return Edge; } }
}

// 기본 렌더러는 화면 배율이 클 때 아이콘·체크·글씨의 세로 위치가 서로 어긋나므로
// 셋 다 항목 높이의 가운데에 직접 그린다
class LightMenuRenderer : ToolStripProfessionalRenderer
{
    static readonly Color Ink = Color.FromArgb(87, 96, 106);
    static readonly Color Text = Color.FromArgb(31, 35, 40);

    public LightMenuRenderer() : base(new LightMenuColors()) { RoundedEdges = false; }

    // 아이콘 칸의 가로 가운데
    static int IconCenterX(ToolStripItemImageRenderEventArgs e)
    {
        return e.ImageRectangle.X + e.ImageRectangle.Width / 2;
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image == null) return;
        int w = e.Image.Width, h = e.Image.Height;
        e.Graphics.DrawImage(e.Image, IconCenterX(e) - w / 2, (e.Item.Height - h) / 2, w, h);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        float s = e.Item.Height / 32f, cx = IconCenterX(e), cy = e.Item.Height / 2f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Ink, 1.8f * s))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            g.DrawLines(pen, new[] { new PointF(cx - 5 * s, cy), new PointF(cx - 1.5f * s, cy + 3.5f * s), new PointF(cx + 5 * s, cy - 4 * s) });
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var r = new Rectangle(e.TextRectangle.X, 0, e.Item.Width - e.TextRectangle.X, e.Item.Height);
        // 강조된 줄도 바탕이 연한 회색이라 글씨는 항상 진한 색 (기본값은 흰 글씨로 바뀜)
        var color = e.Item.Enabled ? Text : Color.FromArgb(160, 166, 173);
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

class GrassWidget : Form
{
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int GWL_HWNDPARENT = -8;
    const int RefreshMs = 30 * 60 * 1000, RetryMs = 5 * 60 * 1000;
    const int BaseW = 236, BaseH = 106, IconSize = 44; // 배율 1일 때 위젯·숨김 아이콘 크기
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

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
    static readonly Color HoverBg = Color.FromArgb(33, 38, 45);
    static readonly Color FutureCell = Color.FromArgb(22, 27, 34);
    static readonly Color[] Levels =
    {
        Color.FromArgb(33, 38, 45), Color.FromArgb(14, 68, 41), Color.FromArgb(0, 109, 50),
        Color.FromArgb(38, 166, 65), Color.FromArgb(57, 211, 83)
    };
    static readonly string[] DayNames = { "일", "월", "화", "수", "목", "금", "토" };
    static readonly Day Empty = new Day();

    readonly Config cfg;
    readonly float dpi;
    float zoom;
    Font fTitle, fSmall, fSmallBold, fCell;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly ToolTip tip = new ToolTip();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly ToolStripMenuItem editItem, hideItem, autoStartItem;
    readonly Image hideIcon, showIcon;
    Dictionary<DateTime, Day> data;
    DateTime lastOk;
    string error, tipText;
    bool loading, drawOwnBorder = true;

    // 화면 상태: 기본(고정) / 위치·크기 편집 / 숨김(아이콘)
    bool editing;
    bool dragging, resizing, moved, btnHover, justExpanded;
    Point dragOffset, resizeStart;
    int resizeStartW;

    int S(float v) { return (int)Math.Round(v * dpi * zoom); } // 위젯 안쪽: 화면 배율 × 사용자 크기
    int D(float v) { return (int)Math.Round(v * dpi); }        // 메뉴·숨김 아이콘: 화면 배율만

    int WidgetW { get { return S(BaseW); } }
    int WidgetH { get { return S(BaseH); } }

    public GrassWidget()
    {
        cfg = Config.Load();
        zoom = cfg.Zoom;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX / 96f;

        Text = "Grass Widget";
        // 창 아이콘: exe에 넣은 잔디 매니저 아이콘(build.bat의 /win32icon)을 그대로 쓴다
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        FormBorderStyle = FormBorderStyle.None;
        // ShowInTaskbar는 기본값(true) 그대로 둔다: false면 WinForms가 숨은 소유 창을 붙여 바탕화면 고정이 풀린다.
        // 작업 표시줄에는 WS_EX_TOOLWINDOW 덕분에 어차피 나타나지 않는다.
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        DoubleBuffered = true;
        CreateFonts();

        var start = StartLocation();
        cfg.X = start.X;
        cfg.Y = start.Y;
        ApplyLayout();

        // 설정 메뉴 (≡ 버튼, 우클릭 공통)
        int icon = D(16);
        hideIcon = Icons.Make("hide", icon);
        showIcon = Icons.Make("show", icon);
        menu.Renderer = new LightMenuRenderer();
        menu.Font = new Font("Malgun Gothic", 9.5f);
        menu.ImageScalingSize = new Size(icon, icon);
        menu.Padding = new Padding(0, D(4), 0, D(4));
        editItem = AddItem("위치 및 크기 조절", Icons.Make("move", icon), delegate { StartEdit(); });
        hideItem = AddItem("위젯 숨기기", hideIcon, delegate { SetHidden(!cfg.Hidden); });
        menu.Items.Add(new ToolStripSeparator());
        AddItem("계정 설정", Icons.Make("user", icon), delegate { OpenWeb(); });
        AddItem("새로고침", Icons.Make("refresh", icon), delegate { RefreshData(); });
        autoStartItem = AddItem("Windows 시작 시 자동 실행", null, delegate { AutoStart = !AutoStart; });
        menu.Items.Add(new ToolStripSeparator());
        AddItem("위젯 종료", Icons.Make("power", icon), delegate { Close(); });
        menu.Opening += delegate
        {
            autoStartItem.Checked = AutoStart;
            editItem.Enabled = !cfg.Hidden;
            hideItem.Text = cfg.Hidden ? "위젯 펼치기" : "위젯 숨기기";
            hideItem.Image = cfg.Hidden ? showIcon : hideIcon;
        };
        menu.Opened += delegate
        {
            int round = 2; // Windows 11 둥근 모서리
            DwmSetWindowAttribute(menu.Handle, 33, ref round, 4);
        };
        menu.Closed += delegate { if (btnHover) { btnHover = false; Invalidate(); } };
        ContextMenuStrip = menu;

        tip.ShowAlways = true; // 활성 창이 아니어도 툴팁 표시
        tip.InitialDelay = 300;
        timer.Tick += delegate { RefreshData(); };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    ToolStripMenuItem AddItem(string text, Image image, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text, image, onClick);
        item.Padding = new Padding(D(4), D(5), D(12), D(5));
        menu.Items.Add(item);
        return item;
    }

    void CreateFonts()
    {
        if (fTitle != null) { fTitle.Dispose(); fSmall.Dispose(); fSmallBold.Dispose(); fCell.Dispose(); }
        fTitle = new Font("Malgun Gothic", 9.5f * zoom, FontStyle.Bold);
        fSmall = new Font("Malgun Gothic", 8f * zoom);
        fSmallBold = new Font("Malgun Gothic", 8f * zoom, FontStyle.Bold);
        fCell = new Font("Segoe UI", 7.5f * zoom, FontStyle.Bold);
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

    protected override bool ShowWithoutActivation { get { return true; } }

    // 소유자를 바탕화면으로 바꿔 Win+D(바탕화면 보기)에도 숨지 않게 한다.
    // WinForms가 창을 보여 줄 때 소유자를 다시 정하므로 보인 뒤(OnShown)에 호출한다.
    void PinToDesktop()
    {
        var desktop = FindWindow("Progman", null);
        if (desktop == IntPtr.Zero) return;
        if (IntPtr.Size == 8) SetWindowLongPtr64(Handle, GWL_HWNDPARENT, desktop);
        else SetWindowLong32(Handle, GWL_HWNDPARENT, desktop.ToInt32());
    }

    Point StartLocation()
    {
        var p = new Point(cfg.X, cfg.Y);
        foreach (var sc in Screen.AllScreens)
            if (sc.WorkingArea.Contains(p.X + S(20), p.Y + S(20))) return p;
        var wa = Screen.PrimaryScreen.WorkingArea; // 처음이거나 모니터가 바뀌면 오른쪽 위
        return new Point(wa.Right - WidgetW - D(24), wa.Top + D(24));
    }

    // cfg.X/Y는 항상 '펼친 위젯'의 왼쪽 위. 숨김 아이콘은 위젯 오른쪽 위(설정 버튼 자리)에 둔다.
    void ApplyLayout()
    {
        if (cfg.Hidden)
        {
            ClientSize = new Size(D(IconSize), D(IconSize));
            Location = new Point(cfg.X + WidgetW - D(IconSize), cfg.Y);
        }
        else
        {
            ClientSize = new Size(WidgetW, WidgetH);
            Location = new Point(cfg.X, cfg.Y);
        }
        Invalidate();
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

    // ── 배치 ───────────────────────────────────────────

    Rectangle MenuButton { get { return new Rectangle(ClientSize.Width - S(30), S(7), S(22), S(22)); } }
    Rectangle Grip { get { return new Rectangle(ClientSize.Width - S(16), ClientSize.Height - S(16), S(14), S(14)); } }
    int CellSize { get { return S(24); } }
    int CellGap { get { return S(6); } }
    int CellLeft { get { return (ClientSize.Width - (7 * CellSize + 6 * CellGap)) / 2; } }
    int CellTop { get { return S(36); } }

    Rectangle CellRect(int i)
    {
        return new Rectangle(CellLeft + i * (CellSize + CellGap), CellTop, CellSize, CellSize);
    }

    // ── 그리기 ─────────────────────────────────────────

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void Fill(Graphics g, Color c, Rectangle r, int radius)
    {
        using (var path = RoundRect(r, radius))
        using (var b = new SolidBrush(c))
            g.FillPath(b, path);
    }

    void DrawText(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags align)
    {
        TextRenderer.DrawText(g, text, font, r, color,
            align | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    // 잔디 매니저 로고: 2×2 잔디 칸 (웹앱 상단 로고와 같은 색 배치)
    static void DrawLogo(Graphics g, int x, int y, int cell, int gap)
    {
        Color[] c = { Levels[2], Levels[4], Levels[3], Levels[1] };
        for (int i = 0; i < 4; i++)
            Fill(g, c[i], new Rectangle(x + (i % 2) * (cell + gap), y + (i / 2) * (cell + gap), cell, cell), Math.Max(1, cell / 4));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (cfg.Hidden) PaintIcon(g);
        else PaintWidget(g);
        if (drawOwnBorder)
            using (var pen = new Pen(Line))
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    // 숨김 상태: 로고 + 오늘 커밋 여부 점
    void PaintIcon(Graphics g)
    {
        int cell = D(10), gap = D(3), size = cell * 2 + gap;
        DrawLogo(g, (ClientSize.Width - size) / 2, (ClientSize.Height - size) / 2, cell, gap);
        if (data == null) return;
        int dot = D(8);
        using (var b = new SolidBrush(Get(DateTime.Today).Count > 0 ? Ok : Warn))
        using (var pen = new Pen(Bg, D(2)))
        {
            var r = new Rectangle(ClientSize.Width - dot - D(5), D(5), dot, dot);
            g.FillEllipse(b, r);
            g.DrawEllipse(pen, r);
        }
    }

    void PaintWidget(Graphics g)
    {
        int w = ClientSize.Width, pad = S(12);
        var today = DateTime.Today;
        var sunday = today.AddDays(-(int)today.DayOfWeek);
        var btn = MenuButton;

        // 제목 줄: 로고 이번 주 잔디 | 연속 N일 | ≡
        DrawLogo(g, pad, S(12), S(5), S(2));
        DrawText(g, "이번 주 잔디", fTitle, Fg, new Rectangle(pad + S(18), S(9), w - pad * 2, S(18)), TextFormatFlags.Left);
        if (data != null && !editing)
            DrawText(g, "연속 " + Streak() + "일", fSmallBold, Ok,
                new Rectangle(pad, S(11), btn.Left - S(6) - pad, S(16)), TextFormatFlags.Right);

        // 설정 버튼: 평소엔 ≡, 편집 중엔 ✓(완료)
        if (btnHover || editing) Fill(g, editing ? Levels[1] : HoverBg, btn, S(5));
        using (var pen = new Pen(editing ? Ok : (btnHover ? Fg : Muted), Math.Max(1f, S(1.6f))))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            float cx = btn.X + btn.Width / 2f, cy = btn.Y + btn.Height / 2f;
            if (editing)
                g.DrawLines(pen, new[] { new PointF(cx - S(5), cy), new PointF(cx - S(1.5f), cy + S(4)), new PointF(cx + S(5), cy - S(4)) });
            else
                for (int i = -1; i <= 1; i++)
                    g.DrawLine(pen, cx - S(5.5f), cy + i * S(4.5f), cx + S(5.5f), cy + i * S(4.5f));
        }

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

        // 상태 줄: 오늘 기여 여부 | 이번 주 활동일 (편집 중에는 사용법 안내)
        var bottom = new Rectangle(pad, S(84), w - pad * 2, S(16));
        if (editing)
        {
            DrawText(g, "끌어서 이동 · 모서리로 크기 조절", fSmall, Muted, bottom, TextFormatFlags.Left);
            using (var pen = new Pen(Ok, Math.Max(1, S(1.5f))) { DashStyle = DashStyle.Dash })
                g.DrawRectangle(pen, 1, 1, w - 3, ClientSize.Height - 3);
            using (var pen = new Pen(Muted, Math.Max(1, S(1.3f))))
            {
                var gr = Grip;
                for (int i = 1; i <= 3; i++)
                    g.DrawLine(pen, gr.Right - i * S(4), gr.Bottom, gr.Right, gr.Bottom - i * S(4));
            }
            return;
        }

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
        DrawText(g, status, fSmallBold, color, new Rectangle(bottom.X, bottom.Y, bottom.Width - S(44), bottom.Height), TextFormatFlags.Left);
        if (data != null)
            DrawText(g, active + "/7일", fSmall, Muted, bottom, TextFormatFlags.Right);
    }

    // ── 상태 전환 ───────────────────────────────────────

    void StartEdit()
    {
        if (cfg.Hidden) return;
        editing = true;
        Invalidate();
    }

    void EndEdit()
    {
        editing = false;
        cfg.Save();
        Cursor = Cursors.Default;
        Invalidate();
    }

    void SetHidden(bool hidden)
    {
        editing = false;
        cfg.Hidden = hidden;
        cfg.Save();
        Cursor = Cursors.Default;
        ApplyLayout();
    }

    void SetZoom(float z)
    {
        z = Math.Max(Config.MinZoom, Math.Min(Config.MaxZoom, z));
        if (Math.Abs(z - zoom) < 0.01f) return;
        zoom = cfg.Zoom = z;
        CreateFonts();
        ClientSize = new Size(WidgetW, WidgetH);
        Invalidate();
    }

    // ── 마우스 ─────────────────────────────────────────
    // 기본: 고정(움직이지 않음), ≡ 클릭 → 메뉴, 더블클릭 → 홈페이지
    // 편집: 끌어서 이동, 오른쪽 아래 모서리로 크기 조절, ✓ 클릭 → 완료
    // 숨김: 클릭 → 펼치기, 끌어서 아이콘 이동

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        moved = false;
        if (cfg.Hidden)
        {
            dragging = true;
            dragOffset = e.Location;
        }
        else if (MenuButton.Contains(e.Location))
        {
            if (editing) EndEdit();
            else menu.Show(this, new Point(MenuButton.Right, MenuButton.Bottom + D(4)), ToolStripDropDownDirection.BelowLeft);
        }
        else if (editing && Grip.Contains(e.Location))
        {
            resizing = true;
            resizeStart = Cursor.Position;
            resizeStartW = ClientSize.Width;
        }
        else if (editing)
        {
            dragging = true;
            dragOffset = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool left = (MouseButtons & MouseButtons.Left) != 0;
        if (resizing && left)
        {
            int dx = Cursor.Position.X - resizeStart.X, dy = Cursor.Position.Y - resizeStart.Y;
            // 가로·세로 중 더 많이 끈 쪽 기준으로 비율을 유지한 채 크기 조절
            float byW = (resizeStartW + dx) / (BaseW * dpi);
            float byH = (resizeStartW * BaseH / (float)BaseW + dy) / (BaseH * dpi);
            SetZoom(Math.Abs(dx) >= Math.Abs(dy) * BaseW / BaseH ? byW : byH);
            return;
        }
        if (dragging && left)
        {
            if (!moved && Math.Abs(e.X - dragOffset.X) + Math.Abs(e.Y - dragOffset.Y) < D(4)) return;
            moved = true;
            var p = Cursor.Position;
            Location = new Point(p.X - dragOffset.X, p.Y - dragOffset.Y);
            return;
        }

        bool hover = !cfg.Hidden && MenuButton.Contains(e.Location);
        if (hover != btnHover)
        {
            btnHover = hover;
            Invalidate(MenuButton);
        }
        var cursor = cfg.Hidden || hover ? Cursors.Hand
            : editing ? (Grip.Contains(e.Location) ? Cursors.SizeNWSE : Cursors.SizeAll)
            : Cursors.Default;
        if (Cursor != cursor) Cursor = cursor;
        UpdateTip(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (btnHover && !menu.Visible)
        {
            btnHover = false;
            Invalidate(MenuButton);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        justExpanded = false;
        if (resizing)
        {
            resizing = false;
            cfg.Save();
        }
        else if (dragging && moved)
        {
            // 숨김 아이콘을 옮겼으면 펼친 위젯 기준 좌표로 바꿔 저장
            cfg.X = cfg.Hidden ? Left + ClientSize.Width - WidgetW : Left;
            cfg.Y = Top;
            cfg.Save();
        }
        else if (dragging && cfg.Hidden)
        {
            SetHidden(false);
            justExpanded = true; // 펼치자마자 이어지는 더블클릭은 무시
        }
        dragging = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && !cfg.Hidden && !editing && !justExpanded && !MenuButton.Contains(e.Location))
            OpenWeb();
    }

    void UpdateTip(Point pt)
    {
        string text = null;
        if (cfg.Hidden) text = "잔디 매니저 · 클릭해서 펼치기";
        else if (MenuButton.Contains(pt)) text = editing ? "완료" : "설정";
        else if (editing) text = "끌어서 이동 · 오른쪽 아래 모서리로 크기 조절";
        else
        {
            var sunday = DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek);
            for (int i = 0; i < 7; i++)
            {
                if (!CellRect(i).Contains(pt)) continue;
                var d = sunday.AddDays(i);
                text = d.Month + "월 " + d.Day + "일 (" + DayNames[i] + ") · " +
                    (d > DateTime.Today ? "아직" : Get(d).Count + "개 기여");
            }
            if (text == null)
                text = (data != null ? "업데이트 " + lastOk.ToString("HH:mm") + " · " : "") + "더블클릭: 홈페이지 열기";
        }
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
            menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
