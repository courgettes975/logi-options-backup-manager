# Logi Options+ Backup Manager

Sauvegarde les réglages de Logi Options+ et de la MX Creative Console, puis les restaure sur un
autre PC Windows 10/11. Application WPF .NET 8, portable (un seul `.exe`, sans installation).

Développé et testé sur Logi Options+ 2.9.1013853, Plugin Service 6.4.2.3414, `schema_version` 26.
Les emplacements et formats ont été détectés sur une installation réelle, jamais supposés.

## Utilisation

Exécutable : `publish\LogiOptionsBackupManager.exe`. Windows demande les droits administrateur au
lancement, parce que la restauration doit arrêter le service `OptionsPlusUpdaterService` (LocalSystem).

1. **Analyse** : se lance à l'ouverture. Elle affiche les versions, les périphériques, les profils
   Creative Console, les processus et services, et chaque clé de `settings.db` avec sa catégorie.
   Elle ne fait que lire.
2. **Sauvegarder** : crée un fichier `.logibak`. Logi Options+ peut rester ouvert : `settings.db`
   est lu par SQLite, qui donne une image cohérente même pendant une écriture.
3. **Restaurer** :
   1. Sur le nouveau PC, installe Logi Options+, lance-le une fois et appaire tes périphériques.
   2. Ouvre le `.logibak` et lis l'analyse (blocages et avertissements).
   3. Clique sur Restaurer. L'outil :
      - fait une copie de sécurité dans `%LOCALAPPDATA%\LogiOptionsBackupManager\pre-restore\` ;
      - ferme Options+ ;
      - fusionne les réglages ;
      - relance Options+.
   4. Reconnecte-toi ensuite au compte Logitech dans Options+.

Journaux : `%LOCALAPPDATA%\LogiOptionsBackupManager\logs\`.

## Ce qui est restauré

| Élément | Source | Mode |
|---|---|---|
| Profils et assignations (boutons, touches, Smart Actions), profils par application, thème, alertes batterie... | clés « Portable » de `settings.db` | fusion : seules ces clés sont remplacées |
| Préférences de l'interface Creative Console | `cc_config.json` (thème, langue, bibliothèque d'icônes...) | fusion, `hostId` conservé |
| Profils MX Creative Keypad / Dialpad | `LogiPluginService\Applications\` | copie avec écrasement, chemins utilisateur réécrits |
| Icônes et sons | `LogiPluginService\Media\` | copie |
| Plugins | `LogiPluginService\Plugins\` : dossiers installés, fichiers `.link` et compilations qu'ils désignent | plugins absents installés ; plugins déjà présents conservés, sauf si « Remplacer » est coché (dossier remplacé en entier) |
| `enable_lps_in_background` | `HKCU\Software\Logitech\SharedSettings` | écriture |

**Jamais restauré** :
- les jetons du compte Logitech (blobs DPAPI dans `HKCU\...\LogiOptionsPlus\Data`, illisibles sur un autre PC) ;
- les numéros de série et le firmware (`dfu/*`) ;
- Easy-Switch et les chemins HID ;
- la télémétrie, les caches et le profil Electron ;
- `ProgramData` ;

Les clés inconnues (par exemple ajoutées par une version future d'Options+) restent celles du PC cible.

## Garde-fous

- Format vérifié avant toute lecture : en-tête SQLite, colonnes de la table `data`, une seule ligne, JSON valide.
- Empreinte SHA-256 de chaque fichier de l'archive : une archive modifiée est refusée.
- Schéma de `settings.db` différent entre source et cible : restauration d'Options+ bloquée, sauf si tu coches « Forcer ».
- Restauration complète (document entier) seulement sur le même PC et le même compte (empreinte du SID). Le SID lui-même n'est pas stocké.
- Extraction protégée contre les chemins sortant du dossier cible (« zip slip »).
- Après écriture, `settings.db` est relu pour vérifier qu'il est valide.

## Format `.logibak` (zip)

```
manifest.json                       versions, périphériques (sans n° de série), empreintes SHA-256
optionsplus/settings.portable.json  clés portables, nettoyées
optionsplus/settings.full.json      document complet (retour arrière sur le même compte uniquement)
optionsplus/cc_config.json
creativeconsole/Applications/...    profils Creative Console
creativeconsole/Media/...
creativeconsole/plugins.json        plugins installés
registry/shared_settings.json
plugins/installed/<Nom>/...         dossiers de plugins installés
plugins/links/<Nom>.link            liens vers des plugins de développement
plugins/linked/<Nom>/...            compilations désignées par ces liens
```

Plugin de développement (`.link`) : si son dossier de compilation existe sur le PC cible, le lien est restauré tel quel. Sinon, la compilation sauvegardée est installée comme un plugin normal dans `Plugins\<name>` (le `name:` de `metadata\LoupedeckPackage.yaml`).

## Développement

```powershell
dotnet test
```

```powershell
dotnet publish src\LogiBackup.App -c Release -o publish
```

- `src/LogiBackup.Core` : détection, SQLite, classement des clés, fusion, sauvegarde, restauration, processus.
- `src/LogiBackup.App` : interface WPF (MVVM, CommunityToolkit.Mvvm).
- `tests/LogiBackup.Core.Tests` : 23 tests xUnit sur une fausse installation en dossier temporaire.
  L'arrêt des processus est remplacé par un faux : les tests ne ferment jamais le vrai Options+.

Les règles de classement des clés sont dans `Settings/SettingsClassifier.cs`. Après une mise à jour
d'Options+, lance l'analyse : une clé classée « Unknown » signale qu'il faut revoir ces règles.

## Limites connues

- Je n'ai pas encore vérifié quel dossier `Loupedeck70`, `71` ou `72` correspond au Keypad et lequel au Dialpad. L'interface affiche les noms bruts.
- Une restauration réelle (arrêt et relance d'Options+) n'a pas encore été faite sur ce PC. Elle a été testée sur une copie isolée de tes vraies données.
- Les assignations d'un modèle absent du PC cible ne servent qu'à sa première connexion.
