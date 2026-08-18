using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace BlackjackAdvisor;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Blackjack Advisor";

    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;

    private Configuration _config = null!;
    private bool _configDirty = false;
    private DateTime _lastConfigSaveUtc = DateTime.MinValue;
    private static readonly TimeSpan ConfigSaveThrottle = TimeSpan.FromSeconds(3);

    private bool _overlayVisible = true;
    private string _lastPlayerHand = "-";
    private int _lastPlayerTotal = 0;
    private string _lastDealerCard = "-";
    private int _lastDealerValue = 0;
    private string _lastAdvice = "";
    private bool _lastIsSoft = false;
    private bool _lastIsPair = false;
    private string _lastParseMode = "-"; // "cartes" ou "numerique", pour info dans l'overlay

    private string _lastKnownBet = "-";
    private long _lastKnownBetGil = 0;
    private bool _expectingBets = false;

    private DateTime _justBustedUtc = DateTime.MinValue;
    private static readonly TimeSpan BustFlashDuration = TimeSpan.FromSeconds(8);

    private string? _lastExportPath = null;
    private int? _lastRecordedDealerValue = null;
    private string? _currentRoundDealerUpCategory = null;

    private string _lastTurnPlayer = "-";
    private bool _isMyTurn = false;

    private DateTime _lastActivityUtc = DateTime.MinValue;
    private static readonly TimeSpan InactivityThreshold = TimeSpan.FromSeconds(90);

    private bool _inDealerResolution = false;
    private string? _dealerPersonaName = null;

    private readonly List<string> _pendingRoundQueue = new();
    private List<string> _activeRoundQueue = new();
    private int _activeQueueIndex = 0;

    // ===== Regles GENERIQUES, independantes de la formulation exacte d'un dealer donne =====
    private static readonly Regex CardRegex = new(@"[♣♦♥♠](10|[2-9JQKA])", RegexOptions.Compiled);
    private static readonly Regex DealerKeywordRegex = new(@"dealer", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HandNumberRegex = new(
        @"hand[^\d\[]{0,20}\[?(?<![♣♦♥♠])(?<val>\d{1,2})(?:\s*or\s*(?<val2>\d{1,2}))?\]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] TurnOrDealMarkers = { "'s turn", "\u2019s turn", "'s cards", "\u2019s cards" };

    private static readonly Regex DealingForRegex = new(
        @"Dealing\s+Cards\s+for\s+(?<name>[A-Za-zÀ-ÿ'\-]+(?:\s+[A-Za-zÀ-ÿ'\-]+)*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RecapNameHandRegex = new(
        @"^(?<name>[A-Za-zÀ-ÿ'\-]+(?:\s+[A-Za-zÀ-ÿ'\-]+)*)'s hand is (?<val>\d{1,2})(?:\s*or\s*(?<val2>\d{1,2}))?\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GenericBannerRegex = new(
        @"={2,}.*?(?<name>[A-Za-zÀ-ÿ][\wÀ-ÿ'\-]*(?:\s+[A-Za-zÀ-ÿ][\wÀ-ÿ'\-]*)*)['\u2019]s\b.*?={2,}",
        RegexOptions.Compiled);

    // Noms capturés en ".+?" (non-greedy, tout caractère) plutôt qu'une classe de caractères
    // stricte : tolère les emoji/tags de monde colles au nom (ex: "Arka Traalh🐉Raiden").
    private static readonly Regex BetLineRegex = new(
        @"^(?<name>.+?):\s*(?<amount>[\d,\.]+\s*[kKmM]?)\s*gil",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BetAmountOnlyRegex = new(
        @"^(?<name>.+?):\s*(?<amount>[\d,\.]+)\s*$",
        RegexOptions.Compiled);

    // Resultat de manche : "<nom>: <total> - Win/Bust/Loss/Push/Blackjack" + montant optionnel.
    private static readonly Regex ResultLineRegex = new(
        @"^(?<name>.+?):\s*\d{1,2}\s*-\s*(?<outcome>Win|Bust|Loss|Push|Blackjack)(?:\s*\(\s*(?:won\s*)?(?<amount>[\d,\.]+)\s*(?:gil)?\s*\))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "You currently have a bank balance of 7,250,000 gil." (tell recu suite a une commande) :
    // bien plus fiable que notre estimation deduite des lignes de resultat.
    private static readonly Regex BankBalanceRegex = new(
        @"bank balance of\s*(?<amount>[\d,\.]+)\s*gil",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NamedHandRegex = new(
        @"(?<word>[A-Za-zÀ-ÿ]+)['\u2019]s\s+Hand\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] DiagnosticKeywords = { "hand", "dealer", "blackjack", "hit or stand", "gamba" };
    private static readonly string[] BetDiagnosticKeywords = { "gil", "bet", "wager" };

    // Lignes narratives frequentes autour des mises, reconnues pour ne pas polluer le diag.
    private static readonly string[] BetNoiseMarkers = { "collecting bets", "betting range", "bets pushed", "have their bets" };

    public Plugin()
    {
        _config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _overlayVisible = _config.OverlayVisible;
        if (string.IsNullOrEmpty(_config.Language)) _config.Language = "fr";

        ChatGui.ChatMessage += OnChatMessage;
        PluginInterface.UiBuilder.Draw += DrawOverlay;
        PluginInterface.UiBuilder.OpenMainUi += ShowOverlay;
        PluginInterface.UiBuilder.OpenConfigUi += ShowOverlay;
        CommandManager.AddHandler("/bj", new CommandInfo(OnCommand)
        {
            HelpMessage = "Affiche/masque l'overlay Blackjack Advisor. | Show/hide the Blackjack Advisor overlay."
        });
    }

    public void Dispose()
    {
        ChatGui.ChatMessage -= OnChatMessage;
        PluginInterface.UiBuilder.Draw -= DrawOverlay;
        PluginInterface.UiBuilder.OpenMainUi -= ShowOverlay;
        PluginInterface.UiBuilder.OpenConfigUi -= ShowOverlay;
        CommandManager.RemoveHandler("/bj");
        SaveConfig(force: true);
    }

    // Appele quand le joueur clique sur "Ouvrir"/l'icone d'engrenage du plug-in dans /xlplugins.
    // Ce plug-in n'a qu'une seule fenetre (l'overlay), donc les deux boutons font la meme chose :
    // s'assurer qu'elle est visible.
    private void ShowOverlay()
    {
        _overlayVisible = true;
        _config.OverlayVisible = true;
        SaveConfig(force: true);
    }

    private void OnCommand(string command, string args)
    {
        _overlayVisible = !_overlayVisible;
        _config.OverlayVisible = _overlayVisible;
        SaveConfig(force: true);
    }

    private void SaveConfig(bool force = false)
    {
        if (!force)
        {
            _configDirty = true;
            if (DateTime.UtcNow - _lastConfigSaveUtc < ConfigSaveThrottle)
                return;
        }

        try
        {
            PluginInterface.SavePluginConfig(_config);
            _lastConfigSaveUtc = DateTime.UtcNow;
            _configDirty = false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[BlackjackAdvisor] Echec de la sauvegarde de la configuration.");
        }
    }

    // ===== Localisation =====
    private bool IsFr => _config.Language == "fr";

    private static readonly Dictionary<string, (string fr, string en)> Strings = new()
    {
        ["no_game"] = ("Aucune partie de blackjack detectee en ce moment.", "No blackjack game detected right now."),
        ["no_game_detail"] = ("Le plug-in n'a recu aucun message reconnu depuis plus d'1 min 30. Verifie que le canal du dealer est bien actif, ou regarde /xllog pour les diagnostics.",
                               "The plug-in hasn't received any recognized message for over 1m30. Check that the dealer's channel is active, or check /xllog for diagnostics."),
        ["bust_alert"] = ("\U0001F4A5 TU AS BUST ! (mise perdue : {0} gil) \U0001F4A5", "\U0001F4A5 YOU BUSTED! (lost bet: {0} gil) \U0001F4A5"),
        ["your_turn"] = ("\u2605 C'EST TON TOUR ({0}) \u2605", "\u2605 IT'S YOUR TURN ({0}) \u2605"),
        ["current_turn"] = ("Tour actuel : {0}", "Current turn: {0}"),
        ["your_hand"] = ("Ta main : {0}", "Your hand: {0}"),
        ["total_label"] = ("Total : {0} ({1}{2})", "Total: {0} ({1}{2})"),
        ["soft"] = ("soft", "soft"),
        ["hard"] = ("hard", "hard"),
        ["pair_suffix"] = (", paire", ", pair"),
        ["dealer_label"] = ("Croupier : {0} ({1})", "Dealer: {0} ({1})"),
        ["numeric_warning"] = ("(cartes non detaillees : paire inconnue, conseil approximatif)", "(cards not detailed: pair unknown, approximate advice)"),
        ["last_bet"] = ("Derniere mise detectee : {0} gil", "Last detected bet: {0} gil"),
        ["bank_balance"] = ("Solde bancaire connu : {0} gil", "Known bank balance: {0} gil"),
        ["net_balance"] = ("Bilan net estime (cumul entre sessions) : {0}{1} gil", "Estimated net balance (cumulative across sessions): {0}{1} gil"),
        ["rounds_played"] = ("Manches jouees : {0}  |  Bust : {1}", "Rounds played: {0}  |  Busts: {1}"),
        ["net_disclaimer"] = ("(estimation basee sur les mises/resultats detectes ; le solde bancaire ci-dessus, si connu, est plus fiable)",
                               "(estimate based on detected bets/results; the bank balance above, if known, is more reliable)"),
        ["stand_now"] = ("Si tu restes : {0:F0}% victoire / {1:F0}% egalite / {2:F0}% defaite", "If you stand: {0:F0}% win / {1:F0}% push / {2:F0}% loss"),
        ["optimal"] = ("Avec strategie optimale : {0:F0}% victoire / {1:F0}% egalite / {2:F0}% defaite", "With optimal strategy: {0:F0}% win / {1:F0}% push / {2:F0}% loss"),
        ["optimal_note"] = ("(hit/stand seulement, ne modelise pas double/split ; deck infini)", "(hit/stand only, doesn't model double/split; infinite deck)"),
        ["advice_label"] = ("Conseil :", "Advice:"),
        ["advice_waiting"] = ("En attente d'une main...", "Waiting for a hand..."),
        ["advice_waiting_hand"] = ("En attente de ta main...", "Waiting for your hand..."),
        ["anti_cheat_header"] = ("Analyse anti-triche (beta)", "Anti-cheat analysis (beta)"),
        ["rounds_recorded"] = ("Manches enregistrees (cumul entre sessions) : {0}", "Rounds recorded (cumulative across sessions): {0}"),
        ["chi_square"] = ("Score chi-carre : {0:F2}", "Chi-square score: {0:F2}"),
        ["anti_cheat_note"] = ("Base sur la distribution des cartes visibles du croupier. Indice statistique, pas une preuve formelle.",
                                "Based on the distribution of the dealer's visible cards. Statistical clue, not formal proof."),
        ["not_enough_data"] = ("Pas assez de donnees ({0}/{1} manches minimum).", "Not enough data ({0}/{1} rounds minimum)."),
        ["verdict_bad"] = ("Distribution ANORMALE (p < 1%). Envisage serieusement de partir.", "Distribution ABNORMAL (p < 1%). Seriously consider leaving."),
        ["verdict_suspect"] = ("Distribution suspecte (p < 5%). Reste vigilant.", "Distribution suspicious (p < 5%). Stay alert."),
        ["verdict_ok"] = ("Distribution normale, rien de suspect pour l'instant.", "Normal distribution, nothing suspicious so far."),
        ["bust_rate_header"] = ("Taux de bust reel vs theorique (par carte visible) :", "Real vs theoretical bust rate (by visible card):"),
        ["export_button"] = ("Exporter l'historique (CSV)", "Export history (CSV)"),
        ["last_export"] = ("Dernier export : {0}", "Last export: {0}"),
        ["reset_history"] = ("Reinitialiser l'historique", "Reset history"),
        ["reset_gil"] = ("Reinitialiser le suivi gil", "Reset gil tracking"),
        ["lang_button"] = ("English", "Francais"),
        ["dealer_resolution"] = ("Croupier (resolution)", "Dealer (resolving)"),
        ["waiting_ellipsis"] = ("(en attente...)", "(waiting...)"),
        ["hand_advance_known"] = ("(cartes non detaillees, connue en avance)", "(cards not detailed, known in advance)"),
        ["hand_no_cards"] = ("(cartes non detaillees)", "(cards not detailed)"),
        ["unknown_name"] = ("(nom inconnu)", "(unknown name)"),
        ["other_player"] = ("(autre joueur)", "(other player)"),
        ["insurance_note"] = ("N'accepte pas l'assurance (mauvais pari, avantage maison ~7%). ", "Don't take insurance (bad bet, ~7% house edge). "),
    };

    private string T(string key, params object[] args)
    {
        if (!Strings.TryGetValue(key, out var pair))
            return key;
        var template = IsFr ? pair.fr : pair.en;
        return args.Length > 0 ? string.Format(template, args) : template;
    }

    // Depuis Dalamud v15, l'evenement ChatMessage passe un seul objet IHandleableChatMessage
    // (au lieu des 5 parametres separes des versions precedentes).
    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        var text = chatMessage.Message.TextValue;
        var chatType = chatMessage.LogKind;

        var looksLikeBlackjack = DiagnosticKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

        // A-3) Solde bancaire connu (tell) : bien plus fiable que notre estimation.
        var balanceMatch = BankBalanceRegex.Match(text);
        if (balanceMatch.Success)
        {
            _config.LastKnownBankBalance = ParseGilAmount(balanceMatch.Groups["amount"].Value);
            SaveConfig(force: true);
            _lastActivityUtc = DateTime.UtcNow;
            Log.Information($"[BlackjackAdvisor] [{chatType}] Solde bancaire connu : {_config.LastKnownBankBalance} gil");
            return;
        }

        // A-2) Resultat de manche : "<nom>: <total> - Win/Bust/Loss/Push/Blackjack (montant)".
        var resultMatch = ResultLineRegex.Match(text);
        if (resultMatch.Success)
        {
            var resultName = resultMatch.Groups["name"].Value.Trim();
            var outcome = resultMatch.Groups["outcome"].Value;
            _lastActivityUtc = DateTime.UtcNow;

            if (IsLocalPlayerSubstring(resultName))
            {
                var amountRaw = resultMatch.Groups["amount"].Success ? resultMatch.Groups["amount"].Value : null;
                HandleMyRoundResult(outcome, amountRaw);
            }

            Log.Information($"[BlackjackAdvisor] [{chatType}] Resultat de manche : {resultName} -> {outcome}");
            return;
        }

        // A-1) Banniere "Players placed following bets:" : active la capture des mises qui suivent.
        if (text.Contains("placed following bets", StringComparison.OrdinalIgnoreCase))
        {
            _expectingBets = true;
            _lastActivityUtc = DateTime.UtcNow;
            return;
        }

        if (_expectingBets)
        {
            var betAmountMatch = BetAmountOnlyRegex.Match(text);
            if (betAmountMatch.Success)
            {
                var betName = betAmountMatch.Groups["name"].Value.Trim();
                if (IsLocalPlayerSubstring(betName))
                {
                    _lastKnownBetGil = ParseGilAmount(betAmountMatch.Groups["amount"].Value);
                    _lastKnownBet = _lastKnownBetGil.ToString("N0");
                    Log.Information($"[BlackjackAdvisor] [{chatType}] Mise detectee : {_lastKnownBetGil} gil");
                }
                _lastActivityUtc = DateTime.UtcNow;
                return;
            }
            _expectingBets = false;
        }

        var betMatch = BetLineRegex.Match(text);
        if (betMatch.Success)
        {
            var betName = betMatch.Groups["name"].Value.Trim();
            if (IsLocalPlayerSubstring(betName))
            {
                _lastKnownBetGil = ParseGilAmount(betMatch.Groups["amount"].Value);
                _lastKnownBet = _lastKnownBetGil.ToString("N0");
                _lastActivityUtc = DateTime.UtcNow;
                Log.Information($"[BlackjackAdvisor] [{chatType}] Mise detectee : {_lastKnownBetGil} gil");
            }
            return;
        }

        // A0) Variante "Dealing Cards for <nom>".
        var dealingForMatch = DealingForRegex.Match(text);
        if (dealingForMatch.Success)
        {
            var name = dealingForMatch.Groups["name"].Value.Trim();
            _pendingRoundQueue.Add(name);
            var isMineDealing = IsLocalPlayerSubstring(name);
            _isMyTurn = isMineDealing;
            _lastTurnPlayer = name;
            _lastActivityUtc = DateTime.UtcNow;
            if (isMineDealing)
            {
                _lastPlayerHand = T("waiting_ellipsis");
                _lastPlayerTotal = 0;
                _lastDealerValue = 0;
                _lastDealerCard = "-";
                _lastAdvice = T("advice_waiting_hand");
            }
            Log.Information($"[BlackjackAdvisor] [{chatType}] Donne annoncee pour : {name} (moi = {isMineDealing})");
            return;
        }

        // A1) Recapitulatif "<nom>'s hand is <n>." sans cartes.
        var recapMatch = RecapNameHandRegex.Match(text);
        if (recapMatch.Success)
        {
            var name = recapMatch.Groups["name"].Value.Trim();
            var isSoftRecap = recapMatch.Groups["val2"].Success;
            var recapVal = isSoftRecap ? int.Parse(recapMatch.Groups["val2"].Value) : int.Parse(recapMatch.Groups["val"].Value);

            if (_dealerPersonaName != null && name.Equals(_dealerPersonaName, StringComparison.OrdinalIgnoreCase))
            {
                _lastDealerCard = recapVal == 11 ? "A" : recapVal.ToString();
                _lastDealerValue = recapVal;
                RecordDealerForAntiCheat(recapVal);
                Log.Information($"[BlackjackAdvisor] [{chatType}] Carte croupier connue en avance (recap) : {recapVal}");
            }
            else if (IsLocalPlayerSubstring(name))
            {
                _lastPlayerHand = T("hand_advance_known");
                _lastPlayerTotal = recapVal;
                _lastIsSoft = isSoftRecap;
                _lastIsPair = false;
                _lastParseMode = "numerique";
                Log.Information($"[BlackjackAdvisor] [{chatType}] Ta main connue en avance (recap) : {recapVal}{(isSoftRecap ? " (soft)" : "")}");
            }
            else
            {
                _pendingRoundQueue.Add(name);
            }

            _lastActivityUtc = DateTime.UtcNow;

            if (_lastPlayerTotal > 0 && _lastDealerValue > 0)
            {
                var allowSurrender = text.Contains("surrender", StringComparison.OrdinalIgnoreCase);
                var allowInsurance = text.Contains("insurance", StringComparison.OrdinalIgnoreCase);
                _lastAdvice = ComposeAdvice(_lastPlayerTotal, _lastDealerValue, _lastIsSoft, false, null, allowSurrender, allowInsurance);
            }
        }

        // A2) Marqueur large de tour/donne.
        var markerIndex = FindTurnOrDealMarkerIndex(text);
        string? cleanedBannerName = null;
        if (markerIndex >= 0)
        {
            cleanedBannerName = CleanPlayerName(text.Substring(0, markerIndex));
        }
        else
        {
            var genericBanner = GenericBannerRegex.Match(text);
            if (genericBanner.Success)
                cleanedBannerName = genericBanner.Groups["name"].Value.Trim();
        }

        if (cleanedBannerName != null)
        {
            _lastActivityUtc = DateTime.UtcNow;

            var matchesKnownPlayer = _pendingRoundQueue.Any(n => n.Equals(cleanedBannerName, StringComparison.OrdinalIgnoreCase))
                                      || _activeRoundQueue.Any(n => n.Equals(cleanedBannerName, StringComparison.OrdinalIgnoreCase));

            var isDealerResolutionBanner = text.Contains("dealer's turn", StringComparison.OrdinalIgnoreCase) ||
                                            text.Contains("dealer\u2019s turn", StringComparison.OrdinalIgnoreCase) ||
                                            (!matchesKnownPlayer && _pendingRoundQueue.Count > 0 && !IsLocalPlayerSubstring(cleanedBannerName));

            if (isDealerResolutionBanner)
            {
                _inDealerResolution = true;
                _isMyTurn = false;
                _lastTurnPlayer = T("dealer_resolution");
                _dealerPersonaName = cleanedBannerName;

                if (_pendingRoundQueue.Count > 0)
                {
                    _activeRoundQueue = new List<string>(_pendingRoundQueue);
                    _activeQueueIndex = 0;
                    _pendingRoundQueue.Clear();
                }

                Log.Information($"[BlackjackAdvisor] [{chatType}] Phase de resolution/reveal du croupier detectee (persona = \"{_dealerPersonaName}\").");
            }
            else
            {
                if (_inDealerResolution)
                {
                    _lastRecordedDealerValue = null;
                    _inDealerResolution = false;
                }

                var isMine = IsLocalPlayerSubstring(text);
                _isMyTurn = isMine;
                _lastTurnPlayer = isMine ? (PlayerState.CharacterName ?? cleanedBannerName) : cleanedBannerName;
                if (isMine)
                {
                    _lastPlayerHand = T("waiting_ellipsis");
                    _lastPlayerTotal = 0;
                    _lastDealerValue = 0;
                    _lastDealerCard = "-";
                    _lastAdvice = T("advice_waiting_hand");
                }
                Log.Information($"[BlackjackAdvisor] [{chatType}] Tour/donne detecte : {_lastTurnPlayer} (moi = {isMine})");
            }
        }

        // B) Extraction generique via les symboles de carte.
        var cardResult = TryExtractFromCards(text);
        if (cardResult.ok)
        {
            _lastActivityUtc = DateTime.UtcNow;
            if (cardResult.hasPlayer)
            {
                ResolveOwnershipForAnonymousHand(text, cardResult.hand);

                // Filet de securite : ton nom explicite dans CE message force la reconnaissance.
                if (IsLocalPlayerSubstring(text))
                {
                    _isMyTurn = true;
                    _lastTurnPlayer = PlayerState.CharacterName ?? _lastTurnPlayer;
                }

                if (_isMyTurn)
                {
                    var allowSurrender = text.Contains("surrender", StringComparison.OrdinalIgnoreCase);
                    var allowInsurance = text.Contains("insurance", StringComparison.OrdinalIgnoreCase);
                    ApplyResult(cardResult.hand, cardResult.total, cardResult.isSoft,
                        cardResult.hasDealer, cardResult.dealerCard, cardResult.dealerValue, "cartes",
                        allowSurrender, allowInsurance);
                }
                else
                {
                    Log.Information($"[BlackjackAdvisor] (diag) Main ignoree (total={cardResult.total}, _isMyTurn=false, tour connu=\"{_lastTurnPlayer}\") : \"{text}\"");
                }
            }
            else if (cardResult.hasDealer)
            {
                _lastDealerCard = cardResult.dealerValue == 11 ? "A" : cardResult.dealerValue.ToString();
                _lastDealerValue = cardResult.dealerValue;

                var isTerminal = text.Contains("bust", StringComparison.OrdinalIgnoreCase) ||
                                  text.Contains("finished", StringComparison.OrdinalIgnoreCase);

                if (isTerminal)
                {
                    RecordDealerRoundOutcome(cardResult.dealerValue > 21, _currentRoundDealerUpCategory);
                }
                else
                {
                    RecordDealerForAntiCheat(cardResult.dealerValue);
                }

                Log.Information($"[BlackjackAdvisor] [{chatType}] Carte(s)/resultat croupier : {cardResult.dealerCard} = {cardResult.dealerValue} (terminal={isTerminal})");
            }
            return;
        }

        // C) Extraction generique via les nombres pres du mot "hand".
        var numResult = TryExtractFromNumbers(text);
        if (numResult.ok)
        {
            _lastActivityUtc = DateTime.UtcNow;

            if (numResult.hasPlayer)
            {
                var namedPlayer = _activeRoundQueue.FirstOrDefault(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (namedPlayer != null)
                {
                    _lastTurnPlayer = namedPlayer;
                    _isMyTurn = IsLocalPlayerSubstring(namedPlayer);
                    var pos = _activeRoundQueue.IndexOf(namedPlayer);
                    if (pos >= _activeQueueIndex)
                        _activeQueueIndex = pos + 1;
                }

                if (IsLocalPlayerSubstring(text))
                {
                    _isMyTurn = true;
                    _lastTurnPlayer = PlayerState.CharacterName ?? _lastTurnPlayer;
                }
            }

            if (numResult.hasDealer)
            {
                RecordDealerForAntiCheat(numResult.dealerValue);
            }

            if (numResult.hasPlayer && _isMyTurn)
            {
                var allowSurrender = text.Contains("surrender", StringComparison.OrdinalIgnoreCase);
                var allowInsurance = text.Contains("insurance", StringComparison.OrdinalIgnoreCase);
                var dealerValue = numResult.hasDealer ? numResult.dealerValue : _lastDealerValue;

                _lastPlayerHand = T("hand_no_cards");
                _lastPlayerTotal = numResult.playerTotal;
                if (numResult.hasDealer)
                {
                    _lastDealerCard = dealerValue == 11 ? "A" : dealerValue.ToString();
                    _lastDealerValue = dealerValue;
                }
                _lastIsSoft = numResult.playerIsSoft;
                _lastIsPair = false;
                _lastParseMode = "numerique";
                _lastAdvice = ComposeAdvice(numResult.playerTotal, dealerValue, numResult.playerIsSoft, false, null, allowSurrender, allowInsurance);

                Log.Information($"[BlackjackAdvisor] [{chatType}] (numerique) Total={numResult.playerTotal}{(numResult.playerIsSoft ? " (soft)" : "")} vs Croupier={dealerValue} -> {_lastAdvice}");
            }
            else if (numResult.hasPlayer && !_isMyTurn)
            {
                Log.Information($"[BlackjackAdvisor] (diag) [{chatType}] Total numerique detecte ({numResult.playerTotal}) mais ignore car _isMyTurn=false, tour connu=\"{_lastTurnPlayer}\" : \"{text}\"");
            }
            else if (numResult.hasDealer && !numResult.hasPlayer)
            {
                _lastDealerCard = numResult.dealerValue == 11 ? "A" : numResult.dealerValue.ToString();
                _lastDealerValue = numResult.dealerValue;
                Log.Information($"[BlackjackAdvisor] [{chatType}] Carte croupier isolee (numerique) : {numResult.dealerValue}");
            }
            return;
        }

        // D) Rien n'a matche.
        if (looksLikeBlackjack)
        {
            Log.Warning($"[BlackjackAdvisor] (diag) Message lie au blackjack non reconnu sur le canal [{chatType}] : \"{text}\"");
        }
        else if (BetNoiseMarkers.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            // Narration connue et sans impact (ex: "Collecting bets!", "All players ... bets pushed!") :
            // pas la peine de logger un avertissement pour ca.
            Log.Information($"[BlackjackAdvisor] [{chatType}] Narration liee aux mises (ignoree, sans impact) : \"{text}\"");
        }
        else if (BetDiagnosticKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Warning($"[BlackjackAdvisor] (diag mise) Ligne liee a une mise non reconnue sur le canal [{chatType}] : \"{text}\"");
        }
    }

    private static long ParseGilAmount(string raw)
    {
        raw = raw.Trim();
        var multiplier = 1L;
        if (raw.Length > 0 && (raw[^1] == 'k' || raw[^1] == 'K')) { multiplier = 1_000; raw = raw[..^1]; }
        else if (raw.Length > 0 && (raw[^1] == 'm' || raw[^1] == 'M')) { multiplier = 1_000_000; raw = raw[..^1]; }
        var digitsOnly = new string(raw.Where(char.IsDigit).ToArray());
        return long.TryParse(digitsOnly, out var val) ? val * multiplier : 0;
    }

    private void HandleMyRoundResult(string outcome, string? amountRaw)
    {
        _config.RoundsPlayed++;

        switch (outcome.ToLowerInvariant())
        {
            case "bust":
                _justBustedUtc = DateTime.UtcNow;
                _config.BustCount++;
                _config.NetGil -= _lastKnownBetGil;
                Log.Information($"[BlackjackAdvisor] TU AS BUST ! Mise perdue : {_lastKnownBetGil} gil");
                break;
            case "loss":
                _config.NetGil -= _lastKnownBetGil;
                break;
            case "win":
            case "blackjack":
                var profit = amountRaw != null ? ParseGilAmount(amountRaw) : _lastKnownBetGil;
                _config.NetGil += profit;
                break;
            case "push":
                break;
        }

        SaveConfig(force: true);
    }

    private string ComposeAdvice(int total, int dealerValue, bool isSoft, bool isPair, string? pairRank, bool allowSurrender, bool allowInsurance)
    {
        var (kind, param) = BasicStrategy.GetAdviceKind(total, dealerValue, isSoft, isPair, pairRank, allowSurrender);
        var advice = TranslateAdvice(kind, param);
        if (allowInsurance)
            advice = T("insurance_note") + advice;
        return advice;
    }

    private string TranslateAdvice(AdviceKind kind, string? param) => kind switch
    {
        AdviceKind.Hit => IsFr ? "Tire." : "Hit.",
        AdviceKind.Stand => IsFr ? "Reste." : "Stand.",
        AdviceKind.Stand21OrMore => IsFr ? "21 ou plus : Reste." : "21 or more: Stand.",
        AdviceKind.DoubleElseHit => IsFr ? "Double si possible, sinon Tire." : "Double if possible, otherwise Hit.",
        AdviceKind.DoubleElseStand => IsFr ? "Double si possible, sinon Reste." : "Double if possible, otherwise Stand.",
        AdviceKind.SplitRank => IsFr ? $"Split (les {param})." : $"Split ({param}s).",
        AdviceKind.SurrenderElseHit => IsFr ? "Abandonne (surrender) si possible, sinon Tire." : "Surrender if possible, otherwise Hit.",
        _ => "?"
    };

    private static int FindTurnOrDealMarkerIndex(string text)
    {
        int bestIndex = -1;
        foreach (var marker in TurnOrDealMarkers)
        {
            var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (bestIndex == -1 || idx < bestIndex))
                bestIndex = idx;
        }
        return bestIndex;
    }

    private void ResolveOwnershipForAnonymousHand(string text, string handStr)
    {
        if (_activeRoundQueue.Count == 0)
            return;

        var namedPlayer = _activeRoundQueue.FirstOrDefault(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
        if (namedPlayer != null)
        {
            _lastTurnPlayer = namedPlayer;
            _isMyTurn = IsLocalPlayerSubstring(namedPlayer);
            var pos = _activeRoundQueue.IndexOf(namedPlayer);
            if (pos >= _activeQueueIndex)
                _activeQueueIndex = pos + 1;
            Log.Information($"[BlackjackAdvisor] Main attribuee via nom explicite : {namedPlayer} (moi = {_isMyTurn})");
            return;
        }

        if (_activeQueueIndex < _activeRoundQueue.Count && GetRanks(handStr).Count == 2)
        {
            var queuedName = _activeRoundQueue[_activeQueueIndex];
            _lastTurnPlayer = queuedName;
            _isMyTurn = IsLocalPlayerSubstring(queuedName);
            _activeQueueIndex++;
            Log.Information($"[BlackjackAdvisor] Main anonyme attribuee via file d'attente : {queuedName} (moi = {_isMyTurn})");
        }
    }

    private string CleanPlayerName(string region)
    {
        var cleaned = region;
        var arrowIdx = cleaned.LastIndexOf("->", StringComparison.Ordinal);
        if (arrowIdx >= 0)
            cleaned = cleaned.Substring(arrowIdx + 2);

        cleaned = Regex.Replace(cleaned, @"Dealing\s+", "", RegexOptions.IgnoreCase);
        cleaned = cleaned.Trim(' ', '=', '\u2605', '\u2606', '\t', '\r', '\n', '-', '>', ',');

        return string.IsNullOrWhiteSpace(cleaned) ? T("unknown_name") : cleaned;
    }

    private int FindDealerSplitIndex(string text)
    {
        var literalMatch = DealerKeywordRegex.Match(text);
        var literalIdx = literalMatch.Success ? literalMatch.Index : -1;

        var personaIdx = -1;
        if (_dealerPersonaName != null)
            personaIdx = text.IndexOf(_dealerPersonaName, StringComparison.OrdinalIgnoreCase);

        var genericIdx = -1;
        foreach (Match m in NamedHandRegex.Matches(text))
        {
            if (!m.Groups["word"].Value.Equals("Your", StringComparison.OrdinalIgnoreCase))
            {
                genericIdx = m.Index;
                break;
            }
        }

        var candidates = new[] { literalIdx, personaIdx, genericIdx }.Where(i => i >= 0).ToList();
        return candidates.Count > 0 ? candidates.Min() : -1;
    }

    private (bool ok, bool hasPlayer, string hand, int total, bool isSoft, bool hasDealer, string dealerCard, int dealerValue) TryExtractFromCards(string text)
    {
        var allCards = CardRegex.Matches(text);
        if (allCards.Count == 0)
            return (false, false, "", 0, false, false, "", 0);

        var dealerSplitIndex = FindDealerSplitIndex(text);

        if (dealerSplitIndex >= 0)
        {
            var playerTokens = new List<string>();
            var playerRanks = new List<string>();
            var afterDealerRanks = new List<string>();
            string? dealerToken = null;

            foreach (Match c in allCards)
            {
                if (c.Index < dealerSplitIndex)
                {
                    playerTokens.Add(c.Value);
                    playerRanks.Add(c.Groups[1].Value);
                }
                else
                {
                    afterDealerRanks.Add(c.Groups[1].Value);
                    if (dealerToken == null) dealerToken = c.Value;
                }
            }

            if (playerRanks.Count > 0)
            {
                var (total, isSoft) = ComputeHandValue(playerRanks);
                var handStr = string.Concat(playerTokens);
                if (afterDealerRanks.Count > 0)
                {
                    var (dealerValue, _) = ComputeHandValue(new List<string> { afterDealerRanks[0] });
                    return (true, true, handStr, total, isSoft, true, dealerToken!, dealerValue);
                }
                return (true, true, handStr, total, isSoft, false, "", 0);
            }

            if (afterDealerRanks.Count > 0)
            {
                var (dealerTotal, _) = ComputeHandValue(afterDealerRanks);
                var dealerHandStr = string.Concat(allCards.Where(c => c.Index >= dealerSplitIndex).Select(c => c.Value));
                return (true, false, "", 0, false, true, dealerHandStr, dealerTotal);
            }

            return (false, false, "", 0, false, false, "", 0);
        }

        if (_inDealerResolution)
        {
            var ranks = allCards.Select(c => c.Groups[1].Value).ToList();
            var tokens = allCards.Select(c => c.Value).ToList();
            var (dealerTotal, _) = ComputeHandValue(ranks);
            return (true, false, "", 0, false, true, string.Concat(tokens), dealerTotal);
        }

        if (allCards.Count >= 2)
        {
            var ranks = allCards.Select(c => c.Groups[1].Value).ToList();
            var tokens = allCards.Select(c => c.Value).ToList();
            var (total, isSoft) = ComputeHandValue(ranks);
            return (true, true, string.Concat(tokens), total, isSoft, false, "", 0);
        }

        return (false, false, "", 0, false, false, "", 0);
    }

    private (bool ok, bool hasPlayer, int playerTotal, bool playerIsSoft, bool hasDealer, int dealerValue) TryExtractFromNumbers(string text)
    {
        var matches = HandNumberRegex.Matches(text);
        if (matches.Count == 0)
            return (false, false, 0, false, false, 0);

        bool hasPlayer = false, hasDealer = false, playerIsSoft = false;
        int playerTotal = 0, dealerValue = 0;

        foreach (Match m in matches)
        {
            var isSoftMatch = m.Groups["val2"].Success;
            var val = isSoftMatch ? int.Parse(m.Groups["val2"].Value) : int.Parse(m.Groups["val"].Value);
            var lookbackStart = Math.Max(0, m.Index - 15);
            var context = text.Substring(lookbackStart, m.Index - lookbackStart);
            var nearDealer = context.IndexOf("dealer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              (_dealerPersonaName != null && context.IndexOf(_dealerPersonaName, StringComparison.OrdinalIgnoreCase) >= 0);

            if (nearDealer)
            {
                if (!hasDealer) { hasDealer = true; dealerValue = val; }
            }
            else
            {
                if (!hasPlayer) { hasPlayer = true; playerTotal = val; playerIsSoft = isSoftMatch; }
            }
        }

        return (hasPlayer || hasDealer, hasPlayer, playerTotal, playerIsSoft, hasDealer, dealerValue);
    }

    private static (int total, bool isSoft) ComputeHandValue(List<string> ranks)
    {
        int total = 0;
        int aceCount = 0;
        foreach (var rank in ranks)
        {
            if (rank == "A") { aceCount++; total += 11; }
            else if (rank is "J" or "Q" or "K" or "10") total += 10;
            else total += int.Parse(rank);
        }
        while (total > 21 && aceCount > 0)
        {
            total -= 10;
            aceCount--;
        }
        return (total, aceCount > 0);
    }

    private void ApplyResult(string handStr, int total, bool isSoft, bool hasDealer, string dealerCard, int dealerValue, string parseMode, bool allowSurrender = false, bool allowInsurance = false)
    {
        var finalDealerCard = hasDealer ? dealerCard : _lastDealerCard;
        var finalDealerValue = hasDealer ? dealerValue : _lastDealerValue;

        var ranks = GetRanks(handStr);
        var isPair = ranks.Count == 2 && NormalizeRank(ranks[0]) == NormalizeRank(ranks[1]);
        var pairRank = isPair ? NormalizeRank(ranks[0]) : null;

        _lastPlayerHand = handStr;
        _lastPlayerTotal = total;
        _lastDealerCard = finalDealerCard;
        _lastDealerValue = finalDealerValue;
        _lastIsSoft = isSoft;
        _lastIsPair = isPair;
        _lastParseMode = parseMode;
        _lastAdvice = ComposeAdvice(total, finalDealerValue, isSoft, isPair, pairRank, allowSurrender, allowInsurance);

        if (hasDealer)
            RecordDealerForAntiCheat(dealerValue);

        Log.Information($"[BlackjackAdvisor] ({parseMode}) Main: {handStr} (Total {total}, {(isSoft ? "soft" : "hard")}{(isPair ? ", paire " + pairRank : "")}) vs Croupier {finalDealerCard} ({finalDealerValue}) -> {_lastAdvice}");
    }

    private void RecordDealerForAntiCheat(int dealerValue)
    {
        if (_inDealerResolution)
            return;

        if (_lastRecordedDealerValue.HasValue && _lastRecordedDealerValue.Value == dealerValue)
            return;

        _lastRecordedDealerValue = dealerValue;
        var category = dealerValue == 11 ? "A" : dealerValue >= 10 ? "10" : dealerValue.ToString();
        _currentRoundDealerUpCategory = category;

        _config.DealerUpHistory.Add(new DealerCardRecord { When = DateTime.Now.ToString("o"), Category = category });
        SaveConfig();

        Log.Information($"[BlackjackAdvisor] (anti-triche) Carte croupier enregistree : {category} (total cumule : {_config.DealerUpHistory.Count})");
    }

    private void RecordDealerRoundOutcome(bool busted, string? category)
    {
        if (category == null)
            return;

        _config.DealerRoundCount.TryGetValue(category, out var rounds);
        _config.DealerRoundCount[category] = rounds + 1;

        if (busted)
        {
            _config.DealerBustCount.TryGetValue(category, out var busts);
            _config.DealerBustCount[category] = busts + 1;
        }

        SaveConfig();
        Log.Information($"[BlackjackAdvisor] (anti-triche) Resultat croupier : categorie={category}, bust={busted} ({_config.DealerRoundCount[category]} manches cumulees pour cette categorie)");
    }

    private (int rounds, double chiSquare, string verdict, System.Numerics.Vector4 color) AnalyzeDealerBias()
    {
        int n = _config.DealerUpHistory.Count;
        const int minRoundsForAnalysis = 20;

        if (n < minRoundsForAnalysis)
            return (n, 0, T("not_enough_data", n, minRoundsForAnalysis), new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f));

        var categories = new[] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "A" };
        double chiSquare = 0;
        foreach (var cat in categories)
        {
            double expectedProp = cat == "10" ? 4.0 / 13.0 : 1.0 / 13.0;
            double expected = expectedProp * n;
            int observed = _config.DealerUpHistory.Count(x => x.Category == cat);
            chiSquare += Math.Pow(observed - expected, 2) / expected;
        }

        const double criticalAlpha01 = 21.666;
        const double criticalAlpha05 = 16.919;

        if (chiSquare >= criticalAlpha01)
            return (n, chiSquare, T("verdict_bad"), new System.Numerics.Vector4(1f, 0.25f, 0.25f, 1f));

        if (chiSquare >= criticalAlpha05)
            return (n, chiSquare, T("verdict_suspect"), new System.Numerics.Vector4(1f, 0.7f, 0.2f, 1f));

        return (n, chiSquare, T("verdict_ok"), new System.Numerics.Vector4(0.3f, 1f, 0.3f, 1f));
    }

    private void ExportHistoryToCsv()
    {
        try
        {
            var dir = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"blackjack_history_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Horodatage,CategorieCarteCroupier");
            foreach (var rec in _config.DealerUpHistory)
                sb.AppendLine($"{rec.When},{rec.Category}");

            File.WriteAllText(path, sb.ToString());
            _lastExportPath = path;
            Log.Information($"[BlackjackAdvisor] Historique exporte : {path}");
        }
        catch (Exception ex)
        {
            _lastExportPath = "(echec de l'export, voir /xllog)";
            Log.Error(ex, "[BlackjackAdvisor] Echec de l'export CSV.");
        }
    }

    private static List<string> GetRanks(string handStr)
    {
        var result = new List<string>();
        foreach (Match c in CardRegex.Matches(handStr))
            result.Add(c.Groups[1].Value);
        return result;
    }

    private static string NormalizeRank(string rank) =>
        (rank is "J" or "Q" or "K") ? "10" : rank;

    private static bool IsLocalPlayerSubstring(string region)
    {
        if (!PlayerState.IsLoaded) return false;
        var local = PlayerState.CharacterName;
        if (string.IsNullOrEmpty(local)) return false;

        if (region.Contains(local, StringComparison.OrdinalIgnoreCase)) return true;
        var localFirstName = local.Split(' ')[0];
        return region.Contains(localFirstName, StringComparison.OrdinalIgnoreCase);
    }

    // ===== Calcul de probabilites (deck infini) =====
    private static readonly Dictionary<(int hardSum, bool hasAce), Dictionary<string, double>> DealerDistCache = new();

    private static readonly (int value, double prob)[] CardDraws =
    {
        (2, 1.0/13), (3, 1.0/13), (4, 1.0/13), (5, 1.0/13), (6, 1.0/13),
        (7, 1.0/13), (8, 1.0/13), (9, 1.0/13), (10, 4.0/13), (1, 1.0/13),
    };

    private static Dictionary<string, double> DealerOutcomeDistribution(int hardSum, bool hasAce)
    {
        if (DealerDistCache.TryGetValue((hardSum, hasAce), out var cached))
            return cached;

        var result = new Dictionary<string, double>();

        if (hardSum > 21)
        {
            result["bust"] = 1.0;
        }
        else
        {
            var eff = (hasAce && hardSum + 10 <= 21) ? hardSum + 10 : hardSum;
            if (eff >= 17)
            {
                result[eff.ToString()] = 1.0;
            }
            else
            {
                foreach (var (value, prob) in CardDraws)
                {
                    var subDist = DealerOutcomeDistribution(hardSum + value, hasAce || value == 1);
                    foreach (var kv in subDist)
                        result[kv.Key] = result.GetValueOrDefault(kv.Key) + prob * kv.Value;
                }
            }
        }

        DealerDistCache[(hardSum, hasAce)] = result;
        return result;
    }

    private static (double win, double push, double lose)? EstimateOutcome(int playerTotal, int dealerUpcard)
    {
        if (playerTotal <= 0 || playerTotal > 21 || dealerUpcard <= 0)
            return null;

        var hardSum = dealerUpcard == 11 ? 1 : dealerUpcard;
        var hasAce = dealerUpcard == 11;
        var dist = DealerOutcomeDistribution(hardSum, hasAce);

        double win = 0, push = 0, lose = 0;
        foreach (var kv in dist)
        {
            if (kv.Key == "bust") { win += kv.Value; continue; }
            var dealerTotal = int.Parse(kv.Key);
            if (dealerTotal < playerTotal) win += kv.Value;
            else if (dealerTotal == playerTotal) push += kv.Value;
            else lose += kv.Value;
        }
        return (win * 100.0, push * 100.0, lose * 100.0);
    }

    private static (double win, double push, double lose) PlayerBestOutcome(
        int hardSum, bool hasAce, Dictionary<string, double> dealerDist,
        Dictionary<(int, bool), (double, double, double)> memo)
    {
        if (memo.TryGetValue((hardSum, hasAce), out var cached))
            return cached;

        if (hardSum > 21)
        {
            var bustResult = (0.0, 0.0, 1.0);
            memo[(hardSum, hasAce)] = bustResult;
            return bustResult;
        }

        var eff = (hasAce && hardSum + 10 <= 21) ? hardSum + 10 : hardSum;

        double standWin = 0, standPush = 0, standLose = 0;
        foreach (var kv in dealerDist)
        {
            if (kv.Key == "bust") { standWin += kv.Value; continue; }
            var dt = int.Parse(kv.Key);
            if (dt < eff) standWin += kv.Value;
            else if (dt == eff) standPush += kv.Value;
            else standLose += kv.Value;
        }

        if (eff >= 21)
        {
            var r = (standWin, standPush, standLose);
            memo[(hardSum, hasAce)] = r;
            return r;
        }

        double hitWin = 0, hitPush = 0, hitLose = 0;
        foreach (var (value, prob) in CardDraws)
        {
            var (w, p, l) = PlayerBestOutcome(hardSum + value, hasAce || value == 1, dealerDist, memo);
            hitWin += prob * w; hitPush += prob * p; hitLose += prob * l;
        }

        var standScore = standWin - standLose;
        var hitScore = hitWin - hitLose;
        var result = hitScore > standScore ? (hitWin, hitPush, hitLose) : (standWin, standPush, standLose);
        memo[(hardSum, hasAce)] = result;
        return result;
    }

    private static (double win, double push, double lose)? EstimateOptimalOutcome(int playerTotal, bool playerIsSoft, int dealerUpcard)
    {
        if (playerTotal <= 0 || playerTotal > 21 || dealerUpcard <= 0)
            return null;

        var dealerHardSum = dealerUpcard == 11 ? 1 : dealerUpcard;
        var dealerHasAce = dealerUpcard == 11;
        var dealerDist = DealerOutcomeDistribution(dealerHardSum, dealerHasAce);

        var playerHardSum = playerIsSoft ? playerTotal - 10 : playerTotal;
        var playerHasAce = playerIsSoft;

        var memo = new Dictionary<(int, bool), (double, double, double)>();
        var (w, p, l) = PlayerBestOutcome(playerHardSum, playerHasAce, dealerDist, memo);
        return (w * 100.0, p * 100.0, l * 100.0);
    }

    private static double TheoreticalBustRate(string category)
    {
        var upVal = category == "A" ? 11 : int.Parse(category);
        var hardSum = upVal == 11 ? 1 : upVal;
        var hasAce = upVal == 11;
        var dist = DealerOutcomeDistribution(hardSum, hasAce);
        return dist.GetValueOrDefault("bust", 0) * 100.0;
    }

    private void DrawOverlay()
    {
        if (!_overlayVisible) return;

        if (_config.WindowPosX >= 0)
            ImGui.SetNextWindowPos(new System.Numerics.Vector2(_config.WindowPosX, _config.WindowPosY), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(_config.WindowSizeX, _config.WindowSizeY), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Blackjack Advisor", ref _overlayVisible))
        {
            var winPos = ImGui.GetWindowPos();
            var winSize = ImGui.GetWindowSize();
            if (Math.Abs(winPos.X - _config.WindowPosX) > 1f || Math.Abs(winPos.Y - _config.WindowPosY) > 1f ||
                Math.Abs(winSize.X - _config.WindowSizeX) > 1f || Math.Abs(winSize.Y - _config.WindowSizeY) > 1f)
            {
                _config.WindowPosX = winPos.X;
                _config.WindowPosY = winPos.Y;
                _config.WindowSizeX = winSize.X;
                _config.WindowSizeY = winSize.Y;
                SaveConfig();
            }

            // Toggle de langue, en haut a droite de la fenetre.
            if (ImGui.Button(T("lang_button")))
            {
                _config.Language = IsFr ? "en" : "fr";
                SaveConfig(force: true);
            }

            var noGameDetected = _lastActivityUtc == DateTime.MinValue ||
                                  (DateTime.UtcNow - _lastActivityUtc) > InactivityThreshold;

            var sinceBust = DateTime.UtcNow - _justBustedUtc;
            if (sinceBust < BustFlashDuration)
            {
                var pulse = (float)(0.5 + 0.5 * Math.Sin(ImGui.GetTime() * 8.0));
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.15f + 0.3f * pulse, 0.15f, 1f), T("bust_alert", _lastKnownBetGil.ToString("N0")));
                ImGui.Separator();
            }

            if (noGameDetected)
            {
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f), T("no_game"));
                ImGui.TextWrapped(T("no_game_detail"));
                ImGui.Separator();
            }

            if (_isMyTurn)
            {
                var pulse = (float)(0.5 + 0.5 * Math.Sin(ImGui.GetTime() * 4.0));
                var bannerColor = new System.Numerics.Vector4(0.2f + 0.6f * pulse, 0.85f, 0.2f, 1f);
                ImGui.TextColored(bannerColor, T("your_turn", _lastTurnPlayer));
            }
            else
            {
                ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f), T("current_turn", _lastTurnPlayer));
            }

            ImGui.Separator();
            ImGui.Text(T("your_hand", _lastPlayerHand));
            ImGui.Text(T("total_label", _lastPlayerTotal, T(_lastIsSoft ? "soft" : "hard"), _lastIsPair ? T("pair_suffix") : ""));
            ImGui.Text(T("dealer_label", _lastDealerCard, _lastDealerValue));
            if (_lastParseMode == "numerique")
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.2f, 1f), T("numeric_warning"));

            ImGui.Text(T("last_bet", _lastKnownBet));
            if (_config.LastKnownBankBalance.HasValue)
            {
                ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.85f, 1f, 1f), T("bank_balance", _config.LastKnownBankBalance.Value.ToString("N0")));
            }
            var netColor = _config.NetGil >= 0
                ? new System.Numerics.Vector4(0.4f, 0.9f, 0.4f, 1f)
                : new System.Numerics.Vector4(0.9f, 0.4f, 0.4f, 1f);
            ImGui.TextColored(netColor, T("net_balance", _config.NetGil >= 0 ? "+" : "", _config.NetGil.ToString("N0")));
            ImGui.Text(T("rounds_played", _config.RoundsPlayed,
                _config.RoundsPlayed > 0 ? $"{_config.BustCount} ({100.0 * _config.BustCount / _config.RoundsPlayed:F0}%)" : _config.BustCount.ToString()));
            ImGui.TextDisabled(T("net_disclaimer"));

            var standOutcome = EstimateOutcome(_lastPlayerTotal, _lastDealerValue);
            var optimalOutcome = EstimateOptimalOutcome(_lastPlayerTotal, _lastIsSoft, _lastDealerValue);
            if (standOutcome.HasValue)
            {
                var (win, push, lose) = standOutcome.Value;
                ImGui.TextColored(win >= 50 ? new System.Numerics.Vector4(0.4f, 0.9f, 0.4f, 1f) : new System.Numerics.Vector4(0.9f, 0.6f, 0.3f, 1f),
                    T("stand_now", win, push, lose));
            }
            if (optimalOutcome.HasValue)
            {
                var (win, push, lose) = optimalOutcome.Value;
                ImGui.TextColored(win >= 50 ? new System.Numerics.Vector4(0.4f, 0.9f, 0.4f, 1f) : new System.Numerics.Vector4(0.9f, 0.6f, 0.3f, 1f),
                    T("optimal", win, push, lose));
                ImGui.TextDisabled(T("optimal_note"));
            }

            ImGui.Separator();
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.2f, 1f), T("advice_label"));
            ImGui.TextWrapped(!string.IsNullOrEmpty(_lastAdvice) ? _lastAdvice : T("advice_waiting"));

            ImGui.Separator();
            if (ImGui.CollapsingHeader(T("anti_cheat_header")))
            {
                var (rounds, chiSquare, verdict, color) = AnalyzeDealerBias();
                ImGui.Text(T("rounds_recorded", rounds));
                if (rounds > 0)
                    ImGui.Text(T("chi_square", chiSquare));
                ImGui.TextColored(color, verdict);
                ImGui.TextWrapped(T("anti_cheat_note"));

                ImGui.Spacing();
                ImGui.Text(T("bust_rate_header"));
                foreach (var cat in new[] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "A" })
                {
                    if (!_config.DealerRoundCount.TryGetValue(cat, out var total) || total < 5)
                        continue;
                    _config.DealerBustCount.TryGetValue(cat, out var busts);
                    var observed = 100.0 * busts / total;
                    var theoretical = TheoreticalBustRate(cat);
                    var diff = observed - theoretical;
                    var lineColor = Math.Abs(diff) > 15
                        ? new System.Numerics.Vector4(1f, 0.5f, 0.3f, 1f)
                        : new System.Numerics.Vector4(0.8f, 0.8f, 0.8f, 1f);
                    ImGui.TextColored(lineColor, $"  {cat} : {observed:F0}% / {theoretical:F0}% (n={total})");
                }

                ImGui.Spacing();
                if (ImGui.Button(T("export_button")))
                {
                    ExportHistoryToCsv();
                }
                if (_lastExportPath != null)
                    ImGui.TextWrapped(T("last_export", _lastExportPath));

                if (ImGui.Button(T("reset_history")))
                {
                    _config.DealerUpHistory.Clear();
                    _config.DealerBustCount.Clear();
                    _config.DealerRoundCount.Clear();
                    _lastRecordedDealerValue = null;
                    SaveConfig(force: true);
                    Log.Information("[BlackjackAdvisor] (anti-triche) Historique reinitialise (y compris la sauvegarde persistante).");
                }

                ImGui.SameLine();
                if (ImGui.Button(T("reset_gil")))
                {
                    _config.NetGil = 0;
                    _config.BustCount = 0;
                    _config.RoundsPlayed = 0;
                    SaveConfig(force: true);
                    Log.Information("[BlackjackAdvisor] Suivi gil reinitialise.");
                }
            }
        }
        else
        {
            if (_config.OverlayVisible != _overlayVisible)
            {
                _config.OverlayVisible = _overlayVisible;
                SaveConfig(force: true);
            }
        }
        ImGui.End();

        if (_configDirty && DateTime.UtcNow - _lastConfigSaveUtc >= ConfigSaveThrottle)
        {
            SaveConfig(force: true);
        }
    }
}

