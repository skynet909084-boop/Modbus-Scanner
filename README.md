# Modbus Scanner

Standalone Windows x64 Modbus RTU (RS-485/serial) and Modbus TCP diagnostic application.

## Build

Open `ModbusScanner/ModbusScanner.csproj` with Visual Studio 2022 / .NET 8, or run:

```powershell
dotnet publish .\ModbusScanner\ModbusScanner.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

A Windows x64 single-file build is also produced automatically by GitHub Actions when changes are pushed to `main`. Open the repository's **Actions** tab and download the `Modbus-Scanner-Windows-x64` artifact from a successful run.

## Features

- Modbus RTU and Modbus TCP connections
- Register/coil reads and supported single/multiple writes
- RTU device discovery and TCP endpoint scanning
- Continuous polling, register interpretation, CSV export, saved connection profiles
- Dark high-contrast operator UI

See [application README](ModbusScanner/README.md) for more details.
