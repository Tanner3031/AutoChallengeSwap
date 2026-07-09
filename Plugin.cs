using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

namespace AutoChallengeSwap
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.tanner.bloobs.autochallengeswap";
        public const string PluginName = "Auto Challenge Swap";
        public const string PluginVersion = "2.15.0";

        internal static ManualLogSource Log;

        // --- Core toggles ---
        internal static ConfigEntry<bool> ModEnabled;
        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<KeyCode> LockKey;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<bool> CheckForUpdates;
        internal static ConfigEntry<bool> PreferNearestCompletion;
        internal static ConfigEntry<string> LowPriorityTypesRaw;
        internal static ConfigEntry<string> LockedIdsRaw;
        internal static ConfigEntry<bool> PinTotalXP;
        internal static ConfigEntry<bool> AutoRepeatables;
        internal static ConfigEntry<bool> EvictManualChallenges;
        internal static ConfigEntry<bool> FillEmptyWithAmbient;
        internal static ConfigEntry<string> BackgroundFillRaw;
        internal static ConfigEntry<bool> SuppressNotifications;
        internal static ConfigEntry<bool> RespectAutoProgress;
        internal static ConfigEntry<int> AmbientIdleSeconds;

        // --- Combat ---
        internal static ConfigEntry<bool> KillSwapAtDeath;
        internal static ConfigEntry<bool> DropTotalXpInCombat;

        // --- Rule lists (advanced) ---
        internal static ConfigEntry<string> ActionTypesRaw;
        internal static ConfigEntry<string> CombatTypesRaw;
        internal static ConfigEntry<string> ExcludedSkillsRaw;
        internal static ConfigEntry<string> ExcludedTypesRaw;

        // --- One checkbox per skill, added at runtime once game data is loaded ---
        internal static readonly Dictionary<string, ConfigEntry<bool>> SkillAutoTrack =
            new Dictionary<string, ConfigEntry<bool>>(StringComparer.OrdinalIgnoreCase);
        private bool _skillsBound;

        // --- cached challenge data (populated once the game is ready) ---
        internal static ChallengeData[] AllChallenges;
        internal static Dictionary<string, ChallengeData> ById =
            new Dictionary<string, ChallengeData>(StringComparer.Ordinal);

        private const string SkillsSection = "5 - Auto-Track Skills (uncheck to exclude)";

        private void Awake()
        {
            Log = Logger;

            ModEnabled = Config.Bind("1 - General", "Enabled", true,
                "Master toggle. When off, the mod never touches your challenge slots (manage them manually).");
            ToggleKey = Config.Bind("1 - General", "ToggleKey", KeyCode.F8,
                "Hotkey to flip Enabled on/off in-game.");
            LockKey = Config.Bind("1 - General", "LockKey", KeyCode.F9,
                "Hotkey to lock/unlock the challenges currently in your slots. Locked challenges are never changed by the mod and survive restarts; the mod auto-manages only the remaining slots.");
            VerboseLogging = Config.Bind("1 - General", "VerboseLogging", false,
                "Write detailed decisions to the BepInEx log (useful while tuning).");
            CheckForUpdates = Config.Bind("1 - General", "CheckForUpdates", true,
                "On startup, check GitHub for a newer release and show an in-game notice if one exists. No files are downloaded or changed.");

            PinTotalXP = Config.Bind("2 - Behavior", "PinTotalXP", true,
                "Keep 'Gain Total Experience XP' permanently in a slot so it never gets swapped out.");
            AutoRepeatables = Config.Bind("2 - Behavior", "AutoRepeatables", true,
                "After a challenge is fully completed, keep auto-tracking its repeatable version.");
            EvictManualChallenges = Config.Bind("2 - Behavior", "EvictManualChallenges", false,
                "If false, challenges you tracked by hand are never auto-removed. If true, any slot may be swapped.");
            FillEmptyWithAmbient = Config.Bind("2 - Behavior", "FillEmptyWithAmbient", true,
                "Let non-excluded passive challenges (e.g. Travel Distance) fill EMPTY leftover slots. They never evict anything.");
            BackgroundFillRaw = Config.Bind("2 - Behavior", "BackgroundFillSkills", "SoulBinding",
                "Comma-separated skills whose XP challenges fill any leftover slot during ANY activity (even while actively skilling), not just when idle. For passive/background skills like SoulBinding that progress on their own. These bypass the idle timer and the per-skill exclusion below. They still only fill genuinely empty slots and never evict anything. Leave blank to disable.");
            PreferNearestCompletion = Config.Bind("2 - Behavior", "PreferNearestCompletion", true,
                "When challenges compete for limited slots (esp. combat), prefer the ones closest to finishing their current tier so you clear challenges (and unlock slots) faster.");
            RespectAutoProgress = Config.Bind("2 - Behavior", "RespectAutoProgress", true,
                "Integrity: only auto-track a challenge up to the tier depth your purchased 'Auto Progress' upgrade covers. Tier 1 is always allowed; deeper tiers require the matching Auto Progress level (so the mod doesn't hand out unlimited progression for free).");
            AmbientIdleSeconds = Config.Bind("2 - Behavior", "AmbientIdleSeconds", 10,
                "Passive challenges (Travel Distance, movement/Dexterity XP, etc.) only fill empty slots after this many seconds without any active skilling or combat. Prevents them popping in while you're mid-activity.");
            SuppressNotifications = Config.Bind("2 - Behavior", "SuppressNotifications", true,
                "Hide the 'Challenge Started / Abandoned' popups generated by automatic swaps.");

            KillSwapAtDeath = Config.Bind("3 - Combat", "KillSwapAtDeath", true,
                "At the instant an enemy dies, briefly track its 'Kill X' challenge so the kill counts, then revert. Lets you farm Hits/Damage AND Kills on limited slots.");
            DropTotalXpInCombat = Config.Bind("3 - Combat", "DropTotalXpInCombat", false,
                "While in combat, don't pin Total XP — frees that slot for Hit/Damage/Kill. (Total XP still applies to your other activities.)");

            ActionTypesRaw = Config.Bind("4 - Rules (advanced)", "ActionTypes",
                "Mine,Chop,Fish,Forage,Gather,Grow,Steal,Cook,Craft,Burn",
                "Comma-separated ChallengeType names that count as intentional 'actions' and drive a slot recompute.");
            CombatTypesRaw = Config.Bind("4 - Rules (advanced)", "CombatTypes", "Hit,Damage,ReduceDamage",
                "Comma-separated ChallengeType names treated as ongoing combat (tracked while fighting). ReduceDamage = block/mitigation.");
            ExcludedSkillsRaw = Config.Bind("4 - Rules (advanced)", "ExtraExcludedSkills", "",
                "Extra comma-separated skill names to exclude, on top of the per-skill checkboxes below.");
            ExcludedTypesRaw = Config.Bind("4 - Rules (advanced)", "ExcludedTypes", "",
                "Comma-separated ChallengeType names that are NEVER auto-tracked (e.g. 'Distance').");
            LowPriorityTypesRaw = Config.Bind("4 - Rules (advanced)", "LowPriorityTypes", "Heal,ReduceDamage",
                "Comma-separated ChallengeType names ranked BELOW everything else when slots are contested (the slow 'trickle' challenges).");
            LockedIdsRaw = Config.Bind("4 - Rules (advanced)", "LockedChallengeIds", "",
                "Internal: challenge IDs you've locked with the Lock hotkey. Usually managed via the hotkey, not edited by hand.");

            RebuildCaches();
            // Rebuild parsed caches AND force the next event to re-evaluate slots. Without the
            // recompute, toggling a behavior setting (e.g. RespectAutoProgress) mid-activity does
            // nothing until you switch activities, because HandleAction short-circuits on an
            // unchanged action key. Mirrors what the F8 toggle does.
            Config.SettingChanged += (_, __) => { RebuildCaches(); Patches.ForceRecomputeNext(); };

            new Harmony(PluginGuid).PatchAll(typeof(Patches));
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded. Enabled={ModEnabled.Value}, ToggleKey={ToggleKey.Value}");

            if (CheckForUpdates.Value)
                StartCoroutine(UpdateChecker.Check(PluginName, PluginVersion));
        }

        private void Update()
        {
            if (!_skillsBound) TryBindGameData();

            if (Input.GetKeyDown(ToggleKey.Value))
            {
                ModEnabled.Value = !ModEnabled.Value;
                Patches.ForceRecomputeNext();
                Log.LogInfo($"Auto Challenge Swap {(ModEnabled.Value ? "ENABLED" : "DISABLED")}");
                var mgr = ChallengeManager.Instance;
                if (mgr != null)
                {
                    string color = ModEnabled.Value ? "#66FF66" : "#FF6666";
                    string state = ModEnabled.Value ? "ON" : "OFF";
                    mgr.ShowMessage($"Auto Challenge Swap: <color={color}>{state}</color>");
                }
            }

            if (Input.GetKeyDown(LockKey.Value))
                Patches.ToggleLockCurrent();
        }

        // Discover the game's challenge list once ChallengeManager exists: cache it, build an
        // id lookup, and add one exclusion checkbox per skill.
        private void TryBindGameData()
        {
            var mgr = ChallengeManager.Instance;
            if (mgr == null) return;

            var field = AccessTools.Field(typeof(ChallengeManager), "allChallengesData");
            var all = field?.GetValue(mgr) as ChallengeData[];
            if (all == null || all.Length == 0) return; // not ready yet — retry next frame

            AllChallenges = all;
            ById = new Dictionary<string, ChallengeData>(StringComparer.Ordinal);
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in all)
            {
                if (c == null) continue;
                if (!string.IsNullOrEmpty(c.challengeId)) ById[c.challengeId] = c;
                if (!string.IsNullOrEmpty(c.skill)) names.Add(c.skill);
                if (c.type == ChallengeType.Experience && !string.IsNullOrEmpty(c.targetItemName))
                    names.Add(c.targetItemName);
            }

            foreach (var name in names)
            {
                bool defaultAllow = !string.Equals(name, "SoulBinding", StringComparison.OrdinalIgnoreCase);
                SkillAutoTrack[name] = Config.Bind(SkillsSection, name, defaultAllow,
                    $"When checked, '{name}' challenges may be auto-tracked. Uncheck to exclude them.");
            }

            _skillsBound = true;
            Log.LogInfo($"Bound game data: {all.Length} challenges, {SkillAutoTrack.Count} skill toggles.");
        }

        // --- parsed rule caches ---
        internal static HashSet<ChallengeType> ActionTypes = new HashSet<ChallengeType>();
        internal static HashSet<ChallengeType> CombatTypes = new HashSet<ChallengeType>();
        internal static HashSet<ChallengeType> ExcludedTypes = new HashSet<ChallengeType>();
        internal static HashSet<ChallengeType> LowPriorityTypes = new HashSet<ChallengeType>();
        internal static HashSet<string> ExtraExcludedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static HashSet<string> BackgroundFillSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static HashSet<string> Locked = new HashSet<string>(StringComparer.Ordinal);

        private static bool _persisting;

        private static void RebuildCaches()
        {
            ActionTypes = ParseTypes(ActionTypesRaw.Value);
            CombatTypes = ParseTypes(CombatTypesRaw.Value);
            ExcludedTypes = ParseTypes(ExcludedTypesRaw.Value);
            LowPriorityTypes = ParseTypes(LowPriorityTypesRaw.Value);
            ExtraExcludedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in Split(ExcludedSkillsRaw.Value))
                ExtraExcludedSkills.Add(s);
            BackgroundFillSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in Split(BackgroundFillRaw.Value))
                BackgroundFillSkills.Add(s);
            if (!_persisting)
            {
                Locked = new HashSet<string>(Split(LockedIdsRaw.Value), StringComparer.Ordinal);
            }
        }

        internal static void PersistLocked()
        {
            _persisting = true;
            LockedIdsRaw.Value = string.Join(",", Locked);
            _persisting = false;
        }

        private static HashSet<ChallengeType> ParseTypes(string raw)
        {
            var set = new HashSet<ChallengeType>();
            foreach (string s in Split(raw))
            {
                if (Enum.TryParse<ChallengeType>(s, true, out var t)) set.Add(t);
                else if (Log != null) Log.LogWarning($"Unknown ChallengeType in config: '{s}'");
            }
            return set;
        }

        private static IEnumerable<string> Split(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) yield break;
            foreach (string part in raw.Split(','))
            {
                string p = part.Trim();
                if (p.Length > 0) yield return p;
            }
        }

        internal static bool IsNameExcluded(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (SkillAutoTrack.TryGetValue(name, out var entry) && !entry.Value) return true;
            if (ExtraExcludedSkills.Contains(name)) return true;
            return false;
        }
    }

    // Lightweight "is there a newer release?" check against the GitHub Releases API.
    // Notify-only: it never downloads or changes any file. If the latest release tag is a
    // higher version than this build, it logs it and shows one in-game notice.
    internal static class UpdateChecker
    {
        // ---- Set GitHubOwner to your GitHub account once the repo exists. While it's empty
        //      the check is skipped entirely (no network call), so builds stay inert until set.
        private const string GitHubOwner = "Tanner3031";
        private const string GitHubRepo  = "AutoChallengeSwap";

        public static IEnumerator Check(string pluginName, string currentVersion)
        {
            if (string.IsNullOrEmpty(GitHubOwner)) yield break;

            string url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("User-Agent", "BloobsMod-UpdateCheck"); // GitHub rejects requests without one
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogInfo($"Update check skipped ({req.error}).");
                    yield break;
                }

                // Only need the release tag; pull it out directly rather than pulling in a JSON lib.
                var m = Regex.Match(req.downloadHandler.text, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success) yield break;

                string latest = m.Groups[1].Value.TrimStart('v', 'V');
                if (!(Version.TryParse(latest, out var newV) && Version.TryParse(currentVersion, out var curV) && newV > curV))
                    yield break;

                string releasesUrl = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";
                Plugin.Log.LogWarning($"{pluginName}: update available — v{latest} (you have v{currentVersion}). {releasesUrl}");

                // Wait until the game UI / EventSystem exists, then show a persistent clickable notice.
                float t = 0f;
                while (ChallengeManager.Instance == null && t < 90f) { t += Time.unscaledDeltaTime; yield return null; }
                UpdateNotice.Show(
                    $"<b>{pluginName}</b> update available: <color=#FFE066>v{latest}</color>\n" +
                    "<size=80%><color=#9FB3C8>Left-click to open GitHub  ·  right-click or ✕ to dismiss</color></size>",
                    releasesUrl);
            }
        }
    }

    // A persistent, clickable "update available" panel. Unlike the game's transient toast it
    // stays on screen until the user dismisses it: left-click opens the GitHub releases page,
    // right-click or the ✕ button closes it. It builds its own top-most overlay canvas so it
    // doesn't depend on any game UI.
    internal class UpdateNotice : MonoBehaviour, IPointerClickHandler
    {
        private const string RootName = "BloobsModUpdateNotice";
        private string _url;

        // Count notices already on screen. Every Bloobs mod names its notice root the same, so
        // this also sees notices from a sibling mod and lets the new one stack below instead of
        // overlapping it.
        private static int CountExisting()
        {
            int n = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
                if (c != null && c.gameObject != null && c.gameObject.name == RootName) n++;
            return n;
        }

        public static void Show(string message, string url)
        {
            try
            {
                // Clicks need an EventSystem; the game has one, but create a fallback just in case.
                if (EventSystem.current == null)
                {
                    var es = new GameObject("BloobsModEventSystem",
                        typeof(EventSystem), typeof(StandaloneInputModule));
                    DontDestroyOnLoad(es);
                }

                int slot = CountExisting();
                var go = new GameObject(RootName);
                DontDestroyOnLoad(go);

                var canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30000; // above game UI
                var scaler = go.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                go.AddComponent<GraphicRaycaster>();

                var notice = go.AddComponent<UpdateNotice>();
                notice._url = url;
                notice.Build(message, slot);
            }
            catch (Exception ex) { Plugin.Log.LogError("UpdateNotice failed: " + ex); }
        }

        private void Build(string message, int slot)
        {
            TMP_FontAsset font = ResolveFont();

            // Panel, pinned to the top-right corner. This is the click surface; because this
            // component (IPointerClickHandler) sits on the parent canvas, clicks on the panel
            // bubble up to OnPointerClick, while the ✕ button (its own handler) does not.
            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(transform, false);
            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(1f, 1f);
            prt.anchoredPosition = new Vector2(-24f, -24f - slot * 108f);
            prt.sizeDelta = new Vector2(390f, 96f);
            panel.GetComponent<Image>().color = new Color(0.07f, 0.09f, 0.13f, 0.97f);

            // Accent bar down the left edge.
            var accent = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accent.transform.SetParent(panel.transform, false);
            var art = accent.GetComponent<RectTransform>();
            art.anchorMin = new Vector2(0f, 0f); art.anchorMax = new Vector2(0f, 1f);
            art.pivot = new Vector2(0f, 0.5f);
            art.sizeDelta = new Vector2(5f, 0f); art.anchoredPosition = Vector2.zero;
            var ai = accent.GetComponent<Image>();
            ai.color = new Color(0.40f, 0.80f, 1f, 1f);
            ai.raycastTarget = false;

            // Message text.
            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(panel.transform, false);
            var trt = textGo.GetComponent<RectTransform>();
            trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(1f, 1f);
            trt.offsetMin = new Vector2(16f, 8f); trt.offsetMax = new Vector2(-30f, -8f);
            var txt = textGo.AddComponent<TextMeshProUGUI>();
            if (font != null) txt.font = font;
            txt.fontSize = 18f;
            txt.color = Color.white;
            txt.alignment = TextAlignmentOptions.Left;
            txt.richText = true;
            txt.raycastTarget = false; // let the panel receive the click
            txt.text = message;

            // Close (✕) button, top-right. Its own click handler dismisses without opening the URL.
            var closeGo = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
            closeGo.transform.SetParent(panel.transform, false);
            var crt = closeGo.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1f, 1f);
            crt.anchoredPosition = new Vector2(-5f, -5f);
            crt.sizeDelta = new Vector2(22f, 22f);
            closeGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.08f);
            closeGo.GetComponent<Button>().onClick.AddListener(Dismiss);

            var xGo = new GameObject("X", typeof(RectTransform));
            xGo.transform.SetParent(closeGo.transform, false);
            var xrt = xGo.GetComponent<RectTransform>();
            xrt.anchorMin = Vector2.zero; xrt.anchorMax = Vector2.one;
            xrt.offsetMin = Vector2.zero; xrt.offsetMax = Vector2.zero;
            var xtxt = xGo.AddComponent<TextMeshProUGUI>();
            if (font != null) xtxt.font = font;
            xtxt.text = "✕";
            xtxt.fontSize = 15f;
            xtxt.color = new Color(1f, 1f, 1f, 0.85f);
            xtxt.alignment = TextAlignmentOptions.Center;
            xtxt.raycastTarget = false;
        }

        private static TMP_FontAsset ResolveFont()
        {
            var f = TMP_Settings.defaultFontAsset;
            if (f != null) return f;
            foreach (var t in Resources.FindObjectsOfTypeAll<TMP_Text>())
                if (t != null && t.font != null) return t.font;
            return null;
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Right) { Dismiss(); return; }
            if (e.button == PointerEventData.InputButton.Left && !string.IsNullOrEmpty(_url))
                Application.OpenURL(_url);
        }

        private void Dismiss()
        {
            if (this != null && gameObject != null) Destroy(gameObject);
        }
    }

    internal static class Patches
    {
        // The action target currently reflected in the slots, e.g. "Mine:Mithril".
        private static string _currentActionKey;

        // IDs the mod itself placed. Anything NOT here is a manual pick, protected unless
        // EvictManualChallenges is on.
        private static readonly HashSet<string> _modManaged = new HashSet<string>();

        // Combat recency: challengeId -> Time.time last seen. Used to know which combat
        // challenges are "active" right now (adapts to your selected style automatically).
        private static readonly Dictionary<string, float> _combatSeen = new Dictionary<string, float>();
        private static string _combatSignature;
        private const float CombatWindow = 4f;

        // Last time a real skilling/combat action produced progress. Passive challenges are
        // gated until you've been idle from these for AmbientIdleSeconds.
        private static float _lastPrimaryTime = -9999f;

        // Slots restored from a save look like manual picks (the mod didn't place them this
        // session). Adopt them the first time the mod acts so it can reorganize them.
        private static bool _adopted;

        internal static bool Suppressing;

        internal static void ForceRecomputeNext()
        {
            _currentActionKey = null;
            _combatSignature = null;
        }

        private static void Log(string msg)
        {
            if (Plugin.VerboseLogging.Value) Plugin.Log.LogInfo(msg);
        }

        private static void AdoptExisting(ChallengeManager mgr)
        {
            if (_adopted) return;
            _adopted = true;
            foreach (var t in mgr.TrackedChallenges)
                if (t != null && !Plugin.Locked.Contains(t.challengeId)) _modManaged.Add(t.challengeId);
            Log($"Adopted {_modManaged.Count} pre-existing tracked challenge(s) as mod-managed.");
        }

        // Fraction (0..1) of the current tier's requirement already met — higher = closer to done.
        private static float CompletionFraction(ChallengeManager mgr, ChallengeData c)
        {
            double progress = mgr.GetProgress(c.challengeId);
            TierData tier;
            double required;
            if (mgr.IsRepeatable(c.challengeId))
            {
                tier = c.LastTier;
                int rc = Math.Min(mgr.GetRepeatCount(c.challengeId), 20);
                required = tier != null ? ChallengeManager.ComputeRepeatableRequirement(tier.requiredAmount, rc) : 0.0;
            }
            else
            {
                tier = c.GetTier(mgr.GetCurrentTierIndex(c.challengeId));
                required = tier != null ? tier.requiredAmount : 0.0;
            }
            if (required <= 0.0) return 0f;
            return Mathf.Clamp01((float)(progress / required));
        }

        private static bool IsLowPriority(ChallengeData c) => Plugin.LowPriorityTypes.Contains(c.type);

        // Order best-first: non-trickle types first, then (optionally) nearest-completion.
        private static List<ChallengeData> Prioritize(ChallengeManager mgr, IEnumerable<ChallengeData> items)
        {
            var q = items.OrderByDescending(c => !IsLowPriority(c));
            if (Plugin.PreferNearestCompletion.Value)
                q = q.ThenByDescending(c => CompletionFraction(mgr, c));
            return q.ToList();
        }

        // Lock/unlock the challenges currently in your slots (persisted across restarts).
        internal static void ToggleLockCurrent()
        {
            var mgr = ChallengeManager.Instance;
            if (mgr == null) return;
            var current = mgr.TrackedChallenges.Where(t => t != null).Select(t => t.challengeId).ToList();
            if (current.Count == 0) { mgr.ShowMessage("No challenges to lock."); return; }

            bool allLocked = current.All(id => Plugin.Locked.Contains(id));
            if (allLocked)
            {
                foreach (var id in current) Plugin.Locked.Remove(id);
                mgr.ShowMessage($"<color=#FFCC66>Unlocked</color> {current.Count} challenge(s) — auto-swap resumed.");
            }
            else
            {
                foreach (var id in current) { Plugin.Locked.Add(id); _modManaged.Remove(id); }
                mgr.ShowMessage($"<color=#66CCFF>Locked</color> {current.Count} challenge(s) — the mod won't change these.");
            }
            Plugin.PersistLocked();
            _currentActionKey = null;
            _combatSignature = null;
        }

        // ---- Mute swap-generated popups --------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChallengeManager), nameof(ChallengeManager.ShowMessage))]
        private static bool ShowMessage_Prefix()
        {
            return !(Suppressing && Plugin.SuppressNotifications.Value);
        }

        // ---- Save load: reset per-session state + flag the save as modded ----------
        // The game reloads the scene when switching saves/characters, destroying the
        // ChallengeManager while this plugin (and its static state) persists. Without a
        // reset, the previous character's mod-managed IDs, adopted flag, and combat
        // recency would leak into the new one and drive wrong keep/evict decisions.
        // LoadPlayerData runs exactly once per character load (playerDataManager.Start),
        // so it's the correct reset point. SetModded is idempotent and persists the flag;
        // patching the load makes it stick (setting it earlier is overwritten by the save's
        // own "IsModded" read).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(playerDataManager), nameof(playerDataManager.LoadPlayerData))]
        private static void LoadPlayerData_Postfix(playerDataManager __instance)
        {
            ResetSessionState();
            try { __instance?.SetModded(); }
            catch (Exception ex) { Plugin.Log.LogError("SetModded failed: " + ex); }
        }

        // Drop all per-character session state so a newly loaded save starts clean.
        internal static void ResetSessionState()
        {
            _currentActionKey = null;
            _combatSignature = null;
            _combatSeen.Clear();
            _modManaged.Clear();
            _ladderCache.Clear();
            _adopted = false;
            _lastPrimaryTime = -9999f;
        }

        // ---- Every activity funnels through AddProgressInternal --------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChallengeManager), "AddProgressInternal")]
        private static void AddProgressInternal_Postfix(ChallengeManager __instance,
            ChallengeType type, string target, double amount, string skill, string category)
        {
            try
            {
                if (!Plugin.ModEnabled.Value) return;
                if (amount <= 0.0 || string.IsNullOrEmpty(target)) return;
                if (__instance.TutorialLockActive) return;

                if (type == ChallengeType.Kill)
                    return; // kills are owned by the Die() hook — don't recompute here

                bool isCombat = Plugin.CombatTypes.Contains(type);
                bool isAction = Plugin.ActionTypes.Contains(type);
                if (isCombat || isAction) _lastPrimaryTime = Time.time;

                if (isCombat)
                    HandleCombat(__instance, type, target, skill);
                else if (isAction)
                    HandleAction(__instance, type, target);
                else if (Plugin.FillEmptyWithAmbient.Value)
                    HandleAmbient(__instance, type, target);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("AutoChallengeSwap error: " + ex);
            }
        }

        // ---- A tracked challenge just finished a tier -------------------------------
        // On tier completion the game REMOVES the challenge from your slots unless it can
        // auto-progress it (ChallengeManager.CompleteTier: _tracked.RemoveAll(...)). This is
        // the norm under RespectAutoProgress: the tier advances past what your Auto Progress
        // upgrade covers, so the game frees the slot. HandleAction/HandleCombat short-circuit
        // on an unchanged action key, so without this the freed slot sits empty until you
        // switch activities (or toggle a setting). Invalidate the cached keys here so the very
        // next progress event refills. This postfix runs BEFORE AddProgressInternal_Postfix in
        // the same call stack (CompleteTier is called from inside AddProgressInternal), so the
        // same smithing/combat tick that completed the tier immediately recomputes the slots.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChallengeManager), "CompleteTier")]
        private static void CompleteTier_Postfix()
        {
            if (!Plugin.ModEnabled.Value) return;
            ForceRecomputeNext();
        }

        // ---- Kill credit: track the dying enemy's Kill challenge just before Die() credits it
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BasicEnemy), "Die")]
        private static void Die_Prefix(BasicEnemy __instance)
        {
            try
            {
                if (!Plugin.ModEnabled.Value || !Plugin.KillSwapAtDeath.Value) return;
                var mgr = ChallengeManager.Instance;
                if (mgr == null || mgr.TutorialLockActive) return;
                if (__instance == null || __instance.GetCurrentHealth() > 0f) return; // not actually dying

                string name = __instance.gameObject.name;
                // Every creature has 4 kill variants (base / Superior / Golden / Corrupted),
                // and the game credits only the challenge whose category matches the exact
                // variant killed. Pre-track by name AND category so the right one counts.
                ChallengeData kill = FindKillChallenge(name, KillCategory(__instance));
                if (kill == null || !IsTrackable(mgr, kill)) return;
                if (mgr.IsTracked(kill.challengeId)) return;
                if (mgr.GetConflictingTracked(kill) != null) return;
                AdoptExisting(mgr);

                DoSuppressed(() =>
                {
                    if (mgr.TrackedCount >= mgr.MaxTrackedCount)
                    {
                        ChallengeData victim = PickKillBorrow(mgr);
                        if (victim == null) return; // only manual picks left — respect them
                        mgr.Abandon(victim);
                        _modManaged.Remove(victim.challengeId);
                    }
                    if (mgr.TryTrack(kill)) _modManaged.Add(kill.challengeId);
                });
                // Force the next action/combat event to rebuild, restoring the borrowed slot.
                _currentActionKey = null;
                _combatSignature = null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Kill-swap error: " + ex);
            }
        }

        // Choose which slot to lend to a kill: the pinned Total XP first, else the
        // least-recently-active combat slot, else any mod-managed slot.
        private static ChallengeData PickKillBorrow(ChallengeManager mgr)
        {
            ChallengeData total = null, leastComplete = null;
            float worst = float.MaxValue;
            foreach (var t in mgr.TrackedChallenges)
            {
                if (t == null || !_modManaged.Contains(t.challengeId)) continue; // never borrow locked/manual
                if (t.type == ChallengeType.Experience &&
                    string.Equals(t.targetItemName, "Total Experience", StringComparison.OrdinalIgnoreCase))
                    total = t;
                float frac = CompletionFraction(mgr, t);
                if (frac < worst) { worst = frac; leastComplete = t; }
            }
            return total ?? leastComplete; // prefer lending the Total XP slot, else the furthest-from-done
        }

        // ---- Combat: keep the actively-progressing Hit/Damage challenges tracked -----
        private static void HandleCombat(ChallengeManager mgr, ChallengeType type, string target, string skill)
        {
            ChallengeData c = FindChallenge(type, target, skill);
            if (c == null) { Log($"Combat: no challenge for {type}/{target}/{skill}"); return; }
            if (!IsTrackable(mgr, c)) { Log($"Combat: '{c.challengeId}' not trackable (excluded/completed)"); return; }
            AdoptExisting(mgr);

            float now = Time.time;
            _combatSeen[c.challengeId] = now;

            // Active combat challenges = those seen within the window.
            var active = _combatSeen
                .Where(kv => now - kv.Value <= CombatWindow)
                .Select(kv => Plugin.ById.TryGetValue(kv.Key, out var cc) ? cc : null)
                .Where(cc => cc != null && IsTrackable(mgr, cc))
                .ToList();

            if (Plugin.PinTotalXP.Value && !Plugin.DropTotalXpInCombat.Value)
            {
                ChallengeData total = mgr.GetChallengeByTarget("Total Experience", ChallengeType.Experience);
                if (total != null && IsTrackable(mgr, total)) active.Add(total);
            }

            if (active.Count == 0) return;

            // Rank so the best challenges win contested slots.
            var desired = Prioritize(mgr, active);

            // Passive background skills (e.g. SoulBinding) fill any slot combat leaves open.
            AppendBackgroundFills(mgr, desired);

            string sig = string.Join(",", desired.Select(d => d.challengeId));
            if (sig == _combatSignature) return; // nothing changed — avoid per-swing churn
            _combatSignature = sig;
            _currentActionKey = null; // leaving/!action — next real action will recompute

            Log($"Combat recompute → [{string.Join(", ", desired.Select(d => d.challengeId))}]");
            ApplyDesired(mgr, desired);
        }

        // An intentional action (mining an ore, chopping a tree, ...) → recompute slots.
        private static void HandleAction(ChallengeManager mgr, ChallengeType type, string target)
        {
            string key = type + ":" + target;
            if (key == _currentActionKey) return;

            ChallengeData action = mgr.GetChallengeByTarget(target, type);
            if (action == null) return;
            AdoptExisting(mgr);

            var desired = new List<ChallengeData>();
            AddIfTrackable(mgr, action, desired);

            if (!string.IsNullOrEmpty(action.skill))
                AddIfTrackable(mgr, mgr.GetChallengeByTarget(action.skill, ChallengeType.Experience), desired);

            // Total XP is always offered LAST (lowest priority). When PinTotalXP is on it's
            // effectively guaranteed a slot; when off it still fills a genuinely open slot but
            // always yields to the real action/skill challenges above it (ApplyDesired fills
            // only within free budget and won't evict manual picks).
            AddIfTrackable(mgr, mgr.GetChallengeByTarget("Total Experience", ChallengeType.Experience), desired);

            // Fill any slot still open after the activity's own challenges with passive
            // background skills (e.g. SoulBinding). Lowest priority, free-budget only.
            AppendBackgroundFills(mgr, desired);

            if (desired.Count == 0) return;

            _currentActionKey = key;
            _combatSignature = null;
            ApplyDesired(mgr, desired);
        }

        // Passive progress (movement, off-skill XP): only fill empty slots, never evict, and
        // only once you've been idle from real activities for AmbientIdleSeconds.
        private static void HandleAmbient(ChallengeManager mgr, ChallengeType type, string target)
        {
            // Background-fill skills (e.g. SoulBinding) skip the idle wait — they progress
            // passively and never compete with what you're actively doing.
            bool isBackground = type == ChallengeType.Experience && Plugin.BackgroundFillSkills.Contains(target);
            if (!isBackground && Time.time - _lastPrimaryTime < Plugin.AmbientIdleSeconds.Value) return;
            if (mgr.TrackedCount >= mgr.MaxTrackedCount) return;

            ChallengeData c = mgr.GetChallengeByTarget(target, type);
            if (c == null) return;
            // Background fill bypasses the per-skill exclusion (listing it is the opt-in);
            // everything else must pass the normal trackable test.
            if (isBackground ? !IsFillable(mgr, c) : !IsTrackable(mgr, c)) return;
            if (mgr.IsTracked(c.challengeId)) return;
            if (mgr.GetConflictingTracked(c) != null) return;

            DoSuppressed(() => { if (mgr.TryTrack(c)) _modManaged.Add(c.challengeId); });
        }

        // The XP challenges for the configured background-fill skills, in list order, that are
        // currently fillable and not already in `desired`. Appended as lowest priority so they
        // only ever land in a genuinely open slot (ApplyDesired fills within free budget).
        private static void AppendBackgroundFills(ChallengeManager mgr, List<ChallengeData> desired)
        {
            if (Plugin.BackgroundFillSkills.Count == 0) return;
            var have = new HashSet<string>(desired.Select(d => d.challengeId));
            foreach (string skill in Plugin.BackgroundFillSkills)
            {
                var c = mgr.GetChallengeByTarget(skill, ChallengeType.Experience);
                if (c != null && IsFillable(mgr, c) && have.Add(c.challengeId))
                    desired.Add(c);
            }
        }

        // Make the tracked set match `desired` (priority order), within budget, without
        // disturbing the player's manual picks.
        private static void ApplyDesired(ChallengeManager mgr, List<ChallengeData> desired)
        {
            var desiredIds = new HashSet<string>(desired.Select(d => d.challengeId));

            DoSuppressed(() =>
            {
                foreach (var t in new List<ChallengeData>(mgr.TrackedChallenges))
                {
                    if (t == null || desiredIds.Contains(t.challengeId)) continue;
                    if (Plugin.Locked.Contains(t.challengeId)) { Log($"  keep (locked): {t.challengeId}"); continue; }
                    if (!_modManaged.Contains(t.challengeId)) { Log($"  keep (manual): {t.challengeId}"); continue; }
                    mgr.Abandon(t);
                    _modManaged.Remove(t.challengeId);
                    Log($"  abandon: {t.challengeId}");
                }

                foreach (var d in desired)
                {
                    if (mgr.IsTracked(d.challengeId)) continue;
                    if (mgr.GetConflictingTracked(d) != null) { Log($"  conflict, skip: {d.challengeId}"); continue; }

                    if (mgr.TrackedCount >= mgr.MaxTrackedCount)
                    {
                        if (!Plugin.EvictManualChallenges.Value) { Log($"  full (manual slots protected), can't add: {d.challengeId}"); break; }
                        var victim = mgr.TrackedChallenges.FirstOrDefault(t =>
                            t != null && !desiredIds.Contains(t.challengeId) && !Plugin.Locked.Contains(t.challengeId));
                        if (victim == null) break;
                        mgr.Abandon(victim);
                        _modManaged.Remove(victim.challengeId);
                        Log($"  evict manual: {victim.challengeId}");
                    }

                    if (mgr.TryTrack(d)) { _modManaged.Add(d.challengeId); Log($"  track: {d.challengeId}"); }
                    else Log($"  TryTrack FAILED: {d.challengeId}");
                }
            });
        }

        // Combat challenges form a ladder that shares a category+skill: e.g. every "Deal Ranged
        // Damage" challenge is type=Damage, category="Damage", skill="Ranged", differing only by
        // the skill level it unlocks at (Lvl 1, 26, 51, ...). The game credits progress to ALL
        // tracked matches; this decides which one to track. Per design we climb the ladder: track
        // the HIGHEST challenge the player has UNLOCKED (so as new rungs unlock the mod moves up
        // to them, chasing the biggest long-term payout), preferring one that still has tiers left
        // over a maxed/repeatable one. Locked challenges are never returned, so the mod can't grant
        // progress the player hasn't earned. (The old logic returned the FIRST category+skill match
        // and then camped/looped it forever — it never advanced to higher siblings as they unlocked.)
        private static ChallengeData FindChallenge(ChallengeType type, string target, string skill)
        {
            if (Plugin.AllChallenges == null)
                return ChallengeManager.Instance?.GetChallengeByTarget(target, type);

            var mgr = ChallengeManager.Instance;
            var pdm = playerDataManager.Instance;

            ChallengeData bestIncomplete = null, bestRepeatable = null;
            foreach (var c in GetLadder(type, target, skill))
            {
                // Unlocked (mirrors the game's Locked gate) and otherwise trackable
                // (exclusions / RespectAutoProgress / not a maxed non-repeatable).
                if (!ChallengeUIManager.MeetsLevelRequirement(pdm, c)) continue;
                if (mgr != null && !IsTrackable(mgr, c)) continue;

                if (mgr != null && mgr.IsFullyCompleted(c.challengeId))
                {
                    if (bestRepeatable == null || c.levelRequired > bestRepeatable.levelRequired) bestRepeatable = c;
                }
                else if (bestIncomplete == null || c.levelRequired > bestIncomplete.levelRequired)
                {
                    bestIncomplete = c;
                }
            }
            // Highest unlocked with tiers remaining; else highest unlocked repeatable; else none
            // (bail rather than track a locked or maxed-non-repeatable challenge).
            return bestIncomplete ?? bestRepeatable;
        }

        // All challenges sharing a (type, category/target, skill) ladder. The membership never
        // changes during a session (it's static game data), so we scan the full list once per
        // ladder and reuse it — the per-hit work then only touches the ~12 siblings, not every
        // challenge in the game. Cleared on save load (ResetSessionState) for safety.
        private static readonly Dictionary<string, List<ChallengeData>> _ladderCache =
            new Dictionary<string, List<ChallengeData>>();

        private static List<ChallengeData> GetLadder(ChallengeType type, string target, string skill)
        {
            string key = (int)type + "|" + (target ?? "") + "|" + (skill ?? "");
            if (_ladderCache.TryGetValue(key, out var list)) return list;

            list = new List<ChallengeData>();
            var all = Plugin.AllChallenges;
            if (all != null)
                foreach (var c in all)
                {
                    if (c == null || c.type != type || !NameMatches(c, target)) continue;
                    // Combat is style-specific (melee Hit=Attack/Damage=Strength, ranged=Ranged,
                    // magic=Magic); only match the skill that produced this progress. Mitigation
                    // passes skill=null and category-only challenges have empty skill — both match.
                    if (!string.IsNullOrEmpty(skill) && !string.IsNullOrEmpty(c.skill) &&
                        !c.skill.Equals(skill, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(c);
                }
            _ladderCache[key] = list;
            return list;
        }

        // The kill category the game will credit — mirrors BasicEnemy.GetKillCategory exactly.
        // "Enemy"/"Boss" for base, plus "_Superior"/"_Golden"/"_Corrupted" for rare variants.
        private static string KillCategory(BasicEnemy enemy)
        {
            string baseCat = enemy.bossMessage ? "Boss" : "Enemy";
            switch (enemy.SuperiorType)
            {
                case SuperiorType.Superior:  return baseCat + "_Superior";
                case SuperiorType.Golden:    return baseCat + "_Golden";
                case SuperiorType.Corrupted: return baseCat + "_Corrupted";
                default:                     return baseCat;
            }
        }

        // The Kill challenge matching BOTH the enemy name and the exact kill category, mirroring
        // the game's Matches + MatchesCategory so we pre-track the same challenge Die() will credit.
        private static ChallengeData FindKillChallenge(string name, string category)
        {
            var all = Plugin.AllChallenges;
            if (all == null) return null;
            foreach (var c in all)
            {
                if (c == null || c.type != ChallengeType.Kill) continue;
                if (!NameMatches(c, name)) continue;
                if (!string.Equals(c.category, category, StringComparison.OrdinalIgnoreCase)) continue;
                return c;
            }
            return null;
        }

        // Mirrors ChallengeManager.Matches exactly, including the category fallback that
        // combat challenges (empty targetItemName, category "Accuracy"/"Damage") rely on.
        private static readonly Regex BracketRangeSuffix = new Regex(@"\(\d+-\d+\)$", RegexOptions.Compiled);

        private static bool NameMatches(ChallengeData c, string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            if (!string.IsNullOrEmpty(c.targetItemName) &&
                c.targetItemName.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
            if (c.aliases != null)
                foreach (var a in c.aliases)
                    if (!string.IsNullOrEmpty(a) && a.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrEmpty(c.targetItemName))
                return string.Equals(c.category, target, StringComparison.OrdinalIgnoreCase);
            return BracketRangeSuffix.IsMatch(c.targetItemName) &&
                   string.Equals(c.category, target, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddIfTrackable(ChallengeManager mgr, ChallengeData c, List<ChallengeData> list)
        {
            if (c != null && IsTrackable(mgr, c)) list.Add(c);
        }

        private static bool IsTrackable(ChallengeManager mgr, ChallengeData c)
        {
            if (IsExcluded(c)) return false;
            string id = c.challengeId;
            if (mgr.IsFullyCompleted(id))
            {
                if (!mgr.IsRepeatable(id)) return false;
                if (!Plugin.AutoRepeatables.Value) return false;
            }
            if (!AutoProgressAllows(mgr, c)) return false;
            return true;
        }

        // Like IsTrackable but ignores the per-skill NAME exclusion — used for background-fill
        // skills, whose presence in the BackgroundFillSkills list is itself the opt-in. Still
        // respects type exclusion, completion/repeatable rules, and the Auto Progress depth cap.
        private static bool IsFillable(ChallengeManager mgr, ChallengeData c)
        {
            if (Plugin.ExcludedTypes.Contains(c.type)) return false;
            string id = c.challengeId;
            if (mgr.IsFullyCompleted(id))
            {
                if (!mgr.IsRepeatable(id)) return false;
                if (!Plugin.AutoRepeatables.Value) return false;
            }
            return AutoProgressAllows(mgr, c);
        }

        // Integrity: don't let the mod auto-track a challenge deeper than the player's purchased
        // "Auto Progress" upgrade would carry it. Tier 1 (index 0) is always allowed; reaching
        // tier index N requires Auto Progress level >= N. Repeatables are always allowed (the
        // game auto-enters those regardless of level).
        private static bool AutoProgressAllows(ChallengeManager mgr, ChallengeData c)
        {
            if (!Plugin.RespectAutoProgress.Value) return true;
            var up = ChallengeAutoProgressUpgrade.Instance;
            if (up == null) return true;
            if (mgr.IsRepeatable(c.challengeId)) return true;
            return mgr.GetCurrentTierIndex(c.challengeId) <= up.AutoProgressLevel;
        }

        private static bool IsExcluded(ChallengeData c)
        {
            if (Plugin.ExcludedTypes.Contains(c.type)) return true;
            if (Plugin.IsNameExcluded(c.skill)) return true;
            if (c.type == ChallengeType.Experience && Plugin.IsNameExcluded(c.targetItemName)) return true;
            return false;
        }

        private static void DoSuppressed(Action action)
        {
            bool prev = Suppressing;
            Suppressing = true;
            try { action(); }
            finally { Suppressing = prev; }
        }
    }
}
