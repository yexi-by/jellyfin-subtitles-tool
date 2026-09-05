using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SubtitlesTool.Tests;

public sealed class AuthorizationTests
{
    [Fact]
    public async Task MissingAuthenticatedUserCannotFallBackToUnrestrictedLibraryAccess()
    {
        using var hashes = new HashRecords();
        using var source = new ThunderSource(new HttpClient());
        var controller = new SubtitlesController(null!, null!, null!, hashes, source, NullLogger<SubtitlesController>.Instance, new MissingUserContext())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var response = Assert.IsType<ObjectResult>(await controller.Info(Guid.NewGuid(), null));
        Assert.Equal(401, response.StatusCode);
    }

    private sealed class MissingUserContext : IAuthorizationContext
    {
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpContext requestContext) => Task.FromResult(new AuthorizationInfo { IsAuthenticated = true, IsApiKey = false });
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpRequest requestContext) => GetAuthorizationInfo(requestContext.HttpContext);
    }

    [Fact]
    public async Task SearchErrorCanBeSentAfterProgressHasStartedTheResponse()
    {
        using var hashes = new HashRecords();
        using var source = new ThunderSource(new HttpClient());
        var context = new DefaultHttpContext();
        var headers = new HeaderDictionary { ["Content-Type"] = "application/x-ndjson; charset=utf-8" };
        headers.IsReadOnly = true;
        context.Features.Set<IHttpResponseFeature>(new StartedResponse { Headers = headers });
        using var body = new MemoryStream();
        context.Response.Body = body;
        var controller = new SubtitlesController(null!, null!, null!, hashes, source, NullLogger<SubtitlesController>.Instance, new MissingUserContext())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        await controller.Search(Guid.NewGuid(), new SubtitlesController.SearchRequest(null), default);
        Assert.Contains("\"type\":\"error\"", System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class StartedResponse : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
