using System.Drawing;
using Pastel;

namespace cambiador.Extensions {
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

  public static class Colors {
    public static string AsMagenta(this string text) => text.Pastel(Color.FromArgb(201, 149, 223));
    public static string AsBlue(this string text) => text.Pastel(Color.FromArgb(129, 189, 237));
    public static string AsCyan(this string text) => text.Pastel(Color.FromArgb(123, 192, 203));
    public static string AsYellow(this string text) => text.Pastel(Color.FromArgb(213, 172, 128));
    public static string AsBlack(this string text) => text.Pastel(Color.Black);
    public static string AsRed(this string text) => text.Pastel(Color.FromArgb(220, 136, 138));
    public static string AsRedBg(this string text) => text.PastelBg(Color.FromArgb(220, 136, 138));
    public static string AsWhiteBg(this string text) => text.PastelBg(Color.FromArgb(186, 191, 201));
  }
}
