using System.Text;
using System.Text.Json;

namespace Chamelang.DummyClient;

class Program
{
    private static readonly HttpClient client = new();

    // 백엔드 Kestrel 포트에 맞게 주소 확인 (필요시 수정)
    private const string serverUrl = "http://localhost:5000";

    static async Task Main(string[] args)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("   Chamelang 백엔드 테스트 더미 클라이언트");
        Console.WriteLine("==================================================\n");

        // 1. 대화방 페르소나 및 언어 설정 입력
        Console.WriteLine("[1] 대화방 설정");

        Console.Write("▶ AI 페르소나 지침 입력 (엔터 시 기본값): ");
        string? personaInput = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(personaInput))
        {
            personaInput = "You are a friendly UC Berkeley college student. Use casual West Coast slang.";
            Console.WriteLine($"  └> [기본값 적용]: {personaInput}");
        }

        Console.Write("▶ AI 답변 언어 입력 (예: Korean, English, Japanese / 엔터 시 Korean): ");
        string? langInput = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(langInput))
        {
            langInput = "Korean";
            Console.WriteLine($"  └> [기본값 적용]: {langInput}");
        }

        // 2. 방 개설 요청
        Console.WriteLine("\n[2] 서버에 대화방 생성 요청 중...");
        var roomRequest = new
        {
            personaInstruction = personaInput,
            lang = langInput
        };

        string? roomId = await CreateRoomAsync(roomRequest);

        if (string.IsNullOrEmpty(roomId))
        {
            Console.WriteLine("\n[오류] 대화방 개설 실패. 서버 상태 및 appsettings.json의 API 키를 확인하세요.");
            return;
        }

        Console.WriteLine($"\n[성공] 대화방 개설 완료! (Room ID: {roomId})");
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("AI 대화를 시작합니다. (종료하려면 'exit' 또는 'q' 입력)\n");

        // 3. 대화 인터페이스 루프
        while (true)
        {
            Console.Write("\n[사용자] > ");
            string? userMessage = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(userMessage))
                continue;

            string exitCheck = userMessage.Trim().ToLower();
            if (exitCheck == "exit" || exitCheck == "q")
            {
                Console.WriteLine("\n테스트를 종료합니다.");
                break;
            }

            Console.Write("[AI] 응답 대기 중...");
            string aiReply = await SendMessageAsync(roomId, userMessage);

            // 대기 중 메시지 지우고 응답 출력
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.WriteLine($"[AI] > {aiReply}");
        }
    }

    private static async Task<string?> CreateRoomAsync(object requestData)
    {
        try
        {
            string json = JsonSerializer.Serialize(requestData);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await client.PostAsync($"{serverUrl}/api/chat/room", content);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"\n[서버 에러 {response.StatusCode}]: {responseJson}");
                return null;
            }

            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.GetProperty("chatRoomId").GetString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[통신 에러]: {ex.Message}");
            return null;
        }
    }

    private static async Task<string> SendMessageAsync(string roomId, string message)
    {
        try
        {
            var requestData = new
            {
                chatRoomId = roomId,
                message = message
            };

            string json = JsonSerializer.Serialize(requestData);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await client.PostAsync($"{serverUrl}/api/chat/message", content);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return $"[에러 {response.StatusCode}]: {responseJson}";
            }

            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.GetProperty("aiReply").GetString() ?? "응답 없음";
        }
        catch (Exception ex)
        {
            return $"[통신 에러]: {ex.Message}";
        }
    }
}