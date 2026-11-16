using System.Text.RegularExpressions;

namespace HuaweiE303VoiceDialer;

internal sealed partial class MainForm
{
    private async Task RefreshNetworkAsync(bool showErrors)
    {
        if (_networkRefreshBusy || IsDisposed) return;
        _networkRefreshBusy = true;
        try
        {
            var status = await _mobileBroadband.ReadStatusAsync();
            if (status is null)
            {
                _networkStatusLabel.Text = "No Huawei Mobile Broadband interface is available. Check the Windows modem driver.";
                return;
            }
            var connected = status.ConnectionState.Contains("connected", StringComparison.OrdinalIgnoreCase)
                && !status.ConnectionState.Contains("not connected", StringComparison.OrdinalIgnoreCase);
            if (connected)
            {
                _sessionBytesReceivedStart ??= status.BytesReceived;
                _sessionBytesSentStart ??= status.BytesSent;
            }
            else
            {
                _sessionBytesReceivedStart = null;
                _sessionBytesSentStart = null;
            }
            var received = connected && _sessionBytesReceivedStart.HasValue
                ? Math.Max(0, status.BytesReceived - _sessionBytesReceivedStart.Value) : 0;
            var sent = connected && _sessionBytesSentStart.HasValue
                ? Math.Max(0, status.BytesSent - _sessionBytesSentStart.Value) : 0;
            _networkStatusLabel.Text = $"{status.Description} | {status.ConnectionState} | Network: {status.RegistrationState} | Signal: {status.Signal} | Session data: ↓ {FormatBytes(received)} / ↑ {FormatBytes(sent)}";
            if (connected && _networkTimer.Enabled) _networkStatusLabel.Text += " | Internet link active";
        }
        catch (Exception ex)
        {
            if (showErrors) SetStatus("Could not read Windows mobile broadband status: " + ex.Message);
            _networkStatusLabel.Text = "Mobile broadband status unavailable: " + ex.Message;
        }
        finally { _networkRefreshBusy = false; }
    }

