
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RemotePlay.Models.Context;
var builder = WebApplication.CreateBuilder(args);
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
    options => options.UseNpgsql(_pgdataSource, b => b.MigrationsAssembly("RemotePlay.DBTool")));

var app = builder.Build();
AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);
using (var serviceScope = app.Services.GetService<IServiceScopeFactory>()?.CreateScope())
{
    if (serviceScope != null)
    {
        var db = serviceScope.ServiceProvider.GetRequiredService<RPContext>();
        if (db.Database.GetPendingMigrations().Any())
            db.Database.Migrate();

    }
    Environment.Exit(0);
}
