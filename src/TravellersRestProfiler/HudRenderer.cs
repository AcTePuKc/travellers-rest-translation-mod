using System;
using BepInEx.Logging;
using UnityEngine;

namespace TravellersRestProfiler;

/// <summary>
/// Draws the status panel over the game.
///
/// Why this is a separate component rather than drawing from the per-frame patch: Unity
/// refuses GUI calls outside OnGUI — "You can only call GUI functions from inside OnGUI" — so
/// the patch can schedule work but cannot draw.
///
/// The component is created lazily from inside a frame, once the game is running, and NOT
/// from the plugin's Awake. That distinction is the whole trick: a component created during
/// Awake never receives OnGUI on this game, while one created later does. An earlier attempt
/// at an interactive window failed for exactly that reason, and it was misread as the game
/// never calling OnGUI at all.
/// </summary>
public sealed class HudRenderer : MonoBehaviour
{
    private static HudRenderer instance;
    private static Texture2D background;
    private static ManualLogSource log;

    /// <summary>
    /// Creates the renderer once, from inside a frame. Safe to call every frame: after the
    /// first success it returns immediately.
    /// </summary>
    public static void EnsureExists(ManualLogSource logger)
    {
        if (instance != null)
        {
            return;
        }

        try
        {
            log = logger;
            var host = new GameObject("TravellersRestProfiler.Hud");
            UnityEngine.Object.DontDestroyOnLoad(host);
            instance = host.AddComponent<HudRenderer>();
            log?.LogInfo("HUD renderer created; the status panel should appear from now on.");
        }
        catch (Exception exception)
        {
            // If this cannot be created the measurement is unaffected, so it is reported and
            // the profiler carries on.
            log?.LogWarning($"Could not create the HUD renderer: {exception.Message}");
        }
    }

    private void OnGUI()
    {
        try
        {
            Draw();
        }
        catch (Exception exception)
        {
            // A panel that throws every frame would be worse than no panel.
            ProfilerRuntime.DisableHud($"HUD drawing failed: {exception.Message}");
        }
    }

    private static void Draw()
    {
        // Every measurement here was wrong at first and clipped the text.
        //
        // At 1080p the font was 14 px while each label was given 18.36 px of height. That
        // sounds like enough and is not: a 14 px font needs about 18.6 px for its ascent and
        // descent alone, before any padding, so the descenders on g, y and z were cut off and
        // the bottoms of the panel's lines were shaved.
        //
        // The rule now is simply that a line gets 1.6x its font size, which leaves room for the
        // glyphs and the spacing between lines.
        var fontSize = Math.Max(11, (int)(Screen.height * 0.0135f));
        var lineHeight = fontSize * 1.6f;
        var padding = Math.Max(6f, fontSize * 0.5f);
        var width = Math.Max(260f, Screen.width * 0.155f);
        var height = lineHeight * 4 + padding * 2;

        var margin = ProfilerRuntime.HudMargin;
        var x = ProfilerRuntime.HudLeft ? margin : Screen.width - width - margin;
        // Bottom-left is the one corner this game leaves empty. The top-left holds the clock
        // and money, the top-right is where achievement notifications appear, and the bottom
        // centre holds the hotbar. Hence the default.
        var y = ProfilerRuntime.HudBottom ? Screen.height - height - margin : margin;
        var box = new Rect(x, y, width, height);

        // A readable panel behind the text. This has to be nearly opaque: at 0.66 alpha the
        // game's own text showed straight through it and the two overlapped into noise.
        GUI.color = new Color(0f, 0f, 0f, 0.9f);
        GUI.DrawTexture(box, Background());
        GUI.color = Color.white;

        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            clipping = TextClipping.Clip,
        };
        style.normal.textColor = new Color(1f, 1f, 1f, 0.95f);

        var textX = box.x + padding;
        var textWidth = width - padding * 2;
        var textY = box.y + padding;

        GUI.Label(new Rect(textX, textY, textWidth, lineHeight), "Travellers Rest profiler", style);
        textY += lineHeight;
        GUI.Label(new Rect(textX, textY, textWidth, lineHeight), ProfilerRuntime.StatusLine(), style);
        textY += lineHeight;
        GUI.Label(new Rect(textX, textY, textWidth, lineHeight), ProfilerRuntime.ElapsedLine(), style);
        textY += lineHeight;
        GUI.Label(new Rect(textX, textY, textWidth, lineHeight), ProfilerRuntime.CountersLine(), style);
    }

    private static Texture2D Background()
    {
        if (background == null)
        {
            background = new Texture2D(1, 1);
            background.SetPixel(0, 0, Color.white);
            background.Apply();
        }
        return background;
    }
}
