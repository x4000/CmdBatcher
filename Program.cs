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

public enum SlotStatus { Idle, Running, Done, Error }

public class ProcessSlot
{
    public CommandPreset Preset;
    public SlotStatus Status = SlotStatus.Idle;
    public Process? Proc;
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

    public static List<CommandPreset> Load()
    {
        try
        {
            // Migrate legacy file if new one doesn't exist yet
            if (!File.Exists(Path) && File.Exists(LegacyPath))
                File.Move(LegacyPath, Path);

            if (File.Exists(Path))
                return JsonSerializer.Deserialize<List<CommandPreset>>(
                    File.ReadAllText(Path)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(List<CommandPreset> presets)
    {
        try
        {
            File.WriteAllText(Path,
                JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true }));
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
        Margin = new Padding(0, 3, 0, 3);
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

        // Row 3: status + time + remove
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
        Button btnRemove = MakeBtn("Remove", FgDim, 64, 24);
        btnRemove.Click += (object? s, EventArgs a) => OnRemove?.Invoke();

        Controls.AddRange(new Control[] {
            dotLabel, labelBox, btnRun, btnStop, btnPeek,
            folderIcon, folderBox, btnBrowse,
            cmdIcon, cmdBox,
            statusLabel, timeLabel, btnRemove,
        });

        // Store buttons for layout
        _topButtons = new Button[] { btnPeek, btnStop, btnRun };
        _browseBtn = btnBrowse;
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
    Button _browseBtn, _removeBtn;

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

    List<CommandPreset> _presets;
    List<ProcessSlot> _slots = new();
    List<CommandCard> _cards = new();
    int _selectedIndex = -1;
    bool _dirty;

    FlowLayoutPanel _listPanel;
    RichTextBox _outputBox;
    Label _outputHeader;
    System.Windows.Forms.Timer _timer;

    public MainForm()
    {
        Text = "Cmd Batcher";
        BackColor = Bg;
        ForeColor = FgMain;
        Size = new Size(1200, 760);
        MinimumSize = new Size(850, 500);
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;

        // Load app icon for window title bar and taskbar
        string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);

        _presets = PresetStore.Load();
        if (_presets.Count == 0)
            _presets.Add(new CommandPreset
            {
                Label = "Example",
                Folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Command = "echo Hello from Cmd Batcher!"
            });

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

        Button btnAdd = MakeTopBtn("+ Add", FgMain);
        Button btnRunAll = MakeTopBtn("Run All", FgGreen);
        Button btnStopAll = MakeTopBtn("Stop All", FgRed);

        btnAdd.Click += (object? s, EventArgs a) => { AddEntry(); };
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
            Width = 300
        };
        btnPanel.Controls.Add(btnStopAll);
        btnPanel.Controls.Add(btnRunAll);
        btnPanel.Controls.Add(btnAdd);
        topBar.Controls.Add(btnPanel);

        // ── Split panel ──
        SplitContainer split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = Bg,
            SplitterDistance = 540,
            SplitterWidth = 6,
        };
        split.Panel1.BackColor = Bg;
        split.Panel2.BackColor = Bg;

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
        _outputHeader = new Label
        {
            Text = "Select a command to view output",
            ForeColor = FgDim, BackColor = Bg,
            Font = new Font("Segoe UI", 10),
            Dock = DockStyle.Top, Height = 28,
            Padding = new Padding(4, 4, 0, 0),
        };
        _outputBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = BgInput, ForeColor = FgMain,
            Font = new Font("Cascadia Mono", 10),
            ReadOnly = true, BorderStyle = BorderStyle.None,
            WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical,
        };
        split.Panel2.Controls.Add(_outputBox);
        split.Panel2.Controls.Add(_outputHeader);

        // Add split first, then topBar — WinForms docks last-added first,
        // so topBar claims its 50px before split fills the remainder.
        Controls.Add(split);
        Controls.Add(topBar);

        // Resize cards to fill width
        _listPanel.Resize += (object? s, EventArgs a) =>
        {
            int w = _listPanel.ClientSize.Width - 30;
            foreach (CommandCard c in _cards) c.Width = w;
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
        for (int i = 0; i < _presets.Count; i++)
        {
            ProcessSlot slot = new ProcessSlot(_presets[i]);
            _slots.Add(slot);
            CommandCard card = new CommandCard(slot) { Width = w };
            int idx = i;
            card.OnRun     += () => RunOne(idx);
            card.OnStop    += () => StopOne(idx);
            card.OnPeek    += () => SelectCard(idx);
            card.OnRemove  += () => RemoveEntry(idx);
            card.OnChanged += () => _dirty = true;
            _cards.Add(card);
            _listPanel.Controls.Add(card);
        }
        _listPanel.ResumeLayout();
    }

    void AddEntry()
    {
        _presets.Add(new CommandPreset
        {
            Label = "New Command",
            Folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Command = "echo hello"
        });
        PresetStore.Save(_presets);
        RebuildCards();
    }

    void RemoveEntry(int index)
    {
        if (_slots[index].Status == SlotStatus.Running)
            StopOne(index);
        _presets.RemoveAt(index);
        PresetStore.Save(_presets);
        if (_selectedIndex == index) _selectedIndex = -1;
        RebuildCards();
    }

    void SelectCard(int index)
    {
        _selectedIndex = index;
        for (int i = 0; i < _cards.Count; i++)
            _cards[i].Selected = (i == index);
    }

    // ── Process control ────────────────────────────────────────────────────

    void RunOne(int index)
    {
        ProcessSlot slot = _slots[index];
        if (slot.Status == SlotStatus.Running) return;

        slot.OutputLines.Clear();
        while (slot.OutputQueue.TryDequeue(out _)) { }
        slot.Status = SlotStatus.Running;
        slot.ExitCode = null;
        slot.StartTime = DateTime.Now;
        slot.EndTime = null;
        PresetStore.Save(_presets);

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
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                slot.Proc = Process.Start(psi)!;

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
    }

    void StopOne(int index)
    {
        ProcessSlot slot = _slots[index];
        if (slot.Proc != null && slot.Status == SlotStatus.Running)
        {
            try { slot.Proc.Kill(true); } catch { }
            slot.AppendLine("[Terminated by user]");
            slot.Status = SlotStatus.Error;
            slot.EndTime = DateTime.Now;
        }
    }

    void RunAll()  { for (int i = 0; i < _slots.Count; i++) RunOne(i); }
    void StopAll() { for (int i = 0; i < _slots.Count; i++) StopOne(i); }

    void SaveAndStopAll()
    {
        PresetStore.Save(_presets);
        StopAll();
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    void Tick(object? sender, EventArgs e)
    {
        if (_dirty)
        {
            _dirty = false;
            PresetStore.Save(_presets);
        }

        for (int i = 0; i < _cards.Count; i++)
        {
            _slots[i].DrainQueue();
            _cards[i].UpdateStatus();
        }

        if (_selectedIndex >= 0 && _selectedIndex < _slots.Count)
        {
            ProcessSlot slot = _slots[_selectedIndex];
            _outputHeader.Text = $"Output: {slot.Preset.Label}";
            _outputHeader.ForeColor = slot.Status switch
            {
                SlotStatus.Running => FgAccent,
                SlotStatus.Done => FgGreen,
                SlotStatus.Error => FgRed,
                _ => FgDim,
            };

            StringBuilder sb = new StringBuilder();
            foreach (string line in slot.OutputLines)
                sb.AppendLine(line);

            string current = _outputBox.Text;
            string next = sb.ToString();
            if (current != next)
            {
                _outputBox.Text = next;
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
