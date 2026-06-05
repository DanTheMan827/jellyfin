using System;
using System.Collections.Generic;
using System.IO;

namespace Emby.Naming.Audio;

/// <summary>
/// Parser for CUE sheet files (.cue).
/// See http://wiki.hydrogenaud.io/index.php?title=Cue_sheet for the format specification.
/// </summary>
public static partial class CueSheetParser
{
    // CUE sheet timecodes use a MM:SS:FF format where FF is a 1/75-second "frame" based on
    // the CD sector rate (75 sectors per second as defined by the Red Book audio standard).
    private const double FramesPerSecond = 75.0;

    /// <summary>
    /// The separator used in virtual paths to identify a CUE track.
    /// Format: {sourceAudioFilePath}::cue::{trackNumber}.
    /// </summary>
    public const string CuePathSeparator = "::cue::";

    /// <summary>
    /// Parses a CUE sheet file and returns the album and track information.
    /// </summary>
    /// <param name="cuePath">Absolute path to the .cue file.</param>
    /// <returns>A <see cref="CueSheet"/> containing the parsed data, or <c>null</c> if parsing fails.</returns>
    public static CueSheet? Parse(string cuePath)
    {
        if (!File.Exists(cuePath))
        {
            return null;
        }

        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(cuePath);
        }
        catch (IOException)
        {
            return null;
        }

