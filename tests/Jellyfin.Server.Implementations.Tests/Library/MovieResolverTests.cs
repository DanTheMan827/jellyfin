using Emby.Naming.Common;
using Emby.Server.Implementations.Library.Resolvers.Movies;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class MovieResolverTests
{
    private static readonly NamingOptions _namingOptions = new();

    [Fact]
    public void Resolve_GivenLocalAlternateVersion_ResolvesToVideo()
    {
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>());
        var itemResolveArgs = new ItemResolveArgs(
            Mock.Of<IServerApplicationPaths>(),
            null)
        {
            Parent = null,
            FileInfo = new FileSystemMetadata
            {
                FullName = "/movies/Black Panther (2018)/Black Panther (2018) - 1080p 3D.mk3d"
            }
        };

        Assert.NotNull(movieResolver.Resolve(itemResolveArgs));
    }

    [Fact]
    public void ResolvePath_GivenIsoInsideMovieFolder_UsesIsoFilePathAndType()
    {
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>());
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(m => m.IgnoreFile(It.IsAny<FileSystemMetadata>(), It.IsAny<Folder>()))
            .Returns(false);

        var itemResolveArgs = new ItemResolveArgs(
            Mock.Of<IServerApplicationPaths>(),
            libraryManager.Object)
        {
            Parent = new Folder(),
            CollectionType = Jellyfin.Data.Enums.CollectionType.movies,
            FileInfo = new FileSystemMetadata
            {
                FullName = "/movies/MyMovie (2026)",
                Name = "MyMovie (2026)",
                IsDirectory = true
            },
            FileSystemChildren =
            [
                new FileSystemMetadata
                {
                    FullName = "/movies/MyMovie (2026)/MyMovie (2026).iso",
                    Name = "MyMovie (2026).iso",
                    IsDirectory = false
                }
            ]
        };

        var video = Assert.IsType<Movie>(movieResolver.ResolvePath(itemResolveArgs));
        Assert.Equal("/movies/MyMovie (2026)/MyMovie (2026).iso", video.Path);
        Assert.Equal(VideoType.Iso, video.VideoType);
    }
}
