using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace HuaweiE303VoiceDialer;

internal sealed partial class MainForm
{
    private async Task RefreshSmsAsync(bool background = false)
    {
        if (background)
        {
            if (_busy || _smsPollingBusy || _callStarted || _incomingCallRinging || _controlPort is not { IsOpen: true }) return;
            _smsPollingBusy = true;
        }
        else if (!BeginBusy()) return;

        try
        {
            if (_controlPortBox.SelectedItem is null) throw new InvalidOperationException("Select the modem control port first.");
            if (!await PrepareSmsStorageAsync())
            {
                _smsStatusLabel.Text = "This modem did not accept PDU-mode SMS or SIM/device storage commands.";
                return;
            }

            var result = await SendAtAsync("AT+CMGL=4", TimeSpan.FromSeconds(15));
            if (!HasOk(result))
            {
                _smsStatusLabel.Text = "The modem could not list messages in its selected storage.";
                Log("SMS list command was rejected by the modem.");
                return;
            }

            var decoded = ParseSmsList(result);
            var rawCount = Regex.Matches(result, @"(?m)^\s*\+CMGL:", RegexOptions.IgnoreCase).Count;
            var grouped = GroupSmsParts(decoded);
            _smsRows.Clear();
            _smsRows.AddRange(grouped.OrderByDescending(row => row.Time));
            _smsGrid.Rows.Clear();
            foreach (var row in _smsRows)
            {
                var gridIndex = _smsGrid.Rows.Add(row.Status, row.Sender, row.Time.ToString("g"), PreviewText(row.Preview));
                _smsGrid.Rows[gridIndex].Tag = row;
            }

            var unread = decoded.Where(message => message.Status.Equals("Unread", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var message in unread)
            {
                if (_knownUnreadSmsIndexes.Add(message.Index) && _smsInitialized)
                    Log("A new SMS is stored on the modem/SIM. Open the SMS tab to read it.");
            }
            _knownUnreadSmsIndexes.RemoveWhere(index => unread.All(message => message.Index != index));
            _smsInitialized = true;
            var capacity = await SendAtAsync("AT+CPMS?", TimeSpan.FromSeconds(3));
            _smsStatusLabel.Text = $"{_smsRows.Count} stored message(s) in {_smsMemory ?? "modem"} memory. {unread.Count} unread. "
                + (Regex.Match(capacity, @"\+CPMS:\s*""\w+"",\s*(\d+)\s*,\s*(\d+)") is { Success: true } match
                    ? $"Storage: {match.Groups[1].Value}/{match.Groups[2].Value}." : "");
            if (rawCount > 0 && decoded.Count == 0)
                _smsStatusLabel.Text = $"Found {rawCount} stored message(s), but this modem's PDU format could not be decoded.";
            if (_smsRows.Count == 0) _smsDetailsBox.Clear();
        }
        catch (Exception ex)
        {
            if (!background) SetStatus("Could not read SMS storage: " + ex.Message);
            _smsStatusLabel.Text = "SMS storage could not be read. " + ex.Message;
            if (!background) Log("SMS read error: " + ex.Message);
        }
        finally
        {
            if (background) _smsPollingBusy = false;
            else EndBusy();
        }
    }

    private async Task<bool> PrepareSmsStorageAsync()
    {
        var supported = await SendAtAsync("AT+CMGF=?", TimeSpan.FromSeconds(3));
        if (!HasOk(supported) || !Regex.IsMatch(supported, @"(?:0|\(0\))")) return false;

        if (string.IsNullOrWhiteSpace(_smsMemory))
        {
            var memories = await SendAtAsync("AT+CPMS=?", TimeSpan.FromSeconds(4));
            if (!HasOk(memories)) return false;
            var names = Regex.Matches(memories, @"""(?<name>[A-Z]{2})""")
                .Select(match => match.Groups["name"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _smsMemory = new[] { "SM", "ME", "MT" }.FirstOrDefault(name => names.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (_smsMemory is null) return false;
        }

        var selectMemory = await SendAtAsync($"AT+CPMS={QuoteAt(_smsMemory)},{QuoteAt(_smsMemory)},{QuoteAt(_smsMemory)}", TimeSpan.FromSeconds(4));
        if (!HasOk(selectMemory))
        {
            _smsMemory = null;
            return false;
        }
        var pduMode = await SendAtAsync("AT+CMGF=0", TimeSpan.FromSeconds(3));
        if (!HasOk(pduMode)) return false;
        await SendAtAsync("AT+CNMI=2,1,0,0,0", TimeSpan.FromSeconds(3));
        return true;
    }

    private static List<DecodedSms> ParseSmsList(string response)
    {
        var messages = new List<DecodedSms>();
        var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var header = Regex.Match(lines[i], @"^\+CMGL:\s*(?<index>\d+)\s*,\s*(?<status>\d+)", RegexOptions.IgnoreCase);
            if (!header.Success || !int.TryParse(header.Groups["index"].Value, out var index)) continue;
            var state = header.Groups["status"].Value switch
            {
                "0" => "Unread",
                "1" => "Read",
                "2" => "Unsent",
                "3" => "Sent",
                _ => "Stored"
            };
            if (SmsPduCodec.TryDecodeDeliver(index, state, lines[i + 1], out var message) && message is not null)
                messages.Add(message);
        }
        return messages;
    }

    private static List<SmsListRow> GroupSmsParts(IReadOnlyList<DecodedSms> decoded)
    {
        var rows = new List<SmsListRow>();
        var consumed = new HashSet<int>();
        foreach (var message in decoded.OrderByDescending(item => item.Timestamp))
        {
            if (!consumed.Add(message.Index)) continue;
            if (message.ConcatenationReference is not null && message.PartCount > 1)
            {
                var group = decoded.Where(candidate => candidate.Sender == message.Sender
                        && candidate.ConcatenationReference == message.ConcatenationReference
                        && candidate.PartCount == message.PartCount
                        && Math.Abs((candidate.Timestamp - message.Timestamp).TotalHours) <= 24)
                    .OrderBy(candidate => candidate.PartNumber)
                    .ToList();
                if (group.Count == message.PartCount)
                {
                    foreach (var part in group) consumed.Add(part.Index);
                    var completeText = string.Concat(group.Select(part => part.Text));
                    rows.Add(new SmsListRow(group.Any(part => part.Status == "Unread") ? "Unread" : message.Status,
                        message.Sender, group.Max(part => part.Timestamp), completeText,
                        group.Select(part => part.Index).ToArray(), completeText));
                    continue;
                }
            }
            rows.Add(new SmsListRow(message.ConcatenationReference is null ? message.Status : $"{message.Status} (part {message.PartNumber}/{message.PartCount})",
                message.Sender, message.Timestamp, message.Text, new[] { message.Index }, message.Text));
        }
        return rows;
    }

    private void ShowSelectedSmsPreview()
    {
        var row = SelectedSmsRow();
        _smsDetailsBox.Text = row is null ? "" : $"From: {row.Sender}{Environment.NewLine}Time: {row.Time:g}{Environment.NewLine}Status: {row.Status}{Environment.NewLine}{Environment.NewLine}{row.FullText}";
    }

    private SmsListRow? SelectedSmsRow() => _smsGrid.CurrentRow?.Tag as SmsListRow;

    private async Task ReadSelectedSmsAsync()
    {
        var row = SelectedSmsRow();
        if (row is null) return;
        if (!BeginBusy()) return;
        try
        {
            if (!await PrepareSmsStorageAsync()) throw new InvalidOperationException("SMS PDU mode is unavailable.");
            var parts = new List<DecodedSms>();
            foreach (var index in row.Indexes)
            {
                var result = await SendAtAsync($"AT+CMGR={index}", TimeSpan.FromSeconds(8));
                var lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var pdu = lines.FirstOrDefault(line => Regex.IsMatch(line, @"^[0-9A-Fa-f]+$"));
                if (pdu is null || !SmsPduCodec.TryDecodeDeliver(index, "Read", pdu, out var decoded) || decoded is null)
                    throw new InvalidOperationException("The modem did not return a readable SMS PDU.");
                parts.Add(decoded);
            }
            var first = parts.OrderBy(part => part.PartNumber).First();
            var body = string.Concat(parts.OrderBy(part => part.PartNumber).Select(part => part.Text));
            _smsDetailsBox.Text = $"From: {first.Sender}{Environment.NewLine}Time: {first.Timestamp:g}{Environment.NewLine}Status: Read{Environment.NewLine}{Environment.NewLine}{body}";
            await RefreshSmsCoreAsync();
        }
        catch (Exception ex)
        {
            _smsStatusLabel.Text = ex.Message;
            SetStatus("Could not read the selected SMS: " + ex.Message);
        }
        finally { EndBusy(); }
    }

    private async Task DeleteSelectedSmsAsync()
    {
        var row = SelectedSmsRow();
        if (row is null) return;
        if (MessageBox.Show(this, $"Delete the selected message from {_smsMemory ?? "modem/SIM"} storage?", "Delete SMS", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        if (!BeginBusy()) return;
        try
        {
            if (!await PrepareSmsStorageAsync()) throw new InvalidOperationException("SMS PDU mode is unavailable.");
            foreach (var index in row.Indexes)
            {
                var response = await SendAtAsync($"AT+CMGD={index}", TimeSpan.FromSeconds(5));
                if (!HasOk(response)) throw new InvalidOperationException($"The modem could not delete message index {index}.");
            }
            _smsDetailsBox.Clear();
            _smsStatusLabel.Text = "Selected message deleted from modem/SIM storage.";
            await RefreshSmsCoreAsync();
        }
        catch (Exception ex) { SetStatus("Could not delete SMS: " + ex.Message); }
        finally { EndBusy(); }
    }

    private void ReplyToSelectedSms()
    {
        var row = SelectedSmsRow();
        if (row is null) return;
        _smsNumberBox.Text = row.Sender;
        _smsComposeBox.Focus();
    }

    private async Task SendSmsAsync()
    {
        var recipient = _smsNumberBox.Text.Trim();
        var body = _smsComposeBox.Text;
        IReadOnlyList<(string Pdu, int TpduLength)> segments;
        try { segments = SmsPduCodec.EncodeSubmit(recipient, body); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SMS not sent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this, $"Send this message as {segments.Count} SMS segment(s) to {recipient}? Operator charges may apply.",
                "Confirm SMS", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!BeginBusy()) return;
        try
        {
            if (!await PrepareSmsStorageAsync()) throw new InvalidOperationException("This modem did not accept SMS PDU mode.");
            for (var i = 0; i < segments.Count; i++)
            {
                var result = await SendSmsPduAsync(segments[i].Pdu, segments[i].TpduLength);
                if (!Regex.IsMatch(result, @"\+CMGS:\s*\d+", RegexOptions.IgnoreCase) || !HasOk(result))
                    throw new InvalidOperationException($"The modem did not confirm SMS segment {i + 1} of {segments.Count}.");
            }
            _smsComposeBox.Clear();
            _smsStatusLabel.Text = $"Sent {segments.Count} segment(s). The modem may not retain sent messages.";
            Log($"SMS accepted by modem ({segments.Count} segment(s)); recipient and message text were not logged.");
            await RefreshSmsCoreAsync();
        }
        catch (Exception ex)
        {
            _smsStatusLabel.Text = "SMS send failed: " + ex.Message;
            SetStatus("SMS send failed. Check registration, balance and SMS service.");
        }
        finally { EndBusy(); }
    }

    private async Task<string> SendSmsPduAsync(string pdu, int tpduLength)
    {
        await _commandLock.WaitAsync();
        try
        {
            var port = EnsureControlPort();
            var pending = port.ReadExisting();
            if (pending.Length > 0) ProcessUnsolicitedData(pending);
            port.Write("AT+CMGF=0\r");
            var mode = await ReadAtResponseAsync(port, TimeSpan.FromSeconds(4));
            if (!HasOk(mode)) return mode;

            port.Write($"AT+CMGS={tpduLength}\r");
            var prompt = await ReadAtResponseAsync(port, TimeSpan.FromSeconds(5), waitForPrompt: true);
            if (!prompt.Contains('>')) return prompt;
            port.Write(pdu + (char)26);
            var result = await ReadAtResponseAsync(port, TimeSpan.FromSeconds(45));
            ProcessUnsolicitedData(result);
            return result;
        }
        finally { _commandLock.Release(); }
    }

    private async Task<string> ReadAtResponseAsync(SerialPort port, TimeSpan timeout, bool waitForPrompt = false)
    {
        var response = new StringBuilder();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            try
            {
                var chunk = port.ReadExisting();
                if (chunk.Length > 0) response.Append(chunk);
            }
            catch (TimeoutException) { }
            var text = response.ToString();
            if (waitForPrompt && text.Contains('>')) return text;
            if (Regex.IsMatch(text, @"(?:^|[\r\n])(?:OK|ERROR|NO CARRIER|BUSY)(?:[\r\n]|$)", RegexOptions.IgnoreCase))
                return text;
            await Task.Delay(50);
        }
        return response.ToString();
    }

    private async Task RefreshSmsCoreAsync()
    {
        if (!await PrepareSmsStorageAsync()) throw new InvalidOperationException("SMS PDU mode is unavailable.");
        var result = await SendAtAsync("AT+CMGL=4", TimeSpan.FromSeconds(15));
        if (!HasOk(result)) throw new InvalidOperationException("The modem did not list its stored messages.");
        var decoded = ParseSmsList(result);
        var rawCount = Regex.Matches(result, @"(?m)^\s*\+CMGL:", RegexOptions.IgnoreCase).Count;
        _smsRows.Clear();
        _smsRows.AddRange(GroupSmsParts(decoded).OrderByDescending(row => row.Time));
        _smsGrid.Rows.Clear();
        foreach (var row in _smsRows)
        {
            var index = _smsGrid.Rows.Add(row.Status, row.Sender, row.Time.ToString("g"), PreviewText(row.Preview));
            _smsGrid.Rows[index].Tag = row;
        }
        _smsStatusLabel.Text = rawCount > 0 && decoded.Count == 0
            ? $"Found {rawCount} stored message(s), but this modem's PDU format could not be decoded."
            : $"{_smsRows.Count} stored message(s) in {_smsMemory ?? "modem"} memory.";
    }

    private static string PreviewText(string text) => text.Replace("\r", " ").Replace("\n", " ").Trim();

    private async Task SendUssdAsync(string request, bool startSession)
    {
        var value = request.Trim();
        var allowed = startSession ? @"^[0-9*#+]{1,64}$" : @"^[\x20-\x7E]{1,64}$";
        if (!Regex.IsMatch(value, allowed) || value.Contains('"'))
        {
            MessageBox.Show(this, startSession ? "Enter a USSD code using digits, *, #, and +." : "Enter a short text or menu number without quotation marks.",
                "Invalid USSD input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!startSession && !_ussdSessionActive)
        {
            MessageBox.Show(this, "There is no active USSD menu to reply to. Send a code first.", "No active USSD session", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!BeginBusy()) return;
        try
        {
            _ussdTranscriptBox.AppendText($"{DateTime.Now:t}  You: {value}{Environment.NewLine}");
            var result = await SendAtAsync($"AT+CUSD=1,{QuoteAt(value)},15", TimeSpan.FromSeconds(4));
            if (!HasOk(result) && !result.Contains("+CUSD:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The modem rejected the USSD request.");
            if (!result.Contains("+CUSD:", StringComparison.OrdinalIgnoreCase)) _ussdSessionActive = true;
            _ussdStatusLabel.Text = "USSD request sent. Waiting for the network response…";
            Log("USSD request sent; code and response were not added to the activity log.");
            _ussdCodeBox.Clear();
            _ussdReplyBox.Clear();
        }
        catch (Exception ex)
        {
            _ussdStatusLabel.Text = "USSD failed: " + ex.Message;
            SetStatus("Could not send USSD request: " + ex.Message);
        }
        finally { EndBusy(); }
    }

    private async Task EndUssdSessionAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            var result = await SendAtAsync("AT+CUSD=2", TimeSpan.FromSeconds(4));
            _ussdSessionActive = false;
            _ussdStatusLabel.Text = HasOk(result) ? "USSD session ended." : "The modem did not confirm the session ended.";
        }
        catch (Exception ex) { _ussdStatusLabel.Text = ex.Message; }
        finally { EndBusy(); }
    }

    private void ProcessUnsolicitedData(string data)
    {
        if (IsDisposed || string.IsNullOrWhiteSpace(data)) return;
        var ussd = Regex.Match(data, @"\+CUSD:\s*(?<mode>\d+)(?:\s*,\s*""(?<text>(?:[^""]|"")*)""(?:\s*,\s*(?<dcs>\d+))?)?", RegexOptions.IgnoreCase);
        if (ussd.Success)
        {
            var text = ussd.Groups["text"].Value.Replace("\"\"", "\"");
            if (int.TryParse(ussd.Groups["dcs"].Value, out var dcs) && (dcs is 72 or 8) && Regex.IsMatch(text, @"\A(?:[0-9A-Fa-f]{4})+\z"))
            {
                try { text = Encoding.BigEndianUnicode.GetString(Convert.FromHexString(text)); }
                catch { }
            }
            if (!string.IsNullOrEmpty(text)) _ussdTranscriptBox.AppendText($"{DateTime.Now:t}  {text}{Environment.NewLine}{Environment.NewLine}");
            if (int.TryParse(ussd.Groups["mode"].Value, out var mode))
            {
                _ussdSessionActive = mode is 1 or 2;
                _ussdStatusLabel.Text = mode switch
                {
                    0 => "Network response received; session ended.",
                    1 or 2 => "Network response received; you can send a menu reply.",
                    4 => "Network does not support this USSD operation.",
                    5 => "USSD network timeout.",
                    _ => "USSD response received."
                };
            }
        }
        if (Regex.IsMatch(data, @"\+CMTI:", RegexOptions.IgnoreCase) && !_busy && !_smsPollingBusy)
        {
            BeginInvoke(async () => await RefreshSmsAsync(background: true));
        }
    }

    private async Task RefreshContactsAsync()
    {
        if (!BeginBusy()) return;
        try
        {
            if (_controlPortBox.SelectedItem is null) throw new InvalidOperationException("Select the modem control port first.");
            var supported = await SendAtAsync("AT+CPBS=?", TimeSpan.FromSeconds(4));
            if (!HasOk(supported))
            {
                _contactStatusLabel.Text = "This modem does not expose its phonebook over AT commands.";
                return;
            }
            var memories = Regex.Matches(supported, @"""(?<name>[A-Z]{2})""")
                .Select(match => match.Groups["name"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var memory = new[] { "SM", "ME" }.FirstOrDefault(name => memories.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (memory is null)
            {
                _contactStatusLabel.Text = "No SIM/device phonebook storage is available.";
                return;
            }
            var selected = await SendAtAsync($"AT+CPBS={QuoteAt(memory)}", TimeSpan.FromSeconds(4));
            if (!HasOk(selected)) throw new InvalidOperationException($"Could not select {memory} phonebook storage.");
            var range = await SendAtAsync("AT+CPBR=?", TimeSpan.FromSeconds(4));
            var rangeMatch = Regex.Match(range, @"\+CPBR:\s*\(\s*\d+\s*-\s*(?<last>\d+)\s*\)");
            _phonebookLastIndex = rangeMatch.Success && int.TryParse(rangeMatch.Groups["last"].Value, out var last)
                ? Math.Clamp(last, 1, 1000) : 250;
            var limits = Regex.Match(range, @"\+CPBR:\s*\(\s*\d+\s*-\s*\d+\s*\)\s*,\s*(?<number>\d+)\s*,\s*(?<name>\d+)");
            if (limits.Success)
            {
                if (int.TryParse(limits.Groups["number"].Value, out var numberLength)) _phonebookNumberLength = Math.Clamp(numberLength, 1, 40);
                if (int.TryParse(limits.Groups["name"].Value, out var nameLength)) _phonebookNameLength = Math.Clamp(nameLength, 1, 40);
            }
            var result = await SendAtAsync($"AT+CPBR=1,{_phonebookLastIndex}", TimeSpan.FromSeconds(20));
            if (!HasOk(result)) throw new InvalidOperationException("The modem did not return phonebook entries.");
            var entries = Regex.Matches(result,
                @"(?m)^\s*\+CPBR:\s*(?<index>\d+)\s*,\s*""(?<number>[^""]*)""\s*,\s*(?<type>\d+)\s*,\s*""(?<name>[^""]*)""");
            _contactsGrid.Rows.Clear();
            foreach (Match entry in entries)
            {
                var idx = _contactsGrid.Rows.Add(entry.Groups["index"].Value, entry.Groups["name"].Value,
                    entry.Groups["number"].Value);
                _contactsGrid.Rows[idx].Tag = int.Parse(entry.Groups["index"].Value, CultureInfo.InvariantCulture);
            }
            var capacity = Regex.Match(result, @"\+CPBS:\s*""\w+"",\s*(?<used>\d+)\s*,\s*(?<total>\d+)");
            _contactStatusLabel.Text = capacity.Success
                ? $"{_contactsGrid.Rows.Count} contacts loaded from {memory}; storage {capacity.Groups["used"].Value}/{capacity.Groups["total"].Value}."
                : $"{_contactsGrid.Rows.Count} contacts loaded from {memory} storage.";
        }
        catch (Exception ex)
        {
            _contactStatusLabel.Text = ex.Message;
            SetStatus("Could not read contacts: " + ex.Message);
        }
        finally { EndBusy(); }
    }

    private void ShowSelectedContact()
    {
        if (_contactsGrid.CurrentRow is null) return;
        _contactNameBox.Text = Convert.ToString(_contactsGrid.CurrentRow.Cells["Name"].Value) ?? "";
        _contactNumberBox.Text = Convert.ToString(_contactsGrid.CurrentRow.Cells["Number"].Value) ?? "";
    }

    private async Task AddContactAsync()
    {
        if (!ValidateContactFields()) return;
        if (!BeginBusy()) return;
        try
        {
            var number = _contactNumberBox.Text.Trim();
            var type = number.StartsWith('+') ? 145 : 129;
            var response = await SendAtAsync($"AT+CPBW=,{QuoteAt(number)},{type},{QuoteAt(_contactNameBox.Text.Trim())}", TimeSpan.FromSeconds(6));
            if (!HasOk(response)) throw new InvalidOperationException("The modem rejected this phonebook entry.");
            _contactStatusLabel.Text = "Contact saved to SIM/device storage.";
            await RefreshContactsCoreAsync();
        }
        catch (Exception ex) { _contactStatusLabel.Text = ex.Message; }
        finally { EndBusy(); }
    }

    private async Task UpdateContactAsync()
    {
        if (!ValidateContactFields()) return;
        if (_contactsGrid.CurrentRow?.Tag is not int index) return;
        if (!BeginBusy()) return;
        try
        {
            var number = _contactNumberBox.Text.Trim();
            var type = number.StartsWith('+') ? 145 : 129;
            var response = await SendAtAsync($"AT+CPBW={index},{QuoteAt(number)},{type},{QuoteAt(_contactNameBox.Text.Trim())}", TimeSpan.FromSeconds(6));
            if (!HasOk(response)) throw new InvalidOperationException("The modem rejected the phonebook change.");
            _contactStatusLabel.Text = "Contact updated in SIM/device storage.";
            await RefreshContactsCoreAsync();
        }
        catch (Exception ex) { _contactStatusLabel.Text = ex.Message; }
        finally { EndBusy(); }
    }

    private async Task DeleteContactAsync()
    {
        if (_contactsGrid.CurrentRow?.Tag is not int index) return;
        if (MessageBox.Show(this, "Delete this contact from SIM/device storage?", "Delete contact", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        if (!BeginBusy()) return;
        try
        {
            var response = await SendAtAsync($"AT+CPBW={index}", TimeSpan.FromSeconds(6));
            if (!HasOk(response)) throw new InvalidOperationException("The modem rejected the deletion.");
            _contactNameBox.Clear();
            _contactNumberBox.Clear();
            _contactStatusLabel.Text = "Contact deleted from SIM/device storage.";
            await RefreshContactsCoreAsync();
        }
        catch (Exception ex) { _contactStatusLabel.Text = ex.Message; }
        finally { EndBusy(); }
    }

    private bool ValidateContactFields()
    {
        var name = _contactNameBox.Text.Trim();
        var number = _contactNumberBox.Text.Trim();
        if (name.Length < 1 || name.Length > _phonebookNameLength || name.Any(ch => char.IsControl(ch) || ch == '"'))
        {
            MessageBox.Show(this, $"Enter a name up to {_phonebookNameLength} characters without quotation marks.", "Invalid contact", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        var digitCount = number.Count(char.IsAsciiDigit);
        if (!Regex.IsMatch(number, @"^\+?[0-9]+$") || digitCount > _phonebookNumberLength)
        {
            MessageBox.Show(this, $"Enter digits with an optional leading +, up to {_phonebookNumberLength} digits.", "Invalid contact", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    private async Task RefreshContactsCoreAsync()
    {
        var supported = await SendAtAsync("AT+CPBS=?", TimeSpan.FromSeconds(4));
        var memory = Regex.Matches(supported, @"""(?<name>[A-Z]{2})""")
            .Select(match => match.Groups["name"].Value)
            .FirstOrDefault(name => name is "SM" or "ME");
        if (memory is null) return;
        await SendAtAsync($"AT+CPBS={QuoteAt(memory)}", TimeSpan.FromSeconds(4));
        var range = await SendAtAsync("AT+CPBR=?", TimeSpan.FromSeconds(4));
        var match = Regex.Match(range, @"\+CPBR:\s*\(\s*\d+\s*-\s*(?<last>\d+)\s*\)");
        if (match.Success && int.TryParse(match.Groups["last"].Value, out var last)) _phonebookLastIndex = Math.Clamp(last, 1, 1000);
        var result = await SendAtAsync($"AT+CPBR=1,{_phonebookLastIndex}", TimeSpan.FromSeconds(20));
        if (!HasOk(result)) return;
        _contactsGrid.Rows.Clear();
        foreach (Match entry in Regex.Matches(result,
            @"(?m)^\s*\+CPBR:\s*(?<index>\d+)\s*,\s*""(?<number>[^""]*)""\s*,\s*\d+\s*,\s*""(?<name>[^""]*)"""))
        {
            var idx = _contactsGrid.Rows.Add(entry.Groups["index"].Value, entry.Groups["name"].Value, entry.Groups["number"].Value);
            _contactsGrid.Rows[idx].Tag = int.Parse(entry.Groups["index"].Value, CultureInfo.InvariantCulture);
        }
    }
}
