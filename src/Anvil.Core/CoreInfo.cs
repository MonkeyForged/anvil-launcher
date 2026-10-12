using System.Reflection;

namespace Anvil.Core;

/// <summary>Zentrale Produkt-Konstanten des Cores.</summary>
public static class CoreInfo
{
    public const string ProductName = "Anvil";

    /// <summary>
    /// Die Version aus <c>Directory.Build.props</c>, zur Laufzeit aus der Assembly gelesen —
    /// damit die Fußzeile keine zweite Wahrheit führt. Ein etwaiger Build-Anhang nach '+'
    /// fällt weg; er gehört nicht in die Oberfläche.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(CoreInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return "0.0.0";
        }

        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
