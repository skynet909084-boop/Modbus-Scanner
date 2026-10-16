using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ModbusScanner.Models;

public sealed class RegisterRow : INotifyPropertyChanged
{
    private ushort _address;
    private double _scale=1, _offset;
    public ushort PendingValue { get; set; }
    public byte Function { get; set; } = 3;
    public string Unit { get; set; } = "";
    public double Scale { get=>_scale; set { if(!double.IsFinite(value))return; _scale=value; OnPropertyChanged(); OnPropertyChanged(nameof(ScaledValue)); } }
    public double Offset { get=>_offset; set { if(!double.IsFinite(value))return; _offset=value; OnPropertyChanged(); OnPropertyChanged(nameof(ScaledValue)); } }
    public double ScaledValue => RawValue*Scale+Offset;
    public short SignedValue => unchecked((short)RawValue);
    private ushort _rawValue;
    private string _description = "";
    private string _quality = "—";
    private string _displayValue = "0";

    public ushort Address { get => _address; set { _address = value; OnPropertyChanged(); } }
    public ushort RawValue { get => _rawValue; set { _rawValue = value; OnPropertyChanged(); OnPropertyChanged(nameof(SignedValue)); OnPropertyChanged(nameof(ScaledValue)); OnPropertyChanged(nameof(HexValue)); OnPropertyChanged(nameof(BinaryValue)); } }
    public string HexValue => $"0x{RawValue:X4}";
    public string BinaryValue => Convert.ToString(RawValue, 2).PadLeft(16, '0');
    public string Description { get => _description; set { _description = value; OnPropertyChanged(); } }
    public string Quality { get => _quality; set { _quality = value; OnPropertyChanged(); } }
    public string DisplayValue { get => _displayValue; set { _displayValue = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
