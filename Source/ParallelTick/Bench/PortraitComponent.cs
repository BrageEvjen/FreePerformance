using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>
    /// mode=portrait: once the save has loaded, generates one male colonist, dresses and styles it from the portrait.*
    /// lines in ptbench.txt, and saves the game's own portrait render (transparent PNG) for every combination of
    /// portrait.hairs x portrait.heads into the bench data folder (portraits\), then quits. Nothing is saved to the game.
    ///
    /// Keys: hairs, heads (comma lists of def names), hair / skin / shirt (r,g,b in 0..1), size (px), zoom, offsetz,
    /// body (BodyTypeDef), beard (BeardDef), shirtdef (apparel ThingDef), rotation (south|east|west).
    /// </summary>
    public class PortraitComponent : GameComponent
    {
        private int frames;
        private bool started;

        public PortraitComponent(Game game)
        {
        }

        public override void GameComponentUpdate()
        {
            if (!BenchConfig.Active || BenchConfig.Mode != "portrait" || started)
                return;
            if (Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting || ++frames < 60)
                return;
            started = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.CameraDriver.StartCoroutine(Guarded(Render()));
        }

        /// <summary>Runs the render coroutine; if it throws, writes the error as the result and quits instead of hanging.</summary>
        private static IEnumerator Guarded(IEnumerator inner)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext())
                        yield break;
                    current = inner.Current;
                }
                catch (Exception e)
                {
                    File.WriteAllText(BenchConfig.ResultPath, "Portrait render FAILED: " + e);
                    Root.Shutdown();
                    yield break;
                }
                yield return current;
            }
        }

        private static string Get(string key, string fallback) => BenchConfig.Portrait.TryGetValue(key, out var v) ? v : fallback;

        private static Color ColorOf(string key, string fallback)
        {
            var p = Get(key, fallback).Split(',').Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
            return new Color(p[0], p[1], p[2]);
        }

        private static IEnumerator Render()
        {
            var dir = Path.Combine(Path.GetDirectoryName(BenchConfig.ResultPath) ?? ".", "portraits");
            // Empty the folder rather than deleting it: a shell sitting in it would make the delete fail.
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir))
                File.Delete(old);
            var log = new List<string>();
            Pawn pawn = null;
            try
            {
                pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, canGeneratePawnRelations: false, fixedBiologicalAge: 21f, fixedChronologicalAge: 21f,
                    fixedGender: Gender.Male, forcedXenotype: XenotypeDefOf.Baseliner));
                pawn.health.RemoveAllHediffs();
                pawn.equipment?.DestroyAllEquipment();
                pawn.apparel?.DestroyAll();
                pawn.story.bodyType = DefDatabase<BodyTypeDef>.GetNamed(Get("body", "Male"));
                pawn.story.HairColor = ColorOf("hair", "0.62,0.48,0.32");
                pawn.story.skinColorOverride = ColorOf("skin", "0.95,0.8,0.7");
                pawn.style.beardDef = DefDatabase<BeardDef>.GetNamed(Get("beard", "NoBeard"));
                pawn.style.FaceTattoo = TattooDefOf.NoTattoo_Face;
                pawn.style.BodyTattoo = TattooDefOf.NoTattoo_Body;
                var shirtDef = DefDatabase<ThingDef>.GetNamed(Get("shirtdef", "Apparel_BasicShirt"));
                var shirt = (Apparel)ThingMaker.MakeThing(shirtDef, shirtDef.MadeFromStuff ? ThingDefOf.Cloth : null);
                shirt.SetColor(ColorOf("shirt", "0.12,0.16,0.26"), reportFailure: false);
                pawn.apparel.Wear(shirt, dropReplacedApparel: false);
            }
            catch (Exception e)
            {
                log.Add("FAILED to make the pawn: " + e);
            }

            if (pawn != null)
            {
                var size = int.Parse(Get("size", "512"));
                var zoom = float.Parse(Get("zoom", "1.28"), CultureInfo.InvariantCulture);
                var offsetZ = float.Parse(Get("offsetz", "0.3"), CultureInfo.InvariantCulture);
                var rotation = Get("rotation", "south") == "east" ? Rot4.East : Get("rotation", "south") == "west" ? Rot4.West : Rot4.South;
                foreach (var hair in Get("hairs", "ShortCut").Split(',').Select(h => h.Trim()))
                foreach (var head in Get("heads", "Male_AverageWide").Split(',').Select(h => h.Trim()))
                {
                    var hairDef = DefDatabase<HairDef>.GetNamedSilentFail(hair);
                    var headDef = DefDatabase<HeadTypeDef>.GetNamedSilentFail(head);
                    if (hairDef == null || headDef == null)
                    {
                        log.Add($"unknown hair '{hair}' or head '{head}'");
                        continue;
                    }
                    pawn.story.hairDef = hairDef;
                    pawn.story.headType = headDef;
                    pawn.Drawer.renderer.SetAllGraphicsDirty();
                    PortraitsCache.SetDirty(pawn);
                    PortraitsCache.PortraitsCacheUpdate();
                    var rt = PortraitsCache.Get(pawn, new Vector2(size, size), rotation, new Vector3(0f, 0f, offsetZ), zoom,
                        supersample: true, compensateForUIScale: false, renderHeadgear: false);
                    yield return new WaitForEndOfFrame();
                    var previous = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.ARGB32, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = previous;
                    File.WriteAllBytes(Path.Combine(dir, $"{hair}__{head}.png"), tex.EncodeToPNG());
                    UnityEngine.Object.Destroy(tex);
                    log.Add($"saved {hair} / {head} ({rt.width}x{rt.height})");
                }
            }
            File.WriteAllText(BenchConfig.ResultPath, "Portrait render\n" + string.Join("\n", log) + "\n");
            Root.Shutdown();
        }
    }
}
