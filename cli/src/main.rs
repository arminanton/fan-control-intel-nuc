mod app;
mod nct5577;
mod pawnio;

use app::NucFanApp;
use eframe::egui;
use nct5577::Nct5577;
use std::env;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::Duration;

static RUNNING: AtomicBool = AtomicBool::new(true);

unsafe extern "system" {
    fn SetConsoleCtrlHandler(
        handler: Option<unsafe extern "system" fn(u32) -> i32>,
        add: i32,
    ) -> i32;
}

unsafe extern "system" fn ctrl_handler(_ctrl_type: u32) -> i32 {
    RUNNING.store(false, Ordering::SeqCst);
    1
}

fn print_usage() {
    println!(
        r#"Intel NUC Fan Controller (NCT5577D / NCT6776F Native Rust)

USAGE:
    nuc-fan-control [SUBCOMMAND]

SUBCOMMANDS:
    gui                      Launch the graphical user interface (default if no arguments)
    status                   Show current temperatures, fan RPM, duty cycle, and mode
    set <PERCENT% | VALUE>   Set manual fan speed (e.g., 'nuc-fan-control set 80%' or 'set 200')
    max                      Crank fan to 100% full tilt (over-rotate / maximum cooling)
    auto                     Restore BIOS automatic SmartFan curve
    enable-boot-max          Configure 100% max fan speed at every system boot and user logon
    disable-boot-max         Remove the boot/logon max fan task
    monitor [INTERVAL_MS]    Continuously stream live temperature and fan telemetry (default 1000ms)
    curve                    Run dynamic fan curve service (safely restores auto on exit)

EXAMPLES:
    nuc-fan-control gui
    nuc-fan-control max
    nuc-fan-control enable-boot-max
    nuc-fan-control status
"#
    );
}

fn print_status(nuc: &Nct5577) {
    match (nuc.get_temperatures(), nuc.get_fan_status()) {
        (Ok(temps), Ok(fan)) => {
            let mode_str = match fan.mode {
                0x00 => "Manual (Software Overridden)",
                0x40 => "Automatic (BIOS SmartFan IV)",
                _other => "Other",
            };
            println!("======================================================");
            println!(" Intel NUC NUC6i7KYK (Skull Canyon) Fan Controller    ");
            println!(" Super I/O: Nuvoton NCT5577D / NCT6776F               ");
            println!("======================================================");
            println!(" CPU Socket Temp (CPUTIN): {:.1}°C", temps.cputin);
            println!(" System Temp     (SYSTIN): {:.1}°C", temps.systin);
            println!(" Aux Temp        (AUXTIN): {:.1}°C", temps.auxtin);
            println!("------------------------------------------------------");
            println!(" CPU Fan Speed           : {} RPM", fan.rpm);
            println!(" Fan Duty Cycle          : {}% (raw PWM: {} / 255)", fan.percent, fan.pwm);
            println!(" Control Mode            : 0x{:02X} -> {}", fan.mode, mode_str);
            println!("======================================================");
        }
        (Err(e), _) | (_, Err(e)) => {
            eprintln!("Error querying telemetry: {}", e);
        }
    }
}

fn parse_pwm(input: &str) -> Result<u8, String> {
    let s = input.trim();
    if s.ends_with('%') {
        let pct_str = &s[..s.len() - 1].trim();
        let pct: f64 = pct_str
            .parse()
            .map_err(|_| format!("Invalid percentage '{}'", s))?;
        if pct < 0.0 || pct > 100.0 {
            return Err("Percentage must be between 0% and 100%".to_string());
        }
        let pwm = (pct * 255.0 / 100.0).round() as u8;
        Ok(pwm)
    } else {
        let val: u32 = s.parse().map_err(|_| format!("Invalid PWM value '{}'", s))?;
        if val > 255 {
            return Err("Raw PWM value must be between 0 and 255".to_string());
        }
        Ok(val as u8)
    }
}

