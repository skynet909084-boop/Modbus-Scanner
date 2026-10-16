using System.IO;
namespace ModbusScanner;
public static class FrameChecks
{
    // Request and response contain Unit+PDU; RTU response also retains its checked CRC.
    public static void Validate(byte[] request,byte[] response,bool tcp)
    {
        int crc=tcp?0:2;
        if(request.Length<6 || response.Length<2 || response[0]!=request[0] || response[1]!=request[1])throw new IOException("Неверный Unit или функция.");
        byte fc=request[1];int quantity=(request[4]<<8)|request[5];
        if(fc is 1 or 2 or 3 or 4){
            int count=fc<=2?(quantity+7)/8:quantity*2;
            if(response.Length!=3+count+crc || response[2]!=count)throw new IOException("Длина данных не совпадает с запросом.");
        }else if(fc is 5 or 6 or 16){
            if(response.Length!=6+crc || !response.AsSpan(2,4).SequenceEqual(request.AsSpan(2,4)))throw new IOException("Подтверждение записи не совпало с запросом.");
        }else throw new IOException("Неподдерживаемая функция.");
    }
}
