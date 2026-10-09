using ChogGames.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructureOptions(builder.Configuration);

var app = builder.Build();

app.Run();