        var baseDirectory = Path.GetDirectoryName(cuePath) ?? string.Empty;
        return ParseLines(lines, baseDirectory, defaultSourceFile: string.Empty);
    }

    /// <summary>
    /// Parses an embedded CUE sheet from a string value (e.g. from a CUESHEET metadata tag).
    /// When no FILE command is present the <paramref name="audioFilePath"/> is used as the source file
    /// for all tracks, which is the correct behaviour for single-file embedded CUE sheets.
    /// </summary>
    /// <param name="cueContent">The raw CUE sheet text.</param>
    /// <param name="audioFilePath">
    /// Absolute path to the audio file that contains the embedded CUE data.
    /// Used as the default source file and as the base directory for relative FILE paths.
    /// </param>
    /// <returns>A <see cref="CueSheet"/> containing the parsed data, or <c>null</c> if parsing fails.</returns>
    public static CueSheet? ParseContent(string cueContent, string audioFilePath)
    {
        if (string.IsNullOrEmpty(cueContent))
        {
            return null;
        }

        var lines = cueContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var baseDirectory = Path.GetDirectoryName(audioFilePath) ?? string.Empty;
        return ParseLines(lines, baseDirectory, defaultSourceFile: audioFilePath);
    }

    /// <summary>
    /// Core CUE sheet parsing logic, shared by both file-based and embedded-string parsing.
    /// </summary>
    /// <param name="lines">The lines of the CUE sheet to parse.</param>
    /// <param name="baseDirectory">
    /// Directory used to resolve relative FILE paths found in the CUE data.
    /// For external .cue files this is the directory that contains the .cue file;
    /// for embedded CUE sheets this is the directory that contains the audio file.
    /// </param>
    /// <param name="defaultSourceFile">
    /// The source audio file to use for tracks when no FILE command has been seen yet.
    /// For external .cue files this is <see cref="string.Empty"/> (a FILE command is required);
    /// for embedded CUE sheets this is the host audio file path.
    /// </param>
    private static CueSheet? ParseLines(IEnumerable<string> lines, string baseDirectory, string defaultSourceFile)
    {
        var sheet = new CueSheet();
        var currentSourceFile = defaultSourceFile;
        CueSheetTrack? currentTrack = null;
        var trackStartMs = new List<(string SourceFile, double StartMs, CueSheetTrack Track)>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var spaceIndex = line.IndexOf(' ');
            if (spaceIndex < 0)
            {
                continue;
            }

            var keyword = line[..spaceIndex];
            var rest = line[(spaceIndex + 1)..];

            if (currentTrack is null)
            {
                // Album-level tags
                if (keyword.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
                {
                    sheet.Title = StripQuotes(rest);
                }
                else if (keyword.Equals("PERFORMER", StringComparison.OrdinalIgnoreCase))
                {
                    sheet.Performer = StripQuotes(rest);
                }
                else if (keyword.Equals("REM", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(sheet.Comment))
                    {
                        sheet.Comment += '\n';
                    }

                    sheet.Comment += rest;
                }
                else if (keyword.Equals("FILE", StringComparison.OrdinalIgnoreCase))
                {
                    currentSourceFile = ParseFilePath(rest, baseDirectory);
                }
                else if (keyword.Equals("TRACK", StringComparison.OrdinalIgnoreCase))
                {
                    currentTrack = StartTrack(rest, currentSourceFile);
                }
            }
            else
            {
                // Track-level tags
                if (keyword.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
                {
                    currentTrack.Title = StripQuotes(rest);
                }
                else if (keyword.Equals("PERFORMER", StringComparison.OrdinalIgnoreCase))
                {
                    currentTrack.Performer = StripQuotes(rest);
                }
                else if (keyword.Equals("REM", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(currentTrack.Comment))
                    {
                        currentTrack.Comment += '\n';
                    }

                    currentTrack.Comment += rest;
                }
                else if (keyword.Equals("FILE", StringComparison.OrdinalIgnoreCase))
                {
                    // A new FILE command within track context — commit current track and switch file
                    currentSourceFile = ParseFilePath(rest, baseDirectory);
                }
                else if (keyword.Equals("INDEX", StringComparison.OrdinalIgnoreCase))
                {
                    var indexParts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (indexParts.Length == 2
                        && int.TryParse(indexParts[0], out var indexNumber)
                        && indexNumber == 1)
                    {
                        var startMs = ParseTimecodeMs(indexParts[1]);
                        if (startMs >= 0)
                        {
                            currentTrack.StartPositionTicks = (long)(startMs * TimeSpan.TicksPerMillisecond);
                            trackStartMs.Add((currentSourceFile, startMs, currentTrack));
                            sheet.Tracks.Add(currentTrack);
                            currentTrack = null;
                        }
                    }
                }
                else if (keyword.Equals("TRACK", StringComparison.OrdinalIgnoreCase))
                {
                    // New track encountered before INDEX 01 — drop previous partial track
                    currentTrack = StartTrack(rest, currentSourceFile);
                }
            }
        }

        // Calculate durations: each track's duration = next track's start (in same file) - this track's start
        for (var i = 0; i < trackStartMs.Count; i++)
        {
            var (sourceFile, startMs, track) = trackStartMs[i];

            // Find the next track that uses the SAME source file
            double? nextStartMs = null;
            for (var j = i + 1; j < trackStartMs.Count; j++)
            {
                if (string.Equals(trackStartMs[j].SourceFile, sourceFile, StringComparison.OrdinalIgnoreCase))
                {
                    nextStartMs = trackStartMs[j].StartMs;
                    break;
                }
            }

            if (nextStartMs.HasValue)
            {
                var durationMs = nextStartMs.Value - startMs;
                if (durationMs > 0)
                {
                    track.DurationTicks = (long)(durationMs * TimeSpan.TicksPerMillisecond);
                }
            }

            // If no next track in same file, DurationTicks remains null (will be filled by probing the actual file)
        }

        // Fill in fallback performer from album level
        foreach (var track in sheet.Tracks)
        {
            if (string.IsNullOrEmpty(track.Performer))
            {
                track.Performer = sheet.Performer;
            }
        }

        return sheet;
    }

    /// <summary>
    /// Gets the physical (actual file-system) path from a CUE virtual path.
    /// If the path contains the <see cref="CuePathSeparator"/>, the part before it is returned.
    /// Otherwise, the original path is returned unchanged.
    /// </summary>
    /// <param name="virtualOrPhysicalPath">The path to examine.</param>
    /// <returns>The physical file path.</returns>
    public static string GetPhysicalPath(string virtualOrPhysicalPath)
    {
        var index = virtualOrPhysicalPath.IndexOf(CuePathSeparator, StringComparison.Ordinal);
        return index >= 0 ? virtualOrPhysicalPath[..index] : virtualOrPhysicalPath;
    }

    /// <summary>
    /// Returns whether the given path is a CUE virtual path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns><c>true</c> if the path is a CUE virtual path.</returns>
    public static bool IsCuePath(string? path)
        => path is not null && path.Contains(CuePathSeparator, StringComparison.Ordinal);

    /// <summary>
    /// Builds a virtual path for a CUE track item.
    /// </summary>
    /// <param name="sourceFilePath">Absolute path to the source audio file.</param>
    /// <param name="trackNumber">The 1-based track number.</param>
    /// <returns>The virtual path for the track item.</returns>
    public static string BuildCuePath(string sourceFilePath, int trackNumber)
        => $"{sourceFilePath}{CuePathSeparator}{trackNumber:D2}";

    private static CueSheetTrack StartTrack(string rest, string currentSourceFile)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var track = new CueSheetTrack
        {
            SourceFile = currentSourceFile
        };

        if (parts.Length >= 1 && int.TryParse(parts[0], out var num))
        {
            track.Number = num;
        }

        return track;
    }

    private static string ParseFilePath(string rest, string baseDirectory)
    {
        // Format: "filename" WAVE  or  filename WAVE
        // Strip the trailing format word (e.g. WAVE, MP3, AIFF, BINARY)
        var lastSpace = rest.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            rest = rest[..lastSpace].Trim();
        }

        var filePath = StripQuotes(rest);

        // Resolve relative paths against the base directory (directory of the .cue file or the audio file)
        if (!Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(baseDirectory))
        {
            filePath = Path.Combine(baseDirectory, filePath);
        }

        return filePath;
    }

    private static double ParseTimecodeMs(string timecode)
    {
        // Format: MM:SS:FF (minutes, seconds, frames at 75 fps)
        var parts = timecode.Split(':');
        if (parts.Length < 3)
        {
            return -1;
        }

        if (!int.TryParse(parts[0], out var minutes)
            || !int.TryParse(parts[1], out var seconds)
            || !int.TryParse(parts[2], out var frames))
        {
            return -1;
        }

        return ((minutes * 60) + seconds + (frames / FramesPerSecond)) * 1000.0;
    }

    private static string StripQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            return s[1..^1];
        }

        return s;
    }
}
