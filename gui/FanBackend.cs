using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NucFanControlGUI
{
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

        private SafeFileHandle _handle;
        private ushort _hwmBase = 0x0A00;
        private bool _isInitialized = false;

        public bool IsInitialized => _isInitialized;

        public FanBackend()
        {
            Initialize();
        }

        private void Initialize()
        {
            var logPath = @"C:\ProgramData\fan_debug.log";
            try
            {
                File.WriteAllText(logPath, "Starting Initialize as " + Environment.UserName + "\n");

                _handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO",
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);

                int err1 = Marshal.GetLastWin32Error();
                File.AppendAllText(logPath, "CreateFile handle invalid: " + _handle.IsInvalid + " Win32Err: " + err1 + "\n");
                if (_handle.IsInvalid) return;

                if (!File.Exists(@"C:\ProgramData\LpcIO.bin"))
                {
                    File.AppendAllText(logPath, "LpcIO.bin does NOT exist!\n");
                    return;
                }

                byte[] bin = File.ReadAllBytes(@"C:\ProgramData\LpcIO.bin");
                File.AppendAllText(logPath, "LpcIO.bin size: " + bin.Length + "\n");

                uint returned;
                if (!DeviceIoControl(_handle, CTL_LOAD, bin, (uint)bin.Length, null, 0, out returned, IntPtr.Zero))
                {
                    int err2 = Marshal.GetLastWin32Error();
                    File.AppendAllText(logPath, "DeviceIoControl CTL_LOAD failed! Win32Err: " + err2 + "\n");
                    return;
                }
                File.AppendAllText(logPath, "CTL_LOAD success\n");

                Exec("ioctl_select_slot", new long[] { 0 }, 0);
                File.AppendAllText(logPath, "select_slot 0 done\n");

                // Enter SIO config (0x87, 0x87)
                WritePort(0x2E, 0x87);
                WritePort(0x2E, 0x87);

                byte id = ReadByte(0x20);
                File.AppendAllText(logPath, "Read SIO ID: 0x" + id.ToString("X2") + "\n");
                if (id != 0xC3)
                {
                    WritePort(0x2E, 0xAA);
                    File.AppendAllText(logPath, "ID is not 0xC3, exiting\n");
                    return;
                }

                Exec("ioctl_find_bars", new long[0], 0);
                File.AppendAllText(logPath, "find_bars done\n");

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
                File.AppendAllText(logPath, "hwmBase: 0x" + _hwmBase.ToString("X4") + "\n");

                WritePort(0x2E, 0xAA);
                _isInitialized = true;
                File.AppendAllText(logPath, "Successfully initialized!\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, "Exception in Initialize: " + ex.ToString() + "\n");
                _isInitialized = false;
            }
        }

        private long[] Exec(string fn, long[] inArgs, int outCount)
        {
            if (_handle == null || _handle.IsInvalid) return new long[outCount];

            byte[] inBuf = new byte[32 + inArgs.Length * 8];
            byte[] nameBytes = Encoding.ASCII.GetBytes(fn);
            Array.Copy(nameBytes, inBuf, Math.Min(31, nameBytes.Length));
            for (int i = 0; i < inArgs.Length; i++)
            {
                BitConverter.GetBytes(inArgs[i]).CopyTo(inBuf, 32 + i * 8);
            }
            byte[] outBuf = new byte[outCount * 8];
            uint returned;
            if (!DeviceIoControl(_handle, CTL_EXEC, inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length, out returned, IntPtr.Zero))
            {
                return new long[outCount];
            }
            long[] res = new long[returned / 8];
            for (int i = 0; i < res.Length; i++) res[i] = BitConverter.ToInt64(outBuf, i * 8);
            return res;
        }

        private void WritePort(ushort port, byte val) => Exec("ioctl_pio_outb", new long[] { port, val }, 0);
        private byte ReadPort(ushort port)
        {
            var r = Exec("ioctl_pio_inb", new long[] { port }, 1);
            return r.Length > 0 ? (byte)r[0] : (byte)0;
        }
        private byte ReadByte(byte register)
        {
            var r = Exec("ioctl_superio_inb", new long[] { register }, 1);
            return r.Length > 0 ? (byte)r[0] : (byte)0;
        }
        private void WriteByte(byte register, byte val) => Exec("ioctl_superio_outb", new long[] { register, val }, 0);

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

        public (float cpuTemp, float sysTemp, float auxTemp) GetTemperatures()
        {
            float cpu = ReadHwm(0, 0x73) + ((ReadHwm(0, 0x74) & 0x80) != 0 ? 0.5f : 0f);
            float aux = ReadHwm(0, 0x75) + ((ReadHwm(0, 0x76) & 0x80) != 0 ? 0.5f : 0f);
            float sys = ReadHwm(0, 0x77) + ((ReadHwm(0, 0x78) & 0x80) != 0 ? 0.5f : 0f);
            return (cpu, sys, aux);
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
            WriteHwm(1, 0x02, 0x00); // Manual Mode
            WriteHwm(1, 0x09, pwm);
        }

        public void SetFanAuto()
        {
            WriteHwm(1, 0x02, 0x40); // BIOS SmartFan IV
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
                p.WaitForExit();
                return p.ExitCode == 0;
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
            _handle?.Dispose();
        }
    }
}
