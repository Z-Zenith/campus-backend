using BackendApi.Contracts;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendApi.Controllers;

// SEK-01: the Coding app's backend — a thin proxy to the self-hosted Code Execution
// Service (Judge0). Runs a multi-file project's entry point (plus any additional files
// flattened alongside it, see Judge0Client) and returns stdout/stderr/exit code.
[ApiController]
[Route("api/v1")]
[Authorize]
public class CodeExecutionController(IJudge0Client judge0) : ControllerBase
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
            var result = await judge0.RunAsync(request.EntryFilePath, request.Files, request.Stdin);
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
}
