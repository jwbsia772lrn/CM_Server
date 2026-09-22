using System.Threading.Channels;
using Chamelang.Backend.Models;
using Chamelang.Backend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(Channel.CreateUnbounded<ChatJob>());

builder.Services.AddSingleton<GeminiService>();

builder.Services.AddHostedService<ChatBackgroundWorker>();

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();