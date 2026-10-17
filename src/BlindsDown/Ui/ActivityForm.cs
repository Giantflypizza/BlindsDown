using System.Globalization;
using BlindsDown.Core.Usage;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Ui;

/// <summary>
/// "Activity" window: who is using the microphone/camera right now (live, with duration),
/// and recent history ("Discord used your microphone 14:02–14:47 (45 min) via SteelSeries Sonar").
/// History rows are merged with <see cref="HistoryStore.MergeShortGaps"/> (15 s), grouped by day, shown
/// in local time; approximate (backfilled) rows are marked "≈". Per row: "Always allow (no popups)".
/// Non-modal; the caller keeps one instance and calls <see cref="SetData"/> when things change.
/// Closing the window only hides it (so its size and position are remembered for the session);
/// the caller disposes it on exit.
/// </summary>
internal sealed class ActivityForm : Form
{
    private const int MaxHistoryRows = 1000;

    /// <summary>In both lists the device column gives up width first when the window is narrow.</summary>
    private const int DeviceColumn = 1;
    private const string AllowText = "Always allow (no popups)";

    private readonly Font _font;
    private readonly ListView _nowList = NewList();
    private readonly ListView _historyList = NewList();
    private readonly Label _nowEmpty;
    private readonly Label _historyEmpty;
    private readonly Label _legend;
    private readonly Label _status;
    private readonly Button _allowButton = DialogLayout.Button(AllowText);
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _allowMenuItem = new(AllowText);
    private readonly System.Windows.Forms.Timer _ticker = new() { Interval = 1000 };

    private IReadOnlyList<UsageSession> _active = [];
    private IReadOnlyList<UsageRecord> _history = [];
    private string _historySignature = string.Empty;
    private DateOnly _historyDay;

