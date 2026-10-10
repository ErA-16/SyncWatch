using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using SyncWatch.Data;
using SyncWatch.Hubs;
using SyncWatch.Services;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<DatabaseContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// [DisableRequestSizeLimit] lifts Kestrel's body cap but leaves the form reader on
// its own 128 MB default, which rejects large multipart posts with a 413 instead.
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = long.MaxValue;
    options.ValueLengthLimit = int.MaxValue;
    options.MemoryBufferThreshold = 64 * 1024;
});

builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();

    var s3Config = new AmazonS3Config
    {
        ServiceURL = config["R2:Endpoint"],
        ForcePathStyle = true,
        AuthenticationRegion = "auto",
        Timeout = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxErrorRetry = 3
    };

    var credentials = new BasicAWSCredentials(config["R2:AccessKey"], config["R2:SecretKey"]);

    return new AmazonS3Client(credentials, s3Config);
});

builder.Services.AddHostedService<RoomExpirationService>();
builder.Services.AddHostedService<PlaybackSyncService>();


builder.Services.AddScoped<IMovieService, MovieService>();
builder.Services.AddScoped<IRoomService, RoomService>();
builder.Services.AddScoped<IPlaybackService, PlaybackService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddSingleton<PresenceTracker>();

builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });



var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<PlaybackHub>("/hubs/playback");


// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();