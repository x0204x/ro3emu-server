using System;
using System.Runtime.InteropServices;

namespace Ro3Emu.ClientHooks;

/// <summary>
/// 每台電腦最多四個 RO3Emu 客戶端。各程序持有一個全域名額直到結束；第五個以代碼 73 拒絕。
/// 保留既有第一、第二個鎖名稱，讓更新前後的客戶端仍會共用已占用名額。
/// </summary>
public static class ControlledMultiInstanceGuard
{
    public const int MaximumClients = 4;
    private const string SlotPrefix = "Global\\RO3.GameClient.Ro3Emu.ControlledSlot.";
    private const string SecurityDescriptor = "D:(A;;0x00100001;;;AU)(A;;GA;;;SY)S:(ML;;NW;;;LW)";
    private const uint SynchronizeAndModify = 0x00100001;
    private const uint WaitObject = 0;
    private const uint WaitAbandoned = 128;
    private const uint WaitTimeout = 258;
    private const uint TooManyClientsExitCode = 73;
    private const uint LockFailureExitCode = 74;

    private static readonly object Sync = new object();

    // Deliberately kept for the process lifetime. Closing or releasing it would make the occupied slot reusable early.
    private static IntPtr _ownedSlot;

    private enum AcquireResult
    {
        Acquired,
        Occupied,
        Failed,
    }

    // C# structs are sequential by default (this client's mscorlib has no StructLayoutAttribute constructor).
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    public static void Enforce()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;

        lock (Sync)
        {
            if (_ownedSlot != IntPtr.Zero) return;

            int error = 0;
            for (int slot = 1; slot <= MaximumClients; slot++)
            {
                var result = TryAcquire(SlotPrefix + slot + ".v1", out var handle, out int nativeError);
                if (result == AcquireResult.Acquired)
                {
                    _ownedSlot = handle;
                    return;
                }
                if (result == AcquireResult.Failed && error == 0) error = nativeError == 0 ? -1 : nativeError;
            }
            if (error == 0) Reject(TooManyClientsExitCode, "RO3Emu 已達四開上限，請先關閉一個遊戲視窗。");
            else Reject(LockFailureExitCode, "RO3Emu 無法確認四開名額（Windows 錯誤 " + error + "）。");
        }
    }

    private static AcquireResult TryAcquire(string name, out IntPtr handle, out int nativeError)
    {
        handle = IntPtr.Zero;
        nativeError = 0;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(SecurityDescriptor, 1, out var descriptor, IntPtr.Zero))
        {
            nativeError = Marshal.GetLastWin32Error();
            return AcquireResult.Failed;
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor,
            };
            handle = CreateMutexExW(ref attributes, name, 0, SynchronizeAndModify);
            if (handle == IntPtr.Zero)
            {
                nativeError = Marshal.GetLastWin32Error();
                return AcquireResult.Failed;
            }
        }
        finally
        {
            LocalFree(descriptor);
        }

        uint wait = WaitForSingleObject(handle, 0);
        if (wait is WaitObject or WaitAbandoned) return AcquireResult.Acquired;

        CloseHandle(handle);
        handle = IntPtr.Zero;
        if (wait == WaitTimeout) return AcquireResult.Occupied;
        nativeError = Marshal.GetLastWin32Error();
        if (nativeError == 0) nativeError = unchecked((int)wait);
        return AcquireResult.Failed;
    }

    private static void Reject(uint exitCode, string message)
    {
        Console.Error.WriteLine("[Ro3Emu.MultiInstance] " + message);
        if (!TerminateProcess(GetCurrentProcess(), exitCode))
            Environment.FailFast("RO3Emu could not stop a client rejected by the controlled instance guard.");
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string text, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateMutexExW(ref SecurityAttributes attributes, string name, uint flags, uint access);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
