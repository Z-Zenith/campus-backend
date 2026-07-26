using BackendApi.Contracts;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendApi.Controllers;

// SEK-01: the Coding app's backend — a thin proxy to the self-hosted Code Execution
// Service (Judge0). No persistence yet (see Judge0Client's own remarks); this just runs
// the given source and returns stdout/stderr/exit code.
[ApiController]
[Route("api/v1")]
[Authorize]
public class CodeExecutionController(IJudge0Client judge0) : ControllerBase
{
    [HttpPost("code/run")]
    public async Task<ActionResult<CodeRunResultDto>> Run(RunCodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { error = "content_required", message = "Source must not be empty." });
        }

        try
        {
            var result = await judge0.RunAsync(request.Language, request.Content, request.Stdin);
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
