using System;
using System.Runtime.InteropServices;

namespace Ro3Emu.ClientHooks;

/// <summary>
/// The RO3Emu client allows two local game processes for multiplayer testing. Each process owns one global slot for
/// its whole lifetime; a third process is rejected with the publisher client's single-instance exit code (73).
/// </summary>
public static class ControlledMultiInstanceGuard
{
    private const string FirstSlot = "Global\\RO3.GameClient.Ro3Emu.ControlledSlot.1.v1";
    private const string SecondSlot = "Global\\RO3.GameClient.Ro3Emu.ControlledSlot.2.v1";
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

            var first = TryAcquire(FirstSlot, out var firstHandle, out int firstError);
            if (first == AcquireResult.Acquired)
            {
                _ownedSlot = firstHandle;
                return;
            }

            var second = TryAcquire(SecondSlot, out var secondHandle, out int secondError);
            if (second == AcquireResult.Acquired)
            {
                _ownedSlot = secondHandle;
                return;
            }

            if (first == AcquireResult.Occupied && second == AcquireResult.Occupied)
                Reject(TooManyClientsExitCode, "RO3Emu already has two game clients running.");

            int error = first == AcquireResult.Failed ? firstError : secondError;
            Reject(LockFailureExitCode, "RO3Emu could not verify its two-client slots (Windows error " + error + ").");
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
