using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ModbusScanner;

/// <summary>Local test-only Modbus TCP server. No external interface binding.</summary>
public sealed class ModbusTcpSimulator : IAsyncDisposable
{
    private readonly ushort[] _holding = new ushort[65536];
    private readonly bool[] _coils = new bool[65536];
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _clients = new();
    private Task? _acceptTask;
    public int Port { get; }

    public ModbusTcpSimulator(int port = 1502)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        _acceptTask = AcceptAsync(_stop.Token);
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                lock (_clients) _clients.Add(ServeAsync(client, ct));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.ReceiveTimeout = 5000;
            using var stream = client.GetStream();
            var header = new byte[7];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExact(stream, header, ct)) return;
                    int length = (header[4] << 8) | header[5];
                    if (header[2] != 0 || header[3] != 0 || length is < 2 or > 254) return;
                    var pdu = new byte[length - 1];
                    if (!await ReadExact(stream, pdu, ct)) return;
                    byte[] response = Process(pdu);
                    var frame = new byte[7 + response.Length];
                    Array.Copy(header, 0, frame, 0, 4);
                    int size = response.Length + 1;
                    frame[4] = (byte)(size >> 8); frame[5] = (byte)size; frame[6] = header[6];
                    Array.Copy(response, 0, frame, 7, response.Length);
                    await stream.WriteAsync(frame, ct);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<bool> ReadExact(NetworkStream stream, byte[] data, CancellationToken ct)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int read = await stream.ReadAsync(data.AsMemory(offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private byte[] Process(byte[] pdu)
    {
        if (pdu.Length < 1) return new byte[] { 0x80, 3 };
        byte fc = pdu[0];
        if (fc is not (1 or 2 or 3 or 4 or 5 or 6 or 16)) return new byte[] { (byte)(fc | 0x80), 1 };
        if (pdu.Length < 5) return Error(fc, 3);
        int address = (pdu[1] << 8) | pdu[2];
        int count = (pdu[3] << 8) | pdu[4];
        lock (_holding)
        {
            if (fc is 1 or 2 or 3 or 4)
            {
                if (pdu.Length != 5 || count < 1 || count > (fc is 1 or 2 ? 2000 : 125)) return Error(fc, 3);
                if (address + count > 65536) return Error(fc, 2);
                if (fc is 1 or 2)
                {
                    var response = new byte[2 + (count + 7) / 8];
                    response[0] = fc; response[1] = (byte)(response.Length - 2);
                    for (int i = 0; i < count; i++)
                        if (fc == 1 && _coils[address + i]) response[2 + i / 8] |= (byte)(1 << (i % 8));
                    return response;
                }
                var words = new byte[2 + count * 2];
                words[0] = fc; words[1] = (byte)(count * 2);
                for (int i = 0; i < count; i++)
                {
                    ushort value = fc == 3 ? _holding[address + i] : (ushort)0;
                    words[2 + i * 2] = (byte)(value >> 8);
                    words[3 + i * 2] = (byte)value;
                }
                return words;
            }
            if (fc is 5 or 6)
            {
                if (pdu.Length != 5) return Error(fc, 3);
                if (fc == 5)
                {
                    if (count is not (0 or 0xFF00)) return Error(fc, 3);
                    _coils[address] = count == 0xFF00;
                }
                else _holding[address] = (ushort)count;
                return pdu;
            }
            if (pdu.Length < 6) return Error(fc, 3);
            int bytes = pdu[5];
            if (count is < 1 or > 123 || bytes != count * 2 || pdu.Length != 6 + bytes) return Error(fc, 3);
            if (address + count > 65536) return Error(fc, 2);
            for (int i = 0; i < count; i++) _holding[address + i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
            return new byte[] { fc, pdu[1], pdu[2], pdu[3], pdu[4] };
        }
    }

    private static byte[] Error(byte fc, byte code) => new[] { (byte)(fc | 0x80), code };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_acceptTask != null) await _acceptTask;
        Task[] tasks;
        lock (_clients) tasks = _clients.ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        _stop.Dispose();
    }
}
