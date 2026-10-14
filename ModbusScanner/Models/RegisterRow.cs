using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ModbusScanner.Models;

public sealed class RegisterRow : INotifyPropertyChanged
{
    private ushort _address;
    private ushort _rawValue;
    private string _description = "";
    private string _quality = "—";
    private string _displayValue = "0";

    public ushort Address { get => _address; set { _address = value; OnPropertyChanged(); } }
    public ushort RawValue { get => _rawValue; set { _rawValue = value; OnPropertyChanged(); OnPropertyChanged(nameof(HexValue)); OnPropertyChanged(nameof(BinaryValue)); } }
    public string HexValue => $"0x{RawValue:X4}";
    public string BinaryValue => Convert.ToString(RawValue, 2).PadLeft(16, '0');
    public string Description { get => _description; set { _description = value; OnPropertyChanged(); } }
    public string Quality { get => _quality; set { _quality = value; OnPropertyChanged(); } }
    public string DisplayValue { get => _displayValue; set { _displayValue = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}