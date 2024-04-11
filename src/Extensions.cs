using SendGrid;
using Serilog.Sinks.Email;

namespace cambiador;
public static class Configuration {
  public static void AddMountedSecrets(this WebApplicationBuilder hostBuilder, IEnumerable<string> folderNames) {
    var path = Path.DirectorySeparatorChar.ToString();

    if (hostBuilder.Environment.IsDevelopment()) {
      path = Directory.GetCurrentDirectory();
    }

    foreach (var folder in folderNames) {
      hostBuilder.Configuration.AddKeyPerFile(Path.Combine(path, "secrets", folder), false, true);
    }
  }
}
public static class Logging {
  public static void AddSerilog(this ConfigureHostBuilder hostBuilder, string appName, string apiKey) {
    var client = new SendGridClient(apiKey?.Trim() ?? string.Empty);

    var email = new EmailConnectionInfo {
      SendGridClient = client,
      FromEmail = "noreply@utah.gov",
      ToEmail = "sgourley@utah.gov",
      EmailSubject = "cambiador problems"
    };

    hostBuilder.UseSerilog((ctx, lc) => lc
          .WriteTo.Email(email, restrictedToMinimumLevel: LogEventLevel.Error)
          .Enrich.FromLogContext()
          .Enrich.WithProperty("Application", appName)
          .ReadFrom.Configuration(ctx.Configuration));
  }
}
public static class Time {
  public static string FriendlyFormat(this long milliseconds) {
    const double minute = 60;
    const double hour = 60D * minute;
    var seconds = milliseconds / 1000D;

    if (seconds < 10) {
      return $"{milliseconds} ms";
    }

    if (seconds < 90) {
      return $"{seconds:F3} seconds";
    }

    if (seconds < 90 * minute) {
      return $"{seconds / minute:F3} minutes";
    }

    return $"{seconds / hour:F3} hours";
  }
}
