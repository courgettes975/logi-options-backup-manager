using System.Text.RegularExpressions;

namespace LogiBackup.Core.Settings;

public enum KeyCategory
{
    /// <summary>Restored on another PC.</summary>
    Portable,
    /// <summary>Tied to a serial number, a HID path or this PC's pairing.</summary>
    DeviceSpecific,
    /// <summary>Tied to the Logitech account.</summary>
    Account,
    Telemetry,
    Cache,
    /// <summary>Install or onboarding state that the target PC must keep for itself.</summary>
    LocalState,
    /// <summary>Not seen during the analysis of Options+ 2.9: never restored, only reported.</summary>
    Unknown,
}

public sealed record KeyClassification(string Key, KeyCategory Category, string Description);

/// <summary>
/// Classifies the top-level keys of the settings.db JSON. The rules come from the analysis
/// of Options+ 2.9.1013853 (schema_version 26). Unknown keys are kept on the target and never
/// overwritten, so a newer Options+ version cannot be damaged by a guess.
/// </summary>
public static class SettingsClassifier
{
    private static readonly (Regex pattern, KeyCategory category, string description)[] Rules =
    [
        (R(@"^profile-[0-9a-fA-F-]{36}$"), KeyCategory.Portable, "Profil d'assignations (boutons, touches, Smart Actions)"),
        (R(@"^profile_keys$"), KeyCategory.Portable, "Liste des profils"),
        (R(@"^applications$"), KeyCategory.Portable, "Applications ayant un profil spécifique"),
        (R(@"^theme$"), KeyCategory.Portable, "Thème"),
        (R(@"^use_system_theme$"), KeyCategory.Portable, "Thème du système"),
        (R(@"^battery_notify$"), KeyCategory.Portable, "Notifications de batterie"),
        (R(@"^battery/[^/]+/warning_notification$"), KeyCategory.Portable, "Alerte batterie par modèle"),
        (R(@"^disable_backlight_on_load_settings$"), KeyCategory.Portable, "Rétroéclairage au chargement"),
        (R(@"^mode_shift_reset_info$"), KeyCategory.Portable, "Mode shift"),
        (R(@"^integration_manager_settings$"), KeyCategory.Portable, "Intégrations (LED SDK)"),
        (R(@"^microphone_handler_optin$"), KeyCategory.Portable, "Gestion du micro"),
        (R(@"^macros_add_triggers_allowed$"), KeyCategory.Portable, "Déclencheurs de macros"),

        (R(@"^dfu/"), KeyCategory.DeviceSpecific, "Firmware, indexé par numéro de série"),
        (R(@"^easy_switch$"), KeyCategory.DeviceSpecific, "Hôtes Easy-Switch et chemins HID"),
        (R(@"^ever_connected_devices$"), KeyCategory.DeviceSpecific, "Historique des périphériques (numéros de série)"),
        (R(@"^slot_prefixes_ever_seen$"), KeyCategory.DeviceSpecific, "Modèles vus sur ce PC"),
        (R(@"^settings_backup_state_v2$"), KeyCategory.DeviceSpecific, "État de la sauvegarde cloud"),
        (R(@"^migration_report$"), KeyCategory.DeviceSpecific, "Rapport de migration"),

        (R(@"^accounts_"), KeyCategory.Account, "Compte Logitech"),
        (R(@"^offer_redemption_"), KeyCategory.Account, "Offres liées au compte"),

        (R(@"^analytics"), KeyCategory.Telemetry, "Télémétrie"),
        (R(@"^crash_reporting$"), KeyCategory.Telemetry, "Rapports de crash"),
        (R(@"^macro_usage"), KeyCategory.Telemetry, "Statistiques de macros"),
        (R(@"^(is_star_rating_pending|next_star_rating_check)$"), KeyCategory.Telemetry, "Demande de note"),
        (R(@"^release_notes_build_id$"), KeyCategory.Telemetry, "Notes de version vues"),

        (R(@"^iconsLocalPathCache$"), KeyCategory.Cache, "Cache des icônes d'applications"),

        (R(@"^(first_time_run|is-global-migrated|settings_transferred|mute_init_by_user|app_permissions_previous|brand|schema_version)$"),
            KeyCategory.LocalState, "État local de l'installation"),
        (R(@"^macros_onboarding_"), KeyCategory.LocalState, "Découverte des macros"),
    ];

    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static KeyClassification Classify(string key)
    {
        foreach (var (pattern, category, description) in Rules)
            if (pattern.IsMatch(key)) return new KeyClassification(key, category, description);
        return new KeyClassification(key, KeyCategory.Unknown, "Clé inconnue (non restaurée)");
    }

    public static bool IsProfileKey(string key) => Rules[0].pattern.IsMatch(key);
}
