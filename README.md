# Blackjack Advisor (plug-in Dalamud, lecture seule)

Ce plug-in **lit** le chat FFXIV pour repérer les messages annonçant les mains d'une partie de blackjack organisée entre joueurs, calcule la décision optimale (stratégie de base) et l'affiche dans un overlay.

**Il n'écrit jamais rien dans le chat et ne clique sur rien.** C'est un outil d'affichage, comme un parseur de dégâts (ACT) — pas un bot qui joue à ta place.

## Fonctionnalités

- Détection **générique** du format d'un dealer (peu importe le libellé exact, tant qu'il y a des symboles de carte ou les mots-clés "hand"/"dealer")
- Gère plusieurs formats simultanément : avec ou sans symboles de carte, avec ou sans nom explicite du joueur, persona du croupier différent du mot "dealer"
- Stratégie de base complète : hit / stand / double / split / surrender / insurance
- Probabilités de victoire (calcul combinatoire exact, deck infini) : "si tu restes" et "avec stratégie optimale"
- Analyse anti-triche : distribution des cartes du croupier (test du chi-carré) + bust réel vs théorique, **sauvegardés entre les sessions**
- Suivi des mises et du bilan net en gil (persistant)
- Alerte visuelle de bust
- Interface bilingue FR/EN (bouton dans l'overlay)
- Commande `/bj` pour afficher/masquer l'overlay

## Pré-requis

1. **XIVLauncher** installé
2. **.NET SDK récent** (le projet cible `Dalamud.NET.Sdk/15.0.0`)
3. Le "Dev Plugin Location" activé dans XIVLauncher (Dalamud in-game → Settings → Experimental → Dev Plugin Locations)

## Compiler et installer

```bash
dotnet build -c Release
```

Dans le jeu :
1. `/xlsettings` → Experimental → Dev Plugin Locations → ajoute le dossier `bin/x64/Release/BlackjackAdvisor/`
2. `/xlplugins` → Dev Tools → Installed Dev Plugins → active "Blackjack Advisor"
3. `/bj` pour afficher/masquer l'overlay à tout moment

## Limites connues

- Ne modélise pas le double/split dans le calcul de probabilité "stratégie optimale"
- Le suivi gil suppose un paiement 1:1 standard sur les gains normaux — à vérifier selon ton dealer
- Un dealer avec un format vraiment inédit (pas de "hand"/"dealer" ni de symboles de carte) peut échapper à la détection ; `/xllog` contient des diagnostics utiles dans ce cas
