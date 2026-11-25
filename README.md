# Intel NUC Hardware Fan Control Suite

Native hardware fan controller and monitoring suite engineered specifically for Intel NUC systems featuring Nuvoton Super I/O chips, such as the Intel NUC Kit NUC6i7KYK (Skull Canyon).

## Overview

Modern Intel NUC tools like Intel NUC Software Studio only support 11th Gen and newer NUC platforms via proprietary WMI endpoints. Older high-performance NUC models like the Skull Canyon (NUC6i7KYK with Core i7-6770HQ) lack those WMI services, leaving users without functional software cooling control.

This repository provides a complete replacement:
1. Native Ring-0 Kernel Driver Bridge (PawnIO 2.2.0) to safely talk to LPC Super I/O ports.
2. Low-Level Super I/O Protocol Driver for Nuvoton NCT5577D / NCT6776F.
3. Native Rust CLI tool (`nuc-fan-control.exe`) for status, monitoring, dynamic curve daemon, and scripted overrides.
4. Modern Windows Desktop GUI (`NucFanControlGUI.exe`) featuring real-time tachometer telemetry, quick cooling presets, custom slider, and boot persistence.

## Hardware Specification

* Target Platform: Intel NUC Kit NUC6i7KYK (Skull Canyon)
* Motherboard: Intel NUC6i7KYB
* CPU: Intel Core i7-6770HQ (4 Cores, 8 Threads, 45W TDP)
* Super I/O Chip: Nuvoton NCT5577D (reports Chip ID 0xC333, compatible with NCT6776F family)
* Configuration Ports: 0x2E (Index), 0x2F (Data)
* Hardware Monitor Logical Device: LDN 0x0B
* Hardware Monitor Base Port: 0x0A00 (Index 0x0A05, Data 0x0A06)

## Register Mapping and Control Logic

* Config Mode Entry: Write `0x87, 0x87` to port `0x2E`.
* Unlock I/O Space: Clear bit 4 of Configuration Register `0x28`.
* Select Bank: Write bank number to HWM register `0x4E` (Port `0x0A05 = 0x4E`, Data `0x0A06`).
* Fan Mode (Bank 1, Register `0x02`):
  * `0x00`: Manual PWM mode (software control).
  * `0x40`: SmartFan IV mode (BIOS automatic curve).
* Fan PWM Output (Bank 1, Register `0x09`):
  * Value `0` to `255` (`255` = 100% full tilt over-rotation).
* Fan Tachometer (Bank 6, Registers `0x58` and `0x59`):
  * 16-bit RPM counter directly read from hardware.
* Temperatures (Bank 0):
  * Register `0x73` / `0x74`: CPU Socket Temperature (CPUTIN).
  * Register `0x75` / `0x76`: Auxiliary Temperature (AUXTIN).
  * Register `0x77` / `0x78`: System Board Temperature (SYSTIN).

## Project Structure

* `cli/`: Rust engine and CLI (`nuc-fan-control.exe`).
* `gui/`: Windows Presentation Foundation (WPF) GUI (`NucFanControlGUI.exe`).
* `assets/`: Signed driver blobs and binary modules (`LpcIO.bin`).

## Usage

### CLI Commands

```text
nuc-fan-control status
nuc-fan-control max
nuc-fan-control set 80%
nuc-fan-control auto
nuc-fan-control monitor
nuc-fan-control enable-boot-max
nuc-fan-control disable-boot-max
```

### GUI Application

Launch `NucFanControlGUI.exe`. It provides:
* Live dashboard showing CPU, System, and Aux temperatures.
* Real-time tachometer showing actual fan RPM and duty percentage.
* One-click Maximum Cooling button (100% full tilt).
* One-click BIOS Auto button to restore default fan curves.
* Quick preset buttons (50% Quiet, 70% Cool, 85% Heavy, 95% Extreme).
* Granular custom fan speed slider.
* Checkbox for startup persistence via Windows Task Scheduler.