    public ActivityForm()
    {
        _font = Theme.BodyFont;
        _nowEmpty = DialogLayout.Hint("Nothing is using your microphone right now.", new Padding(0, 2, 0, 12));
        _historyEmpty = DialogLayout.Hint("No activity yet. When an app uses your microphone, it shows up here.", new Padding(0, 2, 0, 8));
        _legend = DialogLayout.Hint(string.Empty, new Padding(0, 4, 0, 0));
        _status = DialogLayout.Hint(string.Empty, Padding.Empty);
        _status.Dock = DockStyle.None;
        _status.Anchor = AnchorStyles.Left;

        SuspendLayout();
        DialogLayout.Initialize(this, "BlindsDown Activity", _font);
        FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize = new Size(680, 560);
        MinimumSize = new Size(480, 420);

        _nowList.Columns.Add("App");
        _nowList.Columns.Add("Device");
        _nowList.Columns.Add("Since");
        _nowList.Columns.Add("Duration");
        _nowList.Height = 112;
        _nowList.AccessibleName = "Using now";

        _historyList.Columns.Add("App");
        _historyList.Columns.Add("Device");
        _historyList.Columns.Add("When");
        _historyList.AccessibleName = "Recent activity";

        _allowMenuItem.Click += (_, _) => AllowSelected();
        _menu.Items.Add(_allowMenuItem);
        _menu.Opening += (_, e) => e.Cancel = SelectedIdentity() is null;
        _nowList.ContextMenuStrip = _menu;
        _historyList.ContextMenuStrip = _menu;
        _nowList.SelectedIndexChanged += (_, _) => OnSelectionChanged(_nowList, _historyList);
        _historyList.SelectedIndexChanged += (_, _) => OnSelectionChanged(_historyList, _nowList);
        _allowButton.Enabled = false;
        _allowButton.Click += (_, _) => AllowSelected();

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16, 12, 16, 12),
            Margin = Padding.Empty,
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 100f) },
        };
        DialogLayout.AddRow(content, DialogLayout.Heading("Using now", first: true));
        DialogLayout.AddRow(content, _nowList).Dock = DockStyle.Fill;
        DialogLayout.AddRow(content, _nowEmpty);
        DialogLayout.AddRow(content, DialogLayout.Heading("Recent activity"));
        DialogLayout.AddRow(content, _historyEmpty);
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        content.RowCount = content.RowStyles.Count;
        _historyList.Dock = DockStyle.Fill;
        content.Controls.Add(_historyList, 0, content.RowCount - 1);
        DialogLayout.AddRow(content, _legend);
        _nowList.Margin = new Padding(0, 0, 0, 12);
        _historyList.Margin = Padding.Empty;

        Button close = DialogLayout.Button("Close");
        close.Click += (_, _) => Hide();
        CancelButton = close;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 100f) },
            RowStyles = { new RowStyle(SizeType.Percent, 100f), new RowStyle(SizeType.AutoSize) },
        };
        root.Controls.Add(content, 0, 0);
        root.Controls.Add(DialogLayout.CommandBar([_allowButton, _status], [close]), 0, 1);
        Controls.Add(root);

        _ticker.Tick += (_, _) => Tick();
        RenderActive();
        RenderHistory(force: true);

        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>The user asked to stop popups for this app (history still records it).</summary>
    public event Action<AppIdentity>? AllowAppRequested;

    public void SetData(IReadOnlyList<UsageSession> active, IReadOnlyList<UsageRecord> history)
    {
        // Copies: the window keeps these to re-render every second, and the caller's lists may change.
        _active = [.. active];
        _history = [.. history];
        RenderActive();
        RenderHistory(force: false);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            // Reopened on a later day: "Today" must not keep yesterday's rows until the first tick.
            RenderActive();
            RenderHistory(force: ActivityText.LocalDay(DateTimeOffset.UtcNow, Zone) != _historyDay);
            _ticker.Start();
        }
        else
        {
            _ticker.Stop();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Keep the one instance alive (and its size and position) when the user closes the window.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ListViews.FitColumns(_nowList, DeviceColumn);
        ListViews.FitColumns(_historyList, DeviceColumn);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            AllowAppRequested = null;
            _ticker.Dispose();
            _menu.Dispose();
            _font.Dispose();
        }

        base.Dispose(disposing);
    }

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    private static TimeZoneInfo Zone => TimeZoneInfo.Local;

    private void Tick()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (ListViewItem item in _nowList.Items)
        {
            if (item.Tag is UsageSession session)
            {
                item.SubItems[3].Text = ActivityText.Live(session.Duration(now));
            }
        }

        // At midnight "Today" becomes "Yesterday", and start times of live sessions gain their day.
        if (ActivityText.LocalDay(now, Zone) != _historyDay)
        {
            RenderActive();
            RenderHistory(force: true);
        }
    }

    private void RenderActive()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid? selected = _nowList.SelectedItems.Count > 0 && _nowList.SelectedItems[0].Tag is UsageSession s ? s.Id : null;
        _nowList.BeginUpdate();
        _nowList.Items.Clear();
        foreach (UsageSession session in _active.OrderBy(a => a.Start))
        {
            string app = ActivityText.App(session.App.DisplayName, session.ViaApp);
            var item = new ListViewItem(app) { Tag = session, ToolTipText = app };
            item.SubItems.Add(ActivityText.Device(session.Kind, session.DeviceName));
            item.SubItems.Add(ActivityText.Since(session.Start, now, Zone, Culture));
            item.SubItems.Add(ActivityText.Live(session.Duration(now)));
            item.Selected = session.Id == selected;
            _nowList.Items.Add(item);
        }

        _nowList.EndUpdate();
        _nowList.Visible = _active.Count > 0;
        _nowEmpty.Visible = _active.Count == 0;
        ListViews.FitColumns(_nowList, DeviceColumn);
        UpdateAllowButton();
    }

    private void RenderHistory(bool force)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string signature = HistorySignature(_history);
        if (!force && signature == _historySignature)
        {
            return;
        }

        _historySignature = signature;
        _historyDay = ActivityText.LocalDay(now, Zone);
        List<HistoryDay> days = ActivityText.GroupByDay(_history, Zone, now, Culture);
        object? top = _historyList.TopItem?.Tag;
        UsageRecord? selected = _historyList.SelectedItems.Count > 0 ? _historyList.SelectedItems[0].Tag as UsageRecord : null;

        // Native group headers keep the light theme's dark blue in dark mode (unreadable), so there the
        // days are plain header rows in the accent colour instead.
        bool nativeGroups = !UiPalette.IsDark;
        _historyList.BeginUpdate();
        _historyList.Items.Clear();
        _historyList.Groups.Clear();
        _historyList.ShowGroups = nativeGroups;
        int rows = 0;
        bool approximate = false;
        foreach (HistoryDay day in days)
        {
            if (rows >= MaxHistoryRows)
            {
                break;
            }

            ListViewGroup? group = null;
            if (nativeGroups)
            {
                group = new ListViewGroup(day.Label) { Name = day.Day.ToString("O", CultureInfo.InvariantCulture) };
                _historyList.Groups.Add(group);
            }
            else
            {
                _historyList.Items.Add(new ListViewItem(day.Label) { ForeColor = UiPalette.Accent });
            }

            foreach (UsageRecord record in day.Records.Take(MaxHistoryRows - rows))
            {
                var item = new ListViewItem(ActivityText.App(record.AppName, record.ViaApp), group)
                {
                    Tag = record,
                    ToolTipText = ActivityText.Sentence(record, Zone, Culture),
                };
                item.SubItems.Add(ActivityText.Device(record.Kind, record.DeviceName));
                item.SubItems.Add(ActivityText.Range(record, Zone, Culture));
                item.Selected = record.Equals(selected);
                _historyList.Items.Add(item);
                approximate |= record.Approximate;
                rows++;
            }
        }

        _historyList.EndUpdate();
        RestoreTopItem(top);

        _historyList.Visible = rows > 0;
        _historyEmpty.Visible = rows == 0;
        var legend = new List<string>();
        if (approximate)
        {
            legend.Add(ActivityText.ApproximateMark + " Approximate times, taken from Windows' own usage records.");
        }

        if (rows >= MaxHistoryRows)
        {
            legend.Add("Showing the latest " + MaxHistoryRows.ToString("N0", Culture) + " entries.");
        }

        _legend.Text = string.Join(" ", legend);
        _legend.Visible = legend.Count > 0;
        ListViews.FitColumns(_historyList, DeviceColumn);
        UpdateAllowButton();
    }

    private void RestoreTopItem(object? tag)
    {
        if (tag is null || !_historyList.IsHandleCreated)
        {
            return;
        }

        ListViewItem? item = _historyList.Items.Cast<ListViewItem>().FirstOrDefault(i => Equals(i.Tag, tag));
        if (item is not null)
        {
            _historyList.TopItem = item;
        }
    }

    private void OnSelectionChanged(ListView changed, ListView other)
    {
        // One selection across both lists, so the button always refers to what the user last picked.
        if (changed.SelectedItems.Count > 0 && other.SelectedItems.Count > 0)
        {
            other.SelectedItems.Clear();
        }

        _status.Text = string.Empty;
        UpdateAllowButton();
    }

    private void UpdateAllowButton()
    {
        AppIdentity? identity = SelectedIdentity();
        _allowButton.Enabled = identity is not null;
        // "&" would turn into a menu mnemonic.
        _allowMenuItem.Text = identity is null ? AllowText : $"Always allow {identity.DisplayName.Replace("&", "&&", StringComparison.Ordinal)} (no popups)";
    }

    private AppIdentity? SelectedIdentity()
    {
        AppIdentity? identity = null;
        if (_nowList.SelectedItems.Count > 0 && _nowList.SelectedItems[0].Tag is UsageSession session)
        {
            identity = session.App;
        }
        else if (_historyList.SelectedItems.Count > 0 && _historyList.SelectedItems[0].Tag is UsageRecord record)
        {
            identity = ActivityText.IdentityOf(record);
        }

        // "Unknown app" is not one app, so allowing it would silence anything unidentified.
        return identity is null || identity.GroupKey == AppIdentity.Unknown.GroupKey ? null : identity;
    }

    private void AllowSelected()
    {
        AppIdentity? identity = SelectedIdentity();
        if (identity is null)
        {
            return;
        }

        AllowAppRequested?.Invoke(identity);
        _status.Text = $"{identity.DisplayName} won't show popups any more.";
    }

    private static string HistorySignature(IReadOnlyList<UsageRecord> history) =>
        history.Count == 0
            ? "0"
            : string.Create(CultureInfo.InvariantCulture, $"{history.Count}|{history.Max(r => r.Start).UtcTicks}|{history.Max(r => r.End).UtcTicks}");

    private static ListView NewList()
    {
        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            ShowItemToolTips = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        ListViews.AttachAutoFit(list, DeviceColumn);
        return list;
    }
}
