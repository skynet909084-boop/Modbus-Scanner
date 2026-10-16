using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using ModbusScanner.Models;

namespace ModbusScanner;

public partial class MainWindow
{
    private readonly ObservableCollection<SnapshotDifference> _differences = new();
    private readonly Queue<HistoryPoint> _history = new();
    private Dictionary<string,ushort>? _snapshot;
    private string? _snapshotContext;
    private string _dataContext="";
    private void EnsureDataContext()
    {
        if(_dataContext!=ConnectionContext){
            foreach(var row in _rows)row.Quality="Устарело";
            _dataContext=ConnectionContext;
        }
    }
    private void SetBusy(bool busy)
    {
        foreach(var control in new Control[]{ProtocolBox,HostBox,PortBox,ComBox,BaudBox,ParityBox,DataBitsBox,StopBitsBox,UnitBox,FunctionBox,StartAddressBox,QuantityBox,MonitorIntervalBox,RtuBaudsBox,RtuParitiesBox,RtuUnitFromBox,RtuUnitToBox})control.IsEnabled=!busy;
        if(!busy){
            bool tcp=IsTcp;ComBox.IsEnabled=!tcp;BaudBox.IsEnabled=!tcp;ParityBox.IsEnabled=!tcp;DataBitsBox.IsEnabled=!tcp;StopBitsBox.IsEnabled=!tcp;HostBox.IsEnabled=tcp;PortBox.IsEnabled=tcp;
        }
    }
    private long _requests, _errors;
    private double _totalLatency;
    private string _historyContext="";
    private sealed record SnapshotFile(int Version, string Context, DateTimeOffset Captured, Dictionary<string,ushort> Values);
    private sealed record SnapshotDifference(byte Function, ushort Address, string Before, string After, string Bits);
    private sealed record HistoryPoint(DateTimeOffset Time, string Context, byte Function, ushort Address, ushort Raw, double Value, string Unit);
    private string ConnectionContext => IsTcp ? $"TCP|{HostBox.Text.Trim()}|{PortBox.Text}|{UnitBox.Text}" : $"RTU|{ComBox.Text}|{BaudBox.Text}|{ParityBox.Text}|{DataBitsBox.Text}|{StopBitsBox.Text}|{UnitBox.Text}";
    private static string RowKey(byte fc, ushort address) => $"{fc}:{address}";

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        EnsureDataContext();
        _snapshot = _rows.Where(r=>r.Quality=="OK").ToDictionary(r=>RowKey(r.Function,r.Address),r=>r.RawValue);
        _snapshotContext=ConnectionContext;
        UpdateSnapshotComparison();
        Log($"Снимок: {_snapshot.Count} достоверных значений.");
    }
    private void UpdateSnapshotComparison()
    {
        _differences.Clear();
        if (_snapshot==null || _snapshotContext!=ConnectionContext) return;
        foreach(var row in _rows.Where(r=>r.Quality=="OK"))
        {
            if (!_snapshot.TryGetValue(RowKey(row.Function,row.Address),out var before))
                _differences.Add(new(row.Function,row.Address,"нет в снимке",row.RawValue.ToString(),"новый адрес"));
            else if(before!=row.RawValue)
                _differences.Add(new(row.Function,row.Address,before.ToString(),row.RawValue.ToString(),RegisterAnalysis.ChangedBits(before,row.RawValue)));
        }
        foreach(var pair in _snapshot)
        {
            var parts=pair.Key.Split(':'); byte fc=byte.Parse(parts[0]); ushort address=ushort.Parse(parts[1]);
            if(!_rows.Any(r=>r.Function==fc && r.Address==address && r.Quality=="OK"))
                _differences.Add(new(fc,address,pair.Value.ToString(),"нет свежих данных",""));
        }
        SnapshotGrid.ItemsSource=_differences;
    }
    private void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        try {
            if(_snapshot==null) throw new InvalidOperationException("Сначала сделайте снимок.");
            var d=new SaveFileDialog{Filter="Снимок (*.json)|*.json",FileName="snapshot.json"};
            if(d.ShowDialog()==true) File.WriteAllText(d.FileName,JsonSerializer.Serialize(new SnapshotFile(1,_snapshotContext!,DateTimeOffset.Now,_snapshot),new JsonSerializerOptions{WriteIndented=true}));
        } catch(Exception ex){Log(ex.Message);}
    }
    private void LoadSnapshot_Click(object sender,RoutedEventArgs e)
    {
        try {
            var d=new OpenFileDialog{Filter="Снимок (*.json)|*.json"}; if(d.ShowDialog()!=true)return;
            if(new FileInfo(d.FileName).Length>8_000_000)throw new InvalidDataException("Снимок слишком большой.");
            var file=JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(d.FileName)) ?? throw new InvalidDataException("Пустой снимок.");
            if(file.Version!=1 || file.Values==null || file.Values.Count>262144)throw new InvalidDataException("Неверный формат снимка.");
            foreach(var key in file.Values.Keys){var a=key.Split(':');if(a.Length!=2 || !byte.TryParse(a[0],out var f) || f<1 || f>4 || !ushort.TryParse(a[1],out _))throw new InvalidDataException("Неверный адрес снимка.");}
            if(file.Context!=ConnectionContext)throw new InvalidDataException("Снимок относится к другому подключению/Unit. Загрузите соответствующий профиль.");
            _snapshot=file.Values;_snapshotContext=file.Context;UpdateSnapshotComparison();Log("Снимок загружен.");
        }catch(Exception ex){Log(ex.Message);}
    }
    private void CaptureHistory(byte function,ushort start,ushort[] values)
    {
        if(_historyContext!=ConnectionContext){_history.Clear();_historyContext=ConnectionContext;}
        var time=DateTimeOffset.Now;
        for(int i=0;i<values.Length;i++){
            ushort address=(ushort)(start+i);
            var row=_rows.First(r=>r.Function==function && r.Address==address);
            _history.Enqueue(new(time,_historyContext,function,address,values[i],values[i]*row.Scale+row.Offset,row.Unit));
        }
        while(_history.Count>100000)_history.Dequeue();
        DrawTrend();
    }
    private void DrawTrend()
    {
        if(TrendCanvas==null || TrendAddressBox==null)return;
        TrendCanvas.Children.Clear();
        var addresses=TrendAddressBox.Text.Split(',').Select(x=>ushort.TryParse(x.Trim(),out var v)?(ushort?)v:null).ToArray();
        if(addresses.Length>4 || addresses.Any(x=>!x.HasValue))return;
        byte fc=(byte)(FunctionBox.SelectedIndex+1);
        var series=addresses.Select(a=>_history.Where(p=>p.Address==a!.Value && p.Function==fc && double.IsFinite(p.Value)).TakeLast(1000).ToArray()).Where(p=>p.Length>=2).ToArray();
        if(series.Length==0)return;
        double min=series.Min(s=>s.Min(p=>p.Value)),max=series.Max(s=>s.Max(p=>p.Value)),span=Math.Max(max-min,1e-9);
        var first=series.Min(s=>s[0].Time);var last=series.Max(s=>s[^1].Time);
        double width=Math.Max(TrendCanvas.ActualWidth-20,1),height=Math.Max(TrendCanvas.ActualHeight-65,1),seconds=Math.Max((last-first).TotalSeconds,0.001);
        Brush[] colors={Brushes.Turquoise,Brushes.Orange,Brushes.LightPink,Brushes.YellowGreen};
        for(int i=0;i<series.Length;i++){
            var points=series[i];var line=new Polyline{Stroke=colors[i],StrokeThickness=2};
            foreach(var point in points)line.Points.Add(new Point(10+(point.Time-first).TotalSeconds/seconds*width,10+height-(point.Value-min)/span*height));
            TrendCanvas.Children.Add(line);
            var label=new TextBlock{Text=$"FC{fc}/{points[0].Address}: {points[^1].Value:G6} {points[^1].Unit}",Foreground=colors[i]};Canvas.SetTop(label,height+15+(i/2)*18);Canvas.SetLeft(label,(i%2)*300);TrendCanvas.Children.Add(label);
        }
        var axis=new TextBlock{Text=$"min {min:G6} / max {max:G6} · {first:HH:mm:ss}–{last:HH:mm:ss}",Foreground=Brushes.White};TrendCanvas.Children.Add(axis);
    }

    private void TrendChanged(object sender,RoutedEventArgs e)=>DrawTrend();
    private void TrendResized(object sender,SizeChangedEventArgs e)=>DrawTrend();
    private void ClearHistory_Click(object sender,RoutedEventArgs e){_history.Clear();DrawTrend();}
    private void ExportHistory_Click(object sender,RoutedEventArgs e)
    {
        try{
            var d=new SaveFileDialog{Filter="CSV (*.csv)|*.csv",FileName="modbus-history.csv"};if(d.ShowDialog()!=true)return;
            using var writer=new StreamWriter(d.FileName,false,new UTF8Encoding(true));writer.WriteLine("Timestamp;Connection;Function;Address;Raw;Scaled;Unit");
            foreach(var p in _history)writer.WriteLine($"{p.Time:O};{RegisterAnalysis.Csv(p.Context)};{p.Function};{p.Address};{p.Raw};{p.Value.ToString("R",CultureInfo.InvariantCulture)};{RegisterAnalysis.Csv(p.Unit)}");
            Log($"История сохранена: {_history.Count} точек.");
        }catch(Exception ex){Log(ex.Message);}
    }
    private void ExportLog_Click(object sender,RoutedEventArgs e)
    {
        try{var d=new SaveFileDialog{Filter="Журнал (*.txt)|*.txt",FileName="modbus-log.txt"};if(d.ShowDialog()==true)File.WriteAllLines(d.FileName,LogList.Items.Cast<string>().Reverse());}catch(Exception ex){Log(ex.Message);}
    }
    private void LogFrame(string direction,byte[] bytes)=>Log($"{direction}: {Convert.ToHexString(bytes)}");
    private void RecordExchange(double latency,Exception? error)
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.Invoke(()=>RecordExchange(latency,error));return;}
        _requests++;_totalLatency+=latency;
        string category=error switch {null=>"OK",ModbusDeviceException=>"Исключение Modbus",TimeoutException or OperationCanceledException=>"Тайм-аут / отмена",System.Net.Sockets.SocketException=>"Соединение",EndOfStreamException=>"Обрыв",_ when error.Message.Contains("CRC")=>"CRC",_=>"Кадр / обмен"};
        if(error!=null){_errors++;Log($"{category}: {error.Message}");foreach(var row in _rows)row.Quality="Устарело";UpdateSnapshotComparison();}
        DiagnosticsText.Text=$"Запросов {_requests} · ошибок {_errors} ({100.0*_errors/_requests:F1}%) · последний {latency:F1} мс · среднее {_totalLatency/_requests:F1} мс · {category}";
    }
    private bool ApproveWrite(byte function,ushort start,ushort[] values)
    {
        EnsureDataContext();
        if(EnableWritesBox.IsChecked!=true){Log("Запись выключена. Включите разрешение записи.");return false;}
        if(_scanCts!=null){Log("Перед записью остановите поиск/мониторинг.");return false;}
        string preview=string.Join("\n",values.Select((v,i)=>{
            var old=_rows.FirstOrDefault(r=>r.Function==(function==5?1:3)&&r.Address==start+i && r.Quality=="OK");
            return $"{start+i}: {(old==null?"неизвестно":old.RawValue.ToString())} → {v} (0x{v:X4})"+(old==null?"":"; "+RegisterAnalysis.ChangedBits(old.RawValue,v));
        }));
        return MessageBox.Show($"{ConnectionContext}\nFC{function:D2}, адреса {start}–{start+values.Length-1}\n{preview}\n\nОтправить запись?", "Проверка записи",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes;
    }
}
