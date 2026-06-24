#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ATL;
using Emby.Naming.Audio;
using Emby.Naming.AudioBook;
using Emby.Naming.Common;
using Emby.Naming.Video;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;

namespace Emby.Server.Implementations.Library.Resolvers.Audio
{
    /// <summary>
    /// Class AudioResolver.
    /// </summary>
    public class AudioResolver : ItemResolver<MediaBrowser.Controller.Entities.Audio.Audio>, IMultiItemResolver
    {
        private readonly NamingOptions _namingOptions;

        public AudioResolver(NamingOptions namingOptions)
        {
            _namingOptions = namingOptions;
        }

        /// <summary>
        /// Gets the priority.
        /// </summary>
        /// <value>The priority.</value>
        public override ResolverPriority Priority => ResolverPriority.Fifth;

        public MultiItemResolverResult ResolveMultiple(
            Folder parent,
            List<FileSystemMetadata> files,
            CollectionType? collectionType,
            IDirectoryService directoryService)
        {
            var result = ResolveMultipleInternal(parent, files, collectionType);

            if (result is not null)
            {
                foreach (var item in result.Items)
                {
                    SetInitialItemValues((MediaBrowser.Controller.Entities.Audio.Audio)item, null);
                }
            }

            return result;
        }

        private MultiItemResolverResult ResolveMultipleInternal(
            Folder parent,
            List<FileSystemMetadata> files,
            CollectionType? collectionType)
        {
            if (collectionType == CollectionType.books)
            {
                return ResolveMultipleAudio(parent, files, true);
            }

            // For music libraries and unspecified (mixed) libraries, handle .cue files
            if (collectionType == CollectionType.music || collectionType is null)
            {
                return ResolveMultipleCue(files, _namingOptions);
            }

            return null;
        }

        /// <summary>
        /// Resolves the specified args.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>Entities.Audio.Audio.</returns>
        protected override MediaBrowser.Controller.Entities.Audio.Audio Resolve(ItemResolveArgs args)
        {
            // Return audio if the path is a file and has a matching extension

            var collectionType = args.GetCollectionType();

            var isBooksCollectionType = collectionType == CollectionType.books;

            if (args.IsDirectory)
            {
                if (!isBooksCollectionType)
                {
                    return null;
                }

                return FindAudioBook(args, false);
            }

            if (AudioFileParser.IsAudioFile(args.Path, _namingOptions))
            {
                var extension = Path.GetExtension(args.Path.AsSpan());

                if (extension.Equals(".cue", StringComparison.OrdinalIgnoreCase))
                {
                    // .cue files are handled by the multi-item resolver; skip individual resolution
                    return null;
                }

                var isMixedCollectionType = collectionType is null;

                // For conflicting extensions, give priority to videos
                if (isMixedCollectionType && VideoResolver.IsVideoFile(args.Path, _namingOptions))
                {
                    return null;
                }

                MediaBrowser.Controller.Entities.Audio.Audio item = null;

                var isMusicCollectionType = collectionType == CollectionType.music;

                // Use regular audio type for mixed libraries, owned items and music
                if (isMixedCollectionType ||
                    args.Parent is null ||
                    isMusicCollectionType)
                {
                    item = new MediaBrowser.Controller.Entities.Audio.Audio();
                }
                else if (isBooksCollectionType)
                {
                    item = new AudioBook();
                }

                if (item is not null)
                {
                    item.IsShortcut = extension.Equals(".strm", StringComparison.OrdinalIgnoreCase);

                    item.IsInMixedFolder = true;
                }

                return item;
            }

            return null;
        }

