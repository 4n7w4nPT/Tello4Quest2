using System.Collections.Generic;

namespace TelloQuest
{
    /// <summary>
    /// SOURCE UNIQUE du mapping de la manette en vol.
    ///
    /// Pourquoi ce fichier existe : jusqu'ici, ce que fait chaque bouton etait ecrit
    /// a un endroit (TelloGamepadController) et decrit a un autre (le README, et
    /// maintenant l'ecran de legende). Deux endroits a modifier pour un seul
    /// changement, sans rien qui force a le faire - donc une divergence garantie a
    /// terme. Le decouplage Takeoff/Land de la v0.5 en est l'exemple vivant : il a
    /// fallu penser a corriger le tableau du README a la main.
    ///
    /// L'ecran de legende (voir TelloControlsScreen) lit cette table. Quand une
    /// commande change dans TelloGamepadController, la ligne correspondante ici doit
    /// changer avec - et c'est desormais le seul autre endroit a toucher.
    ///
    /// C'est aussi le socle dont le remapping personnalise aura besoin : une fois que
    /// les actions sont nommees et listees quelque part, leur associer un bouton
    /// choisi par le pilote devient un probleme de donnees, pas de refonte.
    /// </summary>
    public static class TelloControlMap
    {
        public enum ControlKind
        {
            /// <summary>Bouton facial - le libelle depend de la marque (Croix/A...).</summary>
            FaceButton,
            /// <summary>Gachette ou tranche - libelle commun aux deux marques.</summary>
            Shoulder,
            /// <summary>Stick analogique.</summary>
            Stick,
            /// <summary>Croix directionnelle.</summary>
            Dpad,
            /// <summary>Bouton systeme (Share/Options...).</summary>
            System
        }

        public struct ControlEntry
        {
            /// <summary>Position au sens Unity ("south", "north"...) pour les boutons
            /// faciaux, afin que TelloUiKit.ButtonName puisse produire le bon libelle
            /// selon la marque detectee. Null pour les autres types.</summary>
            public string facePosition;

            /// <summary>Libelle affiche quand facePosition est null (L1, D-pad, etc.).</summary>
            public string fixedLabel;

            public ControlKind kind;
            public string action;
            public string detail;

            public ControlEntry(ControlKind kind, string facePosition, string fixedLabel, string action, string detail)
            {
                this.kind = kind;
                this.facePosition = facePosition;
                this.fixedLabel = fixedLabel;
                this.action = action;
                this.detail = detail;
            }
        }

        /// <summary>Le mapping en mode PILOTAGE, dans l'ordre d'affichage.</summary>
        /// <summary>The PILOTING mapping, in display order.
        ///
        /// Text is deliberately in English, like the rest of the UI - the app's
        /// screens, the README and the code comments elsewhere are all in English,
        /// and a legend that switches language mid-app reads like an oversight.</summary>
        public static readonly IReadOnlyList<ControlEntry> Piloting = new List<ControlEntry>
        {
            new ControlEntry(ControlKind.Stick, null, "Left stick", "Yaw / Altitude",
                "Left-right rotates in place, up-down climbs and descends."),
            new ControlEntry(ControlKind.Stick, null, "Right stick", "Roll / Pitch",
                "Sideways and forward-backward movement."),

            new ControlEntry(ControlKind.FaceButton, "south", null, "Take off",
                "Ignored if the drone is already flying."),
            new ControlEntry(ControlKind.FaceButton, "east", null, "Land",
                "Ignored if the drone is already on the ground."),
            new ControlEntry(ControlKind.FaceButton, "west", null, "Photo",
                "Saves the current frame as a PNG in Pictures/Tello4Quest2."),
            new ControlEntry(ControlKind.FaceButton, "north", null, "Video recording",
                "Starts / stops an .mp4 recording in Movies/Tello4Quest2."),

            new ControlEntry(ControlKind.Dpad, null, "D-pad", "Flips",
                "Up, down, left or right. One at a time - a new flip is ignored until the previous one is confirmed done."),

            new ControlEntry(ControlKind.Shoulder, null, "L1 / L2", "Speed level -/+",
                "Movement speed sent to the drone."),
            new ControlEntry(ControlKind.Shoulder, null, "R1 / R2", "Sensitivity -/+",
                "Stick dead zone and response curve."),

            new ControlEntry(ControlKind.System, null, "Share / Select", "EMERGENCY STOP",
                "Cuts the motors immediately. Live on every screen, not just Piloting."),
            new ControlEntry(ControlKind.System, null, "Options / Start", "Back to menu",
                "Blocked while the drone is flying - a haptic pulse says so.")
        };

        /// <summary>Label for an entry, taking the detected gamepad brand into account
        /// for face buttons. Used as the fallback when no icon font is available -
        /// TelloControlsScreen prefers the glyph when it can (see ResolveButtonText on
        /// TelloInitGate).</summary>
        public static string ResolveLabel(ControlEntry entry, TelloUiKit.GamepadBrand brand)
        {
            if (entry.kind != ControlKind.FaceButton || entry.facePosition == null) return entry.fixedLabel;
            return TelloUiKit.ButtonName(brand, entry.facePosition);
        }
    }
}
