using System;
using System.IO;
using System.Windows.Forms;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Toolkit.Uwp.Notifications;
using OpenHardwareMonitor.Hardware;
using PowerManagement;

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

    class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FreeConsole();
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        public const string DAEMON_NAME = "tsysmond";
        // public const string DAEMON_DESCRIPTIVE_NAME = "Tex System Monitor Daemon";

        public const bool WAIT_MAIN_EXIT_NOTIFICATION = true;
        public const int WAIT_MAIN_EXIT_NOTIFICATION_MS = 3000;

        public const bool CPU_TEMPERATURE_SAFE_STARTUP = true;
        public const string CPU_TEMPERATURE_SHUTDOWN_LOCKFILE = @"C:\cpuTempShutdown." + DAEMON_NAME + ".lock";
        public const int CPU_TEMPERATURE_POLLING_RATE_MS = 1000;

        public const uint SAFE_CPU_TEMPERATURE_CELSIUS = 65;
        public const uint CRITICAL_CPU_TEMPERATURE_CELSIUS = 80;
        public const uint DANGEROUS_CPU_TEMPERATURE_CELSIUS = 85;

        // public static Icon InternalApplicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        public static readonly string[] optionArguments = { "console" };

        public static ManualResetEvent exitEvent = new ManualResetEvent(false);
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

                if (CPU_TEMPERATURE_SAFE_STARTUP && !safeStartupChecked)
                {
                    if (File.Exists(CPU_TEMPERATURE_SHUTDOWN_LOCKFILE))
                    {
                        if (maxTemp <= SAFE_CPU_TEMPERATURE_CELSIUS)
                        {
                            try
                            {
                                File.Delete(CPU_TEMPERATURE_SHUTDOWN_LOCKFILE);
                            }
                            catch (Exception err)
                            {
                                sendNotification(DAEMON_NAME, $"Couldn't delete the lock file: {err.Message}, terminating thread...", THREAD_NAME, true);

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
                    }

                    safeStartupChecked = true;
                }

                if (maxTemp >= DANGEROUS_CPU_TEMPERATURE_CELSIUS)
                {
                    sendNotification(DAEMON_NAME, $"Reached dangerous temperature: {maxTemp}°C | Shutting down...", THREAD_NAME, true);

                    if (CPU_TEMPERATURE_SAFE_STARTUP)
                    {
                        try
                        {
                            File.Create(CPU_TEMPERATURE_SHUTDOWN_LOCKFILE).Close(); // Flag
                        }
                        catch (Exception err)
                        {
                            sendNotification(DAEMON_NAME, $"Couldn't create the shutdown lock file: {err.Message}", THREAD_NAME, true);
                        }
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

        public static void cleanup()
        {
            exitEvent.Set();

            exitEventBool = true;
        }

        [STAThread]
        public static void Main(string[] args)
        {
            bool isConsoleWindowPresent = GetConsoleWindow() != IntPtr.Zero;
            bool createConsoleWindow = Array.Exists(args, argument => argument == "--console");
            bool consoleWindowCreated = false;

            if (!isConsoleWindowPresent && createConsoleWindow)
            {
                if (!AllocConsole()) return;

                consoleWindowCreated = true;
            }

            string[] unknownOptionArguments = Array.FindAll(args, argument => !Array.Exists(optionArguments, optionArgument => argument == ("--" + optionArgument)));

            if(unknownOptionArguments.Length > 0)
            {
                Console.WriteLine($"Unknown option argument{((unknownOptionArguments.Length > 1) ? "s" : "")}: {String.Join(", ", unknownOptionArguments)}");

                goto mainExit;
            }

            bool acquiredMutex = false;
            Mutex mutex;

            if(isConsoleWindowPresent || consoleWindowCreated) Console.Title = DAEMON_NAME;

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

            Func<threadfn>[] threadFuncs = new Func<threadfn>[] { cpuTempSafetyMeasure, powerSupplySwitchAlerter };
            Thread[] threads = new Thread[threadFuncs.Length];
            bool cleanupInvoked = false;
            bool enableFastExit = false;

            sendNotification(DAEMON_NAME, "Running...", displayDateTime: true);

            for (uint i = 0; i < threads.Length; i++)
            {
                uint capturedIndex = i;

                threads[i] = new Thread(() => threadFuncs[capturedIndex]());

                threads[i].Start();
            }

            Console.CancelKeyPress += (sender, ev) => { // Flag
                if (WAIT_MAIN_EXIT_NOTIFICATION && enableFastExit)
                {
                    Environment.ExitCode = 0;

                    return;
                }

                ev.Cancel = true;

                if (cleanupInvoked) return;

                cleanupInvoked = true;

                cleanup();
            };

            for (uint i = 0; i < threads.Length; i++)
            {
                threads[i].Join();
            }

            sendNotification(DAEMON_NAME, "All threads have finished, exiting from Main() function.", displayDateTime: true);

            mutex.Close();

            enableFastExit = true;

            mainExit:
                if(WAIT_MAIN_EXIT_NOTIFICATION) Thread.Sleep(WAIT_MAIN_EXIT_NOTIFICATION_MS);

                if (consoleWindowCreated) FreeConsole();
        }
    }
}
