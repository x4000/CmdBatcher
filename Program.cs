using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace CmdBatcher;

// ── Data ────────────────────────────────────────────────────────────────────

public class CommandPreset
{
    public string Label { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Command { get; set; } = "";
}

public class CommandGroup
{
    public string Name { get; set; } = "";
    public bool Expanded { get; set; } = true;
    public List<CommandPreset> Commands { get; set; } = new();
}

public enum SlotStatus { Idle, Running, Done, Error }

public class ProcessSlot
{
    public CommandPreset Preset;
    public SlotStatus Status = SlotStatus.Idle;
    public Process? Proc;
    public StreamWriter? StdinWriter;
    public ConcurrentQueue<string> OutputQueue = new();
    public List<string> OutputLines = new();
    public int? ExitCode;
    public DateTime? StartTime;
    public DateTime? EndTime;
    public const int MaxLines = 800;

    public ProcessSlot(CommandPreset preset) => Preset = preset;

    public void AppendLine(string line)
    {
        OutputQueue.Enqueue(line);
    }

    public bool DrainQueue()
    {
        bool any = false;
        while (OutputQueue.TryDequeue(out string? line))
        {
            OutputLines.Add(line);
            if (OutputLines.Count > MaxLines)
                OutputLines.RemoveAt(0);
            any = true;
        }
        return any;
    }

    public string Elapsed
    {
        get
        {
            if (StartTime == null) return "";
            DateTime end = EndTime ?? DateTime.Now;
            TimeSpan span = end - StartTime.Value;
            string t = span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes:D2}m {span.Seconds:D2}s"
                : span.TotalMinutes >= 1
                    ? $"{span.Minutes}m {span.Seconds:D2}s"
                    : $"{span.Seconds}s";
            if (ExitCode.HasValue) t += $"  (exit {ExitCode})";
            return t;
        }
    }
}

// ── Preset I/O ──────────────────────────────────────────────────────────────

public static class PresetStore
{
    static readonly string Path = System.IO.Path.Combine(
        AppContext.BaseDirectory, "_user_session.json");

    static readonly string LegacyPath = System.IO.Path.Combine(
        AppContext.BaseDirectory, "cmdbatcher_presets.json");

    public static List<CommandGroup> Load()
    {
        try
        {
            // Migrate legacy file if new one doesn't exist yet
            if (!File.Exists(Path) && File.Exists(LegacyPath))
                File.Move(LegacyPath, Path);

            if (File.Exists(Path))
            {
                string json = File.ReadAllText(Path).TrimStart();

                // Try loading as List<CommandGroup> first
                List<CommandGroup>? groups = null;
                try
                {
                    groups = JsonSerializer.Deserialize<List<CommandGroup>>(json);
                    // Validate it's actually groups (not flat presets misinterpreted)
                    if (groups != null && groups.Count > 0 && groups[0].Commands != null && groups[0].Commands.Count >= 0
                        && !string.IsNullOrEmpty(groups[0].Name))
                        return groups;
                }
                catch { }

                // Fall back: try loading as flat List<CommandPreset> and migrate
                try
                {
                    List<CommandPreset>? flat = JsonSerializer.Deserialize<List<CommandPreset>>(json);
                    if (flat != null && flat.Count > 0)
                    {
                        List<CommandGroup> migrated = new List<CommandGroup>
                        {
                            new CommandGroup { Name = "Default", Expanded = true, Commands = flat }
                        };
                        Save(migrated); // write back in new format
                        return migrated;
                    }
                }
                catch { }
            }
        }
        catch { }
        return new();
    }

