using Microsoft.EntityFrameworkCore;
using Npgsql;
using RemotePlay.Models.Context;
using RemotePlay.Utils;
using RemotePlay.Models.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using RemotePlay.Hubs;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// 配置 CORS
var allowedOrigins = builder.Configuration.GetSection("CORS:AllowedOrigins").Get<string[]>();
var allowAllOrigins = builder.Configuration.GetValue<bool>("CORS:AllowAllOrigins", false);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowWebClient", policy =>
    {
        if (allowAllOrigins || builder.Environment.IsDevelopment())
        {
            // 开发环境或配置允许时，允许所有来源
            // 注意：AllowAnyOrigin() 不能与 AllowCredentials() 同时使用
            policy.SetIsOriginAllowed(_ => true)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else if (allowedOrigins != null && allowedOrigins.Length > 0)
        {
            // 从配置文件读取允许的来源
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else
        {
            // 默认允许常见的本地开发端口
            policy.WithOrigins(
                    "http://localhost:5173",
                    "http://localhost:3000",
                    "http://127.0.0.1:5173",
                    "http://localhost:5174",
                    "http://localhost:5175")
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
    });
});

#region Add Authentication & Authorization
// 配置JWT认证
var jwtSecret = builder.Configuration["JWT:Secret"] ?? "YourSuperSecretKeyForJWTTokenGenerationMustBeAtLeast32Characters!";
var jwtIssuer = builder.Configuration["JWT:Issuer"] ?? "RemotePlay";
var jwtAudience = builder.Configuration["JWT:Audience"] ?? "RemotePlayClient";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
    
    // SignalR WebSocket连接需要从query string读取token
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var path = context.HttpContext.Request.Path;
            
            // 如果是SignalR Hub路径，尝试从query string读取token
            if (path.StartsWithSegments("/hubs"))
            {
                // SignalR WebSocket连接时，token通过query string传递
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken))
                {
                    context.Token = accessToken;
                    return Task.CompletedTask;
                }
                
                // 如果query string中没有token，尝试从Authorization header读取
                // (用于Long Polling等传输方式)
                var authHeader = context.Request.Headers["Authorization"].ToString();
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    context.Token = authHeader.Substring("Bearer ".Length).Trim();
                }
            }
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();
#endregion

// 添加SignalR服务（用于低延迟控制器输入）
builder.Services.AddSignalR(options =>
{
    // 配置SignalR选项以优化延迟
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
    options.StreamBufferCapacity = 10; // 流缓冲区容量
})
.AddJsonProtocol(options =>
{
    // 配置 JSON 序列化选项，使用 camelCase 命名策略（匹配前端）
    options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.PayloadSerializerOptions.WriteIndented = false;
});

// 提高日志级别，便于握手/串流排查
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddConsole();

#region Add Database
var dbConnectionString = builder.Configuration.GetConnectionString("DB");

