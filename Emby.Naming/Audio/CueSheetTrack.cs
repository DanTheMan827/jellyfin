namespace Emby.Naming.Audio;

/// <summary>
/// Represents a single track in a cue sheet.
/// </summary>
public class CueSheetTrack
{
    /// <summary>
    /// Gets or sets the track number.
    /// </summary>
    public int Number { get; set; }

    /// <summary>
    /// Gets or sets the track title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the track performer.
    /// </summary>
    public string Performer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the track comment.
    /// </summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute path to the source audio file referenced by this track.
    /// </summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start position of this track in the source file, in ticks.
    /// </summary>
    public long StartPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets the duration of this track in ticks.
    /// A null value means the duration could not be determined (e.g., for the last track without a total file duration).
    /// </summary>
    public long? DurationTicks { get; set; }
}
