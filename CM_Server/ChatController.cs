using System.Threading.Channels;
using Chamelang.Backend.Models;
using Chamelang.Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Chamelang.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly ChannelWriter<ChatJob> _channelWriter;

    public ChatController(Channel<ChatJob> channel)
    {
        _channelWriter = channel.Writer;
    }

    [HttpPost("room")]
    public IActionResult CreateRoom([FromBody] CreateRoomRequest request)
    {
        string roomId = "room_" + Guid.NewGuid().ToString("N")[..8];

        ChatBackgroundWorker.RoomPersonaMap[roomId] = (request.PersonaInstruction, request.Lang);

        return Ok(new CreateRoomResponse(roomId));
    }

    [HttpPost("message")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        var job = new ChatJob(request);
        await _channelWriter.WriteAsync(job);

        ChatResponse response = await job.CompletionSource.Task;
        return Ok(response);
    }
}