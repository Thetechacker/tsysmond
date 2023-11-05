using System;
using System.IO;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.Win32;
using OpenHardwareMonitor.Hardware;
using PowerManagement;

namespace TexSystemMonitor {
    using threadfn = System.UInt32;

    class TexSystemMonitor {
        public const string DAEMON_NAME = "tsysmond";
        public const string DAEMON_DESCRIPTIVE_NAME = "Tex System Monitor Daemon";

        public const bool WAIT_MAIN_EXIT_NOTIFICATION = true;
        public const int WAIT_MAIN_EXIT_NOTIFICATION_MS = 3000;

        public const bool CPU_TEMPERATURE_SAFE_STARTUP = true;
        public const string CPU_TEMPERATURE_SHUTDOWN_LOCKFILE = @"C:\cpuTempShutdown." + DAEMON_NAME + ".lock";
        public const int CPU_TEMPERATURE_POLLING_RATE_MS = 1000;

        public const uint SAFE_CPU_TEMPERATURE_CELSIUS = 65;
        public const uint CRITICAL_CPU_TEMPERATURE_CELSIUS = 80;
        public const uint DANGEROUS_CPU_TEMPERATURE_CELSIUS = 85;

        public static Icon InternalApplicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        public static ManualResetEvent exitEvent = new ManualResetEvent(false);
        public static bool exitEventBool = false;

        public static void sendNotification(string title, string content, Icon icon, int notificationPersistMs, bool displayDateTime, bool dispose){
            NotifyIcon notifyIcon = new NotifyIcon();

            notifyIcon.Icon = icon;

            notifyIcon.Visible = true;

            notifyIcon.BalloonTipTitle = title;
            notifyIcon.BalloonTipText = $"{(displayDateTime ? ("[" + DateTime.Now.ToString() + "] ") : "")}{content}";

            notifyIcon.ShowBalloonTip(dispose ? 0 : notificationPersistMs);

            Console.WriteLine($"[{(displayDateTime ? (DateTime.Now.ToString() + " | ") : "")}BalloonNotification] {title}: {content}");

            if(dispose){
                Task.Run(() => {
                    Thread.Sleep(notificationPersistMs);
                    notifyIcon.Dispose();
                });
            }
        }

        public static threadfn cpuTempSafetyMeasure(){
            string THREAD_NAME = DAEMON_NAME + ":" + MethodBase.GetCurrentMethod().Name;

            Computer computer = new Computer();
            bool notifiedCriticalTemperature = false;
            bool noSensors = false;
            bool safeStartupChecked = false;

            computer.CPUEnabled = true;

            computer.Open();

            while(!exitEventBool){
                float? maxTemp = 0;
                bool foundSensors = false;

                foreach(IHardware hardware in computer.Hardware){
                    if(hardware.HardwareType == HardwareType.CPU){
                        hardware.Update();

                        if(hardware.Sensors.Length > 0){
                            foundSensors = true;
                        }

                        foreach(ISensor sensor in hardware.Sensors){
                            if(sensor.SensorType == SensorType.Temperature){
                                if(sensor.Value > maxTemp) maxTemp = sensor.Value;
                            }
                        }
                    }

                    break;
                }

                if(!foundSensors){
                    noSensors = true;

                    goto exitWhile;
                }

                if(CPU_TEMPERATURE_SAFE_STARTUP && !safeStartupChecked){
                    if(File.Exists(CPU_TEMPERATURE_SHUTDOWN_LOCKFILE)){
                        if(maxTemp <= SAFE_CPU_TEMPERATURE_CELSIUS){
                            try {
                                File.Delete(CPU_TEMPERATURE_SHUTDOWN_LOCKFILE);
                            } catch(Exception err){
                                sendNotification(THREAD_NAME, $"Couldn't delete the lock file: {err.Message}, terminating thread...", InternalApplicationIcon, 3000, true, false);

                                break;
                            }
                        } else {
                            if(!PowerUtilities.ExitWindows(ShutdownType.Shutdown, ShutdownReason.MajorSystem, true)){
                                sendNotification(THREAD_NAME, "The CPU temperature is still not safe and couldn't shutdown, terminating thread...", InternalApplicationIcon, 3000, true, false);
                            }

                            break;
                        }
                    }

                    safeStartupChecked = true;
                }

                if(maxTemp >= DANGEROUS_CPU_TEMPERATURE_CELSIUS){
                    sendNotification(THREAD_NAME, $"Reached dangerous temperature: {maxTemp}°C | Shutting down...", InternalApplicationIcon, 3000, true, false);

                    if(CPU_TEMPERATURE_SAFE_STARTUP){
                        try {
                            File.Create(CPU_TEMPERATURE_SHUTDOWN_LOCKFILE);
                        } catch(Exception err){
                            sendNotification(THREAD_NAME, $"Couldn't create the shutdown lock file: {err.Message}", InternalApplicationIcon, 3000, true, false);
                        }
                    }

                    if(!PowerUtilities.ExitWindows(ShutdownType.Shutdown, ShutdownReason.MajorSystem, true)){
                        sendNotification(THREAD_NAME, "Shutdown failed, terminating thread...", InternalApplicationIcon, 3000, true, false);
                    }

                    break;
                } else if((maxTemp >= CRITICAL_CPU_TEMPERATURE_CELSIUS) && !notifiedCriticalTemperature){
                    sendNotification(THREAD_NAME, $"Reached critical temperature: {maxTemp}°C", InternalApplicationIcon, 3000, true, false);

                    notifiedCriticalTemperature = true;
                } else if(notifiedCriticalTemperature && (maxTemp < CRITICAL_CPU_TEMPERATURE_CELSIUS)){
                    notifiedCriticalTemperature = false;

                    sendNotification(THREAD_NAME, $"Cooling down: {maxTemp}°C", InternalApplicationIcon, 3000, true, false);
                }

                Thread.Sleep(CPU_TEMPERATURE_POLLING_RATE_MS);
            }

            exitWhile:
                computer.Close();

                if(noSensors){
                    sendNotification(THREAD_NAME, "No sensors to check, terminating thread...", InternalApplicationIcon, 3000, true, false);
                }

                return 0;
        }

