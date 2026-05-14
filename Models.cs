// =============================================================================
//  Title:   Drive Blocker Finder - Data Models
//  Author:  Joshua "lowbass" Sommerfeldt
//  Date:    2026-05-14
//  Purpose: Plain data classes for the things shown in the UI. Keeping them
//           dumb (no logic) makes binding to WPF controls trivial and keeps
//           unit-testing the scanner straightforward.
// =============================================================================

using System.Collections.Generic;

namespace DriveBlockerFinder.Models;

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
    public List<string> BlockingFiles { get; set; } = new();
}
