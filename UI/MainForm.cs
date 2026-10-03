using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using CursorStudio.Core;

namespace CursorStudio.UI;

public sealed class MainForm : Form
{
    private static readonly int[] SizeChoices = { 32, 48, 64 };

    private readonly RenderCache _cache = new();
    private Workspace _ws = new();
    private string _current = "Arrow";
    private bool _loading;
    private IntPtr _previewHandle = IntPtr.Zero;

    // 顶部
    private Button _btnImport = null!, _btnApplyAll = null!, _btnClearSlot = null!;
    private Button _btnApply = null!, _btnRestoreBackup = null!, _btnRestoreDefault = null!;

    // 左
    private SplitContainer _split = null!;
    private ListView _slotList = null!;
    private ImageList _slotIcons = null!;

    // 右上
    private HotSpotCanvas _canvas = null!;
    private PictureBox _actual = null!;
    private Label _slotTitle = null!, _slotHint = null!;

    // 右下设置
    private ComboBox _sizeBox = null!, _modeBox = null!, _hotBox = null!;
    private NumericUpDown _hotX = null!, _hotY = null!, _tolerance = null!, _scaleNum = null!;
    private CheckBox _removeBg = null!, _shadowCheck = null!;
    private Button _bgColorBtn = null!, _btnMatchSystem = null!;
    private Label _scalePx = null!;

    // 动画
    private NumericUpDown _delayNum = null!;
    private Label _frameInfo = null!;
    private Button _btnStatic = null!;
    private readonly System.Windows.Forms.Timer _animTimer = new();
    private int _previewFrame;

    // 试一试 + 状态 + 方案
    private Panel _tryPanel = null!;
    private Label _tryLabel = null!;
    private Label _status = null!;
    private ComboBox _schemeBox = null!;

    public MainForm()
    {
        AppPaths.EnsureAll();
        CleanupPreviewFolder();

        Text = "鼠标指针美化";
        // 宽度按 1080 设计；设下限是为了保证几排按钮在任何情况下都不会被挤掉
        ClientSize = new Size(1080, 720);
        MinimumSize = new Size(940, 640);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildUi();
        LoadStateOrDefault();

        _animTimer.Tick += (_, _) => AdvancePreviewFrame();

        Shown += (_, _) =>
        {
            TuneSplitter();
            RefreshSlotList();
            SelectSlot(_current);
        };
        FormClosing += (_, _) =>
        {
            SaveState();
            // 这几个都是进程级的资源，以前一直没显式放：
            // 关窗口就退出，靠进程回收也不会出问题，但句柄和 GDI 对象该还的还是要还
            _animTimer.Stop();
            _cache.Dispose();
            if (_previewHandle != IntPtr.Zero)
            {
                Win32.SafeDestroyCursor(_previewHandle);
                _previewHandle = IntPtr.Zero;
            }
        };
    }

