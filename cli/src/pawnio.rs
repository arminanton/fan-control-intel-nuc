
use std::ffi::c_void;
use std::io;
use std::ptr;

type HANDLE = *mut c_void;
type BOOL = i32;
type DWORD = u32;

const GENERIC_READ: DWORD = 0x8000_0000;
const GENERIC_WRITE: DWORD = 0x4000_0000;
const FILE_SHARE_READ: DWORD = 1;
const FILE_SHARE_WRITE: DWORD = 2;
const OPEN_EXISTING: DWORD = 3;
const FILE_ATTRIBUTE_NORMAL: DWORD = 0x80;
const INVALID_HANDLE_VALUE: HANDLE = -1isize as HANDLE;

const DEVICE_TYPE: DWORD = 41394 << 16;
const IOCTL_PIO_LOAD_BINARY: DWORD = 0x821 << 2;
const IOCTL_PIO_EXECUTE_FN: DWORD = 0x841 << 2;
const CTL_LOAD: DWORD = DEVICE_TYPE | IOCTL_PIO_LOAD_BINARY;
const CTL_EXEC: DWORD = DEVICE_TYPE | IOCTL_PIO_EXECUTE_FN;

unsafe extern "system" {
    fn CreateFileW(
        lpFileName: *const u16,
        dwDesiredAccess: DWORD,
        dwShareMode: DWORD,
        lpSecurityAttributes: *mut c_void,
        dwCreationDisposition: DWORD,
        dwFlagsAndAttributes: DWORD,
        hTemplateFile: HANDLE,
    ) -> HANDLE;

    fn DeviceIoControl(
        hDevice: HANDLE,
        dwIoControlCode: DWORD,
        lpInBuffer: *const c_void,
        nInBufferSize: DWORD,
        lpOutBuffer: *mut c_void,
        nOutBufferSize: DWORD,
        lpBytesReturned: *mut DWORD,
        lpOverlapped: *mut c_void,
    ) -> BOOL;

    fn CloseHandle(hObject: HANDLE) -> BOOL;
    fn GetLastError() -> DWORD;
}

pub struct PawnIo {
    handle: HANDLE,
}

unsafe impl Send for PawnIo {}
unsafe impl Sync for PawnIo {}

impl PawnIo {
    pub fn open() -> io::Result<Self> {
        let path: Vec<u16> = "\\\\?\\GLOBALROOT\\Device\\PawnIO"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();

        let handle = unsafe {
            CreateFileW(
                path.as_ptr(),
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                ptr::null_mut(),
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                ptr::null_mut(),
            )
        };

        if handle == INVALID_HANDLE_VALUE || handle.is_null() {
            let err = unsafe { GetLastError() };
            return Err(io::Error::from_raw_os_error(err as i32));
        }

        let mut pawn = Self { handle };
        pawn.load_binary(include_bytes!("../assets/LpcIO.bin"))?;
        Ok(pawn)
    }

    pub fn load_binary(&mut self, binary: &[u8]) -> io::Result<()> {
        let mut returned: DWORD = 0;
        let ok = unsafe {
            DeviceIoControl(
                self.handle,
                CTL_LOAD,
                binary.as_ptr() as *const c_void,
                binary.len() as DWORD,
                ptr::null_mut(),
                0,
                &mut returned,
                ptr::null_mut(),
            )
        };

        if ok == 0 {
            let err = unsafe { GetLastError() };
            return Err(io::Error::from_raw_os_error(err as i32));
        }
        Ok(())
    }

    pub fn exec(&self, fn_name: &str, in_args: &[i64], out_count: usize) -> io::Result<Vec<i64>> {
        let mut in_buf = vec![0u8; 32 + in_args.len() * 8];
        let name_bytes = fn_name.as_bytes();
        let copy_len = name_bytes.len().min(31);
        in_buf[..copy_len].copy_from_slice(&name_bytes[..copy_len]);

        for (i, &arg) in in_args.iter().enumerate() {
            let bytes = arg.to_le_bytes();
            let start = 32 + i * 8;
            in_buf[start..start + 8].copy_from_slice(&bytes);
        }

        let mut out_buf = vec![0u8; out_count * 8];
        let mut returned: DWORD = 0;

        let ok = unsafe {
            DeviceIoControl(
                self.handle,
                CTL_EXEC,
                in_buf.as_ptr() as *const c_void,
                in_buf.len() as DWORD,
                out_buf.as_mut_ptr() as *mut c_void,
                out_buf.len() as DWORD,
                &mut returned,
                ptr::null_mut(),
            )
        };

        if ok == 0 {
            let err = unsafe { GetLastError() };
            return Err(io::Error::from_raw_os_error(err as i32));
        }

        let entries = (returned as usize) / 8;
        let mut result = Vec::with_capacity(entries);
        for i in 0..entries {
            let mut chunk = [0u8; 8];
            chunk.copy_from_slice(&out_buf[i * 8..(i + 1) * 8]);
            result.push(i64::from_le_bytes(chunk));
        }

        Ok(result)
    }

    pub fn write_port(&self, port: u16, val: u8) -> io::Result<()> {
        self.exec("ioctl_pio_outb", &[port as i64, val as i64], 0)?;
        Ok(())
    }

    pub fn read_port(&self, port: u16) -> io::Result<u8> {
        let res = self.exec("ioctl_pio_inb", &[port as i64], 1)?;
        res.first()
            .map(|&v| v as u8)
            .ok_or_else(|| io::Error::new(io::ErrorKind::UnexpectedEof, "No data returned"))
    }

    pub fn read_superio_byte(&self, reg: u8) -> io::Result<u8> {
        let res = self.exec("ioctl_superio_inb", &[reg as i64], 1)?;
        res.first()
            .map(|&v| v as u8)
            .ok_or_else(|| io::Error::new(io::ErrorKind::UnexpectedEof, "No data returned"))
    }

    pub fn write_superio_byte(&self, reg: u8, val: u8) -> io::Result<()> {
        self.exec("ioctl_superio_outb", &[reg as i64, val as i64], 0)?;
        Ok(())
    }

    pub fn select_slot(&self, slot: i64) -> io::Result<()> {
        self.exec("ioctl_select_slot", &[slot], 0)?;
        Ok(())
    }

    pub fn find_bars(&self) -> io::Result<()> {
        self.exec("ioctl_find_bars", &[], 0)?;
        Ok(())
    }
}

impl Drop for PawnIo {
    fn drop(&mut self) {
        if self.handle != INVALID_HANDLE_VALUE && !self.handle.is_null() {
            unsafe {
                CloseHandle(self.handle);
            }
        }
    }
}
