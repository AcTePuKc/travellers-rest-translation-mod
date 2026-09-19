using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using TMPro;

namespace TravellersRestEmployeeRefresh;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.travellersrest.employee-refresh";
    public const string PluginName = "Travellers Rest EmployeeRefresh";
    public const string PluginVersion = "0.1.1";

    private static readonly List<string> MaleFirstNames = new List<string>();
    private static readonly List<string> FemaleFirstNames = new List<string>();
    private static readonly List<string> MaleSurnames = new List<string>();
    private static readonly List<string> FemaleSurnames = new List<string>();
    private static readonly List<string> SharedSurnames = new List<string>();
    private static readonly Dictionary<string, string> UiLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static ManualLogSource log;
    private static ConfigEntry<bool> enableNameOverrides;
    private static ConfigEntry<bool> enableCandidateRefresh;
    private static ConfigEntry<int> refreshLimitPerDay;
    private static ConfigEntry<string> employeeNamesFile;
    private static ConfigEntry<int> maxNameLength;
    private static ConfigEntry<KeyboardShortcut> refreshHotkey;
    private static ConfigEntry<bool> showButton;
    private static ConfigEntry<string> buttonLabel;
    private static ConfigEntry<int> buttonGap;
    private static ConfigEntry<float> heightMultiplier;
    private static ConfigEntry<bool> growUpward;
    private static ConfigEntry<float> verticalOffset;
    private static readonly Dictionary<int, WindowResizeState> WindowResizeStates = new Dictionary<int, WindowResizeState>();
    private static RectTransform refreshButtonRect;
    private static RectTransform refreshTemplateRect;
    private static string translationsDirectory;
    private static int nameReloadPending;
    private static bool activeNamePoolIsRtl;
    private static Func<string, int, bool, string> rtlFixMethod;
    private static bool rtlFixLookupComplete;
    private static int refreshesToday;
    private static int observedWorldTimeInstanceId;
    private static readonly HashSet<string> RightToLeftLanguageCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "fa", "he", "ji", "ur"
    };

    private void Awake()
    {
        log = Logger;
        enableNameOverrides = Config.Bind("Features", "EnableNameOverrides", false, "Use the selected employee-name file instead of the game's generated names. Disabled by default so this mod can be used only for the window layout.");
        enableCandidateRefresh = Config.Bind("Features", "EnableCandidateRefresh", true, "Enable the Refresh button, hotkey, and refresh after a name-file change.");
        refreshLimitPerDay = Config.Bind("Features", "RefreshLimitPerDay", 0, "Maximum manual Refresh uses per in-game day. 0 means unlimited. The counter resets when the game raises WorldTime.OnNextDay.");
        employeeNamesFile = Config.Bind("General", "EmployeeNamesFile", "employee-names.bg.txt", "Name pool inside this mod's translations folder.");
        maxNameLength = Config.Bind("General", "MaxNameLength", 9, "Warn when a first name or surname is longer than this many characters. Longer names are not silently truncated.");
        refreshHotkey = Config.Bind("General", "RefreshHotkey", KeyboardShortcut.Deserialize("F6"), "Regenerate staff candidates while the hire-staff panel is open. Clear the value to disable the hotkey. Requires EnableCandidateRefresh.");
        showButton = Config.Bind("General", "ShowButton", true, "Show a refresh button using the game's own staff-button template and icon. Requires EnableCandidateRefresh.");
        buttonLabel = Config.Bind("General", "ButtonLabel", "Refresh", "Final fallback when the selected employee-names file has no [UI] RefreshButton entry.");
        buttonGap = Config.Bind("General", "ButtonGap", 12, "Horizontal gap between the game's staff button and the refresh button.");
        heightMultiplier = Config.Bind("Window", "HeightMultiplier", 1f, "Height multiplier for the staff hiring window. 1.25 means 125% of the original height; 1 keeps the vanilla size.");
        growUpward = Config.Bind("Window", "GrowUpward", true, "Keep the lower edge stable while increasing the window height.");
        verticalOffset = Config.Bind("Window", "VerticalOffset", 0f, "Move the staff hiring window upward by this many UI units. 0 keeps the vanilla position.");

        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Paths.PluginPath;
        translationsDirectory = Path.Combine(pluginDir, "translations");
        // UI labels come from the selected file even when name overrides are
        // disabled, so the mod's own controls remain independently localizable.
        LoadEmployeeNames(Path.Combine(translationsDirectory, employeeNamesFile.Value));
        if (!enableNameOverrides.Value)
            log.LogInfo("Employee name overrides are disabled; keeping the game's generated names.");
        employeeNamesFile.SettingChanged += (_, __) => Interlocked.Exchange(ref nameReloadPending, 1);
        enableNameOverrides.SettingChanged += (_, __) => Interlocked.Exchange(ref nameReloadPending, 1);
        log.LogInfo($"Refresh hotkey: {refreshHotkey.Value}");

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin));
        var method = AccessTools.Method(typeof(EmployeeInfo), "HHNGPPLGHML");
        if (method == null)
        {
            log.LogWarning("Employee name generation method was not found; keeping game names.");
        }
        else
        {
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(Plugin), nameof(EmployeeNamePostfix)));
            log.LogInfo($"Employee name generation patch applied: {method.FullDescription()}");
        }

        var nextDayMethod = AccessTools.Method(typeof(WorldTime), "SetNextDay");
        if (nextDayMethod != null)
        {
            harmony.Patch(nextDayMethod, postfix: new HarmonyMethod(typeof(Plugin), nameof(WorldTime_SetNextDay_Postfix)));
            log.LogInfo($"Daily Refresh reset patch applied: {nextDayMethod.FullDescription()}");
        }
        else
        {
            log.LogWarning("WorldTime.SetNextDay was not found; daily Refresh will not reset after sleep.");
        }

        var loadGameMethod = AccessTools.Method(typeof(Save), "LoadGame");
        if (loadGameMethod != null)
        {
            harmony.Patch(loadGameMethod, postfix: new HarmonyMethod(typeof(Plugin), nameof(Save_LoadGame_Postfix)));
            log.LogInfo($"Daily Refresh reload reset patch applied: {loadGameMethod.FullDescription()}");
        }
        else
        {
            log.LogWarning("Save.LoadGame was not found; daily Refresh may remain spent after reloading a save.");
        }

        log.LogInfo($"Employee refresh generator found: {refreshMethod}");
    }

    private static bool refreshMethod => AccessTools.Method(typeof(StaffManager), "CreateRandomOptionsWorkers") != null;

    private void Update()
    {
        ObserveWorldTimeInstance();

        var hireUi = HireStaffUI.GKFMIJGIONI(0);
        if (hireUi == null) return;

        SyncRefreshButton(hireUi);

        ApplyWindowResize(hireUi);
        ProcessPendingNameReload();
    }

    private static void ObserveWorldTimeInstance()
    {
        var worldTime = UnityEngine.Object.FindObjectOfType(typeof(WorldTime)) as UnityEngine.Object;
        if (worldTime == null) return;
        var instanceId = worldTime.GetInstanceID();
        if (observedWorldTimeInstanceId != 0 && observedWorldTimeInstanceId != instanceId)
        {
            ResetDailyRefreshLimit();
            log.LogInfo("Daily employee Refresh limit reset after WorldTime was recreated while loading a save.");
        }
        observedWorldTimeInstanceId = instanceId;
    }

    private static void ProcessPendingNameReload()
    {
        if (Interlocked.Exchange(ref nameReloadPending, 0) == 0) return;

        LoadEmployeeNames(Path.Combine(translationsDirectory, employeeNamesFile.Value));

        if (enableCandidateRefresh.Value)
            RefreshCandidates("name-file change", countsTowardsDailyLimit: false);
    }

    private static void SyncRefreshButton(HireStaffUI hireUi)
    {
        if (enableCandidateRefresh.Value && showButton.Value)
        {
            EnsureRefreshButton(hireUi);
            return;
        }

        var existing = hireUi != null
            ? hireUi.transform.Find("ContentHireStaff/VersatileButton/EmployeeRefreshButton")
            : null;
        if (existing != null) existing.gameObject.SetActive(false);
    }

    private static bool EnsureRefreshButton(HireStaffUI hireUi)
    {
        var content = hireUi.transform.Find("ContentHireStaff");
        if (content == null || !content.gameObject.activeInHierarchy) return false;

        // This is the exact hierarchy used by StaffShuffle.  The real game
        // button lives inside the hire window; it is not a ButtonsContext
        // bottom-bar control.
        var buttonHost = content.Find("VersatileButton");
        var template = buttonHost?.Find("Button");
        if (buttonHost == null || template == null) return false;

        var existing = buttonHost.Find("EmployeeRefreshButton");
        if (existing != null)
        {
            existing.gameObject.SetActive(true);
            refreshButtonRect = existing.GetComponent<RectTransform>();
            refreshTemplateRect = template.GetComponent<RectTransform>();
            ConfigureRefreshButton(existing, template);
            return true;
        }

        var clone = UnityEngine.Object.Instantiate(template.gameObject, buttonHost, true);
        clone.name = "EmployeeRefreshButton";
        clone.SetActive(true);
        var cloneRect = clone.GetComponent<RectTransform>();
        var templateRect = template.GetComponent<RectTransform>();
        refreshButtonRect = cloneRect;
        refreshTemplateRect = templateRect;
        var button = clone.GetComponent<Button>();
        if (button == null)
        {
            UnityEngine.Object.Destroy(clone);
            log.LogWarning("The game button template has no Button component.");
            return false;
        }

        button.onClick.RemoveAllListeners();
        for (var i = 0; i < button.onClick.GetPersistentEventCount(); i++)
        {
            button.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
        }
        button.onClick.AddListener(new UnityAction(() => RefreshCandidates("button")));
        ConfigureRefreshButton(clone.transform, template);

        log.LogInfo("Created EmployeeRefresh button from ContentHireStaff/VersatileButton/Button.");
        return true;
    }

    private static void ConfigureRefreshButton(Transform clone, Transform template)
    {
        var textTransform = clone.Find("Text");
        if (textTransform != null)
        {
            foreach (var component in textTransform.GetComponents<Component>())
            {
                var typeName = component.GetType().Name;
                if (typeName.IndexOf("Localis", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Localiz", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (component is Behaviour behaviour) behaviour.enabled = false;
                }
            }
        }

        var label = textTransform != null ? textTransform.GetComponent<TMP_Text>() : null;
        if (label != null)
        {
            label.text = ResolveRefreshButtonLabel();
        }

        var button = clone.GetComponent<Button>();
        if (button != null)
            button.interactable = CanRefreshToday();

        var sourceRect = template.GetComponent<RectTransform>();
        var cloneRect = clone.GetComponent<RectTransform>();
        if (sourceRect != null && cloneRect != null)
        {
            cloneRect.anchoredPosition = new Vector2(
                sourceRect.anchoredPosition.x - sourceRect.pivot.x * sourceRect.rect.width -
                (1f - sourceRect.pivot.x) * cloneRect.rect.width - buttonGap.Value,
                sourceRect.anchoredPosition.y);
        }

        for (var i = 0; i < clone.childCount; i++)
        {
            var cloneChild = clone.GetChild(i);
            var sourceChild = template.Find(cloneChild.name);
            if (sourceChild != null && cloneChild.gameObject.activeSelf != sourceChild.gameObject.activeSelf)
            {
                cloneChild.gameObject.SetActive(sourceChild.gameObject.activeSelf);
            }
        }
    }

    private static Transform FindChildRecursive(Transform root, string childName)
    {
        if (root == null) return null;
        if (root.name == childName) return root;
        for (var i = 0; i < root.childCount; i++)
        {
            var found = FindChildRecursive(root.GetChild(i), childName);
            if (found != null) return found;
        }
        return null;
    }

    private static void ApplyWindowResize(HireStaffUI hireUi)
    {
        var content = hireUi.transform.Find("ContentHireStaff");
        if (content == null)
        {
            log.LogWarning("Resize skipped: ContentHireStaff was not found on the active HireStaffUI instance.");
            return;
        }

        var contentRect = content.GetComponent<RectTransform>();
        if (contentRect == null)
        {
            log.LogWarning("Resize skipped: ContentHireStaff has no RectTransform.");
            return;
        }

        var windowId = hireUi.GetInstanceID();
        if (!WindowResizeStates.TryGetValue(windowId, out var state))
        {
            state = WindowResizeState.Capture(hireUi, contentRect);
            WindowResizeStates[windowId] = state;
            log.LogInfo($"Captured vanilla staff-window geometry: height={state.ContentHeight:0.##}.");
        }

        // HireStaffUI is sometimes recreated while its children survive. Never
        // calculate from an already resized panel: restore our captured vanilla
        // geometry first, then apply the requested multiplier exactly once.
        var multiplier = Mathf.Max(1f, heightMultiplier.Value);
        if (state.AppliedMultiplier == multiplier && state.AppliedGrowUpward == growUpward.Value && state.AppliedVerticalOffset == verticalOffset.Value)
            return;

        state.Restore();
        if (state.ContentHeight <= 0f) return;

        var targetHeight = state.ContentHeight * multiplier;
        var delta = targetHeight - state.ContentHeight;
        AdjustOffsetMinY(contentRect, -delta);
        var contentPosition = contentRect.anchoredPosition;
        contentPosition.y += verticalOffset.Value;
        contentRect.anchoredPosition = contentPosition;

        var scrollComponent = state.ScrollComponent;
        var scrollRect = state.ScrollRect;
        if (scrollRect != null)
        {
            AdjustOffsetMinY(scrollRect, -delta);
            AdjustOffsetMinY(scrollComponent.transform.Find("Viewport")?.GetComponent<RectTransform>(), -delta);
            AdjustOffsetMinY(scrollComponent.transform.Find("Scrollbar Vertical")?.GetComponent<RectTransform>(), delta);
        }
        else
        {
            log.LogWarning("Resize: HireStaffUI.scrollRect was not found; continuing with panel elements.");
        }

        AdjustOffsetMinY(state.NewPanelFilled, -delta);
        AdjustOffsetMinY(state.Adorno, -delta);

        var canvas = state.Canvas;
        if (growUpward.Value)
        {
            if (canvas != null)
            {
                var position = canvas.anchoredPosition;
                position.y += delta * 0.5f;
                canvas.anchoredPosition = position;
            }
        }

        state.AppliedMultiplier = multiplier;
        state.AppliedGrowUpward = growUpward.Value;
        state.AppliedVerticalOffset = verticalOffset.Value;
        log.LogInfo($"Resized staff hiring content: {state.ContentHeight:0.##} -> {targetHeight:0.##} (multiplier={multiplier:0.##}, delta={delta:0.##})");
    }

    private sealed class WindowResizeState
    {
        public RectTransform Content;
        public RectTransform ScrollRect;
        public RectTransform Viewport;
        public RectTransform Scrollbar;
        public RectTransform NewPanelFilled;
        public RectTransform Adorno;
        public RectTransform Canvas;
        public Component ScrollComponent;
        public float ContentHeight;
        public float AppliedMultiplier = -1f;
        public bool AppliedGrowUpward;
        public float AppliedVerticalOffset = float.NaN;
        private readonly Dictionary<RectTransform, RectSnapshot> snapshots = new Dictionary<RectTransform, RectSnapshot>();

        public static WindowResizeState Capture(HireStaffUI hireUi, RectTransform content)
        {
            var state = new WindowResizeState
            {
                Content = content,
                ContentHeight = content.rect.height,
                NewPanelFilled = content.Find("NewPanelFilled")?.GetComponent<RectTransform>(),
                Adorno = content.Find("NewPanelFilled/EmployeeElements/Adorno")?.GetComponent<RectTransform>(),
                Canvas = FindCanvasRect(content)
            };
            var scrollField = typeof(HireStaffUI).GetField("scrollRect", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            state.ScrollComponent = scrollField?.GetValue(hireUi) as Component;
            state.ScrollRect = state.ScrollComponent?.GetComponent<RectTransform>();
            state.Viewport = state.ScrollComponent?.transform.Find("Viewport")?.GetComponent<RectTransform>();
            state.Scrollbar = state.ScrollComponent?.transform.Find("Scrollbar Vertical")?.GetComponent<RectTransform>();
            state.Remember(state.Content);
            state.Remember(state.ScrollRect);
            state.Remember(state.Viewport);
            state.Remember(state.Scrollbar);
            state.Remember(state.NewPanelFilled);
            state.Remember(state.Adorno);
            state.Remember(state.Canvas);
            return state;
        }

        public void Restore()
        {
            foreach (var pair in snapshots)
            {
                if (pair.Key == null) continue;
                pair.Key.offsetMin = pair.Value.OffsetMin;
                pair.Key.anchoredPosition = pair.Value.AnchoredPosition;
            }
        }

        private void Remember(RectTransform rect)
        {
            if (rect != null)
                snapshots[rect] = new RectSnapshot { OffsetMin = rect.offsetMin, AnchoredPosition = rect.anchoredPosition };
        }

        private struct RectSnapshot
        {
            public Vector2 OffsetMin;
            public Vector2 AnchoredPosition;
        }
    }

    private static RectTransform FindCanvasRect(Transform start)
    {
        var current = start;
        while (current != null)
        {
            if (current.GetComponent<Canvas>() != null)
                return current.GetComponent<RectTransform>();
            current = current.parent;
        }

        return null;
    }

    private static void AdjustOffsetMinY(RectTransform rect, float delta)
    {
        if (rect == null) return;
        var offset = rect.offsetMin;
        offset.y += delta;
        rect.offsetMin = offset;
    }

    private static string ResolveRefreshButtonLabel()
    {
        var limit = Mathf.Max(0, refreshLimitPerDay.Value);
        if (limit > 0 && !CanRefreshToday())
            return ResolveUiLabel("RefreshAvailableTomorrow", "Refresh tomorrow");

        var refreshLabel = ResolveUiLabel("RefreshButton", GetGameRefreshLabel());
        if (limit <= 0) return refreshLabel;

        var format = ResolveUiLabel("RefreshRemaining", "{0} ({1}/{2})");
        return string.Format(format, refreshLabel, refreshesToday, limit);
    }

    private static string ResolveUiLabel(string key, string fallback)
    {
        return UiLabels.TryGetValue(key, out var fileLabel) && !string.IsNullOrWhiteSpace(fileLabel)
            ? fileLabel
            : fallback;
    }

    private static string GetGameRefreshLabel()
    {
        try
        {
            var localized = LocalisationSystem.GetStringWithTags("Refresh", 0);
            if (!string.IsNullOrEmpty(localized) && !string.Equals(localized, "Refresh", StringComparison.OrdinalIgnoreCase))
                return localized;
        }
        catch (Exception exception)
        {
            log.LogDebug($"Refresh localization key unavailable: {exception.Message}");
        }

        return buttonLabel.Value;
    }

    private static bool CanRefreshToday()
    {
        return refreshLimitPerDay == null || refreshLimitPerDay.Value <= 0 || refreshesToday < refreshLimitPerDay.Value;
    }

    private static void ResetDailyRefreshLimit()
    {
        if (refreshesToday == 0) return;
        refreshesToday = 0;
        log.LogInfo("Daily employee Refresh limit reset for the new in-game day.");
        foreach (var hireUi in HireStaffUI.instances)
        {
            if (IsHireWindowOpen(hireUi)) SyncRefreshButton(hireUi);
        }
    }

    private static bool IsHireWindowOpen(HireStaffUI hireUi)
    {
        var content = hireUi != null ? hireUi.transform.Find("ContentHireStaff") : null;
        return content != null && content.gameObject.activeInHierarchy;
    }

    private static int TabForWorkerType(WorkerType workerType)
    {
        if ((int)workerType == 1) return 0;
        if ((int)workerType == 2) return 1;
        if ((int)workerType == 8) return 2;
        return 3;
    }

    private static void RefreshCandidates(string source, bool countsTowardsDailyLimit = true)
    {
        if (!enableCandidateRefresh.Value) return;
        if (countsTowardsDailyLimit && !CanRefreshToday())
        {
            log.LogInfo("Employee Refresh blocked: today's configured limit has been reached.");
            return;
        }

        var method = AccessTools.Method(typeof(StaffManager), "CreateRandomOptionsWorkers");
        if (method == null)
        {
            log.LogWarning("The game's staff candidate generator was not found.");
            return;
        }

        var openWindow = false;
        foreach (var hireUi in HireStaffUI.instances)
        {
            if (IsHireWindowOpen(hireUi))
            {
                openWindow = true;
                break;
            }
        }
        if (!openWindow)
        {
            log.LogInfo($"Refresh hotkey was pressed, but the staff hire window is not open.");
            return;
        }

        method.Invoke(null, null);
        if (countsTowardsDailyLimit && refreshLimitPerDay.Value > 0)
            refreshesToday++;
        foreach (var hireUi in HireStaffUI.instances)
        {
            if (IsHireWindowOpen(hireUi))
            {
                // This is the same panel refresh used by StaffShuffle.
                hireUi.FocusTab(TabForWorkerType(hireUi.workerType));
            }
        }
        log.LogInfo($"Staff candidates refreshed via {source} using the game's CreateRandomOptionsWorkers method.");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HireStaffUI), "OpenUI")]
    private static void HireStaffUI_OpenUI_Postfix(HireStaffUI __instance)
    {
        SyncRefreshButton(__instance);
        ApplyWindowResize(__instance);
    }

    private static void WorldTime_SetNextDay_Postfix()
    {
        ResetDailyRefreshLimit();
    }

    private static void Save_LoadGame_Postfix()
    {
        ResetDailyRefreshLimit();
    }

    // StaffShuffle uses this exact hook.  It runs only after the hire window
    // itself is updating with ContentHireStaff active, so one opening is
    // sufficient even if OpenUI is called more than once internally.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HireStaffUI), "Update")]
    private static void HireStaffUI_Update_Postfix(HireStaffUI __instance)
    {
        ProcessPendingNameReload();

        SyncRefreshButton(__instance);

        // The hire-window Update hook is guaranteed to run while the target
        // panel is active.  It is therefore the reliable place for the shortcut.
        if (enableCandidateRefresh.Value && refreshHotkey != null && refreshHotkey.Value.IsDown())
        {
            RefreshCandidates("hotkey");
        }
    }

    private static void LoadEmployeeNames(string path)
    {
        ClearEmployeeNames();
        activeNamePoolIsRtl = IsRightToLeftNamePool(path);
        if (!File.Exists(path))
        {
            log.LogWarning($"Employee name file not found; using game defaults: {path}");
            return;
        }

        var section = string.Empty;
        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith("//")) continue;
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2);
                continue;
            }

            if (section.Equals("UI", StringComparison.OrdinalIgnoreCase))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    UiLabels[key] = value;
                continue;
            }

            var target = section == "MaleFirstNames" ? MaleFirstNames :
                section == "FemaleFirstNames" ? FemaleFirstNames :
                section == "MaleSurnames" ? MaleSurnames :
                section == "FemaleSurnames" ? FemaleSurnames :
                section == "Surnames" ? SharedSurnames : null;
            if (target == null) continue;
            target.Add(line);
            if (maxNameLength.Value > 0 && line.Length > maxNameLength.Value)
                log.LogWarning($"Name '{line}' is {line.Length} characters; recommended maximum is {maxNameLength.Value}.");
        }

        log.LogInfo($"Loaded employee names: male={MaleFirstNames.Count}, female={FemaleFirstNames.Count}, male surnames={MaleSurnames.Count}, female surnames={FemaleSurnames.Count}, shared surnames={SharedSurnames.Count}, UI labels={UiLabels.Count}");
    }

    private static void ClearEmployeeNames()
    {
        MaleFirstNames.Clear();
        FemaleFirstNames.Clear();
        MaleSurnames.Clear();
        FemaleSurnames.Clear();
        SharedSurnames.Clear();
        UiLabels.Clear();
    }

    private static bool IsRightToLeftNamePool(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        const string prefix = "employee-names.";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return RightToLeftLanguageCodes.Contains(fileName.Substring(prefix.Length));
    }

    private static string FixRightToLeftName(string name)
    {
        if (!activeNamePoolIsRtl) return name;

        try
        {
            // Travellers Rest already ships I2 Localization's Arabic shaping and
            // bidi implementation. Resolve it once: one refresh may generate
            // many employees, and reflection for every individual name is slow.
            if (!rtlFixLookupComplete)
            {
                rtlFixLookupComplete = true;
                var localizationType = AccessTools.TypeByName("I2.Loc.LocalizationManager");
                var method = AccessTools.Method(localizationType, "ApplyRTLfix", new[] { typeof(string), typeof(int), typeof(bool) });
                if (method != null)
                    rtlFixMethod = (Func<string, int, bool, string>)Delegate.CreateDelegate(typeof(Func<string, int, bool, string>), method);
                else
                    log.LogWarning("I2 RTL formatter was not found; Arabic employee names will be unformatted.");
            }

            var fixedName = rtlFixMethod?.Invoke(name, 0, true);
            return string.IsNullOrEmpty(fixedName) ? name : fixedName;
        }
        catch (Exception exception)
        {
            log.LogWarning($"Could not apply RTL formatting to employee name: {exception.Message}");
            return name;
        }
    }

    private static void EmployeeNamePostfix(EmployeeInfo __instance, ref string __result)
    {
        if (!enableNameOverrides.Value) return;
        var firstNames = __instance.gender == Gender.Male ? MaleFirstNames : FemaleFirstNames;
        var genderedSurnames = __instance.gender == Gender.Male ? MaleSurnames : FemaleSurnames;
        var surnames = SharedSurnames.Count > 0 ? SharedSurnames : genderedSurnames;
        if (firstNames.Count == 0 || surnames.Count == 0) return;
        var name = $"{firstNames[UnityEngine.Random.Range(0, firstNames.Count)]} {surnames[UnityEngine.Random.Range(0, surnames.Count)]}";
        __result = FixRightToLeftName(name);
    }
}
