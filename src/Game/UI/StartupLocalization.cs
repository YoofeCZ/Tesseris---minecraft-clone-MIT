namespace Tesseris.Game.UI;

/// <summary>Jazyk úvodní obrazovky. Herní obsah zůstává beze změny; překládá se onboarding.</summary>
public enum StartupLanguage
{
    Czech,
    English,
}

/// <summary>Veškerý text hlavní obrazovky a přípravy světa ve dvou plnohodnotných verzích.</summary>
public static class StartupLocalization
{
    public sealed record Copy(
        string SavedWorlds,
        string NoSavedWorld,
        string Play,
        string NewWorld,
        string Name,
        string Seed,
        string SeedHint,
        string WorldPreset,
        string AutomaticPregen,
        string FullViewFormat,
        string CreateWorld,
        string PregenHint,
        string EarlyAccessTitle,
        string[] EarlyAccessLines,
        string FirstStepsTitle,
        string[] FirstStepsLines,
        string ControlsTitle,
        string[] ControlsLines,
        string PreparingWorld,
        string FinalizingGeometry,
        string ChunksFormat,
        string PreparationFailed,
        string PreparationErrorHint,
        string PreparationHint);

    public static Copy For(StartupLanguage language) => language == StartupLanguage.English
        ? English
        : Czech;

    public static string ValidationMessage(StartupLanguage language, ValidationError error) =>
        (language, error) switch
        {
            (StartupLanguage.English, ValidationError.EmptyName) => "Enter a world name.",
            (StartupLanguage.English, ValidationError.NameTooLong) => "World name can have at most 64 characters.",
            (StartupLanguage.English, ValidationError.ReservedName) => "This world name cannot be used.",
            (StartupLanguage.English, ValidationError.SlashInName) => "World name cannot contain a slash.",
            (StartupLanguage.English, ValidationError.InvalidCharacter) => "World name contains an invalid character.",
            (_, ValidationError.EmptyName) => "Zadej nazev sveta.",
            (_, ValidationError.NameTooLong) => "Nazev sveta muze mit nejvyse 64 znaku.",
            (_, ValidationError.ReservedName) => "Tento nazev sveta nelze pouzit.",
            (_, ValidationError.SlashInName) => "Nazev sveta nesmi obsahovat lomitko.",
            (_, ValidationError.InvalidCharacter) => "Nazev sveta obsahuje nepovoleny znak.",
            _ => string.Empty,
        };

    private static readonly Copy Czech = new(
        "ULOZENE SVETY", "ZATIM ZADNY ULOZENY SVET", "HRAT", "NOVY SVET", "NAZEV",
        "SEED", "PRAZDNY = NAHODNY", "TYP SVETA", "AUTOMATICKA PREDGENERACE", "PLNY DOHLED: {0} CHUNKU + OKRAJ",
        "VYTVORIT SVET", "PROBEHNE VZDY PRED VSTUPEM DO HRY",
        "VELMI RANY VYVOJ",
        [
            "TOTO JE HRATELNY PROTOTYP.",
            "MECHANIKY, BALANC I VIZUAL SE BUDOU MENIT.",
            "ULOZENE SVETY SI RADSI ZALOHUJ.",
        ],
        "JAK ZACIT",
        [
            "SBIREJ PAZOUREK A KLACKY NA ZEMI.",
            "E OTEVRE INVENTAR A RECEPTAR.",
            "VYROB PAZOURKOVY KRUMPAC A SEKERU.",
            "SEKEROU KACEJ DREVO, KRUMPACEM KAMEN.",
            "KAMEN PADA JAKO DLAZEBNI KOSTKA.",
            "Z DLAZBY VYROB KAMENNE NASTROJE.",
            "Z 8 KOSTEK VYROB KAMENNOU PEC.",
            "PEC S PALIVEM TAVI ZELEZO A DALSI RUDY.",
        ],
        "OVLADANI",
        [
            "W A S D  POHYB", "MYS  ROZHLIZENI", "SPACE  SKOK / LET NAHORU",
            "LCTRL  LET DOLU (PRI F / G)", "LSHIFT  SPRINT", "C  PLIZENI",
            "LMB / RMB  TEZIT / POLOZIT", "KOLECKO, 1-9  HOTBAR", "E  INVENTAR A VYROBA",
            "G  KREATIV + LET", "F  PRUCHOD ZDEMI", "V  BLOKY / TESANI",
            "R / T  REZIM / VELIKOST TESANI", "+ / -  RYCHLOST CASU", "/  ZAMKNOUT SLUNCE",
            "*  RYCHLOST PRIRODY", "F3  DEBUG OVERLAY", "F4 nebo F1  DEV MENU",
            "F9  ZAZNAM BLIKANI", "ESC  ZAVRIT PANEL / UVOLNIT MYS",
            "MENU: TAB POLE, ENTER VYTVORIT, BACKSPACE SMAZAT",
            "SIPKY  VYBRAT ULOZENY SVET",
        ],
        "PRIPRAVUJI SVET", "DOKONCUJI GEOMETRII...", "CHUNKY {0} / {1}",
        "PRIPRAVA SELHALA", "CHYBA JE V LOGU; SVET NEBYL SPUSTEN NEUPLNY",
        "DO HRY SE VSTOUPI AZ PO DOKONCENI CELEHO OKRUHU");

