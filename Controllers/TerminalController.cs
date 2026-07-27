using BackendApi.Contracts;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendApi.Controllers;

// SEK-01 integrated terminal — see TerminalSessionService's doc comment for scope
// (request/response exec against a persistent sandboxed container, not a live pty shell).
[ApiController]
[Route("api/v1/terminal")]
[Authorize]
public class TerminalController(TerminalSessionService terminalSessions) : ControllerBase
{
    [HttpPost("start")]
    public async Task<ActionResult<TerminalStartResponse>> Start(TerminalStartRequest request, CancellationToken ct)
    {
        if (request.Files.Count == 0)
        {
            return BadRequest(new { error = "validation_error", message = "A project must have at least one file." });
        }

        try
        {
            var sessionId = await terminalSessions.StartAsync(request.Files, ct);
            return Ok(new TerminalStartResponse(sessionId));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "terminal_unavailable", message = "The terminal service is unreachable. Try again shortly." });
        }
    }

    [HttpPost("{sessionId:guid}/exec")]
    public async Task<ActionResult<TerminalExecResultDto>> Exec(Guid sessionId, TerminalExecRequest request, CancellationToken ct)
    {
        try
        {
            var result = await terminalSessions.ExecAsync(sessionId, request.Command, ct);
            return Ok(result);
        }
        catch (TerminalSessionNotFoundException ex)
        {
            return NotFound(new { error = "session_not_found", message = ex.Message });
        }
    }

    [HttpPost("{sessionId:guid}/close")]
    public async Task<IActionResult> Close(Guid sessionId, CancellationToken ct)
    {
        await terminalSessions.CloseAsync(sessionId, ct);
        return NoContent();
    }
}
