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
    private static ManualLogSource log;
    private static ConfigEntry<bool> enableTranslationOverrides;
    private static ConfigEntry<string> translationFile;

    private void Awake()
    {
        log = Logger;
        enableTranslationOverrides = Config.Bind("General", "EnableTranslationOverrides", true, "Apply labels.txt translations to the game.");
        translationFile = Config.Bind("General", "TranslationFile", "labels.txt", "Translation file inside the plugin translations folder.");

        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Paths.PluginPath;
        var labelsPath = Path.Combine(pluginDir, "translations", translationFile.Value);
        LoadLabels(labelsPath);

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

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.GetTranslation))]
    private static void LocalizationManager_GetTranslation_Postfix(string Term, ref string __result)
    {
        if (!enableTranslationOverrides.Value || string.IsNullOrEmpty(Term))
        {
            return;
        }

        if (Labels.TryGetValue(Term, out var replacement))
        {
            __result = replacement;
        }
    }
}
