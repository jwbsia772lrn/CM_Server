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

    public async Task<string> GenerateReplyAsync(string personaInstruction, string lang, string userMessage)
    {
        var googleAi = new GoogleAI(apiKey: _apiKey);

        string combinedSystemInstruction = $"{personaInstruction}\n\nPlease output all your responses in {lang}.";

        var model = googleAi.GenerativeModel(
            model: Model.Gemini3Flash,
            systemInstruction: new Content(combinedSystemInstruction)
        );

        var response = await model.GenerateContent(userMessage);
        return response.Text ?? "No response.";
    }
}