# Modbus Scanner v1.2.0 — release verification checklist

This checklist records what must be tested on Windows before merging into stable main. A green GitHub Actions build is **not** proof that these UI and hardware scenarios passed.

## Safe startup / Первый запуск
- [ ] Start on Windows with no COM ports or network devices; app opens without crashing.
- [ ] Run loopback TCP simulator on 127.0.0.1:1502; connect with Unit ID 1, FC03, address 0, quantity 10.
- [ ] Stop simulator while connected: status changes to unverified; restart and reconnect.
- [ ] Manually disconnect: connection indicator immediately changes from green to unverified.
- [ ] Switch TCP/RTU: gray mode status and correct enabled inputs.

## Protocol correctness / Протокол
- [ ] Read FC01/02 bits, FC03/04 registers and validate address zero/one and max range boundaries.
- [ ] FC05/06/16 write only after explicit permission and confirmation; read-back matches expected values.
- [ ] While monitoring or scanning, attempt write: no Modbus write frame is sent.
- [ ] Start monitoring while a write waits on transport lock: blocked write is rechecked before transmission.
- [ ] Verify incomplete, invalid MBAP, CRC, exception, and timeout responses show meaningful errors.
- [ ] Interrupt TCP exchange and reconnect: no stale response is consumed by next request.
- [ ] Change start address or FC while a read is pending: result retains original request address/function.
- [ ] Test RTU serial unplug, access denied, incorrect baud, and recovery on physical equipment only when available.

## Network tools / Сеть
- [ ] Adapter selection, subnet-derived scan range, 1024-IP limit, stop/cancel, CSV export.
- [ ] Network map explicitly says logical grouping; no unsupported claims about physical switch topology.
- [ ] Ping, TCP probe, DNS, traceroute, gateway comparison, ARP change detection, TXT export.
- [ ] Confirm devices that block ICMP can still be tested via TCP without being labelled offline.

## Accessibility / Удобство
- [ ] Russian and English toggle translates tabs, dialogs, Help, dynamic statuses and diagnostic report headers.
- [ ] Selected language persists after app restart.
- [ ] On 100%, 125%, 150% Windows scaling, no clipped controls or hidden primary actions.
- [ ] Keyboard tab order, visible focus, button labels and readable error guidance.
- [ ] Verify Help examples on a clean installation without real PLC hardware.

## Delivery / Выпуск
- [ ] Windows GitHub Actions build succeeds for the final commit.
- [ ] Downloaded artifact starts on a clean Windows machine.
- [ ] No regression in settings/profile load/save, register map import/export or simulator.
- [ ] Release notes and bilingual Help match shipped behavior.
- [ ] Only after manual QA: review PR and merge into main, then tag v1.2.0.

**Safety:** never run write tests against a production PLC or actuator without explicit permission, isolation and a recovery plan.
