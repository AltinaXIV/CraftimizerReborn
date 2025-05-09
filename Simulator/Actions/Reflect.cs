namespace Craftimizer.Simulator.Actions;

internal sealed class Reflect() : BaseAction(
    ActionCategory.FirstTurn, 69, 100387,
    increasesQuality: true,
    defaultCPCost: 6,
    defaultEfficiency: 300
    )
{
    public override bool IsPossible(RotationSimulator s) => s.IsFirstStep && base.IsPossible(s);

    public override bool CouldUse(RotationSimulator s) => s.IsFirstStep && base.CouldUse(s);

    public override void UseSuccess(RotationSimulator s)
    {
        base.UseSuccess(s);
        s.StrengthenEffect(EffectType.InnerQuiet);
    }
}
