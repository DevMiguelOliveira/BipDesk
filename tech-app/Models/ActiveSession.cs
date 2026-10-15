using System;
using System.Diagnostics;

namespace BipDesk.Tech.Models
{
    public class ActiveSession
    {
        public Machine Machine { get; }
        public Process Process { get; }
        public DateTime StartedAt { get; }

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
    }
}
