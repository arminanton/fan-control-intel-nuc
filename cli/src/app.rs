use crate::nct5577::{FanStatus, Nct5577, Temperatures};
use eframe::egui;
use std::process::Command;
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ControlPreset {
    Auto,
    Max,
    Manual(u8),
}

struct AppState {
    temps: Option<Temperatures>,
    fan: Option<FanStatus>,
    last_update: Option<Instant>,
    current_preset: ControlPreset,
    manual_percent: f32,
    boot_max_enabled: bool,
}

pub struct NucFanApp {
    nuc: Arc<Mutex<Option<Nct5577>>>,
    state: Arc<Mutex<AppState>>,
}

impl NucFanApp {
    pub fn new() -> Self {
        let nuc = match Nct5577::new() {
            Ok(ctrl) => Arc::new(Mutex::new(Some(ctrl))),
            Err(e) => {
                eprintln!("Failed to open NCT5577: {}", e);
                Arc::new(Mutex::new(None))
            }
        };

        let boot_max_enabled = Self::check_boot_max_task();

        let state = Arc::new(Mutex::new(AppState {
            temps: None,
            fan: None,
            last_update: None,
            current_preset: ControlPreset::Max,
            manual_percent: 100.0,
            boot_max_enabled,
        }));

        let nuc_clone = Arc::clone(&nuc);
        let state_clone = Arc::clone(&state);

        thread::spawn(move || {
            loop {
                let (temps, fan) = {
                    let guard = nuc_clone.lock().unwrap();
                    if let Some(ref ctrl) = *guard {
                        (ctrl.get_temperatures().ok(), ctrl.get_fan_status().ok())
                    } else {
                        (None, None)
                    }
                };

                {
                    let mut s = state_clone.lock().unwrap();
                    s.temps = temps;
                    s.fan = fan;
                    s.last_update = Some(Instant::now());

                    if let Some(f) = fan {
                        if f.mode == 0x40 {
                            s.current_preset = ControlPreset::Auto;
                        } else if f.pwm == 255 {
                            s.current_preset = ControlPreset::Max;
                        } else {
                            s.current_preset = ControlPreset::Manual(f.pwm);
                            s.manual_percent = f.percent as f32;
                        }
                    }
                }

                thread::sleep(Duration::from_millis(1000));
            }
        });

        Self { nuc, state }
    }

    fn check_boot_max_task() -> bool {
        let output = Command::new("schtasks")
            .args(&["/Query", "/TN", "NucFanControlMaxAtBoot"])
            .output();
        if let Ok(res) = output {
            res.status.success()
        } else {
            false
        }
    }

    pub fn set_boot_max_task(enable: bool) -> Result<(), String> {
        if enable {
            let status = Command::new("schtasks")
                .args(&[
                    "/Create",
                    "/F",
                    "/TN", "NucFanControlMaxAtBoot",
                    "/TR", r#""C:\Windows\System32
uc-fan-control.exe" max"#,
                    "/SC", "ONSTART",
                    "/RU", "SYSTEM",
                    "/RL", "HIGHEST",
                ])
                .status()
                .map_err(|e| format!("schtasks failed: {}", e))?;

            if !status.success() {
                return Err("Failed to create ONSTART task".to_string());
            }

            let _ = Command::new("schtasks")
                .args(&[
                    "/Create",
                    "/F",
                    "/TN", "NucFanControlMaxAtLogon",
                    "/TR", r#""C:\Windows\System32
uc-fan-control.exe" max"#,
                    "/SC", "ONLOGON",
                    "/RL", "HIGHEST",
                ])
                .status();

            Ok(())
        } else {
            let _ = Command::new("schtasks")
                .args(&["/Delete", "/TN", "NucFanControlMaxAtBoot", "/F"])
                .status();
            let _ = Command::new("schtasks")
                .args(&["/Delete", "/TN", "NucFanControlMaxAtLogon", "/F"])
                .status();
            Ok(())
        }
    }

