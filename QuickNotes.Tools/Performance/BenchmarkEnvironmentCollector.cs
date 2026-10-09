using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuickNotes.Tools.Performance;

public sealed class BenchmarkEnvironmentInfo
{
    public string OsDescription { get; init; } = string.Empty;
    public string FrameworkDescription { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string BuildConfiguration { get; init; } = string.Empty;
    public string CpuName { get; init; } = string.Empty;
    public int LogicalCores { get; init; }
    public double TotalPhysicalMemoryGb { get; init; }
    public string StorageType { get; init; } = string.Empty;
    public string StorageFileSystem { get; init; } = string.Empty;
    public double SystemDpi { get; init; }
    public string SystemTheme { get; init; } = string.Empty;
}

public static class BenchmarkEnvironmentCollector
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    public static BenchmarkEnvironmentInfo Collect(string targetDirectory)
    {
        string os = RuntimeInformation.OSDescription;
        string framework = RuntimeInformation.FrameworkDescription;
        string arch = RuntimeInformation.ProcessArchitecture.ToString();

        string buildConfig;
#if DEBUG
        buildConfig = "Debug";
#else
        buildConfig = "Release";
#endif

        string cpuName = GetCpuName();
        int cores = Environment.ProcessorCount;
        double ramGb = GetTotalMemoryGb();

        var (storageType, fileSystem) = GetStorageInfo(targetDirectory);
        double dpi = GetDpi();
        string theme = GetSystemTheme();

        return new BenchmarkEnvironmentInfo
        {
            OsDescription = os,
            FrameworkDescription = framework,
            Architecture = arch,
            BuildConfiguration = buildConfig,
            CpuName = cpuName,
            LogicalCores = cores,
            TotalPhysicalMemoryGb = ramGb,
            StorageType = storageType,
            StorageFileSystem = fileSystem,
            SystemDpi = dpi,
            SystemTheme = theme
        };
    }

    private static string GetCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string? name = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }
        catch
        {
            // Fallback below
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown CPU";
    }

    private static double GetTotalMemoryGb()
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                return Math.Round((double)memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0), 2);
            }
        }
        catch
        {
            // Fallback
        }

        return 0.0;
    }

    private static (string StorageType, string FileSystem) GetStorageInfo(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "C:\\";
            var drive = new DriveInfo(root);
            string fs = drive.DriveFormat;

            // In Windows 10/11 NVMe/SSD query
            string type = drive.DriveType switch
            {
                DriveType.Fixed => "SSD/Fixed Disk",
                DriveType.Removable => "Removable",
                DriveType.Network => "Network",
                _ => drive.DriveType.ToString()
            };

            return (type, fs);
        }
        catch
        {
            return ("Unknown Storage", "Unknown FS");
        }
    }

    private static double GetDpi()
    {
        try
        {
            uint dpi = GetDpiForSystem();
            if (dpi > 0) return dpi;
        }
        catch
        {
            // Fallback
        }

        return 96.0;
    }

    private static string GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                object? value = key.GetValue("AppsUseLightTheme");
                if (value is int lightTheme)
                {
                    return lightTheme == 0 ? "Dark" : "Light";
                }
            }
        }
        catch
        {
            // Fallback
        }

        return "System";
    }

    public static string SanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile) && path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            path = "<UserProfile>" + path.Substring(userProfile.Length);
        }

        string temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(temp) && path.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            path = "<Temp>" + path.Substring(temp.Length);
        }

        return path;
    }
}
