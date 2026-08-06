using Aerie.Api.Services.Media;

namespace Aerie.Api.Tests.Media;

/// <summary>Covers MediaLibraryUrlResolver.Resolve's three input shapes (library-relative path, http URL, media-source id) and the paths it refuses.</summary>
public class MediaLibraryUrlResolverTests
{
    private const string BaseUrl = "https://home.example.com/media";

    [Fact]
    public void Resolve_RelativePath_BuildsUrlUnderBaseUrl()
    {
        var (url, error) = MediaLibraryUrlResolver.Resolve("Miles Davis/Kind of Blue/01 So What.flac", BaseUrl);

        Assert.Null(error);
        Assert.Equal("https://home.example.com/media/Miles%20Davis/Kind%20of%20Blue/01%20So%20What.flac", url);
    }

    [Fact]
    public void Resolve_RelativePath_EscapesPerSegmentAndNormalizesSeparators()
    {
        var (url, error) = MediaLibraryUrlResolver.Resolve(@"Sigur Rós\( )\1 Vaka.mp3", BaseUrl);

        Assert.Null(error);
        Assert.Equal("https://home.example.com/media/Sigur%20R%C3%B3s/%28%20%29/1%20Vaka.mp3", url);
    }

    [Theory]
    [InlineData("https://home.example.com/media/x.mp3")]
    [InlineData("http://nas/music/x.mp3")]
    [InlineData("media-source://media_source/local/x.mp3")]
    public void Resolve_AlreadyResolvable_PassesThroughUntouched(string input)
    {
        var (url, error) = MediaLibraryUrlResolver.Resolve(input, BaseUrl);

        Assert.Null(error);
        Assert.Equal(input, url);
    }

    [Fact]
    public void Resolve_PassThroughInput_DoesNotNeedBaseUrl()
    {
        var (url, error) = MediaLibraryUrlResolver.Resolve("media-source://media_source/local/x.mp3", baseUrl: null);

        Assert.Null(error);
        Assert.Equal("media-source://media_source/local/x.mp3", url);
    }

    [Theory]
    [InlineData(@"\\NAS\Music\x.mp3")]
    [InlineData("//NAS/Music/x.mp3")]
    [InlineData(@"D:\Music\x.mp3")]
    public void Resolve_LocalOrUncPath_IsRejected(string input)
    {
        var (url, error) = MediaLibraryUrlResolver.Resolve(input, BaseUrl);

        Assert.Null(url);
        Assert.Contains("local or UNC path", error);
    }

    [Fact]
    public void Resolve_RelativePathWithoutBaseUrl_ExplainsTheMissingSetting()
    {
        var (url, error) = MediaLibraryUrlResolver.Resolve("Music/x.mp3", baseUrl: "  ");

        Assert.Null(url);
        Assert.Contains("MediaLibraryBaseUrl", error);
    }

    [Theory]
    [InlineData("../../appsettings.json")]
    [InlineData("Music/../../secrets.json")]
    [InlineData("./Music/x.mp3")]
    public void Resolve_TraversalSegments_AreRejected(string input)
    {
        var (url, error) = MediaLibraryUrlResolver.Resolve(input, BaseUrl);

        Assert.Null(url);
        Assert.Contains("'.' or '..'", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyInput_IsRejected(string? input)
    {
        var (url, error) = MediaLibraryUrlResolver.Resolve(input, BaseUrl);

        Assert.Null(url);
        Assert.Equal("mediaContentId is required", error);
    }

    [Fact]
    public void Resolve_BaseUrlWithTrailingSlash_DoesNotDoubleUpSeparators()
    {
        var (url, error) = MediaLibraryUrlResolver.Resolve("/Music/x.mp3", "https://home.example.com/media/");

        Assert.Null(error);
        Assert.Equal("https://home.example.com/media/Music/x.mp3", url);
    }
}
