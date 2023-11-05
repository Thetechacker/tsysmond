using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Diagnostics;

// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-exitwindowsex

public enum ShutdownType : uint {
    HybridShutdown = 0x00400000,
    LogOff = 0x00,
    PowerOff = 0x00000008,
    Reboot = 0x00000002,
    RestartApps = 0x00000040,
    Shutdown = 0x00000001,

    Force = 0x00000004,
    ForceIfHung = 0x00000010
}

// https://learn.microsoft.com/en-us/windows/win32/shutdown/system-shutdown-reason-codes

public enum ShutdownReason : uint {
    MajorApplication = 0x00040000,
    MajorHardware = 0x00010000,
    MajorLegacyAPI = 0x00070000,
    MajorOperatingSystem = 0x00020000,
    MajorOther = 0x00000000,
    MajorPower = 0x00060000,
    MajorSoftware = 0x00030000,
    MajorSystem = 0x00050000,

    MinorBluescreen = 0x0000000F,
    MinorCordUnplugged = 0x0000000B,
    MinorDisk = 0x00000007,
    MinorEnvironment = 0x0000000C,
    MinorHardwareDriver = 0x0000000D,
    MinorHotfix = 0x00000011,
    MinorHotfixUninstall = 0x00000017,
    MinorHung = 0x00000005,
    MinorInstallation = 0x00000002,
    MinorMaintenance = 0x00000001,
    MinorMMC = 0x00000019,
    MinorNetworkConnectivity = 0x00000014,
    MinorNetworkCard = 0x00000009,
    MinorOther = 0x00000000,
    MinorOtherDriver = 0x0000000E,
    MinorPowerSupply = 0x0000000A,
    MinorProcessor = 0x00000008,
    MinorReconfig = 0x00000004,
    MinorSecurity = 0x00000013,
    MinorSecurityFix = 0x00000012,
    MinorSecurityFixUninstall = 0x00000018,
    MinorServicePack = 0x00000010,
    MinorServicePackUninstall = 0x00000016,
    MinorTermSRV = 0x00000020,
    MinorUnstable = 0x00000006,
    MinorUpgrade = 0x00000003,
    MinorWMI = 0x00000015,

    FlagUserDefined = 0x40000000,
    FlagPlanned = 0x80000000
}

// https://stackoverflow.com/questions/24726116/when-using-exitwindowsex-logoff-works-but-shutdown-and-restart-do-not

public sealed class TokenAdjuster {
    private const int SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int TOKEN_ADJUST_PRIVILEGES = 0X00000020;
    private const int TOKEN_QUERY = 0X00000008;
    private const int TOKEN_ALL_ACCESS = 0X001f01ff;
    private const int PROCESS_QUERY_INFORMATION = 0X00000400;

    [DllImport("advapi32", SetLastError = true), SuppressUnmanagedCodeSecurity]
    private static extern int OpenProcessToken(IntPtr ProcessHandle, int DesiredAccess, ref IntPtr TokenHandle);

    [DllImport("kernel32", SetLastError = true),
     SuppressUnmanagedCodeSecurity]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int AdjustTokenPrivileges(IntPtr TokenHandle, int DisableAllPrivileges, IntPtr NewState, int BufferLength, IntPtr PreviousState, ref int ReturnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, ref LUID lpLuid);

    public static bool EnablePrivilege(string lpszPrivilege, bool bEnablePrivilege){
        bool retval = false;
        int ltkpOld = 0;
        IntPtr hToken = IntPtr.Zero;

        TOKEN_PRIVILEGES tkp = new TOKEN_PRIVILEGES();
        tkp.Privileges = new int[3];

        TOKEN_PRIVILEGES tkpOld = new TOKEN_PRIVILEGES();
        tkpOld.Privileges = new int[3];

        LUID tLUID = new LUID();

        tkp.PrivilegeCount = 1;

        if(bEnablePrivilege){
            tkp.Privileges[2] = SE_PRIVILEGE_ENABLED;
        } else {
            tkp.Privileges[2] = 0;
        }

        if(LookupPrivilegeValue(null, lpszPrivilege, ref tLUID)){
            Process proc = Process.GetCurrentProcess();

            if(proc.Handle != IntPtr.Zero){
                if(OpenProcessToken(proc.Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, ref hToken) != 0){
                    tkp.PrivilegeCount = 1;
                    tkp.Privileges[2] = SE_PRIVILEGE_ENABLED;
                    tkp.Privileges[1] = tLUID.HighPart;
                    tkp.Privileges[0] = tLUID.LowPart;

                    const int bufLength = 256;
                    IntPtr tu = Marshal.AllocHGlobal(bufLength);

                    Marshal.StructureToPtr(tkp, tu, true);

                    if(AdjustTokenPrivileges(hToken, 0, tu, bufLength, IntPtr.Zero, ref ltkpOld) != 0){
                        if(Marshal.GetLastWin32Error() == 0){
                            retval = true;
                        }
                    }

                    TOKEN_PRIVILEGES tokp = (TOKEN_PRIVILEGES)Marshal.PtrToStructure(tu, typeof(TOKEN_PRIVILEGES));
                    Marshal.FreeHGlobal(tu);
                }
            }
        }

        if(hToken != IntPtr.Zero){
            CloseHandle(hToken);
        }

        return retval;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID {
        internal int LowPart;
        internal int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES {
        private LUID Luid;
        private int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES {
        internal int PrivilegeCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        internal int[] Privileges;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct _PRIVILEGE_SET {
        private int PrivilegeCount;
        private int Control;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        private LUID_AND_ATTRIBUTES[] Privileges;
    }
}

namespace PowerManagement {
    public static class PowerUtilities {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ExitWindowsEx(ShutdownType uFlags, ShutdownReason dwReason);

        public static bool ExitWindows(ShutdownType shutdownType, ShutdownReason shutdownReason, bool ajustToken){
            if(ajustToken && !TokenAdjuster.EnablePrivilege("SeShutdownPrivilege", true)){
                return false;
            }

            return ExitWindowsEx(shutdownType, shutdownReason) != 0;
        }
    }
}