fn run_gui() -> eframe::Result<()> {
    let mut native_options = eframe::NativeOptions::default();
    native_options.viewport = egui::ViewportBuilder::default()
        .with_inner_size([460.0, 520.0])
        .with_min_inner_size([420.0, 480.0])
        .with_title("Intel NUC Skull Canyon Fan Control");

    eframe::run_native(
        "Intel NUC Fan Control",
        native_options,
        Box::new(|_cc| Ok(Box::new(NucFanApp::new()))),
    )
}

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 2 {
        if let Err(e) = run_gui() {
            eprintln!("Failed to launch GUI: {}", e);
        }
        return;
    }

    let subcmd = args[1].to_lowercase();
    if subcmd == "gui" {
        if let Err(e) = run_gui() {
            eprintln!("Failed to launch GUI: {}", e);
        }
        return;
    }

    let nuc = match Nct5577::new() {
        Ok(n) => n,
        Err(e) => {
            eprintln!("Failed to initialize Nuvoton NCT5577D controller: {}", e);
            eprintln!("Ensure PawnIO driver is installed and running ('sc query PawnIO').");
            std::process::exit(1);
        }
    };

    match subcmd.as_str() {
        "status" => {
            print_status(&nuc);
        }
        "set" => {
            if args.len() < 3 {
                eprintln!("Error: missing value. Usage: nuc-fan-control set <PERCENT% | VALUE>");
                std::process::exit(1);
            }
            let pwm = match parse_pwm(&args[2]) {
                Ok(p) => p,
                Err(err) => {
                    eprintln!("Error: {}", err);
                    std::process::exit(1);
                }
            };
            println!("Applying manual fan speed: raw PWM {} ({}%)...", pwm, ((pwm as u32 * 100 + 127) / 255));
            if let Err(e) = nuc.set_fan_manual(pwm) {
                eprintln!("Failed to set fan speed: {}", e);
                std::process::exit(1);
            }
            thread::sleep(Duration::from_millis(150));
            print_status(&nuc);
        }
        "max" | "turbo" | "over-rotate" => {
            println!("Engaging MAXIMUM fan cooling (100% PWM over-rotation)...");
            if let Err(e) = nuc.set_fan_manual(255) {
                eprintln!("Failed to engage max fan speed: {}", e);
                std::process::exit(1);
            }
            thread::sleep(Duration::from_millis(200));
            print_status(&nuc);
        }
        "auto" => {
            println!("Restoring BIOS automatic SmartFan control...");
            if let Err(e) = nuc.set_fan_auto() {
                eprintln!("Failed to restore automatic fan speed: {}", e);
                std::process::exit(1);
            }
            thread::sleep(Duration::from_millis(200));
            print_status(&nuc);
        }
        "enable-boot-max" => {
            println!("Configuring 100% MAX fan speed at system boot & logon...");
            match NucFanApp::set_boot_max_task(true) {
                Ok(_) => {
                    println!("Successfully created startup and logon scheduled tasks.");
                    let _ = nuc.set_fan_manual(255);
                    print_status(&nuc);
                }
                Err(e) => {
                    eprintln!("Failed to configure boot task: {}", e);
                    std::process::exit(1);
                }
            }
        }
        "disable-boot-max" => {
            println!("Removing boot/logon max fan tasks...");
            match NucFanApp::set_boot_max_task(false) {
                Ok(_) => println!("Successfully disabled boot/logon max fan tasks."),
                Err(e) => eprintln!("Failed to remove tasks: {}", e),
            }
        }
        "monitor" => {
            let interval_ms = args
                .get(2)
                .and_then(|s| s.parse::<u64>().ok())
                .unwrap_or(1000);

            println!("Streaming live fan and thermal telemetry every {}ms (Press Ctrl+C to exit)...", interval_ms);
            println!("Time     | CPU Temp | System | Fan RPM | PWM Duty | Mode");
            println!("---------+----------+--------+---------+----------+-----------------");

            unsafe {
                SetConsoleCtrlHandler(Some(ctrl_handler), 1);
            }

            while RUNNING.load(Ordering::SeqCst) {
                let now = chrono_or_time_str();
                if let (Ok(temps), Ok(fan)) = (nuc.get_temperatures(), nuc.get_fan_status()) {
                    let mode_tag = if fan.mode == 0x00 { "Manual" } else { "BIOS Auto" };
                    println!(
                        "{} | {:>6.1}°C | {:>4.1}°C | {:>5}   | {:>3}% ({:>3})| {}",
                        now, temps.cputin, temps.systin, fan.rpm, fan.percent, fan.pwm, mode_tag
                    );
                }
                thread::sleep(Duration::from_millis(interval_ms));
            }
            println!("Exiting monitor.");
        }
        "curve" => {
            println!("Starting dynamic fan curve daemon (Press Ctrl+C to exit)...");
            println!("Curve targets: <=50°C -> 40%, 65°C -> 70%, >=80°C -> 100%");

            unsafe {
                SetConsoleCtrlHandler(Some(ctrl_handler), 1);
            }

            let mut last_pwm: u8 = 0;

            while RUNNING.load(Ordering::SeqCst) {
                if let (Ok(temps), Ok(fan)) = (nuc.get_temperatures(), nuc.get_fan_status()) {
                    let temp = temps.cputin;
                    let target_pwm = if temp <= 50.0 {
                        102u8
                    } else if temp >= 80.0 {
                        255u8
                    } else {
                        let ratio = (temp - 50.0) / (80.0 - 50.0);
                        (102.0 + ratio * (255.0 - 102.0)).round() as u8
                    };

                    if (target_pwm as i16 - last_pwm as i16).abs() >= 3 {
                        let _ = nuc.set_fan_manual(target_pwm);
                        last_pwm = target_pwm;
                    }

                    println!(
                        "[{}] Temp: {:.1}°C | Target: {}% ({}) | RPM: {} | Mode: Manual",
                        chrono_or_time_str(), temp, ((last_pwm as u32 * 100 + 127) / 255), last_pwm, fan.rpm
                    );
                }
                thread::sleep(Duration::from_millis(1500));
            }

            println!("Restoring BIOS automatic SmartFan curve upon shutdown...");
            let _ = nuc.set_fan_auto();
            println!("Graceful shutdown complete.");
        }
        "help" | "-h" | "--help" => {
            print_usage();
        }
        unknown => {
            eprintln!("Unknown subcommand: '{}'", unknown);
            print_usage();
            std::process::exit(1);
        }
    }
}

fn chrono_or_time_str() -> String {
    let now = std::time::SystemTime::now();
    let since_the_epoch = now.duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
    let secs = since_the_epoch.as_secs() % 86400;
    let hours = secs / 3600;
    let mins = (secs % 3600) / 60;
    let s = secs % 60;
    format!("{:02}:{:02}:{:02}", hours, mins, s)
}
