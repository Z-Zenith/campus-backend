using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Services;
using BackendApi.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BackendApi.Tests.Controllers;

public class CodeExecutionControllerTests
{
    private static RunCodeProjectRequest SingleFileRequest(string language, string content, string? stdin = null) =>
        new("main.txt", [new CodeFileDto("main.txt", language, content)], stdin);

    [Fact]
    public async Task Run_ReturnsResultFromJudge0()
    {
        var judge0 = new FakeJudge0Client { Result = new CodeRunResultDto("2\n", "", 0, 42, false, "accepted") };
        var controller = new CodeExecutionController(judge0);

        var result = await controller.Run(SingleFileRequest("python", "print(1+1)"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CodeRunResultDto>(ok.Value);
        Assert.Equal("2\n", dto.Stdout);
        Assert.Equal("main.txt", judge0.LastEntryFilePath);
        Assert.Equal("print(1+1)", judge0.LastFiles?[0].Content);
    }

    [Fact]
    public async Task Run_PassesAdditionalFilesThrough()
    {
        var judge0 = new FakeJudge0Client { Result = new CodeRunResultDto("", "", 0, 10, false, "accepted") };
        var controller = new CodeExecutionController(judge0);
        var request = new RunCodeProjectRequest("main.py", [
            new CodeFileDto("main.py", "python", "import helper"),
            new CodeFileDto("helper.py", "python", "x = 1"),
        ], null);

        await controller.Run(request);

        Assert.Equal(2, judge0.LastFiles?.Count);
        Assert.Equal("helper.py", judge0.LastFiles?[1].Path);
    }

    [Fact]
    public async Task Run_RejectsEmptyFileList()
    {
        var judge0 = new FakeJudge0Client();
        var controller = new CodeExecutionController(judge0);

        var result = await controller.Run(new RunCodeProjectRequest("main.py", [], null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(judge0.LastEntryFilePath);
    }

    [Fact]
    public async Task Run_RejectsEntryFileNotInProject()
    {
        var judge0 = new FakeJudge0Client();
        var controller = new CodeExecutionController(judge0);
        var request = new RunCodeProjectRequest("missing.py", [new CodeFileDto("main.py", "python", "print(1)")], null);

        var result = await controller.Run(request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(judge0.LastEntryFilePath);
    }

    [Fact]
    public async Task Run_ReturnsBadRequest_ForUnsupportedLanguage()
    {
        var judge0 = new FakeJudge0Client { ThrowOnRun = new UnsupportedLanguageException("brainfuck") };
        var controller = new CodeExecutionController(judge0);

        var result = await controller.Run(SingleFileRequest("brainfuck", "+++"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("brainfuck", badRequest.Value!.ToString());
    }

    [Fact]
    public async Task Run_ReturnsServiceUnavailable_WhenJudge0IsUnreachable()
    {
        var judge0 = new FakeJudge0Client { ThrowOnRun = new HttpRequestException("connection refused") };
        var controller = new CodeExecutionController(judge0);

        var result = await controller.Run(SingleFileRequest("python", "print(1)"));

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
    }
}
