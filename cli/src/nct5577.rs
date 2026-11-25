
use crate::pawnio::PawnIo;
use std::io;

pub const SIO_CONFIG_PORT: u16 = 0x2E;
pub const SIO_DATA_PORT: u16 = 0x2F;

#[derive(Debug, Clone, Copy)]
pub struct Temperatures {
    pub cputin: f32, // CPU socket temperature
    pub systin: f32, // System / Motherboard temperature
    pub auxtin: f32, // Auxiliary temperature
}

#[derive(Debug, Clone, Copy)]
pub struct FanStatus {
    pub rpm: u16,
    pub pwm: u8,
    pub percent: u8,
    pub mode: u8, // 0x00 = Manual, 0x40 = SmartFan (BIOS automatic)
}

pub struct Nct5577 {
    io: PawnIo,
    hwm_base: u16,
}

impl Nct5577 {
    pub fn new() -> io::Result<Self> {
        let io = PawnIo::open()?;
        io.select_slot(0)?;

        // Enter Super I/O config mode
        io.write_port(SIO_CONFIG_PORT, 0x87)?;
        io.write_port(SIO_CONFIG_PORT, 0x87)?;

        let chip_hi = io.read_superio_byte(0x20)?;
        let chip_lo = io.read_superio_byte(0x21)?;
        let chip_id = ((chip_hi as u16) << 8) | (chip_lo as u16);

        if chip_hi != 0xC3 {
            // Exit config before erroring out
            let _ = io.write_port(SIO_CONFIG_PORT, 0xAA);
            return Err(io::Error::new(
                io::ErrorKind::NotFound,
                format!("Unexpected Super I/O Chip ID: 0x{:04X} (expected 0xC3xx for NCT5577/NCT6776)", chip_id),
            ));
        }

        // Register BARs with PawnIO driver while in config mode
        io.find_bars()?;

        // Select Logical Device Number 0x0B (Hardware Monitor)
        io.write_superio_byte(0x07, 0x0B)?;

        // Check and unlock CR 0x28 (I/O space lock)
        let cr28 = io.read_superio_byte(0x28)?;
        if (cr28 & 0x10) != 0 {
            io.write_superio_byte(0x28, cr28 & !0x10)?;
        }

        // Read HWM Base Address from CR 0x60/0x61
        let base_hi = io.read_superio_byte(0x60)?;
        let base_lo = io.read_superio_byte(0x61)?;
        let hwm_base = ((base_hi as u16) << 8) | (base_lo as u16);

        // Exit config mode
        io.write_port(SIO_CONFIG_PORT, 0xAA)?;

        if hwm_base == 0 || hwm_base == 0xFFFF {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("Invalid HWM Base Address: 0x{:04X}", hwm_base),
            ));
        }

        Ok(Self { io, hwm_base })
    }

    #[inline]
    pub fn read_hwm(&self, bank: u8, reg: u8) -> io::Result<u8> {
        let idx = self.hwm_base + 5;
        let data = self.hwm_base + 6;

        self.io.write_port(idx, 0x4E)?;
        let cur = self.io.read_port(data)?;
        self.io.write_port(data, (cur & 0xF0) | (bank & 0x0F))?;

        self.io.write_port(idx, reg)?;
        self.io.read_port(data)
    }

    #[inline]
    pub fn write_hwm(&self, bank: u8, reg: u8, val: u8) -> io::Result<()> {
        let idx = self.hwm_base + 5;
        let data = self.hwm_base + 6;

        self.io.write_port(idx, 0x4E)?;
        let cur = self.io.read_port(data)?;
        self.io.write_port(data, (cur & 0xF0) | (bank & 0x0F))?;

        self.io.write_port(idx, reg)?;
        self.io.write_port(data, val)?;
        Ok(())
    }

    pub fn get_temperatures(&self) -> io::Result<Temperatures> {
        let cpu_int = self.read_hwm(0, 0x73)? as f32;
        let cpu_frac = if (self.read_hwm(0, 0x74)? & 0x80) != 0 { 0.5 } else { 0.0 };

        let aux_int = self.read_hwm(0, 0x75)? as f32;
        let aux_frac = if (self.read_hwm(0, 0x76)? & 0x80) != 0 { 0.5 } else { 0.0 };

        let sys_int = self.read_hwm(0, 0x77)? as f32;
        let sys_frac = if (self.read_hwm(0, 0x78)? & 0x80) != 0 { 0.5 } else { 0.0 };

        Ok(Temperatures {
            cputin: cpu_int + cpu_frac,
            auxtin: aux_int + aux_frac,
            systin: sys_int + sys_frac,
        })
    }

    pub fn get_fan_status(&self) -> io::Result<FanStatus> {
        let rpm_hi = self.read_hwm(6, 0x58)? as u16;
        let rpm_lo = self.read_hwm(6, 0x59)? as u16;
        let rpm = (rpm_hi << 8) | rpm_lo;

        // Bank 2 is physical CPU fan header
        let mode = self.read_hwm(2, 0x02)?;
        let pwm = self.read_hwm(2, 0x09)?;
        let percent = ((pwm as u32 * 100 + 127) / 255) as u8;

        Ok(FanStatus {
            rpm,
            pwm,
            percent,
            mode,
        })
    }

    pub fn set_fan_manual(&self, pwm: u8) -> io::Result<()> {
        // Synchronize across Banks 1, 2, and 3
        for b in 1..=3 {
            self.write_hwm(b, 0x02, 0x00)?;
            self.write_hwm(b, 0x09, pwm)?;
        }
        Ok(())
    }

    pub fn set_fan_auto(&self) -> io::Result<()> {
        for b in 1..=3 {
            self.write_hwm(b, 0x02, 0x45)?;
        }
        Ok(())
    }
}
