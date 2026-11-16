using System.IO.Ports;
using NAudio.Wave;

namespace HuaweiE303VoiceDialer;

internal sealed class VoiceAudioBridge : IDisposable
{
    private readonly SerialPort _port;
    private readonly int _microphoneDeviceNumber;
    private readonly int _speakerDeviceNumber;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _writeLock = new();
    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playbackBuffer;
    private Task? _readerTask;
    private bool _started;
    private int _faultReported;

    public event Action<string>? Faulted;

    public VoiceAudioBridge(SerialPort port, int microphoneDeviceNumber, int speakerDeviceNumber)
    {
        _port = port;
        _microphoneDeviceNumber = microphoneDeviceNumber;
        _speakerDeviceNumber = speakerDeviceNumber;
    }

    public void Start()
    {
        if (_started) return;
        var format = new WaveFormat(8000, 16, 1);
        _playbackBuffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        _waveOut = new WaveOutEvent
        {
            DeviceNumber = _speakerDeviceNumber,
            DesiredLatency = 80,
            NumberOfBuffers = 3
        };
        _waveOut.Init(_playbackBuffer);
        _waveOut.Play();

        _waveIn = new WaveInEvent
        {
            DeviceNumber = _microphoneDeviceNumber,
            WaveFormat = format,
            BufferMilliseconds = 20,
            NumberOfBuffers = 4
        };
        _waveIn.DataAvailable += OnMicrophoneData;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
        _readerTask = Task.Run(ReadModemAudioLoop);
        _started = true;
    }

    private void OnMicrophoneData(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded <= 0 || !_port.IsOpen || _cancel.IsCancellationRequested) return;
        try
        {
            lock (_writeLock)
                _port.Write(args.Buffer, 0, args.BytesRecorded);
        }
        catch (Exception ex)
        {
            ReportFault(ex.Message);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) ReportFault(args.Exception.Message);
    }

    private void ReadModemAudioLoop()
    {
        var buffer = new byte[4096];
        while (!_cancel.IsCancellationRequested)
        {
            try
            {
                var count = _port.Read(buffer, 0, buffer.Length);
                if (count > 0) _playbackBuffer?.AddSamples(buffer, 0, count);
            }
            catch (TimeoutException)
            {
            }
            catch (Exception ex)
            {
                if (!_cancel.IsCancellationRequested) ReportFault(ex.Message);
                break;
            }
        }
    }

    private void ReportFault(string message)
    {
        if (Interlocked.Exchange(ref _faultReported, 1) == 0)
            Faulted?.Invoke(message);
    }

    public async Task StopAsync()
    {
        if (!_cancel.IsCancellationRequested) _cancel.Cancel();
        try { _waveIn?.StopRecording(); }
        catch { }
        try { _waveOut?.Stop(); }
        catch { }
        if (_readerTask is not null)
        {
            try { await Task.WhenAny(_readerTask, Task.Delay(500)); }
            catch { }
        }
        _started = false;
    }

    public void Dispose()
    {
        try { _waveIn?.Dispose(); }
        catch { }
        try { _waveOut?.Dispose(); }
        catch { }
        _cancel.Dispose();
    }
}
