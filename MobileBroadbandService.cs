using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HuaweiE303VoiceDialer;

internal sealed record MobileBroadbandSnapshot(
    string InterfaceName,
    string Description,
    string ConnectionState,
    string RegistrationState,
    string Signal,
    long BytesReceived,
    long BytesSent);

internal sealed class MobileBroadbandService
{
    private static readonly XNamespace ProfileNamespace = "https://www.microsoft.com/networking/WWAN/profile/v1";
    public async Task<MobileBroadbandSnapshot?> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        var device = await FindInterfaceAsync(cancellationToken);
        if (device is null) return null;

        var connection = await RunNetshAsync(new[] { "mbn", "show", "connection", $"interface={device.Value.Name}" }, cancellationToken);
        var signal = await RunNetshAsync(new[] { "mbn", "show", "signal", $"interface={device.Value.Name}" }, cancellationToken);
        var state = MatchValue(connection.Output, "Interface State") ?? "Unknown";
        var registration = MatchValue(connection.Output, "Register State") ?? "Unknown";
        var signalValue = MatchValue(signal.Output, "Signal") ?? "Unavailable";
        var (received, sent) = ReadAdapterBytes(device.Value.Description);
        return new MobileBroadbandSnapshot(device.Value.Name, device.Value.Description, state, registration,
            signalValue, received, sent);
    }

    public async Task<(bool Success, string Message)> ConnectAsync(string accessPointName, CancellationToken cancellationToken = default)
    {
        var device = await FindInterfaceAsync(cancellationToken);
        if (device is null) return (false, "Windows does not expose a Huawei Mobile Broadband interface.");

        string[] arguments;
        string? profilePath = null;
        var apn = accessPointName.Trim();
        if (apn.Length > 0)
        {
            if (apn.Length > 100 || apn.Any(char.IsControl))
                return (false, "The APN must be 1–100 characters without control characters.");

            profilePath = Path.Combine(Path.GetTempPath(), "gp-modem-" + Guid.NewGuid().ToString("N") + ".xml");
            await WriteTemporaryProfileAsync(profilePath, apn, cancellationToken);
            arguments = new[] { "mbn", "connect", $"interface={device.Value.Name}", "connmode=tmp", $"name={profilePath}" };
        }
        else
        {
            var profiles = await RunNetshAsync(new[] { "mbn", "show", "profiles", $"interface={device.Value.Name}" }, cancellationToken);
            var profileName = Regex.Match(profiles.Output, @"(?im)^\s*Profile Name\s*:\s*(?<name>.+?)\s*$");
            if (!profileName.Success)
                return (false, "Enter your carrier APN first, or create a Windows Mobile Broadband profile.");
            arguments = new[] { "mbn", "connect", $"interface={device.Value.Name}", "connmode=name", $"name={profileName.Groups["name"].Value.Trim()}" };
        }

        try
        {
            var result = await RunNetshAsync(arguments, cancellationToken, TimeSpan.FromSeconds(45));
            if (result.ExitCode == 0) return (true, "Windows accepted the mobile data connection request.");
            return (false, "Windows could not connect. Check the APN, SIM data service, registration, and Windows WWAN service.");
        }
        finally
        {
            if (profilePath is not null)
            {
                try { File.Delete(profilePath); }
                catch { }
            }
        }
    }

    public async Task<(bool Success, string Message)> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var device = await FindInterfaceAsync(cancellationToken);
        if (device is null) return (false, "Windows does not expose a Huawei Mobile Broadband interface.");
        var result = await RunNetshAsync(new[] { "mbn", "disconnect", $"interface={device.Value.Name}" }, cancellationToken,
            TimeSpan.FromSeconds(30));
        return result.ExitCode == 0
            ? (true, "Windows accepted the disconnect request.")
            : (false, "Windows could not disconnect the mobile data session.");
    }

    public async Task<IReadOnlyList<string>> ScanProvidersAsync(CancellationToken cancellationToken = default)
    {
        var device = await FindInterfaceAsync(cancellationToken);
        if (device is null) throw new InvalidOperationException("Windows does not expose a Huawei Mobile Broadband interface.");
        var result = await RunNetshAsync(new[] { "mbn", "show", "visibleproviders", $"interface={device.Value.Name}" },
            cancellationToken, TimeSpan.FromSeconds(90));
        if (result.ExitCode != 0) throw new InvalidOperationException("Windows could not scan available mobile providers.");
        return Regex.Matches(result.Output, @"(?m)^\s*(?<name>[^\r\n]+?)\s*$")
            .Select(match => match.Groups["name"].Value.Trim())
            .Where(line => line.Length > 0 && !line.Contains(':') && !line.StartsWith("-", StringComparison.Ordinal)
                && !line.Contains("provider", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("interface", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
    }

    private async Task<(string Name, string Description)?> FindInterfaceAsync(CancellationToken cancellationToken)
    {
        var result = await RunNetshAsync(new[] { "mbn", "show", "interfaces" }, cancellationToken);
        if (result.ExitCode != 0) return null;
        var blocks = Regex.Split(result.Output, @"(?im)(?=^\s*Name\s*:)");
        foreach (var block in blocks)
        {
            var name = MatchValue(block, "Name");
            var description = MatchValue(block, "Description");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description)) continue;
            if (!description.Contains("Huawei", StringComparison.OrdinalIgnoreCase)
                && !description.Contains("E303", StringComparison.OrdinalIgnoreCase)) continue;
            return (name, description);
        }
        return null;
    }

    private static async Task WriteTemporaryProfileAsync(string path, string apn, CancellationToken cancellationToken)
    {
        var document = new XDocument(
            new XElement(ProfileNamespace + "MBNProfile",
                new XElement(ProfileNamespace + "Name", "GP Modem Temporary"),
                new XElement(ProfileNamespace + "IsDefault", "false"),
                new XElement(ProfileNamespace + "ConnectionMode", "manual"),
                new XElement(ProfileNamespace + "Context",
                    new XElement(ProfileNamespace + "AccessString", apn),
                    new XElement(ProfileNamespace + "Compression", "DISABLE"),
                    new XElement(ProfileNamespace + "AuthProtocol", "NONE"),
                    new XElement(ProfileNamespace + "IPType", "IPV4V6"))));
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await document.SaveAsync(stream, SaveOptions.None, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<(int ExitCode, string Output)> RunNetshAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.Default,
                StandardErrorEncoding = System.Text.Encoding.Default
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            var output = await stdoutTask + Environment.NewLine + await stderrTask;
            return (process.ExitCode, output);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            return (-1, "The Windows mobile broadband command timed out.");
        }
    }

    private static string? MatchValue(string output, string label)
    {
        var match = Regex.Match(output, $@"(?im)^\s*{Regex.Escape(label)}\s*:\s*(?<value>[^\r\n]+)");
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static (long Received, long Sent) ReadAdapterBytes(string description)
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(item => string.Equals(item.Description, description, StringComparison.OrdinalIgnoreCase));
            if (adapter is null) return (0, 0);
            var stats = adapter.GetIPv4Statistics();
            return (stats.BytesReceived, stats.BytesSent);
        }
        catch { return (0, 0); }
    }
}
