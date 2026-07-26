using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Services;
using BackendApi.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BackendApi.Tests.Controllers;

public class CodeExecutionControllerTests
{
    [Fact]
    public async Task Run_ReturnsResultFromJudge0()
    {
        var judge0 = new FakeJudge0Client { Result = new CodeRunResultDto("2\n", "", 0, 42, false) };
        var controller = new CodeExecutionController(judge0);

        var result = await controller.Run(new RunCodeRequest("python", "print(1+1)", null, null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CodeRunResultDto>(ok.Value);
        Assert.Equal("2\n", dto.Stdout);
        Assert.Equal("python", judge0.LastLanguage);
        Assert.Equal("print(1+1)", judge0.LastContent);
    }

    [Fact]
    public async Task Run_RejectsEmptyContent()
    {
        var judge0 = new FakeJudge0Client();
        var controller = new CodeExecutionController(judge0);

        var result = await controller.Run(new RunCodeRequest("python", "   ", null, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(judge0.LastLanguage);
    }

    [Fact]
    public async Task Run_ReturnsBadRequest_ForUnsupportedLanguage()
    {
        var judge0 = new FakeJudge0Client { ThrowOnRun = new UnsupportedLanguageException("brainfuck") };
        var controller = new CodeExecutionController(judge0);

        var result = await controller.Run(new RunCodeRequest("brainfuck", "+++", null, null));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("brainfuck", badRequest.Value!.ToString());
    }

    [Fact]
    public async Task Run_ReturnsServiceUnavailable_WhenJudge0IsUnreachable()
    {
        var judge0 = new FakeJudge0Client { ThrowOnRun = new HttpRequestException("connection refused") };
        var controller = new CodeExecutionController(judge0);

        var result = await controller.Run(new RunCodeRequest("python", "print(1)", null, null));

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
    }
}
