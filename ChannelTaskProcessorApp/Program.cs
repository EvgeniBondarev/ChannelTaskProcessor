using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();


// Register our task processor as a hosted service
builder.Services.AddSingleton<ITaskProcessor, TaskProcessor>();
builder.Services.AddHostedService(provider => (TaskProcessor)provider.GetRequiredService<ITaskProcessor>());

var app = builder.Build();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();