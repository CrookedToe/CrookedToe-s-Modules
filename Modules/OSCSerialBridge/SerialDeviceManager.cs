using System.IO.Ports;
using System.Text;

namespace CrookedToe.Modules.OSCSerialBridge;

internal sealed class SerialDeviceManager : IDisposable
{
    private readonly Action<QueuedSerialLine> lineHandler;
    private readonly Action<string> infoLogger;
    private readonly Action<string, string> errorLogger;
    private readonly Dictionary<string, SerialConnection> connections = new(StringComparer.Ordinal);
    private readonly object syncRoot = new();
    private bool disposed;

    public SerialDeviceManager(Action<QueuedSerialLine> lineHandler, Action<string> infoLogger, Action<string, string> errorLogger)
    {
        this.lineHandler = lineHandler;
        this.infoLogger = infoLogger;
        this.errorLogger = errorLogger;
    }

    public void ApplyConfiguration(IEnumerable<SerialDeviceSnapshot> devices)
    {
        if (disposed)
            return;

        var snapshots = devices
            .Where(device => device.Enabled && !string.IsNullOrWhiteSpace(device.PortName))
            .ToDictionary(device => device.Name, StringComparer.OrdinalIgnoreCase);

        lock (syncRoot)
        {
            if (disposed)
                return;

            var staleConnections = connections.Keys
                .Where(name => !snapshots.ContainsKey(name))
                .ToList();

            foreach (var staleConnection in staleConnections)
            {
                connections[staleConnection].Dispose();
                connections.Remove(staleConnection);
            }

            foreach (var snapshot in snapshots.Values)
            {
                if (!connections.TryGetValue(snapshot.Name, out var connection))
                {
                    connections[snapshot.Name] = new SerialConnection(snapshot, lineHandler, infoLogger, errorLogger);
                    continue;
                }

                if (connection.Matches(snapshot)) continue;

                connection.Dispose();
                connections[snapshot.Name] = new SerialConnection(snapshot, lineHandler, infoLogger, errorLogger);
            }
        }
    }

    public void Dispose()
    {
        List<SerialConnection> existingConnections;

        lock (syncRoot)
        {
            if (disposed)
                return;

            disposed = true;
            existingConnections = connections.Values.ToList();
            connections.Clear();
        }

        foreach (var connection in existingConnections)
            connection.Dispose();
    }

    public bool TryWriteToDevice(string deviceName, string line)
    {
        lock (syncRoot)
        {
            if (disposed || !connections.TryGetValue(deviceName, out var connection))
                return false;

            return connection.TryWrite(line);
        }
    }

    private sealed class SerialConnection : IDisposable
    {
        private readonly SerialDeviceSnapshot snapshot;
        private readonly Action<QueuedSerialLine> lineHandler;
        private readonly Action<string> infoLogger;
        private readonly Action<string, string> errorLogger;
        private readonly object bufferLock = new();
        private readonly object serialLock = new();
        private readonly StringBuilder buffer = new();

        private SerialPort? serialPort;
        private bool disposed;

        public SerialConnection(
            SerialDeviceSnapshot snapshot,
            Action<QueuedSerialLine> lineHandler,
            Action<string> infoLogger,
            Action<string, string> errorLogger)
        {
            this.snapshot = snapshot;
            this.lineHandler = lineHandler;
            this.infoLogger = infoLogger;
            this.errorLogger = errorLogger;

            OpenPort();
        }

        public bool Matches(SerialDeviceSnapshot other)
        {
            return snapshot.Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase) &&
                   snapshot.PortName.Equals(other.PortName, StringComparison.OrdinalIgnoreCase) &&
                   snapshot.BaudRate == other.BaudRate &&
                   snapshot.Enabled == other.Enabled;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            lock (serialLock)
            {
                if (serialPort == null) return;

                try
                {
                    serialPort.DataReceived -= OnSerialDataReceived;
                    if (serialPort.IsOpen)
                        serialPort.Close();
                }
                catch
                {
                }

                try { serialPort.Dispose(); } catch { }
                serialPort = null;
            }
        }

        public bool TryWrite(string line)
        {
            try
            {
                lock (serialLock)
                {
                    if (disposed || serialPort == null || !serialPort.IsOpen) return false;

                    serialPort.Write($"{line}\r\n");
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (!disposed)
                {
                    errorLogger(
                        $"write:{snapshot.PortName}:{ex.Message}",
                        $"Serial write error for '{snapshot.Name}' on {snapshot.PortName}: {ex.Message}");
                }

                return false;
            }
        }

        private void OpenPort()
        {
            try
            {
                serialPort = new SerialPort(snapshot.PortName, snapshot.BaudRate)
                {
                    DtrEnable = true,
                    RtsEnable = true,
                    Encoding = Encoding.UTF8
                };

                serialPort.DataReceived += OnSerialDataReceived;
                serialPort.Open();
                infoLogger($"Connected serial device '{snapshot.Name}' on {snapshot.PortName} @ {snapshot.BaudRate} baud");
            }
            catch (Exception ex)
            {
                errorLogger(
                    $"connect:{snapshot.PortName}:{snapshot.BaudRate}",
                    $"Failed to connect serial device '{snapshot.Name}' on {snapshot.PortName}: {ex.Message}");
                Dispose();
            }
        }

        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (disposed) return;

            try
            {
                string data;
                lock (serialLock)
                {
                    if (disposed || serialPort == null || !serialPort.IsOpen) return;
                    data = serialPort.ReadExisting();
                }

                if (string.IsNullOrEmpty(data)) return;

                List<string> lines;
                lock (bufferLock)
                {
                    buffer.Append(data);
                    lines = ExtractCompletedLines(buffer);
                }

                foreach (var line in lines)
                    lineHandler(new QueuedSerialLine(snapshot.Name, line));
            }
            catch (Exception ex)
            {
                if (!disposed)
                {
                    errorLogger(
                        $"read:{snapshot.PortName}:{ex.Message}",
                        $"Serial read error for '{snapshot.Name}' on {snapshot.PortName}: {ex.Message}");
                }
            }
        }

        private static List<string> ExtractCompletedLines(StringBuilder buffer)
        {
            var contents = buffer.ToString();
            var pieces = contents.Split(['\r', '\n'], StringSplitOptions.None);
            bool endsWithLineBreak = contents.EndsWith('\r') || contents.EndsWith('\n');
            int completedCount = endsWithLineBreak ? pieces.Length : Math.Max(0, pieces.Length - 1);

            var lines = new List<string>(completedCount);
            for (int index = 0; index < completedCount; index++)
            {
                var line = pieces[index].Trim();
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }

            buffer.Clear();
            if (!endsWithLineBreak && pieces.Length > 0)
                buffer.Append(pieces[^1]);

            return lines;
        }
    }
}