    public static void Save(List<CommandGroup> groups)
    {
        try
        {
            File.WriteAllText(Path,
                JsonSerializer.Serialize(groups, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

// ── Card Control ────────────────────────────────────────────────────────────

public class CommandCard : Panel
{
    static readonly Color BgCard    = Color.FromArgb(40, 40, 64);
    static readonly Color BgInput   = Color.FromArgb(30, 30, 48);
    static readonly Color FgMain    = Color.FromArgb(205, 214, 244);
    static readonly Color FgDim     = Color.FromArgb(108, 112, 134);
    static readonly Color FgAccent  = Color.FromArgb(137, 180, 250);
    static readonly Color FgYellow  = Color.FromArgb(249, 226, 175);
    static readonly Color FgGreen   = Color.FromArgb(166, 227, 161);
    static readonly Color FgRed     = Color.FromArgb(243, 139, 168);
    static readonly Color Border    = Color.FromArgb(69, 71, 90);

    public ProcessSlot Slot;
    public event Action? OnRun, OnStop, OnPeek, OnRemove, OnChanged;
    public event Action<Button>? OnMoveRequested;

    Label dotLabel, statusLabel, timeLabel;
    TextBox labelBox, folderBox, cmdBox;
    bool _selected;

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public CommandCard(ProcessSlot slot)
    {
        Slot = slot;
        DoubleBuffered = true;
        BackColor = BgCard;
        Height = 120;
        Margin = new Padding(0, 2, 0, 2);
        Padding = new Padding(10, 8, 10, 8);

        // Row 0: dot + label + buttons
        dotLabel = new Label
        {
            Text = "●", ForeColor = FgDim, BackColor = BgCard,
            Font = new Font("Segoe UI", 12), AutoSize = true,
            Location = new Point(10, 8),
        };

        labelBox = MakeInput(slot.Preset.Label, new Font("Segoe UI Semibold", 11), FgAccent);
        labelBox.Location = new Point(52, 8);
        labelBox.TextChanged += (object? s, EventArgs a) => { slot.Preset.Label = labelBox.Text; OnChanged?.Invoke(); };

        Button btnRun  = MakeBtn("Run", FgGreen, 42, 26);
        Button btnStop = MakeBtn("Stop", FgRed, 42, 26);
        Button btnPeek = MakeBtn("Peek", FgMain, 42, 26);

        btnRun.Click  += (object? s, EventArgs a) => OnRun?.Invoke();
        btnStop.Click += (object? s, EventArgs a) => OnStop?.Invoke();
        btnPeek.Click += (object? s, EventArgs a) => OnPeek?.Invoke();

        // Row 1: folder
        Label folderIcon = new Label
        {
            Text = "Dir:", BackColor = BgCard, ForeColor = FgDim,
            Font = new Font("Segoe UI", 9), AutoSize = true,
            Location = new Point(10, 44),
        };
        folderBox = MakeInput(slot.Preset.Folder, new Font("Cascadia Mono", 9), FgMain);
        folderBox.Location = new Point(52, 42);
        folderBox.TextChanged += (object? s, EventArgs a) => { slot.Preset.Folder = folderBox.Text; OnChanged?.Invoke(); };

        Button btnBrowse = MakeBtn("...", FgMain, 36, 24);
        btnBrowse.Click += (object? s, EventArgs a) =>
        {
            using FolderBrowserDialog dlg = new FolderBrowserDialog();
            if (Directory.Exists(folderBox.Text)) dlg.SelectedPath = folderBox.Text;
            if (dlg.ShowDialog() == DialogResult.OK) folderBox.Text = dlg.SelectedPath;
        };

        // Row 2: command
        Label cmdIcon = new Label
        {
            Text = "Cmd:", BackColor = BgCard, ForeColor = FgDim,
            Font = new Font("Segoe UI", 9), AutoSize = true,
            Location = new Point(10, 70),
        };
        cmdBox = MakeInput(slot.Preset.Command, new Font("Cascadia Mono", 9), FgYellow);
        cmdBox.Location = new Point(52, 68);
        cmdBox.TextChanged += (object? s, EventArgs a) => { slot.Preset.Command = cmdBox.Text; OnChanged?.Invoke(); };

        // Row 3: status + time + move + remove
        statusLabel = new Label
        {
            Text = "idle", ForeColor = FgDim, BackColor = BgCard,
            Font = new Font("Segoe UI", 9), AutoSize = true,
            Location = new Point(10, 96),
        };
        timeLabel = new Label
        {
            Text = "", ForeColor = FgDim, BackColor = BgCard,
            Font = new Font("Segoe UI", 9), AutoSize = true,
            Location = new Point(80, 96),
        };
        Button btnMove = MakeBtn("Move", FgDim, 50, 24);
        btnMove.Click += (object? s, EventArgs a) => OnMoveRequested?.Invoke(btnMove);

        Button btnRemove = MakeBtn("Remove", FgDim, 64, 24);
        btnRemove.Click += (object? s, EventArgs a) => OnRemove?.Invoke();

        Controls.AddRange(new Control[] {
            dotLabel, labelBox, btnRun, btnStop, btnPeek,
            folderIcon, folderBox, btnBrowse,
            cmdIcon, cmdBox,
            statusLabel, timeLabel, btnMove, btnRemove,
        });

        // Store buttons for layout
        _topButtons = new Button[] { btnPeek, btnStop, btnRun };
        _browseBtn = btnBrowse;
        _moveBtn = btnMove;
        _removeBtn = btnRemove;

        // Click on the card background or non-interactive labels to select
        Click += (object? s, EventArgs a) => OnPeek?.Invoke();
        dotLabel.Click += (object? s, EventArgs a) => OnPeek?.Invoke();
        folderIcon.Click += (object? s, EventArgs a) => OnPeek?.Invoke();
        cmdIcon.Click += (object? s, EventArgs a) => OnPeek?.Invoke();
        statusLabel.Click += (object? s, EventArgs a) => OnPeek?.Invoke();
        timeLabel.Click += (object? s, EventArgs a) => OnPeek?.Invoke();

        Resize += (object? s, EventArgs a) => DoLayout();
        DoLayout();
    }

    Button[] _topButtons;
    Button _browseBtn, _moveBtn, _removeBtn;

    void DoLayout()
    {
        int w = ClientSize.Width;
        if (w < 50) return; // not yet sized

        int rightX = w - 10;
        foreach (Button b in _topButtons)
        {
            rightX -= b.Width + 2;
            b.Location = new Point(rightX, 6);
        }
        labelBox.Width = Math.Max(60, rightX - labelBox.Left - 8);

        _browseBtn.Location = new Point(w - 10 - _browseBtn.Width, 40);
        folderBox.Width = Math.Max(60, _browseBtn.Left - folderBox.Left - 4);

        cmdBox.Width = Math.Max(60, w - cmdBox.Left - 14);

        _removeBtn.Location = new Point(w - 10 - _removeBtn.Width, 94);
        _moveBtn.Location = new Point(_removeBtn.Left - _moveBtn.Width - 4, 94);
    }

    TextBox MakeInput(string text, Font font, Color fg) => new TextBox
    {
        Text = text, Font = font, ForeColor = fg,
        BackColor = BgInput, BorderStyle = BorderStyle.FixedSingle,
        Height = 50
    };

    Button MakeBtn(string text, Color fg, int width, int height) => new Button
    {
        Text = text, ForeColor = fg, BackColor = BgCard,
        FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8),
        Size = new Size(width, height),
        FlatAppearance = { BorderSize = 1, BorderColor = Border, MouseOverBackColor = Color.FromArgb(60, 60, 90) },
    };

    static Color StatusColor(SlotStatus s) => s switch
    {
        SlotStatus.Running => Color.FromArgb(137, 180, 250),
        SlotStatus.Done    => Color.FromArgb(166, 227, 161),
        SlotStatus.Error   => Color.FromArgb(243, 139, 168),
        _                  => Color.FromArgb(108, 112, 134),
    };

    public void UpdateStatus()
    {
        dotLabel.ForeColor = StatusColor(Slot.Status);
        statusLabel.Text = Slot.Status.ToString().ToLower();
        timeLabel.Text = Slot.Elapsed;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Color color = _selected ? FgAccent : Border;
        using Pen pen = new Pen(color, _selected ? 2f : 1f);
        Rectangle r = ClientRectangle;
        r.Inflate(-1, -1);
        e.Graphics.DrawRectangle(pen, r);
    }
}

// ── Group Header Control ───────────────────────────────────────────────────

public class GroupHeaderPanel : Panel
{
    static readonly Color Bg        = Color.FromArgb(30, 30, 46);
    static readonly Color BgHeader  = Color.FromArgb(35, 35, 55);
    static readonly Color FgMain    = Color.FromArgb(205, 214, 244);
    static readonly Color FgAccent  = Color.FromArgb(137, 180, 250);
    static readonly Color FgGreen   = Color.FromArgb(166, 227, 161);
    static readonly Color FgDim     = Color.FromArgb(108, 112, 134);
    static readonly Color Border    = Color.FromArgb(69, 71, 90);

    public event Action? OnToggle, OnAddCommand, OnRunGroup, OnRenameGroup, OnRemoveGroup;

    Label _arrowLabel;
    TextBox _nameBox;

    public GroupHeaderPanel(CommandGroup group)
    {
        Height = 34;
        BackColor = BgHeader;
        Margin = new Padding(0, 6, 0, 0);

        _arrowLabel = new Label
        {
            Text = group.Expanded ? "▼" : "▶",
            ForeColor = FgAccent,
            BackColor = BgHeader,
            Font = new Font("Segoe UI", 10),
            AutoSize = true,
            Location = new Point(8, 7),
            Cursor = Cursors.Hand,
        };
        _arrowLabel.Click += (object? s, EventArgs a) => OnToggle?.Invoke();

        _nameBox = new TextBox
        {
            Text = group.Name,
            ForeColor = FgAccent,
            BackColor = BgHeader,
            Font = new Font("Segoe UI Semibold", 11),
            BorderStyle = BorderStyle.None,
            Location = new Point(28, 5),
            Height = 24,
        };
        _nameBox.TextChanged += (object? s, EventArgs a) =>
        {
            group.Name = _nameBox.Text;
            OnRenameGroup?.Invoke();
        };
        _nameBox.Click += (object? s, EventArgs a) => { }; // absorb click so it doesn't toggle

        Button btnAdd = MakeHeaderBtn("+ Add Cmd", FgMain, 80);
        btnAdd.Click += (object? s, EventArgs a) => OnAddCommand?.Invoke();

        Button btnRunGroup = MakeHeaderBtn("Run Group", FgGreen, 80);
        btnRunGroup.Click += (object? s, EventArgs a) => OnRunGroup?.Invoke();

        Button btnRemoveGroup = MakeHeaderBtn("Del Group", FgDim, 72);
        btnRemoveGroup.Click += (object? s, EventArgs a) => OnRemoveGroup?.Invoke();

        Controls.AddRange(new Control[] { _arrowLabel, _nameBox, btnAdd, btnRunGroup, btnRemoveGroup });

        _rightButtons = new Button[] { btnRemoveGroup, btnRunGroup, btnAdd };

        Resize += (object? s, EventArgs a) => DoLayout();
        DoLayout();
    }

    Button[] _rightButtons;

    void DoLayout()
    {
        int w = ClientSize.Width;
        if (w < 50) return;

        int rightX = w - 8;
        foreach (Button b in _rightButtons)
        {
            rightX -= b.Width + 3;
            b.Location = new Point(rightX, 4);
        }
        _nameBox.Width = Math.Max(60, rightX - _nameBox.Left - 8);
    }

    Button MakeHeaderBtn(string text, Color fg, int width) => new Button
    {
        Text = text, ForeColor = fg, BackColor = BgHeader,
        FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8),
        Size = new Size(width, 26),
        FlatAppearance = { BorderSize = 1, BorderColor = Border, MouseOverBackColor = Color.FromArgb(60, 60, 90) },
    };
}

// ── Main Form ───────────────────────────────────────────────────────────────

public class MainForm : Form
{
    static readonly Color Bg      = Color.FromArgb(30, 30, 46);
    static readonly Color BgInput = Color.FromArgb(30, 30, 48);
    static readonly Color FgMain  = Color.FromArgb(205, 214, 244);
    static readonly Color FgDim   = Color.FromArgb(108, 112, 134);
    static readonly Color FgAccent= Color.FromArgb(137, 180, 250);
    static readonly Color FgGreen = Color.FromArgb(166, 227, 161);
    static readonly Color FgRed   = Color.FromArgb(243, 139, 168);

    List<CommandGroup> _groups;

    // Nested by group: _slots[g][c], _cards[g][c]
    List<List<ProcessSlot>> _slots = new();
    List<List<CommandCard>> _cards = new();

    int _selGroup = -1, _selCmd = -1;
    int _outputLineCount;
    int _ticksSinceFullRefresh;
    bool _dirty;

    FlowLayoutPanel _listPanel;
    RichTextBox _outputBox;
    Label _outputHeaderLabel;
    TextBox _stdinBox;
    System.Windows.Forms.Timer _timer;

    public MainForm()
    {
        Text = "Cmd Batcher";
        BackColor = Bg;
        ForeColor = FgMain;
        Size = new Size(1600, 760);
        MinimumSize = new Size(1000, 500);
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;

        // Load app icon for window title bar and taskbar
        string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);

        _groups = PresetStore.Load();
        if (_groups.Count == 0)
        {
            _groups.Add(new CommandGroup
            {
                Name = "Default",
                Expanded = true,
                Commands = new List<CommandPreset>
                {
                    new CommandPreset
                    {
                        Label = "Example",
                        Folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        Command = "echo Hello from Cmd Batcher!"
                    }
                }
            });
        }

        BuildUI();
        RebuildCards();

        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += Tick;
        _timer.Start();

        FormClosing += (object? s, FormClosingEventArgs a) =>
        {
            SaveAndStopAll();
            _timer.Stop();
        };
    }

    void BuildUI()
    {
        // ── Top bar ──
        Panel topBar = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Bg };

        Label title = new Label
        {
            Text = "Cmd Batcher", ForeColor = FgAccent,
            Font = new Font("Segoe UI Semibold", 14), AutoSize = true,
            Location = new Point(16, 12),
        };
        topBar.Controls.Add(title);

        Button btnAddGroup = MakeTopBtn("+ Add Group", FgMain);
        Button btnRunAll = MakeTopBtn("Run All", FgGreen);
        Button btnStopAll = MakeTopBtn("Stop All", FgRed);

        btnAddGroup.Click += (object? s, EventArgs a) => AddGroup();
        btnRunAll.Click += (object? s, EventArgs a) => RunAll();
        btnStopAll.Click += (object? s, EventArgs a) => StopAll();

        // Use a right-aligned FlowLayoutPanel for top buttons
        FlowLayoutPanel btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = false,
            BackColor = Bg,
            Padding = new Padding(0, 6, 8, 0),
            Width = 360,
        };
        btnPanel.Controls.Add(btnStopAll);
        btnPanel.Controls.Add(btnRunAll);
        btnPanel.Controls.Add(btnAddGroup);
        topBar.Controls.Add(btnPanel);

        // ── Split panel ──
        SplitContainer split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = Bg,
            SplitterWidth = 6,
            Width = 1600,
            Panel1MinSize = 450,
            Panel2MinSize = 400,
        };
        split.Panel1.BackColor = Bg;
        split.Panel2.BackColor = Bg;