internal enum AdviceKind
{
    Hit,
    Stand,
    Stand21OrMore,
    DoubleElseHit,
    DoubleElseStand,
    SplitRank,
    SurrenderElseHit,
}

internal static class BasicStrategy
{
    public static (AdviceKind kind, string? param) GetAdviceKind(int total, int dealerUpcard, bool isSoft, bool isPair = false, string? pairRank = null, bool allowSurrender = false)
    {
        if (isPair && pairRank != null)
        {
            var splitAdvice = PairAdvice(pairRank, dealerUpcard);
            if (splitAdvice != null)
                return (AdviceKind.SplitRank, pairRank);
        }

        if (allowSurrender && !isPair && !isSoft)
        {
            var surrenderAdvice = SurrenderAdvice(total, dealerUpcard);
            if (surrenderAdvice)
                return (AdviceKind.SurrenderElseHit, null);
        }

        if (total >= 21) return (AdviceKind.Stand21OrMore, null);

        if (isSoft)
            return SoftTotalAdvice(total, dealerUpcard);

        return HardTotalAdvice(total, dealerUpcard);
    }

    private static bool SurrenderAdvice(int total, int d)
    {
        if (total == 16 && (d == 9 || d == 10 || d == 11)) return true;
        if (total == 15 && d == 10) return true;
        return false;
    }

