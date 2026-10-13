using System.Text.Json.Serialization;
using ChogGames.Api.Catalog;
using ChogGames.Api.Characters;
using ChogGames.Api.ChogLife;
using ChogGames.Api.Health;
using ChogGames.Api.Identity;
using ChogGames.Api.Matches;
using ChogGames.Api.Profile;
using ChogGames.Api.Realtime;
using ChogGames.Api.Rooms;
using ChogGames.Api.Wallet;
using ChogGames.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddGameCatalog(builder.Configuration);
builder.Services.AddGuestIdentity();
builder.Services.AddWalletChain(builder.Configuration);
builder.Services.AddWalletSignIn();
builder.Services.AddCharacters();
builder.Services.AddRooms(builder.Configuration);
builder.Services.AddRealtime();
builder.Services.AddMatchTurns();
builder.Services.AddChogLife();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// After authentication, so limits can be partitioned per player.
app.UseRateLimiter();

app.MapHealthEndpoints();
app.MapGameCatalogEndpoints();
app.MapIdentityEndpoints();
app.MapProfileEndpoints();
app.MapWalletEndpoints();
app.MapWalletSignInEndpoints();
app.MapCharacterEndpoints();
app.MapRoomEndpoints();
app.MapMatchCommandEndpoints();
app.MapRealtimeEndpoints();

app.Run();

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program;
