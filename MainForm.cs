using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace HuaweiE303VoiceDialer;

internal sealed partial class MainForm : Form
{
    private sealed record AudioDeviceChoice(int DeviceNumber, string ProductName, string DisplayName)
    {
        public string Key => DeviceNumber == -1 ? "default" : $"{DeviceNumber}|{ProductName}";
        public override string ToString() => DisplayName;
    }

    private sealed record CurrentCall(int Id, int Direction, int State, int Mode, string? Number);

    private sealed class AudioDeviceSettings
    {
        public string? MicrophoneKey { get; set; }
        public string? SpeakerKey { get; set; }
        public string? LastApn { get; set; }
        public List<CallHistoryItem> CallHistory { get; set; } = new();
        public List<UssdShortcut> UssdShortcuts { get; set; } = new();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WaveOutCapabilitiesNative
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public ushort DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? ProductName;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
        public uint Support;
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutGetNumDevs")]
    private static extern uint WaveOutGetDeviceCount();

    [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern uint WaveOutGetDeviceCapabilities(UIntPtr deviceNumber, ref WaveOutCapabilitiesNative capabilities, uint capabilitiesSize);

    private readonly ComboBox _controlPortBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105 };
    private readonly ComboBox _audioPortBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105 };
    private readonly ComboBox _microphoneBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly ComboBox _speakerBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly TextBox _numberBox = new() { Width = 205, Text = "121" };
    private readonly Button _refreshButton = new() { Text = "Refresh ports", AutoSize = true };
    private readonly Button _refreshAudioButton = new() { Text = "Refresh audio", AutoSize = true };
    private readonly Button _checkButton = new() { Text = "Check modem", AutoSize = true };
    private readonly Button _enableVoiceButton = new() { Text = "Enable PC voice", AutoSize = true, Enabled = false };
    private readonly Button _callButton = new() { Text = "Call", AutoSize = true, Enabled = false };
    private readonly Button _answerButton = new() { Text = "Answer", AutoSize = true, Enabled = false };
    private readonly Button _rejectButton = new() { Text = "Reject", AutoSize = true, Enabled = false };
    private readonly Button _hangupButton = new() { Text = "Hang up", AutoSize = true, Enabled = false };
    private readonly Label _statusLabel = new() { AutoSize = true, Text = "Select the modem ports, then check the modem." };
    private readonly TextBox _logBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        WordWrap = false,
        Font = new Font("Consolas", 9F)
    };
    private readonly System.Windows.Forms.Timer _callTimer = new() { Interval = 900 };
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GP-E303-Voice",
        "dialer-settings.json");
    private SerialPort? _controlPort;
    private SerialPort? _audioPort;
    private VoiceAudioBridge? _audioBridge;
    private bool _callStarted;
    private bool _incomingCallRinging;
    private bool _callMonitorBusy;
    private bool _refreshingAudioDevices;
    private bool _callMonitorErrorLogged;
    private bool _voiceReady;
    private bool _voiceEnableRejected;
    private bool _busy;
    private int? _incomingCallId;
    private string? _incomingCaller;
    private string? _lastFirmwareRevision;

    public MainForm()
    {
        BuildManagerUi();
        _refreshButton.Click += (_, _) => RefreshPorts();
        _refreshAudioButton.Click += (_, _) => RefreshAudioDevices();
        _checkButton.Click += async (_, _) => await CheckModemAsync();
        _enableVoiceButton.Click += async (_, _) => await EnableVoiceAsync();
        _callButton.Click += async (_, _) => await DialAsync();
        _answerButton.Click += async (_, _) => await AnswerIncomingAsync();
        _rejectButton.Click += async (_, _) => await RejectIncomingAsync();
        _hangupButton.Click += async (_, _) => await HangupAsync();
        _callTimer.Tick += async (_, _) => await RefreshCallStateAsync();
        _numberBox.TextChanged += (_, _) => UpdateButtons();
        _microphoneBox.SelectedIndexChanged += (_, _) => { if (!_refreshingAudioDevices) SaveAudioSettings(); UpdateButtons(); };
        _speakerBox.SelectedIndexChanged += (_, _) => { if (!_refreshingAudioDevices) SaveAudioSettings(); UpdateButtons(); };
        Load += async (_, _) =>
        {
            LoadManagerSettings();
            RefreshPorts();
            RefreshAudioDevices();
            if (_controlPortBox.SelectedItem is not null)
            {
                await CheckModemAsync();
                await RefreshSmsAsync();
                await RefreshContactsAsync();
                await ReadDeviceStatusAsync();
                RefreshCallHistoryGrid();
                RefreshUssdShortcutList();
            }
            else
            {
                SetStatus("Connect the modem and click Check modem to start.");
            }
            await RefreshNetworkAsync(showErrors: false);
        };
        FormClosing += async (_, _) => await CloseModemAsync();
    }

    private void RefreshPorts()
    {
        var oldControl = _controlPortBox.SelectedItem?.ToString();
        var oldAudio = _audioPortBox.SelectedItem?.ToString();
        var names = GetPresentSerialPorts(out var verifiedPresent);

        _controlPortBox.Items.Clear();
        _audioPortBox.Items.Clear();
        _controlPortBox.Items.AddRange(names);
        _audioPortBox.Items.AddRange(names);
        SelectPort(_controlPortBox, oldControl, "COM3", names);
        SelectPort(_audioPortBox, oldAudio, "COM4", names);
        Log($"Detected {(verifiedPresent ? "present" : "registered")} serial ports: {(names.Length == 0 ? "none" : string.Join(", ", names))}");
        if (!verifiedPresent) Log("Live port enumeration was unavailable; registered ports may include disconnected devices.");
        UpdateButtons();
    }

    private void RefreshAudioDevices()
    {
        var savedSettings = LoadAudioSettings();
        var microphoneKey = (_microphoneBox.SelectedItem as AudioDeviceChoice)?.Key ?? savedSettings.MicrophoneKey;
        var speakerKey = (_speakerBox.SelectedItem as AudioDeviceChoice)?.Key ?? savedSettings.SpeakerKey;
        var microphoneCount = 0;
        var speakerCount = 0;

        _refreshingAudioDevices = true;
        try
        {
            _microphoneBox.Items.Clear();
            _speakerBox.Items.Clear();
            _microphoneBox.Items.Add(new AudioDeviceChoice(-1, "Default", "System default microphone"));
            _speakerBox.Items.Add(new AudioDeviceChoice(-1, "Default", "System default speakers"));

            microphoneCount = WaveInEvent.DeviceCount;
            for (var i = 0; i < microphoneCount; i++)
            {
                try
                {
                    var productName = WaveInEvent.GetCapabilities(i).ProductName.Trim();
                    if (string.IsNullOrWhiteSpace(productName)) productName = $"Microphone {i + 1}";
                    _microphoneBox.Items.Add(new AudioDeviceChoice(i, productName, $"Device {i}: {productName}"));
                }
                catch (Exception ex)
                {
                    Log($"Microphone {i + 1} could not be listed: {ex.Message}");
                }
            }

            speakerCount = checked((int)WaveOutGetDeviceCount());
            for (var i = 0; i < speakerCount; i++)
            {
                try
                {
                    var capabilities = new WaveOutCapabilitiesNative();
                    var result = WaveOutGetDeviceCapabilities(
                        new UIntPtr((uint)i),
                        ref capabilities,
                        (uint)Marshal.SizeOf<WaveOutCapabilitiesNative>());
                    var productName = result == 0 ? capabilities.ProductName?.Trim() : null;
                    if (string.IsNullOrWhiteSpace(productName)) productName = $"Speaker/output {i + 1}";
                    _speakerBox.Items.Add(new AudioDeviceChoice(i, productName, $"Device {i}: {productName}"));
                }
                catch (Exception ex)
                {
                    Log($"Speaker/output {i + 1} could not be listed: {ex.Message}");
                }
            }

            SelectAudioDevice(_microphoneBox, microphoneKey);
            SelectAudioDevice(_speakerBox, speakerKey);
        }
        catch (Exception ex)
        {
            Log("Audio device enumeration failed: " + ex.Message);
            SelectAudioDevice(_microphoneBox, microphoneKey);
            SelectAudioDevice(_speakerBox, speakerKey);
        }
        finally
        {
            _refreshingAudioDevices = false;
        }

        Log($"Audio devices found: {microphoneCount} microphone(s), {speakerCount} speaker/output device(s).");
        SaveAudioSettings();
        UpdateButtons();
    }

    private static void SelectAudioDevice(ComboBox box, string? key)
    {
        var choice = box.Items.Cast<AudioDeviceChoice>().FirstOrDefault(item => item.Key == key)
            ?? box.Items.Cast<AudioDeviceChoice>().FirstOrDefault(item => item.DeviceNumber == -1)
            ?? box.Items.Cast<AudioDeviceChoice>().FirstOrDefault();
        if (choice is not null) box.SelectedItem = choice;
    }

    private AudioDeviceSettings LoadAudioSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
                return JsonSerializer.Deserialize<AudioDeviceSettings>(File.ReadAllText(_settingsPath)) ?? new AudioDeviceSettings();
        }
        catch (Exception ex)
        {
            Log("Saved audio settings could not be read: " + ex.Message);
        }
        return new AudioDeviceSettings();
    }

    private void SaveAudioSettings()
    {
        if (_refreshingAudioDevices) return;
        try
        {
            var settings = LoadAudioSettings();
            settings.MicrophoneKey = (_microphoneBox.SelectedItem as AudioDeviceChoice)?.Key;
            settings.SpeakerKey = (_speakerBox.SelectedItem as AudioDeviceChoice)?.Key;
            SaveManagerSettings(settings);
        }
        catch (Exception ex)
        {
            Log("Audio settings could not be saved: " + ex.Message);
        }
    }

    private static string[] GetPresentSerialPorts(out bool verifiedPresent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Ports'");
            using var devices = searcher.Get();
            var names = devices
                .Cast<ManagementObject>()
                .Select(device => device["Name"]?.ToString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => Regex.Match(name!, @"\((COM\d+)\)$", RegexOptions.IgnoreCase))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => int.TryParse(name.AsSpan(3), out var number) ? number : int.MaxValue)
                .ToArray();
            verifiedPresent = true;
            return names;
        }
        catch
        {
            verifiedPresent = false;
            return SerialPort.GetPortNames()
                .OrderBy(name => int.TryParse(name.AsSpan(3), out var number) ? number : int.MaxValue)
                .ToArray();
        }
    }

    private static void SelectPort(ComboBox box, string? previous, string preferred, string[] names)
    {
        var choice = names.FirstOrDefault(name => string.Equals(name, previous, StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(name => string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault();
        if (choice is not null) box.SelectedItem = choice;
    }

    private async Task CheckModemAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            var port = EnsureControlPort();
            var hello = await SendAtAsync("AT", TimeSpan.FromSeconds(2));
            if (!HasOk(hello))
            {
                SetStatus("The selected control port did not answer AT.");
                Log("AT check failed: " + Clean(hello));
                _voiceReady = false;
                return;
            }

            var identity = await SendAtAsync("ATI", TimeSpan.FromSeconds(2));
            var voice = await SendAtAsync("AT^CVOICE?", TimeSpan.FromSeconds(2));
            var registration = await SendAtAsync("AT+CREG?", TimeSpan.FromSeconds(2));
            var callerId = await SendAtAsync("AT+CLIP=1", TimeSpan.FromSeconds(2));
            var revision = Regex.Match(identity, @"Revision:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            if (revision.Success)
            {
                var currentRevision = revision.Groups[1].Value.Trim();
                if (!string.Equals(_lastFirmwareRevision, currentRevision, StringComparison.OrdinalIgnoreCase))
                    _voiceEnableRejected = false;
                _lastFirmwareRevision = currentRevision;
            }
            Log("Modem: " + RedactIdentifiers(Clean(identity)));
            Log("Voice mode: " + Clean(voice));
            Log("Network registration: " + Clean(registration));
            Log(HasOk(callerId)
                ? "Caller ID notifications enabled."
                : "Caller ID notifications were not enabled; incoming-call detection will still use AT+CLCC.");

            _voiceReady = TryReadVoiceMode(voice, out var mode) && mode == 0;
            _callTimer.Start();
            _smsTimer.Start();
            if (_voiceReady)
                SetStatus("Modem responds and PC voice mode is enabled. The dial button is ready.");
            else if (TryReadVoiceMode(voice, out mode))
                SetStatus($"Modem answers, but PC voice mode is {mode}. Click Enable PC voice once before calling.");
            else
                SetStatus("Modem answers, but its firmware did not report a usable PC voice mode.");
        }
        catch (Exception ex)
        {
            _callTimer.Stop();
            SetStatus("Could not check the modem: " + ex.Message);
            Log("Check error: " + ex.Message);
            _voiceReady = false;
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task EnableVoiceAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            var voice = await SendAtAsync("AT^CVOICE?", TimeSpan.FromSeconds(2));
            if (!TryReadVoiceMode(voice, out var mode))
            {
                _voiceEnableRejected = true;
                _voiceReady = false;
                SetStatus("The firmware does not expose PC voice mode. No call was placed.");
                Log("Voice check failed: " + Clean(voice));
                return;
            }
            if (mode == 0)
            {
                _voiceReady = true;
                _voiceEnableRejected = false;
                SetStatus("PC voice mode is enabled. You can place a call.");
                return;
            }

            var response = await SendAtAsync("AT^CVOICE=0", TimeSpan.FromSeconds(3));
            Log("Enable PC voice: " + Clean(response));
            if (!HasOk(response))
            {
                _voiceEnableRejected = true;
                _voiceReady = false;
                SetStatus("Firmware rejected AT^CVOICE=0. Calls are disabled until voice-capable firmware is installed.");
                return;
            }

            voice = await SendAtAsync("AT^CVOICE?", TimeSpan.FromSeconds(2));
            _voiceReady = TryReadVoiceMode(voice, out mode) && mode == 0;
            if (_voiceReady)
                SetStatus("PC voice mode is enabled. You can place a call.");
            else
            {
                _voiceEnableRejected = true;
                SetStatus("Voice mode did not enable. Calls remain disabled.");
                Log("Voice readback: " + Clean(voice));
            }
        }
        catch (Exception ex)
        {
            SetStatus("Could not enable PC voice: " + ex.Message);
            Log("Voice enable error: " + ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task DialAsync()
    {
        if (!BeginBusy()) return;
        if (!_voiceReady)
        {
            SetStatus("Enable PC voice first. No call was placed.");
            EndBusy();
            return;
        }
        var number = _numberBox.Text.Trim();
        if (!Regex.IsMatch(number, @"^\+?[0-9*#]{1,32}$"))
        {
            SetStatus("Enter a phone number using digits, with an optional leading +.");
            EndBusy();
            return;
        }

        try
        {
            var voice = await SendAtAsync("AT^CVOICE?", TimeSpan.FromSeconds(2));
            if (!TryReadVoiceMode(voice, out var mode) || mode != 0)
            {
                _voiceReady = false;
                SetStatus("PC voice mode is no longer enabled. No call was placed; check the modem again.");
                Log("Voice check failed: " + Clean(voice));
                return;
            }

            var route = await SendAtAsync("AT^DDSETEX=2", TimeSpan.FromSeconds(2));
            if (!HasOk(route))
            {
                SetStatus("The modem rejected the PC audio route. No call was placed.");
                Log("Audio route: " + Clean(route));
                return;
            }

            StartAudioBridge();

            var response = await SendAtAsync($"ATD{number};", TimeSpan.FromSeconds(5));
            Log("Dial response: " + Clean(response));
            if (response.Contains("NO CARRIER", StringComparison.OrdinalIgnoreCase)
                || response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || !HasOk(response))
            {
                await StopAudioAsync();
                SetStatus("The modem did not start the call. See the response above.");
                return;
            }

            _callStarted = true;
            _currentCallNumber = number;
            RecordCallEvent("Outgoing", number, "Dialing");
            SetStatus("Dialing. Use Hang up to end the call.");
        }
        catch (Exception ex)
        {
            await StopAudioAsync();
            SetStatus("Call setup failed: " + ex.Message);
            Log("Call setup error: " + ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    private void StartAudioBridge()
    {
        var audioName = _audioPortBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(audioName))
            throw new InvalidOperationException("Choose the modem's PCM/audio port.");

        if (_microphoneBox.SelectedItem is not AudioDeviceChoice microphone)
            throw new InvalidOperationException("Choose a microphone or the system default.");

        if (_speakerBox.SelectedItem is not AudioDeviceChoice speaker)
            throw new InvalidOperationException("Choose speakers or the system default.");

        _audioPort = new SerialPort(audioName, 921600, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 120,
            WriteTimeout = 500,
            DtrEnable = false,
            RtsEnable = false,
            Handshake = Handshake.None,
            ReadBufferSize = 32768,
            WriteBufferSize = 32768
        };
        _audioPort.Open();
        _audioBridge = new VoiceAudioBridge(_audioPort, microphone.DeviceNumber, speaker.DeviceNumber);
        _audioBridge.Faulted += AudioBridgeOnFaulted;
        _audioBridge.Start();
        Log($"Audio devices: microphone '{microphone.DisplayName}', speakers '{speaker.DisplayName}'.");
    }

    private async Task AnswerIncomingAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            if (!_incomingCallRinging)
            {
                SetStatus("There is no incoming call to answer.");
                return;
            }
            if (!_voiceReady)
            {
                SetStatus("PC voice mode is not enabled, so this call cannot be answered on the PC.");
                return;
            }

            var currentCalls = await SendAtAsync("AT+CLCC", TimeSpan.FromSeconds(2), preserveInput: true);
            var incoming = ParseCurrentCalls(currentCalls).FirstOrDefault(call => call.Direction == 1 && call.State == 4 && call.Mode == 0);
            if (incoming is null && !HasIncomingRing(currentCalls))
            {
                ClearIncomingCall();
                SetStatus("The incoming call ended before it was answered.");
                return;
            }

            var route = await SendAtAsync("AT^DDSETEX=2", TimeSpan.FromSeconds(2));
            if (!HasOk(route))
            {
                SetStatus("The modem rejected the PC audio route. The incoming call is still ringing.");
                Log("Incoming audio route: " + Clean(route));
                return;
            }

            StartAudioBridge();
            var response = await SendAtAsync("ATA", TimeSpan.FromSeconds(5), preserveInput: true);
            Log("Answer response: " + Clean(response));
            if (response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || response.Contains("NO CARRIER", StringComparison.OrdinalIgnoreCase)
                || (!HasOk(response) && !HasConnect(response)))
            {
                await StopAudioAsync();
                SetStatus("The modem did not answer the call. If it is still ringing, try Answer again.");
                return;
            }

            _callStarted = true;
            _currentCallNumber = _incomingCaller;
            RecordCallEvent("Incoming", _incomingCaller, "Answered");
            ClearIncomingCallState();
            SetStatus("Call answered. Audio is using the selected microphone and speakers.");
        }
        catch (Exception ex)
        {
            await StopAudioAsync();
            SetStatus("Could not answer the call: " + ex.Message);
            Log("Answer error: " + ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task RejectIncomingAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            if (!_incomingCallRinging)
            {
                SetStatus("There is no incoming call to reject.");
                return;
            }

            var response = await SendAtAsync("AT+CHUP", TimeSpan.FromSeconds(3), preserveInput: true);
            Log("Reject response: " + Clean(response));
            RecordCallEvent("Incoming", _incomingCaller, "Rejected");
            _currentCallNumber = null;
            ClearIncomingCallState();
            SetStatus(HasOk(response) || response.Contains("NO CARRIER", StringComparison.OrdinalIgnoreCase)
                ? "Incoming call rejected."
                : "The call may have ended before the modem rejected it.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not reject the incoming call: " + ex.Message);
            Log("Reject error: " + ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task HangupAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            var wasActive = _callStarted;
            var endedNumber = _currentCallNumber;
            if (_controlPort is { IsOpen: true })
            {
                var response = await SendAtAsync("AT+CHUP", TimeSpan.FromSeconds(3), preserveInput: true);
                Log("Hang-up response: " + Clean(response));
            }
            _callStarted = false;
            if (wasActive) RecordCallEvent("Call", endedNumber, "Ended");
            _currentCallNumber = null;
            ClearIncomingCallState();
            await StopAudioAsync();
            SetStatus("Call ended.");
        }
        catch (Exception ex)
        {
            SetStatus("Hang-up returned an error: " + ex.Message);
            Log("Hang-up error: " + ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task RefreshCallStateAsync()
    {
        if (_busy || _callMonitorBusy || _controlPort is not { IsOpen: true }) return;
        _callMonitorBusy = true;
        try
        {
            var result = await SendAtAsync("AT+CLCC", TimeSpan.FromSeconds(1), preserveInput: true);
            _callMonitorErrorLogged = false;
            var calls = ParseCurrentCalls(result);
            var incoming = calls.FirstOrDefault(call => call.Direction == 1 && call.State == 4 && call.Mode == 0);
            if (incoming is not null)
            {
                ShowIncomingCall(incoming, result);
                return;
            }

            var waiting = calls.FirstOrDefault(call => call.Direction == 1 && call.State == 5 && call.Mode == 0);
            if (waiting is not null && _callStarted)
            {
                SetStatus("A second call is waiting; call switching is not supported in this dialer.");
                return;
            }

            if (HasIncomingRing(result) && !_callStarted)
            {
                ShowIncomingCall(null, result);
                return;
            }

            if (_callStarted)
            {
                var active = calls.FirstOrDefault(call => call.State is 0 or 1 or 2 or 3);
                if (active is not null)
                {
                    SetStatus(active.State switch
                    {
                        0 => "Call connected. Audio is using the selected microphone and speakers.",
                        1 => "Call is on hold.",
                        2 => "Dialing…",
                        3 => "Ringing…",
                        _ => "Call in progress. Use Hang up to end it."
                    });
                    return;
                }

                _callStarted = false;
                RecordCallEvent("Call", calls.FirstOrDefault()?.Number ?? _currentCallNumber, "Ended");
                _currentCallNumber = null;
                await StopAudioAsync();
                SetStatus("The network ended the call.");
                UpdateButtons();
                return;
            }

            if (_incomingCallRinging)
            {
                RecordCallEvent("Incoming", _incomingCaller, "Missed");
                ClearIncomingCallState();
                _currentCallNumber = null;
                SetStatus("Incoming call ended before answer.");
                UpdateButtons();
            }
        }
        catch (Exception ex)
        {
            if (!_callMonitorErrorLogged)
            {
                Log("Call monitoring: " + ex.Message);
                _callMonitorErrorLogged = true;
            }
        }
        finally
        {
            _callMonitorBusy = false;
        }
    }

    private void ShowIncomingCall(CurrentCall? call, string response)
    {
        var callId = call?.Id;
        var caller = call?.Number;
        if (string.IsNullOrWhiteSpace(caller))
            caller = ExtractCallerId(response);

        var isNewCall = !_incomingCallRinging || _incomingCallId != callId;
        _incomingCallRinging = true;
        _incomingCallId = callId;
        _incomingCaller = string.IsNullOrWhiteSpace(caller) ? null : caller;
        var callerLabel = _incomingCaller ?? "caller ID unavailable";
        SetStatus($"Incoming call from {callerLabel}. Click Answer or Reject.");
        if (isNewCall)
        {
            Log($"Incoming call from {callerLabel}.");
            RecordCallEvent("Incoming", _incomingCaller, "Ringing");
        }
        _currentCallNumber = _incomingCaller;
        UpdateButtons();
    }

    private static List<CurrentCall> ParseCurrentCalls(string response)
    {
        var calls = new List<CurrentCall>();
        var matches = Regex.Matches(response,
            @"\+CLCC:\s*(?<id>\d+)\s*,\s*(?<direction>\d+)\s*,\s*(?<state>\d+)\s*,\s*(?<mode>\d+)\s*,\s*\d+(?:\s*,\s*""(?<number>[^""]*)""(?:\s*,\s*\d+)?)?",
            RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["id"].Value, out var id)
                || !int.TryParse(match.Groups["direction"].Value, out var direction)
                || !int.TryParse(match.Groups["state"].Value, out var state)
                || !int.TryParse(match.Groups["mode"].Value, out var mode))
                continue;
            calls.Add(new CurrentCall(id, direction, state, mode,
                match.Groups["number"].Success ? match.Groups["number"].Value : null));
        }
        return calls;
    }

    private static bool HasIncomingRing(string response) =>
        Regex.IsMatch(response, @"(?:^|[\r\n])(?:RING|\+CRING:\s*VOICE)(?:[\r\n]|$)", RegexOptions.IgnoreCase);

    private static string? ExtractCallerId(string response)
    {
        var match = Regex.Match(response, @"\+CLIP:\s*""(?<number>[^""]*)""", RegexOptions.IgnoreCase);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups["number"].Value)
            ? match.Groups["number"].Value
            : null;
    }

    private void ClearIncomingCall()
    {
        ClearIncomingCallState();
        UpdateButtons();
    }

    private void ClearIncomingCallState()
    {
        _incomingCallRinging = false;
        _incomingCallId = null;
        _incomingCaller = null;
    }

    private SerialPort EnsureControlPort()
    {
        var name = _controlPortBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Select the modem's AT/control port.");
        if (_controlPort is { IsOpen: true } && string.Equals(_controlPort.PortName, name, StringComparison.OrdinalIgnoreCase))
            return _controlPort;

        CloseControlPort();
        _controlPort = new SerialPort(name, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 100,
            WriteTimeout = 1500,
            DtrEnable = false,
            RtsEnable = false,
            Handshake = Handshake.None,
            NewLine = "\r",
            ReadBufferSize = 8192,
            WriteBufferSize = 4096
        };
        _controlPort.Open();
        return _controlPort;
    }

    private async Task<string> SendAtAsync(string command, TimeSpan timeout, bool preserveInput = false)
    {
        await _commandLock.WaitAsync();
        try
        {
            var port = EnsureControlPort();
            if (!preserveInput)
            {
                var pending = port.ReadExisting();
                if (pending.Length > 0) ProcessUnsolicitedData(pending);
            }
            port.Write(command + "\r");
            var response = new StringBuilder();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                try
                {
                    var chunk = port.ReadExisting();
                    if (chunk.Length > 0) response.Append(chunk);
                }
                catch (TimeoutException)
                {
                }

                var text = response.ToString();
                if (Regex.IsMatch(text, @"(?:^|[\r\n])(?:OK|ERROR|NO CARRIER|BUSY|NO ANSWER|CONNECT(?:\s+\d+)?)(?:[\r\n]|$)", RegexOptions.IgnoreCase))
                {
                    ProcessUnsolicitedData(text);
                    return text;
                }
                await Task.Delay(50);
            }
            var responseText = response.ToString();
            ProcessUnsolicitedData(responseText);
            return responseText;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task StopAudioAsync()
    {
        var bridge = _audioBridge;
        _audioBridge = null;
        if (bridge is not null)
        {
            bridge.Faulted -= AudioBridgeOnFaulted;
            await bridge.StopAsync();
            bridge.Dispose();
        }
        if (_audioPort is not null)
        {
            try { if (_audioPort.IsOpen) _audioPort.Close(); }
            catch { }
            _audioPort.Dispose();
            _audioPort = null;
        }
    }

    private async Task CloseModemAsync()
    {
        _callTimer.Stop();
        _smsTimer.Stop();
        _networkTimer.Stop();
        if (_ussdSessionActive && _controlPort is { IsOpen: true })
        {
            try { await SendAtAsync("AT+CUSD=2", TimeSpan.FromSeconds(2)); }
            catch { }
        }
        if ((_callStarted || _incomingCallRinging) && _controlPort is { IsOpen: true })
        {
            try { await SendAtAsync("AT+CHUP", TimeSpan.FromSeconds(2)); }
            catch { }
        }
        _callStarted = false;
        ClearIncomingCallState();
        await StopAudioAsync();
        CloseControlPort();
    }

    private void CloseControlPort()
    {
        if (_controlPort is null) return;
        try { if (_controlPort.IsOpen) _controlPort.Close(); }
        catch { }
        _controlPort.Dispose();
        _controlPort = null;
    }

    private void AudioBridgeOnFaulted(string message)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            Log("Audio: " + message);
            SetStatus("Audio stream error. Hang up and check the selected microphone and speakers.");
        });
    }

    private bool BeginBusy()
    {
        if (_busy) return false;
        _busy = true;
        UpdateButtons();
        return true;
    }

    private void EndBusy()
    {
        _busy = false;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (IsDisposed) return;
        var callBusy = _callStarted || _incomingCallRinging;
        var idle = !_busy && !callBusy;
        _refreshButton.Enabled = idle;
        _refreshAudioButton.Enabled = idle;
        _checkButton.Enabled = idle;
        _enableVoiceButton.Enabled = idle && !_voiceReady && !_voiceEnableRejected
            && _controlPortBox.SelectedItem is not null;
        _callButton.Enabled = idle && _voiceReady
            && _controlPortBox.SelectedItem is not null
            && _audioPortBox.SelectedItem is not null
            && _microphoneBox.SelectedItem is AudioDeviceChoice
            && _speakerBox.SelectedItem is AudioDeviceChoice
            && Regex.IsMatch(_numberBox.Text.Trim(), @"^\+?[0-9*#]{1,32}$");
        _answerButton.Enabled = !_busy && _incomingCallRinging && _voiceReady
            && _controlPortBox.SelectedItem is not null
            && _audioPortBox.SelectedItem is not null
            && _microphoneBox.SelectedItem is AudioDeviceChoice
            && _speakerBox.SelectedItem is AudioDeviceChoice;
        _rejectButton.Enabled = !_busy && _incomingCallRinging;
        _hangupButton.Enabled = !_busy && _callStarted;
        _numberBox.Enabled = idle;
        _controlPortBox.Enabled = idle;
        _audioPortBox.Enabled = idle;
        _microphoneBox.Enabled = idle;
        _speakerBox.Enabled = idle;
        UpdateManagerButtons();
    }

    private void SetStatus(string text)
    {
        if (IsDisposed) return;
        _statusLabel.Text = text;
    }

    private void Log(string text)
    {
        if (IsDisposed) return;
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }

    private static bool HasOk(string response) => Regex.IsMatch(response, @"(?:^|[\r\n])OK(?:[\r\n]|$)", RegexOptions.IgnoreCase);

    private static bool HasConnect(string response) => Regex.IsMatch(response, @"(?:^|[\r\n])CONNECT(?:\s+\d+)?(?:[\r\n]|$)", RegexOptions.IgnoreCase);

    private static bool TryReadVoiceMode(string response, out int mode)
    {
        var match = Regex.Match(response, @"\^CVOICE:\s*(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out mode)) return true;
        mode = -1;
        return false;
    }

    private static string Clean(string text) => Regex.Replace(text.Trim(), @"[\r\n]+", " | ");

    private static string RedactIdentifiers(string text) => Regex.Replace(text, @"\bIMEI:\s*\d+", "IMEI: [redacted]", RegexOptions.IgnoreCase);
}
