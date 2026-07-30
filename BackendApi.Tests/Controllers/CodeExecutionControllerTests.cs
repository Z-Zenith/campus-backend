using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Services;
using BackendApi.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests.Controllers;

public class CodeExecutionControllerTests
{
    private static RunCodeProjectRequest SingleFileRequest(string language, string content, string? stdin = null) =>
        new("main.txt", [new CodeFileDto("main.txt", language, content)], stdin);

    // B2's RunPreview/StopPreview don't have a fake-able interface (PreviewSessionService
    // isn't behind one — see its own file) — a real instance is harmless to construct for
    // the Run(...) tests below, which never call into it. NewPreviewSessions() below is
    // reused by the dedicated preview tests further down, which DO exercise it for real
    // (an UnavailableContainerCli means persistent-mode calls fail fast/predictably rather
    // than actually needing a container).
    private static PreviewSessionService NewPreviewSessions() =>
        new(NullLogger<PreviewSessionService>.Instance, new ContainerCodeRunner(NullLogger<ContainerCodeRunner>.Instance, new UnavailableContainerCli()));

    [Fact]
    public async Task Run_ReturnsResultFromCodeRunner()
    {
        var codeRunner = new FakeCodeRunner { Result = new CodeRunResultDto("2\n", "", 0, 42, false, "accepted") };
        var controller = new CodeExecutionController(codeRunner, NewPreviewSessions());

        var result = await controller.Run(SingleFileRequest("python", "print(1+1)"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CodeRunResultDto>(ok.Value);
        Assert.Equal("2\n", dto.Stdout);
        Assert.Equal("main.txt", codeRunner.LastEntryFilePath);
        Assert.Equal("print(1+1)", codeRunner.LastFiles?[0].Content);
    }

    [Fact]
    public async Task Run_PassesAdditionalFilesThrough()
    {
        var codeRunner = new FakeCodeRunner { Result = new CodeRunResultDto("", "", 0, 10, false, "accepted") };
        var controller = new CodeExecutionController(codeRunner, NewPreviewSessions());
        var request = new RunCodeProjectRequest("main.py", [
            new CodeFileDto("main.py", "python", "import helper"),
            new CodeFileDto("helper.py", "python", "x = 1"),
        ], null);

        await controller.Run(request);

        Assert.Equal(2, codeRunner.LastFiles?.Count);
        Assert.Equal("helper.py", codeRunner.LastFiles?[1].Path);
    }

    [Fact]
    public async Task Run_RejectsEmptyFileList()
    {
        var codeRunner = new FakeCodeRunner();
        var controller = new CodeExecutionController(codeRunner, NewPreviewSessions());

        var result = await controller.Run(new RunCodeProjectRequest("main.py", [], null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(codeRunner.LastEntryFilePath);
    }

    [Fact]
    public async Task Run_RejectsEntryFileNotInProject()
    {
        var codeRunner = new FakeCodeRunner();
        var controller = new CodeExecutionController(codeRunner, NewPreviewSessions());
        var request = new RunCodeProjectRequest("missing.py", [new CodeFileDto("main.py", "python", "print(1)")], null);

        var result = await controller.Run(request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(codeRunner.LastEntryFilePath);
    }

    [Fact]
    public async Task Run_ReturnsBadRequest_ForUnsupportedLanguage()
    {
        var codeRunner = new FakeCodeRunner { ThrowOnRun = new UnsupportedLanguageException("brainfuck") };
        var controller = new CodeExecutionController(codeRunner, NewPreviewSessions());

        var result = await controller.Run(SingleFileRequest("brainfuck", "+++"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("brainfuck", badRequest.Value!.ToString());
    }

    [Fact]
    public async Task Run_ReturnsServiceUnavailable_WhenCodeRunnerIsUnreachable()
    {
        var codeRunner = new FakeCodeRunner { ThrowOnRun = new HttpRequestException("connection refused") };
        var controller = new CodeExecutionController(codeRunner, NewPreviewSessions());

        var result = await controller.Run(SingleFileRequest("python", "print(1)"));

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
    }

    // B2 live preview

    [Fact]
    public async Task RunPreview_RejectsEmptyFileList()
    {
        var controller = new CodeExecutionController(new FakeCodeRunner(), NewPreviewSessions());

        var result = await controller.RunPreview(new RunPreviewRequest("index.html", []));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task RunPreview_RejectsEntryFileNotInProject()
    {
        var controller = new CodeExecutionController(new FakeCodeRunner(), NewPreviewSessions());
        var request = new RunPreviewRequest("missing.html", [new CodeFileDto("index.html", "html", "<p>hi</p>")]);

        var result = await controller.RunPreview(request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task RunPreview_StaticHtmlProject_ReturnsAReachablePreviewUrl()
    {
        var controller = new CodeExecutionController(new FakeCodeRunner(), NewPreviewSessions());
        var request = new RunPreviewRequest("index.html", [new CodeFileDto("index.html", "html", "<p>hello</p>")]);

        var result = await controller.RunPreview(request);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<RunPreviewResponse>(ok.Value);
        Assert.Equal("static", dto.Mode);
        Assert.True(dto.IsReady);
        Assert.StartsWith("http://127.0.0.1:", dto.PreviewUrl);

        using var http = new HttpClient();
        var response = await http.GetAsync(dto.PreviewUrl);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("hello", body);

        await controller.StopPreview(dto.SessionId);
    }

    [Fact]
    public async Task RunPreview_RejectsALanguageWithNoPreviewMode()
    {
        var controller = new CodeExecutionController(new FakeCodeRunner(), NewPreviewSessions());
        var request = new RunPreviewRequest("main.go", [new CodeFileDto("main.go", "go", "package main")]);

        var result = await controller.RunPreview(request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // No local container runtime available (UnavailableContainerCli, per NewPreviewSessions())
    // must surface as the same 503 signal every other unreachable-execution-backend path uses.
    [Fact]
    public async Task RunPreview_PersistentMode_ReturnsServiceUnavailable_WithNoLocalRuntime()
    {
        var controller = new CodeExecutionController(new FakeCodeRunner(), NewPreviewSessions());
        var request = new RunPreviewRequest("main.py", [new CodeFileDto("main.py", "python", "print('hi')")]);

        var result = await controller.RunPreview(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
    }

    [Fact]
    public async Task StopPreview_AlwaysReturnsNoContent_EvenForAnUnknownSession()
    {
        var controller = new CodeExecutionController(new FakeCodeRunner(), NewPreviewSessions());

        var result = await controller.StopPreview(Guid.NewGuid());

        Assert.IsType<NoContentResult>(result);
    }
}
