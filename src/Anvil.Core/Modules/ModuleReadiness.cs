namespace Anvil.Core.Modules;

/// <summary>
/// Der Befund auf die Frage „ist dieses Modul global einsatzbereit?" (S3.4). Form von
/// <c>PrefixComponent</c> und <c>CatalogEntry</c> nachgebaut: ein selbstprüfender Record, kein
/// Datenbündel.
///
/// <para>
/// <b>Kein <c>bool</c> plus <c>string?</c></b> (Spec E-2): Zwei freie Felder ließen
/// <c>(true, "…")</c> und <c>(false, null)</c> baubar — ein einsatzbereites Modul mit einer
/// Begründung, die niemand liest, und einen Fehlbefund ohne Begründung, den die Grundzeile dann
/// leer zeigen müsste. Beides ist Unsinn, und beides fiele erst in der Oberfläche auf. Der
/// private Konstruktor und die zwei Fabriken machen die zwei sinnvollen Fälle zu den einzigen
/// baubaren.
/// </para>
///
/// <para>
/// <b><see cref="Reason"/> ist englisch und ein ganzer Satz</b>, keine Fehlerkennung: Er geht
/// unverändert vor den Nutzer, als Grundzeile unter der Marke „not ready" (Spec §3.4).
/// </para>
/// </summary>
public sealed record ModuleReadiness
{
    private ModuleReadiness(bool isReady, string? reason)
    {
        IsReady = isReady;
        Reason = reason;
    }

    /// <summary>
    /// Einsatzbereit — keine Begründung, denn es gibt nichts zu begründen. Eine geteilte
    /// Instanz: Der Befund trägt außer dem Ja keinen Zustand.
    /// </summary>
    public static ModuleReadiness Ready { get; } = new(true, null);

    /// <summary>
    /// Nicht einsatzbereit, mit <paramref name="reason"/> als Begründung. Ein leerer oder nur
    /// aus Leerzeichen bestehender Satz ist kein Befund, den die Oberfläche zeigen könnte, und
    /// wirft deshalb, statt eine leere Grundzeile zu erzeugen.
    /// </summary>
    public static ModuleReadiness NotReady(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "Module readiness reason must not be blank.", nameof(reason));
        }

        return new ModuleReadiness(false, reason);
    }

    /// <summary>Ob das Modul jetzt arbeiten kann.</summary>
    public bool IsReady { get; }

    /// <summary>
    /// Die Begründung, wenn <see cref="IsReady"/> <c>false</c> ist — sonst <c>null</c>.
    /// Englisch, ein ganzer Satz, geht unverändert vor den Nutzer (Spec §3.1).
    /// </summary>
    public string? Reason { get; }
}
