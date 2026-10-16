using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BipDesk.Tech.Models
{
    public class ActiveSession : INotifyPropertyChanged
    {
        public Machine Machine { get; }
        public Process Process { get; }
        public DateTime StartedAt { get; }
        public IntPtr WindowHandle { get; set; } = IntPtr.Zero;

        private object? _hostControl;
        public object? HostControl
        {
            get => _hostControl;
            set
            {
                if (_hostControl != value)
                {
                    _hostControl = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsAttached));
                }
            }
        }

        public bool IsAttached => HostControl != null;

        private bool _isCurrent;
        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent != value)
                {
                    _isCurrent = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ActiveSession(Machine machine, Process process)
        {
            Machine = machine;
            Process = process;
            StartedAt = DateTime.Now;
        }

        public string DurationText
        {
            get
            {
                var span = DateTime.Now - StartedAt;
                return $"{(int)span.TotalMinutes:D2}:{span.Seconds:D2}";
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

