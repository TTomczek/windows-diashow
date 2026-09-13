using System.Globalization;

namespace diashow;

public enum AppLanguage
{
    English,
    German,
    Spanish
}

public static class Localization
{
    private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> Translations =
        new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
        {
            [AppLanguage.English] = new Dictionary<string, string>
            {
                ["Title"] = "Diashow",
                ["ChooseFolder"] = "Choose folder...",
                ["ChooseFolderMessage"] = "Choose a folder to start a slideshow.",
                ["NoMediaMessage"] = "No playable media was found in this folder.",
                ["DismissFileError"] = "Dismiss file error notification",
                ["PressToDismiss"] = "Press Enter, Space, or click to dismiss",
                ["VideoPosition"] = "Video position",
                ["Previous"] = "Previous",
                ["PreviousItem"] = "Previous item",
                ["Pause"] = "Pause",
                ["Resume"] = "Resume",
                ["Next"] = "Next",
                ["NextItem"] = "Next item",
                ["Unmute"] = "Unmute",
                ["Mute"] = "Mute",
                ["Reveal"] = "Reveal in explorer",
                ["RevealCurrent"] = "Reveal current file in Explorer",
                ["Fullscreen"] = "Fullscreen",
                ["ToggleFullscreen"] = "Toggle fullscreen",
                ["Settings"] = "Settings",
                ["ImagesOnly"] = "Images only - click for videos only",
                ["VideosOnly"] = "Videos only - click for images and videos",
                ["ImagesAndVideos"] = "Images and videos - click for images only",
                ["Image"] = "Image",
                ["Video"] = "Video",
                ["FileError"] = "File error: {0}",
                ["UnknownError"] = "Unknown error",
                ["ImageDuration"] = "Image duration (seconds)",
                ["ImageDurationAutomation"] = "Image duration in seconds",
                ["PlaybackOrder"] = "Playback order",
                ["FilenameOrder"] = "Filename order",
                ["RandomShuffle"] = "Random shuffle",
                ["Transition"] = "Transition",
                ["InstantSwitch"] = "Instant switch",
                ["SimpleFade"] = "Simple fade",
                ["Slide"] = "Slide left",
                ["KenBurns"] = "Ken Burns",
                ["Crossfade"] = "Crossfade",
                ["Zoom"] = "Zoom",
                ["Cover"] = "Cover",
                ["BlurDissolve"] = "Blur dissolve",
                ["FadeDuration"] = "Fade duration (seconds)",
                ["FadeDurationAutomation"] = "Fade duration in seconds",
                ["Preload"] = "Preload upcoming images",
                ["Language"] = "Language",
                ["KeyboardShortcuts"] = "Keyboard shortcuts",
                ["PauseOrResume"] = "Pause or resume",
                ["PreviousNext"] = "Previous / next item",
                ["Seek"] = "Seek 10 seconds",
                ["CycleMedia"] = "Cycle images, videos, or both",
                ["MuteVideo"] = "Mute or unmute video",
                ["CloseSettings"] = "Close settings or exit fullscreen",
                ["Close"] = "Close",
                ["ChooseFolderDescription"] = "Choose a folder for the slideshow",
                ["MediaName"] = "{0}: {1}"
            },
            [AppLanguage.German] = new Dictionary<string, string>
            {
                ["Title"] = "Diashow",
                ["ChooseFolder"] = "Ordner auswählen...",
                ["ChooseFolderMessage"] = "Wählen Sie einen Ordner, um eine Diashow zu starten.",
                ["NoMediaMessage"] = "In diesem Ordner wurden keine abspielbaren Medien gefunden.",
                ["DismissFileError"] = "Dateifehlermeldung schließen",
                ["PressToDismiss"] = "Zum Schließen Eingabetaste, Leertaste oder Klick verwenden",
                ["VideoPosition"] = "Videoposition",
                ["Previous"] = "Zurück",
                ["PreviousItem"] = "Vorheriges Element",
                ["Pause"] = "Pause",
                ["Resume"] = "Fortsetzen",
                ["Next"] = "Weiter",
                ["NextItem"] = "Nächstes Element",
                ["Unmute"] = "Ton einschalten",
                ["Mute"] = "Stummschalten",
                ["Reveal"] = "Im Explorer anzeigen",
                ["RevealCurrent"] = "Aktuelle Datei im Explorer anzeigen",
                ["Fullscreen"] = "Vollbild",
                ["ToggleFullscreen"] = "Vollbild umschalten",
                ["Settings"] = "Einstellungen",
                ["ImagesOnly"] = "Nur Bilder - für nur Videos klicken",
                ["VideosOnly"] = "Nur Videos - für Bilder und Videos klicken",
                ["ImagesAndVideos"] = "Bilder und Videos - für nur Bilder klicken",
                ["Image"] = "Bild",
                ["Video"] = "Video",
                ["FileError"] = "Dateifehler: {0}",
                ["UnknownError"] = "Unbekannter Fehler",
                ["ImageDuration"] = "Bilddauer (Sekunden)",
                ["ImageDurationAutomation"] = "Bilddauer in Sekunden",
                ["PlaybackOrder"] = "Wiedergabereihenfolge",
                ["FilenameOrder"] = "Nach Dateiname",
                ["RandomShuffle"] = "Zufällige Reihenfolge",
                ["Transition"] = "Übergang",
                ["InstantSwitch"] = "Sofortiger Wechsel",
                ["SimpleFade"] = "Sanfte Überblendung",
                ["Slide"] = "Nach links schieben",
                ["KenBurns"] = "Ken Burns",
                ["Crossfade"] = "Überblenden",
                ["Zoom"] = "Zoom",
                ["Cover"] = "Überdecken",
                ["BlurDissolve"] = "Unscharfe Überblendung",
                ["FadeDuration"] = "Überblenddauer (Sekunden)",
                ["FadeDurationAutomation"] = "Überblenddauer in Sekunden",
                ["Preload"] = "Nächste Bilder vorladen",
                ["Language"] = "Sprache",
                ["KeyboardShortcuts"] = "Tastenkürzel",
                ["PauseOrResume"] = "Pausieren oder fortsetzen",
                ["PreviousNext"] = "Vorheriges / nächstes Element",
                ["Seek"] = "10 Sekunden suchen",
                ["CycleMedia"] = "Bilder, Videos oder beides wechseln",
                ["MuteVideo"] = "Video stummschalten oder Ton einschalten",
                ["CloseSettings"] = "Einstellungen schließen oder Vollbild beenden",
                ["Close"] = "Schließen",
                ["ChooseFolderDescription"] = "Ordner für die Diashow auswählen",
                ["MediaName"] = "{0}: {1}"
            },
            [AppLanguage.Spanish] = new Dictionary<string, string>
            {
                ["Title"] = "Diashow",
                ["ChooseFolder"] = "Elegir carpeta...",
                ["ChooseFolderMessage"] = "Elige una carpeta para iniciar una presentación.",
                ["NoMediaMessage"] = "No se encontraron archivos reproducibles en esta carpeta.",
                ["DismissFileError"] = "Cerrar aviso de error de archivo",
                ["PressToDismiss"] = "Pulsa Intro, espacio o haz clic para cerrar",
                ["VideoPosition"] = "Posición del vídeo",
                ["Previous"] = "Anterior",
                ["PreviousItem"] = "Elemento anterior",
                ["Pause"] = "Pausa",
                ["Resume"] = "Continuar",
                ["Next"] = "Siguiente",
                ["NextItem"] = "Elemento siguiente",
                ["Unmute"] = "Activar sonido",
                ["Mute"] = "Silenciar",
                ["Reveal"] = "Mostrar en el explorador",
                ["RevealCurrent"] = "Mostrar el archivo actual en el Explorador",
                ["Fullscreen"] = "Pantalla completa",
                ["ToggleFullscreen"] = "Alternar pantalla completa",
                ["Settings"] = "Configuración",
                ["ImagesOnly"] = "Solo imágenes - pulsa para ver solo vídeos",
                ["VideosOnly"] = "Solo vídeos - pulsa para ver imágenes y vídeos",
                ["ImagesAndVideos"] = "Imágenes y vídeos - pulsa para ver solo imágenes",
                ["Image"] = "Imagen",
                ["Video"] = "Vídeo",
                ["FileError"] = "Error de archivo: {0}",
                ["UnknownError"] = "Error desconocido",
                ["ImageDuration"] = "Duración de imagen (segundos)",
                ["ImageDurationAutomation"] = "Duración de imagen en segundos",
                ["PlaybackOrder"] = "Orden de reproducción",
                ["FilenameOrder"] = "Orden por nombre",
                ["RandomShuffle"] = "Orden aleatorio",
                ["Transition"] = "Transición",
                ["InstantSwitch"] = "Cambio instantáneo",
                ["SimpleFade"] = "Fundido suave",
                ["Slide"] = "Deslizar a la izquierda",
                ["KenBurns"] = "Ken Burns",
                ["Crossfade"] = "Fundido cruzado",
                ["Zoom"] = "Zoom",
                ["Cover"] = "Cubrir",
                ["BlurDissolve"] = "Disolución borrosa",
                ["FadeDuration"] = "Duración del fundido (segundos)",
                ["FadeDurationAutomation"] = "Duración del fundido en segundos",
                ["Preload"] = "Precargar imágenes siguientes",
                ["Language"] = "Idioma",
                ["KeyboardShortcuts"] = "Atajos de teclado",
                ["PauseOrResume"] = "Pausar o continuar",
                ["PreviousNext"] = "Elemento anterior / siguiente",
                ["Seek"] = "Avanzar 10 segundos",
                ["CycleMedia"] = "Cambiar entre imágenes, vídeos o ambos",
                ["MuteVideo"] = "Silenciar o activar el sonido del vídeo",
                ["CloseSettings"] = "Cerrar configuración o salir de pantalla completa",
                ["Close"] = "Cerrar",
                ["ChooseFolderDescription"] = "Elige una carpeta para la presentación",
                ["MediaName"] = "{0}: {1}"
            }
        };

    public static AppLanguage Current { get; private set; }

    public static void SetLanguage(string? languageCode) =>
        Current = Parse(languageCode) ?? DetectFromOperatingSystem();

    public static string Get(string key, params object[] arguments)
    {
        var value = Translations[Current][key];
        return arguments.Length == 0 ? value : string.Format(CultureInfo.CurrentCulture, value, arguments);
    }

    public static string Code(AppLanguage language) => language switch
    {
        AppLanguage.German => "de",
        AppLanguage.Spanish => "es",
        _ => "en"
    };

    public static AppLanguage? Parse(string? languageCode) =>
        languageCode?.ToLowerInvariant() switch
        {
            "en" => AppLanguage.English,
            "de" => AppLanguage.German,
            "es" => AppLanguage.Spanish,
            _ => null
        };

    private static AppLanguage DetectFromOperatingSystem()
    {
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return language switch
        {
            "de" => AppLanguage.German,
            "es" => AppLanguage.Spanish,
            _ => AppLanguage.English
        };
    }
}
