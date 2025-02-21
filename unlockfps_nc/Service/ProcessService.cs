using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using unlockfps_nc.Model;
using unlockfps_nc.Utility;

namespace unlockfps_nc.Service
{
    public class ProcessService : IDisposable
    {
        private static readonly Native.WinEventProc _eventCallback;
        private static readonly uint[] PriorityClass = {
            0x00000100, // REALTIME_PRIORITY_CLASS
            0x00000080, // HIGH_PRIORITY_CLASS
            0x00008000, // ABOVE_NORMAL_PRIORITY_CLASS
            0x00000020, // NORMAL_PRIORITY_CLASS
            0x00004000, // BELOW_NORMAL_PRIORITY_CLASS
            0x00000040  // IDLE_PRIORITY_CLASS
        };

        private const uint EVENT_SYSTEM_FOREGROUND = 3;
        private const uint WINEVENT_OUTOFCONTEXT = 0;

        private CancellationTokenSource _cts;
        private readonly IntPtr _winEventHook;
        private readonly GCHandle _pinnedCallback;
        private IntPtr _gameHandle;
        private IntPtr _remoteUnityPlayer;
        private IntPtr _remoteUserAssembly;
        private int _gamePid;
        private bool _gameInForeground = true;
        private bool _failover;
        private bool _disposed;

        private IntPtr _pFpsValue;

        private readonly ConfigService _configService;
        private readonly Config _config;
        private readonly IpcService _ipcService;

        static ProcessService()
        {
            _eventCallback = WinEventProc;
        }

        public ProcessService(ConfigService configService, IpcService ipcService)
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _config = _configService.Config;
            _ipcService = ipcService ?? throw new ArgumentNullException(nameof(ipcService));

            _pinnedCallback = GCHandle.Alloc(_eventCallback, GCHandleType.Normal);
            _winEventHook = Native.SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _eventCallback,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);
            
