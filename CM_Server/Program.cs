using System.Threading.Channels;
using Chamelang.Backend.Data;
using Chamelang.Backend.Models;
using Chamelang.Backend.Services;
using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;

var builder = WebApplication.CreateBuilder(args);

// 1. Oracle 전자지갑(Wallet) 경로 설정
string? walletPath = builder.Configuration["Oracle:WalletPath"];
if (string.IsNullOrWhiteSpace(walletPath) || !Directory.Exists(walletPath))
{
    walletPath = FindWalletDirectory(builder.Environment.ContentRootPath);
}

if (!string.IsNullOrEmpty(walletPath) && Directory.Exists(walletPath))
{
    OracleConfiguration.TnsAdmin = walletPath;
    OracleConfiguration.WalletLocation = walletPath;
    Console.WriteLine($"[Oracle] Wallet 경로가 설정되었습니다: {walletPath}");
}
else
{
    Console.WriteLine("[Oracle] 경고: Wallet 경로를 찾을 수 없습니다. appsettings.json의 'Oracle:WalletPath'를 확인하세요.");
}

// 2. Oracle DbContext 등록
string connectionString = builder.Configuration.GetConnectionString("OracleDbContext")
    ?? "User Id=ADMIN;Password=YOUR_PASSWORD;Data Source=sze00rzn1ns47mad_high;";

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseOracle(connectionString);
});

// 3. 서비스 및 백그라운드 워커 등록
builder.Services.AddSingleton(Channel.CreateUnbounded<ChatJob>());
builder.Services.AddSingleton(Channel.CreateUnbounded<DbMessageJob>());
builder.Services.AddSingleton<ChatSessionCacheService>();
var idGenerator = new MessageIdGenerator();
builder.Services.AddSingleton(idGenerator);
builder.Services.AddSingleton<GeminiService>();
builder.Services.AddHostedService<ChatBackgroundWorker>();
builder.Services.AddHostedService<DbPersistenceBackgroundWorker>();
builder.Services.AddHostedService<DataRetentionCleanerWorker>();
builder.Services.AddControllers();

var app = builder.Build();

// 4. 데이터베이스 기본 시드 데이터 및 상태 점검
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var dbContext = services.GetRequiredService<AppDbContext>();

        // LAST_ACTIVE_AT 컬럼 누락 여부 확인 및 자동 마이그레이션
        using (var cmd = dbContext.Database.GetDbConnection().CreateCommand())
        {
            dbContext.Database.OpenConnection();
            cmd.CommandText = "SELECT COUNT(*) FROM user_tab_columns WHERE table_name = 'CHAT_ROOMS' AND column_name = 'LAST_ACTIVE_AT'";
            var colCount = Convert.ToInt32(cmd.ExecuteScalar());
            if (colCount == 0)
            {
                cmd.CommandText = "ALTER TABLE \"CHAT_ROOMS\" ADD (\"LAST_ACTIVE_AT\" TIMESTAMP(6))";
                cmd.ExecuteNonQuery();
                logger.LogInformation("[Oracle Migration] CHAT_ROOMS 테이블에 'LAST_ACTIVE_AT' 컬럼을 추가했습니다.");
            }
        }

        // 테스트용 기본 사용자 확인/시딩
        var existingUser = dbContext.Users.FirstOrDefault(u => u.UserId == 1 || u.Username == "test_user");
        if (existingUser == null)
        {
            dbContext.Users.Add(new User { UserId = 1, Username = "test_user", CreatedAt = DateTime.UtcNow });
            dbContext.SaveChanges();
            logger.LogInformation("[Oracle] 기본 테스트 사용자(USER_ID: 1, USERNAME: 'test_user') 시드 완료.");
        }
        else
        {
            logger.LogInformation("[Oracle] 기본 테스트 사용자 확인: USER_ID={UserId}, USERNAME='{Username}'", existingUser.UserId, existingUser.Username);
        }

        // 고유 MessageIdGenerator 시드값 초기화
        long maxMsgId = dbContext.ChatMessages.Select(m => (long?)m.MessageId).Max() ?? 0;
        idGenerator.Initialize(maxMsgId);
        logger.LogInformation("[Oracle] MessageIdGenerator 초기화 완료 (Current Max ID: {MaxId})", maxMsgId);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "[Oracle] DB 초기화 중 경고/오류 발생 (테이블 및 연결 설정 확인 요망)");
    }
}

app.MapControllers();

app.Run();

// Wallet 디렉터리 자동 탐색 헬퍼 메서드
static string? FindWalletDirectory(string basePath)
{
    var candidates = new[]
    {
        Path.Combine(basePath, "..", "Wallet"),
        Path.Combine(basePath, "Wallet"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Wallet"),
        Path.Combine(AppContext.BaseDirectory, "Wallet")
    };

    foreach (var dir in candidates)
    {
        if (!Directory.Exists(dir)) continue;

        if (File.Exists(Path.Combine(dir, "tnsnames.ora")))
            return Path.GetFullPath(dir);

        foreach (var sub in Directory.GetDirectories(dir))
        {
            if (File.Exists(Path.Combine(sub, "tnsnames.ora")))
                return Path.GetFullPath(sub);
        }
    }

    return null;
}