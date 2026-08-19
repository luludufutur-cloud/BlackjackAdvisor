using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace BlackjackAdvisor;

// Une entree d'historique de la carte visible du croupier, avec horodatage (utile pour
// l'export CSV et pour eventuellement analyser des tendances dans le temps plus tard).
[Serializable]
public class DealerCardRecord
{
    public string When { get; set; } = "";       // format ISO 8601 (DateTime.ToString("o"))
    public string Category { get; set; } = "";    // "2".."9", "10" (regroupe 10/J/Q/K), "A"
}

// Statistiques anti-triche pour UN croupier donne (identifie par son nom de persona).
// Chaque croupier a son propre historique : melanger les croupiers rendrait le test
// statistique inutile (un croupier honnete + un croupier malhonnete se moyennent).
[Serializable]
public class DealerStats
{
    public List<DealerCardRecord> DealerUpHistory { get; set; } = new();
    public Dictionary<string, int> DealerRoundCount { get; set; } = new();
    public Dictionary<string, int> DealerBustCount { get; set; } = new();
}

// Sauvegarde persistante entre les sessions (fichier JSON gere par Dalamud, stocke dans le
// dossier de config du plug-in). C'est ce qui permet a l'analyse anti-triche de s'affiner
// jour apres jour au lieu de repartir de zero a chaque relance du jeu.
[Serializable]
public class Configuration : IPluginConfiguration
{
    // Version 1 = historique anti-triche global (bug, tous croupiers melanges).
    // Version 2 = historique anti-triche par croupier (DealerStatsByName).
    // Version 3 = cles DealerStatsByName normalisees (fusion des variantes d'un meme croupier
    // dont le nom etait parfois decore de glyphes d'icone dans le chat).
    public int Version { get; set; } = 3;

    // Anti-triche : historique par croupier, cle = nom de persona du croupier (ou "?" si le
    // nom n'a pas pu etre determine). Cumule entre sessions, par croupier.
    public Dictionary<string, DealerStats> DealerStatsByName { get; set; } = new();

    // ===== Champs legacy (v1, cumul global tous croupiers confondus) =====
    // Conserves uniquement pour permettre la migration automatique vers DealerStatsByName au
    // premier chargement apres mise a jour (voir Plugin.MigrateLegacyDealerStats). Plus
    // alimentes depuis la v2 : ne pas les utiliser ailleurs que dans la migration.
    public List<DealerCardRecord> DealerUpHistory { get; set; } = new();
    public Dictionary<string, int> DealerRoundCount { get; set; } = new();
    public Dictionary<string, int> DealerBustCount { get; set; } = new();

    // Position/taille de la fenetre de l'overlay, memorisees entre les sessions. -1 = pas
    // encore definie (l'overlay choisira un emplacement par defaut au premier lancement).
    public float WindowPosX { get; set; } = -1;
    public float WindowPosY { get; set; } = -1;
    public float WindowSizeX { get; set; } = 300;
    public float WindowSizeY { get; set; } = 360;

    public bool OverlayVisible { get; set; } = true;

    // Suivi des gils, cumule entre les sessions. NetGil = somme des gains moins les mises
    // perdues (bust/loss). Hypothese sur "amount" dans les messages de resultat : c'est le
    // PROFIT gagne (pas le total rendu) - deduit du fait qu'un gain normal (non blackjack)
    // affiche le meme montant que la mise placee. A confirmer/corriger si l'usage montre le
    // contraire.
    public long NetGil { get; set; } = 0;
    public int BustCount { get; set; } = 0;
    public int RoundsPlayed { get; set; } = 0;

    // Solde bancaire reel le plus recemment connu (via un message "bank balance of X gil"),
    // bien plus fiable que l'estimation NetGil deduite des lignes de resultat.
    public long? LastKnownBankBalance { get; set; } = null;

    // Langue de l'interface : "fr" ou "en".
    public string Language { get; set; } = "fr";
}
