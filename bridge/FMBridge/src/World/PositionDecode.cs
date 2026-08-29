using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FMBridge.World;

/// <summary>
/// Decodes the raw "Position" bitmask (squad_report/query_players/read_entity
/// all surface it as a bare numeric string, e.g. "16384") into compact
/// FM-style labels ("GK", "D (RLC)", "DM", "AM (RL)", "ST (C)").
///
/// Bit layout is the game's own `[Flags] enum PlayingDataFlags.PositionMask`
/// (recovered by static analysis of the game assembly — do NOT confuse it
/// with the two unrelated `enum Position` types that Unity UI Toolkit and
/// the crowd-animation code declare under the same short name). The
/// constants below are a straight port of that decoded bit table into a
/// formatter, not a re-derivation.
///
/// LeftSidedPosition (0x100000) / RightSidedPosition (0x200000) are side
/// MODIFIER flags, not independent position slots: they OR onto a central
/// base flag (DefenderCentre, DefensiveMidfielderCentre, MidfielderCentre,
/// AttackingMidfielderCentre, AttackerCentre) to mean "this central position,
/// played in the left/right half-space" (live-observed masks:
/// 0x100010/0x200010 = DC+L/R, 0x100080/0x200080 = DM+L/R). For groups that
/// already have native left/right base flags of their own (D, M, AM, ST) the
/// modifier just adds that side letter into the group's existing (RLC)
/// bracket. DM has no native side flags at all (there is no DML/DMR in this
/// enum -- wide defensive midfield is expressed via WingBack/Midfielder
/// instead), so a modifier there fully replaces the bare "DM" label with a
/// half-space "DM (L)"/"DM (R)" rather than adding onto a "C".
/// </summary>
internal static class PositionDecode
{
    private const uint Goalkeeper = 1;
    // 0x2 SweeperCentreNotInUse: legacy/unused, never surfaced -- intentionally
    // not decoded to any label (falls through as an unrecognized bit).
    private const uint DefenderRight = 4;
    private const uint DefenderLeft = 8;
    private const uint DefenderCentre = 16;
    private const uint WingBackRight = 32;
    private const uint WingBackLeft = 64;
    private const uint DefensiveMidfielderCentre = 128;
    private const uint MidfielderRight = 256;
    private const uint MidfielderLeft = 512;
    private const uint MidfielderCentre = 1024;
    private const uint AttackingMidfielderRight = 2048;
    private const uint AttackingMidfielderLeft = 4096;
    private const uint AttackingMidfielderCentre = 8192;
    private const uint AttackerCentre = 16384;
    private const uint AttackerRight = 32768;
    private const uint AttackerLeft = 65536;
    private const uint LeftSidedPosition = 1048576;
    private const uint RightSidedPosition = 2097152;

    private sealed class Group
    {
        public string Label;
        public uint Right;
        public uint Left;
        public uint Centre;
        /// <summary>Does this group have real DR/DL-style base flags of its
        /// own, distinct from the L/R side modifiers? Only false for DM,
        /// which has no native side flags in the enum at all.</summary>
        public bool HasNativeSides;
    }

    private static readonly Group[] Groups =
    {
        new() { Label = "D",  Right = DefenderRight, Left = DefenderLeft, Centre = DefenderCentre, HasNativeSides = true },
        new() { Label = "WB", Right = WingBackRight, Left = WingBackLeft, Centre = 0, HasNativeSides = true },
        new() { Label = "DM", Right = 0, Left = 0, Centre = DefensiveMidfielderCentre, HasNativeSides = false },
        new() { Label = "M",  Right = MidfielderRight, Left = MidfielderLeft, Centre = MidfielderCentre, HasNativeSides = true },
        new() { Label = "AM", Right = AttackingMidfielderRight, Left = AttackingMidfielderLeft, Centre = AttackingMidfielderCentre, HasNativeSides = true },
        new() { Label = "ST", Right = AttackerRight, Left = AttackerLeft, Centre = AttackerCentre, HasNativeSides = true },
    };

    /// <summary>Accepts either a bare numeric string ("16384") or a full
    /// TreeWalker.Describe ladder string ("DynamicNumber:16384") -- splits
    /// off any "Type:" prefix itself so callers can hand it the raw prop
    /// value directly. Returns null on empty/unparseable input or a
    /// zero/negative mask (nothing to decode -- caller keeps the raw field
    /// as-is, this is purely additive).</summary>
    public static string Decode(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var sep = raw.IndexOf(':');
        var payload = sep < 0 ? raw : raw.Substring(sep + 1);
        if (!double.TryParse(payload.TrimEnd('f', 'm', 'd'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var num)) return null;
        if (num <= 0 || num > uint.MaxValue) return null;
        return Decode((uint)num);
    }

    public static string Decode(uint mask)
    {
        if (mask == 0) return null;
        var parts = new List<string>();

        if ((mask & Goalkeeper) != 0) parts.Add("GK");

        foreach (var g in Groups)
        {
            var r = g.Right != 0 && (mask & g.Right) != 0;
            var l = g.Left != 0 && (mask & g.Left) != 0;
            var c = g.Centre != 0 && (mask & g.Centre) != 0;
            if (!r && !l && !c) continue;

            if (!g.HasNativeSides)
            {
                // DM: a side modifier means "half-space", not "also centre" --
                // it fully replaces the bare label rather than adding a "C".
                var modL = (mask & LeftSidedPosition) != 0;
                var modR = (mask & RightSidedPosition) != 0;
                if (!modL && !modR) { parts.Add(g.Label); continue; }
                var dmSides = new StringBuilder();
                if (modR) dmSides.Append('R');
                if (modL) dmSides.Append('L');
                parts.Add(g.Label + " (" + dmSides + ")");
                continue;
            }

            // Groups with native R/L base flags: a side modifier on the
            // centre bit just adds that extra letter into the same bracket.
            if (c)
            {
                if ((mask & RightSidedPosition) != 0) r = true;
                if ((mask & LeftSidedPosition) != 0) l = true;
            }
            var sides = new StringBuilder();
            if (r) sides.Append('R');
            if (l) sides.Append('L');
            if (c) sides.Append('C');
            parts.Add(sides.Length == 0 ? g.Label : g.Label + " (" + sides + ")");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
