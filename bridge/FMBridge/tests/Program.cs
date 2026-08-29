using FMBridge.World;

static void Expect(bool value, string message)
{
    if (!value) throw new Exception(message);
}

// Raw masks from PositionDecode's documented FM26 flags.
Expect(PositionDecode.MatchesSlot("16384", "ST (C)"), "ST(C) should match ST(C)");
Expect(PositionDecode.MatchesSlot((16 + 8).ToString(), "D (C)"), "D(C) should match D(LC)");
Expect(PositionDecode.MatchesSlot((2048 + 4096 + 16384).ToString(), "ST (C)"), "ST(C) should match a multi-group mask");
Expect(PositionDecode.MatchesSlot("1", "GK"), "GK should match the goalkeeper bit");
Expect(!PositionDecode.MatchesSlot("16384", "D (C)"), "ST(C) must not match D(C)");
Expect(!PositionDecode.MatchesSlot("16384", "not-a-position"), "invalid labels must not match");
Expect(PositionDecode.MatchesSlot((16 + 1048576).ToString(), "D (L)"), "central defender with left modifier should match D(L)");
Console.WriteLine("PositionDecode tests passed");
