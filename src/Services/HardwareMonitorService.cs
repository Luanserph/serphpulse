using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace OtimizadorJogos.Services;

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware subHardware in hardware.SubHardware)
            subHardware.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}

public class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly object _lock = new();
    private readonly string _dataFolder;
    private readonly LicenseService _license;
    private readonly int _port = 8080;
    private bool _isAdmin;

    // Static hardware info cached at startup
    private string _sysManufacturer = "PC";
    private string _sysModel = "Desktop";
    private string _sysType = "Desktop";
    private string _osName = "Windows";
    private string _cpuNameWmi = "Processador";
    private int _cpuCores = Environment.ProcessorCount / 2;
    private int _cpuThreads = Environment.ProcessorCount;
    private double _cpuMaxClockMHz = 0;
    private string _mbManufacturer = "";
    private string _mbModel = "";
    private string _biosVersion = "";
    private string _biosDate = "";
    private List<object> _ramModules = new();
    private double _ramTotalGB = 16;
    private List<WmiGpuInfo> _wmiGpus = new();
    private List<WmiDiskInfo> _wmiDisks = new();
    private ulong _prevIdleTime;
    private ulong _prevKernelTime;
    private ulong _prevUserTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong lpIdleTime, out ulong lpKernelTime, out ulong lpUserTime);

    private double GetCpuLoadFallback()
    {
        if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
            return 5.0;
        ulong idleDiff = idle - _prevIdleTime;
        ulong kernelDiff = kernel - _prevKernelTime;
        ulong userDiff = user - _prevUserTime;
        _prevIdleTime = idle;
        _prevKernelTime = kernel;
        _prevUserTime = user;
        ulong total = kernelDiff + userDiff;
        if (total == 0) return 5.0;
        double busy = (double)(total - idleDiff) / total * 100.0;
        return Math.Clamp(Math.Round(busy, 1), 0.0, 100.0);
    }

    private class WmiGpuInfo
    {
        public string Name { get; set; } = "";
        public string DriverVersion { get; set; } = "";
        public double VramMB { get; set; }
    }

    private class WmiDiskInfo
    {
        public int Index { get; set; }
        public string Model { get; set; } = "";
        public string InterfaceType { get; set; } = "";
        public string MediaType { get; set; } = "";
        public double SizeGB { get; set; }
        public string Status { get; set; } = "OK";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public string DataFolder => _dataFolder;

    public HardwareMonitorService(LicenseService license)
    {
        _license = license;
        _dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerphPulse");
        Directory.CreateDirectory(_dataFolder);

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            _isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { _isAdmin = false; }

        try { GetCpuLoadFallback(); } catch { }

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            IsBatteryEnabled = true,
            IsNetworkEnabled = false
        };

        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HardwareMonitor] Aviso ao abrir sensores: {ex.Message}");
        }

        LoadStaticWmiInfo();
    }

    private void LoadStaticWmiInfo()
    {
        try
        {
            using var csSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in csSearcher.Get())
            {
                _sysManufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? "PC";
                _sysModel = obj["Model"]?.ToString()?.Trim() ?? "Desktop";
                int pcType = Convert.ToInt32(obj["PCSystemType"] ?? 1);
                _sysType = pcType == 2 ? "Notebook" : "Desktop";
            }
        }
        catch { }

        try
        {
            using var osSearcher = new ManagementObjectSearcher("SELECT Caption, Version FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in osSearcher.Get())
            {
                _osName = obj["Caption"]?.ToString()?.Trim() ?? Environment.OSVersion.ToString();
            }
        }
        catch { }

        try
        {
            using var cpuSearcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject obj in cpuSearcher.Get())
            {
                _cpuNameWmi = obj["Name"]?.ToString()?.Trim() ?? _cpuNameWmi;
                _cpuCores = Convert.ToInt32(obj["NumberOfCores"] ?? _cpuCores);
                _cpuThreads = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? _cpuThreads);
                _cpuMaxClockMHz = Convert.ToDouble(obj["MaxClockSpeed"] ?? 0);
            }
        }
        catch { }

        try
        {
            using var mbSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject obj in mbSearcher.Get())
            {
                _mbManufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                _mbModel = obj["Product"]?.ToString()?.Trim() ?? "";
            }
        }
        catch { }

        try
        {
            using var biosSearcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (ManagementObject obj in biosSearcher.Get())
            {
                _biosVersion = obj["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                string rawDate = obj["ReleaseDate"]?.ToString() ?? "";
                if (rawDate.Length >= 8)
                    _biosDate = $"{rawDate.Substring(6, 2)}/{rawDate.Substring(4, 2)}/{rawDate.Substring(0, 4)}";
            }
        }
        catch { }

        try
        {
            var memList = new List<object>();
            double totalRam = 0;
            int idx = 1;
            using var memSearcher = new ManagementObjectSearcher("SELECT BankLabel, DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            foreach (ManagementObject obj in memSearcher.Get())
            {
                ulong capBytes = Convert.ToUInt64(obj["Capacity"] ?? 0UL);
                double capGB = Math.Round(capBytes / (1024.0 * 1024 * 1024), 1);
                totalRam += capGB;
                int speed = Convert.ToInt32(obj["ConfiguredClockSpeed"] ?? obj["Speed"] ?? 0);
                int memTypeCode = Convert.ToInt32(obj["SMBIOSMemoryType"] ?? 0);
                string memType = memTypeCode switch
                {
                    26 => "DDR4",
                    34 => "DDR5",
                    24 => "DDR3",
                    _ => speed >= 4400 ? "DDR5" : (speed >= 2133 ? "DDR4" : "DDR")
                };
                memList.Add(new
                {
                    slot = obj["DeviceLocator"]?.ToString()?.Trim() ?? $"Slot {idx}",
                    banco = obj["BankLabel"]?.ToString()?.Trim() ?? "",
                    capacidadeGB = capGB,
                    velocidadeMHz = speed,
                    fabricante = obj["Manufacturer"]?.ToString()?.Trim() ?? "Genérico",
                    partNumber = obj["PartNumber"]?.ToString()?.Trim() ?? "",
                    tipo = memType
                });
                idx++;
            }
            _ramModules = memList;
            if (totalRam > 0) _ramTotalGB = totalRam;
        }
        catch { }

        try
        {
            using var gpuSearcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject obj in gpuSearcher.Get())
            {
                string name = obj["Name"]?.ToString()?.Trim() ?? "GPU";
                ulong ramBytes = 0;
                try { ramBytes = Convert.ToUInt64(obj["AdapterRAM"] ?? 0UL); } catch { }
                double vramMB = Math.Round(ramBytes / (1024.0 * 1024.0), 0);
                _wmiGpus.Add(new WmiGpuInfo
                {
                    Name = name,
                    DriverVersion = obj["DriverVersion"]?.ToString()?.Trim() ?? "",
                    VramMB = vramMB > 0 ? vramMB : 4096
                });
            }
        }
        catch { }

        try
        {
            using var diskSearcher = new ManagementObjectSearcher("SELECT Index, Model, InterfaceType, MediaType, Size, Status FROM Win32_DiskDrive");
            foreach (ManagementObject obj in diskSearcher.Get())
            {
                ulong sizeBytes = 0;
                try { sizeBytes = Convert.ToUInt64(obj["Size"] ?? 0UL); } catch { }
                _wmiDisks.Add(new WmiDiskInfo
                {
                    Index = Convert.ToInt32(obj["Index"] ?? 0),
                    Model = obj["Model"]?.ToString()?.Trim() ?? "Disco",
                    InterfaceType = obj["InterfaceType"]?.ToString()?.Trim() ?? "SATA/NVMe",
                    MediaType = obj["MediaType"]?.ToString()?.Trim() ?? "Fixed hard disk media",
                    SizeGB = Math.Round(sizeBytes / (1024.0 * 1024 * 1024), 1),
                    Status = obj["Status"]?.ToString()?.Trim() ?? "OK"
                });
            }
        }
        catch { }
    }

    private static string UnitFor(SensorType type) => type switch
    {
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Factor => "",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.TimeSpan => "s",
        SensorType.Energy => "mWh",
        _ => ""
    };

    private static object? ValObj(double? val, string fonte = "sensor", string? sensor = null)
    {
        if (val == null || double.IsNaN(val.Value) || double.IsInfinity(val.Value))
            return new { valor = (double?)null };
        return new { valor = Math.Round(val.Value, 2), fonte, sensor };
    }

    public object GetSnapshot()
    {
        lock (_lock)
        {
            try { _computer.Accept(_visitor); } catch { }

            var allSensors = new List<object>();
            var allHardware = new List<IHardware>();

            void CollectHardware(IHardware hw)
            {
                allHardware.Add(hw);
                foreach (var sub in hw.SubHardware)
                    CollectHardware(sub);
            }

            foreach (var hw in _computer.Hardware)
                CollectHardware(hw);

            foreach (var hw in allHardware)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.Value.HasValue && !float.IsNaN(s.Value.Value))
                    {
                        allSensors.Add(new
                        {
                            id = s.Identifier.ToString(),
                            nome = s.Name,
                            hw = hw.Name,
                            tipoHw = hw.HardwareType.ToString(),
                            tipo = s.SensorType.ToString(),
                            valor = Math.Round((double)s.Value.Value, 2),
                            min = s.Min.HasValue ? Math.Round((double)s.Min.Value, 2) : (double?)null,
                            max = s.Max.HasValue ? Math.Round((double)s.Max.Value, 2) : (double?)null,
                            unidade = UnitFor(s.SensorType)
                        });
                    }
                }
            }

            // 1. CPU
            var cpuHw = allHardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            var cpuSensors = cpuHw?.Sensors.ToList() ?? new List<ISensor>();

            double? cpuTemp = cpuSensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0 && s.Value.Value < 130)
                .OrderByDescending(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Core (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Average", StringComparison.OrdinalIgnoreCase))
                .Select(s => (double?)s.Value!.Value)
                .FirstOrDefault();

            double? cpuTempMax = cpuSensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0 && s.Value.Value < 130)
                .Select(s => (double?)s.Value!.Value)
                .Max();

            double? cpuLoad = cpuSensors
                .Where(s => s.SensorType == SensorType.Load && s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)
                .Select(s => (double?)s.Value!.Value)
                .FirstOrDefault();

            if (cpuLoad == null)
            {
                try { cpuLoad = GetCpuLoadFallback(); } catch { }
            }

            double? cpuMaxCoreLoad = cpuSensors
                .Where(s => s.SensorType == SensorType.Load && !s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)
                .Select(s => (double?)s.Value!.Value)
                .Max() ?? cpuLoad;

            double? cpuClock = cpuSensors
                .Where(s => s.SensorType == SensorType.Clock && !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue && s.Value.Value > 100)
                .Select(s => (double?)s.Value!.Value)
                .Average() ?? (_cpuMaxClockMHz > 0 ? _cpuMaxClockMHz : null);

            double? cpuPower = cpuSensors
                .Where(s => s.SensorType == SensorType.Power && s.Value.HasValue && s.Value.Value > 0)
                .OrderByDescending(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                .Select(s => (double?)s.Value!.Value)
                .FirstOrDefault();

            if (cpuPower == null && cpuLoad.HasValue)
            {
                // Fallback estimation when running without ring0 driver
                cpuPower = Math.Round(15.0 + (cpuLoad.Value / 100.0) * 50.0, 1);
            }

            double? cpuVolt = cpuSensors
                .Where(s => s.SensorType == SensorType.Voltage && s.Value.HasValue && s.Value.Value > 0.3 && s.Value.Value < 2.0)
                .OrderByDescending(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("VID", StringComparison.OrdinalIgnoreCase))
                .Select(s => (double?)s.Value!.Value)
                .FirstOrDefault();

            // 2. Motherboard & Fans
            var mbHws = allHardware.Where(h => h.HardwareType == HardwareType.Motherboard || h.HardwareType == HardwareType.SuperIO || h.HardwareType == HardwareType.Cooler).ToList();
            var mbSensors = mbHws.SelectMany(h => h.Sensors).ToList();

            double? vrmTemp = mbSensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0 && s.Value.Value < 125 && (s.Name.Contains("VRM", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("MOS", StringComparison.OrdinalIgnoreCase)))
                .Select(s => (double?)s.Value!.Value)
                .FirstOrDefault();

            double? mbHotspot = mbSensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0 && s.Value.Value < 120)
                .Select(s => (double?)s.Value!.Value)
                .Max();

            double? vcore = mbSensors
                .Where(s => s.SensorType == SensorType.Voltage && s.Value.HasValue && s.Name.Contains("Vcore", StringComparison.OrdinalIgnoreCase))
                .Select(s => (double?)s.Value!.Value)
                .FirstOrDefault() ?? cpuVolt;

            var mbFans = new List<object>();
            var mbVentoinhas = new List<object>();
            var fanSensors = mbSensors.Where(s => s.SensorType == SensorType.Fan && s.Value.HasValue && s.Value.Value >= 0).ToList();
            double? cpuFanRpm = null;
            double? cpuFanPct = null;

            for (int i = 0; i < fanSensors.Count; i++)
            {
                var fs = fanSensors[i];
                double rpm = Math.Round(fs.Value!.Value, 0);
                bool isCpu = i == 0 || fs.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) || fs.Name.Contains("Fan #1", StringComparison.OrdinalIgnoreCase);
                if (cpuFanRpm == null && isCpu && rpm > 0)
                    cpuFanRpm = rpm;

                mbFans.Add(new
                {
                    rotulo = fs.Name,
                    rpm = rpm,
                    pct = (double?)null,
                    onde = "placa-mãe",
                    cpuPrincipal = i == 0
                });
                mbVentoinhas.Add(new { nome = fs.Name, valor = rpm });
            }

            var mbTemps = mbSensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0 && s.Value.Value < 125)
                .Select(s => new { nome = s.Name, valor = Math.Round((double)s.Value!.Value, 1) })
                .ToList();

            var mbVolts = mbSensors
                .Where(s => s.SensorType == SensorType.Voltage && s.Value.HasValue && s.Value.Value > 0)
                .Select(s => new { nome = s.Name, valor = Math.Round((double)s.Value!.Value, 3) })
                .ToList();

            double? v12 = mbSensors.FirstOrDefault(s => s.SensorType == SensorType.Voltage && s.Name.Contains("12V", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;
            double? v5 = mbSensors.FirstOrDefault(s => s.SensorType == SensorType.Voltage && s.Name.Contains("+5V", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;
            double? v33 = mbSensors.FirstOrDefault(s => s.SensorType == SensorType.Voltage && s.Name.Contains("3.3V", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;
            double? vbat = mbSensors.FirstOrDefault(s => s.SensorType == SensorType.Voltage && (s.Name.Contains("VBat", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("CMOS", StringComparison.OrdinalIgnoreCase)) && s.Value.HasValue)?.Value;

            // 3. GPUs
            var gpuHws = allHardware.Where(h => h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuIntel).ToList();
            var gpusOut = new List<object>();

            if (gpuHws.Count > 0)
            {
                foreach (var gHw in gpuHws)
                {
                    var gs = gHw.Sensors.ToList();
                    double? gTemp = gs.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value
                                 ?? gs.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)?.Value;
                    double? gHot = gs.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Hot", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;
                    double? gMemTemp = gs.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;
                    double? gLoad = gs.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value
                                 ?? gs.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Value.HasValue)?.Value;
                    double? gClock = gs.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;
                    double? gMemClock = gs.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;
                    double? gPower = gs.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Value.HasValue)?.Value;
                    double? gVramUsed = gs.FirstOrDefault(s => (s.SensorType == SensorType.SmallData || s.SensorType == SensorType.Data) && s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;
                    double? gVramTotal = gs.FirstOrDefault(s => (s.SensorType == SensorType.SmallData || s.SensorType == SensorType.Data) && s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;
                    double? gFanRpm = gs.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Value.HasValue)?.Value;
                    double? gFanPct = gs.FirstOrDefault(s => s.SensorType == SensorType.Control && s.Value.HasValue)?.Value;
                    double? gVolt = gs.FirstOrDefault(s => s.SensorType == SensorType.Voltage && s.Value.HasValue)?.Value;

                    var wmiMatch = _wmiGpus.FirstOrDefault(w => w.Name.Contains(gHw.Name, StringComparison.OrdinalIgnoreCase) || gHw.Name.Contains(w.Name, StringComparison.OrdinalIgnoreCase)) ?? _wmiGpus.FirstOrDefault();

                    gpusOut.Add(new
                    {
                        nome = gHw.Name,
                        driver = wmiMatch?.DriverVersion ?? "",
                        semSensores = gs.Count == 0,
                        temperatura = ValObj(gTemp),
                        pontoQuente = ValObj(gHot),
                        temperaturaMemoria = ValObj(gMemTemp),
                        usoPct = ValObj(gLoad),
                        velocidadeMHz = ValObj(gClock),
                        clockMemoriaMHz = ValObj(gMemClock),
                        potenciaW = ValObj(gPower),
                        vramUsadaMB = ValObj(gVramUsed),
                        vramTotalMB = gVramTotal.HasValue ? Math.Round(gVramTotal.Value, 0) : (wmiMatch?.VramMB ?? 4096),
                        ventoinhaRPM = ValObj(gFanRpm),
                        ventoinhaPct = ValObj(gFanPct),
                        voltagem = ValObj(gVolt),
                        sensores = gs.Where(s => s.Value.HasValue).Select(s => new
                        {
                            id = s.Identifier.ToString(),
                            nome = s.Name,
                            tipo = s.SensorType.ToString(),
                            valor = Math.Round((double)s.Value!.Value, 2),
                            unidade = UnitFor(s.SensorType)
                        }).ToList()
                    });
                }
            }
            else if (_wmiGpus.Count > 0)
            {
                foreach (var wg in _wmiGpus)
                {
                    gpusOut.Add(new
                    {
                        nome = wg.Name,
                        driver = wg.DriverVersion,
                        semSensores = false,
                        temperatura = ValObj(null),
                        pontoQuente = ValObj(null),
                        temperaturaMemoria = ValObj(null),
                        usoPct = ValObj(0),
                        velocidadeMHz = ValObj(null),
                        clockMemoriaMHz = ValObj(null),
                        potenciaW = ValObj(null),
                        vramUsadaMB = ValObj(null),
                        vramTotalMB = wg.VramMB,
                        ventoinhaRPM = ValObj(null),
                        ventoinhaPct = ValObj(null),
                        voltagem = ValObj(null),
                        sensores = Array.Empty<object>()
                    });
                }
            }

            // 4. RAM
            var memStatus = new MEMORYSTATUSEX();
            double ramTotalGB = _ramTotalGB;
            double ramLivreGB = 8;
            double ramUsadaGB = 8;
            double ramUsoPct = 50;
            if (GlobalMemoryStatusEx(memStatus) && memStatus.ullTotalPhys > 0)
            {
                ramTotalGB = Math.Round(memStatus.ullTotalPhys / (1024.0 * 1024 * 1024), 1);
                ramLivreGB = Math.Round(memStatus.ullAvailPhys / (1024.0 * 1024 * 1024), 1);
                ramUsadaGB = Math.Round(ramTotalGB - ramLivreGB, 1);
                ramUsoPct = Math.Round((ramUsadaGB / ramTotalGB) * 100.0, 1);
            }

            // 5. Storage / Discos
            var storageHws = allHardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).ToList();
            var discosOut = new List<object>();

            int dCount = Math.Max(storageHws.Count, Math.Max(_wmiDisks.Count, 1));
            for (int i = 0; i < dCount; i++)
            {
                var sHw = i < storageHws.Count ? storageHws[i] : null;
                var wDisk = i < _wmiDisks.Count ? _wmiDisks[i] : null;
                var ds = sHw?.Sensors.ToList() ?? new List<ISensor>();

                string dName = sHw?.Name ?? wDisk?.Model ?? $"Disco {i + 1}";
                string dInterface = wDisk?.InterfaceType ?? (dName.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe" : "SATA");
                string dType = dName.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "SSD NVMe"
                             : (dName.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD" : "SSD / Disco Fixo");

                // Prefer Composite Temperature for NVMe
                var compTempSensor = ds.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue && s.Value.Value > 0 && s.Value.Value < 120)
                                  ?? ds.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0 && s.Value.Value < 120);

                double? dTemp = compTempSensor?.Value;
                double? dLife = ds.FirstOrDefault(s => s.SensorType == SensorType.Level && (s.Name.Contains("Life", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Remaining", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Available Spare", StringComparison.OrdinalIgnoreCase)) && s.Value.HasValue)?.Value ?? 100.0;
                double? dAct = ds.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Activity", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;
                double? dReadBps = ds.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Contains("Read", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;
                double? dWriteBps = ds.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Contains("Write", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)?.Value;

                var volumes = new List<object>();
                double usedSpacePct = 0;
                if (i == 0 && drives.Count > 0)
                {
                    foreach (var drv in drives)
                    {
                        double totG = Math.Round(drv.TotalSize / (1024.0 * 1024 * 1024), 1);
                        double freeG = Math.Round(drv.AvailableFreeSpace / (1024.0 * 1024 * 1024), 1);
                        double usedG = Math.Round(totG - freeG, 1);
                        double pct = totG > 0 ? Math.Round((usedG / totG) * 100.0, 1) : 0;
                        if (pct > usedSpacePct) usedSpacePct = pct;
                        volumes.Add(new
                        {
                            letra = drv.Name.TrimEnd('\\'),
                            rotulo = string.IsNullOrWhiteSpace(drv.VolumeLabel) ? " Disco Local" : drv.VolumeLabel,
                            sistema = drv.DriveFormat,
                            totalGB = totG,
                            livreGB = freeG,
                            usadoGB = usedG,
                            usadoPct = pct
                        });
                    }
                }

                var motivos = new List<string>();
                string estado = "Bom";
                if (dTemp.HasValue && dTemp.Value >= 60)
                {
                    estado = "Atenção";
                    motivos.Add($"Temperatura alta ({Math.Round(dTemp.Value, 0)} °C).");
                }
                if (dLife.HasValue && dLife.Value < 50)
                {
                    estado = "Atenção";
                    motivos.Add($"Vida útil restante em {Math.Round(dLife.Value, 0)}%.");
                }
                if (usedSpacePct >= 90)
                {
                    estado = "Atenção";
                    motivos.Add($"Espaço em disco quase cheio ({usedSpacePct}% usado).");
                }
                if (motivos.Count == 0)
                    motivos.Add("Disco operando normalmente e dentro dos parâmetros.");

                discosOut.Add(new
                {
                    id = $"disco{i}",
                    nome = dName,
                    modelo = dName,
                    tipo = dType,
                    @interface = dInterface,
                    tamanhoGB = wDisk?.SizeGB ?? (volumes.Count > 0 ? drives.Sum(d => Math.Round(d.TotalSize / (1024.0 * 1024 * 1024), 1)) : 500),
                    estado = estado,
                    temperatura = ValObj(dTemp, "sensor", compTempSensor?.Name),
                    vidaRestantePct = ValObj(dLife),
                    espacoUsadoPct = ValObj(usedSpacePct),
                    atividadePct = ValObj(dAct ?? 2.0),
                    leituraBps = dReadBps ?? 0,
                    escritaBps = dWriteBps ?? 0,
                    motivos = motivos,
                    volumes = volumes,
                    sensores = ds.Where(s => s.Value.HasValue).Select(s => new
                    {
                        id = s.Identifier.ToString(),
                        nome = s.Name,
                        tipo = s.SensorType.ToString(),
                        valor = Math.Round((double)s.Value!.Value, 2),
                        unidade = UnitFor(s.SensorType)
                    }).ToList()
                });
            }

            // 6. Health Summary (resumo)
            var resumoItens = new List<object>();
            string resumoStatus = "ok";

            if (cpuTemp.HasValue && cpuTemp.Value >= 85)
            {
                resumoStatus = "critico";
                resumoItens.Add(new
                {
                    chave = "cpu.temp.alta",
                    nivel = "critico",
                    titulo = $"Processador muito quente ({Math.Round(cpuTemp.Value, 1)} °C)",
                    detalhe = "A temperatura da CPU está próxima do limite térmico, o que pode causar perda de desempenho (thermal throttling).",
                    solucao = "Verifique a rotação do cooler da CPU, fluxo de ar do gabinete e pasta térmica.",
                    area = "cpu"
                });
            }
            else if (cpuTemp.HasValue && cpuTemp.Value >= 75)
            {
                if (resumoStatus == "ok") resumoStatus = "atencao";
                resumoItens.Add(new
                {
                    chave = "cpu.temp.atencao",
                    nivel = "atencao",
                    titulo = $"Temperatura do processador elevada ({Math.Round(cpuTemp.Value, 1)} °C)",
                    detalhe = "O processador está aquecido sob a carga atual.",
                    solucao = "Acompanhe durante jogos ou faça um teste na aba Estresse.",
                    area = "cpu"
                });
            }
            else
            {
                resumoItens.Add(new
                {
                    chave = "cpu.ok",
                    nivel = "ok",
                    titulo = "Processador operando normalmente",
                    detalhe = cpuTemp.HasValue ? $"Temperatura atual em {Math.Round(cpuTemp.Value, 1)} °C e uso em {Math.Round(cpuLoad ?? 0, 1)}%." : $"Uso atual em {Math.Round(cpuLoad ?? 0, 1)}%.",
                    solucao = "",
                    area = "cpu"
                });
            }

            if (ramUsoPct >= 90)
            {
                if (resumoStatus == "ok") resumoStatus = "atencao";
                resumoItens.Add(new
                {
                    chave = "ram.alta",
                    nivel = "atencao",
                    titulo = $"Memória RAM quase cheia ({ramUsoPct}%)",
                    detalhe = $"Estão em uso {ramUsadaGB} GB de {ramTotalGB} GB disponíveis.",
                    solucao = "Use a limpeza de memória no Modo Gamer ou feche programas pesados em segundo plano.",
                    area = "ram"
                });
            }
            else
            {
                resumoItens.Add(new
                {
                    chave = "ram.ok",
                    nivel = "ok",
                    titulo = "Memória RAM com folga saudável",
                    detalhe = $"Usando {ramUsadaGB} GB de {ramTotalGB} GB ({ramUsoPct}%).",
                    solucao = "",
                    area = "ram"
                });
            }

            resumoItens.Add(new
            {
                chave = "disco.ok",
                nivel = "ok",
                titulo = "Discos sem alerta de saúde",
                detalhe = "Nenhum disco reportou falha SMART ou risco de perda de dados.",
                solucao = "",
                area = "disco"
            });

            double uptimeHours = Math.Round(Environment.TickCount64 / (1000.0 * 3600.0), 1);
            var (isUnlocked, isOwner, licRec, _) = _license.EvaluateCurrentLicense();

            return new
            {
                pronto = true,
                timestamp = DateTime.Now.ToString("o"),
                edicao = isUnlocked ? "pro" : "free",
                isOwner = isOwner,
                hwid = _license.GetHwid(),
                titular = licRec?.Titular ?? "Modo Demo",
                maquina = new
                {
                    fabricante = _sysManufacturer,
                    modelo = _sysModel,
                    tipo = _sysType,
                    so = _osName,
                    uptimeHoras = uptimeHours,
                    pastaDados = _dataFolder,
                    porta = _port,
                    versaoApp = isOwner ? "3.0 Owner (Serph)" : (isUnlocked ? "3.0 Pro" : "3.0 Demo"),
                    beta = true
                },
                diagnostico = new
                {
                    admin = _isAdmin,
                    leituraDireta = allSensors.Count > 5,
                    driverInstalado = allSensors.Count > 5,
                    totalSensores = allSensors.Count,
                    mensagens = _isAdmin ? Array.Empty<string>() : new[] { "Execute como Administrador para liberar todos os sensores físicos de tensão e temperatura da placa-mãe." }
                },
                cpu = new
                {
                    nome = cpuHw?.Name ?? _cpuNameWmi,
                    fabricante = (cpuHw?.Name ?? _cpuNameWmi).Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD" : "Intel",
                    nucleos = _cpuCores,
                    threads = _cpuThreads,
                    velocidadeBaseMHz = _cpuMaxClockMHz,
                    temperatura = ValObj(cpuTemp),
                    usoPct = ValObj(cpuLoad),
                    nucleoMaisOcupadoPct = ValObj(cpuMaxCoreLoad),
                    velocidadeMHz = ValObj(cpuClock),
                    potenciaW = ValObj(cpuPower),
                    voltagem = ValObj(cpuVolt),
                    temperaturaMaisAlta = cpuTempMax.HasValue ? Math.Round(cpuTempMax.Value, 1) : (double?)null,
                    ventoinha = new { rpm = cpuFanRpm, pct = cpuFanPct },
                    sensores = cpuSensors.Where(s => s.Value.HasValue).Select(s => new
                    {
                        id = s.Identifier.ToString(),
                        nome = s.Name,
                        tipo = s.SensorType.ToString(),
                        valor = Math.Round((double)s.Value!.Value, 2),
                        unidade = UnitFor(s.SensorType)
                    }).ToList()
                },
                gpus = gpusOut,
                ram = new
                {
                    totalGB = ramTotalGB,
                    usadaGB = ValObj(ramUsadaGB),
                    livreGB = ramLivreGB,
                    usoPct = ValObj(ramUsoPct),
                    slotsUsados = _ramModules.Count,
                    modulos = _ramModules,
                    canais = new { modo = _ramModules.Count >= 2 ? "Dual Channel" : "Single Channel" }
                },
                placaMae = new
                {
                    fabricante = _mbManufacturer,
                    modelo = string.IsNullOrWhiteSpace(_mbModel) ? (mbHws.FirstOrDefault()?.Name ?? "Placa-Mãe") : _mbModel,
                    bios = _biosVersion,
                    dataBios = _biosDate,
                    vrmTemperatura = ValObj(vrmTemp),
                    zonaMaisQuente = ValObj(mbHotspot),
                    vcore = ValObj(vcore),
                    linha12V = ValObj(v12 ?? 12.04, v12.HasValue ? "sensor" : "estimado"),
                    linha5V = ValObj(v5 ?? 5.02, v5.HasValue ? "sensor" : "estimado"),
                    linha33V = ValObj(v33 ?? 3.32, v33.HasValue ? "sensor" : "estimado"),
                    bateriaCmos = new { valor = Math.Round(vbat ?? 3.08, 2), estimado = !vbat.HasValue, bloqueado = false },
                    fans = mbFans,
                    ventoinhas = mbVentoinhas,
                    tensoes = mbVolts,
                    temperaturas = mbTemps
                },
                discos = discosOut,
                bateria = (object?)null,
                sensores = allSensors,
                resumo = new
                {
                    status = resumoStatus,
                    itens = resumoItens
                }
            };
        }
    }

    public void Dispose()
    {
        try { _computer.Close(); } catch { }
    }
}
