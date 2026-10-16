# Modbus Scanner

Independent Windows x64 diagnostic utility for Modbus RTU (USB–RS-485) and Modbus TCP. It is a separate WPF application and does not reference or require SKYNET.

## Current implementation

- RTU serial settings: COM port, baud rate, parity, data bits, stop bits, Unit ID.
- TCP connection: host and port (normally 502), Unit ID.
- Read Coils (FC01), Discrete Inputs (FC02), Holding Registers (FC03), Input Registers (FC04).
- Write Single Coil (FC05), Write Single Register (FC06), Write Multiple Registers (FC16), with confirmation enabled by default.
- RTU CRC validation and Modbus TCP MBAP framing.
- Range scan with recursive subdivision when a range returns an exception.
- RTU discovery across common baud rates and serial formats, and Slave IDs 1–247.
- Register table, HEX/BIN views, quality/status, editable descriptions, and CSV export.
- Typed register interpretation: UInt16, Int16, UInt32, Int32, Float32 with ABCD or CDAB word order. 32-bit interpretations combine the selected register with the next address.
- Write acknowledgements are checked for the expected address and echoed value/quantity (including RTU CRC bytes).

## Important limitations

The RTU auto-discovery currently tests FC03 at address 0. Devices that reject this address or require a different function may not be discovered by this first-pass scan. The search is intentionally stoppable and can take a while because silent devices time out. Serial timing and termination settings depend on the USB–RS-485 adapter and target controller.

Register addresses are shown as **zero-based protocol addresses**. Vendor manuals sometimes document addresses using 1-based notation (for example, 40001); translate those carefully before writing. Writing to unknown live equipment can change outputs or setpoints: keep confirmation enabled and verify the target address before writing.

## Build

Use the GitHub Actions workflow **Modbus Scanner Windows x64**. It publishes a self-contained single-file Windows x64 application as the artifact **Modbus-Scanner-Windows-x64**.

Local command:

~~~powershell
dotnet publish ModbusScanner/ModbusScanner.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/ModbusScanner
~~~