        public static threadfn powerSupplySwitchAlerter(){
            string THREAD_NAME = DAEMON_NAME + ":" + MethodBase.GetCurrentMethod().Name;

            PowerModeChangedEventHandler powerModeChangedHandler = (_, ev) => {
                if(ev.Mode == PowerModes.StatusChange){
                    PowerLineStatus powerLineStatus = SystemInformation.PowerStatus.PowerLineStatus;

                    if(powerLineStatus == PowerLineStatus.Unknown){
                        sendNotification(THREAD_NAME, "Power status is unknown.", InternalApplicationIcon, 3000, true, false);
                    } else {
                        sendNotification(THREAD_NAME, $"Computer is now running on {((powerLineStatus == PowerLineStatus.Online) ? $"AC{(((SystemInformation.PowerStatus.BatteryChargeStatus & BatteryChargeStatus.Charging) > 0) ? " (Battery in charge)" : "")}" : ((powerLineStatus == PowerLineStatus.Offline) ? "battery" : "<UNHANDLED_CASE>"))} power.", InternalApplicationIcon, 3000, true, false);
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

        public static void cleanup(){
            exitEvent.Set();

            exitEventBool = true;
        }

        public static void Main(string[] args){
            Console.Title = DAEMON_NAME;

            Func<threadfn>[] threadFuncs = new Func<threadfn>[]{ cpuTempSafetyMeasure, powerSupplySwitchAlerter };
            Thread[] threads = new Thread[threadFuncs.Length];
            bool cleanupInvoked = false;
            bool enableFastExit = false;

            sendNotification(DAEMON_NAME, "Running...", InternalApplicationIcon, 3000, true, false);

            for(uint i = 0; i < threads.Length; i++){
                uint capturedIndex = i;

                threads[i] = new Thread(() => threadFuncs[capturedIndex]());

                threads[i].Start();
            }

            Console.CancelKeyPress += (sender, ev) => { // Flag
                if(WAIT_MAIN_EXIT_NOTIFICATION && enableFastExit){
                    Environment.ExitCode = 0;

                    return;
                }

                ev.Cancel = true;

                if(cleanupInvoked) return;

                cleanupInvoked = true;

                cleanup();
            };

            for(uint i = 0; i < threads.Length; i++){
                threads[i].Join();
            }

            sendNotification(DAEMON_NAME, "All threads have finished, exiting from Main() function.", InternalApplicationIcon, WAIT_MAIN_EXIT_NOTIFICATION_MS, true, false);

            if(WAIT_MAIN_EXIT_NOTIFICATION){
                enableFastExit = true;

                Thread.Sleep(WAIT_MAIN_EXIT_NOTIFICATION_MS + 1000);
            };
        }
    }
}