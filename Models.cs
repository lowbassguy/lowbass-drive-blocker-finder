// =============================================================================
//  Title:   lowbass' Drive Blocker Finder - Data Models
//  Author:  Joshua "lowbass" Sommerfeldt
//  Date:    2026-05-14
//  Purpose: Plain data classes for the things shown in the UI. Keeping them
//           dumb (no logic) makes binding to WPF controls trivial and keeps
//           unit-testing the scanner straightforward.
// =============================================================================

using System;
using System.Collections.Generic;

namespace LowbassDriveBlockerFinder.Models;

/// <summary>
/// One entry in the drive list at the top of the window.
/// </summary>
public class DriveItem
{
    /// <summary>Drive root path with trailing slash, e.g. "E:\".</summary>
    public string DriveLetter { get; set; } = "";

    /// <summary>User-friendly volume label (or "(not ready)" for empty bays).</summary>
    public string VolumeLabel { get; set; } = "";

    /// <summary>Removable, Fixed, Network, CDRom, Ram, etc.</summary>
    public string DriveType { get; set; } = "";

    /// <summary>Pre-formatted total size, e.g. "32.00 GB".</summary>
    public string TotalSize { get; set; } = "";

    // ToString is what the ComboBox displays, since we don't bind columns there.
    public override string ToString()
        => $"{DriveLetter}  [{DriveType}]  {VolumeLabel}  ({TotalSize})";
}

/// <summary>
/// One row in the "blockers" grid - a process that appears to be holding the drive.
/// </summary>
public class ProcessUsage
{
    /// <summary>Windows process id.</summary>
    public int Pid { get; set; }

    /// <summary>Short process name, e.g. "explorer".</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>Full path to the process's main module (the .exe), if accessible.</summary>
    public string MainModule { get; set; } = "";

    /// <summary>Human-readable reason - what made us flag this process.</summary>
    public string Reason { get; set; } = "";

    /// <summary>List of files/modules on the target drive that this process holds.</summary>
    public List<BlockingFile> BlockingFiles { get; set; } = new();
}

/// <summary>
/// A single file (or module / DLL) on the target drive being held by a process.
/// Carries the source-process handle value when the finding came from Strategy 3
/// (handle enumeration), which is what makes per-handle "Close" possible. For
/// Strategy 1 findings (loaded modules) the Handle is IntPtr.Zero and the Close
/// action is disabled in the UI - you can't unload a module from outside.
/// </summary>
public class BlockingFile
{
    /// <summary>Translated DOS-style path, e.g. "D:\Pictures\foo.jpg".</summary>
    public string Path { get; set; } = "";

    /// <summary>PID of the process that owns the handle / module.</summary>
    public int OwnerPid { get; set; }

    /// <summary>Handle value in the OWNER process. IntPtr.Zero for module findings.</summary>
    public IntPtr Handle { get; set; }

    /// <summary>True when we have a real kernel handle that can be force-closed.</summary>
    public bool CanCloseHandle => Handle != IntPtr.Zero;

    // Used by WPF tooltip text-binding fallbacks and anywhere a string is expected.
    public override string ToString() => Path;
}
