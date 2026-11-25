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

        public MainWindow()
        {
            InitializeComponent();

            _backend = new FanBackend();

            // Check boot persistence
            ChkBootMax.IsChecked = FanBackend.IsBootMaxEnabled();

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

            // Update Temp
            if (TxtCpuTemp != null)
            {
                TxtCpuTemp.Text = $"{temps.cpuTemp:F1}°C";
                if (temps.cpuTemp > 80)
                    TxtCpuTemp.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                else if (temps.cpuTemp > 65)
                    TxtCpuTemp.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                else
                    TxtCpuTemp.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Green
            }

            // Update Fan
            if (TxtFanRpm != null) TxtFanRpm.Text = $"{fan.rpm} RPM";
            if (TxtDuty != null) TxtDuty.Text = $"{fan.percent}%";
            if (TxtMode != null) TxtMode.Text = fan.mode == 0x40 ? "BIOS SmartFan IV" : $"Manual (PWM {fan.pwm})";

            // Update Slider without triggering event loop
            if (!_isUpdatingUi && SliderPwm != null && TxtSliderVal != null)
            {
                _isUpdatingUi = true;
                if (fan.mode != 0x40)
                {
                    SliderPwm.Value = fan.percent;
                    TxtSliderVal.Text = $"{fan.percent}%";
                }
                _isUpdatingUi = false;
            }

            // Board temps
            if (TxtBoardTemps != null)
                TxtBoardTemps.Text = $"Board: SYSTIN {temps.sysTemp:F1}°C | AUXTIN {temps.auxTemp:F1}°C";
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

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int pct))
            {
                byte pwm = (byte)Math.Round((pct * 255.0) / 100.0);
                _backend.SetFanManual(pwm);
                _isUpdatingUi = true;
                if (SliderPwm != null) SliderPwm.Value = pct;
                if (TxtSliderVal != null) TxtSliderVal.Text = $"{pct}%";
                _isUpdatingUi = false;
                RefreshTelemetry();
            }
        }

        private void SliderPwm_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUi || TxtSliderVal == null) return;

            int pct = (int)Math.Round(e.NewValue);
            TxtSliderVal.Text = $"{pct}%";

            byte pwm = (byte)Math.Round((pct * 255.0) / 100.0);
            _backend.SetFanManual(pwm);
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
