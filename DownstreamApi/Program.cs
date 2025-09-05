var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Configure authentication to validate tokens issued for this API.
// In Azure, you'll deploy this API as an App Service with 'Entra ID authentication' enabled.
// For local dev we allow disabling validation via config if needed.
var authority = builder.Configuration["AzureAd:Authority"]; // e.g. https://login.microsoftonline.com/<tenantId>/v2.0
var audience = builder.Configuration["AzureAd:Audience"];   // e.g. api://<client-id> or the App ID URI

if (!string.IsNullOrWhiteSpace(authority))
{
    builder.Services.AddAuthentication("Bearer")
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.TokenValidationParameters.ValidAudiences = new[] { audience, builder.Configuration["AzureAd:ClientId"] };
        });
    builder.Services.AddAuthorization();
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

if (!string.IsNullOrWhiteSpace(authority))
{
    app.UseAuthentication();
    app.UseAuthorization();
}

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

var weatherEndpoint = app.MapGet("/weatherforecast", () =>
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

if (!string.IsNullOrWhiteSpace(authority))
{
    weatherEndpoint.RequireAuthorization();
}

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