        // Set splitter position after layout so percentages work correctly
        split.SplitterDistance = 480;
        Shown += (object? s, EventArgs a) =>
        {
            // Give left panel ~35% of width, output gets ~65%
            split.SplitterDistance = (int)(split.ClientSize.Width * 0.35);
        };

        // Left: scrollable card list
        _listPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Bg,
            Padding = new Padding(4),
        };
        split.Panel1.Controls.Add(_listPanel);

        // Right: output viewer

        // ── Output header bar (label + refresh/clear buttons) ──
        Panel outputHeaderBar = new Panel
        {
            Dock = DockStyle.Top, Height = 32, BackColor = Bg,
        };

        _outputHeaderLabel = new Label
        {
            Text = "Select a command to view output",
            ForeColor = FgDim, BackColor = Bg,
            Font = new Font("Segoe UI", 10),
            AutoSize = true,
            Location = new Point(4, 6),
        };

        Button btnRefresh = new Button
        {
            Text = "Refresh", ForeColor = FgMain, BackColor = Color.FromArgb(40, 40, 64),
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8),
            Size = new Size(60, 26), Dock = DockStyle.Right,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(60, 60, 90) },
        };
        btnRefresh.Click += (object? s, EventArgs a) =>
        {
            _outputLineCount = 0; // force full re-render on next tick
        };

        Button btnClear = new Button
        {
            Text = "Clear", ForeColor = FgDim, BackColor = Color.FromArgb(40, 40, 64),
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8),
            Size = new Size(50, 26), Dock = DockStyle.Right,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(60, 60, 90) },
        };
        btnClear.Click += (object? s, EventArgs a) =>
        {
            if (_selGroup >= 0 && _selGroup < _slots.Count
                && _selCmd >= 0 && _selCmd < _slots[_selGroup].Count)
            {
                _slots[_selGroup][_selCmd].OutputLines.Clear();
            }
            _outputLineCount = 0;
            _outputBox.Text = "";
        };

        outputHeaderBar.Controls.Add(_outputHeaderLabel);
        outputHeaderBar.Controls.Add(btnRefresh);
        outputHeaderBar.Controls.Add(btnClear);

        // ── Stdin input bar ──
        Panel stdinBar = new Panel
        {
            Dock = DockStyle.Bottom, Height = 32, BackColor = Bg,
        };

        _stdinBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = BgInput, ForeColor = FgMain,
            Font = new Font("Cascadia Mono", 10),
            BorderStyle = BorderStyle.FixedSingle,
        };
        _stdinBox.KeyDown += (object? s, KeyEventArgs a) =>
        {
            if (a.KeyCode == Keys.Enter)
            {
                a.SuppressKeyPress = true;
                SendStdinText();
            }
        };

        Button btnSend = new Button
        {
            Text = "Send", ForeColor = FgMain, BackColor = Color.FromArgb(40, 40, 64),
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9),
            Size = new Size(60, 30), Dock = DockStyle.Right,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(60, 60, 90) },
        };
        btnSend.Click += (object? s, EventArgs a) => SendStdinText();

        stdinBar.Controls.Add(_stdinBox);
        stdinBar.Controls.Add(btnSend);

        // ── Output text box ──
        _outputBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = BgInput, ForeColor = FgMain,
            Font = new Font("Cascadia Mono", 10),
            ReadOnly = true, BorderStyle = BorderStyle.None,
            WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical,
        };

        // Add in correct dock order: bottom first, top second, fill last
        split.Panel2.Controls.Add(_outputBox);
        split.Panel2.Controls.Add(stdinBar);
        split.Panel2.Controls.Add(outputHeaderBar);

        // Add split first, then topBar — WinForms docks last-added first,
        // so topBar claims its 50px before split fills the remainder.
        Controls.Add(split);
        Controls.Add(topBar);

        // Resize cards and headers to fill width
        _listPanel.Resize += (object? s, EventArgs a) =>
        {
            int w = _listPanel.ClientSize.Width - 30;
            foreach (Control ctrl in _listPanel.Controls)
                ctrl.Width = w;
        };
    }

    Button MakeTopBtn(string text, Color fg) => new Button
    {
        Text = text, ForeColor = fg, BackColor = Color.FromArgb(40, 40, 64),
        FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10),
        AutoSize = true, MinimumSize = new Size(70, 32),
        Padding = new Padding(8, 2, 8, 2),
        Margin = new Padding(3, 0, 3, 0),
        FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(60, 60, 90) },
    };

    // ── Card management ────────────────────────────────────────────────────

    void RebuildCards()
    {
        _listPanel.SuspendLayout();
        _listPanel.Controls.Clear();
        _cards.Clear();
        _slots.Clear();

        int w = _listPanel.ClientSize.Width - 30;

        for (int g = 0; g < _groups.Count; g++)
        {
            CommandGroup group = _groups[g];
            int gIdx = g;

            // Group header
            GroupHeaderPanel header = new GroupHeaderPanel(group) { Width = w };
            header.OnToggle += () =>
            {
                group.Expanded = !group.Expanded;
                _dirty = true;
                RebuildCards();
            };
            header.OnAddCommand += () => AddCommand(gIdx);
            header.OnRunGroup += () => RunGroup(gIdx);
            header.OnRenameGroup += () => _dirty = true;
            header.OnRemoveGroup += () => RemoveGroup(gIdx);
            _listPanel.Controls.Add(header);

            // Cards for this group
            List<ProcessSlot> groupSlots = new List<ProcessSlot>();
            List<CommandCard> groupCards = new List<CommandCard>();

            if (group.Expanded)
            {
                for (int c = 0; c < group.Commands.Count; c++)
                {
                    ProcessSlot slot = new ProcessSlot(group.Commands[c]);
                    groupSlots.Add(slot);

                    CommandCard card = new CommandCard(slot) { Width = w };
                    int cIdx = c;
                    card.OnRun     += () => RunOne(gIdx, cIdx);
                    card.OnStop    += () => StopOne(gIdx, cIdx);
                    card.OnPeek    += () => SelectCard(gIdx, cIdx);
                    card.OnRemove  += () => RemoveCommand(gIdx, cIdx);
                    card.OnChanged += () => _dirty = true;
                    card.OnMoveRequested += (Button btn) => ShowMoveMenu(btn, gIdx, cIdx);
                    groupCards.Add(card);
                    _listPanel.Controls.Add(card);
                }
            }

            _slots.Add(groupSlots);
            _cards.Add(groupCards);
        }
        _listPanel.ResumeLayout();
    }

    void AddGroup()
    {
        _groups.Add(new CommandGroup
        {
            Name = "New Group",
            Expanded = true,
            Commands = new List<CommandPreset>
            {
                new CommandPreset
                {
                    Label = "New Command",
                    Folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Command = "echo hello"
                }
            }
        });
        PresetStore.Save(_groups);
        RebuildCards();
    }

    void RemoveGroup(int g)
    {
        // Stop all running commands in this group
        if (g < _slots.Count)
        {
            for (int c = 0; c < _slots[g].Count; c++)
                StopOne(g, c);
        }
        _groups.RemoveAt(g);
        if (_selGroup == g) { _selGroup = -1; _selCmd = -1; }
        PresetStore.Save(_groups);
        RebuildCards();
    }

    void AddCommand(int g)
    {
        _groups[g].Commands.Add(new CommandPreset
        {
            Label = "New Command",
            Folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Command = "echo hello"
        });
        _groups[g].Expanded = true;
        PresetStore.Save(_groups);
        RebuildCards();
    }

    void RemoveCommand(int g, int c)
    {
        if (g < _slots.Count && c < _slots[g].Count && _slots[g][c].Status == SlotStatus.Running)
            StopOne(g, c);
        _groups[g].Commands.RemoveAt(c);
        PresetStore.Save(_groups);
        if (_selGroup == g && _selCmd == c) { _selGroup = -1; _selCmd = -1; }
        RebuildCards();
    }

    void ShowMoveMenu(Button btn, int fromGroup, int fromCmd)
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.BackColor = Color.FromArgb(40, 40, 64);
        menu.ForeColor = Color.FromArgb(205, 214, 244);

        for (int g = 0; g < _groups.Count; g++)
        {
            if (g == fromGroup) continue;
            int targetG = g;
            ToolStripMenuItem item = new ToolStripMenuItem(_groups[g].Name);
            item.ForeColor = Color.FromArgb(205, 214, 244);
            item.BackColor = Color.FromArgb(40, 40, 64);
            item.Click += (object? s, EventArgs a) =>
            {
                CommandPreset preset = _groups[fromGroup].Commands[fromCmd];
                _groups[fromGroup].Commands.RemoveAt(fromCmd);
                _groups[targetG].Commands.Add(preset);
                if (_selGroup == fromGroup && _selCmd == fromCmd)
                {
                    _selGroup = -1;
                    _selCmd = -1;
                }
                PresetStore.Save(_groups);
                RebuildCards();
            };
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            ToolStripMenuItem noOther = new ToolStripMenuItem("(no other groups)");
            noOther.Enabled = false;
            menu.Items.Add(noOther);
        }

        menu.Show(btn, new Point(0, btn.Height));
    }

    void SelectCard(int g, int c)
    {
        _selGroup = g;
        _selCmd = c;
        _outputLineCount = 0; // force full refresh of output pane
        for (int gi = 0; gi < _cards.Count; gi++)
            for (int ci = 0; ci < _cards[gi].Count; ci++)
                _cards[gi][ci].Selected = (gi == g && ci == c);
    }

    // ── Process control ────────────────────────────────────────────────────

    void RunOne(int g, int c)
    {
        if (g >= _slots.Count || c >= _slots[g].Count) return;
        ProcessSlot slot = _slots[g][c];
        if (slot.Status == SlotStatus.Running) return;

        slot.OutputLines.Clear();
        while (slot.OutputQueue.TryDequeue(out _)) { }
        slot.Status = SlotStatus.Running;
        slot.ExitCode = null;
        slot.StartTime = DateTime.Now;
        slot.EndTime = null;
        PresetStore.Save(_groups);

        Thread t = new Thread(() =>
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C {slot.Preset.Command}",
                    WorkingDirectory = string.IsNullOrWhiteSpace(slot.Preset.Folder)
                        ? null : slot.Preset.Folder,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                slot.Proc = Process.Start(psi)!;
                slot.StdinWriter = slot.Proc.StandardInput;

                // Read stdout and stderr on separate threads
                Thread stdoutThread = new Thread(() =>
                {
                    try { while (slot.Proc.StandardOutput.ReadLine() is { } line) slot.AppendLine(line); }
                    catch { }
                })
                { IsBackground = true };

                Thread stderrThread = new Thread(() =>
                {
                    try { while (slot.Proc.StandardError.ReadLine() is { } line) slot.AppendLine(line); }
                    catch { }
                })
                { IsBackground = true };

                stdoutThread.Start();
                stderrThread.Start();
                slot.Proc.WaitForExit();
                stdoutThread.Join(2000);
                stderrThread.Join(2000);

                slot.ExitCode = slot.Proc.ExitCode;
                slot.Status = slot.ExitCode == 0 ? SlotStatus.Done : SlotStatus.Error;
            }
            catch (Exception ex)
            {
                slot.AppendLine($"[Runner Error] {ex.Message}");
                slot.Status = SlotStatus.Error;
            }
            finally
            {
                slot.EndTime = DateTime.Now;
            }
        })
        { IsBackground = true };
        t.Start();

        // Auto-peek at this command's output
        SelectCard(g, c);
    }

    void StopOne(int g, int c)
    {
        if (g >= _slots.Count || c >= _slots[g].Count) return;
        ProcessSlot slot = _slots[g][c];
        if (slot.Proc != null && slot.Status == SlotStatus.Running)
        {
            try { slot.Proc.Kill(true); } catch { }
            slot.AppendLine("[Terminated by user]");
            slot.Status = SlotStatus.Error;
            slot.EndTime = DateTime.Now;
        }
    }

    void RunGroup(int g)
    {
        if (g >= _slots.Count) return;
        for (int c = 0; c < _slots[g].Count; c++)
            RunOne(g, c);
    }

    void RunAll()
    {
        for (int g = 0; g < _slots.Count; g++)
            for (int c = 0; c < _slots[g].Count; c++)
                RunOne(g, c);
    }

    void StopAll()
    {
        for (int g = 0; g < _slots.Count; g++)
            for (int c = 0; c < _slots[g].Count; c++)
                StopOne(g, c);
    }

    void SendStdinText()
    {
        string text = _stdinBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        if (_selGroup >= 0 && _selGroup < _slots.Count
            && _selCmd >= 0 && _selCmd < _slots[_selGroup].Count)
        {
            ProcessSlot slot = _slots[_selGroup][_selCmd];
            if (slot.Status == SlotStatus.Running && slot.StdinWriter != null)
            {
                try
                {
                    slot.StdinWriter.WriteLine(text);
                    slot.StdinWriter.Flush();
                    slot.AppendLine($"> {text}");
                }
                catch { }
            }
        }
        _stdinBox.Text = "";
        _stdinBox.Focus();
    }

    void SaveAndStopAll()
    {
        PresetStore.Save(_groups);
        StopAll();
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    void Tick(object? sender, EventArgs e)
    {
        if (_dirty)
        {
            _dirty = false;
            PresetStore.Save(_groups);
        }

        for (int g = 0; g < _cards.Count; g++)
        {
            for (int c = 0; c < _cards[g].Count; c++)
            {
                _slots[g][c].DrainQueue();
                _cards[g][c].UpdateStatus();
            }
        }

        if (_selGroup >= 0 && _selGroup < _slots.Count
            && _selCmd >= 0 && _selCmd < _slots[_selGroup].Count)
        {
            ProcessSlot slot = _slots[_selGroup][_selCmd];
            _outputHeader.Text = $"Output: {slot.Preset.Label}";
            _outputHeader.ForeColor = slot.Status switch
            {
                SlotStatus.Running => FgAccent,
                SlotStatus.Done => FgGreen,
                SlotStatus.Error => FgRed,
                _ => FgDim,
            };

            int totalLines = slot.OutputLines.Count;
            if (totalLines != _outputLineCount)
            {
                if (_outputLineCount == 0)
                {
                    // Full refresh (new selection or first output)
                    StringBuilder sb = new StringBuilder();
                    foreach (string line in slot.OutputLines)
                        sb.AppendLine(line);
                    _outputBox.Text = sb.ToString();
                }
                else
                {
                    // Append only new lines
                    StringBuilder sb = new StringBuilder();
                    for (int n = _outputLineCount; n < totalLines; n++)
                        sb.AppendLine(slot.OutputLines[n]);
                    _outputBox.AppendText(sb.ToString());
                }
                _outputLineCount = totalLines;
                _outputBox.SelectionStart = _outputBox.TextLength;
                _outputBox.ScrollToCaret();
            }
        }
    }
}

// ── Entry point ─────────────────────────────────────────────────────────────

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
