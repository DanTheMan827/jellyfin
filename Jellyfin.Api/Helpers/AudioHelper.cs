using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Audio helper.
/// </summary>
public class AudioHelper
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ITranscodeManager _transcodeManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly EncodingHelper _encodingHelper;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioHelper"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="transcodeManager">Instance of <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="httpContextAccessor">Instance of the <see cref="IHttpContextAccessor"/> interface.</param>
    /// <param name="encodingHelper">Instance of <see cref="EncodingHelper"/>.</param>
    public AudioHelper(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IServerConfigurationManager serverConfigurationManager,
        IMediaEncoder mediaEncoder,
        ITranscodeManager transcodeManager,
        IHttpClientFactory httpClientFactory,
        IHttpContextAccessor httpContextAccessor,
        EncodingHelper encodingHelper)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _serverConfigurationManager = serverConfigurationManager;
        _mediaEncoder = mediaEncoder;
        _transcodeManager = transcodeManager;
        _httpClientFactory = httpClientFactory;
        _httpContextAccessor = httpContextAccessor;
        _encodingHelper = encodingHelper;
    }

    /// <summary>
    /// Get audio stream.
    /// </summary>
    /// <param name="transcodingJobType">Transcoding job type.</param>
    /// <param name="streamingRequest">Streaming controller.Request dto.</param>
    /// <returns>A <see cref="Task"/> containing the resulting <see cref="ActionResult"/>.</returns>
    public async Task<ActionResult> GetAudioStream(
        TranscodingJobType transcodingJobType,
        StreamingRequestDto streamingRequest)
    {
        if (_httpContextAccessor.HttpContext is null)
        {
            throw new ResourceNotFoundException(nameof(_httpContextAccessor.HttpContext));
        }

        bool isHeadRequest = _httpContextAccessor.HttpContext.Request.Method == System.Net.WebRequestMethods.Http.Head;

        // CTS lifecycle is managed internally.
        var cancellationTokenSource = new CancellationTokenSource();

        using var state = await StreamingHelpers.GetStreamingState(
                streamingRequest,
                _httpContextAccessor.HttpContext,
                _mediaSourceManager,
                _userManager,
                _libraryManager,
                _serverConfigurationManager,
                _mediaEncoder,
                _encodingHelper,
                _transcodeManager,
                transcodingJobType,
                cancellationTokenSource.Token)
            .ConfigureAwait(false);

        if (streamingRequest.Static && state.DirectStreamProvider is not null)
        {
            var liveStreamInfo = _mediaSourceManager.GetLiveStreamInfo(streamingRequest.LiveStreamId);
            if (liveStreamInfo is null)
            {
                throw new FileNotFoundException();
            }

            var liveStream = new ProgressiveFileStream(liveStreamInfo.GetStream());
            // TODO (moved from MediaBrowser.Api): Don't hardcode contentType
            return new FileStreamResult(liveStream, MimeTypes.GetMimeType("file.ts"));
        }

        // Static remote stream
        if (streamingRequest.Static && state.InputProtocol == MediaProtocol.Http)
        {
            var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
            return await FileStreamResponseHelpers.GetStaticRemoteStreamResult(state, httpClient, _httpContextAccessor.HttpContext).ConfigureAwait(false);
        }

        if (streamingRequest.Static && state.InputProtocol != MediaProtocol.File)
        {
            return new BadRequestObjectResult($"Input protocol {state.InputProtocol} cannot be streamed statically");
        }

        var outputPath = state.OutputFilePath;

        // Static stream
        // CUE sheet tracks must always be transcoded through ffmpeg so that the correct
        // start-position offset (-ss) and duration limit (-t) are applied. Static serving
        // would deliver the entire source audio file to the client.
        if (streamingRequest.Static && !(state.MediaSource?.StartPositionTicks > 0))
        {
            var contentType = state.GetMimeType("." + state.OutputContainer, false) ?? state.GetMimeType(state.MediaPath);

            if (state.MediaSource?.IsInfiniteStream == true)
            {
                var stream = new ProgressiveFileStream(state.MediaPath, null, _transcodeManager);
                return new FileStreamResult(stream, contentType);
            }

            return FileStreamResponseHelpers.GetStaticFileResult(
                state.MediaPath,
                contentType);
        }

        // Need to start ffmpeg (because media can't be returned directly)
        var encodingOptions = _serverConfigurationManager.GetEncodingOptions();

        string ffmpegCommandLineArguments;
        if (streamingRequest.Static && state.MediaSource?.StartPositionTicks > 0)
        {
            // Static download of a CUE sheet track: extract the bounded segment using stream
            // copy (or a lossless FLAC re-encode for precise frame boundaries) and embed the
            // per-track metadata so the downloaded file is correctly tagged.
            var metadataArgs = BuildCueTrackMetadataArgs(streamingRequest.Id);
            ffmpegCommandLineArguments = _encodingHelper.GetCueTrackStaticDownloadCommandLine(
                state,
                encodingOptions,
                outputPath,
                metadataArgs);
        }
        else
        {
            ffmpegCommandLineArguments = _encodingHelper.GetProgressiveAudioFullCommandLine(state, encodingOptions, outputPath);
        }

        return await FileStreamResponseHelpers.GetTranscodedFile(
            state,
            isHeadRequest,
            _httpContextAccessor.HttpContext,
            _transcodeManager,
            ffmpegCommandLineArguments,
            transcodingJobType,
            cancellationTokenSource).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a sequence of ffmpeg <c>-metadata key=value</c> arguments from the CUE track
    /// fields of the audio item identified by <paramref name="itemId"/>.
    /// </summary>
    /// <param name="itemId">Library item ID of the CUE track.</param>
    /// <returns>
    /// A string of space-separated <c>-metadata key=value</c> arguments, or
    /// <see cref="string.Empty"/> when the item cannot be found.
    /// </returns>
    private string BuildCueTrackMetadataArgs(Guid itemId)
    {
        var item = _libraryManager.GetItemById<Audio>(itemId);
        if (item is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        if (!string.IsNullOrEmpty(item.Name))
        {
            parts.Add(FormatMetadataArg("title", item.Name));
        }

        var artist = item.Artists?.FirstOrDefault() ?? item.AlbumArtists?.FirstOrDefault();
        if (!string.IsNullOrEmpty(artist))
        {
            parts.Add(FormatMetadataArg("artist", artist));
        }

        if (!string.IsNullOrEmpty(item.Album))
        {
            parts.Add(FormatMetadataArg("album", item.Album));
        }

        if (item.IndexNumber.HasValue)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "-metadata track={0}", item.IndexNumber.Value));
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Formats a single ffmpeg <c>-metadata key=value</c> argument, sanitising the value
    /// and quoting the combined <c>key=value</c> token when it contains whitespace so that
    /// the ffmpeg process (started with <c>UseShellExecute = false</c>) receives it as a
    /// single, correctly delimited argument.
    /// </summary>
    /// <remarks>
    /// Shell metacharacters (dollar signs, backticks, etc.) do not need escaping because
    /// the process is never started through a shell.  Control characters are stripped because
    /// they cannot appear inside an ffmpeg metadata value and would corrupt the key=value
    /// parsing.  Embedded double-quotes are escaped with a backslash so that the argument
    /// tokeniser does not close the enclosing quote prematurely.
    /// </remarks>
    private static string FormatMetadataArg(string key, string value)
    {
        // Strip control characters (newlines, carriage returns, tabs, etc.) that would
        // corrupt ffmpeg's key=value argument parsing regardless of quoting.
        var sanitised = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsControl(ch))
            {
                sanitised.Append(ch);
            }
        }

        // Escape any embedded double-quotes so the argument tokeniser does not end the
        // quoted section prematurely.
        var escaped = sanitised.ToString().Replace("\"", "\\\"", StringComparison.Ordinal);

        // Wrap in double-quotes when the value contains spaces or tabs so that the .NET
        // process argument parser passes the full key=value as one token to ffmpeg.
        if (escaped.Contains(' ', StringComparison.Ordinal) || escaped.Contains('\t', StringComparison.Ordinal))
        {
            return string.Format(CultureInfo.InvariantCulture, "-metadata \"{0}={1}\"", key, escaped);
        }

        return string.Format(CultureInfo.InvariantCulture, "-metadata {0}={1}", key, escaped);
    }
}
