using AcDream.UI.Abstractions.Input;
using Drw = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.App.Input;

internal static class RetailEmoteMotionTable
{
    private const uint EmoteInputMap = 0x10000006u;
    private const uint FirstEmoteAction = 0x10000098u;

    private static readonly uint[] Motions =
    [
        (uint)Drw.AFKState,
        (uint)Drw.Akimbo,
        (uint)Drw.ATOYOT,
        (uint)Drw.AkimboState,
        (uint)Drw.AtEaseState,
        (uint)Drw.Beckon,
        (uint)Drw.BeSeeingYou,
        (uint)Drw.BlowKiss,
        (uint)Drw.BowDeep,
        (uint)Drw.BowDeepState,
        (uint)Drw.Cheer,
        (uint)Drw.ClapHands,
        (uint)Drw.ClapHandsState,
        (uint)Drw.Cringe,
        (uint)Drw.CrossArmsState,
        (uint)Drw.Cry,
        (uint)Drw.CurtseyState,
        (uint)Drw.DrudgeDance,
        (uint)Drw.DrudgeDanceState,
        (uint)Drw.HaveASeat,
        (uint)Drw.HaveASeatState,
        (uint)Drw.HeartyLaugh,
        (uint)Drw.Helper,
        (uint)Drw.Kneel,
        (uint)Drw.KneelState,
        (uint)Drw.Knock,
        (uint)Drw.Laugh,
        (uint)Drw.LeanState,
        (uint)Drw.MeditateState,
        (uint)Drw.MimeDrink,
        (uint)Drw.MimeEat,
        (uint)Drw.Mock,
        (uint)Drw.Nod,
        (uint)Drw.NudgeLeft,
        (uint)Drw.NudgeRight,
        (uint)Drw.Plead,
        (uint)Drw.PleadState,
        (uint)Drw.Point,
        (uint)Drw.PointState,
        (uint)Drw.PointDown,
        (uint)Drw.PointDownState,
        (uint)Drw.PointLeft,
        (uint)Drw.PointLeftState,
        (uint)Drw.PointRight,
        (uint)Drw.PointRightState,
        (uint)Drw.PossumState,
        (uint)Drw.Pray,
        (uint)Drw.PrayState,
        (uint)Drw.ReadState,
        (uint)Drw.Salute,
        (uint)Drw.SaluteState,
        (uint)Drw.ScanHorizon,
        (uint)Drw.ScratchHead,
        (uint)Drw.ScratchHeadState,
        (uint)Drw.ShakeFist,
        (uint)Drw.ShakeFistState,
        (uint)Drw.ShakeHead,
        (uint)Drw.Shiver,
        (uint)Drw.ShiverState,
        (uint)Drw.Shoo,
        (uint)Drw.Shrug,
        (uint)Drw.SitState,
        (uint)Drw.SitBackState,
        (uint)Drw.SitCrossleggedState,
        (uint)Drw.Slouch,
        (uint)Drw.SlouchState,
        (uint)Drw.SmackHead,
        (uint)Drw.SnowAngelState,
        (uint)Drw.Spit,
        (uint)Drw.Surrender,
        (uint)Drw.SurrenderState,
        (uint)Drw.TalktotheHandState,
        (uint)Drw.TapFoot,
        (uint)Drw.TapFootState,
        (uint)Drw.Teapot,
        (uint)Drw.ThinkerState,
        (uint)Drw.WarmHands,
        (uint)Drw.Wave,
        (uint)Drw.WaveState,
        (uint)Drw.WaveLow,
        (uint)Drw.WaveHigh,
        (uint)Drw.Winded,
        (uint)Drw.WindedState,
        (uint)Drw.Woah,
        (uint)Drw.WoahState,
        (uint)Drw.YawnStretch,
        (uint)Drw.YMCA,
    ];

    public static int Count => Motions.Length;

    public static bool TryGetMotion(InputAction action, out uint motion)
    {
        motion = 0u;
        if (!RetailActionIdentityTable.TryGetRetailIdentity(
                action,
                out var identity)
            || identity.InputMapId != EmoteInputMap)
        {
            return false;
        }

        uint index = identity.ActionId - FirstEmoteAction;
        if (index >= Motions.Length)
            return false;

        motion = Motions[index];
        return true;
    }
}
