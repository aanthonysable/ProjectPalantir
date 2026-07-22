using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Palantir.Api;
using Palantir.Api.Auth;
using Palantir.Api.Hubs;
using Palantir.Application.Auth;
using Palantir.Application.DependencyInjection;
using Palantir.Infrastructure.DependencyInjection;
using Palantir.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Knowledge uploads can be multi-GB PLC programs / archives.
const long maxUploadBytes = 4L * 1024 * 1024 * 1024;
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
    options.MemoryBufferThreshold = 1024 * 1024; // spill to disk after 1 MB
});
builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Project Palantir API", Version = "v0.1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Pilot JWT from POST /auth/login",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
});
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddPolicy("WebDev", policy =>
        policy.WithOrigins(
                "http://localhost:5173",
                "http://localhost:4173",
                "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});

builder.Services.AddPalantirApplication();
builder.Services.AddPalantirInfrastructure(builder.Configuration);
builder.Services.Configure<HostOptions>(options =>
{
    // Keep Kestrel up if a background worker throws unexpectedly.
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

var pilotJwt = builder.Configuration.GetSection(PilotJwtOptions.SectionName).Get<PilotJwtOptions>()
    ?? new PilotJwtOptions();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = pilotJwt.Issuer,
            ValidateAudience = true,
            ValidAudience = pilotJwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(pilotJwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });
builder.Services.AddAuthorization();

builder.Services.Configure<EntraExternalIdOptions>(
    builder.Configuration.GetSection(EntraExternalIdOptions.SectionName));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
builder.Services.AddScoped<IPilotAuthService, PilotAuthService>();
builder.Services.AddScoped<IEntraExternalIdAuthService, EntraExternalIdAuthService>();
builder.Services.AddDataProtection();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PalantirDbContext>();
    await db.Database.MigrateAsync();
    await DevDataSeeder.SeedAsync(db, scope.ServiceProvider);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("WebDev");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<NotificationsHub>("/hubs/notifications");
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "palantir-api",
    utc = DateTimeOffset.UtcNow
}));

app.Run();

public partial class Program;
