using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using PowerManagement;
using OpenHardwareMonitor.Hardware;

namespace TexSystemMonitor
{
    using threadfn = System.UInt32;

    // https://stackoverflow.com/questions/394816/how-to-get-parent-process-in-net-in-managed-way

    /// <summary>
    /// A utility class to determine a process parent.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ParentProcessUtilities
    {
        // These members must match PROCESS_BASIC_INFORMATION
        internal IntPtr Reserved1;
        internal IntPtr PebBaseAddress;
        internal IntPtr Reserved2_0;
        internal IntPtr Reserved2_1;
        internal IntPtr UniqueProcessId;
        internal IntPtr InheritedFromUniqueProcessId;

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref ParentProcessUtilities processInformation, int processInformationLength, out int returnLength);

        /// <summary>
        /// Gets the parent process of the current process.
        /// </summary>
        /// <returns>An instance of the Process class.</returns>
        public static Process GetParentProcess()
        {
            return GetParentProcess(Process.GetCurrentProcess().Handle);
        }

        /// <summary>
        /// Gets the parent process of specified process.
        /// </summary>
        /// <param name="id">The process id.</param>
        /// <returns>An instance of the Process class.</returns>
        public static Process GetParentProcess(int id)
        {
            Process process = Process.GetProcessById(id);
            return GetParentProcess(process.Handle);
        }

        /// <summary>
        /// Gets the parent process of a specified process.
        /// </summary>
        /// <param name="handle">The process handle.</param>
        /// <returns>An instance of the Process class.</returns>
        public static Process GetParentProcess(IntPtr handle)
        {
            ParentProcessUtilities pbi = new ParentProcessUtilities();
            int returnLength;
            int status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out returnLength);
            if (status != 0)
                throw new Win32Exception(status);

