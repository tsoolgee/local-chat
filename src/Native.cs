using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace LocalChat
{
    static class ProcessCmd
    {
        public static System.Collections.Generic.List<string> Browsers()
        {
            var list = new System.Collections.Generic.List<string>();
            using (var s = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'"))
                foreach (ManagementObject o in s.Get())
                    list.Add(o["CommandLine"] as string);
            return list;
        }

        public static string Get(int pid)
        {
            using (var s = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                foreach (ManagementObject o in s.Get())
                    return o["CommandLine"] as string;
            return null;
        }
    }

    /// Job object that kills every assigned process when this app exits (even on crash).
    static class KillOnExitJob
    {
        static IntPtr _job;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr a, string name);
        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint len);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic;
            public IO_COUNTERS Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        public static void Add(Process p)
        {
            try
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    SetInformationJobObject(_job, 9, ref info, (uint)Marshal.SizeOf(info));
                }
                AssignProcessToJobObject(_job, p.Handle);
            }
            catch (Exception ex) { Log.Write("Job object: " + ex.Message); }
        }
    }
}