            _cts = new CancellationTokenSource();
        }

        private static void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // Implementation will be updated through instance method
        }

        public bool Start()
        {
            if (!ValidateGamePath())
                return false;

            if (IsGameRunning())
            {
                MessageBox.Show("An instance of the game is already running.", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            CleanupPreviousInstance();
            _failover = false;
            _ipcService.Stop();

            KillExistingGameProcesses();
            Task.Run(Worker, _cts.Token);
            return true;
        }

        private bool ValidateGamePath()
        {
            if (File.Exists(_config.GamePath))
                return true;
            
            MessageBox.Show("Game path is invalid.", "Error", 
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        private void CleanupPreviousInstance()
        {
            if (_gameHandle != IntPtr.Zero)
            {
                Native.CloseHandle(_gameHandle);
                _gameHandle = IntPtr.Zero;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
        }

        private void KillExistingGameProcesses()
        {
            var gameProcesses = Process.GetProcesses()
                .Where(x => x.ProcessName is "GenshinImpact" or "YuanShen");
                
            foreach (var process in gameProcesses)
            {
                try
                {
                    process.Kill();
                    process.Dispose();
                }
                catch (Exception)
                {
                    // Log exception or continue
                }
            }
        }

        public void OnFormClosing()
        {
            Dispose();
        }

        private void UpdateWinEventProc(uint eventType, IntPtr hWnd, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (eventType != EVENT_SYSTEM_FOREGROUND || _gameHandle == IntPtr.Zero)
                return;

            Native.GetWindowThreadProcessId(hWnd, out var pid);
            _gameInForeground = pid == _gamePid;

            if (!_config.UsePowerSave)
                return;

            uint targetPriority = _gameInForeground ? 
                PriorityClass[_config.Priority] : PriorityClass[5]; // Use IDLE_PRIORITY_CLASS when in background
            Native.SetPriorityClass(_gameHandle, targetPriority);
        }

        private bool IsGameRunning()
        {
            if (_gameHandle == IntPtr.Zero)
                return false;

            if (!Native.GetExitCodeProcess(_gameHandle, out var exitCode))
                return false;

            return exitCode == 259; // STILL_ACTIVE
        }

        private async Task Worker()
        {
            try
            {
                STARTUPINFO si = new();
                uint creationFlag = _config.SuspendLoad ? 4u : 0u; // CREATE_SUSPENDED
                var gameFolder = Path.GetDirectoryName(_config.GamePath);

                if (!LaunchGameProcess(gameFolder, ref si, out var pi))
                    return;

                if (_config.SuspendLoad)
                    Native.ResumeThread(pi.hThread);

                _gamePid = pi.dwProcessId;
                _gameHandle = pi.hProcess;

                Native.CloseHandle(pi.hThread);

                await WaitForGameWindow();

                if (!SetupData())
                    return;

                await RunMainLoop();
            }
            catch (OperationCanceledException)
            {
                // Task was canceled, clean up
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error in worker thread: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool LaunchGameProcess(string gameFolder, ref STARTUPINFO si, out PROCESS_INFORMATION pi)
        {
            string commandLine = BuildCommandLine();
            bool result = Native.CreateProcess(
                _config.GamePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                _config.SuspendLoad ? 4u : 0u, // CREATE_SUSPENDED
                IntPtr.Zero,
                gameFolder,
                ref si,
                out pi);

            if (!result)
            {
                MessageBox.Show(
                    $"CreateProcess failed ({Marshal.GetLastWin32Error()})\n{Marshal.GetLastPInvokeErrorMessage()}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (!InjectDlls(pi.hProcess))
                return false;

            return true;
        }

        private bool InjectDlls(IntPtr hProcess)
        {
            if (!ProcessUtils.InjectDlls(hProcess, _config.DllList))
            {
                MessageBox.Show(
                    $"Dll Injection failed ({Marshal.GetLastWin32Error()})\n{Marshal.GetLastPInvokeErrorMessage()}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            return true;
        }

        private async Task WaitForGameWindow()
        {
            var token = _cts.Token;
            const int timeoutMs = 30000; // 30 seconds timeout
            const int checkIntervalMs = 100;
            int elapsedMs = 0;

            while (elapsedMs < timeoutMs)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException();

                if (ProcessUtils.GetWindowFromProcessId(_gamePid) != IntPtr.Zero)
                    return;

                await Task.Delay(checkIntervalMs, token);
                elapsedMs += checkIntervalMs;
            }

            throw new TimeoutException("Timed out waiting for game window");
        }

        private async Task RunMainLoop()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested && IsGameRunning())
            {
                try
                {
                    ApplyFpsLimit();
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            HandleGameExit();
        }

        private void HandleGameExit()
        {
            if (IsGameRunning())
                return;

            _ipcService.Stop();
            _pFpsValue = IntPtr.Zero;
            _gameHandle = IntPtr.Zero;

            if (_config.AutoClose)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    Application.Exit();
                });
            }
        }

        private void ApplyFpsLimit()
        {
            if (_pFpsValue == IntPtr.Zero)
                return;

            int fpsTarget = CalculateFpsTarget();

            if (!_failover)
            {
                if (!WriteMemoryDirectly(fpsTarget) && IsGameRunning())
                {
                    // Use IPC service as fallback if direct memory write fails
                    if (Marshal.GetLastWin32Error() == 5) // ERROR_ACCESS_DENIED
                    {
                        _ipcService.Start(_gamePid, _pFpsValue);
                        _failover = true;
                    }
                }
            }
            else
            {
                _ipcService.ApplyFpsLimit(fpsTarget);
            }
        }

        private int CalculateFpsTarget()
        {
            if (!_gameInForeground && _config.UsePowerSave)
                return 10;
            
            return _config.FPSTarget;
        }

        private bool WriteMemoryDirectly(int fpsTarget)
        {
            var toWrite = BitConverter.GetBytes(fpsTarget);
            return Native.WriteProcessMemory(_gameHandle, _pFpsValue, toWrite, 4, out _);
        }

        private string BuildCommandLine()
        {
            var builder = new System.Text.StringBuilder();
            
            builder.Append($"\"{_config.GamePath}\" ");
            
            if (_config.PopupWindow)
                builder.Append("-popupwindow ");

            if (_config.UseCustomRes)
                builder.Append($"-screen-width {_config.CustomResX} -screen-height {_config.CustomResY} ");

            builder.Append($"-screen-fullscreen {(_config.Fullscreen ? 1 : 0)} ");
            
            if (_config.Fullscreen)
                builder.Append($"-window-mode {(_config.IsExclusiveFullscreen ? "exclusive" : "borderless")} ");

            if (_config.UseMobileUI)
                builder.Append("use_mobile_platform -is_cloud 1 -platform_type CLOUD_THIRD_PARTY_MOBILE ");

            builder.Append($"-monitor {_config.MonitorNum} ");
            builder.Append(_config.AdditionalCommandLine);
            
            return builder.ToString();
        }

        private unsafe bool SetupData()
        {
            var gameDir = Path.GetDirectoryName(_config.GamePath);
            var gameName = Path.GetFileNameWithoutExtension(_config.GamePath);
            var dataDir = Path.Combine(gameDir, $"{gameName}_Data");

            var unityPlayerPath = Path.Combine(gameDir, "UnityPlayer.dll");
            var userAssemblyPath = Path.Combine(dataDir, "Native", "UserAssembly.dll");

            // Check if both modules exist
            bool unityPlayerExists = File.Exists(unityPlayerPath);
            bool userAssemblyExists = File.Exists(userAssemblyPath);

            // Try alternate method if files don't exist
            if (!unityPlayerExists && !userAssemblyExists && SetupDataEx())
                return true;

            // Load modules
            using ModuleGuard pUnityPlayer = unityPlayerExists ? 
                Native.LoadLibraryEx(unityPlayerPath, IntPtr.Zero, 32) : null;
            using ModuleGuard pUserAssembly = userAssemblyExists ? 
                Native.LoadLibraryEx(userAssemblyPath, IntPtr.Zero, 32) : null;

            if (pUnityPlayer == null || pUserAssembly == null)
            {
                MessageBox.Show(
                    "Failed to load UnityPlayer.dll or UserAssembly.dll",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (!UpdateRemoteModules())
                return false;

            // Find FPS value pointer based on version
            if (!TryGetFpsPointer(pUnityPlayer, pUserAssembly))
            {
                MessageBox.Show(
                    "Outdated FPS pattern - application needs to be updated for this game version",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            return true;
        }

        private unsafe bool TryGetFpsPointer(ModuleGuard pUnityPlayer, ModuleGuard pUserAssembly)
        {
            // Get UnityPlayer version info
            var dosHeader = Marshal.PtrToStructure<IMAGE_DOS_HEADER>(pUnityPlayer);
            var ntHeader = Marshal.PtrToStructure<IMAGE_NT_HEADERS>(
                (IntPtr)(pUnityPlayer.BaseAddress.ToInt64() + dosHeader.e_lfanew));

            if (ntHeader.FileHeader.TimeDateStamp < 0x656FFAF7U) // < 3.7
            {
                return FindPreVersion37FpsPointer(pUnityPlayer);
            }
            else
            {
                return FindPostVersion37FpsPointer(pUnityPlayer, pUserAssembly, ntHeader);
            }
        }

        private unsafe bool FindPreVersion37FpsPointer(ModuleGuard pUnityPlayer)
        {
            byte* address = (byte*)ProcessUtils.PatternScan(pUnityPlayer, "7F 0F 8B 05 ? ? ? ?");
            if (address == null)
                return false;

            byte* rip = address + 2;
            int rel = *(int*)(rip + 2);
            var localVa = rip + rel + 6;
            var rva = localVa - pUnityPlayer.BaseAddress.ToInt64();
            _pFpsValue = (IntPtr)(_remoteUnityPlayer.ToInt64() + rva);
            return true;
        }

        private unsafe bool FindPostVersion37FpsPointer(
            ModuleGuard pUnityPlayer, 
            ModuleGuard pUserAssembly,
            IMAGE_NT_HEADERS ntHeader)
        {
            byte* rip;
            
            if (ntHeader.FileHeader.TimeDateStamp < 0x656FFAF7U) // < 4.3
            {
                byte* address = (byte*)ProcessUtils.PatternScan(
                    pUserAssembly, "E8 ? ? ? ? 85 C0 7E 07 E8 ? ? ? ? EB 05");
                if (address == null)
                    return false;

                rip = address;
                rip += *(int*)(rip + 1) + 5;
                rip += *(int*)(rip + 3) + 7;
            }
            else
            {
                byte* address = (byte*)ProcessUtils.PatternScan(
                    pUserAssembly, "B9 3C 00 00 00 FF 15");
                if (address == null)
                    return false;

                rip = address;
                rip += 5;
                rip += *(int*)(rip + 2) + 6;
            }

            // Calculate remote address and read memory pointer
            byte* remoteVa = rip - pUserAssembly.BaseAddress.ToInt64() + _remoteUserAssembly.ToInt64();
            byte* dataPtr = null;

            const int maxRetries = 5;
            for (int retry = 0; retry < maxRetries && dataPtr == null; retry++)
            {
                byte[] readResult = new byte[8];
                if (!Native.ReadProcessMemory(_gameHandle, (IntPtr)remoteVa, readResult, readResult.Length, out _))
                {
                    if (retry == maxRetries - 1)
                        return false;
                    
                    Thread.Sleep(200);
                    continue;
                }

                ulong value = BitConverter.ToUInt64(readResult, 0);
                if (value != 0)
                    dataPtr = (byte*)value;
                else if (retry < maxRetries - 1)
                    Thread.Sleep(200);
            }

            if (dataPtr == null)
                return false;

            // Follow pointer chain to find FPS value location
            byte* localVa = dataPtr - _remoteUnityPlayer.ToInt64() + pUnityPlayer.BaseAddress.ToInt64();
            
            const int maxJumps = 10; // Prevent infinite loops
            int jumpCount = 0;
            while ((localVa[0] == 0xE8 || localVa[0] == 0xE9) && jumpCount < maxJumps)
            {
                localVa += *(int*)(localVa + 1) + 5;
                jumpCount++;
            }

            if (jumpCount >= maxJumps)
                return false;

            localVa += *(int*)(localVa + 2) + 6;
            var rva = localVa - pUnityPlayer.BaseAddress.ToInt64();
            _pFpsValue = (IntPtr)(_remoteUnityPlayer.ToInt64() + rva);
            return true;
        }

        private unsafe bool SetupDataEx()
        {
            var gameName = Path.GetFileNameWithoutExtension(_config.GamePath);
            var remoteExe = ProcessUtils.GetModuleBase(_gameHandle, $"{gameName}.exe");
            if (remoteExe == IntPtr.Zero)
                return false;

            using ModuleGuard pGameExecutable = Native.LoadLibraryEx(_config.GamePath, IntPtr.Zero, 32);
            if (pGameExecutable == null)
                return false;

            var vaResults = ProcessUtils.PatternScanAllOccurrences(pGameExecutable, "B9 3C 00 00 00 E8");
            if (vaResults.Count == 0)
                return false;

            byte* localVa = null;
            foreach (var result in vaResults)
            {
                var candidate = (byte*)result + 5;
                candidate += *(int*)(candidate + 1) + 5;
                
                if (*(byte*)candidate == 0xE9)
                {
                    localVa = candidate;
                    break;
                }
            }

            if (localVa == null)
                return false;

            const int maxJumps = 10;
            int jumpCount = 0;
            while ((localVa[0] == 0xE8 || localVa[0] == 0xE9) && jumpCount < maxJumps)
            {
                localVa += *(int*)(localVa + 1) + 5;
                jumpCount++;
            }

            if (jumpCount >= maxJumps)
                return false;

            localVa += *(int*)(localVa + 2) + 6;
            var rva = localVa - pGameExecutable.BaseAddress.ToInt64();
            _pFpsValue = (IntPtr)(remoteExe.ToInt64() + rva);

            return true;
        }

        private bool UpdateRemoteModules()
        {
            const int maxRetries = 10;
            const int retryDelayMs = 2000;

            for (int retry = 0; retry <= maxRetries; retry++)
            {
                _remoteUnityPlayer = ProcessUtils.GetModuleBase(_gameHandle, "UnityPlayer.dll");
                _remoteUserAssembly = ProcessUtils.GetModuleBase(_gameHandle, "UserAssembly.dll");

                if (_remoteUnityPlayer != IntPtr.Zero && _remoteUserAssembly != IntPtr.Zero)
                    return true;

                if (retry == maxRetries)
                    break;

                try
                {
                    Task.Delay(retryDelayMs, _cts.Token).Wait();
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            MessageBox.Show(
                "Failed to get remote module base address after multiple attempts",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }

            _ipcService?.Stop();
            Native.UnhookWinEvent(_winEventHook);
            
            if (_gameHandle != IntPtr.Zero)
            {
                Native.CloseHandle(_gameHandle);
                _gameHandle = IntPtr.Zero;
            }

            if (_pinnedCallback.IsAllocated)
                _pinnedCallback.Free();

            _disposed = true;
        }

        ~ProcessService()
        {
            Dispose(false);
        }
    }
}
