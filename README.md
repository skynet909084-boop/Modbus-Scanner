# Modbus Scanner

Standalone Windows x64 Modbus RTU (RS-485/serial) and Modbus TCP diagnostic application. Current update: **v1.0.2**.

## Build

Open `ModbusScanner/ModbusScanner.csproj` with Visual Studio 2022 / .NET 8, or run:

```powershell
dotnet publish .\ModbusScanner\ModbusScanner.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

A Windows x64 build is produced automatically by GitHub Actions when changes are pushed to `main`. These **Actions artifacts are temporary** and expire after the retention period.

- [Download a permanent packaged release](https://github.com/skynet909084-boop/Modbus-Scanner/releases) when a version is published.
- [Open Actions builds](https://github.com/skynet909084-boop/Modbus-Scanner/actions) to download a recent test build.
- For Modbus register addressing, enter the **zero-based protocol address** (0–65535), not the 4xxxx/3xxxx reference notation. For example, holding register reference 40001 usually corresponds to protocol address 0; confirm the convention in your device manual.

## What changed in v1.0.2

- Improved contrast of labels and muted text for easier reading.
- Clearer hover and selection states for tabs, drop-down lists, and result rows.
- More legible text in the register table and disabled controls.
- Modbus scanning and communication behavior unchanged in this visual update.

## Features

- Modbus RTU and Modbus TCP connections
- Register/coil reads and supported single/multiple writes
- RTU device discovery and TCP endpoint scanning
- Continuous polling, register interpretation, CSV export, saved connection profiles
- Dark high-contrast operator UI

See [application README](ModbusScanner/README.md) for more details.
