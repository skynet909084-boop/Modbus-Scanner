using System.Net.Sockets;
using ModbusScanner;

await using var simulator = new ModbusTcpSimulator(15021);
simulator.Start();
using var tcp = new TcpClient();
await tcp.ConnectAsync("127.0.0.1", 15021);
var stream = tcp.GetStream();
ushort transaction = 0;

async Task<byte[]> Request(params byte[] pdu)
{
    ushort id = ++transaction;
    var frame = new byte[7 + pdu.Length];
    frame[0] = (byte)(id >> 8); frame[1] = (byte)id;
    ushort length = (ushort)(pdu.Length + 1);
    frame[4] = (byte)(length >> 8); frame[5] = (byte)length;
    frame[6] = 1;
    pdu.CopyTo(frame, 7);
    await stream.WriteAsync(frame);
    var header = new byte[7];
    await stream.ReadExactlyAsync(header);
    if (header[0] != frame[0] || header[1] != frame[1] || header[6] != 1)
        throw new Exception("Invalid MBAP response");
    int bytes = (header[4] << 8 | header[5]) - 1;
    if (bytes is < 2 or > 253) throw new Exception("Invalid response length");
    var response = new byte[bytes];
    await stream.ReadExactlyAsync(response);
    return response;
}

async Task Assert(byte[] expected, params byte[] request)
{
    var actual = await Request(request);
    if (!actual.SequenceEqual(expected))
        throw new Exception($"Request {Convert.ToHexString(request)}: expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}");
}

await Assert(new byte[] { 3, 2, 0, 0 }, 3, 0, 0, 0, 1);
await Assert(new byte[] { 6, 0, 5, 0x12, 0x34 }, 6, 0, 5, 0x12, 0x34);
await Assert(new byte[] { 3, 2, 0x12, 0x34 }, 3, 0, 5, 0, 1);
await Assert(new byte[] { 16, 0, 10, 0, 2 }, 16, 0, 10, 0, 2, 4, 0xAB, 0xCD, 0x12, 0x34);
await Assert(new byte[] { 3, 4, 0xAB, 0xCD, 0x12, 0x34 }, 3, 0, 10, 0, 2);
await Assert(new byte[] { 5, 0, 7, 0xFF, 0 }, 5, 0, 7, 0xFF, 0);
await Assert(new byte[] { 1, 1, 1 }, 1, 0, 7, 0, 1);
await Assert(new byte[] { 2, 1, 0 }, 2, 0, 7, 0, 1);
await Assert(new byte[] { 4, 2, 0, 0 }, 4, 0, 10, 0, 1);
await Assert(new byte[] { 0x83, 3 }, 3, 0, 0, 0, 0);
await Assert(new byte[] { 0x90, 2 }, 16, 0xFF, 0xFF, 0, 2, 4, 0, 1, 0, 2);
await Assert(new byte[] { 0xC1, 1 }, 0x41, 0, 0, 0, 1);
Console.WriteLine("PASS: Modbus TCP simulator smoke tests (FC01/02/03/04/05/06/16, readback, exceptions, MBAP)");
