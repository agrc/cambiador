var builder = WebApplication.CreateBuilder(args);

builder.AddMountedSecrets(["db", "email"]);
builder.Host.AddSerilog("cambiador", builder.Configuration["key"]);

var app = builder.Build();
app.UseSerilogRequestLogging();

app.MapGet("/", () => "ready");
app.MapPost("/scheduled", async () => await ChangeDetection.DetectChanges(builder.Configuration["connection"].Trim()));

app.Run();
