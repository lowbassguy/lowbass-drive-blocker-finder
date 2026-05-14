// =============================================================================
//  Title:   lowbass' Drive Blocker Finder - EjectService
//  Author:  Joshua "lowbass" Sommerfeldt
//  Date:    2026-05-14
//  Purpose: Actually ejects (or at least dismounts) a drive once blockers are
//           cleared. Implements the canonical three-step Windows eject:
//
//             1. FSCTL_LOCK_VOLUME      - get exclusive access
//             2. FSCTL_DISMOUNT_VOLUME  - tear down the filesystem mount
//             3. IOCTL_STORAGE_EJECT_MEDIA - tell the device to physically eject
//
//           If LOCK fails, that IS our "still in use" signal. The UI uses this
//           to flag "Hey, scan again, something is still holding it."
// =============================================================================

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LowbassDriveBlockerFinder;

/// <summary>Describes which step of the lock→dismount→eject sequence reached.</summary>
public enum EjectResult
{
    Ok,
    OpenFailed,
    StillInUse,       // FSCTL_LOCK_VOLUME failed (something still has handles)
    DismountFailed,
    NotEjectable,     // Lock+dismount ok; eject ioctl failed (non-removable media)
    Exception
}

public class EjectService
{
    private readonly Logger _log = Logger.Instance;

    // -------- kernel32 P/Invoke ------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    // -------- Windows magic numbers ------------------------------------------
    private const uint GENERIC_READ            = 0x80000000;
    private const uint GENERIC_WRITE           = 0x40000000;
    private const uint FILE_SHARE_READ         = 0x1;
    private const uint FILE_SHARE_WRITE        = 0x2;
    private const uint OPEN_EXISTING           = 3;
    private const uint FSCTL_LOCK_VOLUME       = 0x00090018;
    private const uint FSCTL_DISMOUNT_VOLUME   = 0x00090020;
    private const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;

    /// <summary>
    /// Tries to lock+dismount+eject the given drive. Returns an <see cref="EjectResult"/>
    /// describing which step succeeded or where the failure happened, so callers can
    /// show a precise message instead of guessing "in use" for every failure.
    /// </summary>
    public EjectResult TryEject(string driveLetter)
    {
        _log.Info("EjectService", "TryEject", "START", $"drive={driveLetter}");

        // Normalize to "X:" then build the kernel-namespace volume path "\\.\X:"
        string letter = driveLetter.TrimEnd('\\').TrimEnd(':');
        string ntPath = $@"\\.\{letter}:";
        _log.Debug("EjectService", "TryEject", "PATH", $"ntPath={ntPath}");

        SafeFileHandle? handle = null;
        try
        {
            // Open a handle to the raw volume. Share R/W so we don't deny ourselves access.
            handle = CreateFile(
                ntPath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                _log.Error("EjectService", "TryEject", "FAIL_OPEN",
                    $"win32err={err} - is the drive letter valid?");
                return EjectResult.OpenFailed;
            }

            // Step 1: lock the volume. This is THE call that fails if anything
            // has the drive open. Win32 error 32 (ERROR_SHARING_VIOLATION) is
            // the classic "in use" signal.
            if (!DeviceIoControl(handle, FSCTL_LOCK_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                _log.Warn("EjectService", "TryEject", "FAIL_LOCK",
                    $"win32err={err} - drive is in use; run a Scan to see what's holding it");
                return EjectResult.StillInUse;
            }
            _log.Debug("EjectService", "TryEject", "LOCKED", "exclusive access obtained");

            // Step 2: dismount the filesystem
            if (!DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                _log.Warn("EjectService", "TryEject", "FAIL_DISMOUNT",
                    $"win32err={Marshal.GetLastWin32Error()}");
                return EjectResult.DismountFailed;
            }
            _log.Debug("EjectService", "TryEject", "DISMOUNTED", "filesystem unmounted");

            // Step 3: actually eject (only meaningful for removable media)
            if (!DeviceIoControl(handle, IOCTL_STORAGE_EJECT_MEDIA,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                // Note: fixed disks will fail here. That's expected. The dismount
                // above is what actually freed any in-flight handles for those.
                _log.Warn("EjectService", "TryEject", "FAIL_EJECT_MEDIA",
                    $"win32err={err} - non-ejectable media; dismount still succeeded");
                return EjectResult.NotEjectable;
            }

            _log.Success("EjectService", "TryEject", "EJECTED",
                $"drive={driveLetter} safely removed 🎉");
            return EjectResult.Ok;
        }
        catch (Exception ex)
        {
            _log.Error("EjectService", "TryEject", "EXCEPTION", $"error={ex.Message}");
            return EjectResult.Exception;
        }
        finally
        {
            // SafeFileHandle is IDisposable - this releases the volume handle
            try { handle?.Dispose(); } catch { }
        }
    }
}
