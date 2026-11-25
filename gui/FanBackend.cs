using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NucFanControlGUI
{
    public class VoltageRails
    {
        public double Vcore { get; set; }
        public double Vmem { get; set; }
        public double Avcc { get; set; }
        public double ThreeVcc { get; set; }
        public double Vin4 { get; set; }
        public double Vin6 { get; set; }
    }

    public class ThermalZones
    {
        public float Core0 { get; set; }
        public float Cputin { get; set; }
        public float Systin { get; set; }
        public float Auxtin { get; set; }
    }

    public class CpuHardwareSpecs
    {
        public uint Ratio1C { get; set; } = 35;
        public uint Ratio2C { get; set; } = 34;
        public uint Ratio3C { get; set; } = 33;
        public uint Ratio4C { get; set; } = 32;
        public uint UncoreMin { get; set; } = 8;
        public uint UncoreMax { get; set; } = 35;
        public uint TjMax { get; set; } = 100;
        public bool CfgLock { get; set; } = false;
        public bool OcCapable { get; set; } = true;
    }

    public class FanCurveSettings
    {
        public bool IsEnabled { get; set; } = false;
        public double OffTemp { get; set; } = 40.0;
        public double RampTemp { get; set; } = 55.0;
        public double FullTemp { get; set; } = 75.0;
        public double MinDuty { get; set; } = 40.0;
    }

    public class FanBackend : IDisposable
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        private const uint DEVICE_TYPE = 41394u << 16;
        private const uint IOCTL_PIO_LOAD_BINARY = 0x821 << 2;
        private const uint IOCTL_PIO_EXECUTE_FN = 0x841 << 2;
        private const uint CTL_LOAD = DEVICE_TYPE | IOCTL_PIO_LOAD_BINARY;
        private const uint CTL_EXEC = DEVICE_TYPE | IOCTL_PIO_EXECUTE_FN;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        private SafeFileHandle? _hLpc;
        private SafeFileHandle? _hMsr;
        private ushort _hwmBase = 0x0A00;
        private bool _isLpcInitialized = false;
        private bool _isMsrInitialized = false;

        private uint _tjMax = 100;
        private double _energyUnit = 0.00006103515625;
        private ulong _lastEnergy = 0;
        private DateTime _lastEnergyTime = DateTime.UtcNow;

        private bool _watchdogTripped = false;
        public bool WatchdogTripped => _watchdogTripped;

        public FanCurveSettings CurveConfig { get; } = new FanCurveSettings();
        private byte _lastCurvePwm = 0;

        public bool IsInitialized => _isLpcInitialized;
        public bool IsMsrAvailable => _isMsrInitialized;

        public FanBackend()
        {
            InitializeLpc();
            InitializeMsr();
        }

        private void InitializeLpc()
        {
            try
            {
                _hLpc = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO",
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);

                if (_hLpc.IsInvalid) return;

                byte[] bin = File.ReadAllBytes(@"C:\ProgramData\LpcIO.bin");
                uint returned;
                if (!DeviceIoControl(_hLpc, CTL_LOAD, bin, (uint)bin.Length, null, 0, out returned, IntPtr.Zero))
                    return;

                ExecLpc("ioctl_select_slot", new long[] { 0 }, 0);

                // Enter SIO config (0x87, 0x87)
                WritePort(0x2E, 0x87);
                WritePort(0x2E, 0x87);

                byte id = ReadByte(0x20);
                if (id != 0xC3)
                {
                    WritePort(0x2E, 0xAA);
                    return;
                }

                ExecLpc("ioctl_find_bars", Array.Empty<long>(), 0);

                // LDN 0x0B (Hardware Monitor)
                WriteByte(0x07, 0x0B);

                // Unlock CR 0x28
                byte cr28 = ReadByte(0x28);
                if ((cr28 & 0x10) != 0)
                {
                    WriteByte(0x28, (byte)(cr28 & ~0x10));
                }

                byte baseHi = ReadByte(0x60);
                byte baseLo = ReadByte(0x61);
                _hwmBase = (ushort)((baseHi << 8) | baseLo);

                WritePort(0x2E, 0xAA);
                _isLpcInitialized = true;
            }
            catch
            {
                _isLpcInitialized = false;
            }
        }

        private void InitializeMsr()
        {
            try
            {
                _hMsr = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO",
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);

                if (_hMsr.IsInvalid) return;

                byte[] bin = File.ReadAllBytes(@"C:\ProgramData\IntelMSR.bin");
                uint returned;
                if (!DeviceIoControl(_hMsr, CTL_LOAD, bin, (uint)bin.Length, null, 0, out returned, IntPtr.Zero))
                    return;

                ulong units = ReadMsr(0x606);
                int energyPow = (int)((units >> 8) & 0x1F);
                _energyUnit = energyPow > 0 ? 1.0 / (1 << energyPow) : 0.00006103515625;

                ulong tjMsr = ReadMsr(0x1A2);
                uint val = (uint)((tjMsr >> 16) & 0xFF);
                if (val >= 70 && val <= 115) _tjMax = val;

                _lastEnergy = ReadMsr(0x611);
                _lastEnergyTime = DateTime.UtcNow;
                _isMsrInitialized = true;
            }
            catch
            {
                _isMsrInitialized = false;
            }
        }

        private long[] ExecLpc(string fn, long[] inArgs, int outCount)
        {
            if (_hLpc == null || _hLpc.IsInvalid) return new long[outCount];

            byte[] inBuf = new byte[32 + inArgs.Length * 8];
            byte[] nameBytes = Encoding.ASCII.GetBytes(fn);
            Array.Copy(nameBytes, inBuf, Math.Min(31, nameBytes.Length));
            for (int i = 0; i < inArgs.Length; i++)
            {
                BitConverter.GetBytes(inArgs[i]).CopyTo(inBuf, 32 + i * 8);
            }
            byte[] outBuf = new byte[outCount * 8];
            uint returned;
            if (!DeviceIoControl(_hLpc, CTL_EXEC, inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length, out returned, IntPtr.Zero))
            {
                return new long[outCount];
            }
            long[] res = new long[returned / 8];
            for (int i = 0; i < res.Length; i++) res[i] = BitConverter.ToInt64(outBuf, i * 8);
            return res;
        }

        private ulong ReadMsr(uint msr)
        {
            if (_hMsr == null || _hMsr.IsInvalid) return 0;

            byte[] inBuf = new byte[32 + 8];
            Encoding.ASCII.GetBytes("ioctl_read_msr").CopyTo(inBuf, 0);
            BitConverter.GetBytes((long)msr).CopyTo(inBuf, 32);
            byte[] outBuf = new byte[8];
            uint returned;
            if (!DeviceIoControl(_hMsr, CTL_EXEC, inBuf, (uint)inBuf.Length, outBuf, 8, out returned, IntPtr.Zero))
                return 0;

            return BitConverter.ToUInt64(outBuf, 0);
        }

        private void WriteMsr(uint msr, ulong value)
        {
            if (_hMsr == null || _hMsr.IsInvalid) return;

            byte[] inBuf = new byte[32 + 16];
            Encoding.ASCII.GetBytes("ioctl_write_msr").CopyTo(inBuf, 0);
            BitConverter.GetBytes((long)msr).CopyTo(inBuf, 32);
            BitConverter.GetBytes((long)value).CopyTo(inBuf, 40);
            uint returned;
            DeviceIoControl(_hMsr, CTL_EXEC, inBuf, (uint)inBuf.Length, null, 0, out returned, IntPtr.Zero);
        }

        private void WritePort(ushort port, byte val) => ExecLpc("ioctl_pio_outb", new long[] { port, val }, 0);
        private byte ReadPort(ushort port)
        {
            var r = ExecLpc("ioctl_pio_inb", new long[] { port }, 1);
            return r.Length > 0 ? (byte)r[0] : (byte)0;
        }
        private byte ReadByte(byte register)
        {
            var r = ExecLpc("ioctl_superio_inb", new long[] { register }, 1);
            return r.Length > 0 ? (byte)r[0] : (byte)0;
        }
        private void WriteByte(byte register, byte val) => ExecLpc("ioctl_superio_outb", new long[] { register, val }, 0);

        private void SetBank(byte bank)
        {
            ushort idx = (ushort)(_hwmBase + 5);
            ushort data = (ushort)(_hwmBase + 6);
            WritePort(idx, 0x4E);
            byte cur = ReadPort(data);
            WritePort(data, (byte)((cur & 0xF0) | (bank & 0x0F)));
        }

        public byte ReadHwm(byte bank, byte reg)
        {
            ushort idx = (ushort)(_hwmBase + 5);
            ushort data = (ushort)(_hwmBase + 6);
            SetBank(bank);
            WritePort(idx, reg);
            return ReadPort(data);
        }

        public void WriteHwm(byte bank, byte reg, byte val)
        {
            ushort idx = (ushort)(_hwmBase + 5);
            ushort data = (ushort)(_hwmBase + 6);
            SetBank(bank);
            WritePort(idx, reg);
            WritePort(data, val);
        }

        public VoltageRails GetVoltageRails()
        {
            return new VoltageRails
            {
                Vcore = ReadHwm(0, 0x20) * 0.008,
                Vmem = ReadHwm(0, 0x21) * 0.008,
                Avcc = ReadHwm(0, 0x22) * 0.008 * 2.0,
                ThreeVcc = ReadHwm(0, 0x23) * 0.008 * 2.0,
                Vin4 = ReadHwm(0, 0x24) * 0.008,
                Vin6 = ReadHwm(0, 0x26) * 0.008
            };
        }

        public ThermalZones GetThermalZones()
        {
            float cputin = ReadHwm(0, 0x73) + ((ReadHwm(0, 0x74) & 0x80) != 0 ? 0.5f : 0f);
            float auxtin = ReadHwm(0, 0x75) + ((ReadHwm(0, 0x76) & 0x80) != 0 ? 0.5f : 0f);
            float systin = ReadHwm(0, 0x77) + ((ReadHwm(0, 0x78) & 0x80) != 0 ? 0.5f : 0f);

            float core0 = 0;
            if (_isMsrInitialized)
            {
                ulong therm = ReadMsr(0x19C);
                uint digital = (uint)((therm >> 16) & 0x7F);
                core0 = (float)(_tjMax - digital);

                // Thermal Watchdog Check: if core > 85C, trip safety
                if (core0 >= 85.0f && !_watchdogTripped)
                {
                    _watchdogTripped = true;
                    SetFanManual(255);
                    SetPowerLimits(45.0, 56.0);
                }
                else if (core0 < 75.0f && _watchdogTripped)
                {
                    _watchdogTripped = false;
                }
            }

            // Process Autonomous Dynamic Fan Curve if enabled and watchdog not tripped
            if (CurveConfig.IsEnabled && !_watchdogTripped)
            {
                ProcessDynamicCurve(cputin > core0 ? cputin : core0);
            }

            return new ThermalZones
            {
                Core0 = core0,
                Cputin = cputin,
                Systin = systin,
                Auxtin = auxtin
            };
        }

        private void ProcessDynamicCurve(float temp)
        {
            double targetPct;
            if (temp <= CurveConfig.OffTemp)
            {
                targetPct = CurveConfig.MinDuty;
            }
            else if (temp >= CurveConfig.FullTemp)
            {
                targetPct = 100.0;
            }
            else
            {
                double ratio = (temp - CurveConfig.OffTemp) / (CurveConfig.FullTemp - CurveConfig.OffTemp);
                targetPct = CurveConfig.MinDuty + ratio * (100.0 - CurveConfig.MinDuty);
            }

            byte targetPwm = (byte)Math.Round((targetPct * 255.0) / 100.0);
            if (Math.Abs((int)targetPwm - (int)_lastCurvePwm) >= 3)
            {
                SetFanManual(targetPwm);
                _lastCurvePwm = targetPwm;
            }
        }

        public (int rpm, byte pwm, int percent, byte mode) GetFanStatus()
        {
            byte hi = ReadHwm(6, 0x58);
            byte lo = ReadHwm(6, 0x59);
            int rpm = (hi << 8) | lo;

            byte mode = ReadHwm(1, 0x02);
            byte pwm = ReadHwm(1, 0x09);
            int pct = (int)Math.Round((pwm * 100.0) / 255.0);
            return (rpm, pwm, pct, mode);
        }

        public void SetFanManual(byte pwm)
        {
            WriteHwm(1, 0x02, 0x00);
            WriteHwm(1, 0x09, pwm);
        }

        public void SetFanAuto()
        {
            WriteHwm(1, 0x02, 0x40);
        }

        public (double watts, double mhz, double pl1, double pl2) GetCpuTelemetry()
        {
            if (!_isMsrInitialized) return (0, 0, 45, 56);

            ulong curEnergy = ReadMsr(0x611);
            DateTime now = DateTime.UtcNow;
            double elapsed = (now - _lastEnergyTime).TotalSeconds;
            double watts = 0;
            if (elapsed > 0.1)
            {
                double deltaJ = (uint)(curEnergy - _lastEnergy) * _energyUnit;
                watts = deltaJ / elapsed;
                _lastEnergy = curEnergy;
                _lastEnergyTime = now;
            }

            ulong perf = ReadMsr(0x198);
            uint ratio = (uint)((perf >> 8) & 0xFF);
            double mhz = ratio * 100.0;

            ulong pwr = ReadMsr(0x610);
            double pl1 = (pwr & 0x7FFF) / 8.0;
            double pl2 = ((pwr >> 32) & 0x7FFF) / 8.0;

            return (watts, mhz, pl1, pl2);
        }

        public CpuHardwareSpecs GetCpuHardwareSpecs()
        {
            var specs = new CpuHardwareSpecs();
            if (!_isMsrInitialized) return specs;

            ulong turbo = ReadMsr(0x1AD);
            if (turbo > 0)
            {
                specs.Ratio1C = (uint)(turbo & 0xFF);
                specs.Ratio2C = (uint)((turbo >> 8) & 0xFF);
                specs.Ratio3C = (uint)((turbo >> 16) & 0xFF);
                specs.Ratio4C = (uint)((turbo >> 24) & 0xFF);
            }

            ulong uncore = ReadMsr(0x620);
            if (uncore > 0)
            {
                specs.UncoreMax = (uint)(uncore & 0xFF);
                specs.UncoreMin = (uint)((uncore >> 8) & 0xFF);
            }

            ulong tempTarget = ReadMsr(0x1A2);
            if (tempTarget > 0)
            {
                specs.TjMax = (uint)((tempTarget >> 16) & 0xFF);
            }

            ulong msrE2 = ReadMsr(0xE2);
            specs.CfgLock = (msrE2 & 0x8000) != 0;

            ulong msrCE = ReadMsr(0xCE);
            specs.OcCapable = (msrCE & (1UL << 28)) != 0;

            return specs;
        }

        public void SetPowerLimits(double pl1Watts, double pl2Watts)
        {
            if (!_isMsrInitialized) return;

            ulong cur = ReadMsr(0x610);
            ulong pl1Raw = (ulong)Math.Round(pl1Watts * 8.0) & 0x7FFFUL;
            ulong pl2Raw = (ulong)Math.Round(pl2Watts * 8.0) & 0x7FFFUL;

            ulong updated = (cur & ~0x7FFFUL) | pl1Raw;
            updated = (updated & ~(0x7FFFUL << 32)) | (pl2Raw << 32);

            WriteMsr(0x610, updated);
        }

        public static bool IsBootMaxEnabled()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", "/Query /TN NucFanControlMaxAtBoot")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return p?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public static void SetBootMax(bool enable)
        {
            try
            {
                if (enable)
                {
                    Process.Start(new ProcessStartInfo("schtasks", @"/Create /F /TN NucFanControlMaxAtBoot /TR """"C:\Windows\System32
uc-fan-control.exe"" max"" /SC ONSTART /RU SYSTEM /RL HIGHEST") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
                    Process.Start(new ProcessStartInfo("schtasks", @"/Create /F /TN NucFanControlMaxAtLogon /TR """"C:\Windows\System32
uc-fan-control.exe"" max"" /SC ONLOGON /RL HIGHEST") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
                }
                else
                {
                    Process.Start(new ProcessStartInfo("schtasks", "/Delete /TN NucFanControlMaxAtBoot /F") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
                    Process.Start(new ProcessStartInfo("schtasks", "/Delete /TN NucFanControlMaxAtLogon /F") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _hLpc?.Dispose();
            _hMsr?.Dispose();
        }
    }
}
