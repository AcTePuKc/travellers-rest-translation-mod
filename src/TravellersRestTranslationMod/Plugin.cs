using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using I2.Loc;
using System;
using System.Collections;
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
    private static readonly HashSet<string> ObservedFormattedTexts = new HashSet<string>(StringComparer.Ordinal);
    private static ManualLogSource log;
    private static ConfigEntry<bool> enableTranslationOverrides;
    private static ConfigEntry<string> translationFile;
    private static ConfigEntry<bool> dumpObservedTerms;
    private static ConfigEntry<bool> dumpDialogueDatabase;
    private static ConfigEntry<string> dumpFile;
    private static string dumpPath;
    private static string dialogueDatabaseDumpPath;
    private static bool dialogueDatabaseDumped;
    private static int databaseProbeCount;

    private void Awake()
    {
        log = Logger;
        enableTranslationOverrides = Config.Bind("General", "EnableTranslationOverrides", true, "Apply labels.txt translations to the game.");
        translationFile = Config.Bind("General", "TranslationFile", "labels.txt", "Translation file inside the plugin translations folder.");
        dumpObservedTerms = Config.Bind("Debug", "DumpObservedTerms", false, "Write every localization term requested by the game to a runtime dump.");
        dumpDialogueDatabase = Config.Bind("Debug", "DumpDialogueDatabase", false, "Write the complete Dialogue System database once after it loads.");
        dumpFile = Config.Bind("Debug", "DumpFile", "runtime-labels.txt", "Runtime dump filename inside the plugin translations folder.");

        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Paths.PluginPath;
        var translationsDir = Path.Combine(pluginDir, "translations");
        var labelsPath = Path.Combine(translationsDir, translationFile.Value);
        dumpPath = Path.Combine(translationsDir, dumpFile.Value);
        dialogueDatabaseDumpPath = Path.Combine(translationsDir, "runtime-dialogue-database.txt");
        LoadLabels(labelsPath);
        PrepareRuntimeDump();
        log.LogInfo($"Observed localization dump enabled: {dumpObservedTerms.Value}");
        StartCoroutine(DumpDialogueDatabaseWhenReady());
        log.LogInfo($"Dialogue database dump enabled: {dumpDialogueDatabase.Value}");

        new Harmony(PluginGuid).PatchAll(typeof(Plugin));
        log.LogInfo($"{PluginName} {PluginVersion} loaded with {Labels.Count} labels");
        log.LogInfo($"Labels path: {labelsPath}");
    }

    private void Update()
    {
        if (dumpDialogueDatabase.Value && !dialogueDatabaseDumped)
        {
            TryDumpDialogueDatabase();
        }
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
        ObservedFormattedTexts.Clear();
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

    private IEnumerator DumpDialogueDatabaseWhenReady()
    {
        if (!dumpDialogueDatabase.Value)
        {
            yield break;
        }

        for (var attempt = 0; attempt < 300 && !dialogueDatabaseDumped; attempt++)
        {
            if (TryDumpDialogueDatabase())
            {
                yield break;
            }

            yield return null;
        }

        if (!dialogueDatabaseDumped)
        {
            log.LogWarning("Dialogue System database was not ready for the full runtime dump.");
        }
    }

    private static bool TryDumpDialogueDatabase()
    {
        try
        {
            var managerType = typeof(PixelCrushers.DialogueSystem.DialogueManager);
            var hasInstance = (bool)(managerType.GetProperty("hasInstance")?.GetValue(null, null) ?? false);
            var database = managerType.GetProperty("masterDatabase")?.GetValue(null, null);
            if (database == null)
            {
                var databases = UnityEngine.Resources.FindObjectsOfTypeAll<PixelCrushers.DialogueSystem.DialogueDatabase>();
                if (databaseProbeCount++ % 120 == 0)
                {
                    log.LogInfo($"Dialogue database resource probe: found {databases.Length} database asset(s).");
                }

                if (databases.Length > 0)
                {
                    database = databases[0];
                }
            }
            if (databaseProbeCount++ % 120 == 0)
            {
                log.LogInfo($"Dialogue database probe: hasInstance={hasInstance}, masterDatabase={(database == null ? "null" : database.GetType().FullName)}");
            }
            if (!hasInstance || database == null)
            {
                return false;
            }

            var databaseType = database.GetType();
            var conversations = databaseType.GetField("conversations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(database) as IEnumerable;
            if (conversations == null)
            {
                log.LogWarning("Dialogue database probe: conversations field was not found or was null.");
                return false;
            }

            var output = new StringBuilder();
            output.AppendLine("# Full Dialogue System database dump");
            output.AppendLine("# Generated once after the master database became available.");
            var conversationCount = 0;
            var entryCount = 0;

            foreach (var conversation in conversations)
            {
                var conversationType = conversation.GetType();
                var title = conversationType.GetProperty("Title")?.GetValue(conversation, null)?.ToString() ?? string.Empty;
                var conversationId = conversationType.GetField("id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(conversation)?.ToString() ?? string.Empty;
                var entries = conversationType.GetField("dialogueEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(conversation) as IEnumerable;
                conversationCount++;
                output.AppendLine($"\n# Conversation={EncodeValue(title)}");
                output.AppendLine($"# ConversationId={EncodeValue(conversationId)}");

                if (entries == null)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    var entryType = entry.GetType();
                    var entryId = entryType.GetField("id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(entry)?.ToString() ?? string.Empty;
                    var dialogueText = entryType.GetProperty("DialogueText")?.GetValue(entry, null)?.ToString() ?? string.Empty;
                    var localizedText = entryType.GetProperty("currentLocalizedDialogueText")?.GetValue(entry, null)?.ToString() ?? string.Empty;
                    output.AppendLine($"# EntryId={EncodeValue(entryId)}");
                    output.AppendLine($"# Key=Conversation/{EncodeValue(title)}/Entry/{EncodeValue(entryId)}/Dialogue Text");
                    output.AppendLine($"# DialogueText={EncodeValue(dialogueText)}");
                    output.AppendLine($"# LocalizedDialogueText={EncodeValue(localizedText)}");
                    entryCount++;
                }
            }

            File.WriteAllText(dialogueDatabaseDumpPath, output.ToString(), new UTF8Encoding(false));
            dialogueDatabaseDumped = true;
            log.LogInfo($"Full Dialogue System database dump written: {conversationCount} conversations, {entryCount} entries.");
            return true;
        }
        catch (Exception exception)
        {
            log.LogWarning($"Could not write full Dialogue System database dump: {exception.Message}");
            return false;
        }
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
        var conversationTitle = GetSubtitleConversationTitle(subtitle);
        var canonicalConversationTitle = NormalizeConversationTitle(conversationTitle);
        var entryId = subtitle.dialogueEntry?.id.ToString() ?? string.Empty;
        var resolvedTerm = string.IsNullOrEmpty(canonicalConversationTitle) || string.IsNullOrEmpty(entryId)
            ? string.Empty
            : $"Conversation/{canonicalConversationTitle}/Entry/{entryId}/Dialogue Text";
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
                $"# ConversationTitle={EncodeValue(conversationTitle)}\n" +
                $"# DialogueEntryId={EncodeValue(entryId)}\n" +
                $"# ResolvedTerm={EncodeValue(resolvedTerm)}\n" +
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

    private static void ApplySubtitleTranslation(PixelCrushers.DialogueSystem.Subtitle subtitle)
    {
        if (!enableTranslationOverrides.Value || subtitle?.formattedText == null || subtitle.dialogueEntry == null)
        {
            return;
        }

        var conversationTitle = GetSubtitleConversationTitle(subtitle);
        var canonicalConversationTitle = NormalizeConversationTitle(conversationTitle);
        if (string.IsNullOrEmpty(canonicalConversationTitle))
        {
            return;
        }

        var term = $"Conversation/{canonicalConversationTitle}/Entry/{subtitle.dialogueEntry.id}/Dialogue Text";
        if (Labels.TryGetValue(term, out var replacement))
        {
            log.LogInfo($"Dialogue translation matched: {term}");
            subtitle.dialogueEntry.currentLocalizedDialogueText = replacement;
            subtitle.formattedText = PixelCrushers.DialogueSystem.FormattedText.Parse(replacement, null);
        }
    }

    private static void ApplyResponseTranslation(PixelCrushers.DialogueSystem.Response response)
    {
        var entry = response?.destinationEntry;
        if (!enableTranslationOverrides.Value || entry == null)
        {
            return;
        }

        var conversationTitle = GetConversationTitle(entry.conversationID);
        var canonicalConversationTitle = NormalizeConversationTitle(conversationTitle);
        if (string.IsNullOrEmpty(canonicalConversationTitle))
        {
            return;
        }

        var term = $"Conversation/{canonicalConversationTitle}/Entry/{entry.id}/Dialogue Text";
        if (Labels.TryGetValue(term, out var replacement))
        {
            log.LogInfo($"Response translation matched: {term}");
            response.formattedText = PixelCrushers.DialogueSystem.FormattedText.Parse(replacement, null);
        }
    }

    private static string NormalizeConversationTitle(string conversationTitle)
    {
        return string.IsNullOrEmpty(conversationTitle)
            ? string.Empty
            : conversationTitle.Replace('/', '.');
    }

    private static string GetSubtitleConversationTitle(PixelCrushers.DialogueSystem.Subtitle subtitle)
    {
        var conversationTitle = subtitle?.activeConversationRecord?.conversationTitle;
        if (!string.IsNullOrEmpty(conversationTitle))
        {
            return conversationTitle;
        }

        var dialogueEntry = subtitle?.dialogueEntry;
        if (dialogueEntry == null)
        {
            return string.Empty;
        }

        try
        {
            return GetConversationTitle(dialogueEntry.conversationID);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetConversationTitle(int conversationId)
    {
        return PixelCrushers.DialogueSystem.DialogueManager.masterDatabase
            ?.GetConversation(conversationId)
            ?.Title ?? string.Empty;
    }

    private static void ApplyTranslationOverride(string term, ref string result)
    {
        if (enableTranslationOverrides.Value && Labels.TryGetValue(term, out var replacement))
        {
            result = replacement;
        }

        DumpObservedTerm(term, result);
    }

    private static void DumpFormattedTextParse(string rawText, PixelCrushers.DialogueSystem.FormattedText parsedText)
    {
        if (!dumpObservedTerms.Value || string.IsNullOrEmpty(rawText))
        {
            return;
        }

        var parsed = parsedText?.text ?? string.Empty;
        var signature = $"{rawText}\n{parsed}";
        if (!ObservedFormattedTexts.Add(signature))
        {
            return;
        }

        try
        {
            var block =
                "\n# FormattedText.Parse\n" +
                $"# RawText={EncodeValue(rawText)}\n" +
                $"# ParsedText={EncodeValue(parsed)}\n";
            File.AppendAllText(dumpPath, block, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            log.LogWarning($"Could not write formatted-text trace: {exception.Message}");
        }
    }

    private static void DumpDialogueEntryLookup(string conversationName, int entryId, PixelCrushers.DialogueSystem.DialogueEntry entry)
    {
        if (!dumpObservedTerms.Value)
        {
            return;
        }

        try
        {
            var dialogueText = entry?.currentDialogueText ?? string.Empty;
            var localizedText = entry?.currentLocalizedDialogueText ?? string.Empty;
            var block =
                "\n# DialogueEntry lookup\n" +
                $"# ConversationName={EncodeValue(conversationName ?? string.Empty)}\n" +
                $"# RequestedEntryId={entryId}\n" +
                $"# ResultEntryId={entry?.id.ToString() ?? string.Empty}\n" +
                $"# DialogueText={EncodeValue(dialogueText)}\n" +
                $"# LocalizedDialogueText={EncodeValue(localizedText)}\n";
            File.AppendAllText(dumpPath, block, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            log.LogWarning($"Could not write dialogue-entry trace: {exception.Message}");
        }
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
    [HarmonyPatch(typeof(PixelCrushers.DialogueSystem.FormattedText), nameof(PixelCrushers.DialogueSystem.FormattedText.Parse))]
    private static void FormattedText_Parse_Postfix(string rawText, PixelCrushers.DialogueSystem.FormattedText __result)
    {
        DumpFormattedTextParse(rawText, __result);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DialogueNPCBase), nameof(DialogueNPCBase.GetDialogueEntryFromDatabase))]
    private static void DialogueNPCBase_GetDialogueEntryFromDatabase_Postfix(
        string __0,
        int __1,
        PixelCrushers.DialogueSystem.DialogueEntry __result)
    {
        DumpDialogueEntryLookup(__0, __1, __result);
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
        ApplySubtitleTranslation(__instance?.currentSubtitle);
        DumpObservedSubtitle("StandardUISubtitlePanel.SetSubtitleTextContent", __instance?.currentSubtitle);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DialogueNPCBase), nameof(DialogueNPCBase.GetSubtitleFromDatabase))]
    private static void DialogueNPCBase_GetSubtitleFromDatabase_Postfix(PixelCrushers.DialogueSystem.Subtitle __result)
    {
        ApplySubtitleTranslation(__result);
        DumpObservedSubtitle("DialogueNPCBase.GetSubtitleFromDatabase", __result);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DialogueNPCBase), "OnConversationLine")]
    private static void DialogueNPCBase_OnConversationLine_Prefix(PixelCrushers.DialogueSystem.Subtitle __0)
    {
        ApplySubtitleTranslation(__0);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DialogueNPCBase), "OnConversationLine")]
    private static void DialogueNPCBase_OnConversationLine_Postfix(PixelCrushers.DialogueSystem.Subtitle __0)
    {
        ApplySubtitleTranslation(__0);
        DumpObservedSubtitle("DialogueNPCBase.OnConversationLine", __0);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PixelCrushers.DialogueSystem.BarkController), "Bark",
        new[]
        {
            typeof(PixelCrushers.DialogueSystem.Subtitle),
            typeof(UnityEngine.Transform),
            typeof(UnityEngine.Transform),
            typeof(PixelCrushers.DialogueSystem.IBarkUI)
        })]
    private static void BarkController_Bark_Prefix(PixelCrushers.DialogueSystem.Subtitle __0)
    {
        log.LogInfo($"Bark hook reached: {__0?.dialogueEntry?.conversationID}:{__0?.dialogueEntry?.id}");
        ApplySubtitleTranslation(__0);
        DumpObservedSubtitle("BarkController.Bark", __0);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PixelCrushers.DialogueSystem.BarkController), "Bark",
        new[]
        {
            typeof(PixelCrushers.DialogueSystem.Subtitle),
            typeof(bool)
        })]
    private static void BarkController_BarkSubtitle_Prefix(PixelCrushers.DialogueSystem.Subtitle __0)
    {
        log.LogInfo($"Bark subtitle hook reached: {__0?.dialogueEntry?.conversationID}:{__0?.dialogueEntry?.id}");
        ApplySubtitleTranslation(__0);
        DumpObservedSubtitle("BarkController.Bark(Subtitle)", __0);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PixelCrushers.DialogueSystem.ConversationModel), "GetState",
        new[]
        {
            typeof(PixelCrushers.DialogueSystem.DialogueEntry),
            typeof(bool),
            typeof(bool),
            typeof(bool)
        })]
    private static void ConversationModel_GetState_Postfix(PixelCrushers.DialogueSystem.ConversationState __result)
    {
        if (__result?.subtitle != null)
        {
            log.LogInfo($"Conversation state hook reached: {__result.subtitle.dialogueEntry?.conversationID}:{__result.subtitle.dialogueEntry?.id}");
            ApplySubtitleTranslation(__result.subtitle);
            DumpObservedSubtitle("ConversationModel.GetState", __result.subtitle);

            if (__result.npcResponses != null)
            {
                foreach (var response in __result.npcResponses)
                {
                    ApplyResponseTranslation(response);
                }
            }

            if (__result.pcResponses != null)
            {
                foreach (var response in __result.pcResponses)
                {
                    ApplyResponseTranslation(response);
                }
            }
        }
    }
}
