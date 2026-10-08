using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal static class ShiftReportValidation
{
    internal static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.Strict
    };
    internal static readonly IReadOnlyDictionary<int, IReadOnlyList<ShiftReportQuestion>> Schemas =
        new ReadOnlyDictionary<int, IReadOnlyList<ShiftReportQuestion>>(new Dictionary<int, IReadOnlyList<ShiftReportQuestion>>
        {
            [1] = Array.AsReadOnly<ShiftReportQuestion>([
                new("forms_ready", "Formy przed zalaniem były czyste, prawidłowo złożone i pozamykane."),
                new("planned_pour", "Wszystkie zaplanowane formy zostały prawidłowo zalane."),
                new("settlement_check", "Po ok. 20 minutach sprawdzono osiadanie betonu i uzupełniono braki."),
                new("top_surface", "Górne powierzchnie wyrównano i zagładzono."),
                new("steel_ears", "Uszy stalowe są kompletne i ustawione na właściwej głębokości oraz w odpowiednim położeniu."),
                new("form_locks", "Zamki i połączenia form są domknięte; brak wycieków i rozparcia form."),
                new("form_edges", "Boki i krawędzie form oczyszczono ze świeżego betonu."),
                new("yard_clean", "Plac po zalewaniu uprzątnięto; beton i odpady usunięto."),
                new("tools_stored", "Narzędzia, wibratory i przedłużacze sprawne odłożono na miejsce; usterki zgłoszono.")
            ])
        });

    internal static ShiftReportFields Normalize(ShiftReportFields fields)
    {
        if (fields is null) throw CrmFault.Invalid("fields", "Formularz jest wymagany.");
        static string Text(string? value, int limit, string key)
        {
            if (value is null) throw CrmFault.Invalid(key, "Podaj tekst lub pozostaw puste pole.");
            value = value.Trim();
            if (value.Length > limit) throw CrmFault.Invalid(key, $"Maksymalnie {limit} znaków.");
            return value;
        }
        static ShiftReportEquipment Equipment(ShiftReportEquipment value, string key)
        {
            if (value is null) throw CrmFault.Invalid(key, "Podaj stan sprzętu.");
            var condition = value.Condition?.Trim();
            if (condition is not (null or "sprawne" or "niesprawne")) throw CrmFault.Invalid(key + ".condition", "Wybierz stan sprzętu.");
            return value with { Condition = condition, Reason = Text(value.Reason, 1000, key + ".reason"), Note = Text(value.Note, 1000, key + ".note") };
        }
        var quantities = new Dictionary<string, int?>
        {
            ["assembled"] = fields.Assembled, ["prepared"] = fields.Prepared, ["poured"] = fields.Poured,
            ["checked"] = fields.Checked, ["demoulded"] = fields.Demoulded, ["damaged"] = fields.Damaged
        };
        if (fields.People is < 1 or > 1000) throw CrmFault.Invalid("people", "Podaj liczbę osób od 1 do 1000.");
        foreach (var (key, value) in quantities)
            if (value is < 0 or > 1_000_000) throw CrmFault.Invalid(key, "Podaj całkowitą liczbę sztuk od 0 do 1000000.");
        if (fields.Checks is null || fields.Checks.Length != 9) throw CrmFault.Invalid("checks", "Formularz wymaga dziewięciu odpowiedzi.");
        var checks = fields.Checks.Select(value => value?.Trim()).ToArray();
        for (var i = 0; i < checks.Length; i++)
            if (checks[i] is not (null or "OK" or "NIE" or "N/D")) throw CrmFault.Invalid($"checks.{i}", "Wybierz OK, NIE lub N/D.");
        return fields with
        {
            Leader = Text(fields.Leader, 100, "leader"), Responsible = Text(fields.Responsible, 100, "responsible"),
            Reference = Text(fields.Reference, 200, "reference"), DamageReason = Text(fields.DamageReason, 2000, "damageReason"),
            Vibrators = Equipment(fields.Vibrators, "vibrators"), Extensions = Equipment(fields.Extensions, "extensions"), Checks = checks,
            CorrectedWork = Text(fields.CorrectedWork, 4000, "correctedWork"), RemainingWork = Text(fields.RemainingWork, 4000, "remainingWork"),
            WorkOwner = Text(fields.WorkOwner, 100, "workOwner"), Remarks = Text(fields.Remarks, 4000, "remarks"),
            ProductionPerson = Text(fields.ProductionPerson, 100, "productionPerson"), Controller = Text(fields.Controller, 100, "controller")
        };
    }

    internal static ShiftReportFields ReadFields(JsonElement fields, int schema)
    {
        if (!Schemas.ContainsKey(schema)) throw new CrmFault(409, "Nieobsługiwana wersja formularza. Zaktualizuj aplikację.");
        try { return Normalize(fields.Deserialize<ShiftReportFields>(StoredJson)!); }
        catch (Exception error) when (error is JsonException or CrmFault)
        { throw new CrmFault(409, "Zapisany formularz wymaga zgodnej wersji aplikacji."); }
    }

    internal static Dictionary<string, string> CompletionErrors(ShiftReportFields fields)
    {
        Dictionary<string, string> errors = [];
        const string required = "Uzupełnij pole przed zakończeniem raportu.";
        foreach (var (key, value) in new Dictionary<string, object?>
        {
            ["leader"] = fields.Leader, ["responsible"] = fields.Responsible, ["people"] = fields.People,
            ["assembled"] = fields.Assembled, ["prepared"] = fields.Prepared, ["poured"] = fields.Poured,
            ["checked"] = fields.Checked, ["demoulded"] = fields.Demoulded, ["damaged"] = fields.Damaged,
            ["productionPerson"] = fields.ProductionPerson, ["controller"] = fields.Controller
        }) if (value is null or "") errors[key] = required;
        if (fields.Damaged > 0 && fields.DamageReason.Length == 0) errors["damageReason"] = "Podaj powód uszkodzenia.";
        foreach (var (key, equipment) in new[] { ("vibrators", fields.Vibrators), ("extensions", fields.Extensions) })
        {
            if (equipment.Condition is null) errors[key + ".condition"] = "Wybierz stan sprzętu.";
            if (equipment.Condition == "niesprawne" && equipment.Reason.Length == 0) errors[key + ".reason"] = "Podaj powód usterki lub osobę odpowiedzialną.";
        }
        for (var i = 0; i < fields.Checks.Length; i++) if (fields.Checks[i] is null) errors[$"checks.{i}"] = "Wybierz OK, NIE lub N/D.";
        if (fields.Checks.Contains("NIE") && fields.CorrectedWork.Length == 0 && fields.RemainingWork.Length == 0)
            errors["correctedWork"] = "Wyjaśnij wynik NIE: co poprawiono lub co zostało do wykonania.";
        if (fields.RemainingWork.Length > 0 && fields.WorkOwner.Length == 0) errors["workOwner"] = "Wskaż osobę odpowiedzialną za pozostałe prace.";
        return errors;
    }
    internal static bool Warning(ShiftReportFields fields) => fields.Checks.Contains("NIE") || fields.Vibrators.Condition == "niesprawne" || fields.Extensions.Condition == "niesprawne" || fields.RemainingWork.Length > 0;
    internal static void RequireComplete(ShiftReportFields fields)
    {
        var errors = CompletionErrors(fields);
        if (errors.Count > 0) throw new CrmFault(422, "Uzupełnij zaznaczone pola.", errors.ToDictionary(x => x.Key, x => new[] { x.Value }));
    }
    internal static string Reason(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 2000) throw CrmFault.Invalid("reason", "Podaj powód (do 2000 znaków).");
        return value;
    }
}
