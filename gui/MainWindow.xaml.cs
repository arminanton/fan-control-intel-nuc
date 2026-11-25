using System;
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

            // Set up 1-second polling timer
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _isUpdatingUi = false;

            // Initial refresh
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
                if (TxtCpuTemp != null)
                {
                    TxtCpuTemp.Text = "ERR";
                    TxtCpuTemp.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
                return;
            }

            var temps = _backend.GetTemperatures();
            var fan = _backend.GetFanStatus();
            var cpu = _backend.GetCpuTelemetry();

            // Update Temperature Card
            if (TxtCpuTemp != null)
            {
                TxtCpuTemp.Text = string.Format("{0:F1}°C", temps.cpuTemp);
                if (temps.cpuTemp > 80)
                    TxtCpuTemp.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Crimson
                else if (temps.cpuTemp > 65)
                    TxtCpuTemp.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                else
                    TxtCpuTemp.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Emerald
            }

            if (TxtCoreTemp != null)
            {
                TxtCoreTemp.Text = string.Format("Core: {0:F0}°C", cpu.coreTemp);
            }

            // Update Fan Speed Card
            if (TxtFanRpm != null) TxtFanRpm.Text = string.Format("{0} RPM", fan.rpm);
            if (TxtDuty != null) TxtDuty.Text = string.Format("Duty: {0}%", fan.percent);

            // Update Package Power Card (Watts)
            if (TxtWatts != null)
            {
                TxtWatts.Text = string.Format("{0:F1} W", cpu.watts);
            }
            if (TxtPlLimits != null)
            {
                TxtPlLimits.Text = string.Format("PL1: {0:F0}W | PL2: {1:F0}W", cpu.pl1, cpu.pl2);
            }

            // Update CPU Clock Card
            if (TxtClock != null)
            {
                TxtClock.Text = string.Format("{0:F0} MHz", cpu.mhz);
            }
            if (TxtMode != null)
            {
                TxtMode.Text = fan.mode == 0x40 ? "BIOS Auto" : string.Format("PWM {0}", fan.pwm);
            }

            // Update Fan Slider without feedback loop
            if (!_isUpdatingUi && SliderPwm != null && TxtSliderVal != null)
            {
                _isUpdatingUi = true;
                if (fan.mode != 0x40)
                {
                    SliderPwm.Value = fan.percent;
                    TxtSliderVal.Text = string.Format("{0}%", fan.percent);
                }
                _isUpdatingUi = false;
            }

            // Board Sensors Footer
            if (TxtBoardTemps != null)
            {
                TxtBoardTemps.Text = string.Format("Board: SYSTIN {0:F1}°C | AUXTIN {1:F1}°C | eDRAM: 128MB L4 Active", temps.sysTemp, temps.auxTemp);
            }
        }

        private void BtnMax_Click(object sender, RoutedEventArgs e)
        {
            _backend.SetFanManual(255);
            _isUpdatingUi = true;
            if (SliderPwm != null) SliderPwm.Value = 100;
            if (TxtSliderVal != null) TxtSliderVal.Text = "100%";
            _isUpdatingUi = false;
            RefreshTelemetry();
        }

        private void BtnAuto_Click(object sender, RoutedEventArgs e)
        {
            _backend.SetFanAuto();
            RefreshTelemetry();
        }

        private void FanPreset_Click(object sender, RoutedEventArgs e)
        {
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

            int pct = (int)Math.Round(e.NewValue);
            TxtSliderVal.Text = string.Format("{0}%", pct);

            byte pwm = (byte)Math.Round((pct * 255.0) / 100.0);
            _backend.SetFanManual(pwm);
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

        private void ChkBootMax_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUi) return;
            bool enable = ChkBootMax.IsChecked == true;
            FanBackend.SetBootMax(enable);
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            _backend.Dispose();
            base.OnClosed(e);
        }
    }
}
