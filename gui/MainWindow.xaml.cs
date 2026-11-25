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
            _isUpdatingUi = false;
            RefreshTelemetry();
        }

        private void BtnAuto_Click(object sender, RoutedEventArgs e)
        {
            if (ChkFanCurve != null) ChkFanCurve.IsChecked = false;
            _backend.CurveConfig.IsEnabled = false;

            _backend.SetFanAuto();
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
                RefreshTelemetry();
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

            BtnStressTest.IsEnabled = false;
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
                TxtStressLabel.Text = string.Format("STABILITY TEST: {0}/15s", s);
            }

            cts.Cancel();
            await Task.Delay(500);

            _isStressTesting = false;
            BtnStressTest.IsEnabled = true;
            ProgStress.Visibility = Visibility.Collapsed;
            TxtStressLabel.Text = "⚡ RUN 15-SECOND HARDWARE STABILITY TEST";

            RefreshTelemetry();
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            _backend.Dispose();
            base.OnClosed(e);
        }
    }
}