    fn apply_max(&self) {
        let guard = self.nuc.lock().unwrap();
        if let Some(ref ctrl) = *guard {
            let _ = ctrl.set_fan_manual(255);
        }
        let mut s = self.state.lock().unwrap();
        s.current_preset = ControlPreset::Max;
        s.manual_percent = 100.0;
    }

    fn apply_auto(&self) {
        let guard = self.nuc.lock().unwrap();
        if let Some(ref ctrl) = *guard {
            let _ = ctrl.set_fan_auto();
        }
        let mut s = self.state.lock().unwrap();
        s.current_preset = ControlPreset::Auto;
    }

    fn apply_manual_percent(&self, percent: f32) {
        let pwm = ((percent.clamp(0.0, 100.0) * 255.0 / 100.0).round()) as u8;
        let guard = self.nuc.lock().unwrap();
        if let Some(ref ctrl) = *guard {
            let _ = ctrl.set_fan_manual(pwm);
        }
        let mut s = self.state.lock().unwrap();
        s.current_preset = ControlPreset::Manual(pwm);
        s.manual_percent = percent;
    }
}

impl eframe::App for NucFanApp {
    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        ui.ctx().request_repaint_after(Duration::from_millis(1000));

        let (temps, fan, boot_max, mut manual_pct, current_preset) = {
            let s = self.state.lock().unwrap();
            (s.temps, s.fan, s.boot_max_enabled, s.manual_percent, s.current_preset)
        };

        ui.vertical_centered(|ui| {
            ui.add_space(8.0);
            ui.heading(
                egui::RichText::new("⚡ Intel NUC Skull Canyon Fan Control")
                    .size(20.0)
                    .strong()
                    .color(egui::Color32::from_rgb(0, 180, 255)),
            );
            ui.label(
                egui::RichText::new("NUC6i7KYK • Nuvoton NCT5577D Super I/O")
                    .size(12.0)
                    .color(egui::Color32::GRAY),
            );
        });

        ui.add_space(10.0);
        ui.separator();
        ui.add_space(8.0);

        // Telemetry Cards Row
        ui.columns(3, |cols| {
            // Col 0: CPU Temp
            cols[0].group(|ui| {
                ui.vertical_centered(|ui| {
                    ui.label(egui::RichText::new("CPU TEMPERATURE").size(11.0).color(egui::Color32::LIGHT_GRAY));
                    if let Some(t) = temps {
                        let color = if t.cputin > 80.0 {
                            egui::Color32::from_rgb(255, 70, 70)
                        } else if t.cputin > 65.0 {
                            egui::Color32::from_rgb(255, 170, 0)
                        } else {
                            egui::Color32::from_rgb(0, 230, 120)
                        };
                        ui.label(egui::RichText::new(format!("{:.1}°C", t.cputin)).size(24.0).strong().color(color));
                    } else {
                        ui.label(egui::RichText::new("--.-°C").size(24.0).color(egui::Color32::GRAY));
                    }
                });
            });

            // Col 1: Fan RPM
            cols[1].group(|ui| {
                ui.vertical_centered(|ui| {
                    ui.label(egui::RichText::new("FAN SPEED").size(11.0).color(egui::Color32::LIGHT_GRAY));
                    if let Some(f) = fan {
                        ui.label(egui::RichText::new(format!("{} RPM", f.rpm)).size(24.0).strong().color(egui::Color32::from_rgb(0, 200, 255)));
                    } else {
                        ui.label(egui::RichText::new("---- RPM").size(24.0).color(egui::Color32::GRAY));
                    }
                });
            });

            // Col 2: Fan Duty
            cols[2].group(|ui| {
                ui.vertical_centered(|ui| {
                    ui.label(egui::RichText::new("DUTY CYCLE").size(11.0).color(egui::Color32::LIGHT_GRAY));
                    if let Some(f) = fan {
                        let mode_str = if f.mode == 0x40 { "BIOS Auto" } else { "Manual" };
                        ui.label(egui::RichText::new(format!("{}%", f.percent)).size(24.0).strong().color(egui::Color32::WHITE));
                        ui.label(egui::RichText::new(format!("PWM: {} ({})", f.pwm, mode_str)).size(10.0).color(egui::Color32::GRAY));
                    } else {
                        ui.label(egui::RichText::new("--%").size(24.0).color(egui::Color32::GRAY));
                    }
                });
            });
        });