            try
            {
                return Process.GetProcessById(pbi.InheritedFromUniqueProcessId.ToInt32());
            }
            catch (ArgumentException)
            {
                // not found
                return null;
            }
        }
    }

    struct RegistryDefaultValue
    {
        public string Name;
        public RegistryValueKind Kind;
        public object DefaultValue;
        public Func<object, object> ValueChecker;
    }

    class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        public const string DAEMON_NAME = "tsysmond";
        public const string DAEMON_EXTENDED_NAME = "TexSystemMonitor";
        // public const string DAEMON_DESCRIPTIVE_NAME = "Tex System Monitor Daemon";

        public const bool WAIT_MAIN_EXIT_NOTIFICATION = true;
        public const int WAIT_MAIN_EXIT_NOTIFICATION_MS = 3000;

        public const int CPU_TEMPERATURE_POLLING_RATE_MS = 1000;

        public const uint SAFE_CPU_TEMPERATURE_CELSIUS = 65;
        public const uint CRITICAL_CPU_TEMPERATURE_CELSIUS = 80;
        public const uint DANGEROUS_CPU_TEMPERATURE_CELSIUS = 85;

        public static Func<threadfn>[] threadFuncs = new Func<threadfn>[] { cpuTempSafetyMeasure, powerSupplySwitchAlerter };

        public const string optionArgumentPrefix = "--";
        public static readonly string[] optionArguments = { "console", "enableTelemetry" };

        public static DateTime? daemonStartedAt = null;

        public static RegistryKey registry = null;
        public static Dictionary<string, object> registryValuesSnapshot = null;

        public static readonly RegistryDefaultValue[] registryDefaultValues = {
            new RegistryDefaultValue { Name = "CPUTempSafeStartup", Kind = RegistryValueKind.DWord, DefaultValue = 0x00000000, ValueChecker = null },
            new RegistryDefaultValue { Name = "TelemetryFileLocation", Kind = RegistryValueKind.String, DefaultValue = @"C:\Tex\telemetry.txt", ValueChecker = null }
        };

        public static ManualResetEvent exitEvent = null;
        public static bool exitEventBool = false;

        public static void sendNotification(string title, string content, string attributionContent = null, bool displayDateTime = false)
        {
            Console.WriteLine($"[{(displayDateTime ? (DateTime.Now.ToString() + " | ") : "")}ToastNotification] {title}{((attributionContent != null) ? (" (" + attributionContent + ")") : "")}{((content != null) ? (": " + content) : "")}");

            new ToastContentBuilder()
                .AddText(title)
                .AddText((displayDateTime ? ("[" + DateTime.Now.ToString() + "] ") : "") + content)
                .AddAttributionText(attributionContent)
                .Show();
        }

        public static bool createMissingDirectories(string path)
        {
            string[] folders = Path.GetDirectoryName(path).Split(new string[] { "\\", "/" }, StringSplitOptions.RemoveEmptyEntries);

            string folder = "";

            for(int i = 0; i < folders.Length; i++)
            {
                folder += folders[i] + "\\";

                if (Directory.Exists(folder)) continue;

                try
                {
                    Directory.CreateDirectory(folder);
                } catch
                {
                    return false;
                }
            }

            return true;
        }

        public static threadfn cpuTempSafetyMeasure()
        {
            string THREAD_NAME = MethodBase.GetCurrentMethod().Name;

            Computer computer = new Computer();
            bool notifiedCriticalTemperature = false;
            bool noSensors = false;
            bool safeStartupChecked = false;

            computer.CPUEnabled = true;

            computer.Open();

            while (!exitEventBool)
            {
                float? maxTemp = 0;
                bool foundSensors = false;

                foreach (IHardware hardware in computer.Hardware)
                {
                    if (hardware.HardwareType == HardwareType.CPU)
                    {
                        hardware.Update();

                        if (hardware.Sensors.Length > 0)
                        {
                            foundSensors = true;
                        }

                        foreach (ISensor sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature)
                            {
                                if (sensor.Value > maxTemp) maxTemp = sensor.Value;
                            }
                        }
                    }

                    break;
                }

                if (!foundSensors)
                {
                    noSensors = true;

                    goto exitWhile;
                }

                if (!safeStartupChecked && ((uint)(int)registryValuesSnapshot["CPUTempSafeStartup"] > 0))
                {
                    if (maxTemp <= SAFE_CPU_TEMPERATURE_CELSIUS)
                    {
                        try
                        {
                            registry.SetValue("CPUTempSafeStartup", 0x00000000);
                        }
                        catch (Exception exc)
                        {
                            sendNotification(DAEMON_NAME, $"Couldn't manage the registry: \"{exc.Message}\", terminating thread...", THREAD_NAME, true);

                            break;
                        }
                    }
                    else
                    {
                        if (!PowerUtilities.ExitWindows(ShutdownType.Shutdown, ShutdownReason.MajorSystem, true))
                        {
                            sendNotification(DAEMON_NAME, "The CPU temperature is still not safe and couldn't shutdown, terminating thread...", THREAD_NAME, true);
                        }

                        break;
                    }

                    safeStartupChecked = true;
                }

                if (maxTemp >= DANGEROUS_CPU_TEMPERATURE_CELSIUS)
                {
                    sendNotification(DAEMON_NAME, $"Reached dangerous temperature: {maxTemp}°C | Shutting down...", THREAD_NAME, true);

                    try
                    {
                        registry.SetValue("CPUTempSafeStartup", 0xFFFFFFFF);
                    } catch(Exception exc)
                    {
                        sendNotification(DAEMON_NAME, $"Couldn't manage the registry: \"{exc.Message}\"", THREAD_NAME, true);
                    }

                    if (!PowerUtilities.ExitWindows(ShutdownType.Shutdown, ShutdownReason.MajorSystem, true))
                    {
                        sendNotification(DAEMON_NAME, "Shutdown failed, terminating thread...", THREAD_NAME, true);
                    }

                    break;
                }
                else if ((maxTemp >= CRITICAL_CPU_TEMPERATURE_CELSIUS) && !notifiedCriticalTemperature)
                {
                    sendNotification(DAEMON_NAME, $"Reached critical temperature: {maxTemp}°C", THREAD_NAME, true);

                    notifiedCriticalTemperature = true;
                }
                else if (notifiedCriticalTemperature && (maxTemp < CRITICAL_CPU_TEMPERATURE_CELSIUS))
                {
                    notifiedCriticalTemperature = false;

                    sendNotification(DAEMON_NAME, $"Cooling down: {maxTemp}°C", THREAD_NAME, true);
                }

                Thread.Sleep(CPU_TEMPERATURE_POLLING_RATE_MS);
            }

        exitWhile:
            computer.Close();

            if (noSensors)
            {
                sendNotification(DAEMON_NAME, "No sensors to check, terminating thread...", THREAD_NAME, true);
            }

            return 0;
        }

        public static threadfn powerSupplySwitchAlerter()
        {
            string THREAD_NAME = MethodBase.GetCurrentMethod().Name;

            PowerModeChangedEventHandler powerModeChangedHandler = (_, ev) => {
                if (ev.Mode == PowerModes.StatusChange)
                {
                    PowerLineStatus powerLineStatus = SystemInformation.PowerStatus.PowerLineStatus;

                    if (powerLineStatus == PowerLineStatus.Unknown)
                    {
                        sendNotification(DAEMON_NAME, "Power status is unknown.", THREAD_NAME, true);
                    }
                    else
                    {
                        sendNotification(DAEMON_NAME, $"Computer is now running on {((powerLineStatus == PowerLineStatus.Online) ? $"AC{(((SystemInformation.PowerStatus.BatteryChargeStatus & BatteryChargeStatus.Charging) > 0) ? " (Battery in charge)" : "")}" : ((powerLineStatus == PowerLineStatus.Offline) ? "battery" : "<UNHANDLED_CASE>"))} power.", THREAD_NAME, true);
                    }
                }
            };

            PowerModeChangedEventArgs powerModeArgs = new PowerModeChangedEventArgs(PowerModes.StatusChange);
            powerModeChangedHandler(null, powerModeArgs);

            SystemEvents.PowerModeChanged += powerModeChangedHandler;

            exitEvent.WaitOne();

            SystemEvents.PowerModeChanged -= powerModeChangedHandler;

            return 0;
        }

        public static threadfn telemetryThread()
        {
            string THREAD_NAME = MethodBase.GetCurrentMethod().Name;

            if (!createMissingDirectories((string)registryValuesSnapshot["TelemetryFileLocation"]))
            {
                sendNotification(DAEMON_NAME, $"Couldn't create the missing directories for the telemetry file: \"{(string)registryValuesSnapshot["TelemetryFileLocation"]}\"", THREAD_NAME, true);
            }

            try
            {
                File.AppendAllText((string)registryValuesSnapshot["TelemetryFileLocation"], ("{daemonStartedAt: " + daemonStartedAt + "}\n"));
            } catch(Exception exc)
            {
                sendNotification(DAEMON_NAME, $"Couldn't write or append data to the telemetry file: \"{(string)registryValuesSnapshot["TelemetryFileLocation"]}\"\n\"{exc.Message}\"");

                return 0;
            }

            string interestedSource = "DCOM";

            EventLog eventLog = new EventLog();

            eventLog.Log = "System";
            eventLog.MachineName = Environment.MachineName;

            // string prevEntries = "[Events\\]\n" + String.Join("\n", eventLog.Entries.Cast<EventLogEntry>().Where(entry => entry.Source == interestedSource).Select(entry => $"[Event]\nSource: {entry.Source}\nType: {entry.EntryType}\nGenerated at: {entry.TimeGenerated}\nID: {entry.InstanceId}\nMessage: \"{entry.Message}\"")) + "\n[Events/]";

            // try
            // {
            //     File.AppendAllText((string)registryValuesSnapshot["TelemetryFileLocation"], prevEntries);
            // } catch(Exception exc)
            // {
            //     sendNotification(DAEMON_NAME, $"Couldn't write or append data to the telemetry file: \"{(string)registryValuesSnapshot["TelemetryFileLocation"]}\"\n\"{exc.Message}\"");

            //     return 0;
            // }

            eventLog.Source = interestedSource;

            bool suspensionCleanup = false;

            EntryWrittenEventHandler eventEntryHandler = (object sender, EntryWrittenEventArgs e) => {
                EventLogEntry entry = e.Entry;

                try
                {
                    if(!File.Exists((string)registryValuesSnapshot["TelemetryFileLocation"])) File.AppendAllText((string)registryValuesSnapshot["TelemetryFileLocation"], ("{daemonStartedAt: " + daemonStartedAt + "}\n"));

                    File.AppendAllText((string)registryValuesSnapshot["TelemetryFileLocation"], $"[Event]\nSource: {entry.Source}\nType: {entry.EntryType}\nGenerated at: {entry.TimeGenerated}\nID: {entry.InstanceId}\nMessage: \"{entry.Message}\"\n");
                } catch(Exception exc)
                {
                    sendNotification(DAEMON_NAME, $"Couldn't write or append data to the telemetry file: \"{(string)registryValuesSnapshot["TelemetryFileLocation"]}\"\n\"{exc.Message}\"\nPerpetually suspending thread...");

                    suspensionCleanup = true;

                    eventLog.EnableRaisingEvents = false;

                    eventLog.Close();
                }
            };

            eventLog.EntryWritten += eventEntryHandler;

            eventLog.EnableRaisingEvents = true;

            exitEvent.WaitOne();

            if (!suspensionCleanup)
            {
                eventLog.EnableRaisingEvents = false;

                eventLog.Close();
            }

            eventLog.EntryWritten -= eventEntryHandler;

            return 0;
        }

        [STAThread]
        public static void Main(string[] args)
        {
            daemonStartedAt = DateTime.Now;

            bool isConsoleWindowPresent = GetConsoleWindow() != IntPtr.Zero;
            bool createConsoleWindow = args.Any(argument => argument == (optionArgumentPrefix + "console"));

            if (!isConsoleWindowPresent && createConsoleWindow)
            {
                if (!AllocConsole()) return;

                isConsoleWindowPresent = true;
            }

            IEnumerable<string> unknownOptionArguments = args.Where(argument => !optionArguments.Any(optionArgument => argument == (optionArgumentPrefix + optionArgument)));

            if(unknownOptionArguments.Count() > 0)
            {
                Console.WriteLine($"Unknown option argument{((unknownOptionArguments.Count() > 1) ? "s" : "")}: {String.Join(", ", unknownOptionArguments)}");

                goto mainExit;
            }

            bool acquiredMutex = false;

            Mutex mutex;

            if(isConsoleWindowPresent) Console.Title = DAEMON_NAME;

            mutex = new Mutex(true, DAEMON_NAME, out acquiredMutex);

            if (!acquiredMutex)
            {
                string parentProcessExecutablePath = "{null}";

                Process currentProcess = Process.GetCurrentProcess();

                if(Environment.Is64BitProcess == Environment.Is64BitOperatingSystem)
                {
                    Process parentProcess = ParentProcessUtilities.GetParentProcess(currentProcess.Id);

                    if(parentProcess != null)
                    {
                        parentProcessExecutablePath = parentProcess.MainModule.FileName;
                    }
                } else
                {
                    parentProcessExecutablePath = "{UnsupportedOperationNull}";
                }

                sendNotification(DAEMON_NAME, $"Another instance of {currentProcess.MainModule.ModuleName} is already in execution.{((parentProcessExecutablePath != "{null}") ? ("\nSpawned by: \"" + parentProcessExecutablePath + "\"") : "")}", displayDateTime: true);

                goto mainExit;
            }

            registry = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\" + DAEMON_EXTENDED_NAME);

            if(registry == null)
            {
                sendNotification(DAEMON_NAME, "Couldn't create or open the registry.");

                goto mainExit;
            }

            registryValuesSnapshot = new Dictionary<string, object>();

            foreach(RegistryDefaultValue regDefValue in registryDefaultValues)
            {
                object value = null;
                RegistryValueKind? valueKind = null;

                Exception registryException = null;

                try
                {
                    value = registry.GetValue(regDefValue.Name);

                    if(value != null) valueKind = registry.GetValueKind(regDefValue.Name);
                } catch(Exception exc)
                {
                    registryException = exc;

                    goto registryException;
                }

                if((value == null) || (valueKind != regDefValue.Kind))
                {
                    value = regDefValue.DefaultValue;

                    try
                    {
                        registry.SetValue(regDefValue.Name, value);
                    } catch(Exception exc)
                    {
                        registryException = exc;

                        goto registryException;
                    }
                } else if(regDefValue.ValueChecker != null)
                {
                    object tempValue = regDefValue.ValueChecker(value);

                    if(tempValue != null)
                    {
                        value = tempValue;

                        try
                        {
                            registry.SetValue(regDefValue.Name, value);
                        }
                        catch (Exception exc)
                        {
                            registryException = exc;

                            goto registryException;
                        }
                    }
                }

                registryValuesSnapshot.Add(regDefValue.Name, value);

            registryException:
                if (registryException != null)
                {
                    sendNotification(DAEMON_NAME, $"Couldn't manage the registry: \"{registryException.Message}\", exiting from Main() function.");

                    goto mutexExit;
                }
            }

            bool enableTelemetry = args.Any(argument => argument == (optionArgumentPrefix + "enableTelemetry"));

            if (enableTelemetry && !threadFuncs.Any(func => func == telemetryThread)) threadFuncs = threadFuncs.Prepend(telemetryThread).ToArray();

            exitEvent = new ManualResetEvent(false);

            Thread[] threads = new Thread[threadFuncs.Length];

            bool exitEventInvoked = false;
            bool enableFastExit = false;

            sendNotification(DAEMON_NAME, "Running...", displayDateTime: true);

            for (uint i = 0; i < threads.Length; i++)
            {
                uint capturedIndex = i;

                threads[i] = new Thread(() => threadFuncs[capturedIndex]());

                threads[i].Start();
            }

            Console.CancelKeyPress += (sender, ev) => {
                if (WAIT_MAIN_EXIT_NOTIFICATION && enableFastExit && !acquiredMutex)
                {
                    Environment.ExitCode = 0;

                    return;
                }

                ev.Cancel = true;

                if (exitEventInvoked) return;

                exitEventInvoked = true;

                exitEvent.Set();

                exitEventBool = true;
            };

            for (uint i = 0; i < threads.Length; i++)
            {
                threads[i].Join();
            }

            sendNotification(DAEMON_NAME, "All threads have finished, exiting from Main() function.", displayDateTime: true);

            enableFastExit = true;

        mutexExit:
            if (registry != null) registry.Close();

            mutex.Close();

            acquiredMutex = false;

        mainExit:
            if (WAIT_MAIN_EXIT_NOTIFICATION) Thread.Sleep(WAIT_MAIN_EXIT_NOTIFICATION_MS);
        }
    }
}
