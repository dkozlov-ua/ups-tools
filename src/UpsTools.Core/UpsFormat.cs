using System.Globalization;

namespace UpsTools.Core;

/// <summary>Short display formats shared by the dashboard and notifications.</summary>
public static class UpsFormat
{
    public static string Watts(double? w) => w is { } v ? $"{v.ToString("0", CultureInfo.CurrentCulture)} W" : "—";

    public static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours} h {t.Minutes:00} m"
        : $"{Math.Max(0, (int)t.TotalMinutes)} m";
}
