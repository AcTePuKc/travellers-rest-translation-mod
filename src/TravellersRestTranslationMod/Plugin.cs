using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using I2.Loc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace TravellersRestTranslationMod;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.travellersrest.translation";
    public const string PluginName = "Travellers Rest Translation Loader";
    public const string PluginVersion = "0.1.0";

    private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly HashSet<string> ObservedTerms = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> ObservedSubtitles = new HashSet<string>(StringComparer.Ordinal);
    private static ManualLogSource log;
    private static ConfigEntry<bool> enableTranslationOverrides;
    private static ConfigEntry<string> translationFile;
    private static ConfigEntry<bool> dumpObservedTerms;
    private static ConfigEntry<string> dumpFile;
    private static string dumpPath;

    private void Awake()
    {
        log = Logger;
        enableTranslationOverrides = Config.Bind("General", "EnableTranslationOverrides", true, "Apply labels.txt translations to the game.");
        translationFile = Config.Bind("General", "TranslationFile", "labels.txt", "Translation file inside the plugin translations folder.");
        dumpObservedTerms = Config.Bind("Debug", "DumpObservedTerms", false, "Write every localization term requested by the game to a runtime dump.");
        dumpFile = Config.Bind("Debug", "DumpFile", "runtime-labels.txt", "Runtime dump filename inside the plugin translations folder.");

        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Paths.PluginPath;
        var translationsDir = Path.Combine(pluginDir, "translations");
        var labelsPath = Path.Combine(translationsDir, translationFile.Value);
        dumpPath = Path.Combine(translationsDir, dumpFile.Value);
        LoadLabels(labelsPath);
        PrepareRuntimeDump();

        new Harmony(PluginGuid).PatchAll(typeof(Plugin));
        log.LogInfo($"{PluginName} {PluginVersion} loaded with {Labels.Count} labels");
        log.LogInfo($"Labels path: {labelsPath}");
    }

    private static void LoadLabels(string path)
    {
        Labels.Clear();
        if (!File.Exists(path))
        {
            log.LogWarning($"Translation file not found: {path}");
            return;
        }

        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = rawLine.TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("//"))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line.Substring(0, separator);
            var value = DecodeValue(line.Substring(separator + 1));
            if (key.Length > 0 && value.Length > 0)
            {
                Labels[key] = value;
            }
        }
    }

    private static string DecodeValue(string value)
    {
        return value
            .Replace("\\r\\n", "\n")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t");
    }

    private static string EncodeValue(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    private static void PrepareRuntimeDump()
    {
        ObservedTerms.Clear();
        ObservedSubtitles.Clear();
        if (!dumpObservedTerms.Value)
        {
            return;
        }

        var directory = Path.GetDirectoryName(dumpPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            dumpPath,
            "# Runtime localization terms observed by Travellers Rest Translation Loader\n" +
            "# This file is regenerated when the game starts with DumpObservedTerms enabled.\n",
            new UTF8Encoding(false));
        log.LogInfo($"Runtime localization dump enabled: {dumpPath}");
    }

    private static void DumpObservedTerm(string term, string value)
    {
        if (!dumpObservedTerms.Value || !ObservedTerms.Add(term))
        {
            return;
        }

        try
        {
            File.AppendAllText(
                dumpPath,
                $"{term}={EncodeValue(value)}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            log.LogWarning($"Could not write runtime localization dump: {exception.Message}");
        }
    }

    private static void DumpObservedSubtitle(string hook, PixelCrushers.DialogueSystem.Subtitle subtitle)
    {
        if (!dumpObservedTerms.Value || subtitle == null)
        {
            return;
        }

        var entryTag = subtitle.entrytag ?? string.Empty;
        var rawText = subtitle.formattedText?.text ?? string.Empty;
        var dialogueText = subtitle.dialogueEntry?.currentDialogueText ?? string.Empty;
        var localizedText = subtitle.dialogueEntry?.currentLocalizedDialogueText ?? string.Empty;
        var signature = $"{hook}\n{entryTag}\n{rawText}\n{dialogueText}\n{localizedText}";
        if (!ObservedSubtitles.Add(signature))
        {
            return;
        }

        try
        {
            var block =
                $"\n# Subtitle hook: {hook}\n" +
                $"# EntryTag={EncodeValue(entryTag)}\n" +
                $"# Speaker={EncodeValue(subtitle.speakerInfo?.Name)}\n" +
                $"# RawText={EncodeValue(rawText)}\n" +
                $"# DialogueText={EncodeValue(dialogueText)}\n" +
                $"# LocalizedDialogueText={EncodeValue(localizedText)}\n";
            File.AppendAllText(dumpPath, block, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            log.LogWarning($"Could not write runtime subtitle dump: {exception.Message}");
        }
    }

    private static void ApplyTranslationOverride(string term, ref string result)
    {
        if (enableTranslationOverrides.Value && Labels.TryGetValue(term, out var replacement))
        {
            result = replacement;
        }

        DumpObservedTerm(term, result);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.GetTranslation))]
    private static void LocalizationManager_GetTranslation_Postfix(string Term, ref string __result)
    {
        if (string.IsNullOrEmpty(Term))
        {
            return;
        }

        ApplyTranslationOverride(Term, ref __result);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PixelCrushers.UILocalizationManager), nameof(PixelCrushers.UILocalizationManager.GetLocalizedText))]
    private static void UILocalizationManager_GetLocalizedText_Postfix(string __0, ref string __result)
    {
        if (string.IsNullOrEmpty(__0))
        {
            return;
        }

        ApplyTranslationOverride(__0, ref __result);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PixelCrushers.DialogueSystem.DialogueManager), nameof(PixelCrushers.DialogueSystem.DialogueManager.GetLocalizedText))]
    private static void DialogueManager_GetLocalizedText_Postfix(string __0, ref string __result)
    {
        if (!string.IsNullOrEmpty(__0))
        {
            ApplyTranslationOverride(__0, ref __result);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PixelCrushers.DialogueSystem.DialogueSystemController), nameof(PixelCrushers.DialogueSystem.DialogueSystemController.GetLocalizedText))]
    private static void DialogueSystemController_GetLocalizedText_Postfix(string __0, ref string __result)
    {
        if (!string.IsNullOrEmpty(__0))
        {
            ApplyTranslationOverride(__0, ref __result);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PixelCrushers.DialogueSystem.LocalizedTextTable), "GetText")]
    private static void LocalizedTextTable_GetText_Postfix(string __0, ref string __result)
    {
        if (!string.IsNullOrEmpty(__0))
        {
            ApplyTranslationOverride(__0, ref __result);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PixelCrushers.DialogueSystem.AbstractDialogueUI), nameof(PixelCrushers.DialogueSystem.AbstractDialogueUI.ShowSubtitle))]
    private static void AbstractDialogueUI_ShowSubtitle_Postfix(PixelCrushers.DialogueSystem.Subtitle __0)
    {
        DumpObservedSubtitle("AbstractDialogueUI.ShowSubtitle", __0);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PixelCrushers.DialogueSystem.StandardUISubtitlePanel), "SetSubtitleTextContent")]
    private static void StandardUISubtitlePanel_SetSubtitleTextContent_Postfix(PixelCrushers.DialogueSystem.StandardUISubtitlePanel __instance)
    {
        DumpObservedSubtitle("StandardUISubtitlePanel.SetSubtitleTextContent", __instance?.currentSubtitle);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DialogueNPCBase), nameof(DialogueNPCBase.GetSubtitleFromDatabase))]
    private static void DialogueNPCBase_GetSubtitleFromDatabase_Postfix(PixelCrushers.DialogueSystem.Subtitle __result)
    {
        DumpObservedSubtitle("DialogueNPCBase.GetSubtitleFromDatabase", __result);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DialogueNPCBase), "OnConversationLine")]
    private static void DialogueNPCBase_OnConversationLine_Postfix(PixelCrushers.DialogueSystem.Subtitle __0)
    {
        DumpObservedSubtitle("DialogueNPCBase.OnConversationLine", __0);
    }
}
