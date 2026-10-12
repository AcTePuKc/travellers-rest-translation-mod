using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace TravellersRestProfiler;

/// <summary>
/// One installed BepInEx plugin.
/// </summary>
public sealed class ModEntry
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string AssemblyName { get; set; } = "";
    public string Location { get; set; } = "";
    public int PatchCount { get; set; }
    public int HarmonyPatchCount { get; set; }
    /// <summary>True when a second copy of this GUID is also loaded.</summary>
    public bool Duplicated { get; set; }

    public string ShortName =>
        string.IsNullOrEmpty(Name) ? (string.IsNullOrEmpty(AssemblyName) ? Guid : AssemblyName) : Name;
}

/// <summary>
/// Reads what BepInEx actually loaded.
///
/// This is deliberately metadata-only: it reports what is installed rather than
/// guessing what any mod costs. Everything here comes from BepInEx itself
/// (<see cref="Chainloader.PluginInfos"/>) or from the assembly on disk, so it
/// cannot be wrong about which mods are running.
///
/// A duplicate GUID means the same mod is installed twice and both copies are
/// loaded, which doubles whatever that mod does and is a common cause of
/// unexplained slowdown. Detecting it was one of the most useful things the
/// Graveyard Keeper 2 profiler did, and it needs no instrumentation at all.
/// </summary>
public static class ModInventory
{
    public static List<ModEntry> Collect(ManualLogSource log)
    {
        var entries = new List<ModEntry>();
        try
        {
            var infos = Chainloader.PluginInfos;
            if (infos == null)
            {
                log?.LogWarning("BepInEx reported no plugin infos; the mod list will be empty.");
                return entries;
            }

            var guidCounts = infos.Values
                .GroupBy(info => info.Metadata?.GUID ?? "")
                .ToDictionary(group => group.Key, group => group.Count());

            foreach (var pair in infos)
            {
                var info = pair.Value;
                var meta = info?.Metadata;
                var entry = new ModEntry
                {
                    Guid = meta?.GUID ?? pair.Key ?? "",
                    Name = meta?.Name ?? "",
                    Version = meta?.Version?.ToString() ?? "",
                    Duplicated = !string.IsNullOrEmpty(meta?.GUID)
                                 && guidCounts.TryGetValue(meta.GUID, out var n) && n > 1,
                };

                var type = info?.Instance?.GetType();
                var assembly = type?.Assembly;
                if (assembly != null)
                {
                    entry.AssemblyName = assembly.GetName()?.Name ?? "";
                    try
                    {
                        entry.Location = assembly.Location ?? "";
                    }
                    catch (NotSupportedException)
                    {
                        // dynamic or in-memory assembly; leave Location empty
                    }
                    entry.PatchCount = CountAttributedMethods(assembly);
                }

                entries.Add(entry);
            }
        }
        catch (Exception exception)
        {
            log?.LogWarning($"Could not read the BepInEx plugin list: {exception.Message}");
        }

        return entries.OrderBy(e => e.ShortName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Counts methods in the assembly that declare a Harmony attribute. This is a
    /// static property of the code, not a measurement of runtime cost.
    /// </summary>
    private static int CountAttributedMethods(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes().Sum(type =>
                type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Static | BindingFlags.Instance)
                    .Count(method => method.CustomAttributes.Any(attribute =>
                    {
                        var name = attribute.AttributeType.Name;
                        return name.StartsWith("Harmony", StringComparison.Ordinal);
                    })));
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types?.Where(t => t != null).Sum(type =>
                type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Static | BindingFlags.Instance)
                    .Count(method => method.CustomAttributes.Any(attribute =>
                        attribute.AttributeType.Name.StartsWith("Harmony", StringComparison.Ordinal)))) ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