        /// <summary>
        /// Resolves CUE data (external .cue files and embedded CUESHEET tags) in a file list and
        /// creates Audio items for each track.  Audio files that are referenced by CUE data are
        /// excluded from ExtraFiles so they do not appear as duplicate standalone items.
        /// </summary>
        private static MultiItemResolverResult ResolveMultipleCue(List<FileSystemMetadata> files, NamingOptions namingOptions)
        {
            var cueFiles = files
                .Where(f => !f.IsDirectory && Path.GetExtension(f.Name).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var result = new MultiItemResolverResult();

            // Track which audio file paths are "claimed" by a CUE sheet (external or embedded)
            var referencedAudioPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Process external .cue files
            foreach (var cueFile in cueFiles)
            {
                var sheet = CueSheetParser.Parse(cueFile.FullName);
                if (sheet is null || sheet.Tracks.Count == 0)
                {
                    continue;
                }

                AddTracksFromSheet(sheet, result, referencedAudioPaths);
            }

            // 2. For audio files not already claimed by an external .cue, check for an embedded CUESHEET tag
            foreach (var file in files)
            {
                if (file.IsDirectory)
                {
                    continue;
                }

                var ext = Path.GetExtension(file.Name);
                if (ext.Equals(".cue", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (referencedAudioPaths.Contains(file.FullName))
                {
                    // Already claimed by an external .cue file
                    continue;
                }

                if (!AudioFileParser.IsAudioFile(file.FullName, namingOptions))
                {
                    // Not a recognised audio file
                    continue;
                }

                // Use ATL to read only the tags (no audio data) looking for an embedded CUESHEET tag.
                // ATL.Track with a file path reads metadata lazily; we only access AdditionalFields.
                try
                {
                    var atlTrack = new Track(file.FullName);
                    if (atlTrack.AdditionalFields.TryGetValue("CUESHEET", out var embeddedCue)
                        && !string.IsNullOrWhiteSpace(embeddedCue))
                    {
                        var sheet = CueSheetParser.ParseContent(embeddedCue, file.FullName);
                        if (sheet is not null && sheet.Tracks.Count > 0)
                        {
                            AddTracksFromSheet(sheet, result, referencedAudioPaths);
                        }
                    }
                }
                catch (IOException)
                {
                    // File read failure — skip this file silently
                }
                catch (UnauthorizedAccessException)
                {
                    // Permission denied — skip this file silently
                }
            }

            if (result.Items.Count == 0)
            {
                // Neither external .cue files nor embedded CUESHEET tags produced any tracks —
                // let normal per-file resolution handle everything.
                return null;
            }

            // Files NOT referenced by any CUE sheets (and not the .cue files themselves) are extra
            foreach (var file in files)
            {
                if (file.IsDirectory)
                {
                    result.ExtraFiles.Add(file);
                }
                else if (!cueFiles.Contains(file)
                    && !referencedAudioPaths.Contains(file.FullName))
                {
                    result.ExtraFiles.Add(file);
                }
            }

            return result;
        }

        /// <summary>
        /// Creates Audio items from all tracks in <paramref name="sheet"/> and adds them to
        /// <paramref name="result"/>. Also registers each track's source file in
        /// <paramref name="referencedAudioPaths"/> so it is not surfaced as a standalone item.
        /// </summary>
        private static void AddTracksFromSheet(
            CueSheet sheet,
            MultiItemResolverResult result,
            HashSet<string> referencedAudioPaths)
        {
            foreach (var track in sheet.Tracks)
            {
                if (string.IsNullOrEmpty(track.SourceFile))
                {
                    continue;
                }

                referencedAudioPaths.Add(track.SourceFile);

                var virtualPath = CueSheetParser.BuildCuePath(track.SourceFile, track.Number);

                var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
                {
                    Path = virtualPath,
                    Name = string.IsNullOrEmpty(track.Title) ? $"Track {track.Number:D2}" : track.Title,
                    IndexNumber = track.Number,
                    Album = sheet.Title,
                    Artists = [string.IsNullOrEmpty(track.Performer) ? sheet.Performer : track.Performer],
                    AlbumArtists = [sheet.Performer],
                    StartPositionTicks = track.StartPositionTicks,
                    RunTimeTicks = track.DurationTicks,
                    IsInMixedFolder = true
                };

                result.Items.Add(audioItem);
            }
        }

        private AudioBook FindAudioBook(ItemResolveArgs args, bool parseName)
        {
            // TODO: Allow GetMultiDiscMovie in here
            var result = ResolveMultipleAudio(args.Parent, args.GetActualFileSystemChildren(), parseName);

            if (result is null || result.Items.Count != 1 || result.Items[0] is not AudioBook item)
            {
                return null;
            }

            // If we were supporting this we'd be checking filesFromOtherItems
            item.IsInMixedFolder = false;
            item.Name = Path.GetFileName(item.ContainingFolderPath);
            return item;
        }

        private MultiItemResolverResult ResolveMultipleAudio(Folder parent, IEnumerable<FileSystemMetadata> fileSystemEntries, bool parseName)
        {
            var files = new List<FileSystemMetadata>();
            var leftOver = new List<FileSystemMetadata>();

            // Loop through each child file/folder and see if we find a video
            foreach (var child in fileSystemEntries)
            {
                if (child.IsDirectory)
                {
                    leftOver.Add(child);
                }
                else
                {
                    files.Add(child);
                }
            }

            var resolver = new AudioBookListResolver(_namingOptions);
            var resolverResult = resolver.Resolve(files).ToList();

            var result = new MultiItemResolverResult
            {
                ExtraFiles = leftOver,
                Items = new List<BaseItem>()
            };

            var isInMixedFolder = resolverResult.Count > 1 || (parent is not null && parent.IsTopParent);

            foreach (var resolvedItem in resolverResult)
            {
                if (resolvedItem.Files.Count > 1)
                {
                    // For now, until we sort out naming for multi-part books
                    continue;
                }

                // Until multi-part books are handled letting files stack hides them from browsing in the client
                if (resolvedItem.Files.Count == 0 || resolvedItem.Extras.Count > 0 || resolvedItem.AlternateVersions.Count > 0)
                {
                    continue;
                }

                var firstMedia = resolvedItem.Files[0];

                var libraryItem = new AudioBook
                {
                    Path = firstMedia.Path,
                    IsInMixedFolder = isInMixedFolder,
                    ProductionYear = resolvedItem.Year,
                    Name = parseName ?
                        resolvedItem.Name :
                        Path.GetFileNameWithoutExtension(firstMedia.Path),
                    // AdditionalParts = resolvedItem.Files.Skip(1).Select(i => i.Path).ToArray(),
                    // LocalAlternateVersions = resolvedItem.AlternateVersions.Select(i => i.Path).ToArray()
                };

                result.Items.Add(libraryItem);
            }

            result.ExtraFiles.AddRange(files.Where(i => !ContainsFile(resolverResult, i)));

            return result;
        }

        private static bool ContainsFile(IEnumerable<AudioBookInfo> result, FileSystemMetadata file)
        {
            return result.Any(i => ContainsFile(i, file));
        }

        private static bool ContainsFile(AudioBookInfo result, FileSystemMetadata file)
        {
            return result.Files.Any(i => ContainsFile(i, file)) ||
                result.AlternateVersions.Any(i => ContainsFile(i, file)) ||
                result.Extras.Any(i => ContainsFile(i, file));
        }

        private static bool ContainsFile(AudioBookFileInfo result, FileSystemMetadata file)
        {
            return string.Equals(result.Path, file.FullName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
