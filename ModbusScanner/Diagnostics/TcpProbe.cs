using System.IO;
using System.Net.Sockets;
namespace ModbusScanner;

public static class TcpProbe
{
    // A valid exception proves Modbus presence even if this register is not implemented.
    public static bool Validate(byte[] header,byte[] tail,byte unit,byte function,ushort transaction)
    {
        if(header.Length!=7 || header[0]!=(byte)(transaction>>8) || header[1]!=(byte)transaction || header[2]!=0 || header[3]!=0 || header[6]!=unit)return false;
        if(((header[4]<<8)|header[5])!=tail.Length+1)return false;
        if(tail.Length==2 && tail[0]==(function|0x80))return tail[1]!=0;
        return tail.Length==4 && tail[0]==function && tail[1]==2;
    }
    public static async Task<string?> RunAsync(string host,int port,byte unit,byte function,ushort address,int timeout,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(timeout);
        using var client=new TcpClient{NoDelay=true};
        await client.ConnectAsync(host,port,deadline.Token);
        var stream=client.GetStream();
        byte[] frame={0x53,0x11,0,0,0,6,unit,function,(byte)(address>>8),(byte)address,0,1};
        await stream.WriteAsync(frame,deadline.Token);
        byte[] header=new byte[7];await stream.ReadExactlyAsync(header,deadline.Token);
        int length=(header[4]<<8)|header[5];if(length<2 || length>254)return null;
        byte[] tail=new byte[length-1];await stream.ReadExactlyAsync(tail,deadline.Token);
        if(!Validate(header,tail,unit,function,0x5311))return null;
        return tail.Length==2?$"Modbus подтверждён, exception 0x{tail[1]:X2}":"Modbus подтверждён чтением";
    }
}
