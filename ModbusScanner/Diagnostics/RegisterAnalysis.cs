using System.Globalization;

namespace ModbusScanner;

public static class RegisterAnalysis
{
    public static uint Order32(ushort first, ushort second, string order)
    {
        byte a=(byte)(first>>8), b=(byte)first, c=(byte)(second>>8), d=(byte)second;
        byte[] bytes = order switch {
            "ABCD" => new[]{a,b,c,d}, "BADC" => new[]{b,a,d,c},
            "CDAB" => new[]{c,d,a,b}, "DCBA" => new[]{d,c,b,a},
            _ => throw new ArgumentException("Неизвестный порядок байтов", nameof(order))
        };
        return ((uint)bytes[0]<<24)|((uint)bytes[1]<<16)|((uint)bytes[2]<<8)|bytes[3];
    }
    public static string Float(ushort first, ushort second, string order) =>
        BitConverter.Int32BitsToSingle(unchecked((int)Order32(first,second,order))).ToString("G9",CultureInfo.InvariantCulture);
    public static string ChangedBits(ushort before, ushort after) => string.Join(", ",
        Enumerable.Range(0,16).Where(i=>((before^after)&(1<<i))!=0).Select(i=>$"b{i}: {(before>>i)&1}→{(after>>i)&1}"));
    public static string Csv(string? value) => "\""+(value??"").Replace("\"","\"\"")+"\"";
}
public sealed record RegisterDefinition(ushort Address, byte Function, string Name, string Unit, double Scale, double Offset);
public sealed class ModbusDeviceException : System.IO.IOException
{
    public byte Code { get; }
    public ModbusDeviceException(byte code) : base($"Modbus exception 0x{code:X2}: {Describe(code)}") => Code=code;
    private static string Describe(byte code) => code switch {
        1=>"функция не поддерживается",2=>"недопустимый адрес",3=>"недопустимое значение",4=>"ошибка устройства",
        5=>"запрос принят",6=>"устройство занято",10=>"путь шлюза недоступен",11=>"нет ответа за шлюзом",_=>"неизвестный код"
    };
}
