using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using unlockfps_nc.Utility;

namespace unlockfps_nc.Service
{
    public enum IpcStatus
    {
        Error = -1,
        None = 0,
        HostAwaiting = 1,
        ClientReady = 2,
        ClientExit = 3,
        HostExit = 4
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct IpcData
    {
        public ulong Address;
        public int Value;
        public IpcStatus Status;
    }

    public class IpcService : IDisposable
    {
        private bool _started = false;
        private IntPtr _pFpsValue = IntPtr.Zero;
        private MemoryMappedFile? _sharedMemory = null;
        private MemoryMappedViewAccessor? _sharedMemoryAccessor = null;
        private string _stubPath = string.Empty;
        private IntPtr _stubModule = IntPtr.Zero;
        private IntPtr _wndHook = IntPtr.Zero;
        private const int WH_CALLWNDPROC = 3;

        public async Task<void> Start(int processId, IntPtr pFpsValue)
        {
            if (_started)
            {
                return;
            }
            _pFpsValue = pFpsValue;

            using (_sharedMemory = MemoryMappedFile.CreateOrOpen("2DE95FDC-6AB7-4593-BFE6-760DD4AB422B", 1024, MemoryMappedFileAccess.ReadWrite))
            using (_sharedMemoryAccessor = _sharedMemory.CreateViewAccessor())
            {
                WriteToSharedMemory(_pFpsValue, 60, IpcStatus.HostAwaiting);

                _stubPath = GetUnlockerStubPath();
                _stubModule = Native.LoadLibrary(_stubPath);
                if (_stubModule == IntPtr.Zero)
                {
                    string errorMessage = $@"Failed to load stub module: {Marshal.GetLastWin32Error()}{Environment.NewLine}{Marshal.GetLastPInvokeErrorMessage()}";
                    MessageBox.Show(errorMessage, @"Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var stubWndProc = Native.GetProcAddress(_stubModule, "WndProc");
                var targetWindow = ProcessUtils.GetWindowFromProcessId(processId);
                var threadId = Native.GetWindowThreadProcessId(targetWindow, out uint _);

                _wndHook = Native.SetWindowsHookEx(WH_CALLWNDPROC, stubWndProc, _stubModule, threadId);
                if (_wndHook == IntPtr.Zero)
                {
                    string errorMessage = $@"Failed to set window hook: {Marshal.GetLastWin32Error()}{Environment.NewLine}{Marshal.GetLastPInvokeErrorMessage()}";
                    MessageBox.Show(errorMessage, @"Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!Native.PostThreadMessage(threadId, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    string errorMessage = $@"Failed to post thread message: {Marshal.GetLastWin32Error()}{Environment.NewLine}{Marshal.GetLastPInvokeErrorMessage()}";
                    MessageBox.Show(errorMessage, @"Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                int retryCount = 0;
                while (true)
                {
                    IpcData ipcData = new IpcData();
                    _sharedMemoryAccessor.Read(0, out ipcData);

                    if (ipcData.Status == IpcStatus.ClientReady)
                    {
                        break;
                    }

                    if (retryCount >= 5)
                    {
                        MessageBox.Show(@"Failed to start the unlocker.", @"Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    retryCount++;
                    await Task.Delay(500);
                }
            }

            _started = true;
        }

        public void ApplyFpsLimit(int fps)
        {
            if (_pFpsValue == IntPtr.Zero)
            {
                return;
            }

            WriteToSharedMemory(_pFpsValue, fps, IpcStatus.None);
        }

        public void Stop()
        {
            _started = false;
            _pFpsValue = IntPtr.Zero;

            WriteToSharedMemory(IntPtr.Zero, 0, IpcStatus.HostExit);
            Task.Delay(200).Wait();
            Native.UnhookWindowsHookEx(_wndHook);
            Native.FreeLibrary(_stubModule);
        }

        private void WriteToSharedMemory(IntPtr address, int fps, IpcStatus status)
        {
            IpcData ipcData = new IpcData
            {
                Address = (ulong)address,
                Value = fps,
                Status = status
            };

            _sharedMemoryAccessor?.Write(0, ref ipcData);
        }

        private string GetUnlockerStubPath()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("unlockfps_nc.Resources.UnlockerStub.dll");

            var filePath = Path.Combine(AppContext.BaseDirectory, "UnlockerStub.dll");
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            stream.CopyTo(fileStream);

            return filePath;
        }

        public void Dispose()
        {
            Stop();
            _sharedMemoryAccessor?.Dispose();
            _sharedMemory?.Dispose();
            if (_stubModule != IntPtr.Zero)
            {
                Native.FreeLibrary(_stubModule);
                _stubModule = IntPtr.Zero;
            }
            if (_wndHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_wndHook);
                _wndHook = IntPtr.Zero;
            }
        }
    }
}