    private static readonly Copy English = new(
        "SAVED WORLDS", "NO SAVED WORLD YET", "PLAY", "NEW WORLD", "NAME",
        "SEED", "EMPTY = RANDOM", "WORLD PRESET", "AUTOMATIC PREGENERATION", "FULL VIEW: {0} CHUNKS + BORDER",
        "CREATE WORLD", "ALWAYS RUNS BEFORE ENTERING THE GAME",
        "VERY EARLY DEVELOPMENT",
        [
            "THIS IS A PLAYABLE PROTOTYPE.",
            "MECHANICS, BALANCE AND VISUALS WILL CHANGE.",
            "BACK UP SAVED WORLDS WHEN THEY MATTER.",
        ],
        "GETTING STARTED",
        [
            "PICK UP FLINT AND STICKS ON THE GROUND.",
            "E OPENS INVENTORY AND RECIPE BOOK.",
            "CRAFT A FLINT PICKAXE AND AXE.",
            "FELL TREES WITH AXE; MINE STONE WITH PICK.",
            "MINED STONE DROPS AS FRACTURED STONE.",
            "CRAFT STONE TOOLS DIRECTLY FROM FRACTURED STONE.",
            "CRAFT A STONE FURNACE FROM 8 FRACTURED STONE.",
            "USE FUEL TO SMELT IRON AND OTHER ORES.",
        ],
        "CONTROLS",
        [
            "W A S D  MOVE", "MOUSE  LOOK", "SPACE  JUMP / FLY UP",
            "LCTRL  FLY DOWN (WITH F / G)", "LSHIFT  SPRINT", "C  CROUCH",
            "LMB / RMB  MINE / PLACE", "WHEEL, 1-9  HOTBAR", "E  INVENTORY & CRAFTING",
            "G  CREATIVE + FLIGHT", "F  NOCLIP", "V  BLOCKS / CHISELING",
            "R / T  CHISEL MODE / SIZE", "+ / -  TIME SPEED", "/  FREEZE SUN",
            "*  NATURE SPEED", "F3  DEBUG OVERLAY", "F4 or F1  DEV MENU",
            "F9  FLICKER RECORDING", "ESC  CLOSE PANEL / RELEASE MOUSE",
            "MENU: TAB FIELD, ENTER CREATE, BACKSPACE ERASE",
            "ARROWS  SELECT SAVED WORLD",
        ],
        "PREPARING WORLD", "FINALIZING GEOMETRY...", "CHUNKS {0} / {1}",
        "PREPARATION FAILED", "SEE THE LOG; THE INCOMPLETE WORLD WAS NOT STARTED",
        "THE GAME OPENS AFTER THE WHOLE AREA IS READY");
}

/// <summary>Chyby jména se drží jako význam, aby se zobrazily v právě zvoleném jazyce.</summary>
public enum ValidationError
{
    None,
    EmptyName,
    NameTooLong,
    ReservedName,
    SlashInName,
    InvalidCharacter,
}
