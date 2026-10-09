using System.Text.Json.Serialization;
using ChogGames.Api.Catalog;
using ChogGames.Api.Health;
using ChogGames.Api.Identity;
using ChogGames.Api.Profile;
using ChogGames.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddGameCatalog(builder.Configuration);
builder.Services.AddGuestIdentity();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapGameCatalogEndpoints();
app.MapIdentityEndpoints();
app.MapProfileEndpoints();

app.Run();

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program;
