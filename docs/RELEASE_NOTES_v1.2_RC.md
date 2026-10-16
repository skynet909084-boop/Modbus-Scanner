# Modbus Scanner v1.2.0 — Release Candidate Notes

**Status:** release candidate preparation; not approved as stable.

## Changes / Изменения
- Modbus RTU/TCP: read/write controls, DEC/HEX input, read-back verification and safe write blocking during monitoring/scans.
- Register interpretation, maps, monitoring, simulator, IP discovery, logical network map and network diagnostics.
- USB / RS-485 adapters: read-only Windows COM/VID/PID discovery (FTDI, WCH CH34x, CP210x, PL2303), COM selection for RTU and TXT diagnostics export.
- Russian and English interface and Help.

## Important limitations / Ограничения
- USB drivers are **not bundled or installed**; use Windows and official manufacturer drivers.
- USB chip identification is approximate; presence of a COM port does not validate A/B wiring, termination or Modbus response.
- Network map is **logical**, not verified physical switch topology.
- Windows CI compilation is not a substitute for hardware or manual GUI testing.

## Mandatory release gates / Обязательные проверки
1. Latest branch commit passes Windows GitHub Actions build and produces usable Windows artifacts.
2. Loopback TCP simulator smoke test and read/write verification pass.
3. No regression on network diagnostics, export, cancellation, bilingual UI and settings persistence.
4. A tester validates GUI startup, scaling, connection/disconnection and a USB RS-485 adapter on Windows.
5. Any physical write test is done only on isolated authorized test equipment, never on production equipment.
6. All blocking defects fixed; PR reviewed and merged to main; release/tag v1.2.0 created only then.

See [Release QA checklist](RELEASE_CHECKLIST_v1.2.md).

**RU:** Релиз пока не подтверждён. Не используйте предварительную сборку для записи в действующее промышленное оборудование без испытаний и разрешения.
