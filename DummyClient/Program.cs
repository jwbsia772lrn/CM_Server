using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Chamelang.DummyClient;

class Program
{
    private static readonly HttpClient httpClient = new();
    private static string serverUrl = "http://localhost:58019";
    private static string currentUserId = "1";
    private static string currentRoomId = "";
    private static readonly string roomsFilePath = Path.Combine(AppContext.BaseDirectory, "recent_rooms.json");

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("==================================================");
        Console.WriteLine("   Chamelang 백엔드 테스트 더미 클라이언트 (v2.0)");
        Console.WriteLine("==================================================");

        // 초기 시작 시 기본 로그인(유저 식별) 진행
        await PromptUserLoginAsync();

        bool running = true;
        while (running)
        {
            PrintStatusHeader();
            Console.WriteLine("[메인 메뉴]");
            Console.WriteLine("  1. 유저 변경 / 인증 (User Identity 식별)");
            Console.WriteLine("  2. 신규 대화방 개설 (FK_CHAT_ROOMS_USER 검증)");
            Console.WriteLine("  3. 기존 대화방 입장 및 대화내역 동기화 (재접속 검증)");
            Console.WriteLine("  4. 저장된 ROOM_ID 목록 관리 (영속성 확인)");
            Console.WriteLine("  5. 실시간 대화 모드 (현재 선택된 방)");
            Console.WriteLine("  6. 비동기/대량 메시지 부하 테스트 (무결성 및 정렬 검증)");
            Console.WriteLine("  7. DB 제약조건 위반 예외 응답 테스트 (서버 안정성 검증)");
            Console.WriteLine("  8. 서버 URL 변경 (현재: " + serverUrl + ")");
            Console.WriteLine("  0. 종료");
            Console.Write("\n선택 > ");

            string? input = Console.ReadLine()?.Trim();
            Console.WriteLine();

            switch (input)
            {
                case "1":
                    await PromptUserLoginAsync();
                    break;
                case "2":
                    await HandleCreateRoomAsync();
                    break;
                case "3":
                    await HandleJoinRoomAsync();
                    break;
                case "4":
                    ShowSavedRooms();
                    break;
                case "5":
                    await HandleLiveChatAsync();
                    break;
                case "6":
                    await HandleBulkMessageTestAsync();
                    break;
                case "7":
                    await HandleExceptionTestAsync();
                    break;
                case "8":
                    ChangeServerUrl();
                    break;
                case "0":
                    running = false;
                    Console.WriteLine("클라이언트를 종료합니다.");
                    break;
                default:
                    Console.WriteLine("올바른 번호를 선택하세요.");
                    break;
            }

            if (running)
            {
                Console.WriteLine("\n[Enter 키를 누르면 메뉴로 돌아갑니다]");
                Console.ReadLine();
            }
        }
    }

    private static void PrintStatusHeader()
    {
        Console.WriteLine("\n==================================================");
        Console.WriteLine($"[접속 서버]: {serverUrl}");
        Console.WriteLine($"[현재 유저]: {(string.IsNullOrEmpty(currentUserId) ? "(미인증)" : currentUserId)}");
        Console.WriteLine($"[활성 방ID]: {(string.IsNullOrEmpty(currentRoomId) ? "(선택되지 않음)" : currentRoomId)}");
        Console.WriteLine("==================================================");
    }

    #region 1. 사용자 식별자(User Identity) 동작

    private static async Task PromptUserLoginAsync()
    {
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("[1] 사용자 식별(User Identity) 절차");
        Console.WriteLine("--------------------------------------------------");
        Console.Write("▶ 사용할 USER_ID 입력 (엔터 시 기본값 '1' 또는 'test_user'): ");
        string? userId = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = "1";
        }

        Console.WriteLine($"\n서버에 유저('{userId}') 존재 여부 확인 중...");

        try
        {
            var response = await httpClient.GetAsync($"{serverUrl}/api/user/{userId}");
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                string userName = doc.RootElement.GetProperty("userName").GetString() ?? userId;
                currentUserId = userId;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[인증 성공] 확인된 사용자: {userName} (USER_ID: {userId})");
                Console.ResetColor();
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[알림] DB에 '{userId}'가 존재하지 않습니다.");
                Console.ResetColor();
                Console.Write("▶ 이 유저 ID를 새로 등록하시겠습니까? (Y/n): ");
                string? ans = Console.ReadLine()?.Trim().ToLower();
                if (ans != "n")
                {
                    Console.Write("▶ 닉네임/유저 이름 입력 (엔터 시 기본값): ");
                    string? name = Console.ReadLine()?.Trim();
                    if (string.IsNullOrWhiteSpace(name)) name = userId + "_name";

                    var regResponse = await httpClient.PostAsJsonAsync($"{serverUrl}/api/user", new { userId, userName = name });
                    if (regResponse.IsSuccessStatusCode)
                    {
                        currentUserId = userId;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[성공] 새 유저 등록 및 세션 식별 완료: {name} ({userId})");
                        Console.ResetColor();
                    }
                    else
                    {
                        string err = await regResponse.Content.ReadAsStringAsync();
                        LogServerError("유저 등록 실패", regResponse.StatusCode, err);
                    }
                }
                else
                {
                    currentUserId = userId;
                    Console.WriteLine($"[주의] 미존재 유저 '{userId}' 상태로 유지합니다. (FK 제약조건 테스트 가능)");
                }
            }
            else
            {
                string err = await response.Content.ReadAsStringAsync();
                LogServerError("유저 확인 통신 실패", response.StatusCode, err);
            }
        }
        catch (Exception ex)
        {
            LogClientException("서버 연결 실패", ex);
        }
    }

    #endregion

    #region 2. 영속적인 방 식별자(ROOM_ID) 관리 및 생성

    private static async Task HandleCreateRoomAsync()
    {
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("[2] 신규 대화방 개설 (FK_CHAT_ROOMS_USER 연동)");
        Console.WriteLine("--------------------------------------------------");

        if (string.IsNullOrEmpty(currentUserId))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[경고] USER_ID가 설정되지 않았습니다. 먼저 유저 로그인을 진행하세요.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"▶ 방 생성자 USER_ID: {currentUserId}");
        Console.Write("▶ AI 페르소나 지침 (엔터 시 기본값): ");
        string? persona = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(persona))
            persona = "You are a friendly college student. Use casual slang.";

        Console.Write("▶ AI 답변 언어 (엔터 시 Korean): ");
        string? lang = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(lang))
            lang = "Korean";

        var payload = new
        {
            userId = currentUserId,
            personaInstruction = persona,
            lang = lang
        };

        try
        {
            var response = await httpClient.PostAsJsonAsync($"{serverUrl}/api/chat/room", payload);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                LogServerError("방 생성 거부됨", response.StatusCode, responseJson);
                return;
            }

            using var doc = JsonDocument.Parse(responseJson);
            string roomId = doc.RootElement.GetProperty("chatRoomId").GetString() ?? "";

            currentRoomId = roomId;

            // 로컬 영속 저장소(recent_rooms.json)에 저장
            SaveRoomLocally(new StoredRoomInfo
            {
                RoomId = roomId,
                UserId = currentUserId,
                Persona = persona,
                Lang = lang,
                CreatedAt = DateTime.UtcNow
            });

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[성공] 대화방이 DB에 영구 생성되었습니다!");
            Console.WriteLine($"  - ROOM_ID: {roomId}");
            Console.WriteLine($"  - 소유자: {currentUserId}");
            Console.WriteLine($"  - 로컬 파일에 방 번호가 저장되었습니다.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            LogClientException("방 생성 요청 실패", ex);
        }
    }

    private static async Task HandleJoinRoomAsync()
    {
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("[3] 기존 대화방 입장 및 재접속 검증");
        Console.WriteLine("--------------------------------------------------");

        var rooms = LoadSavedRooms();
        if (rooms.Count > 0)
        {
            Console.WriteLine("▶ 최근 저장된 방 목록:");
            for (int i = 0; i < rooms.Count; i++)
            {
                Console.WriteLine($"  [{i + 1}] {rooms[i].RoomId} (유저: {rooms[i].UserId}, 언어: {rooms[i].Lang}, 생성: {rooms[i].CreatedAt:yyyy-MM-dd HH:mm})");
            }
            Console.WriteLine("  [0] 직접 방 번호 입력");
            Console.Write("\n입장할 방 선택 > ");
            string? choice = Console.ReadLine()?.Trim();

            if (int.TryParse(choice, out int idx) && idx >= 1 && idx <= rooms.Count)
            {
                currentRoomId = rooms[idx - 1].RoomId;
            }
            else
            {
                Console.Write("입장할 ROOM_ID 직접 입력: ");
                currentRoomId = Console.ReadLine()?.Trim() ?? "";
            }
        }
        else
        {
            Console.Write("입장할 ROOM_ID 입력: ");
            currentRoomId = Console.ReadLine()?.Trim() ?? "";
        }

        if (string.IsNullOrWhiteSpace(currentRoomId))
        {
            Console.WriteLine("방 번호가 입력되지 않았습니다.");
            return;
        }

        // 1. 방 정보 검증 (서버 재시작 후에도 유지되는지 확인)
        Console.WriteLine($"\n서버 DB에서 방({currentRoomId}) 정보 확인 중...");
        try
        {
            var roomResponse = await httpClient.GetAsync($"{serverUrl}/api/chat/room/{currentRoomId}");
            if (!roomResponse.IsSuccessStatusCode)
            {
                string err = await roomResponse.Content.ReadAsStringAsync();
                LogServerError("방 진입 실패", roomResponse.StatusCode, err);
                currentRoomId = "";
                return;
            }

            string roomJson = await roomResponse.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(roomJson);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[성공] DB에서 방 확인 완료!");
            Console.WriteLine($"  - 소유자: {doc.RootElement.GetProperty("userId").GetString()}");
            Console.WriteLine($"  - 페르소나: {doc.RootElement.GetProperty("personaInstruction").GetString()}");
            Console.WriteLine($"  - 언어: {doc.RootElement.GetProperty("lang").GetString()}");
            Console.ResetColor();

            // 2. 과거 대화 내역 동기화
            await FetchAndPrintChatHistoryAsync(currentRoomId);
        }
        catch (Exception ex)
        {
            LogClientException("대화방 접속 실패", ex);
        }
    }

    #endregion

    #region 3. 이전 대화 기록 동기화 (Chat History Fetching)

    private static async Task FetchAndPrintChatHistoryAsync(string roomId)
    {
        Console.WriteLine("\n[대화 기록 동기화] DB(CHAT_MESSAGES)에서 이전 대화 내역 조회 중...");
        try
        {
            var response = await httpClient.GetAsync($"{serverUrl}/api/chat/room/{roomId}/messages");
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                LogServerError("대화 내역 조회 실패", response.StatusCode, err);
                return;
            }

            var messages = await response.Content.ReadFromJsonAsync<List<ChatMessageDto>>();
            if (messages == null || messages.Count == 0)
            {
                Console.WriteLine("▶ 저장된 이전 대화 내역이 없습니다. (새로운 대화를 시작하세요)");
                return;
            }

            // 발송 시각 및 순서 기준 정렬 확인
            var sortedMessages = messages.OrderBy(m => m.MsgTimestamp).ThenBy(m => m.MessageId).ToList();

            Console.WriteLine("==================================================");
            Console.WriteLine($"   동기화된 이전 대화 내역 (총 {sortedMessages.Count}건)");
            Console.WriteLine("==================================================");

            foreach (var msg in sortedMessages)
            {
                string timeStr = msg.MsgTimestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                if (msg.SenderType.ToUpper() == "USER")
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[USER] ({timeStr} | MsgID: {msg.MessageId})");
                    Console.WriteLine($"  > {msg.Content}");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[AI]   ({timeStr} | MsgID: {msg.MessageId})");
                    Console.WriteLine($"  > {msg.Content}");
                }
                Console.ResetColor();
            }
            Console.WriteLine("==================================================");
        }
        catch (Exception ex)
        {
            LogClientException("대화 내역 동기화 오류", ex);
        }
    }

    #endregion

    #region 4. 실시간 대화 모드

    private static async Task HandleLiveChatAsync()
    {
        if (string.IsNullOrEmpty(currentRoomId))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[경고] 먼저 대화방을 개설하거나 입장해야 합니다.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine($"실시간 대화 모드 시작 (방 ID: {currentRoomId})");
        Console.WriteLine("종료하려면 'exit' 또는 'q'를 입력하세요.");
        Console.WriteLine("--------------------------------------------------");

        while (true)
        {
            Console.Write("\n[사용자] > ");
            string? msg = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(msg)) continue;

            string cmd = msg.Trim().ToLower();
            if (cmd == "exit" || cmd == "q")
            {
                Console.WriteLine("대화 모드를 종료합니다.");
                break;
            }

            Console.Write("[AI] 응답 대기 및 DB 저장 중...");
            var result = await SendMessageAsync(currentRoomId, msg, currentUserId);
            Console.SetCursorPosition(0, Console.CursorTop);

            if (result.Success)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[AI] (MsgID: {result.Response?.AiMessageId} | {result.Response?.MsgTimestamp.ToLocalTime():HH:mm:ss}) > {result.Response?.AiReply}");
                Console.ResetColor();
            }
            else
            {
                LogServerError("메시지 전송 실패", result.StatusCode, result.ErrorBody);
            }
        }
    }

    private static async Task<(bool Success, System.Net.HttpStatusCode StatusCode, ChatResponse? Response, string ErrorBody)>
        SendMessageAsync(string roomId, string message, string userId)
    {
        try
        {
            var payload = new
            {
                chatRoomId = roomId,
                message = message,
                userId = userId
            };

            var response = await httpClient.PostAsJsonAsync($"{serverUrl}/api/chat/message", payload);
            if (response.IsSuccessStatusCode)
            {
                var chatResp = await response.Content.ReadFromJsonAsync<ChatResponse>();
                return (true, response.StatusCode, chatResp, "");
            }
            else
            {
                string err = await response.Content.ReadAsStringAsync();
                return (false, response.StatusCode, null, err);
            }
        }
        catch (Exception ex)
        {
            return (false, System.Net.HttpStatusCode.InternalServerError, null, ex.Message);
        }
    }

    #endregion

    #region 5. 비동기/대량 메시지 전송 시 무결성 수용 (부하 테스트)

    private static async Task HandleBulkMessageTestAsync()
    {
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("[6] 비동기/대량 메시지 부하 테스트 (무결성 및 정렬 수용)");
        Console.WriteLine("--------------------------------------------------");

        if (string.IsNullOrEmpty(currentRoomId))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[경고] 활성화된 방 ID가 없습니다. 먼저 방에 입장하세요.");
            Console.ResetColor();
            return;
        }

        Console.Write("전송할 연속 메시지 개수 (기본 3, 최대 10): ");
        string? countStr = Console.ReadLine()?.Trim();
        if (!int.TryParse(countStr, out int count) || count <= 0) count = 3;
        if (count > 10) count = 10;

        Console.WriteLine($"\n[부하 테스트] {count}개의 메시지를 짧은 간격(비동기)으로 전송합니다...");
        var sendTasks = new List<Task<(int Index, string SentText, bool Success, ChatResponse? Resp, string Error)>>();

        for (int i = 1; i <= count; i++)
        {
            int index = i;
            string testMsg = $"[부하테스트 #{index}] AI 번호순서 확인용 메시지입니다. 현재시각: {DateTime.Now:HH:mm:ss.fff}";
            Console.WriteLine($"  -> 메시지 #{index} 발송 요청...");

            sendTasks.Add(Task.Run(async () =>
            {
                var res = await SendMessageAsync(currentRoomId, testMsg, currentUserId);
                return (index, testMsg, res.Success, res.Response, res.ErrorBody);
            }));

            // 부하 테스트: 아주 짧은 딜레이로 연쇄 발송
            await Task.Delay(100);
        }

        Console.WriteLine("\n모든 응답 대기 중 (DB I/O 및 Gemini AI 처리)...");
        var results = await Task.WhenAll(sendTasks);

        Console.WriteLine("\n==================================================");
        Console.WriteLine("             부하 테스트 결과 리포트");
        Console.WriteLine("==================================================");

        int successCount = 0;
        foreach (var r in results.OrderBy(x => x.Resp?.UserMessageId ?? x.Index))
        {
            if (r.Success && r.Resp != null)
            {
                successCount++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[성공 #{r.Index}] USER_MSG_ID: {r.Resp.UserMessageId} | AI_MSG_ID: {r.Resp.AiMessageId}");
                Console.WriteLine($"  타임스탬프: {r.Resp.MsgTimestamp.ToLocalTime():HH:mm:ss.fff}");
                Console.WriteLine($"  AI 응답: {r.Resp.AiReply.Replace("\n", " ").Substring(0, Math.Min(60, r.Resp.AiReply.Length))}...");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[실패 #{r.Index}] 오류: {r.Error}");
                Console.ResetColor();
            }
        }

        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine($"총 전송: {count}건 | 성공: {successCount}건 | 실패: {count - successCount}건");
        Console.WriteLine("DB 동기화 여부를 확인하기 위해 최신 대화 목록을 다시 조회합니다.");
        await FetchAndPrintChatHistoryAsync(currentRoomId);
    }

    #endregion

    #region 6. DB 제약조건 위반에 따른 예외 응답 처리

    private static async Task HandleExceptionTestAsync()
    {
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("[7] DB 제약조건 위반 예외 응답 처리 검증");
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("서버가 크래시되지 않고 정상 에러 패킷을 반환하며,");
        Console.WriteLine("클라이언트가 연결 유지 상태에서 안전하게 로깅하는지 검증합니다.\n");

        // 케이스 1: 가짜 유저 ID로 방 생성 시도 (FK_CHAT_ROOMS_USER 위반)
        Console.WriteLine("▶ [시나리오 1] 유효하지 않은 가짜 USER_ID로 방 생성 요청");
        string fakeUserId = "fake_user_non_existent_" + Guid.NewGuid().ToString("N")[..6];
        var fakeRoomPayload = new
        {
            userId = fakeUserId,
            personaInstruction = "Test",
            lang = "Korean"
        };
        try
        {
            var res1 = await httpClient.PostAsJsonAsync($"{serverUrl}/api/chat/room", fakeRoomPayload);
            string body1 = await res1.Content.ReadAsStringAsync();
            if (!res1.IsSuccessStatusCode)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  └> [성공 검증] 서버가 크래시되지 않고 정상 거부했습니다!");
                Console.ResetColor();
                LogServerError("시나리오 1 거부 결과", res1.StatusCode, body1);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  └> [경고] 존재하지 않는 유저인데 방이 생성되었습니다 (FK 제약조건 누락 의심)");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            LogClientException("시나리오 1 요청 예외", ex);
        }

        // 케이스 2: 존재하지 않는 방 번호로 메시지 전송
        Console.WriteLine("\n▶ [시나리오 2] 존재하지 않는 ROOM_ID로 메시지 전송 요청");
        string fakeRoomId = "room_fake_99999999";
        var fakeMsgPayload = new
        {
            chatRoomId = fakeRoomId,
            message = "테스트 메시지",
            userId = currentUserId
        };
        try
        {
            var res2 = await httpClient.PostAsJsonAsync($"{serverUrl}/api/chat/message", fakeMsgPayload);
            string body2 = await res2.Content.ReadAsStringAsync();
            if (!res2.IsSuccessStatusCode)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  └> [성공 검증] 서버가 존재하지 않는 방을 감지하고 정상 에러 응답을 반환했습니다!");
                Console.ResetColor();
                LogServerError("시나리오 2 거부 결과", res2.StatusCode, body2);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  └> [경고] 없는 방인데 성공 응답이 왔습니다.");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            LogClientException("시나리오 2 요청 예외", ex);
        }

        // 케이스 3: 필수 필드 누락 (빈 메시지 전송)
        Console.WriteLine("\n▶ [시나리오 3] 필수 필드(메시지 본문) 누락/공백 요청");
        var emptyPayload = new
        {
            chatRoomId = currentRoomId.Length > 0 ? currentRoomId : "room_test",
            message = "   ",
            userId = currentUserId
        };
        try
        {
            var res3 = await httpClient.PostAsJsonAsync($"{serverUrl}/api/chat/message", emptyPayload);
            string body3 = await res3.Content.ReadAsStringAsync();
            if (!res3.IsSuccessStatusCode)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  └> [성공 검증] 서버가 빈 본문을 감지하고 400 Bad Request를 반환했습니다!");
                Console.ResetColor();
                LogServerError("시나리오 3 거부 결과", res3.StatusCode, body3);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  └> [경고] 빈 메시지가 허용되었습니다.");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            LogClientException("시나리오 3 요청 예외", ex);
        }
    }

    #endregion

    #region 로컬 파일 영속성 및 유틸리티

    private static void SaveRoomLocally(StoredRoomInfo room)
    {
        try
        {
            var list = LoadSavedRooms();
            list.RemoveAll(r => r.RoomId == room.RoomId);
            list.Insert(0, room);
            if (list.Count > 20) list = list.Take(20).ToList();

            string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(roomsFilePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[로컬 저장 오류]: {ex.Message}");
        }
    }

    private static List<StoredRoomInfo> LoadSavedRooms()
    {
        try
        {
            if (!File.Exists(roomsFilePath)) return new List<StoredRoomInfo>();
            string json = File.ReadAllText(roomsFilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<StoredRoomInfo>>(json) ?? new List<StoredRoomInfo>();
        }
        catch
        {
            return new List<StoredRoomInfo>();
        }
    }

    private static void ShowSavedRooms()
    {
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("[4] 로컬 파일(recent_rooms.json)에 저장된 방 목록");
        Console.WriteLine("--------------------------------------------------");
        var rooms = LoadSavedRooms();
        if (rooms.Count == 0)
        {
            Console.WriteLine("저장된 방 목록이 없습니다.");
            return;
        }

        for (int i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            Console.WriteLine($"[{i + 1}] ROOM_ID: {r.RoomId}");
            Console.WriteLine($"    유저: {r.UserId} | 언어: {r.Lang} | 생성시각: {r.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"    페르소나: {r.Persona}");
        }
    }

    private static void ChangeServerUrl()
    {
        Console.Write($"새 서버 URL 입력 (현재: {serverUrl}): ");
        string? newUrl = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(newUrl))
        {
            serverUrl = newUrl.TrimEnd('/');
            Console.WriteLine($"서버 URL이 '{serverUrl}'로 변경되었습니다.");
        }
    }

    private static void LogServerError(string title, System.Net.HttpStatusCode status, string body)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[서버 응답 에러] {title} (HTTP {(int)status} {status}):");
        Console.WriteLine($"  └> {body}");
        Console.ResetColor();
    }

    private static void LogClientException(string title, Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[클라이언트 예외] {title}: {ex.Message}");
        Console.ResetColor();
    }

    #endregion
}

public class StoredRoomInfo
{
    public string RoomId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Persona { get; set; } = string.Empty;
    public string Lang { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class ChatMessageDto
{
    public long MessageId { get; set; }
    public string ChatRoomId { get; set; } = string.Empty;
    public string SenderType { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime MsgTimestamp { get; set; }
}

public class ChatResponse
{
    public long UserMessageId { get; set; }
    public long AiMessageId { get; set; }
    public string AiReply { get; set; } = string.Empty;
    public DateTime MsgTimestamp { get; set; }
}