    private static bool? PairAdvice(string rank, int d)
    {
        var shouldSplit = rank switch
        {
            "A" => true,
            "10" => false,
            "9" => d != 7 && d != 10 && d != 11,
            "8" => true,
            "7" => d >= 2 && d <= 7,
            "6" => d >= 2 && d <= 6,
            "5" => false,
            "4" => d == 5 || d == 6,
            "3" or "2" => d >= 2 && d <= 7,
            _ => false,
        };
        return shouldSplit ? true : null;
    }

    private static (AdviceKind, string?) HardTotalAdvice(int total, int d)
    {
        if (total <= 8) return (AdviceKind.Hit, null);
        if (total == 9) return (d >= 3 && d <= 6) ? (AdviceKind.DoubleElseHit, null) : (AdviceKind.Hit, null);
        if (total == 10) return (d <= 9) ? (AdviceKind.DoubleElseHit, null) : (AdviceKind.Hit, null);
        if (total == 11) return (AdviceKind.DoubleElseHit, null);
        if (total == 12) return (d >= 4 && d <= 6) ? (AdviceKind.Stand, null) : (AdviceKind.Hit, null);
        if (total >= 13 && total <= 16) return (d >= 2 && d <= 6) ? (AdviceKind.Stand, null) : (AdviceKind.Hit, null);
        return (AdviceKind.Stand, null);
    }

    private static (AdviceKind, string?) SoftTotalAdvice(int total, int d)
    {
        switch (total)
        {
            case 13:
            case 14:
                return (d >= 5 && d <= 6) ? (AdviceKind.DoubleElseHit, null) : (AdviceKind.Hit, null);
            case 15:
            case 16:
                return (d >= 4 && d <= 6) ? (AdviceKind.DoubleElseHit, null) : (AdviceKind.Hit, null);
            case 17:
                return (d >= 3 && d <= 6) ? (AdviceKind.DoubleElseHit, null) : (AdviceKind.Hit, null);
            case 18:
                if (d >= 3 && d <= 6) return (AdviceKind.DoubleElseStand, null);
                if (d == 2 || d == 7 || d == 8) return (AdviceKind.Stand, null);
                return (AdviceKind.Hit, null);
            default:
                return (AdviceKind.Stand, null);
        }
    }
}