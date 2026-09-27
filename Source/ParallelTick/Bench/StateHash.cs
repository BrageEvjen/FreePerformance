using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace ParallelTick.Bench
{
    /// <summary>
    /// Fingerprint of the simulation state, split into parts so a divergence between two runs shows what
    /// diverged first: RNG consumption, thing state (ids, positions, hit points, stacks) or pawn state (jobs, needs).
    /// </summary>
    public static class StateHash
    {
        public struct Parts
        {
            public uint RandSeed, RandIterations;
            public ulong Things, Pawns;

            public ulong Combined
            {
                get
                {
                    var h = new Fnv();
                    h.Mix(RandSeed);
                    h.Mix(RandIterations);
                    h.Mix((long)Things);
                    h.Mix((long)Pawns);
                    return h.Value;
                }
            }

            public override string ToString() => $"{Combined:X16} rand={RandSeed:X8}/{RandIterations} things={Things:X16} pawns={Pawns:X16}";
        }

        private struct Fnv
        {
            private ulong h;
            public ulong Value => h == 0 ? 14695981039346656037UL : h;

            public void Mix(long v)
            {
                if (h == 0)
                    h = 14695981039346656037UL;
                h ^= (ulong)v;
                h *= 1099511628211UL;
            }
        }

        private static readonly AccessTools.FieldRef<uint> RandSeed =
            AccessTools.StaticFieldRefAccess<uint>(AccessTools.Field(typeof(Rand), "seed"));
        private static readonly AccessTools.FieldRef<uint> RandIterations =
            AccessTools.StaticFieldRefAccess<uint>(AccessTools.Field(typeof(Rand), "iterations"));

        public static Parts Compute()
        {
            var things = new Fnv();
            var pawns = new Fnv();
            long Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

            things.Mix(Find.TickManager.TicksGame);
            foreach (var map in Find.Maps)
            {
                var all = map.listerThings.AllThings;
                things.Mix(all.Count);
                foreach (var t in all)
                {
                    things.Mix(t.thingIDNumber);
                    things.Mix(t.Position.x);
                    things.Mix(t.Position.z);
                    things.Mix(t.HitPoints);
                    things.Mix(t.stackCount);
                    if (t is Pawn p)
                    {
                        pawns.Mix(p.thingIDNumber);
                        pawns.Mix(p.CurJobDef?.shortHash ?? 0);
                        pawns.Mix(p.health?.hediffSet?.hediffs.Count ?? 0);
                        if (p.needs?.food != null)
                            pawns.Mix(Bits(p.needs.food.CurLevel));
                        if (p.needs?.rest != null)
                            pawns.Mix(Bits(p.needs.rest.CurLevel));
                    }
                }
            }

            return new Parts
            {
                RandSeed = RandSeed(),
                RandIterations = RandIterations(),
                Things = things.Value,
                Pawns = pawns.Value,
            };
        }

        /// <summary>One line per thing with the fields the hash covers (plus a few more), for diffing two runs.</summary>
        public static IEnumerable<string> Describe()
        {
            var tick = Find.TickManager.TicksGame;
            foreach (var map in Find.Maps)
            {
                var all = map.listerThings.AllThings;
                for (var i = 0; i < all.Count; i++)
                {
                    var t = all[i];
                    var line = $"{tick} #{i} {t.ThingID} {t.def.category} {t.Position} hp={t.HitPoints} n={t.stackCount}";
                    if (t is Pawn p)
                        line += $" job={p.CurJobDef?.defName} tgt={p.CurJob?.targetA} dest={p.pather?.Destination} " +
                                $"food={p.needs?.food?.CurLevel:R} rest={p.needs?.rest?.CurLevel:R} hediffs={p.health?.hediffSet?.hediffs.Count}";
                    yield return line;
                }
            }
        }
    }
}
