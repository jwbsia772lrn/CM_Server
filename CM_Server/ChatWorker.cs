using System.Collections.Concurrent;
using System.Threading.Channels;
using Chamelang.Backend.Models;

namespace Chamelang.Backend.Services;

public class ChatBackgroundWorker : BackgroundService
{
    private readonly ChannelReader<ChatJob> _channelReader;
    private readonly GeminiService _geminiService;

    public static readonly ConcurrentDictionary<string, (string PersonaInstruction, string Lang)> RoomPersonaMap = new();

    public ChatBackgroundWorker(Channel<ChatJob> channel, GeminiService geminiService)
    {
        _channelReader = channel.Reader;
        _geminiService = geminiService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _channelReader.ReadAllAsync(stoppingToken))
        {
            try
            {
                string roomId = job.Request.ChatRoomId;

                if (!RoomPersonaMap.TryGetValue(roomId, out var roomConfig))
                {
                    throw new Exception("존재하지 않는 대화방입니다.");
                }

                string aiReply = await _geminiService.GenerateReplyAsync(
                    roomConfig.PersonaInstruction,
                    roomConfig.Lang,
                    job.Request.Message
                );

                job.CompletionSource.SetResult(new ChatResponse(aiReply));
            }
            catch (Exception ex)
            {
                job.CompletionSource.SetException(ex);
            }
        }
    }
}