if (string.IsNullOrWhiteSpace(dbConnectionString))
{
    string? GetDatabaseSetting(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = builder.Configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    var dbHost = GetDatabaseSetting("Database:Host", "DB_HOST");
    var dbName = GetDatabaseSetting("Database:Name", "DB_NAME");
    var dbUser = GetDatabaseSetting("Database:User", "DB_USER", "Database:Username", "DB_USERNAME");
    var dbPassword = GetDatabaseSetting("Database:Password", "DB_PASSWORD");
    var dbPortRaw = GetDatabaseSetting("Database:Port", "DB_PORT");

    if (string.IsNullOrWhiteSpace(dbHost))
    {
        throw new InvalidOperationException("Database host 配置缺失，请设置 `Database:Host` 或环境变量 `DB_HOST`。");
    }

    if (string.IsNullOrWhiteSpace(dbName))
    {
        throw new InvalidOperationException("Database 名称配置缺失，请设置 `Database:Name` 或环境变量 `DB_NAME`。");
    }

    if (string.IsNullOrWhiteSpace(dbUser))
    {
        throw new InvalidOperationException("Database 用户配置缺失，请设置 `Database:User` 或环境变量 `DB_USER`。");
    }

    var dbPort = 5432;
    if (!string.IsNullOrWhiteSpace(dbPortRaw) && int.TryParse(dbPortRaw, out var parsedPort))
    {
        dbPort = parsedPort;
    }

    var connectionStringBuilder = new NpgsqlConnectionStringBuilder
    {
        Host = dbHost,
        Port = dbPort,
        Database = dbName,
        Username = dbUser,
        Password = dbPassword ?? string.Empty
    };

    dbConnectionString = connectionStringBuilder.ConnectionString;
}

var dataSourceBuilder = new NpgsqlDataSourceBuilder(dbConnectionString);
dataSourceBuilder.EnableDynamicJson();
dataSourceBuilder.UseJsonNet(settings: new()
{
    NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore
});
var _pgdataSource = dataSourceBuilder.Build();
builder.Services
    .AddDbContext<RPContext>(
    options => options.UseNpgsql(_pgdataSource));

#endregion

#region Add Custom Services
// 配置RemotePlay设置
builder.Services.Configure<RemotePlayConfig>(
    builder.Configuration.GetSection("RemotePlay"));

// 配置 WebRTC 相关参数（如端口范围、公网 IP 等）
builder.Services.Configure<WebRTCConfig>(
    builder.Configuration.GetSection("WebRTC"));

// 配置设备状态更新服务
builder.Services.Configure<RemotePlay.Services.Device.DeviceStatusUpdateConfig>(
    builder.Configuration.GetSection("RemotePlay:DeviceStatusUpdate"));

// 注册HttpClient
builder.Services.AddHttpClient();

// 注册核心服务
builder.Services.AddSingleton<RemotePlay.Contracts.Services.IDeviceDiscoveryService, RemotePlay.Services.Device.DeviceDiscoveryService>();
builder.Services.AddScoped<RemotePlay.Contracts.Services.IRegisterService, RemotePlay.Services.Session.RegisterService>();
builder.Services.AddScoped<RemotePlay.Services.RemotePlay.IRemotePlayService, RemotePlay.Services.RemotePlay.RemotePlayService>();
// 会话配置与服务
builder.Services.Configure<RemotePlay.Models.PlayStation.SessionConfig>(o => { });
builder.Services.AddSingleton<RemotePlay.Contracts.Services.ISessionService, RemotePlay.Services.Session.SessionService>();
builder.Services.AddSingleton<RemotePlay.Contracts.Services.IStreamingService, RemotePlay.Services.Streaming.StreamingService>();
builder.Services.AddSingleton<RemotePlay.Contracts.Services.IControllerService, RemotePlay.Services.Controller.ControllerService>();
builder.Services.AddScoped<RemotePlay.Contracts.Services.IDeviceSettingsService, RemotePlay.Services.Device.DeviceSettingsService>();

// 注册Profile相关服务
builder.Services.AddScoped<RemotePlay.Contracts.Services.IOAuthService, RemotePlay.Services.Auth.OAuthService>();
builder.Services.AddScoped<RemotePlay.Contracts.Services.IProfileService, RemotePlay.Services.Profile.ProfileService>();

// 注册认证服务
builder.Services.AddScoped<RemotePlay.Contracts.Services.IAuthService, RemotePlay.Services.Auth.AuthService>();

// 注册WebRTC服务
builder.Services.AddSingleton<RemotePlay.Services.WebRTC.WebRTCSignalingService>();

// 注册延时统计服务
builder.Services.AddSingleton<RemotePlay.Services.Statistics.LatencyStatisticsService>();

// 注册 HLS 清理后台服务
builder.Services.AddHostedService<RemotePlay.Services.Hls.HlsCleanupService>();

// 注册设备状态更新后台服务
builder.Services.AddHostedService<RemotePlay.Services.Device.DeviceStatusUpdateService>();
#endregion

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "v1");
    });
}

//app.UseHttpsRedirection();

// 配置静态文件选项
var staticFileOptions = new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? "";
        
        // 设置 HLS 文件的 MIME 类型和缓存头
        if (path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.ContentType = "video/mp2t";
            ctx.Context.Response.Headers.CacheControl = "public, max-age=3600, must-revalidate";
            ctx.Context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        }
        // .m3u8 文件由 HlsPathFixMiddleware 处理
    }
};

// 添加路由中间件
app.UseRouting();

// 使用 CORS 中间件（必须在 UseRouting 之后，UseAuthentication 之前）
app.UseCors("AllowWebClient");

// HLS 路径修复中间件（处理 .m3u8 文件的路径转换）
app.UseMiddleware<RemotePlay.Middleware.HlsPathFixMiddleware>();

// 静态文件中间件（处理所有静态文件，包括 .ts 文件）
app.UseStaticFiles(staticFileOptions);

app.UseErrorHandling();

// 认证和授权中间件
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// 映射SignalR Hub（用于低延迟控制器输入）
app.MapHub<ControllerHub>("/hubs/controller");

// 映射SignalR Hub（用于设备状态更新）
app.MapHub<DeviceStatusHub>("/hubs/device-status");

// 映射SignalR Hub（用于流媒体控制）
app.MapHub<StreamingHub>("/hubs/streaming");

app.Run();