    // ================================================================ 界面搭建

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            // 不加这句 WinForms 会把行高按"内容比重"乱分，固定高度那几行就塌了
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));   // 工具栏
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 主体
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));   // 方案栏
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // 状态栏

        root.Controls.Add(BuildToolbar(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildSchemeBar(), 0, 2);
        root.Controls.Add(BuildStatusBar(), 0, 3);

        Controls.Add(root);
    }

    private static Button MakeButton(string text, EventHandler onClick, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 30),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = true,
        };
        if (primary) b.Font = new Font(b.Font, FontStyle.Bold);
        b.Click += onClick;
        return b;
    }

    private Control BuildToolbar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = false,
            Padding = new Padding(10, 9, 10, 9),
            BackColor = SystemColors.Control,
        };

        _btnImport = MakeButton("导入图片…", (_, _) => ImportImage());
        _btnApplyAll = MakeButton("套用到全部指针", (_, _) => ApplyToAllSlots());
        _btnClearSlot = MakeButton("清除此指针", (_, _) => ClearCurrentSlot());

        _btnApply = MakeButton("应用到系统", (_, _) => ApplyToSystem(), primary: true);
        _btnApply.Margin = new Padding(20, 0, 8, 0);
        _btnRestoreBackup = MakeButton("还原备份", (_, _) => RestoreBackup());
        _btnRestoreDefault = MakeButton("恢复 Windows 默认", (_, _) => RestoreWindowsDefault());

        bar.Controls.AddRange(new Control[]
        {
            _btnImport, _btnApplyAll, _btnClearSlot,
            _btnApply, _btnRestoreBackup, _btnRestoreDefault,
        });
        return bar;
    }

    /// <summary>
    /// SplitContainer 的分隔位置和最小面板尺寸都不能在构造时设：
    /// 那会儿控件宽度还是默认的 150，设 250 会直接抛 ArgumentOutOfRangeException。
    /// 只能等窗口出来、控件量到真实宽度之后再调，而且还得夹在合法区间里。
    /// </summary>
    private void TuneSplitter()
    {
        try
        {
            _split.Panel1MinSize = 190;
            _split.Panel2MinSize = 420;
            _split.SplitterWidth = 4;

            int max = _split.Width - _split.Panel2MinSize - _split.SplitterWidth;
            if (max <= _split.Panel1MinSize) return;
            _split.SplitterDistance = Math.Clamp(250, _split.Panel1MinSize, max);
        }
        catch
        {
            // 布局还没稳定就算了，用默认值也能用
        }
    }

    private Control BuildBody()
    {
        var split = _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 4,
        };

        // ---------------- 左：指针位列表 ----------------
        _slotIcons = new ImageList { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
        _slotList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            SmallImageList = _slotIcons,
            BorderStyle = BorderStyle.None,
        };
        _slotList.Columns.Add("指针位置", 150);
        _slotList.Columns.Add("状态", 72);
        _slotList.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            if (_slotList.SelectedItems.Count == 0) return;
            if (_slotList.SelectedItems[0].Tag is not CursorSlot slot) return;
            if (slot.RegName == _current) return;

            SaveCurrentFromControls();
            _current = slot.RegName;
            LoadCurrentIntoControls();
        };
        _slotList.Resize += (_, _) => ResizeListColumns();

        split.Panel1.Controls.Add(_slotList);
        split.Panel1.Padding = new Padding(0);

        // ---------------- 右：详情 ----------------
        var detail = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
        };
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 预览 + 热点
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 设置
        // 96 改成 80：设置区多了一行「动画帧」之后，最小窗口（940×640）下
        // 右侧那条指针位说明就只剩 30px，文字会被截掉。这里让出 16px 给它
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));   // 试一试

        detail.Controls.Add(BuildPreviewRow(), 0, 0);
        detail.Controls.Add(BuildSettingsGrid(), 0, 1);
        detail.Controls.Add(BuildTryPanel(), 0, 2);

        split.Panel2.Controls.Add(detail);
        split.Panel2.Padding = new Padding(10, 0, 0, 0);

        return split;
    }

    private Control BuildPreviewRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _slotTitle = new Label
        {
            Text = "正常选择",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 2),
        };
        row.Controls.Add(_slotTitle, 0, 0);

        _canvas = new HotSpotCanvas { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
        _canvas.HotSpotPicked += (_, p) => OnCanvasPicked(p);
        row.Controls.Add(_canvas, 0, 1);

        var side = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            Margin = new Padding(0),
        };
        side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var actualCaption = new Label
        {
            Text = "实际大小",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 4),
        };
        side.Controls.Add(actualCaption, 0, 0);

        _actual = new PictureBox
        {
            Dock = DockStyle.Fill,
            // 必须是 CenterImage 不能用 Zoom：Zoom 会把这张 108×108 的预览图再缩放一遍，
            // 就看不见指针"真实的像素大小"了
            SizeMode = PictureBoxSizeMode.CenterImage,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0),
        };
        side.Controls.Add(_actual, 0, 1);

        _slotHint = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 6, 0, 0),
        };
        side.Controls.Add(_slotHint, 0, 2);

        row.Controls.Add(side, 1, 0);
        row.SetRowSpan(side, 2);
        return row;
    }

    private Control BuildSettingsGrid()
    {
        var g = new TableLayoutPanel
        {
            // Dock=Top 而不是 Fill：Fill 在 AutoSize 行里容易量出 0 高度，
            // Top 会老实按内容高度撑开，同时宽度照样铺满
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 5,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            Margin = new Padding(0, 8, 0, 0),
            Padding = new Padding(10, 8, 10, 8),
            BackColor = SystemColors.Control,
        };
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 202));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) g.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 下拉项的文案要短。DropDownList 只显示选中项，太长会被右边的箭头盖住
        // （截成"32 × 32 （Window▾"这种），宽度按最长的一项留够
        _sizeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 192, Margin = new Padding(0, 2, 12, 2) };
        _sizeBox.Items.AddRange(new object[] { "32 × 32（默认）", "48 × 48", "64 × 64（更清晰）" });
        _sizeBox.SelectedIndexChanged += (_, _) => OnSizeChanged();

        _modeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 192, Margin = new Padding(0, 2, 12, 2) };
        _modeBox.Items.AddRange(new object[] { "适应（保持比例）", "拉伸填满" });
        _modeBox.SelectedIndexChanged += (_, _) => OnSettingChanged();

        _hotBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 192, Margin = new Padding(0, 2, 12, 2) };
        _hotBox.Items.AddRange(new object[] { "自动（按指针类型）", "手动指定" });
        _hotBox.SelectedIndexChanged += (_, _) => OnHotModeChanged();

        _hotX = new NumericUpDown { Width = 62, Minimum = 0, Maximum = 255, Margin = new Padding(0, 2, 4, 2) };
        _hotY = new NumericUpDown { Width = 62, Minimum = 0, Maximum = 255, Margin = new Padding(0, 2, 0, 2) };
        _hotX.ValueChanged += (_, _) => OnSettingChanged();
        _hotY.ValueChanged += (_, _) => OnSettingChanged();

        _removeBg = new CheckBox
        {
            Text = "去除背景（抠成透明）",
            AutoSize = true,
            Margin = new Padding(0, 4, 12, 2),
        };
        _removeBg.CheckedChanged += (_, _) => { UpdateBgEnabled(); OnSettingChanged(); };

        _bgColorBtn = new Button
        {
            Text = "背景色",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 26),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(0, 2, 12, 2),
            FlatStyle = FlatStyle.System,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageAlign = ContentAlignment.MiddleLeft,
        };
        _bgColorBtn.Click += (_, _) => PickBackgroundColor();

        _tolerance = new NumericUpDown { Width = 62, Minimum = 0, Maximum = 100, Margin = new Padding(0, 2, 0, 2) };
        _tolerance.ValueChanged += (_, _) => OnSettingChanged();

        _scaleNum = new NumericUpDown
        {
            Width = 62,
            Minimum = 30,
            Maximum = 100,
            Increment = 5,
            Margin = new Padding(0, 2, 2, 2),
        };
        _scaleNum.ValueChanged += (_, _) => OnLayoutChanged();

        _scalePx = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(6, 6, 0, 2),
        };

        _btnMatchSystem = new Button
        {
            Text = "对齐系统箭头",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 26),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(10, 2, 12, 2),
            FlatStyle = FlatStyle.System,
        };
        _btnMatchSystem.Click += (_, _) => MatchSystemCursorSize();

        _shadowCheck = new CheckBox
        {
            // 收窄窗口时这行会先被挤，文案短一点才不会被裁掉
            Text = "加投影（浅底更清楚）",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 2),
        };
        _shadowCheck.CheckedChanged += (_, _) => OnSettingChanged();

        // 第 1 行
        g.Controls.Add(MakeCaption("渲染尺寸"), 0, 0);
        g.Controls.Add(_sizeBox, 1, 0);
        g.Controls.Add(MakeCaption("缩放方式"), 2, 0);
        g.Controls.Add(_modeBox, 3, 0);

        // 第 2 行：相对大小。这一行是"别比原来的指针大一圈"那个问题的解药
        g.Controls.Add(MakeCaption("相对大小"), 0, 1);

        var scaleRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
        scaleRow.Controls.Add(_scaleNum);
        scaleRow.Controls.Add(new Label { Text = "%", AutoSize = true, Margin = new Padding(2, 6, 0, 0) });
        scaleRow.Controls.Add(_scalePx);
        g.Controls.Add(scaleRow, 1, 1);

        // 按钮和复选框各占一格，不塞进同一个 FlowLayoutPanel。
        // 塞一起的话在窄窗口下整行是"要嘛全放得下、要嘛把最后一个裁掉"，
        // 而 AutoSize 的列量出来的宽度又不足以触发换行，结果就是复选框被切掉一截。
        g.Controls.Add(_btnMatchSystem, 2, 1);
        g.Controls.Add(_shadowCheck, 3, 1);

        // 第 3 行
        g.Controls.Add(MakeCaption("热点位置"), 0, 2);
        g.Controls.Add(_hotBox, 1, 2);

        var xy = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
        xy.Controls.Add(new Label { Text = "X", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        xy.Controls.Add(_hotX);
        xy.Controls.Add(new Label { Text = "Y", AutoSize = true, Margin = new Padding(10, 6, 4, 0) });
        xy.Controls.Add(_hotY);
        g.Controls.Add(MakeCaption("坐标"), 2, 2);
        g.Controls.Add(xy, 3, 2);

        // 第 4 行
        g.Controls.Add(_removeBg, 0, 3);
        g.SetColumnSpan(_removeBg, 2);

        g.Controls.Add(_bgColorBtn, 2, 3);

        var tol = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
        tol.Controls.Add(new Label { Text = "容差", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        tol.Controls.Add(_tolerance);
        g.Controls.Add(tol, 3, 3);

        // 第 5 行：动画。只有导入多帧（GIF 或一次选多张图）之后才用得上，
        // 没有动画时整行禁用，免得让人以为静态指针坏了
        _delayNum = new NumericUpDown
        {
            Width = 62,
            Minimum = AniFile.MinDelayMs,
            Maximum = AniFile.MaxDelayMs,
            Increment = 10,
            Value = AniFile.DefaultDelayMs,
            Margin = new Padding(0, 2, 2, 2),
        };
        _delayNum.ValueChanged += (_, _) => OnAnimationChanged();

        _frameInfo = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(6, 6, 0, 2),
        };

        _btnStatic = new Button
        {
            Text = "只用第一帧",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 26),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(10, 2, 12, 2),
            FlatStyle = FlatStyle.System,
        };
        _btnStatic.Click += (_, _) => DropAnimation();

        g.Controls.Add(MakeCaption("动画帧"), 0, 4);

        var delayRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
        delayRow.Controls.Add(_delayNum);
        delayRow.Controls.Add(new Label { Text = "毫秒 / 帧", AutoSize = true, Margin = new Padding(2, 6, 0, 0) });
        g.Controls.Add(delayRow, 1, 4);

        g.Controls.Add(_btnStatic, 2, 4);
        g.Controls.Add(_frameInfo, 3, 4);

        return g;
    }

    private static Label MakeCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 6, 8, 2),
    };

    private Control BuildTryPanel()
    {
        _tryPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 8, 0, 0),
        };
        _tryLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "把鼠标移到这一块里，试试改完的实际手感",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
        };
        _tryPanel.Controls.Add(_tryLabel);
        return _tryPanel;
    }

    private Control BuildSchemeBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = false,
            Padding = new Padding(10, 9, 10, 9),
            BackColor = SystemColors.Control,
        };

        _schemeBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Width = 200,
            Margin = new Padding(0, 0, 8, 0),
        };

        bar.Controls.Add(new Label { Text = "方案", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
        bar.Controls.Add(_schemeBox);
        bar.Controls.Add(MakeButton("保存", (_, _) => SaveScheme()));
        bar.Controls.Add(MakeButton("另存为…", (_, _) => SaveSchemeAs()));
        bar.Controls.Add(MakeButton("删除", (_, _) => DeleteScheme()));
        bar.Controls.Add(MakeButton("导入主题包…", (_, _) => ImportThemePack()));
        bar.Controls.Add(MakeButton("导入方案包…", (_, _) => ImportPack()));
        bar.Controls.Add(MakeButton("导出方案包…", (_, _) => ExportPack()));
        return bar;
    }

    private Control BuildStatusBar()
    {
        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 12, 0),
            AutoEllipsis = true,
            BackColor = SystemColors.Control,
            ForeColor = SystemColors.GrayText,
        };
        return _status;
    }

    private void ResizeListColumns()
    {
        if (_slotList.Columns.Count < 2) return;
        int fixedPart = _slotList.Columns[1].Width;
        int w = _slotList.ClientSize.Width - fixedPart - 4;
        if (w > 60) _slotList.Columns[0].Width = w;
    }

    // ================================================================ 状态存取

    private void LoadStateOrDefault()
    {
        _ws = Store.LoadState() ?? new Workspace();
        _schemeBox.Text = _ws.SchemeName;
        RefreshSchemeList();
    }

    private void SaveState()
    {
        try
        {
            SaveCurrentFromControls();
            Store.SaveState(_ws);
        }
        catch { /* 关窗口时存状态失败不该弹框拦人 */ }
    }

    private void RefreshSchemeList()
    {
        string keep = _schemeBox.Text;
        _schemeBox.Items.Clear();
        foreach (var n in Store.ListSchemes()) _schemeBox.Items.Add(n);
        _schemeBox.Text = keep;
    }

    // ================================================================ 列表

    private void RefreshSlotList()
    {
        _loading = true;
        try
        {
            int selected = _slotList.SelectedIndices.Count > 0 ? _slotList.SelectedIndices[0] : 0;

            // 顺序要紧：先清空 Items 再清空 Images。
            // 反过来的话列表项还指着已经被释放的图标索引，中间一旦重绘就会画到坏数据
            _slotList.BeginUpdate();
            _slotList.Items.Clear();
            _slotIcons.Images.Clear();

            foreach (var slot in CursorSlots.All)
            {
                var st = _ws.Peek(slot.RegName);
                var item = new ListViewItem(slot.DisplayName) { Tag = slot };

                if (st?.IsConfigured == true)
                {
                    string? err = null;
                    // 外部文件（导入的主题包）读它的首帧；自己渲染的走缓存
                    Bitmap? thumb = st.UsesExternal
                        ? Store.ReadExternalPreview(st.ExternalCursor!, 20, out _)
                        : _cache.Render(st.SourceImage!, ThumbSettings(st), out err);

                    if (thumb is not null)
                    {
                        // ImageList 会长期持有这张图；缓存里的图随时可能被淘汰，
                        // 所以必须复制一份给它，不能直接把缓存的引用塞进去
                        using var copy = new Bitmap(thumb);
                        if (st.UsesExternal) thumb.Dispose();   // 这张是我们自己的
                        _slotIcons.Images.Add(copy);
                        item.ImageIndex = _slotIcons.Images.Count - 1;
                        item.SubItems.Add(st.UsesExternal ? "外部" : "已设置");
                    }
                    else
                    {
                        item.SubItems.Add("图片丢失");
                        item.ForeColor = Color.FromArgb(190, 60, 60);
                    }
                }
                else
                {
                    item.SubItems.Add("—");
                    item.ForeColor = SystemColors.GrayText;
                }

                _slotList.Items.Add(item);
            }

            _slotList.EndUpdate();

            if (_slotList.Items.Count > 0)
                _slotList.Items[Math.Clamp(selected, 0, _slotList.Items.Count - 1)].Selected = true;

            ResizeListColumns();
        }
        finally
        {
            _loading = false;
        }

        SyncSelectionToList();
    }

    private static RenderSettings ThumbSettings(SlotState st)
    {
        var rs = st.ToRenderSettings();
        rs.Size = 20;
        return rs;
    }

    private void SyncSelectionToList()
    {
        foreach (ListViewItem item in _slotList.Items)
        {
            if (item.Tag is CursorSlot s && s.RegName == _current)
            {
                item.Selected = true;
                item.EnsureVisible();
                break;
            }
        }
    }

    private void SelectSlot(string regName)
    {
        _current = regName;
        _loading = true;
        SyncSelectionToList();
        _loading = false;
        LoadCurrentIntoControls();
    }

    // ================================================================ 控件 ↔ 数据

    private void LoadCurrentIntoControls()
    {
        var slot = CursorSlots.ByRegName(_current);
        if (slot is null) return;

        var st = _ws.For(_current);

        _loading = true;
        try
        {
            _slotTitle.Text = slot.DisplayName;
            _slotHint.Text = slot.Hint;

            _sizeBox.SelectedIndex = Math.Max(0, Array.IndexOf(SizeChoices, st.Size));
            _modeBox.SelectedIndex = st.KeepAspect ? 0 : 1;
            _hotBox.SelectedIndex = st.HotX < 0 || st.HotY < 0 ? 0 : 1;
            _hotX.Value = Math.Clamp(st.HotX < 0 ? 0 : st.HotX, 0, 255);
            _hotY.Value = Math.Clamp(st.HotY < 0 ? 0 : st.HotY, 0, 255);
            _hotX.Enabled = _hotY.Enabled = st.HotX >= 0 && st.HotY >= 0;

            _scaleNum.Value = Math.Clamp((int)Math.Round(st.Scale * 100), 30, 100);
            _shadowCheck.Checked = st.Shadow;

            _removeBg.Checked = st.RemoveBackground;
            _tolerance.Value = Math.Clamp(st.Tolerance, 0, 100);
            // 这个 Tag 是背景色的唯一来源：SaveCurrentFromControls 会把它写回 st。
            // 忘了设的话，每次切指针位都会把背景色悄悄重置成白色。
            _bgColorBtn.Tag = SlotState.ParseColor(st.BackgroundKey, Color.White);
            UpdateBgButtonFace();
            UpdateBgEnabled();
            UpdateAnimationControls();
            UpdateExternalUi();      // 放最后：外部文件要把上面这些再盖掉
        }
        finally
        {
            _loading = false;
        }

        RefreshPreview();
    }

    private void SaveCurrentFromControls()
    {
        if (_loading) return;
        var st = _ws.For(_current);

        st.Size = SizeChoices[Math.Clamp(_sizeBox.SelectedIndex, 0, SizeChoices.Length - 1)];
        st.KeepAspect = _modeBox.SelectedIndex == 0;
        st.Scale = Math.Clamp((double)_scaleNum.Value / 100.0, 0.30, 1.00);
        st.Shadow = _shadowCheck.Checked;
        st.RemoveBackground = _removeBg.Checked;
        st.Tolerance = (int)_tolerance.Value;
        st.BackgroundKey = SlotState.FormatColor(CurrentBgColor());
        if (st.IsAnimated)
            st.FrameDelayMs = Math.Clamp((int)_delayNum.Value, AniFile.MinDelayMs, AniFile.MaxDelayMs);

        if (_hotBox.SelectedIndex == 0)
        {
            st.HotX = -1;
            st.HotY = -1;
        }
        else
        {
            st.HotX = (int)_hotX.Value;
            st.HotY = (int)_hotY.Value;
        }
    }

    private Color CurrentBgColor() => _bgColorBtn.Tag is Color c ? c : Color.White;

    private void UpdateBgEnabled()
    {
        bool on = _removeBg.Checked && !CurrentIsExternal;
        _bgColorBtn.Enabled = on;
        _tolerance.Enabled = on;
    }

    /// <summary>当前指针位用的是不是外部文件（导入的主题包）。</summary>
    private bool CurrentIsExternal => _ws.Peek(_current)?.UsesExternal == true;

    /// <summary>
    /// 外部文件是原样使用的，渲染相关的设置对它**一点作用都没有**。
    /// 所以这些控件直接灰掉，并在提示里说清楚——不然用户拖了半天没反应，会以为程序坏了。
    /// </summary>
    private void UpdateExternalUi()
    {
        bool ext = CurrentIsExternal;

        _sizeBox.Enabled = !ext;
        _modeBox.Enabled = !ext;
        _scaleNum.Enabled = !ext;
        _btnMatchSystem.Enabled = !ext;
        _shadowCheck.Enabled = !ext;
        _removeBg.Enabled = !ext;
        _hotBox.Enabled = !ext;
        _hotX.Enabled = !ext && _hotBox.SelectedIndex == 1;
        _hotY.Enabled = _hotX.Enabled;
        _delayNum.Enabled = !ext && _ws.Peek(_current)?.IsAnimated == true;
        _btnStatic.Enabled = _delayNum.Enabled;

        if (ext)
        {
            _frameInfo.Text = "原样使用外部文件";
            _scalePx.Text = "";
        }
    }

    // ================================================================ 动画

    /// <summary>把动画那一行的控件同步成当前指针位的状态。调用点在 _loading 保护内，不会反过来触发事件。</summary>
    private void UpdateAnimationControls()
    {
        var st = _ws.Peek(_current);
        bool animated = st?.IsAnimated == true;

        _delayNum.Enabled = animated;
        _btnStatic.Enabled = animated;

        if (!animated)
        {
            _delayNum.Value = Math.Clamp(st?.FrameDelayMs ?? AniFile.DefaultDelayMs,
                                         AniFile.MinDelayMs, AniFile.MaxDelayMs);
            _frameInfo.Text = st?.HasImage == true ? "静态（1 帧）" : "";
            return;
        }

        _delayNum.Value = Math.Clamp(st!.FrameDelayMs, AniFile.MinDelayMs, AniFile.MaxDelayMs);
        _frameInfo.Text = DescribeAnimation(st);
    }

    /// <summary>
    /// 帧数和帧率。文案要短——这一格在最小窗口（940）下留给它的宽度有限，
    /// 写长了标签会想折成两行，布局自检会报"文字可能显示不全"。
    /// 「这个位置系统不播动画」那句放到状态栏去说。
    /// </summary>
    private string DescribeAnimation(SlotState st)
    {
        int fps = (int)Math.Round(1000.0 / Math.Max(1, st.FrameDelayMs));
        string text = $"共 {st.FrameCount} 帧 · 约 {fps} fps";

        // 系统只在「忙碌」和「后台运行」两个位置播放动画，别的位置传了 .ani 也只显示第一帧。
        // 这是 Windows 的行为，不是本程序的限制
        if (_current != "Wait" && _current != "AppStarting")
            text += "（系统不播）";
        return text;
    }

    private void OnAnimationChanged()
    {
        if (_loading) return;

        var st = _ws.Peek(_current);
        if (st?.IsAnimated != true) return;

        st.FrameDelayMs = (int)_delayNum.Value;
        _frameInfo.Text = DescribeAnimation(st);
        RefreshPreview();       // 重开定时器，让新帧间隔立刻生效
    }

    /// <summary>把动画砍成一张静态图：只留第一帧。</summary>
    private void DropAnimation()
    {
        var st = _ws.Peek(_current);
        if (st?.IsAnimated != true) return;

        int dropped = st.ExtraFrames!.Count;
        st.ExtraFrames = null;
        st.HotX = -1;   // 热点是按并集算的，帧集变了要重算
        st.HotY = -1;

        RefreshPreview();
        RefreshSlotList();
        SelectSlot(_current);
        SetStatus($"已改成静态指针，丢掉了 {dropped} 帧。点「应用到系统」生效。");
    }

    /// <summary>定时器到点：换下一帧。</summary>
    private void AdvancePreviewFrame()
    {
        var st = _ws.Peek(_current);
        if (st?.IsAnimated != true)
        {
            _animTimer.Stop();
            return;
        }

        _previewFrame = (_previewFrame + 1) % st.FrameCount;
        ShowPreviewFrame(st);
    }

    /// <summary>只更新画布和"实际大小"两张图，不动其它控件——每 50ms 跑一次，要够轻。</summary>
    private void ShowPreviewFrame(SlotState st)
    {
        var slot = CursorSlots.ByRegName(_current);
        var sources = st.AllFrames();
        if (slot is null || sources.Count == 0) return;

        _previewFrame = Math.Clamp(_previewFrame, 0, sources.Count - 1);

        var rs = st.ToRenderSettings();
        var bmp = _cache.Render(sources[_previewFrame], rs, out _);
        if (bmp is null) return;

        Point hot = Store.ResolveHotSpot(st, slot, sources, _cache);
        _canvas.SetImage(bmp, hot);
        SwapActualImage(MakeActualPreview(bmp));
    }

    // ================================================================ 预览

    private void RefreshPreview()
    {
        var st = _ws.Peek(_current);
        var slot = CursorSlots.ByRegName(_current);

        _animTimer.Stop();

        if (st?.IsConfigured != true || slot is null)
        {
            _previewFrame = 0;
            _canvas.SetImage(null, Point.Empty);
            SwapActualImage(MakePlaceholder());
            SetTryCursor(null);
            UpdateScaleHint();
            UpdateStatus();
            return;
        }

        // 外部文件（导入的主题包）不走渲染管线，直接读它本身
        if (st.UsesExternal)
        {
            _animTimer.Stop();
            _previewFrame = 0;

            var external = Store.ReadExternalPreview(st.ExternalCursor!, st.Size, out Point extHot);
            if (external is null)
            {
                _canvas.SetImage(null, Point.Empty);
                SetStatus($"读不出外部指针文件：{Path.GetFileName(st.ExternalCursor!)}", error: true);
                return;
            }

            try
            {
                _canvas.SetImage(external, extHot);
                SwapActualImage(MakeActualPreview(external));

                // 外部文件的热点是它自带的，回显出来让用户看得见
                _loading = true;
                _hotBox.SelectedIndex = 1;
                _hotX.Value = Math.Clamp(extHot.X, 0, 255);
                _hotY.Value = Math.Clamp(extHot.Y, 0, 255);
                _loading = false;
            }
            finally
            {
                external.Dispose();
            }

            // 「试一试」直接把原文件交给系统：.ani 的话系统自己就会播
            SetTryCursor(File.Exists(st.ExternalCursor!) ? st.ExternalCursor : null);
            UpdateScaleHint();
            UpdateStatus();
            return;
        }

        var sources = st.AllFrames();
        _previewFrame = Math.Clamp(_previewFrame, 0, sources.Count - 1);

        var rs = st.ToRenderSettings();
        var bmp = _cache.Render(sources[_previewFrame], rs, out string? err);

        if (bmp is null)
        {
            _canvas.SetImage(null, Point.Empty);
            SetStatus($"渲染失败：{err}", error: true);
            return;
        }

        Point hot = Store.ResolveHotSpot(st, slot, sources, _cache);

        _canvas.SetImage(bmp, hot);

        // 自动热点时把算出来的坐标回显到输入框，用户能看见它到底定在哪
        if (rs.IsAutoHotSpot)
        {
            _loading = true;
            _hotX.Value = Math.Clamp(hot.X, 0, 255);
            _hotY.Value = Math.Clamp(hot.Y, 0, 255);
            _loading = false;
        }

        SwapActualImage(MakeActualPreview(bmp));

        SetTryCursor(MakePreviewCursor(st, slot));
        UpdateScaleHint();
        UpdateStatus();

        // 动画的话让画布自己转起来。「试一试」那块不用管——它拿到的是 .ani，
        // 系统自己就会播，这也是"真的换成了动画指针"最直接的证据
        if (st.IsAnimated)
        {
            _animTimer.Interval = Math.Clamp(st.FrameDelayMs, AniFile.MinDelayMs, AniFile.MaxDelayMs);
            _animTimer.Start();
        }
    }

    /// <summary>换图时先装新的再扔旧的：反过来的话 PictureBox 会有一瞬间指着已释放的图。</summary>
    private void SwapActualImage(Bitmap next)
    {
        var old = _actual.Image;
        _actual.Image = next;
        old?.Dispose();
    }

    /// <summary>把渲染结果按 1:1 画在棋盘格上，让用户看见真实的像素大小。</summary>
    private static Bitmap MakeActualPreview(Bitmap rendered)
    {
        const int box = 108;
        var bmp = new Bitmap(box, box, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        const int cell = 9;
        using var light = new SolidBrush(Color.White);
        using var dark = new SolidBrush(Color.FromArgb(236, 236, 240));
        g.FillRectangle(light, new Rectangle(0, 0, box, box));
        for (int y = 0; y < box; y += cell)
        for (int x = 0; x < box; x += cell)
            if (((x / cell) + (y / cell)) % 2 == 1)
                g.FillRectangle(dark, x, y, cell, cell);

        int left = (box - rendered.Width) / 2;
        int top = (box - rendered.Height) / 2;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(rendered, left, top, rendered.Width, rendered.Height);

        using var pen = new Pen(Color.FromArgb(60, 0, 0, 0));
        g.DrawRectangle(pen, left - 1, top - 1, rendered.Width + 1, rendered.Height + 1);
        return bmp;
    }

    private static Bitmap MakePlaceholder()
    {
        var bmp = new Bitmap(108, 108, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        using var pen = new Pen(Color.FromArgb(215, 215, 220));
        g.DrawLine(pen, 0, 0, 108, 108);
        g.DrawLine(pen, 108, 0, 0, 108);
        return bmp;
    }

    // ================================================================ 试一试的指针

    /// <summary>
    /// 生成一个临时 .cur 给「试一试」区域用。
    /// 必须每次都写新文件名：Windows 的 LoadCursorFromFile 是按路径缓存的，
    /// 同名文件改了内容它也不会重新读。
    /// </summary>
    private string? MakePreviewCursor(SlotState st, CursorSlot slot)
    {
        try
        {
            string dir = Path.Combine(AppPaths.Root, "preview");
            Directory.CreateDirectory(dir);

            int size = st.Size;
            var rs = st.ToRenderSettings();
            rs.Size = size;

            var sources = st.AllFrames();
            Point hot = Store.ResolveHotSpot(st, slot, sources, _cache);

            string ext;
            string tmp;

            if (st.IsAnimated)
            {
                // 每帧装一个完整的 .cur，再套进 .ani 里交给 Windows 自己播。
                // 「试一试」里动起来的那一下，就是应用到系统之后的样子
                var frames = new List<byte[]>(sources.Count);
                foreach (var src in sources)
                {
                    var fb = _cache.Render(src, rs, out _);
                    if (fb is null) return null;
                    frames.Add(CurFile.BuildBytes(new[] { new CurImage(fb, hot.X, hot.Y) }));
                }

                ext = ".ani";
                tmp = Path.Combine(dir, "building.ani");
                AniFile.Write(tmp, frames, size, st.FrameDelayMs);
            }
            else
            {
                var bmp = _cache.Render(sources[0], rs, out _);
                if (bmp is null) return null;

                ext = ".cur";
                tmp = Path.Combine(dir, "building.cur");
                CurFile.Write(tmp, new[] { new CurImage(bmp, hot.X, hot.Y) });
            }

            string hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(tmp)))[..12];
            string final = Path.Combine(dir, $"{slot.RegName}_{hash}{ext}");

            if (!File.Exists(final)) File.Move(tmp, final, overwrite: true);
            else File.Delete(tmp);

            CleanupPreviewFolder(keep: Path.GetFileName(final));
            return final;
        }
        catch
        {
            return null;
        }
    }

    private void SetTryCursor(string? curPath)
    {
        Cursor? next = null;
        IntPtr handle = IntPtr.Zero;

        if (curPath is not null)
        {
            handle = Win32.LoadCursor(curPath);
            if (handle != IntPtr.Zero)
            {
                try { next = new Cursor(handle); }
                catch { handle = IntPtr.Zero; next = null; }
            }
        }

        // 先把新指针装上去，再销毁旧的——反过来会让控件短暂引用一个已经销毁的句柄
        _tryPanel.Cursor = next;
        _tryLabel.Cursor = next;

        if (_previewHandle != IntPtr.Zero) Win32.SafeDestroyCursor(_previewHandle);
        _previewHandle = handle;
    }

    private static void CleanupPreviewFolder(string? keep = null)
    {
        try
        {
            string dir = Path.Combine(AppPaths.Root, "preview");
            if (!Directory.Exists(dir)) return;
            foreach (var pattern in new[] { "*.cur", "*.ani" })
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
            {
                if (keep is not null && string.Equals(Path.GetFileName(f), keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Delete(f);
            }
        }
        catch { /* 临时文件清理失败无所谓 */ }
    }

    // ================================================================ 事件

    private void OnCanvasPicked(Point p)
    {
        _loading = true;
        _hotBox.SelectedIndex = 1;      // 一点画布就切到"手动指定"
        _hotX.Value = Math.Clamp(p.X, 0, 255);
        _hotY.Value = Math.Clamp(p.Y, 0, 255);
        _loading = false;

        SaveCurrentFromControls();
        RefreshPreview();
    }

    private void OnSizeChanged() => OnLayoutChanged();

    /// <summary>
    /// 渲染尺寸或相对大小变了。
    /// 这两件事都会改变图形在画布上的落位，手点过的热点必须跟着换算过去，
    /// 否则箭头一换到 64 或者一改大小，点击点就偏了——手感上就是"鼠标点不准"。
    /// </summary>
    private void OnLayoutChanged()
    {
        if (_loading) return;

        var st = _ws.For(_current);
        int oldSize = st.Size;
        double oldScale = st.Scale;

        SaveCurrentFromControls();   // 控件里的新 size/scale 写进 st
        RemapHotSpot(st, oldSize, oldScale);
        RefreshPreview();
    }

    private void RemapHotSpot(SlotState st, int oldSize, double oldScale)
    {
        if (st.HotX < 0 || st.HotY < 0) return;              // 自动热点自己会重算
        if (st.SourceImage is null) return;
        if (oldSize == st.Size && Math.Abs(oldScale - st.Scale) < 0.0001) return;

        var src = _cache.GetSourceSize(st.SourceImage);
        if (src is null) return;

        var oldRect = Renderer.FitRect(src.Value.Width, src.Value.Height, oldSize, st.KeepAspect, oldScale);
        var newRect = Renderer.FitRect(src.Value.Width, src.Value.Height, st.Size, st.KeepAspect, st.Scale);

        var p = Renderer.MapHotSpot(new Point(st.HotX, st.HotY), oldRect, newRect);
        st.HotX = Math.Clamp(p.X, 0, st.Size - 1);
        st.HotY = Math.Clamp(p.Y, 0, st.Size - 1);

        _loading = true;
        _hotX.Value = Math.Clamp(st.HotX, 0, 255);
        _hotY.Value = Math.Clamp(st.HotY, 0, 255);
        _loading = false;
    }

    /// <summary>
    /// 量一下系统当前那个箭头在画布里实际占多大，把"相对大小"对齐过去。
    /// 这是"导入的图别比原来的指针大一圈"最直接的答案——不用猜，直接量。
    /// </summary>
    private void MatchSystemCursorSize()
    {
        string? path = CursorRegistry.ReadCurrent().GetValueOrDefault("Arrow");
        if (string.IsNullOrEmpty(path))
        {
            SetStatus("读不到系统当前箭头的路径，没法对齐。可以自己拖「相对大小」。", error: true);
            return;
        }

        string expanded = Environment.ExpandEnvironmentVariables(path);
        var frame = File.Exists(expanded) ? CurFile.ReadFrame(expanded, 32) : null;
        if (frame is null)
        {
            SetStatus($"读不出「{Path.GetFileName(path)}」的内容，没法对齐。可以自己拖「相对大小」。", error: true);
            return;
        }

        using (frame.Image)
        {
            var ink = CurFile.InkBounds(frame.Image);
            if (ink.IsEmpty)
            {
                SetStatus("系统箭头的内容框是空的，没法对齐。", error: true);
                return;
            }

            double ratio = (double)Math.Max(ink.Width, ink.Height) / frame.Image.Width;
            int pct = Math.Clamp((int)Math.Round(ratio * 100), 30, 100);

            _scaleNum.Value = pct;   // 顺手触发 OnLayoutChanged → 重算热点 + 重绘
            SetStatus($"系统箭头的内容框是 {ink.Width}×{ink.Height}，占 {frame.Image.Width}×{frame.Image.Height} " +
                      $"画布的 {pct}%，已把「相对大小」对齐过去。");
        }
    }

    private void UpdateScaleHint()
    {
        var st = _ws.Peek(_current);
        if (st?.HasImage != true)
        {
            _scalePx.Text = "";
            return;
        }
        int target = Math.Clamp((int)Math.Round(st.Size * st.Scale), 1, st.Size);
        _scalePx.Text = $"长边约 {target} px";
    }

    private void OnHotModeChanged()
    {
        if (_loading) return;
        bool manual = _hotBox.SelectedIndex == 1;
        _hotX.Enabled = manual;
        _hotY.Enabled = manual;

        SaveCurrentFromControls();
        RefreshPreview();
    }

    private void OnSettingChanged()
    {
        if (_loading) return;
        SaveCurrentFromControls();
        RefreshPreview();
    }

    // ================================================================ 导入 / 清除

    private void ImportImage()
    {
        using var dlg = new OpenFileDialog
        {
            // 多选是给动画用的：挑一串按顺序排好的 PNG，就是一段动画。
            // 单选一张静态图的行为和以前完全一样
            Title = "选图片（GIF 会导成动画；也可以一次选多张当动画的帧）",
            Filter = ImageLoader.FramesDialogFilter,
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AssignImagesFromFiles(dlg.FileNames);
    }

    /// <summary>
    /// 把一组文件导进来当一个指针位的图。
    /// 一个文件里有多帧（GIF）就用它的全部帧；多选多个文件就按选择顺序当帧。
    /// 两种情况合起来都指向同一件事：得到一个"帧列表"。
    /// </summary>
    private void AssignImagesFromFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        var storedFrames = new List<string>();
        int delayMs = AniFile.DefaultDelayMs;
        bool truncated = false;
        string firstName = Path.GetFileName(paths[0]);

        try
        {
            foreach (var path in paths)
            {
                var loaded = ImageLoader.LoadFrames(path);
                truncated |= loaded.Truncated;

                // 多帧文件的延时用它自己的；多个文件各带各的延时时，取第一个有意义的
                if (loaded.Frames.Count > 1 && delayMs == AniFile.DefaultDelayMs)
                    delayMs = loaded.DelayMs;

                foreach (var frame in loaded.Frames)
                {
                    try { storedFrames.Add(Store.ImportBitmap(frame)); }
                    finally { frame.Dispose(); }
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus("导入失败：" + ex.Message, error: true);
            MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (storedFrames.Count == 0)
        {
            SetStatus("这些文件里一帧都没读出来。", error: true);
            return;
        }

        var st = _ws.For(_current);
        foreach (var old in st.AllFrames())
            if (!storedFrames.Contains(old, StringComparer.OrdinalIgnoreCase))
                _cache.ForgetSource(old);

        st.SetFrames(storedFrames);
        st.FrameDelayMs = delayMs;
        _previewFrame = 0;
        st.HotX = -1;
        st.HotY = -1;

        bool uniform = DetectBackgroundOf(storedFrames[0], out var key);
        st.BackgroundKey = SlotState.FormatColor(key);
        st.RemoveBackground = uniform;
        if (uniform && st.Tolerance < 20) st.Tolerance = 30;

        LoadCurrentIntoControls();
        RefreshSlotList();
        SelectSlot(_current);

        string what = st.IsAnimated
            ? $"已导入 {st.FrameCount} 帧（{firstName} 等）作为动画指针"
            : $"已把「{firstName}」指定给「{CursorSlots.ByRegName(_current)?.DisplayName}」";

        string tail = st.IsAnimated
            ? (_current is "Wait" or "AppStarting"
                ? "，点「应用到系统」就能看到它动起来。"
                : "。注意这个位置 Windows 不播放动画，只会显示第一帧。")
            : (uniform ? "，并自动识别到纯色背景、已开启抠图。" : "。可以点「应用到系统」了。");

        if (truncated) tail += $"（帧数超过 {AniFile.MaxFrames}，已均匀抽帧）";

        SetStatus(what + tail);
    }

    /// <summary>白底图自动把抠图打开——导入截图/logo 时最常需要的就这一步，只有真判定成纯色底才敢开。</summary>
    private static bool DetectBackgroundOf(string storedPath, out Color key)
    {
        key = Color.White;
        try
        {
            using var bmp = ImageLoader.Load(storedPath);
            bool uniform;
            (key, uniform) = Renderer.DetectBackground(bmp);
            return uniform;
        }
        catch
        {
            return false;   // 探测失败就用默认值，不拦着导入
        }
    }

    private void AssignImageFromFile(string path) => AssignImagesFromFiles(new[] { path });

    private void PasteFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsImage())
            {
                SetStatus("剪贴板里没有图片。", error: true);
                return;
            }

            using var img = Clipboard.GetImage();
            if (img is null)
            {
                SetStatus("剪贴板里的图片读不出来。", error: true);
                return;
            }

            AppPaths.EnsureAll();
            string tmp = Path.Combine(Path.GetTempPath(), $"cursorstudio_paste_{Guid.NewGuid():N}.png");
            img.Save(tmp, ImageFormat.Png);
            try { AssignImageFromFile(tmp); }
            finally { try { File.Delete(tmp); } catch { } }
        }
        catch (Exception ex)
        {
            SetStatus("粘贴失败：" + ex.Message, error: true);
        }
    }

    private void ApplyToAllSlots()
    {
        var src = _ws.Peek(_current);
        if (src?.IsConfigured != true)
        {
            SetStatus("当前指针位还没有配图，先「导入图片」或「导入主题包」再套用。", error: true);
            return;
        }

        bool external = src.UsesExternal;
        var ans = MessageBox.Show(this,
            external
                ? $"把当前这个指针文件原样套用到全部 {CursorSlots.All.Count} 个指针位？\n\n" +
                  "· 所有位置都会用同一个文件（大小、热点都不改）\n" +
                  "· 会覆盖掉其他指针位已经配好的图\n"
                : $"把当前这张图套用到全部 {CursorSlots.All.Count} 个指针位？\n\n" +
                  "· 每个指针位的热点会自动按类型重算（箭头取尖角、十字取正中）\n" +
                  "· 会覆盖掉其他指针位已经配好的图\n",
            "套用到全部指针", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ans != DialogResult.OK) return;

        SaveCurrentFromControls();

        foreach (var slot in CursorSlots.All)
        {
            if (slot.RegName == _current) continue;
            var st = _ws.For(slot.RegName);

            if (external)
            {
                st.ExternalCursor = src.ExternalCursor;
                st.SourceImage = null;
                st.ExtraFrames = null;
                continue;
            }

            st.ExternalCursor = null;
            st.SourceImage = src.SourceImage;
            // 列表要复制一份，不能几个指针位共用同一个 List 对象
            st.ExtraFrames = src.ExtraFrames is null ? null : new List<string>(src.ExtraFrames);
            st.FrameDelayMs = src.FrameDelayMs;
            st.Size = src.Size;
            st.KeepAspect = src.KeepAspect;
            st.Scale = src.Scale;
            st.Shadow = src.Shadow;
            st.RemoveBackground = src.RemoveBackground;
            st.BackgroundKey = src.BackgroundKey;
            st.Tolerance = src.Tolerance;
            st.HotX = -1;   // 热点各按各的类型算，别照搬
            st.HotY = -1;
        }

        RefreshSlotList();
        SetStatus($"已套用到全部 {CursorSlots.All.Count} 个指针位。点「应用到系统」让它生效。");
    }

    private void ClearCurrentSlot()
    {
        var st = _ws.Peek(_current);
        if (st?.IsConfigured != true)
        {
            SetStatus("这个指针位本来就没有配图。");
            return;
        }

        string name = CursorSlots.ByRegName(_current)?.DisplayName ?? _current;
        var ans = MessageBox.Show(this,
            $"清除「{name}」的配图？\n\n清除后这个指针位会保持系统原样，下次「应用到系统」时不会被改动。",
            "清除此指针", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ans != DialogResult.OK) return;

        st.SourceImage = null;
        st.ExtraFrames = null;
        st.ExternalCursor = null;
        st.HotX = -1;
        st.HotY = -1;

        RefreshSlotList();
        SelectSlot(_current);
        SetStatus($"已清除「{name}」。注意：系统里现在还是旧指针，点「应用到系统」才会真正换回去。");
    }

    private void PickBackgroundColor()
    {
        using var dlg = new ColorDialog
        {
            FullOpen = true,
            Color = CurrentBgColor(),
            AnyColor = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _bgColorBtn.Tag = dlg.Color;
        UpdateBgButtonFace();
        _removeBg.Checked = true;
        OnSettingChanged();
    }

    private void UpdateBgButtonFace()
    {
        var c = CurrentBgColor();
        _bgColorBtn.Text = $"背景色 #{c.R:X2}{c.G:X2}{c.B:X2}";
        // 用图片当色块：Windows 的主题色会让按钮背景着色，直接设 BackColor 未必显示得出来
        var swatch = new Bitmap(14, 14);
        using (var g = Graphics.FromImage(swatch))
        {
            g.Clear(c);
            using var pen = new Pen(Color.FromArgb(120, 120, 130));
            g.DrawRectangle(pen, 0, 0, 13, 13);
        }
        var old = _bgColorBtn.Image;
        _bgColorBtn.Image = swatch;
        old?.Dispose();
    }

    // ================================================================ 应用 / 还原

    private void ApplyToSystem()
    {
        SaveCurrentFromControls();

        if (_ws.ConfiguredCount == 0)
        {
            SetStatus("还没有给任何指针位配图，没什么可应用的。", error: true);
            return;
        }

        var built = Store.BuildCursorFiles(_ws, _cache);

        if (built.Files.Count == 0)
        {
            SetStatus("生成指针文件失败：" + string.Join("；", built.Warnings), error: true);
            MessageBox.Show(this,
                "所有指针位都生成失败了，没有改动系统设置。\n\n" + string.Join("\n", built.Warnings),
                "应用失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        bool madeBackup = false;
        try
        {
            madeBackup = Store.EnsureBackup();
            CursorRegistry.Apply(built.Files, _ws.SchemeName);
        }
        catch (Exception ex)
        {
            SetStatus("应用失败：" + ex.Message, error: true);
            MessageBox.Show(this,
                "改注册表的时候出错了，系统指针可能没变。\n\n" + ex.Message,
                "应用失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string msg = $"已应用 {built.Files.Count} 个指针位。切到别的窗口看一眼效果。";
        if (built.Warnings.Count > 0)
            msg += $"（{built.Warnings.Count} 个失败）";

        SetStatus(msg + (madeBackup ? "  已自动备份原设置，随时可以「还原备份」。" : ""));

        if (built.Warnings.Count > 0)
            MessageBox.Show(this, "这些指针位没能生成：\n\n" + string.Join("\n", built.Warnings),
                "部分成功", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void RestoreBackup()
    {
        var snap = Store.ReadBackup();
        if (snap is null)
        {
            SetStatus("还没有备份。备份会在你第一次点「应用到系统」时自动生成。", error: true);
            return;
        }

        var ans = MessageBox.Show(this,
            $"还原到 {snap.CreatedAt} 备份的系统指针设置？（方案「{snap.SchemeName}」）\n\n" +
            "只影响系统指针，你在这里配的那些图片不会丢。",
            "还原备份", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ans != DialogResult.OK) return;

        try
        {
            CursorRegistry.Restore(snap);
            SetStatus($"已还原到 {snap.CreatedAt} 的指针设置。");
        }
        catch (Exception ex)
        {
            SetStatus("还原失败：" + ex.Message, error: true);
            MessageBox.Show(this, ex.Message, "还原失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestoreWindowsDefault()
    {
        var ans = MessageBox.Show(this,
            "把系统指针恢复成 Windows 自带的 Aero 方案？\n\n" +
            "· 会重置全部 17 个指针位\n" +
            "· 之前自动备份的那份不受影响，之后还能用「还原备份」找回\n",
            "恢复 Windows 默认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ans != DialogResult.OK) return;

        try
        {
            CursorRegistry.RestoreWindowsDefault();
            SetStatus("已恢复成 Windows 默认指针。");
        }
        catch (Exception ex)
        {
            SetStatus("恢复失败：" + ex.Message, error: true);
            MessageBox.Show(this, ex.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ================================================================ 方案

    private string AskName(string title, string initial)
    {
        using var dlg = new NameDialog(title, initial);
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.EnteredName : "";
    }

    private void SaveScheme()
    {
        string name = _schemeBox.Text.Trim();
        if (name.Length == 0) { SaveSchemeAs(); return; }

        SaveCurrentFromControls();
        _ws.SchemeName = name;
        Store.SaveScheme(_ws);
        RefreshSchemeList();
        SetStatus($"方案「{name}」已保存。");
    }

    private void SaveSchemeAs()
    {
        string name = AskName("另存为方案", _schemeBox.Text.Trim().Length > 0 ? _schemeBox.Text.Trim() : "我的指针方案");
        if (name.Length == 0) return;

        SaveCurrentFromControls();
        _ws.SchemeName = name;
        Store.SaveScheme(_ws);
        _schemeBox.Text = name;
        RefreshSchemeList();
        SetStatus($"方案「{name}」已保存。");
    }

    private void DeleteScheme()
    {
        string name = _schemeBox.Text.Trim();
        if (name.Length == 0 || !Store.ListSchemes().Contains(name))
        {
            SetStatus("当前名字不对应任何已保存的方案。", error: true);
            return;
        }

        if (MessageBox.Show(this, $"删除方案「{name}」？", "删除方案",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        Store.DeleteScheme(name);
        RefreshSchemeList();
        SetStatus($"方案「{name}」已删除。");
    }

    private void ImportThemePack()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选一个指针主题包文件夹（里面有 .cur / .ani，通常还带一个 install.inf）",
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        Store.ThemeImportResult result;
        try
        {
            result = Store.ImportTheme(dlg.SelectedPath, _ws);
        }
        catch (Exception ex)
        {
            SetStatus("导入主题包失败：" + ex.Message, error: true);
            MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (result.Applied.Count == 0)
        {
            SetStatus("这个文件夹里没找到能认出来的指针文件（.cur / .ani）。", error: true);
            MessageBox.Show(this,
                "这个文件夹里没有 .cur / .ani 文件，或者一个都没认出来。\n\n" +
                "主题包通常是解压出来的一整个文件夹，里面应该有 normal.cur、link.cur、busy.ani 这类文件。",
                "没认出来", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cache.Clear();
        RefreshSlotList();
        SelectSlot(_current);

        // 把"认到了哪儿、靠什么认的、还有什么没认出来"一次说清楚，
        // 用户才知道该不该手动补几个
        var sb = new StringBuilder();
        sb.AppendLine($"认出 {result.Applied.Count} 个指针位");
        sb.AppendLine(result.InfPath is null
            ? "（这个包没有 install.inf，是按文件名认的）"
            : $"（按包里的 {result.InfPath} 认的）");
        sb.AppendLine();

        foreach (var (_, display, source) in result.Applied)
            sb.AppendLine($"  · {display}　{(source == SlotMatchSource.InstallInf ? "包内指定" : "按文件名")}");

        if (result.Unmapped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"没认出来（{result.Unmapped.Count} 个，没有动它们）：");
            foreach (var name in result.Unmapped.Take(12)) sb.AppendLine("  · " + name);
            if (result.Unmapped.Count > 12) sb.AppendLine($"  · …还有 {result.Unmapped.Count - 12} 个");
        }

        sb.AppendLine();
        sb.AppendLine("这些位置是**原样使用**包里的文件（不改大小和热点）。");
        sb.AppendLine("点「应用到系统」生效。");

        MessageBox.Show(this, sb.ToString(), "导入主题包", MessageBoxButtons.OK, MessageBoxIcon.Information);
        SetStatus($"已从主题包认出 {result.Applied.Count} 个指针位。点「应用到系统」生效。");
    }

    private void ExportPack()
    {
        SaveCurrentFromControls();

        if (_ws.ConfiguredCount == 0)
        {
            SetStatus("还没有给任何指针位配图，没什么可导出的。", error: true);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "导出方案包",
            Filter = "指针方案包|*.zip",
            FileName = Store.Sanitize(_schemeBox.Text.Trim().Length > 0 ? _schemeBox.Text.Trim() : _ws.SchemeName) + ".zip",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var built = Store.BuildCursorFiles(_ws, _cache);
            Store.ExportPack(_ws, dlg.FileName, built);
            SetStatus($"已导出到 {dlg.FileName}（含 .cur 成品 + 可再次编辑的源图）。");
        }
        catch (Exception ex)
        {
            SetStatus("导出失败：" + ex.Message, error: true);
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportPack()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "导入方案包",
            Filter = "指针方案包|*.zip",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        Workspace ws;
        try
        {
            ws = Store.ImportPack(dlg.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("导入失败：" + ex.Message, error: true);
            MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cache.Clear();
        _ws = ws;
        _ws.SchemeName = Path.GetFileNameWithoutExtension(dlg.FileName);
        _schemeBox.Text = _ws.SchemeName;

        RefreshSlotList();
        SelectSlot(_current);

        if (ws.MissingImages.Count > 0)
        {
            string list = string.Join("、", ws.MissingImages);
            SetStatus($"方案包已导入，但 {ws.MissingImages.Count} 个指针位没带源图：{list}", error: true);
            MessageBox.Show(this,
                "方案包已导入，但下面这些指针位里没有源图，已经留空：\n\n  " + list +
                "\n\n其余指针位可以正常「应用到系统」。",
                "部分导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            SetStatus($"方案包「{_ws.SchemeName}」已导入，共 {_ws.ConfiguredCount} 个指针位。点「应用到系统」生效。");
        }
    }

    // ================================================================ 杂项

    private void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.FromArgb(190, 50, 50) : SystemColors.GrayText;
    }

    private void UpdateStatus() => UpdateStatusCore();

    private void UpdateStatusCore()
    {
        int n = _ws.ConfiguredCount;
        SetStatus(n == 0
            ? "还没有配任何指针。点左上角「导入图片」开始；也可以直接按 Ctrl+V 粘贴剪贴板里的图。"
            : $"当前方案「{_ws.SchemeName}」共配了 {n} / {CursorSlots.All.Count} 个指针位。");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Control && e.KeyCode == Keys.V)
        {
            PasteFromClipboard();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.S)
        {
            SaveScheme();
            e.Handled = true;
        }
    }
}
