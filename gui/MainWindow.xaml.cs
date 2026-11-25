using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NucFanControlGUI
{
    public partial class MainWindow : Window
    {
        private readonly FanBackend _backend;
        private readonly DispatcherTimer _timer;
        private bool _isUpdatingUi = true;
        private bool _isStressTesting = false;

        private double _currentPl1 = 50.0;
        private double _currentPl2 = 60.0;

        public MainWindow()
        {
            InitializeComponent();

            _backend = new FanBackend();

            // Check boot persistence
            ChkBootMax.IsChecked = FanBackend.IsBootMaxEnabled();

            // Query initial power limits from MSR
            var cpu = _backend.GetCpuTelemetry();
            if (cpu.pl1 > 0) _currentPl1 = cpu.pl1;
            if (cpu.pl2 > 0) _currentPl2 = cpu.pl2;

            if (SliderPl1 != null) SliderPl1.Value = _currentPl1;
            if (SliderPl2 != null) SliderPl2.Value = _currentPl2;
            if (TxtPl1Val != null) TxtPl1Val.Text = string.Format("{0:F0}W", _currentPl1);
            if (TxtPl2Val != null) TxtPl2Val.Text = string.Format("{0:F0}W", _currentPl2);

            // Populate CPU Hardware specs
            var specs = _backend.GetCpuHardwareSpecs();
            if (TxtRatio1C != null) TxtRatio1C.Text = string.Format("{0}x", specs.Ratio1C);
            if (TxtRatio2C != null) TxtRatio2C.Text = string.Format("{0}x", specs.Ratio2C);
            if (TxtRatio3C != null) TxtRatio3C.Text = string.Format("{0}x", specs.Ratio3C);
            if (TxtRatio4C != null) TxtRatio4C.Text = string.Format("{0}x", specs.Ratio4C);
            if (TxtUncoreSpecs != null)
                TxtUncoreSpecs.Text = string.Format("Ring / Uncore Frequency: {0}00 MHz to {1}00 MHz | TjMax: {2}°C", specs.UncoreMin, specs.UncoreMax, specs.TjMax);
            if (TxtLockSpecs != null)
                TxtLockSpecs.Text = string.Format("Silicon Status: CFG Lock {0} | MSR 0xCE OC Flag {1}", specs.CfgLock ? "LOCKED" : "UNLOCKED", specs.OcCapable ? "ACTIVE" : "INACTIVE");

            // Populate initial curve settings
            if (SliderCurveOff != null) SliderCurveOff.Value = _backend.CurveConfig.OffTemp;
            if (SliderCurveRamp != null) SliderCurveRamp.Value = _backend.CurveConfig.RampTemp;
            if (SliderCurveFull != null) SliderCurveFull.Value = _backend.CurveConfig.FullTemp;
            if (SliderCurveMinDuty != null) SliderCurveMinDuty.Value = _backend.CurveConfig.MinDuty;

            // Populate UEFI Boot Order
            var bootEntries = _backend.GetUefiBootEntries();
            if (ListBootOrder != null)
            {
                ListBootOrder.ItemsSource = bootEntries;
            }

            // Populate Visual BIOS Decoded NVRAM Settings
            var bs = _backend.GetBiosSettings();
            if (TxtBiosFanMode != null) TxtBiosFanMode.Text = string.Format("BIOS Fan Mode: {0}", bs.FanMode);
            if (TxtBiosMinTemp != null) TxtBiosMinTemp.Text = string.Format("BIOS Min Temp: {0}°C", bs.MinTemp);
            if (TxtBiosMinDuty != null) TxtBiosMinDuty.Text = string.Format("BIOS Min Duty: {0}%", bs.MinDuty);
            if (TxtBiosDutyInc != null) TxtBiosDutyInc.Text = string.Format("Duty Increment: {0}%/°C", bs.DutyIncrement);
            if (TxtBiosAperture != null) TxtBiosAperture.Text = string.Format("IGD Aperture: {0}", bs.ApertureSize);
            if (TxtBiosAudio != null) TxtBiosAudio.Text = string.Format("Audio Controller: {0}", bs.AudioEnabled ? "Enabled" : "Disabled");
            if (TxtBiosHdmi != null) TxtBiosHdmi.Text = string.Format("HDMI/DP Audio: {0}", bs.HdmiAudio ? "Enabled" : "Disabled");
            if (TxtBiosFixedDuty != null) TxtBiosFixedDuty.Text = string.Format("Fixed Duty: {0}%", bs.FixedDuty);

            HighlightPowerChip(_currentPl1);
            HighlightGpuPolicy(0);
            HighlightAperture(1);
            HighlightLedColor("Blue");
            HighlightLedPattern("Solid");

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _isUpdatingUi = false;

            RefreshTelemetry();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            RefreshTelemetry();
        }

        private void RefreshTelemetry()
        {
            if (!_backend.IsInitialized)
            {
                if (TxtCputin != null)
                {
                    TxtCputin.Text = "ERR";
                    TxtCputin.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
                return;
            }

            var temps = _backend.GetThermalZones();
            var fan = _backend.GetFanStatus();
            var cpu = _backend.GetCpuTelemetry();
            var rails = _backend.GetVoltageRails();
            var gpu = _backend.GetGpuTelemetry();

            // Update Fan Tachometer
            if (TxtFanRpm != null) TxtFanRpm.Text = string.Format("{0} RPM", fan.rpm);
            if (TxtDuty != null) TxtDuty.Text = string.Format("{0}%", fan.percent);
            if (TxtMode != null)
            {
                if (_backend.CurveConfig.IsEnabled)
                    TxtMode.Text = string.Format("Mode: Autonomous Curve (PWM {0})", fan.pwm);
                else
                    TxtMode.Text = fan.mode == 0x45 ? "Mode: BIOS SmartFan IV" : string.Format("Mode: Manual (PWM {0})", fan.pwm);
            }

            // Update Thermal Zones
            if (TxtCoreTemp != null)
            {
                TxtCoreTemp.Text = string.Format("{0:F1}°C", temps.Core0);
                TxtCoreTemp.Foreground = GetTempBrush(temps.Core0);
            }
            if (TxtCputin != null)
            {
                TxtCputin.Text = string.Format("{0:F1}°C", temps.Cputin);
                TxtCputin.Foreground = GetTempBrush(temps.Cputin);
            }
            if (TxtSystin != null) TxtSystin.Text = string.Format("{0:F1}°C", temps.Systin);
            if (TxtAuxtin != null) TxtAuxtin.Text = string.Format("{0:F1}°C", temps.Auxtin);

            // Update Voltage Rails
            if (TxtVcore != null) TxtVcore.Text = string.Format("VCORE: {0:F3} V", rails.Vcore);
            if (TxtVmem != null) TxtVmem.Text = string.Format("MEM VCC: {0:F3} V", rails.Vmem);
            if (TxtAvcc != null) TxtAvcc.Text = string.Format("AVCC: {0:F3} V", rails.Avcc);
            if (Txt3vcc != null) Txt3vcc.Text = string.Format("3VCC: {0:F3} V", rails.ThreeVcc);
            if (TxtVin4 != null) TxtVin4.Text = string.Format("PCH CORE: {0:F3} V", rails.Vin4);
            if (TxtVin6 != null) TxtVin6.Text = string.Format("STANDBY: {0:F3} V", rails.Vin6);

            // Update CPU Power & Clocks
            if (TxtWatts != null) TxtWatts.Text = string.Format("{0:F1} W", cpu.watts);
            if (TxtPlSummary != null) TxtPlSummary.Text = string.Format("PL1: {0:F0}W | PL2: {1:F0}W", cpu.pl1, cpu.pl2);
            if (TxtClock != null) TxtClock.Text = string.Format("{0:F0} MHz", cpu.mhz);

            // Update iGPU Telemetry
            if (TxtGpuWatts != null) TxtGpuWatts.Text = string.Format("{0:F1} W", gpu.GpuWatts);
            if (TxtGpuPolicy != null)
            {
                string pName = gpu.Policy switch
                {
                    0 => "Policy: 0 (Maximum Performance / Gaming / AI)",
                    13 => "Policy: 13 (Balanced)",
                    24 => "Policy: 24 (Power Saver)",
                    _ => string.Format("Policy: {0}", gpu.Policy)
                };
                TxtGpuPolicy.Text = pName;
            }

            // Update Safety Watchdog Status
            if (TxtWatchdogStatus != null)
            {
                if (_backend.WatchdogTripped)
                {
                    TxtWatchdogStatus.Text = "THERMAL FALLBACK TRIGGERED";
                    TxtWatchdogStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
                else
                {
                    TxtWatchdogStatus.Text = "GUARD ACTIVE";
                    TxtWatchdogStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                }
            }

            // Sync Slider if in manual mode and curve is off
            if (!_isUpdatingUi && !_backend.CurveConfig.IsEnabled && SliderPwm != null && TxtSliderVal != null)
            {
                _isUpdatingUi = true;
                if (fan.mode != 0x45)
                {
                    SliderPwm.Value = fan.percent;
                    TxtSliderVal.Text = string.Format("{0}%", fan.percent);
                }
                _isUpdatingUi = false;
            }
        }

        private SolidColorBrush GetTempBrush(float temp)
        {
            if (temp > 80) return new SolidColorBrush(Color.FromRgb(239, 68, 68));
            if (temp > 65) return new SolidColorBrush(Color.FromRgb(245, 158, 11));
            return new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }

        private void BtnMax_Click(object sender, RoutedEventArgs e)
        {
            if (ChkFanCurve != null) ChkFanCurve.IsChecked = false;
            _backend.CurveConfig.IsEnabled = false;

            _backend.SetFanManual(255);
            _isUpdatingUi = true;
            if (SliderPwm != null) SliderPwm.Value = 100;
            if (TxtSliderVal != null) TxtSliderVal.Text = "100%";
            HighlightFanChip(100);
            _isUpdatingUi = false;
            RefreshTelemetry();
        }

        private void BtnAuto_Click(object sender, RoutedEventArgs e)
        {
            if (ChkFanCurve != null) ChkFanCurve.IsChecked = false;
            _backend.CurveConfig.IsEnabled = false;

            _backend.SetFanAuto();
            ResetFanChips();
            RefreshTelemetry();
        }

        private void FanPreset_Click(object sender, RoutedEventArgs e)
        {
            if (ChkFanCurve != null) ChkFanCurve.IsChecked = false;
            _backend.CurveConfig.IsEnabled = false;

            if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int pct))
            {
                byte pwm = (byte)Math.Round((pct * 255.0) / 100.0);
                _backend.SetFanManual(pwm);
                _isUpdatingUi = true;
                if (SliderPwm != null) SliderPwm.Value = pct;
                if (TxtSliderVal != null) TxtSliderVal.Text = string.Format("{0}%", pct);
                HighlightFanChip(pct);
                _isUpdatingUi = false;
                RefreshTelemetry();
            }
        }

        private void SliderPwm_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || TxtSliderVal == null) return;

            if (ChkFanCurve != null && ChkFanCurve.IsChecked == true)
            {
                ChkFanCurve.IsChecked = false;
                _backend.CurveConfig.IsEnabled = false;
            }

            int pct = (int)Math.Round(e.NewValue);
            TxtSliderVal.Text = string.Format("{0}%", pct);

            byte pwm = (byte)Math.Round((pct * 255.0) / 100.0);
            _backend.SetFanManual(pwm);
            HighlightFanChip(pct);
        }

        private void ChkFanCurve_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            _backend.CurveConfig.IsEnabled = ChkFanCurve.IsChecked == true;
            RefreshTelemetry();
        }

        private void SliderCurve_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi) return;

            if (SliderCurveOff != null && TxtCurveOffVal != null)
            {
                _backend.CurveConfig.OffTemp = Math.Round(SliderCurveOff.Value);
                TxtCurveOffVal.Text = string.Format("{0:F0}°C", _backend.CurveConfig.OffTemp);
            }
            if (SliderCurveRamp != null && TxtCurveRampVal != null)
            {
                _backend.CurveConfig.RampTemp = Math.Round(SliderCurveRamp.Value);
                TxtCurveRampVal.Text = string.Format("{0:F0}°C", _backend.CurveConfig.RampTemp);
            }
            if (SliderCurveFull != null && TxtCurveFullVal != null)
            {
                _backend.CurveConfig.FullTemp = Math.Round(SliderCurveFull.Value);
                TxtCurveFullVal.Text = string.Format("{0:F0}°C", _backend.CurveConfig.FullTemp);
            }
            if (SliderCurveMinDuty != null && TxtCurveMinDutyVal != null)
            {
                _backend.CurveConfig.MinDuty = Math.Round(SliderCurveMinDuty.Value);
                TxtCurveMinDutyVal.Text = string.Format("{0:F0}%", _backend.CurveConfig.MinDuty);
            }
        }

        private void PowerPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, out double pl1))
            {
                double pl2 = pl1 + 10.0;
                _currentPl1 = pl1;
                _currentPl2 = pl2;

                _backend.SetPowerLimits(_currentPl1, _currentPl2);

                _isUpdatingUi = true;
                if (SliderPl1 != null) SliderPl1.Value = _currentPl1;
                if (SliderPl2 != null) SliderPl2.Value = _currentPl2;
                if (TxtPl1Val != null) TxtPl1Val.Text = string.Format("{0:F0}W", _currentPl1);
                if (TxtPl2Val != null) TxtPl2Val.Text = string.Format("{0:F0}W", _currentPl2);
                HighlightPowerChip(_currentPl1);
                _isUpdatingUi = false;

                RefreshTelemetry();
            }
        }

        private void SliderPl1_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || TxtPl1Val == null) return;

            _currentPl1 = Math.Round(e.NewValue);
            TxtPl1Val.Text = string.Format("{0:F0}W", _currentPl1);

            if (_currentPl1 > _currentPl2)
            {
                _currentPl2 = _currentPl1;
                if (SliderPl2 != null) SliderPl2.Value = _currentPl2;
            }

            _backend.SetPowerLimits(_currentPl1, _currentPl2);
            HighlightPowerChip(_currentPl1);
        }

        private void SliderPl2_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || TxtPl2Val == null) return;

            _currentPl2 = Math.Round(e.NewValue);
            TxtPl2Val.Text = string.Format("{0:F0}W", _currentPl2);

            if (_currentPl2 < _currentPl1)
            {
                _currentPl1 = _currentPl2;
                if (SliderPl1 != null) SliderPl1.Value = _currentPl1;
            }

            _backend.SetPowerLimits(_currentPl1, _currentPl2);
        }

        private void GpuPolicy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && uint.TryParse(tagStr, out uint pol))
            {
                _backend.SetGpuPolicy(pol);
                HighlightGpuPolicy(pol);
                RefreshTelemetry();
            }
        }

        private void Aperture_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && uint.TryParse(tagStr, out uint apIdx))
            {
                _backend.SetAperturePreset(apIdx);
                HighlightAperture(apIdx);
                if (TxtActiveAperture != null)
                {
                    TxtActiveAperture.Text = apIdx switch
                    {
                        0 => "128 MB",
                        1 => "256 MB",
                        2 => "512 MB",
                        3 => "1024 MB (1 GB)",
                        _ => "256 MB"
                    };
                }
            }
        }

        private void LedColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string color)
            {
                _backend.SetLedColor(color);
                HighlightLedColor(color);
                UpdateLedStatusText();
            }
        }

        private void LedPattern_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string pattern)
            {
                _backend.SetLedPattern(pattern);
                HighlightLedPattern(pattern);
                UpdateLedStatusText();
            }
        }

        private void UpdateLedStatusText()
        {
            if (TxtActiveLed != null)
            {
                TxtActiveLed.Text = string.Format("{0} • {1}", _backend.LedColorMode, _backend.LedPatternMode);
            }
        }

        private void Peripheral_Changed(object sender, RoutedEventArgs e)
        {
            // Visual toggle handling for CIR, SD, Audio
        }

        private void HighlightPowerChip(double pl1)
        {
            SetChipActive(BtnPwr35, Math.Abs(pl1 - 35.0) < 1.0);
            SetChipActive(BtnPwr45, Math.Abs(pl1 - 45.0) < 1.0);
            SetChipActive(BtnPwr55, Math.Abs(pl1 - 55.0) < 1.0);
            SetChipActive(BtnPwr65, Math.Abs(pl1 - 65.0) < 1.0);
        }

        private void HighlightFanChip(int pct)
        {
            SetChipActive(BtnFan25, pct == 25);
            SetChipActive(BtnFan50, pct == 50);
            SetChipActive(BtnFan75, pct == 75);
            SetChipActive(BtnFan100, pct == 100);
        }

        private void ResetFanChips()
        {
            SetChipActive(BtnFan25, false);
            SetChipActive(BtnFan50, false);
            SetChipActive(BtnFan75, false);
            SetChipActive(BtnFan100, false);
        }

        private void HighlightGpuPolicy(uint pol)
        {
            SetChipActive(BtnGpuMax, pol == 0);
            SetChipActive(BtnGpuBal, pol == 13);
            SetChipActive(BtnGpuEco, pol == 24);
        }

        private void HighlightAperture(uint apIdx)
        {
            SetChipActive(BtnAp128, apIdx == 0);
            SetChipActive(BtnAp256, apIdx == 1);
            SetChipActive(BtnAp512, apIdx == 2);
            SetChipActive(BtnAp1024, apIdx == 3);
        }

        private void HighlightLedColor(string color)
        {
            SetChipActive(BtnLedBlue, color == "Blue");
            SetChipActive(BtnLedAmber, color == "Amber");
            SetChipActive(BtnLedOff, color == "Off");
        }

        private void HighlightLedPattern(string pat)
        {
            SetChipActive(BtnLedSolid, pat == "Solid");
            SetChipActive(BtnLedBlink, pat == "Blink");
            SetChipActive(BtnLedFade, pat == "Fade");
        }

        private void SetChipActive(Button? btn, bool active)
        {
            if (btn == null) return;
            if (active)
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(2, 132, 199)); // Neon Cyan/Blue
                btn.Foreground = new SolidColorBrush(Colors.White);
            }
            else
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)); // Slate
                btn.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            }
        }

        private void ChkBootMax_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            bool enable = ChkBootMax.IsChecked == true;
            FanBackend.SetBootMax(enable);
        }

        private async void BtnStressTest_Click(object sender, RoutedEventArgs e)
        {
            if (_isStressTesting) return;
            _isStressTesting = true;

            BtnStressTest.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // Glowing Crimson
            ProgStress.Visibility = Visibility.Visible;
            ProgStress.Value = 0;
            TxtStressLabel.Text = "TESTING HARDWARE STABILITY...";

            var cts = new CancellationTokenSource();
            var token = cts.Token;

            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                Task.Run(() =>
                {
                    double x = 1.0001;
                    while (!token.IsCancellationRequested)
                    {
                        x = Math.Sin(x) * Math.Cos(x) + Math.Sqrt(x);
                    }
                }, token);
            }

            for (int s = 1; s <= 15; s++)
            {
                await Task.Delay(1000);
                ProgStress.Value = (s * 100.0) / 15.0;
                TxtStressLabel.Text = string.Format("STABILITY TEST ACTIVE ({0}/15s)", s);
            }

            cts.Cancel();
            await Task.Delay(500);

            _isStressTesting = false;
            BtnStressTest.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // Royal Blue
            ProgStress.Visibility = Visibility.Collapsed;
            TxtStressLabel.Text = "RUN 15-SECOND HARDWARE STABILITY TEST";

            RefreshTelemetry();
        }

        private void BtnBootWindows_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Set Windows Boot Manager (Boot0000) as the next boot target and reboot?", "Reboot into Windows", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _backend.SetBootNext(0x0000);
                System.Diagnostics.Process.Start("shutdown", "/r /t 2");
            }
        }

        private void BtnBootMac_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Set Mac OS X OpenCore (Boot0002) as the next boot target and reboot?", "Reboot into macOS", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _backend.SetBootNext(0x0002);
                System.Diagnostics.Process.Start("shutdown", "/r /t 2");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            _backend.Dispose();
            base.OnClosed(e);
        }
    }
}
