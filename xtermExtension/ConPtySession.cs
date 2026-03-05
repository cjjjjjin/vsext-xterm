using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace xtermExtension
{
    internal sealed class ConPtySession : IDisposable
    {
        private static readonly IntPtr ProcThreadAttributePseudoConsole = new IntPtr(22 | 0x20000);
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const int StartfUseStdHandles = 0x00000100;
        private const uint Infinite = 0xFFFFFFFF;
        private const uint HandleFlagInherit = 0x00000001;
        private const int S_OK = 0;

        private SafeFileHandle inputForPseudoConsole;
        private SafeFileHandle outputForPseudoConsole;
        private SafeFileHandle inputFromHost;
        private SafeFileHandle outputToHost;

        private FileStream inputStream;
        private FileStream outputStream;

        private IntPtr pseudoConsoleHandle;
        private IntPtr attributeList;
        private PROCESS_INFORMATION processInfo;

        private readonly CancellationTokenSource exitWatcherCts = new CancellationTokenSource();
        private bool disposed;

        public event EventHandler ProcessExited;

        public Stream InputStream
        {
            get { return inputStream; }
        }

        public Stream OutputStream
        {
            get { return outputStream; }
        }

        public ConPtySession(string appPath, string args, string cwd, int cols, int rows)
        {
            Initialize(appPath, args, cwd, cols, rows);
            StartExitWatcher();
        }

        public static bool IsSupported()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return false;
            }

            try
            {
                RTL_OSVERSIONINFOEX versionInfo = new RTL_OSVERSIONINFOEX();
                versionInfo.dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(RTL_OSVERSIONINFOEX));
                if (RtlGetVersion(ref versionInfo) != 0)
                {
                    return false;
                }

                return versionInfo.dwMajorVersion > 10
                    || (versionInfo.dwMajorVersion == 10 && versionInfo.dwBuildNumber >= 17763);
            }
            catch
            {
                return false;
            }
        }

        public void Resize(int cols, int rows)
        {
            if (disposed || pseudoConsoleHandle == IntPtr.Zero)
            {
                return;
            }

            short safeCols = (short)Math.Max(1, Math.Min(short.MaxValue, cols));
            short safeRows = (short)Math.Max(1, Math.Min(short.MaxValue, rows));
            int hr = ResizePseudoConsole(pseudoConsoleHandle, new COORD(safeCols, safeRows));
            if (hr != S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        public void Kill()
        {
            if (processInfo.hProcess != IntPtr.Zero)
            {
                TerminateProcess(processInfo.hProcess, 1);
            }
        }

        private void Initialize(string appPath, string args, string cwd, int cols, int rows)
        {
            try
            {
                CreatePipes();
                CreatePseudoConsoleHandle(cols, rows);
                CreateAttributeList();
                StartProcess(appPath, args, cwd);

                inputStream = new FileStream(inputFromHost, FileAccess.Write, 4096, false);
                outputStream = new FileStream(outputToHost, FileAccess.Read, 4096, false);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void CreatePipes()
        {
            SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
            sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
            sa.bInheritHandle = true;
            sa.lpSecurityDescriptor = IntPtr.Zero;

            if (!CreatePipe(out inputForPseudoConsole, out inputFromHost, ref sa, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create ConPTY input pipe.");
            }

            if (!CreatePipe(out outputToHost, out outputForPseudoConsole, ref sa, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create ConPTY output pipe.");
            }

            SetHandleInformation(inputFromHost, HandleFlagInherit, 0);
            SetHandleInformation(outputToHost, HandleFlagInherit, 0);
        }

        private void CreatePseudoConsoleHandle(int cols, int rows)
        {
            short safeCols = (short)Math.Max(1, Math.Min(short.MaxValue, cols));
            short safeRows = (short)Math.Max(1, Math.Min(short.MaxValue, rows));
            int hr = CreatePseudoConsole(
                new COORD(safeCols, safeRows),
                inputForPseudoConsole,
                outputForPseudoConsole,
                0,
                out pseudoConsoleHandle);

            if (hr != S_OK)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        private void CreateAttributeList()
        {
            IntPtr attributeListSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize thread attribute list.");
            }

            if (!UpdateProcThreadAttribute(
                attributeList,
                0,
                ProcThreadAttributePseudoConsole,
                pseudoConsoleHandle,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to attach pseudo console attribute.");
            }
        }

        private void StartProcess(string appPath, string args, string cwd)
        {
            STARTUPINFOEX startupInfo = new STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            startupInfo.StartupInfo.dwFlags = StartfUseStdHandles;
            startupInfo.lpAttributeList = attributeList;

            StringBuilder commandLine = new StringBuilder();
            if (!string.IsNullOrEmpty(appPath))
            {
                if (appPath.Contains(" "))
                {
                    commandLine.Append('"').Append(appPath).Append('"');
                }
                else
                {
                    commandLine.Append(appPath);
                }
            }

            if (!string.IsNullOrWhiteSpace(args))
            {
                commandLine.Append(' ').Append(args);
            }

            if (!CreateProcess(
                null,
                commandLine.ToString(),
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                IntPtr.Zero,
                cwd,
                ref startupInfo,
                out processInfo))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Failed to start process for ConPTY session.");
            }
        }

        private void StartExitWatcher()
        {
            _ = Task.Run(() =>
            {
                if (processInfo.hProcess == IntPtr.Zero)
                {
                    return;
                }

                WaitForSingleObject(processInfo.hProcess, Infinite);
                if (exitWatcherCts.IsCancellationRequested)
                {
                    return;
                }

                EventHandler handler = ProcessExited;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            });
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;

            try { exitWatcherCts.Cancel(); } catch { }
            try { inputStream?.Dispose(); } catch { }
            try { outputStream?.Dispose(); } catch { }
            try { inputFromHost?.Dispose(); } catch { }
            try { outputToHost?.Dispose(); } catch { }
            try { inputForPseudoConsole?.Dispose(); } catch { }
            try { outputForPseudoConsole?.Dispose(); } catch { }

            try
            {
                if (pseudoConsoleHandle != IntPtr.Zero)
                {
                    ClosePseudoConsole(pseudoConsoleHandle);
                    pseudoConsoleHandle = IntPtr.Zero;
                }
            }
            catch { }

            try
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                    attributeList = IntPtr.Zero;
                }
            }
            catch { }

            SafeCloseHandle(ref processInfo.hThread);
            SafeCloseHandle(ref processInfo.hProcess);

            try { exitWatcherCts.Dispose(); } catch { }
        }

        private static void SafeCloseHandle(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;

            public COORD(short x, short y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RTL_OSVERSIONINFOEX
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(
            out SafeFileHandle hReadPipe,
            out SafeFileHandle hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(
            SafeHandle hObject,
            uint dwMask,
            uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(
            COORD size,
            SafeHandle hInput,
            SafeHandle hOutput,
            uint dwFlags,
            out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            [In] ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEX versionInfo);
    }
}
