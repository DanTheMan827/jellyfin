using System.Collections.Generic;

namespace Emby.Naming.Audio;

/// <summary>
/// Represents a parsed cue sheet file.
/// </summary>
public class CueSheet
{
    /// <summary>
    /// Gets or sets the album title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the album performer.
    /// </summary>
    public string Performer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the comment.
    /// </summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>
    /// Gets the list of tracks in this cue sheet.
    /// </summary>
    public List<CueSheetTrack> Tracks { get; } = new();
}
