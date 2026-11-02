using Microsoft.Extensions.Configuration;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;

namespace Chamelang.Backend.Services;

public class GeminiService
{
    private readonly string _apiKey;

    public GeminiService(IConfiguration configuration)
    {
        _apiKey = configuration["Gemini:ApiKey"]
                  ?? throw new InvalidOperationException("Gemini API 키가 설정되지 않았습니다.");
    }

    /// <summary>
    /// 슬라이딩 윈도우 기반 이전 대화 컨텍스트(최대 20개)를 포함하여 Gemini 응답을 생성
    /// </summary>
    public async Task<string> GenerateReplyWithHistoryAsync(
        string personaInstruction,
        string lang,
        IReadOnlyList<CachedMessage> history,
        string userMessage)
    {
        var googleAi = new GoogleAI(apiKey: _apiKey);

        string combinedSystemInstruction = $"{personaInstruction}\n\nPlease output all your responses in {lang}.";

        var model = googleAi.GenerativeModel(
            model: Model.Gemini3Flash,
            systemInstruction: new Content(combinedSystemInstruction)
        );

        // 슬라이딩 윈도우: 최근 최대 20개의 메시지 히스토리 조립
        var contents = new List<Content>();

        var window = history.TakeLast(20);
        foreach (var msg in window)
        {
            string role = msg.SenderType.Equals("AI", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
            contents.Add(new Content(msg.Content, role));
        }

        // 현재 사용자 입력 추가
        contents.Add(new Content(userMessage, "user"));

        var request = new GenerateContentRequest
        {
            Contents = contents
        };

        var response = await model.GenerateContent(request);
        return response.Text ?? "No response.";
    }
}