    private async Task ConnectMobileDataAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            SaveManagerSettings();
            _networkStatusLabel.Text = "Requesting a mobile data connection from Windows…";
            var result = await _mobileBroadband.ConnectAsync(_apnBox.Text);
            _networkStatusLabel.Text = result.Message;
            if (!result.Success) SetStatus(result.Message);
            else
            {
                _sessionBytesReceivedStart = null;
                _sessionBytesSentStart = null;
                await RefreshNetworkAsync(showErrors: false);
            }
        }
        catch (Exception ex)
        {
            _networkStatusLabel.Text = "Connection failed: " + ex.Message;
            SetStatus("Could not start mobile data: " + ex.Message);
        }
        finally { EndBusy(); }
    }

    private async Task DisconnectMobileDataAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            var result = await _mobileBroadband.DisconnectAsync();
            _networkStatusLabel.Text = result.Message;
            if (!result.Success) SetStatus(result.Message);
            else await RefreshNetworkAsync(showErrors: false);
        }
        catch (Exception ex)
        {
            _networkStatusLabel.Text = "Disconnect failed: " + ex.Message;
            SetStatus("Could not disconnect mobile data: " + ex.Message);
        }
        finally { EndBusy(); }
    }

    private async Task ReadDeviceStatusAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            var identity = await SendAtAsync("ATI", TimeSpan.FromSeconds(3));
            var registration = await SendAtAsync("AT+CREG?", TimeSpan.FromSeconds(3));
            var signal = await SendAtAsync("AT+CSQ", TimeSpan.FromSeconds(3));
            var sim = await SendAtAsync("AT+CPIN?", TimeSpan.FromSeconds(3));
            var operatorName = await SendAtAsync("AT+COPS?", TimeSpan.FromSeconds(4));
            var voice = await SendAtAsync("AT^CVOICE?", TimeSpan.FromSeconds(3));
            var model = Regex.Match(identity, @"(?im)^Model:\s*(?<value>[^\r\n]+)");
            var revision = Regex.Match(identity, @"(?im)^Revision:\s*(?<value>[^\r\n]+)");
            var manufacturer = Regex.Match(identity, @"(?im)^Manufacturer:\s*(?<value>[^\r\n]+)");
            var op = Regex.Match(operatorName, @"\+COPS:\s*\d+\s*,\s*\d+\s*,\s*""(?<value>[^""]*)""");
            var simState = Regex.Match(sim, @"\+CPIN:\s*(?<value>[^\r\n]+)");
            var csq = Regex.Match(signal, @"\+CSQ:\s*(?<rssi>\d+)");
            var signalText = csq.Success ? DescribeCsq(int.Parse(csq.Groups["rssi"].Value)) : "unknown";
            var voiceText = TryReadVoiceMode(voice, out var voiceMode) ? $"PC voice mode {voiceMode}" : "PC voice mode unavailable";
            _deviceInfoLabel.Text = $"{manufacturer.Groups["value"].Value.Trim()} {model.Groups["value"].Value.Trim()} | Firmware {revision.Groups["value"].Value.Trim()} | "
                + $"Operator {op.Groups["value"].Value.Trim()} | Signal {signalText} | Registration {Clean(registration)} | SIM {simState.Groups["value"].Value.Trim()} | {voiceText}";
            _deviceInfoLabel.MaximumSize = new Size(880, 0);
            Log("Device status refreshed; hardware identifiers are omitted.");
            if (simState.Success && simState.Groups["value"].Value.Trim().Equals("SIM PUK", StringComparison.OrdinalIgnoreCase))
                SetStatus("The SIM requires its carrier-provided PUK code before it can register.");
        }
        catch (Exception ex)
        {
            _deviceInfoLabel.Text = "Device status could not be read: " + ex.Message;
            SetStatus("Could not read modem status: " + ex.Message);
        }
        finally { EndBusy(); }
    }

    private async Task RunDiagnosticsAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            var checks = new List<string>();
            foreach (var command in new[] { "AT", "AT+CREG?", "AT+CSQ", "AT+CPIN?", "AT+CMGF=?", "AT+CPBS=?", "AT+CUSD=?", "AT^CVOICE?" })
            {
                var response = await SendAtAsync(command, TimeSpan.FromSeconds(4));
                var summary = HasOk(response) ? Clean(response) : "not supported / no response";
                if (command == "AT") summary = HasOk(response) ? "modem responds" : "no response";
                checks.Add(command + "  " + summary);
            }
            var network = await _mobileBroadband.ReadStatusAsync();
            checks.Add(network is null ? "Windows MBN  no Huawei interface" :
                $"Windows MBN  {network.ConnectionState}; registration {network.RegistrationState}; signal {network.Signal}");
            MessageBox.Show(this, string.Join(Environment.NewLine, checks), "Modem diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Log("Safe read-only diagnostics completed.");
        }
        catch (Exception ex)
        {
            SetStatus("Diagnostics failed: " + ex.Message);
            Log("Diagnostics error: " + ex.Message);
        }
        finally { EndBusy(); }
    }

    private async Task ScanNetworksAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            _providerBox.Items.Clear();
            _deviceInfoLabel.Text = "Scanning operators. This can take up to two minutes…";
            var response = await SendAtAsync("AT+COPS=?", TimeSpan.FromSeconds(125));
            var providers = Regex.Matches(response,
                @"\((?<status>[0-3])\s*,\s*""(?<long>[^""]*)""\s*,\s*""(?<short>[^""]*)""\s*,\s*""(?<numeric>\d+)""(?:\s*,\s*(?<act>\d+))?\)");
            foreach (Match match in providers)
            {
                var item = new ProviderChoice(int.Parse(match.Groups["status"].Value), match.Groups["long"].Value,
                    match.Groups["short"].Value, match.Groups["numeric"].Value,
                    int.TryParse(match.Groups["act"].Value, out var act) ? act : null);
                if (!_providerBox.Items.Cast<ProviderChoice>().Any(existing => existing.Numeric == item.Numeric))
                    _providerBox.Items.Add(item);
            }
            if (_providerBox.Items.Count > 0) _providerBox.SelectedIndex = 0;
            _deviceInfoLabel.Text = _providerBox.Items.Count == 0
                ? "No available operators were returned. The modem may be out of coverage or this scan is unsupported."
                : $"Found {_providerBox.Items.Count} operator(s). Select one, or choose automatic selection.";
        }
        catch (Exception ex)
        {
            _deviceInfoLabel.Text = "Network scan failed: " + ex.Message;
            SetStatus("Could not scan mobile networks: " + ex.Message);
        }
        finally { EndBusy(); }
    }

    private async Task SelectProviderAsync()
    {
        if (_providerBox.SelectedItem is not ProviderChoice provider) return;
        if (MessageBox.Show(this, $"Ask the modem to register with {provider.LongName} ({provider.Numeric})? This can temporarily disconnect service.",
                "Select operator", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (!BeginBusy()) return;
        try
        {
            var response = await SendAtAsync($"AT+COPS=1,2,{QuoteAt(provider.Numeric)}", TimeSpan.FromSeconds(30));
            _deviceInfoLabel.Text = HasOk(response)
                ? $"Operator selection requested for {provider.LongName}. Allow time for registration."
                : "The modem rejected this operator selection.";
            await Task.Delay(2500);
            await RefreshNetworkAsync(showErrors: false);
        }
        catch (Exception ex) { SetStatus("Could not select operator: " + ex.Message); }
        finally { EndBusy(); }
    }

    private async Task SetAutomaticProviderAsync()
    {
        if (MessageBox.Show(this, "Return the modem to automatic network selection?", "Automatic network selection",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!BeginBusy()) return;
        try
        {
            var response = await SendAtAsync("AT+COPS=0", TimeSpan.FromSeconds(30));
            _deviceInfoLabel.Text = HasOk(response) ? "Automatic network selection requested." : "The modem rejected the request.";
            await Task.Delay(2000);
            await RefreshNetworkAsync(showErrors: false);
        }
        catch (Exception ex) { SetStatus("Could not return to automatic operator selection: " + ex.Message); }
        finally { EndBusy(); }
    }

    private async Task ReadSimPinStatusAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            var response = await SendAtAsync("AT+CPIN?", TimeSpan.FromSeconds(4));
            var status = Regex.Match(response, @"\+CPIN:\s*(?<state>[^\r\n]+)");
            var lockState = await SendAtAsync("AT+CLCK=\"SC\",2", TimeSpan.FromSeconds(4));
            var lockMatch = Regex.Match(lockState, @"\+CLCK:\s*(?<enabled>[01])");
            var pinLock = lockMatch.Success ? (lockMatch.Groups["enabled"].Value == "1" ? "enabled" : "disabled") : "unavailable";
            _deviceInfoLabel.Text = status.Success
                ? $"SIM state: {status.Groups["state"].Value.Trim()}; SIM PIN lock: {pinLock}."
                : "The modem did not report SIM security state. " + Clean(response);
        }
        catch (Exception ex) { _deviceInfoLabel.Text = "SIM security status unavailable: " + ex.Message; }
        finally { EndBusy(); }
    }

    private async Task SetSimPinLockAsync(bool enable)
    {
        var action = enable ? "enable" : "disable";
        if (MessageBox.Show(this, $"{(enable ? "Enable" : "Disable")} the SIM PIN lock? A wrong PIN can reduce the SIM's remaining attempts.",
                "SIM PIN protection", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var pin = PromptText("Enter the current SIM PIN. It is used once and is not saved.", "SIM PIN", "", password: true);
        if (pin is null) return;
        if (!Regex.IsMatch(pin, @"^\d{4,8}$"))
        {
            MessageBox.Show(this, "A SIM PIN must contain 4–8 digits.", "Invalid SIM PIN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!BeginBusy()) return;
        try
        {
            var response = await SendAtAsync($"AT+CLCK=\"SC\",{(enable ? 1 : 0)},{QuoteAt(pin)}", TimeSpan.FromSeconds(8));
            _deviceInfoLabel.Text = HasOk(response)
                ? $"SIM PIN lock {action} request succeeded. Check SIM status after re-registration."
                : "The modem rejected the SIM PIN operation. No automatic retry was made.";
        }
        catch (Exception ex) { _deviceInfoLabel.Text = "SIM PIN operation failed: " + ex.Message; }
        finally { EndBusy(); }
    }

    private async Task ChangeSimPinAsync()
    {
        var current = PromptText("Enter the current SIM PIN. It is not saved.", "Change SIM PIN", "", password: true);
        if (current is null) return;
        var next = PromptText("Enter the new 4–8 digit SIM PIN. It is not saved.", "Change SIM PIN", "", password: true);
        if (next is null) return;
        if (!Regex.IsMatch(current, @"^\d{4,8}$") || !Regex.IsMatch(next, @"^\d{4,8}$"))
        {
            MessageBox.Show(this, "Both PINs must contain 4–8 digits.", "Invalid SIM PIN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this, "Change the SIM PIN now? A wrong current PIN uses an attempt.", "Change SIM PIN",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (!BeginBusy()) return;
        try
        {
            var response = await SendAtAsync($"AT+CPWD=\"SC\",{QuoteAt(current)},{QuoteAt(next)}", TimeSpan.FromSeconds(8));
            _deviceInfoLabel.Text = HasOk(response) ? "SIM PIN changed." : "The modem rejected the PIN change. No retry was made.";
        }
        catch (Exception ex) { _deviceInfoLabel.Text = "SIM PIN change failed: " + ex.Message; }
        finally { EndBusy(); }
    }

    private async Task UnlockSimWithPukAsync()
    {
        if (!BeginBusy()) return;
        string? simState = null;
        try
        {
            var status = await SendAtAsync("AT+CPIN?", TimeSpan.FromSeconds(4));
            simState = Regex.Match(status, @"\+CPIN:\s*(?<state>[^\r\n]+)").Groups["state"].Value.Trim();
        }
        catch (Exception ex)
        {
            EndBusy();
            SetStatus("Could not check SIM state: " + ex.Message);
            return;
        }
        finally
        {
            if (_busy) EndBusy();
        }

        if (!string.Equals(simState, "SIM PUK", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, $"The SIM reports '{simState ?? "unknown"}', not SIM PUK. The PUK command was not sent.",
                "SIM does not require PUK", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, "Only enter the PUK supplied by your carrier. An incorrect PUK can permanently block the SIM. This app will send one attempt only.",
                "Unlock SIM", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var puk = PromptText("Enter the carrier-provided PUK.", "Unlock SIM", "", password: true);
        var newPin = PromptText("Choose a new 4–8 digit PIN.", "Unlock SIM", "", password: true);
        if (puk is null || newPin is null) return;
        if (!Regex.IsMatch(puk, @"^\d{8}$") || !Regex.IsMatch(newPin, @"^\d{4,8}$"))
        {
            MessageBox.Show(this, "PUK must be 8 digits and the new PIN must be 4–8 digits.", "Invalid SIM credentials", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!BeginBusy()) return;
        try
        {
            var response = await SendAtAsync($"AT+CPIN={QuoteAt(puk)},{QuoteAt(newPin)}", TimeSpan.FromSeconds(15));
            _deviceInfoLabel.Text = HasOk(response) ? "PUK accepted. Recheck SIM and network registration." : "PUK was rejected. No automatic retry was made.";
        }
        catch (Exception ex) { _deviceInfoLabel.Text = "SIM unlock failed: " + ex.Message; }
        finally { EndBusy(); }
    }

    private static string DescribeCsq(int value) => value switch
    {
        99 => "unknown",
        0 => "≤ −113 dBm",
        >= 1 and <= 31 => $"about {(-113 + 2 * value)} dBm",
        _ => "unknown"
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
