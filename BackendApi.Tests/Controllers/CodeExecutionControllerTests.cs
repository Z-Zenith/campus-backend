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
    public async Task Run_ReturnsResultFromCodeRunner()
    {
        var codeRunner = new FakeCodeRunner { Result = new CodeRunResultDto("2\n", "", 0, 42, false, "accepted") };
        var controller = new CodeExecutionController(codeRunner);

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
        var controller = new CodeExecutionController(codeRunner);
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
        var controller = new CodeExecutionController(codeRunner);

        var result = await controller.Run(new RunCodeProjectRequest("main.py", [], null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(codeRunner.LastEntryFilePath);
    }

    [Fact]
    public async Task Run_RejectsEntryFileNotInProject()
    {
        var codeRunner = new FakeCodeRunner();
        var controller = new CodeExecutionController(codeRunner);
        var request = new RunCodeProjectRequest("missing.py", [new CodeFileDto("main.py", "python", "print(1)")], null);

        var result = await controller.Run(request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(codeRunner.LastEntryFilePath);
    }

    [Fact]
    public async Task Run_ReturnsBadRequest_ForUnsupportedLanguage()
    {
        var codeRunner = new FakeCodeRunner { ThrowOnRun = new UnsupportedLanguageException("brainfuck") };
        var controller = new CodeExecutionController(codeRunner);

        var result = await controller.Run(SingleFileRequest("brainfuck", "+++"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("brainfuck", badRequest.Value!.ToString());
    }

    [Fact]
    public async Task Run_ReturnsServiceUnavailable_WhenCodeRunnerIsUnreachable()
    {
        var codeRunner = new FakeCodeRunner { ThrowOnRun = new HttpRequestException("connection refused") };
        var controller = new CodeExecutionController(codeRunner);

        var result = await controller.Run(SingleFileRequest("python", "print(1)"));

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
    }
}