        ui.add_space(12.0);

        // Big Action Buttons
        ui.group(|ui| {
            ui.label(egui::RichText::new("COOLING PRESETS").strong().size(13.0));
            ui.add_space(6.0);

            ui.horizontal(|ui| {
                let is_max = current_preset == ControlPreset::Max;
                let max_btn = egui::Button::new(
                    egui::RichText::new("🚀 MAXIMUM (100% Full Tilt)")
                        .size(13.0)
                        .strong()
                        .color(if is_max { egui::Color32::BLACK } else { egui::Color32::WHITE }),
                )
                .fill(if is_max {
                    egui::Color32::from_rgb(255, 60, 60)
                } else {
                    egui::Color32::from_rgb(180, 40, 40)
                })
                .min_size(egui::vec2(190.0, 36.0));

                if ui.add(max_btn).clicked() {
                    self.apply_max();
                }

                ui.add_space(10.0);

                let is_auto = current_preset == ControlPreset::Auto;
                let auto_btn = egui::Button::new(
                    egui::RichText::new("⚙ BIOS Auto (SmartFan)")
                        .size(13.0)
                        .color(if is_auto { egui::Color32::BLACK } else { egui::Color32::WHITE }),
                )
                .fill(if is_auto {
                    egui::Color32::from_rgb(100, 200, 100)
                } else {
                    egui::Color32::from_rgb(50, 100, 50)
                })
                .min_size(egui::vec2(160.0, 36.0));

                if ui.add(auto_btn).clicked() {
                    self.apply_auto();
                }
            });

            ui.add_space(8.0);
            ui.horizontal(|ui| {
                if ui.button("60% Quiet").clicked() {
                    self.apply_manual_percent(60.0);
                }
                if ui.button("75% Cool").clicked() {
                    self.apply_manual_percent(75.0);
                }
                if ui.button("85% Aggressive").clicked() {
                    self.apply_manual_percent(85.0);
                }
                if ui.button("95% Extreme").clicked() {
                    self.apply_manual_percent(95.0);
                }
            });

            ui.add_space(10.0);
            ui.label(egui::RichText::new("Custom Fan Speed Slider:").size(12.0));
            let slider = ui.add(egui::Slider::new(&mut manual_pct, 10.0..=100.0).text("%").step_by(1.0));
            if slider.changed() {
                self.apply_manual_percent(manual_pct);
            }
        });

        ui.add_space(10.0);

        // Startup / Init / Boot Options
        ui.group(|ui| {
            ui.label(egui::RichText::new("STARTUP & PERSISTENCE").strong().size(13.0));
            ui.add_space(4.0);

            let mut boot_val = boot_max;
            if ui.checkbox(&mut boot_val, "Set 100% MAXIMUM fan speed as default at system boot & logon").changed() {
                let res = Self::set_boot_max_task(boot_val);
                let mut s = self.state.lock().unwrap();
                if res.is_ok() {
                    s.boot_max_enabled = boot_val;
                }
            }
            ui.label(
                egui::RichText::new("Ensures hardware fans immediately over-rotate to 100% upon boot/init/reboot to keep the NUC cooled.")
                    .size(11.0)
                    .color(egui::Color32::GRAY),
            );
        });

        ui.add_space(6.0);
        ui.horizontal(|ui| {
            ui.label(egui::RichText::new("System Board:").size(11.0).color(egui::Color32::GRAY));
            if let Some(t) = temps {
                ui.label(egui::RichText::new(format!("SYSTIN: {:.1}°C | AUXTIN: {:.1}°C", t.systin, t.auxtin)).size(11.0).color(egui::Color32::LIGHT_GRAY));
            }
        });
    }
}
