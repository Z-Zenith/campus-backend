using BackendApi.Contracts;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendApi.Controllers;

// SEK-01: the Coding app's backend — runs a multi-file project's entry point (each file
// written to its own real relative path, see ContainerCodeRunner) and returns
// stdout/stderr/exit code.
[ApiController]
[Route("api/v1")]
[Authorize]
public class CodeExecutionController(ICodeRunner codeRunner, PreviewSessionService previewSessions) : ControllerBase
{
    [HttpPost("code/run")]
    public async Task<ActionResult<CodeRunResultDto>> Run(RunCodeProjectRequest request)
    {
        if (request.Files.Count == 0)
        {
            return BadRequest(new { error = "validation_error", message = "A project must have at least one file." });
        }
        if (!request.Files.Any(f => f.Path == request.EntryFilePath))
        {
            return BadRequest(new { error = "validation_error", message = $"Entry file '{request.EntryFilePath}' is not one of the project's files." });
        }

        try
        {
            var result = await codeRunner.RunAsync(request.EntryFilePath, request.Files, request.Stdin);
            return Ok(result);
        }
        catch (UnsupportedLanguageException ex)
        {
            return BadRequest(new { error = "unsupported_language", message = ex.Message });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "code_execution_unavailable", message = "The Code Execution Service is unreachable. Try again shortly." });
        }
    }

    // B2 live preview (SDA/SEK plan): HTML/CSS/JS projects get a static file server (no
    // container — see PreviewSessionService); Node.js/Python projects that start a real
    // server get the sandboxed persistent-container mode. The desktop client opens the
    // returned previewUrl as a new tab in its own built-in browser.
    [HttpPost("code/run-preview")]
    public async Task<ActionResult<RunPreviewResponse>> RunPreview(RunPreviewRequest request)
    {
        if (request.Files.Count == 0)
        {
            return BadRequest(new { error = "validation_error", message = "A project must have at least one file." });
        }
        if (!request.Files.Any(f => f.Path == request.EntryFilePath))
        {
            return BadRequest(new { error = "validation_error", message = $"Entry file '{request.EntryFilePath}' is not one of the project's files." });
        }

        try
        {
            var (sessionId, port, mode, isReady) = await previewSessions.StartAsync(request.EntryFilePath, request.Files);
            return Ok(new RunPreviewResponse(sessionId, $"http://127.0.0.1:{port}/", mode, isReady));
        }
        catch (UnsupportedLanguageException ex)
        {
            return BadRequest(new { error = "unsupported_language", message = ex.Message });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "code_execution_unavailable", message = "The Code Execution Service is unreachable. Try again shortly." });
        }
    }

    [HttpPost("code/run-preview/{sessionId}/stop")]
    public async Task<IActionResult> StopPreview(Guid sessionId)
    {
        await previewSessions.StopAsync(sessionId);
        return NoContent();
    }
}
