using Azure.Core;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Configure HttpClient that will call the downstream API using Managed Identity.
// We assume the caller is deployed with a User Assigned Managed Identity whose clientId is provided in config.
builder.Services.AddHttpClient("DownstreamApi", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(cfg["Downstream:BaseUrl"] ?? "https://localhost:5002/");
});

builder.Services.AddSingleton<TokenProvider>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.MapGet("/proxyweather", async (IHttpClientFactory httpClientFactory, TokenProvider tokenProvider, IConfiguration config) =>
{
    var client = httpClientFactory.CreateClient("DownstreamApi");
    var scope = config["Downstream:Scope"] ?? config["Downstream:ResourceId"] + "/.default"; // if using App ID URI format
    var token = await tokenProvider.GetAccessTokenAsync(scope);
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    var response = await client.GetAsync("weatherforecast");
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
})
.WithName("ProxyWeather");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

// Simple token provider that uses Managed Identity (user-assigned if ClientId specified) to get tokens for the downstream API.
class TokenProvider
{
    private readonly IConfiguration _config;
    private readonly TokenCredential _credential;
    public TokenProvider(IConfiguration config)
    {
        _config = config;
        var clientId = config["ManagedIdentity:ClientId"]; // user-assigned identity client id
        _credential = string.IsNullOrWhiteSpace(clientId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId });
    }

    public async Task<string> GetAccessTokenAsync(string scopeOrResource)
    {
        if (string.IsNullOrWhiteSpace(scopeOrResource))
            throw new ArgumentException("Scope/Resource must be provided", nameof(scopeOrResource));

        // If caller passes a resource without /.default we append it for AAD v2 endpoint.
        if (!scopeOrResource.Contains('/'))
        {
            // assume it's already a scope
        }
        else if (!scopeOrResource.EndsWith("/.default", StringComparison.OrdinalIgnoreCase))
        {
            scopeOrResource = scopeOrResource + "/.default";
        }
        var ctx = new TokenRequestContext(new[] { scopeOrResource });
        var token = await _credential.GetTokenAsync(ctx, CancellationToken.None);
        return token.Token;
    }
}
