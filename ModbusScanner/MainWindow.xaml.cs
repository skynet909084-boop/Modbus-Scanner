using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ModbusScanner.Models;

namespace ModbusScanner;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<RegisterRow> _rows = new();
    private readonly object _serialGate = new();
    private SerialPort? _serial;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;
    private CancellationTokenSource? _scanCts;
    private bool _connected;
    private bool IsTcp => ProtocolBox.SelectedIndex == 1;
    private int ValueMode => ValueTypeBox.SelectedIndex;

    public MainWindow()
    {
        InitializeComponent();
        RegistersGrid.ItemsSource = _rows;
        RefreshComPorts();
        ProtocolBox_SelectionChanged(this, null!);
        Log("Modbus Scanner запущен. Запись по умолчанию требует подтверждения.");
    }


    private sealed record ScannerProfile(
        int ProtocolIndex, string Host, string Port, string? ComPort, int BaudIndex,
        int ParityIndex, int DataBitsIndex, int StopBitsIndex, string UnitId,
        int FunctionIndex, string StartAddress, string Quantity, int ValueTypeIndex, string MonitorInterval);

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Сохранить профиль Modbus", Filter = "Профиль Modbus (*.json)|*.json", DefaultExt = ".json", FileName = "modbus-profile.json" };
            if (dialog.ShowDialog() != true) return;
            var profile = new ScannerProfile(ProtocolBox.SelectedIndex, HostBox.Text, PortBox.Text, ComBox.SelectedItem?.ToString(),
                BaudBox.SelectedIndex, ParityBox.SelectedIndex, DataBitsBox.SelectedIndex, StopBitsBox.SelectedIndex,
                UnitBox.Text, FunctionBox.SelectedIndex, StartAddressBox.Text, QuantityBox.Text, ValueTypeBox.SelectedIndex, MonitorIntervalBox.Text);
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            Log("Профиль сохранён: " + dialog.FileName);
        }
        catch (Exception ex) { Log("Не удалось сохранить профиль: " + ex.Message); }
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Загрузить профиль Modbus", Filter = "Профиль Modbus (*.json)|*.json|Все файлы (*.*)|*.*", DefaultExt = ".json" };
            if (dialog.ShowDialog() != true) return;
            var profile = JsonSerializer.Deserialize<ScannerProfile>(File.ReadAllText(dialog.FileName))
                ?? throw new InvalidDataException("Файл профиля пуст или имеет неверный формат.");
            if (profile.ProtocolIndex is < 0 or > 1 || profile.FunctionIndex is < 0 or > 3 || profile.ValueTypeIndex is < 0 or > 7 ||
                profile.BaudIndex < 0 || profile.BaudIndex >= BaudBox.Items.Count || profile.ParityIndex < 0 || profile.ParityIndex >= ParityBox.Items.Count ||
                profile.DataBitsIndex < 0 || profile.DataBitsIndex >= DataBitsBox.Items.Count || profile.StopBitsIndex < 0 || profile.StopBitsIndex >= StopBitsBox.Items.Count)
                throw new InvalidDataException("В профиле содержатся недопустимые индексы настроек.");
            Disconnect();
            ProtocolBox.SelectedIndex = profile.ProtocolIndex;
            HostBox.Text = profile.Host ?? "";
            PortBox.Text = profile.Port ?? "502";
            if (!string.IsNullOrWhiteSpace(profile.ComPort) && ComBox.Items.Contains(profile.ComPort)) ComBox.SelectedItem = profile.ComPort;
            BaudBox.SelectedIndex = profile.BaudIndex; ParityBox.SelectedIndex = profile.ParityIndex;
            DataBitsBox.SelectedIndex = profile.DataBitsIndex; StopBitsBox.SelectedIndex = profile.StopBitsIndex;
            UnitBox.Text = profile.UnitId ?? "1"; FunctionBox.SelectedIndex = profile.FunctionIndex;
            StartAddressBox.Text = profile.StartAddress ?? "0"; QuantityBox.Text = profile.Quantity ?? "100";
            ValueTypeBox.SelectedIndex = profile.ValueTypeIndex; MonitorIntervalBox.Text = profile.MonitorInterval ?? "1000";
            Log("Профиль загружен: " + dialog.FileName);
            StatusText.Text = "Профиль загружен · подключение не проверено";
        }
        catch (Exception ex) { Log("Не удалось загрузить профиль: " + ex.Message); }
    }

    private void ValueTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInterpretedValues();

    private void RegistersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is not RegisterRow row || WriteAddressBox is null || WriteValueBox is null) return;
        WriteAddressBox.Text = row.Address.ToString(CultureInfo.InvariantCulture);
        WriteValueBox.Text = row.RawValue.ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateInterpretedValues()
    {
        if (ValueTypeBox is null || RegistersGrid is null) return;
        var byAddress = _rows.ToDictionary(r => (int)r.Address, r => r.RawValue);
        foreach (var row in _rows)
        {
            uint bits = row.RawValue;
            bool needsPair = ValueMode >= 2;
            if (needsPair)
            {
                if (!byAddress.TryGetValue(row.Address + 1, out ushort next)) { row.DisplayValue = "— (нужен следующий регистр)"; continue; }
                bits = ValueMode is 5 or 6 or 7 ? ((uint)next << 16) | row.RawValue : ((uint)row.RawValue << 16) | next;
            }
            row.DisplayValue = ValueMode switch
            {
                1 => unchecked((short)row.RawValue).ToString(CultureInfo.InvariantCulture),
                2 or 5 => bits.ToString(CultureInfo.InvariantCulture),
                3 or 6 => unchecked((int)bits).ToString(CultureInfo.InvariantCulture),
                4 or 7 => BitConverter.Int32BitsToSingle(unchecked((int)bits)).ToString("G9", CultureInfo.InvariantCulture),
                _ => row.RawValue.ToString(CultureInfo.InvariantCulture)
            };
        }
    }

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComBox is null) return;
        bool tcp = IsTcp;
        ComBox.IsEnabled = !tcp;
        BaudBox.IsEnabled = !tcp;
        ParityBox.IsEnabled = !tcp;
        DataBitsBox.IsEnabled = !tcp;
        StopBitsBox.IsEnabled = !tcp;
        HostBox.IsEnabled = tcp;
        PortBox.IsEnabled = tcp;
        StatusText.Text = tcp ? "Режим Modbus TCP" : "Режим Modbus RTU";
    }

    private void RefreshCom_Click(object sender, RoutedEventArgs e) => RefreshComPorts();

    private void RefreshComPorts()
    {
        var old = ComBox.SelectedItem?.ToString();
        ComBox.ItemsSource = SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (old is not null && ComBox.Items.Contains(old)) ComBox.SelectedItem = old;
        else if (ComBox.Items.Count > 0) ComBox.SelectedIndex = 0;
        Log("COM-порты: " + (ComBox.Items.Count == 0 ? "не найдены" : string.Join(", ", ComBox.Items.Cast<string>())));
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_connected) { Disconnect(); return; }
            await EnsureConnectedAsync();
            var unit = GetUnit();
            var fc = (byte)(FunctionBox.SelectedIndex + 1);
            var values = await ReadAsync(unit, fc, GetStart(), 1, CancellationToken.None);
            _connected = true;
            StatusText.Text = $"Связь подтверждена · Unit {unit}";
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            Log($"Связь OK: Unit={unit}, FC={fc}, ответ получен.");
            if (values.Length > 0) WriteValueBox.Text = values[0].ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Нет подтверждённой связи";
            StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            Log("Подключение/проверка: " + ex.Message);
            Disconnect();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (IsTcp)
        {
            if (_tcp?.Connected == true && _stream is not null) return;
            Disconnect();
            int port = ParseInt(PortBox.Text, 1, 65535, "TCP-порт");
            _tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 2000, SendTimeout = 2000 };
            await _tcp.ConnectAsync(HostBox.Text.Trim(), port, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            _stream = _tcp.GetStream();
            _stream.ReadTimeout = 2000;
            _stream.WriteTimeout = 2000;
            Log($"TCP подключён: {HostBox.Text.Trim()}:{port}");
        }
        else
        {
            if (_serial?.IsOpen == true) return;
            Disconnect();
            var com = ComBox.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(com)) throw new InvalidOperationException("Выберите COM-порт.");
            int baud = int.Parse(((ComboBoxItem)BaudBox.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture);
            int dataBits = int.Parse(((ComboBoxItem)DataBitsBox.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture);
            var parity = ParityBox.SelectedIndex switch { 1 => Parity.Even, 2 => Parity.Odd, _ => Parity.None };
            var stop = StopBitsBox.SelectedIndex == 1 ? StopBits.Two : StopBits.One;
            _serial = new SerialPort(com, baud, parity, dataBits, stop)
            {
                ReadTimeout = 650,
                WriteTimeout = 650,
                DtrEnable = false,
                RtsEnable = false
            };
            _serial.Open();
            Log($"RTU порт открыт: {com}, {baud}, {parity}, {dataBits}{(stop == StopBits.One ? "N1" : "N2")}");
        }
    }

    private void Disconnect()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Close(); } catch { }
        try { if (_serial?.IsOpen == true) _serial.Close(); } catch { }
        _stream = null; _tcp = null; _serial = null; _connected = false;
    }


    private async void StartMonitor_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        int interval;
        try { interval = ParseInt(MonitorIntervalBox.Text, 200, 60000, "Период мониторинга"); }
        catch (Exception ex) { Log(ex.Message); return; }
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var ct = cts.Token;
        Log($"Мониторинг запущен: FC{FunctionBox.SelectedIndex + 1}, адрес {GetStart()}, количество {GetQuantity()}, период {interval} мс.");
        StatusText.Text = "Мониторинг активен";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await EnsureConnectedAsync(ct);
                    var unit = GetUnit();
                    var function = (byte)(FunctionBox.SelectedIndex + 1);
                    var start = GetStart();
                    var values = await ReadAsync(unit, function, start, GetQuantity(), ct);
                    ApplyReadValues(start, values, function);
                    _connected = true;
                    StatusText.Text = $"Мониторинг · OK · {DateTime.Now:HH:mm:ss}";
                    StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    StatusText.Text = "Мониторинг · ошибка связи";
                    StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    Log($"Мониторинг: {ex.Message}; повторное подключение…");
                    Disconnect();
                    await Task.Delay(Math.Min(interval, 1500), ct);
                }
                int delay = interval - (int)started.ElapsedMilliseconds;
                if (delay > 0) await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            bool wasCurrentMonitor = ReferenceEquals(_scanCts, cts);
            if (wasCurrentMonitor)
            {
                _scanCts = null;
                StatusText.Text = "Мониторинг остановлен";
                StatusText.Foreground = System.Windows.Media.Brushes.LightGray;
                Log("Мониторинг остановлен.");
            }
            cts.Dispose();
        }
    }

    private void ApplyReadValues(ushort start, ushort[] values, byte function)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ushort address = (ushort)(start + i);
            var row = _rows.FirstOrDefault(r => r.Address == address);
            if (row is null) { row = new RegisterRow { Address = address }; _rows.Add(row); }
            row.RawValue = values[i];
            row.Quality = "OK";
            if (function is 1 or 2) row.Description = values[i] == 0 ? "OFF" : "ON";
        }
        SortRows();
        UpdateInterpretedValues();
    }

    private async void ReadRange_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureConnectedAsync();
            var values = await ReadAsync(GetUnit(), (byte)(FunctionBox.SelectedIndex + 1), GetStart(), GetQuantity(), CancellationToken.None);
            ApplyReadValues(GetStart(), values, (byte)(FunctionBox.SelectedIndex + 1));
            Log($"Прочитано {values.Length} значений с адреса {GetStart()} (FC{FunctionBox.SelectedIndex + 1}).");
            StatusText.Text = $"Прочитано: {values.Length}";
        }
        catch (Exception ex) { Log("Ошибка чтения: " + ex.Message); StatusText.Text = "Ошибка чтения"; }
    }

    private async Task<ushort[]> ReadAsync(byte unit, byte function, ushort start, ushort quantity, CancellationToken ct)
    {
        if (quantity is < 1 or > 125 && function is 3 or 4) throw new ArgumentOutOfRangeException(nameof(quantity), "FC03/04: допустимо 1–125 регистров.");
        if (quantity is < 1 or > 2000 && function is 1 or 2) throw new ArgumentOutOfRangeException(nameof(quantity), "FC01/02: допустимо 1–2000 бит.");
        byte[] request = { unit, function, (byte)(start >> 8), (byte)start, (byte)(quantity >> 8), (byte)quantity };
        var response = await ExchangeAsync(request, function, ct);
        if (response.Length < 3 || response[0] != unit) throw new IOException("Ответ Modbus имеет неверный адрес или длину.");
        if ((response[1] & 0x80) != 0) throw new IOException($"Modbus exception code {response.ElementAtOrDefault(2):X2} для FC{function}.");
        if (response[1] != function) throw new IOException("Функция в ответе не совпадает с запросом.");
        int byteCount = response[2];
        if (response.Length < 3 + byteCount) throw new IOException("Ответ обрезан: не хватает данных.");
        if (function is 1 or 2)
        {
            var bits = new ushort[quantity];
            for (int i = 0; i < quantity; i++) bits[i] = (ushort)((response[3 + i / 8] >> (i % 8)) & 1);
            return bits;
        }
        if (byteCount != quantity * 2) throw new IOException($"Неожиданная длина данных: {byteCount} байт.");
        var result = new ushort[quantity];
        for (int i = 0; i < quantity; i++) result[i] = (ushort)((response[3 + i * 2] << 8) | response[4 + i * 2]);
        return result;
    }

    private async Task<byte[]> ExchangeAsync(byte[] pdu, byte function, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        if (IsTcp)
        {
            var stream = _stream ?? throw new IOException("TCP-поток не открыт.");
            ushort tid = unchecked(++_transactionId);
            // MBAP is 6 bytes, followed by Unit ID and the Modbus PDU.
            byte[] frame = new byte[6 + pdu.Length];
            frame[0] = (byte)(tid >> 8); frame[1] = (byte)tid;
            frame[2] = 0; frame[3] = 0;
            ushort len = (ushort)pdu.Length;
            frame[4] = (byte)(len >> 8); frame[5] = (byte)len; frame[6] = pdu[0];
            Array.Copy(pdu, 1, frame, 7, pdu.Length - 1);
            await stream.WriteAsync(frame, ct);
            byte[] header = new byte[7];
            await ReadExactAsync(stream, header, ct);
            int length = (header[4] << 8) | header[5];
            if (header[0] != frame[0] || header[1] != frame[1] || header[2] != 0 || header[3] != 0 || length < 2 || length > 254)
                throw new IOException("Некорректный MBAP заголовок.");
            byte[] response = new byte[length];
            response[0] = header[6];
            // Read function and data after the Unit ID.
            byte[] tail = new byte[length - 1];
            await ReadExactAsync(stream, tail, ct);
            Array.Copy(tail, 0, response, 1, tail.Length);
            return response;
        }

        return await Task.Run(() =>
        {
            lock (_serialGate)
            {
                var serial = _serial ?? throw new IOException("COM-порт не открыт.");
                serial.DiscardInBuffer();
                byte[] frame = new byte[pdu.Length + 2];
                Array.Copy(pdu, frame, pdu.Length);
                ushort crc = Crc16(frame, pdu.Length);
                frame[^2] = (byte)crc; frame[^1] = (byte)(crc >> 8);
                serial.Write(frame, 0, frame.Length);
                var first = ReadSerialByte(serial);
                if (first != pdu[0]) throw new IOException("Ответ от другого Slave ID.");
                var second = ReadSerialByte(serial);
                if ((second & 0x80) != 0)
                {
                    var exception = ReadSerialByte(serial);
                    int crcLow = ReadSerialByte(serial);
                    int crcHigh = ReadSerialByte(serial);
                    byte[] exceptionFrame = { (byte)first, (byte)second, (byte)exception };
                    ushort expectedExceptionCrc = Crc16(exceptionFrame, exceptionFrame.Length);
                    ushort receivedExceptionCrc = (ushort)(crcLow | (crcHigh << 8));
                    if (expectedExceptionCrc != receivedExceptionCrc)
                        throw new IOException("CRC16 исключения RTU не совпадает.");
                    throw new IOException($"Modbus exception {exception:X2}.");
                }
                if (second != function) throw new IOException("Функция ответа не совпадает.");
                byte[] answer;
                if (function is 5 or 6 or 16)
                {
                    // FC05/06/16 replies echo the address/value or address/quantity; there is no byte-count field.
                    answer = new byte[8];
                    answer[0] = (byte)first; answer[1] = (byte)second;
                    for (int i = 2; i < answer.Length; i++) answer[i] = (byte)ReadSerialByte(serial);
                }
                else
                {
                    var byteCount = ReadSerialByte(serial);
                    byte[] body = new byte[byteCount + 2];
                    for (int i = 0; i < body.Length; i++) body[i] = (byte)ReadSerialByte(serial);
                    answer = new byte[3 + body.Length];
                    answer[0] = (byte)first; answer[1] = (byte)second; answer[2] = (byte)byteCount;
                    Array.Copy(body, 0, answer, 3, body.Length);
                }
                ushort expected = Crc16(answer, answer.Length - 2);
                ushort received = (ushort)(answer[^2] | (answer[^1] << 8));
                if (expected != received) throw new IOException("CRC16 ответа не совпадает.");
                return answer;
            }
        }, ct);
    }

    private static int ReadSerialByte(SerialPort serial)
    {
        int value = serial.ReadByte();
        if (value < 0) throw new EndOfStreamException("Ожидался ответ RTU.");
        return value;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (n == 0) throw new EndOfStreamException("TCP-соединение закрыто во время ответа.");
            offset += n;
        }
    }

    private static ushort Crc16(byte[] data, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++) crc = (ushort)(((crc & 1) != 0) ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return crc;
    }

    private async void WriteRegister_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ushort address = ParseUShort(WriteAddressBox.Text, "Адрес записи");
            ushort value = ParseUShort(WriteValueBox.Text, "Значение");
            if (ConfirmWriteBox.IsChecked == true && MessageBox.Show($"Записать {value} (0x{value:X4}) в Holding Register {address} устройства Unit {GetUnit()}?", "Подтверждение записи", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await WriteAsync(GetUnit(), 6, address, new[] { value }, CancellationToken.None);
            Log($"FC06: записан регистр {address} = {value} (0x{value:X4}).");
            await ReadBackAsync(address, 1);
        }
        catch (Exception ex) { Log("Ошибка записи FC06: " + ex.Message); }
    }

    private async void WriteMultiple_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RegistersGrid.SelectedItem is not RegisterRow row) throw new InvalidOperationException("Выберите строку в таблице.");
            RegistersGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            RegistersGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var selected = _rows.Where(r => r.Address >= row.Address).OrderBy(r => r.Address).ToList();
            var contiguous = new List<RegisterRow> { selected[0] };
            for (int i = 1; i < selected.Count && contiguous.Count < 123; i++)
            {
                if (selected[i].Address != contiguous[^1].Address + 1) break;
                contiguous.Add(selected[i]);
            }
            var values = contiguous.Select(r => r.RawValue).ToArray();
            if (ConfirmWriteBox.IsChecked == true && MessageBox.Show($"Записать {values.Length} регистров начиная с {row.Address}?", "Подтверждение записи", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await WriteAsync(GetUnit(), 16, row.Address, values, CancellationToken.None);
            Log($"FC16: записано {values.Length} регистров с адреса {row.Address}.");
            await ReadBackAsync(row.Address, (ushort)values.Length);
        }
        catch (Exception ex) { Log("Ошибка записи FC16: " + ex.Message); }
    }

    private async void WriteCoil_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ushort address = ParseUShort(WriteAddressBox.Text, "Адрес Coil");
            bool on = ParseUShort(WriteValueBox.Text, "Значение Coil") != 0;
            if (ConfirmWriteBox.IsChecked == true && MessageBox.Show($"Установить Coil {address} в {(on ? "ON" : "OFF")}?", "Подтверждение записи", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await WriteAsync(GetUnit(), 5, address, new[] { (ushort)(on ? 1 : 0) }, CancellationToken.None);
            Log($"FC05: Coil {address} = {(on ? "ON" : "OFF")}.");
            await ReadBackAsync(address, 1, 1);
        }
        catch (Exception ex) { Log("Ошибка записи FC05: " + ex.Message); }
    }

    private async Task WriteAsync(byte unit, byte function, ushort address, ushort[] values, CancellationToken ct)
    {
        byte[] request;
        if (function == 6 || function == 5)
        {
            ushort value = function == 5 ? (values[0] != 0 ? (ushort)0xFF00 : (ushort)0) : values[0];
            request = new byte[] { unit, function, (byte)(address >> 8), (byte)address, (byte)(value >> 8), (byte)value };
        }
        else
        {
            if (values.Length is < 1 or > 123) throw new ArgumentOutOfRangeException(nameof(values), "FC16 поддерживает 1–123 регистров.");
            request = new byte[7 + values.Length * 2];
            request[0] = unit; request[1] = 16; request[2] = (byte)(address >> 8); request[3] = (byte)address;
            request[4] = (byte)(values.Length >> 8); request[5] = (byte)values.Length; request[6] = (byte)(values.Length * 2);
            for (int i = 0; i < values.Length; i++) { request[7 + i * 2] = (byte)(values[i] >> 8); request[8 + i * 2] = (byte)values[i]; }
        }
        var response = await ExchangeAsync(request, function, ct);
        if (response.Length < 6 || response[0] != unit) throw new IOException("Некорректное подтверждение записи.");
        if ((response[1] & 0x80) != 0) throw new IOException($"Устройство отклонило запись: exception {response.ElementAtOrDefault(2):X2}.");
        if (response[1] != function) throw new IOException("Неверная функция в подтверждении записи.");
        int expectedResponseLength = IsTcp ? 6 : 8; // RTU includes two CRC bytes.
        if (response.Length != expectedResponseLength) throw new IOException("Неверная длина подтверждения записи.");
        ushort echoedAddress = (ushort)((response[2] << 8) | response[3]);
        ushort echoedValueOrCount = (ushort)((response[4] << 8) | response[5]);
        ushort expectedEcho = function == 16 ? (ushort)values.Length : function == 5 ? (values[0] != 0 ? (ushort)0xFF00 : (ushort)0) : values[0];
        if (echoedAddress != address || echoedValueOrCount != expectedEcho)
            throw new IOException($"Подтверждение записи не совпало: адрес={echoedAddress}, значение/количество={echoedValueOrCount}; ожидалось адрес={address}, значение/количество={expectedEcho}.");
    }

    private async Task ReadBackAsync(ushort address, ushort quantity, byte? function = null)
    {
        byte fc = function ?? 3;
        var values = await ReadAsync(GetUnit(), fc, address, quantity, CancellationToken.None);
        for (int i = 0; i < values.Length; i++)
        {
            var row = _rows.FirstOrDefault(r => r.Address == address + i);
            if (row is null) { row = new RegisterRow { Address = (ushort)(address + i) }; _rows.Add(row); }
            row.RawValue = values[i]; row.Quality = "OK";
        }
        SortRows();
        UpdateInterpretedValues();
    }

    private async void RefreshSelected_Click(object sender, RoutedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is not RegisterRow row) return;
        try { await ReadBackAsync(row.Address, 1, (byte)(FunctionBox.SelectedIndex + 1)); Log($"Повторное чтение адреса {row.Address} выполнено."); }
        catch (Exception ex) { row.Quality = "Ошибка"; Log("Повторное чтение: " + ex.Message); }
    }

    private async void ScanRtu_Click(object sender, RoutedEventArgs e)
    {
        if (IsTcp) { Log("Автопоиск скорости предназначен для RTU. Для TCP используйте поиск регистров и адресов."); return; }
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        try
        {
            if (string.IsNullOrWhiteSpace(ComBox.SelectedItem?.ToString())) throw new InvalidOperationException("Выберите COM-порт.");
            int preferredBaud = int.Parse(((ComboBoxItem)BaudBox.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture);
            int[] baudRates = new[] { preferredBaud, 9600, 19200, 38400, 57600, 115200, 4800, 2400, 1200 }.Distinct().ToArray();
            var formats = new[] { (Parity.None, 8, StopBits.One), (Parity.Even, 8, StopBits.One), (Parity.Odd, 8, StopBits.One), (Parity.None, 8, StopBits.Two), (Parity.Even, 8, StopBits.Two), (Parity.Odd, 8, StopBits.Two), (Parity.Even, 7, StopBits.One), (Parity.Odd, 7, StopBits.One) };
            int hits = 0;
            byte? firstHitUnit = null;
            Log("Начат автопоиск RTU. Сначала проверяется выбранная скорость; поиск остановится после полного прохода ID на первой рабочей комбинации.");
            foreach (int baud in baudRates)
            {
                foreach (var format in formats)
                {
                    ct.ThrowIfCancellationRequested();
                    int hitsBeforeFormat = hits;
                    Disconnect();
                    try
                    {
                        var com = ComBox.SelectedItem!.ToString()!;
                        _serial = new SerialPort(com, baud, format.Item1, format.Item2, format.Item3) { ReadTimeout = 180, WriteTimeout = 180 };
                        _serial.Open();
                        for (byte unit = 1; unit <= 247; unit++)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                var response = await ReadAsync(unit, 3, 0, 1, ct);
                                hits++;
                                firstHitUnit ??= unit;
                                Log($"НАЙДЕНО: COM={com}, {baud}, {format.Item1}, {format.Item2}{(format.Item3 == StopBits.One ? "N1" : "N2")}, Slave ID={unit}; FC03 адрес 0 = {response[0]}.");
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex) when (ex.Message.Contains("Modbus exception", StringComparison.OrdinalIgnoreCase))
                            {
                                // A valid exception response still proves the baud/format/Slave ID combination is alive.
                                hits++;
                                firstHitUnit ??= unit;
                                Log($"ОТВЕТ ЕСТЬ: COM={com}, {baud}, {format.Item1}, {format.Item2}{(format.Item3 == StopBits.One ? "N1" : "N2")}, Slave ID={unit}; адрес 0 отклонён устройством: {ex.Message}");
                            }
                            catch { }
                        }
                        Disconnect();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log($"Параметры {baud}/{format.Item1}/{format.Item2}/{format.Item3}: {ex.Message}"); Disconnect(); }

                    if (hits > hitsBeforeFormat)
                    {
                        BaudBox.SelectedItem = BaudBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == baud.ToString(CultureInfo.InvariantCulture));
                        ParityBox.SelectedIndex = format.Item1 switch { Parity.Even => 1, Parity.Odd => 2, _ => 0 };
                        DataBitsBox.SelectedIndex = format.Item2 - 5;
                        StopBitsBox.SelectedIndex = format.Item3 == StopBits.Two ? 1 : 0;
                        if (firstHitUnit.HasValue) UnitBox.Text = firstHitUnit.Value.ToString(CultureInfo.InvariantCulture);
                        Log($"Параметры обнаружены: скорость {baud}, {format.Item1}, {format.Item2} бит, {format.Item3}. Перебор других скоростей/форматов остановлен; найденные ID перечислены выше.");
                        break;
                    }
                }
                if (hits > 0) break;
            }
            Log($"Автопоиск RTU завершён. Найдено ответов: {hits}.");
        }
        catch (OperationCanceledException) { Log("Автопоиск остановлен пользователем."); }
        catch (Exception ex) { Log("Ошибка автопоиска: " + ex.Message); }
        finally { Disconnect(); StatusText.Text = "Автопоиск завершён"; }
    }

    private async void ScanRegisters_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel(); _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        try
        {
            await EnsureConnectedAsync(ct);
            int start = GetStart();
            int quantity = GetQuantity();
            if (start + quantity > 65536) throw new InvalidOperationException("Диапазон выходит за адрес 65535.");
            byte fc = (byte)(FunctionBox.SelectedIndex + 1);
            Log($"Поиск реализованных адресов: FC{fc}, {start}…{start + quantity - 1}.");
            int found = await ScanRegisterRangeAsync(GetUnit(), fc, start, quantity, ct);
            Log($"Поиск диапазона завершён. Ответивших адресов: {found}.");
        }
        catch (OperationCanceledException) { Log("Поиск регистров остановлен."); }
        catch (Exception ex) { Log("Ошибка поиска регистров: " + ex.Message); }
    }

    private async Task<int> ScanRegisterRangeAsync(byte unit, byte function, int start, int quantity, CancellationToken ct)
    {
        int found = 0;
        int max = function is 1 or 2 ? 128 : 32;
        for (int offset = 0; offset < quantity; offset += max)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(max, quantity - offset);
            try
            {
                var values = await ReadAsync(unit, function, (ushort)(start + offset), (ushort)count, ct);
                for (int i = 0; i < values.Length; i++)
                {
                    ushort addr = (ushort)(start + offset + i);
                    var row = _rows.FirstOrDefault(r => r.Address == addr);
                    if (row is null) { row = new RegisterRow { Address = addr }; _rows.Add(row); }
                    row.RawValue = values[i]; row.Quality = "OK";
                    found++;
                }
                Log($"FC{function}: диапазон {start + offset}–{start + offset + count - 1} отвечает.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (count > 1)
                {
                    int left = count / 2;
                    found += await ScanRegisterRangeAsync(unit, function, start + offset, left, ct);
                    found += await ScanRegisterRangeAsync(unit, function, start + offset + left, count - left, ct);
                }
                else
                {
                    var row = _rows.FirstOrDefault(r => r.Address == start + offset);
                    if (row is null) { row = new RegisterRow { Address = (ushort)(start + offset) }; _rows.Add(row); }
                    row.Quality = "No response / exception";
                    Log($"Адрес {start + offset}: {ex.Message}");
                }
            }
        }
        SortRows();
        UpdateInterpretedValues();
        return found;
    }

    private async void ScanTcpNetwork_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        try
        {
            if (!IPAddress.TryParse(TcpScanStartBox.Text.Trim(), out var startIp) || startIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new FormatException("Начальный адрес должен быть IPv4.");
            if (!IPAddress.TryParse(TcpScanEndBox.Text.Trim(), out var endIp) || endIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new FormatException("Конечный адрес должен быть IPv4.");
            int port = ParseInt(TcpScanPortBox.Text, 1, 65535, "Порт сканирования");
            int timeout = ParseInt(TcpScanTimeoutBox.Text, 100, 5000, "Тайм-аут");
            uint start = IpToUInt(startIp), end = IpToUInt(endIp);
            if (end < start) throw new InvalidOperationException("Конечный IP должен быть не меньше начального.");
            if (end - start > 4095) throw new InvalidOperationException("За один запуск разрешено сканировать максимум 4096 адресов.");
            TcpScanResultsList.Items.Clear();
            StatusText.Text = "Поиск TCP-узлов…";
            Log($"TCP-поиск: {startIp}–{endIp}, порт {port}, тайм-аут {timeout} мс.");
            int found = 0, checkedCount = 0;
            using var gate = new SemaphoreSlim(32);
            var tasks = new List<Task>();
            for (uint address = start; address <= end; address++)
            {
                uint current = address;
                await gate.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        using var client = new TcpClient { NoDelay = true };
                        try
                        {
                            await client.ConnectAsync(UIntToIp(current), port, ct).AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeout), ct);
                            string endpoint = $"{UIntToIp(current)}:{port}";
                            await Dispatcher.InvokeAsync(() => TcpScanResultsList.Items.Add(endpoint));
                            Interlocked.Increment(ref found);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { /* Closed or filtered port: not a hit. */ }
                        finally { Interlocked.Increment(ref checkedCount); }
                    }
                    finally { gate.Release(); }
                }, ct));
                if (address == uint.MaxValue) break;
            }
            await Task.WhenAll(tasks);
            StatusText.Text = $"TCP-поиск завершён · найдено {found}";
            Log($"TCP-поиск завершён: найдено открытых узлов {found} из {checkedCount} проверенных IP.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "TCP-поиск остановлен";
            Log("TCP-поиск остановлен пользователем.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка TCP-поиска";
            Log("TCP-поиск: " + ex.Message);
        }
    }

    private void TcpScanResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TcpScanResultsList.SelectedItem is not string endpoint) return;
        int separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(endpoint[(separator + 1)..], out int port)) return;
        HostBox.Text = endpoint[..separator];
        PortBox.Text = port.ToString(CultureInfo.InvariantCulture);
        ProtocolBox.SelectedIndex = 1;
        Log($"Выбран TCP-узел {endpoint}. Укажите Unit ID и нажмите «Подключить / проверить» для подтверждения Modbus.");
    }

    private static uint IpToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress UIntToIp(uint ip) => new(new byte[]
    {
        (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip
    });

    private void StopScan_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV UTF-8 (*.csv)|*.csv", FileName = "modbus-registers.csv" };
        if (dialog.ShowDialog() != true) return;
        var sb = new StringBuilder("Address;Decimal;Hex;Binary;Interpreted;Description;Quality\r\n");
        foreach (var row in _rows.OrderBy(r => r.Address))
            sb.Append(row.Address).Append(';').Append(row.RawValue).Append(';').Append(row.HexValue).Append(';')
              .Append(row.BinaryValue).Append(';').Append(row.DisplayValue).Append(';').Append('\"').Append(row.Description.Replace("\"", "\"\"")).Append('\"').Append(';').Append(row.Quality).Append("\r\n");
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        Log($"Экспортировано {_rows.Count} строк в {dialog.FileName}");
    }

    private void SortRows()
    {
        var ordered = _rows.OrderBy(r => r.Address).ToList();
        _rows.Clear();
        foreach (var row in ordered) _rows.Add(row);
    }

    private byte GetUnit() => (byte)ParseInt(UnitBox.Text, 0, 247, "Unit ID");
    private ushort GetStart() => ParseUShort(StartAddressBox.Text, "Стартовый адрес");
    private ushort GetQuantity() => (ushort)ParseInt(QuantityBox.Text, 1, 2000, "Количество");
    private static ushort ParseUShort(string text, string field)
    {
        if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return v;
        throw new FormatException($"{field}: введите число 0–65535 (можно 0xHEX).");
    }
    private static int ParseInt(string text, int min, int max, string field)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max) return v;
        throw new FormatException($"{field}: допустимый диапазон {min}–{max}.");
    }

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(message)); return; }
        LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        while (LogList.Items.Count > 1000) LogList.Items.RemoveAt(LogList.Items.Count - 1);
    }

    protected override void OnClosed(EventArgs e)
    {
        _scanCts?.Cancel();
        Disconnect();
        base.OnClosed(e);
    }
}