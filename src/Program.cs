var builder = WebApplication.CreateBuilder(args);

builder.AddMountedSecrets(new[] { "db", "email" });
builder.Host.AddSerilog("cambiador", builder.Configuration["key"]);

var app = builder.Build();
app.UseSerilogRequestLogging();

app.MapGet("/", () => "ready");
app.MapGet("/scheduled", async () => await ChangeDetection.DetectChanges(builder.Configuration["connection"].Trim()));

app.Run();
