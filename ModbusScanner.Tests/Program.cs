using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ModbusScanner;
using ModbusScanner.Models;

static class Program
{
    static int count;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
    [STAThread]
    static void Main()
    {
        Check(RegisterAnalysis.Order32(0x1234,0x5678,"ABCD")==0x12345678,"ABCD");
        Check(RegisterAnalysis.Order32(0x1234,0x5678,"BADC")==0x34127856,"BADC");
        Check(RegisterAnalysis.Order32(0x1234,0x5678,"CDAB")==0x56781234,"CDAB");
        Check(RegisterAnalysis.Order32(0x1234,0x5678,"DCBA")==0x78563412,"DCBA");
        Check(RegisterAnalysis.Float(0x3f80,0,"ABCD")=="1","Float32 IEEE754");
        Check(RegisterAnalysis.ChangedBits(0,0x8001)=="b0: 0→1, b15: 0→1","bit edges");
        Check(RegisterAnalysis.Csv("a;\"b")=="\"a;\"\"b\"","CSV escaping");
        var row=new RegisterRow{RawValue=65535,Scale=0.1,Offset=-10};Check(row.SignedValue==-1 && Math.Abs(row.ScaledValue-6543.5)<0.001,"signed and scaled");
        byte[] header={0x53,0x11,0,0,0,5,1};byte[] tail={3,2,0,42};
        Check(TcpProbe.Validate(header,tail,1,3,0x5311),"probe valid");
        header[1]=0x12;Check(!TcpProbe.Validate(header,tail,1,3,0x5311),"wrong transaction rejected");header[1]=0x11;
        header[5]=3;Check(TcpProbe.Validate(header,new byte[]{0x83,2},1,3,0x5311),"exception proves Modbus");
        Check(!TcpProbe.Validate(header,new byte[]{0x84,2},1,3,0x5311),"wrong exception function");
        ProbeLoopback(false).GetAwaiter().GetResult();ProbeLoopback(true).GetAwaiter().GetResult();
        var app=new Application();var window=new MainWindow();
        var enable=(CheckBox)window.FindName("EnableWritesBox");Check(enable.IsChecked==false,"writes disabled on startup");
        var method=typeof(MainWindow).GetMethod("ApplyReadValues",BindingFlags.NonPublic|BindingFlags.Instance)!;
        method.Invoke(window,new object[]{(ushort)0,new ushort[]{10,20},(byte)3});
        method.Invoke(window,new object[]{(ushort)0,new ushort[]{30},(byte)4});
        var rows=(System.Collections.ObjectModel.ObservableCollection<RegisterRow>)typeof(MainWindow).GetField("_rows",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
        Check(rows.Count==3 && rows.Count(r=>r.Address==0)==2,"FC address spaces isolated");
        typeof(MainWindow).GetMethod("Snapshot_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,new object[]{window,new RoutedEventArgs()});
        method.Invoke(window,new object[]{(ushort)0,new ushort[]{11,20},(byte)3});
        var differences=(DataGrid)window.FindName("SnapshotGrid");
        Check(differences.Items.Count==1,"snapshot comparison uses production apply path");
        window.Close();Console.WriteLine($"{count} checks passed");
    }
    static async Task ProbeLoopback(bool stalled)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline=new CancellationTokenSource(3000);
        var server=Task.Run(async()=>{
            using var peer=await listener.AcceptTcpClientAsync(deadline.Token);var stream=peer.GetStream();byte[] request=new byte[12];await stream.ReadExactlyAsync(request,deadline.Token);
            if(stalled){await Task.Delay(500,deadline.Token);return;}
            byte[] response={request[0],request[1],0,0,0,5,1,3,2,0,42};
            foreach(byte b in response){await stream.WriteAsync(new[]{b},deadline.Token);await Task.Delay(2,deadline.Token);}
        });
        try{
            if(stalled){try{await TcpProbe.RunAsync("127.0.0.1",port,1,3,0,100,deadline.Token);throw new Exception("probe did not time out");}catch(OperationCanceledException){Check(true,"probe deadline");}}
            else Check(await TcpProbe.RunAsync("127.0.0.1",port,1,3,0,1000,deadline.Token)!=null,"fragmented TCP probe");
            await server;
        }finally{listener.Stop();}
    }
}
