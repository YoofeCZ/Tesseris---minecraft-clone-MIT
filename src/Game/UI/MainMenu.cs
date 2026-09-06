using System.Globalization;
using System.Text;

using Tesseris.Game.World;

namespace Tesseris.Game.UI;

/// <summary>Pole, do kterého se v úvodním menu právě píše.</summary>
public enum MainMenuField
{
    Name,
    Seed,
}

/// <summary>Ověřená data potřebná k založení světa.</summary>
public readonly record struct WorldCreationRequest(string Name, int Seed);

/// <summary>
/// Čistý stav úvodního menu. Nezná OpenTK ani vykreslování, takže stejné chování používá
/// klávesnice, myš i testy.
/// </summary>
public sealed class MainMenu
{
    public const int MaximumTextElements = 64;

    /// <summary>
    /// Celý běžný dohled hry. Příprava není hráčské nastavení: proběhne vždy před vstupem,
    /// aby se generování nepralo o výkon s fyzikou a hraním.
    /// </summary>
    public const int AutomaticPregenRadius = ChunkStreamer.DefaultViewDistanceChunks;

    private bool _replaceDefaultNameOnInput;

    public MainMenu(string initialName = "novy-svet", string initialSeed = "")
    {
        NameText = initialName ?? string.Empty;
        SeedText = initialSeed ?? string.Empty;
        _replaceDefaultNameOnInput = string.Equals(NameText, "novy-svet", StringComparison.Ordinal);
    }

    public string NameText { get; private set; }

    public string SeedText { get; private set; }

    public MainMenuField FocusedField { get; private set; } = MainMenuField.Name;

    /// <summary>Jazyk titulní obrazovky a jejích validačních chyb.</summary>
    public StartupLanguage Language { get; private set; } = StartupLanguage.Czech;

    public int PregenRadius => AutomaticPregenRadius;

    /// <summary>Přidá tisknutelný text do aktivního pole.</summary>
    public void AppendText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string printable = string.Concat(text.Where(character => !char.IsControl(character)));
        if (printable.Length == 0)
        {
            return;
        }

        if (FocusedField == MainMenuField.Name)
        {
            NameText = _replaceDefaultNameOnInput
                ? AppendWithinLimit(string.Empty, printable)
                : AppendWithinLimit(NameText, printable);
            _replaceDefaultNameOnInput = false;
        }
        else
        {
            SeedText = AppendWithinLimit(SeedText, printable);
        }
    }

    /// <summary>Smaže poslední celý znak, ne pouze půlku UTF-16 dvojice.</summary>
    public void Backspace()
    {
        if (FocusedField == MainMenuField.Name)
        {
            _replaceDefaultNameOnInput = false;
            NameText = RemoveLastTextElement(NameText);
        }
        else
        {
            SeedText = RemoveLastTextElement(SeedText);
        }
    }

    public void Focus(MainMenuField field) => FocusedField = field;

    public void SetLanguage(StartupLanguage language) => Language = language;

    public void FocusNext() => FocusedField = FocusedField == MainMenuField.Name
        ? MainMenuField.Seed
        : MainMenuField.Name;

    /// <summary>
    /// Připraví požadavek pro katalog. Náhodný seed se dodává zvenku, aby model zůstal
    /// deterministický a dal se snadno otestovat.
    /// </summary>
    public bool TryCreateRequest(
        int randomSeed,
        out WorldCreationRequest request,
        out string error)
    {
        error = ValidateName(NameText, Language);
        if (error.Length != 0)
        {
            request = default;
            return false;
        }

        request = new WorldCreationRequest(
            NameText.Trim(),
            ResolveSeed(SeedText, randomSeed));
        return true;
    }

    /// <summary>Vrátí prázdný řetězec, když je název použitelný.</summary>
    public static string ValidateName(string name, StartupLanguage language = StartupLanguage.Czech)
    {
        ValidationError validation = ValidateNameKind(name);
        return StartupLocalization.ValidationMessage(language, validation);
    }

    public static ValidationError ValidateNameKind(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return ValidationError.EmptyName; }

        string trimmed = name.Trim();
        if (StringInfo.ParseCombiningCharacters(trimmed).Length > MaximumTextElements)
        { return ValidationError.NameTooLong; }

        if (trimmed is "." or "..")
        { return ValidationError.ReservedName; }

        if (trimmed.Contains('/') || trimmed.Contains('\\'))
        { return ValidationError.SlashInName; }

        if (trimmed.Any(char.IsControl))
        { return ValidationError.InvalidCharacter; }

        return ValidationError.None;
    }

    /// <summary>
    /// Číslo se použije přímo, prázdné pole dostane dodanou náhodnou hodnotu a text se
    /// převede stabilním FNV-1a nad UTF-8. Na rozdíl od GetHashCode se výsledek mezi běhy nemění.
    /// </summary>
    public static int ResolveSeed(string text, int randomSeed)
    {
        ArgumentNullException.ThrowIfNull(text);

        string normalized = text.Trim();
        if (normalized.Length == 0)
        {
            return randomSeed;
        }

        if (int.TryParse(
            normalized,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int numericSeed))
        {
            return numericSeed;
        }

        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;
        uint hash = OffsetBasis;

        foreach (byte value in Encoding.UTF8.GetBytes(normalized))
        {
            hash ^= value;
            hash *= Prime;
        }

        return unchecked((int)hash);
    }

    private static string RemoveLastTextElement(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        int[] starts = StringInfo.ParseCombiningCharacters(text);
        return text[..starts[^1]];
    }

    private static string AppendWithinLimit(string current, string addition)
    {
        string combined = current + addition;
        int[] starts = StringInfo.ParseCombiningCharacters(combined);
        return starts.Length <= MaximumTextElements
            ? combined
            : combined[..starts[MaximumTextElements]];
    }
}
