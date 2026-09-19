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
    public const string PluginVersion = "0.1.0";

    private static readonly List<string> MaleFirstNames = new List<string>();
    private static readonly List<string> FemaleFirstNames = new List<string>();
    private static readonly List<string> MaleSurnames = new List<string>();
    private static readonly List<string> FemaleSurnames = new List<string>();
    private static readonly List<string> SharedSurnames = new List<string>();
    private static readonly Dictionary<string, string> UiLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static ManualLogSource log;
    private static ConfigEntry<string> employeeNamesFile;
    private static ConfigEntry<int> maxNameLength;
    private static ConfigEntry<KeyboardShortcut> refreshHotkey;
    private static ConfigEntry<bool> showButton;
    private static ConfigEntry<string> buttonLabel;
    private static ConfigEntry<int> buttonGap;
    private static ConfigEntry<float> heightMultiplier;
    private static ConfigEntry<bool> growUpward;
    private static ConfigEntry<float> verticalOffset;
    private static readonly HashSet<int> ResizedWindows = new HashSet<int>();
    private static RectTransform refreshButtonRect;
    private static RectTransform refreshTemplateRect;
    private static string translationsDirectory;
    private static int nameReloadPending;

    private void Awake()
    {
        log = Logger;
        employeeNamesFile = Config.Bind("General", "EmployeeNamesFile", "employee-names.bg.txt", "Name pool inside this mod's translations folder.");
        maxNameLength = Config.Bind("General", "MaxNameLength", 9, "Warn when a first name or surname is longer than this many characters. Longer names are not silently truncated.");
        refreshHotkey = Config.Bind("General", "RefreshHotkey", KeyboardShortcut.Deserialize("F6"), "Regenerate staff candidates while the hire-staff panel is open. Clear the value to disable the hotkey.");
        showButton = Config.Bind("General", "ShowButton", true, "Show a refresh button using the game's own staff-button template and icon.");
        buttonLabel = Config.Bind("General", "ButtonLabel", "Refresh", "Final fallback when the selected employee-names file has no [UI] RefreshButton entry.");
        buttonGap = Config.Bind("General", "ButtonGap", 12, "Horizontal gap between the game's staff button and the refresh button.");
        heightMultiplier = Config.Bind("Window", "HeightMultiplier", 1.25f, "Height multiplier for the staff hiring window. 1.25 means 125% of the original height.");
        growUpward = Config.Bind("Window", "GrowUpward", true, "Keep the lower edge stable while increasing the window height.");
        verticalOffset = Config.Bind("Window", "VerticalOffset", 40f, "Move the staff hiring window upward by this many UI units.");

        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Paths.PluginPath;
        translationsDirectory = Path.Combine(pluginDir, "translations");
        LoadEmployeeNames(Path.Combine(translationsDirectory, employeeNamesFile.Value));
        employeeNamesFile.SettingChanged += (_, __) => Interlocked.Exchange(ref nameReloadPending, 1);
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

        log.LogInfo($"Employee refresh generator found: {refreshMethod}");
    }

    private static bool refreshMethod => AccessTools.Method(typeof(StaffManager), "CreateRandomOptionsWorkers") != null;

    private void Update()
    {
        var hireUi = HireStaffUI.GKFMIJGIONI(0);
        if (hireUi == null) return;

        if (showButton.Value)
        {
            EnsureRefreshButton(hireUi);
        }

        ApplyWindowResize(hireUi);
        ProcessPendingNameReload();
    }

    private static void ProcessPendingNameReload()
    {
        if (Interlocked.Exchange(ref nameReloadPending, 0) == 0) return;

        LoadEmployeeNames(Path.Combine(translationsDirectory, employeeNamesFile.Value));
        RefreshCandidates("name-file change");
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
            label.text = ResolveButtonLabel();
        }

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
        if (heightMultiplier.Value <= 1f) return;

        var content = hireUi.transform.Find("ContentHireStaff");
        if (content == null)
        {
            log.LogWarning("Resize skipped: ContentHireStaff was not found on the active HireStaffUI instance.");
            return;
        }

        if (ResizedWindows.Contains(hireUi.GetInstanceID())) return;

        var contentRect = content.GetComponent<RectTransform>();
        if (contentRect == null)
        {
            log.LogWarning("Resize skipped: ContentHireStaff has no RectTransform.");
            return;
        }

        var currentHeight = contentRect.rect.height;
        if (currentHeight <= 0f) return;

        var targetHeight = currentHeight * heightMultiplier.Value;
        var delta = targetHeight - currentHeight;
        AdjustOffsetMinY(contentRect, -delta);
        var contentPosition = contentRect.anchoredPosition;
        contentPosition.y += verticalOffset.Value;
        contentRect.anchoredPosition = contentPosition;

        var scrollField = typeof(HireStaffUI).GetField("scrollRect", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var scrollComponent = scrollField?.GetValue(hireUi) as Component;
        var scrollRect = scrollComponent?.GetComponent<RectTransform>();
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

        AdjustOffsetMinY(content.Find("NewPanelFilled")?.GetComponent<RectTransform>(), -delta);
        AdjustOffsetMinY(content.Find("NewPanelFilled/EmployeeElements/Adorno")?.GetComponent<RectTransform>(), -delta);

        var canvas = FindCanvasRect(content);
        if (growUpward.Value)
        {
            if (canvas != null)
            {
                var position = canvas.anchoredPosition;
                position.y += delta * 0.5f;
                canvas.anchoredPosition = position;
            }
        }

        ResizedWindows.Add(hireUi.GetInstanceID());
        log.LogInfo($"Resized staff hiring content: {currentHeight:0.##} -> {targetHeight:0.##} (delta={delta:0.##})");
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

    private static string ResolveButtonLabel()
    {
        if (UiLabels.TryGetValue("RefreshButton", out var fileLabel) && !string.IsNullOrWhiteSpace(fileLabel))
            return fileLabel;

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

    private static void RefreshCandidates(string source)
    {
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
        if (showButton.Value)
        {
            EnsureRefreshButton(__instance);
        }
        ApplyWindowResize(__instance);
    }

    // StaffShuffle uses this exact hook.  It runs only after the hire window
    // itself is updating with ContentHireStaff active, so one opening is
    // sufficient even if OpenUI is called more than once internally.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HireStaffUI), "Update")]
    private static void HireStaffUI_Update_Postfix(HireStaffUI __instance)
    {
        ProcessPendingNameReload();

        if (showButton.Value)
        {
            EnsureRefreshButton(__instance);
        }

        // The hire-window Update hook is guaranteed to run while the target
        // panel is active.  It is therefore the reliable place for the shortcut.
        if (refreshHotkey != null && refreshHotkey.Value.IsDown())
        {
            RefreshCandidates("hotkey");
        }
    }

    private static void LoadEmployeeNames(string path)
    {
        MaleFirstNames.Clear();
        FemaleFirstNames.Clear();
        MaleSurnames.Clear();
        FemaleSurnames.Clear();
        SharedSurnames.Clear();
        UiLabels.Clear();
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

    private static void EmployeeNamePostfix(EmployeeInfo __instance, ref string __result)
    {
        var firstNames = __instance.gender == Gender.Male ? MaleFirstNames : FemaleFirstNames;
        var genderedSurnames = __instance.gender == Gender.Male ? MaleSurnames : FemaleSurnames;
        var surnames = SharedSurnames.Count > 0 ? SharedSurnames : genderedSurnames;
        if (firstNames.Count == 0 || surnames.Count == 0) return;
        __result = $"{firstNames[UnityEngine.Random.Range(0, firstNames.Count)]} {surnames[UnityEngine.Random.Range(0, surnames.Count)]}";
    }
}
