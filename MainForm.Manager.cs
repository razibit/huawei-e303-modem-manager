using System.Diagnostics;
using System.IO.Ports;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HuaweiE303VoiceDialer;

internal sealed partial class MainForm
{
    private sealed class CallHistoryItem
    {
        public DateTime Time { get; set; }
        public string Direction { get; set; } = "Call";
        public string Number { get; set; } = "Unknown";
        public string Result { get; set; } = "";
    }

    private sealed class UssdShortcut
    {
        public string Label { get; set; } = "";
        public string Code { get; set; } = "";
        public override string ToString() => $"{Label}  ({Code})";
    }

    private sealed record SmsListRow(string Status, string Sender, DateTime Time, string Preview,
        IReadOnlyList<int> Indexes, string FullText);
    private sealed record ProviderChoice(int Status, string LongName, string ShortName, string Numeric, int? AccessTechnology)
    {
        public override string ToString() => $"{LongName} ({Numeric})";
    }

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _smsTimer = new() { Interval = 12000 };
    private readonly System.Windows.Forms.Timer _networkTimer = new() { Interval = 8000 };
    private readonly MobileBroadbandService _mobileBroadband = new();
    private readonly TextBox _smsNumberBox = new() { Width = 220 };
    private readonly TextBox _smsComposeBox = new() { Multiline = true, Height = 95, Width = 520, MaxLength = 4000, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _smsDetailsBox = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _ussdCodeBox = new() { Width = 220 };
    private readonly TextBox _ussdReplyBox = new() { Width = 260 };
    private readonly RichTextBox _ussdTranscriptBox = new() { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false };
    private readonly TextBox _contactNameBox = new() { Width = 220 };
    private readonly TextBox _contactNumberBox = new() { Width = 220 };
    private readonly TextBox _apnBox = new() { Width = 220, PlaceholderText = "Carrier APN" };
    private readonly Label _networkStatusLabel = new() { AutoSize = true, Text = "Mobile broadband status has not been checked." };
    private readonly Label _deviceInfoLabel = new() { AutoSize = true, Text = "Check modem to read device information." };
    private readonly Label _smsStatusLabel = new() { AutoSize = true, Text = "SMS storage will be checked when the modem connects." };
    private readonly Label _ussdStatusLabel = new() { AutoSize = true, Text = "USSD is ready when the modem is registered." };
    private readonly Label _contactStatusLabel = new() { AutoSize = true, Text = "Contacts will be loaded from the SIM/device phonebook." };
    private readonly DataGridView _callHistoryGrid = CreateGrid();
    private readonly DataGridView _smsGrid = CreateGrid();
    private readonly DataGridView _contactsGrid = CreateGrid();
    private readonly DataGridView _shortcutsGrid = CreateGrid();
    private readonly ComboBox _providerBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly ListBox _shortcutList = new() { Width = 370, Height = 100 };
    private readonly HashSet<int> _knownUnreadSmsIndexes = new();
    private readonly List<SmsListRow> _smsRows = new();
    private readonly List<CallHistoryItem> _callHistory = new();
    private List<UssdShortcut> _ussdShortcuts = new();
    private string? _smsMemory;
    private int _phonebookLastIndex = 250;
    private int _phonebookNumberLength = 20;
    private int _phonebookNameLength = 14;
    private bool _smsInitialized;
    private bool _ussdSessionActive;
    private bool _smsPollingBusy;
    private bool _networkRefreshBusy;
    private string? _currentCallNumber;
    private long? _sessionBytesReceivedStart;
    private long? _sessionBytesSentStart;

    private void BuildManagerUi()
    {
        Text = "GP Modem Manager";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(950, 700);
        Size = new Size(1120, 820);
        FormBorderStyle = FormBorderStyle.Sizable;

        var portGroup = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        portGroup.Controls.Add(new Label { Text = "AT/control port", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        portGroup.Controls.Add(_controlPortBox);
        portGroup.Controls.Add(new Label { Text = "PCM/audio port", AutoSize = true, Padding = new Padding(8, 7, 0, 0) });
        portGroup.Controls.Add(_audioPortBox);
        portGroup.Controls.Add(_refreshButton);
        portGroup.Controls.Add(_checkButton);
        portGroup.Controls.Add(_enableVoiceButton);

        var audioDeviceGroup = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        audioDeviceGroup.Controls.Add(new Label { Text = "Microphone", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        audioDeviceGroup.Controls.Add(_microphoneBox);
        audioDeviceGroup.Controls.Add(new Label { Text = "Speakers", AutoSize = true, Padding = new Padding(8, 7, 0, 0) });
        audioDeviceGroup.Controls.Add(_speakerBox);
        audioDeviceGroup.Controls.Add(_refreshAudioButton);

        var statusPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 4, 10, 8) };
        statusPanel.Controls.Add(_statusLabel);
        var explanation = new Label
        {
            Text = "Use this app with the GP modem manager closed. SMS and calls may incur operator charges.",
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(10)
        };

        Controls.Add(_tabs);
        Controls.Add(explanation);
        Controls.Add(statusPanel);
        Controls.Add(audioDeviceGroup);
        Controls.Add(portGroup);

        BuildCallPage();
        BuildSmsPage();
        BuildUssdPage();
        BuildContactsPage();
        BuildInternetPage();
        BuildDevicePage();
        BuildActivityPage();

        _smsTimer.Tick += async (_, _) => await RefreshSmsAsync(background: true);
        _networkTimer.Tick += async (_, _) => await RefreshNetworkAsync(showErrors: false);
        _tabs.SelectedIndexChanged += (_, _) => UpdateManagerButtons();
        UpdateManagerButtons();
    }

    private void BuildCallPage()
    {
        var page = new TabPage("Calls");
        var dialGroup = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        dialGroup.Controls.Add(new Label { Text = "Number", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        dialGroup.Controls.Add(_numberBox);
        dialGroup.Controls.Add(_callButton);
        dialGroup.Controls.Add(_answerButton);
        dialGroup.Controls.Add(_rejectButton);
        dialGroup.Controls.Add(_hangupButton);
        ConfigureGrid(_callHistoryGrid, ("Time", "Time", 150), ("Direction", "Direction", 100), ("Number", "Number", 180), ("Result", "Result", 200));
        var refresh = new Button { Text = "Refresh history", AutoSize = true };
        var clear = new Button { Text = "Clear history", AutoSize = true };
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        tools.Controls.Add(refresh);
        tools.Controls.Add(clear);
        refresh.Click += (_, _) => RefreshCallHistoryGrid();
        clear.Click += (_, _) => ClearCallHistory();
        page.Controls.Add(_callHistoryGrid);
        page.Controls.Add(tools);
        page.Controls.Add(dialGroup);
        _tabs.TabPages.Add(page);
    }

    private void BuildSmsPage()
    {
        var page = new TabPage("SMS");
        ConfigureGrid(_smsGrid, ("Status", "Status", 100), ("From / To", "Sender", 170), ("Time", "Time", 145), ("Message", "Preview", 380));
        _smsGrid.SelectionChanged += (_, _) => ShowSelectedSmsPreview();
        var refresh = new Button { Text = "Refresh inbox", AutoSize = true };
        var read = new Button { Text = "Read selected", AutoSize = true };
        var delete = new Button { Text = "Delete selected", AutoSize = true };
        var reply = new Button { Text = "Reply", AutoSize = true };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(read);
        toolbar.Controls.Add(reply);
        toolbar.Controls.Add(delete);
        toolbar.Controls.Add(_smsStatusLabel);
        refresh.Click += async (_, _) => await RefreshSmsAsync();
        read.Click += async (_, _) => await ReadSelectedSmsAsync();
        delete.Click += async (_, _) => await DeleteSelectedSmsAsync();
        reply.Click += (_, _) => ReplyToSelectedSms();

        var preview = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220 };
        preview.Panel1.Controls.Add(_smsGrid);
        preview.Panel2.Controls.Add(_smsDetailsBox);
        var composer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        composer.Controls.Add(new Label { Text = "Recipient", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        composer.Controls.Add(_smsNumberBox);
        composer.Controls.Add(_smsComposeBox);
        var send = new Button { Text = "Send SMS", AutoSize = true };
        composer.Controls.Add(send);
        send.Click += async (_, _) => await SendSmsAsync();
        page.Controls.Add(preview);
        page.Controls.Add(composer);
        page.Controls.Add(toolbar);
        _tabs.TabPages.Add(page);
    }

    private void BuildUssdPage()
    {
        var page = new TabPage("USSD & packages");
        var requestRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        requestRow.Controls.Add(new Label { Text = "Dial code", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        requestRow.Controls.Add(_ussdCodeBox);
        var send = new Button { Text = "Send code", AutoSize = true };
        var end = new Button { Text = "End session", AutoSize = true };
        requestRow.Controls.Add(send);
        requestRow.Controls.Add(end);
        var replyRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        replyRow.Controls.Add(new Label { Text = "Menu reply", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        replyRow.Controls.Add(_ussdReplyBox);
        var reply = new Button { Text = "Send reply", AutoSize = true };
        replyRow.Controls.Add(reply);
        replyRow.Controls.Add(_ussdStatusLabel);
        send.Click += async (_, _) => await SendUssdAsync(_ussdCodeBox.Text, startSession: true);
        reply.Click += async (_, _) => await SendUssdAsync(_ussdReplyBox.Text, startSession: false);
        end.Click += async (_, _) => await EndUssdSessionAsync();

        var shortcutPanel = new GroupBox { Text = "Saved balance / package shortcuts", Dock = DockStyle.Right, Width = 390, Padding = new Padding(8) };
        _shortcutList.Dock = DockStyle.Fill;
        var shortcutTools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        var addShortcut = new Button { Text = "Add", AutoSize = true };
        var removeShortcut = new Button { Text = "Remove", AutoSize = true };
        shortcutTools.Controls.Add(addShortcut);
        shortcutTools.Controls.Add(removeShortcut);
        shortcutPanel.Controls.Add(_shortcutList);
        shortcutPanel.Controls.Add(shortcutTools);
        addShortcut.Click += (_, _) => AddUssdShortcut();
        removeShortcut.Click += (_, _) => RemoveUssdShortcut();
        _shortcutList.DoubleClick += async (_, _) =>
        {
            if (_shortcutList.SelectedItem is UssdShortcut shortcut)
                await SendUssdAsync(shortcut.Code, startSession: true);
        };
        var conversationPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        conversationPanel.Controls.Add(_ussdTranscriptBox);
        page.Controls.Add(conversationPanel);
        page.Controls.Add(shortcutPanel);
        page.Controls.Add(replyRow);
        page.Controls.Add(requestRow);
        _tabs.TabPages.Add(page);
    }

    private void BuildContactsPage()
    {
        var page = new TabPage("Contacts");
        ConfigureGrid(_contactsGrid, ("Index", "Index", 70), ("Name", "Name", 240), ("Number", "Number", 210));
        _contactsGrid.SelectionChanged += (_, _) => ShowSelectedContact();
        var fields = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        fields.Controls.Add(new Label { Text = "Name", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        fields.Controls.Add(_contactNameBox);
        fields.Controls.Add(new Label { Text = "Number", AutoSize = true, Padding = new Padding(8, 7, 0, 0) });
        fields.Controls.Add(_contactNumberBox);
        var refresh = new Button { Text = "Refresh SIM contacts", AutoSize = true };
        var add = new Button { Text = "Add contact", AutoSize = true };
        var update = new Button { Text = "Update selected", AutoSize = true };
        var delete = new Button { Text = "Delete selected", AutoSize = true };
        fields.Controls.Add(refresh);
        fields.Controls.Add(add);
        fields.Controls.Add(update);
        fields.Controls.Add(delete);
        fields.Controls.Add(_contactStatusLabel);
        refresh.Click += async (_, _) => await RefreshContactsAsync();
        add.Click += async (_, _) => await AddContactAsync();
        update.Click += async (_, _) => await UpdateContactAsync();
        delete.Click += async (_, _) => await DeleteContactAsync();
        _contactsGrid.CellDoubleClick += (_, _) =>
        {
            if (_contactsGrid.CurrentRow is null) return;
            _smsNumberBox.Text = Convert.ToString(_contactsGrid.CurrentRow.Cells["Number"].Value) ?? "";
            _numberBox.Text = _smsNumberBox.Text;
        };
        page.Controls.Add(_contactsGrid);
        page.Controls.Add(fields);
        _tabs.TabPages.Add(page);
    }

    private void BuildInternetPage()
    {
        var page = new TabPage("Internet");
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14), WrapContents = true };
        panel.Controls.Add(new Label { Text = "APN", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        panel.Controls.Add(_apnBox);
        var connect = new Button { Text = "Connect", AutoSize = true };
        var disconnect = new Button { Text = "Disconnect", AutoSize = true };
        var refresh = new Button { Text = "Refresh status", AutoSize = true };
        panel.Controls.Add(connect);
        panel.Controls.Add(disconnect);
        panel.Controls.Add(refresh);
        panel.Controls.Add(_networkStatusLabel);
        var help = new Label
        {
            Text = "The APN is supplied by your carrier. It is saved only in this app's settings. The connection request uses Windows Mobile Broadband.",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(14),
            MaximumSize = new Size(850, 0)
        };
        connect.Click += async (_, _) => await ConnectMobileDataAsync();
        disconnect.Click += async (_, _) => await DisconnectMobileDataAsync();
        refresh.Click += async (_, _) => await RefreshNetworkAsync(showErrors: true);
        page.Controls.Add(help);
        page.Controls.Add(panel);
        _tabs.TabPages.Add(page);
    }

    private void BuildDevicePage()
    {
        var page = new TabPage("Device & settings");
        _deviceInfoLabel.Dock = DockStyle.Top;
        _deviceInfoLabel.Padding = new Padding(14);
        var unavailable = new Label
        {
            Text = "Not provided by this E303 setup: MMS, video calling, WLAN/VPN, SIM Toolkit and modem firmware updating.",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(14),
            MaximumSize = new Size(880, 0)
        };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14), WrapContents = true };
        var refresh = new Button { Text = "Read device status", AutoSize = true };
        var diagnose = new Button { Text = "Run safe diagnostics", AutoSize = true };
        var scan = new Button { Text = "Scan networks", AutoSize = true };
        var select = new Button { Text = "Select network", AutoSize = true };
        var automatic = new Button { Text = "Use automatic network selection", AutoSize = true };
        var pinStatus = new Button { Text = "Check SIM lock status", AutoSize = true };
        var pinChange = new Button { Text = "Change SIM PIN", AutoSize = true };
        var pinEnable = new Button { Text = "Enable SIM PIN", AutoSize = true };
        var pinDisable = new Button { Text = "Disable SIM PIN", AutoSize = true };
        var puk = new Button { Text = "Unlock SIM with PUK", AutoSize = true };
        panel.Controls.Add(refresh);
        panel.Controls.Add(diagnose);
        panel.Controls.Add(scan);
        panel.Controls.Add(_providerBox);
        panel.Controls.Add(select);
        panel.Controls.Add(automatic);
        panel.Controls.Add(pinStatus);
        panel.Controls.Add(pinEnable);
        panel.Controls.Add(pinDisable);
        panel.Controls.Add(pinChange);
        panel.Controls.Add(puk);
        refresh.Click += async (_, _) => await ReadDeviceStatusAsync();
        diagnose.Click += async (_, _) => await RunDiagnosticsAsync();
        scan.Click += async (_, _) => await ScanNetworksAsync();
        select.Click += async (_, _) => await SelectProviderAsync();
        automatic.Click += async (_, _) => await SetAutomaticProviderAsync();
        pinStatus.Click += async (_, _) => await ReadSimPinStatusAsync();
        pinEnable.Click += async (_, _) => await SetSimPinLockAsync(enable: true);
        pinDisable.Click += async (_, _) => await SetSimPinLockAsync(enable: false);
        pinChange.Click += async (_, _) => await ChangeSimPinAsync();
        puk.Click += async (_, _) => await UnlockSimWithPukAsync();
        page.Controls.Add(_deviceInfoLabel);
        page.Controls.Add(unavailable);
        page.Controls.Add(panel);
        _tabs.TabPages.Add(page);
    }

    private void BuildActivityPage()
    {
        var page = new TabPage("Activity");
        page.Controls.Add(_logBox);
        _tabs.TabPages.Add(page);
    }

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        RowHeadersVisible = false,
        AutoGenerateColumns = false,
        BackgroundColor = SystemColors.Window
    };

    private static void ConfigureGrid(DataGridView grid, params (string Header, string Name, int Width)[] columns)
    {
        grid.Columns.Clear();
        foreach (var (header, name, width) in columns)
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, Name = name, Width = width, SortMode = DataGridViewColumnSortMode.Automatic });
    }

    private void LoadManagerSettings()
    {
        var settings = LoadAudioSettings();
        _apnBox.Text = settings.LastApn ?? "";
        _callHistory.Clear();
        _callHistory.AddRange(settings.CallHistory ?? new List<CallHistoryItem>());
        _ussdShortcuts = settings.UssdShortcuts ?? new List<UssdShortcut>();
        RefreshCallHistoryGrid();
        RefreshUssdShortcutList();
    }

    private void SaveManagerSettings(AudioDeviceSettings? existing = null)
    {
        try
        {
            var settings = existing ?? LoadAudioSettings();
            settings.MicrophoneKey = (_microphoneBox.SelectedItem as AudioDeviceChoice)?.Key ?? settings.MicrophoneKey;
            settings.SpeakerKey = (_speakerBox.SelectedItem as AudioDeviceChoice)?.Key ?? settings.SpeakerKey;
            settings.LastApn = _apnBox.Text.Trim();
            settings.CallHistory = _callHistory.TakeLast(300).ToList();
            settings.UssdShortcuts = _ussdShortcuts.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log("App settings could not be saved: " + ex.Message); }
    }

    private void RecordCallEvent(string direction, string? number, string result)
    {
        _callHistory.Insert(0, new CallHistoryItem
        {
            Time = DateTime.Now,
            Direction = direction,
            Number = string.IsNullOrWhiteSpace(number) ? "Unknown" : number,
            Result = result
        });
        if (_callHistory.Count > 300) _callHistory.RemoveRange(300, _callHistory.Count - 300);
        RefreshCallHistoryGrid();
        SaveManagerSettings();
    }

    private void RefreshCallHistoryGrid()
    {
        _callHistoryGrid.Rows.Clear();
        foreach (var entry in _callHistory.OrderByDescending(entry => entry.Time))
            _callHistoryGrid.Rows.Add(entry.Time.ToString("g"), entry.Direction, entry.Number, entry.Result);
    }

    private void ClearCallHistory()
    {
        if (MessageBox.Show(this, "Clear the call history saved on this PC?", "Clear call history", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _callHistory.Clear();
        RefreshCallHistoryGrid();
        SaveManagerSettings();
    }

    private void RefreshUssdShortcutList()
    {
        _shortcutList.Items.Clear();
        foreach (var shortcut in _ussdShortcuts) _shortcutList.Items.Add(shortcut);
    }

    private void AddUssdShortcut()
    {
        var label = PromptText("Name this shortcut", "Add USSD shortcut", "Balance");
        if (label is null) return;
        var code = PromptText("Enter the USSD code (for example, *123#).", "Add USSD shortcut", "");
        if (code is null) return;
        if (!Regex.IsMatch(code.Trim(), @"^[0-9*#+]{1,64}$"))
        {
            MessageBox.Show(this, "Use digits, *, #, and + only.", "Invalid USSD code", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ussdShortcuts.Add(new UssdShortcut { Label = label.Trim(), Code = code.Trim() });
        RefreshUssdShortcutList();
        SaveManagerSettings();
    }

    private void RemoveUssdShortcut()
    {
        if (_shortcutList.SelectedItem is not UssdShortcut selected) return;
        _ussdShortcuts.Remove(selected);
        RefreshUssdShortcutList();
        SaveManagerSettings();
    }

    private static string? PromptText(string prompt, string title, string initialValue, bool password = false)
    {
        using var dialog = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(440, 125)
        };
        var label = new Label { Text = prompt, Left = 12, Top = 12, Width = 410, AutoSize = true };
        var box = new TextBox { Left = 12, Top = 42, Width = 410, Text = initialValue, UseSystemPasswordChar = password };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 266, Width = 75, Top = 82 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 347, Width = 75, Top = 82 };
        dialog.Controls.AddRange(new Control[] { label, box, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK ? box.Text : null;
    }

    private void UpdateManagerButtons()
    {
        if (IsDisposed) return;
        var idle = !_busy && !_callStarted && !_incomingCallRinging;
        var hasPort = _controlPortBox.SelectedItem is not null;
        foreach (var control in new[] { _smsNumberBox, _smsComposeBox, _ussdCodeBox, _ussdReplyBox, _contactNameBox, _contactNumberBox, _apnBox })
            control.Enabled = idle;
        _smsGrid.Enabled = !_busy;
        _contactsGrid.Enabled = !_busy;
        _shortcutList.Enabled = idle;
        foreach (TabPage page in _tabs.TabPages)
        {
            if (page.Text == "Calls") continue;
            foreach (var button in Descendants(page).OfType<Button>()) button.Enabled = idle;
        }
        _smsStatusLabel.Enabled = true;
        _networkStatusLabel.Enabled = true;
        _smsTimer.Enabled = hasPort;
        _networkTimer.Enabled = true;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static string QuoteAt(string value)
    {
        if (value.Any(ch => char.IsControl(ch) || ch == '"'))
            throw new ArgumentException("The value contains characters that cannot be sent safely to the modem.");
        return "\"" + value + "\"";
    }
}
